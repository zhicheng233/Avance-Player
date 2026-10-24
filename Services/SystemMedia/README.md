# Services/SystemMedia

系统媒体控制集成。把当前播放状态发布给操作系统的媒体浮层/媒体键，再把系统发来的命令转回 `MainWindowViewModel`。

## 文件

| 文件 | 作用 |
| --- | --- |
| `SystemMediaContracts.cs` | 状态快照、重复模式、命令类型和 `ISystemMediaBackend` 接口。 |
| `SystemMediaService.cs` | 门面。`CreateDefault()` 按系统选后端，`CreateNull()` 给测试/不支持的环境用。 |
| `SystemMediaArtworkCache.cs` | 给系统一个能读的封面路径。sidecar 的 JPEG/PNG 直接用，内嵌封面和其他格式写成缓存 PNG。 |
| `LinuxMprisSystemMediaBackend.cs` | Linux 的 MPRIS（D-Bus），通过 `Tmds.DBus.Protocol` 实现。 |
| `WindowsSmtcSystemMediaBackend.cs` | Windows 的 SMTC，用 `LibSMTC`，只在 Windows TFM 下编译。 |
| `MacNowPlayingSystemMediaBackend.cs` | macOS Now Playing 的托管侧。 |
| `MacNowPlayingNative.cs` | macOS 的 Objective-C 桥，P/Invoke 到系统 Now Playing。 |

## 命令流

```text
系统媒体键/浮层
  → 平台后端 CommandReceived
  → SystemMediaService.CommandReceived
  → MainWindowViewModel
  → PlaybackService / 队列逻辑
```

## 注意

- `SystemMediaService.Update`/`Clear` 是 best-effort，后端内部异常不能影响播放。桌面服务不在、D-Bus 断了，应用还得继续放歌。
- `CreateNull()` 返回一个什么都不做的后端，`IsAvailable` 是 false；测试里用它省得依赖桌面环境。
- 封面缓存按“歌曲路径 + 文件大小 + 修改时间”做 key，100ms 一次的快照轮询不会反复写文件。缓存目录默认在 `<LocalApplicationData>/AvancePlayer/artwork-cache`。
- Linux 的 MPRIS 路径在当前机器上验证过。Windows SMTC 和 macOS Now Playing 的代码已经接好，但还没在对应系统上做过运行时验证。
- 平台实现只在对应系统上实例化；Windows 后端用 `#if WINDOWS` 包着，Linux/macOS 构建不会引到。
