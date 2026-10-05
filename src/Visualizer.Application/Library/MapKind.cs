namespace Visualizer.Application.Library;

/// <summary>
/// Which of the app's map folders a song is in. <see cref="External"/> is anything outside the app store, such as the
/// Steam folder, which the app only ever reads. The numbers are the first digit of the song's short id.
/// </summary>
public enum MapKind { External = -1, Base = 0, Custom = 1, Edited = 2 }
