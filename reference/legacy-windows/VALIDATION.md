# 接收验证记录

日期：2026-09-22。设备：Computer-05。对象：当前工作树的独立临时副本。

## 构建

`dotnet build SteamCuratorHub.slnx -c Debug --nologo -v minimal`

退出码 0；0 个警告，0 个错误；构建耗时约 5 秒。

## 本地集成检查

`dotnet run --project SteamCuratorHub.IntegrationTests/SteamCuratorHub.IntegrationTests.csproj -c Debug --no-build -- --local-only`

退出码 0；最终输出 `PASS`。输出包括：

- Steam/Gemini 传输契约、重试和配置隔离。
- 评论研究、详情局部失败、页面选择竞态、生命周期、搜索和缓存。
- 本地会话计时、启动器测试适配器、只读成就刷新。
- 历史与草稿、临时 SQLite、账号隔离、任务生命周期与本地发布记录。

发布核心检查由测试入口调用，不单独打印结果。旧 ViewModel 测试仍包含本地直接记账的历史行为；其通过不证明 Steam 发布成功。

本次没有启动应用 GUI、真实游戏、真实 Steam 发布/领取/成就写入。没有执行 Release 构建与真实 API 测试。
