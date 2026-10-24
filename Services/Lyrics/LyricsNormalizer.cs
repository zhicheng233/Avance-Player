using System;
using System.Collections.Generic;
using System.Linq;
using Avance_Player.Models;

namespace Avance_Player.Services.Lyrics;

public static class LyricsNormalizer {
    /// <summary>
    /// 给行排序，补结束时间。只有开始时间的行一直唱到下一行开始；最后一行到整首结束。
    /// </summary>
    public static IReadOnlyList<LyricLine> Finalize(IEnumerable<LyricLine> source, TimeSpan trackDuration) {
        var lines = source
            .Where(line => !string.IsNullOrWhiteSpace(line.Text) || line.HasTranslation || line.HasRomanization)
            .OrderBy(line => line.Timestamp)
            .ThenBy(line => line.End)
            .ToList();

        for (var index = 0; index < lines.Count; index++) {
            var line = lines[index];
            var nextStart = index + 1 < lines.Count ? lines[index + 1].Timestamp : trackDuration;
            if (line.End <= line.Timestamp) line.End = nextStart;
            if (line.End <= line.Timestamp) line.End = line.Timestamp;
        }

        return lines;
    }

    /// <summary>
    /// QRC/YRC/LYS 用零时长的词表示空格。把这种词并到旁边的词里，
    /// 当前词索引才不会停在空档上。
    /// </summary>
    public static IReadOnlyList<LyricWord> NormalizeWords(IEnumerable<LyricWord> source) {
        var words = new List<LyricWord>();
        var pending = string.Empty;

        foreach (var word in source) {
            if (string.IsNullOrEmpty(word.Text)) continue;
            if (string.IsNullOrWhiteSpace(word.Text) && word.End <= word.Start) {
                if (words.Count > 0) {
                    var previous = words[^1];
                    words[^1] = new LyricWord(previous.Text + word.Text, previous.Start, previous.End);
                }
                else {
                    pending += word.Text;
                }
                continue;
            }

            words.Add(new LyricWord(pending + word.Text, word.Start, word.End));
            pending = string.Empty;
        }

        if (pending.Length > 0 && words.Count > 0) {
            var last = words[^1];
            words[^1] = new LyricWord(last.Text + pending, last.Start, last.End);
        }

        return words;
    }

    public static string TextFromWords(IEnumerable<LyricWord> words) =>
        string.Concat(words.Select(word => word.Text));

    /// <summary>
    /// 没有结束时间的词（增强 LRC、部分卡拉 OK 标签）补成下一个词的开始或者行尾。
    /// 要重建行，因为 <see cref="LyricLine.Words"/> 是只读的。
    /// </summary>
    public static IReadOnlyList<LyricLine> FillWordEnds(IEnumerable<LyricLine> source) {
        var result = new List<LyricLine>();
        foreach (var line in source) {
            if (!line.HasWordTiming) {
                result.Add(line);
                continue;
            }

            var words = new List<LyricWord>(line.Words.Count);
            for (var index = 0; index < line.Words.Count; index++) {
                var word = line.Words[index];
                var end = word.End;
                if (end <= word.Start) {
                    end = index + 1 < line.Words.Count ? line.Words[index + 1].Start : line.End;
                    if (end <= word.Start) end = word.Start;
                }
                words.Add(new LyricWord(word.Text, word.Start, end));
            }

            result.Add(new LyricLine(line.Timestamp, line.Text, line.Translation, line.Romanization, line.End, words));
        }
        return result;
    }
}
