using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using DOSI.CORE.Security;

namespace DOSI.CORE.ProjectSystem;

/// <summary>Severity bucket for a <see cref="DOSIDiagnostic"/>.</summary>
public enum DOSIDiagnosticSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
/// Structured compiler diagnostic surfaced by the IDE's Error List. Carries
/// the original Roslyn fields plus a best-effort plain-English suggested fix
/// keyed off the diagnostic <see cref="Code"/>.
/// </summary>
public sealed class DOSIDiagnostic
{
    public DOSIDiagnosticSeverity Severity { get; init; } = DOSIDiagnosticSeverity.Error;
    /// <summary>Roslyn diagnostic id (e.g. <c>"CS0103"</c>) or empty for non-compiler messages.</summary>
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    /// <summary>Absolute path of the source file the diagnostic points at, or empty if unknown.</summary>
    public string FilePath { get; init; } = string.Empty;
    /// <summary>1-based start line. 0 when the diagnostic has no location.</summary>
    public int Line { get; init; }
    /// <summary>1-based start column. 0 when the diagnostic has no location.</summary>
    public int Column { get; init; }
    /// <summary>1-based end line, defaulting to <see cref="Line"/>.</summary>
    public int EndLine { get; init; }
    /// <summary>1-based end column, defaulting to <see cref="Column"/>.</summary>
    public int EndColumn { get; init; }
    /// <summary>Plain-English hint shown below the message in the Error List, or null.</summary>
    public string? SuggestedFix { get; init; }
}

/// <summary>
/// Outcome of a <see cref="DOSIProjectCompiler"/> run.
/// </summary>
public sealed class DOSIBuildResult
{
    public bool Success { get; init; }
    /// <summary>Pre-formatted diagnostic strings (kept for backwards-compat with the Output pane).</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
    /// <summary>Structured diagnostics for the IDE Error List. Empty for purely-informational builds.</summary>
    public IReadOnlyList<DOSIDiagnostic> StructuredDiagnostics { get; init; } = Array.Empty<DOSIDiagnostic>();
    public Assembly? Assembly { get; init; }
    public string Output { get; init; } = string.Empty;
    public Control? ReturnedControl { get; init; }
    public Exception? RuntimeException { get; init; }
}

/// <summary>
/// Compiles a <see cref="DOSIProject"/> in-memory with Roslyn and (optionally)
/// invokes its entry point. References every assembly already loaded in the
/// current AppDomain plus the runtime's TPA list, so user code can freely use
/// DOSI controls, Avalonia, and BCL APIs.
/// </summary>
public static class DOSIProjectCompiler
{
    private static IReadOnlyList<MetadataReference>? _cachedReferences;

    /// <summary>Compiles the project. Does not invoke the entry point.</summary>
    public static DOSIBuildResult Build(DOSIProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var sources = project.EnumerateSourceFiles().ToList();
        if (sources.Count == 0)
        {
            // Pure-form / asset-only project (e.g. a single .dosiform with no
            // user C# yet). There's nothing to compile, but it's not a failure -
            // the form runtime can still launch the .dosiform on Run. Surface
            // a hint so the user knows why no assembly was produced.
            return new DOSIBuildResult
            {
                Success = true,
                Diagnostics = new[] { "No .cs source files in project - nothing to compile (form/asset-only project)." }
            };
        }

        var trees = sources
            .Select(p =>
            {
                string text;
                try { text = UserVault.ReadAllText(p); }
                catch (Exception ex) { return (Tree: (SyntaxTree?)null, Error: $"{p}: {ex.Message}"); }

                // Allow "script-style" files (top-level statements + no class/namespace)
                // so users can just write a few lines and `return someControl;`.
                text = MaybeRewriteScript(text, p);

                return (Tree: CSharpSyntaxTree.ParseText(text, path: p), Error: (string?)null);
            })
            .ToList();

        var parseErrors = trees.Where(t => t.Error != null).Select(t => t.Error!).ToList();
        if (parseErrors.Count > 0)
        {
            return new DOSIBuildResult
            {
                Success = false,
                Diagnostics = parseErrors,
                StructuredDiagnostics = parseErrors
                    .Select(msg => new DOSIDiagnostic
                    {
                        Severity = DOSIDiagnosticSeverity.Error,
                        Message = msg
                    })
                    .ToList()
            };
        }

        var compilation = CSharpCompilation.Create(
            assemblyName: project.Name + "_" + Guid.NewGuid().ToString("N"),
            syntaxTrees: trees.Select(t => t.Tree!),
            references: GetReferences(),
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug,
                allowUnsafe: false,
                // Suppress benign assembly-binding noise that comes from referencing
                // both AppDomain-loaded assemblies and the TPA fallbacks - they often
                // disagree on minor versions but the runtime resolves them anyway.
                specificDiagnosticOptions: new[]
                {
                    new KeyValuePair<string, ReportDiagnostic>("CS1701", ReportDiagnostic.Suppress),
                    new KeyValuePair<string, ReportDiagnostic>("CS1702", ReportDiagnostic.Suppress),
                    new KeyValuePair<string, ReportDiagnostic>("CS1705", ReportDiagnostic.Suppress),
                }));

