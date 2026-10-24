# Services/Lyrics

歌词加载和解析。流程是：

```text
文件字节
  → LyricsTextDecoder（编码识别）
  → LyricsParserRegistry（按扩展名/内容选解析器）
  → 具体解析器
  → LyricsNormalizer（排序、补结束时间、整理逐词）
  → LyricsDocument
```

## 文件

| 文件 | 作用 |
| --- | --- |
| `ILyricsParser.cs` | 解析器接口、`LyricsParseContext`、`LyricsParseException`。 |
| `LyricsParserRegistry.cs` | 扩展名分派和内容嗅探。`.krc` 直接走二进制；其他先试 XML/ASS/SRT 等特征，再退回 LRC。 |
| `LyricsService.cs` | 异步读文件，按优先级找同目录 sidecar，调用解析器。 |
| `LyricsTextDecoder.cs` | BOM、严格 UTF-8、GB18030、Shift-JIS 的识别。GB18030 和 Shift-JIS 都能解时数假名数量决定。 |
| `LyricsNormalizer.cs` | 行排序、补结束时间、把零时长的空格词并进相邻词、给缺结束时间的词补边界、从逐词拼出整行文本。 |
| `LyricsTime.cs` | LRC/ASS 时间戳、TTML 的 clock/frame/tick 时间、全局 offset 的解析。 |
| `LrcLyricsParser.cs` | 普通 LRC 和增强 LRC（`<mm:ss.xx>` 逐词标签、`offset`）。 |
| `SrtLyricsParser.cs` | SRT 字幕，天然只有整行时间。 |
| `AssLyricsParser.cs` | ASS/SSA，支持 `\k`/`\kf`/`\ko` 卡拉 OK 标签，翻译/罗马音对话行会配对。 |
| `TtmlLyricsParser.cs` | Apple/AMLL 风格 TTML，支持逐词、`x-translation`/`x-roman`/`x-bg` 和元数据翻译表。 |
| `LysLyricsParser.cs` | 网易云 LYS。 |
| `QrcLyricsParser.cs` | QQ 音乐 QRC（明文或已解密的）。加密 QRC 会明确报错，不会硬解。 |
| `KrcLyricsParser.cs` | 酷狗 KRC，支持明文和 XOR+zlib 加密。 |
| `YrcLyricsParser.cs` | 网易云 YRC。 |

## 支持的文件

`LyricsService.SupportedExtensions`：

```text
.ttml .xml .lys .qrc .krc .yrc .eslrc .lrc .ass .ssa .srt
```

同目录找歌词的优先级：

```text
.ttml .lys .qrc .krc .yrc .eslrc .lrc .ass .ssa .srt .xml
```

匹配大小写不敏感，Windows 拷过来的 `.LRC` 在 Linux 上也能认。

## 注意

- 解析器只负责把文本变成 `LyricLine`/`LyricWord`，不读写文件；读文件的事在 `LyricsService`。
- 纯 LRC/SRT 没有逐词信息，UI 会退化成整行渐变，这是格式本身决定的。
- 加密的 QQ 音乐 QRC 无法离线解码，检测到会抛 `LyricsParseException`，提示用户换明文格式。
- 解析器返回的行可能乱序，`LyricsNormalizer` 会统一排序；新解析器也走它。
- 逐词时间最好一开始就算对。`LyricsNormalizer.FillWordEnds` 只补缺失的结束时间，不会裁剪超出整行的词。

## 加新格式

1. 实现 `ILyricsParser`，填 `Format`、`CanParse`、`Parse`。
2. 在 `LyricsFormat` 枚举里加一项。
3. 在 `LyricsParserRegistry` 里注册实例，补扩展名分派和嗅探顺序。
4. 如果需要新的时间写法，先看 `LyricsTime` 里有没有现成 helper。
5. 用合成样例测，别把有版权的歌词提交进仓库。
