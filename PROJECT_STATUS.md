# 项目状态

更新：2026-10-29。阶段：macOS arm64 桌面 MVP、P0 会话/并行操作与时长回读一致性修复已合并；正式界面按已确认的第四轮方向实现与验证；真实 Steam、发布、付费 API、视觉验收及正式分发尚未通过。

GitHub 是唯一基线，需求状态以 [REQUIREMENTS.md](docs/REQUIREMENTS.md) 为准。用户于 2026-10-17 授权全权自律开发，进一步要求推进到首版完整功能 MVP，并选择 Gemini API；仅在必要时确认需求与实现细节。未决产品选项与工程默认分别记录。

## 当前工作与精确基线

| 工作 | 分支 / 提交 | 记录与状态 |
| --- | --- | --- |
| 本轮源码基线（`main`） | `21aeb3e8f6baffb57ea439d783bff943add2408f` | [PR #20](https://github.com/TeaganRicardo/SteamCuratorHub/pull/20) 已合并用户确认需求与交付状态；生产代码承接 [PR #18](https://github.com/TeaganRicardo/SteamCuratorHub/pull/18) 的时长修复 |
| 正式游戏工作台 | `codex/formal-workbench-20261029` | [草稿 PR #21](https://github.com/TeaganRicardo/SteamCuratorHub/pull/21) 已按用户最终确认接入生产服务；282/282 本机测试、源码 Electron fixture 和独立复核完成；安装包与 CI 记录见 PR，[实施范围](docs/ux/implementation.md)保留真实验收门槛 |
| 会话与并行操作修复（`main` 历史提交） | `ba47a2b23ef4553aa5db5594eaa4aff4a26da1c7` | [PR #17](https://github.com/TeaganRicardo/SteamCuratorHub/pull/17) 已于 2026-10-18 合并；[对应主线 CI 成功](https://github.com/TeaganRicardo/SteamCuratorHub/actions/runs/39151447941) |
| 时长回读一致性修复 | 最终 PR head `60ddf0d540426e300b02889a4e298c46f9403b2b` | [PR #18](https://github.com/TeaganRicardo/SteamCuratorHub/pull/18) 已合并；修复、独立审查、本机构建与[七项 CI 成功](https://github.com/TeaganRicardo/SteamCuratorHub/actions/runs/39631664594)均对应此 head |
| 正式前端交互原型 | `prototype/ux-direction-20261021`；`7056f19d4aacd4dcdfffe88cd91ffc9a2dc0c388` | [草稿 PR #19](https://github.com/TeaganRicardo/SteamCuratorHub/pull/19) 保留可操作草图与历史验证记录；2026-10-29 整套方向已获确认，正式实现另见本轮分支 |
| MVP 合并点（`main` 历史提交） | `731c6a1aea973f4ddf786042936b8a4f610d0a48` | [PR #11](https://github.com/TeaganRicardo/SteamCuratorHub/pull/11) 已于 2026-10-18 squash 合并；[主分支 CI 七项成功](https://github.com/TeaganRicardo/SteamCuratorHub/actions/runs/39134308435) |
| 完整桌面 MVP 原 PR head | `3696effea408e93378aada8e71ac842f78a3926c` | [PR #11](https://github.com/TeaganRicardo/SteamCuratorHub/pull/11) 最终复审和补丁；[同 head 七项 CI 成功](https://github.com/TeaganRicardo/SteamCuratorHub/actions/runs/39134063102)；[#10](https://github.com/TeaganRicardo/SteamCuratorHub/issues/10) 保留真实验收门槛 |
| 前期 Steam 验证工具 | `ee363c01eb876005a539ee70221576a8c3696d98` | [PR #9](https://github.com/TeaganRicardo/SteamCuratorHub/pull/9) 已先前 squash 合并；18 项测试只证明独立子实验 |

原工作目录中的 `.workbuddy-ai/` 和 `deliverables/` 在接手时即未跟踪，保留本机且不纳入提交。验证工具及桌面 MVP 在各自独立 managed worktree 开发，不改参考原型或用户原文件。

## 当前 MVP 实现

使用说明与逐项本机验收步骤见 [MVP.md](docs/MVP.md)。首版采用 Electron + Node.js，成就/统计由独立 Objective-C++ helper 接入当前本机 Steam 客户端；此组合是工程选择，不将私有 ABI 的 fixture 成功解释为实机可用。

- 库与商店搜索、游戏介绍、个人评测入口；单游戏唯一群组任务，累计时长目标与进度分开。
- 同账号多 AppID 并行协议运行、单停、本次期限与累计目标；Steam 总分钟回读、本机 elapsed 和运行历史分别记录，异常停止不自动续跑。
- 成就搜索/筛选/全球百分比/排序，单项及批量解锁/重锁，INT/FLOAT 统计修改；预览、账号/库 digest/旧值校验、Store 回调及独立进程回读，逐项保留 failed/unknown。
- Steam 评论样本、Brave 名称搜索与实际媒体取文、来源身份/覆盖状态；Gemini 生成研究摘要和建议稿，翻译、短评摘要及 BBCode 排版。只有 Gemini 是 AI 供应商；Brave 是独立搜索工程选型。
- 中英文文章草稿、版本冲突处理、直接排版编辑与复制时输出 Steam BBCode、可编辑群组尾巴、纯文本短评与独立全文链接；AI 建议不会静默覆盖手改草稿。
- 独立 Steam 网页会话、冻结发布内容与账号/目标校验、辅助填表；本人提交、结果 URL、人工确认与长评 API 回读核验分别记账。鉴赏家直接提交未实现为无条件能力。
- 本机有界 JSON 工作区、系统加密或会话内凭据、备份导出/恢复、损坏文件恢复路径；Electron fixture smoke 与 arm64 ad-hoc 打包脚本。

工程默认：简体中文界面，文章语言独立；隐藏成就默认折叠；严格少于 200 个 Unicode 码点；关窗清理、先到先停；不做自动领取或云同步。当前 Windows 原生写入/安装包、最低 macOS 版本、Developer ID 签名/公证仍未验收。

## 当前验证范围与待完成证据

PR #11 的原冻结版本先有 **191/191 离线测试**通过，后续修复全文链接目标校验、导入原生证据来源、凭据设置原子性及损坏凭据恢复，并修复 Windows CI 中只读句柄 fsync 失败。2026-10-18 复审还发现生产媒体 HTTPS GET 路径遗漏 `req.end()`；已在 `dbbc83d` 修复，并于 `3696eff` 添加请求发送和私有 DNS 地址拒绝两项离线回归。现最终 PR head 和已合并的主线各自七项 CI 全绿；历史失败仍保留在 Actions 记录，不能据此跳过真实 API 验收。

含开发依赖的 `npm audit` 为 0；原生 fixture 与 synthetic dylib 检查通过。Electron 44.9.0 的源码 fixture 与打包后 fixture 均已实际启动通过，覆盖七个页面切换、Node 隔离、原生 helper 合成数据、研究与发布准备。该运行时 SHA-256 校验值为 `12d873a5ae98a6598e730402e9278c5d132e97ea1a4405a7713bec2a3e83921f`。

前期本机保留 `dist/Steam Curator Hub-darwin-arm64/Steam Curator Hub.app` 及 `.local/package.json`、`.local/mvp-smoke.json`、`.local/packaged-smoke.json` 报告；原冻结版本 ad-hoc `codesign --verify --deep --strict` 通过。当前 `main` 的原生 fixture、源码/打包 Electron smoke、ad-hoc 打包检查已由对应 GitHub Actions 重新执行成功，但 CI 不上传安装包，GitHub 暂无供用户直接下载的正式构建。声明最低 macOS 13.0，实际本机仅 macOS 27 检查过；无法据此宣称最低系统版本兼容。尚未 Developer ID 签名或公证。

CI 保留 macOS / Windows / Linux × Node 22 / 24 的离线语法、测试与时长 fixture；新增 `macos-15` arm64 的原生 fixture、Electron smoke 与本机打包检查。非 macOS 的原生平台测试允许明确 skip；CI 不提供凭据、不连接 Steam、不调用付费 API，不上传构建或用户数据产物。

真实 Steam 登录/分钟增长、未安装 Windows-only 游戏上的原生读写、官方网页字段及发布结果、Gemini/Brave 实际 API、Gatekeeper 分发和用户视觉检查都保留为未通过门槛，由用户在本机执行。fixture 与私有 ABI 合成库测试只证明相应本地路径；没有真实结果时不关闭 #5/#6/#7。

## 前序调研与子实验

- [Steam 能力调研](docs/research/steam-feasibility.md)：区分 Web API、协议时长与本地成就/统计写入；有固定上游版本、许可与回读要求，实际 Mac 兼容性仍待用户验证。
- [发布与会话调研](docs/research/publishing-feasibility.md)：记录个人长评、短评和独立全文链接、框架会话路径及 #6 验证方案；文档本身不证明当前账号表单可用。
- [AI 调研](docs/research/ai-feasibility.md)：早期候选服务、实际取文、作品匹配、来源/生成隔离与失败处理；当前供应商已按需求更新为 Gemini，媒体检索工程实现为 Brave。
- [时长验证工具 PR #9](https://github.com/TeaganRicardo/SteamCuratorHub/pull/9)：两个 AppID 完整集合请求、单停保留另一款、前中后 Steam 分钟回读；fixture 明确不能通过 Steam 验收，异常清理与本机证据保存。

前序在 `ee363c01eb876005a539ee70221576a8c3696d98` 执行 18 项时长工具离线测试与 fixture，两个独立审查复核通过，补修登录超时取消 Guard 提示及读回跨时钟间隙。该记录只描述子实验，不覆盖当前桌面模块。PR #8/#9 合并状态和精确提交见 GitHub。

## 2026-10-18 用户首轮真实构筑反馈

本轮用户已在 RDC 主机自行编译并体验最新构筑（未测试 Gemini/Brave API key 功能及真实发布）。新增三个首版 P0 阻断：

- [#13](https://github.com/TeaganRicardo/SteamCuratorHub/issues/13)：当前 MVP UI/UX 判定不可用；MVP 界面仅保留为功能验证壳，正式前端实现前必须经过多轮用户交互确认。
- [#14](https://github.com/TeaganRicardo/SteamCuratorHub/issues/14)：协议登录提交后存在反馈空窗，且当前 SteamService 显式关闭 auto relogin / refresh-token renewal；要求可见登录状态、加密持久会话及应用内有界重连。
- [#15](https://github.com/TeaganRicardo/SteamCuratorHub/issues/15)：当前 controller / renderer 主动互斥协议模拟运行与 native 成就/统计操作；用户确认两者必须可同时使用，需要改 contract 与回归测试。

因此“完整 MVP 已实现”仅成立于离线功能接线层面，不能视为产品可用或发布候选。#10 保持 Open。

## 2026-10-18 P0 底层修复

接手远端 `main` 为 `7f3fb8ad26f187a8a80f7cfb17f9369e36043c6c`；复核并合并仅同步用户需求的 [PR #16](https://github.com/TeaganRicardo/SteamCuratorHub/pull/16) 后，开发基线为 `24be6a8eea3ae44c8c82c7a1054a1fa3c1d1b237`。修复分支 `codex/session-and-stats-fixes` 使用独立工作树，不改已有用户目录或 MVP 工作树；[PR #17](https://github.com/TeaganRicardo/SteamCuratorHub/pull/17) 已合并至 `ba47a2b23ef4553aa5db5594eaa4aff4a26da1c7`，其交付记录包含最终 head、审查、构建及 CI 证据。

- #14：登录提交、Guard、成功/失败保持同一可见上下文，包含账号、阶段与明确错误；加密保存已确认账号的 refresh token 并接入启动恢复与续期。应用管理三次有界重连；退出应用保留保存会话，显式退出连接清除；密码不保存，认证恢复不恢复模拟运行。
- #15：移除 controller 和 renderer 的全局 run/native 互斥。保留原生操作自身串行、账号/预览/旧值校验和未知写入门禁；新增真实 controller + SteamService 的合成边界回归，覆盖相同/其他 AppID 读写、运行 preflight 并行、写入期间独立启停和完整 `gamesPlayed` 集合。
- 凭据回滚文件不保留 Steam 会话；设置预备提交检测凭据版本，避免覆盖刚续期或清除的会话。加密不可用或写盘失败保留明确 session-only / persistenceError，不使用明文回退。
- 本机已执行 241/241 项完整离线测试、桌面语法检查与时长 fixture，含开发依赖的漏洞审计为 0。Electron fixture smoke 已通过：在同一 AppID active 时执行真实 helper 的合成读写，检查双运行、单停和 Node 隔离；最终构建与精确提交结果以 PR #17 的交付记录为准。

#13 的正式 UI 仍冻结；本轮只修功能验证壳的登录状态反馈。#14/#15 的代码回归不替代用户真实 Steam 重验；#5/#6/#7/#10 继续保持开放，不据此标记发布候选。

## 2026-10-21 时长回读一致性修复

在独立工作树重新安装锁定依赖后，当前主线 241/241 项离线测试通过。新增回归先证明 `10 → 30 → 25` 分钟被错误归为正常增长；修复后，每笔读数同时与起点和上一笔观察比较。回落的真实读数仍保留，标为 inconsistent，使用现有 unknown 清理路径停止活动请求，不自动重连或续跑。相同读数保持可用。

修复后的完整离线测试 242/242 通过，桌面语法检查、时长 fixture 和含开发依赖的漏洞审计通过（0 漏洞）。本轮不连接真实 Steam、不调用付费 API、不发布评测；#13 正式前端冻结及 #5/#6/#7/#10/#14/#15 的人工验收门槛继续保留。

2026-10-29 收尾核实：最终 head `60ddf0d` 的七项 CI 均成功，规范与需求两路独立复核各为 0 项发现。本机构建的 26 个已记录源码文件与该 head 字节一致，arm64 二进制、ZIP 摘要和严格签名验证通过；源码及打包 Electron fixture 的原记录均为通过。随后将 PR #18 合并为 `e53a902`，合并提交的源码树与候选一致。此次收尾未新增真实 Steam 验收证据。

## 后续顺序与真实门槛

[#13](https://github.com/TeaganRicardo/SteamCuratorHub/issues/13) 的用户确认已覆盖游戏工作台、正文优先且资料按需展开、可拖拽宽度、输入即排版和复制时输出 Steam 格式。用户于 2026-10-29 明确回复“已确认，继续”，授权按整套方向开始正式实现。后续连接、查找、任务与恢复路径见 [PR #19 的流程草案](https://github.com/TeaganRicardo/SteamCuratorHub/blob/7056f19d4aacd4dcdfffe88cd91ffc9a2dc0c388/docs/ux/round-4.md)，正式实现使用独立分支 `codex/formal-workbench-20261029`，范围和验证记录见[实施说明](docs/ux/implementation.md)。原型的 35 项模块语法检查、241/241 离线测试和[七项 CI](https://github.com/TeaganRicardo/SteamCuratorHub/actions/runs/39647880806)均已通过；原型与主线修复分别验证。

1. [#10](https://github.com/TeaganRicardo/SteamCuratorHub/issues/10)：首版 MVP 代码已经合并，主线 CI 全绿；等待用户执行真实 Steam、AI、发布和视觉验收再判定最终关闭。
2. [#5](https://github.com/TeaganRicardo/SteamCuratorHub/issues/5)：用户选择至少两款拥有的 AppID，至少一个未安装且 Windows-only；验证并行分钟增长、单停、成就解锁/重锁与 INT/FLOAT 提交回调和独立回读。
3. [#6](https://github.com/TeaganRicardo/SteamCuratorHub/issues/6)：用户执行真实官方网页登录、目标/字段检查、填表及发布；长短评各自保留结果和来源，未知结果先核实再重试。
4. [#7](https://github.com/TeaganRicardo/SteamCuratorHub/issues/7)：根据 #5/#6 结果核实当前工程选择的实际兼容范围，再确定最低系统版本和后续 Windows/正式分发路线。
5. 用户本机填写 Gemini 模型/密钥及 Brave Search 密钥，验证实际检索、取文与生成。当前不执行付费试调用，也不将空配置下的 Steam 样本报告为完整站外研究。

## 既有参考快照与历史验证

2026-09-22 从 Computer-05 指定目录接收 100 个源文件、项目文件与历史文档，保存在 `reference/legacy-windows/`，不含会话、真实数据库、构建产物或第三方离线网页。快照 SHA-256 传输校验通过，原始摘要见 `SOURCE_MANIFEST.json`。

当时在独立临时副本中执行 Windows Debug build（0 warning / 0 error）及 `--local-only` 集成检查，均通过。它们属于历史 Windows 原型结果，不是新版或本轮真实 Steam 验证。分析见 [LEGACY_ASSESSMENT.md](docs/LEGACY_ASSESSMENT.md)。
