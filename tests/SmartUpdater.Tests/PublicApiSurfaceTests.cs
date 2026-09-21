using System.Reflection;
using System.Runtime.CompilerServices;
using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class PublicApiSurfaceTests
{
    private static readonly Assembly PackageAssembly = typeof(UpdateMode).Assembly;

    [Fact]
    public void Only_expected_types_are_public()
    {
        // 期望清单由 PublicApi/ExpectedPublicApi.*.cs 按类型分文件登记：强制每一次公开面扩张都是有意识的决定。
        string[] expected = ExpectedPublicApi.All();

        string[] actual = [.. PackageAssembly
            .GetExportedTypes()
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)];

        Assert.Equal([.. expected.OrderBy(n => n, StringComparer.Ordinal)], actual);
    }

    [Fact]
    public void Expected_registry_has_no_duplicate_names()
    {
        string[] all = [.. ExpectedPublicApi.Groups().SelectMany(g => g)];

        string[] duplicates = [.. all.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key)];

        Assert.Empty(duplicates);
    }

    [Fact]
    public void All_public_types_live_in_the_single_flat_namespace()
    {
        foreach (Type t in PackageAssembly.GetExportedTypes())
        {
            Assert.Equal("SimpleOneX.SmartUpdater", t.Namespace);
        }
    }

    // 局限：这只是第二道网。
    //  - StartsWith("System") 是粗过滤：名字以 System 开头的第三方包会被放行；反过来，属于框架、名字却不以 System 开头的
    //    程序集（如 Microsoft.Win32.Registry）会被误判为第三方，届时需要有意识地调整这里的过滤条件。
    //  - 程序集引用表里只有代码真正用到的程序集：一个从未被使用的 PackageReference 不会出现在这里。
    // "SmartUpdater.csproj 里没有任何 PackageReference" 才是零第三方依赖的硬证据。
    [Fact]
    public void Assembly_has_no_third_party_references()
    {
        string[] offenders = [.. PackageAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => !n.StartsWith("System", StringComparison.Ordinal)
                     && !n.Equals("netstandard", StringComparison.Ordinal))];

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_types_are_forwarded_out_of_the_assembly()
    {
        // GetExportedTypes() 看不到类型转发：[assembly: TypeForwardedTo(typeof(X))] 会把别的程序集的类型
        // 悄悄挂进公开面，而上面的清单测试全部仍然通过。
        Assert.Empty(PackageAssembly.GetForwardedTypes());
    }

    [Fact]
    public void All_types_live_in_the_single_flat_namespace()
    {
        // 上面的命名空间测试只看 public 类型；全局约束是"所有文件同一命名空间"，internal 类型放错命名空间同样要抓。
        // 编译器生成的类型（<PrivateImplementationDetails>、闭包类等）不受此约束。
        string[] offenders = [.. PackageAssembly
            .GetTypes()
            .Where(t => !IsCompilerGenerated(t))
            .Where(t => t.Namespace != "SimpleOneX.SmartUpdater")
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)];

        Assert.Empty(offenders);
    }

    [Fact]
    public void Internals_are_visible_only_to_the_test_assembly()
    {
        // 每多一个 InternalsVisibleTo，就等于把 internal 类型暴露给另一个程序集，是公开面的隐性扩张。
        // 有意新增时（例如 Packer 的测试项目）必须同步改这个清单。
        string[] actual = [.. PackageAssembly
            .GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(a => a.AssemblyName!)
            .OrderBy(n => n, StringComparer.Ordinal)];

        Assert.Equal(["SmartUpdater.Packer", "SmartUpdater.Packer.Tests", "SmartUpdater.Tests"], actual);
    }

    private static bool IsCompilerGenerated(Type type)
    {
        for (Type? t = type; t is not null; t = t.DeclaringType)
        {
            if (t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            {
                return true;
            }
        }

        return false;
    }
}
