# Services/Persistence

播放器状态和曲库的持久化。

状态文件默认在：

```text
<LocalApplicationData>/AvancePlayer/state.json
```

Linux 上一般是 `~/.local/share/AvancePlayer/state.json`。

## 文件

| 文件 | 作用 |
| --- | --- |
| `PlayerState.cs` | 存盘用的 DTO。`CurrentVersion = 3`，下面分 `Playback`、`Library`、`Ui` 三块。 |
| `IPlayerStateStore.cs` | 读写接口，方便测试替换。 |
| `JsonPlayerStateStore.cs` | JSON 实现。写的时候先写临时文件再替换，上一版留成 `state.json.bak`；读失败会记到 `LastError`。 |
| `DebouncedStateSaver.cs` | 防抖保存。播放进度、音量这些高频变化先攒着，静置一会儿再落盘；退出时可以 `Flush`。 |
| `LibraryRestorer.cs` | 按保存的绝对路径重新读标签、封面和歌词，生成 `RestoredLibrary`。 |

## 存了什么

`PlaybackSettings`：

- 音量、上一次非零音量。
- 随机、循环模式。
- 连续速度 `Speed`（老的 `SpeedIndex` 还能读）。
- 音调同步开关、半音偏移、均衡器预设。
- 上一次播放的路径和位置。

`LibrarySettings`：

- 曲库里的文件路径列表。
- 歌单名字和每个歌单的路径顺序。
- 手动导入的歌词文件，按音频路径索引。

`UiSettings`：

- 语言。
- 悬浮歌词窗口是否显示。

## 恢复逻辑

- 恢复时只按保存的绝对路径重新加载，不会顺手扫描整个目录，上次运行后新加的文件不会自己冒出来。
- 旧的相对 demo 路径直接跳过，不报错。
- 文件已经不在了的条目会跳过；单个坏文件不会让整次恢复失败。
- 手动导入的歌词路径会覆盖扫描到的 sidecar。
- 恢复**不会自动播放**，只把当前曲目和位置摆好，等用户第一次按播放。

## 注意

- `JsonPlayerStateStore` 的写操作有锁，`DebouncedStateSaver` 负责合并调用；别在 UI 线程上直接 `Save`。
- 坏 JSON 或者必需区块缺失时 `Load` 返回 null，原因记在 `LastError`；新增字段靠默认值兼容。
- 新增设置项给默认值，不要抬 `CurrentVersion` 除非真的需要迁移逻辑。
- 状态里不要存 `Bitmap`、`Music` 对象或任何运行时对象，只存能重建它们的路径和值。
