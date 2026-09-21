namespace SimpleOneX.SmartUpdater.Packer;

/// <summary>命令行的帮助文本。参数出错时打印 <see cref="Usage"/>；<c>help</c> 命令打印 <see cref="Full"/>。</summary>
internal static class HelpText
{
    /// <summary>两个子命令的概览与选项写法。</summary>
    public static string Usage { get; } = """
        用法：smartupdater <命令> [选项]

        命令：
          pack    把已 publish 的应用目录打成升级包，并新建或更新 releases.json
          sign    给 releases.json 里的条目签名（需要私钥，应在能访问私钥的机器上单独执行）
          help    显示本说明（含各命令的完整选项）

        选项写法：--name value 或 --name=value；选项名只能是 kebab-case（小写字母、数字、连字符）。
        后面没有跟值的选项是开关（如 --force），开关不接受值；只有标明"可重复"的选项（如 --preserve）才能出现多次。
        注意：选项的值不能以 -- 开头。需要这样的值时写成 --notes=--例外 的形式。

        退出码：0 成功；1 用法错误（命令或选项写错了）；2 输入有问题（文件缺失、内容非法）；
                3 冲突（如同版本内容不同，需要 --force）；4 意外错误。
        """;

    /// <summary><c>pack</c> 的完整选项表。</summary>
    public static string PackUsage { get; } = """
        smartupdater pack —— 生成升级包，并新建或更新 releases.json

        用法：smartupdater pack --input <dir> --version <v> --output <dir> [选项]

        必填：
          --input <dir>                       已 publish 的应用目录
          --version <v>                       2~4 段版本号，如 1.2.4
          --output <dir>                      发布目录；不存在则创建

        可选：
          --package-name <name>               zip 文件名的前缀；默认取输入目录根部唯一 *.exe 的基名
          --channel <name>                    写进 feed 的 channel；默认 stable
          --min-updatable-from <v>            阶梯升级门槛；默认不写该字段
          --mode optional|mandatory           默认 optional
          --rollout-percent <0-100>           灰度百分比；默认 100
          --notes <text>                      更新说明，与 --notes-file 互斥；默认不写该字段
                                              （值以 -- 开头时写成 --notes=--例外）
          --notes-file <path>                 从文件读入更新说明（UTF-8，去掉 BOM），与 --notes 互斥
          --preserve <glob>                   命中的文件标为 preserve（升级时不覆盖）；可重复
                                              glob 支持 * ? **，不支持 [] 与 {}；用正斜杠，不区分大小写
          --poll-interval-seconds <n>         feed 的 client 段；默认不写
          --jitter-window-seconds <n>         同上
          --heartbeat-interval-seconds <n>    同上
          --released-at <iso8601>             默认当前 UTC 时间（秒精度）；给定后输出可复现
          --force                             允许替换"同版本但内容不同"的条目，也允许改写 feed 里已有的 channel；
                                              替换后旧条目的 signature 随之丢弃，需要重新 sign

        说明：
          同版本、内容完全相同的重复打包是幂等的：已有条目原样保留（包括它的 signature），不需要 --force。
          同一份 releases.json 里不允许同时出现 1.2.4 与 1.2.4.0（--force 也不放行）：它们是两个不同的版本，
          灰度分桶也不同。本命令只检查"本次版本"与已有条目是否混用，不检查已有条目彼此之间。
        """;

    /// <summary><c>sign</c> 的完整选项表。</summary>
    public static string SignUsage { get; } = """
        smartupdater sign —— 给 releases.json 里的条目签名

        用法：smartupdater sign --feed <releases.json> --key <private.pem> [选项]

        必填：
          --feed <releases.json>              要签名的 feed 文件
          --key <private.pem>                 ECDSA P-256 私钥，PKCS#8 或 SEC1 PEM

        可选：
          --resign                            连已有 signature 的条目一起重签；默认只签尚未签名的条目

        说明：
          只改 signature 字段，其余字段与未知字段逐字保留；写回时空白归一到 Packer 的写出设置（2 空格缩进、放宽转义）。
          pack 产出的 feed 经 sign 后除新增的 signature 行外逐字不变；手工编辑过的 feed 会被重新排版。
          任一条目无法签名则整体失败、feed 不改动。签名成功后打印公钥（base64 SPKI），配到 UpdateClientOptions.PublicKey。
          不做密钥管理，也不提供密钥生成命令。
        """;

    /// <summary><c>help</c> 命令的完整输出：概览加上各命令的选项表。</summary>
    public static string Full { get; } = string.Join("\n\n", Usage, PackUsage, SignUsage);
}
