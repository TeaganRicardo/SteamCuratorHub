# 长评、鉴赏家短评与浏览器会话可行性

更新：2026-10-17。对应：[调研 #3](https://github.com/TeaganRicardo/SteamCuratorHub/issues/3)、[实机验证 #6](https://github.com/TeaganRicardo/SteamCuratorHub/issues/6)。性质：一手资料调研与验证方案；未实现产品，未登录、填充或提交 Steam 表单。

调研开始时以 `git ls-remote` 核对 GitHub：main 为 `de0adcbbb9df995e3663a45db10f834eaeecda88`；工作分支 `docs/global-project-plan` 为 `272498910bb8445c39936b2ca2954a10aa0c9822`。产品范围以 [REQUIREMENTS.md](../REQUIREMENTS.md) 第 6–8、10 节为准；[历史原型分析](../LEGACY_ASSESSMENT.md) 仅用于识别旧实现缺口。

## 1. 当前可作出的判断

- **有官方网页能力依据，有待验证的应用内发布路线。** Valve 说明个人用户可对通过 Steam 启动且有游玩时间的产品写评测；鉴赏家可对 Steam 上的游戏推荐，并可附外部完整评测链接。两种发布目标应分开建模，个人长评不应因鉴赏家自动提交尚未验证而退出首版范围。[个人评测说明](https://partner.steamgames.com/doc/store/reviews)、[鉴赏家说明](https://store.steampowered.com/about/curators/)
- **本次未找到适用于普通玩家的、公开文档承诺支持的个人评测或鉴赏家评测写入 API。** 核查范围是当前官方 Web API 目录、`IUserReviewsService`、`ISteamCommunity` 与 OAuth 文档；`IUserReviewsService` 明确为读取评测。这个有限范围的检索结论，不证明 Steam 内部没有接口，也不把网页使用的未公开请求变成正式 API。[Web API 目录](https://partner.steamgames.com/doc/webapi)、[评测读取接口](https://partner.steamgames.com/doc/webapi/IUserReviewsService)、[Community 接口](https://partner.steamgames.com/doc/webapi/ISteamCommunity)、[OAuth](https://partner.steamgames.com/doc/webapi_overview/oauth)
- **短评与全文链接分离有官方能力依据，当前登录表单字段仍待取证。** 鉴赏家介绍同时描述短文字和可选外链，但只用含糊的“couple hundred characters”说明文本长度。本次没有取得账号管理页，不能据此确认当前字段名、精确上限、计数单位、可接受 URL 或提交请求。[鉴赏家说明](https://store.steampowered.com/about/curators/)
- **三个框架都有文档支持的网页承载路线，但都没有在本项目证明 Steam 登录和发布可用。** Electron 的持久会话分区、Tauri 的平台 WebView、Avalonia 的 NativeWebView 可以进入候选验证；选型仍需与 [Steam 本地能力调研](steam-feasibility.md) 和真实 Mac 验证共同决定，不能只由能嵌入网页决定。

## 2. 发布目标、字段与未知项

| 项目 | 当前产品要求 | 一手资料能确认的边界 | #6 必须补的页面证据 |
| --- | --- | --- | --- |
| 个人长评 | 作者正文加唯一投稿群组尾巴；正文、模板和组合快照分别保存 | 玩家评测允许正面或负面；资格与实际游玩时间有关，购买并非唯一来源。[个人评测说明](https://partner.steamgames.com/doc/store/reviews) | 当前账号/AppID 的可写资格；新建与编辑已有评测入口；正文、推荐、语言、免费获取披露等实际控件；BBCode、长度、预览与结果 URL |
| 鉴赏家短评 | 从长评概括，可编辑；无尾巴、不附 URL | 官方允许具备适当群组角色的成员开始鉴赏家推荐；公开评测列表存在 Recommended / Not Recommended / Informational 类型。[鉴赏家说明](https://store.steampowered.com/about/curators/)、[官方商店列表](https://store.steampowered.com/curators/curatorsreviewing/?appid=564150) | 两个给定群组分别核实实际发布权限；当前推荐类型、AppID 选择和短评输入；编辑既有推荐的行为及冲突处理 |
| 全文链接 | 另一文本框单独保存、校验与填入；不占短评计数 | 官方说明每次推荐可以附完整评测或视频等链接。[鉴赏家说明](https://store.steampowered.com/about/curators/) | 当前链接控件、支持 Steam 个人评测 URL 与否、长度/协议/域名要求、提交后实际展示与跳转目标；不得从旧脚本猜字段名 |
| 发布结果链接 | 指向这一次长评或短评结果的证据 | 与全文链接具有不同用途 | 取得可重新访问的结果 URL；URL、作者、AppID、群组和内容版本匹配；仅打开编辑页不算结果 |

群组加入、鉴赏家管理/发布权限、Curator Connect 接收权限和账号拥有某游戏是不同事实。官方将 Curator Connect 领取限定为有对应权限的成员；这不证明用户当前账号具备发布或领取权限。自动领取不在此次范围。[Curators and Curator Connect](https://partner.steamgames.com/doc/marketing/curators)

旧原型的 `BuildCuratorRecommendationFillScript` 只含正文与推荐状态，没有全文链接参数；旧 `200` 常量及选择器只是本地源码事实。新版本不得继承短评尾巴，也不得把 `Publication.SteamUrl` 同时用作全文链接。证据见 [LEGACY_ASSESSMENT.md §3.5–3.7](../LEGACY_ASSESSMENT.md)。

### 2.1 字符限制需要两条验证线

产品规则来自需求：“200 字以下”，中英文字与符号计 1；是否恰好允许 200、空格/换行、组合字符和 emoji 如何处理仍未确认。平台规则必须从当前登录表单及其实际校验取得，两者分别记录。即使 Steam 允许更长，应用仍需遵守最终确定的产品规则；即使应用计数合格，也需显示平台拒绝结果。

#6 先做不提交的输入/预览，覆盖 199 / 200 / 201 个普通字符、纯中文、中英混排、空格、换行、`é` 与 `e + combining acute`、单 emoji、肤色组合、国旗、ZWJ 家庭 emoji。记录原字符串与 UTF-16 code unit / Unicode scalar / grapheme 三种长度，以及页面剩余计数、`maxlength` 等属性与可见提示。属性只是线索；不能通过真实发布边界文本来代替预览验证。最终提交校验是否与前端一致，只能在用户选择的真实发布中补充。

## 3. 登录与跨平台网页会话

Steam OpenID 的官方用途是向第三方证明 SteamID，不授予商店评测写入权限，也不证明应用内发布 WebView 已登录。Steam OAuth 要向 Valve 申请 Client ID；官方描述的支持范围及权限不能当成评测发布授权。因此身份绑定、网页会话和本地 Steam 运行/统计会话必须分别观察，再核对账号一致性。[身份文档](https://partner.steamgames.com/doc/features/auth?l=english)、[OAuth 文档](https://partner.steamgames.com/doc/webapi_overview/oauth)

| 候选 | macOS / Windows 网页路径 | 会话保存与隔离事实 | 需要验证与承担的代价 |
| --- | --- | --- | --- |
| Electron | `WebContentsView` 承载远程网页；应用随 Electron 分发 Chromium 与 Node.js。[WebContentsView](https://www.electronjs.org/docs/latest/api/web-contents-view)、[安全文档](https://www.electronjs.org/docs/latest/tutorial/security) | `session.fromPartition` 的 `persist:` 前缀启用持久会话；同名分区在应用内共享，无前缀为内存会话。[Session](https://www.electronjs.org/docs/latest/api/session) | 独立 Steam 分区与本地 UI；远程页关闭 Node integration、启用 context isolation / sandbox；限制导航、弹窗、权限与 IPC。应用需跟进所分发浏览器更新。页面 DOM 与登录仍会变化，不能因两平台使用 Chromium 就省略 Windows 验证。[安全文档](https://www.electronjs.org/docs/latest/tutorial/security) |
| Tauri 2 | 系统引擎：macOS WKWebView、Windows WebView2；跨平台不代表同一引擎版本。[WebView 版本](https://v2.tauri.app/reference/webview-versions/) | 当前 API 文档中 Windows 可设 `dataDirectory`；macOS 不支持此路径参数，应使用 `dataStoreIdentifier`，该能力要求 macOS 14+，并标注自 Tauri 2.9.0 提供。[Webview API](https://v2.tauri.app/reference/javascript/api/namespacewebview/) | 单独的发布 WebView，不向 Steam 远程源赋本地命令权限；默认仅随包代码可用 Tauri API，放宽远程 capability 必须专门评估。[Capabilities](https://v2.tauri.app/security/capabilities/) Mac 最低系统版本、销毁/重建会话、跨域登录/弹窗和脚本回传需验证；不能直接照搬 Windows profile 方案。 |
| Avalonia | 当前官方 `NativeWebView` 在 Windows 使用 WebView2、macOS 使用 WKWebView；公开 API 提供脚本执行、导航事件和平台支持时的 Cookie manager。[嵌入文档](https://docs.avaloniaui.net/docs/app-development/embedding-web-content)、[NativeWebView](https://docs.avaloniaui.net/controls/web/nativewebview) | Windows environment 有 `ProfileName` / `UserDataFolder`；Apple environment 有 `NonPersistentDataStore` / `DataStoreIdentifier`。[Environment](https://docs.avaloniaui.net/controls/web/webview-environment) | 创建环境前决定隔离方式；Apple 标识符能力的实际 OS 下限按选定版本与 Apple API核实。脚本消息不能直接获文件/Steam helper 权限。布局嵌入、输入法、登录跳转及 Cookie 清除均需本机验证，C# 不等于旧 WPF/WebView2 可直接跨平台。 |
| 系统默认浏览器辅助提交 | 应用打开验证过的 Steam HTTPS 页面，由用户登录、粘贴和提交 | 浏览器管理自己的会话；应用不得假设可继承系统浏览器 Cookie 或读取其 DOM | 保留长短评与独立链接的可复制内容、目标检查和结果 URL 导入；无法自动核实的部分标明“用户确认”。能打开网页不证明已登录或已发布。此为建议的降级流程，尚未实测。 |

上述各平台 API 的文档存在，不证明 Steam 接受该内嵌引擎的登录方式。Steam Guard、二维码/设备确认、验证码、第三方 Cookie/跨域跳转、会话过期、代理与网络限制都留给 #6 实测，遇到认证阻塞由用户本人处理。

### 3.1 建议的隔离边界

此处是工程建议，尚未批准为框架决定：

1. 草稿、文章预览和应用控制页使用本地受信任 UI；Steam 发布视图使用独立浏览器环境；站外调研再用独立环境或后台取文，不共享 Steam profile。
2. profile 以不含用户名/凭据的本地标识定位；记录绑定 SteamID 和会话代次。即使产品只管理一个 Steam 账号，也必须检测误登录与账号切换；“存在 Cookie”不能作为账号一致的证据。
3. 每次填充与提交前重新核对 HTTPS origin、页面路径、账号、AppID、群组及编辑/新建模式；未知、歧义、登录跳转或无权限时停止填充。外部域名的新窗口默认交系统浏览器，必须保留登录流程实际需要的受控例外。
4. 页面脚本只接收冻结的待填字段，返回结构化检查结果；不把凭据、Cookie、CSRF token、任意脚本执行器或本地 Steam 命令开放给远程页面。页面内需要的 CSRF 流程由官方页面会话承担，不硬编码 Cookie 请求作为首版接口。
5. 退出账号时关闭该环境的页面、清除其 Cookie/站点数据并重建环境，再核实已退出；持久 profile 只保留本机，默认不进入草稿备份、日志、Git、云同步或 AI 请求。不能保证对本机管理员或磁盘读取者的保密，应在实现时明确 OS 凭据存储与权限边界。

## 4. 依赖与许可对选型的影响

本次没有引入、下载或锁定 SDK。以下是当前一手依据，真正选型时仍需固定包版本、读取对应许可证并生成依赖清单；此表不是全部传递依赖的许可审计。

| 组件 | 本次确认 | 实施边界 |
| --- | --- | --- |
| Electron 本体 | 官方仓库为 MIT，要求保留版权与许可通知。[LICENSE](https://github.com/electron/electron/blob/main/LICENSE) | Chromium、Node.js 及其他第三方通知另行保留；不能只附 Electron 的一份 MIT 即宣称整包许可已处理。运行时升级和实际包体积另测。 |
| Tauri 本体 | 官方仓库提供 MIT 与 Apache-2.0 许可证。[MIT](https://github.com/tauri-apps/tauri/blob/dev/LICENSE-MIT)、[Apache-2.0](https://github.com/tauri-apps/tauri/blob/dev/LICENSE-APACHE-2.0) | 按锁定版本及各 crate/npm 包声明确认适用选项；系统 WebView 的 SDK、安装包与运行时各自核对，不从 Tauri 的许可推导全部组件许可。 |
| Avalonia 与 WebView | Avalonia 主仓库标 MIT；当前 `Avalonia.Controls.WebView` 仓库也明确 MIT，已发布 `12.1.1`。不应沿用“官方 WebView 一定需要商业 Accelerate”这一旧结论。[Avalonia](https://github.com/AvaloniaUI/Avalonia)、[WebView LICENSE](https://github.com/AvaloniaUI/Avalonia.Controls.WebView/blob/main/LICENSE)、[WebView 12.1.1](https://github.com/AvaloniaUI/Avalonia.Controls.WebView/releases/tag/12.1.1) | 官方曾说明 WebView 随 Avalonia 12 转开源；旧版 Accelerate、XPF、商业工具及其他组件不能据此一并判为 MIT。若使用不同版本或替代浏览器控件，重新核查。[官方说明](https://avaloniaui.net/blog/the-avalonia-webview-is-going-open-source) |
| WebView2 运行时 | Microsoft 要求发布应用时保证目标机具备 Runtime；Evergreen 自动更新，Fixed Version 由应用方携带并更新。[分发文档](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/deployment-distribution) | Windows 安装/离线安装、架构匹配、运行时与 SDK 的实际分发许可单独确认；不要把整个运行时放进公开源码目录。 |
| Steamworks SDK | 网页辅助发布方案本身没有证据需要链接 Steamworks 原生 SDK | 本地时长/成就适配的 SDK、第三方封装和许可在独立 Steam 能力决策中处理；网页会话可用不代表取得任意 Steam SDK 的分发授权。 |

## 5. 发布后核验与失败恢复

以下是建议的服务契约，用于 #6 原型与后续实现评估，尚无本项目运行证据。

每次发布意图冻结 `账号 SteamID + AppID + 类型 + 群组 ID（短评必需）+ 语言 + 草稿版本 + 正文/尾巴/全文链接快照 + 内容摘要 + 会话代次 + 尝试 ID`。文章正文/全文链接与发布结果 URL 分开；长评与短评各有尝试、结果和状态，长评成功不将短评整体标为成功。

| 状态 | 允许记录的事实 | 下一步 |
| --- | --- | --- |
| 草稿 / 待准备 | 已保存草稿、目标与版本 | 进行资格、身份、字段及内容校验 |
| 已准备 / 已填充 | 页面身份与目标检查、字段回读相符 | 展示预览；用户提交或进入另行确定的应用内提交流程；此时不是发布成功 |
| 已提交待核实 | 用户报告提交或观察到请求/跳转；网络超时、崩溃等导致结果未知 | 保存原尝试，查询已有结果；阻止盲目再次提交 |
| 已核实 | URL/结果身份与目标、内容和类型相符，并记录证据来源与时间 | 保存发布快照；后续草稿修改另计，不改写已发布事实 |
| 确定未提交 / 确定失败 | 有明确前置阻止或 Steam 拒绝依据 | 修复后生成新的尝试；不能用查询暂无结果推导确定失败 |
| 结果不匹配 / 人工待确认 | 作者、游戏、群组、内容或新建/编辑关系不明 | 保留证据和草稿，由用户核实；不自动改写远端内容 |

个人长评的**候选回读渠道**是官方 `IUserReviewsService/GetAppReviews/v1/`：文档返回作者 SteamID、`recommendationid`、正文、推荐与更新时间。旧 `/appreviews/<appid>?json=1` 已被标为 deprecated。读取需选择所有购买来源与所需语言，避免遗漏 Key/免费来源；按 ID/作者和目标 AppID 匹配，再核对冻结内容。匿名响应可能缓存至多 10 分钟，429 为限流；因此读不到最新结果不等于提交失败。此渠道是公开列表，是否能及时找到当前账号评测仍需实测，不可作唯一核验器。[当前官方接口与迁移说明](https://partner.steamgames.com/doc/webapi/IUserReviewsService)

鉴赏家结果优先回到实际发布结果页或当前群组/AppID 对应公开推荐，核对短评、推荐状态及全文链接；本次没有查得具备同等保证的官方鉴赏家写入/结果读取 API。可先由用户打开结果 URL 并确认，记录 `用户确认`；未来只有实际读取并匹配内容时才标 `应用回读核实`。服务端返回成功、按钮点击、HTTP 2xx、本地数据库写入或 URL 格式正确均不足以单独核实。页面仍待审核/不可见时保留该结果，不自动创建第二条。

## 6. #6 的有界技术验证计划

先建立最小会话/表单验证壳，复用相同场景逐一评估候选，不同时开发完整编辑器。默认优先验证有现成持久分区与受控内容视图的 Electron 路线，是本报告的验证顺序建议；它不构成框架定案。若系统 WebView 能在 Mac 完成相同门槛，Tauri / Avalonia 继续保留。框架安装体积、内存与打包体验须实测后进入选型。

### 6.1 不提交阶段

1. 固定待验证分支、SHA、系统/CPU、框架包与浏览器引擎版本；使用独立本机 profile。测试账号由用户本人登录及完成 Steam Guard，应用不接收密码；核对显示身份与预期 SteamID。
2. 以用户选定 AppID 和唯一投稿群组检查长评可写资格、短评管理权限、已有评测与新建/编辑模式。两个已给定群组分别测试权限；任一未知不自动填。
3. 用户取得当前表单可见证据及必要的脱敏结构记录；编制真实字段映射。长评正文/尾巴各一份，短评只有正文，全文链接另一字段；先预览再回读字段，禁止点提交。测试 §2.1 计数样例、BBCode/纯文本差异及链接校验。
4. 关闭重开、应用重启、主动退出/重新登录、过期/断网、切到另一账号；确认旧 profile、迟到脚本与旧尝试不会写入当前草稿或错误目标。失败要保留已保存内容。
5. 验证未知页面、重复匹配控件、AppID/群组不符、无权限、草稿版本变化时停止；确认外部媒体网页不能访问 Steam profile 或应用本地命令。

不提交阶段只能说明字段准备、页面预览与本机会话行为，不能将服务端计数规则、最终发布或跨平台兼容标为通过。

### 6.2 用户选择的真实发布阶段

1. 实际公开发布由用户操作或另行明确授权。先展示完整冻结快照、目标账号/游戏/群组与新建或更新关系；正文/尾巴模板未确认时不擅自定稿。
2. 用户选一个真实长评完成发布；取得结果 URL、核对作者/AppID/正文/尾巴并刷新回读。再独立验证短评三部分：正文、推荐类型、全文链接实际展示及目标。不能用两个目标的总状态代替逐项结果。
3. 有控制地覆盖“提交后断网/响应丢失”的恢复：先查询既有结果并人工核实，确认不会盲重试。避免为测试重复发布或反复改写真实文章；能用本地故障注入证明的状态机部分与真实远端证据分开报告。
4. 若鉴赏家内嵌提交或结果识别不可靠，验收辅助发布边界：保存/复制字段、打开正确官方页面、用户提交并导入结果 URL；明确哪些步骤由用户确认。个人长评继续交付，不将短评直接提交列为已经实现。
5. Windows 后续使用同一场景重新验证输入法、引擎版本、profile 保存与失效、安装依赖和实际发布回读；Mac 通过不自动代表 Windows 通过。

### 6.3 实际交付物与停止条件

#6 交付能力矩阵应逐项标为 `文档依据 / 未实测 / 不提交实测通过 / 用户发布与回读通过 / 失败及原因`，包含精确 SHA、OS/引擎版本、字段映射、实际限制、脱敏证据路径、结果来源和未验证项。登录会话、Cookie、CSRF、真实数据库和含个人信息的完整页面仅保留本机，公开 PR 只收适合公开的结论与合成样例。

账号无法可靠识别、群组权限不明、目标/内容不匹配或结果未知时，停止该次发布动作并保留草稿；这不妨碍继续离线编辑、保存与不依赖会话的开发。若所有内嵌候选均不能可靠登录，记录原因并以系统浏览器辅助路线验证个人发布；在有证据前不宣布条件能力通过。

## 7. 本次执行与未验证项

实际执行：读取仓库入口、状态、需求、历史分析及相关脚本；读取 GitHub #1/#3/#6；核对远端 main/工作分支 SHA；于 2026-10-17 在线读取本文链接的官方文档、官方源码许可证与 WebView release 记录。搜索发现的论坛帖子未用于平台字段、上限或框架结论。

未执行：任何框架构建或安装、Mac/Windows WebView 启动、登录/Steam Guard、当前账号表单检查、文本填充、Cookie 保存/清除、公开发布、服务端回读或性能测量。本文完成 #3 的资料与方案交付；#6 保持待实测，桌面框架仍未选择。
