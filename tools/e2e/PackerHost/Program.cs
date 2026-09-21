using System.Reflection;

// 用法：PackerHost pack|sign <选项…>。
// Packer 的引擎类型是 internal，这里用反射调用，因此不需要改 Packer 的可见性，也不给 Packer 增加命令行入口。
const string Namespace = "SimpleOneX.SmartUpdater.Packer.";
const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

try
{
    Assembly packer = Assembly.Load("SmartUpdater.Packer");

    Type parser = packer.GetType(Namespace + "ArgumentParser", throwOnError: true)!;
    object?[] parseArgs = [args, null, null];
    bool parsedOk = (bool)parser.GetMethod("TryParse", Static)!.Invoke(null, parseArgs)!;
    if (!parsedOk)
    {
        Console.Error.WriteLine(parseArgs[2]);
        return 1;
    }

    object parsed = parseArgs[1]!;
    string command = (string)parsed.GetType().GetProperty("Command")!.GetValue(parsed)!;
    string? engine = command switch
    {
        "pack" => "PackCommand",
        "sign" => "SignCommand",
        _ => null,
    };
    if (engine is null)
    {
        Console.Error.WriteLine($"PackerHost 只支持 pack / sign，收到 '{command}'。");
        return 1;
    }

    MethodInfo run = packer.GetType(Namespace + engine, throwOnError: true)!.GetMethod("Run", Static)!;
    return (int)run.Invoke(null, [parsed, Console.Out, Console.Error])!;
}
catch (TargetInvocationException ex)
{
    Console.Error.WriteLine(ex.InnerException ?? ex);
    return 4;
}
