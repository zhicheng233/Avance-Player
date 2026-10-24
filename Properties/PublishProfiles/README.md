# PublishProfiles

`dotnet publish` 的按平台配置，一个 RID 一份。

| 配置 | 运行时 | 说明 |
| --- | --- | --- |
| `win-x64.pubxml` | Windows x64 | Windows 上会切到 `net10.0-windows10.0.19041.0`，SMTC 需要这个 TFM。 |
| `osx-x64.pubxml` | macOS Intel | 普通 `net10.0`。 |
| `osx-arm64.pubxml` | macOS Apple Silicon | 普通 `net10.0`。 |
| `linux-x64.pubxml` | Linux x64 | 普通 `net10.0`。 |
| `linux-arm64.pubxml` | Linux ARM64 | 普通 `net10.0`。 |

## 共同设置

- `SelfContained=true`：默认把 .NET 运行时一起带上。
- `PublishSingleFile=false`：Avalonia 和 LibVLC 的原生资源得跟托管程序集放一起，单文件反而麻烦。
- `PublishTrimmed=false`、`PublishReadyToRun=false`、`PublishAot=false`：现在不折腾裁剪和 AOT。
- 输出目录统一是 `$(MSBuildProjectDirectory)/bin/Release/net10.0/<rid>/publish/`。Windows 的 TFM 在构建时会变成 `net10.0-windows10.0.19041.0`，所以实际路径也跟着变。

## 用法

```bash
dotnet publish Avance-Player.csproj -p:PublishProfile=linux-x64
dotnet publish Avance-Player.csproj -p:PublishProfile=osx-arm64
```

如果目标机器已经装了 .NET 10 运行时，可以发框架依赖版，体积小不少：

```bash
dotnet publish Avance-Player.csproj -p:PublishProfile=linux-x64 -p:SelfContained=false
```

## 原生 LibVLC

- Windows / macOS 的输出里会带 `VideoLAN.LibVLC.Windows` / `VideoLAN.LibVLC.Mac` 提供的原生运行时，由 `Avance-Player.csproj` 里的条件引用决定。
- Linux 输出不带原生 libvlc，目标机器得装系统的 `libvlc` 包。
- 要换自定义运行时，运行时用 `AVANCE_LIBVLC_PATH` 指过去，或者把 `libvlc` 目录放在程序旁边。

## 加新平台

复制一份现有 `.pubxml`，改 `RuntimeIdentifier` 和 `PublishDir` 里的 RID 就行。`TargetFramework` 一般保持 `net10.0`；只有需要 Windows 专有 API 的构建才用 Windows TFM。加完记得在根 README 的发布表里补一行。
