using System.Security.Cryptography;
using System.Text.Json;

namespace SimpleOneX.SmartUpdater;

/// <summary>一次应用升级包的请求。</summary>
/// <param name="PackagePath">已下载并通过包级校验的 zip 的完整路径。</param>
/// <param name="CurrentVersion">当前已安装版本；未知（首次安装）为 null，此时退回本地清单里的版本。</param>
/// <param name="MainExecutableRelativePath">主程序 exe 相对安装目录的路径（正斜杠）；它排在提交序列的最后。</param>
internal sealed record ApplyRequest(string PackagePath, Version? CurrentVersion, string MainExecutableRelativePath);

/// <summary>应用成功的结果。</summary>
/// <param name="FromVersion">更新前版本；首次安装为 null。</param>
/// <param name="ToVersion">更新后版本。</param>
/// <param name="WrittenCount">写入（新增或替换）的文件数，不含 <c>.smartupdater/manifest.json</c>。</param>
/// <param name="DeletedCount">删除的文件数。</param>
/// <param name="SkippedCount">因 preserve 且目标已存在而跳过的文件数。</param>
internal sealed record ApplyResult(Version? FromVersion, Version ToVersion, int WrittenCount, int DeletedCount, int SkippedCount);

/// <summary>应用进度：按清单文件计数（不含清单本身），暂存与安装后校验各报一遍。</summary>
internal readonly record struct ApplyProgress(UpdateStage Stage, int CompletedFiles, int TotalFiles, long BytesProcessed, long TotalBytes);

/// <summary>应用升级包失败。<see cref="Stage"/> 直接用于失败事件与上报。</summary>
internal sealed class UpdateFailedException : Exception
{
    public UpdateFailedException(UpdateStage stage, string message, Exception? innerException = null, IReadOnlyList<Exception>? rollbackFailures = null)
        : base(message, innerException)
    {
        Stage = stage;
        RollbackFailures = rollbackFailures ?? [];
    }

    /// <summary>失败发生的阶段。<see cref="UpdateStage.Rollback"/> 表示回滚未能完成。</summary>
    public UpdateStage Stage { get; }

    /// <summary>回滚过程中收集到的失败。非空 ⇒ journal 已保留，等待启动恢复重试。</summary>
    public IReadOnlyList<Exception> RollbackFailures { get; }
}

/// <summary>准备、提交并做安装后校验。不删 .suold、不清缓存、不重启——那些属于启动恢复与调用方。</summary>
internal sealed class PackageApplier
{
    private const int CopyBufferSize = 81920;

    private readonly UpdateLayout _layout;
    private readonly IFileOperations _fs;
    private readonly IUpdateLog _log;
    private readonly TimeProvider _time;
    private readonly JournalStore _journal;
    private readonly UpdateStateStore _state;
    private readonly FileSwapper _swapper;

    public PackageApplier(UpdateLayout layout, IFileOperations fs, IUpdateLog log, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _layout = layout;
        _fs = fs;
        _log = log;
        _time = timeProvider;
        _journal = new JournalStore(layout.JournalFile, fs, log);
        _state = new UpdateStateStore(layout.StateFile, fs, log);
        _swapper = new FileSwapper(fs, log);
    }

