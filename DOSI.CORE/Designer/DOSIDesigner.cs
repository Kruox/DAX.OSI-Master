using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DOSI.CORE.AccentManagement;
using DOSI.CORE.UIComponents;
using DOSI.CORE.UIComponents.WindowManagement;
// Alias: both DOSI.CORE.UIComponents and Avalonia.Controls expose a
// TextChangedEventArgs. The cast in DOSITextBox's BindEvent needs the DOSI
// one explicitly to avoid CS0104 ambiguity at the call site.
using DosiTextChangedEventArgs = DOSI.CORE.UIComponents.TextChangedEventArgs;

namespace DOSI.CORE.Designer;

// =============================================================================
// DOSI Visual Designer (MVP)
//
// Persistence model: sidecar .dosiform JSON file. Hosts (typically a code
// editor) recognise this extension and open it inside DOSIDesigner instead
// of DOSICodeEditor; the Run path feeds the file through DOSIFormLoader to
// build a real DOSIWindow at runtime.
//
// Out of scope for MVP (intentional - additive next steps):
//   - Editing event handlers / code-behind
//   - Mixing custom user C# with the form
//   - Multi-select, marquee, alignment guides
//   - Undo / redo
//   - Code-gen to .cs
//
// What it covers:
//   - Toolbox of DOSI controls (drag onto the canvas)
//   - Click-to-select + arrow-key nudge + drag-to-move + corner resize
//   - Snap-to-grid (8 px), grid overlay
//   - Live property grid for the selected control
//   - Document JSON load/save, dirty tracking, runtime loader
// =============================================================================

#region ── Document model ────────────────────────────────────────────────────

/// <summary>
/// Serialisable description of a single placed control on the form. The
/// <see cref="Type"/> is one of the keys in <see cref="DOSIDesignerControlCatalog"/>;
/// <see cref="Properties"/> holds simple JSON-friendly values keyed by the
/// catalog's editable property names (see <see cref="DOSIDesignerProperty"/>).
/// </summary>
/// <summary>
/// How a control attaches to the form's edges. Applied at runtime by the
/// loader so a designed-once form re-flows when the user resizes the running
/// window. Drives the WinForms-style "snap to edge" experience.
/// </summary>
public enum DOSIDock
{
    None,
    Top,
    Bottom,
    Left,
    Right,
    Fill
}

public sealed class DOSIFormControlDef
{
    public string Type { get; set; } = "";
    /// <summary>
    /// Stable identifier the runtime uses to find this control's preview
    /// instance + bind its event handlers. Auto-generated when the control
    /// is first dropped (e.g. "button1") and editable in the property grid.
    /// </summary>
    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 100;
    public double Height { get; set; } = 32;
    /// <summary>
    /// Edge the control snaps to at runtime. <see cref="DOSIDock.None"/>
    /// keeps the absolute X/Y/Width/Height the user laid out in the designer.
    /// </summary>
    public DOSIDock Dock { get; set; } = DOSIDock.None;
    public Dictionary<string, JsonElement> Properties { get; set; } = new();
    /// <summary>
    /// Event-name -&gt; C# code body. The runtime synthesises one method per
    /// entry, compiles them all into a single class, and wires each method
    /// to the matching event on the live preview instance.
    /// </summary>
    public Dictionary<string, string> Handlers { get; set; } = new();
}

/// <summary>Top-level document persisted as the <c>.dosiform</c> file.</summary>
public sealed class DOSIFormDocument
{
    public int FormatVersion { get; set; } = 1;
    public string Title { get; set; } = "Form";
    public double Width { get; set; } = 480;
    public double Height { get; set; } = 320;
    /// <summary>
    /// Whether the form's chrome shows a maximize button. Maps to
    /// <c>DOSIWindow.CanMaximize</c> at runtime.
    /// </summary>
    public bool CanMaximize { get; set; } = true;
    /// <summary>
    /// Whether the form's chrome shows a minimize button. Maps to
    /// <c>DOSIWindow.CanMinimize</c> at runtime.
    /// </summary>
    public bool CanMinimize { get; set; } = true;
    public List<DOSIFormControlDef> Controls { get; set; } = new();
    /// <summary>
    /// Form-level event handlers (Load, Closing). Stored alongside per-control
    /// handlers but bound to the host <c>DOSIWindow</c> instead. The handler
    /// compiler treats this as a synthetic control whose Name is "Form".
    /// </summary>
    public Dictionary<string, string> Handlers { get; set; } = new();
}

/// <summary>
/// Payload for <see cref="DOSIDesigner.EditHandlerRequested"/>. Identifies the
/// control + event the user wants to write code for; the IDE responds by
/// opening (or focusing) a code-behind tab.
/// </summary>
public sealed class DOSIDesignerEditHandlerRequestedEventArgs : EventArgs
{
    public required string ControlName { get; init; }
    public required string EventName { get; init; }
}

/// <summary>
/// Payload for <see cref="DOSIDesigner.ControlRenamed"/>. Carries the old
/// and new name of a control so the IDE can rewrite any open code-behind
/// buffers (specifically: method names like <c>OldName_Click</c> become
/// <c>NewName_Click</c>) before the user hits Run.
/// </summary>
public sealed class DOSIDesignerControlRenamedEventArgs : EventArgs
{
    public required string OldName { get; init; }
    public required string NewName { get; init; }
}

/// <summary>JSON load/save for <see cref="DOSIFormDocument"/>.</summary>
public static class DOSIFormSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    public static string Serialize(DOSIFormDocument doc) =>
        JsonSerializer.Serialize(doc, Options);

    public static DOSIFormDocument? TryDeserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new DOSIFormDocument();
        try { return JsonSerializer.Deserialize<DOSIFormDocument>(json, Options); }
        catch { return null; }
    }

    public static DOSIFormDocument Load(string path)
    {
        if (!File.Exists(path)) return new DOSIFormDocument();
        try
        {
            // Route through UserVault so encrypted .dosiform files (saved
            // when the active user has a vault unlocked - which is now
            // the default after sign-in) decrypt transparently. The
            // vault's ReadAllText falls through to plain File.ReadAllText
            // when the file isn't encrypted, so legacy plaintext forms
            // still load cleanly. Without this the encrypted bytes hit
            // the JSON deserializer, fail, and the IDE silently opens a
            // blank form - which read as "save didn't persist" because
            // the on-disk file IS up-to-date but unreadable through the
            // wrong code path.
            var json = DOSI.CORE.Security.UserVault.ReadAllText(path);
            return TryDeserialize(json) ?? new DOSIFormDocument();
        }
        catch { return new DOSIFormDocument(); }
    }

    public static void Save(string path, DOSIFormDocument doc) =>
        // Symmetric with Load: WriteAllText encrypts when the vault is
        // unlocked, falls through to plaintext when locked - a guest /
        // pre-setup flow can still save forms.
        DOSI.CORE.Security.UserVault.WriteAllText(path, Serialize(doc));
}

#endregion

#region ── Control catalog (what you can drop onto a form) ───────────────────

/// <summary>
/// Type of editor used for a control's property in the property grid. Keeps
/// the grid driven by metadata so adding a new control type is a matter of
/// listing its properties, not writing UI plumbing.
/// </summary>
public enum DOSIDesignerPropertyKind
{
    String,
    Bool,
    Int,
    Double
}

/// <summary>
/// One editable property of a designable control. The catalog uses these to
/// (a) build the property grid and (b) push values into the live preview when
/// the user types.
/// </summary>
public sealed class DOSIDesignerProperty
{
    public required string Name { get; init; }
    public required DOSIDesignerPropertyKind Kind { get; init; }
    public required object Default { get; init; }
}

/// <summary>Catalog entry: one designable control type.</summary>
public sealed class DOSIDesignerControlEntry
{
    public required string TypeKey { get; init; }              // e.g. "DOSIButton"
    public required string DisplayName { get; init; }          // toolbox label
    public required Func<Control> Factory { get; init; }       // build a fresh instance
    public required Action<Control, string, JsonElement> Apply;// push property value
    public required IReadOnlyList<DOSIDesignerProperty> Properties { get; init; }
    public Size DefaultSize { get; init; } = new(120, 32);

    /// <summary>
    /// Name of the event a double-click in the designer should open for this
    /// control type (e.g. "Click" for buttons). Null when the control has no
    /// natural primary event - double-click does nothing in that case.
    /// </summary>
    public string? PrimaryEvent { get; init; }

    /// <summary>
    /// All events the property-panel "Events" section should list for this
    /// control type. The first entry is conventionally the same as
    /// <see cref="PrimaryEvent"/>. Empty / null falls back to just the
    /// primary event so existing entries don't need to set this.
    /// </summary>
    public IReadOnlyList<string>? Events { get; init; }

