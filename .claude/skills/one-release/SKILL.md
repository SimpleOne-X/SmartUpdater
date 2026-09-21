---
name: one-release
description: SmartUpdater 一键发版——输入 /one-release X.Y.Z 就全自动完成定版、CHANGELOG、版本号、本地验绿、推 dev、等自动合并进 main、打 v* 标签、监控发包到 nuget.org 并核对。用户说"发版 / one-release / 发包 X.Y.Z"时使用。勿用于其他项目。
disable-model-invocation: true
argument-hint: "X.Y.Z"
---

# one-release：SmartUpdater 一键发版

**用户输入 `/one-release X.Y.Z` 就是授权发这个版本，全程不要再问确认。** 只有下面写明"停"的情况才中止并报告原因。

背景：开发在 `dev`；`dev` 推送后 `auto-merge` 工作流自动开 PR 到 `main` 并启用 auto-merge，`build-test` 变绿后自动合并；在 `main` 上打 `v*` 标签后 `release` 工作流自动发到 nuget.org（Trusted Publishing）并创建 GitHub Release。本机不 pack、不 push 包，也没有 NuGet key。

## 1. 校验并同步

1. `X.Y.Z` 必须匹配 `^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$`；不匹配就停。
2. `git fetch origin --tags`，`git tag -l 'v*' --sort=-v:refname`：目标版本必须大于最新标签，且 `vX.Y.Z` 不存在；否则停。
3. 工作区必须干净（`git status --porcelain` 为空），否则停。
4. 切到 `dev`，`git pull --ff-only origin dev`，再 `git merge --no-edit origin/main`（把 main 上的自动合并提交带回 dev）。
5. 上一次发版之后 `dev` 必须有实质提交：`git log v<上一版>..dev --oneline` 为空就停。
6. 最近一次 `gh run list --limit 5` 若是 0–2 秒的 failure，是调度层拒绝，停并报告原因，不要推标签。

## 2. 准备（在 `dev`）

1. **CHANGELOG.md**：保留顶部空的 `## [Unreleased]`，其后新增 `## [X.Y.Z] - <今天 YYYY-MM-DD>`。条目按 `git diff v<上一版>..dev` 的**真实改动**写，归入 Added / Changed / Fixed / Removed，不要照提交标题猜；面向使用者，不写过程和历史。更新底部链接：`[Unreleased]: …/compare/vX.Y.Z...HEAD`，并新增 `[X.Y.Z]: …/releases/tag/vX.Y.Z`。
2. **版本号**：`src/SmartUpdater/SmartUpdater.csproj` 的 `<Version>` 改成 `X.Y.Z`（发布版本以标签为准，这里只是本地默认值）。README 里如有写死的版本号一并改。
3. 主版本号跟随 .NET 主版本（10.x ↔ .NET 10），次版本加功能，修订号修 bug；若用户给的版本号与改动性质明显不符，只在最终报告里提醒，不中止。

## 3. 本地验绿

```powershell
dotnet build SmartUpdater.slnx -c Release
dotnet test  SmartUpdater.slnx -c Release
```


## 4. 提交并推 dev

```powershell
git add -A
git commit -m "chore(release): X.Y.Z 定版 changelog 与版本号

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
git push origin dev
```

## 5. 等自动合并进 main

轮询到该提交进入 `main`（每 20 秒一次，最多 15 分钟）：

```powershell
gh pr list --base main --head dev --state all --limit 1 --json number,state,mergedAt
```

PR 状态变成 `MERGED` 即可。超时或 `ci` 红了就停并报告失败的 job 与日志（`gh run view <id> --log-failed`）；**不要用管理员权限绕过、不要改保护规则**。

## 6. 打标签（在 main 上）

```powershell
git fetch origin
git switch main
git pull --ff-only origin main
```

确认 `main` 上的 `CHANGELOG.md` 含 `## [X.Y.Z]` 且 csproj `<Version>` 是 `X.Y.Z`，然后：

```powershell
git tag vX.Y.Z
git push origin vX.Y.Z
```

## 7. 监控发包并核对

1. `gh run list --workflow release --limit 1` 取 run id，`gh run watch <id> --exit-status`。失败就停，报告失败的 step 与日志；标签已推出，需要重发时补一个新的修订号，不要删标签重推（除非该 run 在 verify 阶段就失败、包从未发出）。
2. 核对 GitHub Release：`gh release view vX.Y.Z` 应有 `.nupkg` 与 `.snupkg`。
3. 核对 nuget.org：`curl -s https://api.nuget.org/v3-flatcontainer/simpleonex.smartupdater/index.json`，应包含 `X.Y.Z`；新包可能有几分钟索引延迟，每 30 秒重试，最多 15 分钟。
4. 回到 `dev` 并同步：`git switch dev`，`git merge --ff-only origin/main`（不能快进就 `git merge --no-edit origin/main`）。

## 8. 最终报告

用中文，给出实际命令输出：版本号、release run 链接、GitHub Release 资产、nuget.org 是否已能查到该版本。没验证到的项如实写"未确认"。
