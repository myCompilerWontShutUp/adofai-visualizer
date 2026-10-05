using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Visualizer.App;
using Visualizer.App.Rendering;
using Visualizer.App.Themes;
using Visualizer.App.ViewModels;
using Visualizer.App.Views;
using Visualizer.Application.Cache;
using Visualizer.Application.Maps;
using Visualizer.Application.Playback;
using Visualizer.Core.Localization;
using Visualizer.Core.Maps;
using Visualizer.Core.Settings;
using Visualizer.Infrastructure.Cache;
using Visualizer.Infrastructure.Maps;
using Visualizer.Infrastructure.Parsing;
using static Visualizer.Tests.Program;
using Path = System.IO.Path;

namespace Visualizer.Tests;

/// <summary>
/// Language files, song info while stopped, ◀ ▶ song stepping and long titles, ★ change marks against the applied settings,
/// consistent "on" button looks, the app version and the hit glow in the judgment line color.
/// </summary>
internal static class LocalizationThemeTests
{
    private sealed class Clock : IMonotonicClock { public double NowMs { get; set; } }

    private sealed class FixedCache(IReadOnlyList<MapCacheEntry> entries) : IReadOnlyMapCache
    {
        public Task<IReadOnlyList<MapCacheEntry>> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(entries);
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    private static MapCacheEntry Song(string id, string name, ParsedTimeline timeline, MapSongInfo? info = null) =>
        new(id, @"C:\maps", $"{id}\\main.adofai", name, MapParseStatus.Supported, timeline, Array.Empty<string>(), "", AdofaiTimelineParser.CurrentVersion, info);

