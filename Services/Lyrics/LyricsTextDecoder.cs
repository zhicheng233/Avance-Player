using System;
using System.Text;

namespace Avance_Player.Services.Lyrics;

/// <summary>
/// Decodes lyric files whose encoding is not always UTF-8. Chinese LRC/ASS files are commonly
/// GB18030, while Japanese files may be Shift-JIS. BOMs win; otherwise strict UTF-8 is tried
/// first, then the two legacy code pages.
/// </summary>
public static class LyricsTextDecoder {
    static LyricsTextDecoder() {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string Decode(byte[] bytes) {
        if (bytes.Length == 0) return string.Empty;

        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
            return TrimBom(Encoding.UTF32.GetString(bytes, 4, bytes.Length - 4));
        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            return TrimBom(new UTF32Encoding(true, true).GetString(bytes, 4, bytes.Length - 4));
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return TrimBom(Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return TrimBom(Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2));
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return TrimBom(Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));

        try {
            return TrimBom(new UTF8Encoding(false, true).GetString(bytes));
        }
        catch (DecoderFallbackException) {
            // Fall through to the legacy encodings.
        }

        var gb18030 = TryDecode("GB18030", bytes);
        var shiftJis = TryDecode("shift_jis", bytes);
        if (gb18030 is not null && shiftJis is not null) {
            // Both legacy code pages can accept the same bytes. Japanese text decodes to kana in
            // Shift-JIS but to unrelated CJK ideographs in GB18030, so prefer the candidate with
            // more kana; otherwise keep the documented GB18030 priority.
            return TrimBom(CountKana(shiftJis) > CountKana(gb18030) ? shiftJis : gb18030);
        }
        if (gb18030 is not null) return TrimBom(gb18030);
        if (shiftJis is not null) return TrimBom(shiftJis);

        return TrimBom(Encoding.UTF8.GetString(bytes));
    }

    private static string? TryDecode(string name, byte[] bytes) {
        try {
            var encoding = Encoding.GetEncoding(
                name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            return encoding.GetString(bytes);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) {
            return null;
        }
    }

    private static int CountKana(string text) {
        var count = 0;
        foreach (var character in text) {
            if (character is >= '\u3040' and <= '\u30ff'
                or >= '\u31f0' and <= '\u31ff'
                or >= '\uff66' and <= '\uff9d') {
                count++;
            }
        }
        return count;
    }

    private static string TrimBom(string text) =>
        text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
}
