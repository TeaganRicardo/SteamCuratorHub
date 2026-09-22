# Steam Curator Hub 项目交接

更新时间：2026-09-08

这份文件是下一轮开发的权威入口。打开本文件夹后，先阅读本文件，再检查 `git status` 和相关源码。当前工作树包含大量尚未提交但有效的开发成果；必须原样保留，禁止使用 `git reset --hard`、`git checkout --` 或任何会覆盖未提交修改的操作。

## 1. 仓库状态

- 工作区：`C:\Users\admin\Documents\Steam Curator Hub`
- 当前分支：`codex/dev`
- 当前 HEAD：`4555e194cc42d9dd381f7c252f5bf08700e8765c`
- HEAD 提交：`feat: add real recent games and task management UI`
- 当前成果尚未建立新的 Git commit。
- 已清理所有开发过程中产生的 `.codex-*patch*` 临时补丁；项目根目录没有待处理的 `.orig` 或 `.rej`。
- `git diff --check` 通过；输出仅包含 Git 对 LF/CRLF 转换的提示。

## 2. 当前产品定义

技术栈与产品约束：

- WPF + .NET 10 + MVVM + SQLite + WebView2。
- 单 Steam 账号。
- 软件支持简体中文和英文；语言切换位于 Settings。
- 游戏名、成就名、商店详情和评论内容随软件语言切换，并提供双向内容回退。
- 长评与 Curator 短评按类型、语言独立保存。
- Gemini 免费 API 的翻译和摘要属于一期功能。
- 创建任务默认目标 4 小时、截止日期为领取后 7 天；弹窗允许修改两项。
- 示例与真实 Store 回归 AppID：`4242040`；中文名“千层恋心”，英文名“Koikata: How Our Love Grows”。
- 用户可见名称统一使用 `Library / 游戏库`。内部导航 Key、类名和文件名仍可保留 `Inventory`，无需为了命名做高风险重构。
- 左侧栏负责 Dashboard、Library、Playthrough、Editor、Curator、History & drafts、Settings，以及活跃任务 Banner；底部账号入口保留。
- 不在界面中展示开发要求或技术限制说明，只保留用户完成操作所需的功能性文案。
- 当前仍遵守“暂不调用界面控制”：后续开发可使用代码检查、构建、自动化测试和真实 Steam 只读 API；在用户再次授权前不要自动操作 Windows 窗口。

## 3. 当前验证基线

整理交接时已实际执行并通过：

```text
Debug build:  0 warnings, 0 errors
Release build: 0 warnings, 0 errors
Debug local integration: PASS
Release local integration: PASS
```

执行命令：

```powershell
dotnet build SteamCuratorHub.slnx -c Debug --no-restore
dotnet run --project SteamCuratorHub.IntegrationTests\SteamCuratorHub.IntegrationTests.csproj -c Debug --no-build -- --local-only
dotnet build SteamCuratorHub.slnx -c Release --no-restore
dotnet run --project SteamCuratorHub.IntegrationTests\SteamCuratorHub.IntegrationTests.csproj -c Release --no-build -- --local-only
git diff --check
```

本地集成测试当前覆盖：

- Steam 请求 Header 隔离、有限重试、近期游戏和 Store 搜索传输契约。
- Gemini 配置、Header 隔离、重试、取消、响应映射、内容缓存和上下文隔离。
- 商店详情双语局部失败、评论失败降级、整体失败与描述回退。
- Library 快速选择竞态、迟到成功/错误隔离、导航取消、任务上下文隔离。
- ViewModel 生命周期、重复任务详情请求、加载状态释放、重复 Dispose、迟到 Library 结果隔离。
- Library/Store 搜索范围、阈值、防抖、取消、排序、分页、ownership 与错误恢复。
- 单调时钟运行会话、暂停、完成、崩溃恢复和测试启动器适配器。
- 只读成就双语合并、局部失败和原子持久化。
- 新的两阶段发布工作流、内容校验、推荐状态、票据过期/过时/跨任务保护、Curator Connect 发现脚本与导入合并。
- 历史与草稿筛选、恢复、受保护删除、归档、SQLite 重启兼容。
- SQLite 账号隔离、旧数据库保护、草稿独立保存、任务生命周期与级联删除。

