# Services

业务层。窗口和 view model 通过 `PlayerServices` 拿服务，不自己 `new` LibVLC、JSON store 或扫描器。

## 根目录文件

| 文件 | 作用 |
| --- | --- |
| `PlayerServices.cs` | 默认组合根。`CreateDefault()` 把播放后端、状态存储、扫描器、恢复器、歌词服务和系统媒体串起来。 |
| `LocalFolderScanner.cs` | 递归扫本地文件夹，读标签、内嵌封面和同目录的封面/歌词，生成 `Music`。也负责按已知路径恢复曲库。支持的音频扩展名在 `AudioExtensions` 里，封面优先找 `cover`/`folder`/`front`/`album`/`artwork`。 |
| `FolderWatchService.cs` | 盯着已导入的根目录（`FileSystemWatcher`，递归 + 2 秒防抖），有音频/封面/歌词增删改名就发 `Changed`；另有 2 分钟一次的数量+时间戳指纹轮询兜底，网络盘和通知不可用的文件系统靠它。 |
| `ArtworkPaletteExtractor.cs` | 从封面像素里采主色、强调色、辅助色和背景色。 |
| `MonetPaletteService.cs` | 从封面种子色生成界面的暖色调配色，紫色会特意往琥珀方向掰。 |
| `LocalizedText.cs` | 中英文字典和索引器。`Text["Key"]` 绑定用，切语言时同时发 `Item` 和 `Item[]`。 |

## 子目录

| 目录 | 内容 |
| --- | --- |
| [`Diagnostics/`](Diagnostics/README.md) | `--diagnostics` 的控制台播放检查。 |
| [`Input/`](Input/README.md) | SharpHook 全局快捷键。 |
| [`Lyrics/`](Lyrics/README.md) | TTML、LRC、QRC、KRC、YRC、LYS、ASS、SRT 的解析器栈。 |
| [`Persistence/`](Persistence/README.md) | `state.json` 的读写、防抖保存和曲库恢复。 |
| [`Playback/`](Playback/README.md) | 播放会话、LibVLC/模拟后端和原生运行时初始化。 |
| [`SystemMedia/`](SystemMedia/README.md) | Linux MPRIS、Windows SMTC、macOS Now Playing。 |

## 数据怎么流

- 导入：`LocalFolderScanner.ScanAsync` → `Music` 列表 → `MainWindowViewModel` 加进曲库和歌单。扫描是递归的，跳过隐藏/系统目录和符号链接目录，按路径排序，方便结果稳定。
- 刷新：导入时把根目录记进 `Playlist.FolderRoots`（持久化为 `PlaylistState.FolderRoots`），`FolderWatchService` 据此监听。变化触发 `MainWindowViewModel.RefreshFolderAsync`：一个根目录只扫一次，再分别套到所有引用它的歌单上。磁盘上没了的条目（按 `File.Exists` 判断）从歌单和曲库移除；正在播放的那首也照删，播放不会被打断，libvlc 还握着已打开的句柄。应用启动时 `RefreshAllFoldersAsync` 补扫一次，把关闭期间的增删追平。
- 播放：view model 把 `Music.FilePath` 交给 `PlaybackService` → `SwitchableAudioBackend` 按绝对/相对路径选 LibVLC 或模拟后端。
- 歌词：`LyricsService.LoadAsync` → `LyricsParserRegistry` 按扩展名/内容选解析器 → `LyricsNormalizer` 收尾 → `LyricsDocument`。
- 主题：`ArtworkPaletteExtractor` 采封面 → `MonetPaletteService` 生成界面配色。
- 状态：view model 组装 `PlayerState` → `DebouncedStateSaver` → `JsonPlayerStateStore` 落盘。

## 约定

- 服务里不碰 Avalonia 窗口，也不做导航；播放队列、循环、随机、A–B、睡眠定时这些规则留在 `MainWindowViewModel`。
- 文件 I/O 尽量异步，慢盘或网络盘不能卡 UI 线程。
- 手删记录：从歌单里手动移除的路径连同当时的文件指纹（大小 + 最后写入时间）存进 `LibrarySettings.DismissedPaths`，重扫不会加回；文件真被换过则指纹对不上，允许重新出现。老状态文件没有指纹时按"仍是同一首"处理并补写当前值。
- 平台相关实现放子目录，调用方只依赖接口或门面。系统媒体控制是 best-effort，后端挂掉不能影响播放。
