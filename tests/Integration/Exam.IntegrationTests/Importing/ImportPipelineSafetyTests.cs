using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Exam.Contracts.Administration;
using Exam.Contracts.Common;
using Exam.Contracts.Exams;
using Exam.Contracts.Importing;
using Exam.Contracts.Versioning;
using Exam.Domain.Importing;
using Exam.Infrastructure.Packaging;

namespace Exam.IntegrationTests.Importing;

public sealed class ImportPipelineSafetyTests
{
    private static ImportPipeline Pipeline(SyntheticPublicationClient api) => new(api)
    { CompileOverride = SyntheticImportFixture.CompileFixtureAsync };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavingCompatibleSettingsDuringCompilationUsesTheLatestSettingsWithoutRecompiling(bool incompatible)
    {
        using var source = new SyntheticImportFixture();
        using var api = new SyntheticPublicationClient(source);
        var compilations = 0;
        var pipeline = new ImportPipeline(api)
        {
            CompileOverride = async (request, token) =>
            {
                compilations++;
                api.Detail = api.Detail with { Summary = api.Detail.Summary with { Name = "更新后的考试", SettingsVersion = 2 } };
                api.Current = api.Current with { Versions = api.Current.Versions with { ScheduleVersion = 2 } };
                if (incompatible)
                {
                    var subject = api.Detail.Settings.Subjects.Single();
                    api.Detail = api.Detail with { Settings = new([subject with { Groups = [subject.Groups.Single() with { QuestionCount = 2 }] }]) };
                }
                return await SyntheticImportFixture.CompileFixtureAsync(request, token);
            }
        };
        if (incompatible)
            await Assert.ThrowsAsync<ArgumentException>(() => pipeline.PrepareAsync(source.Source, api.Current.ExamId));
        else
        {
            using var prepared = await pipeline.PrepareAsync(source.Source, api.Current.ExamId);
            Assert.Equal("更新后的考试", prepared.ExamName);
            Assert.Equal(2, prepared.ExpectedSettingsVersion);
            var published = await pipeline.PublishAsync(prepared);
            Assert.True(prepared.MatchesPublication(published));
        }
        Assert.Equal(1, compilations);
    }

    [Theory]
    [InlineData("subject-name")]
    [InlineData("subject-score")]
    [InlineData("group-order")]
    [InlineData("group-count")]
    [InlineData("group-type")]
    [InlineData("abstain")]
    [InlineData("group-score")]
    [InlineData("wrong-score")]
    public void ConfiguredSettingsAreAuthoritative_NotJustTheExamName(string fault)
    {
        using var source = new SyntheticImportFixture();
        var subject = source.Settings.Subjects.Single();
        var group = subject.Groups.Single();
        switch (fault)
        {
            case "subject-name": subject = subject with { Name = "另一科目" }; break;
            case "subject-score": subject = subject with { MaxScore = "2" }; break;
            case "group-order": group = group with { Order = 2 }; break;
            case "group-count": group = group with { QuestionCount = 2 }; break;
            case "group-type": group = group with { Type = "multiple" }; break;
            case "abstain": group = group with { AllowAbstain = false }; break;
            case "group-score": group = group with { MaxScore = "2" }; break;
            case "wrong-score": group = group with { OneCorrect = group.OneCorrect! with { Wrong = "-1" } }; break;
        }
        var settings = new ExamSettings([subject with { Groups = [group] }]);
        Assert.Throws<ArgumentException>(() => ImportCatalogBinding.Bind(File.ReadAllText(source.ManifestPath), File.ReadAllText(source.AnswerPath), settings));
    }

