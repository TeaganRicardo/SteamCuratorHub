# Windows 历史原型分析

日期：2026-09-22。方法：当前工作树源码检查，Windows Debug 构建及 local-only 检查。未运行 GUI 或真实 Steam 操作。

源码根目录：[reference/legacy-windows](../reference/legacy-windows/README.md)。以下“已有”表示源码存在，并不等于真实服务端验收完成。

## 1. 总体判断

这是具有实际 HTTP 读取、持久化和测试的 Windows 原型，不是仅有界面的展示稿。WPF、Windows 目标框架、WebView2 和 DispatcherTimer 使其不能直接当作 Mac 首版。

源码共 100 个导入文件，约 975 KB。`MainWindowViewModel.cs` 有 1,887 行，协调游戏库、运行、编辑、发布和历史；新服务与旧 UI 并存是主要接线问题。用户已明确跨平台版本重新选择技术栈，无需复用本原型代码。此分析只记录旧版本事实和设计参考，不能将原型交接清单当作新项目的开发顺序。

## 2. 能力清单

| 功能 | 源码证据 | 当前边界 |
|---|---|---|
| 库与近期游戏 | `SteamWebApiClient`、`SteamGameCatalogService` | 有真实 HTTP 读取；本次只有测试适配器验证 |
| 库/商店搜索 | `MainWindowViewModel`、`InventoryPanel` | 区分搜索范围，防抖、取消、分页、迟到结果隔离 |
| 商店详情与评论 | `ReviewResearchViewModel`、`GameStoreDetailsPanel`、`ReviewResearchPanel` | 双语详情、评论研究、部分失败回退 |
| AI 翻译和摘要 | `FreeGeminiService` | HTTP 实现与模拟测试存在；没有验证当前模型可用性和免费额度 |
| 长评编辑 | `EditorPanel`、`EditorPanel.xaml.cs` | BBCode 工具栏、保存草稿；未做视觉验收 |
| 本地持久化 | `SqliteReviewTaskRepository` | 任务、草稿、发布、账号隔离等有本地验证 |
| 本地会话服务 | `ReviewTaskPlaySessionService` | 有单调计时、checkpoint；主 UI 仍用旧计时 |
| Steam 挂时长 | 未找到对应实现 | Steam URI 启动器及本地秒数不能替代该功能 |
| 成就查询 | `SteamAchievementRefreshService`、`SteamWebApiClient` | 只读、双语合并；新刷新服务未完整接入主 UI |
| 成就修改与全球率 | 未找到写入/全球百分比实现 | 原型不能满足本次类 SAM 必需需求 |
| 多群组配置 | `CuratorPublishingSettings`、`JsonCuratorPublishingSettingsService` | 核心服务存在；UI 仍绑定示例 `GroupOptions` |
| 发布准备/确认 | `ReviewPublicationWorkflow` | 有草稿版本、过期和重复确认保护；未接入旧发布按钮 |
| 页面填充 | `SteamPublishingAutomationScripts`、`SteamWebViewService.Publishing` | 填入长评/短评，定位 offer；不点击提交 |
| Curator Connect | `CuratorCopyImportService`、`CuratorCopyWorkflow` | 发现/合并/准备核心存在；UI 尚有直接本地领取记录 |
| 历史草稿 | `HistoryDraftService`、`HistoryDraftsViewModel` | 核心与 SQLite 测试存在；主界面仍用旧时间线 |

## 3. 与当前需求直接相关的缺口

### 3.1 本地时长与 Steam 时长混淆

[`MainWindowViewModel`](../reference/legacy-windows/SteamCuratorHub.App/Presentation/ViewModels/MainWindowViewModel.cs) 的 `StartRun` 只开启本地定时器；`TickRun` 每次增加 `1d / 3600`；`MarkComplete` 直接把 `PlayedHours` 设置为 `TargetHours`。这些操作没有证明 Steam 收到了运行会话。

[`ReviewTaskPlaySessionService`](../reference/legacy-windows/SteamCuratorHub.App/Infrastructure/Services/ReviewTaskPlaySessionService.cs) 更可靠地测量本地经过时间，可选使用 `SteamUriGameLauncher` 启动真实游戏，但仍没有模拟运行协议。新需求应分别表示本地经过时间、Steam 总时长和评测进度。

