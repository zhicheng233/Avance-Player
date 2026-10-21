using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avance_Player.Models;

namespace Avance_Player.Services.Lyrics;

/// <summary>
/// File-system side of lyric loading. Parsing is synchronous and small; the only async work is
/// reading the file so the UI thread is not blocked by slow network/removable drives.
/// </summary>
public sealed class LyricsService {
    public static readonly IReadOnlyList<string> SupportedExtensions = [
        ".ttml", ".xml", ".lys", ".qrc", ".krc", ".yrc", ".eslrc", ".lrc", ".ass", ".ssa", ".srt"
    ];

    private static readonly string[] SidecarPriority = [
        ".ttml", ".lys", ".qrc", ".krc", ".yrc", ".eslrc", ".lrc", ".ass", ".ssa", ".srt", ".xml"
    ];

    public async Task<LyricsDocument> LoadAsync(
        string path,
        TimeSpan trackDuration,
        CancellationToken cancellationToken = default) {
        byte[] bytes;
        try {
            bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException) {
            throw new LyricsParseException($"Could not read the lyric file: {exception.Message}");
        }

        if (!LyricsParserRegistry.TryParse(bytes, Path.GetFileName(path), trackDuration, out var document, out var error)
            || document is null) {
            throw new LyricsParseException(error ?? "The lyric file could not be parsed.");
        }

        return document;
    }

    /// <summary>
    /// Looks for a sidecar with the same base name as the audio file. Word-level formats win over
    /// line-level ones so a TTML file is preferred to an LRC next to it.
    /// </summary>
    public async Task<LyricsDocument?> TryLoadSidecarAsync(
        string? audioPath,
        TimeSpan trackDuration,
        CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(audioPath)) return null;

        string basePath;
        try {
            basePath = Path.ChangeExtension(audioPath, null);
        }
        catch (ArgumentException) {
            return null;
        }

        if (string.IsNullOrWhiteSpace(basePath)) return null;

        string? lastError = null;
        foreach (var extension in SidecarPriority) {
            var candidate = basePath + extension;
            if (!File.Exists(candidate)) continue;

            try {
                return await LoadAsync(candidate, trackDuration, cancellationToken).ConfigureAwait(false);
            }
            catch (LyricsParseException exception) {
                lastError = exception.Message;
            }
        }

        if (lastError is not null) throw new LyricsParseException(lastError);
        return null;
    }
}
