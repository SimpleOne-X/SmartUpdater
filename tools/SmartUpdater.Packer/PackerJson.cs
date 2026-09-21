using System.Text.Encodings.Web;
using System.Text.Json;

namespace SimpleOneX.SmartUpdater.Packer;

/// <summary>Packer 写出 JSON 的统一设置，feed 与 manifest 共用。</summary>
internal static class PackerJson
{
    /// <summary>
    /// 必须基于 <see cref="SmartUpdaterJsonContext"/>.Default.Options 复制：源生成上下文的
    /// <c>[JsonSourceGenerationOptions]</c>（camelCase 命名、忽略 null、Version 转换器）只作用于 Default 实例，
    /// 直接 new 一份空 options 会静默退回 PascalCase，客户端读不出这样的 manifest。
    /// <para>默认编码器会把中文 notes 转成 \uXXXX、把 + 转成 \u002B，让 feed 既难读又难 diff，这里放宽转义。
    /// 签名不受影响：签名覆盖的是 ReleaseSignature 定义的规范化 JSON，不是 feed 文件的字节。</para>
    /// </summary>
    public static JsonSerializerOptions Write { get; } = new(SmartUpdaterJsonContext.Default.Options)
    {
        WriteIndented = true,
        IndentCharacter = ' ',
        IndentSize = 2,

        // 缩进写出时的换行默认取 Environment.NewLine：Windows 上是 \r\n、Linux 上是 \n，
        // 同一输入在两个平台会打出字节不同的 manifest（进而不同的包哈希）。固定成 \n。
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>先写同目录临时文件再替换，避免写到一半的 releases.json 被客户端读到。</summary>
    public static void WriteAtomic(string path, ReadOnlySpan<byte> content)
    {
        string temp = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理是尽力而为：不能让它盖住真正的写入失败。
        }
    }
}