### 3.2 成就仅只读

[`SteamWebApiClient`](../reference/legacy-windows/SteamCuratorHub.App/Infrastructure/Services/SteamWebApiClient.cs) 读取玩家成就与 schema；[`Achievement`](../reference/legacy-windows/SteamCuratorHub.App/Domain/Models/Achievement.cs) 保存名称、说明、解锁状态/时间和 Progress。源码未找到全球成就比例字段/读取接口，也未找到成就提交实现。

### 3.3 旧发布和领取命令直接记账

主 ViewModel 的 `PublishLongReview` / `PublishShortReview` 直接设置 `IsPublished=true` 并添加 `Succeeded=true` 的本地 Publication。`ClaimCuratorCopy` 直接将 Copy 标为 Claimed。它们不能证明 Steam 操作成功。

[`SteamSessionPanel.xaml.cs`](../reference/legacy-windows/SteamCuratorHub.App/Presentation/Views/SteamSessionPanel.xaml.cs) 仍与旧命令相连；因此“按钮存在”“本地测试通过”和“端到端发布可用”必须分开。

### 3.4 新确认服务也不是服务端核验器

[`ReviewPublicationWorkflow.Confirm`](../reference/legacy-windows/SteamCuratorHub.App/Application/Services/ReviewPublicationWorkflow.cs) 检查票据、任务、AppID、草稿版本和内容，但自身不查询 Steam 结果；`steamUrl` 是可选参数。需要调用层提供明确的结果来源，不能仅接上新服务就宣称自动验证发布成功。

### 3.5 短评尾巴与全文链接

[`ReviewContentComposer.Compose`](../reference/legacy-windows/SteamCuratorHub.App/Application/Services/ReviewContentComposer.cs) 在提供 group 时，对长评和短评都调用 `AppendFooter`。这与用户描述的“长评加组尾，短评填写长评全文链接”存在区别，需按需求重定。

[`BuildCuratorRecommendationFillScript`](../reference/legacy-windows/SteamCuratorHub.App/Application/Services/SteamPublishingAutomationScripts.cs) 只有群组、AppID、正文和推荐状态参数，没有全文链接参数。`Publication.SteamUrl` 是发布记录 URL，不等于鉴赏家表单中的全文链接输入。

### 3.6 多群组发布与总状态

`ReviewTask` 只有一个 `CuratorGroup` 字符串；草稿按类型与语言查找，没有目标群组维度。`Publication` 没有明确的群组 ID 和语言字段。新 Confirm 成功任一类型会将整个任务设为 Published。这些设计需要在多组投稿、长评完成但短评未完成的场景中重新评估。

### 3.7 字数和语言语义

组合器使用 .NET grapheme 枚举并限制短评为 200，但旧发布命令没有调用该组合器。`FreeGeminiService.SummarizeAsync` 提示词要求中立要点摘要，没有专门约束为作者立场一致的 200 字短评。该常量不能证明 Steam 当前表单的长度规则。软件语言与草稿语言有耦合，用户是否需要独立选取也待确认。

## 4. 历史实现参考

本节保留原型提供的思路与案例；新版本按重新选型和独立实现规划，不设旧代码迁移任务。

- Steam HTTP 读取、取消/重试与部分失败处理：可参考需求场景，所用端点仍需重新核实。
- SQLite 与草稿/历史逻辑：保留测试案例，重新核对群组、语言和发布完成的语义。
- BBCode 工具栏和转换：参考功能清单及格式案例，按新技术栈设计 UI。
- Gemini：参考错误处理思路，供应商和模型选择待确认。
- WebView 脚本：只能作为待实测候选；登录、当前 DOM、提交和结果识别都需单独验证。
- 测试：保留局部失败、竞态、上下文隔离、持久化等案例；不继承把本地记账等同发布成功的旧期望。

## 5. 验证范围

本次 Debug 构建和 `--local-only` 均通过，见[验证记录](../reference/legacy-windows/VALIDATION.md)。未验证 Mac、GUI、当前真实 Steam 接口、真实 Gemini、挂时长、成就写入或发布。

历史 `PROJECT_HANDOFF.md` 记录了更早的验证与开发计划；它们保留用于追溯，不覆盖本次结果和新需求。
