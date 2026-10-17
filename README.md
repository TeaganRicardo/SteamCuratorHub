# Steam Curator Hub

个人 Steam 鉴赏家工作台：游戏库与商店查询、多游戏并行运行、成就和统计管理、评论研究、个人长评及鉴赏家短评。一个已连接的 Steam 账号管理多个鉴赏组，任务和历史按账号隔离。

首版采用 Electron + Node.js，原生成就/统计 helper 与安装包优先支持 **macOS arm64**。Windows 是后续产品目标，当前不提供 Windows 原生写入或安装包承诺。[MVP #10](https://github.com/TeaganRicardo/SteamCuratorHub/issues/10) 跟踪本轮完整工作流交付；真实 Steam、网页发布和付费 API 验收尚未通过，离线测试及演示不能替代这些结果。

## 本机起步

需要 Node.js 22 或更新版本。macOS 原生 helper 构建还需要 Xcode Command Line Tools。进入仓库根目录：

```sh
npm ci
npm run build:native
npm run start:demo
```

演示使用合成游戏、成就与资料，不登录 Steam、不写真实账号，也不调用付费 AI 或搜索 API。正常运行使用 `npm start`；在设置中填写 Gemini API 密钥、模型及 Brave Search 密钥后，才会在用户发起研究时调用对应服务。未配置媒体搜索时明确显示资料覆盖不足，Steam 评论样本仍可取得。

```sh
npm run check
npm test
npm run smoke
npm run package:mac
```

`smoke` 是隔离临时用户目录的 Electron fixture 检查；`package:mac` 生成本机 `dist/` 下的 arm64 `.app`，执行 ad-hoc 签名和验证，尚未 Developer ID 签名或公证。完整起步、数据路径、能力边界和用户本机验收步骤见 [MVP 使用与验收](docs/MVP.md)。

## 项目入口

- [项目状态](PROJECT_STATUS.md)：当前分支、基线、实现与未验证门槛。
- [产品需求](docs/REQUIREMENTS.md)：用户确认、工程默认、原型行为与未决产品选项。
- [MVP 使用与验收](docs/MVP.md)：操作、构建、本机数据及真实结果记录。
- [模块契约](desktop/CONTRACT.md)、[原生 helper](desktop/native/README.md)：接口和验证范围。
- [Steam 能力调研](docs/research/steam-feasibility.md)、[发布与会话调研](docs/research/publishing-feasibility.md)、[AI 调研](docs/research/ai-feasibility.md)：早期一手来源与方案比较；当前供应商决定以需求文档为准。
- [并行时长验证工具](tools/steam-validation/README.md)：独立 CLI 子实验，已合并 [PR #9](https://github.com/TeaganRicardo/SteamCuratorHub/pull/9)。
- [历史原型分析](docs/LEGACY_ASSESSMENT.md)、[Windows 参考快照](reference/legacy-windows/README.md)、[协作规则](AGENTS.md)。

GitHub 是唯一开发基线。用户工作区备份、凭据和机器配置保留本机；不使用 Git 提交真实用户数据库或 Steam 会话。公开仓库暂未指定开源许可证；原生接入参考来源及分发保留要求见 [第三方说明](desktop/native/THIRD_PARTY_NOTICES.md)。
