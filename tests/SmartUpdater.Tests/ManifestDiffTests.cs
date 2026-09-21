using SimpleOneX.SmartUpdater;

namespace SmartUpdater.Tests;

public class ManifestDiffTests
{
    private static ManifestFile File(string path, string sha, FilePolicy policy = FilePolicy.Replace)
        => new() { Path = path, Sha256 = sha, Size = 1, Policy = policy };

    private static PackageManifest Manifest(string version, params ManifestFile[] files)
        => new() { SchemaVersion = 1, Version = Version.Parse(version), Files = files };

    private static readonly Func<string, bool> NothingExists = _ => false;
    private static readonly Func<string, bool> EverythingExists = _ => true;

    [Fact]
    public void First_install_writes_everything()
    {
        var incoming = Manifest("1.0.0", File("a.exe", "h1"), File("b.dll", "h2"));

        var diff = ManifestDiff.Compute(localManifest: null, incoming, NothingExists);

        Assert.Equal(2, diff.Writes.Count);
        Assert.Empty(diff.Deletes);
        Assert.Empty(diff.Skips);
    }

    [Fact]
    public void Unchanged_hash_is_not_written()
    {
        var local = Manifest("1.0.0", File("a.exe", "same"));
        var incoming = Manifest("1.1.0", File("a.exe", "same"));

        var diff = ManifestDiff.Compute(local, incoming, EverythingExists);

        Assert.Empty(diff.Writes);
        Assert.Empty(diff.Deletes);
    }

    [Fact]
    public void Changed_hash_is_written()
    {
        var local = Manifest("1.0.0", File("a.exe", "old"));
        var incoming = Manifest("1.1.0", File("a.exe", "new"));

        var diff = ManifestDiff.Compute(local, incoming, EverythingExists);

        Assert.Equal("a.exe", Assert.Single(diff.Writes).Path);
    }

    [Fact]
    public void File_present_locally_but_absent_from_new_manifest_is_deleted()
    {
        var local = Manifest("1.0.0", File("a.exe", "h1"), File("gone.dll", "h2"));
        var incoming = Manifest("1.1.0", File("a.exe", "h1"));

        var diff = ManifestDiff.Compute(local, incoming, EverythingExists);

        Assert.Equal("gone.dll", Assert.Single(diff.Deletes));
    }

    [Fact]
    public void New_file_is_written()
    {
        var local = Manifest("1.0.0", File("a.exe", "h1"));
        var incoming = Manifest("1.1.0", File("a.exe", "h1"), File("new.dll", "h2"));

        var diff = ManifestDiff.Compute(local, incoming, EverythingExists);

        Assert.Equal("new.dll", Assert.Single(diff.Writes).Path);
    }

    [Fact]
    public void Preserve_file_is_written_when_target_is_absent()
    {
        var incoming = Manifest("1.0.0", File("appsettings.json", "h", FilePolicy.Preserve));

        var diff = ManifestDiff.Compute(null, incoming, NothingExists);

        Assert.Equal("appsettings.json", Assert.Single(diff.Writes).Path);
        Assert.Empty(diff.Skips);
    }

    [Fact]
    public void Preserve_file_is_skipped_when_target_already_exists()
    {
        var incoming = Manifest("1.1.0", File("appsettings.json", "different", FilePolicy.Preserve));
        var local = Manifest("1.0.0", File("appsettings.json", "original", FilePolicy.Preserve));

        var diff = ManifestDiff.Compute(local, incoming, EverythingExists);

        Assert.Empty(diff.Writes);
        Assert.Equal("appsettings.json", Assert.Single(diff.Skips).Path);
    }

    [Fact]
    public void Iron_rule_1_preserve_file_never_enters_deletes()
    {
        // 新清单仍然列出它（内容哈希已变），目标也已存在：它必须落在 Skips，绝不能进 Deletes
        var local = Manifest("1.0.0",
            File("a.exe", "h1"),
            File("appsettings.json", "h2", FilePolicy.Preserve));
        var incoming = Manifest("1.1.0",
            File("a.exe", "h1"),
            File("appsettings.json", "h2-new", FilePolicy.Preserve));

        var diff = ManifestDiff.Compute(local, incoming, EverythingExists);

        Assert.DoesNotContain("appsettings.json", diff.Deletes);
        Assert.Equal("appsettings.json", Assert.Single(diff.Skips).Path);
    }

    [Fact]
    public void Iron_rule_2_preserve_file_dropped_from_new_manifest_is_still_not_deleted()
    {
        // 新版本不再列出这个 preserve 文件——它可能是使用者的数据，绝不能删
        var local = Manifest("1.0.0",
            File("a.exe", "h1"),
            File("user-data.db", "h2", FilePolicy.Preserve));
        var incoming = Manifest("1.1.0", File("a.exe", "h1"));

        var diff = ManifestDiff.Compute(local, incoming, EverythingExists);

        Assert.Empty(diff.Deletes);
    }

    [Fact]
    public void Replace_file_dropped_from_new_manifest_is_deleted()
    {
        // 与上一条对照：非 preserve 的文件消失了就该删
        var local = Manifest("1.0.0", File("a.exe", "h1"), File("old.dll", "h2"));
        var incoming = Manifest("1.1.0", File("a.exe", "h1"));

        var diff = ManifestDiff.Compute(local, incoming, EverythingExists);

        Assert.Equal("old.dll", Assert.Single(diff.Deletes));
    }

    [Fact]
    public void Paths_are_compared_case_insensitively_on_windows()
    {
        var local = Manifest("1.0.0", File("App.exe", "same"));
        var incoming = Manifest("1.1.0", File("app.exe", "same"));

        var diff = ManifestDiff.Compute(local, incoming, EverythingExists);

        Assert.Empty(diff.Writes);
        Assert.Empty(diff.Deletes);
    }