    [Fact]
    public void BindChangesOnlyPrivateIdentifiers_AndUsesExactScores()
    {
        using var source = new SyntheticImportFixture();
        var original = File.ReadAllText(source.ManifestPath);
        var answers = File.ReadAllText(source.AnswerPath);
        var subject = source.Settings.Subjects.Single();
        var group = subject.Groups.Single();
        var settings = new ExamSettings([subject with { MaxScore = "1.00", Groups = [group with
        { MaxScore = "1.0", OneCorrect = new("1.0", "0.00", "0") }] }]);
        var bound = ImportCatalogBinding.Bind(original, answers, settings);
        var result = JsonSerializer.Deserialize<ExamManifest>(bound, ImportJson.Create())!;
        Assert.Equal("subject-current", result.Questions.Single().SubjectId);
        Assert.Equal("group-current", result.Questions.Single().GroupId);
        Assert.Equal(original, File.ReadAllText(source.ManifestPath));
        ImportCatalogBinding.RequireMatch(bound, answers, source.Name, settings);
        Assert.Throws<ArgumentException>(() => ImportCatalogBinding.RequireMatch(original, answers, source.Name, settings));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("folder/../outside.txt")]
    [InlineData("manifest.json:stream")]
    [InlineData("NUL.tex")]
    [InlineData("folder./item.txt")]
    [InlineData("folder//item.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("/outside.txt")]
    [InlineData("folder\\item.txt")]
    public async Task ZipRejectsTraversalWindowsAliasesAndStreams(string entryName)
    {
        using var source = new SyntheticImportFixture();
        var zip = Path.Combine(source.Root, "invalid.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var entry = new StreamWriter(archive.CreateEntry(entryName).Open());
            entry.Write("synthetic");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => ExamImportWorkspace.CreateAsync(zip));
        Assert.False(File.Exists(Path.Combine(source.Root, "outside.txt")));
    }

    [Fact]
    public async Task ZipRejectsCaseAliasesAndSymlinks()
    {
        using var source = new SyntheticImportFixture();
        var zip = Path.Combine(source.Root, "aliases.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var entry = new StreamWriter(archive.CreateEntry("manifest.json").Open())) entry.Write("{}");
            using (var entry = new StreamWriter(archive.CreateEntry("MANIFEST.JSON").Open())) entry.Write("{}");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => ExamImportWorkspace.CreateAsync(zip));
        var linked = Path.Combine(source.Root, "linked.zip");
        using (var archive = ZipFile.Open(linked, ZipArchiveMode.Create))
        {
            var item = archive.CreateEntry("link");
            item.ExternalAttributes = unchecked((int)0xA1FF0000);
            using var entry = new StreamWriter(item.Open()); entry.Write("../outside");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => ExamImportWorkspace.CreateAsync(linked));
    }

    [Fact]
    public async Task EmptyDirectoriesCannotBypassTheSnapshotEntryLimit()
    {
        using var source = new SyntheticImportFixture();
        for (var index = 0; index < 2049; index++) Directory.CreateDirectory(Path.Combine(source.Source, "empty-" + index));
        await Assert.ThrowsAsync<InvalidDataException>(() => ExamImportWorkspace.CreateAsync(source.Source));
    }

    [Fact]
    public async Task MissingOrInvalidMaterialsStopBeforeCompilationAndAnyUpload()
    {
        using var source = new SyntheticImportFixture();
        using var api = new SyntheticPublicationClient(source);
        var compilations = 0;
        var pipeline = new ImportPipeline(api) { CompileOverride = (_, _) => { compilations++; throw new InvalidOperationException(); } };
        File.Delete(source.AnswerPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => pipeline.PrepareAsync(source.Source, api.Current.ExamId));
        File.WriteAllText(source.AnswerPath, "{}");
        await Assert.ThrowsAnyAsync<ArgumentException>(() => pipeline.PrepareAsync(source.Source, api.Current.ExamId));
        Assert.Equal(0, compilations);
        Assert.Equal(0, api.Uploads);
    }

    [Fact]
    public async Task PrivateSnapshotDoesNotFollowLaterEditsToTheOriginalSource()
    {
        using var source = new SyntheticImportFixture();
        using var api = new SyntheticPublicationClient(source);
        var pipeline = Pipeline(api);
        using var prepared = await pipeline.PrepareAsync(source.Source, api.Current.ExamId);
        Assert.Equal("单项选择题", prepared.Questions.Single().Type);
        Assert.Equal("A:1页、B:1页", prepared.Questions.Single().RegionPages);
        File.WriteAllText(source.ManifestPath, "changed original after preview");
        var result = await pipeline.PublishAsync(prepared);
        Assert.True(prepared.MatchesPublication(result));
        Assert.Equal(1, api.Uploads);
    }

    [Fact]
    public async Task ChangedPreviewPdfIsRejectedBeforeAnyUpload()
    {
        using var source = new SyntheticImportFixture();
        using var api = new SyntheticPublicationClient(source);
        var pipeline = Pipeline(api);
        using var prepared = await pipeline.PrepareAsync(source.Source, api.Current.ExamId);
        await File.AppendAllTextAsync(prepared.Questions.Single().PdfPath, "changed preview");
        await Assert.ThrowsAsync<InvalidDataException>(() => pipeline.PublishAsync(prepared));
        Assert.Equal(0, api.Uploads);
    }

    [Fact]
    public async Task MissingManagedCertificatesAndChangedSettingsNeverUpload()
    {
        using var source = new SyntheticImportFixture();
        using var api = new SyntheticPublicationClient(source);
        var pipeline = Pipeline(api);
        using var prepared = await pipeline.PrepareAsync(source.Source, api.Current.ExamId);
        api.NoRecipients = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.PublishAsync(prepared));
        api.NoRecipients = false;
        api.Detail = api.Detail with { Summary = api.Detail.Summary with { SettingsVersion = 2 } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.PublishAsync(prepared));
        Assert.Equal(0, api.Uploads);
    }

    [Fact]
    public async Task LostReceiptReplaysOriginalUpload_NotAVisibleLocalVersion()
    {
        using var source = new SyntheticImportFixture();
        using var api = new SyntheticPublicationClient(source);
        var pipeline = Pipeline(api);
        using var prepared = await pipeline.PrepareAsync(source.Source, api.Current.ExamId);
        api.OnUpload = _ => throw new HttpRequestException("synthetic lost receipt after local commit");
        await Assert.ThrowsAsync<HttpRequestException>(() => pipeline.PublishAsync(prepared));
        var reads = api.Reads;
        Assert.Equal(1, api.Current.Versions.PaperVersion); // Visibility alone is intentionally insufficient.
        api.Current = api.Current with { LifecycleState = ExamLifecycle.Running };
        api.OnUpload = null;
        var replay = await pipeline.PublishAsync(prepared);
        Assert.True(prepared.MatchesPublication(replay));
        Assert.Equal(reads, api.Reads);
        Assert.Equal(2, api.Uploads);
        Assert.Same(api.Requests[0], api.Requests[1]);
        Assert.Equal(api.MaterialsCiphertexts[0], api.MaterialsCiphertexts[1]);
        Assert.Equal(api.Ciphertexts[0], api.Ciphertexts[1]);
    }

    [Fact]
    public async Task SavedPreviewSurvivesProcessBoundary_AndSerializesPublishers()
    {
        using var source = new SyntheticImportFixture();
        using var api = new SyntheticPublicationClient(source);
        var pipeline = Pipeline(api);
        var output = Path.Combine(source.Root, "saved-preview");
        using (var prepared = await pipeline.PrepareAsync(source.Source, api.Current.ExamId))
            await prepared.ExportAsync(output);
        using (var loaded = await PreparedExamImport.LoadAsync(output, api))
        {
            await Assert.ThrowsAsync<IOException>(() => PreparedExamImport.LoadAsync(output, api));
            api.OnUpload = _ => throw new HttpRequestException("synthetic response loss");
            await Assert.ThrowsAsync<HttpRequestException>(() => pipeline.PublishAsync(loaded));
        }
        using (var restarted = await PreparedExamImport.LoadAsync(output, api))
        {
            api.OnUpload = null;
            var result = await pipeline.PublishAsync(restarted);
            Assert.True(restarted.MatchesPublication(result));
        }
        Assert.Equal(2, api.Uploads);
        Assert.Equal(api.Requests[0].Publication.RequestId, api.Requests[1].Publication.RequestId);
        Assert.Equal(JsonSerializer.Serialize(api.Requests[0]), JsonSerializer.Serialize(api.Requests[1]));
        Assert.Equal(api.MaterialsCiphertexts[0], api.MaterialsCiphertexts[1]);
        Assert.Equal(api.Ciphertexts[0], api.Ciphertexts[1]);
    }

    [Fact]
    public async Task MalformedSavedPreviewReleasesItsExclusiveLock()
    {
        using var source = new SyntheticImportFixture();
        using var api = new SyntheticPublicationClient(source);
        var output = Path.Combine(source.Root, "saved-preview");
        using (var prepared = await Pipeline(api).PrepareAsync(source.Source, api.Current.ExamId)) await prepared.ExportAsync(output);
        var journal = Path.Combine(output, "preview.json");
        var original = await File.ReadAllTextAsync(journal);
        await File.WriteAllTextAsync(journal, "{");
        await Assert.ThrowsAsync<JsonException>(() => PreparedExamImport.LoadAsync(output, api));
        await File.WriteAllTextAsync(journal, original);
        using var loaded = await PreparedExamImport.LoadAsync(output, api);
        Assert.Single(loaded.Questions);
    }

    [Fact]
    public async Task ConnectionSwitchAndWrongExamReceiptCannotBecomeSuccess()
    {
        using var source = new SyntheticImportFixture();
        using var api = new SyntheticPublicationClient(source);
        var pipeline = Pipeline(api);
        using var prepared = await pipeline.PrepareAsync(source.Source, api.Current.ExamId);
        api.Generation++;
        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.PublishAsync(prepared));
        Assert.Equal(0, api.Uploads);
        api.Generation--;
        api.OnUpload = actual => Task.FromResult(actual with { ExamId = "other-exam" });
        await Assert.ThrowsAsync<InvalidDataException>(() => pipeline.PublishAsync(prepared));
    }

