using System.Text;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace Exam.StudentUnitTests;

/// <summary>
/// B03 显示管线底层：Windows.Data.Pdf 能在学生端运行时加载并渲染真实 PDF。
/// 现有合成包 fixtures 只带 PDF 头不是有效文档，无法覆盖该链路；
/// 此处动态构造带精确 xref 的最小多页 PDF 验证 WinRT 管线本身可用。
/// 页面渲染坐标、缩放与点击复测按契约由 D/B 联合执行，不在本测试范围。
/// </summary>
public sealed class PdfRenderPipelineTests
{
    [Fact]
    public async Task WinrtPipelineLoadsAndRendersMultiPagePdf()
    {
        var pdf = CreateMinimalPdf(pageCount: 2);
        using var source = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(source.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(pdf);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        source.Seek(0);

        var document = await PdfDocument.LoadFromStreamAsync(source);
        Assert.Equal(2u, document.PageCount);

        // pageIndex 相对逐题 PDF 从 0 起（契约 4.3）：GetPage 直接使用该值。
        using var page = document.GetPage(1);
        // WinRT 页面尺寸单位为 DIP（1/96 英寸）：US Letter 612pt × 4/3 = 816 DIP。
        Assert.InRange(page.Dimensions.MediaBox.Width, 804, 828);
        Assert.InRange(page.Dimensions.MediaBox.Height, 1044, 1068);

        using var output = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(output, new PdfPageRenderOptions { DestinationWidth = 300 });
        Assert.True(output.Size > 100, "渲染输出不应为空。");

        var header = new byte[8];
        using (var reader = new DataReader(output.GetInputStreamAt(0)))
        {
            await reader.LoadAsync(8);
            reader.ReadBytes(header);
        }
        Assert.Equal(137, header[0]); // PNG 魔数开头，渲染产物可直接进 WPF 位图。
    }

    [Fact]
    public async Task InvalidPdfStreamIsRejectedNotCrashing()
    {
        using var source = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(source.GetOutputStreamAt(0)))
        {
            writer.WriteBytes("%PDF-1.4\n% synthetic header only\n"u8.ToArray());
            await writer.StoreAsync();
            writer.DetachStream();
        }
        source.Seek(0);
        await Assert.ThrowsAnyAsync<Exception>(() => PdfDocument.LoadFromStreamAsync(source).AsTask());
    }

    /// <summary>生成带精确 xref 偏移的最小多页 PDF（Helvetica 文本，US Letter 页面）。</summary>
    private static byte[] CreateMinimalPdf(int pageCount)
    {
        var builder = new StringBuilder();
        builder.Append("%PDF-1.4\n");
        var offsets = new List<long>();

        void AddObject(int id, string body)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append($"{id} 0 obj\n{body}\nendobj\n");
        }

        var pageIds = Enumerable.Range(0, pageCount).Select(i => 4 + i * 2).ToArray();
        AddObject(1, "<< /Type /Catalog /Pages 2 0 R >>");
        AddObject(2, $"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(id => $"{id} 0 R"))}] /Count {pageCount} >>");
        AddObject(3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        for (var i = 0; i < pageCount; i++)
        {
            var contentId = 5 + i * 2;
            AddObject(4 + i * 2,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {contentId} 0 R >>");
            var content = $"BT /F1 24 Tf 72 720 Td (Page {i + 1}) Tj ET";
            AddObject(5 + i * 2, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            builder.Append($"{offset:D10} 00000 n \n");
        }
        builder.Append($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
