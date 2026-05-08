using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using DOSI.CORE.ProjectSystem;

namespace DOSI.CORE.Designer;

/// <summary>
/// Outcome of a per-form handler-compilation pass. <see cref="Handlers"/>
/// maps "<c>controlName.eventName</c>" to a delegate the runtime loader
/// then attaches via <see cref="DOSIDesignerControlEntry.BindEvent"/>.
/// </summary>
public sealed class DOSIFormHandlerCompileResult
{
    public bool Success { get; init; }
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
    public Dictionary<string, Delegate> Handlers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Synthesises a single C# source file containing one method per
/// (control, event) pair on a <see cref="DOSIFormDocument"/>, compiles it
/// with Roslyn, then returns the open delegates so the runtime loader can
/// wire them to the live control instances.
///
/// Why a synthesised wrapper class instead of CSharpScript:
///   - Stays inside the same Roslyn surface area the IDE's project compiler
///     already pulls in, so no extra package reference.
///   - Methods get strong typing - the user writes a real C# body that the
///     compiler validates (intellisense via the property grid is wishful;
///     this at least keeps "Run" honest).
///   - The synthesised method signature exactly matches the event delegate,
///     so wiring is a straight Delegate.CreateDelegate call.
/// </summary>
public static class DOSIFormHandlerCompiler
{
    /// <summary>The class name the synthesised file declares.</summary>
    private const string GeneratedClassName = "__DosiFormHandlers";

    public static DOSIFormHandlerCompileResult Compile(DOSIFormDocument doc)
    {
        // Collect every (owner, event) pair that has non-empty user code.
        // Owner is either a control's Name or the synthetic "Form" name for
        // form-level handlers (Load / Closing).
        var handlers = new List<(string OwnerName, string Event, string MethodName, string Body, EventSig Sig)>();

        // Form-level handlers first - keyed under the synthetic owner "Form".
        foreach (var (evName, body) in doc.Handlers)
        {
            if (string.IsNullOrWhiteSpace(body)) continue;
            var sig = ResolveFormEventSignature(evName);
            if (sig == null) continue;
            var methodName = "Form_" + SanitizeIdent(evName);
            handlers.Add(("Form", evName, methodName, body, sig.Value));
        }

        // Per-control handlers.
        foreach (var def in doc.Controls)
        {
            if (def.Handlers.Count == 0) continue;
            if (string.IsNullOrWhiteSpace(def.Name)) continue;
            var entry = DOSIDesignerControlCatalog.Find(def.Type);
            if (entry == null) continue;

            foreach (var (evName, body) in def.Handlers)
            {
                if (string.IsNullOrWhiteSpace(body)) continue;
                var sig = ResolveEventSignature(def.Type, evName);
                if (sig == null) continue;
                var methodName = SanitizeIdent(def.Name) + "_" + SanitizeIdent(evName);
                handlers.Add((def.Name, evName, methodName, body, sig.Value));
            }
        }

        if (handlers.Count == 0)
        {
            return new DOSIFormHandlerCompileResult { Success = true };
        }

        // Build the source text. Each handler becomes a static method on a
        // single class so the runtime can grab them via reflection.
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using Avalonia;");
        sb.AppendLine("using Avalonia.Controls;");
        sb.AppendLine("using Avalonia.Interactivity;");
        sb.AppendLine("using Avalonia.Media;");
        sb.AppendLine("using DOSI.CORE.UIComponents;");
        sb.AppendLine();
        sb.Append("public static class ").AppendLine(GeneratedClassName);
        sb.AppendLine("{");
        foreach (var h in handlers)
        {
            sb.Append("    public static void ").Append(h.MethodName).Append('(')
              .Append(h.Sig.SenderType).Append(" sender, ")
              .Append(h.Sig.ArgsType).AppendLine(" e)");
            sb.AppendLine("    {");
            // User body verbatim - they own the contents.
            foreach (var line in h.Body.Replace("\r\n", "\n").Split('\n'))
            {
                sb.Append("        ").AppendLine(line);
            }
            sb.AppendLine("    }");
        }
        sb.AppendLine("}");

        var source = sb.ToString();
        var tree = CSharpSyntaxTree.ParseText(source, path: "<form-handlers>");
        var compilation = CSharpCompilation.Create(
            assemblyName: "DosiFormHandlers_" + Guid.NewGuid().ToString("N"),
            syntaxTrees: new[] { tree },
            references: DOSIProjectCompiler.GetReferencesInternal(),
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug,
                allowUnsafe: false,
                specificDiagnosticOptions: new[]
                {
                    new KeyValuePair<string, ReportDiagnostic>("CS1701", ReportDiagnostic.Suppress),
                    new KeyValuePair<string, ReportDiagnostic>("CS1702", ReportDiagnostic.Suppress),
                    new KeyValuePair<string, ReportDiagnostic>("CS1705", ReportDiagnostic.Suppress)
                }));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        var diags = emit.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error || d.Severity == DiagnosticSeverity.Warning)
            .Select(d => d.ToString())
            .ToList();

