using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Exam.Student.Core;

/// <summary>
/// 合成加密试题包（Mock）：B02 在 D03 加密格式定稿前的占位实现。
/// 格式：[魔数 8 字节 "HZPKG1\0"][AES-GCM nonce 12][密文]（GCM 认证标签附在密文尾部）。
/// 密钥由服务端（Mock）持有，开考指令下发时通过解锁引用交付；
/// 磁盘上只出现本结构的密文，题目明文仅存在于解锁后的内存中。
/// D03 样包就绪后：本类型退化为测试工具，正式路径按 D03 格式解析。
/// </summary>
public static class SyntheticExamPackage
{
    private const int MagicLength = 7;
    private const int NonceLength = 12;
    private static ReadOnlySpan<byte> Magic => "HZPKG1\0"u8;

    /// <summary>生成 256 位内容密钥（服务端持有序列化形式，学生端不持久化）。</summary>
    public static byte[] CreateKey() => RandomNumberGenerator.GetBytes(32);

    /// <summary>构造合成试卷明文（B03 之前的占位题目结构）。</summary>
    public static byte[] CreatePlaintext(string examId, long paperVersion)
    {
        var payload = new
        {
            format = "synthetic-student-package",
            examId,
            paperVersion,
            questions = new object[]
            {
                new { no = 1, subject = "数学", type = "单项选择", stem = "（合成占位）下列等式正确的是？", options = new[] { "A. 1+1=2", "B. 1+1=3" } },
                new { no = 2, subject = "数学", type = "多项选择", stem = "（合成占位）大于 1 的数有？", options = new[] { "A. 0", "B. 2", "C. 3" } },
            },
        };
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
    }

    /// <summary>用内容密钥加密明文，产出落盘的加密包字节。</summary>
    public static byte[] Seal(byte[] plaintext, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plaintext, ciphertext, tag);

        var result = new byte[MagicLength + NonceLength + ciphertext.Length + tag.Length];
        Magic.CopyTo(result);
        nonce.CopyTo(result, MagicLength);
        ciphertext.CopyTo(result, MagicLength + NonceLength);
        tag.CopyTo(result, MagicLength + NonceLength + ciphertext.Length);
        return result;
    }

    /// <summary>用密钥解锁加密包；格式不符、密钥错误或被篡改（认证标签校验失败）均返回 false。</summary>
    public static bool TryUnseal(byte[] package, byte[] key, out byte[] plaintext)
    {
        plaintext = Array.Empty<byte>();
        if (package is null || package.Length < MagicLength + NonceLength + 16)
        {
            return false;
        }

        if (!package.AsSpan(0, MagicLength).SequenceEqual(Magic))
        {
            return false;
        }

        var nonce = package[MagicLength..(MagicLength + NonceLength)];
        var ciphertext = package[(MagicLength + NonceLength)..^16];
        var tag = package[^16..];
        var output = new byte[ciphertext.Length];
        try
        {
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(nonce, ciphertext, tag, output);
        }
        catch (CryptographicException)
        {
            return false;
        }

        plaintext = output;
        return true;
    }
}
