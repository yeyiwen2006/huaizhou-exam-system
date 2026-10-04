using System.Security.Cryptography;

namespace Exam.Infrastructure.Packaging;

/// <summary>Reject excess bytes before allocation and erase the owned buffer on disposal.</summary>
internal sealed class BoundedMaterialBuffer(int maximum) : MemoryStream
{
    private void Check(long count)
    {
        if (count < 0 || count > maximum - Position)
            throw new InvalidDataException("源材料归档超过允许大小。");
    }
    public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
    public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { Check(buffer.Length); return base.WriteAsync(buffer, cancellationToken); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    { Check(count); return base.WriteAsync(buffer, offset, count, cancellationToken); }
    public override void SetLength(long value)
    {
        if (value < 0 || value > maximum) throw new InvalidDataException("源材料归档超过允许大小。");
        base.SetLength(value);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && TryGetBuffer(out var segment) && segment.Array is { } bytes)
            CryptographicOperations.ZeroMemory(bytes);
        base.Dispose(disposing);
    }
}
