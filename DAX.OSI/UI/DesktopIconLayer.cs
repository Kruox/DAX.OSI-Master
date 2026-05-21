using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DAX.OSI.DefaultApplications;
using DOSI.CORE;
using DOSI.CORE.AccentManagement;
using DOSI.CORE.Apps;
using DOSI.CORE.UIComponents;
using DOSI.CORE.UIComponents.WindowManagement;
using DOSI.CORE.UserManagement;
// Disambiguate System.IO.Path from Avalonia.Controls.Shapes.Path - both are
// in scope via the using directives above and we only ever want the IO one.
using Path = System.IO.Path;

namespace DAX.OSI.UI;

/// <summary>
/// Renders the contents of <c>&lt;UserHome&gt;/Desktop/</c> as draggable
/// tiles directly on the wallpaper, mirroring how every mainstream
/// desktop OS surfaces a "desktop" folder. Owns:
/// <list type="bullet">
///   <item><description>Tile rendering (icon + label, file vs folder).</description></item>
///   <item><description>Selection (single, Ctrl-toggle, marquee box).</description></item>
///   <item><description>Drag-to-reposition with multi-select group drag.</description></item>
///   <item><description>Double-click activation (folder -> file explorer
///   navigated to it; file -> plug-in / image-viewer / metadata fallback,
///   matching DOSIFileExplorer.ActivateTile).</description></item>
///   <item><description>Per-tile context menu (Open, Copy, Cut, Delete).</description></item>
///   <item><description>Delete-key removes every selected tile.</description></item>
///   <item><description>Persistence of x/y per file name via
///   <see cref="DesktopIconLayout"/>.</description></item>
///   <item><description>FileSystemWatcher debounced rebind so dropping a
///   file into the Desktop folder from anywhere shows up immediately.</description></item>
/// </list>
/// <para>
/// PARENTING: this class IS a <see cref="Canvas"/> and is added to the
/// desktop's wallpaper layer (the one DOSIScreen exposes as
/// <c>Desktop</c>). Tiles use <see cref="Canvas.LeftProperty"/> /
/// <see cref="Canvas.TopProperty"/> for absolute positioning.
/// </para>
/// </summary>
public sealed class DesktopIconLayer : Canvas
{
    private const double TileWidth = 88;
    private const double TileHeight = 92;
    private const double GridGapX = 12;
    private const double GridGapY = 16;
    private const double GridStartX = 16;
    // Push the first row clear of the taskbar (28 px tall, docked at top)
    // plus a small breathing margin. Without this the auto-placed first
    // row of new icons rendered half-tucked under the taskbar.
    private const double TaskbarHeight = 28;
    private const double GridStartY = TaskbarHeight + 12;

    // Snap-to-grid is a session-scoped toggle shared across every layer
    // (primary + secondary monitors) so flipping it on the primary
    // monitor's wallpaper menu instantly affects all desktops without
    // each layer having to wire its own ack-handler. In-memory only -
    // not persisted to disk yet because the DesktopIconLayout JSON
    // schema is currently positions-only; adding a top-level "snap"
    // flag would require a one-time migration, which we can defer
    // until the user asks for snap-state to survive a reboot.
    private static bool _snapToGridEnabled;

    /// <summary>
    /// Quantises (x, y) to the auto-grid cell origin so a dropped tile
    /// always lands on an exact grid intersection. Independent of which
    /// layer it's called from since GridStartX / GridStartY / TileWidth /
    /// TileHeight / GridGapX / GridGapY are constants. Caller is
    /// responsible for clamping to canvas bounds afterwards.
    /// </summary>
    private static (double X, double Y) SnapToGrid(double x, double y)
    {
        var col = (int)Math.Round((x - GridStartX) / (TileWidth + GridGapX));
        var row = (int)Math.Round((y - GridStartY) / (TileHeight + GridGapY));
        if (col < 0) col = 0;
        if (row < 0) row = 0;
        return (
            GridStartX + col * (TileWidth + GridGapX),
            GridStartY + row * (TileHeight + GridGapY)
        );
    }

    // ----- Cross-instance registry -----
    // Every attached icon layer registers itself so a tile drag on one
    // monitor can find the layer under the pointer at release time and
    // hand the file off (move it from this layer's backing folder into
    // the target layer's). The same registry is consulted by the desktop
    // and the file explorer when looking for cross-window drop targets.
    private static readonly List<DesktopIconLayer> _openLayers = new();

    /// <summary>
    /// Returns the icon layer whose monitor contains <paramref name="screenPos"/>
    /// (in physical screen coords), along with the local point in that
    /// layer's coordinate space. Used by cross-monitor tile drag-drop to
    /// find which monitor the user released over.
    /// <para>
    /// Resolution strategy: walks <see cref="DosiHostRegistry.All"/> and
    /// tests <see cref="IDosiHost.TargetScreen"/><c>.Bounds.Contains(screenPos)</c>.
    /// This is the SAME pattern <see cref="DOSI.CORE.UIComponents.WindowManagement.DOSIWindow"/>
    /// uses for its proven cross-monitor window-drag handoff - testing
    /// against the physical Screen.Bounds avoids every per-window
    /// <c>ClientSize</c> / <c>RenderScaling</c> rounding quirk that
    /// borderless-FullScreen secondary monitors are prone to. Once the
    /// host is identified, the layer lookup is just "which registered
    /// layer's TopLevel matches the target host's TopLevel?" - a single
    /// reference compare.
    /// </para>
    /// </summary>
    public static (DesktopIconLayer Layer, Point LocalPoint)? FindDropTarget(Avalonia.PixelPoint screenPos)
    {
        // Step 1: identify which monitor's host the screen point belongs to.
        DOSI.CORE.UIComponents.IDosiHost? targetHost = null;
        foreach (var h in DOSI.CORE.UIComponents.DosiHostRegistry.All)
        {
            var s = h.TargetScreen;
            if (s != null && s.Bounds.Contains(screenPos)) { targetHost = h; break; }
        }
        if (targetHost is not Avalonia.Controls.TopLevel targetTop) return null;

        // Step 2: find the registered DesktopIconLayer hosted by that
        // monitor's TopLevel. There's only ever one per host (DesktopScreen
        // for primary, ExtensionScreen for each secondary).
        foreach (var layer in _openLayers)
        {
            if (!ReferenceEquals(Avalonia.Controls.TopLevel.GetTopLevel(layer), targetTop)) continue;

            // Step 3: convert the screen-pixel release point into the
            // target TopLevel's DIP coords using ITS OWN PointToClient -
            // proven safe because we know screenPos is actually inside
            // this TopLevel's monitor (the registry test above just
            // confirmed it).
            Avalonia.Point local;
            try { local = targetTop.PointToClient(screenPos); }
            catch { continue; }

            var layerOrigin = layer.TranslatePoint(new Point(0, 0), targetTop);
            if (layerOrigin == null) continue;
            return (layer, new Point(local.X - layerOrigin.Value.X, local.Y - layerOrigin.Value.Y));
        }
        return null;
    }

    /// <summary>Absolute path of this layer's backing desktop folder, or null until bound.</summary>
    public string? DesktopPath => _desktopPath;

    private static AccentManager Accents => AccentManager.Instance;

    private readonly Dictionary<string, Border> _tilesByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Border> _selected = new();

    private string? _desktopPath;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _watcherDebounce;

    // Per-instance desktop sub-folder name. Primary monitor uses
    // "Desktop" (matches every prior version), secondary monitors get
    // their own ("Desktop-Monitor2", "Desktop-Monitor3", ...) so each
    // physical display has spatially-distinct icons + an independent
    // marquee + drag scope. Stored as just the folder name; the absolute
    // path is computed lazily in BindToCurrentUser.
    private readonly string _desktopFolderName;

    // After a rename / drag-drop / paste we set this to the absolute
    // path the user expects to see selected once the FSW reconcile
    // brings the new tile in. One-shot: consumed by Reconcile on the
    // first match.
    private string? _pendingSelectionPath;

    // Marquee state
    private bool _marqueeActive;
    private Point _marqueeStart;
    private Border? _marqueeRect;

    // Drag state
    private bool _draggingTiles;
    private Point _dragOriginScreen;
    private readonly Dictionary<Border, Point> _dragStartPositions = new();
    private bool _dragMoved;
    // Last cursor position in physical screen pixels, refreshed by
    // UpdateDragGhost on every PointerMoved. The release-time hit-test
    // uses THIS instead of re-converting PointerReleasedEventArgs.GetPosition
    // through PointToScreen because the latter can return stale or clamped
    // coords under pointer capture on borderless-FullScreen secondary
    // monitors - which made a release ON another monitor or ON the
    // explorer's items area silently miss the hit-test, fall through to
    // local-persist, and look like the tile "snapped back".
    private Avalonia.PixelPoint? _lastDragScreenPos;

    // ----- Cross-window / cross-monitor drag ghost -----
    // Tiles, like DOSIWindow chrome, can't render outside their parent
    // TopLevel - so dragging an icon "into" a file explorer or onto
    // another monitor visually clips at the source MonitorWindow's bezel.
    // The same pooled DragGhostWindow that DOSIWindow uses solves this:
    // we snapshot the dragged tile(s) into a bitmap, arm the ghost at
    // drag start, and atomically swap to it the first frame the cursor
    // leaves the source TopLevel. The tile stays in its source canvas
    // so the existing position-persist / handoff logic still works.
    private Avalonia.Media.Imaging.RenderTargetBitmap? _dragGhostSnapshot;
    private Avalonia.PixelPoint _dragGhostCursorOffset;
    private Avalonia.Controls.TopLevel? _dragSourceTopLevel;
    private double _dragGhostWidthDip;
    private double _dragGhostHeightDip;
    private bool _dragGhostArmed;
    private bool _dragGhostShown;

    /// <summary>
    /// Creates an icon layer backed by the primary user-Desktop folder
    /// ("<c>~/Desktop</c>"). Used by the primary <see cref="DesktopScreen"/>
    /// to preserve historical behaviour.
    /// </summary>
    public DesktopIconLayer() : this("Desktop") { }

