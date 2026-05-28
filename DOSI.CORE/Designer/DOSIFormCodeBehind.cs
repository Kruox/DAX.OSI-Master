using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DOSI.CORE.Designer;

/// <summary>
/// Generates and parses the C# "code-behind" view for a <see cref="DOSIFormDocument"/>.
/// Mirrors the VB / WinForms experience: every control with a primary event
/// gets a stub method, and the user types their logic inside the body. The
/// document's <see cref="DOSIFormControlDef.Handlers"/> dictionary is the
/// single source of truth - generation reads from it, parsing writes back.
/// </summary>
public static class DOSIFormCodeBehind
{
    private const string ClassName = "Form";

    /// <summary>
    /// Produces a full C# file containing one method per (control, primaryEvent)
    /// pair. Methods that already have user code keep their bodies; the rest
    /// get an empty stub so the user can just start typing.
    /// </summary>
    public static string Generate(DOSIFormDocument doc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// =================================================================");
        sb.AppendLine("// AUTO-GENERATED CODE-BEHIND for: " + doc.Title);
        sb.AppendLine("// You can edit any method body - your changes are saved back into");
        sb.AppendLine("// the .dosiform document. Do NOT rename the methods or change their");
        sb.AppendLine("// signatures: they're matched by name to the controls on the form.");
        sb.AppendLine("// =================================================================");
        sb.AppendLine("using System;");
        sb.AppendLine("using Avalonia;");
        sb.AppendLine("using Avalonia.Controls;");
        sb.AppendLine("using Avalonia.Interactivity;");
        sb.AppendLine("using Avalonia.Media;");
        sb.AppendLine("using DOSI.CORE.UIComponents;");
        sb.AppendLine();
        sb.Append("public static class ").AppendLine(ClassName);
        sb.AppendLine("{");

        // ----- Form-level stubs first -----
        var formEvents = new[]
        {
            ("Load",    ("object", "Avalonia.Interactivity.RoutedEventArgs")),
            ("Closing", ("object", "DOSI.CORE.UIComponents.WindowManagement.DOSIWindowClosingEventArgs")),
            ("Closed",  ("object", "DOSI.CORE.UIComponents.WindowManagement.DOSIWindowEventArgs"))
        };
        var anyForm = false;
        foreach (var (evName, sigPair) in formEvents)
        {
            var hasBody = doc.Handlers.TryGetValue(evName, out var fbody) && !string.IsNullOrWhiteSpace(fbody);
            if (anyForm || hasBody)
            {
                if (anyForm) sb.AppendLine();
                anyForm = true;
                sb.Append("    // [Form].").AppendLine(evName);
                sb.Append("    public static void Form_").Append(evName).Append('(')
                  .Append(sigPair.Item1).Append(" sender, ")
                  .Append(sigPair.Item2).AppendLine(" e)");
                sb.AppendLine("    {");
                if (hasBody)
                {
                    foreach (var line in fbody!.Replace("\r\n", "\n").Split('\n'))
                        sb.Append("        ").AppendLine(line);
                }
                else
                {
                    foreach (var line in DefaultBodyFor("Form", evName).Split('\n'))
                        sb.Append("        ").AppendLine(line.TrimEnd());
                }
                sb.AppendLine("    }");
            }
        }

        var any = anyForm;
        foreach (var def in doc.Controls)
        {
            var entry = DOSIDesignerControlCatalog.Find(def.Type);
            if (entry?.PrimaryEvent == null) continue;
            if (string.IsNullOrWhiteSpace(def.Name)) continue;

            // Emit a stub for the primary event always (so a freshly-dropped
            // control's main event is one keystroke away). For secondary
            // events, only emit when the user actually wrote code - otherwise
            // the file would balloon with unused stubs.
            var events = entry.Events ?? new[] { entry.PrimaryEvent };
            foreach (var evName in events)
            {
                var sig = SignatureFor(def.Type, evName);
                if (sig == null) continue;

                var hasBody = def.Handlers.TryGetValue(evName, out var body)
                              && !string.IsNullOrWhiteSpace(body);
                var isPrimary = string.Equals(evName, entry.PrimaryEvent, StringComparison.OrdinalIgnoreCase);
                if (!hasBody && !isPrimary) continue;

                var methodName = $"{def.Name}_{evName}";

                if (any) sb.AppendLine();
                any = true;

                sb.Append("    // [").Append(def.Type).Append(" '").Append(def.Name).Append("'].")
                  .Append(evName).AppendLine();
                sb.Append("    public static void ").Append(methodName).Append('(')
                  .Append(sig.Value.SenderType).Append(" sender, ")
                  .Append(sig.Value.ArgsType).AppendLine(" e)");
                sb.AppendLine("    {");
                if (hasBody)
                {
                    foreach (var line in body!.Replace("\r\n", "\n").Split('\n'))
                        sb.Append("        ").AppendLine(line);
                }
                else
                {
                    var hint = def.Type + " '" + def.Name + "'";
                    foreach (var line in DefaultBodyFor(hint, evName).Split('\n'))
                        sb.Append("        ").AppendLine(line.TrimEnd());
                }
                sb.AppendLine("    }");
            }
        }

        if (!any)
        {
            sb.AppendLine("    // No controls with editable events yet.");
            sb.AppendLine("    // Drop a Button, TextBox, Slider, or CheckBox onto the form,");
            sb.AppendLine("    // then come back here and a stub method will appear.");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Renders a single handler stub (the same shape <see cref="Generate"/>
    /// would emit, but indented one level so the caller can splice it inside
    /// an existing <c>public static class</c> body). Used by the IDE when
    /// the user double-clicks a control AFTER the code-behind tab is
    /// already open - we append just the missing stub instead of clobbering
    /// the user's in-flight edits with a full regen. Returns <c>null</c>
    /// for unknown control names or events without a known signature.
    /// </summary>
    public static string? GenerateStub(DOSIFormDocument doc, string controlName, string eventName)
    {
        if (string.IsNullOrWhiteSpace(controlName) || string.IsNullOrWhiteSpace(eventName))
            return null;

        // Form-level events: synthetic owner "Form" -> Form_<event>.
        if (string.Equals(controlName, "Form", StringComparison.OrdinalIgnoreCase))
        {
            var fsig = FormSignatureFor(eventName);
            if (fsig == null) return null;
            return BuildStub("Form", $"Form_{eventName}", eventName, fsig.Value);
        }

        // Per-control event: look up signature from the catalog.
        var def = doc.Controls.FirstOrDefault(c =>
            string.Equals(c.Name, controlName, StringComparison.Ordinal));
        if (def == null) return null;
        var sig = SignatureFor(def.Type, eventName);
        if (sig == null) return null;
        return BuildStub(def.Type + " '" + def.Name + "'",
                         $"{def.Name}_{eventName}", eventName, sig.Value);
    }

    private static string BuildStub(string ownerHint, string methodName,
                                    string eventName, (string SenderType, string ArgsType) sig)
    {
        var sb = new StringBuilder();
        sb.Append("    // [").Append(ownerHint).Append("].").Append(eventName).AppendLine();
        sb.Append("    public static void ").Append(methodName).Append('(')
          .Append(sig.SenderType).Append(" sender, ")
          .Append(sig.ArgsType).AppendLine(" e)");
        sb.AppendLine("    {");
        foreach (var line in DefaultBodyFor(ownerHint, eventName).Split('\n'))
            sb.Append("        ").AppendLine(line.TrimEnd());
        sb.AppendLine("    }");
        return sb.ToString();
    }

    /// <summary>
    /// Per-(control, event) opinionated starter body. Beats a generic
    /// "Write your code here." comment because the user can immediately
    /// see HOW to interact with sender / args - the most common friction
    /// point reported on the visual designer.
    /// </summary>
    private static string DefaultBodyFor(string ownerHint, string eventName)
    {
        // Owner hint shape is "&lt;Type&gt; '&lt;name&gt;'" for control events,
        // and just "Form" for form-level events. We only need the type
        // prefix to specialise the stub.
        bool isButton = ownerHint.StartsWith("DOSIButton", StringComparison.OrdinalIgnoreCase);
        bool isLabel = ownerHint.StartsWith("DOSILabel", StringComparison.OrdinalIgnoreCase);
        bool isTextBox = ownerHint.StartsWith("DOSITextBox", StringComparison.OrdinalIgnoreCase);
        bool isSlider = ownerHint.StartsWith("DOSISlider", StringComparison.OrdinalIgnoreCase);
        bool isForm = ownerHint.Equals("Form", StringComparison.OrdinalIgnoreCase);

        return (isForm, eventName.ToLowerInvariant()) switch
        {
            (true,  "load")    => "// Fires once after the form is shown.\n// Write initial setup here.",
            (true,  "closing") => "// Set e.Cancel = true to prevent the form from closing.",
            (true,  "closed")  => "// The form has closed. Release any resources here.",
            _ when isButton    => "var btn = (DOSIButton)sender;\n// Toast example - shown on whichever DOSI screen is active.\n// (Use the parameterless DOSIPopNotification.Show(text) overload;\n//  the host-aware overload requires a Panel you don't have here.)\nDOSIPopNotification.Show($\"You clicked '{btn.Text}'\");",
            _ when isLabel     => "var label = (DOSILabel)sender;\n// The label was clicked. Read or change label.Text here.\nDOSIPopNotification.Show($\"Label clicked: '{label.Text}'\");",
            _ when isTextBox   => "var box = (DOSITextBox)sender;\n// box.Text is the current contents.\nSystem.Diagnostics.Debug.WriteLine(box.Text);",
            _ when isSlider    => "var slider = (DOSISlider)sender;\n// e is the new value (double).\nSystem.Diagnostics.Debug.WriteLine($\"Slider: {(int)e}\");",
            _ => "// Write your code here."
        };
    }

    private static (string SenderType, string ArgsType)? FormSignatureFor(string evName) =>
        evName.ToLowerInvariant() switch
        {
            "load"    => ("object", "Avalonia.Interactivity.RoutedEventArgs"),
            "closing" => ("object", "DOSI.CORE.UIComponents.WindowManagement.DOSIWindowClosingEventArgs"),
            "closed"  => ("object", "DOSI.CORE.UIComponents.WindowManagement.DOSIWindowEventArgs"),
            _ => null
        };

    /// <summary>
    /// Parses code-behind text and updates <paramref name="doc"/>'s handler
    /// bodies in place. Methods named <c>&lt;ControlName&gt;_&lt;Event&gt;</c>
    /// that match a control on the form are written to <see cref="DOSIFormControlDef.Handlers"/>.
    /// Unknown methods are ignored (so the user can add helpers without breaking anything).
    /// Returns parse diagnostics (errors only); empty list means clean parse.
    /// </summary>
    public static IReadOnlyList<string> Parse(string code, DOSIFormDocument doc)
    {
        var diags = new List<string>();
        if (string.IsNullOrWhiteSpace(code)) return diags;

        SyntaxTree tree;
        try { tree = CSharpSyntaxTree.ParseText(code); }
        catch (Exception ex)
        {
            diags.Add("Parse failed: " + ex.Message);
            return diags;
        }

        // Surface any parse errors so the user knows something's wrong before
        // hitting Run (which would also fail to compile).
        foreach (var d in tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
            diags.Add(d.ToString());

        var root = tree.GetRoot();

        // Build a lookup so we can match method names back to controls in O(1).
        var lookup = new Dictionary<string, (DOSIFormControlDef? Def, string Event)>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in doc.Controls)
        {
            var entry = DOSIDesignerControlCatalog.Find(def.Type);
            if (entry?.PrimaryEvent == null || string.IsNullOrWhiteSpace(def.Name)) continue;
            var events = entry.Events ?? new[] { entry.PrimaryEvent };
            foreach (var ev in events)
                lookup[$"{def.Name}_{ev}"] = (def, ev);
        }
        // Form-level handler slots: any Form_<X> in the source maps here.
        // Def is null to signal "write to doc.Handlers instead of def.Handlers".
        lookup["Form_Load"]    = (null, "Load");
        lookup["Form_Closing"] = (null, "Closing");
        lookup["Form_Closed"]  = (null, "Closed");

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var name = method.Identifier.Text;
            if (!lookup.TryGetValue(name, out var hit)) continue;

            var body = method.Body;
            string snippet;
            if (body == null)
            {
                snippet = string.Empty;
            }
            else
            {
                // Strip the outermost { } and the leading per-line indentation so
                // the snippet stored in JSON reads naturally (the runtime compiler
                // re-indents it when synthesising the wrapper class).
                var inner = body.ToString();
                inner = TrimBraces(inner);
                snippet = StripIndent(inner);
            }

            if (hit.Def != null)
                hit.Def.Handlers[hit.Event] = snippet;
            else if (!string.IsNullOrWhiteSpace(snippet))
                doc.Handlers[hit.Event] = snippet;
            else
                doc.Handlers.Remove(hit.Event);
        }

        return diags;
    }

    private static string TrimBraces(string s)
    {
        s = s.Trim();
        if (s.StartsWith('{')) s = s[1..];
        if (s.EndsWith('}')) s = s[..^1];
        return s.Trim('\r', '\n');
    }

    private static string StripIndent(string s)
    {
        var lines = s.Replace("\r\n", "\n").Split('\n');
        // Find the smallest leading-whitespace count among non-empty lines.
        var min = int.MaxValue;
        foreach (var l in lines)
        {
            if (string.IsNullOrWhiteSpace(l)) continue;
            var n = 0;
            while (n < l.Length && l[n] == ' ') n++;
            if (n < min) min = n;
        }
        if (min == int.MaxValue || min == 0) return string.Join("\n", lines);
        return string.Join("\n", lines.Select(l => l.Length >= min ? l.Substring(min) : l));
    }

    /// <summary>
    /// Same (typeKey, event) -> signature mapping the runtime compiler uses,
    /// surfaced here so the generated code-behind shows the correct args type
    /// the user will be working with.
    /// </summary>
    private static (string SenderType, string ArgsType)? SignatureFor(string typeKey, string evName) =>
        (typeKey.ToLowerInvariant(), evName.ToLowerInvariant()) switch
        {
            ("dosibutton", "click")             => ("object", "Avalonia.Interactivity.RoutedEventArgs"),
            ("dosilabel", "click")              => ("object", "Avalonia.Interactivity.RoutedEventArgs"),
            ("dositextbox", "textchanged")      => ("object", "DOSI.CORE.UIComponents.TextChangedEventArgs"),
            ("dosislider", "valuechanged")      => ("object", "double"),
            _ => null
        };
}
