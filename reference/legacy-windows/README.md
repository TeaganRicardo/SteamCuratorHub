# Windows 历史原型

这是参考快照，保留旧实现供需求分析与后续移植评估使用。原型内 `PROJECT_HANDOFF.md` 是 2026-09-08 的历史材料；新项目状态与规则请从仓库根目录读取。

## 来源与完整性

- 接收日期：2026-09-22。
- 来源：用户授权的 Computer-05 原型目录当前工作树。
- 源仓库 HEAD：`4555e194cc42d9dd381f7c252f5bf08700e8765c`。快照包括其后的未提交修改和新增文件，不能用这个 SHA 代替快照内容。
- Google Drive 对应路径：`SteamCuratorHub/Legacy Version/Steam Curator Hub`。本次以 Windows 当前工作树为导入来源，未做 Drive 每个文件的逐字节对比。
- 导入原文件：100 个，共 974,823 字节。`SOURCE_MANIFEST.json` 记录逐文件 SHA-256；本说明、清单和验证记录为本次新增文档。
- 传输 ZIP SHA-256：`21f8807c4aa5e36012c554d053910f48d12da11477c0bd8e0aa6c4630f082b4b`。
- 原文件内容保持原样；ZIP 路径分隔符转换为仓库路径。
- 未导入 `.git` 历史、bin/obj、运行数据库、浏览器会话、密钥配置，以及 `Steam Text Formating` 第三方离线网页副本。旧交接文档中的离线网页路径因此不再可用。

检查源文件中的常见 API Key、GitHub Token、私钥、会话字段、邮件地址和长字面量。命中项经核对为测试数据和 SteamID 转换常量，未发现真实凭据；检查不等于对原型整体安全性的保证。

## 平台及验证

WPF / .NET 10 Windows / SQLite / WebView2。此目录不是 Mac 实现。

2026-09-22 在独立 Windows 副本中执行：

```powershell
dotnet build SteamCuratorHub.slnx -c Debug --nologo -v minimal
dotnet run --project SteamCuratorHub.IntegrationTests/SteamCuratorHub.IntegrationTests.csproj -c Debug --no-build -- --local-only
```

结果：构建 0 warning / 0 error；本地集成检查 PASS。结果摘要见 `VALIDATION.md`。未执行 GUI、Steam 写入或真实服务回归。

已知：运行页仍有本地计时逻辑；成就是只读查询；旧发布与领取命令可能只修改本地记录。完整说明见 [历史原型分析](../../docs/LEGACY_ASSESSMENT.md)。
