# Steam 并行时长与成就统计写入：证据与验证边界

更新：2026-10-17。关联：[能力调研 #2](https://github.com/TeaganRicardo/SteamCuratorHub/issues/2)、[Mac 实验 #5](https://github.com/TeaganRicardo/SteamCuratorHub/issues/5)。

研究输入为 `docs/REQUIREMENTS.md`、上述两个 Issue，以及本轮实际读取的 Valve 文档、上游源码、许可文件和版本元数据。接手时远端 `main` 为 `de0adcbbb9df995e3663a45db10f834eaeecda88`，研究分支 `docs/global-project-plan` 为 `272498910bb8445c39936b2ca2954a10aa0c9822`。文档页面按访问日记录，源码链接固定到提交；`latest` 标签不作为可复现基线。

## 结论与本轮范围

存在值得做独立实验的两条路线：用 Steam 客户端协议报告多个 AppID 正在运行；用本机 Steam 客户端接口读取和提交各游戏的成就/统计值。两者应分开实现、验证。**本轮没有连接 Steam、登录账号、运行游戏或修改真实账号；目标 Mac 的 Windows-only 未安装游戏、多 AppID 实际时长及成就写入全部仍待 #5 验证。**

原报告“Multi-AppID idle playtime is technically feasible”的结论过强：上游说明和实现支持选择候选，但不能代替本项目目标账号、客户端版本、平台和 AppID 的结果证据。存在 macOS 分支、能编译、方法返回 true 或本地计时都不构成验收通过。

可以立即开发的最小交付是独立的双 AppID 时长实验程序及离线 fixture：完整 AppID 集合启停、单项停止、账号核对、冲突/掉线终止、运行前/中/后服务器分钟数采样和结构化证据。离线部分不需要 Steam、凭据或 SDK；真实实验由用户执行。成就原生接口在 ABI、许可和回读路径落实前保留为独立实验，不能以只读功能替代写入需求。

## 能力矩阵

“文档支持”表示来源描述该能力；“源码存在”表示本轮读到相应实现；二者均不代表本项目实测通过。

| 已确认需求 | 公开 Steam Web API | Steam 客户端协议 | 本机 Steam 接口 | 本项目证据状态 |
|---|---|---|---|---|
| 查询拥有的游戏、Steam 总时长 | `GetOwnedGames` 可读，受游戏详情可见性影响；不能写时长 [V1] | `steam-user` 提供 `getUserOwnedApps`，含累计分钟字段 [N1] | 可另作候选，不能把本地缓存当最新服务器事实 | 资料已核实；未读真实账号 |
| 多 AppID 同时累计时长 | 未找到普通玩家设置累计时长的公开端点 | `gamesPlayed` 接受数组；ASF 说明其空闲游玩功能累计时长 [N1][A1] | 启动 URI/游戏进程不是本需求的证据 | 候选；并行区间内每项增长待实测 |
| Mac 管理未安装 Windows-only 游戏 | Web 查询不要求游戏安装；不提供运行能力 | ASF 明确描述不下载游戏、可处理其他系统的游戏 [A2] | Enhanced 有 macOS 实现；未找到满足本项目该目标组合的验收记录 [E1][E2] | 时长与成就分别待验证，不能互相推导 |
| 成就 schema、玩家状态、全球解锁率 | `GetSchemaForGame`、`GetPlayerAchievements`、`GetGlobalAchievementPercentagesForApp` [V2] | 本轮未确认 `steam-user` 的通用成就写入能力 | 官方 `ISteamUserStats` 可读；第三方封装另行验证 [V3] | 可选只读来源；缺失值不得当 0% |
| 单项/批量解锁与重锁 | `SetUserStatsForGame` 要求拥有目标 AppID 的发行商密钥，不适用于普通玩家客户端 [V2] | 不从“协议能报运行”推导“能改成就” | 官方 `SetAchievement`/`ClearAchievement`；SAM 派生实现调用客户端接口 [V3][S1][E2] | 源码存在；本项目单项、批量、重锁均未实测 |
| 整型/浮点统计值修改 | 同上，发行商写权限不等于用户 Web API key 权限 | 本轮不选择无已核实实现的协议写入 | 官方 `SetStat` 有 int32/float 重载；上游有对应调用 [V3][E2] | 逐游戏、逐条目可写性待测 |
| 提交成功与持久化回读 | 读接口可作为适用条目的独立核对来源 | `gamesPlayed` 本身没有持久化分钟数确认 [N2] | `Set*` 与提交及回读应分阶段；上游写计数不足以证明服务器结果 [V3][E2] | 必须增加独立结果记录 |

Steam 库累计时长与游戏自定义统计项是不同数据。即使游戏定义了名为 `playtime` 的 INT/FLOAT，也不能把 `SetStat` 的成功说成改写了 Steam 库总时长。这是根据两组 API 各自用途得到的实现边界 [V1][V4]。

## 并行时长路线：优先做协议实验

### 固定候选与可直接观察的语义

本轮 npm 元数据核实 `steam-user@5.3.0`，MIT，声明 Node.js `>=14.0.0`；其发布元数据 `gitHead` 为 `e39d7cb8e0d4c83905f2d3896fb354b13f2591ec`。默认分支另为 `02818bd08090b58ff54f1d9ce0073319b8e1eef7`，而 GitHub `releases/latest` 当时仍是 `v5.2.3`。实验应锁定实际 npm 版本和 lockfile，不能混用“最新 release”和“最新包”的含义 [N3]。

固定提交的 `components/apps.js` 显示：数组转换为 `ClientGamesPlayedWithDataBlob.games_played`；`appLaunched`/`appQuit` 依据本地 `_playingAppIds` 的变化主动触发，没有等待服务器累计时长确认。`gamesPlayed` 不是带持久化结果的 Promise。`force=true` 会在被阻塞时踢掉其他游玩会话，因此实验始终使用 `force=false`，发生 `playingState.blocked` 时停止本次实验 [N2]。

每次请求应表达**账号当前完整运行集合**：先发送 `[A, B]`，单停 A 后发送 `[B]`，最后发送 `[]`。多个游戏各建一个互不协调的计时器并分别发送 `[A]`/`[B]`，不能证明并行。集合协调器是本项目设计建议，其输入和发出的请求可以完全离线验证。

ASF 是交叉核对来源，不作为默认嵌入依赖。其配置说明 `GamesPlayedWhileIdle` 需要各 AppID 的有效许可，说明上限为 32；FAQ 描述不下载游戏文件及跨操作系统处理。32 是上游当前记录的协议使用边界，不是本项目已验证的服务保证，首个实验只用两个 AppID [A1][A2]。ASF 本轮基线为 `78226b4ea508a4da78ee271c4f71a8fb3c1d1b25`，发布标签为 `6.3.10.6`，Apache-2.0 [A3]。

### 需要由实验回答的问题

1. 当前登录账号是否确实拥有 A、B；其中至少一个是否为用户确认的 Windows-only 且未安装游戏？不从图标、启动失败或缺失缓存推导平台/安装状态。
2. A、B 在共同运行区间内，分别能否读到 Steam `playtime_forever` 的增长？该字段单位为分钟；本地秒数仅记录实验区间 [N1]。
3. 单停 A 后 B 是否继续增长？A 的延迟入账和 B 的增量应分别保存；短区间内没有变化不立即等于不支持。
4. 掉线、冲突、睡眠和退出时实际发生什么？首次程序采用掉线停止、不自动重连/恢复；不能把空集合请求已发送说成服务器确认全部停止。

资料未给出所有账号和游戏通用的入账延迟。实验应保留原始采样时间、原始分钟数和超时边界，结果为 `verified`、`failed`、`unsupported` 或 `unverified`；不使用本地时间补齐服务器数字。

## 成就/统计路线：官方语义与第三方实现分开

官方 `ISteamUserStats` 的 `SetStat` 修改内存状态；`StoreStats` 提交后应处理 `UserStatsStored_t`，包括被约束拒绝时的结果。INT/FLOAT 类型必须匹配；客户端不可写 GS/Official GS 条目，且统计可能有单调递增、最小/最大值等约束 [V3][V4]。这支持按条目显示能力与失败原因，不能承诺任何游戏的全部条目均可写。

官方当前将 `RequestCurrentStats` 标为 Deprecated，并说明客户端已在游戏启动前加载数据；页面同时保留旧初始化文字。第三方外部进程加载指定 AppID 的路径应以所用接口版本、相应回调和实际读取结果验证，不能机械套用旧版“调用一次即就绪”的流程 [V3]。

批量修改应先读取并展示计划，再分别保存每项 API 结果、提交结果和重新读取值；不假设原子事务。部分 `Set*` 已发生后，`StoreStats=false`、超时或进程退出不能简单标为“完全没有修改”，也不能盲目重复整批。读取到的值若仍来自同一客户端缓存，要标明来源；验证持久化需要新读取及可取得的服务器/客户端刷新证据。

### SAM 候选核对

| 上游及本轮固定版本 | 本轮实际查到的内容 | 能证明的范围与限制 |
|---|---|---|
| `gibbed/SteamAchievementManager`；`de8b71048a0cee3c3e97cd8535e0f55ca86513e4`；release `7.0.41` | zlib；C# SAM 的 `Client.Initialize` 设置 `SteamAppId`，建立 pipe、接入已登录用户，并核对 AppID [S1] | 是本机客户端接口的参考实现；不是 macOS arm64 或本项目实机证据 |
| `PaulCombal/SamRewritten`；`0f1f5f855d3c2dfe04258b127d42b8a95837605f`；release `v1.6.1` | README 写明 Windows/Linux；源文件 SPDX 为 `GPL-3.0-only`；本轮 loader 只有 Linux/Windows 分支 [R1][R2] | 可研究流程；不能写为已支持 Mac，也不能按宽松许可直接吸收源码 |
| `asdfghj1237890/SteamAchievementManager-Enhanced`；`f6c7bbabad55d61802766d94b12bb4e16e8bbc76`；release `v1.4.2` | zlib；Rust `imp_macos.rs` 用 `dlopen` 加载本机 `steamclient.dylib`，`SteamClient018`、`ISteamUserStats013`、vtable 调用具备读写路径 [E1][E2][E3] | 提供 Mac 候选；不是本项目对未安装 Windows-only 游戏的写入证明 |

Enhanced 的详细说明将 Mac 写入验证限定为**对已经解锁的成就做幂等重设**，清除成就交由用户验证。不能把其 README 的概括性“read/write”转写成已验证了新解锁、重锁、批量与 INT/FLOAT 全部成功 [E1]。

该版本 `imp_macos.rs::write_stats` 有 schema 权限检查、逐项 `Set*` 与 `StoreStats`；此函数最终返回基于方法返回值的计数，没有等待 `UserStatsStored_t` 及独立持久化回读。因此只宜作为适配器研究起点，仍须实现结果核实 [E2]。库进度也可能只来自本机 stats 缓存；缓存不存在不是“未解锁”证据 [E1]。

其测试文档还明确：只读 `read-game` 实验会短暂使账号显示在该游戏中；“不写成就”不能扩展为“对 Steam 状态完全没有影响”。本轮没有运行任何此类上游程序 [E4]。

## macOS 架构及公开分发边界

Valve 官方文档说明 SDK 的 `libsteam_api.dylib` 提供 x64/arm64，并说明初始化需要运行中的 Steam、正确 AppID、同一系统用户及有效许可 [V5]。这份 SDK runtime 与第三方直接加载的 **`steamclient.dylib` 是两个不同库**；前者支持 arm64不能证明目标机器的后者存在匹配架构和 ABI。对照实验要记录 macOS、CPU、Steam build、目标 dylib 架构/摘要、封装提交和接口版本；本轮没有读取本机 Steam 安装或断言其架构。

SDK 文档将 `redistributable_bin` 列为可分发运行库位置，不能据此推导整个 SDK、客户端二进制或第三方工具中任何文件都能公开提交 [V6]。本轮未下载 SDK 或核查随包协议；若后续采用 SDK，应核实所用版本实际许可、文件清单和分发条件。直接加载用户已安装的客户端库可避免将该库放入 Git，但仍不能用第三方源码许可替 Valve 文件授予分发权。

本轮只选择读取上游或以正常包管理方式引用，不复制其代码。若日后吸收代码：MIT 保留版权和许可；zlib 保留声明并注明修改；Apache-2.0 保留相应许可/声明义务；SamRewritten 的 GPL-3.0-only 单独评估。上游根目录许可不覆盖 Steam SDK/client、图标和所有传递依赖。Valve 也单独说明 SDK 与 copyleft 组合的兼容性问题；这条文档针对 Steam 分发，不能泛化成已裁定本项目任何组合的许可结果 [N3][A3][S1][R2][E3][V7]。

## 对架构的具体影响

建议保留三个边界，产品层不直接拿 SDK bool、进程存活或计时器作为事实：

```text
SteamReadAdapter
  readOwnedApps / readPlaytime / readSchema / readAchievements / readGlobalRates

PlaytimeTransport（一个账号会话的完整 AppID 集合）
  setPlaying(appIds, force=false) / observeConflict / stop / disconnect

UserStatsTransport（固定账号、AppID、接口版本的独立 worker）
  read / previewChanges / apply / awaitStoreResult / refreshAndVerify
```

每次操作绑定 `runId`、账号身份、AppID 与依赖版本；迟到回调不能更新其他账号或游戏。成就 worker 在加载 Steam 库前固定 AppID，进程隔离是候选设计，因为上游同样使用进程环境绑定 AppID [S1][E2]。协议会话与本地客户端是否互相影响必须作为 #5 的冲突项实测，不能因为各自可用就假设可同时使用。

停止条件沿用需求的分层：任务的 Steam 累计目标与本次运行结束时间分别保存；“先到先停”仍是建议默认，未擅自提升为已确认规则。实验程序可以先使用固定实验区间，完整产品调度不在此次验证范围。

## #5 最小实验与回读标准

### 无账号 fixture 与程序交付

- 固定依赖、操作系统和运行时版本；把 transport 注入，默认 fixture 模式，不读取 Steam 目录或现有会话。
- fixture 覆盖 `[A,B] → [B] → []`、冲突、掉线、身份不符、缺失/未增长/迟到的时长样本；缺证据时输出 `unverified`。
- 可选真实模式由用户显式启动，展示确切两个 AppID、账号核对方式、区间和停止步骤；不保存密码、refresh token、web cookie 或完整用户库。
- 依赖审计结果与离线测试分别记录；override、能加载 protobuf 或打包成功只证明相应本地行为，不能替代真实登录及协议兼容性结果。

### 由用户执行的真实结果

| 阶段 | 必须保留的事实 | 通过边界 |
|---|---|---|
| 准备 | 平台/架构、Steam build、程序 SHA、包版本、账号核对结果；A/B 的有效许可、Windows-only 和未安装声明 | 明确目标，不能用随意挑选的免费测试 AppID 替换验收目标 |
| 共同运行 | 原始起始分钟数、同一次集合请求 `[A,B]`、中段服务器读取、采样时间 | A、B 在共同区间各有服务器累计分钟增加；本地计时不是替代证据 |
| 单停 | 请求 `[B]`、过渡和最终采样；异常/延迟入账说明 | B 在单停后继续增长；A 的实际停止结果独立标记 |
| 退出/异常 | 最终 `[]` 请求、断开状态、Steam 状态的用户确认；未确认停止则保留 unknown | 不留下被错误标为已停止的未知会话 |
| 成就单项及批量 | AppID、schema/权限、修改前值、计划值、逐项结果、提交回调、新读取结果 | 新解锁、重锁、批量分别记录，幂等重设不代替状态改变 |
| INT/FLOAT | 类型、合法范围、原值/提交值/回读值、回调/错误 | 两类型各自有实际状态改变及持久化证据；受限条目明确记原因 |

只完成时长程序和 fixture 时，#5 仍保持进行中或待用户验证；不能关闭成就写入及 Windows-only 未安装游戏的验收。若某项失败，保留原硬性要求、实际错误和可复现环境，再决定替代路线；不降级为串行队列或仅已安装 Mac 游戏。

## 一手来源清单

以下来源均于 2026-10-17 读取。Valve 文档为动态页面；GitHub 代码/许可固定提交。上游的“已验证”只归属于其原作者陈述。

- [V1：Valve IPlayerService，GetOwnedGames/GetSingleGamePlaytime](https://partner.steamgames.com/doc/webapi/IPlayerService)。单游戏接口另有 key 关联 AppID 条件，不能默认比库列表更通用。
- [V2：Valve Web API ISteamUserStats，读接口及发行商 SetUserStatsForGame](https://partner.steamgames.com/doc/webapi/ISteamUserStats)。
- [V3：Valve 本地 ISteamUserStats，Set/ClearAchievement、SetStat、StoreStats 与回调](https://partner.steamgames.com/doc/api/ISteamUserStats)。
- [V4：Valve Stats and Achievements，类型、限制、离线缓存和 Set By](https://partner.steamgames.com/doc/features/achievements)。
- [V5：Valve Steamworks API Overview，macOS runtime 与初始化](https://partner.steamgames.com/doc/sdk/api?l=english)。
- [V6：Valve Steamworks SDK 目录说明](https://partner.steamgames.com/doc/sdk)。
- [V7：Valve Distributing Open Source Applications on Steam](https://partner.steamgames.com/doc/sdk/uploading/distributing_opensource)。
- [N1：steam-user 发布提交 README，gamesPlayed/getUserOwnedApps/playingState](https://github.com/DoctorMcKay/node-steam-user/blob/e39d7cb8e0d4c83905f2d3896fb354b13f2591ec/README.md)。
- [N2：steam-user 发布提交 components/apps.js](https://github.com/DoctorMcKay/node-steam-user/blob/e39d7cb8e0d4c83905f2d3896fb354b13f2591ec/components/apps.js#L45-L100)。
- [N3：steam-user 5.3.0 package.json](https://github.com/DoctorMcKay/node-steam-user/blob/e39d7cb8e0d4c83905f2d3896fb354b13f2591ec/package.json)、[MIT LICENSE](https://github.com/DoctorMcKay/node-steam-user/blob/e39d7cb8e0d4c83905f2d3896fb354b13f2591ec/LICENSE)、[npm 5.3.0 元数据](https://registry.npmjs.org/steam-user/5.3.0)。npm 元数据本轮同时通过 `npm view steam-user@5.3.0 version license engines gitHead dist.integrity --json` 核对。
- [A1：ASF Configuration，GamesPlayedWhileIdle](https://github.com/JustArchiNET/ArchiSteamFarm/wiki/Configuration#gamesplayedwhileidle)。Wiki 动态页，按访问日记录。
- [A2：ASF FAQ，macOS/跨平台和时长说明](https://github.com/JustArchiNET/ArchiSteamFarm/wiki/FAQ)。Wiki 动态页，按访问日记录。
- [A3：ASF 固定提交及 Apache-2.0 LICENSE](https://github.com/JustArchiNET/ArchiSteamFarm/blob/78226b4ea508a4da78ee271c4f71a8fb3c1d1b25/LICENSE.txt)、[6.3.10.6 release](https://github.com/JustArchiNET/ArchiSteamFarm/releases/tag/6.3.10.6)。
- [S1：SAM Client.cs](https://github.com/gibbed/SteamAchievementManager/blob/de8b71048a0cee3c3e97cd8535e0f55ca86513e4/SAM.API/Client.cs)、[zlib LICENSE](https://github.com/gibbed/SteamAchievementManager/blob/de8b71048a0cee3c3e97cd8535e0f55ca86513e4/LICENSE.txt)、[7.0.41 release](https://github.com/gibbed/SteamAchievementManager/releases/tag/7.0.41)。
- [R1：SamRewritten README](https://github.com/PaulCombal/SamRewritten/blob/0f1f5f855d3c2dfe04258b127d42b8a95837605f/README.md)、[v1.6.1 release](https://github.com/PaulCombal/SamRewritten/releases/tag/v1.6.1)。
- [R2：SamRewritten loader 的平台分支及 GPL-3.0-only SPDX](https://github.com/PaulCombal/SamRewritten/blob/0f1f5f855d3c2dfe04258b127d42b8a95837605f/src/steam_client/create_client.rs)、[GPL LICENSE](https://github.com/PaulCombal/SamRewritten/blob/0f1f5f855d3c2dfe04258b127d42b8a95837605f/LICENSE)。
- [E1：Enhanced web README，Mac 幂等写验证与缓存边界](https://github.com/asdfghj1237890/SteamAchievementManager-Enhanced/blob/f6c7bbabad55d61802766d94b12bb4e16e8bbc76/web/README.md#L104-L166)。
- [E2：Enhanced imp_macos.rs，加载、回调准备和写入实现](https://github.com/asdfghj1237890/SteamAchievementManager-Enhanced/blob/f6c7bbabad55d61802766d94b12bb4e16e8bbc76/web/steam-core/src/imp_macos.rs)。
- [E3：Enhanced zlib LICENSE](https://github.com/asdfghj1237890/SteamAchievementManager-Enhanced/blob/f6c7bbabad55d61802766d94b12bb4e16e8bbc76/LICENSE.txt)、[v1.4.2 release](https://github.com/asdfghj1237890/SteamAchievementManager-Enhanced/releases/tag/v1.4.2)。
- [E4：Enhanced TESTING，真实集成与 presence 副作用](https://github.com/asdfghj1237890/SteamAchievementManager-Enhanced/blob/f6c7bbabad55d61802766d94b12bb4e16e8bbc76/web/TESTING.md#L212-L234)。