    /// <summary>
    /// Subscribes <paramref name="handler"/> to the named event on the live
    /// runtime instance. Returns true if the binding succeeded. Designer
    /// preview instances pass through the SAME binder so what you see at
    /// design time and what runs at runtime stay in lockstep.
    /// </summary>
    public Func<Control, string, Delegate, bool>? BindEvent { get; init; }
}

/// <summary>
/// Single source of truth for "what controls exist in the designer". Adding
/// a new draggable control = appending one entry here.
/// </summary>
public static class DOSIDesignerControlCatalog
{
    private static readonly List<DOSIDesignerControlEntry> _entries = Build();

    public static IReadOnlyList<DOSIDesignerControlEntry> Entries => _entries;

    public static DOSIDesignerControlEntry? Find(string typeKey) =>
        _entries.FirstOrDefault(e =>
            string.Equals(e.TypeKey, typeKey, StringComparison.OrdinalIgnoreCase));

    private static List<DOSIDesignerControlEntry> Build() => new()
    {
        new DOSIDesignerControlEntry
        {
            TypeKey = "DOSIButton",
            DisplayName = "Button",
            Factory = () => new DOSIButton { Text = "Button" },
            Apply = (c, n, v) =>
            {
                if (c is DOSIButton b && string.Equals(n, "Text", StringComparison.OrdinalIgnoreCase))
                    b.Text = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
            },
            Properties = new[]
            {
                new DOSIDesignerProperty { Name = "Text", Kind = DOSIDesignerPropertyKind.String, Default = "Button" }
            },
            DefaultSize = new Size(120, 32),
            PrimaryEvent = "Click",
            BindEvent = (c, ev, h) =>
            {
                if (c is DOSIButton b && string.Equals(ev, "Click", StringComparison.OrdinalIgnoreCase))
                {
                    b.Click += (EventHandler<Avalonia.Interactivity.RoutedEventArgs>)h;
                    return true;
                }
                return false;
            }
        },

        new DOSIDesignerControlEntry
        {
            TypeKey = "DOSITextBox",
            DisplayName = "TextBox",
            Factory = () => new DOSITextBox { PlaceholderText = "Enter text..." },
            Apply = (c, n, v) =>
            {
                if (c is not DOSITextBox tb) return;
                switch (n.ToLowerInvariant())
                {
                    case "text":            tb.Text = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString(); break;
                    case "placeholdertext": tb.PlaceholderText = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString(); break;
                    case "useroundedends":  tb.UseRoundedEnds = v.ValueKind == JsonValueKind.True; break;
                    case "usepasswordchar": tb.UsePasswordChar = v.ValueKind == JsonValueKind.True; break;
                }
            },
            Properties = new[]
            {
                new DOSIDesignerProperty { Name = "Text",            Kind = DOSIDesignerPropertyKind.String, Default = "" },
                new DOSIDesignerProperty { Name = "PlaceholderText", Kind = DOSIDesignerPropertyKind.String, Default = "Enter text..." },
                new DOSIDesignerProperty { Name = "UseRoundedEnds",  Kind = DOSIDesignerPropertyKind.Bool,   Default = false },
                new DOSIDesignerProperty { Name = "UsePasswordChar", Kind = DOSIDesignerPropertyKind.Bool,   Default = false }
            },
            DefaultSize = new Size(180, 32),
            PrimaryEvent = "TextChanged",
            BindEvent = (c, ev, h) =>
            {
                if (c is DOSITextBox tb && string.Equals(ev, "TextChanged", StringComparison.OrdinalIgnoreCase))
                {
                    tb.TextChanged += (EventHandler<DosiTextChangedEventArgs>)h;
                    return true;
                }
                return false;
            }
        },

        new DOSIDesignerControlEntry
        {
            TypeKey = "DOSILabel",
            DisplayName = "Label",
            Factory = () => new DOSILabel { Text = "Label" },
            Apply = (c, n, v) =>
            {
                if (c is not DOSILabel l) return;
                switch (n.ToLowerInvariant())
                {
                    case "text":
                        l.Text = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
                        break;
                    case "fontsize":
                        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && d > 4 && d < 200)
                            l.FontSize = d;
                        break;
                    case "usedropshadow":
                        l.UseDropShadow = v.ValueKind == JsonValueKind.True;
                        break;
                }
            },
            Properties = new[]
            {
                new DOSIDesignerProperty { Name = "Text",          Kind = DOSIDesignerPropertyKind.String, Default = "Label" },
                new DOSIDesignerProperty { Name = "FontSize",      Kind = DOSIDesignerPropertyKind.Double, Default = 13.0 },
                new DOSIDesignerProperty { Name = "UseDropShadow", Kind = DOSIDesignerPropertyKind.Bool,   Default = true }
            },
            DefaultSize = new Size(120, 24),
            PrimaryEvent = "Click",
            BindEvent = (c, ev, h) =>
            {
                if (c is DOSILabel l && string.Equals(ev, "Click", StringComparison.OrdinalIgnoreCase))
                {
                    l.Click += (EventHandler<Avalonia.Interactivity.RoutedEventArgs>)h;
                    return true;
                }
                return false;
            }
        },

        new DOSIDesignerControlEntry
        {
            TypeKey = "DOSISlider",
            DisplayName = "Slider",
            Factory = () => new DOSISlider { Minimum = 0, Maximum = 100, Value = 50 },
            Apply = (c, n, v) =>
            {
                if (c is not DOSISlider s) return;
                switch (n.ToLowerInvariant())
                {
                    case "minimum": if (v.TryGetDouble(out var min)) s.Minimum = min; break;
                    case "maximum": if (v.TryGetDouble(out var max)) s.Maximum = max; break;
                    case "value":   if (v.TryGetDouble(out var val)) s.Value = val; break;
                }
            },
            Properties = new[]
            {
                new DOSIDesignerProperty { Name = "Minimum", Kind = DOSIDesignerPropertyKind.Double, Default = 0.0 },
                new DOSIDesignerProperty { Name = "Maximum", Kind = DOSIDesignerPropertyKind.Double, Default = 100.0 },
                new DOSIDesignerProperty { Name = "Value",   Kind = DOSIDesignerPropertyKind.Double, Default = 50.0 }
            },
            DefaultSize = new Size(200, 32),
            PrimaryEvent = "ValueChanged",
            BindEvent = (c, ev, h) =>
            {
                if (c is DOSISlider s && string.Equals(ev, "ValueChanged", StringComparison.OrdinalIgnoreCase))
                {
                    s.ValueChanged += (EventHandler<double>)h;
                    return true;
                }
                return false;
            }
        }

        // NOTE: Only DOSI custom controls live in the toolbox by design.
        // Label / CheckBox / Panel / Image (raw Avalonia primitives) were
        // removed because they don't theme with the rest of the OS and lead
        // to mixed-look forms. When DOSILabel / DOSICheckBox / DOSIPanel land
        // they should be added here using the same catalog-entry shape.
    };
}

#endregion

#region ── Runtime loader (turns a document into a live DOSIWindow) ──────────

/// <summary>
/// Builds a live <see cref="DOSIWindow"/> from a <see cref="DOSIFormDocument"/>.
/// Used by the IDE Run path so visually-built forms launch as real DOSI apps.
/// </summary>
public static class DOSIFormLoader
{
    public static DOSIWindow Build(DOSIFormDocument doc) => Build(doc, out _);