`PublishingIntegrationChecks.Run()` 已接入测试入口，但本身不打印单独一行；能到达最终 `PASS` 即表示它已执行通过。

本轮没有重新执行真实 Steam 全量只读回归，也没有做 GUI 验证。较早版本曾通过真实账号的库、近期游戏、成就、商店元数据和评论回归；最新工作树仍需在完成 UI/VM 接线后重新验证。真实回归入口不带 `--local-only`：

```powershell
dotnet run --project SteamCuratorHub.IntegrationTests\SteamCuratorHub.IntegrationTests.csproj -c Release --no-build
```

该入口应始终保持只读：不得启动游戏、修改成就、领取 Copy 或提交评论。

## 4. 已完成并已接入的功能

### 4.1 Library 与 Steam Store

- Library 本地搜索、排序、分页与真实游戏数据。
- Library / Steam Store 互斥分段搜索。
- Store 使用公共 HTTPS `api/storesearch`，不会携带 Steam Web API Key。
- Store 查询最少 2 个字符，350ms 防抖，旧请求取消，generation/query/language/scope 多重迟到结果隔离。
- Store 结果保持 Steam 相关性顺序，过滤 bundle、无效 AppID、空名称、重复 AppID 和非 HTTPS 图片。
- 2 分钟、最多 100 项的服务层搜索缓存。
- Store 结果可打开详情，也可建立任务。
- 新建任务弹窗已有真实近期游戏 Grid、真实封面、搜索和任务设置底栏。
- 侧边栏 Active Tasks 的加号可打开快速创建弹窗。
- 任务在侧边栏和 Dashboard 可进入运行、编辑、修改时间和删除。

主要文件：

- `SteamCuratorHub.App/Application/Abstractions/IGameCatalogService.cs`
- `SteamCuratorHub.App/Application/Abstractions/ISteamApiClient.cs`
- `SteamCuratorHub.App/Infrastructure/Services/SteamWebApiClient.cs`
- `SteamCuratorHub.App/Infrastructure/Services/SteamGameCatalogService.cs`
- `SteamCuratorHub.App/Presentation/Views/InventoryPanel.xaml`
- `SteamCuratorHub.App/Presentation/ViewModels/MainWindowViewModel.cs`

### 4.2 共享商店详情与评论研究

Library 与 Editor 已复用同一个详情系统：

- Hero 图片和 Header → Capsule → Icon 字符串级回退。
- 中英文名称、AppID、是否在库、累计时长和评论摘要。
- Steam 官方本地化介绍；长介绍展开/收起。
- Gemini 描述翻译、原文/译文切换。
- 真实玩家评论、单条翻译、批量翻译和 AI 评论摘要。
- 加载、空、评论不可用和错误状态。
- 独立滚动与可拖动 GridSplitter。
- 详情请求支持取消、generation 隔离和页面切换取消。
- 英文详情、中文详情和评论独立降级；只有双语详情都失败时整体失败。
- 研究内容缓存已有 TTL、容量上限和并发测试。

主要文件：

- `SteamCuratorHub.App/Presentation/Views/GameStoreDetailsPanel.xaml`
- `SteamCuratorHub.App/Presentation/Views/ReviewResearchPanel.xaml`
- `SteamCuratorHub.App/Presentation/ViewModels/ReviewResearchViewModel.cs`
- `SteamCuratorHub.App/Infrastructure/Services/FreeGeminiService.cs`

### 4.3 Library 浏览上下文与任务上下文隔离

- Library 浏览选择存放在 `_librarySelectedGame`。
- 进入 Playthrough / Editor / Curator 时，`SelectedGame` 强制跟随 `SelectedTask.Game`。
- 返回 Library 时恢复之前浏览的游戏。
- 后台任务详情仅在 task-bound section 更新当前详情，不能覆盖 Library 正在浏览的其他游戏。
- 相同 AppID 的任务详情加载去重，删除任务和 Dispose 后的迟到回调不会重新写入状态。

