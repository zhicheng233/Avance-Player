# Controls

自绘控件和动画小工具。图标全部用几何图形画，不依赖 emoji 或系统字体，这样各个平台的表现一致。

## 文件

| 文件 | 作用 |
| --- | --- |
| `WaveProgressBar.cs` | 波浪形进度条。播放控制台里当 seek 条，音量面板里用 `IsSeekBar=False` 当平面滑块；`LoopStart`/`LoopEnd` 画 A–B 循环标记。 |
| `FluidBackdrop.cs` | 沉浸歌词背后的慢速流体色场，几块径向渐变按正弦轨迹漂，中间压暗保证歌词可读。 |
| `LyricLineTextBlock.cs` | 逐字歌词文本。`Line` 绑定 `LyricLine`，`Position`/`IsPlaying` 驱动渐变填充；内部有个 16ms 的小计时器，在两次 view model 更新之间自己外推。 |
| `Motion.cs` | 一次性动画的公共方法：`Tween`、`TransformTween`、`RunAsync`、`RunTransformAsync`、`Pop`，还有注册给 Avalonia 的自定义 `ITransform` 插值器。 |
| `FadeSlidePageTransition.cs` | 页面切换动画：淡入加一点点上浮/缩放。主内容区、控制台当前曲目和悬浮歌词行都在用。 |
| `MediaGlyph.cs` | 播放相关的几何图标，`MediaGlyphKind` 里有上一首/下一首/播放/随机/循环/队列/更多/歌词/加减/关闭/全屏。 |
| `OptionGlyph.cs` | 设置弹层里的图标：`Sliders`、`Speed`、`Loop`、`Clock`。 |
| `TransportGlyph.cs` | 播放/暂停图标，`IsPlaying` 一翻就换形状。 |
| `VolumeGlyph.cs` | 单色喇叭图标，`Level` 和 `IsMuted` 控制里面那几道声波。 |

## WaveProgressBar 几个容易踩的点

- `Value` 是 0 到 `Maximum` 的进度，不是 0..1。seek 条和音量条共用这个控件，单位由调用方决定。
- `IsSeekBar=True` 时才会发 `SeekStarted`/`SeekCompleted`；拖动过程中只更新指针预览，松手才提交。
- 轨道两端留了 7px 内缩，指针位置按可用宽度夹到 0..1，所以点到最边上也会落到起点/终点。
- 自己有个 16ms 的动画计时器做填充缓动、涟漪和 thumb 光晕。`IsPlaying` 控制波浪是否继续动、播放时填充是否慢爬；暂停、seek、切歌这类跳变会直接落位，不横扫过去。
- A–B 标记画在轨道下面，`LoopStart`/`LoopEnd` 传 0..1 的进度比例（界面绑的是 `AbLoopStartFraction`/`AbLoopEndFraction`），没开循环时传 -1。

## 约定

- 悬停/按下的位移加在内层内容上，别加在带点击事件的 `Border` 上，不然命中区域会跟着动。
- 一次性动画统一走 `Motion.RunTransformAsync`，它动画的是 `Visual.RenderTransform`。不要直接动画 `TranslateTransform`/`ScaleTransform` 子对象，Avalonia 自带的那套只认 `Visual`。
- 渐隐用目标颜色的零 alpha 版本（比如 `#001E1D19`），不要用 `Transparent`。`Transparent` 是白色零 alpha，过渡时会闪灰。
- 需要新图标时优先往 `MediaGlyph`/`OptionGlyph` 里加枚举和几何路径，别引入字体符号或平台主题覆盖。