    /// <summary>
    /// Build overload that also reports any handler-compilation diagnostics
    /// (so the IDE Run output can surface compile errors in user-written
    /// event handler code without crashing the launch).
    /// </summary>
    public static DOSIWindow Build(DOSIFormDocument doc, out IReadOnlyList<string> handlerDiagnostics)
    {
        // Compile all per-control event handlers in one pass. If anything in
        // the user's code fails to compile we still build the window - the
        // form opens, just with the broken handlers unwired - and surface
        // the diagnostics so they show up in the Output pane.
        var compileResult = DOSIFormHandlerCompiler.Compile(doc);
        handlerDiagnostics = compileResult.Diagnostics;

        var canvas = new Canvas
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        // Track docked controls so we can re-position them when the form
        // window is resized at runtime. Non-docked controls stay where the
        // designer placed them.
        var docked = new List<(Control Ctrl, DOSIDock Dock, double DesignedW, double DesignedH)>();

        foreach (var def in doc.Controls)
        {
            var entry = DOSIDesignerControlCatalog.Find(def.Type);
            if (entry == null) continue;

            var ctrl = entry.Factory();
            foreach (var (k, v) in def.Properties) entry.Apply(ctrl, k, v);
            ctrl.Width = def.Width;
            ctrl.Height = def.Height;
            Canvas.SetLeft(ctrl, def.X);
            Canvas.SetTop(ctrl, def.Y);

            // Attach any user-compiled handlers for this control.
            if (entry.BindEvent != null && !string.IsNullOrWhiteSpace(def.Name))
            {
                foreach (var (evName, _) in def.Handlers)
                {
                    if (compileResult.Handlers.TryGetValue($"{def.Name}.{evName}", out var del))
                    {
                        try { entry.BindEvent(ctrl, evName, del); }
                        catch { /* malformed binding; surfaced as diagnostic above */ }
                    }
                }
            }

            if (def.Dock != DOSIDock.None)
                docked.Add((ctrl, def.Dock, def.Width, def.Height));

            canvas.Children.Add(ctrl);
        }

        // Live re-flow for docked controls. The DOSIWindow's content area
        // is the canvas, so SizeChanged fires whenever the user resizes.
        if (docked.Count > 0)
        {
            void Reflow()
            {
                var w = canvas.Bounds.Width;
                var h = canvas.Bounds.Height;
                if (w <= 0 || h <= 0) return;
                foreach (var d in docked)
                {
                    switch (d.Dock)
                    {
                        case DOSIDock.Top:
                            Canvas.SetLeft(d.Ctrl, 0); Canvas.SetTop(d.Ctrl, 0);
                            d.Ctrl.Width = w; d.Ctrl.Height = d.DesignedH;
                            break;
                        case DOSIDock.Bottom:
                            Canvas.SetLeft(d.Ctrl, 0); Canvas.SetTop(d.Ctrl, Math.Max(0, h - d.DesignedH));
                            d.Ctrl.Width = w; d.Ctrl.Height = d.DesignedH;
                            break;
                        case DOSIDock.Left:
                            Canvas.SetLeft(d.Ctrl, 0); Canvas.SetTop(d.Ctrl, 0);
                            d.Ctrl.Width = d.DesignedW; d.Ctrl.Height = h;
                            break;
                        case DOSIDock.Right:
                            Canvas.SetLeft(d.Ctrl, Math.Max(0, w - d.DesignedW)); Canvas.SetTop(d.Ctrl, 0);
                            d.Ctrl.Width = d.DesignedW; d.Ctrl.Height = h;
                            break;
                        case DOSIDock.Fill:
                            Canvas.SetLeft(d.Ctrl, 0); Canvas.SetTop(d.Ctrl, 0);
                            d.Ctrl.Width = w; d.Ctrl.Height = h;
                            break;
                    }
                }
            }
            canvas.SizeChanged += (_, _) => Reflow();
            // First-pass after the canvas measures itself.
            canvas.AttachedToVisualTree += (_, _) =>
                Avalonia.Threading.Dispatcher.UIThread.Post(Reflow,
                    Avalonia.Threading.DispatcherPriority.Loaded);
        }

        var window = new DOSIWindow
        {
            Title = string.IsNullOrWhiteSpace(doc.Title) ? "Form" : doc.Title,
            WindowWidth = Math.Max(160, doc.Width),
            WindowHeight = Math.Max(120, doc.Height),
            CanMaximize = doc.CanMaximize,
            CanMinimize = doc.CanMinimize,
            Content = canvas
        };
        WireFormHandlers(window, doc, compileResult);
        return window;
    }

    public static DOSIWindow BuildFromFile(string path) => Build(DOSIFormSerializer.Load(path));

    /// <summary>
    /// Attaches the form-level event handlers (Load / Closing) compiled by
    /// <see cref="DOSIFormHandlerCompiler"/>. Form handlers are keyed under
    /// the synthetic name "Form" so the same delegate-lookup code path used
    /// for per-control handlers applies.
    /// </summary>
    private static void WireFormHandlers(DOSIWindow window,
        DOSIFormDocument doc, DOSIFormHandlerCompileResult compileResult)
    {
        foreach (var (evName, _) in doc.Handlers)
        {
            if (!compileResult.Handlers.TryGetValue($"Form.{evName}", out var del)) continue;
            try
            {
                switch (evName.ToLowerInvariant())
                {
                    case "load":
                        // Avalonia's Loaded event fires after the visual tree
                        // is attached - the closest analogue to WinForms Load.
                        var loadHandler = (EventHandler<Avalonia.Interactivity.RoutedEventArgs>)del;
                        window.Loaded += loadHandler;
                        break;
                    case "closing":
                        var closingHandler = (EventHandler<DOSI.CORE.UIComponents.WindowManagement.DOSIWindowClosingEventArgs>)del;
                        window.Closing += closingHandler;
                        break;
                    case "closed":
                        var closedHandler = (EventHandler<DOSI.CORE.UIComponents.WindowManagement.DOSIWindowEventArgs>)del;
                        window.Closed += closedHandler;
                        break;
                }
            }
            catch { /* malformed binding; surfaced as compile diagnostic */ }
        }
    }
}

#endregion

#region ── Designer view (the actual editor surface) ─────────────────────────

/// <summary>
/// Design-time editor for a <see cref="DOSIFormDocument"/>. Hosts a toolbox
/// on the left, a fixed-size canvas in the middle (the form surrogate), and
/// a property grid on the right. The IDE shows this in place of the code
/// editor when the active tab's file ends in <c>.dosiform</c>.
/// </summary>
public sealed class DOSIDesigner : UserControl
{
    private const double GridStep = 8.0;
    private const double HandleSize = 8.0;

    private static AccentManager Accents => AccentManager.Instance;

    private DOSIFormDocument _doc;
    private bool _isDirty;
    private readonly Canvas _canvas = new();
    private DOSIWindow _previewWindow = null!;
    private readonly StackPanel _propertyHost = new() { Orientation = Orientation.Vertical, Spacing = 6 };
    private readonly TextBlock _propertyTitle;

    /// <summary>Per-placed-control adornment + selection plumbing.</summary>
    private readonly Dictionary<Control, Adornment> _adornments = new();
    private Control? _selected;
    // Additional controls in the multi-select set. The "singular"
    // _selected is also conceptually part of this set (it's the
    // anchor / property-grid focus); _multiSelected only holds the
    // EXTRA controls so the rest of the code can keep treating
    // _selected as the primary focus without rewriting every call site.
    private readonly HashSet<Control> _multiSelected = new();
    // Canvas overlay that hosts the live alignment guides drawn during a
    // drag (1px lines snapping to peer-control edges + form centerlines).
    // Lazy-built on first guide render so screens that never drag pay
    // no cost.
    private Canvas? _guidesLayer;

    // Drag state
    private bool _isDragging;
    private Point _dragStart;
    private double _dragStartX, _dragStartY;
    private bool _isResizing;
    private double _dragStartW, _dragStartH;
    // Per-(selected control, starting top-left) snapshot taken at
    // BeginDrag time so a group drag can compute each child's
    // destination from its own origin instead of accumulating
    // floating-point drift through repeated mutations.
    private readonly Dictionary<Control, (double X, double Y)> _groupDragOrigins = new();

    public event EventHandler? Modified;
    /// <summary>
    /// Raised when the user double-clicks a control (or hits the Edit Handler
    /// button in the property panel). The IDE handles this by opening a real
    /// code-behind tab - much better UX than a cramped modal dialog.
    /// </summary>
    public event EventHandler<DOSIDesignerEditHandlerRequestedEventArgs>? EditHandlerRequested;
    /// <summary>
    /// Raised when the user renames a control via the property panel's
    /// Name row. The IDE handles this by renaming the corresponding
    /// method(s) in any open code-behind buffer so handler bindings
    /// don't silently break. The args carry both the old and new name
    /// so a string-rename pass is trivial.
    /// </summary>
    public event EventHandler<DOSIDesignerControlRenamedEventArgs>? ControlRenamed;
    public bool IsDirty => _isDirty;
    public DOSIFormDocument Document => _doc;

