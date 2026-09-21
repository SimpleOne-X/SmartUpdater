using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace SimpleOneX.SmartUpdater.Packer;

/// <summary>
/// 主窗口。只做三件事：把控件里的文本收成 <see cref="PackForm"/> / <see cref="SignForm"/>，
/// 把命令放到后台线程执行（<see cref="CommandRunner"/>），把结果回到界面线程写进日志区。
/// 校验、参数组装、结果格式化都在 Logic 目录里，那里没有任何界面依赖，由 Packer.Tests 覆盖。
/// </summary>
internal sealed class MainView
{
    private readonly TextBox _input = new();
    private readonly TextBox _output = new();
    private readonly TextBox _version = new();
    private readonly TextBox _packageName = new();
    private readonly TextBox _channel = new();
    private readonly TextBox _rolloutPercent = new();
    private readonly TextBox _minUpdatableFrom = new();
    private readonly MultiLineTextBox _notes = new();
    private readonly MultiLineTextBox _preserve = new();
    private readonly CheckBox _isMandatory = new();
    private readonly CheckBox _force = new();

    private readonly TextBox _feed = new();
    private readonly TextBox _key = new();
    private readonly CheckBox _resign = new();

    private readonly Button _packButton = new();
    private readonly Button _signButton = new();
    private readonly MultiLineTextBox _log = new();

    private Window? _window;

    /// <summary>构建主窗口。</summary>
    public Window Build()
    {
        _packButton.Content("生成升级包 (pack)").OnClick(OnPack);
        _signButton.Content("签名 (sign)").OnClick(OnSign);
        _isMandatory.Content("强制更新（mandatory）");
        _force.Content("强制替换同版本条目（--force）");
        _resign.Content("重签已有签名的条目（--resign）");
        _version.Text("1.0.0.0");
        _notes.Wrap(true);
        _log.IsReadOnly(true).Wrap(true);

        _window = new Window()
            .Title("SmartUpdater 打包工具")
            .Icon(IconSource.FromResource<MainView>("X-logo.ico"))
            .Resizable(860, 900)
            .Padding(12)
            .Content(
                new StackPanel()
                    .Spacing(8)
                    .Children(
                        new Label().Text("打包 (pack)").FontSize(16).Bold(),
                        PackGrid(),
                        new StackPanel().Horizontal().Spacing(16).Children(_isMandatory, _force),
                        _packButton,
                        new Label().Text("签名 (sign)").FontSize(16).Bold(),
                        SignGrid(),
                        _resign,
                        _signButton,
                        new Label().Text("日志").FontSize(16).Bold(),
                        _log.Height(260)));

        return _window;
    }

    private Grid PackGrid() => new Grid()
        .Columns("130,*,Auto")
        .Rows("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto")
        .Spacing(6)
        .Children(Flatten(
            Field(0, "应用目录", _input, BrowseFolder(_input, "选择已 publish 的应用目录")),
            Field(1, "输出目录", _output, BrowseFolder(_output, "选择输出目录")),
            Field(2, "版本号（四段）", _version),
            Field(3, "包名前缀（可选）", _packageName),
            Field(4, "渠道（可选）", _channel),
            Field(5, "灰度百分比（可选）", _rolloutPercent),
            Field(6, "最低可升级版本（可选）", _minUpdatableFrom),
            Field(7, "更新说明（可选）", _notes.Height(60)),
            Field(8, "preserve（每行一个）", _preserve.Height(60))));

    private Grid SignGrid() => new Grid()
        .Columns("130,*,Auto")
        .Rows("Auto,Auto")
        .Spacing(6)
        .Children(Flatten(
            Field(0, "releases.json", _feed, BrowseFile(_feed, "选择 releases.json")),
            Field(1, "私钥文件 (PEM)", _key, BrowseFile(_key, "选择 ECDSA P-256 私钥"))));

    private static Element[] Flatten(params Element[][] rows) => rows.SelectMany(r => r).ToArray();

