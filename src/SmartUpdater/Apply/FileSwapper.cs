namespace SimpleOneX.SmartUpdater;

/// <summary>提交时对目标文件做的事。</summary>
internal enum SwapKind
{
    /// <summary>删除目标（实际是改名为 <c>.suold</c>）。</summary>
    Delete,

    /// <summary>用 <c>.sunew</c> 替换目标，或在目标不存在时新增。</summary>
    Write,
}

/// <summary>一个待提交的文件操作。</summary>
/// <param name="Kind">操作类型。</param>
/// <param name="TargetPath">目标文件的完整路径。</param>
/// <param name="HadTarget">preparing 时 <c>FileExists(TargetPath)</c> 的结果；<see cref="SwapKind.Delete"/> 操作填 true，无意义。</param>
internal sealed record SwapOperation(SwapKind Kind, string TargetPath, bool HadTarget);

/// <summary>提交中途失败。已尝试逆序回滚；<see cref="RollbackFailures"/> 为空表示磁盘已回到提交前的状态。</summary>
internal sealed class SwapFailedException : Exception
{
    public SwapFailedException(string message, Exception commitFailure, IReadOnlyList<Exception> rollbackFailures, int failedOperationIndex)
        : base(message, commitFailure)
    {
        CommitFailure = commitFailure;
        RollbackFailures = rollbackFailures;
        FailedOperationIndex = failedOperationIndex;
    }

    /// <summary>让提交中断的那个异常（也是 <see cref="Exception.InnerException"/>）。</summary>
    public Exception CommitFailure { get; }

    /// <summary>回滚过程中收集到的失败；空表示已干净回滚。</summary>
    public IReadOnlyList<Exception> RollbackFailures { get; }

    /// <summary>出错的操作序号，从 1 开始。</summary>
    public int FailedOperationIndex { get; }

    /// <summary>是否已干净回滚。</summary>
    public bool IsRolledBack => RollbackFailures.Count == 0;
}

/// <summary>按序改名提交；失败逆序回滚；.suold 由改名产生，零拷贝。</summary>
internal sealed class FileSwapper
{
    private readonly IFileOperations _fs;
    private readonly IUpdateLog _log;

    public FileSwapper(IFileOperations fs, IUpdateLog log)
    {
        _fs = fs;
        _log = log;
    }

    /// <summary>
    /// 按 <paramref name="operations"/> 的顺序提交。开始前先预检全部 <c>.sunew</c>，缺失即 <see cref="InvalidOperationException"/> 且不触碰任何文件。
    /// 前提：<paramref name="operations"/> 里不能有重复的 TargetPath；清单路径不得带 .sunew / .suold 后缀（<see cref="ManifestPathValidator"/> 已保证）。
    /// 中途失败：对已开始的操作逆序回滚，然后抛 <see cref="SwapFailedException"/>。
    /// </summary>
    public void Commit(IReadOnlyList<SwapOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        // 预检：任何一个 .sunew 缺失就不开始，不触碰任何文件
        foreach (SwapOperation op in operations)
        {
            if (op.Kind == SwapKind.Write && !_fs.FileExists(SwapFileNames.NewPath(op.TargetPath)))
            {
                throw new InvalidOperationException($"待提交文件不存在：{SwapFileNames.NewPath(op.TargetPath)}");
            }
        }

        var started = new List<SwapOperation>(operations.Count);

        try
        {
            foreach (SwapOperation op in operations)
            {
                started.Add(op);
                Apply(op);
            }
        }
        catch (Exception ex)
        {
            _log.Error(UpdateStage.Commit, $"提交在第 {started.Count} 个操作失败：{started[^1].Kind} {started[^1].TargetPath}", ex);
            IReadOnlyList<Exception> rollbackFailures = Rollback(started);
            throw new SwapFailedException(
                rollbackFailures.Count == 0 ? "提交失败，已回滚。" : $"提交失败，且回滚有 {rollbackFailures.Count} 处失败。",
                ex,
                rollbackFailures,
                started.Count);
        }
    }

    /// <summary>
    /// 状态化、逆序、逐项 try/catch 的回滚，永不抛出；返回收集到的失败（空 = 全部成功）。
    /// 对未开始、半途、已完成的操作都正确且幂等，因此启动恢复可以反复调用。
    /// 前提：新一轮更新开始前，调用方必须先清掉上一轮残留的 *.suold，否则陈旧备份会被当成本轮的回滚源。
    /// </summary>
    public IReadOnlyList<Exception> Rollback(IReadOnlyList<SwapOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var failures = new List<Exception>();

        for (int i = operations.Count - 1; i >= 0; i--)
        {
            SwapOperation op = operations[i];
            try
            {
                Undo(op);
            }
            catch (Exception ex)
            {
                _log.Error(UpdateStage.Rollback, $"回滚失败：{op.Kind} {op.TargetPath}", ex);
                failures.Add(ex);
            }
        }

        return failures;
    }

