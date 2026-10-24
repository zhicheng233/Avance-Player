using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;

namespace Avance_Player.Models;

public sealed class Music : INotifyPropertyChanged {
    private bool _isCurrent;
    private IReadOnlyList<LyricLine> _lyrics = [];

    public required string Title { get; init; }
    public required string Artist { get; init; }
    public required string Album { get; init; }
    public required string Genre { get; init; }
    public required string ArtworkColor { get; init; }
    public required TimeSpan Duration { get; init; }
    public string DurationLabel => Duration.ToString(@"m\:ss");
    public string FilePath { get; init; } = string.Empty;

    /// <summary>导入的绝对路径为 true；老的 demo 源/测试用的相对路径是 false。</summary>
    public bool IsLocalFile {
        get {
            if (string.IsNullOrWhiteSpace(FilePath)) return false;
            try {
                return Path.IsPathFullyQualified(FilePath);
            }
            catch (Exception) {
                return false;
            }
        }
    }

    /// <summary>导入的文件，而且现在还在磁盘上。</summary>
    public bool HasPlayableFile => IsLocalFile && File.Exists(FilePath);

    /// <summary>
    /// 封面，来自同目录图片或音频文件内嵌图。没封面的歌这里是 null，
    /// 退回 <see cref="ArtworkColor"/>。
    /// </summary>
    public Bitmap? Artwork { get; init; }

    /// <summary>同目录封面的路径。只有图片真的解码成功才设。</summary>
    public string? ArtworkPath { get; init; }

    /// <summary>
    /// 从封面取的主色/强调色/次色，给沉浸歌词的流体背景用。
    /// 没封面的歌就是 null，退回 UI 那套灰调。
    /// </summary>
    public string? ArtworkAccentColor { get; init; }
    public string? ArtworkSecondaryColor { get; init; }
    public string? ArtworkBackgroundColor { get; init; }

    /// <summary>本地文件码率，kbps，用来推音质档位标签。</summary>
    public int BitrateKbps { get; init; } = 1411;
    public string BitrateLabel => $"{BitrateKbps} kbps";

    /// <summary>
    /// 无损档位按大家习惯的载体叫，跟本地播放器标文件的方式一致，
    /// 不是流媒体那种音质切换。
    /// </summary>
    public string QualityTier => BitrateKbps switch {
        >= 2300 => "Hi-Res",
        >= 1450 => "DVD",
        >= 1000 => "CD",
        >= 320 => "HQ",
        _ => "Standard"
    };

    public string LocalQualityLabel => $"{BitrateLabel} · {QualityTier}";

    /// <summary>
    /// 歌词可以换：真实曲库会在歌曲对象建好之后再挂 sidecar。
    /// setter 要通知 <c>CurrentTrack.Lyrics</c> 这类绑定。
    /// </summary>
    public IReadOnlyList<LyricLine> Lyrics {
        get => _lyrics;
        set {
            if (ReferenceEquals(_lyrics, value)) return;
            _lyrics = value;
            OnPropertyChanged();
        }
    }

    /// <summary>当前挂上的歌词格式，前提是从文件解析来的。</summary>
    public LyricsFormat? LyricsFormat { get; set; }

    /// <summary>原始文件名/路径，加载状态提示用。</summary>
    public string? LyricsSourcePath { get; set; }

    /// <summary>
    /// 用户自己选过歌词文件才为 true。只存这些路径；sidecar 歌词交给扫描器重新找，
    /// 重启后新的 sidecar 还能顶上来。
    /// </summary>
    public bool HasManualLyrics { get; set; }

    /// <summary>播放器设的，队列行靠它高亮当前歌曲。</summary>
    public bool IsCurrent {
        get => _isCurrent;
        set {
            if (_isCurrent == value) return;
            _isCurrent = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
