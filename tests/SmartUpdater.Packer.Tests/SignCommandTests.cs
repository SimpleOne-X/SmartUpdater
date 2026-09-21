using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SimpleOneX.SmartUpdater.Packer.Tests;

public sealed class SignCommandTests
{
    private const string TwoReleaseFeed = """
        {
          "schemaVersion": 1,
          "channel": "stable",
          "vendorNote": "内部备注",
          "releases": [
            {
              "version": "1.2.4",
              "releasedAt": "2026-09-18T10:00:00Z",
              "package": {
                "url": "packages/MyApp-1.2.4.zip",
                "size": 12345678,
                "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
              },
              "minUpdatableFrom": "1.0.0",
              "mode": "mandatory",
              "rolloutPercent": 100,
              "notes": "修复若干问题",
              "ticketId": "OPS-42"
            },
            {
              "version": "1.2.3",
              "releasedAt": "2026-09-17T10:00:00Z",
              "package": {
                "url": "packages/MyApp-1.2.3.zip",
                "size": 900,
                "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
              },
              "mode": "optional",
              "rolloutPercent": 50
            }
          ]
        }
        """;

    private static (string FeedPath, string KeyPath, ECDsa Key) Arrange(TempDirectory temp, string feedJson)
    {
        string feedPath = Path.Combine(temp.Path, "releases.json");
        File.WriteAllText(feedPath, feedJson);

        ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string keyPath = Path.Combine(temp.Path, "private.pem");
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());

