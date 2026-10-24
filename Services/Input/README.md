# Services/Input

全局快捷键。

## GlobalHotkeyService.cs

用 SharpHook 的 `SimpleGlobalHook` 监听系统级按键，主要给窗口没焦点时用：

- `Ctrl+Alt+P`：播放/暂停。
- `Ctrl+Alt+Left` / `Ctrl+Alt+Right`：上一首/下一首。
- `Ctrl+Alt+Up` / `Ctrl+Alt+Down`：音量加减。

`GlobalHotkeyAction` 枚举把这几个动作传给 `MainWindowViewModel`。

## 注意

- `AppIsActive` 在窗口激活时由 `MainWindow` 设置，避免应用在前台时同一个键触发两次（窗口自己的快捷键和全局钩子）。
- 钩子起不来（比如 Wayland 限制、权限不允许）时只是降级成“没有全局快捷键”，不会让应用启动失败；这时 `IsRunning` 是 false，窗口自己的快捷键照常工作。
- Windows/macOS 上会尝试吞掉匹配的组合键；Linux/X11 下 SharpHook 的按键抑制不一定生效，别指望它一定拦得住。
- 回调运行在钩子线程，只做轻量转交，别在里面直接碰 UI。异常也不能往外抛，不然钩子线程会挂。
- 窗口内部的快捷键（空格、左右方向键长按加速、`Ctrl+左右` 切歌等）在 `MainWindow.axaml.cs` 里，不归这个服务管。
