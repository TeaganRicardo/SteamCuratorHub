# 项目状态

更新：2026-10-17。阶段：首版 macOS arm64 桌面 MVP 实现与交付收敛；真实 Steam、发布、付费 API 及用户视觉验收尚未通过。

GitHub 是唯一基线，需求状态以 [REQUIREMENTS.md](docs/REQUIREMENTS.md) 为准。用户于 2026-10-17 授权全权自律开发，进一步要求推进到首版完整功能 MVP，并选择 Gemini API；仅在必要时确认需求与实现细节。未决产品选项与工程默认分别记录。

## 当前工作与精确基线

| 工作 | 分支 / 提交 | 记录与状态 |
| --- | --- | --- |
| 当前 main / MVP 开发基线 | `1cd2f9d3cf346937d55ed98a2b0e03f965258999` | 已包含调研 [PR #8](https://github.com/TeaganRicardo/SteamCuratorHub/pull/8) 与时长工具 [PR #9](https://github.com/TeaganRicardo/SteamCuratorHub/pull/9)；不是 MVP 最终 head |
| 完整桌面 MVP | `codex/mvp`，独立工作树；首次冻结 `d1ea2463626e72e7c04b8bb2584dffea1f4f65bf` | [PR #11](https://github.com/TeaganRicardo/SteamCuratorHub/pull/11) / [Issue #10](https://github.com/TeaganRicardo/SteamCuratorHub/issues/10)：最终 head、审查及 CI 以 PR 记录为准 |
| 时长子实验已审 head | `ee363c01eb876005a539ee70221576a8c3696d98` | PR #9 squash 合并为上述 main；历史工具验证只适用于该子实验 |

原工作目录中的 `.workbuddy-ai/` 和 `deliverables/` 在接手时即未跟踪，保留本机且不纳入提交。验证工具及桌面 MVP 在各自独立 managed worktree 开发，不改参考原型或用户原文件。

## 当前 MVP 实现

使用说明与逐项本机验收步骤见 [MVP.md](docs/MVP.md)。首版采用 Electron + Node.js，成就/统计由独立 Objective-C++ helper 接入当前本机 Steam 客户端；此组合是工程选择，不将私有 ABI 的 fixture 成功解释为实机可用。

- 库与商店搜索、游戏介绍、个人评测入口；单游戏唯一群组任务，累计时长目标与进度分开。
- 同账号多 AppID 并行协议运行、单停、本次期限与累计目标；Steam 总分钟回读、本机 elapsed 和运行历史分别记录，异常停止不自动续跑。
- 成就搜索/筛选/全球百分比/排序，单项及批量解锁/重锁，INT/FLOAT 统计修改；预览、账号/库 digest/旧值校验、Store 回调及独立进程回读，逐项保留 failed/unknown。
- Steam 评论样本、Brave 名称搜索与实际媒体取文、来源身份/覆盖状态；Gemini 生成研究摘要和建议稿，翻译、短评摘要及 BBCode 排版。只有 Gemini 是 AI 供应商；Brave 是独立搜索工程选型。
- 中英文文章草稿、版本冲突处理、BBCode 原文/预览、可编辑群组尾巴、纯文本短评与独立全文链接；AI 建议不会静默覆盖手改草稿。
- 独立 Steam 网页会话、冻结发布内容与账号/目标校验、辅助填表；本人提交、结果 URL、人工确认与长评 API 回读核验分别记账。鉴赏家直接提交未实现为无条件能力。
- 本机有界 JSON 工作区、系统加密或会话内凭据、备份导出/恢复、损坏文件恢复路径；Electron fixture smoke 与 arm64 ad-hoc 打包脚本。

工程默认：简体中文界面，文章语言独立；隐藏成就默认折叠；严格少于 200 个 Unicode 码点；关窗清理、先到先停；不做自动领取或云同步。当前 Windows 原生写入/安装包、最低 macOS 版本、Developer ID 签名/公证仍未验收。

## 当前验证范围与待完成证据

首次冻结 head 的全套 **191/191 离线测试**、31 个桌面模块语法检查通过。其后独立审查修复全文链接目标校验、导入原生证据来源、凭据设置原子性及损坏凭据恢复；Windows CI 发现的只读句柄 fsync 失败已改为可写句柄持久保存，并补回归。最终测试数量与同一 head 的七个 CI 任务结果见 PR #11，保留中间 head 的失败记录。

含开发依赖的 `npm audit` 为 0；原生 fixture 与 synthetic dylib 检查通过。Electron 44.9.0 的源码 fixture 与打包后 fixture 均已实际启动通过，覆盖七个页面切换、Node 隔离、原生 helper 合成数据、研究与发布准备。该运行时 SHA-256 校验值为 `12d873a5ae98a6598e730402e9278c5d132e97ea1a4405a7713bec2a3e83921f`。

本机产物为 `dist/Steam Curator Hub-darwin-arm64/Steam Curator Hub.app`，ad-hoc `codesign --verify --deep --strict` 已通过；元数据和 smoke 报告在本机 `.local/package.json`、`.local/mvp-smoke.json` 与 `.local/packaged-smoke.json`。包声明最低 macOS 13.0，但仅本机 macOS 27 执行过当前运行检查，不能据此宣称 13.0 兼容已确认。最终独立审查、同一提交的 CI 状态和重建记录在 head 冻结后补入；以上中间工作树证据不能替代最终 head 收敛。

CI 保留 macOS / Windows / Linux × Node 22 / 24 的离线语法、测试与时长 fixture；新增 `macos-15` arm64 的原生 fixture、Electron smoke 与本机打包检查。非 macOS 的原生平台测试允许明确 skip；CI 不提供凭据、不连接 Steam、不调用付费 API，不上传构建或用户数据产物。

真实 Steam 登录/分钟增长、未安装 Windows-only 游戏上的原生读写、官方网页字段及发布结果、Gemini/Brave 实际 API、Gatekeeper 分发和用户视觉检查都保留为未通过门槛，由用户在本机执行。fixture 与私有 ABI 合成库测试只证明相应本地路径；没有真实结果时不关闭 #5/#6/#7。

## 前序调研与子实验

- [Steam 能力调研](docs/research/steam-feasibility.md)：区分 Web API、协议时长与本地成就/统计写入；有固定上游版本、许可与回读要求，实际 Mac 兼容性仍待用户验证。
- [发布与会话调研](docs/research/publishing-feasibility.md)：记录个人长评、短评和独立全文链接、框架会话路径及 #6 验证方案；文档本身不证明当前账号表单可用。
- [AI 调研](docs/research/ai-feasibility.md)：早期候选服务、实际取文、作品匹配、来源/生成隔离与失败处理；当前供应商已按需求更新为 Gemini，媒体检索工程实现为 Brave。
- [时长验证工具 PR #9](https://github.com/TeaganRicardo/SteamCuratorHub/pull/9)：两个 AppID 完整集合请求、单停保留另一款、前中后 Steam 分钟回读；fixture 明确不能通过 Steam 验收，异常清理与本机证据保存。

前序在 `ee363c01eb876005a539ee70221576a8c3696d98` 执行 18 项时长工具离线测试与 fixture，两个独立审查复核通过，补修登录超时取消 Guard 提示及读回跨时钟间隙。该记录只描述子实验，不覆盖当前桌面模块。PR #8/#9 合并状态和精确提交见 GitHub。

## 后续顺序与真实门槛

1. [#10](https://github.com/TeaganRicardo/SteamCuratorHub/issues/10) 收敛完整 MVP 的最终 head、独立审查、CI、本机 smoke 与 ad-hoc 构建，保留与真实验收的边界。
2. [#5](https://github.com/TeaganRicardo/SteamCuratorHub/issues/5)：用户选择至少两款拥有的 AppID，至少一个未安装且 Windows-only；验证并行分钟增长、单停、成就解锁/重锁与 INT/FLOAT 提交回调和独立回读。
3. [#6](https://github.com/TeaganRicardo/SteamCuratorHub/issues/6)：用户执行真实官方网页登录、目标/字段检查、填表及发布；长短评各自保留结果和来源，未知结果先核实再重试。
4. [#7](https://github.com/TeaganRicardo/SteamCuratorHub/issues/7)：根据 #5/#6 结果核实当前工程选择的实际兼容范围，再确定最低系统版本和后续 Windows/正式分发路线。
5. 用户本机填写 Gemini 模型/密钥及 Brave Search 密钥，验证实际检索、取文与生成。当前不执行付费试调用，也不将空配置下的 Steam 样本报告为完整站外研究。

## 既有参考快照与历史验证

2026-09-22 从 Computer-05 指定目录接收 100 个源文件、项目文件与历史文档，保存在 `reference/legacy-windows/`，不含会话、真实数据库、构建产物或第三方离线网页。快照 SHA-256 传输校验通过，原始摘要见 `SOURCE_MANIFEST.json`。

当时在独立临时副本中执行 Windows Debug build（0 warning / 0 error）及 `--local-only` 集成检查，均通过。它们属于历史 Windows 原型结果，不是新版或本轮真实 Steam 验证。分析见 [LEGACY_ASSESSMENT.md](docs/LEGACY_ASSESSMENT.md)。
