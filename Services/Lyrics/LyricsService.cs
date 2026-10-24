using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avance_Player.Models;

namespace Avance_Player.Services.Lyrics;

/// <summary>
/// 歌词加载里跟文件系统打交道的那部分。解析本身同步而且不大；唯一异步的是读文件，
/// 免得慢的网络盘/移动盘把 UI 线程卡住。
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
    /// 找跟音频同名的 sidecar。逐词格式优先于整行级，所以旁边同时有 TTML 和 LRC 时选 TTML。
    /// </summary>
    public async Task<LyricsDocument?> TryLoadSidecarAsync(
        string? audioPath,
        TimeSpan trackDuration,
        CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(audioPath)) return null;

        string? directory;
        string? baseName;
        try {
            directory = Path.GetDirectoryName(audioPath);
            baseName = Path.GetFileNameWithoutExtension(audioPath);
        }
        catch (ArgumentException) {
            return null;
        }

        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(baseName)) return null;

        // sidecar 匹配不区分大小写：Windows 拷过来的文件夹里可能是 "Song.LRC"，
        // 音频叫 "Song.flac"，Linux 上不这么比就找不着。
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try {
            foreach (var file in Directory.EnumerateFiles(directory)) files.TryAdd(Path.GetFileName(file), file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException) {
            return null;
        }

        string? lastError = null;
        foreach (var extension in SidecarPriority) {
            if (!files.TryGetValue(baseName + extension, out var candidate)) continue;

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
