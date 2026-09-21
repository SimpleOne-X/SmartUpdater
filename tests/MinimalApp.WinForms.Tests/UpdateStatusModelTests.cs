namespace SimpleOneX.SmartUpdater.Samples.WinForms.Tests;

public sealed class UpdateStatusModelTests
{
    [Fact]
    public void Idle_shows_the_current_version_and_enables_the_check_button()
    {
        UpdateStatusModel model = UpdateStatusModel.Idle(new Version(1, 0, 0));

        Assert.Equal("当前版本 1.0.0，尚未检查更新。", model.StatusText);
        Assert.Equal(0F, model.ProgressValue);
        Assert.Equal("已是最新", model.TagText);
        Assert.Equal(StatusKind.Neutral, model.TagKind);
        Assert.True(model.CheckButtonEnabled);
    }

    [Fact]
    public void Checking_disables_the_check_button()
    {
        UpdateStatusModel model = UpdateStatusModel.Checking(new Version(1, 0, 0));

        Assert.Equal("正在检查更新……", model.StatusText);
        Assert.False(model.CheckButtonEnabled);
    }

    [Fact]
    public void Update_available_names_both_versions()
    {
        UpdateStatusModel model = UpdateStatusModel.Available(new Version(1, 0, 0), new Version(1, 1, 0));

        Assert.Equal("发现新版本 1.1.0（当前 1.0.0）。", model.StatusText);
        Assert.Equal("有更新", model.TagText);
        Assert.Equal(StatusKind.Attention, model.TagKind);
    }

    [Theory]
    [InlineData(0.0, "正在下载 1.1.0：0%")]
    [InlineData(0.425, "正在下载 1.1.0：43%")]
    [InlineData(1.0, "正在下载 1.1.0：100%")]
    public void Download_progress_is_rounded_to_whole_percent(double percent, string expected)
    {
        UpdateStatusModel model = UpdateStatusModel.Progress(UpdatePhase.Download, new Version(1, 1, 0), percent);

        Assert.Equal(expected, model.StatusText);
        Assert.Equal((float)percent, model.ProgressValue);
        Assert.False(model.CheckButtonEnabled);
    }

    [Fact]
    public void Progress_percent_is_clamped_into_zero_one()
    {
        Assert.Equal(1F, UpdateStatusModel.Progress(UpdatePhase.Download, new Version(1, 1, 0), 1.7).ProgressValue);
        Assert.Equal(0F, UpdateStatusModel.Progress(UpdatePhase.Download, new Version(1, 1, 0), -0.3).ProgressValue);
    }

    [Fact]
    public void Progress_percent_that_is_not_a_number_is_treated_as_zero()
    {
        // 0 / 0（总字节数未知）之类的除法会产生 NaN；不能让它流进进度条或被强转成 int。
        UpdateStatusModel model = UpdateStatusModel.Progress(UpdatePhase.Download, new Version(1, 1, 0), double.NaN);

        Assert.Equal(0F, model.ProgressValue);
        Assert.Equal("正在下载 1.1.0：0%", model.StatusText);
    }

    // xunit 的 public 测试方法不能拿 internal 枚举当参数（CS0051），所以用字符串再 Enum.Parse。
    [Theory]
    [InlineData("Verify", "正在校验 1.1.0：50%")]
    [InlineData("Commit", "正在应用 1.1.0：50%")]
    public void Each_phase_has_its_own_wording(string phaseName, string expected)
    {
        UpdatePhase phase = Enum.Parse<UpdatePhase>(phaseName);

        Assert.Equal(expected, UpdateStatusModel.Progress(phase, new Version(1, 1, 0), 0.5).StatusText);
    }

    [Fact]
    public void Mandatory_mode_says_so_and_never_offers_a_choice()
    {
        UpdateStatusModel model = UpdateStatusModel.Mandatory(new Version(1, 1, 0));

        Assert.Equal("强制更新中（1.1.0）……", model.StatusText);
        Assert.Equal("强制更新", model.TagText);
        Assert.False(model.CheckButtonEnabled);
    }

    [Fact]
    public void Postponed_returns_to_idle_wording_but_remembers_the_offer()
    {
        UpdateStatusModel model = UpdateStatusModel.Postponed(new Version(1, 0, 0), new Version(1, 1, 0));

        Assert.Equal("已推迟更新到 1.1.0，下次启动再问。", model.StatusText);
        Assert.True(model.CheckButtonEnabled);
    }

    [Fact]
    public void Skipped_says_which_version_was_skipped()
    {
        UpdateStatusModel model = UpdateStatusModel.Skipped(new Version(1, 0, 0), new Version(1, 1, 0));

        Assert.Equal("已跳过版本 1.1.0。", model.StatusText);
        Assert.True(model.CheckButtonEnabled);
    }

    [Fact]
    public void Failure_keeps_the_app_usable_and_shows_the_stage()
    {
        UpdateStatusModel model = UpdateStatusModel.Failed("Download", "连接被重置");

        Assert.Equal("更新失败（Download）：连接被重置。程序可继续使用。", model.StatusText);
        Assert.Equal(StatusKind.Bad, model.TagKind);
        // 更新失败不能让主功能陪葬。
        Assert.True(model.CheckButtonEnabled);
    }

    [Fact]
    public void Just_updated_status_bar_text_uses_the_expected_wording()
    {
        // 新版本启动后在状态栏显示「已从 X 更新到 Y」，不弹窗。
        Assert.Equal(
            "已从 1.0.0 更新到 1.1.0",
            UpdateStatusModel.JustUpdatedStatusBar(new Version(1, 0, 0), new Version(1, 1, 0)));
    }

    [Fact]
    public void Just_updated_status_bar_tolerates_an_unknown_from_version()
    {
        Assert.Equal(
            "已更新到 1.1.0",
            UpdateStatusModel.JustUpdatedStatusBar(null, new Version(1, 1, 0)));
    }
}