### 4.4 Editor 格式控件

Editor 已加入 Steam 文本格式控件，参考仓库中的 `Steam Text Formating/Text Formatting.html`：

- H1、H2、H3、粗体、斜体、下划线、删除线。
- Spoiler、noparse、quote、code。
- 无序列表、有序列表、链接、分隔线和表格模板。
- 使用自定义 `FormatButton`，没有退回原生按钮外观。

链接当前以模板 `[url=https://]...[/url]` 插入，尚未增加 URL 小弹窗。

### 4.5 Gemini 一期能力

- 默认模型为 `gemini-3.5-flash-lite`。
- `GOOGLE_API_KEY` 优先，`GEMINI_API_KEY` 回退。
- 固定 Google HTTPS endpoint，Key 只通过 `x-goog-api-key` 发送。
- 超时、取消、有限重试、安全错误映射、输入截断和 prompt injection 隔离。
- 单条/批量翻译、商店介绍翻译、评论正文摘要和配置状态。
- Settings 已有 AI 状态卡；设置文件不保存凭据。

## 5. 已完成核心层、尚未完整接入 UI/主 ViewModel

这一节是下一轮的首要工作。核心代码和自动化测试已经存在，但主界面仍保留部分旧逻辑。

### 5.1 运行与成就链路

已完成：

- `ReviewTaskPlaySessionService`：单活动会话、单调时钟、15 秒 checkpoint、暂停、完成、异常退出恢复。
- `SteamUriGameLauncher`：校验 AppID 后使用 Steam URI；自动化测试只使用适配器，不会真的启动游戏。
- `SteamAchievementRefreshService`：真实只读 API、英中并行、局部失败回退、原子持久化、删除任务迟到隔离。
- `MainWindowViewModel` 已注入 `_playSession` / `_achievementRefresh`，启动时能恢复遗留的 Running 标记，也有加载/错误/同步时间等展示属性。

未完成：

- `MainWindowViewModel` 中 `StartRun`、`PauseRun`、`TickRun`、`MarkComplete` 仍主要使用旧的直接计时和 `PlayedHours += 1/3600` 逻辑，尚未真正改为调用 `_playSession`。
- `LoadAchievementsAsync` 仍直接调用 `SteamGameCatalogService.ApiClient`；`RefreshAchievements` 只是转调旧加载方法，尚未使用 `_achievementRefresh.RefreshAsync`。
- `Dispose` 尚未调用 `_playSession.Dispose()`。
- 修改日程、切换任务、删除任务和离开当前任务时需要统一通过运行服务安全暂停。
- Playthrough UI 目前只新增了成就错误文本，尚缺刷新按钮、刷新进度、已解锁数量、同步时间和运行启动错误。
- `PlaythroughPanel.xaml` 的任务选择按钮样式仍有 `FocusVisualStyle="{x:Null}"`，应删除并保留全局键盘焦点视觉。
- `AchievementLastSyncedLabel` 应按软件 `Language` 选择文化格式，不能依赖操作系统 `CultureInfo.CurrentCulture`。

相关文件：

- `SteamCuratorHub.App/Application/Abstractions/IPlaySessionService.cs`
- `SteamCuratorHub.App/Application/Abstractions/IAchievementRefreshService.cs`
- `SteamCuratorHub.App/Infrastructure/Services/ReviewTaskPlaySessionService.cs`
- `SteamCuratorHub.App/Infrastructure/Services/SteamUriGameLauncher.cs`
- `SteamCuratorHub.App/Infrastructure/Services/SteamAchievementRefreshService.cs`
- `SteamCuratorHub.App/Presentation/Views/PlaythroughPanel.xaml`
- `SteamCuratorHub.IntegrationTests/RuntimeIntegrationChecks.cs`

### 5.2 发布确认链路

新的安全核心已经完成：

