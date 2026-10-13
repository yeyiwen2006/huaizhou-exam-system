using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Exam.Application.Participation;
using Exam.Contracts.Archives;
using Exam.Contracts.Common;
using Exam.Contracts.Exams;
using Exam.Contracts.Importing;
using Exam.Contracts.Resources;
using Exam.Contracts.Versioning;
using Exam.Domain.Scoring;
using Exam.Domain.Importing;
using Exam.Infrastructure.Archives;
using Exam.Infrastructure.Compilation;
using Exam.Infrastructure.Packaging;
using Exam.Infrastructure.Postgres.Exams;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Postgres.Scoring;
using Exam.Infrastructure.Postgres.Versioning;
using Exam.Infrastructure.Security;
using Exam.IntegrationTests.Postgres;
using Npgsql;

namespace Exam.IntegrationTests.Archives;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class PostgresExamArchiveTests(PostgresDatabaseFixture fixture)
{
    [Fact]
    public async Task Active_exam_is_copied_to_an_independent_database_with_history_resources_and_retry_safe_overwrite()
    {
        using var sourceCertificate = Certificate("archive-source");
        using var targetCertificate = Certificate("archive-target");
        var sourceResources = Portable("source-node", sourceCertificate);
        var source = new PostgresExamArchiveService(fixture.DataSource, sourceResources);
        var seed = await SeedAsync(fixture.DataSource, sourceCertificate);
        var archive = await source.ExportAsync(seed.ExamId);
        Assert.DoesNotContain("synthetic-source-verifier", JsonSerializer.Serialize(archive.Manifest));
        Assert.DoesNotContain(seed.SessionId, JsonSerializer.Serialize(archive.Manifest));
        Assert.DoesNotContain(seed.OtherExamId, JsonSerializer.Serialize(archive.Manifest));
        // A can continue recording answers after the point-in-time export.
        await Sql(fixture.DataSource, "UPDATE exam.participation_progress SET answer_version=answer_version+1 WHERE exam_id=@exam", ("exam", seed.ExamId));
        Assert.Equal("active", await Scalar(fixture.DataSource, "SELECT activation_status FROM exam.activation_devices WHERE device_id=@id", ("id", seed.DeviceId)));
        Assert.Equal(1L, await Scalar(fixture.DataSource, "SELECT count(*) FROM exam.student_sessions WHERE session_id=@id", ("id", seed.SessionId)));
        using var transport = new MemoryStream(); await archive.WriteAsync(transport); transport.Position = 0;
        var received = await ExamArchiveDocument.ReadAsync(transport);

        var targetDatabase = new PostgresDatabaseFixture();
        await targetDatabase.InitializeAsync();
        try
        {
            var target = new PostgresExamArchiveService(targetDatabase.DataSource, Portable("target-node", targetCertificate));
            Assert.Empty((await target.PreviewAsync(received)).Conflicts);
            var request = new ExamArchiveImportRequest(Guid.NewGuid().ToString("N"), "copy");
            var imported = await target.ImportAsync(received, request, "target-operator");
            Assert.NotEqual(seed.ExamId, imported.ExamId);
            Assert.Equal(imported, await target.ImportAsync(received, request, "target-operator"));
            Assert.Equal(1L, await Scalar(targetDatabase.DataSource, "SELECT count(*) FROM exam.exam_catalog"));
            var importedSubmission = (string)(await Scalar(targetDatabase.DataSource, "SELECT submission_id FROM exam.final_submissions WHERE exam_id=@exam", ("exam", imported.ExamId)))!;
            Assert.NotEqual(seed.SubmissionId, importedSubmission);
            var input = await new PostgresGradingInputs(targetDatabase.DataSource).LoadSubmissionAsync(importedSubmission);
            Assert.NotNull(input); Assert.Equal(imported.ExamId, input.ExamId);
            Assert.Equal("1", await Scalar(targetDatabase.DataSource, "SELECT total_score FROM exam.grading_results WHERE exam_id=@exam", ("exam", imported.ExamId)));
            Assert.Equal(1L, await Scalar(targetDatabase.DataSource, "SELECT answer_version FROM exam.participation_progress WHERE exam_id=@exam", ("exam", imported.ExamId)));
            Assert.Equal(2L, await Scalar(fixture.DataSource, "SELECT answer_version FROM exam.participation_progress WHERE exam_id=@exam", ("exam", seed.ExamId)));
            Assert.Equal("revoked", await Scalar(targetDatabase.DataSource, "SELECT activation_status FROM exam.activation_devices"));
            Assert.Equal(0L, await Scalar(targetDatabase.DataSource, "SELECT count(*) FROM exam.operator_accounts"));
            Assert.NotNull(await new PostgresGradingInputs(targetDatabase.DataSource).LoadRulesAsync(imported.ExamId));
            var resource = await new PostgresVersionedResourceStore(targetDatabase.DataSource).FindAsync(imported.ExamId, "paper-package", 1);
            Assert.NotNull(resource); Assert.Equal(new[] { "target-node" }, resource.Envelope.WrappedKeys.Keys);
            var decoded = await Portable("target-node", targetCertificate).ExportAsync(resource);
            Assert.Equal(imported.ExamId, StudentPaperPackage.ReadManifest(decoded.Content, imported.ExamId, 1).ExamId);
            Assert.NotNull(await new PostgresPaperMaterialStore(targetDatabase.DataSource).FindAsync(imported.ExamId, 1));
            var fromTarget = await target.ExportAsync(imported.ExamId);
            Assert.Equal(seed.ExamId, fromTarget.Manifest.OriginExamId);
            Assert.Equal(imported.ExamId, Assert.Single((await target.PreviewAsync(fromTarget)).Conflicts).ExamId);
            var versions = new PostgresExamVersioningService(targetDatabase.DataSource);
            var snapshot = versions.GetExam(imported.ExamId).Value!;
            var adjusted = versions.UpdateSchedule(new(imported.ExamId, snapshot.Versions.ScheduleVersion,
                new(snapshot.Schedule.StartsAt.AddMinutes(1), snapshot.Schedule.EndsAt.AddMinutes(1)), DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N")));
            Assert.True(adjusted.Succeeded, adjusted.Error);

            var copy = await target.ImportAsync(received, new(Guid.NewGuid().ToString("N"), "copy"), "target-operator");
            Assert.NotEqual(imported.ExamId, copy.ExamId); Assert.NotEqual(imported.ExamName, copy.ExamName);
            var preview = await target.PreviewAsync(received);
            Assert.Equal(2, preview.Conflicts.Count);
            var selected = preview.Conflicts.Single(item => item.ExamId == imported.ExamId);
            var overwritten = await target.ImportAsync(received, new(Guid.NewGuid().ToString("N"), "overwrite", selected.ExamId, selected.Version), "target-operator");
            Assert.Equal(imported.ExamId, overwritten.ExamId);
            Assert.Equal(1L, await Scalar(targetDatabase.DataSource, "SELECT count(*) FROM exam.final_submissions WHERE exam_id=@exam", ("exam", copy.ExamId)));
            Assert.Equal(2L, await Scalar(targetDatabase.DataSource, "SELECT count(*) FROM exam.exam_catalog"));
            Assert.Equal(seed.ExamId, (await source.ExportAsync(seed.ExamId)).Manifest.SourceExamId);
        }
        finally { await targetDatabase.DisposeAsync(); }
    }

    [Fact]
    public async Task Invalid_archive_and_unrelated_overwrite_target_leave_existing_exams_unchanged()
    {
        using var certificate = Certificate("archive-validation");
        var service = new PostgresExamArchiveService(fixture.DataSource, Portable("source-node", certificate));
        var seed = await SeedAsync(fixture.DataSource, certificate);
        var archive = await service.ExportAsync(seed.ExamId);
        await Assert.ThrowsAsync<ParticipationRuleException>(() => service.ImportAsync(archive,
            new(Guid.NewGuid().ToString("N"), "overwrite", seed.OtherExamId, 1), "operator"));
        var participant = archive.Manifest.Tables["participations"][0]!;
        participant["exam_id"] = seed.OtherExamId;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(archive, new(Guid.NewGuid().ToString("N"), "copy"), "operator"));
        Assert.Equal(1L, await Scalar(fixture.DataSource, "SELECT count(*) FROM exam.final_submissions WHERE exam_id=@exam", ("exam", seed.ExamId)));
        Assert.Equal(1L, await Scalar(fixture.DataSource, "SELECT count(*) FROM exam.exam_catalog WHERE exam_id=@exam", ("exam", seed.OtherExamId)));
    }

    private static async Task<Seed> SeedAsync(NpgsqlDataSource dataSource, X509Certificate2 certificate)
    {
        var unique = Guid.NewGuid().ToString("N");
        var versions = new PostgresExamVersioningService(dataSource);
        var catalog = new PostgresExamCatalog(dataSource, versions, TimeProvider.System);
        var settings = new ExamSettings([new("s", "科目", "1", [new("g", "单选", "single", 1, 1, "1", false, new("1", "0", "0"), null)])]);
        var now = DateTimeOffset.UtcNow;
        var created = await catalog.CreateAsync(new(unique, "归档合成考试" + unique[..6], new(now.AddHours(1), now.AddHours(2)), settings), "source-operator");
        var other = await catalog.CreateAsync(new(unique + "-other", "另一场" + unique[..6], new(now.AddHours(3), now.AddHours(4)), settings), "source-operator");
        var exam = created.Summary.ExamId;
        var name = created.Summary.Name;
        var manifest = new ExamManifest(1, exam, name, [new("s", "科目", "1")],
            [new("g", "s", "单选", "single", 1, 1, false, "1", new(new("1", "0", "0"), null))],
            [new("q", 1, "s", "g", $"questions/{name}_1.tex", ["A", "B"], null)], []);
        var answers = new AnswerKeyFile(1, exam, [new("q", 1, ["A"])]);
        var manifestJson = JsonSerializer.Serialize(manifest, ImportJson.Create());
        var answerJson = JsonSerializer.Serialize(answers, ImportJson.Create());
        var rules = ImportedGradingRules.Create(manifestJson, answerJson);
        var paper = new StudentPaperManifest(2, exam, name, 1, [new("s", "科目")], [new("g", "s", "单选", 1)],
            [new(rules.Structure.Questions[0], $"questions/{name}_1.pdf", null)], []);
        var regions = new OptionRegionArtifact(1, OptionRegionArtifact.AnchorSchemeName, exam, 1,
            [new("q", [new("A", 0, .1, .1, .1, .1), new("B", 0, .3, .1, .1, .1)])]);
        var plain = Zip(new Dictionary<string, byte[]>
        {
            [StudentPaperPackage.ManifestPath] = JsonSerializer.SerializeToUtf8Bytes(paper, ImportJson.Create()),
            [OptionRegionArtifact.FileName] = Encoding.UTF8.GetBytes(OptionRegionArtifactJson.Serialize(regions)),
            [$"questions/{name}_1.pdf"] = "%PDF-1.4\nsynthetic"u8.ToArray()
        });
        using var ciphertext = new MemoryStream();
        var envelope = await new PackageEncryptionService().EncryptAsync(new MemoryStream(plain), ciphertext, unique + "-resource", 1, [new("source-node", certificate)]);
        await new PostgresVersionedResourceStore(dataSource).SaveAsync(new(unique + "-resource", exam, "paper-package", 1, ciphertext.ToArray(), envelope, now, "source-operator"));
        var sourceFiles = new Dictionary<string, byte[]>
        {
            ["manifest.json"] = Encoding.UTF8.GetBytes(manifestJson), ["server-only/answer-key.json"] = Encoding.UTF8.GetBytes(answerJson),
            [$"questions/{name}_1.tex"] = Encoding.UTF8.GetBytes("合成题目")
        };
        var materialBytes = PaperMaterialsArchive.Build(exam, 1, sourceFiles);
        var materialHash = Convert.ToHexString(SHA256.HashData(materialBytes)).ToLowerInvariant();
        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            PostgresPaperMaterialStore.SaveInTransaction(connection, transaction, new(exam, 1, materialBytes, materialHash,
                Convert.ToHexString(SHA256.HashData(plain)).ToLowerInvariant(), 1, sourceFiles.Count,
                QuestionContentDigest.Compute(manifest, new InMemoryMaterialFileSource(sourceFiles)), now, "source-operator"));
            await transaction.CommitAsync();
        }
        Assert.True(versions.PublishPaper(new(exam, 0, unique + "-resource", rules.Structure, [], new(true, true, true, true), "source-operator", "初始试卷", now, unique + "-paper", materialHash)).Succeeded);
        Assert.True(versions.PublishGradingRules(new(exam, 0, unique + "-rules", new(true, true, true), "source-operator", "初始评分规则", now, unique + "-rules", manifestJson, answerJson)).Succeeded);
        var seed = new Seed(exam, other.Summary.ExamId, unique + "-device", unique + "-session", unique + "-submission");
        await Sql(dataSource, """
            INSERT INTO exam.activation_devices(device_id,client_number,activation_date,activation_status,protected_credential,credential_version,created_at,version)
              VALUES(@device,@number,CURRENT_DATE,'active','synthetic-source-verifier',1,@now,1);
            INSERT INTO exam.candidate_roster_entries VALUES(@exam,'candidate','合成考生','1','1');
            INSERT INTO exam.student_sessions VALUES(@session,@device,'candidate','合成考生','1','1',ARRAY[@exam,@other],jsonb_build_object(@exam,'candidate',@other,'other-candidate'),@now);
            INSERT INTO exam.entry_preparations VALUES(@preparation,@session,@exam,'candidate',@device,1,1,@now);
            INSERT INTO exam.participations(participation_id,exam_id,candidate_id,entry_version,business_state,authorized_device_id,authorization_version,version)
              VALUES(@participation,@exam,'candidate',1,'submitted',@device,1,1);
            INSERT INTO exam.participation_progress VALUES(@participation,@exam,'candidate',1,@device,1,1,1,'{"q":{"kind":"selected","optionIds":["A"]}}','q',@now);
            INSERT INTO exam.final_submissions VALUES(@submission,@exam,'candidate',@participation,@device,1,1,'{"q":{"kind":"selected","optionIds":["A"]}}',@now,@receipt,1);
            INSERT INTO exam.grading_results VALUES(@submission,@exam,'candidate',1,'{}','{}','1',@now);
            INSERT INTO exam.grading_outbox(submission_id,grading_version,created_at,completed_at) VALUES(@submission,1,@now,@now);
            INSERT INTO exam.command_events(event_id,create_request_id,exam_id,participation_id,entry_version,event_type,state_version,target_device_id,target_authorization_version,resource_version,protected_unlock_reference,created_at,next_delivery_at)
              VALUES(@event,@event,@exam,@participation,1,'Start',1,@device,1,1,@unlock,@now,@now);
            UPDATE exam.exam_entry_access SET lifecycle='running' WHERE exam_id=@exam;
            """, ("exam", exam), ("other", seed.OtherExamId), ("device", seed.DeviceId), ("session", seed.SessionId),
            ("number", Random.Shared.NextInt64(100000000000, 999999999999).ToString()), ("now", now),
            ("preparation", unique + "-preparation"), ("participation", unique + "-participation"), ("submission", seed.SubmissionId),
            ("receipt", unique + "-receipt"), ("event", unique + "-event"), ("unlock", "resource-envelope:" + unique + "-resource"));
        Assert.True(versions.ExecuteScheduleTrigger(new(exam, 1, ScheduleTriggerKind.Start, now.AddHours(1), unique + "-start")).Value!.Applied);
        return seed;
    }