    private static Element[] Field(int row, string caption, Element editor, Button? browse = null)
    {
        Element label = new Label().Text(caption).Row(row).Column(0);
        editor.Row(row).Column(1);
        return browse is null ? [label, editor] : [label, editor, browse.Row(row).Column(2)];
    }

    private Button BrowseFolder(TextBox target, string title)
        => new Button().Content("浏览…").OnClick(() =>
        {
            string? picked = FileDialog.SelectFolder(new FolderDialogOptions { Owner = _window, Title = title });
            if (!string.IsNullOrEmpty(picked))
            {
                target.Text = picked;
            }
        });

    private Button BrowseFile(TextBox target, string title)
        => new Button().Content("浏览…").OnClick(() =>
        {
            string? picked = FileDialog.OpenFile(new OpenFileDialogOptions { Owner = _window, Title = title });
            if (!string.IsNullOrEmpty(picked))
            {
                target.Text = picked;
            }
        });

    private void OnPack()
    {
        var form = new PackForm
        {
            InputDirectory = _input.Text,
            OutputDirectory = _output.Text,
            Version = _version.Text,
            PackageName = _packageName.Text,
            Channel = _channel.Text,
            IsMandatory = _isMandatory.IsChecked == true,
            RolloutPercent = _rolloutPercent.Text,
            MinUpdatableFrom = _minUpdatableFrom.Text,
            Notes = _notes.Text,
            Preserve = _preserve.Text,
            Force = _force.IsChecked == true,
        };

        IReadOnlyList<string> problems = FormValidator.Validate(form);
        if (problems.Count > 0)
        {
            AppendLog("[pack] 未执行：\n" + string.Join('\n', problems.Select(p => "  " + p)));
            return;
        }

        Execute("pack", CommandLineBuilder.BuildPack(form), succeeded: () =>
        {
            // pack 成功后把它写出的 feed 预填给 sign，省一次手选。
            if (string.IsNullOrWhiteSpace(_feed.Text))
            {
                _feed.Text = CommandLineBuilder.DefaultFeedPath(form.OutputDirectory);
            }
        });
    }

    private void OnSign()
    {
        var form = new SignForm { FeedPath = _feed.Text, KeyPath = _key.Text, Resign = _resign.IsChecked == true };

        IReadOnlyList<string> problems = FormValidator.Validate(form);
        if (problems.Count > 0)
        {
            AppendLog("[sign] 未执行：\n" + string.Join('\n', problems.Select(p => "  " + p)));
            return;
        }

        Execute("sign", CommandLineBuilder.BuildSign(form), succeeded: null);
    }

    /// <summary>在线程池上跑命令，界面线程只负责禁用按钮与回写日志，所以窗口不会冻结。</summary>
    private void Execute(string command, string[] args, Action? succeeded)
    {
        SetBusy(true);
        AppendLog($"[{command}] 开始执行……");

        IDispatcher dispatcher = Application.Current!.Dispatcher!;
        _ = Task.Run(() =>
        {
            CommandResult result;
            try
            {
                result = CommandRunner.Run(args);
            }
            catch (Exception ex)
            {
                // 命令入口自己会把已知的 IO 错误转成退出码；走到这里说明是没预料到的异常，
                // 不能让它把后台任务静默吞掉、按钮永远禁用。
                result = new CommandResult(ExitCode.Unexpected, string.Empty, ex.Message);
            }

            dispatcher.BeginInvoke(() =>
            {
                AppendLog(ResultFormatter.Format(command, result));
                if (result.ExitCode == ExitCode.Success)
                {
                    succeeded?.Invoke();
                }

                SetBusy(false);
            });
        });
    }

    private void SetBusy(bool busy)
    {
        _packButton.IsEnabled = !busy;
        _signButton.IsEnabled = !busy;
    }

    private void AppendLog(string text)
        => _log.Text = _log.Text.Length == 0 ? text : _log.Text + "\n\n" + text;
}
