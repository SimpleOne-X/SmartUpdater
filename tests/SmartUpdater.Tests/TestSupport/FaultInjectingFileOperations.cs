using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

/// <summary>一次文件操作的记录。Index 从 1 开始。</summary>
internal sealed record FileOperation(int Index, string Kind, string Path, string? SecondPath = null)
{
    public static readonly string[] MutatingKinds =
        ["CreateDirectory", "Move", "Delete", "OpenWrite", "FlushToDisk", "SetAttributes"];

    public bool IsMutating => MutatingKinds.Contains(Kind, StringComparer.Ordinal);

    public override string ToString()
        => SecondPath is null ? $"{Index}: {Kind} {Path}" : $"{Index}: {Kind} {Path} -> {SecondPath}";
}

/// <summary>模拟"进程在此刻死亡"。抛出后同一实例上的所有后续操作也都抛出。</summary>
internal sealed class SimulatedCrashException(string message) : Exception(message);

/// <summary>
/// 包住真实文件系统：记录每一次操作，并按 <see cref="Policy"/> 在指定操作处抛出。
/// 崩溃语义：策略返回 <see cref="SimulatedCrashException"/> 后置 <see cref="Crashed"/>，此后每次操作都抛出，磁盘停在崩溃瞬间。
/// 非崩溃的 IO 故障用 <see cref="FailOnce"/> / <see cref="FailAlways"/>（抛 <see cref="IOException"/>，之后照常工作）。
/// </summary>
internal sealed class FaultInjectingFileOperations(IFileOperations inner) : IFileOperations
{
    private readonly List<FileOperation> _operations = [];

    public IReadOnlyList<FileOperation> Operations => _operations;

    public IReadOnlyList<FileOperation> MutatingOperations => [.. _operations.Where(o => o.IsMutating)];

    public Func<FileOperation, Exception?>? Policy { get; set; }

    /// <summary>策略放行后、真正执行前的观察点。测试用它在指定操作前篡改磁盘（模拟杀软改写、外部并发改动）。</summary>
    public Action<FileOperation>? OnOperation { get; set; }

    public bool Crashed { get; private set; }

    public static Func<FileOperation, Exception?> CrashAt(int index)
        => op => op.Index == index ? new SimulatedCrashException($"模拟崩溃于第 {index} 次操作：{op}") : null;

    // FailOnce / FailAlways：pathSuffix 匹配的是 op.Path；对 Move 而言那是源路径（目标在 SecondPath）。
    public static Func<FileOperation, Exception?> FailOnce(string kind, string pathSuffix)
    {
        bool fired = false;
        return op =>
        {
            if (fired || op.Kind != kind || !op.Path.EndsWith(pathSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            fired = true;
            return new IOException($"模拟 IO 故障：{op}");
        };
    }

    public static Func<FileOperation, Exception?> FailAlways(string kind, string pathSuffix)
        => op => op.Kind == kind && op.Path.EndsWith(pathSuffix, StringComparison.OrdinalIgnoreCase)
            ? new IOException($"模拟 IO 故障：{op}")
            : null;

    public static Func<FileOperation, Exception?> Any(params Func<FileOperation, Exception?>[] policies)
        => op => policies.Select(p => p(op)).FirstOrDefault(e => e is not null);

    private void Before(string kind, string path, string? secondPath = null)
    {
        var op = new FileOperation(_operations.Count + 1, kind, path, secondPath);
        _operations.Add(op);

        if (Crashed)
        {
            throw new SimulatedCrashException($"进程已崩溃，磁盘不再响应：{op}");
        }

        Exception? failure = Policy?.Invoke(op);
        if (failure is SimulatedCrashException)
        {
            Crashed = true;
        }

        if (failure is not null)
        {
            throw failure;
        }

        OnOperation?.Invoke(op);
    }

    public bool FileExists(string path)
    {
        Before("FileExists", path);
        return inner.FileExists(path);
    }

    public bool DirectoryExists(string path)
    {
        Before("DirectoryExists", path);
        return inner.DirectoryExists(path);
    }

    public void CreateDirectory(string path)
    {
        Before("CreateDirectory", path);
        inner.CreateDirectory(path);
    }

    public void Move(string sourcePath, string destinationPath, bool overwrite)
    {
        Before("Move", sourcePath, destinationPath);
        inner.Move(sourcePath, destinationPath, overwrite);
    }

    public void Delete(string path)
    {
        Before("Delete", path);
        inner.Delete(path);
    }

    public Stream OpenWrite(string path)
    {
        Before("OpenWrite", path);
        return new TrackedStream(inner.OpenWrite(path), path);
    }

    public Stream OpenRead(string path)
    {
        Before("OpenRead", path);
        return inner.OpenRead(path);
    }

    public void FlushToDisk(Stream stream)
    {
        var tracked = (TrackedStream)stream;
        Before("FlushToDisk", tracked.Path);
        inner.FlushToDisk(tracked.Inner);
    }

    public IReadOnlyList<string> EnumerateFiles(string directory, string searchPattern, bool recursive)
    {
        Before("EnumerateFiles", directory, searchPattern);
        return inner.EnumerateFiles(directory, searchPattern, recursive);
    }

    public FileAttributes GetAttributes(string path)
    {
        Before("GetAttributes", path);
        return inner.GetAttributes(path);
    }

    public void SetAttributes(string path, FileAttributes attributes)
    {
        Before("SetAttributes", path);
        inner.SetAttributes(path, attributes);
    }

    public long GetFileLength(string path)
    {
        Before("GetFileLength", path);
        return inner.GetFileLength(path);
    }

    /// <summary>记住路径的透传流，让 FlushToDisk 也能带上路径。Dispose 一定释放内层句柄。</summary>
    private sealed class TrackedStream(Stream inner, string path) : Stream
    {
        public Stream Inner => inner;

        public string Path => path;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
