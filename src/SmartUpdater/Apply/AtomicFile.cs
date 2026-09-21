using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace SimpleOneX.SmartUpdater;

/// <summary>写临时文件 → flush 到磁盘 → 原子改名。任何一步崩溃，目标文件要么是旧内容要么是新内容。</summary>
internal static class AtomicFile
{
    /// <summary>临时文件后缀。读取时忽略，下一次写入前删除。</summary>
    public const string TempSuffix = ".tmp";

    /// <summary>
    /// 原子地把 <paramref name="content"/> 写成 <paramref name="path"/>。固定四步：
    /// 删除陈旧的临时文件 → 写临时文件 → flush 到磁盘 → 改名覆盖目标。
    /// 临时文件与目标同目录同卷，NTFS 上的改名是原子的，所以目标永远不会是残缺内容。
    /// </summary>
    public static void Write(IFileOperations fs, string path, ReadOnlySpan<byte> content)
    {
        string temp = path + TempSuffix;

        fs.Delete(temp);

        using (Stream stream = fs.OpenWrite(temp))
        {
            stream.Write(content);
            fs.FlushToDisk(stream);
        }

        fs.Move(temp, path, overwrite: true);
    }

    /// <summary>读取整个文件。文件不存在返回 null。临时文件不参与读取。</summary>
    public static byte[]? ReadAllBytes(IFileOperations fs, string path)
    {
        if (!fs.FileExists(path))
        {
            return null;
        }

        using Stream stream = fs.OpenRead(path);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>用源生成的类型信息把 <paramref name="value"/> 序列化为 UTF-8 JSON，并原子写入。</summary>
    public static void WriteJson<T>(IFileOperations fs, string path, T value, JsonTypeInfo<T> typeInfo)
        => Write(fs, path, JsonSerializer.SerializeToUtf8Bytes(value, typeInfo));

    /// <summary>不存在返回 null；内容损坏时 <see cref="JsonException"/> 原样抛出，由调用方决定处置。</summary>
    public static T? ReadJson<T>(IFileOperations fs, string path, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        byte[]? bytes = ReadAllBytes(fs, path);
        return bytes is null ? null : JsonSerializer.Deserialize(SkipUtf8Bom(bytes), typeInfo);
    }

    /// <summary>跳过开头的 UTF-8 BOM（EF BB BF）。旧版记事本保存的合法 JSON / 文本带 BOM，System.Text.Json 会因此拒绝整份内容。</summary>
    public static ReadOnlySpan<byte> SkipUtf8Bom(ReadOnlySpan<byte> bytes)
        => bytes.StartsWith("﻿"u8) ? bytes[3..] : bytes;
}
