using System;
using Avance_Player.Services.Lyrics;
using Avance_Player.Services.Persistence;
using Avance_Player.Services.Playback;

namespace Avance_Player.Services;

/// <summary>
/// Default composition root for the player. The window/view model receives this object instead of
/// constructing LibVLC, JSON stores or scanners directly; tests can provide their own instances.
/// </summary>
public sealed class PlayerServices : IDisposable {
    private bool _disposed;

    public PlayerServices(
        PlaybackService playback,
        IPlayerStateStore stateStore,
        DebouncedStateSaver stateSaver,
        LocalFolderScanner scanner,
        LibraryRestorer libraryRestorer,
        LyricsService lyrics) {
        Playback = playback;
        StateStore = stateStore;
        StateSaver = stateSaver;
        Scanner = scanner;
        LibraryRestorer = libraryRestorer;
        Lyrics = lyrics;
    }

    public PlaybackService Playback { get; }
    public IPlayerStateStore StateStore { get; }
    public DebouncedStateSaver StateSaver { get; }
    public LocalFolderScanner Scanner { get; }
    public LibraryRestorer LibraryRestorer { get; }
    public LyricsService Lyrics { get; }

    public static PlayerServices CreateDefault(string? statePath = null) {
        var lyrics = new LyricsService();
        var scanner = new LocalFolderScanner(lyrics);
        var local = new LibVlcAudioBackend();
        var simulated = new SimulatedAudioBackend();
        var switchable = new SwitchableAudioBackend(local, simulated);
        var playback = new PlaybackService(switchable);
        var stateStore = new JsonPlayerStateStore(statePath);
        var stateSaver = new DebouncedStateSaver(stateStore);
        var libraryRestorer = new LibraryRestorer(scanner, lyrics);
        return new PlayerServices(playback, stateStore, stateSaver, scanner, libraryRestorer, lyrics);
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        StateSaver.Dispose();
        Playback.Dispose();
    }
}
