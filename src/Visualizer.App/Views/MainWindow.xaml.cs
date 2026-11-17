using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Visualizer.App.Commands;
using Visualizer.App.Input;
using Visualizer.App.Localization;
using Visualizer.App.Themes;
using Visualizer.App.ViewModels;
using Visualizer.Core.Localization;
using Visualizer.Core.Settings;
using Visualizer.Application.Cache;
using Visualizer.Application.Input;
using Visualizer.Application.Maps;
using Visualizer.Application.Playback;
using Visualizer.Infrastructure.Cache;
using Visualizer.Infrastructure.Input;
using Visualizer.Infrastructure.Maps;
using Visualizer.Infrastructure.Parsing;
using Visualizer.Application.Library;
using Visualizer.Infrastructure.Storage;

namespace Visualizer.App.Views;

public partial class MainWindow : Window, ICmdHost
{
    private readonly MainViewModel _model = new(PlaybackClock);
    private readonly WindowsRawInputMonitor _input = new();
    private IReadOnlyDictionary<int, InputAction> _bindings = new Dictionary<int, InputAction>();
    private IReadOnlyDictionary<int, int> _laneKeys = new Dictionary<int, int>();
    private HwndSource? _windowSource;
    private WorkspaceMapCache? _cache;
    private AppStorage? _storage;
    private WorkspaceSongPreferences? _songs;
    private MapLibrary? _maps;
    private SongIdBook? _ids;
    private SongMarkBook? _marks;
    private readonly Rendering.FrameStats _frames = new();
    /// <summary>The frame thread's multimedia scheduling, taken once the window is up and given back when it closes.</summary>
    private MultimediaScheduling? _scheduling;
    private NoteEditStore? _edits;
    /// <summary>The edit window while it is open; there is never more than one.</summary>
    private NoteEditWindow? _editor;
    /// <summary>Which song the open editor holds, so a second song is turned away instead of silently taking the window.</summary>
    private string? _editorSong;
    /// <summary>The timeline the open editor works on, which the console's note reads with the lanes as they stand.</summary>
    private Visualizer.Core.Maps.ParsedTimeline? _editorTimeline;
    private bool _dialogOpen;
    /// <summary>
    /// Whether another part of this app is being used. The settings panel and the edit window are not modal (the console
    /// must stay usable), so this window keeps out of the way itself.
    /// </summary>
    private bool Busy => _dialogOpen || _editor is not null;
    private bool _renderHooked;
    private bool _songBoxHooked;
    private string _appliedTheme = "";
    /// <summary>The settings dialog while it is open, so the console can apply its unapplied changes.</summary>
    private SettingsPanel? _settingsPanel;
    private CmdWindow? _console;
    private CmdSession? _consoleSession;

