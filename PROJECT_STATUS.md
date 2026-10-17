# 项目状态

更新：2026-10-17。阶段：技术可行性调研收敛，独立验证工具开发；新版桌面框架尚未决定。

GitHub 是唯一基线，需求状态以 [REQUIREMENTS.md](docs/REQUIREMENTS.md) 为准。用户于 2026-10-17 授权全权自律开发，仅在必要时确认需求与实现细节；已确认范围可按独立分支、PR、审查和验证推进，未决产品选项不自动转成已确认需求。

## 当前工作与精确基线

| 工作 | 分支 / 提交 | 记录与状态 |
| --- | --- | --- |
| 本轮接手 main | `main` / `de0adcbbb9df995e3663a45db10f834eaeecda88` | 2026-10-17 重新 fetch 并核实 GitHub；不是最终交付 SHA |
| 已有 Steam 调研 | `docs/global-project-plan` / 接手时 `272498910bb8445c39936b2ca2954a10aa0c9822` | [PR #8](https://github.com/TeaganRicardo/SteamCuratorHub/pull/8)：补充来源、发布与 AI 调研；最终 head、审查和合并结果见 PR |
| 时长子实验工具 | `codex/steam-validation` / 已复核实现 `ee363c01eb876005a539ee70221576a8c3696d98` | [PR #9](https://github.com/TeaganRicardo/SteamCuratorHub/pull/9)：独立工作树、离线测试和用户运行的 live 模式；后续修复及最终 head 以 PR 为准 |

原工作目录中的 `.workbuddy-ai/` 和 `deliverables/` 在接手时即未跟踪，保留本机且不纳入本轮提交。验证工具在独立 managed worktree 开发，不改参考原型或用户原文件。

## 本轮交付与结论

- [Steam 能力调研](docs/research/steam-feasibility.md)：区分 Web API、协议时长与本地成就/统计写入；补充固定上游版本、许可与回读要求。多 AppID 与无安装依赖有候选依据；目标 Mac 的真实可用性仍待用户实验。
- [发布与会话调研](docs/research/publishing-feasibility.md)：记录个人长评、短评和独立全文链接、三个框架的会话路径及 #6 验证方案。未取得当前账号表单，未确认平台字符计数或实际发布可用性。
- [AI 调研](docs/research/ai-feasibility.md)：从游戏名检索、实际取文、作品匹配、来源/生成隔离与失败处理；候选服务尚未选定，未调用付费 API。Steam 评论新接口与供应商保存/使用约束按当前一手文档记录。
- [时长验证工具 PR #9](https://github.com/TeaganRicardo/SteamCuratorHub/pull/9)：两个 AppID 完整集合请求、单停保留另一款、前中后 Steam 分钟回读；fixture 明确不能通过 Steam 验收；异常清理与本机证据保存。仅实现 #5 的时长子实验，成就/统计写入未实现。

## 本次实际执行的验证

- 本机 macOS 27.0.1 / arm64、Node.js 26.10.0：时长工具在 `ee363c01eb876005a539ee70221576a8c3696d98` 的 18 项离线测试通过，fixture 完整运行。覆盖目标/账号校验、并行集合、单停、回读缺失/延迟取消、断线/冲突、保存失败、清理未知、睡眠/时钟异常与依赖加载/编解码。
- 锁定传递依赖修复后 `npm audit` 为 0；仅证明本次已知漏洞审计与测试覆盖的路径，真实认证及所有上游功能未验证。
- 两个独立审查在同一实现 head 复核通过：补修登录超时取消 Guard 提示与读回跨时钟间隙；合成 PTY 验证超时后退出，无真实登录。
- PR #9 配置 macOS / Windows / Linux × Node 22 / 24 CI。最终 head 的运行结果与独立审查记录在 PR，不能把首次实现的测试迁移成后续版本的验证。
- 三份报告依据一手文档/源码，执行相对链接和差异空白检查；文档调研不等于接口实测。

未执行真实 Steam 登录、游戏运行/时长增长、成就解锁/重锁、整型/浮点统计提交、网页填表/登录/发布、付费 AI 调用或 GUI 验收。未发布可安装的新应用。

## 需求与后续顺序

1. [#2](https://github.com/TeaganRicardo/SteamCuratorHub/issues/2)、[#3](https://github.com/TeaganRicardo/SteamCuratorHub/issues/3)、[#4](https://github.com/TeaganRicardo/SteamCuratorHub/issues/4) 按三份调研报告收敛；只关闭资料调研，不关闭实机门槛。
2. [#5](https://github.com/TeaganRicardo/SteamCuratorHub/issues/5) 保持开放：用户选两款拥有的 AppID（至少一个未安装且 Windows-only），手动运行时长子实验并核对 Steam；继续开发独立成就/统计 native 实验，必须观察提交回调和独立回读，不能以幂等设值或 SDK 布尔返回替代。
3. [#6](https://github.com/TeaganRicardo/SteamCuratorHub/issues/6) 保持开放：按发布报告建立最小会话/表单验证壳；登录、游戏实机、视觉与真实发布验收由用户执行。模板与字符计数等到相应流程再确认。
4. [#7](https://github.com/TeaganRicardo/SteamCuratorHub/issues/7) 等 #5/#6 的实际证据后决定桌面框架、Steam 组件、最低系统版本与分发组合。独立 CLI 的 Node.js 不构成桌面框架定案。
5. 按确定的技术组合逐个交付库/任务、研究、编辑与发布流程。供应商和付费预算只在真实 AI 接入前确认；无需一次性决定所有产品选项。

## 既有参考快照与历史验证

2026-09-22 从 Computer-05 指定目录接收 100 个源文件、项目文件与历史文档，保存在 `reference/legacy-windows/`，不含会话、真实数据库、构建产物或第三方离线网页。快照 SHA-256 传输校验通过，原始摘要见 `SOURCE_MANIFEST.json`。

当时在独立临时副本中执行 Windows Debug build（0 warning / 0 error）及 `--local-only` 集成检查，均通过。它们属于历史 Windows 原型结果，不是新版或本轮真实 Steam 验证。分析见 [LEGACY_ASSESSMENT.md](docs/LEGACY_ASSESSMENT.md)。
