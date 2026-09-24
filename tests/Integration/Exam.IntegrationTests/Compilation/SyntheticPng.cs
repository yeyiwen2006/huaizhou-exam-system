using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Exam.IntegrationTests.Compilation;

/// <summary>
/// 生成一张最小的合法 PNG（棋盘格灰度图），供编译用例当作合成题图。
/// 自己按格式写出，不依赖任何图像库，也不把二进制素材放进仓库。
/// </summary>
internal static class SyntheticPng
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Create(int width = 16, int height = 12)
    {
        var pixels = new byte[height * (1 + width)];
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * (1 + width);
            pixels[rowStart] = 0;                       // 每行的过滤器字节
            for (var x = 0; x < width; x++)
            {
                pixels[rowStart + 1 + x] = (byte)((x + y) % 2 == 0 ? 0 : 255);
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(pixels);
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;   // 位深
        header[9] = 0;   // 颜色类型：灰度
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream target, string type, byte[] data)
    {
        Span<byte> scratch = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(scratch, data.Length);
        target.Write(scratch);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        target.Write(typeBytes);
        target.Write(data);

        var crc = Crc32([.. typeBytes, .. data]);
        BinaryPrimitives.WriteUInt32BigEndian(scratch, crc);
        target.Write(scratch);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var index = 0u; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) == 1 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }
}
