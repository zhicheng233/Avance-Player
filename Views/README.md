# Views

两个独立的视图：全窗口沉浸歌词页和置顶的悬浮歌词窗口。主窗口本身在仓库根目录的 `MainWindow.axaml`，不在这里。

## 文件

| 文件 | 作用 |
| --- | --- |
| `ImmersiveLyricsView.axaml` | 沉浸歌词模式：`FluidBackdrop` 光场、封面/曲目信息、歌词列表、底部传输控制条。 |
| `ImmersiveLyricsView.axaml.cs` | 分段入场/退场动画，监听 `IsLyricsMode`；退场动画跑完才切回普通界面。 |
| `FloatingLyricsWindow.axaml` | 悬浮歌词窗口：430×168，可拖，置顶，不在任务栏显示。 |
| `FloatingLyricsWindow.axaml.cs` | 拖动、Esc 隐藏、每次显示时重放上浮淡入。 |

## 沉浸歌词

- 背景颜色走 `LyricsBackdropBase`/`LyricsBackdropAccent`/`LyricsBackdropSecondary`，有封面时用采样色，没封面退回 `Palette`。
- 歌词列表叫 `LyricsList`，`MainWindow.axaml.cs` 里的 `ScrollActiveLyric` 通过名字找到它，动态加 top/bottom padding 把当前行顶到中间。改列表名字时两边要一起改。
- 入场是分段的：背景先亮，然后封面落位、歌词列从右边滑进来、传输栏升起，最后环境光场把 `GlowOpacity` 拉满。退场按反序来，动画跑完才执行 `ExitLyricsCommand` 把 `IsLyricsMode` 置 false。
- 动画统一用 `Controls/Motion.cs`；不要直接改 `TranslateTransform` 子对象。

## 悬浮歌词

- `Topmost=True`、`ShowInTaskbar=False`、`CanResize=False`。显示位置由 `MainWindow.PositionFloatingLyricsWindow` 算，贴在屏幕工作区右下角。
- 窗口里除了按钮，其他地方都能拖；`OnPointerPressed` 往上找祖先里的 `Button`，点图标不会变成拖窗口。
- 按 Esc 把 `IsFloatingLyricsVisible` 置 false，窗口隐藏但实例还在。显示/隐藏由 `MainWindow.SyncFloatingLyricsWindow` 同步，主窗口隐藏时也会把它一起藏起来。
- 歌词行用 `LyricLineTextBlock`，绑定 `CurrentLyric`、`CurrentPositionSeconds`、`IsPlaying`，逐字填充跟主界面一致。
- 切歌时 `TransitioningContentControl` 用 `FadeSlidePageTransition` 做一次小位移的淡入。

## 注意

- 这两个视图都拿 `MainWindowViewModel` 当 `DataContext`，不要在这里 new 服务。
- 视图代码只处理窗口和动画，播放、队列、持久化逻辑回 view model。
- 悬浮窗口关闭时走隐藏而不是销毁；`MainWindow` 负责生命周期，别在 `Closed` 里重复 Dispose。
