# 更新日志

格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循[语义化版本](https://semver.org/lang/zh-CN/)。

版本号规则：**主版本号 = 所用的 .NET 主版本**（10.x 对应 .NET 10），次版本号加功能，修订号修 bug。破坏性变更尽量攒到换 .NET 大版本时一起发；周期内确需破坏的在次版本发，并在该版本段落顶部加粗提示。

发布节奏：开发在 `dev` 上进行，合并到 `main` 后在 `main` 上打 `v*` 标签，标签触发发包。**发了什么，以本文件为准。**

## [Unreleased]

## [10.0.1] - 2026-09-21

### Changed

- 包内 README 增加 NuGet 版本徽章。库的代码与行为没有变化，与 10.0.0 一致。

## [10.0.0] - 2026-09-21

首次发布 NuGet 包 `SimpleOneX.SmartUpdater`。

### Added

- 更新能力
  - 程序在运行中替换自己并重启，不需要 updater.exe，包内没有任何 `.exe`
  - 日志式两阶段提交：中途断电或崩溃，下次启动自动恢复；替换出错按相反顺序回滚
  - 下载支持断点续传，逐文件校验 SHA-256，下载前检查磁盘空间
  - 用 `ETag` / `304` 轮询检查新版本，轮询间隔带随机抖动
  - 发布方式：可选 / 强制更新、灰度、阶梯升级（`minUpdatableFrom`）、升级时保留用户文件
  - 可选 ECDSA P-256 签名验签
- 接入方式
  - `SmartUpdaterApp.Run(args)`、`StartupResult.TryAcquireSingleInstance`、`SmartUpdaterApp.ActivateExistingInstance`
  - `UpdateClient` / `UpdateClientOptions`，事件：`UpdateAvailable`、`ProgressChanged`、`Restarting`、`Failed`
  - 支持 HTTP 与文件共享（UNC）两种服务器；目标框架 `net10.0`，仅 Windows，兼容 NativeAOT，零第三方依赖
- 配套工具（不随包发布）：`SmartUpdater.Packer` 打包与签名图形界面、`MinimalApp.WinForms` 示例、`MockServer` 模拟服务器、端到端脚本

[Unreleased]: https://github.com/SimpleOne-X/SmartUpdater/compare/v10.0.1...HEAD
[10.0.1]: https://github.com/SimpleOne-X/SmartUpdater/releases/tag/v10.0.1
[10.0.0]: https://github.com/SimpleOne-X/SmartUpdater/releases/tag/v10.0.0