    /// <summary>
    /// 同步执行（磁盘 IO，调用方用 Task.Run 包起来）。成功返回结果；失败抛 <see cref="UpdateFailedException"/>；
    /// 已有 journal 时抛 <see cref="InvalidOperationException"/>（必须先跑 UpdateRecovery）；
    /// 取消抛 <see cref="OperationCanceledException"/>（只在 preparing 阶段响应，进入 committing 之后不可取消）。
    /// </summary>
    public ApplyResult Apply(ApplyRequest request, IProgress<ApplyProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 0. 一次只允许一个事务；残留的 journal 必须先由 UpdateRecovery 处理（恢复要用到它记录的 .sunew / .suold，这里什么都不能动）
        if (_journal.Read().Status != JournalStatus.Missing)
        {
            throw new InvalidOperationException("存在未完成的更新事务（journal.json），必须先运行启动恢复。");
        }

        // 1. 打开包、读本地清单、算差集
        PackageContents package;
        try
        {
            package = PackageReader.Open(request.PackagePath);
        }
        catch (PackageFormatException ex)
        {
            _log.Error(UpdateStage.Verify, "升级包不合法", ex);
            throw new UpdateFailedException(UpdateStage.Verify, ex.Message, ex);
        }

        using (package)
        {
            RejectFileDirectoryCollisions(package.Manifest);

            PackageManifest? local = ReadLocalManifest();
            ManifestDiffResult diff = ManifestDiff.Compute(local, package.Manifest, relative => _fs.FileExists(_layout.ResolveInstallPath(relative)));
            LogDecisions(diff);

            Version toVersion = package.Manifest.Version;
            Version? fromVersion = request.CurrentVersion ?? local?.Version;

            // 上一次更新遗留的 .suold / 孤儿 .sunew：journal 已确认不存在，它们只可能是陈旧数据。
            // 必须在进入 preparing 之前清掉，否则回滚时"未开始的替换"与"已完成的替换"在磁盘上分不清。
            RemoveStaleSwapFiles();

            // 2. 排序（deletes → writes → manifest → 主 exe），记 hadTarget，写 journal preparing
            List<string> deletes = [.. diff.Deletes.OrderBy(p => p, StringComparer.Ordinal)];
            List<PlannedWrite> writes = PlanWrites(diff, request.MainExecutableRelativePath);
            var operations = new List<SwapOperation>(deletes.Count + writes.Count);
            operations.AddRange(deletes.Select(d => new SwapOperation(SwapKind.Delete, _layout.ResolveInstallPath(d), HadTarget: true)));
            operations.AddRange(writes.Select(w => new SwapOperation(SwapKind.Write, w.FullPath, HadTarget: _fs.FileExists(w.FullPath))));

            var journal = new UpdateJournal
            {
                State = JournalState.Preparing,
                FromVersion = fromVersion,
                ToVersion = toVersion,
                StartedAt = _time.GetUtcNow(),
                Writes = [.. writes.Select((w, i) => new JournalEntry { Path = w.RelativePath, HadTarget = operations[deletes.Count + i].HadTarget })],
                Deletes = deletes,
            };

            try
            {
                _journal.Write(journal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Error(UpdateStage.Commit, "无法写入 journal（preparing），更新未开始", ex);
                TryDeleteJournal();   // 清掉可能残留的 journal.json.tmp
                throw new UpdateFailedException(UpdateStage.Commit, $"无法写入更新事务日志：{ex.Message}", ex);
            }
            catch (InvalidOperationException ex)
            {
                // Write 拒绝写入：步骤 0 之后磁盘上冒出了别的事务的 journal（或损坏的 journal）。它不属于本次事务，不能删，留给启动恢复；此时还没写过任何 .sunew
                _log.Error(UpdateStage.Commit, "journal 写入被拒绝（preparing），更新未开始", ex);
                throw new UpdateFailedException(UpdateStage.Commit, $"无法写入更新事务日志：{ex.Message}", ex);
            }

            int totalFiles = writes.Count(w => w.File is not null);
            long totalBytes = writes.Where(w => w.File is not null).Sum(w => w.File!.Size);

            // 3–4. 流式写 .sunew 并边写边校验
            try
            {
                StagePendingFiles(package, writes, progress, totalFiles, totalBytes, cancellationToken);
            }
            catch (Exception ex)
            {
                _swapper.DeletePendingFiles(operations);
                TryDeleteJournal();

                if (ex is OperationCanceledException)
                {
                    _log.Warning(UpdateStage.Commit, "更新在准备阶段被取消，已清理 .sunew 与 journal");
                    throw;
                }

                if (ex is UpdateFailedException)
                {
                    throw;
                }

                if (ex is InvalidDataException)
                {
                    _log.Error(UpdateStage.Verify, "升级包内的条目数据已损坏，已清理", ex);
                    throw new UpdateFailedException(UpdateStage.Verify, $"升级包内的条目数据已损坏：{ex.Message}", ex);
                }

                _log.Error(UpdateStage.Commit, "写入待提交文件失败，已清理", ex);
                throw new UpdateFailedException(UpdateStage.Commit, $"写入待提交文件失败：{ex.Message}", ex);
            }

            // 5. committing
            journal.State = JournalState.Committing;
            try
            {
                _journal.Write(journal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // InvalidOperationException = Write 拒绝（journal 被外部改坏 / 转移非法），同样尚未触碰现存文件。
                // 磁盘上的 journal 仍是 preparing（原子写没有完成），尚未触碰任何现存文件：等同未发生
                _log.Error(UpdateStage.Commit, "无法写入 journal（committing），已清理", ex);
                _swapper.DeletePendingFiles(operations);
                TryDeleteJournal();
                throw new UpdateFailedException(UpdateStage.Commit, $"无法写入更新事务日志：{ex.Message}", ex);
            }

            // 6. 按序改名提交
            try
            {
                _swapper.Commit(operations);
            }
            catch (SwapFailedException ex)
            {
                if (ex.IsRolledBack)
                {
                    _swapper.DeletePendingFiles(operations);
                    TryDeleteJournal();
                    throw new UpdateFailedException(UpdateStage.Commit, $"提交失败，已回滚：{ex.CommitFailure.Message}", ex);
                }

                _log.Error(UpdateStage.Rollback, "提交失败且回滚未完成，journal 已保留，等待启动恢复重试", ex);
                throw new UpdateFailedException(UpdateStage.Rollback, $"提交失败且回滚未完成：{ex.CommitFailure.Message}", ex, ex.RollbackFailures);
            }
            catch (InvalidOperationException ex)
            {
                // FileSwapper 的预检发现 .sunew 不见了（被杀软隔离、被外部清理）：一个文件都还没动，等同未发生
                _log.Error(UpdateStage.Commit, "提交前发现待提交文件缺失，已清理", ex);
                _swapper.DeletePendingFiles(operations);
                TryDeleteJournal();
                throw new UpdateFailedException(UpdateStage.Commit, $"提交前发现待提交文件缺失：{ex.Message}", ex);
            }

            // 7a. 安装后校验
            IReadOnlyList<string> mismatches = VerifyInstalled(writes, progress, totalFiles, totalBytes);
            if (mismatches.Count > 0)
            {
                string detail = string.Join(", ", mismatches);
                _log.Error(UpdateStage.Verify, $"安装后校验不符，回滚：{detail}");
                throw RollBackInstalled(operations, UpdateStage.Verify, $"安装后校验不符：{detail}", inner: null);
            }

            // 7b. done；7c. state 只能在 done 之后写
            journal.State = JournalState.Done;
            try
            {
                _journal.Write(journal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // InvalidOperationException = Write 拒绝（journal 被外部改坏 / 转移非法）：事务状态无法确认，同样撤回。
                // 磁盘上的 journal 仍是 committing，新文件已全部就位并通过校验，但事务无法标记完成：撤回，回到确定的旧版本
                _log.Error(UpdateStage.Commit, "无法写入 journal（done），回滚", ex);
                throw RollBackInstalled(operations, UpdateStage.Commit, $"无法写入更新事务日志：{ex.Message}", ex);
            }

            UpdateInstalledVersion(toVersion);

            return new ApplyResult(fromVersion, toVersion, totalFiles, deletes.Count, diff.Skips.Count);
        }
    }

    /// <summary>manifest.json 自身对应的计划写入项：<see cref="File"/> 为 null。</summary>
    private sealed record PlannedWrite(string RelativePath, string FullPath, ManifestFile? File);

    private List<PlannedWrite> PlanWrites(ManifestDiffResult diff, string mainExecutableRelativePath)
    {
        List<ManifestFile> ordered = [.. diff.Writes.OrderBy(f => f.Path, StringComparer.Ordinal)];
        ManifestFile? mainExecutable = ordered.FirstOrDefault(f => string.Equals(f.Path, mainExecutableRelativePath, StringComparison.OrdinalIgnoreCase));

        var plan = new List<PlannedWrite>(ordered.Count + 1);
        foreach (ManifestFile file in ordered)
        {
            if (!ReferenceEquals(file, mainExecutable))
            {
                plan.Add(new PlannedWrite(file.Path, _layout.ResolveInstallPath(file.Path), file));
            }
        }

        plan.Add(new PlannedWrite(UpdateLayout.ManifestRelativePath, _layout.ManifestFile, null));

        if (mainExecutable is not null)
        {
            plan.Add(new PlannedWrite(mainExecutable.Path, _layout.ResolveInstallPath(mainExecutable.Path), mainExecutable));
        }

        return plan;
    }

    /// <summary>
    /// 清单里同时有文件 <c>a</c> 与 <c>a/b.txt</c>（文件名与目录名撞车，Windows 上不分大小写）：包校验不拒绝这种包，
    /// 放到磁盘上会先建出目录 <c>a</c>、随后 <c>a.sunew → a</c> 的改名必然失败。在动任何东西之前就拒绝。
    /// </summary>
    private void RejectFileDirectoryCollisions(PackageManifest manifest)
    {
        var files = new HashSet<string>(manifest.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);

        foreach (ManifestFile file in manifest.Files)
        {
            int separator = file.Path.IndexOf('/', StringComparison.Ordinal);
            while (separator >= 0)
            {
                string ancestor = file.Path[..separator];
                if (files.Contains(ancestor))
                {
                    string message = $"升级包不合法：'{ancestor}' 既是文件又是 '{file.Path}' 的上级目录";
                    _log.Error(UpdateStage.Verify, message);
                    throw new UpdateFailedException(UpdateStage.Verify, message);
                }

                separator = file.Path.IndexOf('/', separator + 1);
            }
        }
    }

    /// <summary>
    /// 读本地清单。不存在 → null（首次安装）。损坏（解析失败 / 路径不可信）→ Warning 后同样返回 null：
    /// 按首次安装处理 = 不删任何文件、重写全部；差集算不出来就不删是唯一安全的选择，代价是多写几个文件。
    /// 读不了（IO 失败）不能猜——猜错的方向可能是"以为是首次安装而把用户文件当成新增"——直接失败。
    /// </summary>
    private PackageManifest? ReadLocalManifest()
    {
        PackageManifest? manifest;
        try
        {
            manifest = AtomicFile.ReadJson(_fs, _layout.ManifestFile, SmartUpdaterJsonContext.Default.PackageManifest);
        }
        catch (JsonException ex)
        {
            _log.Warning(UpdateStage.Commit, "本地 manifest.json 无法解析，按首次安装处理（不删除任何文件）", ex);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(UpdateStage.Commit, "无法读取本地 manifest.json", ex);
            throw new UpdateFailedException(UpdateStage.Commit, $"无法读取本地清单：{ex.Message}", ex);
        }

        if (manifest is null)
        {
            if (_fs.FileExists(_layout.ManifestFile))
            {
                _log.Warning(UpdateStage.Commit, "本地 manifest.json 的内容为 JSON null，按首次安装处理（不删除任何文件）");
            }

            return null;
        }

        // 源生成器不尊重可空注解：JSON 里的 null 会穿过 required / 默认值。本地清单也不能信任其路径（ResolveInstallPath 会对逃逸路径抛出）。
        if (manifest.Files is null || manifest.Files.Any(f => f is null))
        {
            _log.Warning(UpdateStage.Commit, "本地 manifest.json 缺少 files 或含空条目，按首次安装处理（不删除任何文件）");
            return null;
        }

        IReadOnlyList<string> pathErrors = ManifestPathValidator.Validate(manifest.Files);
        if (pathErrors.Count > 0)
        {
            _log.Warning(UpdateStage.Commit, $"本地 manifest.json 含不安全的路径，按首次安装处理（不删除任何文件）：{string.Join("; ", pathErrors)}");
            return null;
        }

        return manifest;
    }

    private void LogDecisions(ManifestDiffResult diff)
    {
        foreach (ManifestFile file in diff.Writes)
        {
            _log.Information(UpdateStage.Commit, $"write {file.Path}");
        }

        foreach (string path in diff.Deletes)
        {
            _log.Information(UpdateStage.Commit, $"delete {path}");
        }

        foreach (ManifestFile file in diff.Skips)
        {
            _log.Information(UpdateStage.Commit, $"skip {file.Path}（preserve 且目标已存在）");
        }
    }

    /// <summary>
    /// 删除安装目录里递归找到的全部 <c>*.suold</c> 与 <c>*.sunew</c>。删得掉的记 Information；删不掉的记 Warning，
    /// 并让本次更新在动任何文件之前失败（"跳过"会把无法区分的歧义带进回滚，正是要避免的）。
    /// 连枚举本身都失败（例如某个子目录 ACL 拒绝访问，一次异常会中止整个扫描，连可访问目录里的陈旧文件也看不到）同样让本次更新失败：
    /// "开始前不存在陈旧 .suold / .sunew"这一前提无法确认，绝不能带着它进入 preparing。
    /// </summary>
    private void RemoveStaleSwapFiles()
    {
        IReadOnlyList<string> stale;
        try
        {
            stale =
            [
                .. _fs.EnumerateFiles(_layout.InstallDirectory, SwapFileNames.OldSearchPattern, recursive: true)
                    .Concat(_fs.EnumerateFiles(_layout.InstallDirectory, SwapFileNames.NewSearchPattern, recursive: true))
                    .Where(SwapFileNames.HasSwapSuffix)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.Ordinal),
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(UpdateStage.Commit, $"无法枚举安装目录 {_layout.InstallDirectory}，无法确认上次更新是否遗留了 .suold / .sunew，本次更新未开始", ex);
            throw new UpdateFailedException(
                UpdateStage.Commit,
                $"无法枚举安装目录 {_layout.InstallDirectory}，无法确认上次更新是否遗留了 .suold / .sunew，本次更新未开始：{ex.Message}",
                ex);
        }

        var failed = new List<string>();
        foreach (string path in stale)
        {
            try
            {
                _fs.Delete(path);
                _log.Information(UpdateStage.Commit, $"清理上次更新遗留的文件：{path}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warning(UpdateStage.Commit, $"无法清理上次更新遗留的文件：{path}", ex);
                failed.Add(Path.GetRelativePath(_layout.InstallDirectory, path).Replace('\\', '/'));
            }
        }

        if (failed.Count > 0)
        {
            throw new UpdateFailedException(UpdateStage.Commit, $"上次更新遗留的文件无法清除，本次更新未开始：{string.Join(", ", failed)}");
        }
    }

    private void StagePendingFiles(
        PackageContents package,
        IReadOnlyList<PlannedWrite> writes,
        IProgress<ApplyProgress>? progress,
        int totalFiles,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[CopyBufferSize];
        int completed = 0;
        long processed = 0;

        foreach (PlannedWrite write in writes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string pending = SwapFileNames.NewPath(write.FullPath);
            EnsureDirectory(Path.GetDirectoryName(write.FullPath)!);
            _fs.Delete(pending);   // 清陈旧：崩溃可能留下半截 .sunew，绝不当成已暂存

            if (write.File is null)
            {
                // manifest.json 自身：内容是新清单的序列化，不校验
                using Stream target = _fs.OpenWrite(pending);
                target.Write(JsonSerializer.SerializeToUtf8Bytes(package.Manifest, SmartUpdaterJsonContext.Default.PackageManifest));
                _fs.FlushToDisk(target);
                continue;
            }

            ManifestFile file = write.File;
            string actualHash;
            long actualSize;
            using (Stream source = package.OpenEntry(file.Path))
            using (Stream target = _fs.OpenWrite(pending))
            {
                (actualHash, actualSize) = CopyAndHash(source, target, file.Size, buffer);
                if (actualSize > file.Size)
                {
                    string message = $"待提交文件 {file.Path} 的内容超出清单声明的 {file.Size} 字节，已停止读取";
                    _log.Error(UpdateStage.Verify, message);
                    throw new UpdateFailedException(UpdateStage.Verify, message);
                }

                _fs.FlushToDisk(target);
            }

            if (actualSize != file.Size)
            {
                string message = $"待提交文件 {file.Path} 的大小与清单不符（期望 {file.Size}，实际 {actualSize}）";
                _log.Error(UpdateStage.Verify, message);
                throw new UpdateFailedException(UpdateStage.Verify, message);
            }

            if (!string.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                string message = $"待提交文件 {file.Path} 的 SHA-256 与清单不符（期望 {file.Sha256}，实际 {actualHash}）";
                _log.Error(UpdateStage.Verify, message);
                throw new UpdateFailedException(UpdateStage.Verify, message);
            }

            completed++;
            processed += file.Size;
            progress?.Report(new ApplyProgress(UpdateStage.Commit, completed, totalFiles, processed, totalBytes));
        }
    }

    /// <summary>
    /// 从 zip 条目流复制到目标流并同时计算 SHA-256。不信任头部声明的大小（stored 条目可以谎报，一个字节的声明可以吐出几 GB）：
    /// 每次最多只请求到"声明大小 + 1"字节，读到多出的那一个字节就立刻停止，不写入它，返回的大小 &gt; <paramref name="declaredSize"/>。
    /// </summary>
    private static (string Hash, long Size) CopyAndHash(Stream source, Stream target, long declaredSize, byte[] buffer)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;

        while (true)
        {
            long remaining = declaredSize - total;
            int request = remaining >= buffer.Length ? buffer.Length : (int)remaining + 1;
            int read = source.Read(buffer, 0, request);
            if (read <= 0)
            {
                break;
            }

            total += read;
            if (total > declaredSize)
            {
                break;
            }

            hasher.AppendData(buffer, 0, read);
            target.Write(buffer, 0, read);
        }

        return (Convert.ToHexStringLower(hasher.GetHashAndReset()), total);
    }

    private IReadOnlyList<string> VerifyInstalled(IReadOnlyList<PlannedWrite> writes, IProgress<ApplyProgress>? progress, int totalFiles, long totalBytes)
    {
        var mismatches = new List<string>();
        int completed = 0;
        long processed = 0;

        foreach (PlannedWrite write in writes)
        {
            if (write.File is null)
            {
                continue;   // manifest.json 自身不参与校验
            }

            try
            {
                using Stream stream = _fs.OpenRead(write.FullPath);
                string actual = Convert.ToHexStringLower(SHA256.HashData(stream));
                if (!string.Equals(actual, write.File.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    mismatches.Add($"{write.RelativePath}（期望 {write.File.Sha256}，实际 {actual}）");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Error(UpdateStage.Verify, $"安装后校验无法读取 {write.RelativePath}", ex);
                mismatches.Add($"{write.RelativePath}（无法读取：{ex.Message}）");
            }

            completed++;
            processed += write.File.Size;
            progress?.Report(new ApplyProgress(UpdateStage.Verify, completed, totalFiles, processed, totalBytes));
        }

        return mismatches;
    }

    /// <summary>
    /// 提交已完成之后的撤回（安装后校验不符、或 done 无法落盘）：逆序回滚 + 清 .sunew。
    /// 回滚零失败 → 删 journal，返回 <paramref name="cleanStage"/> 阶段的异常；否则保留 journal 等启动恢复，返回 Rollback 阶段的异常。
    /// </summary>
    private UpdateFailedException RollBackInstalled(IReadOnlyList<SwapOperation> operations, UpdateStage cleanStage, string reason, Exception? inner)
    {
        IReadOnlyList<Exception> failures = _swapper.Rollback(operations);
        _swapper.DeletePendingFiles(operations);

        if (failures.Count == 0)
        {
            TryDeleteJournal();
            return new UpdateFailedException(cleanStage, $"{reason}，已回滚", inner);
        }

        _log.Error(UpdateStage.Rollback, "回滚未完成，journal 已保留，等待启动恢复重试");
        return new UpdateFailedException(UpdateStage.Rollback, $"{reason}，且回滚未完成", inner, failures);
    }

    private void EnsureDirectory(string path)
    {
        if (!_fs.DirectoryExists(path))
        {
            _fs.CreateDirectory(path);
        }
    }

    /// <summary>"done 恢复会补齐"：这里失败只记 Warning，不影响已经完成的更新。</summary>
    private void UpdateInstalledVersion(Version toVersion)
    {
        try
        {
            UpdateState state = _state.Load();
            state.CurrentVersion = toVersion;
            _state.Save(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning(UpdateStage.Commit, "更新已完成，但无法写入 state.json；启动时 done 恢复会补齐", ex);
        }
    }

    /// <summary>清理失败不能掩盖原始异常；同一个 fs 可能已经"崩溃"，所以必须吞掉一切。</summary>
    private void TryDeleteJournal()
    {
        try
        {
            _journal.Delete();
        }
        catch (Exception ex)
        {
            _log.Error(UpdateStage.Commit, "清理 journal 失败", ex);
        }
    }
}