    public DOSIDesigner(DOSIFormDocument doc)
    {
        _doc = doc;
        Background = Accents.WindowChromeBrush;

        // ----- Toolbox (left) -----
        var toolbox = BuildToolbox();

        // ----- Canvas (centre) -----
        _canvas.Background = Accents.WindowContentBrush;
        _canvas.Width = _doc.Width;
        _canvas.Height = _doc.Height;
        _canvas.PointerPressed += (_, e) =>
        {
            // Click empty canvas = deselect.
            if (e.Source == _canvas) Select(null);
        };
        DrawGrid();

        // Use a REAL DOSIWindow as the preview chrome. This is the same
        // control the runtime instantiates on Run, so the designer is now
        // pixel-identical to what the user will actually see - same dark
        // navy chrome, same accent edge, same drop shadow, same traffic
        // lights on the right. We disable hit-testing on the chrome so its
        // built-in drag / minimize / maximize handlers don't fire (this is
        // an inline preview, not a real top-level window).
        _previewWindow = new DOSIWindow
        {
            Title = _doc.Title,
            CanMaximize = _doc.CanMaximize,
            CanMinimize = _doc.CanMinimize,
            CanResize = false,
            Content = _canvas,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24)
        };
        _previewWindow.WindowWidth = _doc.Width;
        // +28 for the chrome title-bar height; DOSIWindow doesn't expose it
        // as a property but the runtime always reserves that strip at the top.
        _previewWindow.WindowHeight = _doc.Height + 28;
        // DOSIWindow's constructor starts at Opacity=0 + scale 0.95 expecting
        // WindowManager.OpenWindow to call PlayOpenAnimationAsync. Inline
        // previews never go through WindowManager, so without this nudge the
        // preview chrome stays invisible. Reset to the post-animation state
        // so the user sees the form straight away.
        _previewWindow.Opacity = 1;
        _previewWindow.RenderTransform = new Avalonia.Media.ScaleTransform(1, 1);
        // Double-clicking the form chrome itself (anywhere outside the
        // hosted controls) opens the form's Load handler - matches the
        // WinForms convention where double-clicking the form surface
        // generates Form_Load. Handled at the tunnel level so DOSIWindow's
        // own DoubleTapped (which would maximize) doesn't fire.
        _previewWindow.AddHandler(
            Avalonia.Input.InputElement.DoubleTappedEvent,
            (object? _, Avalonia.Input.TappedEventArgs e) =>
            {
                // Don't fire when the user double-clicked an actual control
                // on the canvas - that's the per-control handler's job. We
                // only want to react to clicks landing on the form chrome
                // / blank canvas.
                if (e.Source is Control src && _adornments.ContainsKey(src)) return;
                e.Handled = true;
                EditHandlerRequested?.Invoke(this, new DOSIDesignerEditHandlerRequestedEventArgs
                {
                    ControlName = "Form",
                    EventName = "Load"
                });
            },
            Avalonia.Interactivity.RoutingStrategies.Bubble,
            handledEventsToo: true);

