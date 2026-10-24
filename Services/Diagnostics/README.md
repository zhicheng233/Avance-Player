# Services/Diagnostics

本地播放的命令行检查。

## PlaybackDiagnostics.cs

`PlaybackDiagnostics.Run()` 对应 `--diagnostics` 参数，从 `Program.cs` 直接调用。它故意不启动 Avalonia，所以 SSH 或者无头机器上也能跑。

输出包括：

- 操作系统、进程架构、程序基目录。
- `state.json` 的实际路径。
- LibVLC 版本和 changeset；初始化失败时打印错误并返回 1。
- 可用的音频输出、音频设备和音频滤镜列表。

```bash
~/.dotnet/dotnet run --project Avance-Player.csproj -- --diagnostics
```

用途：

- 确认系统里有没有装原生 libvlc，或者 `AVANCE_LIBVLC_PATH` 指得对不对。
- 换机器/远程桌面时先看看有没有音频设备。
- 排查“为什么本地文件播不出声”，比直接开 UI 快。

它构造 `LibVlcRuntime` 时只传 `--no-video --quiet`，不碰播放器和均衡器，所以不会因为声卡被占用之类的问题卡住。