- `ReviewContentComposer`：换行规范化、组尾只追加一次、Unicode grapheme 计数、Steam 长评/短评限制、稳定哈希。
- Curator 短评支持 Recommended / NotRecommended / Informational 三种状态；推荐状态参与草稿版本、哈希、发布幂等和票据过时判断。
- `ReviewPublicationWorkflow.Prepare(...)` 创建 30 分钟内有效、只存在于内存的发布票据。
- 只有 `ReviewPublicationWorkflow.Confirm(...)` 在精确匹配 task/AppID/draft/version/content/recommendation 后才记录成功。
- Confirm 支持 Steam HTTPS URL 校验、幂等、过期、取消、跨任务、跨 App 和 stale draft 防护。
- `SteamWebViewService.Publishing.cs` 实现 `ISteamPublishingAutomation`：长评自动填充、Curator 短评自动填充、Curator Connect offer 定位和 offer 发现。
- DOM 脚本只填充或定位，不点击提交按钮；操作要求 WebView Steam 身份为 Verified。
- `CuratorCopyWorkflow` 是两阶段确认；`CuratorCopyImportService` 按 Steam offer ID / AppID 安全合并真实发现结果。
- SQLite 已迁移草稿/发布推荐状态和 Curator Copy 的 offer/AppID/过期/导入字段。

关键阻断：

- `MainWindowViewModel.PublishLongReview` 和 `PublishShortReview` 仍是旧逻辑，会在本地直接将草稿标成已发布并写入成功记录。
- `SteamSessionPanel.xaml.cs` 的确认按钮仍调用上述旧命令。
- 在完成新工作流接线前，不要使用当前 UI 的“确认发布”按钮进行真实操作。
- 下一轮必须把页面流程改为：保存草稿 → `Prepare` 取得票据 → WebView 自动填充 → 用户在 Steam 页面提交 → 明确确认 Steam 已成功 → `Confirm` 精确记账。
- 草稿内容、语言、推荐状态、组尾、选中任务或 AppID 改变时，应取消/废弃旧票据。
- 旧的 `RunLocalWorkflowAsync` 仍测试“命令立即记录发布”的历史行为；完成迁移后必须重写这部分测试，不能因为旧测试通过而保留不安全语义。

相关文件：

- `SteamCuratorHub.App/Application/Services/ReviewContentComposer.cs`
- `SteamCuratorHub.App/Application/Services/ReviewPublicationWorkflow.cs`
- `SteamCuratorHub.App/Application/Services/SteamPublishingAutomationScripts.cs`
- `SteamCuratorHub.App/Application/Services/CuratorCopyWorkflow.cs`
- `SteamCuratorHub.App/Application/Services/CuratorCopyImportService.cs`
- `SteamCuratorHub.App/Application/Abstractions/ISteamPublishingAutomation.cs`
- `SteamCuratorHub.App/Infrastructure/Services/SteamWebViewService.Publishing.cs`
- `SteamCuratorHub.App/Presentation/Views/SteamSessionPanel.xaml`
- `SteamCuratorHub.App/Presentation/Views/SteamSessionPanel.xaml.cs`
- `SteamCuratorHub.IntegrationTests/PublishingIntegrationChecks.cs`
- `SteamCuratorHub.IntegrationTests/PublishingAdvancedIntegrationChecks.cs`

### 5.3 Curator 设置与组尾

已完成：

- `CuratorPublishingSettings` / `CuratorGroupProfile` 数据模型。
- `JsonCuratorPublishingSettingsService`：单独保存公开 Curator identifier、选中组和中英文组尾，不保存凭据。
- identifier、组 ID、名称、数量和 footer 长度校验；超长 footer 会拒绝保存，不会截断 Unicode。
- `CuratorGroupViewModel` 可编辑组名称、identifier 和中英文 footer。

未完成：

- `MainWindowViewModel` 尚未加载、暴露和保存这些设置。
- Settings 尚无 Curator 配置卡、添加/删除/保存组控件。
- Editor 仍绑定硬编码的 `GroupOptions`：`Indie Gems`、`Roguelike Roundup`、`Cozy Corner`。
- 任务的 `CuratorGroup` 应改为保存稳定的 group ID；Editor 和 Curator 页面共享同一选中组。
- SteamSessionPanel 当前仍使用未持久化的 `CuratorIdentifierBox`。

