# ViewModels

主窗口的状态和命令。XAML 里绑定的大部分属性都在这里，窗口代码只负责窗口本身的事。

## 文件

| 文件 | 作用 |
| --- | --- |
| `MainWindowViewModel.cs` | 主 ViewModel，也是最大的一个。导航、曲库、歌单、播放规则、歌词模式、导入、状态保存/恢复、双语文本和 UI 时钟都在这里。 |
| `PageViewModels.cs` | 页面模型：曲库页、分类浏览、分类分组、曲目集合、歌单列表、歌单详情。 |
| `PlaybackOptions.cs` | 弹层里的单选标签（`PlaybackOptionViewModel`），定时关闭和短选项行都用它。 |
| `EqualizerBandViewModel.cs` | 一个归一化的 EQ 频段（0..1，0.5 平直）。 |
| `RelayCommand.cs` | 很薄的 `ICommand` 实现，`Action<object?>` + 可选 `Predicate<object?>`。 |

## MainWindowViewModel 管什么

- 导航：`NavigateCommand`、`OpenPlaylistCommand`、`OpenCategoryGroupCommand`，`CurrentPage` 换页面；艺术家/专辑/流派都是按曲库现场分组。
- 播放：`PlayTrackCommand`、`TogglePlaybackCommand`、上一首/下一首、停止、`SeekToLyricCommand`。
- 队列和顺序：`PlaybackQueue`、随机历史、`RepeatMode`、`PlaybackOrderLabel`。
- A–B 循环：`SetAbLoopACommand`/`SetAbLoopBCommand`/`ClearAbLoopCommand` 和对应状态。
- 睡眠定时：`SleepTimerOptions`、`SleepTimerRemainingLabel`，关闭/15/30/45/60/90 分钟。
- 播放设置：音量、静音、0.5–2.0 连续速度、音调同步、音调偏移、5 段均衡器。
- 界面：三个弹层（queue/sound/more）、`IsCompactLayout`、沉浸歌词模式、全屏、悬浮歌词。
- 双语：`Text["Key"]` 取字符串，`ToggleLanguageCommand` 切换。
- 导入：`ImportFolderToPlaylistAsync` 走 `StorageProvider` 和 `LocalFolderScanner`；导入完自动选中第一首但不自动播。
- 歌单：`CreatePlaylistCommand`/`DeletePlaylistCommand`，曲目靠 `AddTrackToPlaylistCommand`/`RemoveTrackFromPlaylistCommand` 进出；歌单详情页会跟着 `IsEmpty` 刷新。
- 持久化：`RestoreAsync` 恢复曲库和上次位置，属性变化时调度 `DebouncedStateSaver`。

## 几个关键行为

- 100ms 的播放计时器轮询 `PlaybackService.Snapshot`，更新 `CurrentPositionSeconds`；33ms 的 ambient 计时器只推进 `AmbientPhase`。
- seek 条拖动时写 `SeekPositionSeconds` 做本地预览，松手才 `CommitSeek`。后端上报的位置更新不会反向调 `Seek`。
- 切歌会取消还没提交的 seek，避免旧目标落到新歌上。
- 后端事件带 session id，`PlaybackService` 过滤后 view model 再切到 UI 线程。
- 恢复状态不会自动播放；第一次播放从保存的位置开始。
- 空曲库时 `CurrentTrack` 是占位对象，播放/导入歌词/停止/悬浮歌词这些命令都会先判断有没有真曲目。
- 切换音调同步或（后端支持时）移调，会在当前本地曲目的同一位置重新加载 media。

## 约定

- 播放规则留在 view model，服务层只做 I/O 和会话。不要在 `MainWindow.axaml.cs` 里加队列逻辑。
- 新命令用 `RelayCommand`，需要在状态变化时刷新可用性的，调 `RaiseCanExecuteChanged()`。
- 有绑定需求的属性手动发通知；计算属性依赖的属性变化时一起通知。
- 不要把 `Bitmap` 或窗口对象塞进状态；持久化的东西交给 `PlayerState`。
