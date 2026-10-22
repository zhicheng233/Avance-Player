using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avance_Player.Models;

namespace Avance_Player.Services.Persistence;

public sealed record RestoredLibrary(
    IReadOnlyList<Music> Tracks,
    IReadOnlyDictionary<string, Music> ByPath,
    IReadOnlyList<string> MissingPaths);

/// <summary>
/// Rebuilds the path-based library and playlist state by re-reading metadata, artwork and lyrics.
/// Demo paths are mapped to the built-in tracks; absolute paths go through the folder scanner.
/// </summary>
public sealed class LibraryRestorer {
    private readonly LocalFolderScanner _scanner;

    public LibraryRestorer(LocalFolderScanner scanner) {
        _scanner = scanner;
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
                missing.Add(path);
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

        return new RestoredLibrary(result.Tracks, byPath, missing);
    }
}
