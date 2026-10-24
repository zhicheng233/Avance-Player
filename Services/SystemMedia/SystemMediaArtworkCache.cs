using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media.Imaging;
using Avance_Player.Models;

namespace Avance_Player.Services.SystemMedia;

/// <summary>
/// 给系统媒体控制一个能读的当前封面路径。sidecar 的 JPEG/PNG 可以直接用；
/// 内嵌封面和其他图片格式会转成一张小 PNG 缓存，这样 MPRIS、SMTC、Now Playing
/// 都能拿到能读的图。
/// </summary>
public sealed class SystemMediaArtworkCache {
    private static readonly HashSet<string> DirectFormats = new(StringComparer.OrdinalIgnoreCase) {
        ".jpg", ".jpeg", ".png"
    };

    private readonly object _sync = new();
    private readonly Dictionary<string, string?> _paths = new(StringComparer.Ordinal);
    private readonly string _directory;

    public SystemMediaArtworkCache(string? directory = null) {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AvancePlayer",
            "artwork-cache");
    }

    /// <summary>
    /// 返回系统能读的当前歌曲封面路径；没有真封面就返回 null。
    /// 结果按歌曲/文件版本缓存，100ms 一次的媒体快照轮询才不会太贵。
    /// </summary>
    public string? GetPath(Music track) {
        var sidecar = track.ArtworkPath;
        if (IsDirectFile(sidecar)) return sidecar;

        var artwork = track.Artwork;
        if (artwork is null) return null;

        var key = CacheKey(track);
        lock (_sync) {
            if (_paths.TryGetValue(key, out var cached) && cached is not null && File.Exists(cached))
                return cached;

            var path = WritePng(artwork, key);
            _paths[key] = path;
            return path;
        }
    }

    private static bool IsDirectFile(string? path) {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try {
            return File.Exists(path) && DirectFormats.Contains(Path.GetExtension(path));
        }
        catch (Exception) {
            return false;
        }
    }

    private static string CacheKey(Music track) {
        if (!string.IsNullOrWhiteSpace(track.FilePath)) {
            try {
                var info = new FileInfo(track.FilePath);
                if (info.Exists) {
                    return $"{track.FilePath}\u001f{info.Length}\u001f{info.LastWriteTimeUtc.Ticks}";
                }
            }
            catch (Exception) {
                // 文件没了或者读不了，就往下走 metadata 里的 key。
            }
        }

        return $"{track.Title}\u001f{track.Artist}\u001f{track.Album}";
    }

    private string? WritePng(Bitmap artwork, string key) {
        try {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, $"{Fnv1a(key):x16}.png");
            using var stream = File.Create(path);
            artwork.Save(stream, PngBitmapEncoderOptions.Default);
            return path;
        }
        catch (Exception) {
            // 封面是可选的；缓存写失败不能把播放或者系统控制搞挂。
            return null;
        }
    }

    private static ulong Fnv1a(string value) {
        var hash = 14695981039346656037UL;
        foreach (var character in value) {
            hash ^= character;
            hash *= 1099511628211UL;
        }
        return hash;
    }
}
