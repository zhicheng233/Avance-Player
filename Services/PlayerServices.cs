using System;
using Avance_Player.Services.Lyrics;
using Avance_Player.Services.Persistence;
using Avance_Player.Services.Playback;
using Avance_Player.Services.SystemMedia;

namespace Avance_Player.Services;

/// <summary>
/// 播放器的默认组合根。窗口/view model 拿这个对象，不自己去 new LibVLC、JSON store 或扫描器；
/// 测试可以塞自己的实现。
/// </summary>
public sealed class PlayerServices : IDisposable {
    private bool _disposed;

    public PlayerServices(
        PlaybackService playback,
        IPlayerStateStore stateStore,
        DebouncedStateSaver stateSaver,
        LocalFolderScanner scanner,
        LibraryRestorer libraryRestorer,
        LyricsService lyrics,
        SystemMediaService? systemMedia = null,
        SystemMediaArtworkCache? systemMediaArtwork = null) {
        Playback = playback;
        StateStore = stateStore;
        StateSaver = stateSaver;
        Scanner = scanner;
        LibraryRestorer = libraryRestorer;
        Lyrics = lyrics;
        SystemMedia = systemMedia ?? SystemMediaService.CreateNull();
        SystemMediaArtwork = systemMediaArtwork ?? new SystemMediaArtworkCache();
    }

    public PlaybackService Playback { get; }
    public IPlayerStateStore StateStore { get; }
    public DebouncedStateSaver StateSaver { get; }
    public LocalFolderScanner Scanner { get; }
    public LibraryRestorer LibraryRestorer { get; }
    public LyricsService Lyrics { get; }
    public SystemMediaService SystemMedia { get; }
    public SystemMediaArtworkCache SystemMediaArtwork { get; }

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
        var systemMedia = SystemMediaService.CreateDefault();
        return new PlayerServices(playback, stateStore, stateSaver, scanner, libraryRestorer, lyrics, systemMedia);
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        StateSaver.Dispose();
        Playback.Dispose();
        SystemMedia.Dispose();
    }
}
