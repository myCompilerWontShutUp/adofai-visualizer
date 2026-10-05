namespace Visualizer.Core.Settings;

/// <summary>
/// What the song list is sorted by. Only values the cache or the map file already holds; the order here is the order the
/// settings list shows. "Manual" is the order set by dragging in the song list window.
/// </summary>
public enum SongSortField { Difficulty, Title, Artist, Author, NoteCount, Duration, Created, Modified, Synced, Manual }

/// <summary>
/// Which of the app's map folders the song list shows. The order here is the order the settings list shows: each folder
/// on its own first, then the pairs, then everything. The default is base and custom together, since an edit is picked
/// by its slot on the main screen rather than as a song of its own (user rule).
/// </summary>
public enum SongGroupField { Base, Custom, Edited, BaseCustom, CustomEdited, BaseEdited, All }
