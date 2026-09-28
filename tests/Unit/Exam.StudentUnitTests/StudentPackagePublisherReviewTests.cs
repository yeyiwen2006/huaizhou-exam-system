using System.IO;
using Exam.Contracts.Importing;
using Exam.Infrastructure.Packaging;
using Exam.Infrastructure.Security;

namespace Exam.StudentUnitTests;

public sealed partial class StudentPackagePublisherEndToEndTests
{
    [Fact]
    public async Task Review_CancelledPublishDoesNoValidationOrFileWork()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var request = new StudentPackageRequest(Path.Combine(_root, "missing"), Path.Combine(_root, "compiled"),
            Path.Combine(_root, "preview"), "package", 1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new StudentPackagePublisher(new PackageEncryptionService(4096)).PublishAsync(ImportManifest(), request, cancelled.Token));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task Review_PdfOutsideCompileRootIsRejectedBeforeExtraction()
    {
        var packageRoot = Path.Combine(_root, "package");
        WritePackage(packageRoot);
        var compiledRoot = Directory.CreateDirectory(Path.Combine(_root, "compiled")).FullName;
        var foreign = Path.Combine(_root, "合成选项题_1.pdf");
        File.WriteAllText(foreign, "synthetic invalid PDF; must not reach the PDF parser");
        var compiled = new Dictionary<string, TeXCompileResult>
        {
            [QuestionId] = new(true, 0, false, new(true, 1, foreign, [], 0), foreign, [])
        };
        var result = await new StudentPackagePublisher(new PackageEncryptionService(4096)).PublishAsync(ImportManifest(),
            new StudentPackageRequest(packageRoot, compiledRoot, Path.Combine(_root, "preview"), "package", 1)
            { CompiledQuestions = compiled });
        Assert.False(result.Succeeded);
        Assert.Contains(result.Issues, issue => issue.Code == StudentPackageIssueCode.PathEscaped);
        Assert.False(Directory.Exists(Path.Combine(_root, "preview")));
    }
}
