# Steam 并行时长验证工具

范围：Issue [#5](https://github.com/TeaganRicardo/SteamCuratorHub/issues/5) 的**时长子实验**。它是独立 Node.js CLI，不代表已经选定桌面框架，不修改参考原型。成就/统计写入、GUI 和真实游戏验收仍未实现/执行，不能用本工具关闭 #5 或决定 #7。

## 本轮验收条件

- 离线 fixture 不登录、不导入 Steam 适配器、不访问 Steam 文件、不等候真实分钟；输出始终标为 `fixture_only`，不会产生“Steam 能力已通过”。
- 用户手动运行 live：核对计划 SteamID64；在同一个会话请求两个 AppID，先停止一款并保留另一款；按计划停止全部。
- 读取 Steam `playtime_forever`，保留启动前、单停后、全部停止后的分钟快照；缺失数据不按零处理。命令发送不视为服务端确认。
- 断线、另一会话在玩、取消、睡眠/时钟异常及保存失败时中断并尝试清空本会话和注销，不自动重连、踢出其他会话或重试。
- 结果只写本机；凭据、原始服务端错误、SteamID64 不写结果。已有文件拒绝覆盖。

## 离线运行（开发者可执行）

需要 Node.js 22 或更新版本：

```sh
npm ci --ignore-scripts
npm test
npm run probe:fixture
```

默认报告位于忽略的 `.local/steam-validation/<run-id>.json`。`state=collected` 只表示采集流程结束，`acceptance=pending_user_verification` 在所有模式均保留。开发过程的报告同时记录 `sourceHead` 和 `sourceTree`，dirty 工作树不能声称等于已提交版本。

## 真实实验（由用户执行）

先选定自己拥有的两个 AppID，至少一个同时满足未安装且 Windows-only。工具中的 `uninstalled` / `windowsOnly` 是**用户提供的事实标记**，工具没有扫描磁盘或读取商店来自动验证它们。应另存商店平台/安装状态和 Steam 客户端版本的本机证据。

1. 复制 `plan.example.json` 到 `.local/plan.local.json`，填写自己已核对的 SteamID64 和 AppID。示例的 0 和占位 ID 故意无效，不能误运行。两款分别默认请求 15 / 30 分钟；可设置 60–3600 秒。两次停止间隔须大于回读等候时间（1–120 秒）再加 15 秒，为库的 10 秒请求超时和调度保留余量。这些是实验时长，不是产品累计目标。
2. 关闭正在玩的真实游戏，保留 Steam 客户端。不要在同一账号同时运行另一个验证实例。确认没有账号/游戏冲突后，由本人从交互终端运行：

   ```sh
   node tools/steam-validation/cli.mjs --live --plan .local/plan.local.json
   ```

3. 本人输入账号、密码和 Steam Guard；密码及验证码不回显，程序不保存 token、会话或 machine-auth 文件。需要 Steam 手机端确认时由本人操作。CLI 不读取 Steam 安装目录、浏览器 cookie 或系统钥匙串。
4. 检查报告 `snapshots`。两个目标均需 Steam 回读增长；单停后观察第二款继续，结合请求时间线和中间/最终快照判断。`increase_observed` 只表示计数增长，不能独自证明增长一定由本实验产生，也不证明两款整个区间均在计时。
5. 检查 `cleanup` 与 Steam 客户端实际运行状态。`cleanup=logged_off` 表示库观察到连接断开；`cleanupClear=requested_without_acknowledgement` 明确没有服务器停止确认。退出、崩溃及强杀后仍需用户核对真实状态。

账号私密设置或服务器缓存可能导致库列表缺失/时长延迟。缺少目标时会停止；没有增长时记 `no_increase_observed`，不能据此直接宣布功能不支持。等待后另行读取/核对，保留原报告；本工具不自动重新运行游戏请求。暂停/重连/重启的完整生命周期仍属于后续产品实现及实测范围。

系统睡眠、系统时间倒退或事件循环长时间停顿会中断。本工具不会阻止睡眠；实验时用户应保持机器唤醒。无法捕获的进程强杀不能承诺清理成功。

## 依赖与边界

- [`steam-user@5.3.0`](https://www.npmjs.com/package/steam-user/v/5.3.0)，MIT；上游 [npm 发布对应源码](https://github.com/DoctorMcKay/node-steam-user/tree/e39d7cb8e0d4c83905f2d3896fb354b13f2591ec)。仅 live 动态导入。`gamesPlayed(apps, false)` 发完整数组；它没有持久化 ack，`appLaunched` / `appQuit` 也不作为 Steam 计时证据。
- 2026-10-17 初次安装的传递依赖审计出现高危/严重项；锁定 override `adm-zip=0.6.1`、`protobufjs=7.6.6`（含 `steam-appticket` 原先的 6.x）后 `npm audit` 为零。测试包含构造器、所用协议消息和 app-ticket protobuf 的本地编码/解码 smoke；**未证明所有上游功能或真实认证兼容性**。不用 `npm audit fix --force` 降级整个 Steam 客户端。
- 仓库未指定整体开源许可证。`private=true`、`UNLICENSED` 防止意外 npm 发布，不为参考原型或第三方重新授予许可证；不分发 Steam SDK/客户端二进制。

来源：上游 [`gamesPlayed` / playingState / getUserOwnedApps 文档](https://github.com/DoctorMcKay/node-steam-user/blob/e39d7cb8e0d4c83905f2d3896fb354b13f2591ec/README.md)，[Steam 调研](../../docs/research/steam-feasibility.md)。

## 后续实际验收记录

保留脱敏结果表：平台/架构、Steam 客户端版本、精确 Git SHA、两个 AppID、未安装/平台证据、前中后 Steam 总分钟、单停后的第二款变化、退出结果、失败/未知原因。报告和本机计划不提交 Git。成就解锁/重锁、整型/浮点统计修改须由独立 native 实验完成提交回调和独立回读，当前值始终为 `not_tested`。