        using var ms = new MemoryStream();
        EmitResult emit = compilation.Emit(ms);

        var emitDiags = emit.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error || d.Severity == DiagnosticSeverity.Warning)
            .ToList();
        var diagnostics = emitDiags.Select(FormatDiagnostic).ToList();
        var structured = emitDiags.Select(ToStructured).ToList();

        if (!emit.Success)
        {
            return new DOSIBuildResult
            {
                Success = false,
                Diagnostics = diagnostics,
                StructuredDiagnostics = structured
            };
        }

        ms.Seek(0, SeekOrigin.Begin);
        Assembly assembly;
        try
        {
            assembly = Assembly.Load(ms.ToArray());
        }
        catch (Exception ex)
        {
            return new DOSIBuildResult
            {
                Success = false,
                Diagnostics = diagnostics.Concat(new[] { "Assembly load failed: " + ex.Message }).ToList(),
                StructuredDiagnostics = structured.Concat(new[]
                {
                    new DOSIDiagnostic
                    {
                        Severity = DOSIDiagnosticSeverity.Error,
                        Message = "Assembly load failed: " + ex.Message
                    }
                }).ToList()
            };
        }

        return new DOSIBuildResult
        {
            Success = true,
            Diagnostics = diagnostics,
            StructuredDiagnostics = structured,
            Assembly = assembly
        };
    }

    /// <summary>
    /// Builds the project and, on success, invokes the manifest's entry method.
    /// Captures stdout into <see cref="DOSIBuildResult.Output"/>; if the entry
    /// returns a <see cref="Control"/>, it's exposed via <see cref="DOSIBuildResult.ReturnedControl"/>.
    /// </summary>
    public static DOSIBuildResult BuildAndRun(DOSIProject project)
    {
        var build = Build(project);
        if (!build.Success || build.Assembly == null) return build;

        var entryType = build.Assembly.GetType(project.Manifest.EntryType, throwOnError: false)
                        ?? build.Assembly.GetTypes()
                            .FirstOrDefault(t => t.Name.Equals(project.Manifest.EntryType,
                                                               StringComparison.Ordinal));

        if (entryType == null)
        {
            var msg = $"Entry type '{project.Manifest.EntryType}' was not found in the compiled assembly.";
            return new DOSIBuildResult
            {
                Success = false,
                Diagnostics = build.Diagnostics.Concat(new[] { msg }).ToList(),
                StructuredDiagnostics = build.StructuredDiagnostics.Concat(new[]
                {
                    new DOSIDiagnostic
                    {
                        Severity = DOSIDiagnosticSeverity.Error,
                        Code = "DOSI001",
                        Message = msg,
                        SuggestedFix = $"Define a '{project.Manifest.EntryType}' class in your project, or update the project's EntryType in the manifest."
                    }
                }).ToList(),
                Assembly = build.Assembly
            };
        }

        var entryMethod = entryType.GetMethod(
            project.Manifest.EntryMethod,
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);

        if (entryMethod == null)
        {
            var msg = $"Static method '{project.Manifest.EntryMethod}' was not found on '{entryType.FullName}'.";
            return new DOSIBuildResult
            {
                Success = false,
                Diagnostics = build.Diagnostics.Concat(new[] { msg }).ToList(),
                StructuredDiagnostics = build.StructuredDiagnostics.Concat(new[]
                {
                    new DOSIDiagnostic
                    {
                        Severity = DOSIDiagnosticSeverity.Error,
                        Code = "DOSI002",
                        Message = msg,
                        SuggestedFix = $"Add 'public static Control {project.Manifest.EntryMethod}()' to '{entryType.Name}', or update the project's EntryMethod in the manifest."
                    }
                }).ToList(),
                Assembly = build.Assembly
            };
        }

        var sb = new StringBuilder();
        var captureWriter = new StringWriter(sb);
        var originalOut = Console.Out;
        var originalErr = Console.Error;

        Control? returnedControl = null;
        Exception? runtimeException = null;

        try
        {
            Console.SetOut(captureWriter);
            Console.SetError(captureWriter);

            var args = entryMethod.GetParameters().Length == 0
                ? Array.Empty<object?>()
                : new object?[entryMethod.GetParameters().Length];

            var result = entryMethod.Invoke(null, args);

            if (result is Control c)
                returnedControl = c;
        }
        catch (TargetInvocationException tie)
        {
            runtimeException = tie.InnerException ?? tie;
        }
        catch (Exception ex)
        {
            runtimeException = ex;
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }

        var output = sb.ToString();
        if (runtimeException != null)
        {
            output += Environment.NewLine + "[Runtime] " + runtimeException.GetType().Name + ": " + runtimeException.Message;
        }

        // Carry the build's structured diagnostics through (the original code
        // dropped them on the floor here, so even a real Roslyn warning would
        // never reach the Error List after a Run). When the user's entry point
        // threw at runtime, append a synthetic diagnostic so the failure shows
        // up alongside compile diagnostics in the Error List rather than being
        // buried inside the OUTPUT log.
        var structured = build.StructuredDiagnostics.ToList();
        if (runtimeException != null)
        {
            structured.Add(BuildRuntimeDiagnostic(runtimeException, project));
        }

        return new DOSIBuildResult
        {
            Success = runtimeException == null,
            Diagnostics = build.Diagnostics,
            StructuredDiagnostics = structured,
            Assembly = build.Assembly,
            Output = output,
            ReturnedControl = returnedControl,
            RuntimeException = runtimeException
        };
    }

    /// <summary>
    /// Wraps a runtime exception thrown from the user's entry point into a
    /// <see cref="DOSIDiagnostic"/> so it surfaces in the IDE Error List.
    /// Tries to recover the originating file + line by walking the exception's
    /// stack frames for the first one whose file path lives inside the active
    /// project; falls back to a location-less diagnostic when nothing matches
    /// (e.g. the exception came from BCL code with no PDB).
    /// </summary>
    private static DOSIDiagnostic BuildRuntimeDiagnostic(Exception ex, DOSIProject project)
    {
        string file = string.Empty;
        int line = 0;
        int column = 0;
        try
        {
            var trace = new System.Diagnostics.StackTrace(ex, fNeedFileInfo: true);
            var rootFull = Path.GetFullPath(project.FolderPath);
            for (int i = 0; i < trace.FrameCount; i++)
            {
                var frame = trace.GetFrame(i);
                var f = frame?.GetFileName();
                if (string.IsNullOrEmpty(f)) continue;
                if (!f.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;
                file = f;
                line = frame!.GetFileLineNumber();
                column = frame!.GetFileColumnNumber();
                break;
            }
        }
        catch { /* stack-walk is best-effort */ }

        return new DOSIDiagnostic
        {
            Severity = DOSIDiagnosticSeverity.Error,
            Code = "DOSI100",
            Message = $"{ex.GetType().Name}: {ex.Message}",
            FilePath = file,
            Line = line,
            Column = column,
            EndLine = line,
            EndColumn = column,
            SuggestedFix = "Run threw a " + ex.GetType().Name + ". Check the OUTPUT pane for the full message and the call site shown above."
        };
    }

    private static string FormatDiagnostic(Diagnostic d)
    {
        var loc = d.Location.GetLineSpan();
        var file = string.IsNullOrEmpty(loc.Path) ? "" : Path.GetFileName(loc.Path);
        var line = loc.IsValid ? loc.StartLinePosition.Line + 1 : 0;
        var col = loc.IsValid ? loc.StartLinePosition.Character + 1 : 0;
        var sev = d.Severity == DiagnosticSeverity.Error ? "error" : "warning";
        return $"{file}({line},{col}): {sev} {d.Id}: {d.GetMessage()}";
    }

    /// <summary>
    /// Projects a Roslyn <see cref="Diagnostic"/> into the IDE-facing
    /// <see cref="DOSIDiagnostic"/> shape (1-based positions, severity bucket,
    /// suggested-fix string). The Error List in DOSIIDE consumes this.
    /// </summary>
    private static DOSIDiagnostic ToStructured(Diagnostic d)
    {
        var loc = d.Location.GetLineSpan();
        var hasPos = loc.IsValid;
        return new DOSIDiagnostic
        {
            Severity = d.Severity switch
            {
                DiagnosticSeverity.Error => DOSIDiagnosticSeverity.Error,
                DiagnosticSeverity.Warning => DOSIDiagnosticSeverity.Warning,
                _ => DOSIDiagnosticSeverity.Info
            },
            Code = d.Id ?? string.Empty,
            Message = d.GetMessage(),
            FilePath = loc.Path ?? string.Empty,
            Line = hasPos ? loc.StartLinePosition.Line + 1 : 0,
            Column = hasPos ? loc.StartLinePosition.Character + 1 : 0,
            EndLine = hasPos ? loc.EndLinePosition.Line + 1 : 0,
            EndColumn = hasPos ? loc.EndLinePosition.Character + 1 : 0,
            SuggestedFix = SuggestFix(d.Id)
        };
    }

    /// <summary>
    /// Best-effort plain-English remediation hint for the most common Roslyn
    /// diagnostics our users hit. Returns null for codes we don't have a
    /// specific tip for; the IDE simply omits the "Fix:" line in that case.
    /// Keep entries terse - this surfaces under the message in the Error List.
    /// </summary>
    private static string? SuggestFix(string code) => code switch
    {
        // ----- Lookup / resolution -----
        "CS0103" => "Check the spelling, or add a 'using' for the namespace that defines this name.",
        "CS0246" => "Add the missing 'using' directive (e.g. 'using System;') or check the type name spelling.",
        "CS0104" => "The name is ambiguous - qualify it with its namespace (e.g. 'System.Timer' vs 'System.Threading.Timer').",
        "CS0117" => "The type doesn't define this member. Check spelling or look for a different overload / extension method.",
        "CS1061" => "The type doesn't have this member. Verify the spelling, the type, or whether you need to add a 'using' for an extension method.",

        // ----- Syntax -----
        "CS1002" => "Add the missing semicolon ';' at the end of the statement.",
        "CS1003" => "Syntax error - usually a missing or extra punctuation character (',', ';', '}', etc.).",
        "CS1513" => "Add the missing closing brace '}'.",
        "CS1514" => "Add the missing opening brace '{'.",
        "CS1525" => "Unexpected token. The expression isn't valid here - check punctuation and operator placement.",

        // ----- Types / conversions -----
        "CS0029" => "Cannot implicitly convert. Add an explicit cast '(Type)value' or change the variable's type.",
        "CS0266" => "Cannot implicitly convert (loss of precision risk). Add an explicit cast.",
        "CS0019" => "The operator can't be applied to these operand types - check the types or convert one side.",
        "CS0021" => "Cannot index the value with [] - the type isn't an array, list, dictionary, or indexable collection.",

        // ----- Method calls -----
        "CS1501" => "No overload of the method takes this many arguments. Check the parameter list.",
        "CS1503" => "Argument type doesn't match the parameter type. Cast it or pass a different value.",
        "CS7036" => "A required argument wasn't provided - supply all parameters that don't have default values.",

        // ----- Control flow -----
        "CS0161" => "Not all code paths return a value. Add a 'return' statement before the closing brace.",
        "CS0165" => "Local variable used before being assigned. Initialize it first or assign it on every path.",

        // ----- Nullability -----
        "CS8600" => "Possible null assignment to a non-nullable type. Mark the target as nullable ('Type?') or guard with a null check.",
        "CS8602" => "Possible dereference of a null value. Add a null check ('if (x is not null)'), use '?.', or assert with '!'.",
        "CS8604" => "Possible null reference passed as argument. Guard the value before passing it.",

        // ----- Misc common -----
        "CS0234" => "The type or namespace doesn't exist in this assembly. Check the namespace path or add a project reference.",
        "CS0535" => "Implement the missing interface member, or remove the interface from the class.",
        "CS0407" => "Method group has the wrong return type for the delegate. Change the delegate or the method signature.",

        _ => null
    };

    /// <summary>
    /// If <paramref name="text"/> looks like a "script" file - it contains top-level
    /// statements but no class / record / struct / interface / namespace at the top -
    /// rewrite it into the canonical <c>public static class Program { public static Control Run() { ... } }</c>
    /// shape and inject the standard DOSI/Avalonia <c>using</c> directives so the
    /// user doesn't have to remember them. Files that already contain a real type
    /// declaration are passed through unchanged.
    /// </summary>
    private static string MaybeRewriteScript(string text, string path)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        SyntaxTree tree;
        try { tree = CSharpSyntaxTree.ParseText(text, path: path); }
        catch { return text; }

        if (tree.GetRoot() is not CompilationUnitSyntax root) return text;

        // If the file already declares any type / namespace, treat it as a normal
        // C# source and leave it alone.
        var hasRealType = root.Members.Any(m =>
            m is BaseTypeDeclarationSyntax or BaseNamespaceDeclarationSyntax);
        if (hasRealType) return text;

        // No top-level statements either? Nothing to rewrite.
        var globals = root.Members.OfType<GlobalStatementSyntax>().ToList();
        if (globals.Count == 0) return text;

        var existingUsings = new HashSet<string>(
            root.Usings
                .Where(u => u.Name != null)
                .Select(u => u.Name!.ToString()),
            StringComparer.Ordinal);

        var autoUsings = new[]
        {
            "System", "System.Collections.Generic", "System.Linq", "System.Threading.Tasks",
            "Avalonia", "Avalonia.Controls", "Avalonia.Controls.Shapes", "Avalonia.Input",
            "Avalonia.Layout", "Avalonia.Media", "Avalonia.Threading",
            "DOSI.CORE.AccentManagement", "DOSI.CORE.UIComponents",
            "DOSI.CORE.UIComponents.WindowManagement", "DOSI.CORE.UserManagement"
        };

        var sb = new StringBuilder();

        // 1) Preserve user-declared usings exactly as written.
        foreach (var u in root.Usings)
            sb.AppendLine(u.ToFullString().TrimEnd('\r', '\n'));

        // 2) Inject our standard usings (skip ones the user already has).
        foreach (var ns in autoUsings)
            if (!existingUsings.Contains(ns))
                sb.AppendLine("using " + ns + ";");

        sb.AppendLine();
        sb.AppendLine("public static class Program");
        sb.AppendLine("{");
        sb.AppendLine("    public static global::Avalonia.Controls.Control Run()");
        sb.AppendLine("    {");

        // 3) Splat each top-level statement into the method body, indented.
        foreach (var g in globals)
        {
            var src = g.ToFullString().Replace("\r\n", "\n").TrimEnd('\n');
            foreach (var line in src.Split('\n'))
                sb.AppendLine(line.Length == 0 ? string.Empty : "        " + line);
        }

        // 4) If the user never wrote `return ...;`, fall back to returning null so
        //    the generated method still satisfies its signature (the IDE will
        //    just show "[Run] Entry point returned no Control to display.").
        var hasReturn = globals
            .SelectMany(g => g.DescendantNodes().OfType<ReturnStatementSyntax>())
            .Any();
        if (!hasReturn)
            sb.AppendLine("        return null!;");

        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// Reference list used by Roslyn compilation. Internal so other in-process
    /// compilers (e.g. the visual designer's per-handler compiler) can reuse
    /// the same cached list instead of rebuilding it from AppDomain scans.
    /// </summary>
    internal static IReadOnlyList<MetadataReference> GetReferencesInternal() => GetReferences();

    private static IReadOnlyList<MetadataReference> GetReferences()
    {
        if (_cachedReferences != null) return _cachedReferences;

        // Key on the assembly's SIMPLE NAME (e.g. "System.ObjectModel") so we
        // never feed Roslyn two copies of the same assembly at different
        // versions - that's what produces all the CS1701/1702 warning noise.
        var refs = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (!File.Exists(path)) return;

            var name = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(name)) return;
            if (refs.ContainsKey(name)) return; // first one wins

            try { refs[name] = MetadataReference.CreateFromFile(path); }
            catch { /* skip unloadable */ }
        }

        // 1) AppDomain-loaded assemblies first - these are the EXACT versions
        //    the host process resolved, so user code can safely bind to them.
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;
            string? location;
            try { location = asm.Location; } catch { continue; }
            TryAdd(location);
        }

        // 2) Trusted Platform Assemblies fill in any BCL surface that hasn't
        //    been loaded yet. Anything already added in step 1 is skipped.
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (!string.IsNullOrEmpty(tpa))
        {
            foreach (var path in tpa.Split(Path.PathSeparator,
                                           StringSplitOptions.RemoveEmptyEntries))
            {
                TryAdd(path);
            }
        }

        _cachedReferences = refs.Values.ToList();
        return _cachedReferences;
    }
}