    /// <summary>
    /// Creates an icon layer backed by <paramref name="desktopFolderName"/>
    /// under the user's home directory. Used by secondary monitors so
    /// each display gets its own spatially-distinct icon set
    /// ("<c>~/Desktop-Monitor2</c>", "<c>~/Desktop-Monitor3</c>", ...).
    /// The folder is auto-created on first bind.
    /// </summary>
    public DesktopIconLayer(string desktopFolderName)
    {
        _desktopFolderName = string.IsNullOrWhiteSpace(desktopFolderName) ? "Desktop" : desktopFolderName;
        // Stretch across the desktop. Background must be hit-test-visible
        // (transparent works) so empty-space clicks deselect / start a
        // marquee. Without a Background brush the Canvas would let clicks
        // fall through to the wallpaper layer below.
        Background = Brushes.Transparent;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        IsHitTestVisible = true;
        Focusable = true;

        PointerPressed += OnLayerPointerPressed;
        PointerMoved += OnLayerPointerMoved;
        PointerReleased += OnLayerPointerReleased;
        KeyDown += OnLayerKeyDown;

        AttachedToVisualTree += (_, _) =>
        {
            BindToCurrentUser();
            Accents.AccentChanged += OnAccentChanged;
            FileClipboard.Changed += OnClipboardChanged;
            // Secondary monitors (ExtensionScreen) attach BEFORE login, so
            // the initial BindToCurrentUser() above sees no user and leaves
            // _desktopPath == null forever. That caused cross-monitor drag
            // handoff to fail with "target layer has no DesktopPath" and
            // the tile would snap back to the source monitor. Re-bind on
            // every user change so the watcher + path are wired the moment
            // someone signs in (and re-wired on user switch / sign-out).
            UserManager.CurrentUserChanged += OnCurrentUserChanged;
            if (!_openLayers.Contains(this)) _openLayers.Add(this);

            // We're parented into a Canvas (DOSIScreen.Desktop). Canvas
            // ignores HorizontalAlignment.Stretch / VerticalAlignment.Stretch
            // and would size us to our tile content bounds only - meaning
            // clicks on the empty wallpaper region wouldn't hit us and the
            // "click outside any tile to deselect" path would never fire.
            // Mirror the parent's Bounds on every layout pass so we always
            // fill the desktop and capture every click on it.
            if (Parent is Control parent)
            {
                void Sync()
                {
                    Width = parent.Bounds.Width;
                    Height = parent.Bounds.Height;
                }
                Sync();
                parent.LayoutUpdated += (_, _) => Sync();
            }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            StopWatcher();
            Accents.AccentChanged -= OnAccentChanged;
            FileClipboard.Changed -= OnClipboardChanged;
            UserManager.CurrentUserChanged -= OnCurrentUserChanged;
            _openLayers.Remove(this);
        };
    }