    [Fact]
    public async Task HttpAdapterRequiresStableServerIdentityAndNonemptyBusinessResult()
    {
        var node = "node-a";
        var epoch = 2L;
        var empty = false;
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new ApiEnvelope<PublicationRecipients>("server", node, epoch,
                "synthetic", empty ? null : new([]), null), options: JsonDefaults.Create())
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
        var api = new HttpExamPublicationClient(http);
        await api.GetPublicationRecipientsAsync();
        node = "node-b";
        await Assert.ThrowsAsync<ExamPublicationApiException>(() => api.GetPublicationRecipientsAsync());
        epoch = 3;
        await api.GetPublicationRecipientsAsync();
        empty = true;
        var failure = await Assert.ThrowsAsync<ExamPublicationApiException>(() => api.GetPublicationRecipientsAsync());
        Assert.Equal("EMPTY_RESPONSE", failure.Code);
    }

    [Fact]
    public async Task ChangedMaterialsCiphertextCannotBeReplayedAsTheOriginalPublication()
    {
        using var source = new SyntheticImportFixture();
        using var api = new SyntheticPublicationClient(source);
        var pipeline = Pipeline(api);
        using var prepared = await pipeline.PrepareAsync(source.Source, api.Current.ExamId);
        api.OnUpload = _ => throw new HttpRequestException("synthetic lost receipt");
        await Assert.ThrowsAsync<HttpRequestException>(() => pipeline.PublishAsync(prepared));
        await File.AppendAllTextAsync(Path.Combine(prepared.PreviewDirectory, "materials.enc"), "changed");
        api.OnUpload = null;
        await Assert.ThrowsAsync<InvalidDataException>(() => pipeline.PublishAsync(prepared));
        Assert.Equal(1, api.Uploads);
    }

    [Theory]
    [InlineData("reason")]
    [InlineData("changedQuestionIds")]
    [InlineData("materialsSha256")]
    [InlineData("materials.enc")]
    public async Task SavedRetryRejectsChangedRevisionMetadataAndMaterialsCiphertext(string field)
    {
        using var source = new SyntheticImportFixture();
        using var api = new SyntheticPublicationClient(source);
        var pipeline = Pipeline(api);
        var output = Path.Combine(source.Root, "saved-retry");
        using (var prepared = await pipeline.PrepareAsync(source.Source, api.Current.ExamId)) await prepared.ExportAsync(output);
        using (var prepared = await PreparedExamImport.LoadAsync(output, api))
        {
            api.OnUpload = _ => throw new HttpRequestException("synthetic lost receipt");
            await Assert.ThrowsAsync<HttpRequestException>(() => pipeline.PublishAsync(prepared));
        }
        if (field == "materials.enc") await File.AppendAllTextAsync(Path.Combine(output, field), "changed");
        else
        {
            var path = Path.Combine(output, "upload.json");
            var saved = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            saved["upload"]!["publication"]![field] = field == "changedQuestionIds"
                ? new System.Text.Json.Nodes.JsonArray("different")
                : System.Text.Json.Nodes.JsonValue.Create("changed");
            await File.WriteAllTextAsync(path, saved.ToJsonString());
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => PreparedExamImport.LoadAsync(output, api));
        Assert.Equal(1, api.Uploads);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handle(request));
    }
}