    private static async Task Sql(NpgsqlDataSource dataSource, string sql, params (string Name, object Value)[] values)
    { await using var command = dataSource.CreateCommand(sql); foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value); await command.ExecuteNonQueryAsync(); }
    private static async Task<object?> Scalar(NpgsqlDataSource dataSource, string sql, params (string Name, object Value)[] values)
    { await using var command = dataSource.CreateCommand(sql); foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value); return await command.ExecuteScalarAsync(); }
    private static byte[] Zip(Dictionary<string, byte[]> files)
    { using var output = new MemoryStream(); using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true)) foreach (var file in files) { using var entry = zip.CreateEntry(file.Key).Open(); entry.Write(file.Value); } return output.ToArray(); }
    private static X509Certificate2 Certificate(string name)
    { using var key = RSA.Create(2048); return new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1)); }
    private static PortableExamResources Portable(string node, X509Certificate2 certificate) => new(new Unwrapper(node, certificate), new Provider(node, certificate), new PackageEncryptionService());
    private sealed class Unwrapper(string node, X509Certificate2 certificate) : IPackageKeyUnwrapper
    { public byte[] Unwrap(EncryptedPackageEnvelope envelope) => PackageEncryptionService.UnwrapDataKey(envelope, node, certificate); }
    private sealed class Provider(string node, X509Certificate2 certificate) : IPublicationCertificateProvider
    { public PublicationCertificateLease Acquire() => new([new(node, X509CertificateLoader.LoadCertificate(certificate.RawData))]); }
    private sealed record Seed(string ExamId, string OtherExamId, string DeviceId, string SessionId, string SubmissionId);
}
