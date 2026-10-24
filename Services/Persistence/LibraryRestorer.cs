using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avance_Player.Models;
using Avance_Player.Services.Lyrics;

namespace Avance_Player.Services.Persistence;

public sealed record RestoredLibrary(
    IReadOnlyList<Music> Tracks,
    IReadOnlyDictionary<string, Music> ByPath,
    IReadOnlyList<string> MissingPaths);

/// <summary>
/// 按保存的绝对路径重新读标签、封面和歌词，把曲库和歌单状态搭回来。
/// 老版本留下的相对 demo 路径直接忽略。
/// </summary>
public sealed class LibraryRestorer {
    private readonly LocalFolderScanner _scanner;
    private readonly LyricsService _lyrics;

    public LibraryRestorer(LocalFolderScanner scanner, LyricsService? lyrics = null) {
        _scanner = scanner;
        _lyrics = lyrics ?? new LyricsService();
    }

    public async Task<RestoredLibrary> RestoreAsync(
        PlayerState state,
        IReadOnlyList<Music> knownTracks,
        LocalFolderScanLabels labels,
        CancellationToken cancellationToken = default) {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        var byPath = new Dictionary<string, Music>(comparer);
        foreach (var track in knownTracks) {
            if (!string.IsNullOrWhiteSpace(track.FilePath)) byPath.TryAdd(track.FilePath, track);
        }

        var allPaths = new List<string>();
        var seen = new HashSet<string>(comparer);
        foreach (var path in state.Library.TrackPaths ?? []) {
            if (!string.IsNullOrWhiteSpace(path) && seen.Add(path)) allPaths.Add(path);
        }

        foreach (var playlist in state.Library.Playlists ?? []) {
            if (playlist is null) continue;
            foreach (var path in playlist.TrackPaths ?? []) {
                if (!string.IsNullOrWhiteSpace(path) && seen.Add(path)) allPaths.Add(path);
            }
        }

        var localPaths = new List<string>();
        var missing = new List<string>();
        foreach (var path in allPaths) {
            if (byPath.ContainsKey(path)) continue;

            bool absolute;
            try {
                absolute = Path.IsPathFullyQualified(path);
            }
            catch (Exception) {
                absolute = false;
            }

            if (!absolute) {
                // 老版本会塞相对 demo 路径。这些歌已经不在曲库里了，悄悄跳过，
                // 别报一堆文件丢失。
                continue;
            }

            if (!File.Exists(path)) {
                missing.Add(path);
                continue;
            }

            localPaths.Add(path);
        }

        var result = await _scanner.LoadTracksAsync(localPaths, labels, cancellationToken).ConfigureAwait(false);
        foreach (var track in result.Tracks) {
            if (!string.IsNullOrWhiteSpace(track.FilePath)) byPath[track.FilePath] = track;
        }

        foreach (var path in localPaths) {
            if (!byPath.ContainsKey(path)) missing.Add(path);
        }

        await ApplySavedLyricsAsync(state, byPath, cancellationToken).ConfigureAwait(false);
        return new RestoredLibrary(result.Tracks, byPath, missing);
    }

    /// <summary>
    /// 把用户从音频目录外手动选的歌词文件重新挂上。sidecar 扫描器已经管了；
    /// 这层只管显式路径。歌词文件丢了或者读不了就跳过，别影响整个曲库恢复。
    /// </summary>
    private async Task ApplySavedLyricsAsync(
        PlayerState state,
        Dictionary<string, Music> byPath,
        CancellationToken cancellationToken) {
        if (state.Library.LyricPaths is not { Count: > 0 } savedPaths) return;

        foreach (var pair in savedPaths) {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) continue;
            if (!byPath.TryGetValue(pair.Key, out var track)) continue;

            var lyricsPath = pair.Value;
            try {
                if (!Path.IsPathFullyQualified(lyricsPath) || !File.Exists(lyricsPath)) continue;
            }
            catch (Exception) {
                continue;
            }

            try {
                var document = await _lyrics.LoadAsync(lyricsPath, track.Duration, cancellationToken)
                    .ConfigureAwait(false);
                track.Lyrics = document.Lines;
                track.LyricsFormat = document.Format;
                track.LyricsSourcePath = lyricsPath;
                track.HasManualLyrics = true;
            }
            catch (OperationCanceledException) {
                throw;
            }
            catch (LyricsParseException) {
                // 扫描器已经挂上的 sidecar 歌词留着。
            }
            catch (Exception) {
                // 手动歌词读不了不能把整个恢复搞失败。
            }
        }
    }
}
