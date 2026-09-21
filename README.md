<div align="center">

<img src="assets/X-logo-256.png" alt="SmartUpdater" width="128" height="128">

# SmartUpdater

*让 Windows 桌面程序自己更新自己：不带 updater.exe，断电不怕，失败自动回滚。*

[![NuGet](https://img.shields.io/nuget/v/SimpleOneX.SmartUpdater?logo=nuget)](https://www.nuget.org/packages/SimpleOneX.SmartUpdater)
[![license](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)
[![Stars](https://img.shields.io/github/stars/SimpleOne-X/SmartUpdater?style=flat&logo=github)](https://github.com/SimpleOne-X/SmartUpdater/stargazers)
[![Forks](https://img.shields.io/github/forks/SimpleOne-X/SmartUpdater?style=flat&logo=github)](https://github.com/SimpleOne-X/SmartUpdater/forks)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![platform](https://img.shields.io/badge/platform-Windows-0078D6)

📖 [使用手册](docs/使用手册.md) · 🚀 [快速开始](#怎么用) · 📋 [更新日志](CHANGELOG.md) · 🏁 [里程碑](#里程碑)

</div>

---

NuGet 包名：`SimpleOneX.SmartUpdater`

## 这是什么

**让你的 Windows 桌面程序自己更新自己。** 装一个 NuGet 包，加几行代码，程序就能在后台发现新版本、下载、替换自己并重启。

- **不用管理员权限**：装在用户目录即可，更新时不弹 UAC。
- **不需要额外的 updater.exe**：包里没有任何 `.exe`，零第三方依赖。
- **更新过程不会把程序弄坏**：中途断电或崩溃，下次启动会自动恢复。
- **服务器很简单**：只要一个能放静态文件的地方（Nginx、IIS、OSS、共享目录都行）。

> 目标框架 `net10.0`，仅支持 Windows，兼容 NativeAOT。

## 亮点

| 亮点 | 说明 |
|---|---|
| **自己替换自己** | Windows 允许给正在运行的 `.exe` 和已加载的 `.dll` 改名，所以主程序能在运行中把自己换掉再重启，不需要另外带一个 updater 程序 |
| **断电、崩溃不怕** | 更新分"先写好新文件、记一笔日志、再改名生效"几步，每一步都可以恢复。测试里在每一个操作步骤上模拟崩溃，重启后都能收敛到完整的新版本或完整的旧版本，不会出现新旧文件混在一起 |
| **失败自动回滚** | 替换到一半出错，会按相反顺序把已换的文件改回去；旧文件就是改名产生的备份，不额外占磁盘 |
| **下载可靠** | 断线后从断点继续下载；下载完逐个文件校验 SHA-256，对不上就不安装；下载前先检查磁盘空间够不够 |
| **检查更新很省流量** | 用 `ETag` / `304` 轮询，版本没变化时几乎不产生流量；轮询间隔带随机抖动，不会让所有客户端同一秒去请求服务器 |
| **发布方式灵活** | 灰度（先给 5% 的用户）、强制更新或可选更新、阶梯升级（太老的版本先升到中间版本）、升级时保留用户配置文件（如 `appsettings.json`） |
| **防篡改** | 可选的 ECDSA P-256 签名：发布时签名，客户端填公钥验签，签名不对就拒绝安装 |
| **看得见发生了什么** | 自带滚动日志文件；可选把"更新成功 / 失败 / 心跳"上报到你的服务器；也可以用回调接到你自己的日志系统 |
| **轻量、干净** | 一个 NuGet 包，零第三方依赖，包内没有 `.exe`；不要管理员权限，更新时不弹 UAC |
| **服务器随便选** | 只要能放静态文件：Nginx、IIS、OSS，甚至局域网共享目录都行 |

## 怎么用

### 1. 安装

```powershell
dotnet add package SimpleOneX.SmartUpdater
```

所有公开类型都在命名空间 `SimpleOneX.SmartUpdater` 下。

### 2. 在 `Main` 里接入

```csharp
using SimpleOneX.SmartUpdater;

[STAThread]
static void Main(string[] args)              // 必须接收 args
{
    // 必须是 Main 的第一句：处理"更新后重启"的交接和崩溃恢复
    StartupResult startup = SmartUpdaterApp.Run(args);

    // 单实例：已经有一个在运行就通知它，自己退出
    using SingleInstanceHandle? instance = startup.TryAcquireSingleInstance("MyApp");
    if (instance is null)
    {
        SmartUpdaterApp.ActivateExistingInstance();
        return;
    }

    // ……之后才是你自己的启动代码
    ApplicationConfiguration.Initialize();
    Application.Run(new MainForm(startup));
}
```

### 3. 启动更新

指向服务器上的 `releases.json`，一行就够：

```csharp
await new UpdateClient("https://你的服务器/updates/releases.json").RunAsync();
```

`RunAsync` 会定期检查新版本。**只有新版本已经启动后它才会返回**，所以返回时你的程序应当退出（例如关闭主窗口）：

```csharp
private readonly CancellationTokenSource _cts = new();

protected override async void OnShown(EventArgs e)
{
    var client = new UpdateClient("https://你的服务器/updates/releases.json");
    try   { await client.RunAsync(_cts.Token); Close(); }   // 新版本已启动，本进程退出
    catch (OperationCanceledException) { }                   // 你自己取消了
}
```

**想让用户自己决定要不要更新？** 订阅 `UpdateAvailable` 事件（只在"可选更新"时触发；不订阅就自动更新）：

```csharp
client.UpdateAvailable += (sender, e) =>
{
    if (MessageBox.Show($"发现新版本 {e.Version.ToString(3)}，现在更新吗？\n\n{e.Notes}",
                        "更新", MessageBoxButtons.YesNo) == DialogResult.Yes)
        e.Accept();      // 现在更新
    else
        e.Postpone();    // 稍后再问；e.Skip() 则跳过这个版本
};
```

其他事件：`ProgressChanged`（下载进度）、`Restarting`（即将重启，可在这里保存数据）、`Failed`（更新失败）。事件已经切回 UI 线程，处理器里不需要自己 `Invoke`。

更新完成后，新版本启动时可以提示一下（建议放状态栏，别弹窗）：

```csharp
if (startup.JustUpdated)
    statusBar.Text = $"已从 {startup.FromVersion?.ToString(3)} 更新到 {startup.ToVersion?.ToString(3)}";
```

### 4. 发布新版本

1. 发布你的应用：`dotnet publish MyApp -c Release -o .\publish`
2. 打开打包工具（图形界面）：`dotnet run -c Release --project tools/SmartUpdater.Packer`
3. 在界面里选 `publish` 目录，填版本号，点"生成升级包 (pack)"，得到：

   ```
   releases/
   ├── releases.json              # "新版本清单"，客户端每隔一会儿来看一眼
   └── packages/
       └── MyApp-1.2.4.zip        # 升级包
   ```

4. 把 `releases` 目录上传到服务器。**先传 `packages/`，最后传 `releases.json`**，客户端就不会看到指向不存在文件的条目。

需要防篡改时，用界面里的"签名"功能签名，再把它显示的公钥填到客户端的 `UpdateClientOptions.PublicKey`。

## 必须知道的几件事

1. **`SmartUpdaterApp.Run(args)` 必须是 `Main` 的第一句**，在任何界面初始化、单实例检查之前。
2. **程序要装在当前用户能写入的目录**（推荐 `%LOCALAPPDATA%\你的应用\`）。装在 `Program Files` 下更新功能会被自动禁用，程序照常运行、不报错。
3. **`RunAsync` 返回就意味着新版本已经启动**，你的程序应当退出。
4. 用共享目录（UNC）当服务器时没有 TLS 保护，请务必先开签名。

更多细节（全部选项、灰度、强制更新、保留用户配置、排障）见《使用手册》。

## 里程碑

**已完成**（都有自动化测试守住，4 个测试项目共 1811 个测试全部通过）

- [x] **更新协议与校验**：`releases.json` 与包内清单的格式和校验、版本比较、灰度分桶、签名验签
- [x] **更新引擎**：日志式两阶段提交、崩溃恢复、失败回滚、下载与哈希校验、磁盘空间检查、滚动日志与上报队列
- [x] **客户端与公开 API**：`SmartUpdaterApp`、`UpdateClient`、事件、HTTP / 文件共享两种传输、启动交接、单实例
- [x] **发布工具**：Packer 图形界面（打包、签名），同一输入打出逐字节相同的包
- [x] **示例与联调**：WinForms 示例应用；模拟服务器（故障注入、限速、断流、改写 feed）
- [x] **端到端脚本**：场景 S1（可选更新 → 用户点"现在更新" → 重启到新版本），真实进程 + 真实窗口

**尚未完成**

- [ ] 其余端到端场景的脚本：回滚、强制更新、灰度、签名、断点续传、断网、崩溃恢复（它们的核心逻辑有单元与集成测试覆盖，只是还没有"真实进程 + 真实窗口"的验证）
- [ ] 更新提交过程被中断后再次崩溃的极端序列，可能静默降级到较旧的文件集（需要增加提交进度日志，会改动协议）
- [ ] Packer 在高并发下偶发写文件失败（杀毒软件占用），计划加有限次重试
- [ ] Packer 图形界面的中文字体与文件对话框还没有截图确认

详细的验证情况和已知问题见[测试文档](docs/测试文档.md)。

## 文档

| 文档 | 适合谁 |
|---|---|
| [使用手册](docs/使用手册.md) | **从这里开始**：怎么把更新功能接进你的程序、怎么发新版本、出错怎么办 |
| [更新日志](CHANGELOG.md) | 每个版本有什么变化 |
| [测试文档](docs/测试文档.md) | 怎么跑测试、每类测试守住什么、模拟服务器的联调接口 |

## 示例

- `samples/MinimalApp.WinForms`：完整的 WinForms 示例，包含检测、弹窗询问、下载、替换、重启的真实往返。
- `samples/MockServer`：本地模拟服务器，用来联调和制造故障：`dotnet run --project samples/MockServer -- --root .\releases --port 0`。

## 仓库结构

```
src/SmartUpdater/           包本体
tests/                      单元 / 集成测试
tools/SmartUpdater.Packer/  打包与签名工具（图形界面）
tools/e2e/                  端到端脚本（真实进程 + 真实窗口）
tools/aot-smoke/            AOT 冒烟检查
tools/pack-check/           断言 NuGet 包内没有 .exe
samples/                    示例应用与模拟服务器
docs/                       文档
```

## 许可

Apache-2.0，见 [LICENSE](LICENSE)。
