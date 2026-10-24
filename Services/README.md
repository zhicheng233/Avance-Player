# Services

业务层。窗口和 view model 通过 `PlayerServices` 拿服务，不自己 `new` LibVLC、JSON store 或扫描器。

## 根目录文件

| 文件 | 作用 |
| --- | --- |
| `PlayerServices.cs` | 默认组合根。`CreateDefault()` 把播放后端、状态存储、扫描器、恢复器、歌词服务和系统媒体串起来。 |
| `LocalFolderScanner.cs` | 递归扫本地文件夹，读标签、内嵌封面和同目录的封面/歌词，生成 `Music`。也负责按已知路径恢复曲库。支持的音频扩展名在 `AudioExtensions` 里，封面优先找 `cover`/`folder`/`front`/`album`/`artwork`。 |
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
- 播放：view model 把 `Music.FilePath` 交给 `PlaybackService` → `SwitchableAudioBackend` 按绝对/相对路径选 LibVLC 或模拟后端。
- 歌词：`LyricsService.LoadAsync` → `LyricsParserRegistry` 按扩展名/内容选解析器 → `LyricsNormalizer` 收尾 → `LyricsDocument`。
- 主题：`ArtworkPaletteExtractor` 采封面 → `MonetPaletteService` 生成界面配色。
- 状态：view model 组装 `PlayerState` → `DebouncedStateSaver` → `JsonPlayerStateStore` 落盘。

## 约定

- 服务里不碰 Avalonia 窗口，也不做导航；播放队列、循环、随机、A–B、睡眠定时这些规则留在 `MainWindowViewModel`。
- 文件 I/O 尽量异步，慢盘或网络盘不能卡 UI 线程。
- 平台相关实现放子目录，调用方只依赖接口或门面。系统媒体控制是 best-effort，后端挂掉不能影响播放。
