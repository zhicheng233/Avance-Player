using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avance_Player.Models;
using Avance_Player.Services.Lyrics;
using TagFile = TagLib.File;

namespace Avance_Player.Services;

public sealed record LocalFolderScanLabels(string UnknownArtist, string UnknownAlbum, string UnknownGenre);

public sealed record LocalFolderScanResult(
    IReadOnlyList<Music> Tracks,
    int AudioFileCount,
    int SkippedFileCount,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 递归扫文件夹里的音频，顺便配同目录的封面和歌词。标签和内嵌封面走 TagLib#；
/// 这个服务不解码也不放声音。
/// </summary>
public sealed class LocalFolderScanner {
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase) {
        ".mp3", ".flac", ".m4a", ".aac", ".wav", ".ogg", ".opus", ".wma",
        ".ape", ".alac", ".aiff", ".aif", ".dsf", ".dff", ".mka"
    };

    private static readonly string[] CoverExtensions = [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif"];
    private static readonly string[] CoverPriority = ["cover", "folder", "front", "album", "artwork"];

    // 没有可读封面时的兜底色块。有封面的歌用采样出来的主色，palette 会跟着专辑图走。
    private static readonly string[] ArtworkColors = [
        "#5F7F72", "#3F7A86", "#B3785A", "#B66F6B", "#A88E5E", "#5C8471", "#4B7B84", "#7C6A8A"
    ];

    private static readonly Regex LeadingTrackNumber = new(@"^\s*\d{1,3}\s*[-._ ]\s*(.+)$", RegexOptions.Compiled);

    private readonly LyricsService _lyricsService;

    public LocalFolderScanner(LyricsService? lyricsService = null) {
        _lyricsService = lyricsService ?? new LyricsService();
    }

    public async Task<LocalFolderScanResult> ScanAsync(
        string rootFolder,
        LocalFolderScanLabels labels,
        CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder)) {
            throw new DirectoryNotFoundException(rootFolder);
        }

        var audioFiles = EnumerateAudioFiles(rootFolder);
        var filesByDirectory = audioFiles
            .GroupBy(path => Path.GetDirectoryName(path) ?? rootFolder, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var coverPathCache = new Dictionary<string, string?>(StringComparer.Ordinal);
        var coverBitmapCache = new Dictionary<string, Bitmap?>(StringComparer.Ordinal);
        var coverPaletteCache = new Dictionary<string, ArtworkPalette?>(StringComparer.Ordinal);
        var tracks = new List<Music>(audioFiles.Count);
        var warnings = new List<string>();

        foreach (var path in audioFiles) {
            cancellationToken.ThrowIfCancellationRequested();
            try {
                var directory = Path.GetDirectoryName(path) ?? rootFolder;
                var siblingCount = filesByDirectory.TryGetValue(directory, out var siblings) ? siblings.Count : 1;
                var track = await CreateTrackAsync(
                    path,
                    directory,
                    siblingCount,
                    labels,
                    coverPathCache,
                    coverBitmapCache,
                    coverPaletteCache,
                    warnings,
                    cancellationToken).ConfigureAwait(false);
                if (track is not null) tracks.Add(track);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException) {
                warnings.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }

        return new LocalFolderScanResult(tracks, audioFiles.Count, audioFiles.Count - tracks.Count, warnings);
    }

    /// <summary>
    /// 按给定顺序加载已知路径。恢复曲库时用；跟 <see cref="ScanAsync"/> 不一样，
    /// 它不会递归枚举目录，上次运行之后新加的文件不会被顺手导进来。
    /// </summary>
    public async Task<LocalFolderScanResult> LoadTracksAsync(
        IReadOnlyList<string> paths,
        LocalFolderScanLabels labels,
        CancellationToken cancellationToken = default) {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        var requested = new List<string>(paths.Count);
        foreach (var path in paths) {
            if (string.IsNullOrWhiteSpace(path) || !seen.Add(path)) continue;
            try {
                if (!AudioExtensions.Contains(Path.GetExtension(path))) continue;
                if (!File.Exists(path)) continue;
                requested.Add(path);
            }
            catch (Exception exception) when (exception is ArgumentException
                                                  or NotSupportedException
                                                  or PathTooLongException) {
                // 存坏的路径直接跳过，别让整个恢复失败。
            }
        }

        var coverPathCache = new Dictionary<string, string?>(comparer);
        var coverBitmapCache = new Dictionary<string, Bitmap?>(comparer);
        var coverPaletteCache = new Dictionary<string, ArtworkPalette?>(comparer);
        var siblingCountCache = new Dictionary<string, int>(comparer);
        var tracks = new List<Music>(requested.Count);
        var warnings = new List<string>();

        foreach (var path in requested) {
            cancellationToken.ThrowIfCancellationRequested();
            try {
                var directory = Path.GetDirectoryName(path) ?? string.Empty;
                if (!siblingCountCache.TryGetValue(directory, out var siblingCount)) {
                    siblingCount = CountAudioFilesInDirectory(directory);
                    siblingCountCache[directory] = siblingCount;
                }

                var track = await CreateTrackAsync(
                    path,
                    directory,
                    siblingCount,
                    labels,
                    coverPathCache,
                    coverBitmapCache,
                    coverPaletteCache,
                    warnings,
                    cancellationToken).ConfigureAwait(false);
                if (track is not null) tracks.Add(track);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException) {
                warnings.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }

        return new LocalFolderScanResult(tracks, requested.Count, requested.Count - tracks.Count, warnings);
    }

    private static int CountAudioFilesInDirectory(string directory) {
        if (string.IsNullOrWhiteSpace(directory)) return 1;

        try {
            var count = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Count(path => AudioExtensions.Contains(Path.GetExtension(path)));
            return Math.Max(1, count);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException) {
            return 1;
        }
    }

    private static List<string> EnumerateAudioFiles(string rootFolder) {
        var options = new EnumerationOptions {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            // 跳过符号链接目录，免得链接成环，扫描跑不完。
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
        };

        try {
            return Directory.EnumerateFiles(rootFolder, "*", options)
                .Where(path => AudioExtensions.Contains(Path.GetExtension(path)))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException) {
            return [];
        }
    }

    private async Task<Music?> CreateTrackAsync(
        string path,
        string directory,
        int siblingAudioCount,
        LocalFolderScanLabels labels,
        Dictionary<string, string?> coverPathCache,
        Dictionary<string, Bitmap?> coverBitmapCache,
        Dictionary<string, ArtworkPalette?> coverPaletteCache,
        List<string> warnings,
        CancellationToken cancellationToken) {
        TagFile? tagFile = null;
        try {
            try {
                tagFile = TagFile.Create(path);
            }
            catch (Exception exception) when (exception is TagLib.CorruptFileException
                                                  or TagLib.UnsupportedFormatException
                                                  or IOException
                                                  or UnauthorizedAccessException) {
                warnings.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }

            var title = FirstNonEmpty(tagFile?.Tag.Title, CleanFallbackTitle(path));
            var artist = FirstNonEmpty(tagFile?.Tag.FirstPerformer, tagFile?.Tag.FirstAlbumArtist, labels.UnknownArtist);
            var album = FirstNonEmpty(tagFile?.Tag.Album, Path.GetFileName(directory), labels.UnknownAlbum);
            var genre = FirstNonEmpty(tagFile?.Tag.FirstGenre, labels.UnknownGenre);
            var duration = tagFile?.Properties.Duration ?? TimeSpan.Zero;
            if (duration <= TimeSpan.Zero) duration = TimeSpan.FromSeconds(210);
            var bitrate = tagFile?.Properties.AudioBitrate ?? 0;

            var coverPath = FindCoverPath(directory, coverPathCache);
            var artwork = coverPath is null ? null : LoadBitmap(coverPath, coverBitmapCache);
            // 同目录封面真的解码成功才把路径报出去。不然坏封面会把内嵌图挡住，
            // 系统媒体控制那边就没图了。
            var artworkPath = artwork is not null ? coverPath : null;
            if (artwork is null) artwork = LoadEmbeddedArtwork(tagFile);

            ArtworkPalette? coverPalette = null;
            if (artwork is not null) {
                if (artworkPath is null) {
                    coverPalette = ArtworkPaletteExtractor.Extract(artwork);
                }
                else if (!coverPaletteCache.TryGetValue(artworkPath, out coverPalette)) {
                    coverPalette = ArtworkPaletteExtractor.Extract(artwork);
                    coverPaletteCache[artworkPath] = coverPalette;
                }
            }

            var (lyrics, lyricsPath) = await TryLoadSameBaseLyricsAsync(path, duration, warnings, cancellationToken)
                .ConfigureAwait(false);

            // 只有一首歌的文件夹，歌词经常按专辑名起（"lyrics.lrc"），不跟歌名走。
            // 只有单个音频文件时才用这个兜底，免得把别人的词挂上来。
            if (lyrics is null && siblingAudioCount == 1) {
                (lyrics, lyricsPath) = await TryLoadLooseLyricsAsync(directory, duration, warnings, cancellationToken)
                    .ConfigureAwait(false);
            }

            var track = new Music {
                Title = title,
                Artist = artist,
                Album = album,
                Genre = genre,
                Duration = duration,
                ArtworkColor = coverPalette?.DominantColor ?? PickArtworkColor(path),
                FilePath = path,
                BitrateKbps = bitrate,
                Artwork = artwork,
                ArtworkPath = artworkPath,
                ArtworkAccentColor = coverPalette?.AccentColor,
                ArtworkSecondaryColor = coverPalette?.SecondaryColor,
                ArtworkBackgroundColor = coverPalette?.BackgroundColor
            };

            if (lyrics is not null) {
                track.Lyrics = lyrics.Lines;
                track.LyricsFormat = lyrics.Format;
                track.LyricsSourcePath = lyricsPath;
            }

            return track;
        }
        finally {
            tagFile?.Dispose();
        }
    }

    private static string CleanFallbackTitle(string path) {
        var name = Path.GetFileNameWithoutExtension(path);
        var match = LeadingTrackNumber.Match(name);
        return match.Success ? match.Groups[1].Value.Trim() : name;
    }

    private static string FirstNonEmpty(params string?[] values) {
        foreach (var value in values) {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return string.Empty;
    }

    private static string PickArtworkColor(string path) {
        // FNV-1a 跨进程稳定，string.GetHashCode 不是。
        var hash = 2166136261u;
        foreach (var character in path) {
            hash ^= character;
            hash *= 16777619u;
        }
        return ArtworkColors[hash % (uint)ArtworkColors.Length];
    }

    private static string? FindCoverPath(string directory, Dictionary<string, string?> cache) {
        if (cache.TryGetValue(directory, out var cached)) return cached;

        string? result = null;
        try {
            var images = Directory.EnumerateFiles(directory)
                .Where(path => CoverExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            foreach (var preferredName in CoverPriority) {
                result = images.FirstOrDefault(path =>
                    string.Equals(Path.GetFileNameWithoutExtension(path), preferredName, StringComparison.OrdinalIgnoreCase));
                if (result is not null) break;
            }

            result ??= images.FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException) {
            result = null;
        }

        cache[directory] = result;
        return result;
    }

    private static Bitmap? LoadBitmap(string path, Dictionary<string, Bitmap?> cache) {
        if (cache.TryGetValue(path, out var cached)) return cached;

        Bitmap? bitmap = null;
        try {
            bitmap = new Bitmap(path);
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or ArgumentException
                                              or NotSupportedException) {
            bitmap = null;
        }

        cache[path] = bitmap;
        return bitmap;
    }

    private static Bitmap? LoadEmbeddedArtwork(TagFile? tagFile) {
        var pictures = tagFile?.Tag.Pictures;
        if (pictures is null || pictures.Length == 0) return null;

        var data = pictures[0].Data?.Data;
        if (data is null || data.Length == 0) return null;

        try {
            using var stream = new MemoryStream(data);
            return new Bitmap(stream);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException) {
            return null;
        }
    }

    private async Task<(LyricsDocument? Document, string? Path)> TryLoadLooseLyricsAsync(
        string directory,
        TimeSpan duration,
        List<string> warnings,
        CancellationToken cancellationToken) {
        List<string> candidates;
        try {
            candidates = Directory.EnumerateFiles(directory)
                .Where(path => LyricsService.SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .OrderBy(path => LyricsPriority(Path.GetExtension(path)))
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException) {
            return (null, null);
        }

        foreach (var candidate in candidates) {
            try {
                var document = await _lyricsService.LoadAsync(candidate, duration, cancellationToken).ConfigureAwait(false);
                return (document, candidate);
            }
            catch (LyricsParseException exception) {
                warnings.Add($"{Path.GetFileName(candidate)}: {exception.Message}");
            }
        }

        return (null, null);
    }

    private async Task<(LyricsDocument? Document, string? Path)> TryLoadSameBaseLyricsAsync(
        string audioPath,
        TimeSpan duration,
        List<string> warnings,
        CancellationToken cancellationToken) {
        var directory = Path.GetDirectoryName(audioPath);
        var baseName = Path.GetFileNameWithoutExtension(audioPath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(baseName)) return (null, null);

        List<string> candidates;
        try {
            candidates = Directory.EnumerateFiles(directory)
                .Where(path => string.Equals(Path.GetFileNameWithoutExtension(path), baseName, StringComparison.OrdinalIgnoreCase))
                .Where(path => LyricsService.SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .OrderBy(path => LyricsPriority(Path.GetExtension(path)))
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException) {
            return (null, null);
        }

        foreach (var candidate in candidates) {
            try {
                var document = await _lyricsService.LoadAsync(candidate, duration, cancellationToken).ConfigureAwait(false);
                return (document, candidate);
            }
            catch (LyricsParseException exception) {
                warnings.Add($"{Path.GetFileName(candidate)}: {exception.Message}");
            }
        }

        return (null, null);
    }

    private static int LyricsPriority(string extension) {
        for (var index = 0; index < LyricsService.SupportedExtensions.Count; index++) {
            if (string.Equals(LyricsService.SupportedExtensions[index], extension, StringComparison.OrdinalIgnoreCase)) {
                return index;
            }
        }
        return int.MaxValue;
    }
}