    private void OnCurrentUserChanged(object? sender, DOSIUser? user)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // User signed out: drop the binding so we don't keep watching
            // the previous user's folder. Sign-in / switch: re-bind so the
            // new user's Desktop-MonitorN folder is wired up and visible
            // as a valid drop target for cross-monitor drag handoff.
            if (user == null)
            {
                StopWatcher();
                _desktopPath = null;
                Children.Clear();
                _tilesByPath.Clear();
                _selected.Clear();
                return;
            }
            BindToCurrentUser();
        });
    }

    // =====================================================================
    // User binding + watcher
    // =====================================================================

    private void BindToCurrentUser()
    {
        var user = UserManager.CurrentUser;
        if (user == null) return;
        try
        {
            UserManager.EnsureUserSubfolders(user);
            _desktopPath = Path.Combine(UserManager.GetUserFolder(user.Username), _desktopFolderName);
            Directory.CreateDirectory(_desktopPath);
            StartWatcher(_desktopPath);
            Rebuild();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DesktopIconLayer] Bind failed: {ex.Message}");
        }
    }

    private void StartWatcher(string path)
    {
        StopWatcher();
        try
        {
            _watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true
            };
            _watcher.Created += OnWatcherEvent;
            _watcher.Deleted += OnWatcherEvent;
            _watcher.Renamed += OnWatcherEvent;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DesktopIconLayer] Watcher failed: {ex.Message}");
            _watcher = null;
        }
    }

    private void StopWatcher()
    {
        if (_watcher != null)
        {
            try { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); } catch { }
            _watcher = null;
        }
        _watcherDebounce?.Stop();
        _watcherDebounce = null;
    }

    private void OnWatcherEvent(object sender, FileSystemEventArgs e)
    {
        // Coalesce burst events (a single drop can fire 4-5 events) onto
        // one rebuild on the dispatcher thread.
        Dispatcher.UIThread.Post(() =>
        {
            _watcherDebounce?.Stop();
            _watcherDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _watcherDebounce.Tick += (_, _) =>
            {
                _watcherDebounce?.Stop();
                _watcherDebounce = null;
                Reconcile();
            };
            _watcherDebounce.Start();
        });
    }

    private void OnAccentChanged(object? sender, EventArgs e)
    {
        // Re-tint every tile's selection chrome AND swap its glyph for a
        // freshly-built one (the glyph captures accent colours as plain
        // GradientStop / Brush values at build time, so it doesn't track
        // the live palette by itself). Each swap is crossfaded so the
        // change reads as a smooth animation, not a snap.
        foreach (var tile in _tilesByPath.Values)
        {
            ApplyTileVisualState(tile);
            AnimateGlyphSwap(tile);
        }
    }

    private void OnClipboardChanged(object? sender, EventArgs e)
    {
        // Greyscale visual on cut tiles - identical to how every other OS
        // signals "this will move on next paste".
        foreach (var tile in _tilesByPath.Values)
            ApplyTileVisualState(tile);
    }

    // =====================================================================
    // Rebuild
    // =====================================================================

    /// <summary>
    /// Full (re)build from disk. Used on initial bind / user switch where
    /// no animation is wanted. Watcher events route through
    /// <see cref="Reconcile"/> instead so individual create / delete
    /// events animate cleanly without redrawing every untouched tile.
    /// </summary>
    private void Rebuild()
    {
        if (_desktopPath == null) return;
        Children.Clear();
        _tilesByPath.Clear();
        _selected.Clear();

        IEnumerable<string> entries;
        try
        {
            // Folders first, then files - matches how the file explorer
            // sorts its grid view.
            var dirs  = Directory.EnumerateDirectories(_desktopPath);
            var files = Directory.EnumerateFiles(_desktopPath)
                .Where(f => !IsHiddenSettingsFile(f));
            entries = dirs.Concat(files);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DesktopIconLayer] Enumerate failed: {ex.Message}");
            return;
        }

        // First pass: build tiles, place at saved positions.
        var unplaced = new List<Border>();
        foreach (var path in entries)
        {
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) continue;

            var isDir = Directory.Exists(path);
            var tile = BuildTile(path, name, isDir);
            _tilesByPath[path] = tile;

            var saved = DesktopIconLayout.Get(name);
            if (saved != null)
            {
                // Forward-migrate any pre-existing positions that fall under
                // the taskbar (saved before the taskbar inset existed).
                var sx = saved.X;
                var sy = Math.Max(TaskbarHeight + 4, saved.Y);
                Canvas.SetLeft(tile, sx);
                Canvas.SetTop(tile, sy);
                if (sy != saved.Y) DesktopIconLayout.Save(name, sx, sy);
                Children.Add(tile);
            }
            else
            {
                unplaced.Add(tile);
            }
        }

        // Second pass: auto-grid every tile that didn't have a saved
        // position. Skip cells already occupied by saved-position tiles
        // so a partially-customised layout doesn't have new icons land
        // on top of existing ones.
        AutoPlace(unplaced, animate: false);

        // Self-heal: drop any layout keys whose file no longer exists
        // in ANY desktop folder. Catches pre-fix orphans + anything a
        // crash / external mutation left dangling, so a same-name
        // re-create can never inherit the dead entry's pinned position
        // ("New folder (5)" syndrome).
        DesktopIconLayout.PruneOrphans();
    }

    /// <summary>
    /// Diff the current set of tiles against what's on disk and animate
    /// just the deltas: new files fade + scale-in, deleted files fade +
    /// scale-out, untouched files are left exactly where they are. This
    /// is what watcher events drive so a single create or delete reads
    /// as a smooth motion instead of every tile flickering through a
    /// full rebuild.
    /// </summary>
    private void Reconcile()
    {
        if (_desktopPath == null) return;

        HashSet<string> diskNow;
        try
        {
            diskNow = new HashSet<string>(
                Directory.EnumerateFileSystemEntries(_desktopPath)
                         .Where(p => !IsHiddenSettingsFile(p)),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DesktopIconLayer] Reconcile enumerate failed: {ex.Message}");
            return;
        }

        // Removals first: anything in the cache that no longer exists on
        // disk. Animate out, then drop.
        var gone = _tilesByPath
            .Where(kv => !diskNow.Contains(kv.Key))
            .Select(kv => kv.Key)
            .ToList();
        foreach (var path in gone)
        {
            var tile = _tilesByPath[path];
            _tilesByPath.Remove(path);
            _selected.Remove(tile);
            AnimateTileOut(tile, () => Children.Remove(tile));
        }

        // Insertions: anything on disk we don't have a tile for yet.
        // Auto-place each through the same path as Rebuild so saved
        // positions are honoured on a freshly-pasted file.
        var fresh = new List<Border>();
        foreach (var path in diskNow)
        {
            if (_tilesByPath.ContainsKey(path)) continue;

            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) continue;

            var isDir = Directory.Exists(path);
            var tile = BuildTile(path, name, isDir);
            _tilesByPath[path] = tile;

            var saved = DesktopIconLayout.Get(name);
            if (saved != null)
            {
                var sx = saved.X;
                var sy = Math.Max(TaskbarHeight + 4, saved.Y);
                Canvas.SetLeft(tile, sx);
                Canvas.SetTop(tile, sy);
                Children.Add(tile);
                AnimateTileIn(tile);
                TryConsumePendingSelection(tile, path);
            }
            else
            {
                fresh.Add(tile);
            }
        }

        if (fresh.Count > 0) AutoPlace(fresh, animate: true);

        // Self-heal: same rationale as Rebuild. Watcher-driven reconcile
        // is the steady-state code path, so this is where the JSON
        // converges to disk after the user (or anything else) mutates
        // a desktop folder.
        DesktopIconLayout.PruneOrphans();
    }

    /// <summary>
    /// Cross-layer entry point: forces this layer to immediately diff
    /// against disk and animate any new tiles in. Called by the source
    /// layer right after a successful cross-monitor file move so the
    /// destination materializes the dropped tile in the same frame
    /// instead of waiting on its own FileSystemWatcher debounce (~250
    /// ms). Eliminates the visible "nothing on either monitor" gap that
    /// otherwise looks like the dragged tile vanished.
    /// </summary>
    internal void ForceReconcileFromExternal()
    {
        if (_desktopPath == null) return;
        // Cancel any pending watcher reconcile so we don't double-animate
        // the same insert when the watcher's debounce eventually fires.
        _watcherDebounce?.Stop();
        _watcherDebounce = null;
        Reconcile();
    }

    private static bool IsHiddenSettingsFile(string path)
    {
        var name = Path.GetFileName(path);
        // Don't render the layout file itself etc. as a desktop icon.
        return !string.IsNullOrEmpty(name) && name.StartsWith('.');
    }

    /// <summary>
    /// Forces every tile on this layer into a clean row-major grid,
    /// starting at the top-left, ignoring previously saved positions.
    /// One-shot tidy that's useful after a paste flurry or a layout-file
    /// corruption left icons stacked on top of each other. Persists the
    /// new positions immediately so the arrangement survives a rebuild.
    /// </summary>
    public void AutoArrangeAll()
    {
        if (_desktopPath == null) return;

        // Snapshot, clear positions, then funnel through AutoPlace so it
        // computes occupied cells from an empty layer and emits a clean
        // top-left-first walk.
        var tiles = _tilesByPath.Values
            .Where(Children.Contains)
            .OrderBy(t => t.Tag is TileMeta meta ? meta.Name : string.Empty,
                     StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Drop the tiles' canvas coords so AutoPlace's occupied-cell
        // calculation starts from a blank slate; otherwise the existing
        // positions (which may include the very stack we're trying to
        // unjam) would lock cells against reassignment.
        foreach (var tile in tiles)
        {
            Children.Remove(tile);
        }

        AutoPlace(tiles, animate: false);
    }

    private void AutoPlace(List<Border> unplaced, bool animate)
    {
        if (unplaced.Count == 0) return;

        // Pre-compute occupied cells from already-placed tiles.
        var occupied = new HashSet<(int col, int row)>();
        foreach (var existing in _tilesByPath.Values.Where(Children.Contains))
        {
            var cx = Canvas.GetLeft(existing);
            var cy = Canvas.GetTop(existing);
            if (double.IsNaN(cx) || double.IsNaN(cy)) continue;
            var c = (int)Math.Round((cx - GridStartX) / (TileWidth + GridGapX));
            var r = (int)Math.Round((cy - GridStartY) / (TileHeight + GridGapY));
            if (c >= 0 && r >= 0) occupied.Add((c, r));
        }

        // Walk row-major picking the next free cell.
        int col = 0, row = 0;
        foreach (var tile in unplaced)
        {
            while (occupied.Contains((col, row)))
            {
                row++;
                // Cap the column count at something sane (8) so we don't
                // walk off-screen. Real screen-bounds clamp happens on drop.
                if (row > 12) { row = 0; col++; }
            }
            occupied.Add((col, row));
            var x = GridStartX + col * (TileWidth + GridGapX);
            var y = GridStartY + row * (TileHeight + GridGapY);
            Canvas.SetLeft(tile, x);
            Canvas.SetTop(tile, y);
            Children.Add(tile);
            if (animate) AnimateTileIn(tile);
            if (animate && tile.Tag is TileMeta autoMeta)
                TryConsumePendingSelection(tile, autoMeta.Path);

            // Persist auto-placed positions immediately so the layout
            // stays stable across rebuilds (a watcher event would
            // otherwise re-shuffle anything not yet persisted).
            if (tile.Tag is TileMeta meta)
                DesktopIconLayout.Save(meta.Name, x, y);

            row++;
            if (row > 12) { row = 0; col++; }
        }
    }

    // =====================================================================
    // Tile rendering
    // =====================================================================

    /// <summary>
    /// Per-tile metadata. Stored on <see cref="Border.Tag"/> so any code
    /// path with a tile reference can recover the absolute path, the
    /// file name, the directory flag, the inner host that owns the
    /// glyph (for accent re-tinting), AND the label TextBlock (for
    /// inline-rename swap-in).
    /// </summary>
    private sealed record TileMeta(string Path, string Name, bool IsDirectory, Border IconHost, TextBlock Label);

    private Border BuildTile(string path, string name, bool isDirectory)
    {
        var glyph = isDirectory ? BuildFolderGlyph() : BuildFileGlyph(name);

        // Swappable host for the glyph - OnAccentChanged crossfades a
        // freshly-built glyph in here so accent flips re-tint the tile
        // without us rebuilding (and re-positioning) the whole tile.
        var iconHost = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Child = glyph
        };

        var label = new TextBlock
        {
            Text = name,
            FontSize = 11,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = TileWidth - 8,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 6, 0, 0),
            // Tight readable shadow so labels survive over light wallpapers
            // - the wallpaper hue is unknown, so we lean on a dark drop.
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Children = { iconHost, label }
        };

        var tile = new Border
        {
            Width = TileWidth,
            Height = TileHeight,
            Padding = new Thickness(4, 6),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = stack,
            Tag = new TileMeta(path, name, isDirectory, iconHost, label)
        };

        // Click directly on the label while the tile is already the lone
        // selection begins an inline rename - same pattern Windows /
        // macOS use. The first click selects the tile (handled by the
        // tile's PointerPressed); the second click on the label arrives
        // a moment later and triggers rename. Clicks on the GLYPH never
        // start rename - reserved for activation / drag.
        label.PointerPressed += (_, e) =>
        {
            if (_selected.Contains(tile) && _selected.Count == 1)
            {
                e.Handled = true;
                BeginInlineRename(tile);
            }
        };

        tile.PointerEntered += (_, _) =>
        {
            if (!_selected.Contains(tile))
                tile.Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
        };
        tile.PointerExited += (_, _) =>
        {
            if (!_selected.Contains(tile))
                tile.Background = Brushes.Transparent;
        };

        tile.PointerPressed += (s, e) => OnTilePointerPressed(tile, e);
        tile.DoubleTapped += (s, e) =>
        {
            e.Handled = true;
            ActivateTile(tile);
        };

        // Per-tile context menu
        tile.ContextMenu = BuildTileContextMenu(tile);

        return tile;
    }

    private DOSIContextMenu BuildTileContextMenu(Border tile)
    {
        var menu = new DOSIContextMenu();

        var open = new MenuItem { Header = "Open" };
        open.Click += (_, _) => ActivateTile(tile);

        var copy = new MenuItem { Header = "Copy" };
        copy.Click += (_, _) => { if (tile.Tag is TileMeta m) FileClipboard.Copy(m.Path); };

        var cut = new MenuItem { Header = "Cut" };
        cut.Click += (_, _) => { if (tile.Tag is TileMeta m) FileClipboard.Cut(m.Path); };

        var rename = new MenuItem { Header = "Rename" };
        rename.Click += (_, _) => BeginInlineRename(tile);

        var delete = new MenuItem { Header = "Delete" };
        delete.Click += (_, _) => DeleteTiles(GetEffectiveSelection(tile));

        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        menu.Items.Add(copy);
        menu.Items.Add(cut);
        menu.Items.Add(new Separator());
        menu.Items.Add(rename);
        menu.Items.Add(delete);

        // If right-click happens on a tile that isn't selected, replace
        // the current selection with just this tile so the menu actions
        // operate on what the user clicked, not on a stale selection.
        menu.Opening += (_, _) =>
        {
            if (!_selected.Contains(tile))
            {
                ClearSelection();
                Select(tile);
            }
        };

        return menu;
    }

    private List<Border> GetEffectiveSelection(Border anchor)
    {
        if (_selected.Contains(anchor)) return _selected.ToList();
        return new List<Border> { anchor };
    }

    // =====================================================================
    // Wallpaper (empty-space) context menu
    //
    // Built per-layer so EACH monitor's right-click menu targets its OWN
    // desktop folder (~/Desktop on the primary, ~/Desktop-MonitorN on
    // extensions). DesktopScreen and ExtensionScreen both attach this to
    // their Desktop canvas's ContextMenu - previously only the primary
    // had a wallpaper menu because DesktopScreen owned the implementation
    // outright and ExtensionScreen never wired one. Hosting the builder
    // on DesktopIconLayer keeps the per-monitor folder routing implicit:
    // every menu action uses _desktopPath, which is already bound to the
    // correct subfolder by BindToCurrentUser.
    // =====================================================================

    /// <summary>
    /// Builds the wallpaper / empty-space right-click menu for THIS layer's
    /// monitor. Paste + New folder + Open Files all target
    /// <see cref="_desktopPath"/> so a right-click on monitor 2 creates the
    /// new folder on monitor 2, not on monitor 1.
    /// </summary>
    public DOSIContextMenu BuildWallpaperContextMenu()
    {
        var menu = new DOSIContextMenu();

        var refresh = new MenuItem { Header = "Refresh" };
        refresh.Click += (_, _) => Reconcile();

        var paste = new MenuItem { Header = "Paste" };
        paste.Click += (_, _) => PasteClipboardHere();

        var newFolder = new MenuItem { Header = "New folder" };
        newFolder.Click += (_, _) => CreateNewFolderHere();

        // Snap-to-grid toggle - shared static state so every layer's
        // wallpaper menu observes the same checked/unchecked state and
        // a flip on monitor 1 affects future drops on monitor 2 too.
        var snapToggle = new MenuItem { Header = "Snap to grid" };
        snapToggle.Click += (_, _) => _snapToGridEnabled = !_snapToGridEnabled;

        // Auto-arrange: one-shot tidy. Doesn't change the persistent
        // snap-to-grid preference, just walks current tiles into the
        // grid from the top-left. Useful escape hatch when a layout
        // file gets corrupted or a paste flurry stacks icons.
        var autoArrange = new MenuItem { Header = "Auto-arrange icons" };
        autoArrange.Click += (_, _) => AutoArrangeAll();

        var openExplorer = new MenuItem { Header = "Open Files" };
        openExplorer.Click += (_, _) =>
        {
            var explorer = new DOSIFileExplorer();
            if (!string.IsNullOrEmpty(_desktopPath)) explorer.RequestNavigate(_desktopPath);
            WindowManager.Instance?.OpenWindow(explorer);
        };

        var openTrash = new MenuItem { Header = "Open Trash" };
        openTrash.Click += (_, _) =>
        {
            var user = UserManager.CurrentUser;
            if (user == null) return;
            var explorer = new DOSIFileExplorer();
            explorer.RequestNavigate(FileTrash.GetTrashRoot(user));
            WindowManager.Instance?.OpenWindow(explorer);
        };

        menu.Opening += (_, _) =>
        {
            paste.IsEnabled = FileClipboard.HasContent;
            paste.Header = FileClipboard.HasContent && !string.IsNullOrEmpty(FileClipboard.Path)
                ? $"Paste \u201C{Path.GetFileName(FileClipboard.Path!.TrimEnd(Path.DirectorySeparatorChar))}\u201D"
                : "Paste";

            // Reflect the live snap-to-grid state with a leading check
            // mark - simplest portable "checked menu item" affordance
            // that doesn't require dropping in a ToggleMenuItem subclass.
            snapToggle.Header = _snapToGridEnabled ? "\u2713  Snap to grid" : "    Snap to grid";

            // Auto-arrange is only meaningful when there's something to
            // arrange; disable it on an empty desktop so the user doesn't
            // wonder why nothing happens.
            autoArrange.IsEnabled = _tilesByPath.Count > 0;

            var user = UserManager.CurrentUser;
            if (user != null)
            {
                var n = FileTrash.List(user).Count;
                openTrash.Header = n > 0
                    ? $"Open Trash ({n})"
                    : "Open Trash";
                openTrash.IsEnabled = true;
            }
            else
            {
                openTrash.IsEnabled = false;
            }
        };

        menu.Items.Add(refresh);
        menu.Items.Add(paste);
        menu.Items.Add(new Separator());
        menu.Items.Add(newFolder);
        menu.Items.Add(new Separator());
        menu.Items.Add(snapToggle);
        menu.Items.Add(autoArrange);
        menu.Items.Add(new Separator());
        menu.Items.Add(openExplorer);
        menu.Items.Add(openTrash);
        return menu;
    }

    /// <summary>
    /// Pastes the staged <see cref="FileClipboard"/> entry into this
    /// layer's desktop folder. Honours Cut vs Copy semantics and renames
    /// on collision so the user never silently clobbers an existing file.
    /// The watcher animates the resulting tile in automatically.
    /// </summary>
    private void PasteClipboardHere()
    {
        if (!FileClipboard.HasContent) return;
        if (string.IsNullOrEmpty(_desktopPath)) return;
        var src = FileClipboard.Path;
        if (string.IsNullOrEmpty(src) ||
            (!File.Exists(src) && !Directory.Exists(src)))
        {
            FileClipboard.Clear();
            try { DOSIPopNotification.Show("Clipboard source no longer exists."); } catch { }
            return;
        }
        try
        {
            Directory.CreateDirectory(_desktopPath);
            var name = Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar));
            var dst = ChooseUniqueDestination(Path.Combine(_desktopPath, name));
            if (FileClipboard.CurrentMode == FileClipboard.Mode.Cut)
            {
                if (Directory.Exists(src)) Directory.Move(src, dst);
                else                       File.Move(src, dst);
                DesktopIconLayout.RenameIfOnDesktop(src, dst);
                FileClipboard.Clear();
            }
            else
            {
                if (Directory.Exists(src)) CopyDirectoryRecursive(src, dst);
                else                       File.Copy(src, dst, overwrite: false);
            }
            _pendingSelectionPath = Path.GetFullPath(dst);
        }
        catch (Exception ex)
        {
            try { DOSIPopNotification.Show($"Paste failed: {ex.Message}"); } catch { }
        }
    }

    /// <summary>
    /// Creates "New folder" / "New folder (2)" / ... in this layer's
    /// desktop folder. Selection focuses the new tile once the watcher
    /// reconciles it.
    /// </summary>
    private void CreateNewFolderHere()
    {
        if (string.IsNullOrEmpty(_desktopPath)) return;
        try
        {
            Directory.CreateDirectory(_desktopPath);
            var dst = ChooseUniqueDestination(Path.Combine(_desktopPath, "New folder"));
            Directory.CreateDirectory(dst);
            _pendingSelectionPath = Path.GetFullPath(dst);
        }
        catch (Exception ex)
        {
            try { DOSIPopNotification.Show($"Could not create folder: {ex.Message}"); } catch { }
        }
    }

    private static void CopyDirectoryRecursive(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: false);
        foreach (var d in Directory.EnumerateDirectories(src))
            CopyDirectoryRecursive(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    // =====================================================================
    // Selection
    // =====================================================================

    private void Select(Border tile, bool additive = false)
    {
        if (!additive) ClearSelection();
        if (_selected.Add(tile)) ApplyTileVisualState(tile);
    }

    private void Deselect(Border tile)
    {
        if (_selected.Remove(tile)) ApplyTileVisualState(tile);
    }

    private void ClearSelection()
    {
        var snapshot = _selected.ToList();
        _selected.Clear();
        foreach (var t in snapshot) ApplyTileVisualState(t);
    }

    private void ApplyTileVisualState(Border tile)
    {
        if (_selected.Contains(tile))
        {
            // Accent-tinted selection: stronger fill + accent border, just
            // like the file-explorer tile selection (DOSIFileExplorer.SelectTile).
            var a = Accents.AccentPrimary;
            tile.Background = new SolidColorBrush(Color.FromArgb(90, a.R, a.G, a.B));
            tile.BorderBrush = new SolidColorBrush(Color.FromArgb(180, a.R, a.G, a.B));
        }
        else
        {
            tile.Background = Brushes.Transparent;
            tile.BorderBrush = Brushes.Transparent;
        }

        // Cut indicator - lighter opacity on tiles staged for a move.
        if (tile.Tag is TileMeta m &&
            FileClipboard.HasContent &&
            FileClipboard.CurrentMode == FileClipboard.Mode.Cut &&
            string.Equals(FileClipboard.Path, m.Path, StringComparison.OrdinalIgnoreCase))
        {
            tile.Opacity = 0.55;
        }
        else
        {
            tile.Opacity = 1.0;
        }
    }

    // =====================================================================
    // Pointer interactions (tile clicks vs. marquee vs. drag)
    // =====================================================================

    private void OnTilePointerPressed(Border tile, PointerPressedEventArgs e)
    {
        // Right-click bubbles up to the context menu host - let it open
        // without stealing the gesture for selection/drag.
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed) return;

        e.Handled = true;
        Focus();

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (ctrl)
        {
            // Toggle membership.
            if (_selected.Contains(tile)) Deselect(tile);
            else Select(tile, additive: true);
        }
        else
        {
            // If the tile isn't already in the selection, replace the
            // selection with just this tile. If it IS already selected,
            // KEEP the existing multi-selection so a drag moves the group.
            if (!_selected.Contains(tile))
                Select(tile);
        }

        // Begin a potential drag. We DON'T capture the pointer yet -
        // doing so would intercept the second press of a double-click
        // (the captured target becomes the layer, not the tile, so the
        // tile's DoubleTapped never resolves). Capture is taken in
        // OnLayerPointerMoved once the pointer crosses the drag
        // threshold, by which point a double-click is no longer in play.
        _draggingTiles = true;
        _dragMoved = false;
        _dragOriginScreen = e.GetPosition(this);
        _dragStartPositions.Clear();
        foreach (var t in _selected)
        {
            _dragStartPositions[t] = new Point(Canvas.GetLeft(t), Canvas.GetTop(t));
        }

        ArmDragGhost(tile, e);
    }

    private void OnLayerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Empty-space click: clear selection (unless Ctrl is held, in which
        // case we preserve current and start a marquee that adds).
        var src = e.Source as Visual;
        var hitTile = src as Border;
        if (hitTile == null || hitTile.Tag is not TileMeta)
        {
            // Could still be a child of a tile - walk up to find one.
            hitTile = FindAncestorTile(src);
        }

        if (hitTile != null) return; // tile handler above will deal with it

        // Right-click on empty desktop falls through to the desktop-screen
        // wallpaper context menu (Refresh / Paste / New folder / Open Files).
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed) return;

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
            ClearSelection();

        // Begin a marquee.
        _marqueeActive = true;
        _marqueeStart = e.GetPosition(this);
        _marqueeRect = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(40, Accents.AccentPrimary.R, Accents.AccentPrimary.G, Accents.AccentPrimary.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(180, Accents.AccentPrimary.R, Accents.AccentPrimary.G, Accents.AccentPrimary.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Width = 0,
            Height = 0,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(_marqueeRect, _marqueeStart.X);
        Canvas.SetTop(_marqueeRect, _marqueeStart.Y);
        Children.Add(_marqueeRect);

        e.Pointer.Capture(this);
        e.Handled = true;
        Focus();
    }

    private static Border? FindAncestorTile(Visual? v)
    {
        while (v != null)
        {
            if (v is Border b && b.Tag is TileMeta) return b;
            v = v.GetVisualParent();
        }
        return null;
    }

    private void OnLayerPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggingTiles)
        {
            var p = e.GetPosition(this);
            var dx = p.X - _dragOriginScreen.X;
            var dy = p.Y - _dragOriginScreen.Y;

            // Movement threshold so a click that doesn't actually drag
            // doesn't fire the persistence path.
            if (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) < 4) return;
            if (!_dragMoved)
            {
                _dragMoved = true;
                // Now that we know it's really a drag (not a click that
                // might be the first half of a double-click), capture the
                // pointer so movement outside the tile still tracks.
                e.Pointer.Capture(this);
            }

            foreach (var (tile, start) in _dragStartPositions)
            {
                var nx = Math.Max(0, start.X + dx);
                // Clamp Y to the taskbar bottom so a user can't accidentally
                // drop an icon into the dead zone behind the taskbar.
                var ny = Math.Max(TaskbarHeight + 4, start.Y + dy);
                Canvas.SetLeft(tile, nx);
                Canvas.SetTop(tile, ny);
            }

            UpdateDragGhost(e);
        }
        else if (_marqueeActive && _marqueeRect != null)
        {
            var p = e.GetPosition(this);
            var x = Math.Min(p.X, _marqueeStart.X);
            var y = Math.Min(p.Y, _marqueeStart.Y);
            var w = Math.Abs(p.X - _marqueeStart.X);
            var h = Math.Abs(p.Y - _marqueeStart.Y);
            Canvas.SetLeft(_marqueeRect, x);
            Canvas.SetTop(_marqueeRect, y);
            _marqueeRect.Width = w;
            _marqueeRect.Height = h;

            // Live-update selection to reflect what's inside the marquee.
            // Ctrl preserves the prior selection (additive marquee).
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                var snap = _selected.ToList();
                foreach (var t in snap) Deselect(t);
            }
            var marqueeRect = new Rect(x, y, w, h);
            foreach (var tile in _tilesByPath.Values)
            {
                var tileRect = new Rect(
                    Canvas.GetLeft(tile), Canvas.GetTop(tile),
                    tile.Width, tile.Height);
                if (marqueeRect.Intersects(tileRect))
                {
                    if (!_selected.Contains(tile)) Select(tile, additive: true);
                }
            }
        }
    }

    // =====================================================================
    // Cross-window / cross-monitor drag ghost
    //
    // Mirrors the proven DOSIWindow drag-ghost pattern: snapshot the
    // dragged tile(s) into a RenderTargetBitmap at drag start, arm the
    // pooled DragGhostWindow (configured but hidden), then flip its
    // opacity the first frame the cursor leaves the source TopLevel.
    // Without this, dragging a desktop icon visually clips at the
    // monitor bezel / source-window edge because Avalonia controls can't
    // render outside their parent native window - the SAME constraint
    // that motivated DragGhostWindow's existence for DOSIWindow.
    // =====================================================================

    /// <summary>
    /// Snapshots the visual rect that encloses every tile in
    /// <see cref="_dragStartPositions"/> and configures the pooled
    /// <see cref="DragGhostWindow"/> with that bitmap. The ghost stays
    /// invisible (Opacity = 0) until the cursor actually leaves the
    /// source TopLevel - see <see cref="UpdateDragGhost"/>. The cursor
    /// offset is captured against the anchor tile so the ghost stays
    /// anchored under the user's finger no matter how the snapshot
    /// rect's origin relates to the tile they grabbed.
    /// </summary>
    private void ArmDragGhost(Border anchorTile, PointerPressedEventArgs e)
    {
        // Single-monitor + no other DOSI hosts: nothing to handoff to,
        // skip the snapshot cost.
        if (DOSI.CORE.UIComponents.DosiHostRegistry.All.Count <= 1 &&
            DAX.OSI.DefaultApplications.DOSIFileExplorer.OpenInstanceCount == 0)
        {
            // Even on single-monitor, we still want the ghost when a file
            // explorer is open so the tile can visibly cross the explorer
            // window. Skip only when there's literally nowhere to drop.
            return;
        }

        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        try
        {
            // Compute the bounding rect that encloses every dragged tile
            // in our (DesktopIconLayer) DIP coords. For a single-tile drag
            // this is just the tile's own rect.
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var tile in _dragStartPositions.Keys)
            {
                var tx = Canvas.GetLeft(tile);
                var ty = Canvas.GetTop(tile);
                if (tx < minX) minX = tx;
                if (ty < minY) minY = ty;
                if (tx + tile.Width  > maxX) maxX = tx + tile.Width;
                if (ty + tile.Height > maxY) maxY = ty + tile.Height;
            }
            if (minX == double.MaxValue) return;

            var groupW = Math.Max(1, maxX - minX);
            var groupH = Math.Max(1, maxY - minY);

            // Snapshot the entire layer (cheap - it's a Canvas with N
            // tiles, no heavy children) then we'll position the ghost
            // so the snapshot's group-rect lines up under the cursor.
            // Rendering just the group rect would need a temporary
            // visual; rendering the layer and offsetting the ghost is
            // simpler and the bitmap memory cost is trivial.
            var scaling = topLevel.RenderScaling > 0 ? topLevel.RenderScaling : 1.0;
            var pixelSize = new Avalonia.PixelSize(
                Math.Max(1, (int)(groupW * scaling)),
                Math.Max(1, (int)(groupH * scaling)));
            var dpi = new Avalonia.Vector(96 * scaling, 96 * scaling);
            var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(pixelSize, dpi);

            // Render each dragged tile into the bitmap at its offset from
            // the group origin. Avoid snapshotting `this` (the whole layer)
            // because that would include unselected tiles + the marquee
            // layer.
            //
            // CRITICAL: do NOT call Measure/Arrange here. The tiles are
            // already arranged by the parent Canvas through their
            // Canvas.Left / Canvas.Top attached properties; forcing a
            // re-Arrange with a Rect anchored at (0,0) triggers a layout
            // pass that briefly displaces the live tile to the canvas
            // origin - which is the "tile flashes in the upper-left for a
            // split second on drag start" symptom. ImmediateRenderTo
            // walks the visual tree and renders into the bitmap context
            // directly; it doesn't need layout to have just run.
            using (var ctx = bmp.CreateDrawingContext())
            {
                foreach (var tile in _dragStartPositions.Keys)
                {
                    var tx = Canvas.GetLeft(tile);
                    var ty = Canvas.GetTop(tile);
                    using (ctx.PushTransform(Avalonia.Matrix.CreateTranslation(tx - minX, ty - minY)))
                    {
                        ImmediateRenderTo(ctx, tile);
                    }
                }
            }

            _dragGhostSnapshot = bmp;
            _dragGhostWidthDip = groupW;
            _dragGhostHeightDip = groupH;
            _dragSourceTopLevel = topLevel;

            // Cursor offset: position of cursor relative to the group's
            // top-left, in pixels at source scaling. The ghost is sized
            // in DIPs (groupW/H) and positioned in screen pixels.
            var cursorLocal = e.GetPosition(this);
            var groupOriginLocal = new Point(minX, minY);
            var cursorScreen = topLevel.PointToScreen(this.TranslatePoint(cursorLocal, topLevel) ?? cursorLocal);
            var groupOriginScreen = topLevel.PointToScreen(this.TranslatePoint(groupOriginLocal, topLevel) ?? groupOriginLocal);
            _dragGhostCursorOffset = new Avalonia.PixelPoint(
                cursorScreen.X - groupOriginScreen.X,
                cursorScreen.Y - groupOriginScreen.Y);

            var ghost = DOSI.CORE.UIComponents.WindowManagement.DragGhostWindow.GetOrCreate();
            ghost.ConfigureFor(bmp, groupW, groupH, groupOriginScreen);
            ghost.SetVisible(false);

            _dragGhostArmed = true;
            _dragGhostShown = false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DesktopIconLayer] ArmDragGhost failed: {ex.Message}");
            _dragGhostSnapshot = null;
            _dragGhostArmed = false;
            _dragGhostShown = false;
            _dragSourceTopLevel = null;
        }
    }

    /// <summary>
    /// Avalonia's <see cref="Avalonia.Media.Imaging.RenderTargetBitmap.Render"/>
    /// only accepts a single root visual. To composite N tiles into one
    /// bitmap with per-tile offsets, render directly via the drawing
    /// context using each control's own Render virtual. Walking the
    /// visual tree manually keeps us off of internal renderer APIs.
    /// </summary>
    private static void ImmediateRenderTo(Avalonia.Media.DrawingContext ctx, Visual visual)
    {
        visual.Render(ctx);
        foreach (var child in visual.GetVisualChildren())
        {
            var tx = child.Bounds.X;
            var ty = child.Bounds.Y;
            using (ctx.PushTransform(Avalonia.Matrix.CreateTranslation(tx, ty)))
            {
                ImmediateRenderTo(ctx, child);
            }
        }
    }

    /// <summary>
    /// Moves the ghost to follow the cursor. Shows the ghost the very
    /// first frame of a real drag (regardless of whether the cursor has
    /// crossed a TopLevel boundary) and hides the in-source tiles so
    /// the dragged content is visible OVER every DOSIWindow chrome in
    /// the source MonitorWindow's _globalOverlay - which is what makes
    /// a drag-onto-an-open-DOSIFileExplorer visually work. Without this
    /// the source tile renders UNDER the explorer window (the wallpaper
    /// Canvas sits beneath _globalOverlay in the host grid) and the
    /// drag looks like nothing's happening, even though the cursor is
    /// tracking. Hot path - called on every PointerMoved.
    /// </summary>
    private void UpdateDragGhost(PointerEventArgs e)
    {
        if (!_dragGhostArmed || _dragSourceTopLevel == null) return;
        var ghost = DOSI.CORE.UIComponents.WindowManagement.DragGhostWindow.Shared;
        if (ghost == null) return;

        Avalonia.PixelPoint cursorScreen;
        try
        {
            var cursorLocal = e.GetPosition(_dragSourceTopLevel);
            cursorScreen = _dragSourceTopLevel.PointToScreen(cursorLocal);
        }
        catch { return; }

        // Cache the live screen cursor for the release-time hit-test.
        // This is the SAME value we just used to position the ghost, so
        // by definition it's the cursor location the user actually sees.
        _lastDragScreenPos = cursorScreen;

        var groupScreen = new Avalonia.PixelPoint(
            cursorScreen.X - _dragGhostCursorOffset.X,
            cursorScreen.Y - _dragGhostCursorOffset.Y);
        ghost.MoveTo(groupScreen);

        // Show the ghost on first PointerMoved tick of a real drag. The
        // ghost is a topmost transparent click-through window, so it
        // floats above EVERY DOSI surface (wallpaper canvas, app windows,
        // taskbar, secondary monitors) for the entire drag - which is the
        // only way to get a visible cross-canvas / cross-monitor drag in
        // Avalonia (controls can't render outside their parent TopLevel).
        if (!_dragGhostShown)
        {
            ghost.SetVisible(true);
            foreach (var tile in _dragStartPositions.Keys) tile.Opacity = 0;
            _dragGhostShown = true;
        }
    }

    /// <summary>
    /// Restores tile opacity, hides the ghost, and releases the snapshot.
    /// Called from <see cref="OnLayerPointerReleased"/> regardless of
    /// whether the drop was local, cross-window, or cross-monitor.
    /// </summary>
    private void TeardownDragGhost()
    {
        if (!_dragGhostArmed && !_dragGhostShown) return;
        try { DOSI.CORE.UIComponents.WindowManagement.DragGhostWindow.Shared?.HideGhost(); } catch { }
        foreach (var tile in _dragStartPositions.Keys) tile.Opacity = 1;
        _dragGhostSnapshot = null;
        _dragGhostArmed = false;
        _dragGhostShown = false;
        _dragSourceTopLevel = null;
    }

    private void OnLayerPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_draggingTiles)
        {
            _draggingTiles = false;
            // Always tear down the ghost - drop logic below moves the
            // file(s) on disk; the destination's watcher will materialise
            // the real tile, so no ghost needs to persist.
            TeardownDragGhost();
            if (_dragMoved)
            {
                // Cross-surface handoff: if the pointer was released over
                // another monitor's DesktopIconLayer or inside an open
                // file-explorer's items area, move the file(s) into that
                // target folder instead of persisting a local position.
                // The watcher on both sides animates the visual transfer
                // (source tile fades out, target tile fades in) - no
                // bespoke animation needed and the on-disk state is the
                // single source of truth, so an in-flight crash leaves
                // the user's files in a consistent place.
                bool handed = TryHandoffDraggedTiles(e);
                if (!handed)
                {
                    // Local drag: persist the new position for every dragged tile.
                    foreach (var tile in _dragStartPositions.Keys)
                    {
                        if (tile.Tag is TileMeta m)
                        {
                            var x = Canvas.GetLeft(tile);
                            var y = Canvas.GetTop(tile);
                            // Snap-to-grid: if enabled, quantise both the
                            // saved coords AND the live Canvas position so
                            // the tile visibly lands on the cell on release
                            // (no separate animation - just a one-frame jump,
                            // which reads as the snap "happening").
                            if (_snapToGridEnabled)
                            {
                                (x, y) = SnapToGrid(x, y);
                                Canvas.SetLeft(tile, x);
                                Canvas.SetTop(tile, y);
                            }
                            DesktopIconLayout.Save(m.Name, x, y);
                        }
                    }
                }
            }
            _dragStartPositions.Clear();
            _lastDragScreenPos = null;
        }

        if (_marqueeActive)
        {
            _marqueeActive = false;
            if (_marqueeRect != null)
            {
                Children.Remove(_marqueeRect);
                _marqueeRect = null;
            }
        }

        e.Pointer.Capture(null);
    }

    /// <summary>
    /// Cross-surface drag handoff. Uses the cached <see cref="_lastDragScreenPos"/>
    /// (the same screen coord the ghost was tracking) as the authoritative
    /// release point - re-deriving it from <see cref="PointerReleasedEventArgs"/>
    /// via <c>PointToScreen</c> at release time is unreliable on borderless
    /// FullScreen secondary monitors under pointer capture, which was the
    /// root cause of the "tile snaps back to its original monitor" symptom.
    /// Priority: (1) open file-explorer items area, (2) another monitor's
    /// DesktopIconLayer. On a hit we move the file(s) on disk; the destination's
    /// FileSystemWatcher materializes the real tile on the other side and
    /// the source's watcher removes the local one.
    /// </summary>
    private bool TryHandoffDraggedTiles(PointerReleasedEventArgs e)
    {
        // Prefer the live screen pos cached during the drag. Fall back
        // only if no PointerMoved fired (shouldn't happen for a real
        // drag - the threshold guard requires movement first).
        Avalonia.PixelPoint screenPos;
        if (_lastDragScreenPos.HasValue)
        {
            screenPos = _lastDragScreenPos.Value;
        }
        else
        {
            var top = Avalonia.Controls.TopLevel.GetTopLevel(this);
            if (top == null) { Debug.WriteLine("[Handoff] FAIL: no TopLevel and no cached screen pos"); return false; }
            try { screenPos = top.PointToScreen(e.GetPosition(top)); }
            catch { Debug.WriteLine("[Handoff] FAIL: PointToScreen threw"); return false; }
        }

        Debug.WriteLine($"[Handoff] release screenPos={screenPos} sourceDesktop='{_desktopPath}'");

        // --- Priority 1: open file-explorer items area ---
        var explorerHit = DAX.OSI.DefaultApplications.DOSIFileExplorer.FindDropTarget(screenPos);
        if (explorerHit.HasValue)
        {
            Debug.WriteLine($"[Handoff] explorer hit -> '{explorerHit.Value.CurrentPath}'");
            if (_desktopPath != null &&
                !string.Equals(
                    System.IO.Path.TrimEndingDirectorySeparator(explorerHit.Value.CurrentPath),
                    System.IO.Path.TrimEndingDirectorySeparator(_desktopPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                bool ok = MoveDraggedFilesTo(explorerHit.Value.CurrentPath, revertLocalPositions: true);
                Debug.WriteLine($"[Handoff] explorer move result={ok}");
                return ok;
            }
            Debug.WriteLine("[Handoff] explorer hit ignored (same folder as source desktop)");
        }

        // --- Priority 2: another monitor's desktop icon layer ---
        var layerHit = FindDropTarget(screenPos);
        if (layerHit.HasValue && !ReferenceEquals(layerHit.Value.Layer, this))
        {
            var target = layerHit.Value.Layer;
            Debug.WriteLine($"[Handoff] layer hit -> targetDesktop='{target.DesktopPath ?? "<null>"}' anchor={layerHit.Value.LocalPoint}");
            if (target.DesktopPath == null)
            {
                Debug.WriteLine("[Handoff] FAIL: target layer has no DesktopPath - not bound to a user yet");
                return false;
            }

            // Compute the per-tile drop position on the target layer so the
            // user's ORIGINAL grab point on the tile lands directly under
            // the release cursor - the same invariant the drag ghost has
            // been maintaining for the entire drag. The previous formula
            // used (anchor + dragDelta), where dragDelta = currentTilePos -
            // startPos. dragDelta is "how far the tile traveled during the
            // drag in source coords" - often 1500+ px on a multi-monitor
            // setup - which placed the dropped tile hundreds to thousands
            // of pixels away from the cursor. That's why "drag back to
            // main monitor doesn't land where the mouse is" AND why "tile
            // disappears after 2 round trips" (each bad drop accumulated
            // off-screen offset until the tile was outside the visible
            // layer on both monitors and looked vanished).
            //
            // Correct formula: target.TopLeft = anchor - grabOffset,
            // where grabOffset = (_dragOriginScreen - tile.startPos) is
            // the cursor offset relative to the tile's top-left at press.
            // Offsets are coordinate-system-neutral, so they translate
            // 1:1 from source-layer DIPs to target-layer DIPs.
            //
            // Also clamp into the target layer's visible bounds (with a
            // tile-size margin so the tile is never entirely off-screen)
            // so any future arithmetic mistake can't silently lose icons.
            var anchorPt = layerHit.Value.LocalPoint;
            double maxX = Math.Max(0, target.Bounds.Width  - TileWidth);
            double maxY = Math.Max(TaskbarHeight + 4, target.Bounds.Height - TileHeight);
            foreach (var (tile, startPos) in _dragStartPositions)
            {
                if (tile.Tag is not TileMeta m) continue;
                var grabOffsetX = _dragOriginScreen.X - startPos.X;
                var grabOffsetY = _dragOriginScreen.Y - startPos.Y;
                var targetX = anchorPt.X - grabOffsetX;
                var targetY = anchorPt.Y - grabOffsetY;
                targetX = Math.Clamp(targetX, 0, maxX);
                targetY = Math.Clamp(targetY, TaskbarHeight + 4, maxY);
                DesktopIconLayout.Save(m.Name, targetX, targetY);
            }
            // revertLocalPositions: false - we DON'T want the source tiles
            // to snap back to their pre-drag spot for ~250 ms waiting on
            // the watcher: that produces the "flashes back to main monitor"
            // flare the user sees. Instead we proactively animate them out
            // below, AND proactively reconcile the target so the destination
            // tile appears in the same frame. Watchers still fire later but
            // both find a no-op steady state.
            var sourceTiles = _dragStartPositions.Keys.ToList();
            bool moved = MoveDraggedFilesTo(target.DesktopPath, revertLocalPositions: false, forgetLayoutOnMove: false);
            Debug.WriteLine($"[Handoff] cross-monitor move result={moved}");
            if (moved)
            {
                // Drop the moved tiles from THIS layer's cache + animate
                // them out so the source monitor reads as "the icon left"
                // instantly. The on-disk file is already gone, so the
                // source watcher's eventual Reconcile sees them as
                // missing-from-cache AND missing-from-disk (no-op).
                foreach (var tile in sourceTiles)
                {
                    if (tile.Tag is not TileMeta m) continue;
                    _tilesByPath.Remove(m.Path);
                    _selected.Remove(tile);
                    // Restore opacity (the drag-ghost teardown sets it to
                    // 1, but defensive) before the fade-out so the user
                    // sees a clean scale-out animation.
                    tile.Opacity = 1;
                    AnimateTileOut(tile, () => Children.Remove(tile));
                }
                // Drive the destination's reconcile so the dropped tile
                // materializes at its saved position. CRITICAL: defer this
                // via Dispatcher.UIThread.Post(..., Background) - mirrors
                // the same constraint DOSIWindow.TryHandoffToMonitorAtCursor
                // documents. Mutating a DIFFERENT TopLevel's visual tree
                // (the target monitor's canvas) while we're still inside
                // the source's PointerReleased handler can race with the
                // post-release pointer-exit / cursor-update events the
                // platform fires on the way out and leave the dispatcher
                // in a state where neither monitor's tile re-renders -
                // exactly the "after a few cross-monitor drags the tile
                // vanishes from both screens" symptom. Background priority
                // runs after pending input + render frames, letting the
                // source's event chain fully unwind first.
                var pendingTarget = target;
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => pendingTarget.ForceReconcileFromExternal(),
                    Avalonia.Threading.DispatcherPriority.Background);
            }
            return moved;
        }

        Debug.WriteLine($"[Handoff] FAIL: no target. layerHit={(layerHit.HasValue ? "self-or-null" : "null")} explorerHit={(explorerHit.HasValue ? "same-folder" : "null")}");
        return false;
    }

    /// <summary>
    /// Moves every currently-dragged tile's underlying file/folder into
    /// <paramref name="destinationFolder"/>. On collision, appends " (2)",
    /// " (3)", ... before the extension - same rule the file explorer uses.
    /// If <paramref name="revertLocalPositions"/> is true, the tiles are
    /// snapped back to their pre-drag positions until the file-system-watcher
    /// removes them, so the source desktop doesn't briefly show them
    /// hovering in their dropped-but-not-here location.
    /// <para>
    /// <paramref name="forgetLayoutOnMove"/> controls whether successfully
    /// moved files have their <see cref="DesktopIconLayout"/> entry wiped.
    /// Same-monitor moves into a file explorer SHOULD forget (the file is
    /// leaving the desktop). Cross-monitor desktop-to-desktop moves should
    /// NOT forget, because the caller has already <c>Save</c>d the new
    /// position under the same name key - calling <c>Forget</c> here would
    /// silently wipe it and the target monitor's <see cref="Reconcile"/>
    /// would fall through to <c>AutoPlace</c> (top-left of the grid). That
    /// was the actual "tile snaps back to the original monitor" symptom:
    /// the file DID transfer, but with no saved position it auto-placed
    /// somewhere wrong, and combined with the source-side Reconcile
    /// arriving first, the user saw it appear to bounce home.
    /// </para>
    /// </summary>
    private bool MoveDraggedFilesTo(string destinationFolder, bool revertLocalPositions, bool forgetLayoutOnMove = true)
    {
        if (string.IsNullOrEmpty(destinationFolder)) return false;
        try { Directory.CreateDirectory(destinationFolder); } catch { return false; }

        int moved = 0;
        foreach (var (tile, startPos) in _dragStartPositions)
        {
            if (tile.Tag is not TileMeta m) continue;
            bool perTileMoved = false;
            try
            {
                var dst = ChooseUniqueDestination(Path.Combine(destinationFolder, m.Name));
                if (m.IsDirectory) Directory.Move(m.Path, dst);
                else               File.Move(m.Path, dst);
                if (forgetLayoutOnMove)
                {
                    // Forget the source layout entry so a future tile with the
                    // same name doesn't inherit the old position on this monitor.
                    DesktopIconLayout.Forget(m.Name);
                }
                moved++;
                perTileMoved = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DesktopIconLayer] Handoff move failed: {ex.Message}");
            }

            // CRITICAL: only revert the on-canvas position when the move
            // actually succeeded. The old code reverted unconditionally,
            // which corrupted the local-persist fallback - if the entire
            // batch failed (moved == 0) we'd return false, the caller
            // would run the \"local drag persist\" branch, and that branch
            // reads Canvas.GetLeft/GetTop. With everything pre-reverted
            // to startPos those reads write the ORIGINAL position back
            // to DesktopIconLayout - which is exactly the \"tile snaps
            // back to its original spot\" symptom the user reported.
            if (revertLocalPositions && perTileMoved)
            {
                // Snap back so the source view doesn't briefly show the
                // tile mid-air at the drop position before the watcher
                // removes it. The watcher fires within ~250 ms.
                Canvas.SetLeft(tile, startPos.X);
                Canvas.SetTop(tile, startPos.Y);
            }
        }
        return moved > 0;
    }

    /// <summary>
    /// Same collision rule as <c>DOSIFileExplorer.ChooseUniqueDestination</c>:
    /// appends " (2)", " (3)", ... before the extension when the desired
    /// path is taken. Bounded to 1000 attempts.
    /// </summary>
    private static string ChooseUniqueDestination(string desired)
    {
        if (!File.Exists(desired) && !Directory.Exists(desired)) return desired;
        var dir = Path.GetDirectoryName(desired) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(desired);
        var ext  = Path.GetExtension(desired);
        for (int i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        return desired;
    }

    // =====================================================================
    // Activation, delete, key handling
    // =====================================================================

    /// <summary>
    /// If <paramref name="path"/> matches the one-shot
    /// <see cref="_pendingSelectionPath"/>, promote the freshly-built
    /// <paramref name="tile"/> to the current selection and clear the
    /// pending marker. Used so a rename / paste / drop puts focus on the
    /// renamed-or-created tile once the FSW reconcile delivers it.
    /// </summary>
    private void TryConsumePendingSelection(Border tile, string path)
    {
        if (string.IsNullOrEmpty(_pendingSelectionPath)) return;
        if (!string.Equals(Path.GetFullPath(path), _pendingSelectionPath,
                           StringComparison.OrdinalIgnoreCase)) return;
        _pendingSelectionPath = null;
        ClearSelection();
        Select(tile);
    }

    private void OnLayerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _selected.Count > 0)
        {
            DeleteTiles(_selected.ToList());
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _selected.Count == 1)
        {
            ActivateTile(_selected.First());
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && _selected.Count == 1)
        {
            BeginInlineRename(_selected.First());
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            // Cheap "refresh from disk": run the same diff-reconcile the
            // FileSystemWatcher uses so a manual refresh feels identical
            // to (and benefits from the same animations as) an automatic
            // one. F5 is a convention every user expects on a desktop.
            Reconcile();
            e.Handled = true;
        }
        else if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Ctrl+A: select every tile. Uses the existing additive
            // Select() path so the marquee / single-tile selection
            // visuals are consistent.
            foreach (var tile in _tilesByPath.Values)
            {
                if (!_selected.Contains(tile)) Select(tile, additive: true);
            }
            e.Handled = true;
        }
    }

    private void ActivateTile(Border tile)
    {
        if (tile.Tag is not TileMeta m) return;

        if (m.IsDirectory)
        {
            var explorer = new DOSIFileExplorer();
            explorer.RequestNavigate(m.Path);
            WindowManager.Instance?.OpenWindow(explorer);
            return;
        }

        var ext = Path.GetExtension(m.Path);

        // Try plug-in route first (matches DOSIFileExplorer.ActivateTile).
        var plugin = LoadedAppRegistry.FindForFile(ext);
        if (plugin != null)
        {
            if (plugin.Activate() is DOSIWindow appWindow)
            {
                plugin.OpenPath(appWindow, m.Path);
                WindowManager.Instance?.OpenWindow(appWindow);
            }
            return;
        }

        // Image fallback.
        if (DOSIImageViewer.SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            var viewer = new DOSIImageViewer(m.Path);
            WindowManager.Instance?.OpenWindow(viewer);
            return;
        }

        // No handler - quietly toast.
        try { DOSIPopNotification.Show($"No app installed for {ext}"); } catch { }
    }

    private void DeleteTiles(IList<Border> tiles)
    {
        // Snapshot first so the iterator survives the visual removals.
        var snapshot = tiles.ToList();

        // CRITICAL: do the disk delete + DesktopIconLayout.Forget
        // SYNCHRONOUSLY before kicking off the 180 ms scale-out
        // animation. The old flow did both inside the animation's
        // completion callback, which meant for 180 ms after the user
        // clicked Delete:
        //   * the file was still on disk, so an immediate "New folder"
        //     gesture saw the old name and bumped to "New folder (2)";
        //   * the .desktop-layout.json entry was still live, so a
        //     same-name re-create inherited the deleted icon's grid
        //     position instead of auto-placing.
        // Now the JSON + disk reflect reality the instant the action
        // fires; the animation is purely cosmetic and decoupled.
        foreach (var tile in snapshot)
        {
            if (tile.Tag is not TileMeta m) continue;

            // Drop from caches BEFORE the animation so a fast follow-up
            // Reconcile (from the watcher event) doesn't try to animate
            // it out a second time.
            _tilesByPath.Remove(m.Path);
            _selected.Remove(tile);

            // Disk side, run NOW.
            try
            {
                var user = UserManager.CurrentUser;
                if (user != null)
                {
                    // Soft-delete via the trash so the user can recover
                    // accidents. The trash is per-user under
                    // <UserHome>/.trash/ and is the canonical delete
                    // path for the entire shell now - DOSIFileExplorer
                    // uses the same call.
                    var trashed = FileTrash.Send(user, m.Path);
                    if (trashed == null)
                    {
                        // Trash failed (e.g. cross-device move) - fall
                        // back to a hard delete so the user's action
                        // still has SOME effect, and surface the fact.
                        if (m.IsDirectory) Directory.Delete(m.Path, recursive: true);
                        else               File.Delete(m.Path);
                        try { DOSIPopNotification.Show($"Permanently deleted \u201C{m.Name}\u201D (trash unavailable)"); }
                        catch { }
                    }
                }
                else
                {
                    // No signed-in user shouldn't normally happen here
                    // (the desktop only renders for a signed-in user)
                    // but be defensive.
                    if (m.IsDirectory) Directory.Delete(m.Path, recursive: true);
                    else               File.Delete(m.Path);
                }
                // Wipe the layout JSON entry the same instant the file
                // leaves disk so a same-name re-create can't inherit
                // the deleted icon's pinned position.
                DesktopIconLayout.Forget(m.Name);
            }
            catch (Exception ex)
            {
                try { DOSIPopNotification.Show($"Could not delete \u201C{m.Name}\u201D: {ex.Message}"); }
                catch { }
                // Delete failed - put the tile back in the cache so
                // the next watcher reconcile doesn't think it's gone
                // and the user can retry.
                _tilesByPath[m.Path] = tile;
                continue;
            }

            // Visual fade-out only; disk + JSON are already consistent.
            AnimateTileOut(tile, () => Children.Remove(tile));
        }
    }

    // =====================================================================
    // Inline rename - swap the label TextBlock for an editable TextBox
    // =====================================================================

    /// <summary>
    /// Replaces the tile's label with an editable text box pre-filled with
    /// the current name. Commits on Enter / focus-loss, cancels on Escape.
    /// On commit, performs the disk rename, persists the new layout key,
    /// and lets the FileSystemWatcher reconcile the visual swap (so the
    /// new name + path is wired into a fresh tile through the same code
    /// path as a freshly-created file).
    /// </summary>
    private void BeginInlineRename(Border tile)
    {
        if (tile.Tag is not TileMeta m) return;
        if (tile.Child is not StackPanel stack) return;
        var labelIndex = stack.Children.IndexOf(m.Label);
        if (labelIndex < 0) return;

        var editor = new TextBox
        {
            Text = m.Name,
            FontSize = 11,
            Padding = new Thickness(4, 2),
            MinWidth = TileWidth - 8,
            MaxWidth = TileWidth - 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            AcceptsReturn = false,
            Margin = new Thickness(0, 6, 0, 0),
        };

        bool finished = false;
        void Restore() { stack.Children[labelIndex] = m.Label; }

        void Commit()
        {
            if (finished) return;
            finished = true;

            var newName = (editor.Text ?? string.Empty).Trim();
            // Empty / unchanged / invalid -> just restore the label.
            if (string.IsNullOrEmpty(newName) || newName == m.Name)
            {
                Restore();
                return;
            }
            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                try { DOSIPopNotification.Show("That name contains characters that aren't allowed."); }
                catch { }
                Restore();
                return;
            }

            var parent = Path.GetDirectoryName(m.Path);
            if (string.IsNullOrEmpty(parent)) { Restore(); return; }
            var newPath = Path.Combine(parent, newName);
            if (File.Exists(newPath) || Directory.Exists(newPath))
            {
                try { DOSIPopNotification.Show("An item with that name already exists."); }
                catch { }
                Restore();
                return;
            }

            // Carry the saved position over to the new name BEFORE the
            // rename so the watcher's reconcile sees the new file with a
            // saved layout entry and doesn't auto-grid it elsewhere.
            var saved = DesktopIconLayout.Get(m.Name);
            if (saved != null)
            {
                DesktopIconLayout.Save(newName, saved.X, saved.Y);
                DesktopIconLayout.Forget(m.Name);
            }

            try
            {
                if (m.IsDirectory) Directory.Move(m.Path, newPath);
                else               File.Move(m.Path, newPath);
                // Tell the next FSW reconcile to re-select the renamed
                // tile so the user doesn't have to click to recover
                // focus on the file they just renamed.
                _pendingSelectionPath = Path.GetFullPath(newPath);
            }
            catch (Exception ex)
            {
                try { DOSIPopNotification.Show($"Could not rename: {ex.Message}"); }
                catch { }
                // Roll the layout entry back so the original tile keeps
                // its position when the watcher next reconciles.
                if (saved != null)
                {
                    DesktopIconLayout.Save(m.Name, saved.X, saved.Y);
                    DesktopIconLayout.Forget(newName);
                }
                Restore();
            }
            // On success the watcher will fire Reconcile which removes
            // the old tile (animated out) and adds the new one (animated
            // in) at the inherited saved position - no manual swap needed.
        }

        void Cancel()
        {
            if (finished) return;
            finished = true;
            Restore();
        }

        editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Commit(); }
            else if (e.Key == Key.Escape) { e.Handled = true; Cancel(); }
        };
        editor.LostFocus += (_, _) => Commit();

        stack.Children[labelIndex] = editor;
        // Pre-select the stem (everything before the extension) so a
        // straight type-over doesn't blow away the file extension.
        Dispatcher.UIThread.Post(() =>
        {
            editor.Focus();
            var stem = Path.GetFileNameWithoutExtension(m.Name);
            editor.SelectionStart = 0;
            editor.SelectionEnd = string.IsNullOrEmpty(stem) ? (editor.Text ?? string.Empty).Length : stem.Length;
        }, DispatcherPriority.Loaded);
    }

    // =====================================================================
    // Animations - tile insert / remove / accent-glyph crossfade
    // =====================================================================

    /// <summary>
    /// Per-tick frame interval shared by every animation in this layer.
    /// 60 fps target.
    /// </summary>
    private static readonly TimeSpan AnimFrame = TimeSpan.FromMilliseconds(16);

    /// <summary>Pop-in: scale 0.6 -> 1.0, opacity 0 -> 1, ease-out cubic, 220 ms.</summary>
    private static void AnimateTileIn(Border tile)
    {
        const double duration = 220;
        var scale = new ScaleTransform(0.6, 0.6);
        tile.RenderTransform = scale;
        tile.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        tile.Opacity = 0;

        var start = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = AnimFrame };
        timer.Tick += (_, _) =>
        {
            var t = Math.Clamp((DateTime.UtcNow - start).TotalMilliseconds / duration, 0, 1);
            var e = 1 - Math.Pow(1 - t, 3);
            scale.ScaleX = scale.ScaleY = 0.6 + 0.4 * e;
            tile.Opacity = e;
            if (t >= 1)
            {
                timer.Stop();
                tile.RenderTransform = null;
            }
        };
        timer.Start();
    }

    /// <summary>Pop-out: scale 1.0 -> 0.7, opacity 1 -> 0, ease-in quad, 180 ms.</summary>
    private static void AnimateTileOut(Border tile, Action onComplete)
    {
        const double duration = 180;
        var scale = new ScaleTransform(1, 1);
        tile.RenderTransform = scale;
        tile.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        var startOpacity = tile.Opacity;

        var start = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = AnimFrame };
        timer.Tick += (_, _) =>
        {
            var t = Math.Clamp((DateTime.UtcNow - start).TotalMilliseconds / duration, 0, 1);
            var e = t * t;
            scale.ScaleX = scale.ScaleY = 1 - 0.3 * e;
            tile.Opacity = startOpacity * (1 - e);
            if (t >= 1)
            {
                timer.Stop();
                onComplete();
            }
        };
        timer.Start();
    }

    /// <summary>
    /// Plays the per-tile pop-out animation on every currently-rendered
    /// tile simultaneously, then hides the entire layer. Used by the
    /// sign-out and shutdown flows so the desktop icons gracefully
    /// retract instead of vanishing in a single frame when the chrome
    /// hides itself.
    /// <para>
    /// Returns a task that completes once every tile has finished its
    /// individual scale + fade tween (or immediately if there are no
    /// tiles to animate). Safe to call multiple times - subsequent calls
    /// on an already-hidden layer are no-ops.
    /// </para>
    /// <para>
    /// Tiles are identified by their <c>TileMeta</c> tag so transient
    /// non-tile children (drag ghosts, layout helpers) are ignored.
    /// </para>
    /// </summary>
    public Task AnimateAllTilesOutAsync()
    {
        // Already collapsed -> nothing to do.
        if (!IsVisible || Children.Count == 0)
        {
            IsVisible = false;
            return Task.CompletedTask;
        }

        // Snapshot the current tile list - AnimateTileOut's completion
        // callback mutates Children, so iterating live would skip
        // entries.
        var tiles = Children
            .OfType<Border>()
            .Where(b => b.Tag is TileMeta)
            .ToList();

        if (tiles.Count == 0)
        {
            IsVisible = false;
            return Task.CompletedTask;
        }

        // Block any new pointer interaction for the duration of the
        // farewell animation - tiles shouldn't be draggable, clickable
        // or context-menu-able while they're retracting.
        IsHitTestVisible = false;

        var remaining = tiles.Count;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var tile in tiles)
        {
            AnimateTileOut(tile, () =>
            {
                // Don't remove the tile from Children here - the layer
                // is about to be hidden wholesale and the caller (sign-
                // out flow) will tear the screen down moments later.
                // Removing mid-iteration would also race with any
                // pending layout pass that captured the children list.
                if (System.Threading.Interlocked.Decrement(ref remaining) == 0)
                {
                    IsVisible = false;
                    tcs.TrySetResult(true);
                }
            });
        }
        return tcs.Task;
    }

    /// <summary>
    /// Swap a tile's glyph for a freshly-built one (current accent palette)
    /// with a 200 ms opacity crossfade. The TileMeta carries a reference
    /// to the tile's IconHost so we don't need to walk the visual tree.
    /// </summary>
    private void AnimateGlyphSwap(Border tile)
    {
        if (tile.Tag is not TileMeta m) return;
        var newGlyph = m.IsDirectory ? BuildFolderGlyph() : BuildFileGlyph(m.Name);

        // Cheap path: if the layer isn't on screen yet (e.g. accent
        // changed before AttachedToVisualTree fired) skip the animation.
        if (m.IconHost.Bounds.Width <= 0)
        {
            m.IconHost.Child = newGlyph;
            return;
        }

        const double duration = 200;
        var host = m.IconHost;
        var start = DateTime.UtcNow;

        // Fade host out, swap, fade back in - simpler and more reliable
        // than overlaying both glyphs since the existing glyph captures
        // accent colours via plain Brush instances.
        var timer = new DispatcherTimer { Interval = AnimFrame };
        bool swapped = false;
        timer.Tick += (_, _) =>
        {
            var t = Math.Clamp((DateTime.UtcNow - start).TotalMilliseconds / duration, 0, 1);
            if (!swapped && t >= 0.5)
            {
                host.Child = newGlyph;
                swapped = true;
            }
            // Triangular fade: 1 -> 0 -> 1 across the duration.
            host.Opacity = t < 0.5 ? 1 - t * 2 : (t - 0.5) * 2;
            if (t >= 1)
            {
                timer.Stop();
                host.Opacity = 1;
            }
        };
        timer.Start();
    }

    // =====================================================================
    // Glyph helpers - simple, neutral, accent-tinted
    // =====================================================================

    private Control BuildFolderGlyph()
    {
        var a = Accents.AccentPrimary;
        var b = Accents.AccentSecondary;
        var body = new Border
        {
            Width = 46,
            Height = 36,
            CornerRadius = new CornerRadius(4, 4, 4, 4),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(255, a.R, a.G, a.B), 0),
                    new GradientStop(Color.FromArgb(255, b.R, b.G, b.B), 1)
                }
            },
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
            BorderThickness = new Thickness(1)
        };
        var tab = new Border
        {
            Width = 18,
            Height = 6,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(255, b.R, b.G, b.B)),
            CornerRadius = new CornerRadius(3, 3, 0, 0),
            Margin = new Thickness(4, -4, 0, 0)
        };
        var grid = new Grid
        {
            Width = 50,
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { tab, body }
        };
        return grid;
    }

    private Control BuildFileGlyph(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();
        if (ext.Length > 4) ext = ext.Substring(0, 4);

        var page = new Border
        {
            Width = 38,
            Height = 46,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromArgb(235, 248, 248, 252)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
            BorderThickness = new Thickness(1)
        };

        var corner = new Polygon
        {
            Points = new Avalonia.Collections.AvaloniaList<Point>
            {
                new(28, 0), new(38, 10), new(28, 10)
            },
            Fill = new SolidColorBrush(Color.FromArgb(255, 200, 200, 210)),
            Stroke = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top
        };

        var label = new TextBlock
        {
            Text = string.IsNullOrEmpty(ext) ? "FILE" : ext,
            FontSize = 8,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Accents.AccentPrimary),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 6)
        };

        return new Grid
        {
            Width = 50,
            Height = 46,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { page, corner, label }
        };
    }
}
