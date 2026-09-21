using System.Text.Json;
using System.Text.Json.Nodes;

namespace SimpleOneX.SmartUpdater.Packer.Tests;

public sealed class FeedWriterTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 9, 18, 10, 0, 0, TimeSpan.Zero);

    private static FeedEntryInput Entry(
        string version,
        string sha256 = "aa",
        long size = 100,
        UpdateMode mode = UpdateMode.Optional,
        int rolloutPercent = 100,
        string? notes = null,
        string? minUpdatableFrom = null)
        => new(
            new Version(version),
            Stamp,
            $"packages/MyApp-{version}.zip",
            size,
            sha256,
            minUpdatableFrom is null ? null : new Version(minUpdatableFrom),
            mode,
            rolloutPercent,
            notes);

    private static FeedUpdateOptions Options(bool force = false, string? channel = "stable")
        => new(channel, null, null, null, force);

    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void New_feed_has_the_documented_shape()
    {
        string json = FeedWriter.Upsert(existingJson: null, Entry("1.2.4"), Options());
        JsonNode feed = Parse(json);

        Assert.Equal(1, feed["schemaVersion"]!.GetValue<int>());
        Assert.Equal("stable", feed["channel"]!.GetValue<string>());
        JsonNode release = feed["releases"]!.AsArray()[0]!;
        Assert.Equal("1.2.4", release["version"]!.GetValue<string>());
        Assert.Equal("2026-09-18T10:00:00Z", release["releasedAt"]!.GetValue<string>());
        Assert.Equal("packages/MyApp-1.2.4.zip", release["package"]!["url"]!.GetValue<string>());
        Assert.Equal(100, release["package"]!["size"]!.GetValue<long>());
        Assert.Equal("aa", release["package"]!["sha256"]!.GetValue<string>());
        Assert.Equal("optional", release["mode"]!.GetValue<string>());
        Assert.Equal(100, release["rolloutPercent"]!.GetValue<int>());
    }

    [Fact]
    public void Optional_fields_are_omitted_when_not_given()
    {
        JsonNode release = Parse(FeedWriter.Upsert(null, Entry("1.2.4"), Options()))["releases"]!.AsArray()[0]!;

        Assert.Null(release["minUpdatableFrom"]);
        Assert.Null(release["notes"]);
        Assert.Null(release["signature"]);
    }

    [Fact]
    public void Optional_fields_are_written_when_given()
    {
        FeedEntryInput entry = Entry("1.2.4", notes: "修复若干问题", minUpdatableFrom: "1.0.0");

        JsonNode release = Parse(FeedWriter.Upsert(null, entry, Options()))["releases"]!.AsArray()[0]!;

        Assert.Equal("1.0.0", release["minUpdatableFrom"]!.GetValue<string>());
        Assert.Equal("修复若干问题", release["notes"]!.GetValue<string>());
    }

    [Fact]
    public void Chinese_notes_are_not_escaped()
    {
        string json = FeedWriter.Upsert(null, Entry("1.2.4", notes: "修复若干问题"), Options());

        Assert.Contains("修复若干问题", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Releases_are_sorted_by_version_descending()
    {
        string json = FeedWriter.Upsert(null, Entry("1.2.3"), Options());
        json = FeedWriter.Upsert(json, Entry("1.10.0"), Options());
        json = FeedWriter.Upsert(json, Entry("1.9.0"), Options());

        string[] versions =
        [
            .. Parse(json)["releases"]!.AsArray().Select(r => r!["version"]!.GetValue<string>()),
        ];

        // 版本比较，不是字符串比较：1.10.0 > 1.9.0。
        Assert.Equal(["1.10.0", "1.9.0", "1.2.3"], versions);
    }

    [Fact]
    public void Repacking_identical_content_is_idempotent_without_force()
    {
        string json = FeedWriter.Upsert(null, Entry("1.2.4"), Options());
        string again = FeedWriter.Upsert(json, Entry("1.2.4"), Options());

        Assert.Single(Parse(again)["releases"]!.AsArray());
    }

    [Fact]
    public void Repacking_identical_content_ignores_released_at()
    {
        string json = FeedWriter.Upsert(null, Entry("1.2.4"), Options());

        FeedEntryInput later = Entry("1.2.4") with { ReleasedAt = Stamp.AddDays(1) };
        string again = FeedWriter.Upsert(json, later, Options());

        Assert.Single(Parse(again)["releases"]!.AsArray());
    }

    [Fact]
    public void Repacking_identical_content_keeps_an_existing_signature()
    {
        string json = FeedWriter.Upsert(null, Entry("1.2.4"), Options());
        JsonNode signed = Parse(json);
        signed["releases"]!.AsArray()[0]!["signature"] = "c2ln";

        string again = FeedWriter.Upsert(signed.ToJsonString(PackerJson.Write), Entry("1.2.4"), Options());

        Assert.Equal("c2ln", Parse(again)["releases"]!.AsArray()[0]!["signature"]!.GetValue<string>());
    }

    [Fact]
    public void Different_content_for_the_same_version_needs_force()
    {
        string json = FeedWriter.Upsert(null, Entry("1.2.4", sha256: "aa"), Options());

        FeedConflictException error = Assert.Throws<FeedConflictException>(
            () => FeedWriter.Upsert(json, Entry("1.2.4", sha256: "bb"), Options()));

        Assert.Contains("1.2.4", error.Message, StringComparison.Ordinal);
        Assert.Contains("--force", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Force_replaces_the_conflicting_entry()
    {
        string json = FeedWriter.Upsert(null, Entry("1.2.4", sha256: "aa"), Options());

        string replaced = FeedWriter.Upsert(json, Entry("1.2.4", sha256: "bb"), Options(force: true));

        JsonArray releases = Parse(replaced)["releases"]!.AsArray();
        Assert.Single(releases);
        Assert.Equal("bb", releases[0]!["package"]!["sha256"]!.GetValue<string>());
    }

    [Fact]
    public void Force_drops_a_stale_signature_when_the_content_changed()
    {
        string json = FeedWriter.Upsert(null, Entry("1.2.4", sha256: "aa"), Options());
        JsonNode signed = Parse(json);
        signed["releases"]!.AsArray()[0]!["signature"] = "c2ln";

        string replaced = FeedWriter.Upsert(
            signed.ToJsonString(PackerJson.Write), Entry("1.2.4", sha256: "bb"), Options(force: true));

        // 内容变了，旧签名一定对不上；留着它等于发布一个必然验签失败的条目。
        Assert.Null(Parse(replaced)["releases"]!.AsArray()[0]!["signature"]);
    }

    [Fact]
    public void Mixing_three_and_four_segment_forms_of_the_same_version_is_rejected()
    {
        string json = FeedWriter.Upsert(null, Entry("1.2.4"), Options());

        FeedConflictException error = Assert.Throws<FeedConflictException>(
            () => FeedWriter.Upsert(json, Entry("1.2.4.0"), Options()));

        Assert.Contains("1.2.4", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Force_does_not_allow_mixing_version_forms()
    {
        string json = FeedWriter.Upsert(null, Entry("1.2.4"), Options());

        Assert.Throws<FeedConflictException>(
            () => FeedWriter.Upsert(json, Entry("1.2.4.0"), Options(force: true)));
    }

    [Fact]
    public void Channel_mismatch_is_rejected()
    {
        string json = FeedWriter.Upsert(null, Entry("1.2.4"), Options(channel: "stable"));

        Assert.Throws<FeedConflictException>(
            () => FeedWriter.Upsert(json, Entry("1.2.5"), Options(channel: "beta")));
    }

    [Fact]
    public void Client_segment_is_written_when_given()
    {
        string json = FeedWriter.Upsert(
            null, Entry("1.2.4"), new FeedUpdateOptions("stable", 300, 600, 21600, Force: false));

        JsonNode client = Parse(json)["client"]!;
        Assert.Equal(300, client["pollIntervalSeconds"]!.GetValue<int>());
        Assert.Equal(600, client["jitterWindowSeconds"]!.GetValue<int>());
        Assert.Equal(21600, client["heartbeatIntervalSeconds"]!.GetValue<int>());
    }

    [Fact]
    public void Client_segment_is_absent_when_not_given()
        => Assert.Null(Parse(FeedWriter.Upsert(null, Entry("1.2.4"), Options()))["client"]);

    [Fact]
    public void Existing_client_segment_survives_a_pack_that_does_not_mention_it()
    {
        string json = FeedWriter.Upsert(
            null, Entry("1.2.4"), new FeedUpdateOptions("stable", 300, null, null, Force: false));

        string next = FeedWriter.Upsert(json, Entry("1.2.5"), Options());

        Assert.Equal(300, Parse(next)["client"]!["pollIntervalSeconds"]!.GetValue<int>());
    }

    [Fact]
    public void Unknown_fields_in_the_existing_feed_are_preserved()
    {
        string existing = """
            {
              "schemaVersion": 1,
              "channel": "stable",
              "vendorNote": "内部备注",
              "releases": [
                {
                  "version": "1.2.3",
                  "releasedAt": "2026-09-17T10:00:00Z",
                  "package": { "url": "packages/MyApp-1.2.3.zip", "size": 90, "sha256": "bb" },
                  "ticketId": "OPS-42"
                }
              ]
            }
            """;

        JsonNode feed = Parse(FeedWriter.Upsert(existing, Entry("1.2.4"), Options()));

        Assert.Equal("内部备注", feed["vendorNote"]!.GetValue<string>());
        JsonNode older = feed["releases"]!.AsArray().Single(r => r!["version"]!.GetValue<string>() == "1.2.3")!;
        Assert.Equal("OPS-42", older["ticketId"]!.GetValue<string>());
    }

    [Fact]
    public void Malformed_existing_feed_is_rejected()
        => Assert.Throws<JsonException>(() => FeedWriter.Upsert("{ broken", Entry("1.2.4"), Options()));

    [Fact]
    public void Output_is_stable_for_the_same_inputs()
    {
        string a = FeedWriter.Upsert(null, Entry("1.2.4"), Options());
        string b = FeedWriter.Upsert(null, Entry("1.2.4"), Options());

        Assert.Equal(a, b);
    }

    // ---- 辅助方法与边界行为 ----

    private static string[] Names(JsonNode node) => [.. node.AsObject().Select(p => p.Key)];

    private static string[] Versions(string json)
        => [.. Parse(json)["releases"]!.AsArray().Select(r => r!["version"]!.GetValue<string>())];

    [Fact]
    public void New_feed_writes_top_level_fields_in_the_documented_order()
    {
        string json = FeedWriter.Upsert(
            null, Entry("1.2.4"), new FeedUpdateOptions("stable", 300, null, null, Force: false));

        Assert.Equal(["schemaVersion", "channel", "client", "releases"], Names(Parse(json)));
    }

    [Fact]
    public void New_entry_writes_fields_in_the_documented_order()
    {
        JsonNode release = Parse(FeedWriter.Upsert(
            null, Entry("1.2.4", notes: "n", minUpdatableFrom: "1.0.0"), Options()))["releases"]!.AsArray()[0]!;

        Assert.Equal(
            ["version", "releasedAt", "package", "minUpdatableFrom", "mode", "rolloutPercent", "notes"],
            Names(release));
        Assert.Equal(["url", "size", "sha256"], Names(release["package"]!));
    }

    [Fact]
    public void Mandatory_mode_and_rollout_percent_are_written()
    {
        JsonNode release = Parse(FeedWriter.Upsert(
            null, Entry("1.2.4", mode: UpdateMode.Mandatory, rolloutPercent: 5), Options()))["releases"]!.AsArray()[0]!;

        Assert.Equal("mandatory", release["mode"]!.GetValue<string>());
        Assert.Equal(5, release["rolloutPercent"]!.GetValue<int>());
    }

    [Fact]
    public void Released_at_is_written_in_utc_whatever_the_offset()
    {
        FeedEntryInput plusEight = Entry("1.2.4") with
        {
            ReleasedAt = new DateTimeOffset(2026, 9, 18, 18, 0, 0, TimeSpan.FromHours(8)),
        };

        JsonNode release = Parse(FeedWriter.Upsert(null, plusEight, Options()))["releases"]!.AsArray()[0]!;

        Assert.Equal("2026-09-18T10:00:00Z", release["releasedAt"]!.GetValue<string>());
    }

    [Fact]
    public void Output_uses_lf_line_endings_only()
    {
        string json = FeedWriter.Upsert(null, Entry("1.2.4"), Options());
        json = FeedWriter.Upsert(json, Entry("1.2.5"), Options());

        Assert.Contains('\n', json);
        Assert.DoesNotContain('\r', json);
    }

    // ---- 幂等比较：同版本比较规定的每个字段都得真的参与比较 ----

    private static FeedEntryInput Changed(string field) => field switch
    {
        "url" => Entry("1.2.4") with { PackageUrl = "packages/Other-1.2.4.zip" },
        "size" => Entry("1.2.4", size: 101),
        "sha256" => Entry("1.2.4", sha256: "bb"),
        "mode" => Entry("1.2.4", mode: UpdateMode.Mandatory),
        "rolloutPercent" => Entry("1.2.4", rolloutPercent: 50),
        "minUpdatableFrom" => Entry("1.2.4", minUpdatableFrom: "1.0.0"),
        "notes" => Entry("1.2.4", notes: "changed"),
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };

    [Theory]
    [InlineData("url")]
    [InlineData("size")]
    [InlineData("sha256")]
    [InlineData("mode")]
    [InlineData("rolloutPercent")]
    [InlineData("minUpdatableFrom")]
    [InlineData("notes")]
    public void A_change_in_any_compared_field_is_a_conflict(string field)
    {
        string json = FeedWriter.Upsert(null, Entry("1.2.4"), Options());

        Assert.Throws<FeedConflictException>(() => FeedWriter.Upsert(json, Changed(field), Options()));
    }

    [Fact]
    public void Dropping_optional_fields_that_the_existing_entry_has_is_a_conflict()
    {
        string json = FeedWriter.Upsert(
            null, Entry("1.2.4", notes: "n", minUpdatableFrom: "1.0.0"), Options());

        Assert.Throws<FeedConflictException>(() => FeedWriter.Upsert(json, Entry("1.2.4", notes: "n"), Options()));
        Assert.Throws<FeedConflictException>(() => FeedWriter.Upsert(json, Entry("1.2.4", minUpdatableFrom: "1.0.0"), Options()));
    }

    [Fact]
    public void Missing_mode_and_rollout_in_an_existing_entry_mean_their_defaults()
    {
        // 运维手写、没带 mode / rolloutPercent 的已签名条目：语义上与"optional / 100"相同，不该逼人用 --force 丢签名。
        const string existing = """
            {
              "schemaVersion": 1,
              "channel": "stable",
              "releases": [
                {
                  "version": "1.2.4",
                  "releasedAt": "2026-09-01T00:00:00Z",
                  "package": { "url": "packages/MyApp-1.2.4.zip", "size": 100, "sha256": "aa" },
                  "signature": "c2ln"
                }
              ]
            }
            """;

        JsonNode release = Parse(FeedWriter.Upsert(existing, Entry("1.2.4"), Options()))["releases"]!.AsArray().Single()!;

        Assert.Equal("c2ln", release["signature"]!.GetValue<string>());
        Assert.Equal("2026-09-01T00:00:00Z", release["releasedAt"]!.GetValue<string>());
    }

    [Fact]
    public void Mode_is_compared_ignoring_case()
    {
        // 客户端读 mode 不分大小写，"Mandatory" 与 "mandatory" 是同一个模式。
        string json = FeedWriter.Upsert(null, Entry("1.2.4", mode: UpdateMode.Mandatory), Options())
            .Replace("\"mandatory\"", "\"Mandatory\"", StringComparison.Ordinal);

        string again = FeedWriter.Upsert(json, Entry("1.2.4", mode: UpdateMode.Mandatory), Options());

        Assert.Contains("\"Mandatory\"", again, StringComparison.Ordinal);
    }

    [Fact]
    public void An_existing_field_of_the_wrong_type_is_not_equal_to_anything()
    {
        // size 写成了字符串：不能被当成"缺失"而悄悄判成相同。
        string json = FeedWriter.Upsert(null, Entry("1.2.4"), Options())
            .Replace("\"size\": 100", "\"size\": \"100\"", StringComparison.Ordinal);

        Assert.Throws<FeedConflictException>(() => FeedWriter.Upsert(json, Entry("1.2.4"), Options()));
    }

    [Fact]
    public void Adding_a_version_leaves_older_signed_entries_untouched()
    {
        string json = FeedWriter.Upsert(null, Entry("1.2.3"), Options());
        JsonNode signed = Parse(json);
        signed["releases"]!.AsArray()[0]!["signature"] = "c2ln";

        string next = FeedWriter.Upsert(signed.ToJsonString(PackerJson.Write), Entry("1.2.4"), Options());

        JsonNode older = Parse(next)["releases"]!.AsArray().Single(r => r!["version"]!.GetValue<string>() == "1.2.3")!;
        Assert.Equal("c2ln", older["signature"]!.GetValue<string>());
    }

    // ---- 同版本重复出现 ----

    [Fact]
    public void Duplicate_entries_of_the_same_version_need_force_and_collapse_to_one()
    {
        string json = FeedWriter.Upsert(null, Entry("1.2.4"), Options());
        JsonNode doubled = Parse(json);
        doubled["releases"]!.AsArray().Add(doubled["releases"]![0]!.DeepClone());
        string doubledJson = doubled.ToJsonString(PackerJson.Write);

        Assert.Throws<FeedConflictException>(() => FeedWriter.Upsert(doubledJson, Entry("1.2.4"), Options()));

        string fixedUp = FeedWriter.Upsert(doubledJson, Entry("1.2.4"), Options(force: true));
        Assert.Equal(["1.2.4"], Versions(fixedUp));
    }

    // ---- 1.2.4 与 1.2.4.0 冲突的两个方向与两位版本 ----

    [Theory]
    [InlineData("1.2.4.0", "1.2.4")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("1.2", "1.2.0.0")]
    [InlineData("1.2.0", "1.2")]
    public void Mixing_version_forms_is_rejected_in_both_directions(string existing, string incoming)
    {
        string json = FeedWriter.Upsert(null, Entry(existing), Options());

        Assert.Throws<FeedConflictException>(() => FeedWriter.Upsert(json, Entry(incoming), Options(force: true)));
    }

    [Fact]
    public void Different_versions_that_merely_look_alike_are_not_mixing()
    {
        string json = FeedWriter.Upsert(null, Entry("1.2.4"), Options());

        string next = FeedWriter.Upsert(json, Entry("1.2.4.1"), Options());

        Assert.Equal(["1.2.4.1", "1.2.4"], Versions(next));
    }

    // ---- channel ----

    [Fact]
    public void Missing_channel_option_means_stable_for_a_new_feed()
    {
        string json = FeedWriter.Upsert(null, Entry("1.2.4"), Options(channel: null));

        Assert.Equal("stable", Parse(json)["channel"]!.GetValue<string>());
    }

    [Fact]
    public void Missing_channel_option_means_stable_for_an_existing_feed_too()
    {
        string beta = FeedWriter.Upsert(null, Entry("1.2.4"), Options(channel: "beta"));

        FeedConflictException error = Assert.Throws<FeedConflictException>(
            () => FeedWriter.Upsert(beta, Entry("1.2.5"), Options(channel: null)));

        Assert.Contains("--channel", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Force_rewrites_a_mismatching_channel()
    {
        string beta = FeedWriter.Upsert(null, Entry("1.2.4"), Options(channel: "beta"));

        string next = FeedWriter.Upsert(beta, Entry("1.2.5"), Options(force: true, channel: "stable"));

        Assert.Equal("stable", Parse(next)["channel"]!.GetValue<string>());
    }

    [Fact]
    public void A_feed_without_a_channel_field_is_not_a_mismatch_and_gets_one()
    {
        const string existing = """{ "schemaVersion": 1, "releases": [] }""";

        JsonNode feed = Parse(FeedWriter.Upsert(existing, Entry("1.2.4"), Options(channel: "beta")));

        Assert.Equal("beta", feed["channel"]!.GetValue<string>());
    }

    // ---- client 段 ----

    [Fact]
    public void Client_parameters_override_only_the_given_fields()
    {
        string json = FeedWriter.Upsert(
            null, Entry("1.2.4"), new FeedUpdateOptions("stable", 300, 600, null, Force: false));

        string next = FeedWriter.Upsert(
            json, Entry("1.2.5"), new FeedUpdateOptions("stable", 900, null, 21600, Force: false));

        JsonNode client = Parse(next)["client"]!;
        Assert.Equal(900, client["pollIntervalSeconds"]!.GetValue<int>());
        Assert.Equal(600, client["jitterWindowSeconds"]!.GetValue<int>());
        Assert.Equal(21600, client["heartbeatIntervalSeconds"]!.GetValue<int>());
    }

    [Fact]
    public void Client_parameters_still_apply_when_the_entry_is_an_identical_repack()
    {
        string json = FeedWriter.Upsert(null, Entry("1.2.4"), Options());

        string next = FeedWriter.Upsert(
            json, Entry("1.2.4"), new FeedUpdateOptions("stable", 300, null, null, Force: false));

        Assert.Equal(300, Parse(next)["client"]!["pollIntervalSeconds"]!.GetValue<int>());
    }

    [Fact]
    public void A_new_client_segment_goes_before_releases_in_an_existing_feed()
    {
        string json = FeedWriter.Upsert(null, Entry("1.2.4"), Options());

        string next = FeedWriter.Upsert(
            json, Entry("1.2.5"), new FeedUpdateOptions("stable", 300, null, null, Force: false));

        Assert.Equal(["schemaVersion", "channel", "client", "releases"], Names(Parse(next)));
    }

    [Fact]
    public void A_client_field_that_is_not_an_object_is_rejected_when_client_parameters_are_given()
    {
        const string existing = """{ "schemaVersion": 1, "channel": "stable", "client": 5, "releases": [] }""";

        Assert.Throws<JsonException>(() => FeedWriter.Upsert(
            existing, Entry("1.2.4"), new FeedUpdateOptions("stable", 300, null, null, Force: false)));
    }

    // ---- 已有 feed 的结构 ----

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    public void An_existing_feed_whose_top_level_is_not_an_object_is_rejected(string existing)
        => Assert.Throws<JsonException>(() => FeedWriter.Upsert(existing, Entry("1.2.4"), Options()));

    [Fact]
    public void An_existing_feed_whose_releases_is_not_an_array_is_rejected()
        => Assert.Throws<JsonException>(() => FeedWriter.Upsert(
            """{ "schemaVersion": 1, "releases": {} }""", Entry("1.2.4"), Options()));

    [Fact]
    public void An_existing_feed_without_releases_gets_one()
    {
        JsonNode feed = Parse(FeedWriter.Upsert("""{ "schemaVersion": 1 }""", Entry("1.2.4"), Options()));

        Assert.Equal(["1.2.4"], feed["releases"]!.AsArray().Select(r => r!["version"]!.GetValue<string>()));
    }

    [Fact]
    public void An_empty_existing_feed_text_is_rejected_not_treated_as_new()
        => Assert.Throws<JsonException>(() => FeedWriter.Upsert("", Entry("1.2.4"), Options()));

    [Fact]
    public void Entries_with_unparseable_versions_are_kept_after_the_sorted_ones_in_their_original_order()
    {
        const string existing = """
            {
              "schemaVersion": 1,
              "channel": "stable",
              "releases": [
                { "version": "next-gen" },
                "stray",
                { "version": "1.0.0", "package": { "url": "u", "size": 1, "sha256": "s" } },
                { "note": "no version at all" }
              ]
            }
            """;

        JsonArray releases = Parse(FeedWriter.Upsert(existing, Entry("1.2.4"), Options()))["releases"]!.AsArray();

        string[] shape =
        [
            .. releases.Select(r => r is JsonObject o && o["version"] is not null
                ? o["version"]!.GetValue<string>()
                : r?.ToJsonString() ?? "null"),
        ];
        Assert.Equal(["1.2.4", "1.0.0", "next-gen", "\"stray\"", """{"note":"no version at all"}"""], shape);
    }

    [Fact]
    public void Unknown_values_are_preserved_verbatim_including_number_formatting()
    {
        const string existing = """
            { "schemaVersion": 1, "channel": "stable", "weight": 1.50, "big": 12345678901234567890, "vendorNote": "内部备注", "releases": [] }
            """;

        string json = FeedWriter.Upsert(existing, Entry("1.2.4"), Options());

        Assert.Contains("\"weight\": 1.50", json, StringComparison.Ordinal);
        Assert.Contains("\"big\": 12345678901234567890", json, StringComparison.Ordinal);
        Assert.Contains("\"vendorNote\": \"内部备注\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Force_replacement_keeps_top_level_unknown_fields()
    {
        string json = FeedWriter.Upsert(null, Entry("1.2.4", sha256: "aa"), Options());
        JsonNode edited = Parse(json);
        edited["vendorNote"] = "内部备注";

        string replaced = FeedWriter.Upsert(
            edited.ToJsonString(PackerJson.Write), Entry("1.2.4", sha256: "bb"), Options(force: true));

        Assert.Equal("内部备注", Parse(replaced)["vendorNote"]!.GetValue<string>());
        Assert.Equal(["1.2.4"], Versions(replaced));
    }
}
