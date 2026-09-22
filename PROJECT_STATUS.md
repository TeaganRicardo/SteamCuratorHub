# 项目状态

更新：2026-09-22。阶段：需求对齐及技术可行性准备。

## 已完成

- 建立公开仓库；初始基线提交为 `723ccb380ba20b0c5d71c756f2d60bd64a5d8a20`。
- 从 Computer-05 的指定目录接收当前工作树快照，包括未提交修改和新增文件。
- 核实 Google Drive 中存在对应的 `SteamCuratorHub/Legacy Version/Steam Curator Hub` 上传目录。
- 将 100 个源文件、项目文件和历史文档保存至 `reference/legacy-windows/`；未导入旧 Git 历史、构建产物、会话、数据库及第三方离线网页副本。
- 完成源码核对、公开导入检查及[需求草案](docs/REQUIREMENTS.md)。

## 本次实际验证

在 Computer-05 的独立临时副本中执行，原目录未被修改：

- Windows Debug build：PASS，0 warning / 0 error。
- Debug `--local-only` 集成检查：PASS，退出码 0。
- 100 文件快照传输 SHA-256 校验通过；原始源文件逐文件摘要见参考目录的 `SOURCE_MANIFEST.json`。

未执行 Release 构建、GUI 验收、真实 Steam API 回归、发布、领取、游戏启动、挂时长或成就写入。历史文档中的早期验证不计作本轮结果。

## 当前结论

原型是可构建的 Windows WPF 应用。它有游戏库、评论研究、编辑器和 SQLite 基础，但尚未完成新需求要求的挂时长、成就修改、全球解锁率和完整发布链路。详见[历史原型分析](docs/LEGACY_ASSESSMENT.md)。

新版本尚未开始实现，Mac 可行性尚未实测。旧原型默认时长、截止日期、双语策略和 Gemini 选择均未被重新确认为新需求。

## 下一步

1. 根据需求草案确认投稿关系、Mac 游戏覆盖范围，以及原型功能的保留范围。
2. 建立限范围技术验证：Mac Steam 会话与时长、成就读写回读、评论发布与结果核实。
3. 根据结果选择跨平台实现方式，再拆分首版开发任务。