    public MainWindow()
    {
        InitializeComponent();
        LoadStoredSettings();
        SpeakConsole();
        TellLinkerEase(_model.Settings.JudgmentEaseScale);
        TellLinkerKeys(_model.Settings);
        _model.ForgetSettingsHistory();
        _model.SettingsCommitted += SettingsCommitted;
        ApplyTheme();
        // Cheap string compare; runs for every property change, including per-frame status text.
        _model.PropertyChanged += (_, _) =>
        {
            if (_model.Settings.Theme != _appliedTheme) ApplyTheme();
            // With the auto tile count the keys the game counts follow the lanes the loaded song uses.
            if (_keysTold && !ReferenceEquals(_model.Playback.Notes, _keysNotes)) TellLinkerKeys(_model.Settings);
        };
        DataContext = _model;
        // Asked only from a visible window: an unshown window (tests, captures) never blocks on a dialog.
        _model.ConfirmWiderLanes = message => IsVisible && ConfirmDialog.Ask(this, _model.Settings,
            _model.Text["WiderLanesTitle"], message, _model.Text["AllowChange"], _model.Text["Cancel"]);
        _model.NotifySyncFirst = message =>
        {
            if (IsVisible) ConfirmDialog.Ask(this, _model.Settings, _model.Text["SyncFirstTitle"], message, _model.Text["Confirm"], null);
        };
        try { _model.ExampleRoot = Path.Combine(WorkspaceMapCache.FindRepositoryRoot(AppContext.BaseDirectory), ".example"); }
        catch (Exception error) when (IsExpectedError(error)) { _model.ExampleRoot = null; }
        Field.Session = _model.Playback;
        Field.Judge = _model.Presses;
        SongPicker.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(ShowSongTitleStart, DispatcherPriority.Background);
        SongPicker.LostKeyboardFocus += (_, _) => ShowSongTitleStart();
        // A click would still reach the buttons while one of those windows is open; it went nowhere before, so it does now.
        PreviewMouseDown += (_, e) =>
        {
            // Not for a press on the song-end offer itself: taking the offer away on the way down would pull the button
            // out from under the press, and its click would never arrive. Those buttons say they were used themselves.
            if (!(e.OriginalSource is Visual pressed && SongEndCard.IsAncestorOf(pressed))) _model.NoteInput();
            if (Busy && !(e.OriginalSource is Visual source
                && (SettingsHost.IsAncestorOf(source) || PreviewBar.IsAncestorOf(source)))) e.Handled = true;
        };
        // Fixed shortcuts, not settings: Ctrl+T opens the console and Ctrl+E the note editor while this app has the focus.
        PreviewKeyDown += (_, e) =>
        {
            _model.NoteInput();
            if (CmdWindow.IsShortcut(e))
            {
                e.Handled = true;
                ConsoleKeyInWindow();
            }
            // Not while the settings or the editor are up: a tunneling handler on the window runs before the settings
            // panel's own, and the settings have to close themselves first (they may hold unapplied changes).
            else if (!Busy && IsEditShortcut(e))
            {
                e.Handled = true;
                if (EditLoadedSong() is { } reason) _model.SetMessage(reason);
            }
            else if (_previewingSettings && e.Key == Key.Escape)
            {
                e.Handled = true;
                EndSettingsPreview();
            }
            // The song box's own keys are taken here, before the box: an editable box handles them on the way down as well,
            // ahead of any handler on it.
            else if (!Busy && e.OriginalSource is Visual source && SongPicker.IsAncestorOf(source)) SongBoxKey(e);
        };
        SizeChanged += (_, _) => { if (_settingsPanel is not null) SizeSettings(); };
        SourceInitialized += (_, _) => InitializeInput();
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            if (_renderHooked) CompositionTarget.Rendering -= RenderFrame;
            _renderHooked = false;
            _windowSource?.RemoveHook(ObserveMessage);
            _input.KeyPressed -= HandleObservedKey;
            _input.KeyReleased -= HandleReleasedKey;
            StopGameSync();
            // The game plays with its own judgment and its own key limiter again once this app is gone.
            TellLinkerEase(1);
            TellLinkerKeys(null);
            // Taken on this thread, so it goes back on this thread.
            _scheduling?.Dispose();
            _scheduling = null;
            try { _input.Dispose(); }
            catch (Win32Exception error) { _model.SetInputStatus(_model.Text.Format("InputReleaseFailed", error.Message)); }
        };
    }

    /// <summary>
    /// A note open in the console is kept while the app runs, but closing the app would lose what is not written. This is
    /// the only way out that cannot come back to it, so it asks first (user rule).
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnClosing(e);
        if (e.Cancel || CmdLog.Shared.Report is not { Changed: true } report || !IsVisible) return;
        if (!ConfirmDialog.Ask(this, _model.Settings, _model.Text["ReportUnsavedTitle"],
                _model.Text.Format("ReportUnsaved", report.Title), _model.Text["Close"], _model.Text["Cancel"], risky: true))
            e.Cancel = true;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Loaded can fire again after re-parenting; never stack frame handlers.
        if (!_renderHooked)
        {
            CompositionTarget.Rendering += RenderFrame;
            _renderHooked = true;
        }
        HookSongBox();
        PrepareSettings();
        // Tests never read or write the app store (user rule), even with this window shown.
        if (!UseAppStore) return;
        try
        {
            _cache ??= new WorkspaceMapCache(Storage);
            await _model.RefreshLibraryAsync(_cache);
            // Earlier versions synced the bundled example into the cache; remove those entries from the app cache.
            var cached = await _cache.ReadAsync();
            if (cached.Any(_model.IsExampleEntry))
                await _cache.ReplaceAsync(cached.Where(entry => !_model.IsExampleEntry(entry)).ToArray());
            // What the update brought comes first, before the last song is offered (user rule).
            ShowUpdateNotes();
            OfferLastSong();
        }
        catch (Exception error) when (IsExpectedError(error)) { _model.SetMessage(_model.Text.Format("CacheReadFailed", error.Message)); }
    }

    /// <summary>
    /// The first start of a new version says what changed, in front of everything (user request). The version is written
    /// down first, so a crash while the notes show cannot bring them back on every start.
    /// </summary>
    private void ShowUpdateNotes()
    {
        var text = _model.Text;
        var language = _model.Settings.Language;
        var seen = Storage.ReadSeenVersion();
        var versions = ReleaseNotes.ToShow(language, seen, AppInfo.Version, Storage.RanBefore);
        if (seen != AppInfo.Version) Storage.WriteSeenVersion(AppInfo.Version);
        if (versions.Count == 0 || !IsVisible) return;
        var notes = string.Join("\n\n", versions.Select(version => version + "\n" + text[ReleaseNotes.Prefix + version]));
        var dialog = new ConfirmDialog(_model.Settings, text.Format("UpdateNotesTitle", AppInfo.Version), notes, text["UpdateNotesClose"], null)
        {
            Owner = this,
            // An update starts the app from a script, which Windows may not let take the front: this one does.
            Topmost = true
        };
        dialog.ShowDialog();
    }

    /// <summary>
    /// Offers the song loaded last (user rule): the app starts on it rather than on the first song of the list, but only
    /// after asking, every time it starts. The question folds out the facts the song list shows.
    /// </summary>
    public bool OfferLastSong()
    {
        if (!IsVisible || _model.LastSongId is not { } id
            || _model.AllSongs.FirstOrDefault(song => song.Id == id) is not { } last) return false;
        var text = _model.Text;
        if (!ConfirmDialog.Ask(this, _model.Settings, text["LastSongTitle"], text.Format("LastSongQuestion", _model.DisplayName(last)),
            text["Load"], text["Cancel"], details: SongDetails.Lines(last, text), detailsLabel: text["SongDetailsButton"])) return false;
        _model.LoadChosen(last, null);
        return true;
    }

    private WorkspaceSongPreferences SongStore => _songs ??= new WorkspaceSongPreferences(Storage);
    private MapLibrary Maps => _maps ??= new MapLibrary(Storage);
    private SongIdBook IdBook => _ids ??= new SongIdBook(Storage);
    private SongMarkBook MarkBook => _marks ??= new SongMarkBook(Storage);
    private NoteEditStore Edits => _edits ??= new NoteEditStore(Maps);

    /// <summary>The saved layout of an edited copy, or null. A damaged edit file is reported and the song plays as made.</summary>
    private NoteEdit? LayoutOf(Visualizer.Application.Cache.MapCacheEntry entry)
    {
        if (!UseAppStore || Maps.KindOf(entry) != MapKind.Edited) return null;
        try { return Edits.Read(entry)?.Edit; }
        catch (Exception error) when (IsExpectedError(error))
        {
            _model.SetMessage(_model.Text.Format("SettingsReadFailed", error.Message));
            return null;
        }
    }

    /// <summary>Map file dates for the song list sort. The files are only stat'ed, never opened for writing.</summary>
    private static (DateTime? Created, DateTime? Modified) SongFileTimes(Visualizer.Application.Cache.MapCacheEntry entry)
    {
        try
        {
            var path = Path.Combine(entry.RootDirectory, entry.RelativePath);
            if (!File.Exists(path)) return (null, null);
            return (File.GetCreationTime(path), File.GetLastWriteTime(path));
        }
        catch (Exception error) when (IsExpectedError(error)) { return (null, null); }
    }

    /// <summary>
    /// Whether this process reads and writes the app store. The test harness turns it off so a window it builds never
    /// touches the settings or the song list of the real installation.
    /// </summary>
    public static bool UseAppStore { get; set; } = true;

    /// <summary>
    /// The clock the window's view model plays by. The test harness hands in one it can wind forward, so a rule written
    /// in seconds (the song-end offer waits one) can be checked without the test waiting them out.
    /// </summary>
    public static IMonotonicClock? PlaybackClock { get; set; }

    /// <summary>
    /// The pipe the sync mod connects to. The test harness gives each window its own name so it never fights a running
    /// app for the one shared name; nothing else changes it.
    /// </summary>
    public static string GameSyncPipeName { get; set; } = Visualizer.Infrastructure.Sync.NamedPipeGameSyncServer.PipeName;

    /// <summary>A store of the test's own for the checks that read one (scannow); the app never sets it.</summary>
    public static AppStorage? StoreForTests { get; set; }

    private AppStorage Storage => _storage ??= StoreForTests ?? AppStorage.Open(AppContext.BaseDirectory);

    /// <summary>
    /// Restores the settings written by an earlier run. A missing file means first run, and an unusable one is reported and
    /// left alone so the next Apply can replace it; either way the app starts on the defaults.
    /// </summary>
    private void LoadStoredSettings()
    {
        _model.SongFileTimes = SongFileTimes;
        if (!UseAppStore) return;
        _model.SongKind = Maps.KindOf;
        _model.SongSlot = Maps.SlotOf;
        _model.SongFamily = FamilyOf;
        _model.SongLayout = LayoutOf;
        _model.SongMarksChanged = SaveSongMarks;
        try
        {
            if (Storage.ReadSettings() is { } json) _model.ApplySettings(SettingsJson.Import(json, new VisualizerSettings()));
            _model.SongPreferences = SongStore.Read();
            _model.SongIdMap = IdBook.Read();
            var marks = MarkBook.Read();
            _model.RestoreMarks(marks.New, marks.Last);
            if (Storage.Warning is { } warning) _model.SetMessage(_model.Text.Format("DataFolderFallback", warning));
            else if (Storage.MovedFrom is { } from) _model.SetMessage(_model.Text.Format("DataFolderShared", Storage.DefaultRoot, from));
            else if (Storage.LeftBehind is { } behind) _model.SetMessage(_model.Text.Format("DataFolderLeftBehind", behind, Storage.DefaultRoot));
        }
        catch (SettingsJsonException error) { _model.SetMessage(_model.Text.Format("SettingsReadFailed", error.Message)); }
        catch (Exception error) when (IsExpectedError(error)) { _model.SetMessage(_model.Text.Format("SettingsReadFailed", error.Message)); }
    }

    /// <summary>
    /// Points the store at the folder in <paramref name="settings"/> and writes them there. Files already written to the old
    /// store are left untouched, so a new folder starts empty and the songs have to be synced again.
    /// </summary>
    private void SaveSettings(VisualizerSettings settings)
    {
        if (!UseAppStore) return;
        try
        {
            var previous = Storage.Root;
            Storage.UseRoot(settings.DataRootDirectory, settings.MapRootDirectory);
            // Auto save off keeps the applied settings for this run only; everything else still follows the folder.
            if (settings.AutoSaveSettings) Storage.WriteSettings(SettingsJson.Export(settings));
            _songs = null;
            _maps = null;
            _ids = null;
            _marks = null;
            _edits = null;
            _model.SongKind = Maps.KindOf;
            _model.SongSlot = Maps.SlotOf;
            _model.SongFamily = FamilyOf;
            SongStore.Write(_model.SongPreferences);
            MarkBook.Write(new SongMarks(_model.NewSongs, _model.LastSongId));
            if (string.Equals(previous, Storage.Root, StringComparison.OrdinalIgnoreCase)) return;
            _cache = null;
            // Pins and redirects belong to the store too; kept from the old one, they would be written into the new one.
            _pins = null;
            _pinsUnreadable = null;
            _redirects = null;
            _redirectsUnreadable = null;
            _model.SetMessage(_model.Text.Format("DataFolderMoved", Storage.Root));
            ReloadLibrary();
        }
        catch (Exception error) when (IsExpectedError(error)) { _model.SetMessage(_model.Text.Format("SettingsWriteFailed", error.Message)); }
    }

    private void SaveSongMarks()
    {
        if (!UseAppStore) return;
        try { MarkBook.Write(new SongMarks(_model.NewSongs, _model.LastSongId)); }
        catch (Exception error) when (IsExpectedError(error)) { _model.SetMessage(_model.Text.Format("SettingsWriteFailed", error.Message)); }
    }

    private async void ReloadLibrary()
    {
        try
        {
            _cache ??= new WorkspaceMapCache(Storage);
            await _model.RefreshLibraryAsync(_cache);
        }
        catch (Exception error) when (IsExpectedError(error)) { _model.SetMessage(_model.Text.Format("CacheReadFailed", error.Message)); }
    }

    private void ApplyTheme()
    {
        _appliedTheme = _model.Settings.Theme;
        ThemeCatalog.Apply(Resources, _appliedTheme);
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        _console?.ApplyTheme(_appliedTheme);
    }

    private void HookSongBox()
    {
        if (_songBoxHooked) return;
        SongPicker.ApplyTemplate();
        if (SongPicker.Template?.FindName("PART_EditableTextBox", SongPicker) is not TextBox box) return;
        box.TextChanged += (_, _) => Dispatcher.BeginInvoke(ShowSongTitleStart, DispatcherPriority.Background);
        _songBoxHooked = true;
    }

    /// <summary>
    /// Shows the beginning of a long song title. The editable box otherwise keeps the caret, and so the view, at the end of
    /// the picked title. Left alone while the user is typing in it.
    /// </summary>
    public void ShowSongTitleStart()
    {
        if (SongPicker.Template?.FindName("PART_EditableTextBox", SongPicker) is not TextBox box || box.IsKeyboardFocusWithin) return;
        box.Select(0, 0);
        box.ScrollToHome();
    }

    private void RenderFrame(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) return;
        // Timed to tell a slow frame of our own from one the system never gave us (console: fps). The event carries the time
        // the compositor meant the frame for, so a gap there is a frame that never came, not one we were late to handle.
        var started = _frames.NowMs;
        var composed = e is RenderingEventArgs frame ? frame.RenderingTime.TotalMilliseconds : double.NaN;
        FollowGameSync();
        _model.RefreshPlaybackInfo();
        Field.Tick();
        _frames.Add(started, composed, _frames.NowMs - started);
    }

    private void SlowerClick(object sender, RoutedEventArgs e) => _model.StepSpeed(-1);

    private void FasterClick(object sender, RoutedEventArgs e) => _model.StepSpeed(1);

    private void SpeedResetClick(object sender, RoutedEventArgs e) => _model.ApplySpeed(100);

    /// <summary>The speed box applies on Enter and gives the focus back to the window; Escape drops what was typed.</summary>
    private void SpeedBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _model.ApplyTypedSpeed();
            Keyboard.Focus(this);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _model.CancelTypedSpeed();
            Keyboard.Focus(this);
        }
    }

    private void SpeedBoxLostFocus(object sender, RoutedEventArgs e)
    {
        // The box goes away by itself once the song starts. Losing the focus that way is not "leaving the box", so a
        // half-typed number is dropped instead of being applied at the moment play begins.
        if (_model.ShowFieldControls) _model.ApplyTypedSpeed();
        else _model.CancelTypedSpeed();
    }

    /// <summary>The map sync, from the settings button or the console's sync command. The console watches the progress.</summary>
    public async Task SyncMapsAsync(IProgress<double>? progress = null)
    {
        ExampleButton.IsEnabled = false;
        try
        {
            _model.SetMessage(_model.Text["SyncRunning"]);
            await CopyAndScanAsync(rebuild: false, progress);
        }
        catch (Exception error) when (IsExpectedError(error)) { _model.SetMessage(_model.Text.Format("SyncFailed", error.Message)); }
        finally { ExampleButton.IsEnabled = true; }
    }

    /// <summary>The song-end offer: the song from its beginning. Always in the same place (user rule).</summary>
    private void RestartFromStartClick(object sender, RoutedEventArgs e) => _model.RestartFromBeginning();

    /// <summary>The song-end offer: the song from its last checkpoint, shown only when there is one.</summary>
    private void LastCheckpointClick(object sender, RoutedEventArgs e) => _model.RestartAtLastCheckpoint();

    /// <summary>Folds the song's details away and back. It is a button and nothing else: no setting, no saving (user rule).</summary>
    private void HideOverlayClick(object sender, RoutedEventArgs e) => _model.SongOverlayHidden = !_model.SongOverlayHidden;

    /// <summary>Ctrl+E: the note editor for the loaded song. Fixed, like Ctrl+T, so it is not in the key settings.</summary>
    public static bool IsEditShortcut(Key key, ModifierKeys modifiers) => key == Key.E && modifiers == ModifierKeys.Control;

    /// <summary>Whether a key event is Ctrl+E, whatever the input method made of the E.</summary>
    public static bool IsEditShortcut(KeyEventArgs e) => IsEditShortcut(KeyNames.Real(e), Keyboard.Modifiers);

    private void EditSongClick(object sender, RoutedEventArgs e)
    {
        if (EditLoadedSong() is { } reason) _model.SetMessage(reason);
    }

    /// <summary>
    /// Brings the Steam folder into the custom folder, then brings the cache up to date with all three app folders: only
    /// the levels that came in or changed are parsed. With <paramref name="rebuild"/> the custom folder is rebuilt from
    /// scratch and every level parsed again, which is how unsubscribed songs go. The map folder is only read; a map folder
    /// that cannot be read still leaves the app's own folders syncing.
    /// </summary>
    private async Task CopyAndScanAsync(bool rebuild, IProgress<double>? overall = null, CancellationToken cancellationToken = default)
    {
        if (!UseAppStore) return;
        _cache ??= new WorkspaceMapCache(Storage);
        // One number for the whole job (user request): copying is the first CopyShare of it and parsing the three folders
        // the rest, so the screen can say 0% to 100% instead of counting files.
        const double CopyShare = 0.4;
        void Step(double fraction)
        {
            var percent = (int)Math.Round(Math.Clamp(fraction, 0, 1) * 100);
            _model.SetMessage(_model.Text.Format(rebuild ? "ResyncProgress" : "SyncProgress", percent));
            overall?.Report(Math.Clamp(fraction, 0, 1));
        }
        var copy = new Progress<MapCopyProgress>(step => Step(step.Total == 0 ? CopyShare : CopyShare * step.Copied / step.Total));
        Step(0);
        try
        {
            var source = new ReadOnlyDirectoryMapSource(_model.Settings.MapRootDirectory);
            if (rebuild) await Maps.RebuildCustomAsync(source, copy, cancellationToken);
            else await Maps.SyncCustomAsync(source, copy, cancellationToken);
        }
        catch (Exception error) when (IsExpectedError(error)) { _model.SetMessage(_model.Text.Format("SyncFailed", error.Message)); }

        var before = (await _cache.ReadAsync(cancellationToken)).Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var service = new MapSyncService(_cache, _cache, new AdofaiTimelineParser());
        var reports = new List<MapSyncReport>();
        var roots = Maps.Roots.ToArray();
        for (var index = 0; index < roots.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = roots[index];
            if (!Directory.Exists(root)) Directory.CreateDirectory(root);
            // Each folder takes an equal share of what is left; inside one, the share follows the levels parsed.
            var from = CopyShare + (1 - CopyShare) * index / roots.Length;
            var span = (1 - CopyShare) / roots.Length;
            var scan = new Progress<MapScanProgress>(step => Step(from + (step.Total == 0 ? span : span * step.Parsed / step.Total)));
            // A sync parses only the songs that came in or changed; the rebuild reads every one again (user rule).
            reports.Add(await _model.SyncAsync(service, new ReadOnlyDirectoryMapSource(root), scan, reparse: rebuild));
        }
        Step(1);
        await DropForeignEntriesAsync(cancellationToken);
        AssignIds();
        // Songs this sync added are new. The first sync into an empty library marks none, since every song would be.
        if (before.Count > 0) _model.MarkNew(_model.AllSongs.Select(entry => entry.Id).Where(id => !before.Contains(id)));
        _model.ReportSync(reports);
    }

    /// <summary>
    /// Clears out cache entries that are not in the app's map folders, such as the ones earlier versions wrote straight
    /// from the Steam folder. The songs themselves are untouched; only the cache is tidied.
    /// </summary>
    private async Task DropForeignEntriesAsync(CancellationToken cancellationToken)
    {
        if (_cache is null) return;
        var entries = await _cache.ReadAsync(cancellationToken);
        var kept = entries.Where(entry => Maps.KindOf(entry) != MapKind.External).ToArray();
        if (kept.Length == entries.Count) return;
        await _cache.ReplaceAsync(kept, cancellationToken);
        _model.SetLibrary(kept);
    }

    /// <summary>Gives every song its eight-character id (first digit = folder) and remembers them for the next run.</summary>
    private void AssignIds()
    {
        if (!UseAppStore) return;
        try
        {
            var ids = SongIds.Assign(_model.AllSongs.Select(entry => (entry.Id, (int)Maps.KindOf(entry))), _model.SongIdMap);
            _model.SongIdMap = ids;
            IdBook.Write(ids);
        }
        catch (Exception error) when (IsExpectedError(error)) { _model.SetMessage(_model.Text.Format("SettingsWriteFailed", error.Message)); }
    }

    /// <summary>
    /// Rebuilds the custom folder so it matches the Steam folder, dropping songs that are no longer subscribed. Asks first,
    /// since it deletes copies.
    /// </summary>
    public async Task ResyncAsync(IProgress<double>? progress = null)
    {
        try { await CopyAndScanAsync(rebuild: true, progress); }
        catch (Exception error) when (IsExpectedError(error)) { _model.SetMessage(_model.Text.Format("SyncFailed", error.Message)); }
    }

    /// <summary>
    /// Opens the note editor for the loaded song (the settings window's button). Returns null on success, otherwise the
    /// reason in <paramref name="language"/> (the app's language when null).
    /// </summary>
    public string? EditLoadedSong(string? language = null)
    {
        if (!UseAppStore || _model.LoadedEntry is not { } entry) return UiText.Get(language ?? _model.Settings.Language, "EditNoSong");
        return OpenEditor(entry, language);
    }

    /// <summary>
    /// Opens the note editor for a song, loaded or not (the console's edit takes any id). Only one window exists: a second
    /// call brings it forward instead of opening another. Null on success, otherwise the reason.
    /// </summary>
    private string? OpenEditor(MapCacheEntry entry, string? language)
    {
        var reasons = language ?? _model.Settings.Language;
        if (_editor is { } open)
        {
            open.Activate();
            // One window only (user rule): another song cannot take it over, and the work in it is not thrown away.
            return _editorSong == entry.Id ? null : UiText.Get(reasons, "EditOtherOpen");
        }
        if (!UseAppStore) return UiText.Get(reasons, "EditNoSong");
        // The loaded song keeps its timeline in the session; any other song brings its own from the cache.
        var timeline = _model.LoadedEntry?.Id == entry.Id && _model.Playback.Timeline is { } loaded ? loaded : entry.Timeline;
        if (entry.Status != MapParseStatus.Supported || timeline is null) return UiText.Get(reasons, "EditUnsupported");
        try
        {
            var settings = _model.Settings;
            var songFolder = Edits.SongFolderOf(entry);
            var existing = Edits.Read(entry);
            // Every slot can be picked on save, a used one after a warning, so a song with nine edits still opens.
            var session = existing is { } saved
                ? NoteEditSession.FromLayout(timeline, saved.Edit, AllLanes)
                : FromRules(timeline, settings with { Language = reasons }, entry.SourceHash);
            // One lane of the main screen, so the sheet matches it; all eight lanes are shown either way.
            var laneWidth = Field.ActualWidth > 0
                ? Field.ActualWidth / Math.Max(1, settings.GetDisplayTiles(_model.Playback.Notes.Select(note => note.Tile)).Count) : 60;
            var place = FamilyOf(entry);
            var window = new NoteEditWindow(session, settings, _model.DisplayName(entry), Maps.SlotOf(entry), laneWidth,
                topMs: _editorPlaces.TryGetValue(place, out var top) ? top : null)
            {
                Owner = this,
                Slots = Edits.SlotsOf(songFolder),
                RuleNotes = RuleNotesOf(timeline, settings with { Language = reasons }, entry.SourceHash),
                ToggleConsole = () => ToggleConsole(),
                ConsoleShortcut = ConsoleKeyInWindow
            };
            _editor = window;
            _editorSong = entry.Id;
            _editorTimeline = timeline;
            // Not a dialog: a modal window disables every other window of the app, and the console has to stay usable
            // while this one is open (user rule). So what followed the dialog happens when it closes.
            window.Closed += (_, _) =>
            {
                _editorPlaces[place] = window.TopMs;
                _editor = null;
                _editorSong = null;
                _editorTimeline = null;
                try { if (window.Result is { } result) SaveEdit(entry, result); }
                catch (Exception error) when (IsExpectedError(error)) { _model.SetMessage(error.Message); }
            };
            window.Show();
            return null;
        }
        catch (Exception error) when (IsExpectedError(error)) { return error.Message; }
    }

    /// <summary>
    /// Where the editor was left for each song, so opening it again goes back there (user rule). Kept until the app exits
    /// and never written down. Keyed by the song with its edits, which share their hit times, so every version opens there.
    /// </summary>
    private readonly Dictionary<string, double> _editorPlaces = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The editor always shows tiles 1..8, whatever the key count.</summary>
    private static readonly int[] AllLanes = [1, 2, 3, 4, 5, 6, 7, 8];

    /// <summary>
    /// The notes the rules make for this song right now, which the editor draws behind the edited ones. A song the rules
    /// cannot place (often the reason it was edited) simply has no layer rather than keeping the editor shut.
    /// </summary>
    private static IReadOnlyList<EditNote> RuleNotesOf(Visualizer.Core.Maps.ParsedTimeline timeline, VisualizerSettings settings, string hash)
    {
        try { return FromRules(timeline, settings, hash).Notes; }
        catch (Visualizer.Core.Visualization.NotePlacementException) { return []; }
    }

    /// <summary>The notes the rules make right now, as the editor's starting point. Public so a test can hold it to the map's clock.</summary>
    public static NoteEditSession FromRules(Visualizer.Core.Maps.ParsedTimeline timeline, VisualizerSettings settings, string hash)
    {
        var notes = PlaybackSession.GenerateSongNotes(timeline, settings, out var hits);
        var lanes = new Dictionary<int, int>(notes.Count);
        var chords = new List<IReadOnlyList<int>>();
        for (var i = 0; i < notes.Count; i++)
        {
            lanes[hits[i]] = notes[i].Tile;
            // A chord is the notes marked StartsChord in a row and the one after the last, which is how the renderer reads them.
            if (notes[i].StartsChord && i + 1 < notes.Count && (i == 0 || !notes[i - 1].StartsChord))
            {
                var end = i + 1;
                while (end + 1 < notes.Count && notes[end].StartsChord) end++;
                chords.Add(hits.Skip(i).Take(end - i + 1).ToArray());
            }
        }
        // Built through the same path a saved layout takes, so both sit on the map's own clock. Taking the times from the
        // prepared notes instead put every one of these on the playback clock, which starts at the first hit — the ghost
        // notes then sat about one tile early (user report).
        var edit = new NoteEdit(hash, lanes, chords);
        return new NoteEditSession(NoteEditSession.BuildNotes(timeline, edit), AllLanes, hash);
    }

    /// <summary>Writes the edit into its slot, then scans the edited folder so the new song joins the list.</summary>
    private void SaveEdit(Visualizer.Application.Cache.MapCacheEntry entry, NoteEditResult result)
    {
        try
        {
            var text = new ReadOnlyDirectoryMapSource(entry.RootDirectory).ReadAsync(entry.RelativePath).GetAwaiter().GetResult().Text;
            var saved = Path.GetFullPath(Path.Combine(Maps.EditedRoot, Edits.Write(entry, text, result.Layout, _model.Settings, result.Slot)));
            // Saved back over the edit on screen: load it again so the main screen shows the new lanes.
            _ = ScanEditedAsync(Maps.SlotOf(entry) == result.Slot ? entry.Id : null, saved);
        }
        catch (Exception error) when (IsExpectedError(error)) { _model.SetMessage(_model.Text.Format("SyncFailed", error.Message)); }
    }

    /// <summary>Scans the edited folder; <paramref name="savedPath"/>, the edit just saved, is marked recently.</summary>
    private async Task ScanEditedAsync(string? reloadId = null, string? savedPath = null)
    {
        try
        {
            _cache ??= new WorkspaceMapCache(Storage);
            var service = new MapSyncService(_cache, _cache, new AdofaiTimelineParser());
            if (!Directory.Exists(Maps.EditedRoot)) Directory.CreateDirectory(Maps.EditedRoot);
            var report = await _model.SyncAsync(service, new ReadOnlyDirectoryMapSource(Maps.EditedRoot));
            AssignIds();
            if (savedPath is not null && _model.AllSongs.FirstOrDefault(song => string.Equals(
                    Path.GetFullPath(Path.Combine(song.RootDirectory, song.RelativePath)), savedPath, StringComparison.OrdinalIgnoreCase)) is { } saved)
                _model.MarkRecentEdit(saved.Id);
            _model.ReportSync([report]);
            if (reloadId is not null && _model.LoadedEntry?.Id == reloadId && _model.AllSongs.FirstOrDefault(song => song.Id == reloadId) is { } fresh)
                _model.Reload(fresh, saved: true);
        }
        catch (Exception error) when (IsExpectedError(error)) { _model.SetMessage(_model.Text.Format("SyncFailed", error.Message)); }
    }

    /// <summary>Deletes a song the app owns and takes it out of the cache. Map folders are never touched.</summary>
    public string? RemoveSong(Visualizer.Application.Cache.MapCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            Maps.Remove(entry);
            Forget(entry);
            return null;
        }
        catch (Exception error) when (IsExpectedError(error)) { return error.Message; }
    }

    /// <summary>
    /// Deletes the app's own copy for the song list window's remove button. An edited song is kept for restore, exactly as the
    /// console's remove does; anything else loses its copy, as delete does. Returns why it could not be done, or null.
    /// </summary>
    private string? RemoveListed(MapCacheEntry entry)
    {
        if (!UseAppStore) return _model.Text["SongListRemoveOff"];
        try
        {
            if (Maps.SlotOf(entry) is not null) Maps.Hold(entry, _model.DisplayName(entry));
            else Maps.Remove(entry);
            Forget(entry);
            return null;
        }
        catch (Exception error) when (IsExpectedError(error)) { return _model.Text.Format("SongListRemoveFailed", error.Message); }
    }

    /// <summary>Takes a song that left the map folders out of the cache and the list, and off the screen if it was loaded.</summary>
    private void Forget(MapCacheEntry entry)
    {
        _cache ??= new WorkspaceMapCache(Storage);
        var entries = _cache.ReadAsync().GetAwaiter().GetResult().Where(other => other.Id != entry.Id).ToArray();
        _cache.ReplaceAsync(entries).GetAwaiter().GetResult();
        if (_model.LoadedEntry?.Id == entry.Id) _model.ClearLoaded();
        _model.SetLibrary(entries);
    }

    /// <summary>Reads the bundled example read-only and plays it directly; it is not synced into the song library.</summary>
    private async void ExampleClick(object sender, RoutedEventArgs e)
    {
        if (_restoring) return;
        if (ExampleButton.IsChecked != true) { RestoreLoaded(); return; }
        RememberLoaded();
        _restoring = true;
        try { PreviewButton.IsChecked = false; }
        finally { _restoring = false; }
        ExampleButton.IsEnabled = false;
        try
        {
            var source = new ReadOnlyDirectoryMapSource(_model.ExampleRoot
                ?? Path.Combine(WorkspaceMapCache.FindRepositoryRoot(AppContext.BaseDirectory), ".example"));
            var raw = await source.ReadAsync("main.adofai");
            var parser = new AdofaiTimelineParser();
            var result = await Task.Run(() => parser.Parse(raw));
            _model.LoadExample(new MapCacheEntry("bundled-example", source.RootDirectory, "main.adofai", result.SongName ?? "Plum - R",
                result.Status, result.Status == MapParseStatus.Supported ? result.Timeline : null, result.Warnings.ToArray(), "", parser.Version, result.Info));
            if (_model.Playback.Timeline is not null && _model.Playback.State == PlaybackState.Off) _model.Toggle();
        }
        catch (Exception error) when (IsExpectedError(error)) { _model.SetMessage(_model.Text.Format("ExampleFailed", error.Message)); }
        finally { ExampleButton.IsEnabled = true; }
    }

    /// <summary>
    /// The manual is modal and owned by this window, so it cannot open twice and the main window stays out of reach while it is
    /// open. The button holds the pressed look meanwhile.
    /// </summary>
    private void HelpClick(object sender, RoutedEventArgs e)
    {
        try { new HelpWindow(_model.Settings) { Owner = this }.ShowDialog(); }
        finally { HelpButton.IsChecked = false; }
    }

    /// <summary>The song that was loaded before the rule preview or the example took over; null when nothing was.</summary>
    private MapCacheEntry? _restoreEntry;
    private bool _guideLoaded;
    private bool _restoring;

    private void RememberLoaded()
    {
        // Switching straight from the preview to the example keeps the song both of them replaced.
        if (_guideLoaded) return;
        _restoreEntry = _model.LoadedEntry;
        _guideLoaded = true;
    }

    /// <summary>Pressing the preview or the example again goes back to what was on screen before.</summary>
    private void RestoreLoaded()
    {
        _restoring = true;
        try
        {
            PreviewButton.IsChecked = false;
            ExampleButton.IsChecked = false;
            if (_restoreEntry is { } entry) _model.LoadCached(entry);
            else _model.ClearLoaded();
        }
        finally
        {
            _restoring = false;
            _guideLoaded = false;
            _restoreEntry = null;
        }
    }

    /// <summary>Loading a song by hand ends the preview or example state without putting the old song back.</summary>
    private void ForgetGuide()
    {
        _restoring = true;
        try { PreviewButton.IsChecked = false; ExampleButton.IsChecked = false; }
        finally { _restoring = false; }
        _guideLoaded = false;
        _restoreEntry = null;
    }

    private void LoadSongClick(object sender, RoutedEventArgs e) => LoadSong();

    /// <summary>Loads the chosen song and folds the song panel away, leaving the note field the room.</summary>
    private void LoadSong()
    {
        if (!_model.LoadSelectedSong()) return;
        ForgetGuide();
        MapPanel.IsExpanded = false;
    }
    private void PreviousSongClick(object sender, RoutedEventArgs e) => _model.MoveSong(-1);
    private void NextSongClick(object sender, RoutedEventArgs e) => _model.MoveSong(1);
    private void PreviewClick(object sender, RoutedEventArgs e)
    {
        if (_restoring) return;
        if (PreviewButton.IsChecked != true) { RestoreLoaded(); return; }
        RememberLoaded();
        _restoring = true;
        try { ExampleButton.IsChecked = false; }
        finally { _restoring = false; }
        _model.LoadPreview();
    }
    private void ResetClick(object sender, RoutedEventArgs e) => _model.Reset();
    private void StartClick(object sender, RoutedEventArgs e) => _model.StartFromButton();

    /// <summary>
    /// Enter and the arrows in the song box, taken before the box's own handling: its Enter took the
    /// song highlighted when its list opened, the one loaded before, whatever had been typed since. Enter loads the picked song
    /// or the first of the searched list; the arrows pick in that list.
    /// </summary>
    private void SongBoxKey(KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SongPicker.IsDropDownOpen = false;
            LoadSong();
        }
        else if (e.Key is Key.Up or Key.Down && (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
        {
            e.Handled = true;
            OpenSongList();
            if (_model.PickInSearch(e.Key == Key.Down ? 1 : -1) is { } picked
                && SongPicker.ItemContainerGenerator.ContainerFromItem(picked) is FrameworkElement row)
                row.BringIntoView();
        }
    }

    /// <summary>Typing in the song box opens the filtered list; Enter and the arrows were handled on the way down.</summary>
    private void SongPickerKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Escape or Key.Tab or Key.Enter or Key.Up or Key.Down)) OpenSongList();
    }

    /// <summary>
    /// Opens the song list and leaves the caret where it was: an editable box selects all its text when its list opens, so the
    /// next letter would replace the ones typed so far ("hul" would become "l").
    /// </summary>
    private void OpenSongList()
    {
        if (SongPicker.IsDropDownOpen) return;
        var box = SongPicker.Template?.FindName("PART_EditableTextBox", SongPicker) as TextBox;
        var (start, length) = box is null ? (0, 0) : (box.SelectionStart, box.SelectionLength);
        SongPicker.IsDropDownOpen = true;
        box?.Select(start, length);
    }
    private void PauseResumeClick(object sender, RoutedEventArgs e) => _model.PauseResume();
    private void ToggleClick(object sender, RoutedEventArgs e) => _model.Toggle();
    private void PreviousCheckpointClick(object sender, RoutedEventArgs e) => _model.MoveCheckpoint(-1);
    private void NextCheckpointClick(object sender, RoutedEventArgs e) => _model.MoveCheckpoint(1);

    private void SectionBoxKeyDown(object sender, KeyEventArgs e)
    {
        // Handled inside this text box only: Enter applies the tile, Escape restores it; global input is untouched.
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        var binding = SectionBox.GetBindingExpression(TextBox.TextProperty);
        if (e.Key == Key.Enter) binding?.UpdateSource();
        else binding?.UpdateTarget();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void SettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>
    /// A settings panel built ahead of time and never shown. Building one costs about a tenth of a second and laying it
    /// out three times that (measured, not guessed), and neither gets cheaper the second time round, so the only way the
    /// button can answer at once is to have the work already done. The spare is used as it is and a new one is made
    /// afterwards: nothing that has been on screen is ever reused, so no state can carry over.
    /// </summary>
    private SettingsPanel? _spareSettings;
    /// <summary>The settings the spare was built from; it is only good while those are still the applied ones.</summary>
    private VisualizerSettings? _spareFor;
    private bool _makingSpare;

    /// <summary>
    /// Builds the spare while the app has nothing else to do. Called once the window is up and after every settings
    /// change, since a spare built from older settings would open showing the wrong values.
    /// </summary>
    private void PrepareSettings()
    {
        if (!IsVisible || _makingSpare || _dialogOpen) return;
        if (_spareSettings is not null && ReferenceEquals(_spareFor, _model.Settings)) return;
        _makingSpare = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            _makingSpare = false;
            // Nothing is built while the settings are open: the panel on screen is the one that matters.
            if (!IsVisible || _dialogOpen) return;
            try
            {
                var settings = _model.Settings;
                var panel = new SettingsPanel(settings);
                // Laid out at the size the host will give it, so putting it in does not measure everything again.
                var wide = WindowBody.ActualWidth > 0 ? WindowBody.ActualWidth : ActualWidth;
                var high = WindowBody.ActualHeight > 0 ? WindowBody.ActualHeight : ActualHeight;
                var room = new Size(Math.Max(SettingsLeastWidth, wide), Math.Max(SettingsLeastWidth, high));
                panel.Measure(room);
                panel.Arrange(new Rect(new Point(0, 0), room));
                _spareSettings = panel;
                _spareFor = settings;
            }
            // A spare is a convenience: without one the settings open the slow way, which is what they did before.
            catch (Exception error) when (IsExpectedError(error)) { }
        });
    }

    /// <summary>Whether a spare is waiting, so the next press of the settings button answers at once.</summary>
    public bool SettingsReady => _spareSettings is not null && ReferenceEquals(_spareFor, _model.Settings);

    /// <summary>The spare while it still matches the applied settings, otherwise a panel built here and now.</summary>
    private SettingsPanel TakeSettings()
    {
        var ready = _spareSettings is { } spare && ReferenceEquals(_spareFor, _model.Settings) ? spare : null;
        _spareSettings = null;
        _spareFor = null;
        return ready ?? new SettingsPanel(_model.Settings);
    }

    /// <summary>
    /// How much of the window the settings cover (user rule), and the least they are ever given. The host decides the
    /// width, not the panel: a narrower window simply hands over all of it.
    /// </summary>
    private const double SettingsShare = 1.0;
    private const double SettingsLeastWidth = 400;

    /// <summary>
    /// The settings are part of this window, not a window of their own (user rule): they slide in over the note field
    /// from the right and go away again, so nothing else about the app is blocked while they are open.
    /// </summary>
    private void OpenSettings()
    {
        if (_dialogOpen)
        {
            _settingsPanel?.Focus();
            return;
        }
        _dialogOpen = true;
        var committed = false;
        try
        {
            // Effects and colors preview on the note field while they are edited; leaving without Apply restores them.
            var panel = TakeSettings();
            panel.Preview = _model.PreviewVisuals;
            panel.DataRootInUse = UseAppStore ? Storage.Root : "";
            panel.SongPreferences = _model.SongPreferences;
            panel.ResyncMaps = () => { _ = ResyncAsync(); return true; };
            panel.SyncMaps = () => _ = SyncMapsAsync();
            panel.EditSongRequested = () => { if (EditLoadedSong() is { } reason) _model.SetMessage(reason); };
            panel.SongListFields = string.Join(' ', _model.Settings.GetSongListFields());
            panel.OpenSongList = ShowSongList;
            panel.OpenConsole = ConsoleKeyInWindow;
            panel.SettingsApplied = ApplyFromDialog;
            panel.Previewing = ShowSettingsPreview;
            _settingsPanel = panel;
            panel.Leaving = () =>
            {
                // Whatever asked it to go (Apply, the button, Escape, the console, the editor) ends here.
                if (!ReferenceEquals(_settingsPanel, panel)) return;
                // The song the preview borrowed comes back before anything is applied to it.
                if (_previewingSettings) EndSettingsPreview(reopen: false);
                try
                {
                    if (panel.AppliedSettings is { } settings)
                    {
                        _model.SongPreferences = panel.SongPreferences;
                        _model.ApplySettings(settings);
                        committed = true;
                    }
                }
                catch (Exception error) when (IsExpectedError(error)) { _model.SetMessage(error.Message); }
                finally
                {
                    if (!committed) _model.EndVisualPreview();
                    _settingsPanel = null;
                    _dialogOpen = false;
                    ShowSettings(false);
                    PrepareSettings();
                }
            };
            SettingsHost.Child = panel;
            SizeSettings();
            ShowSettings(true);
            panel.Focus();
        }
        catch (Exception error) when (IsExpectedError(error))
        {
            _model.SetMessage(error.Message);
            if (!committed) _model.EndVisualPreview();
            _settingsPanel = null;
            _dialogOpen = false;
            SettingsHost.Child = null;
        }
    }

    private bool _previewingSettings;

    /// <summary>
    /// The settings step aside and the rules they hold play on the preview timeline (user rule). They are not applied:
    /// the panel keeps every change, and going back leaves it exactly as it was. Nothing else on the window answers the
    /// mouse meanwhile, so no song can be loaded or changed under it.
    /// </summary>
    private void ShowSettingsPreview(VisualizerSettings pending)
    {
        if (_settingsPanel is null || _previewingSettings) return;
        RememberLoaded();
        _previewingSettings = true;
        _model.LoadPreview(pending);
        // Blocked input means no Start button to press, so the notes come down by themselves.
        if (_model.Playback.Notes.Count > 0) _model.Start();
        PreviewBarText.Text = _model.Text["PreviewOpen"];
        PreviewBackButton.Content = _model.Text["PreviewBack"];
        PreviewBar.Visibility = Visibility.Visible;
        ShowSettings(false);
    }

    private void PreviewBackClick(object sender, RoutedEventArgs e) => EndSettingsPreview();

    /// <summary>Back from the preview: the song that was loaded returns and the settings slide in untouched.</summary>
    private void EndSettingsPreview(bool reopen = true)
    {
        if (!_previewingSettings) return;
        _previewingSettings = false;
        PreviewBar.Visibility = Visibility.Collapsed;
        RestoreLoaded();
        if (!reopen || _settingsPanel is null) return;
        ShowSettings(true);
        // The colours and effects being edited show on the note field again, as they did before the preview.
        try { _model.PreviewVisuals(_settingsPanel.BuildSettings()); }
        catch (ArgumentException) { }
        _settingsPanel.Focus();
    }

    /// <summary>
    /// All of the window, but never so little that the rows have to be squeezed. The measure is the body inside the
    /// window, not the window: Window.ActualWidth counts the frame too, and the host is aligned right, so a width taken
    /// from the window hung past the left edge by however wide the frame is (user report).
    /// </summary>
    private void SizeSettings()
    {
        var room = WindowBody.ActualWidth > 0 ? WindowBody.ActualWidth : ActualWidth;
        SettingsHost.Width = Math.Max(Math.Min(room, SettingsLeastWidth), room * SettingsShare);
    }

    /// <summary>
    /// Puts the settings over the window or takes them away. No movement (user rule): they are there the moment they are
    /// asked for and gone the moment they are done with.
    /// </summary>
    private void ShowSettings(bool open)
    {
        SettingsHost.IsHitTestVisible = open;
        SettingsSlide.X = open ? 0 : Math.Max(SettingsHost.Width, SettingsLeastWidth);
        if (!open && _settingsPanel is null) SettingsHost.Child = null;
    }

    /// <summary>
    /// Opens the song list over the window (modal, so the settings behind it cannot be used meanwhile). The result is only
    /// pending: it is written when the settings are applied.
    /// </summary>
    private SongListResult? ShowSongList(IReadOnlyList<SongPreference> pending) =>
        AskSongList(pending, _settingsPanel?.BuildSettings() ?? _model.Settings);

    /// <summary>
    /// The song list window over the songs the lists show: the practice maps <paramref name="settings"/> leave out are not
    /// in it (user rule). The choices kept for those are handed back with the window's own, so confirming it drops none.
    /// </summary>
    private SongListResult? AskSongList(IReadOnlyList<SongPreference> pending, VisualizerSettings settings)
    {
        var listed = _model.ListedSongs(settings);
        var window = new SongListWindow(listed, pending, settings, SongFileTimes, _model.IsHiddenByDefault, _model.SongFamilies)
        {
            Owner = this,
            RemoveSong = RemoveListed
        };
        if (window.ShowDialog() != true || window.Result is not { } result) return null;
        var shown = listed.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var left = _model.AllSongs.Select(entry => entry.Id).Where(id => !shown.Contains(id)).ToHashSet(StringComparer.Ordinal);
        return result with { Preferences = [.. result.Preferences, .. pending.Where(song => left.Contains(song.Id))] };
    }

    private void ApplyFromDialog(VisualizerSettings settings)
    {
        if (_settingsPanel is { } panel) _model.SongPreferences = panel.SongPreferences;
        _model.ApplySettings(settings);
    }

    /// <summary>
    /// Every applied change ends here, whatever made it (the settings window, the console, a forced lane change, rollback):
    /// the key bindings follow and the settings are saved.
    /// </summary>
    /// <summary>
    /// The console speaks the app's language (user rule), and so do the reasons kept for it between commands (a link
    /// note, an inspect stop), which are written before anyone types.
    /// </summary>
    private void SpeakConsole() => ConsoleText.Language = Languages.Resolve(_model.Settings.Language) ?? Languages.English;

    private void SettingsCommitted()
    {
        SpeakConsole();
        UpdateBindings();
        SaveSettings(_model.Settings);
        TellLinkerEase(_model.Settings.JudgmentEaseScale);
        TellLinkerKeys(_model.Settings);
        PrepareSettings();
    }

    /// <summary>
    /// Tells the game's linker the judgment ease (user request), whether or not settings are saved: it applies only while
    /// this app runs, so closing puts the game's own judgment back (<see cref="AppStorage.WriteJudgmentEase"/>).
    /// </summary>
    private void TellLinkerEase(double scale)
    {
        if (!UseAppStore) return;
        try { Storage.WriteJudgmentEase(scale); }
        catch (Exception error) when (IsExpectedError(error) || error is ArgumentOutOfRangeException)
            { _model.SetMessage(_model.Text.Format("SettingsWriteFailed", error.Message)); }
    }

    /// <summary>The notes the keys were last worked out for, whether they are told at all, and what was written.</summary>
    private IReadOnlyList<Visualizer.Core.Visualization.VisualizerNote>? _keysNotes;
    private bool _keysTold;
    private string? _keysWritten;

    /// <summary>
    /// Tells the linker the keys for the game's key limiter (user request): this app's lane keys for the maximum tile count
    /// (with the auto tile count, for the lanes the loaded song uses) and its reset key while it runs, none once it closes
    /// (<see cref="GameKeys.For"/>, <see cref="AppStorage.WriteKeyLimiter"/>). Written only when the keys change.
    /// </summary>
    private void TellLinkerKeys(VisualizerSettings? settings)
    {
        if (!UseAppStore) return;
        _keysTold = settings is not null;
        _keysNotes = _model.Playback.Notes;
        var keys = settings is null ? [] : GameKeys.For(settings, _keysNotes.Select(note => note.Tile));
        var line = string.Join(" ", keys);
        if (line == _keysWritten) return;
        try
        {
            Storage.WriteKeyLimiter(keys);
            _keysWritten = line;
        }
        catch (Exception error) when (IsExpectedError(error) || error is ArgumentException)
            { _model.SetMessage(_model.Text.Format("SettingsWriteFailed", error.Message)); }
    }

    /// <summary>
    /// Ctrl+T: opens the console; pressed again it minimizes the console, and once more it shows it
    /// again, and so on. Only the console changes; this window stays as it is.
    /// </summary>
    public CmdWindow ToggleConsole()
    {
        if (_console is not { } open) return ShowConsole();
        open.ToggleMinimized();
        return open;
    }

    /// <summary>Opens the console, or brings the open one forward. It stays open after the settings dialog closes.</summary>
    public CmdWindow ShowConsole()
    {
        if (_console is { } open)
        {
            if (open.WindowState == WindowState.Minimized) open.ToggleMinimized();
            else open.Activate();
            return open;
        }
        // It opens the way this window was shown: with the focus in the app, without it in tests.
        // A running inspect -flow keeps owning the console even if its window is closed and opened again.
        if (_consoleSession is not { Busy: true }) _consoleSession = new CmdSession(this);
        var console = new CmdWindow(_consoleSession, _appliedTheme) { ShowActivated = ShowActivated, Shortcut = ConsoleKeyInWindow };
        console.Closed += (_, _) => _console = null;
        _console = console;
        // An unshown main window (tests, captures) only creates it.
        if (IsVisible)
        {
            console.Owner = this;
            console.Show();
        }
        return console;
    }

    private void InitializeInput()
    {
        try
        {
            UpdateBindings();
            _input.KeyPressed += HandleObservedKey;
            _input.KeyReleased += HandleReleasedKey;
            _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _windowSource?.AddHook(ObserveMessage);
            _input.Start(new WindowInteropHelper(this).Handle);
            _observingKeys = true;
            // The frames arrive on this thread, so it asks Windows for the multimedia scheduling class: with a game running,
            // frames the compositor did hand out were reaching this app late (console: fps, Handoff).
            _scheduling ??= MultimediaScheduling.Register();
            _model.SetInputStatus(_model.Text["InputWatching"]);
        }
        catch (Exception error) when (error is Win32Exception or ArgumentException)
        {
            _model.SetInputStatus(_model.Text.Format("InputUnavailable", error.Message));
        }
    }

    private void UpdateBindings()
    {
        _bindings = ControlBindings.Create(_model.Settings);
        _laneKeys = ControlBindings.CreateLaneKeys(_model.Settings);
        Field.ReleaseAllLanes();
        // Ctrl and T join the bound keys so the console shortcut still arrives while the game has the focus.
        _input.SetObservedKeys(_bindings.Keys.Concat(_laneKeys.Keys)
            .Concat([ControlKey, LeftControlKey, RightControlKey, ConsoleKey]));
    }

    private nint ObserveMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        _input.ObserveWindowMessage(message, lParam);
        // Leave handled unchanged: WPF/DefWindowProc performs normal processing and cleanup.
        return 0;
    }

    /// <summary>Ctrl and T as the keyboard reports them; raw input gives the plain Ctrl code with a side flag.</summary>
    private const int ControlKey = 0x11, LeftControlKey = 0xA2, RightControlKey = 0xA3, ConsoleKey = 0x54, EscapeKey = 0x1B;

    /// <summary>Whether Ctrl is held, followed through the observed keys so Ctrl+T works while the game has the focus.</summary>
    private bool _controlHeld;

    private void HandleObservedKey(int virtualKey)
    {
        if (virtualKey is ControlKey or LeftControlKey or RightControlKey) _controlHeld = true;
        else if (virtualKey == ConsoleKey && _controlHeld && ObservedConsoleKey()) return;
        // Esc stops link inspect wherever it is pressed, before anything else reads it.
        if (virtualKey == EscapeKey && _inspection is not null)
        {
            StopInspect("Esc");
            return;
        }
        var hasAction = _bindings.TryGetValue(virtualKey, out var action);
        var hasLane = _laneKeys.TryGetValue(virtualKey, out var tile);
        if (!hasAction && !hasLane) return;
        // Runs inside the window procedure: an escaping exception would take down the app.
        try
        {
            // Typing in the console must never start, reset or press lanes.
            // The note editor uses a, s, d and the number keys itself, so nothing reaches the lanes while it is open.
            if (_dialogOpen || _editor is not null || _console is { IsActive: true } || (IsActive && IsEditingInput())) return;
            // Any key the app reacts to counts as input, so the song-end offer waits for its quiet second again; the
            // reset key still sees the offer it answers.
            var offered = _model.ShowSongEndNotice;
            _model.NoteInput();
            var settings = _model.Settings;
            bool? foreground = null;
            // ON: ADOFAI or this visualizer (including its dialogs) is the foreground app. OFF: any app.
            bool Foreground() => !settings.OnlyReactWhenAdofaiForeground
                || (foreground ??= WindowsForegroundObserver.IsCurrentProcessForeground()
                    || WindowsForegroundObserver.IsProcessForeground(settings.AdofaiProcessName));
            var paused = _model.Playback.State == PlaybackState.Paused;
            var waiting = _model.Playback.State == PlaybackState.Waiting;
            // With link auto on, the linker alone puts the playhead anywhere (user rule): the keys that start, reset, pause or
            // move it are the game's, and a lane key only marks its note.
            // link inspect sends the lane keys itself, and its start key must not start or reset anything either.
            var gameMoves = _linkAuto || _inspection is not null;
            if (hasAction && !(gameMoves && InputPolicy.MovesPlayhead(action)) && InputPolicy.ShouldReact(action, settings,
                    action is InputAction.Toggle or InputAction.OpenSettings || Foreground(), false))
            {
                switch (action)
                {
                    case InputAction.Reset: _model.ResetFromKey(offered); break;
                    // While paused, a start key that is also a lane key resumes on that lane's note below.
                    case InputAction.Start: if (!(paused && hasLane)) _model.StartFromKey(); break;
                    case InputAction.Pause: _model.PauseFromKey(); break;
                    case InputAction.PreviousCheckpoint: _model.MoveCheckpoint(-1); break;
                    case InputAction.NextCheckpoint: _model.MoveCheckpoint(1); break;
                    case InputAction.Toggle: _model.Toggle(); break;
                    case InputAction.OpenSettings: Dispatcher.BeginInvoke(new Action(OpenSettings)); break;
                }
            }
            // Start runs first, so the first hit that starts playback can also mark its note.
            if (hasLane && _model.Playback.State != PlaybackState.Off
                && InputPolicy.ShouldReact(InputAction.LaneKey, settings, Foreground(), false))
            {
                if (!gameMoves && InputPolicy.ShouldReact(InputAction.Start, settings, Foreground(), false))
                {
                    if (paused) _model.ResumeFromLane(tile);
                    else _model.StartFromLane(tile);
                }
                // The press that started playback plays its first note, the whole chord if it is one.
                Field.PressLane(tile, _model.PressLane(tile, waiting && _model.Playback.State == PlaybackState.Playing));
            }
        }
        catch (Exception error) when (IsExpectedError(error)) { _model.SetMessage(error.Message); }
    }

    /// <summary>Whether the keyboard is observed, which is then where Ctrl+T is acted on.</summary>
    private bool _observingKeys;

    /// <summary>
    /// Ctrl+T as a window of this app saw it. While keys are observed, the observed press is what toggles the console, for
    /// the game and every window of this app alike, so a window only keeps the key to itself: acting here as well would
    /// toggle twice. Without observed keys the window is all there is.
    /// </summary>
    private void ConsoleKeyInWindow()
    {
        if (!_observingKeys) ToggleConsole();
    }

    /// <summary>
    /// Ctrl+T as the keyboard reported it: the one place that acts on it while keys are observed (user rule: with the
    /// game or this visualizer in front). Reading it here rather than in the windows means the Korean input method, a
    /// window with no handler of its own (song list, help) or a window with nothing focused cannot swallow it.
    /// </summary>
    private bool ObservedConsoleKey()
    {
        try
        {
            // A question waiting for an answer has the app to itself; the console comes after it.
            if (System.Windows.Interop.ComponentDispatcher.IsThreadModal) return false;
            var settings = _model.Settings;
            if (!InputPolicy.ShouldOpenConsole(settings, WindowsForegroundObserver.IsCurrentProcessForeground(),
                    WindowsForegroundObserver.IsProcessForeground(settings.AdofaiProcessName))) return false;
            Dispatcher.BeginInvoke(new Action(() => ToggleConsole()));
            return true;
        }
        catch (Exception error) when (IsExpectedError(error)) { return false; }
    }

    private void HandleReleasedKey(int virtualKey)
    {
        if (virtualKey is ControlKey or LeftControlKey or RightControlKey) _controlHeld = false;
        if (!_laneKeys.TryGetValue(virtualKey, out var tile)) return;
        Field.ReleaseLane(tile);
        // A long note let go before its end window shows the miss effect (visual only).
        try { if (_model.ReleaseLane(tile)) Field.MarkMiss(tile); }
        catch (Exception error) when (IsExpectedError(error)) { _model.SetMessage(error.Message); }
    }

    private static bool IsEditingInput()
    {
        for (var element = Keyboard.FocusedElement as DependencyObject; element is not null;)
        {
            if (element is TextBoxBase or ComboBox) return true;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    private static bool IsExpectedError(Exception error) => error is IOException or UnauthorizedAccessException
        or ArgumentException or InvalidOperationException or InvalidDataException or System.Text.Json.JsonException;
}
