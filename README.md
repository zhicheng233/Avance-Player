# Avance Player

基于 Avalonia 的桌面音乐播放器，支持导入本地文件夹、通过 LibVLC 真实播放本地文件、逐字同步歌词、悬浮歌词窗口、中英双语界面、播放设置和状态持久化。

## 项目结构

```text
Avance-Player/
├── Program.cs                  入口；--diagnostics 也在这里分流
├── App.axaml / App.axaml.cs    应用资源、主题、转换器注册，启动时创建 MainWindow
├── MainWindow.axaml            主窗口：导航壳、播放控制台、三个弹层、页面模板
├── MainWindow.axaml.cs         窗口快捷键、托盘、歌曲行点击、歌词居中、沉浸页进出
├── Avance-Player.csproj        目标框架、NuGet 依赖和平台条件
├── Avance-Player.slnx          解决方案文件
├── app.manifest                Windows 应用清单
├── Assets/                     托盘/窗口图标
├── Controls/                   自绘控件：进度条、光场、图标、歌词文本、动画工具
├── Converters/                 XAML 绑定转换器
├── Models/                     歌曲、歌单、歌词数据模型
├── Properties/
│   └── PublishProfiles/        各平台的 dotnet publish 配置
├── Services/                   业务服务
│   ├── Diagnostics/            --diagnostics 的播放检查
│   ├── Input/                  全局快捷键
│   ├── Lyrics/                 歌词格式解析、编码识别和加载
│   ├── Persistence/            状态存取与恢复
│   ├── Playback/               播放会话、LibVLC 封装和模拟后端
│   └── SystemMedia/            MPRIS / SMTC / Now Playing
├── ViewModels/                 主窗口和页面 ViewModel、命令
└── Views/                      沉浸歌词页、悬浮歌词窗口
```

每个目录里都有一份 README，写了这个目录具体放什么、文件之间怎么串：

| 目录 | 说明 |
| --- | --- |
| [`Assets/`](Assets/README.md) | 托盘和窗口图标 |
| [`Controls/`](Controls/README.md) | 自绘控件和一次性动画工具 |
| [`Converters/`](Converters/README.md) | XAML 值转换器 |
| [`Models/`](Models/README.md) | 曲目、歌单、歌词模型 |
| [`Properties/`](Properties/README.md) | 项目级配置 |
| [`Properties/PublishProfiles/`](Properties/PublishProfiles/README.md) | 各 RID 的发布配置 |
| [`Services/`](Services/README.md) | 服务层总览 |
| [`Services/Diagnostics/`](Services/Diagnostics/README.md) | 命令行播放诊断 |
| [`Services/Input/`](Services/Input/README.md) | 全局快捷键 |
| [`Services/Lyrics/`](Services/Lyrics/README.md) | 歌词解析器栈 |
| [`Services/Persistence/`](Services/Persistence/README.md) | 状态持久化与恢复 |
| [`Services/Playback/`](Services/Playback/README.md) | 播放后端和会话 |
| [`Services/SystemMedia/`](Services/SystemMedia/README.md) | 系统媒体控制集成 |
| [`ViewModels/`](ViewModels/README.md) | 主窗口和页面状态 |
| [`Views/`](Views/README.md) | 沉浸歌词页和悬浮歌词窗口 |

`bin/`、`obj/`、`.git/`、`.idea/`、`.omo/` 这些是构建产物或工具目录，没有单独写 README。更细的开发约定看 `AGENTS.md`。

## 环境要求

- .NET SDK 10.0
- 原生 LibVLC：
  - **Linux：** 安装发行版提供的 `libvlc` 包。
  - **Windows / macOS：** 项目引用 `VideoLAN.LibVLC.Windows` / `VideoLAN.LibVLC.Mac`，里面自带原生运行时。
- 可选：把 `AVANCE_LIBVLC_PATH` 指向一个放着自定义原生 LibVLC 运行时的目录。

## 构建与运行

```bash
dotnet build Avance-Player.slnx
dotnet run --project Avance-Player.csproj
```

如果机器上装了多个 .NET SDK，直接指定对应的可执行文件，比如：

```bash
~/.dotnet/dotnet build Avance-Player.slnx
```

## 诊断

只跑控制台播放检查，不打开 UI：

```bash
dotnet run --project Avance-Player.csproj -- --diagnostics
```

它会打印 LibVLC 版本、音频输出/设备和可用的音频滤镜。在无头机器上，或者想确认原生运行时有没有装好时，挺有用。

## 发布

发布配置在 `Properties/PublishProfiles`：

| 配置 | 运行时 |
| --- | --- |
| `win-x64` | Windows x64 |
| `osx-x64` | macOS Intel |
| `osx-arm64` | macOS Apple Silicon |
| `linux-x64` | Linux x64 |
| `linux-arm64` | Linux ARM64 |

```bash
dotnet publish Avance-Player.csproj -p:PublishProfile=win-x64
dotnet publish Avance-Player.csproj -p:PublishProfile=osx-arm64
dotnet publish Avance-Player.csproj -p:PublishProfile=linux-x64
```

输出在 `bin/Release/<TargetFramework>/<runtime>/publish/`。Windows 构建会用 `net10.0-windows10.0.19041.0`，其他平台还是 `net10.0`。

这些配置会带上自包含的 .NET 运行时，但关掉了单文件发布和裁剪，因为 Avalonia 和 LibVLC 的原生资源得跟托管程序集放在一起。Windows 和 macOS 的发布输出里带原生 LibVLC 运行时；Linux 的输出仍然需要系统装 `libvlc` 包。安装包打包还没做。

如果目标机器已经装了 .NET 10 运行时，也可以发一个体积更小的框架依赖版本：

```bash
dotnet publish Avance-Player.csproj -p:PublishProfile=linux-x64 -p:SelfContained=false
```

## 说明

- 曲库启动时是空的，用“添加本地文件夹”导入。导入的本地文件走真实的 LibVLC 播放，支持 seek、音量、变速和均衡器处理。
- 内置的模拟后端只留给测试和旧的相对路径源，没有音频输出，均衡器对它也不起作用。
- 播放器状态写到 `<LocalApplicationData>/AvancePlayer/state.json`。
