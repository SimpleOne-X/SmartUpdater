namespace SimpleOneX.SmartUpdater;

/// <summary>
/// 磁盘写操作的唯一出口。<c>Apply/</c>、更新状态存取与上报队列里的所有磁盘写操作都必须经由它，
/// 测试才能在任意一次操作处注入崩溃或 IO 故障。只读探测（磁盘空间、目录可写性、日志）不参与事务，直接用 System.IO。
/// </summary>
internal interface IFileOperations
{
    /// <summary>文件是否存在。路径指向目录时返回 false。</summary>
    bool FileExists(string path);

    /// <summary>目录是否存在。</summary>
    bool DirectoryExists(string path);

    /// <summary>创建目录，缺失的上级目录一并创建。目录已存在时不报错。</summary>
    void CreateDirectory(string path);

    /// <summary>
    /// 移动或改名文件。同目录同卷的改名在 NTFS 上是原子的。
    /// <paramref name="overwrite"/> 为 false 且目标已存在时抛 <see cref="IOException"/>，源与目标都保持原样。
    /// </summary>
    void Move(string sourcePath, string destinationPath, bool overwrite);

    /// <summary>删除文件。文件不存在（含所在目录不存在）时是空操作；带只读属性的文件先清属性再删。</summary>
    void Delete(string path);

    /// <summary>打开文件用于写：不存在则创建，已存在则截断；独占，不与任何其他句柄共享。</summary>
    Stream OpenWrite(string path);

    /// <summary>打开文件用于读：允许其他句柄同时读写，校验哈希时不因别的读写句柄失败。</summary>
    Stream OpenRead(string path);

    /// <summary>
    /// 把流里的内容真正落到磁盘（<c>FileStream.Flush(flushToDisk: true)</c>）。
    /// 只能传经同一个实例 <see cref="OpenWrite"/> 得到的流：测试用的装饰器会把流再包一层，别的流会转型失败。
    /// </summary>
    void FlushToDisk(Stream stream);

    /// <summary>
    /// 枚举目录下匹配 <paramref name="searchPattern"/> 的文件，返回完整路径。目录不存在时返回空列表。
    /// 递归时无权访问的子目录被跳过而不是让整次枚举抛出：启动恢复在 foreach 的头部调用它，一个受限子目录不能中止整次恢复。
    /// </summary>
    IReadOnlyList<string> EnumerateFiles(string directory, string searchPattern, bool recursive);

    /// <summary>读取文件属性。</summary>
    FileAttributes GetAttributes(string path);

    /// <summary>设置文件属性。</summary>
    void SetAttributes(string path, FileAttributes attributes);

    /// <summary>文件字节数。</summary>
    long GetFileLength(string path);
}
