using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class AppIdentityTests
{
    [Fact]
    public void Same_assembly_and_path_yields_same_id()
    {
        string a = AppIdentity.Derive("MyApp", @"C:\Users\x\AppData\Local\MyApp\app\MyApp.exe");
        string b = AppIdentity.Derive("MyApp", @"C:\Users\x\AppData\Local\MyApp\app\MyApp.exe");

        Assert.Equal(a, b);
    }

    [Fact]
    public void Same_assembly_at_different_paths_yields_different_ids()
    {
        // 这条是 AppId 必须带路径哈希的理由：同一个程序装两份（正式目录与测试目录），
        // 若只用程序集名，两边会共用同一个缓存和日志目录，互相覆盖。
        string prod = AppIdentity.Derive("MyApp", @"C:\Apps\MyApp\MyApp.exe");
        string test = AppIdentity.Derive("MyApp", @"D:\Test\MyApp\MyApp.exe");

        Assert.NotEqual(prod, test);
    }

    [Fact]
    public void Different_assemblies_yield_different_ids()
    {
        string a = AppIdentity.Derive("AppOne", @"C:\Apps\Shared\app.exe");
        string b = AppIdentity.Derive("AppTwo", @"C:\Apps\Shared\app.exe");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Path_comparison_ignores_case_and_trailing_separators()
    {
        string a = AppIdentity.Derive("MyApp", @"C:\Apps\MyApp\MyApp.exe");
        string b = AppIdentity.Derive("MyApp", @"c:\apps\myapp\myapp.exe");

        Assert.Equal(a, b);
    }

    [Fact]
    public void Id_has_assembly_name_then_dash_then_eight_hex_chars()
    {
        string id = AppIdentity.Derive("MyApp", @"C:\Apps\MyApp\MyApp.exe");

        Assert.StartsWith("MyApp-", id, StringComparison.Ordinal);
        string suffix = id["MyApp-".Length..];
        Assert.Equal(8, suffix.Length);
        Assert.Matches("^[0-9a-f]{8}$", suffix);
    }

    [Theory]
    [InlineData("My App")]
    [InlineData("My/App")]
    [InlineData("My:App")]
    [InlineData("My*App?")]
    public void Unsafe_characters_in_assembly_name_are_sanitized(string assemblyName)
    {
        string id = AppIdentity.Derive(assemblyName, @"C:\Apps\x\x.exe");

        Assert.True(AppIdentity.IsValid(id), $"'{id}' 未通过 IsValid 的字符集校验（只允许 ASCII 字母、数字与 '-' '_' '.'）");
    }

    [Fact]
    public void Sanitizing_does_not_collapse_distinct_names()
    {
        // "My App" 与 "My/App" 清洗后都是 "My_App"，但路径相同，
        // 若不把原始名混入哈希就会撞。
        string a = AppIdentity.Derive("My App", @"C:\Apps\x\x.exe");
        string b = AppIdentity.Derive("My/App", @"C:\Apps\x\x.exe");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Empty_assembly_name_falls_back_to_a_usable_id()
    {
        string id = AppIdentity.Derive("", @"C:\Apps\x\x.exe");

        Assert.True(AppIdentity.IsValid(id));
        Assert.NotEmpty(id);
    }

    [Theory]
    [InlineData("MyApp-1a2b3c4d", true)]
    [InlineData("My_App-1a2b3c4d", true)]
    [InlineData("My App-1a2b3c4d", false)]
    [InlineData("My/App-1a2b3c4d", false)]
    [InlineData("", false)]
    public void IsValid_rejects_ids_with_unsafe_characters(string id, bool expected)
    {
        Assert.Equal(expected, AppIdentity.IsValid(id));
    }

    // 守 .TrimEnd('\\')：路径带不带尾部分隔符是同一个位置，必须得到同一个 AppId。
    [Fact]
    public void Trailing_separator_does_not_change_the_id()
    {
        string plain = AppIdentity.Derive("MyApp", @"C:\Apps\MyApp");

        Assert.Equal(plain, AppIdentity.Derive("MyApp", @"C:\Apps\MyApp\"));
        Assert.Equal(plain, AppIdentity.Derive("MyApp", @"C:\Apps\MyApp\\"));
    }

    // 守 .Replace('/', '\\')：路径里的 / 与 \ 是同一个分隔符，写法不同不该得到不同的 AppId。
    [Fact]
    public void Forward_slashes_equal_backslashes()
    {
        Assert.Equal(
            AppIdentity.Derive("MyApp", @"C:\Apps\MyApp\MyApp.exe"),
            AppIdentity.Derive("MyApp", "C:/Apps/MyApp/MyApp.exe"));
    }

    // 守空名兜底：空串、空格、多个空格都回落为 "app"，而不是得到以 '-' 或 '_' 开头的 AppId。
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    public void Blank_assembly_name_uses_app_prefix(string blank)
    {
        string id = AppIdentity.Derive(blank, @"C:\Apps\x\x.exe");

        Assert.StartsWith("app-", id, StringComparison.Ordinal);
        Assert.True(AppIdentity.IsValid(id));
    }

    // 守清洗时的替换字符：不安全字符换成 '_'，不是随便一个合法字符。
    // Unsafe_characters_in_assembly_name_are_sanitized 只断言 IsValid，换成 'X' 也能通过。
    [Theory]
    [InlineData("My App")]
    [InlineData("My/App")]
    [InlineData("My*App")]
    [InlineData("My:App")]
    public void Unsafe_characters_in_assembly_name_become_underscores(string assemblyName)
    {
        string id = AppIdentity.Derive(assemblyName, @"C:\Apps\x\x.exe");

        Assert.StartsWith("My_App-", id, StringComparison.Ordinal);
        Assert.True(AppIdentity.IsValid(id));
    }

    // 已知答案。AppId 会落盘（目录名、Mutex 名），所以哈希必须跨进程、跨版本稳定；
    // 只在同一进程内比两次的测试，看不出哈希被换成每进程加盐的算法。
    // 期望值不是从被测代码里抄的，是用独立于 C# 的工具算出来的：
    //   material = "<原始程序集名>|<规范化路径>"，路径把 / 换成 \、去掉尾部的 \、转小写，程序集名保持原样；
    //   material 按 UTF-8 编码（无结尾换行）→ SHA-256 → 取前 4 字节 → 小写十六进制 8 位。
    //   printf '%s' 'MyApp|c:\apps\myapp\myapp.exe' | sha256sum   得 f4bc3658…
    //   printf '%s' 'My App|c:\apps\x\x.exe' | sha256sum          得 cfb7506f…
    // sha256sum、openssl dgst -sha256、certutil -hashfile、PowerShell 的 Get-FileHash 四个工具结果一致。
    // 第二行的原始名 "My App" 与清洗后的 "My_App" 哈希不同，所以它同时钉住了"哈希输入用原始程序集名"。
    // 第三行含非 ASCII：程序集名与路径里的中文用户名都按 UTF-8 进哈希，前缀是清洗后的名字（每个汉字换成一个 '_'）。
    //   material = "我的应用|c:\users\张三\app\我的应用.exe"，UTF-8 共 49 字节（19 个 ASCII 字符 + 10 个汉字 × 3 字节）；
    //   用 PowerShell 的 [System.Text.UTF8Encoding]::new($false).GetBytes(material) 写成文件（无 BOM、无换行），
    //   sha256sum、openssl dgst -sha256、certutil -hashfile、Get-FileHash 四个工具的结果一致，前 8 位是 63c2193e。
    //   前两行是纯 ASCII，钉不住编码：把 Encoding.UTF8 换成 ASCII 或 Latin1，它们照样通过。
    // AppId 会落盘，有意改算法须同时评估旧目录与旧 Mutex 名的迁移。
    [Theory]
    [InlineData("MyApp", @"C:\Apps\MyApp\MyApp.exe", "MyApp-f4bc3658")]
    [InlineData("My App", @"C:\Apps\x\x.exe", "My_App-cfb7506f")]
    [InlineData("我的应用", @"C:\Users\张三\App\我的应用.exe", "____-63c2193e")]
    public void Derive_matches_known_answers(string assemblyName, string executablePath, string expected)
    {
        Assert.Equal(expected, AppIdentity.Derive(assemblyName, executablePath));
    }

    // 守 IsValid 的字符集边界：反斜杠是路径分隔符，中文等非 ASCII 字母也不在允许集里。
    [Theory]
    [InlineData(@"My\App-1a2b3c4d")]
    [InlineData("我的-1a2b3c4d")]
    public void IsValid_rejects_backslash_and_non_ascii_letters(string id)
    {
        Assert.False(AppIdentity.IsValid(id));
    }

    // 钉住规范化的顺序：先把 / 换成 \，再去掉尾部分隔符。
    // 顺序反了，"C:/Apps/MyApp/" 去尾部时末尾还是 /，去不掉，换斜杠后多出一个 \，AppId 就变了。
    [Fact]
    public void Forward_slash_path_with_trailing_separator_equals_the_plain_backslash_path()
    {
        Assert.Equal(
            AppIdentity.Derive("MyApp", @"C:\Apps\MyApp"),
            AppIdentity.Derive("MyApp", "C:/Apps/MyApp/"));
    }

    // 守空路径快速失败：路径哈希是 AppId 的一个维度，空路径会让 AppId 静默退化成只由程序集名决定。
    // 空串与纯空白必须恰好抛 ArgumentException（Assert.Throws 不接受子类）；null 抛 ArgumentNullException。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_executable_path_throws_ArgumentException(string blank)
    {
        Assert.Throws<ArgumentException>(() => AppIdentity.Derive("MyApp", blank));
    }

    [Fact]
    public void Null_executable_path_throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => AppIdentity.Derive("MyApp", null!));
    }

    // 守哈希输入的字符编码（路径）：路径里的中文（如中文用户名下的每用户安装目录）必须真正进入哈希。
    // 按 ASCII 或 Latin1 编码时，每个汉字都会变成 '?'，"张三" 与 "李四" 下的两份安装会撞成同一个 AppId，
    // 共用同一个缓存目录、日志目录与互斥体。
    [Fact]
    public void Paths_differing_only_in_a_non_ascii_user_name_yield_different_ids()
    {
        string zhang = AppIdentity.Derive("MyApp", @"C:\Users\张三\App\MyApp.exe");
        string li = AppIdentity.Derive("MyApp", @"C:\Users\李四\App\MyApp.exe");

        Assert.NotEqual(zhang, li);
    }

    // 守哈希输入的字符编码（程序集名）：清洗会把每个汉字换成 '_'，"我的应用" 与 "你的应用" 的前缀都是 "____"，
    // 只有哈希能把它们区分开，前提是原始名里的汉字真的进了哈希。
    [Fact]
    public void Non_ascii_assembly_names_with_the_same_sanitized_prefix_yield_different_ids()
    {
        string mine = AppIdentity.Derive("我的应用", @"C:\Apps\x\x.exe");
        string yours = AppIdentity.Derive("你的应用", @"C:\Apps\x\x.exe");

        Assert.StartsWith("____-", mine, StringComparison.Ordinal);
        Assert.StartsWith("____-", yours, StringComparison.Ordinal);
        Assert.NotEqual(mine, yours);
    }
}
