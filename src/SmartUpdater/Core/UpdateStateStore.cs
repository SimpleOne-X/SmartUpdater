using System.Text.Json;

namespace SimpleOneX.SmartUpdater;

/// <summary>
/// <c>.smartupdater/state.json</c> 的读写。<see cref="Load"/> 永不抛出：状态文件出任何问题都不能让程序起不来
/// （安装目录只读时更新被禁用，但程序照常运行）。<see cref="Save"/> 的 IO 异常原样抛出，由调用方决定。
/// </summary>
internal sealed class UpdateStateStore
{
    /// <summary>损坏的状态文件被改名保留证据时追加的后缀。</summary>
    public const string CorruptBackupSuffix = ".bad";

    private readonly string _stateFilePath;
    private readonly IFileOperations _fs;
    private readonly IUpdateLog _log;

    public UpdateStateStore(string stateFilePath, IFileOperations fs, IUpdateLog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFilePath);
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(log);

        _stateFilePath = stateFilePath;
        _fs = fs;
        _log = log;
    }

    /// <summary>
    /// 读取状态。永不抛出。
    /// 文件不存在 → 新建（含新 <see cref="UpdateState.DeviceGuid"/>）并保存；
    /// 文件损坏 → 改名为 <c>state.json.bad</c> 后按不存在处理；
    /// <see cref="UpdateState.DeviceGuid"/> 为空、跳过列表里有 null / 不高于当前版本 / 重复的条目 → 修好后保存；
    /// 健康的文件一次都不写；保存失败只记 Warning，返回内存中的状态。
    /// </summary>
    public UpdateState Load()
    {
        // 必须先判存在：ReadJson 对"文件不存在"与"内容是 null 字面量"都返回 null，
        // 而前者是新建、后者是损坏（要改名保留），两者处置不同。
        if (!_fs.FileExists(_stateFilePath))
        {
            _log.Information(null, $"未找到状态文件 {_stateFilePath}，创建新的状态。");
            return CreateAndPersist();
        }

        UpdateState? state;

        try
        {
            state = AtomicFile.ReadJson(_fs, _stateFilePath, SmartUpdaterJsonContext.Default.UpdateState);
        }
        catch (JsonException ex)
        {
            return MoveAsideAndRecreate(ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不到不等于损坏：文件可能只是被杀软或别的进程暂时占住。不保存，免得覆盖一份其实完好的状态。
            _log.Warning(null, $"无法读取状态文件 {_stateFilePath}，本次使用内存中的新状态，不覆盖原文件。", ex);
            return NewState();
        }

        if (state is null)
        {
            return MoveAsideAndRecreate(exception: null);
        }

        if (Repair(state))
        {
            TrySave(state);
        }

        return state;
    }

    /// <summary>原子写。目录不存在先创建；IO 异常原样抛出。</summary>
    public void Save(UpdateState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        // 条件化创建：目录已在时不多做一次写操作，保存恰好是 AtomicFile 的四步。
        string? directory = Path.GetDirectoryName(_stateFilePath);
        if (!string.IsNullOrEmpty(directory) && !_fs.DirectoryExists(directory))
        {
            _fs.CreateDirectory(directory);
        }

        AtomicFile.WriteJson(_fs, _stateFilePath, state, SmartUpdaterJsonContext.Default.UpdateState);
    }

    private static UpdateState NewState() => new() { DeviceGuid = Guid.NewGuid() };

    private UpdateState CreateAndPersist()
    {
        UpdateState state = NewState();
        TrySave(state);
        return state;
    }

    private UpdateState MoveAsideAndRecreate(Exception? exception)
    {
        string backup = _stateFilePath + CorruptBackupSuffix;

        try
        {
            _fs.Move(_stateFilePath, backup, overwrite: true);
            _log.Warning(null, $"状态文件 {_stateFilePath} 已损坏，已改名为 {backup} 保留，并重建。设备标识会随之变化。", exception);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 改不了名就放弃保留证据：原文件会被新状态覆盖（保存也失败则由 TrySave 记录）。
            _log.Warning(null, $"状态文件 {_stateFilePath} 已损坏，且无法改名为 {backup}；将直接重建。", ex);
        }

        return CreateAndPersist();
    }

    /// <summary>补齐空的 DeviceGuid、修整跳过列表。返回是否改动过（改动过才需要回写）。</summary>
    private static bool Repair(UpdateState state)
    {
        bool changed = false;

        if (state.DeviceGuid == Guid.Empty)
        {
            state.DeviceGuid = Guid.NewGuid();
            changed = true;
        }

        // "skippedVersions": null 反序列化后是 null（编译期看不出来）。
        List<Version> original = state.SkippedVersions ?? [];

        // 跳过 = "直到有更新版本才再问"，所以不高于当前版本的条目没有意义；
        // 手改坏的文件可能带 null 元素（"skippedVersions":[null]），一并丢弃。
        List<Version> pruned = [.. original
            .Where(v => v is not null && (state.CurrentVersion is null || v > state.CurrentVersion))
            .Distinct()];

        if (state.SkippedVersions is null || pruned.Count != original.Count)
        {
            state.SkippedVersions = pruned;
            changed = true;
        }

        return changed;
    }

    private void TrySave(UpdateState state)
    {
        try
        {
            Save(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning(null, $"无法保存状态文件 {_stateFilePath}，本次使用内存中的状态（更新功能可能受限）。", ex);
        }
    }
}

/// <summary>对 <see cref="UpdateState"/> 跳过版本集合的操作。</summary>
internal static class UpdateStateExtensions
{
    /// <summary>
    /// 把 <paramref name="version"/> 加入跳过列表。等于当前版本或已存在 → false 且不改动。
    /// "跳过当前版本"只表示不再提示，不能进入 <see cref="ReleaseSelector"/> 消费的集合，否则允许降级时会挑到次低版本。
    /// </summary>
    public static bool TrySkipVersion(this UpdateState state, Version version)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(version);

        if (version.Equals(state.CurrentVersion) || state.SkippedVersions.Contains(version))
        {
            return false;
        }

        state.SkippedVersions.Add(version);
        return true;
    }

    /// <summary>供 <see cref="ReleaseSelector"/> 消费的跳过集合：排除当前版本（即便文件里有）与 null 元素。</summary>
    public static IReadOnlySet<Version> GetSkippedVersionsForSelection(this UpdateState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new HashSet<Version>(state.SkippedVersions.Where(v => v is not null && !v.Equals(state.CurrentVersion)));
    }
}