    /// <summary>删除每个 <see cref="SwapKind.Write"/> 操作的 <c>.sunew</c>，逐项收集失败。回滚之后调用；提交成功后 <c>.sunew</c> 已被改名走，不必调用。</summary>
    public IReadOnlyList<Exception> DeletePendingFiles(IReadOnlyList<SwapOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var failures = new List<Exception>();

        foreach (SwapOperation op in operations)
        {
            if (op.Kind != SwapKind.Write)
            {
                continue;
            }

            try
            {
                _fs.Delete(SwapFileNames.NewPath(op.TargetPath));
            }
            catch (Exception ex)
            {
                _log.Error(UpdateStage.Rollback, $"删除待提交文件失败：{SwapFileNames.NewPath(op.TargetPath)}", ex);
                failures.Add(ex);
            }
        }

        return failures;
    }

    private void Apply(SwapOperation op)
    {
        string target = op.TargetPath;
        string old = SwapFileNames.OldPath(target);

        if (op.Kind == SwapKind.Delete)
        {
            if (!_fs.FileExists(target))
            {
                _log.Information(UpdateStage.Commit, $"delete {target}：目标不存在，跳过");
                return;
            }

            RemoveStale(old);
            _fs.Move(target, old, overwrite: false);
            _log.Information(UpdateStage.Commit, $"delete {target}（已改名为 .suold）");
            return;
        }

        bool backedUp = false;
        if (_fs.FileExists(target))
        {
            RemoveStale(old);
            _fs.Move(target, old, overwrite: false);
            backedUp = true;
        }

        _fs.Move(SwapFileNames.NewPath(target), target, overwrite: false);
        _log.Information(UpdateStage.Commit, backedUp ? $"write {target}（旧文件已改名为 .suold）" : $"write {target}（新文件）");
    }

    private void Undo(SwapOperation op)
    {
        string target = op.TargetPath;
        string old = SwapFileNames.OldPath(target);

        if (op.Kind == SwapKind.Delete)
        {
            if (_fs.FileExists(old) && !_fs.FileExists(target))
            {
                _fs.Move(old, target, overwrite: false);
                _log.Information(UpdateStage.Rollback, $"恢复 {target}（从 .suold 移回）");
            }

            return;
        }

        if (op.HadTarget)
        {
            if (!_fs.FileExists(old))
            {
                return;   // 未开始，或外部已改动：不猜
            }

            // 已完成的 Write 一定已把 .sunew 改名成 target；所以"target 在 + .sunew 也在"只可能是这一步还没开始，
            // 此时 .suold 是上一轮更新遗留的陈旧备份（.suold 要保留到新版本成功启动后才删），target 才是当前版本。
            // 若照常处理，会把陈旧备份盖回 target，回滚后的 DeletePendingFiles 再把被挤到 .sunew 的当前版本删掉。
            if (_fs.FileExists(target) && _fs.FileExists(SwapFileNames.NewPath(target)))
            {
                _log.Warning(UpdateStage.Rollback, $"{target} 的写入尚未开始，{old} 是遗留的陈旧备份，不动");
                return;
            }

            if (_fs.FileExists(target))
            {
                MoveAway(target);
            }

            _fs.Move(old, target, overwrite: false);
            _log.Information(UpdateStage.Rollback, $"恢复 {target}（从 .suold 移回）");
            return;
        }

        if (_fs.FileExists(target))
        {
            MoveAway(target);
            _log.Information(UpdateStage.Rollback, $"撤销新增 {target}（改名为 .sunew）");
        }
    }

    // 用改名而不是删除：新版本 exe 可能正在运行（恢复由新进程执行时），运行中的文件能改名不能删。
    private void MoveAway(string path)
    {
        string pending = SwapFileNames.NewPath(path);
        if (_fs.FileExists(pending))
        {
            _log.Warning(UpdateStage.Rollback, $"发现残留的待提交文件，先删除：{pending}");
            _fs.Delete(pending);
        }

        _fs.Move(path, pending, overwrite: false);
    }

    private void RemoveStale(string old)
    {
        if (_fs.FileExists(old))
        {
            _log.Warning(UpdateStage.Commit, $"发现陈旧备份，先删除：{old}");
            _fs.Delete(old);
        }
    }
}
