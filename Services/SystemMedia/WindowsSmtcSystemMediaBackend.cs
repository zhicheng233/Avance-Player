#if WINDOWS
using System;
using System.IO;
using System.Threading.Tasks;
using LibSMTC;
using Windows.Media;

namespace Avance_Player.Services.SystemMedia;

/// <summary>
/// Windows System Media Transport Controls 集成。这个文件只在 Windows 目标下编译
/// （Windows 构建时项目会切到 Windows TFM），所以 Linux/macOS 构建不需要 Windows SDK 投影。
/// </summary>
public sealed class WindowsSmtcSystemMediaBackend : ISystemMediaBackend {
    private readonly LibSmtcController? _controller;
    private string? _artworkPath;
    private string? _lastTitle;
    private string? _lastArtist;
    private string? _lastAlbum;
    private bool? _lastPlaying;
    private bool? _lastShuffle;
    private SystemMediaRepeatMode? _lastRepeat;
    private DateTime _lastTimelineUtc = DateTime.MinValue;
    private bool _disposed;

    public WindowsSmtcSystemMediaBackend() {
        try {
            _controller = new LibSmtcController { IsEnabled = true };
            _controller.SetButtons(new SmtcButtonOptions {
                PlayEnabled = true,
                PauseEnabled = true,
                StopEnabled = true,
                NextEnabled = true,
                PreviousEnabled = true
            });
            _controller.PlayRequested += (_, _) => Emit(SystemMediaCommandKind.Play);
            _controller.PauseRequested += (_, _) => Emit(SystemMediaCommandKind.Pause);
            _controller.StopRequested += (_, _) => Emit(SystemMediaCommandKind.Stop);
            _controller.NextRequested += (_, _) => Emit(SystemMediaCommandKind.Next);
            _controller.PreviousRequested += (_, _) => Emit(SystemMediaCommandKind.Previous);
            _controller.SeekRequested += (_, args) => Emit(new SystemMediaCommand(SystemMediaCommandKind.Seek, args.Position));
            _controller.ShuffleChangeRequested += (_, args) => Emit(new SystemMediaCommand(SystemMediaCommandKind.Shuffle, Flag: args.ShuffleEnabled));
            _controller.RepeatModeChangeRequested += (_, args) => Emit(new SystemMediaCommand(
                SystemMediaCommandKind.Repeat,
                Repeat: args.RepeatMode switch {
                    MediaPlaybackAutoRepeatMode.Track => SystemMediaRepeatMode.Track,
                    MediaPlaybackAutoRepeatMode.List => SystemMediaRepeatMode.Playlist,
                    _ => SystemMediaRepeatMode.Off
                }));
        }
        catch (Exception) {
            _controller = null;
        }
    }

    public bool IsAvailable => !_disposed && _controller?.IsEnabled == true;
    public event Action<SystemMediaCommand>? CommandReceived;

    public void Update(SystemMediaSnapshot snapshot) {
        if (_disposed || _controller is null) return;

        try {
            if (!string.Equals(_lastTitle, snapshot.Title, StringComparison.Ordinal)
                || !string.Equals(_lastArtist, snapshot.Artist, StringComparison.Ordinal)
                || !string.Equals(_lastAlbum, snapshot.Album, StringComparison.Ordinal)) {
                _lastTitle = snapshot.Title;
                _lastArtist = snapshot.Artist;
                _lastAlbum = snapshot.Album;
                _controller.SetMusicMetadata(
                    snapshot.Title,
                    snapshot.Artist,
                    snapshot.Album,
                    snapshot.Artist,
                    0,
                    0,
                    Array.Empty<string>());
            }

            if (!string.Equals(_artworkPath, snapshot.ArtworkPath, StringComparison.Ordinal)) {
                _artworkPath = snapshot.ArtworkPath;
                if (!string.IsNullOrWhiteSpace(snapshot.ArtworkPath) && File.Exists(snapshot.ArtworkPath))
                    _ = SetThumbnailAsync(snapshot.ArtworkPath);
                else
                    _controller.ClearThumbnail();
            }

            if (DateTime.UtcNow - _lastTimelineUtc >= TimeSpan.FromSeconds(1)) {
                _lastTimelineUtc = DateTime.UtcNow;
                _controller.SetTimeline(snapshot.Position, snapshot.Duration);
            }

            if (_lastShuffle != snapshot.IsShuffle) {
                _lastShuffle = snapshot.IsShuffle;
                _controller.IsShuffleActive = snapshot.IsShuffle;
            }

            if (_lastRepeat != snapshot.RepeatMode) {
                _lastRepeat = snapshot.RepeatMode;
                _controller.RepeatMode = snapshot.RepeatMode switch {
                    SystemMediaRepeatMode.Track => MediaPlaybackAutoRepeatMode.Track,
                    SystemMediaRepeatMode.Playlist => MediaPlaybackAutoRepeatMode.List,
                    _ => MediaPlaybackAutoRepeatMode.None
                };
            }

            if (_lastPlaying != snapshot.IsPlaying) {
                _lastPlaying = snapshot.IsPlaying;
                if (snapshot.IsPlaying) _controller.Play();
                else _controller.Pause();
            }
        }
        catch (Exception) {
            // shell 重启时 SMTC 会话可能会没。
        }
    }

    public void Clear() {
        if (_disposed || _controller is null) return;
        try {
            _controller.Clear();
            _controller.Stop();
            _lastTitle = null;
            _lastArtist = null;
            _lastAlbum = null;
            _lastPlaying = null;
            _lastShuffle = null;
            _lastRepeat = null;
            _artworkPath = null;
            _lastTimelineUtc = DateTime.MinValue;
        }
        catch (Exception) {
            // 忽略关闭时的竞态。
        }
    }

    private async Task SetThumbnailAsync(string path) {
        try {
            await _controller!.SetThumbnailFromFileAsync(path).ConfigureAwait(false);
        }
        catch (Exception) {
            // 不支持的图片格式，或者 shell 正在关。
        }
    }

    private void Emit(SystemMediaCommandKind kind) =>
        Emit(new SystemMediaCommand(kind));

    private void Emit(SystemMediaCommand command) {
        if (!_disposed) CommandReceived?.Invoke(command);
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        try {
            _controller?.Dispose();
        }
        catch (Exception) {
            // 忽略关闭时的竞态。
        }
    }
}
#endif
