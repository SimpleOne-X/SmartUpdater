using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimpleOneX.SmartUpdater;

// AOT 冒烟宿主：让 ILC 真正走到客户端的可达路径（FileReleaseFeed → Validate → Explain → Options → 签名 → 上报序列化 → 传输构造 → SmartUpdaterApp.Run → UpdateClient.RunAsync）。
// 任一步失败以非零退出并打印原因；全部成功最后一行打印 AOT-SMOKE OK。
// 本文件由 tools/aot-smoke/run-aot-smoke.ps1 拷进 WorkDir 的宿主项目（作为 Program.cs）；仓库里保留一份是为了让它能被审查与 diff。
static int Fail(string reason)
{
    Console.WriteLine($"AOT-SMOKE FAIL: {reason}");
    return 1;
}

string scratch = Path.Combine(AppContext.BaseDirectory, "aot-smoke-scratch");
Directory.CreateDirectory(scratch);
string feedPath = Path.Combine(scratch, "releases.json");
File.WriteAllText(feedPath, """
{
  "schemaVersion": 1,
  "channel": "stable",
  "client": { "pollIntervalSeconds": 300, "jitterWindowSeconds": 600, "heartbeatIntervalSeconds": 21600 },
  "releases": [
    { "version": "1.2.4", "releasedAt": "2026-09-18T10:00:00Z",
      "package": { "url": "packages/MyApp-1.2.4.zip", "size": 12345678, "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" },
      "minUpdatableFrom": "1.0.0", "mode": "mandatory", "rolloutPercent": 100, "notes": "修复若干问题" }
  ]
}
""", new UTF8Encoding(false));

try
{
    // 1. 文件 feed → 校验 → 挑选
    var fileFeed = new FileReleaseFeed(feedPath);
    FeedResult result = await fileFeed.GetAsync(null, CancellationToken.None);
    if (result.IsNotModified || result.Document is null) { return Fail("FileReleaseFeed 没有返回文档"); }
    ValidatedFeed validated = ReleaseFeedValidator.Validate(result.Document, NullUpdateLog.Instance);
    SelectionResult selection = ReleaseSelector.Explain(validated.Document, new SelectionContext(new Version(1, 0, 0, 0), Guid.Parse("11111111-2222-3333-4444-555555555555"), new HashSet<Version>(), false));
    ReleaseEntry? selected = selection.Selected;
    if (selected is null || selected.Version != new Version(1, 2, 4, 0)) { return Fail($"挑选结果不对：{selection.Outcome} {selection.Selected?.Version}"); }

    // 2. 签名原语
    using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    string spki = ReleaseSignature.ExportPublicKey(key);
    ReleaseEntry raw = validated.RawOf(selected);
    var signed = new ReleaseEntry
    {
        Version = raw.Version, ReleasedAt = raw.ReleasedAt, Package = raw.Package, MinUpdatableFrom = raw.MinUpdatableFrom,
        Mode = raw.Mode, RolloutPercent = raw.RolloutPercent, Notes = raw.Notes, Signature = ReleaseSignature.Sign(raw, key),
    };
    using (ECDsa publicKey = ReleaseSignature.ImportPublicKey(spki))
    {
        if (!ReleaseSignature.Verify(signed, publicKey)) { return Fail("签名验证应通过"); }
        var tampered = new ReleaseEntry { Version = signed.Version, ReleasedAt = signed.ReleasedAt, Package = signed.Package, Mode = signed.Mode, RolloutPercent = signed.RolloutPercent, Notes = "tampered", Signature = signed.Signature };
        if (ReleaseSignature.Verify(tampered, publicKey)) { return Fail("篡改后的签名不应通过"); }
    }

    // 3. 选项解析（internal，经 InternalsVisibleTo("AotSmoke")）
    ResolvedClientOptions resolved = ResolvedClientOptions.From(new UpdateClientOptions { Feed = fileFeed, PublicKey = spki, AppId = "AotSmoke-00000000" }, new UpdateEnvironment());
    if (resolved.AppId != "AotSmoke-00000000") { return Fail("AppId 解析不对"); }

    // 4. 上报序列化
    string json = JsonSerializer.Serialize(
        new UpdateReport { EventType = UpdateEventType.Heartbeat, DeviceGuid = Guid.NewGuid(), IsSuccess = true, ReportedAt = DateTimeOffset.UtcNow, ToVersion = new Version(1, 2, 4, 0) },
        SmartUpdaterJsonContext.Default.UpdateReport);
    if (!json.Contains("\"eventType\":\"Heartbeat\"", StringComparison.Ordinal)) { return Fail("UpdateReport 序列化不对"); }

    // 5. HTTP 传输可构造
    using var http = new HttpClient();
    _ = new HttpReleaseFeed(http, "https://localhost/updates/releases.json");
    _ = new HttpPackageDownloader(http, new Uri("https://localhost/updates/releases.json"));
    _ = new HttpUpdateReporter(http, "https://localhost/api/v1/update-reports");

    // 6. 启动收尾与单实例（实例名带随机后缀：并行跑多份冒烟时命名 Mutex / 事件不冲突）
    string instanceName = $"AotSmoke-{Guid.NewGuid():N}";
    StartupResult startup = SmartUpdaterApp.Run([], "AotSmoke-00000000");
    if (!startup.IsUpdateEnabled) { return Fail($"启动收尾禁用了更新：{startup.UpdateDisabledReason}"); }
    using (SingleInstanceHandle? instance = startup.TryAcquireSingleInstance(instanceName))
    {
        if (instance is null) { return Fail("第一次 TryAcquireSingleInstance 不应为 null"); }
        if (startup.TryAcquireSingleInstance(instanceName) is not null) { return Fail("第二次 TryAcquireSingleInstance 应为 null"); }
        if (!SmartUpdaterApp.ActivateExistingInstance()) { return Fail("ActivateExistingInstance 应返回 true"); }
    }

    // 7. 客户端主循环入口（立即取消）
    using var client = new UpdateClient(new UpdateClientOptions { Feed = fileFeed, Downloader = new HttpPackageDownloader(http), AppId = "AotSmoke-00000000", EnableFileLogging = false });
    client.UpdateAvailable += (_, _) => { };
    client.ProgressChanged += (_, _) => { };
    client.Restarting += (_, _) => { };
    client.Failed += (_, _) => { };
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    try
    {
        await client.RunAsync(cts.Token);
        return Fail("RunAsync 在已取消的 token 上应抛 OperationCanceledException");
    }
    catch (OperationCanceledException)
    {
    }
}
finally
{
    try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
    try { Directory.Delete(Path.Combine(AppContext.BaseDirectory, ".smartupdater"), recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
}

Console.WriteLine("AOT-SMOKE OK");
return 0;
