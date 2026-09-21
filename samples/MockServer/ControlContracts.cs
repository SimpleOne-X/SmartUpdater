namespace SimpleOneX.SmartUpdater.MockServer;

// 控制面请求 / 响应的 record 集中放在这个文件。
// 上报端点用 JsonNode 原样接收任意 JSON，不需要任何契约类型。

/// <summary>对某个精确路径注入一个 HTTP 故障。</summary>
/// <param name="Path">请求路径，以 / 开头；不以 / 开头时由服务器补上。</param>
/// <param name="Status">要返回的状态码，400..599。</param>
/// <param name="RetryAfterSeconds">非 null 时写 Retry-After 响应头。</param>
/// <param name="FailCount">null = 一直失败；n = 前 n 次失败，第 n+1 次起放行。</param>
internal sealed record FaultRequest(string Path, int Status, int? RetryAfterSeconds, int? FailCount);

/// <summary>
/// 对 <c>/releases.json</c> 做一次内存叠加。两种模式互斥：给了 <paramref name="Body"/> 就逐字当作 feed；
/// 否则读磁盘上的 feed，按其余字段改动后叠加。全部字段缺省（null）表示"不改这一项"。
/// </summary>
/// <param name="Body">逐字下发的 feed 文本（可以是畸形 JSON）；与其余字段互斥。</param>
/// <param name="Version">选中要改的 release 条目（<c>version</c> 精确匹配）；null = 改全部条目。</param>
/// <param name="RolloutPercent">改选中条目的 <c>rolloutPercent</c>。</param>
/// <param name="Mode">改选中条目的 <c>mode</c>。</param>
/// <param name="PackageSha256">改选中条目的 <c>package.sha256</c>，用来构造"哈希故意写错"。</param>
/// <param name="SchemaVersion">改顶层 <c>schemaVersion</c>，用来构造"未知的大版本"。</param>
/// <param name="Client">覆盖 / 新建 <c>client</c> 段的三个值。</param>
internal sealed record FeedOverlayRequest(
    string? Body,
    string? Version,
    int? RolloutPercent,
    string? Mode,
    string? PackageSha256,
    int? SchemaVersion,
    ClientPolicyPatch? Client);

/// <summary>feed 里 <c>client</c> 段的三个可调值；null 表示保持原值。</summary>
internal sealed record ClientPolicyPatch(int? PollIntervalSeconds, int? JitterWindowSeconds, int? HeartbeatIntervalSeconds);

/// <summary>
/// 对某个精确路径的响应体做限速与 / 或中途断流。两个开关共用一条设置，因为断点续传的场景要配合使用：
/// 先限速让下载慢下来，再在中途切断，客户端才有机会用 Range 续传。
/// </summary>
/// <param name="Path">请求路径，以 / 开头；不以 / 开头时由服务器补上。</param>
/// <param name="BytesPerSecond">null 或 0 = 不限速；&gt; 0 = 每秒放行这么多字节（按 4096 字节一块节流）。</param>
/// <param name="CutAfterBytes">null = 不切断；n（&gt;= 1）= 响应体写满 n 字节后停止写入并中断连接。</param>
internal sealed record TransferRequest(string Path, int? BytesPerSecond, long? CutAfterBytes);
