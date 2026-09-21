using System.Reflection;

namespace SimpleOneX.SmartUpdater.Samples.WinForms;

/// <summary>
/// 启动早期把安装目录里的托管程序集全部加载进来。
/// 非 AOT 交付时，"提交完成 → 旧进程退出"之间若首次加载某个程序集，会加载到<b>新版本</b>的 dll，
/// 可能 MissingMethodException。安装目录里的 dll 正是会被升级包替换的那些，全部预加载即可闭合这个窗口；
/// 共享框架里的程序集不在安装目录下，不会被替换，因此不需要预热。
/// </summary>
internal static class Preheat
{
    /// <summary>加载安装目录下的全部托管 dll。</summary>
    /// <returns>成功加载的程序集数量。</returns>
    public static int LoadInstallDirectoryAssemblies()
    {
        int loaded = 0;
        foreach (string path in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
        {
            try
            {
                Assembly.Load(AssemblyName.GetAssemblyName(path));
                loaded++;
            }
            catch (BadImageFormatException)
            {
                // 原生 dll，跳过。
            }
            catch (FileLoadException)
            {
                // 已以别的身份加载过。
            }
            catch (IOException)
            {
                // 文件被占用或消失：预热是尽力而为。
            }
        }

        return loaded;
    }
}
