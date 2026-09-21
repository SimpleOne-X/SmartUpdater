namespace SimpleOneX.SmartUpdater;

/// <summary>挑选版本时需要的本机状态。</summary>
/// <param name="CurrentVersion">当前已安装的版本。</param>
/// <param name="DeviceGuid">设备唯一标识，用于灰度判定。</param>
/// <param name="SkippedVersions">使用者已明确跳过的版本。</param>
/// <param name="AllowVersionDowngrade">是否允许降级到更低版本。</param>
internal readonly record struct SelectionContext(
    Version CurrentVersion,
    Guid DeviceGuid,
    IReadOnlySet<Version> SkippedVersions,
    bool AllowVersionDowngrade);

/// <summary>单个条目的判定结论。</summary>
internal enum SelectionVerdict
{
    /// <summary>条件 2~4 全部满足。</summary>
    Reachable,

    /// <summary>当前版本低于该条目的最低可升级门槛（条件 2）。</summary>
    BelowFloor,

    /// <summary>该版本在使用者的跳过集合里（条件 3）。</summary>
    Skipped,

    /// <summary>未命中灰度批次（条件 4）。</summary>
    OutOfRollout,
}

/// <summary>单个条目的判定追踪。</summary>
/// <param name="Entry">被判定的条目。</param>
/// <param name="Verdict">判定结论。</param>
/// <param name="Bucket">该设备与该版本的灰度哈希桶。</param>
/// <param name="RolloutPercent">条目上的灰度百分比原值（不钳制）。</param>
/// <param name="Floor">仅在 <see cref="SelectionVerdict.BelowFloor"/> 时非 null：最低可升级门槛。</param>
internal readonly record struct SelectionTrace(ReleaseEntry Entry, SelectionVerdict Verdict, int Bucket, int RolloutPercent, Version? Floor);

/// <summary>一次挑选的整体结论。</summary>
internal enum SelectionOutcome
{
    /// <summary>选出了一个条目。</summary>
    Selected,

    /// <summary>没有条目满足条件 2~4（含空 feed）。</summary>
    NoReachableEntry,

    /// <summary>最高可达条目就是当前版本。</summary>
    AlreadyCurrent,

    /// <summary>最高可达条目低于当前版本，且不允许降级。</summary>
    DowngradeNotAllowed,
}

/// <summary>挑选结果，附带每个条目的判定。</summary>
/// <param name="Selected">选中的条目；没有则为 null。</param>
/// <param name="Outcome">整体结论。</param>
/// <param name="HighestReachable">按版本降序的第一个可达条目。</param>
/// <param name="Traces">按版本降序、覆盖全部条目的判定。</param>
internal sealed record SelectionResult(ReleaseEntry? Selected, SelectionOutcome Outcome, ReleaseEntry? HighestReachable, IReadOnlyList<SelectionTrace> Traces);

/// <summary>按四个条件挑选下一个要安装的版本。</summary>
internal static class ReleaseSelector
{
    /// <summary>
    /// 连续跳板的次数上限。超过即放弃本轮并上报。
    /// 没有这个上限，feed 配置成环（A 要求先装 B，B 又要求先装 A）会让客户端无限重启升级。
    /// </summary>
    public const int MaxChainLength = 5;

    /// <summary>挑出下一个要安装的条目；没有可安装的则返回 null。</summary>
    public static ReleaseEntry? Select(ReleaseFeedDocument feed, SelectionContext context)
        => Explain(feed, context).Selected;

    /// <summary>与 <see cref="Select"/> 同一套规则，但返回每个条目的判定（按版本降序，覆盖全部条目）与整体结论。</summary>
    public static SelectionResult Explain(ReleaseFeedDocument feed, SelectionContext context)
    {
        ArgumentNullException.ThrowIfNull(feed);

        // 不信任服务端的排序，自己按版本降序。先按条件 2~4（门槛 / 未跳过 / 灰度）取"最高的可达条目"；
        // 够不着最高版时自然落到较老条目，这就是阶梯升级：较老的那条成为跳板。
        List<SelectionTrace> traces = feed.Releases
            .OrderByDescending(r => r.Version)
            .Select(r => Judge(r, context))
            .ToList();

        ReleaseEntry? target = traces
            .Where(t => t.Verdict == SelectionVerdict.Reachable)
            .Select(t => (ReleaseEntry?)t.Entry)
            .FirstOrDefault();

        if (target is null)
        {
            return new SelectionResult(null, SelectionOutcome.NoReachableEntry, null, traces);
        }

        // 条件 1 只对这一个条目判断，而不是当作逐条过滤：逐条过滤在允许降级时会让
        // "已经在最高可达版本上"的客户端被推到次高版本，下一轮又升回来，永远来回横跳。
        int comparison = target.Version.CompareTo(context.CurrentVersion);
        if (comparison > 0)
        {
            return new SelectionResult(target, SelectionOutcome.Selected, target, traces);
        }

        if (comparison == 0)
        {
            return new SelectionResult(null, SelectionOutcome.AlreadyCurrent, target, traces);
        }

        return context.AllowVersionDowngrade
            ? new SelectionResult(target, SelectionOutcome.Selected, target, traces)
            : new SelectionResult(null, SelectionOutcome.DowngradeNotAllowed, target, traces);
    }

    private static SelectionTrace Judge(ReleaseEntry entry, SelectionContext context)
    {
        int bucket = RolloutGate.BucketOf(context.DeviceGuid, entry.Version);

        // 2. 当前版本必须达到该条目的最低可升级门槛
        if (entry.MinUpdatableFrom is { } floor && context.CurrentVersion < floor)
        {
            return new SelectionTrace(entry, SelectionVerdict.BelowFloor, bucket, entry.RolloutPercent, floor);
        }

        // 3. 未被使用者跳过
        if (context.SkippedVersions.Contains(entry.Version))
        {
            return new SelectionTrace(entry, SelectionVerdict.Skipped, bucket, entry.RolloutPercent, null);
        }

        // 4. 命中灰度批次
        SelectionVerdict verdict = RolloutGate.IsIncluded(context.DeviceGuid, entry.Version, entry.RolloutPercent)
            ? SelectionVerdict.Reachable
            : SelectionVerdict.OutOfRollout;
        return new SelectionTrace(entry, verdict, bucket, entry.RolloutPercent, null);
    }
}
