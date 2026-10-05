using System.Text;

namespace Visualizer.Infrastructure.Storage;

/// <summary>
/// The notes written in the console's report editor, as plain text files in <c>reports/</c> under the app store.
///
/// They exist for the times the user cannot reach an agent: a problem is written down here and an agent reads it later,
/// fixes it and marks it done. So a note is kept **for good** — nothing here deletes by version — and whether it has been
/// taken care of is kept **beside** the note rather than inside it, so the user's own text can never be mistaken for the
/// mark and the mark cannot be typed over. Writing a note again clears that mark: new words mean there is new work.
///
/// Nothing outside the app store is touched, and a title is only ever a file name.
/// </summary>
public sealed class ReportBook(AppStorage storage)
{
    private readonly AppStorage _storage = storage ?? throw new ArgumentNullException(nameof(storage));

    private string Root => Path.Combine(_storage.Root, "reports");
    // The bookkeeping is deliberately not ".txt": every .txt in the folder is a note, so a file of ours cannot show up as
    // one. Until 0.9.1 these were version.txt and written.txt, which the listing offered as notes named "version" and
    // "written" — and deleting "version" made the next call think the folder was from another version and clear it.
    private string WrittenPath => Path.Combine(Root, "written.mark");
    private string DonePath => Path.Combine(Root, "done.mark");

    /// <summary>
    /// A title may only be letters, digits, spaces, dashes and underscores. Everything else is refused, so a title can
    /// never walk out of the folder (AGENTS.md rule 5).
    /// </summary>
    public static bool IsTitle(string? title) =>
        title is { Length: > 0 and <= 64 } && title.Trim() == title
        && title.All(letter => char.IsLetterOrDigit(letter) || letter is ' ' or '-' or '_');

