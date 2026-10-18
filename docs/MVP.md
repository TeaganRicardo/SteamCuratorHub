# 首版 MVP：使用、构建与本机验收

更新：2026-10-18。首版 macOS MVP 已由 [PR #11](https://github.com/TeaganRicardo/SteamCuratorHub/pull/11) 合并至 `main`（合并提交 `731c6a1aea973f4ddf786042936b8a4f610d0a48`）。原开发基线是 `1cd2f9d3cf346937d55ed98a2b0e03f965258999`；[MVP #10](https://github.com/TeaganRicardo/SteamCuratorHub/issues/10) 继续跟踪用户真实验收。最终 CI 和本机构建证据见 [PROJECT_STATUS.md](../PROJECT_STATUS.md) 及对应 PR。

首版已连接桌面生产模块，覆盖库/任务、并行运行、成就/统计、研究、编辑和辅助发布。当前真实 Steam 时长增长与成就/统计写入、真实网页登录与发布、Gemini/Brave 付费 API，以及用户视觉验收均未通过。离线 fixture、合成动态库、Electron smoke 和 ad-hoc 签名只证明各自覆盖的实现路径。

2026-10-18 用户实测已确认当前 UI/UX 不可用（[#13](https://github.com/TeaganRicardo/SteamCuratorHub/issues/13)），此界面仅作为功能验证壳；正式界面须先进行多轮交互确认。本轮底层修复范围为 [#14](https://github.com/TeaganRicardo/SteamCuratorHub/issues/14) 的连续登录反馈、加密会话与有界重连，以及 [#15](https://github.com/TeaganRicardo/SteamCuratorHub/issues/15) 的运行和成就并用；离线通过后仍需重新进行真实验收。

## 环境与启动

首版安装包与原生 helper 面向 macOS arm64。需要 Node.js 22 或更新版本；编译 helper 需要 Xcode Command Line Tools。正常成就/统计操作还需要用户安装并登录自己的 Steam 客户端。应用不分发 Steam SDK 或客户端动态库。包声明最低 macOS 13.0，但当前本机运行检查仅在 macOS 27 执行过，最低版本兼容性尚未验收；Windows 原生 helper 与安装包尚未交付，Linux 不在首版产品承诺内。

在仓库根目录执行：

```sh
npm ci
npm run build:native
npm run start:demo
```

演示模式使用合成账号、游戏、成就、统计和资料，窗口会标明演示。演示不登录真实 Steam、不加载真实 Steam 客户端库，不调用 Gemini 或 Brave。演示工作区使用 `fixture-workspace.json`；它不代表任何真实账号的时长、成就、媒体文章或发布结果。

正常使用：

```sh
npm start
```

Steam 协议连接与 Steam 官方发布网页是独立会话。连接协议账号后工作区按实际 SteamID64 显示该账号任务；设置中的手动绑定仅用于离线编辑，不等于已登录，也不能代替原生写入时的账号核对。

提交账号密码后，登录窗口持续展示账号名和验证阶段，Steam Guard、成功和失败仍在同一窗口反馈；也可主动关闭窗口，从状态栏或“查看登录进度”继续查看。成功取得的可复用会话使用系统加密保存，下一次应用启动优先恢复。密码和 Guard 验证码不持久保存。系统加密或写盘失败时，界面明确提示本次连接尚未持久保存；不会退回明文文件。

瞬时网络断线可使用内存中的会话凭据自动重连，最多三次，等待 1、3、10 秒，每次认证最多等待 120 秒；稳定连接一分钟后恢复下一轮预算。凭据过期或被拒绝会清除保存会话并要求重新授权，其他登录冲突保留明确错误。认证恢复不会自动重新模拟运行；先前运行保持未知/暂停状态，需要用户另行启动。关闭应用保留加密会话；点击“退出连接并停止运行”会清除保存会话，下一次启动不再自动恢复该账号。

## 功能与当前边界

| 工作区 | 当前行为 | 结果边界 |
| --- | --- | --- |
| 游戏库与商店 | 库内/全商店查询、名称或 AppID 搜索、详情与官方页面、个人评测入口 | 未拥有游戏可建写作任务，运行仍须协议库确认拥有 |
| 评测任务 | 单账号单游戏唯一任务、唯一投稿群组、来源备注、可选累计分钟目标、进度状态 | 手动完成任务只改变进度，不改时长、成就或发布事实 |
| 运行与时长 | 多 AppID 同时请求、每款独立启停、累计目标、本次期限、读回和持久历史 | 请求与本机计时不证明 Steam 已计入；单停保留其他运行请求 |
| 成就与统计 | 运行中可对相同或其他 AppID 查询、预览、解锁/重锁及编辑 INT/FLOAT；保留搜索/筛选/排序、全球率与隐藏项折叠 | 原生操作自身串行，账号/schema/旧值限制仍有效；平均速率统计不直接赋值；真实 Steam 并行效果待验收 |
| 研究 | 当前 Steam 评论接口、Brave 名称搜索、独立读取媒体、身份/版本检查、来源、摘要与建议稿 | 付费墙、读取失败、同名/版本歧义和来源不足均保留状态 |
| 编辑 | 简中/英文草稿、版本校验、BBCode 原文/预览、翻译、格式、短评建议 | AI 建议需用户应用；迟到结果不能覆盖另一任务、语言或已改版本 |
| 长短评 | 长评按当前群组组合尾巴，短评正文与全文链接分别保存 | 短评不加尾巴/全文链接，首版保守限制为最多 199 个 Unicode 码点 |
| 辅助发布 | 冻结账号/AppID/群组/语言/内容版本，官方网页会话、目标与字段检查、填入 | 用户本人核对并提交，自动填入不是发布成功；不提供鉴赏家自动最终提交 |
| 保存恢复 | 有界 JSON 工作区、运行历史、操作/发布记录、备份、损坏恢复、单独加密凭据 | 导入的发布成功降为未核实，未结束运行转为结果未知，凭据不导入导出 |

工程默认是简体中文界面、文章语言独立、隐藏成就折叠、先到先停、关窗清理，不在后台持续挂时长。群组链接尾巴只是可编辑起始模板，请在正式发布前核对。自动领取 Curator Connect、云同步与双语界面不在本轮已交付范围。

## Gemini 与媒体搜索配置

用户已选择 Gemini API。进入“设置”，填写 Gemini API 密钥、该账号可用的模型 ID，默认 API base URL 为：

```text
https://generativelanguage.googleapis.com/v1beta
```

模型不写死在代码中，不承诺固定免费额度。Gemini 负责研究摘要、可编辑初稿、翻译、短评与 BBCode 处理，使用 `generateContent` 的 JSON 输出。原稿与建议分开，生成失败或取消保留已有资料和草稿；token 上限、来源数量和请求超时均有界，失败不会自动重试付费调用。上游没有返回 token 用量时保留未知状态，不把它记作零消耗。

媒体按游戏名检索使用独立 **Brave Web Search API**，需要在设置中填写 Brave Search API 密钥。Brave 是搜索服务工程选型；首版仍只有 Gemini 一家 AI 供应商。搜索标题/摘要不作为文章证据，应用只把安全 HTTP 实际取得的媒体摘录交给 Gemini。匹配包括 AppID、作品名和版本/DLC/重制/续作歧义；仅名称匹配且没有 AppID 的媒体保留版本未独立确认提示。

缺少 Brave 密钥时返回 `missing_search_configuration` 和媒体覆盖不足；仍可读取 Steam 评论，已配置 Gemini 时可以基于该有限样本生成并明确限制。缺 Gemini 密钥或模型时只保留取得的资料，不伪造生成结果。无法读到正文的来源保留失败状态，不用模型记忆、搜索片段或生成内容冒充已读文章。媒体来源数上限不包含额外的 Steam 评论样本来源。

Gemini 和 Brave 按各自账号规则计量；开发与 CI 不提供密钥或执行付费试调用。搜索结果只用于本次定位，不保留原始 API 结果作为媒体正文。当前不使用 Google Search grounding 或 URL Context 把 Grounded Results 改造成可编辑资料库。接口与使用范围依据 [Gemini API](https://ai.google.dev/api/generate-content)、[Google 附加条款](https://ai.google.dev/gemini-api/terms)、[URL Context](https://ai.google.dev/gemini-api/docs/generate-content/url-context)、[Brave Web Search GET](https://api-dashboard.search.brave.com/api-reference/web/search/get) 和 [Brave API 条款](https://api-dashboard.search.brave.com/terms-of-service)；搜索服务不授予第三方文章版权。

密钥使用 Electron 系统加密持久保存；系统加密不可用时可选择仅本次会话。API 密钥绑定目标服务 origin，改变地址时重新提供相应密钥。Steam Web API key 当前可选，原生 helper 不依赖它；它不是 Gemini 或 Brave 的替代密钥。Steam 登录密码和 Guard 验证码仅用于本次内存登录；可复用 refresh token 在账号认证确认后保存在单独凭据文件的加密字段中，不交给 renderer 或工作区。

## 本机数据、恢复与退出

正常工作区与 `credentials.json` 放在 Electron 的 `app.getPath('userData')` 目录，代码入口见 [desktop/main.mjs](../desktop/main.mjs)；它们不位于 Git 跟踪的源码目录。`workspace.json` 保存任务、双语草稿、资料、运行历史及操作/发布结果，凭据另存加密文件，Steam 发布网页数据由独立账号 partition 管理。退出连接清理相应网页会话。

设置中的导出仅导出工作区，不导出凭据、Steam 登录会话或浏览器 cookies。导入前停止活动操作；导入会替换当前工作区，确认后才写入，原数据保留为 `.previous`。工作区总序列化大小限制为 20,000,000 字节，运行历史另有数量上限；超过边界或写盘失败会报错，不能把未持久保存的结果说成已保存。

启动时工作区损坏会提供恢复 `.previous` 或选择备份的选项；备份先校验，原损坏文件保留为本机 `.corrupt-*`。不会静默新建空数据覆盖旧文件。导入/恢复的已成功发布记录需要重新核实；未结束运行变为 `outcome_unknown`，不会自动续跑。导入不会带入认证凭据；应用启动仅恢复本机独立保存的有效加密会话。凭据回滚文件排除 Steam 会话字段，显式退出也无需系统加密可用即可清除保存会话。

损坏的凭据文件会在设置中显示恢复入口，不阻断离线草稿与备份。显式恢复先归档原凭据文件，再重新填写密钥。设置保存先准备密文，工作区写入失败不会替换旧密钥；凭据提交失败会尝试恢复原配置，恢复失败则明确报告配置未完整保存。导入的成就成功历史也降为待核实并标明导入来源，不能继承本机实际写入证据。全文链接须精确对应当前 SteamID64 和 AppID；已有错误链接保留供修正，准备短评与填表前会拒绝。

运行历史以每次会话 UUID 保存，可记录相同 AppID 的多次运行。Steam 已记录总分钟、最后回读时间/状态、本机 elapsed、目标/期限、停止原因分别保留。异常重启不补算离线时间，本机 elapsed 从不添加到 Steam 总分钟；演示与导入历史分别标明来源。

关闭窗口按退出处理：先处理未保存草稿，再停止本应用的运行请求、取消 AI、关闭原生子进程和网页会话。Steam 请求无服务器确认时，清理状态仍可能未知，不能把关闭窗口理解为已核实平台停止。异常掉线、真实游戏冲突、睡眠或时钟间隙不会自动重试写入或恢复运行。

## 离线验证与构建

```sh
npm run check
npm test
npm run probe:fixture
npm run smoke
npm run package:mac
node desktop/smoke.mjs '--binary=dist/Steam Curator Hub-darwin-arm64/Steam Curator Hub.app/Contents/MacOS/Steam Curator Hub'
```

`check` 检查桌面模块语法；`test` 包含协议、工作区、控制器、资料、编辑/发布和原生 fixture。原生测试在支持的 macOS 上编译 helper，并用合成 dylib 验证 dlopen/vtable/callback 路径；其他平台会明确 skip 对应原生测试，不表示该平台原生能力已实现。

`smoke` 使用临时隔离用户目录启动真实 Electron 壳、调用 fixture IPC 并检查页面入口；报告写到本机 `.local/mvp-smoke.json`，临时 profile 结束后删除。打包后的 `--binary` 命令直接运行 `.app` 内可执行文件，报告为 `.local/packaged-smoke.json`，同样使用隔离 profile。本轮两条路径均已实际通过；它们不登录 Steam、不调用真实 API，也不是用户视觉验收。

`package:mac` 仅在 macOS arm64 执行，输出 `dist/Steam Curator Hub-darwin-arm64/Steam Curator Hub.app`；包路径和签名元数据保存在 `.local/package.json`，包内 `Contents/Resources/app/build-info.json` 另记录 `sourceCommit/sourceTree/sourceDirty/builtAt`，供用户核对实际构建的精确源码。脚本构建 helper、打包并执行 `codesign --verify --deep --strict`。当前为 **ad-hoc 签名，未公证**，没有 Developer ID 或 Gatekeeper 分发验收，不能宣称已满足公开发布条件。包内不包含用户数据、Steam 库、参考原型或测试合成动态库；第三方说明随源码保留。

CI 的六组合保留 macOS/Windows/Linux × Node 22/24；桌面 job 使用标准 `macos-15` arm64 runner，并断言实际架构，执行原生 fixture、Electron smoke 和打包。[GitHub 官方 runner 列表](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)确认该标准标签为 arm64，工作流没有使用不存在的 `macos-15-arm64` 标准标签。CI 不上传产物，结果仅保留在工作流日志与临时 runner 内；用户本机构建报告保留本地。

## 用户本机真实验收

以下由用户本人执行，开发代理不通过 RDC 操作游戏或代替视觉验收。先记录本轮应用提交 SHA、包版本/路径、Mac 架构/系统、Steam 客户端版本与目标 AppID；日志和账号数据保留本地，无需上传。各项记录“已证明 / 失败 / 不支持 / 待验证”，缺结果就保留待验证。

1. **账号与任务。** 正常启动，连接目标 Steam 账号，检查提交后窗口保留账号和阶段，完成本人 Guard 验证；核对 SteamID64 与“会话已加密保存”状态。关窗退出再启动，检查无需密码即可恢复原账号且没有自动运行；受控断线后观察有界重连，原运行不续跑。显式退出连接后再重启，确认不会恢复已退出账号。刷新库，选两款确实拥有的游戏，至少一款在此 Mac 未安装且仅提供 Windows 版本。分别建任务、选择唯一群组，确认浏览别的游戏和切换文章语言不改变草稿归属。手动绑定 ID 只验离线编辑，不作为登录证据。
2. **并行时长。** 记录两款 Steam 总分钟起点和读回时间，显示各自累计目标与本次期限，再并行启动；观察每款本机 elapsed 和 Steam 分钟。单停一款，确认另一款继续；停止前后再次读取 Steam，并在官方客户端核对。分别保存 AppID、前中后分钟/时间、停止原因与读回状态。仅本机计时增长不能通过。另测总目标已达到不发运行请求、期限自动停止、退出、掉线和睡眠后的未知/暂停状态。
3. **成就与统计。** 保持至少两款协议游戏运行，在同一账号、上述未安装 Windows-only 目标读取成就/统计，必须覆盖正在运行的相同 AppID，也检查其他 AppID；记录实际 schema、接口版本与客户端库 digest。选择允许客户端修改的成就，分别验证单项/批量解锁及重新锁定；选择实际存在且可写的 INT 与 FLOAT 字段，记录旧值、提交值、Store 回调和新 helper 回读值。核对操作前后各款运行请求保持，单停一款后其他款继续；实际 Steam 冲突保留原始结果，不接受要求全局停跑作为替代。核对 `verification.scope` 与 `independentServerRead:false`：当前 verified 的范围是匹配 Store 回调加独立进程客户端刷新，不是独立 HTTP 服务器证明。缺 schema、权限拒绝、超时和逐项 unknown 不能改为成功；未知结果先刷新并人工检查，再决定新尝试。不要用已安装 Mac 游戏代替 Windows-only 目标通过验收。
4. **研究与编辑。** 在设置填入本人 Gemini/Brave 密钥和可用模型，主动发起一次实际研究；核对搜索游戏名、媒体实际取得摘录、URL/日期、版本关系及 Steam 样本。测试读取失败、取消和来源不足提示。检查中文/英文翻译、BBCode 排版、短评字符计数及单独全文链接；保留原稿，预览后应用。生成期间改动长评或切换语言/任务，确认旧建议无法覆盖新版本。实际 API 会计量费用，本次开发未代用户试调用。
5. **辅助发布。** 先检查群组尾巴与推荐/披露信息，分别准备长评和短评冻结快照。打开官方发布页，由用户本人登录，核对网页账号、AppID、群组及字段；检查并填入后本人决定提交。字段不兼容时保留错误并用复制辅助填写，不能报告自动填入成功。长评保存真实结果 URL 后执行 API 回读核验；短评保留官方结果 URL 与人工确认来源。打开页面、按钮已点、填写成功和人工确认都不能混写为应用自动核实；超时/未知先查询结果，不盲目重试。两种发布各自独立，不因一个成功标记另一个成功。
6. **保存与恢复。** 修改双语草稿、导出工作区，退出重启检查任务/草稿/历史；导入备份后核对凭据未导入、成功发布需重验、未结束会话为结果未知且没有自动启动。另检查关窗保存、系统加密可用性和包的实际打开表现，完成用户视觉验收。

验收结果继续归属 [#5](https://github.com/TeaganRicardo/SteamCuratorHub/issues/5)、[#6](https://github.com/TeaganRicardo/SteamCuratorHub/issues/6) 与 [#7](https://github.com/TeaganRicardo/SteamCuratorHub/issues/7)。实现或 fixture 通过不能关闭这些真实门槛，也不能把某个目标失败自动降级为串行运行、只读成就或仅支持已安装 Mac 游戏。
