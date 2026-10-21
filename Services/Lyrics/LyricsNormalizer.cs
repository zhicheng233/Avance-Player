using System;
using System.Collections.Generic;
using System.Linq;
using Avance_Player.Models;

namespace Avance_Player.Services.Lyrics;

public static class LyricsNormalizer {
    /// <summary>
    /// Sorts lines and gives each one an end time. A line that only has a start time runs until
    /// the next line starts (the last line runs to the track duration).
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
    /// QRC/YRC/LYS encode spaces as zero-duration words. Merge those into the neighbouring word
    /// so the active-word index never lands on a gap.
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
    /// Gives words without an end time (enhanced LRC, some karaoke tags) the next word's start or
    /// the line end. Rebuilds the lines because <see cref="LyricLine.Words"/> is read-only.
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
