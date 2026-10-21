using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avance_Player.Models;

namespace Avance_Player.Services.Lyrics;

public static class LyricsParserRegistry {
    private static readonly LrcLyricsParser Lrc = new();
    private static readonly SrtLyricsParser Srt = new();
    private static readonly AssLyricsParser Ass = new();
    private static readonly TtmlLyricsParser Ttml = new();
    private static readonly LysLyricsParser Lys = new();
    private static readonly QrcLyricsParser Qrc = new();
    private static readonly KrcLyricsParser Krc = new();
    private static readonly YrcLyricsParser Yrc = new();

    private static readonly ILyricsParser[] SniffOrder = [Ttml, Ass, Srt, Krc, Yrc, Qrc, Lys, Lrc];

    public static bool TryParse(
        byte[] bytes,
        string fileName,
        TimeSpan trackDuration,
        out LyricsDocument? document,
        out string? error) {
        document = null;
        error = null;

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension == ".krc") {
            try {
                document = Krc.ParseBytes(bytes, new LyricsParseContext(fileName, trackDuration));
                return true;
            }
            catch (LyricsParseException exception) {
                error = exception.Message;
                return false;
            }
            catch (Exception exception) {
                error = exception.Message;
                return false;
            }
        }

        string content;
        try {
            content = LyricsTextDecoder.Decode(bytes);
        }
        catch (Exception exception) {
            error = $"Could not decode the lyric file: {exception.Message}";
            return false;
        }

        return TryParse(content, fileName, trackDuration, out document, out error);
    }

    public static bool TryParse(
        string content,
        string fileName,
        TimeSpan trackDuration,
        out LyricsDocument? document,
        out string? error) {
        document = null;
        error = null;

        var parser = SelectByExtension(fileName);
        if (parser is not null) {
            return TryRun(parser, content, fileName, trackDuration, out document, out error);
        }

        foreach (var candidate in SniffOrder) {
            if (!candidate.CanParse(content)) continue;
            if (TryRun(candidate, content, fileName, trackDuration, out document, out error)) return true;
        }

        error ??= "Unsupported lyric file format. Supported formats: TTML, LYS, QRC, KRC, YRC, LRC/ESLRC, ASS/SSA, SRT.";
        return false;
    }

    private static bool TryRun(
        ILyricsParser parser,
        string content,
        string fileName,
        TimeSpan trackDuration,
        out LyricsDocument? document,
        out string? error) {
        try {
            document = parser.Parse(content, new LyricsParseContext(fileName, trackDuration));
            error = null;
            return document.Lines.Count > 0;
        }
        catch (LyricsParseException exception) {
            document = null;
            error = exception.Message;
            return false;
        }
        catch (Exception exception) {
            document = null;
            error = exception.Message;
            return false;
        }
    }

    private static ILyricsParser? SelectByExtension(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch {
        ".lrc" or ".eslrc" => Lrc,
        ".srt" => Srt,
        ".ass" or ".ssa" => Ass,
        ".ttml" or ".xml" => Ttml,
        ".lys" => Lys,
        ".qrc" => Qrc,
        ".krc" => Krc,
        ".yrc" => Yrc,
        _ => null
    };
}
