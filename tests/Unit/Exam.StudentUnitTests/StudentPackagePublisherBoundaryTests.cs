using System.IO;
using Exam.Contracts.Importing;
using Exam.Infrastructure.Packaging;
using Exam.Infrastructure.Security;

namespace Exam.StudentUnitTests;

public sealed partial class StudentPackagePublisherEndToEndTests
{
    [Theory]
    [InlineData("manifest", StudentPackageIssueCode.MetadataInvalid)]
    [InlineData("extra", StudentPackageIssueCode.OptionRegionInvalid)]
    [InlineData("failed", StudentPackageIssueCode.OptionRegionInvalid)]
    [InlineData("timeout", StudentPackageIssueCode.OptionRegionInvalid)]
    [InlineData("exit", StudentPackageIssueCode.OptionRegionInvalid)]
    [InlineData("empty", StudentPackageIssueCode.OptionRegionInvalid)]
    [InlineData("oversized", StudentPackageIssueCode.OptionRegionInvalid)]
    [InlineData("relative", StudentPackageIssueCode.PathEscaped)]
    [InlineData("plaintext", StudentPackageIssueCode.EncryptionFailed)]
    [InlineData("recipients", StudentPackageIssueCode.EncryptionFailed)]
    public async Task Review_PublisherRejectsInvalidInputsBeforePdfParsingOrOutput(
        string fault, StudentPackageIssueCode expected)
    {
        var packageRoot = Path.Combine(_root, "package");
        WritePackage(packageRoot);
        var compilation = Directory.CreateDirectory(Path.Combine(_root, "compiled")).FullName;
        var pdf = Path.Combine(compilation, "合成选项题_1.pdf");
        // Deliberately not a parseable PDF: every case must fail its earlier boundary check.
        File.WriteAllText(pdf, "synthetic guard fixture");
        var manifest = ImportManifest();
        var compiled = new Dictionary<string, TeXCompileResult>
        {
            [QuestionId] = new(true, 0, false, new(true, 1, pdf, [], 0), pdf, [])
        };
        var request = new StudentPackageRequest(packageRoot, compilation, Path.Combine(_root, "preview"),
            "package", PaperVersion) { CompiledQuestions = compiled };
        switch (fault)
        {
            case "manifest": manifest = manifest with { ExamId = "other-synthetic-exam" }; break;
            case "extra": compiled.Add("not-in-manifest", compiled[QuestionId]); break;
            case "failed": compiled[QuestionId] = compiled[QuestionId] with { Succeeded = false }; break;
            case "timeout": compiled[QuestionId] = compiled[QuestionId] with { TimedOut = true }; break;
            case "exit": compiled[QuestionId] = compiled[QuestionId] with { ExitCode = 1 }; break;
            case "empty": File.WriteAllBytes(pdf, []); break;
            case "oversized":
                using (var file = File.OpenWrite(pdf)) file.SetLength(StudentPackageBuilder.MaxPackageBytes + 1L);
                break;
            case "relative": compiled[QuestionId] = compiled[QuestionId] with { OutputPdfPath = "relative.pdf" }; break;
            case "recipients": request = request with { EncryptedPackagePath = Path.Combine(_root, "paper.epkg"), Recipients = [] }; break;
        }
        var result = await new StudentPackagePublisher(new PackageEncryptionService(4096)).PublishAsync(manifest, request);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Issues, issue => issue.Code == expected);
        Assert.Null(result.Envelope);
        Assert.Null(result.EncryptedPackagePath);
        Assert.False(Directory.Exists(request.StudentPackageDirectory));
        Assert.False(File.Exists(Path.Combine(_root, "paper.epkg")));
    }
}
