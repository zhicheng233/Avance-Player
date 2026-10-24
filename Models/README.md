# Models

歌曲、歌单和歌词的数据模型。这里只放数据和对 UI 友好的通知；文件加载在 `Services/`，模型本身最多看一眼文件还在不在。

## 文件

| 文件 | 作用 |
| --- | --- |
| `Music.cs` | 一首歌。标签信息、时长、文件路径、封面位图/路径、从封面采出来的颜色、码率和质量档位、歌词文档，以及 `IsCurrent` 这类绑定状态。 |
| `Playlist.cs` | 歌单：一个名字加 `ObservableCollection<Music>`。 |
| `LyricLine.cs` | 一行歌词：开始/结束时间、原文、翻译、罗马音、逐词列表，还有 `CurrentWordIndex`。 |
| `LyricWord.cs` | 行里的一个带时间的词或音节。只有整行时间的格式不会填这个列表。 |
| `LyricsDocument.cs` | 解析完的歌词文档：格式枚举、行列表、标题/艺术家/专辑、偏移、警告，以及 `HasWordTiming`。 |

## Music 里几个常用属性

- `IsLocalFile`：路径是绝对路径才算本地导入文件。
- `HasPlayableFile`：本地文件并且现在还在磁盘上。恢复曲库和播放前会看这个。
- `ArtworkColor` / `ArtworkAccentColor` / `ArtworkSecondaryColor` / `ArtworkBackgroundColor`：导入时从封面采的配色，没封面就用路径哈希生成。
- `BitrateKbps` 和 `QualityTier`：只用于界面上那个“码率 · 档位”标签，不是解码探测。本地文件读 TagLib 的码率，TagLib 报 0 时就落到 Standard 档。
- `Lyrics`、`LyricsFormat`、`LyricsSourcePath`、`HasManualLyrics`：当前歌词文档和来源。手动导入的歌词路径会跟着状态一起存。
- `IsCurrent`：列表里高亮当前播放行。

## 约定

- 模型里不做文件加载，扫描和解析都在 `Services/`；只有 `HasPlayableFile` 会 `File.Exists` 检查一下。`Music` 的封面字段是已经加载好的 `Bitmap`，由扫描器负责填。
- 只有需要驱动绑定的属性才实现 `INotifyPropertyChanged`；`Music` 和 `Playlist` 会通知，纯记录类型不用。
- `LyricLine.Text` 要始终等于 `Words` 拼起来的文本，悬浮歌词和读屏都靠这个兜底。
- 曲库启动时是空的；模型本身不关心 demo 数据，旧的相对路径源只是保持兼容。
