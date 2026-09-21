namespace SimpleOneX.SmartUpdater;

/// <summary>启动恢复对磁盘做了什么（上报字段）。</summary>
internal enum RecoveryAction
{
    /// <summary>没有未完成的事务，正常启动。</summary>
    None,

    /// <summary>事务停在 preparing：丢弃全部 .sunew，等同于更新未发生。</summary>
    DiscardedPreparing,

    /// <summary>已回滚到旧版本（committing 分支、损坏的 journal、或手工回滚入口）。</summary>
    RolledBack,

    /// <summary>事务已 done：补齐 state、清理备份与缓存，本次启动就是"刚更新完"。</summary>
    CompletedUpdate,
}

/// <summary>一次启动恢复的结果。<see cref="Errors"/> 非空表示有修复动作没做成，由调用方决定是否上报。</summary>
/// <param name="Action">对磁盘做了什么。</param>
/// <param name="FromVersion">journal 记录的更新前版本；未知时 null。</param>
/// <param name="ToVersion">journal 记录的目标版本；未知时 null。</param>
/// <param name="Errors">恢复过程中收集到的问题描述；空表示全部修复动作都成功。</param>
internal sealed record RecoveryResult(RecoveryAction Action, Version? FromVersion, Version? ToVersion, IReadOnlyList<string> Errors)
{
    /// <summary>"什么都没发生"的结果。</summary>
    public static RecoveryResult None { get; } = new(RecoveryAction.None, null, null, []);

    /// <summary>本次启动是否紧接在一次成功的更新之后（调用方据此构造 StartupResult 并上报 Updated）。</summary>
    public bool JustUpdated => Action == RecoveryAction.CompletedUpdate;
}

/// <summary>崩溃恢复表。幂等、可重入、永不抛出；journal 永远最后删。</summary>
internal sealed class UpdateRecovery
{
    private readonly UpdateLayout _layout;
    private readonly IFileOperations _fs;
    private readonly IUpdateLog _log;
    private readonly JournalStore _journal;
    private readonly UpdateStateStore _state;
    private readonly FileSwapper _swapper;

    public UpdateRecovery(UpdateLayout layout, IFileOperations fs, IUpdateLog log)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(log);