    /// <summary>
    /// A note as the console lists it. <see cref="Done"/> is the version it was marked done in, and empty while it is
    /// still open; it is kept beside the note, not in it, so nobody can type it in or out.
    /// </summary>
    public sealed record ReportNote(string Title, string Version, string Done, int Lines, IReadOnlyList<string> Text,
        string State = "", string Why = "")
    {
        /// <summary>The word for where this note stands: open, done, or asked while it waits on an answer.</summary>
        public string Standing => State.Length > 0 ? State : Done.Length > 0 ? ReportBook.Done : ReportBook.Open;

        /// <summary>Whether the note's own words hold this one.</summary>
        public bool Holds(string word) => Text.Any(line => line.Contains(word, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>Where a note stands. Anything else reads as <see cref="Open"/>.</summary>
    public const string Open = "open";
    /// <summary>Taken care of, in the version written beside it.</summary>
    public const string Done = "done";
    /// <summary>Looked at but not settled: it waits on an answer, and the reason says what is needed.</summary>
    public const string Asked = "asked";

    /// <summary>Every note with the version it was written for, where it stands and why.</summary>
    public IReadOnlyList<ReportNote> Notes()
    {
        var written = Marks(WrittenPath);
        var done = Marks(DonePath);
        var notes = new List<ReportNote>();
        foreach (var title in Titles())
        {
            var text = Read(title) ?? [];
            var (state, version, why) = Split(done.TryGetValue(title, out var mark) ? mark : "");
            notes.Add(new ReportNote(title, written.TryGetValue(title, out var at) ? at : "",
                state == Done ? version : "", text.Count, text, state, why));
        }
        return notes;
    }

    /// <summary>
    /// One mark: "state\tversion\twhy". A bare version is the 0.9.2 shape and reads as done, so older folders keep working.
    /// </summary>
    private static (string State, string Version, string Why) Split(string mark)
    {
        if (mark.Length == 0) return (Open, "", "");
        var parts = mark.Split('\t');
        if (parts[0] is not (Done or Asked or Open)) return (Done, parts[0], "");
        return (parts[0], parts.Length > 1 ? parts[1] : "", parts.Length > 2 ? parts[2] : "");
    }

    /// <summary>Where a note stands and why, or null when there is no such note.</summary>
    public (string State, string Version, string Why)? StandingOf(string title)
    {
        if (!Has(title)) return null;
        return Split(Marks(DonePath).TryGetValue(title, out var mark) ? mark : "");
    }

    /// <summary>
    /// Writes where a note stands: <see cref="Done"/> with the version it was fixed in, <see cref="Asked"/> with what is
    /// still needed, or <see cref="Open"/> to clear it. <paramref name="why"/> is written in the language the settings are
    /// set to, since the user is who reads it (user rule). False when there is no note by that title.
    ///
    /// This is the one way the mark is set, so whoever does it leaves it in the same shape.
    /// </summary>
    public bool SetState(string title, string state, string? version = null, string? why = null)
    {
        if (!Has(title)) return false;
        var done = Marks(DonePath);
        if (state == Open)
        {
            if (!done.Remove(title)) return true;
        }
        else
        {
            // Tabs separate the three, so neither one may carry one of its own.
            done[title] = string.Join('\t', state, Clean(version), Clean(why));
        }
        SaveMarks(DonePath, done);
        return true;

        static string Clean(string? text) => (text ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();
    }

    /// <summary>Marks a note taken care of, or opens it again with <paramref name="version"/> null.</summary>
    public bool SetDone(string title, string? version) =>
        SetState(title, version is { Length: > 0 } ? Done : Open, version);

    /// <summary>Removes every note that is marked done. Returns the titles that went.</summary>
    public IReadOnlyList<string> ClearDone()
    {
        var gone = new List<string>();
        foreach (var note in Notes().Where(note => note.Standing == Done))
        {
            if (Delete(note.Title)) gone.Add(note.Title);
        }
        return gone;
    }

    /// <summary>Gives a note another title. Null when it went through, otherwise why not.</summary>
    public string? Rename(string title, string name)
    {
        if (!IsTitle(title) || !IsTitle(name)) throw new ArgumentException(TitleRule, nameof(name));
        if (!Has(title)) return $"{title} was never written.";
        if (title.Equals(name, StringComparison.OrdinalIgnoreCase)) return null;
        if (Has(name)) return $"{name} is already a note.";
        File.Move(PathOf(title), PathOf(name));
        Move(WrittenPath, title, name);
        Move(DonePath, title, name);
        return null;
    }

    /// <summary>Carries a mark over to another title, or drops it when <paramref name="name"/> is null.</summary>
    private static void Move(string path, string title, string? name)
    {
        var map = Marks(path);
        if (!map.Remove(title, out var value)) return;
        if (name is { Length: > 0 }) map[name] = value;
        SaveMarks(path, map);
    }

    public IReadOnlyList<string> Titles() =>
        Directory.Exists(Root)
            ? Directory.GetFiles(Root, "*.txt").Select(Path.GetFileNameWithoutExtension).OfType<string>()
                .Where(IsTitle).OrderBy(title => title, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];

    /// <summary>The lines of one note, or null when there is none by that title.</summary>
    public IReadOnlyList<string>? Read(string title)
    {
        if (!IsTitle(title)) throw new ArgumentException(TitleRule, nameof(title));
        var path = PathOf(title);
        return File.Exists(path) ? File.ReadAllLines(path) : null;
    }

    /// <summary>
    /// Writes a note. Doing so **opens it again**: words added to a note that was done are new work for the next version
    /// (user rule), so the mark beside it goes.
    /// </summary>
    public void Write(string title, IReadOnlyList<string> lines, string? version = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (!IsTitle(title)) throw new ArgumentException(TitleRule, nameof(title));
        Directory.CreateDirectory(Root);
        var temp = Path.Combine(Root, $"report-{Guid.NewGuid():N}.tmp");
        File.WriteAllLines(temp, lines, new UTF8Encoding(false));
        File.Move(temp, PathOf(title), overwrite: true);
        // The version it was written for is kept beside the notes: the note itself is the user's text and nothing else.
        if (version is { Length: > 0 })
        {
            var written = Marks(WrittenPath);
            written[title] = version;
            SaveMarks(WrittenPath, written);
        }
        SetState(title, Open);
    }

    /// <summary>Whether a note by that title was there to remove.</summary>
    public bool Delete(string title)
    {
        if (!IsTitle(title)) throw new ArgumentException(TitleRule, nameof(title));
        var path = PathOf(title);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        Move(WrittenPath, title, null);
        Move(DonePath, title, null);
        return true;
    }

    private const string TitleRule = "A report title may only be letters, digits, spaces, dashes and underscores.";

    private bool Has(string title) => IsTitle(title) && File.Exists(PathOf(title));

    /// <summary>One of the mark files: title to value, tab separated.</summary>
    private static Dictionary<string, string> Marks(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return map;
        foreach (var line in File.ReadAllLines(path))
        {
            var tab = line.IndexOf('\t');
            if (tab > 0) map[line[..tab]] = line[(tab + 1)..];
        }
        return map;
    }

    private static void SaveMarks(string path, Dictionary<string, string> map)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, map.Where(pair => pair.Key.Length > 0).Select(pair => pair.Key + "\t" + pair.Value),
            new UTF8Encoding(false));
    }

    private string PathOf(string title) => Path.Combine(Root, title + ".txt");
}
