namespace SimpleOneX.SmartUpdater;

/// <summary>
/// 升级过程中两类交换文件的命名约定：<c>.sunew</c> 是待提交的新文件，<c>.suold</c> 是被替换或被删除的旧文件（由改名产生，零拷贝）。
/// 后缀写在磁盘上，崩溃残留的文件靠它在下一次启动时被认出来，所以取值不能改。
/// </summary>
internal static class SwapFileNames
{
    /// <summary>待提交的新文件后缀。</summary>
    public const string NewSuffix = ".sunew";

    /// <summary>被替换或被删除的旧文件后缀。</summary>
    public const string OldSuffix = ".suold";

    /// <summary>目标文件对应的新文件路径：<paramref name="targetPath"/> + <see cref="NewSuffix"/>。</summary>
    public static string NewPath(string targetPath) => targetPath + NewSuffix;

    /// <summary>目标文件对应的旧文件路径：<paramref name="targetPath"/> + <see cref="OldSuffix"/>。</summary>
    public static string OldPath(string targetPath) => targetPath + OldSuffix;

    /// <summary>文件名是否以任一交换后缀结尾，不分大小写。清单里的路径不允许这样命名。</summary>
    public static bool HasSwapSuffix(string fileName)
        => fileName.EndsWith(NewSuffix, StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(OldSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>枚举全部新文件用的搜索模式：<c>*.sunew</c>。</summary>
    public static string NewSearchPattern => "*" + NewSuffix;

    /// <summary>枚举全部旧文件用的搜索模式：<c>*.suold</c>。</summary>
    public static string OldSearchPattern => "*" + OldSuffix;
}
