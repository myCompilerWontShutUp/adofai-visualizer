namespace Visualizer.Application.Library;

/// <summary>
/// The short ids the console shows: eight characters whose first is the digit of the folder the song is in (0 base, 1 custom,
/// 2 edited) and whose other seven are lower-case letters and digits with at least one letter, so ids repeat less and never
/// read as a number. They are only a handle for typing; the app itself keeps identifying songs by their path hash, so a song
/// keeps its id as long as it stays where it is. An id of the old all-digit form no longer matches, so it is replaced the
/// first time round.
/// </summary>
public static class SongIds
{
    public const int Length = 8;
    private const string Letters = "abcdefghijklmnopqrstuvwxyz";
    private const string Characters = Letters + "0123456789";

    /// <summary>
    /// Gives every song an id, keeping the one it already has when that still matches its folder. Ids never repeat.
    /// <paramref name="songs"/> is (cache id, first digit); entries of songs that are gone are kept, so a song that comes
    /// back to the same place gets the same id again.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Assign(IEnumerable<(string CacheId, int Digit)> songs,
        IReadOnlyDictionary<string, string>? known = null, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(songs);
        var dice = random ?? Random.Shared;
        var result = known is null ? [] : new Dictionary<string, string>(known, StringComparer.Ordinal);
        var taken = new HashSet<string>(result.Values, StringComparer.Ordinal);
        foreach (var (cacheId, digit) in songs)
        {
            if (string.IsNullOrWhiteSpace(cacheId) || digit is < 0 or > 9) continue;
            if (result.TryGetValue(cacheId, out var current) && Matches(current, digit)) continue;
            if (current is not null) taken.Remove(current);
            string id;
            do { id = digit.ToString(System.Globalization.CultureInfo.InvariantCulture) + Tail(dice); }
            while (!taken.Add(id));
            result[cacheId] = id;
        }
        return result;
    }

    /// <summary>Whether an id has the current form and starts with the folder's digit.</summary>
    public static bool Matches(string? id, int digit) =>
        id is { Length: Length } && id[0] == (char)('0' + digit) && id[1..].All(Characters.Contains) && id[1..].Any(Letters.Contains);

    /// <summary>Whether a word could be an id: eight letters and digits after a folder digit, in any letter case.</summary>
    public static bool LooksLikeId(string? word) =>
        word is { Length: Length } && word[0] is >= '0' and <= '2' && word.All(char.IsAsciiLetterOrDigit);

    private static string Tail(Random dice)
    {
        var tail = Enumerable.Range(0, Length - 1).Select(_ => Characters[dice.Next(Characters.Length)]).ToArray();
        // At least one letter, so an id never reads as a number.
        if (!tail.Any(Letters.Contains)) tail[dice.Next(tail.Length)] = Letters[dice.Next(Letters.Length)];
        return new string(tail);
    }
}