        _layout = layout;
        _fs = fs;
        _log = log;
        _journal = new JournalStore(layout.JournalFile, fs, log);
        _state = new UpdateStateStore(layout.StateFile, fs, log);
        _swapper = new FileSwapper(fs, log);
    }

    /// <summary>
    /// 按 journal 的状态执行恢复表。幂等、可重入、永不抛出（问题进 <see cref="RecoveryResult.Errors"/>）。
    /// 每个分支都把删 journal 放在最后：恢复过程中再崩溃，下次启动仍进入同一分支重做。
    /// </summary>
    public RecoveryResult Run()
    {
        try
        {
            return RunCore();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 兜底：各分支的已知失败点都已单独处理，这里只接住没预见到的 IO / 权限 / 路径类异常。
            // 没做成的事留给下次启动（恢复本身幂等），绝不能让程序因为恢复失败而起不来。
            _log.Error(UpdateStage.Rollback, "启动恢复意外失败，已放弃本次恢复", ex);
            return new RecoveryResult(RecoveryAction.None, null, null, [Describe(ex)]);
        }
    }

    private RecoveryResult RunCore()
    {
        JournalReadResult read;
        try
        {
            read = _journal.Read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不到不等于损坏（可能被杀软暂时占住）：不知道事务状态就不动磁盘，journal 原样留给下次启动。
            _log.Error(UpdateStage.Rollback, "无法读取 journal，本次跳过恢复", ex);
            return new RecoveryResult(RecoveryAction.None, null, null, [Describe(ex)]);
        }

        if (read.Status == JournalStatus.Missing)
        {
            // 视为正常启动：不扫描 *.suold，散落的备份留给下一次 done / preparing 清理。
            return RecoveryResult.None;
        }

        if (read.Status == JournalStatus.Corrupt)
        {
            return RestoreEverything("journal 损坏，无法得知事务状态；已按 *.suold 做通用回滚", "启动时发现 journal 但无法解析，按 *.suold 做通用回滚");
        }

        UpdateJournal journal = read.Journal!;
        _log.Information(UpdateStage.Rollback, $"启动时发现 journal：{Literal(journal.State)}（{journal.FromVersion?.ToString() ?? "(首次安装)"} → {journal.ToVersion}）");

        return journal.State switch
        {
            JournalState.Preparing => DiscardPreparing(journal),
            JournalState.Committing => RollBackCommitting(journal),
            JournalState.Done => CompleteDone(journal),

            // 越界的状态值（被篡改成数字、或将来新增的状态）：契约是永不抛出，按损坏处理走通用回滚。
            _ => RestoreEverything(
                $"journal 的状态 {(int)journal.State} 无法识别；已按 *.suold 做通用回滚",
                $"启动时发现无法识别的 journal 状态 {(int)journal.State}，按 *.suold 做通用回滚"),
        };
    }

    /// <summary>
    /// 通用回滚：所有 <c>*.suold</c> 改回原名，再删掉全部 <c>*.sunew</c>。不看也不碰 journal
    /// （是否删 journal 由调用方决定）。供 <c>--smartupdater-rollback</c> 与损坏 journal 使用。
    /// </summary>
    public RecoveryResult RollbackFromBackups()
    {
        List<string> errors = RestoreBackups();
        errors.AddRange(DeleteAll(SwapFileNames.NewSearchPattern));
        return new RecoveryResult(RecoveryAction.RolledBack, null, null, errors);
    }

    /// <summary>损坏 / 无法识别的 journal：记 Error → 通用回滚 → 删 journal。版本未知，所以 from / to 都是 null。</summary>
    private RecoveryResult RestoreEverything(string error, string logMessage)
    {
        _log.Error(UpdateStage.Rollback, logMessage);
        var errors = new List<string> { error };
        List<string> restoreFailures = RestoreBackups();
        errors.AddRange(restoreFailures);
        errors.AddRange(DeleteAll(SwapFileNames.NewSearchPattern));

        if (restoreFailures.Count == 0)
        {
            TryDeleteJournal(errors);
        }
        else
        {
            // 有 *.suold 没能还原：它可能是唯一一份旧版本。留着 journal，下次启动仍进入本分支重试；
            // 删了的话下次是 Missing 分支，不会再回滚。与 committing 分支一致。
            _log.Warning(UpdateStage.Rollback, $"通用回滚有 {restoreFailures.Count} 处失败，保留 journal 待下次启动重试");
        }

        return new RecoveryResult(RecoveryAction.RolledBack, null, null, errors);
    }

    private RecoveryResult DiscardPreparing(UpdateJournal journal)
    {
        // preparing 表示一个现存文件都没动过：.sunew 是不是完整的无关紧要，一律删掉。
        List<string> errors = DeleteAll(SwapFileNames.NewSearchPattern);
        TryDeleteJournal(errors);
        _log.Information(UpdateStage.Rollback, "preparing 阶段的残留已丢弃，等同于更新未发生");
        return new RecoveryResult(RecoveryAction.DiscardedPreparing, journal.FromVersion, journal.ToVersion, errors);
    }

    private RecoveryResult RollBackCommitting(UpdateJournal journal)
    {
        var errors = new List<string>();
        List<SwapOperation> operations = BuildOperations(journal, errors);

        List<string> failures = [.. _swapper.Rollback(operations).Select(Describe)];
        errors.AddRange(failures);

        // 删 .sunew 的失败不算回滚失败：新版本 exe 若正在运行、已被改名为 .sunew，删不掉是正常的。
        errors.AddRange(DeleteAll(SwapFileNames.NewSearchPattern));

        if (failures.Count == 0)
        {
            TryDeleteJournal(errors);
            _log.Information(UpdateStage.Rollback, $"已回滚到 {journal.FromVersion?.ToString() ?? "(空安装)"}");
        }
        else
        {
            // 保留 journal：下次启动重试；PackageApplier 在 journal 存在时拒绝开始新更新。
            _log.Warning(UpdateStage.Rollback, $"回滚有 {failures.Count} 处失败，保留 journal 待下次启动重试");
        }

        return new RecoveryResult(RecoveryAction.RolledBack, journal.FromVersion, journal.ToVersion, errors);
    }

    private RecoveryResult CompleteDone(UpdateJournal journal)
    {
        var errors = new List<string>();
        FixInstalledVersion(journal.ToVersion, errors);
        errors.AddRange(DeleteAll(SwapFileNames.OldSearchPattern));
        errors.AddRange(DeleteAll(SwapFileNames.NewSearchPattern));
        errors.AddRange(ClearDownloadCache());

        // 无论前面是否全部成功都删 journal：JustUpdated 只能出现一次，残留由下一次 done / preparing 兜底。
        TryDeleteJournal(errors);
        _log.Information(UpdateStage.Rollback, $"更新已完成：{journal.FromVersion?.ToString() ?? "(首次安装)"} → {journal.ToVersion}，备份与缓存已清理");
        return new RecoveryResult(RecoveryAction.CompletedUpdate, journal.FromVersion, journal.ToVersion, errors);
    }

    /// <summary>由 journal 还原提交序列：deletes 在前、writes 在后，与 <see cref="PackageApplier"/> 的提交顺序一致。</summary>
    private List<SwapOperation> BuildOperations(UpdateJournal journal, List<string> errors)
    {
        var operations = new List<SwapOperation>();

        foreach (string path in journal.Deletes)
        {
            TryAdd(SwapKind.Delete, path, hadTarget: true);
        }

        foreach (JournalEntry entry in journal.Writes)
        {
            TryAdd(SwapKind.Write, entry.Path, entry.HadTarget);
        }

        return operations;

        void TryAdd(SwapKind kind, string relativePath, bool hadTarget)
        {
            try
            {
                operations.Add(new SwapOperation(kind, _layout.ResolveInstallPath(relativePath), hadTarget));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                // 被篡改成逃逸路径或超长路径（PathTooLongException 是 IOException）：跳过这一条，其余照常回滚。
                _log.Error(UpdateStage.Rollback, $"journal 里的路径不合法，跳过：{relativePath}", ex);
                errors.Add($"journal 路径不合法：{relativePath}（{ex.Message}）");
            }
        }
    }

    /// <summary>对每个 <c>X.suold</c>：占位的 <c>X</c> 先改名挪开（可能正在运行，能改名不能删），再把备份移回原名。</summary>
    private List<string> RestoreBackups()
    {
        var errors = new List<string>();

        foreach (string backup in Enumerate(_layout.InstallDirectory, SwapFileNames.OldSearchPattern, recursive: true, errors))
        {
            string target = backup[..^SwapFileNames.OldSuffix.Length];
            try
            {
                if (_fs.FileExists(target))
                {
                    string pending = SwapFileNames.NewPath(target);
                    if (_fs.FileExists(pending))
                    {
                        _fs.Delete(pending);
                    }

                    _fs.Move(target, pending, overwrite: false);   // 占位的新文件可能正在运行：改名而不删
                }

                _fs.Move(backup, target, overwrite: false);
                _log.Information(UpdateStage.Rollback, $"恢复 {target}（从 .suold 移回）");
            }
            catch (Exception ex)
            {
                _log.Error(UpdateStage.Rollback, $"恢复 {target} 失败", ex);
                errors.Add(Describe(ex));
            }
        }

        return errors;
    }

    private List<string> DeleteAll(string searchPattern)
    {
        var errors = new List<string>();

        foreach (string file in Enumerate(_layout.InstallDirectory, searchPattern, recursive: true, errors))
        {
            try
            {
                _fs.Delete(file);
                _log.Debug(UpdateStage.Rollback, $"已删除 {file}");
            }
            catch (Exception ex)
            {
                _log.Error(UpdateStage.Rollback, $"删除 {file} 失败", ex);
                errors.Add($"{Describe(ex)}：{file}");
            }
        }

        return errors;
    }

    /// <summary>清空下载缓存目录里的文件：不递归、不删目录本身。</summary>
    private List<string> ClearDownloadCache()
    {
        var errors = new List<string>();

        if (!_fs.DirectoryExists(_layout.DownloadCacheDirectory))
        {
            return errors;
        }

        foreach (string file in Enumerate(_layout.DownloadCacheDirectory, "*", recursive: false, errors))
        {
            try
            {
                _fs.Delete(file);
            }
            catch (Exception ex)
            {
                _log.Error(UpdateStage.Rollback, $"删除下载缓存 {file} 失败", ex);
                errors.Add($"{Describe(ex)}：{file}");
            }
        }

        return errors;
    }

    /// <summary>枚举失败（无权、路径过长、IO 故障）记成一条 Errors 并当作没有匹配项，不让一次枚举打断整次恢复。</summary>
    private IReadOnlyList<string> Enumerate(string directory, string searchPattern, bool recursive, List<string> errors)
    {
        try
        {
            return _fs.EnumerateFiles(directory, searchPattern, recursive);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Error(UpdateStage.Rollback, $"枚举 {directory}（{searchPattern}）失败", ex);
            errors.Add($"{Describe(ex)}：{directory}");
            return [];
        }
    }

    /// <summary>done 之后补 state：<see cref="PackageApplier"/> 写完 journal(done) 才写 state，崩在这中间就由这里补上。</summary>
    private void FixInstalledVersion(Version toVersion, List<string> errors)
    {
        try
        {
            UpdateState state = _state.Load();
            if (!toVersion.Equals(state.CurrentVersion))
            {
                state.CurrentVersion = toVersion;
                _state.Save(state);
                _log.Information(UpdateStage.Rollback, $"state.json 的当前版本已补成 {toVersion}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(UpdateStage.Rollback, "更新 state.json 失败", ex);
            errors.Add(Describe(ex));
        }
    }

    private void TryDeleteJournal(List<string> errors)
    {
        try
        {
            _journal.Delete();
        }
        catch (Exception ex)
        {
            _log.Error(UpdateStage.Rollback, "删除 journal 失败", ex);
            errors.Add(Describe(ex));
        }
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    private static string Literal(JournalState state) => state switch
    {
        JournalState.Preparing => "preparing",
        JournalState.Committing => "committing",
        JournalState.Done => "done",
        _ => state.ToString(),
    };
}
