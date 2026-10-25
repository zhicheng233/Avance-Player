using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Avance_Player.Services;

/// <summary>
/// 盯着已经导入过的文件夹，有音频文件增删改名就通知上层重扫。
///
/// <see cref="FileSystemWatcher"/> 一次只能盯一个目录（不含子目录里的独立实例），
/// 这里给每个根目录挂一个递归 watcher，事件先攒起来做防抖：
/// 复制一张专辑会一次性刷出几十个事件，重扫一次就够，不用扫几十次。
/// </summary>
public sealed class FolderWatchService : IDisposable {
    /// <summary>安静这么久之后才真的通知上层。拷大文件时事件会连续来很久。</summary>
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(2);

    /// <summary>兜底轮询间隔：网络盘和不支持递归通知的平台上 watcher 可能什么都不报。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase) {
        ".mp3", ".flac", ".m4a", ".aac", ".wav", ".ogg", ".opus", ".wma",
        ".ape", ".alac", ".aiff", ".aif", ".dsf", ".dff", ".mka"
    };

    /// <summary>同目录的封面/歌词改了也要刷，不然换了封面不生效。</summary>
    private static readonly HashSet<string> SidecarExtensions = new(StringComparer.OrdinalIgnoreCase) {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif",
        ".lrc", ".eslrc", ".ttml", ".lys", ".qrc", ".krc", ".yrc", ".ass", ".ssa", ".srt"
    };

    private readonly object _sync = new();
    private readonly Dictionary<string, WatchedFolder> _folders = new(StringComparer.Ordinal);
    private readonly Timer _debounceTimer;
    private readonly Timer _pollTimer;
    private bool _disposed;

    public FolderWatchService() {
        _debounceTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        _pollTimer = new Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>某个根目录里可能有变化了。参数是根目录，上层据此决定要不要重扫。</summary>
    public event Action<string>? Changed;

    /// <summary>路径比较方式跟别处保持一致：Windows 不区分大小写。</summary>
    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>开始盯这个根目录；重复调用是幂等的。</summary>
    public void Watch(string rootFolder) {
        if (_disposed || string.IsNullOrWhiteSpace(rootFolder)) return;

        string fullPath;
        try {
            if (!Path.IsPathFullyQualified(rootFolder)) return;
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootFolder));
        }
        catch (Exception) {
            return;
        }

        if (!Directory.Exists(fullPath)) return;

        lock (_sync) {
            if (_disposed || _folders.ContainsKey(fullPath)) return;

            var watched = new WatchedFolder(fullPath);
            try {
                var watcher = new FileSystemWatcher(fullPath) {
                    IncludeSubdirectories = true,
                    // 只报文件名和目录名变化；大小/时间戳的抖动跟导入无关。
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    InternalBufferSize = 64 * 1024
                };
                watcher.Created += (_, e) => OnFileEvent(fullPath, e.FullPath);
                watcher.Deleted += (_, e) => OnFileEvent(fullPath, e.FullPath);
                watcher.Renamed += (_, e) => OnFileEvent(fullPath, e.FullPath);
                watcher.Error += (_, e) => OnWatcherError(fullPath, e.GetException());
                watcher.EnableRaisingEvents = true;
                watched.Watcher = watcher;
            }
            catch (Exception) {
                // 目录被删、权限不足、文件系统不支持通知：还能靠兜底轮询。
            }

            _folders[fullPath] = watched;
        }

        EnsurePolling();
    }

    /// <summary>不再盯这个根目录。</summary>
    public void Unwatch(string rootFolder) {
        if (string.IsNullOrWhiteSpace(rootFolder)) return;

        string fullPath;
        try {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootFolder));
        }
        catch (Exception) {
            return;
        }

        lock (_sync) {
            if (!_folders.Remove(fullPath, out var watched)) return;
            watched.Dispose();
            if (_folders.Count == 0) _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// <summary>按当前根目录集合重设监听，导入/删除歌单后调一次。</summary>
    public void Sync(IEnumerable<string> rootFolders) {
        var wanted = new HashSet<string>(PathComparer);
        foreach (var folder in rootFolders) {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            try {
                if (Path.IsPathFullyQualified(folder)) {
                    wanted.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
                }
            }
            catch (Exception) {
                // 存坏的路径跳过。
            }
        }

        List<string> unwatch;
        lock (_sync) {
            unwatch = _folders.Keys.Where(path => !wanted.Contains(path)).ToList();
        }

        foreach (var path in unwatch) Unwatch(path);
        foreach (var path in wanted) Watch(path);
    }

    private void OnFileEvent(string rootFolder, string changedPath) {
        if (_disposed) return;
        if (!IsRelevant(changedPath)) return;
        MarkDirty(rootFolder);
    }

    private static bool IsRelevant(string path) {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var extension = Path.GetExtension(path);
        if (AudioExtensions.Contains(extension) || SidecarExtensions.Contains(extension)) return true;

        // 目录事件没有扩展名。新建、改名、整个删掉都会改变里面的音频集合，都得重扫；
        // 删掉的那个目录已经不在磁盘上了，判断不了类型，干脆按相关处理。
        return extension.Length == 0;
    }

    private void OnWatcherError(string rootFolder, Exception exception) {
        if (_disposed) return;
        // 缓冲区溢出时会丢事件，最稳的办法就是直接重扫这个根目录。
        MarkDirty(rootFolder);
    }

    private void MarkDirty(string rootFolder) {
        lock (_sync) {
            if (_disposed || !_folders.TryGetValue(rootFolder, out var watched)) return;
            watched.Dirty = true;
        }

        // 每来一个事件就把计时器往后推，拷完一批文件后只触发一次。
        _debounceTimer.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
    }

    private void Flush() {
        List<string> dirty;
        lock (_sync) {
            if (_disposed) return;
            dirty = _folders.Where(pair => pair.Value.Dirty).Select(pair => pair.Key).ToList();
            foreach (var folder in dirty) _folders[folder].Dirty = false;
        }

        foreach (var folder in dirty) RaiseChanged(folder);
    }

    /// <summary>
    /// 兜底：即使一个通知都没收到，也定期重扫一遍。
    /// 用文件数量+最新写入时间做指纹，没变就不打扰上层。
    /// </summary>
    private void Poll() {
        List<string> changed;
        lock (_sync) {
            if (_disposed) return;
            changed = [];
            foreach (var pair in _folders) {
                var fingerprint = ComputeFingerprint(pair.Key);
                if (fingerprint is null || fingerprint == pair.Value.Fingerprint) continue;
                pair.Value.Fingerprint = fingerprint;
                // 第一次轮询只是把基线填上，不该立刻触发重扫。
                if (pair.Value.HasBaseline) changed.Add(pair.Key);
                pair.Value.HasBaseline = true;
            }
        }

        foreach (var folder in changed) RaiseChanged(folder);
        EnsurePolling();
    }

    private static string? ComputeFingerprint(string rootFolder) {
        try {
            if (!Directory.Exists(rootFolder)) return null;
            var count = 0;
            var newest = DateTime.MinValue;
            foreach (var path in Directory.EnumerateFiles(rootFolder, "*", new EnumerationOptions {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
                     })) {
                if (!IsRelevant(path)) continue;
                count++;
                try {
                    var written = File.GetLastWriteTimeUtc(path);
                    if (written > newest) newest = written;
                }
                catch (Exception) {
                    // 单个文件读不到就算了，数量和别的文件的时间戳还能用。
                }
            }

            return $"{count}:{newest.Ticks}";
        }
        catch (Exception) {
            return null;
        }
    }

    private void EnsurePolling() {
        lock (_sync) {
            if (_disposed) return;
            if (_folders.Count == 0) {
                _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
                return;
            }

            _pollTimer.Change(PollInterval, PollInterval);
        }
    }

    private void RaiseChanged(string rootFolder) {
        try {
            Changed?.Invoke(rootFolder);
        }
        catch (Exception) {
            // 上层的重扫崩了不该把监听线程带走。
        }
    }

    public void Dispose() {
        lock (_sync) {
            if (_disposed) return;
            _disposed = true;
            foreach (var watched in _folders.Values) watched.Dispose();
            _folders.Clear();
        }

        _debounceTimer.Dispose();
        _pollTimer.Dispose();
    }

    private sealed class WatchedFolder(string rootFolder) : IDisposable {
        public string RootFolder { get; } = rootFolder;
        public FileSystemWatcher? Watcher { get; set; }
        public bool Dirty { get; set; }

        /// <summary>
        /// 建 watcher 时的目录指纹。这里故意不立刻算：算指纹要递归遍历整个目录，
        /// 在 UI 线程上做会卡一下。留给第一次轮询（后台计时器线程）去填基线。
        /// </summary>
        public string? Fingerprint { get; set; }
        public bool HasBaseline { get; set; }

        public void Dispose() {
            try {
                if (Watcher is null) return;
                Watcher.EnableRaisingEvents = false;
                Watcher.Dispose();
            }
            catch (Exception) {
                // 已经释放或者目录没了，忽略。
            }
            finally {
                Watcher = null;
            }
        }
    }
}
