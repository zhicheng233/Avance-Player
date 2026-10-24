# Assets

界面和托盘用的图标资源。`Avance-Player.csproj` 里用 `<AvaloniaResource Include="Assets/**" Exclude="Assets/README.md" />` 把图标打进程序集（这份 README 不嵌进去），代码里通过 `avares://` 加载。

| 文件 | 用途 |
| --- | --- |
| `tray.png` | 现在实际用的图标。`MainWindow.axaml.cs` 里用 `AssetLoader.Open(new Uri("avares://Avance-Player/Assets/tray.png"))` 读出来，同时给窗口和托盘设置图标。 |
| `tray.ico` | Windows 的多尺寸 ico。目前代码里没有直接引用，先放着备用（安装包、快捷方式之类）。 |

换图标的时候注意：

- `tray.png` 用透明背景的正方形，托盘和窗口标题栏都会缩放它。尺寸不用太大，256×256 足够。
- 资源 URI 里的程序集名是 `Avance-Player`，跟命名空间里的 `Avance_Player` 不一样，别写错。
- 这个目录里的文件（除了这份 README）都会被嵌进程序集，别往这里放临时图片或者大体积素材。