相关文件：

- `SteamCuratorHub.App/Domain/Models/CuratorPublishingSettings.cs`
- `SteamCuratorHub.App/Application/Abstractions/ICuratorPublishingSettingsService.cs`
- `SteamCuratorHub.App/Infrastructure/Services/JsonCuratorPublishingSettingsService.cs`
- `SteamCuratorHub.App/Presentation/ViewModels/CuratorGroupViewModel.cs`

### 5.4 History & drafts

已完成：

- `HistoryDraftService`：按任务/类型/语言/内容种类查询；打开草稿；恢复归档/已发布任务到编辑上下文；删除未发布草稿；保护已发布修订；归档任务。
- 发布内容以不可变快照暴露，没有编辑或删除接口。
- `HistoryDraftsViewModel`：筛选、选择、打开、恢复、删除和归档命令，以及本地化行模型。
- SQLite 和重启测试已通过。

未完成：

- `MainWindow` 仍显示旧的 `HistoryItems` 时间线，尚未使用 `HistoryDraftsViewModel`。
- 主 ViewModel 尚未处理 `DraftEditorRequested`：需要激活对应任务、切换软件语言或载入草稿语言、填入独立编辑缓冲并跳转 Editor/Curator。
- 删除草稿和归档任务后，需要刷新 `ActiveTasks`、`HistoryTasks`、当前任务、侧边栏和 Dashboard 计数。
- UI 需要任务/类型/语言/草稿或发布筛选、内容预览、打开/恢复、删除未发布草稿和归档按钮。

相关文件：

- `SteamCuratorHub.App/Application/Abstractions/IHistoryDraftService.cs`
- `SteamCuratorHub.App/Application/Services/HistoryDraftService.cs`
- `SteamCuratorHub.App/Presentation/ViewModels/HistoryDraftsViewModel.cs`
- `SteamCuratorHub.IntegrationTests/HistoryDraftIntegrationChecks.cs`

## 6. 数据与安全边界

真实 Steam 凭据只能通过环境变量读取，不能写入源码、Markdown、JSON、日志、截图、commit 或最终回复。当前代码支持的主要环境变量名称：

```text
STEAM_WEB_API_KEY        # 首选 Steam Key
STEAM_API_KEY            # Steam Key 兼容回退
STEAM_ID                 # 首选 SteamID
STEAM_STEAM_ID           # SteamID 兼容回退
GOOGLE_API_KEY           # 首选 Gemini Key
GEMINI_API_KEY           # Gemini Key 回退
GEMINI_MODEL             # 可选模型覆盖
STEAM_CURATOR_DB_PATH    # 测试/开发数据库路径覆盖
STEAM_CURATOR_SETTINGS_PATH
STEAM_CURATOR_PUBLISHING_SETTINGS_PATH
```

安全规则：

- Store 搜索和商店详情的公共接口不携带 Steam Web API Key。
- Gemini Key 只放在 `x-goog-api-key`，不得出现在 URL、异常消息或日志。
- WebView Session 数据属于本机应用数据，账号身份不匹配时禁止发布自动填充。
- 自动化测试只能使用测试 adapter，不得调用真实 Steam URI launcher。
- 发布自动化在测试中不得提交；Curator Connect 测试不得领取或拒绝真实 offer。
- 自动发布功能即使后续进一步自动化，也必须保留一次面向用户的明确最终确认，并以 Steam 成功结果为本地记账依据。

## 7. 下一轮建议执行顺序

