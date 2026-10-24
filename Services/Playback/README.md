# Services/Playback

播放会话和音频后端。队列、循环、随机、A–B、睡眠定时这些规则在 view model，这里只管“把当前这个源放出来”。

## 结构

```text
PlayerServices
  └── PlaybackService
        └── SwitchableAudioBackend
              ├── LibVlcAudioBackend   （绝对路径的本地文件）
              └── SimulatedAudioBackend（相对路径的旧 demo/测试源）
```

## 文件

| 文件 | 作用 |
| --- | --- |
| `PlaybackContracts.cs` | `AudioSource`、`PlaybackSession`、快照、结束/失败事件、`EqualizerSettings` 和 `IAudioBackend` 接口。 |
| `PlaybackService.cs` | 管理当前会话，转发后端的 `Ended`/`Failed`，保存音量/速度/音调/均衡器的当前值。 |
| `SwitchableAudioBackend.cs` | 按 `AudioSource.IsLocalFile` 选 LibVLC 或模拟后端。 |
| `LibVlcAudioBackend.cs` | LibVLCSharp + 原生 libvlc 的真实播放。 |
| `LibVlcRuntime.cs` | 原生运行时初始化、路径查找和实例选项。 |
| `SimulatedAudioBackend.cs` | 没有音频输出的假后端，只按 `TimeProvider` 推进时钟。 |

## 会话和事件

- 每次 `Start` 都生成一个新的 `PlaybackSession.Id`，全局递增。
- 后端事件带 session，`PlaybackService` 只转当前会话的事件，避免切歌后旧的 `Ended` 把新歌顶掉。
- `MainWindowViewModel` 再把事件切回 UI 线程。后端回调线程不直接改绑定属性。
- 100ms 的 UI 计时器只读 `Snapshot`。后端上报的位置变化不能反过来调用 `Seek`，只有用户拖动、歌词跳转和 A–B 回绕才 seek。

## LibVLC 相关

- `LibVlcRuntime` 会先找 `AVANCE_LIBVLC_PATH`，再找 app 目录下的 `libvlc` 文件夹，最后交给 LibVLCSharp 自己找系统库。找不到时 `TryGet` 返回 null，播放会显示双语错误，不会崩。
- 每个 `LibVlcAudioBackend` 一个 `LibVLC` 实例和一个 `MediaPlayer`。默认关视频、默认做音频时间伸缩；关掉音调同步时才加 `--no-audio-time-stretch`。音量/速度/均衡器直接落到 MediaPlayer。
- 均衡器是 UI 的 5 段 0..1 映射到原生 10 段；模拟后端不处理。
- 手动移调在 libvlc 3 的公开 API 里做不到。`SupportsPitchShift` 是 false，偏移值只存偏好，UI 会说明当前后端不支持。音调同步开关是有效的，它控制 `--audio-time-stretch`，切换时会在同一位置重载当前本地 media。
- `Seek` 直接设置 `MediaPlayer.Time`，遇到不支持 seek 的格式会留在当前位置，不抛给调用方。

## 模拟后端

- 只用于测试和旧的相对路径源，没有声音。
- 时钟由 `TimeProvider` 驱动，默认 50ms 一跳，方便在测试里替换。
- 音量、速度、均衡器调用会记住但不会产生音频效果。

## 加后端

实现 `IAudioBackend` 就行，不需要继承树。然后把路由加进 `SwitchableAudioBackend` 或 `PlayerServices.CreateDefault()`。记得处理：

- 加载失败时返回 `AudioLoadResult.Fail`，不要抛到调用方。
- `Stop`/切歌后旧事件不能再发。
- `Snapshot` 要能在没有会话时安全返回零值。