        return (feedPath, keyPath, key);
    }

    private static int Run(out string stdout, out string stderr, params string[] args)
    {
        Assert.True(ArgumentParser.TryParse(args, out ParsedCommandLine? parsed, out string? parseError), parseError);

        var output = new StringWriter();
        var error = new StringWriter();
        int code = SignCommand.Run(parsed!, output, error);

        stdout = output.ToString();
        stderr = error.ToString();
        return code;
    }

    /// <summary>把 feed 里指定版本的条目读回成 ReleaseEntry（含 Signature），供验签用。</summary>
    private static ReleaseEntry ReadEntry(string feedPath, string version)
    {
        ReleaseFeedDocument feed = JsonSerializer.Deserialize(
            File.ReadAllText(feedPath), SmartUpdaterJsonContext.Default.ReleaseFeedDocument)!;

        return feed.Releases.Single(r => r.Version.ToString() == version);
    }

    [Fact]
    public void Every_unsigned_entry_gets_a_signature_that_verifies()
    {
        using var temp = new TempDirectory();
        (string feedPath, string keyPath, ECDsa key) = Arrange(temp, TwoReleaseFeed);
        using (key)
        {
            int code = Run(out _, out string stderr, "sign", "--feed", feedPath, "--key", keyPath);

            Assert.Equal(ExitCode.Success, code);
            Assert.Empty(stderr);

            foreach (string version in (string[])["1.2.4", "1.2.3"])
            {
                ReleaseEntry entry = ReadEntry(feedPath, version);
                Assert.NotNull(entry.Signature);
                Assert.True(ReleaseSignature.Verify(entry, key), $"{version} 的签名未通过验证");
            }
        }
    }

    [Fact]
    public void Signature_is_valid_base64()
    {
        using var temp = new TempDirectory();
        (string feedPath, string keyPath, ECDsa key) = Arrange(temp, TwoReleaseFeed);
        using (key)
        {
            Run(out _, out _, "sign", "--feed", feedPath, "--key", keyPath);

            // 签名是 DER（变长 70~72 字节），所以只断言"能 base64 解码"，不断言固定长度。
            byte[] raw = Convert.FromBase64String(ReadEntry(feedPath, "1.2.4").Signature!);
            Assert.InRange(raw.Length, 68, 74);
        }
    }

    [Fact]
    public void Only_the_signature_field_is_added()
    {
        using var temp = new TempDirectory();
        (string feedPath, string keyPath, ECDsa key) = Arrange(temp, TwoReleaseFeed);
        using (key)
        {
            // 先用 Packer 的写出设置把原 feed 归一一次，排除纯排版差异。
            string normalised = JsonNode.Parse(TwoReleaseFeed)!.ToJsonString(PackerJson.Write);
            File.WriteAllText(feedPath, normalised);

            Run(out _, out _, "sign", "--feed", feedPath, "--key", keyPath);

            // 前一个属性会多出一个逗号（"rolloutPercent": 100 -> "rolloutPercent": 100,），差分前先去掉行尾逗号。
            string[] before = [.. normalised.ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd(','))];
            string[] after = [.. File.ReadAllText(feedPath).ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd(','))];
            string[] added = [.. after.Except(before)];

            Assert.Equal(before.Length + 2, after.Length);
            Assert.All(added, line => Assert.Contains("\"signature\"", line, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Unknown_fields_survive_signing()
    {
        using var temp = new TempDirectory();
        (string feedPath, string keyPath, ECDsa key) = Arrange(temp, TwoReleaseFeed);
        using (key)
        {
            Run(out _, out _, "sign", "--feed", feedPath, "--key", keyPath);

            JsonNode feed = JsonNode.Parse(File.ReadAllText(feedPath))!;
            Assert.Equal("内部备注", feed["vendorNote"]!.GetValue<string>());
            Assert.Equal("OPS-42", feed["releases"]!.AsArray()[0]!["ticketId"]!.GetValue<string>());
        }
    }

    [Fact]
    public void Already_signed_entries_are_skipped_by_default()
    {
        using var temp = new TempDirectory();
        (string feedPath, string keyPath, ECDsa key) = Arrange(temp, TwoReleaseFeed);
        using (key)
        {
            Run(out _, out _, "sign", "--feed", feedPath, "--key", keyPath);
            string firstSignature = ReadEntry(feedPath, "1.2.4").Signature!;

            int code = Run(out string stdout, out _, "sign", "--feed", feedPath, "--key", keyPath);

            Assert.Equal(ExitCode.Success, code);
            Assert.Equal(firstSignature, ReadEntry(feedPath, "1.2.4").Signature);
            Assert.Contains("0", stdout, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Resign_replaces_existing_signatures_and_they_still_verify()
    {
        using var temp = new TempDirectory();
        (string feedPath, string keyPath, ECDsa key) = Arrange(temp, TwoReleaseFeed);
        using (key)
        {
            Run(out _, out _, "sign", "--feed", feedPath, "--key", keyPath);
            string firstSignature = ReadEntry(feedPath, "1.2.4").Signature!;

            Run(out _, out _, "sign", "--feed", feedPath, "--key", keyPath, "--resign");
            ReleaseEntry entry = ReadEntry(feedPath, "1.2.4");

            // ECDSA 的 k 是随机的，重签必然得到不同的字节 —— 但两者都必须验得过。
            Assert.NotEqual(firstSignature, entry.Signature);
            Assert.True(ReleaseSignature.Verify(entry, key));
        }
    }

    [Fact]
    public void Mixed_feed_only_signs_the_unsigned_entry()
    {
        using var temp = new TempDirectory();
        (string feedPath, string keyPath, ECDsa key) = Arrange(temp, TwoReleaseFeed);
        using (key)
        {
            // 先整体签一次，再把 1.2.3 的签名删掉。
            Run(out _, out _, "sign", "--feed", feedPath, "--key", keyPath);
            string keptSignature = ReadEntry(feedPath, "1.2.4").Signature!;

            JsonNode feed = JsonNode.Parse(File.ReadAllText(feedPath))!;
            feed["releases"]!.AsArray().Single(r => r!["version"]!.GetValue<string>() == "1.2.3")!
                .AsObject().Remove("signature");
            File.WriteAllText(feedPath, feed.ToJsonString(PackerJson.Write));

            Run(out _, out _, "sign", "--feed", feedPath, "--key", keyPath);

            Assert.Equal(keptSignature, ReadEntry(feedPath, "1.2.4").Signature);
            Assert.True(ReleaseSignature.Verify(ReadEntry(feedPath, "1.2.3"), key));
        }
    }

    [Theory]
    [InlineData("rolloutPercent", 5)]
    [InlineData("version", "1.2.5")]
    public void Tampering_with_a_field_breaks_verification(string field, object newValue)
    {
        using var temp = new TempDirectory();
        (string feedPath, string keyPath, ECDsa key) = Arrange(temp, TwoReleaseFeed);
        using (key)
        {
            Run(out _, out _, "sign", "--feed", feedPath, "--key", keyPath);

            JsonNode feed = JsonNode.Parse(File.ReadAllText(feedPath))!;
            JsonObject target = feed["releases"]!.AsArray()[0]!.AsObject();
            target[field] = newValue is int i ? JsonValue.Create(i) : JsonValue.Create((string)newValue);
            File.WriteAllText(feedPath, feed.ToJsonString(PackerJson.Write));

            Assert.False(ReleaseSignature.Verify(ReadEntry(feedPath, target["version"]!.GetValue<string>()), key));
        }
    }

    [Fact]
    public void Tampering_with_the_package_hash_breaks_verification()
    {
        using var temp = new TempDirectory();
        (string feedPath, string keyPath, ECDsa key) = Arrange(temp, TwoReleaseFeed);
        using (key)
        {
            Run(out _, out _, "sign", "--feed", feedPath, "--key", keyPath);

            JsonNode feed = JsonNode.Parse(File.ReadAllText(feedPath))!;
            feed["releases"]!.AsArray()[0]!["package"]!["sha256"] =
                "0000000000000000000000000000000000000000000000000000000000000000";
            File.WriteAllText(feedPath, feed.ToJsonString(PackerJson.Write));

            Assert.False(ReleaseSignature.Verify(ReadEntry(feedPath, "1.2.4"), key));
        }
    }

    [Fact]
    public void A_different_key_does_not_verify()
    {
        using var temp = new TempDirectory();
        (string feedPath, string keyPath, ECDsa key) = Arrange(temp, TwoReleaseFeed);
        using (key)
        {
            Run(out _, out _, "sign", "--feed", feedPath, "--key", keyPath);

            using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Assert.False(ReleaseSignature.Verify(ReadEntry(feedPath, "1.2.4"), other));
        }
    }

    [Fact]
    public void Public_key_is_printed_in_the_form_the_client_expects()
    {
        using var temp = new TempDirectory();
        (string feedPath, string keyPath, ECDsa key) = Arrange(temp, TwoReleaseFeed);
        using (key)
        {
            Run(out string stdout, out _, "sign", "--feed", feedPath, "--key", keyPath);

            string expected = ReleaseSignature.ExportPublicKey(key);
            Assert.Contains(expected, stdout, StringComparison.Ordinal);

            // 打印出来的串必须能被客户端的导入函数吃下去。
            using ECDsa imported = ReleaseSignature.ImportPublicKey(expected);
            Assert.True(ReleaseSignature.Verify(ReadEntry(feedPath, "1.2.4"), imported));
        }
    }

    [Fact]
    public void Public_key_is_printed_even_when_nothing_needed_signing()
    {
        using var temp = new TempDirectory();
        (string feedPath, string keyPath, ECDsa key) = Arrange(temp, TwoReleaseFeed);
        using (key)
        {
            Run(out _, out _, "sign", "--feed", feedPath, "--key", keyPath);

            Run(out string stdout, out _, "sign", "--feed", feedPath, "--key", keyPath);

            Assert.Contains(ReleaseSignature.ExportPublicKey(key), stdout, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Sec1_pem_from_openssl_is_accepted()
    {
        using var temp = new TempDirectory();
        (string feedPath, _, ECDsa key) = Arrange(temp, TwoReleaseFeed);
        using (key)
        {
            // openssl ecparam -name prime256v1 -genkey 产出的就是 SEC1 形态。
            string sec1Path = Path.Combine(temp.Path, "sec1.pem");
            File.WriteAllText(sec1Path, key.ExportECPrivateKeyPem());

            int code = Run(out _, out _, "sign", "--feed", feedPath, "--key", sec1Path);

            Assert.Equal(ExitCode.Success, code);
            Assert.True(ReleaseSignature.Verify(ReadEntry(feedPath, "1.2.4"), key));
        }
    }

    [Fact]
    public void A_feed_with_one_bad_entry_is_not_modified_at_all()
    {
        using var temp = new TempDirectory();
        string broken = """
            {
              "schemaVersion": 1,
              "releases": [
                {
                  "version": "1.2.4",
                  "releasedAt": "2026-09-18T10:00:00Z",
                  "package": { "url": "p.zip", "size": 1, "sha256": "aa" }
                },
                {
                  "version": "not-a-version",
                  "releasedAt": "2026-09-17T10:00:00Z",
                  "package": { "url": "q.zip", "size": 1, "sha256": "bb" }
                }
              ]
            }
            """;
        (string feedPath, string keyPath, ECDsa key) = Arrange(temp, broken);
        using (key)
        {
            int code = Run(out _, out string stderr, "sign", "--feed", feedPath, "--key", keyPath);

            Assert.Equal(ExitCode.Input, code);
            Assert.Contains("not-a-version", stderr, StringComparison.Ordinal);
            Assert.Equal(broken, File.ReadAllText(feedPath));
        }
    }

    [Fact]
    public void Missing_feed_exits_with_the_input_code()
    {
        using var temp = new TempDirectory();
        (_, string keyPath, ECDsa key) = Arrange(temp, TwoReleaseFeed);
        using (key)
        {
            string missing = Path.Combine(temp.Path, "nope.json");

            int code = Run(out _, out string stderr, "sign", "--feed", missing, "--key", keyPath);

            Assert.Equal(ExitCode.Input, code);
            Assert.Contains(missing, stderr, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Malformed_feed_exits_with_the_input_code()
    {
        using var temp = new TempDirectory();
        (string feedPath, string keyPath, ECDsa key) = Arrange(temp, "{ broken");
        using (key)
        {
            Assert.Equal(ExitCode.Input, Run(out _, out _, "sign", "--feed", feedPath, "--key", keyPath));
            Assert.Equal("{ broken", File.ReadAllText(feedPath));
        }
    }

    [Fact]
    public void Missing_key_file_exits_with_the_input_code()
    {
        using var temp = new TempDirectory();
        (string feedPath, _, ECDsa key) = Arrange(temp, TwoReleaseFeed);
        using (key)
        {
            string missing = Path.Combine(temp.Path, "nope.pem");

            int code = Run(out _, out string stderr, "sign", "--feed", feedPath, "--key", missing);

            Assert.Equal(ExitCode.Input, code);
            Assert.Contains(missing, stderr, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_key_file_that_is_not_a_pem_exits_with_the_input_code()
    {
        using var temp = new TempDirectory();
        (string feedPath, _, ECDsa key) = Arrange(temp, TwoReleaseFeed);
        using (key)
        {
            string junkPath = Path.Combine(temp.Path, "junk.pem");
            File.WriteAllText(junkPath, "这不是 PEM");

            Assert.Equal(ExitCode.Input, Run(out _, out _, "sign", "--feed", feedPath, "--key", junkPath));
        }
    }

    [Fact]
    public void An_rsa_key_exits_with_the_input_code()
    {
        using var temp = new TempDirectory();
        (string feedPath, _, ECDsa key) = Arrange(temp, TwoReleaseFeed);
        using (key)
        {
            using RSA rsa = RSA.Create(2048);
            string rsaPath = Path.Combine(temp.Path, "rsa.pem");
            File.WriteAllText(rsaPath, rsa.ExportPkcs8PrivateKeyPem());

            Assert.Equal(ExitCode.Input, Run(out _, out _, "sign", "--feed", feedPath, "--key", rsaPath));
        }
    }

    [Theory]
    [InlineData("--feed")]
    [InlineData("--key")]
    public void Missing_required_options_exit_with_the_usage_code(string missing)
    {
        using var temp = new TempDirectory();
        (string feedPath, string keyPath, ECDsa key) = Arrange(temp, TwoReleaseFeed);
        using (key)
        {
            List<string> args = ["sign"];
            if (missing != "--feed") { args.AddRange(["--feed", feedPath]); }
            if (missing != "--key") { args.AddRange(["--key", keyPath]); }

            int code = Run(out _, out string stderr, [.. args]);

            Assert.Equal(ExitCode.Usage, code);
            Assert.Contains(missing, stderr, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_bad_entry_is_reported_not_just_the_first()
    {
        using var temp = new TempDirectory();
        string broken = """
            {
              "schemaVersion": 1,
              "releases": [
                { "version": "bad-one", "releasedAt": "2026-09-18T10:00:00Z", "package": { "url": "p.zip", "size": 1, "sha256": "aa" } },
                { "version": "1.0.0", "releasedAt": "2026-09-17T10:00:00Z", "package": { "url": "q.zip", "size": 1, "sha256": "bb" } },
                { "version": "bad-two", "releasedAt": "2026-09-16T10:00:00Z", "package": { "url": "r.zip", "size": 1, "sha256": "cc" } }
              ]
            }
            """;
        (string feedPath, string keyPath, ECDsa key) = Arrange(temp, broken);
        using (key)
        {
            int code = Run(out _, out string stderr, "sign", "--feed", feedPath, "--key", keyPath);

            Assert.Equal(ExitCode.Input, code);
            Assert.Contains("bad-one", stderr, StringComparison.Ordinal);
            Assert.Contains("bad-two", stderr, StringComparison.Ordinal);
            Assert.Equal(broken, File.ReadAllText(feedPath));
        }
    }

    [Fact]
    public void A_feed_without_a_releases_array_exits_with_the_input_code()
    {
        using var temp = new TempDirectory();
        const string noReleases = """{ "schemaVersion": 1, "releases": "nope" }""";
        (string feedPath, string keyPath, ECDsa key) = Arrange(temp, noReleases);
        using (key)
        {
            Assert.Equal(ExitCode.Input, Run(out _, out _, "sign", "--feed", feedPath, "--key", keyPath));
            Assert.Equal(noReleases, File.ReadAllText(feedPath));
        }
    }

    [Fact]
    public void Sign_usage_lists_exactly_the_options_the_command_knows()
    {
        string[] documented =
        [
            .. System.Text.RegularExpressions.Regex.Matches(HelpText.SignUsage, @"^ {2}--([a-z0-9-]+)", System.Text.RegularExpressions.RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value),
        ];

        Assert.Equal(SignCommand.KnownOptions.Order(StringComparer.Ordinal), documented.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Sign_usage_states_the_formatting_trade_off()
    {
        Assert.Contains("2 空格缩进", HelpText.SignUsage, StringComparison.Ordinal);
        Assert.Contains("重新排版", HelpText.SignUsage, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_option_exits_with_the_usage_code()
    {
        using var temp = new TempDirectory();
        (string feedPath, string keyPath, ECDsa key) = Arrange(temp, TwoReleaseFeed);
        using (key)
        {
            int code = Run(out _, out string stderr,
                "sign", "--feed", feedPath, "--key", keyPath, "--generate-key");

            Assert.Equal(ExitCode.Usage, code);
            Assert.Contains("--generate-key", stderr, StringComparison.Ordinal);
        }
    }
}
