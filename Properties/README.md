# Properties

项目级的 MSBuild 配置。应用运行时不会读这个目录，里面放的是构建和发布用的东西。

目前只有 `PublishProfiles/`：每个 RID 一份 `.pubxml`，`dotnet publish -p:PublishProfile=...` 的时候用。具体差异和输出路径看 [`PublishProfiles/README.md`](PublishProfiles/README.md)。

程序本身的设置不在这里：播放器状态写到 `<LocalApplicationData>/AvancePlayer/state.json`，由 `Services/Persistence/` 管。
