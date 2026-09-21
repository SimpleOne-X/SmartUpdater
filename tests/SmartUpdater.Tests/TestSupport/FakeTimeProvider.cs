namespace SmartUpdater.Tests;

/// <summary>可拨动的时钟。本地时区固定为 UTC+8，测试结果不随机器时区变化。</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    public static readonly TimeZoneInfo EastEight =
        TimeZoneInfo.CreateCustomTimeZone("UTC+08", TimeSpan.FromHours(8), "UTC+08", "UTC+08");

    private readonly TimeZoneInfo _zone;

    public FakeTimeProvider(DateTimeOffset utcNow, TimeZoneInfo? zone = null)
    {
        UtcNow = utcNow;
        _zone = zone ?? EastEight;
    }

    public DateTimeOffset UtcNow { get; set; }

    public void Advance(TimeSpan by) => UtcNow += by;

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public override TimeZoneInfo LocalTimeZone => _zone;
}
