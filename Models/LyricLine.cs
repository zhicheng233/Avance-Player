using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Avance_Player.Models;

public sealed class LyricLine(
    TimeSpan timestamp,
    string text,
    string? translation = null,
    string? romanization = null,
    TimeSpan? end = null,
    IReadOnlyList<LyricWord>? words = null) : INotifyPropertyChanged {
    private bool _isCurrent;
    private int _currentWordIndex = -1;

    public TimeSpan Timestamp { get; } = timestamp;

    /// <summary>这一行结束的时间。解析器按格式、下一行或者整首时长来填。</summary>
    public TimeSpan End { get; set; } = end ?? timestamp;

    public string Text { get; } = text;
    public string? Translation { get; } = translation;
    public string? Romanization { get; } = romanization;
    public bool HasTranslation => !string.IsNullOrWhiteSpace(Translation);
    public bool HasRomanization => !string.IsNullOrWhiteSpace(Romanization);

    /// <summary>带时间的词/音节。整行级格式这里是空的。</summary>
    public IReadOnlyList<LyricWord> Words { get; } = words ?? [];
    public bool HasWordTiming => Words.Count > 0;

    public bool IsCurrent {
        get => _isCurrent;
        set {
            if (_isCurrent == value) return;
            _isCurrent = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 当前唱到第几个词，第一个词之前是 -1。播放器更新；
    /// 歌词渲染器监听这个属性，不用每 100ms 重建视觉树。
    /// </summary>
    public int CurrentWordIndex {
        get => _currentWordIndex;
        set {
            if (_currentWordIndex == value) return;
            _currentWordIndex = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