        if (!emit.Success)
        {
            return new DOSIFormHandlerCompileResult { Success = false, Diagnostics = diags };
        }

        ms.Seek(0, SeekOrigin.Begin);
        var asm = Assembly.Load(ms.ToArray());
        var type = asm.GetType(GeneratedClassName);
        if (type == null)
        {
            return new DOSIFormHandlerCompileResult
            {
                Success = false,
                Diagnostics = new[] { $"Compiled assembly missing type '{GeneratedClassName}'." }
            };
        }

        var bound = new Dictionary<string, Delegate>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in handlers)
        {
            var mi = type.GetMethod(h.MethodName, BindingFlags.Public | BindingFlags.Static);
            if (mi == null) continue;
            try
            {
                var del = Delegate.CreateDelegate(h.Sig.DelegateType, mi);
                bound[$"{h.OwnerName}.{h.Event}"] = del;
            }
            catch (Exception ex)
            {
                diags.Add($"Couldn't bind {h.OwnerName}.{h.Event}: {ex.Message}");
            }
        }

        return new DOSIFormHandlerCompileResult
        {
            Success = true,
            Diagnostics = diags,
            Handlers = bound
        };
    }

    private static string SanitizeIdent(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
        }
        if (sb.Length == 0) return "_";
        if (char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    /// <summary>
    /// Holds the open-generic shape of an event so we can synthesise a method
    /// with a matching signature. Keep this list aligned with the BindEvent
    /// implementations in DOSIDesignerControlCatalog.
    /// </summary>
    private readonly struct EventSig
    {
        public required string SenderType { get; init; }
        public required string ArgsType { get; init; }
        public required Type DelegateType { get; init; }
    }

    private static EventSig? ResolveEventSignature(string typeKey, string evName)
    {
        // (typeKey, evName) -> the closed delegate type the runtime expects.
        // Matches the BindEvent casts in DOSIDesignerControlCatalog.Build().
        return (typeKey.ToLowerInvariant(), evName.ToLowerInvariant()) switch
        {
            ("dosibutton", "click") => new EventSig
            {
                SenderType = "object",
                ArgsType = "Avalonia.Interactivity.RoutedEventArgs",
                DelegateType = typeof(EventHandler<Avalonia.Interactivity.RoutedEventArgs>)
            },
            ("dosilabel", "click") => new EventSig
            {
                SenderType = "object",
                ArgsType = "Avalonia.Interactivity.RoutedEventArgs",
                DelegateType = typeof(EventHandler<Avalonia.Interactivity.RoutedEventArgs>)
            },
            ("dositextbox", "textchanged") => new EventSig
            {
                SenderType = "object",
                ArgsType = "DOSI.CORE.UIComponents.TextChangedEventArgs",
                DelegateType = typeof(EventHandler<DOSI.CORE.UIComponents.TextChangedEventArgs>)
            },
            ("dosislider", "valuechanged") => new EventSig
            {
                SenderType = "object",
                ArgsType = "double",
                DelegateType = typeof(EventHandler<double>)
            },
            _ => null
        };
    }

    /// <summary>
    /// Form-level event signatures. Maps to events on the host DOSIWindow:
    /// Load -&gt; Avalonia's Loaded (fires once after attach), Closing -&gt;
    /// DOSIWindow.Closing (cancellable via the args).
    /// </summary>
    private static EventSig? ResolveFormEventSignature(string evName) =>
        evName.ToLowerInvariant() switch
        {
            "load" => new EventSig
            {
                SenderType = "object",
                ArgsType = "Avalonia.Interactivity.RoutedEventArgs",
                DelegateType = typeof(EventHandler<Avalonia.Interactivity.RoutedEventArgs>)
            },
            "closing" => new EventSig
            {
                SenderType = "object",
                ArgsType = "DOSI.CORE.UIComponents.WindowManagement.DOSIWindowClosingEventArgs",
                DelegateType = typeof(EventHandler<DOSI.CORE.UIComponents.WindowManagement.DOSIWindowClosingEventArgs>)
            },
            "closed" => new EventSig
            {
                SenderType = "object",
                ArgsType = "DOSI.CORE.UIComponents.WindowManagement.DOSIWindowEventArgs",
                DelegateType = typeof(EventHandler<DOSI.CORE.UIComponents.WindowManagement.DOSIWindowEventArgs>)
            },
            _ => null
        };
}
