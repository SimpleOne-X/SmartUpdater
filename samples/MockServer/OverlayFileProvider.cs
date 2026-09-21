using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace SimpleOneX.SmartUpdater.MockServer;

/// <summary>一份放在内存里的"文件"。Length 与 LastModified 由调用方决定，静态文件中间件据此算 ETag。</summary>
internal sealed class MemoryFileInfo(string name, byte[] content, DateTimeOffset lastModified) : IFileInfo
{
    public bool Exists => true;

    public long Length => content.Length;

    public string? PhysicalPath => null;

    public string Name => name;

    public DateTimeOffset LastModified => lastModified;

    public bool IsDirectory => false;

    public Stream CreateReadStream() => new MemoryStream(content, writable: false);
}

/// <summary>
/// 把若干路径用内存内容盖住底层的物理目录，磁盘一字不改。
/// 静态文件中间件照常为叠加出来的 IFileInfo 生成 ETag、处理 If-None-Match 与 Range，
/// 因此 ETag 由中间件原生生成，不需要自己写端点。
/// </summary>
internal sealed class OverlayFileProvider(IFileProvider inner) : IFileProvider
{
    private readonly Dictionary<string, MemoryFileInfo> _overlay = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public void Set(string subpath, byte[] content, DateTimeOffset lastModified)
    {
        string key = Normalize(subpath);
        lock (_gate)
        {
            _overlay[key] = new MemoryFileInfo(Path.GetFileName(key), content, lastModified);
        }
    }

    public bool Remove(string subpath)
    {
        lock (_gate)
        {
            return _overlay.Remove(Normalize(subpath));
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _overlay.Clear();
        }
    }

    public IFileInfo GetFileInfo(string subpath)
    {
        lock (_gate)
        {
            if (_overlay.TryGetValue(Normalize(subpath), out MemoryFileInfo? file))
            {
                return file;
            }
        }

        return inner.GetFileInfo(subpath);
    }

    public IDirectoryContents GetDirectoryContents(string subpath) => inner.GetDirectoryContents(subpath);

    public IChangeToken Watch(string filter) => inner.Watch(filter);

    private static string Normalize(string subpath)
        => subpath.StartsWith('/') ? subpath : "/" + subpath;
}
