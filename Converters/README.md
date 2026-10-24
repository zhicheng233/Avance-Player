# Converters

XAML 绑定用的小转换器，都在 `App.axaml` 里注册成资源。

| 文件 | 资源键 | 作用 |
| --- | --- | --- |
| `ArtworkColorConverter.cs` | `ArtworkColorConverter` | 把模型里的十六进制颜色字符串转成 `SolidColorBrush`。解析失败就返回透明刷子，不让绑定报错。 |
| `CountToBoolConverter.cs` | `CountToBoolConverter` / `CountToEmptyConverter` | 集合数量转 bool。默认“有内容 = true”；`Invert=True` 时反过来，用来控制空状态提示。 |
| `LevelHeightConverter.cs` | `LevelHeightConverter` | 把 0..1 的均衡器档位换成像素高度（8 到 48），给 5 段 EQ 的柱状指示用。 |

## 说明

- 这层只做展示转换，不碰播放或文件系统。业务判断留在 view model。
- `CountToBoolConverter` 只认 `int`，其他类型按 0 处理；绑定 `ObservableCollection.Count` 时，集合变化会通过 `Count` 属性通知刷新。
- `LevelHeightConverter` 的输出范围是按当前 56px 高的 EQ 区域调的。改面板高度时记得一起看这个常量。
- 新增转换器就在 `App.axaml` 的 `Application.Resources` 里加一行，命名跟现有的一致。
