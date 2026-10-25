# Avance Player · UML 类图

本目录保存项目 UML 类图的 Mermaid 源码（`.mmd`）和渲染出的 PNG。
图由源码生成，**修改图请改 `.mmd`，不要直接改 PNG**。

## 图集

| 文件 | 内容 |
|---|---|
| `01-models-viewmodels.png` | 模型（`Music` / `Playlist` / 歌词行词文档）与视图模型（`MainWindowViewModel`、`PageViewModel` 族、`RelayCommand`） |
| `02-services.png` | 服务层与组合根：`PlayerServices` 持有全部服务；播放后端路由；持久化；文件夹扫描 |
| `03-lyrics.png` | 歌词解析子系统（策略模式）：`ILyricsParser` 的 8 个实现与 `LyricsParserRegistry` |
| `04-system-media.png` | 系统媒体集成：MPRIS / SMTC / Now Playing / 空实现，以及快照与命令数据 |
| `05-playback-values.png` | 播放值对象与事件（全为 `record`，`PlaybackSession.Id` 用于过滤过期事件） |
| `06-views-controls.png` | 外壳窗口与自绘控件：窗口生命周期、`Control`/`TemplatedControl`/`TextBlock` 继承树、图标枚举、动效 |
| `07-infrastructure.png` | 基础设施：本地化文本、Monet 配色、封面取色、全局快捷键、值转换器 |

## 重新渲染

需要 Node.js 22+ 和 Chromium。示例（在任意临时目录安装 CLI）：

```bash
mkdir -p /tmp/opencode/uml-render && cd /tmp/opencode/uml-render
npm init -y && npm install @mermaid-js/mermaid-cli

cat > puppeteer.json <<'EOF'
{
  "executablePath": "/usr/bin/chromium",
  "protocolTimeout": 300000,
  "args": ["--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage"]
}
EOF
```

或直接使用本目录的 `render.sh`：

```bash
./render.sh              # 渲染全部
./render.sh 03-lyrics    # 只渲染某一张
```

## 约定

- 每张图在头部声明 `config: layout: elk`：默认的 dagre 布局在节点较多的类图上会排成
  又窄又高的长条，甚至布局超时；ELK 排布更紧凑，也能在十几秒内完成。
- 外部类型（Avalonia、BCL、第三方库）统一标成 `<<external>>`，避免图里出现不属于本项目的成员。
- 关系线含义：`<|--` 继承实线实心三角；`<|..` 接口实现虚线实心三角；`o--` 聚合空心菱形；
  `*--` 组合实心菱形；`-->` 关联；`..>` 依赖。