    [Fact]
    public void Smartupdater_directory_never_participates()
    {
        // 本地清单里混进了 .smartupdater/ 下的文件也不能被删
        var local = Manifest("1.0.0",
            File("a.exe", "h1"),
            File(".smartupdater/state.json", "h2"));
        var incoming = Manifest("1.1.0", File("a.exe", "h1"));

        var diff = ManifestDiff.Compute(local, incoming, EverythingExists);

        Assert.Empty(diff.Deletes);
    }

    [Fact]
    public void Unchanged_hash_is_rewritten_when_target_is_missing()
    {
        var local = Manifest("1.0.0", File("a.exe", "same"));
        var incoming = Manifest("1.1.0", File("a.exe", "same"));

        var diff = ManifestDiff.Compute(local, incoming, NothingExists);

        Assert.Equal("a.exe", Assert.Single(diff.Writes).Path);
    }

    [Fact]
    public void Preserve_file_with_same_hash_is_rewritten_when_user_deleted_it()
    {
        var local = Manifest("1.0.0", File("appsettings.json", "same", FilePolicy.Preserve));
        var incoming = Manifest("1.1.0", File("appsettings.json", "same", FilePolicy.Preserve));

        var diff = ManifestDiff.Compute(local, incoming, NothingExists);

        Assert.Equal("appsettings.json", Assert.Single(diff.Writes).Path);
        Assert.Empty(diff.Skips);
    }

    [Fact]
    public void Hash_comparison_ignores_case()
    {
        var local = Manifest("1.0.0", File("a.exe", "ABCDEF"));
        var incoming = Manifest("1.1.0", File("a.exe", "abcdef"));

        var diff = ManifestDiff.Compute(local, incoming, EverythingExists);

        Assert.Empty(diff.Writes);
    }

    [Fact]
    public void Preserve_file_in_the_middle_does_not_stop_later_files_from_being_written()
    {
        // preserve 条目夹在两个普通条目中间：跳过它之后，排在它后面的文件仍然要被处理
        var local = Manifest("1.0.0",
            File("a.exe", "a-old"),
            File("appsettings.json", "p-same", FilePolicy.Preserve),
            File("b.dll", "b-old"));
        var incoming = Manifest("1.1.0",
            File("a.exe", "a-new"),
            File("appsettings.json", "p-same", FilePolicy.Preserve),
            File("b.dll", "b-new"));

        var diff = ManifestDiff.Compute(local, incoming, EverythingExists);

        Assert.Equal("appsettings.json", Assert.Single(diff.Skips).Path);
        Assert.Equal(2, diff.Writes.Count);
        Assert.Contains(diff.Writes, f => f.Path == "a.exe");
        Assert.Contains(diff.Writes, f => f.Path == "b.dll");
    }

    [Fact]
    public void Replace_file_listed_after_a_preserve_file_is_still_deleted()
    {
        // 本地清单里 preserve 条目排在前面：跳过它之后，排在它后面、被新清单丢掉的普通文件仍然要删
        var local = Manifest("1.0.0",
            File("a.exe", "h1"),
            File("user-data.db", "h2", FilePolicy.Preserve),
            File("old.dll", "h3"));
        var incoming = Manifest("1.1.0", File("a.exe", "h1"));

        var diff = ManifestDiff.Compute(local, incoming, EverythingExists);

        Assert.Equal("old.dll", Assert.Single(diff.Deletes));
    }

    [Fact]
    public void First_install_keeps_existing_preserve_file_and_writes_the_rest()
    {
        // 首次接入（没有本地清单）时，安装目录里可能已经有使用者的 preserve 文件：保留它，不能因为是首次就全写覆盖
        var incoming = Manifest("1.0.0",
            File("appsettings.json", "h1", FilePolicy.Preserve),
            File("q.dll", "h2"));

        var diff = ManifestDiff.Compute(localManifest: null, incoming, EverythingExists);

        Assert.Equal("appsettings.json", Assert.Single(diff.Skips).Path);
        Assert.Equal("q.dll", Assert.Single(diff.Writes).Path);
    }

    [Fact]
    public void Directories_that_only_resemble_the_state_directory_are_deleted_when_dropped()
    {
        // .smartupdater-old/ 与 sub/.smartupdater/ 都不是根下的元数据目录 .smartupdater/：被新清单丢掉时必须照常删除
        var local = Manifest("1.0.0",
            File("a.exe", "h1"),
            File(".smartupdater-old/x.dll", "h2"),
            File("sub/.smartupdater/y.dll", "h3"));
        var incoming = Manifest("1.1.0", File("a.exe", "h1"));

        var diff = ManifestDiff.Compute(local, incoming, EverythingExists);

        Assert.Equal(2, diff.Deletes.Count);
        Assert.Contains(".smartupdater-old/x.dll", diff.Deletes);
        Assert.Contains("sub/.smartupdater/y.dll", diff.Deletes);
    }

    [Fact]
    public void Smartupdater_directory_entries_in_new_manifest_are_never_written()
    {
        // 新清单里混进了 .smartupdater/ 下的条目（含大小写不同的写法）也不能被写入
        var local = Manifest("1.0.0", File("a.exe", "h1"));
        var incoming = Manifest("1.1.0",
            File("a.exe", "h1"),
            File(".smartupdater/x", "h2"),
            File(".SmartUpdater/z.json", "h3"));

        var diff = ManifestDiff.Compute(local, incoming, EverythingExists);

        Assert.Empty(diff.Writes);
    }
}