1. 先运行 Debug build 和 Debug `--local-only`，确认工作树未被转移过程改变。
2. 完成运行服务到 `MainWindowViewModel` 的接线：Start/Pause/Tick/Complete、任务切换、日程修改、删除、Dispose。
3. 将成就加载统一到 `IAchievementRefreshService`，补齐 Playthrough 刷新/进度/同步时间/错误 UI，并移除 `FocusVisualStyle="{x:Null}"`。
4. 将 `ReviewPublicationWorkflow`、`ICuratorPublishingSettingsService`、`CuratorCopyWorkflow`、`CuratorCopyImportService` 接入主 ViewModel。
5. 重构 `SteamSessionPanel` 为票据式工作流；删除 UI 到旧 `Publish*Command` 的直接成功记录路径。
6. 在 Settings 接入 Curator identifier 和组管理；将 Editor/Curator 的组选择绑定到稳定 group ID。
7. 完成 Curator 页面三态推荐控件、真实 offer 发现/合并/定位和两阶段领取确认。
8. 用 `HistoryDraftsViewModel` 替换旧 History 时间线，并完成打开/恢复/删除/归档的主界面协调。
9. 重写旧发布命令集成测试，增加 ViewModel 级票据过时、切任务、改语言、改组尾、重复确认和失败恢复测试。
10. 运行 Debug/Release build、两个 local-only 套件、`git diff --check` 和凭据扫描。
11. 在没有 429 风险时运行一次完整真实 Steam 只读回归。
12. 只有用户重新允许界面控制后，才进行 WPF/WebView2 GUI 验收；GUI 验收必须覆盖 100%/125%/150% 缩放、最小窗口宽度、键盘焦点、滚动条、弹窗、长文本和中英文切换。
13. 完成 self review 后再创建一个说明清晰的 checkpoint commit。

## 8. 下一里程碑验收标准

全链路完成至少需要满足：

- 从 Library 或 Store 搜索真实游戏 → 创建任务 → 任务成为当前活跃上下文。
- Playthrough 启动时只在用户点击后打开 Steam 游戏，使用单调时钟记录真实观察时长；暂停、退出、崩溃恢复不虚增时间。
- 成就通过真实只读 API 刷新，中英文可独立回退，失败不会清空已有成就。
- Editor 长评和 Curator 短评按语言独立保存；组尾只追加一次。
- 长评/短评在 Steam 页面被自动填充，内容改变会让旧票据失效；本地只在 Steam 成功后记录 publication。
- Curator 推荐状态三选一并参与草稿版本和发布记录。
- Curator Connect 能发现真实 offer、按 offer ID/AppID 合并、定位对应项，并通过两阶段确认记录领取。
- History 能筛选和打开草稿、恢复任务、删除未发布草稿、保护发布快照和归档任务。
- 软件语言切换后，导航、状态、游戏、成就、详情、评论和新功能文案同步切换。
- 所有窗口、弹窗、开关、日期、ComboBox、滚动条和焦点视觉保持应用自定义风格。
- Debug/Release 构建 0 warning / 0 error；本地测试与真实 Steam 只读测试通过；无凭据进入仓库。

## 9. 当前已知但不阻断编译的问题

- 主 ViewModel 仍较大，下一轮以完成链路为优先，不建议同时进行大规模架构重写。
- `SteamGameCatalogService` 对局部详情失败不写永久详情缓存，故评论接口故障时再次选择可能重复请求；可后续增加短 TTL partial cache。
- 图片只做 URL 候选选择；实际图片下载失败后，WPF Image 尚不会自动尝试下一候选。
- Editor 链接格式控件没有专用 URL 输入弹窗。
- AI 翻译结果和评论摘要可以继续增加高度限制与展开/收起。
- 新功能的本地化 Key 和 AutomationProperties 尚未全部补齐。
- WPF UI 尚未在本轮进行人工或自动窗口验收。

## 10. 本次交接整理中做的最小修复

- 在集成测试入口加入 `using SteamCuratorHub.IntegrationTests;`，使新发布和历史测试可被顶层 Program 调用。
- 修正历史测试中“主任务草稿数量”错误期望：主任务实际为 2 条，另一个任务为 1 条。
- 修正历史不可变快照测试：恢复任务会合法改变聚合的 `TaskStatus`，测试现在只比较真正不可变的 publication 字段。
- 清理所有已经应用的临时 patch 文件。

这些修复之后，Debug/Release 和两个 local-only 测试均已重新通过。