    public static IEnumerable<(string Name, Action Run)> Create(string artifacts)
    {
        yield return ("Language files: every language has the same keys, messages follow the language and missing keys fall back", () =>
        {
            Assert(Languages.All.Select(l => l.Id).SequenceEqual(new[] { Languages.Korean, Languages.English }), "Korean first, then English");
            Assert(Languages.All.Select(l => l.Name).SequenceEqual(new[] { "한국어", "English" }), "Names come from the files");
            var korean = Languages.Keys(Languages.Korean).Order(StringComparer.Ordinal).ToArray();
            var english = Languages.Keys(Languages.English).Order(StringComparer.Ordinal).ToArray();
            Assert(korean.SequenceEqual(english), "Missing in english: " + string.Join(", ", korean.Except(english)) + " / missing in korean: " + string.Join(", ", english.Except(korean)));
            Equal(Languages.Get(Languages.Korean, "Language"), "언어(Language)");
            Equal(Languages.Get(Languages.English, "Language"), "Language");
            Equal(Languages.Get("KOREAN", "Sync"), "동기화");
            Equal(Languages.Get(Languages.Korean, "No such key"), "No such key");
            Equal(Languages.Get("klingon", "Sync"), "Sync");
            var s = new VisualizerSettings();
            Equal(s.Language, "korean");
            Assert((s with { NoteTravelTimeMs = -1 }).Validate()[0].Contains("노트 이동 시간"), "Korean validation");
            Assert((s with { Language = Languages.English, NoteTravelTimeMs = -1 }).Validate()[0].Contains("Travel time"), "English validation");
            var unknown = (s with { Language = "klingon" }).ValidateFields();
            Assert(unknown.Any(e => e.Field == "Language" && e.Message.Contains("korean, english")), "Unknown languages list the files");
            Assert((s with { Language = "English" }).Validate().Count == 0, "Language ids ignore letter case");

            var window = new SettingsPanel(s, new MemorySettingsLayoutStore());
            Assert(FindAll<TextBlock>(window).Any(t => t.Text == "언어(Language)") && FindAll<TextBlock>(window).Any(t => t.Text == "동타 판정 간격 (ms↓)"), "Korean labels");
            Assert(FindAll<TextBlock>(window).All(t => t.Text != "타격 효과"), "The hit effect color is part of the judgment line");
            var englishWindow = new SettingsPanel(s with { Language = Languages.English }, new MemorySettingsLayoutStore());
            Assert(FindAll<TextBlock>(englishWindow).Any(t => t.Text == "Language") && FindAll<TextBlock>(englishWindow).Any(t => t.Text == "Simultaneous window (ms↓)"), "English labels");
            Assert(SettingsJson.Export(s).Contains("\"Language\": \"korean\"") && !SettingsJson.Export(s).Contains("HitEffectColor"), "String ids in JSON");
        });

        yield return ("Every version has update notes in every language, and the first start of a version shows the right ones", () =>
        {
            // User request: the first start after an update says what changed. A version without notes would say nothing.
            foreach (var language in Languages.All)
                Assert(ReleaseNotes.Versions(language.Id).Any(version => version.ToString(3) == AppInfo.Version),
                    $"{language.Id} has no ReleaseNotes.{AppInfo.Version}");
            var english = Languages.English;
            var all = ReleaseNotes.Versions(english);
            Assert(all.SequenceEqual(all.OrderDescending()), "Newest first");
            Assert(!ReleaseNotes.ToShow(english, null, AppInfo.Version, ranBefore: false).Any(), "A fresh copy has nothing to compare with");
            Assert(ReleaseNotes.ToShow(english, null, AppInfo.Version, ranBefore: true).SequenceEqual([AppInfo.Version]), "From before notes existed");
            Assert(!ReleaseNotes.ToShow(english, AppInfo.Version, AppInfo.Version, true).Any(), "Seen already");
            Assert(ReleaseNotes.ToShow(english, "0.9.2", AppInfo.Version, true).SequenceEqual([AppInfo.Version]), "Every version since the one seen");
            // Back on an older version after a newer one: that version's own notes.
            Assert(ReleaseNotes.ToShow(english, "99.0.0", AppInfo.Version, true).SequenceEqual([AppInfo.Version]), "Gone back: that version's notes");
            Assert(!ReleaseNotes.ToShow(english, "0.9.2", AppInfo.Version + "x", true).Any(), "An unreadable version shows nothing");

            // As the main window shows it: one button, the version, then its lines.
            var korean = Languages.Korean;
            var notes = AppInfo.Version + "\n" + Languages.Get(korean, ReleaseNotes.Prefix + AppInfo.Version);
            var dialog = new ConfirmDialog(new VisualizerSettings(), Languages.Format(korean, "UpdateNotesTitle", AppInfo.Version), notes,
                Languages.Get(korean, "UpdateNotesClose"), null);
            Capture(dialog, Path.Combine(artifacts, "update-notes.png"), 440, 420);
        });

        yield return ("Initial settings come from DefaultSettings.jsonc: every setting listed once, valid, and equal to a new setting", () =>
        {
            var settable = typeof(VisualizerSettings).GetProperties().Where(p => p.CanWrite).Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
            var listed = DefaultSettings.Keys.Order(StringComparer.Ordinal).ToArray();
            Assert(settable.SequenceEqual(listed),
                $"Missing in the file: {string.Join(", ", settable.Except(listed))} / not settings: {string.Join(", ", listed.Except(settable))}");
            var s = new VisualizerSettings();
            s.EnsureValid();
            var repo = WorkspaceMapCache.FindRepositoryRoot(AppContext.BaseDirectory);
            var file = File.ReadAllText(Path.Combine(repo, "src", "Visualizer.Core", "Settings", "DefaultSettings.jsonc"));
            // The file is a complete settings JSON, so importing it over anything gives exactly the initial settings.
            Equal(SettingsJson.Import(file, s with { NoteTravelTimeMs = 1, Theme = "white" }), s);
            Assert(Languages.Get(Languages.Korean, "InputScopeHelp").StartsWith("활성화하면", StringComparison.Ordinal)
                && !Languages.Get(Languages.Korean, "ExtraLanesHelp").Contains("켜면"), "System wording: 활성화 / 비활성화");
            Equal(Languages.Get(Languages.Korean, "Extra lanes"), "여분 레인 배치");
        });

        yield return ("Keys built from a name reach the language files, and the app's words and colors come from no other place", () =>
        {
            var keys = Languages.Keys(Languages.Korean).ToHashSet(StringComparer.Ordinal);
            // Keys the code builds out of a name, which the key-by-key checks above cannot see.
            var settings = typeof(VisualizerSettings).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            var hints = keys.Where(k => k.StartsWith("Hint.", StringComparison.Ordinal)).Select(k => k[5..]).ToArray();
            Assert(hints.Length > 50, $"The hints are still there: {hints.Length}");
            Assert(hints.All(settings.Contains), "Hint keys with no such setting: "
                + string.Join(", ", hints.Where(h => !settings.Contains(h))));
            void Family(string prefix, IEnumerable<string> values)
            {
                var want = values.ToArray();
                var missing = want.Where(v => !keys.Contains($"{prefix}.{v}")).ToArray();
                var over = keys.Where(k => k.StartsWith(prefix + ".", StringComparison.Ordinal))
                    .Select(k => k[(prefix.Length + 1)..]).Where(v => !want.Contains(v, StringComparer.Ordinal)).ToArray();
                Assert(missing.Length == 0 && over.Length == 0,
                    $"{prefix}.*: missing {string.Join(", ", missing)} / no such value {string.Join(", ", over)}");
            }
            Family("SongSort", Enum.GetNames<SongSortField>());
            Family("SongGroup", Enum.GetNames<SongGroupField>());
            Family("SongField", VisualizerSettings.SongListFieldNames);
            Family("JudgmentMode", ["Off", "MagicCircle", "Always"]);
            Family("JudgmentDifficulty", ["Lenient", "Normal", "Strict"]);

            // The rest of the rule is about where words and colors may be written, which only the source shows.
            var app = Path.Combine(WorkspaceMapCache.FindRepositoryRoot(AppContext.BaseDirectory), "src", "Visualizer.App");
            var sources = Directory.GetFiles(app, "*.cs", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(app, "*.xaml", SearchOption.AllDirectories)).ToArray();
            Assert(sources.Length > 30, $"The app's source was not found: {app}");
            var themeKeys = ThemeCatalog.BrushKeys
                .SelectMany(key => new[] { key, key.Replace("Brush", "Color", StringComparison.Ordinal) })
                .Concat(["PrimaryBrush", "ToggleOnBrush", "DialogBackdropBrush", "PrimaryColor", "ToggleOnColor", "DialogBackdropColor"])
                .ToHashSet(StringComparer.Ordinal);
            var styleKeys = System.Text.RegularExpressions.Regex
                .Matches(File.ReadAllText(Path.Combine(app, "Themes", "Theme.xaml")), "x:Key=\"([A-Za-z0-9_]+)\"")
                .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            var unresolved = new List<string>();
            var staticColors = new List<string>();
            var korean = new List<string>();
            var looked = 0; // So an empty scan cannot pass quietly (app 0.8.9 lesson).
            foreach (var file in sources)
            {
                var name = Path.GetFileName(file);
                var text = File.ReadAllText(file);
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"DynamicResource\s+([A-Za-z0-9_]+)"))
                {
                    looked++;
                    if (!themeKeys.Contains(m.Groups[1].Value) && !styleKeys.Contains(m.Groups[1].Value))
                        unresolved.Add($"{name}: {m.Groups[1].Value}");
                }
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"StaticResource\s+([A-Za-z0-9_]+)"))
                    if (themeKeys.Contains(m.Groups[1].Value)) staticColors.Add($"{name}: {m.Groups[1].Value}");
                // The console is English whatever the language is (user rule), so Korean in it is a slip.
                if (name is "CmdSession.cs" or "CmdCatalog.cs" or "CmdWindow.cs" or "ReportEditor.cs")
                    foreach (var line in text.Split('\n'))
                        if (line.Any(c => c is >= '가' and <= '힣') && line.TrimStart() is var t
                            && !t.StartsWith("//", StringComparison.Ordinal) && !t.StartsWith('*'))
                            korean.Add($"{name}: {line.Trim()}");
            }
            Assert(looked > 50, $"The colors the app asks for were not seen at all: {looked}");
            Assert(unresolved.Count == 0, "Colors asked for that no theme has: " + string.Join(" | ", unresolved));
            // A theme change rewrites the dictionary, so a color taken once (StaticResource) would keep the old theme's.
            Assert(staticColors.Count == 0, "Theme colors taken with StaticResource: " + string.Join(" | ", staticColors));
            Assert(korean.Count == 0, "Korean in the console, which is English only: " + string.Join(" | ", korean));
        });

        yield return ("Window colors come from the palette files only: every brush has a plain color too and the panel takes its own", () =>
        {
            // The rule is that window colors live in the palette files. Every brush is also offered as a plain Color, for
            // the places that take one instead of a brush (an effect's glow), so nothing has to copy a theme's value.
            foreach (var theme in ThemeCatalog.All)
            {
                var resources = new ResourceDictionary();
                ThemeCatalog.Apply(resources, theme.Id);
                foreach (var key in ThemeCatalog.BrushKeys)
                {
                    Assert(resources[key] is System.Windows.Media.Brush, $"{theme.Id}: {key}");
                    Assert(resources[key.Replace("Brush", "Color", StringComparison.Ordinal)] is System.Windows.Media.Color,
                        $"{theme.Id}: the color of {key}");
                }
            }
            // The glows in the settings header used to hold the universe theme's own values; now each theme has its own.
            var universe = new ResourceDictionary();
            var white = new ResourceDictionary();
            ThemeCatalog.Apply(universe, "universe");
            ThemeCatalog.Apply(white, "white");
            Assert((System.Windows.Media.Color)universe["FireColor"] != (System.Windows.Media.Color)white["FireColor"], "Own fire color");
            Assert((System.Windows.Media.Color)universe["AccentColor"] != (System.Windows.Media.Color)white["AccentColor"], "Own accent color");
            // And the panel's own background is the theme's, not a value from its XAML.
            var panel = new SettingsPanel(new VisualizerSettings(), new MemorySettingsLayoutStore());
            // The panel uses the dialog backdrop, which is the window color or the star field, depending on the theme.
            Assert(ReferenceEquals(panel.Background, panel.FindResource("DialogBackdropBrush")), "The panel takes the theme's backdrop");
            Assert(ReferenceEquals(panel.Foreground, panel.FindResource("TextBrush")), "And the theme's text brush");
        });

        yield return ("Lane backgrounds follow the theme until set: dark keeps the former tint, universe is faintly blue", () =>
        {
            Assert(ThemeCatalog.LaneBackgrounds(ThemeCatalog.Dark).Select(c => c.ToString())
                .SequenceEqual(Enumerable.Range(0, 8).Select(i => i % 2 == 0 ? "#00FFFFFF" : "#08FFFFFF")), "Dark keeps the earlier lane colors");
            foreach (var theme in ThemeCatalog.All)
            {
                var lanes = ThemeCatalog.LaneBackgrounds(theme.Id);
                Assert(lanes.Count == 8 && lanes[0] != lanes[1] && lanes[0] == lanes[2], $"{theme.Id}: neighbouring lanes differ and alternate");
            }
            var universe = ThemeCatalog.LaneBackgrounds(ThemeCatalog.Universe);
            Assert(universe[0].B > universe[0].R && universe[0].A < 0x20, "Universe is a faint blue tint");
            Assert(new VisualizerSettings().FollowsThemeLaneBackgrounds, "Follows the theme by default");

            const int width = 200, height = 400;
            byte[] Render(string theme)
            {
                var session = new PlaybackSession(new Clock());
                session.LoadTimeline(new[] { 5000d }, new VisualizerSettings { DynamicTiming = false, Theme = theme });
                session.Toggle();
                var field = new NoteField { Session = session };
                field.Measure(new Size(width, height));
                field.Arrange(new Rect(0, 0, width, height));
                field.UpdateLayout();
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(field);
                var pixels = new byte[width * height * 4];
                bitmap.CopyPixels(pixels, width * 4, 0);
                return pixels;
            }
            int Blue(byte[] pixels, int x) => pixels[(100 * width + x) * 4];
            var dark = Render(ThemeCatalog.Dark);
            var blue = Render(ThemeCatalog.Universe);
            Assert(Blue(blue, 25) > Blue(dark, 25), $"Universe lanes are bluer ({Blue(blue, 25)} vs {Blue(dark, 25)})");
            Assert(Blue(blue, 75) != Blue(blue, 25) && Blue(dark, 75) != Blue(dark, 25), "Neighbouring lanes are told apart");
        });

        yield return ("Song info is read, cached and shown over the field only while a loaded song is stopped", () =>
        {
            var repo = WorkspaceMapCache.FindRepositoryRoot(AppContext.BaseDirectory);
            var example = new AdofaiTimelineParser().Parse(new ReadOnlyDirectoryMapSource(Path.Combine(repo, ".example")).ReadAsync("main.adofai").GetAwaiter().GetResult());
            Equal(example.Info, new MapSongInfo("Plum", "R", "KanoKaz & NumbEr07", 7));

            var root = Path.Combine(artifacts, "song-info-cache", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "AGENTS.md"), "Test-owned fixture");
            File.WriteAllText(Path.Combine(root, "Visualizer.sln"), "Test-owned fixture");
            var cache = new WorkspaceMapCache(root);
            var cached = new MapCacheEntry("info", Path.Combine(root, "maps"), "main.adofai", "Plum - R", MapParseStatus.Supported,
                new ParsedTimeline(new[] { 0d, 500 }), Array.Empty<string>(), "", AdofaiTimelineParser.CurrentVersion, example.Info);
            cache.ReplaceAsync(new[] { cached, cached with { Id = "old", Info = null } }).GetAwaiter().GetResult();
            var read = cache.ReadAsync().GetAwaiter().GetResult();
            Equal(read.Single(e => e.Id == "info").Info, example.Info);
            Equal(read.Single(e => e.Id == "old").Info, null);

            var window = new MainWindow();
            var model = (MainViewModel)window.DataContext;
            var timeline = new ParsedTimeline(new[] { 0d, 500, 1000, 1500, 2000 }, null, false, new[] { 1, 2, 3, 4, 5 });
            model.SelectedMap = Song("plum", "Plum - R", timeline, example.Info);
            model.LoadSelectedSong(); // Loading switches the visualizer on.
            Assert(model.ShowSongOverlay, "Shown when reset (waiting)");
            Equal(model.OverlayInfo, "작곡가 Plum · 난이도 7");
            Equal(model.OverlayTiles, "길이 0:02 · 남은 시간 0:02 · 타일 5개 · 마법진 0개"); // The bar is on by default.
            Assert(model.OverlayProgress == "처음부터 · 자동 진행 활성화", model.OverlayProgress);
            Assert(model.ShowFieldControls, "The speed, the tile and the slot are on the field while it waits");
            Equal(((SolidColorBrush)model.OverlayBrush).Color, Colors.White);
            // Nothing on the line is underlined while every control is where the song itself put it (user rule).
            Assert(model.OverlayTileParts.All(part => !part.Changed), "Nothing changed it yet");
            // The wash behind the letters is the opposite of them, so the notes running past cannot break them up.
            Assert(((SolidColorBrush)model.OverlayBackBrush).Color.R < 60 && ((SolidColorBrush)model.OverlayBackBrush).Color.A > 128,
                "Dark and solid enough behind white letters: " + ((SolidColorBrush)model.OverlayBackBrush).Color);
            Capture(window, Path.Combine(artifacts, "song-overlay.png"), 480, 800);
            Equal(((FrameworkElement)window.FindName("SongOverlay")).Visibility, Visibility.Visible);
            // The band with the speed, the tile and the slot goes when the song runs, and the window has to hear about
            // it: the property was right all along but no binding was told, so the controls sat on the field while the
            // game played (user report).
            var raised = new List<string>();
            void Seen(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName is { } name) raised.Add(name);
            }
            model.PropertyChanged += Seen;
            model.Start();
            model.PropertyChanged -= Seen;
            Assert(!model.ShowSongOverlay, "Hidden while playing");
            Assert(!model.ShowFieldControls, "The controls go while it plays");
            Assert(raised.Contains(nameof(MainViewModel.ShowFieldControls)), "And the window is told: " + string.Join(", ", raised));
            model.PauseResume();
            Assert(model.ShowSongOverlay, "Shown while paused");
            Assert(!model.ShowFieldControls, "They are away while it is paused");
            model.ApplySettings(model.Settings with { PlaybackSpeedPercent = 50 });
            Assert(!model.OverlayProgress.Contains("배속") && model.OverlayProgress.Contains("체크포인트 비활성화됨"), model.OverlayProgress);
            model.ApplySettings(model.Settings with { PlaybackSpeedPercent = 100 });
            Assert(model.StartAtTile(2), "Section start");
            Equal(model.OverlayTiles, "길이 0:01 · 남은 시간 0:01 · 남은 타일 3개 / 전체 5개 · 마법진 0개");
            Equal(model.OverlayProgress, "체크포인트 비활성화됨");
            // A practice tile moved the clock and the tile count; the magic circle count is never marked (user rule).
            Assert(model.OverlayTileParts.Take(3).All(part => part.Changed) && !model.OverlayTileParts[3].Changed,
                "A practice tile changes everything but the circles: "
                + string.Join(" | ", model.OverlayTileParts.Select(p => $"{p.Text}={p.Changed}")));
            model.SectionText = "";
            model.ApplySettings(model.Settings with { PlaybackSpeedPercent = 80 });
            var speedParts = model.OverlayTileParts;
            // The speed stretches the clock but moves neither the tile count nor the magic circles: scaling every gap by
            // the same amount leaves the equal ones equal (user report: the circles were underlined with no change).
            Assert(speedParts[0].Changed && speedParts[1].Changed && !speedParts[2].Changed && !speedParts[3].Changed,
                "Only the clock moves with the speed: " + string.Join(" | ", speedParts.Select(p => $"{p.Text}={p.Changed}")));
            model.ApplySettings(model.Settings with { PlaybackSpeedPercent = 100 });
            model.ApplySettings(model.Settings with { LaneBackgroundColors = string.Join(" ", Enumerable.Repeat("#FFF0F0F0", 8)) });
            Equal(((SolidColorBrush)model.OverlayBrush).Color, Colors.Black);
            Assert(((SolidColorBrush)model.OverlayBackBrush).Color.R > 200, "And light behind black letters");
            Capture(window, Path.Combine(artifacts, "song-overlay-light-lanes.png"), 480, 800);
            model.ApplySettings(model.Settings with { Language = Languages.English });
            Equal(model.OverlayInfo, "Artist Plum · Difficulty 7");
            model.ApplySettings(model.Settings with { ShowSongInfo = false });
            Assert(!model.ShowSongOverlay, "The General switch hides it");
            window.Close();

            var unknown = new MainViewModel();
            unknown.SelectedMap = Song("bare", "Bare", timeline);
            unknown.LoadSelectedSong();
            unknown.Toggle();
            Equal(unknown.OverlayInfo, "곡 정보를 찾을 수 없음");
            var preview = new MainViewModel();
            preview.LoadPreview();
            Assert(!preview.ShowSongOverlay, "The rule preview is not a song");
        });

        yield return ("◀ ▶ step through songs and load them; a long title shows its beginning", () =>
        {
            var entries = new[] { " - FM-1 Cutscene", "B song", "C song" }.Select((name, i) => Song($"s{i}", name, new ParsedTimeline(new[] { 0d, 500 }))).ToArray();
            var model = new MainViewModel();
            model.RefreshLibraryAsync(new FixedCache(entries)).GetAwaiter().GetResult();
            model.SongQuery = entries[0].SongName;
            Equal(model.LibraryView.Cast<object>().Count(), 3);
            model.RefreshLibraryAsync(new FixedCache(entries)).GetAwaiter().GetResult();
            Equal(model.LibraryView.Cast<object>().Count(), 3);
            Assert(model.MoveSong(1), "Next song"); Equal(model.SongName, "B song");
            Assert(model.MoveSong(1), "Next song"); Equal(model.SongName, "C song");
            Assert(model.MoveSong(1), "The last wraps to the first"); Equal(model.SongName, " - FM-1 Cutscene");
            Assert(model.MoveSong(-1), "The first wraps to the last"); Equal(model.SongName, "C song");
            Assert(model.MoveSong(-1), "Previous song"); Equal(model.SongName, "B song");
            var single = new MainViewModel();
            single.RefreshLibraryAsync(new FixedCache(entries.Take(1).ToArray())).GetAwaiter().GetResult();
            Assert(single.CanMoveSong && single.MoveSong(1), "One song that is not loaded yet can be loaded");
            Assert(!single.CanMoveSong && !single.MoveSong(1), "Only the loaded song: nothing else to step to");
            Equal(model.SongQuery, "B song"); Equal(model.SelectedMap?.Id, "s1");
            Equal(model.LibraryView.Cast<object>().Count(), 3);
            var empty = new MainViewModel();
            var notices = new List<string>();
            empty.NotifySyncFirst = notices.Add;
            Assert(!empty.CanMoveSong && !empty.MoveSong(1) && notices.Count == 1, "Without songs it asks to sync");

            var window = new MainWindow();
            var main = (MainViewModel)window.DataContext;
            var longName = "An artist with a very long name - A song title that is far too long to fit inside the song box at all";
            main.RefreshLibraryAsync(new FixedCache(new[] { Song("long", longName, new ParsedTimeline(new[] { 0d, 500 })) })).GetAwaiter().GetResult();
            Capture(window, Path.Combine(artifacts, "song-stepping.png"), 480, 800);
            var picker = (ComboBox)window.FindName("SongPicker");
            var box = (TextBox)picker.Template.FindName("PART_EditableTextBox", picker);
            box.Select(box.Text.Length, 0);
            box.ScrollToEnd();
            box.UpdateLayout();
            window.ShowSongTitleStart();
            box.UpdateLayout();
            Assert(box.CaretIndex == 0 && box.HorizontalOffset == 0, $"The title starts at its beginning (caret {box.CaretIndex}, offset {box.HorizontalOffset})");
            bool Enabled(string name) => ((UIElement)window.FindName(name)).IsEnabled;
            Assert(Enabled("NextSongButton") && Enabled("PreviousSongButton"), "The one song is not loaded yet");
            Assert(!Enabled("NextCheckpointButton") && !Enabled("PreviousCheckpointButton"), "No song: checkpoint arrows are grey");
            main.MoveSong(1);
            Assert(!Enabled("NextSongButton") && !Enabled("PreviousSongButton"), "Only the loaded song: song arrows are grey");
            Capture(window, Path.Combine(artifacts, "arrows-disabled.png"), 480, 800);
            window.Close();
        });

        yield return ("★ marks settings changed since Apply; sections turn orange only for those", () =>
        {
            var window = new SettingsPanel(new VisualizerSettings { NoteTravelTimeMs = 650 }, new MemorySettingsLayoutStore());
            int Stars() => FindAll<TextBlock>(window).Count(t => t.Text == "★" && t.Visibility == Visibility.Visible);
            Assert(Stars() == 0 && window.SectionState("SectionDisplay") == "default", "Applied values are unmarked, even when not defaults");
            var travel = FindAll<TextBox>(window).Single(t => t.Text == "650");
            travel.Text = "600";
            Assert(window.IsMarkedChanged("NoteTravelTimeMs") && window.SectionState("SectionDisplay") == "changed", "Changed from the applied value, even back to the default");
            Assert(!window.IsMarkedChanged("NoteThickness") && Stars() == 1, "Only that setting");
            travel.Text = "650";
            Assert(!window.IsMarkedChanged("NoteTravelTimeMs") && window.SectionState("SectionDisplay") == "default", "Back to the applied value");
            var four = FindAll<TextBox>(window).Single(t => t.Text == "3 4 5 6");
            four.Text = "6 5 4 3";
            Assert(!window.IsMarkedChanged("FourKeyPattern"), "The same tiles in another order are unchanged");
            four.Text = "3 4 5 7";
            Assert(window.IsMarkedChanged("FourKeyPattern") && window.IsMarkedChanged("FourKeyOrder") && Stars() == 1, "The pattern row shares one star");
            four.Text = "3 4 5 6";
            FindAll<TextBox>(window).Single(t => t.Text == "94, 218, 242").Text = "rgb(94, 218, 242)";
            Assert(!window.IsMarkedChanged("NoteColor"), "Another spelling of the same color is unchanged");
            var language = FindAll<ComboBox>(window).First(c => c.ItemsSource is string[] names && names.Contains("English"));
            language.SelectedIndex = 1;
            Assert(window.IsMarkedChanged("Language") && window.SectionState("SectionGeneral") == "changed", "Language change is marked");
            Capture(window, Path.Combine(artifacts, "settings-changed-marks.png"), 640, 1200);
        });

        yield return ("Unpressed notes leave when their press window ends; the judgment line slowdown slides passed notes to the bottom line", () =>
        {
            const int width = 200, height = 400;
            // Defaults: judgment line 72px above the bottom line (y 298 of 370), travel 720ms, press window ±150ms. Tile 5 is lane 3 (x 125).
            byte Blue((PlaybackSession Session, Clock Clock) run, NotePressJudge? judge, double timelineMs, int y)
            {
                run.Clock.NowMs = timelineMs;
                var field = new NoteField { Session = run.Session, Judge = judge };
                field.Measure(new Size(width, height));
                field.Arrange(new Rect(0, 0, width, height));
                field.UpdateLayout();
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(field);
                var pixels = new byte[width * height * 4];
                bitmap.CopyPixels(pixels, width * 4, 0);
                return pixels[(y * width + 125) * 4];
            }
            (PlaybackSession Session, Clock Clock) Started(VisualizerSettings settings)
            {
                var clock = new Clock();
                var session = new PlaybackSession(clock);
                session.LoadTimeline(new[] { 0d, 5000 }, settings);
                session.Toggle();
                session.Start();
                return (session, clock);
            }
            // The transparency of a hit note is pinned here so editing the initial settings does not move these pixel checks.
            var basis = new VisualizerSettings
            {
                DynamicTiming = false, SimultaneousWindowMs = 0, PressedNoteTransparencyPercent = 60, NoteTravelTimeMs = 720
            };
            Equal(basis.JudgmentLineHeightDip, 72d); Equal(basis.NoteTravelTimeMs, 720d); Assert(!basis.SlowNotesPastJudgmentLine, "The slowdown defaults off");

            var linear = Started(basis);
            Assert(Blue(linear, null, 100, 339) > 150, "Inside its window an unpressed note keeps falling past the line");
            Assert(Blue(linear, null, 160, 362) < 60, "When the window ends it is gone, before reaching the bottom line");

            var pressedSession = Started(basis);
            var judge = new NotePressJudge();
            Equal(judge.Press(pressedSession.Session, 5, 150), (int?)0);
            Assert(Blue(pressedSession, judge, 160, 362) > 80, "A pressed (dimmed) note still falls to the bottom line");

            var slow = Started(basis with { SlowNotesPastJudgmentLine = true });
            Assert(Blue(slow, null, 140, 368) > 150, "Slowed: near the end of the window the note is just above the bottom line");
            Assert(Blue(Started(basis), null, 140, 368) < 60, "Without the slowdown it would already be gone at that height");
            Assert(Blue(slow, null, 160, 368) < 60, "Slowed notes leave at the bottom line when the window ends");
            Assert(basis.HasSameNoteRulesAs(basis with { SlowNotesPastJudgmentLine = true }), "Visual only");
        });

        yield return ("Settings layout: map folder with a folder picker in Advanced, 1K tile aligned with the tiles column, no theme arrows, neutral focus", () =>
        {
            var window = new SettingsPanel(new VisualizerSettings(), new MemorySettingsLayoutStore());
            string? asked = null;
            window.PickFolder = current => { asked = current; return @"D:\Levels"; };
            // Two folder pickers share the label: the map folder first, the data folder right below it.
            var pickers = FindAll<Button>(window).Where(b => Equals(b.Content, "파일")).ToList();
            Equal(pickers.Count, 2);
            var browse = pickers[0];
            browse.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Equal(asked, VisualizerSettings.DefaultMapRoot);
            Equal(window.BuildSettings().MapRootDirectory, @"D:\Levels");
            window.PickFolder = _ => null;
            browse.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Equal(window.BuildSettings().MapRootDirectory, @"D:\Levels");
            Assert(window.IsMarkedChanged("MapRootDirectory") && window.SectionState("SectionAdvanced") == "changed", "The picked folder is a pending change");

            var expanders = FindAll<Expander>(window).ToList();
            List<string> Labels(Expander expander) => FindAll<TextBlock>((DependencyObject)expander.Content).Select(t => t.Text).ToList();
            Assert(!Labels(expanders[0]).Contains("맵 폴더"), "No map folder in General");
            // The input scope moved up to the general settings; the magic circle bar joined Advanced.
            Assert(Labels(expanders[0]).Contains("전역 입력 범위"), "The input scope sits in the general settings");
            // The experimental card follows Advanced, so Advanced is the second to last.
            var advanced = Labels(expanders[^2]);
            int At(string label) => advanced.IndexOf(label);
            // The experimental switch leads the card; chord section lanes follow tile preference.
            Assert(At("실험적 기능") == 0 && At("실험적 기능") < At("여분 레인 배치") && At("여분 레인 배치") < At("판정선 노트 변속")
                && At("판정선 노트 변속") < At("순서 선호도 사용") && At("순서 선호도 사용") < At("동타 구간 레인 분리")
                && At("동타 구간 레인 분리") < At("불러오기 배속 유지") && At("불러오기 배속 유지") < At("맵 폴더")
                && At("맵 폴더") < At("데이터 저장 폴더") && At("데이터 저장 폴더") < At("ADOFAI 프로세스 이름"),
                "Advanced order: " + string.Join(" / ", advanced));
            // The other chord rules sit right under the chord window in the timing card.
            var timing = Labels(expanders.Single(expander => Labels(expander).Contains("동타 판정 간격 (ms↓)")));
            int Timing(string label) => timing.IndexOf(label);
            Assert(Timing("동타 판정 간격 (ms↓)") < Timing("동타 중앙배치 불가") && Timing("동타 중앙배치 불가") < Timing("동타 우선 배치 라인")
                && Timing("동타 우선 배치 라인") < Timing("시작 대기 시간 (ms)"), "Timing order: " + string.Join(" / ", timing));
            // The chord rules left the experimental card; the two placement rules being tried out took their place.
            var experiments = Labels(expanders[^1]);
            Assert(experiments.Contains("동타 앞 단타 레인 분리") && experiments.Contains("연타 시 패턴 재시작")
                && advanced.Contains("동타 구간 레인 분리"), "The experimental card: " + string.Join(" / ", experiments));
            Assert(window.FindName("PreviousThemeButton") is null && window.FindName("NextThemeButton") is null, "No theme arrows");
            Assert(FindAll<TextBlock>(window).Any(t => t.Text == "타일 순서 선호도"), "Renamed column caption");

            Capture(window, Path.Combine(artifacts, "settings-pattern-rows.png"), 640, 2600);
            var root = (UIElement)window.Content;
            var initial = FindAll<TextBox>(window).Single(t => t.Text == "5");
            var fourTiles = FindAll<TextBox>(window).Single(t => t.Text == "3 4 5 6");
            Assert(Math.Abs(initial.ActualWidth - fourTiles.ActualWidth) < 0.5
                && Math.Abs(initial.TranslatePoint(new Point(0, 0), root).X - fourTiles.TranslatePoint(new Point(0, 0), root).X) < 0.5,
                $"The 1K tile box matches the tiles column ({initial.ActualWidth} vs {fourTiles.ActualWidth})");
            Assert(((SolidColorBrush)window.FindResource("FocusBrush")).Color != ((SolidColorBrush)window.FindResource("AccentBrush")).Color,
                "Focus after a click is not the accent (on) color");
            Assert(new VisualizerSettings().OnlyReactWhenAdofaiForeground, "Global input scope defaults on (app 0.1.2)");
        });

        yield return ("One look for on states, the theme selector, the app version and the judgment-colored hit glow", () =>
        {
            var window = new MainWindow();
            var model = (MainViewModel)window.DataContext;
            model.Playback.LoadTimeline(new[] { 0d, 5000, 9000 }, model.Settings);
            model.Toggle(); model.Start(); model.PauseResume();
            Capture(window, Path.Combine(artifacts, "main-paused-states.png"), 480, 800);
            var soft = window.FindResource("AccentSoftBrush");
            var pause = (Control)window.FindName("PauseButton");
            var power = (Control)window.FindName("PowerButton");
            var auto = (ToggleButton)window.FindName("AutoCheckpointToggle");
            Assert(auto.IsChecked == true && ReferenceEquals(auto.Background, soft), "Auto on uses the accent-soft fill");
            Assert(ReferenceEquals(pause.Background, soft) && ReferenceEquals(power.Background, soft), "Paused and powered on share it");

            Assert(System.Text.RegularExpressions.Regex.IsMatch(AppInfo.Version, @"^\d+\.\d+\.\d+$"), "major.minor.patch: " + AppInfo.Version);
            var version = (TextBlock)window.FindName("VersionLabel");
            Equal(version.Text, "v" + AppInfo.Version);
            var content = (UIElement)window.Content;
            var corner = version.TranslatePoint(new Point(version.ActualWidth, version.ActualHeight), content);
            var gear = (FrameworkElement)window.FindName("SettingsButton");
            var gearCorner = gear.TranslatePoint(new Point(gear.ActualWidth, gear.ActualHeight), content);
            Assert(Math.Abs(corner.X - gearCorner.X) < 4 && corner.Y > gearCorner.Y, $"Bottom right, under the setting button ({corner} vs {gearCorner})");
            window.Close();

            var dialog = new SettingsPanel(new VisualizerSettings(), new MemorySettingsLayoutStore());
            var picker = (ComboBox)dialog.FindName("ThemePicker");
            var selector = (FrameworkElement)dialog.FindName("ThemeSelector");
            Capture(dialog, Path.Combine(artifacts, "settings-theme-selector.png"), 640, 780);
            // The theme is a settings row now, not a corner of the header (user rule).
            var headerBar = (FrameworkElement)dialog.FindName("HeaderBar");
            Assert(!headerBar.IsAncestorOf(selector), "The theme is out of the header");
            Assert(FindAll<Expander>(dialog).Any(card => card.IsAncestorOf(selector)), "and inside a settings card");
            Equal(picker.Items.Count, 3);

            // The hit glow belongs to the judgment line: a pressed lane with a hit glows in its color.
            const int width = 200, height = 400;
            var session = new PlaybackSession(new Clock());
            session.LoadTimeline(new[] { 0d, 5000 }, new VisualizerSettings { DynamicTiming = false, SimultaneousWindowMs = 0, JudgmentLineColor = "#00FF00", JudgmentLineHeightDip = 36 });
            session.Toggle();
            var field = new NoteField { Session = session };
            field.PressLane(3, true);
            field.Measure(new Size(width, height));
            field.Arrange(new Rect(0, 0, width, height));
            field.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(field);
            var pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);
            // Just above the 2px line, inside the fresh glow ellipse (radius 12 px vertically).
            var index = (331 * width + 25) * 4;
            Assert(pixels[index + 1] > pixels[index + 2] + 40 && pixels[index + 1] > pixels[index] + 40,
                $"Green glow above lane 1: B {pixels[index]} G {pixels[index + 1]} R {pixels[index + 2]}");
        });
    }
}