        var canvasScroller = new DOSIScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = _previewWindow
        };

        // ----- Property grid (right) -----
        _propertyTitle = new TextBlock
        {
            Text = "PROPERTIES",
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextSecondaryBrush,
            LetterSpacing = 1.2,
            Margin = new Thickness(0, 0, 0, 8)
        };

        var propWrap = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Children = { _propertyTitle, _propertyHost }
        };
        var propScroller = new DOSIScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = propWrap
        };
        var propPanel = new Border
        {
            Width = 240,
            Background = Accents.WindowChromeBrush,
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Padding = new Thickness(12),
            Child = propScroller
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
        };
        grid.Children.Add(toolbox); Grid.SetColumn(toolbox, 0);
        grid.Children.Add(canvasScroller); Grid.SetColumn(canvasScroller, 1);
        grid.Children.Add(propPanel); Grid.SetColumn(propPanel, 2);
        Content = grid;

        // Hydrate canvas from doc + arrow-key nudge support
        Focusable = true;
        KeyDown += OnKeyDown;
        foreach (var def in _doc.Controls) MaterializeFromDef(def);
        RenderFormProperties();
    }

    #region Toolbox

    private Control BuildToolbox()
    {
        var list = new StackPanel { Orientation = Orientation.Vertical, Spacing = 4 };

        var header = new TextBlock
        {
            Text = "TOOLBOX",
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextSecondaryBrush,
            LetterSpacing = 1.2,
            Margin = new Thickness(0, 0, 0, 8)
        };
        list.Children.Add(header);

        foreach (var entry in DOSIDesignerControlCatalog.Entries)
        {
            // Snapshot for the click handler closure.
            var snapshot = entry;
            var item = new Border
            {
                Padding = new Thickness(10, 6),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock
                {
                    Text = entry.DisplayName,
                    FontSize = 12,
                    Foreground = Accents.TextPrimaryBrush
                }
            };
            item.PointerEntered += (_, _) => item.Background = Accents.ButtonBackgroundHoverBrush;
            item.PointerExited += (_, _) => item.Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
            // Click-to-add: drops a new instance at (16, 16) on the canvas. Real
            // drag-from-toolbox is a deliberate Phase 2 - click-to-add covers
            // 100% of the workflow with a fraction of the complexity.
            item.PointerReleased += (_, _) => AddNew(snapshot);
            list.Children.Add(item);
        }

        return new Border
        {
            Width = 180,
            Background = Accents.WindowChromeBrush,
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(12),
            Child = list
        };
    }

    private void AddNew(DOSIDesignerControlEntry entry)
    {
        // Stagger so successive drops don't stack invisibly.
        var (x, y) = NextDropPoint();
        var def = new DOSIFormControlDef
        {
            Type = entry.TypeKey,
            // camelCase + 1-based index (button1, button2, ...) to match the
            // VB / WinForms convention. Stable name = stable handler binding.
            Name = AutoNameFor(entry),
            X = x,
            Y = y,
            Width = entry.DefaultSize.Width,
            Height = entry.DefaultSize.Height
        };
        // Seed with the entry's defaults so the new control reads as configured
        // even before the user touches the property grid.
        foreach (var p in entry.Properties)
            def.Properties[p.Name] = JsonSerializer.SerializeToElement(p.Default);

        _doc.Controls.Add(def);
        var ctrl = MaterializeFromDef(def);
        Select(ctrl);
        MarkDirty();
    }

    private string AutoNameFor(DOSIDesignerControlEntry entry)
    {
        // Strip the DOSI prefix and lowercase the first letter so a button
        // becomes "button", a slider "slider", etc.
        var basis = entry.DisplayName;
        if (string.IsNullOrEmpty(basis)) basis = entry.TypeKey;
        if (basis.StartsWith("DOSI", StringComparison.OrdinalIgnoreCase) && basis.Length > 4) basis = basis[4..];
        if (basis.Length > 0) basis = char.ToLowerInvariant(basis[0]) + basis[1..];
        // Find the first free <basis>N suffix.
        for (var i = 1; i < 9999; i++)
        {
            var candidate = basis + i;
            if (_doc.Controls.All(d => !string.Equals(d.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
        return basis + Guid.NewGuid().ToString("N")[..6];
    }

    private (double X, double Y) NextDropPoint()
    {
        // Cascade based on current population; wraps within the canvas.
        var n = _doc.Controls.Count;
        var x = SnapTo(16 + (n % 6) * 24);
        var y = SnapTo(16 + (n % 6) * 24);
        return (x, y);
    }

    #endregion

    #region Canvas materialisation + selection

    /// <summary>
    /// Builds the live preview Control for a definition, places it on the
    /// canvas, wires the adornment handles, and registers it for selection.
    /// </summary>
    private Control MaterializeFromDef(DOSIFormControlDef def)
    {
        var entry = DOSIDesignerControlCatalog.Find(def.Type);
        if (entry == null)
        {
            // Unknown type (older format / typo). Show a placeholder so the
            // user can see + delete it instead of silently swallowing it.
            var ph = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(60, 200, 0, 0)),
                Width = def.Width,
                Height = def.Height,
                Child = new TextBlock { Text = $"?{def.Type}", Foreground = Brushes.White, Margin = new Thickness(4) }
            };
            PlaceOnCanvas(ph, def);
            return ph;
        }

        var ctrl = entry.Factory();
        foreach (var (k, v) in def.Properties) entry.Apply(ctrl, k, v);
        ctrl.Width = def.Width;
        ctrl.Height = def.Height;
        // Preview controls must NOT eat pointer input - we want clicks to
        // bubble up so the designer can take over for selection / drag.
        ctrl.IsHitTestVisible = false;

        PlaceOnCanvas(ctrl, def);
        return ctrl;
    }

    private void PlaceOnCanvas(Control ctrl, DOSIFormControlDef def)
    {
        Canvas.SetLeft(ctrl, def.X);
        Canvas.SetTop(ctrl, def.Y);
        _canvas.Children.Add(ctrl);

        // Adornment overlay: the rectangle that captures pointer input on
        // behalf of the (hit-test-disabled) preview control. Drawn slightly
        // larger so its border + handles don't overlap the preview pixels.
        var overlay = new Border
        {
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeAll)
        };
        overlay.Width = def.Width;
        overlay.Height = def.Height;
        Canvas.SetLeft(overlay, def.X);
        Canvas.SetTop(overlay, def.Y);

        var selectionFrame = new Rectangle
        {
            Stroke = Accents.AccentPrimaryBrush,
            StrokeThickness = 1.5,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(new double[] { 4, 2 }),
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
            IsVisible = false
        };
        selectionFrame.Width = def.Width;
        selectionFrame.Height = def.Height;
        Canvas.SetLeft(selectionFrame, def.X);
        Canvas.SetTop(selectionFrame, def.Y);

        // Bottom-right resize handle.
        var handle = new Rectangle
        {
            Width = HandleSize,
            Height = HandleSize,
            Fill = Accents.AccentPrimaryBrush,
            Stroke = Brushes.White,
            StrokeThickness = 1,
            IsVisible = false,
            Cursor = new Cursor(StandardCursorType.TopLeftCorner)
        };
        Canvas.SetLeft(handle, def.X + def.Width - HandleSize / 2);
        Canvas.SetTop(handle, def.Y + def.Height - HandleSize / 2);

        _canvas.Children.Add(selectionFrame);
        _canvas.Children.Add(overlay);
        _canvas.Children.Add(handle);

        var ad = new Adornment
        {
            Def = def,
            Preview = ctrl,
            Overlay = overlay,
            SelectionFrame = selectionFrame,
            Handle = handle
        };
        _adornments[ctrl] = ad;

        overlay.PointerPressed += (_, e) => BeginDrag(ctrl, e, resizing: false);
        handle.PointerPressed += (_, e) => BeginDrag(ctrl, e, resizing: true);
        overlay.PointerMoved += (_, e) => DoDrag(ctrl, e);
        handle.PointerMoved += (_, e) => DoDrag(ctrl, e);
        overlay.PointerReleased += (_, _) => EndDrag();
        handle.PointerReleased += (_, _) => EndDrag();
        // Double-click on the control opens the code editor for its primary
        // event - mimics the VB / WinForms designer interaction. EndDrag()
        // first because the first click of the double-tap triggered
        // BeginDrag, and we're about to navigate away before the user gets
        // a chance to release the pointer over the canvas. Without this the
        // _isDragging flag stays true and the next mouse move on return
        // drags the previously-clicked control off-position.
        overlay.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            EndDrag();
            EditPrimaryHandler(def);
        };
    }

    private void Select(Control? ctrl)
    {
        if (_selected != null && _adornments.TryGetValue(_selected, out var prev))
        {
            prev.SelectionFrame.IsVisible = false;
            prev.Handle.IsVisible = false;
        }
        // Plain Select always collapses the multi-select set - it's the
        // "pick exactly this one" gesture. Callers wanting additive
        // selection go through ToggleMultiSelect.
        ClearMultiSelectVisuals();
        _multiSelected.Clear();
        _selected = ctrl;
        if (ctrl != null && _adornments.TryGetValue(ctrl, out var cur))
        {
            cur.SelectionFrame.IsVisible = true;
            cur.Handle.IsVisible = true;
            RenderControlProperties(cur);
            Focus();
        }
        else
        {
            RenderFormProperties();
        }
    }

    /// <summary>
    /// Adds <paramref name="ctrl"/> to (or removes it from) the
    /// multi-select set, leaving the singular <see cref="_selected"/>
    /// as the anchor / property-grid focus. The first Shift-click on a
    /// fresh control promotes the existing anchor into the set so the
    /// user sees both highlighted - matches Finder / Explorer
    /// convention. The property grid stays bound to the original
    /// anchor; multi-select is for bulk move / delete, not bulk
    /// property editing (which has no sensible UI for mixed values).
    /// </summary>
    private void ToggleMultiSelect(Control ctrl)
    {
        if (_selected == null)
        {
            // No anchor yet - the Shift-click becomes the anchor.
            Select(ctrl);
            return;
        }
        if (ctrl == _selected) return; // anchor is implicitly selected

        if (_multiSelected.Contains(ctrl))
        {
            _multiSelected.Remove(ctrl);
            if (_adornments.TryGetValue(ctrl, out var ad))
            {
                ad.SelectionFrame.IsVisible = false;
                ad.Handle.IsVisible = false;
            }
        }
        else
        {
            _multiSelected.Add(ctrl);
            if (_adornments.TryGetValue(ctrl, out var ad))
            {
                ad.SelectionFrame.IsVisible = true;
                // Resize handle stays hidden for non-anchor selections -
                // grabbing it on a satellite would only resize the
                // satellite, which is confusing in a group.
                ad.Handle.IsVisible = false;
            }
        }
    }

    /// <summary>
    /// Yields every control currently part of the selection - the
    /// singular anchor plus any extras added via Shift-click. Empty if
    /// nothing is selected.
    /// </summary>
    private IEnumerable<Control> EnumerateSelected()
    {
        if (_selected != null) yield return _selected;
        foreach (var c in _multiSelected) yield return c;
    }

    private void ClearMultiSelectVisuals()
    {
        foreach (var c in _multiSelected)
        {
            if (_adornments.TryGetValue(c, out var ad))
            {
                ad.SelectionFrame.IsVisible = false;
                ad.Handle.IsVisible = false;
            }
        }
    }

    #endregion

    #region Drag / resize / nudge

    private void BeginDrag(Control ctrl, PointerPressedEventArgs e, bool resizing)
    {
        // Shift-click on an unselected control adds it to the multi-select;
        // shift-click on an already-selected control removes it. Plain
        // click on an unselected control replaces the selection (so a
        // single click in empty space + click-on-control behaves like
        // every shell file browser). Multi-select isn't compatible with
        // resize (resize handles target one control's bounds), so a
        // resize gesture always collapses to a singular selection.
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (resizing)
        {
            Select(ctrl);
        }
        else if (shift)
        {
            ToggleMultiSelect(ctrl);
        }
        else if (_selected != ctrl && !_multiSelected.Contains(ctrl))
        {
            Select(ctrl);
        }
        // Else: plain click on a control already in the multi-select set
        // preserves the group so the user can drag the whole thing.

        var ad = _adornments[ctrl];
        _isDragging = !resizing;
        _isResizing = resizing;
        _dragStart = e.GetPosition(_canvas);
        _dragStartX = ad.Def.X;
        _dragStartY = ad.Def.Y;
        _dragStartW = ad.Def.Width;
        _dragStartH = ad.Def.Height;
        // Snapshot every selected control's starting position so a
        // group drag can recompute each one's destination from its own
        // origin (rather than accumulating drift through repeated
        // ApplyGeometry calls).
        _groupDragOrigins.Clear();
        foreach (var sel in EnumerateSelected())
        {
            if (_adornments.TryGetValue(sel, out var selAd))
                _groupDragOrigins[sel] = (selAd.Def.X, selAd.Def.Y);
        }
        e.Pointer.Capture(resizing ? ad.Handle : ad.Overlay);
        e.Handled = true;
    }

    private void DoDrag(Control ctrl, PointerEventArgs e)
    {
        if (!_isDragging && !_isResizing) return;
        if (!_adornments.TryGetValue(ctrl, out var ad)) return;

        var p = e.GetPosition(_canvas);
        var dx = p.X - _dragStart.X;
        var dy = p.Y - _dragStart.Y;

        if (_isResizing)
        {
            ad.Def.Width = Math.Max(16, SnapTo(_dragStartW + dx));
            ad.Def.Height = Math.Max(16, SnapTo(_dragStartH + dy));
            ApplyGeometry(ad);
            MarkDirty();
            return;
        }

        // SNAP + GUIDES (single-select drag only - group drags skip both
        // because there's no obvious anchor to snap on, and 8 dancing
        // guides would be noise).
        bool altDisablesSnap = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        bool isGroup = _multiSelected.Count > 0;
        double snappedX = SnapTo(_dragStartX + dx);
        double snappedY = SnapTo(_dragStartY + dy);

        if (!isGroup && !altDisablesSnap)
        {
            ComputeSnapAndGuides(ad, ref snappedX, ref snappedY);
        }
        else
        {
            ClearGuides();
        }

        if (isGroup)
        {
            // Compute the actual delta the anchor moved through (after
            // SnapTo) and apply it to every selected control from its
            // snapshotted origin.
            double anchorDx = Math.Clamp(snappedX, 0, Math.Max(0, _doc.Width - ad.Def.Width)) - _dragStartX;
            double anchorDy = Math.Clamp(snappedY, 0, Math.Max(0, _doc.Height - ad.Def.Height)) - _dragStartY;
            foreach (var sel in EnumerateSelected())
            {
                if (!_adornments.TryGetValue(sel, out var selAd)) continue;
                if (!_groupDragOrigins.TryGetValue(sel, out var origin)) continue;
                selAd.Def.X = Math.Clamp(SnapTo(origin.X + anchorDx), 0,
                                         Math.Max(0, _doc.Width - selAd.Def.Width));
                selAd.Def.Y = Math.Clamp(SnapTo(origin.Y + anchorDy), 0,
                                         Math.Max(0, _doc.Height - selAd.Def.Height));
                ApplyGeometry(selAd);
            }
        }
        else
        {
            ad.Def.X = Math.Clamp(snappedX, 0, Math.Max(0, _doc.Width - ad.Def.Width));
            ad.Def.Y = Math.Clamp(snappedY, 0, Math.Max(0, _doc.Height - ad.Def.Height));
            ApplyGeometry(ad);
        }
        MarkDirty();
    }

    private void EndDrag()
    {
        _isDragging = false;
        _isResizing = false;
        ClearGuides();
        _groupDragOrigins.Clear();
    }

    private const double SnapThreshold = 4.0;
    private static readonly IBrush GuideBrush =
        new SolidColorBrush(Color.FromArgb(220, 255, 105, 180));

    /// <summary>
    /// Mutates <paramref name="x"/> / <paramref name="y"/> to snap the
    /// dragged adornment <paramref name="ad"/> to the nearest peer-control
    /// edge / center or the form centerline, when within
    /// <see cref="SnapThreshold"/> pixels. Also paints 1px guide lines
    /// on the guides overlay for any axis that snapped, so the user
    /// sees what's aligning. Skipped for resize gestures (they have
    /// their own meaning) and group drags (8 guides at once is noise).
    /// </summary>
    private void ComputeSnapAndGuides(Adornment ad, ref double x, ref double y)
    {
        EnsureGuidesLayer();
        _guidesLayer!.Children.Clear();

        double w = ad.Def.Width, h = ad.Def.Height;
        double formW = _doc.Width, formH = _doc.Height;

        // Candidate targets we measure against, by axis. Each candidate
        // is (targetCoord, where-to-draw-the-line, axisLength). We
        // collect all peers + the canvas centerlines + edges.
        var xCandidates = new List<double> { 0, formW / 2 - w / 2, formW - w };
        var yCandidates = new List<double> { 0, formH / 2 - h / 2, formH - h };

        foreach (var (other, otherAd) in _adornments)
        {
            if (other == ad.Preview || ReferenceEquals(otherAd, ad)) continue;
            // Skip every adornment whose Preview is the dragged control
            // (the dictionary keys by Preview).
            if (ReferenceEquals(other, ad.Preview)) continue;
            var od = otherAd.Def;
            // X axis: align left-to-left, right-to-right, center-to-center.
            xCandidates.Add(od.X);                              // left-left
            xCandidates.Add(od.X + od.Width - w);               // right-right
            xCandidates.Add(od.X + od.Width / 2 - w / 2);       // center-center
            // Y axis: same three.
            yCandidates.Add(od.Y);
            yCandidates.Add(od.Y + od.Height - h);
            yCandidates.Add(od.Y + od.Height / 2 - h / 2);
        }

        double snappedX = x, snappedY = y;
        double bestDx = SnapThreshold, bestDy = SnapThreshold;
        double bestXTarget = double.NaN, bestYTarget = double.NaN;

        foreach (var t in xCandidates)
        {
            var d = Math.Abs(t - x);
            if (d < bestDx) { bestDx = d; snappedX = t; bestXTarget = t; }
        }
        foreach (var t in yCandidates)
        {
            var d = Math.Abs(t - y);
            if (d < bestDy) { bestDy = d; snappedY = t; bestYTarget = t; }
        }

        x = snappedX;
        y = snappedY;

        // Paint a full-height guide on the snapped X (so the user can see
        // which other control's edge they're aligned with) and a full-width
        // guide on the snapped Y. Only paint when a snap actually happened.
        if (!double.IsNaN(bestXTarget))
        {
            // The dragged control's left edge is at snappedX. Draw the
            // line there, but if the snap aligned center/right we want
            // the line on the actual aligned edge, not on `x` (the left).
            // Cheap to just draw on the left edge - it visually reads
            // as "this column aligns" either way.
            var line = new Rectangle
            {
                Width = 1,
                Height = formH,
                Fill = GuideBrush,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(line, snappedX);
            Canvas.SetTop(line, 0);
            _guidesLayer.Children.Add(line);
        }
        if (!double.IsNaN(bestYTarget))
        {
            var line = new Rectangle
            {
                Width = formW,
                Height = 1,
                Fill = GuideBrush,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(line, 0);
            Canvas.SetTop(line, snappedY);
            _guidesLayer.Children.Add(line);
        }
    }

    private void EnsureGuidesLayer()
    {
        if (_guidesLayer != null) return;
        _guidesLayer = new Canvas
        {
            IsHitTestVisible = false,
            Width = _doc.Width,
            Height = _doc.Height
        };
        // Layered on top of every adornment so the lines aren't
        // occluded by the dragged control's own preview.
        _canvas.Children.Add(_guidesLayer);
        Canvas.SetLeft(_guidesLayer, 0);
        Canvas.SetTop(_guidesLayer, 0);
    }

    private void ClearGuides()
    {
        if (_guidesLayer == null) return;
        _guidesLayer.Children.Clear();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_selected == null) return;

        // Delete: route through the multi-aware path so a Ctrl-A or
        // Shift-built group can be wiped in one keypress.
        if (e.Key == Key.Delete)
        {
            DeleteSelected();
            e.Handled = true;
            return;
        }

        // Arrow keys: nudge every selected control. Shift = grid step,
        // unmodified = 1px. WinForms / Designer convention.
        double dx = 0, dy = 0;
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? GridStep : 1d;
        switch (e.Key)
        {
            case Key.Left:  dx = -step; break;
            case Key.Right: dx = step; break;
            case Key.Up:    dy = -step; break;
            case Key.Down:  dy = step; break;
            default: return;
        }

        foreach (var sel in EnumerateSelected())
        {
            if (!_adornments.TryGetValue(sel, out var selAd)) continue;
            selAd.Def.X = Math.Clamp(selAd.Def.X + dx, 0, Math.Max(0, _doc.Width - selAd.Def.Width));
            selAd.Def.Y = Math.Clamp(selAd.Def.Y + dy, 0, Math.Max(0, _doc.Height - selAd.Def.Height));
            ApplyGeometry(selAd);
        }
        MarkDirty();
        e.Handled = true;
    }

    private void DeleteSelected()
    {
        // Snapshot first - we'll mutate _multiSelected via Select(null)
        // below and don't want the foreach to throw.
        var victims = EnumerateSelected().ToList();
        if (victims.Count == 0) return;
        foreach (var c in victims)
        {
            if (!_adornments.TryGetValue(c, out var ad)) continue;
            _canvas.Children.Remove(ad.Preview);
            _canvas.Children.Remove(ad.Overlay);
            _canvas.Children.Remove(ad.SelectionFrame);
            _canvas.Children.Remove(ad.Handle);
            _adornments.Remove(c);
            _doc.Controls.Remove(ad.Def);
        }
        _selected = null;
        _multiSelected.Clear();
        RenderFormProperties();
        MarkDirty();
    }

    private void ApplyGeometry(Adornment ad)
    {
        ad.Preview.Width = ad.Def.Width;
        ad.Preview.Height = ad.Def.Height;
        Canvas.SetLeft(ad.Preview, ad.Def.X);
        Canvas.SetTop(ad.Preview, ad.Def.Y);

        ad.Overlay.Width = ad.Def.Width;
        ad.Overlay.Height = ad.Def.Height;
        Canvas.SetLeft(ad.Overlay, ad.Def.X);
        Canvas.SetTop(ad.Overlay, ad.Def.Y);

        ad.SelectionFrame.Width = ad.Def.Width;
        ad.SelectionFrame.Height = ad.Def.Height;
        Canvas.SetLeft(ad.SelectionFrame, ad.Def.X);
        Canvas.SetTop(ad.SelectionFrame, ad.Def.Y);

        Canvas.SetLeft(ad.Handle, ad.Def.X + ad.Def.Width - HandleSize / 2);
        Canvas.SetTop(ad.Handle, ad.Def.Y + ad.Def.Height - HandleSize / 2);
    }

    private static double SnapTo(double v) => Math.Round(v / GridStep) * GridStep;

    #endregion

    #region Property grid

    /// <summary>Render the form-level properties (Title / Width / Height) when nothing is selected.</summary>
    private void RenderFormProperties()
    {
        _propertyTitle.Text = "FORM PROPERTIES";
        _propertyHost.Children.Clear();

        AddSectionHeader("Identity");
        AddRow("Title", _doc.Title, s =>
        {
            _doc.Title = s;
            _previewWindow.Title = s;
            MarkDirty();
        });
        AddDivider();

        AddSectionHeader("Layout");
        AddDoubleRow("Width", _doc.Width, v =>
        {
            _doc.Width = Math.Max(80, v);
            _canvas.Width = _doc.Width;
            _previewWindow.WindowWidth = _doc.Width;
            DrawGrid();
            MarkDirty();
        });
        AddDoubleRow("Height", _doc.Height, v =>
        {
            _doc.Height = Math.Max(60, v);
            _canvas.Height = _doc.Height;
            _previewWindow.WindowHeight = _doc.Height + 28;
            DrawGrid();
            MarkDirty();
        });
        AddDivider();

        // ----- Chrome section: control which window-frame buttons appear.
        // Maps 1-1 to DOSIWindow.CanMinimize / CanMaximize, applied at runtime
        // by DOSIFormLoader so the user can hide minimize/maximize when the
        // form is meant to be modal-style.
        AddSectionHeader("Chrome");
        AddBoolRow("Show Minimize", _doc.CanMinimize, b =>
        {
            _doc.CanMinimize = b;
            _previewWindow.CanMinimize = b;
            MarkDirty();
        });
        AddBoolRow("Show Maximize", _doc.CanMaximize, b =>
        {
            _doc.CanMaximize = b;
            _previewWindow.CanMaximize = b;
            MarkDirty();
        });
        AddDivider();

        // ----- Events: handled in the code-behind tab now (a DOSIDropDown
        // sits above the editor there). Keeping a single source of truth
        // for event-picking means the designer property panel stays focused
        // on layout / chrome and the code editor on, well, code.
        _propertyHost.Children.Add(new TextBlock
        {
            Text = "Tip: double-click anywhere on the form chrome to jump to Form_Load. Use the events dropdown in the code-behind tab for Closing / Closed.",
            FontSize = 10,
            Foreground = Accents.TextSecondaryBrush,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });
    }

    /// <summary>
    /// One row in the Events section: event name on the left, status pill
    /// on the right ("+" to add, check-mark when a handler exists). Clicking
    /// anywhere in the row opens the code-behind tab for that event.
    /// </summary>
    private void AddEventRow(string evName, bool hasCode, Action onClick)
    {
        var name = new TextBlock
        {
            Text = evName,
            FontSize = 12,
            Foreground = Accents.TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        var status = new TextBlock
        {
            Text = hasCode ? "\u2713" : "+",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = hasCode
                ? new SolidColorBrush(Accents.TextOnAccent)
                : Accents.TextSecondaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var statusBadge = new Border
        {
            Width = 22,
            Height = 20,
            CornerRadius = new CornerRadius(3),
            Background = hasCode
                ? Accents.AccentPrimaryBrush
                : new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            Child = status
        };
        var rowGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };
        rowGrid.Children.Add(name); Grid.SetColumn(name, 0);
        rowGrid.Children.Add(statusBadge); Grid.SetColumn(statusBadge, 1);

        var row = new Border
        {
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = rowGrid
        };
        row.PointerEntered += (_, _) => row.Background = Accents.ButtonBackgroundHoverBrush;
        row.PointerExited += (_, _) => row.Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
        row.PointerReleased += (_, _) => onClick();
        _propertyHost.Children.Add(row);
    }

    /// <summary>Render the editable properties of the currently-selected control.</summary>
    private void RenderControlProperties(Adornment ad)
    {
        var entry = DOSIDesignerControlCatalog.Find(ad.Def.Type);
        _propertyTitle.Text = (entry?.DisplayName ?? ad.Def.Type).ToUpperInvariant();
        _propertyHost.Children.Clear();

        // ----- Identity section -----
        AddSectionHeader("Identity");
        AddRow("Name", ad.Def.Name, s =>
        {
            // Names must be C#-ident-friendly because the handler compiler
            // uses them verbatim as method-name prefixes.
            var sanitized = SanitizeIdentLoose(s);
            var previous = ad.Def.Name;
            if (string.Equals(previous, sanitized, StringComparison.Ordinal)) return;
            ad.Def.Name = sanitized;
            // Notify the IDE so any open code-behind tab can rename
            // <OldName>_<Event> methods to <NewName>_<Event> in place -
            // otherwise the user's typed handler bodies silently
            // disconnect from the renamed control at next Run.
            if (!string.IsNullOrWhiteSpace(previous) && !string.IsNullOrWhiteSpace(sanitized))
            {
                try
                {
                    ControlRenamed?.Invoke(this,
                        new DOSIDesignerControlRenamedEventArgs
                        {
                            OldName = previous,
                            NewName = sanitized
                        });
                }
                catch { /* listener errors must not break the property panel */ }
            }
            MarkDirty();
        });
        AddDivider();

        // ----- Layout section -----
        AddSectionHeader("Layout");
        AddDoubleRow("X", ad.Def.X, v => { ad.Def.X = SnapTo(v); ApplyGeometry(ad); MarkDirty(); });
        AddDoubleRow("Y", ad.Def.Y, v => { ad.Def.Y = SnapTo(v); ApplyGeometry(ad); MarkDirty(); });
        AddDoubleRow("Width", ad.Def.Width, v => { ad.Def.Width = Math.Max(16, SnapTo(v)); ApplyGeometry(ad); MarkDirty(); });
        AddDoubleRow("Height", ad.Def.Height, v => { ad.Def.Height = Math.Max(16, SnapTo(v)); ApplyGeometry(ad); MarkDirty(); });
        AddDockRow(ad.Def.Dock, d =>
        {
            ad.Def.Dock = d;
            // Visual hint on the canvas: show a dashed dock-edge marker so
            // the user can see at a glance which controls will snap.
            UpdateDockOverlay(ad);
            MarkDirty();
        });

        if (entry == null) return;

        if (entry.Properties.Count > 0)
        {
            AddDivider();
            AddSectionHeader("Properties");
        }

        foreach (var p in entry.Properties)
        {
            var prop = p;
            switch (prop.Kind)
            {
                case DOSIDesignerPropertyKind.String:
                    AddRow(prop.Name, GetString(ad.Def, prop.Name, prop.Default?.ToString() ?? ""), s =>
                    {
                        ad.Def.Properties[prop.Name] = JsonSerializer.SerializeToElement(s);
                        entry.Apply(ad.Preview, prop.Name, ad.Def.Properties[prop.Name]);
                        MarkDirty();
                    });
                    break;
                case DOSIDesignerPropertyKind.Bool:
                    AddBoolRow(prop.Name, GetBool(ad.Def, prop.Name, (bool)prop.Default), b =>
                    {
                        ad.Def.Properties[prop.Name] = JsonSerializer.SerializeToElement(b);
                        entry.Apply(ad.Preview, prop.Name, ad.Def.Properties[prop.Name]);
                        MarkDirty();
                    });
                    break;
                case DOSIDesignerPropertyKind.Double:
                case DOSIDesignerPropertyKind.Int:
                    AddDoubleRow(prop.Name, GetDouble(ad.Def, prop.Name, Convert.ToDouble(prop.Default)), v =>
                    {
                        object boxed = prop.Kind == DOSIDesignerPropertyKind.Int ? (object)(int)v : v;
                        ad.Def.Properties[prop.Name] = JsonSerializer.SerializeToElement(boxed);
                        entry.Apply(ad.Preview, prop.Name, ad.Def.Properties[prop.Name]);
                        MarkDirty();
                    });
                    break;
            }
        }

        // ----- Events: intentionally NOT rendered as a UI list. The user
        // workflow is purely double-click-to-edit (matches the WinForms /
        // VB designer convention) - any extra row of buttons here just adds
        // visual noise and competes with that gesture. The PrimaryEvent of
        // the catalog entry is what double-click opens.
    }

    private static string SanitizeIdentLoose(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
        if (sb.Length == 0) return "_";
        if (char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    private void AddSectionHeader(string label)
    {
        _propertyHost.Children.Add(new TextBlock
        {
            Text = label.ToUpperInvariant(),
            FontSize = 9,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accents.TextSecondaryBrush,
            LetterSpacing = 1.0,
            Opacity = 0.85,
            Margin = new Thickness(0, 6, 0, 4)
        });
    }

    private void AddDivider()
    {
        _propertyHost.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(0, 6),
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255))
        });
    }

    private static string GetString(DOSIFormControlDef def, string name, string fallback)
    {
        if (!def.Properties.TryGetValue(name, out var v)) return fallback;
        return v.ValueKind == JsonValueKind.String ? (v.GetString() ?? fallback) : v.ToString();
    }

    private static bool GetBool(DOSIFormControlDef def, string name, bool fallback)
    {
        if (!def.Properties.TryGetValue(name, out var v)) return fallback;
        return v.ValueKind == JsonValueKind.True;
    }

    private static double GetDouble(DOSIFormControlDef def, string name, double fallback)
    {
        if (!def.Properties.TryGetValue(name, out var v)) return fallback;
        return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : fallback;
    }

    private void AddRow(string label, string value, Action<string> onCommit)
    {
        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush
        };
        var tb = new DOSITextBox
        {
            Text = value,
            FontSize = 12,
            Height = 28,
            // Stretch within the property column and clamp to the column
            // width so long values (e.g. "asdasdasd...") don't blow out the
            // panel and run off the right side of the IDE window.
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxWidth = 200
        };
        // Avoid feedback loops with the live preview when the user types fast.
        tb.TextChanged += (_, _) => onCommit(tb.Text ?? "");
        _propertyHost.Children.Add(new StackPanel { Children = { lbl, tb }, Spacing = 2 });
    }

    private void AddDoubleRow(string label, double value, Action<double> onCommit)
    {
        AddRow(label, value.ToString(System.Globalization.CultureInfo.InvariantCulture), s =>
        {
            if (double.TryParse(s, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
                onCommit(d);
        });
    }

    /// <summary>
    /// Compact horizontal toggle group for the Dock property. Maps better
    /// to the spatial concept (top/bottom/left/right/fill/none) than a
    /// dropdown would, and DOSI doesn't ship a ComboBox anyway.
    /// </summary>
    private void AddDockRow(DOSIDock current, Action<DOSIDock> onCommit)
    {
        var lbl = new TextBlock
        {
            Text = "Dock",
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4
        };

        var values = new[] { DOSIDock.None, DOSIDock.Top, DOSIDock.Bottom, DOSIDock.Left, DOSIDock.Right, DOSIDock.Fill };
        var glyphs = new[] { "\u2715", "\u25B2", "\u25BC", "\u25C0", "\u25B6", "\u25A0" };
        var tooltips = new[] { "None", "Top", "Bottom", "Left", "Right", "Fill" };

        var pills = new List<Border>();
        for (var i = 0; i < values.Length; i++)
        {
            var idx = i; // capture
            var pill = new Border
            {
                Width = 28,
                Height = 24,
                CornerRadius = new CornerRadius(4),
                Background = values[i] == current
                    ? Accents.AccentPrimaryBrush
                    : new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock
                {
                    Text = glyphs[i],
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = values[i] == current
                        ? new SolidColorBrush(Accents.TextOnAccent)
                        : Accents.TextPrimaryBrush
                }
            };
            ToolTip.SetTip(pill, tooltips[idx]);
            pill.PointerReleased += (_, _) =>
            {
                // Recolour all pills so only the selected one is highlighted.
                for (var k = 0; k < pills.Count; k++)
                {
                    var on = values[k] == values[idx];
                    pills[k].Background = on
                        ? Accents.AccentPrimaryBrush
                        : new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
                    if (pills[k].Child is TextBlock t)
                        t.Foreground = on
                            ? new SolidColorBrush(Accents.TextOnAccent)
                            : Accents.TextPrimaryBrush;
                }
                onCommit(values[idx]);
            };
            pills.Add(pill);
            row.Children.Add(pill);
        }

        _propertyHost.Children.Add(new StackPanel { Children = { lbl, row }, Spacing = 4 });
    }

    /// <summary>
    /// Visual hint in the designer when a control has Dock set: a thin
    /// accent-coloured bar along the docked edge of the form-preview canvas.
    /// Just a marker - the canvas keeps showing the absolute layout the
    /// user designed; live re-flow only happens at runtime.
    /// </summary>
    private void UpdateDockOverlay(Adornment ad)
    {
        // Reuse the existing selection frame as the hint - colour-tweak it
        // when Dock != None so the user gets immediate feedback. (Keeps the
        // adornment surface count low; full edge bars would need their own
        // shapes per-control.)
        ad.SelectionFrame.Stroke = ad.Def.Dock != DOSIDock.None
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x2E)) // amber: snapped
            : Accents.AccentPrimaryBrush;
    }

    private void AddBoolRow(string label, bool value, Action<bool> onCommit)
    {
        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = Accents.TextSecondaryBrush
        };
        var stateText = new TextBlock
        {
            Text = value ? "On" : "Off",
            FontSize = 12,
            Foreground = Accents.TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        var cb = new CheckBox
        {
            IsChecked = value,
            Content = stateText,
            Padding = new Thickness(6, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Foreground = Accents.TextPrimaryBrush
        };
        cb.IsCheckedChanged += (_, _) =>
        {
            var on = cb.IsChecked == true;
            stateText.Text = on ? "On" : "Off";
            onCommit(on);
        };
        _propertyHost.Children.Add(new StackPanel { Children = { lbl, cb }, Spacing = 2 });
    }

    /// <summary>
    /// Raise <see cref="EditHandlerRequested"/> so the IDE can open a real
    /// code-behind tab. Replaces the cramped modal dialog the earlier MVP
    /// used - users can now write real, multi-screen C# with line numbers,
    /// scrolling, syntax highlighting, the works.
    /// </summary>
    private void EditPrimaryHandler(DOSIFormControlDef def)
    {
        var entry = DOSIDesignerControlCatalog.Find(def.Type);
        if (entry?.PrimaryEvent == null) return;
        if (string.IsNullOrWhiteSpace(def.Name)) def.Name = AutoNameFor(entry);

        EditHandlerRequested?.Invoke(this, new DOSIDesignerEditHandlerRequestedEventArgs
        {
            ControlName = def.Name,
            EventName = entry.PrimaryEvent
        });
    }

    /// <summary>
    /// Re-render the property panel for the currently-selected control.
    /// Called by the IDE after the user saves the code-behind tab so the
    /// "Add handler" / "Edit handler" label flips to its updated state.
    /// </summary>
    public void RefreshSelectedProperties()
    {
        if (_selected != null && _adornments.TryGetValue(_selected, out var ad))
            RenderControlProperties(ad);
    }

    /// <summary>
    /// Drops the current selection (visual handles + property-grid focus).
    /// Called by the IDE when re-activating the designer tab after the user
    /// jumped to the code-behind tab - leaving a control "still selected"
    /// when they switch back was visually + programmatically confusing.
    /// Also clears any latent drag state so the next mouse move doesn't
    /// re-engage a drag that was implicitly started by the first half of a
    /// double-tap.
    /// </summary>
    public void ClearSelection()
    {
        EndDrag();
        Select(null);
    }

    #endregion

    #region Grid + dirty + save

    private void DrawGrid()
    {
        // Cheap grid: a tiled-dot pattern via a shape isn't worth the
        // complexity here. Render a sparse Path of horizontal + vertical
        // hairlines once; redrawn whenever the form size changes.
        // Children added at the bottom of the canvas Z-order.
        for (var i = _canvas.Children.Count - 1; i >= 0; i--)
        {
            if (_canvas.Children[i] is Line) _canvas.Children.RemoveAt(i);
        }
        var pen = new SolidColorBrush(Color.FromArgb(28, 0, 0, 0));
        for (double x = 0; x <= _doc.Width; x += GridStep * 4)
        {
            var ln = new Line { StartPoint = new Point(x, 0), EndPoint = new Point(x, _doc.Height), Stroke = pen, StrokeThickness = 1, IsHitTestVisible = false };
            _canvas.Children.Insert(0, ln);
        }
        for (double y = 0; y <= _doc.Height; y += GridStep * 4)
        {
            var ln = new Line { StartPoint = new Point(0, y), EndPoint = new Point(_doc.Width, y), Stroke = pen, StrokeThickness = 1, IsHitTestVisible = false };
            _canvas.Children.Insert(0, ln);
        }
    }

    private void MarkDirty()
    {
        if (_isDirty) { Modified?.Invoke(this, EventArgs.Empty); return; }
        _isDirty = true;
        Modified?.Invoke(this, EventArgs.Empty);
    }

    public void MarkClean() => _isDirty = false;

    /// <summary>Serialise the current document. Caller writes it to disk.</summary>
    public string GetSerialized() => DOSIFormSerializer.Serialize(_doc);

    #endregion

    #region Helpers

    private sealed class Adornment
    {
        public required DOSIFormControlDef Def;
        public required Control Preview;
        public required Border Overlay;
        public required Rectangle SelectionFrame;
        public required Rectangle Handle;
    }

    #endregion
}

#endregion
