using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Security;
using Exam.IntegrationTests.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Exam.IntegrationTests.Resources;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class PostgresVersionedResourceStoreTests(PostgresDatabaseFixture fixture)
{
    [Fact]
    public async Task EncryptedPackage_RoundTripsThroughDatabase_AndBothNodesCanDecrypt()
    {
        using var nodeA = CreateCertificate("node-a");
        using var nodeB = CreateCertificate("node-b");
        var source = RandomNumberGenerator.GetBytes(16_777);
        var packageId = $"package-{Guid.NewGuid():N}";
        var examId = $"exam-{Guid.NewGuid():N}";
        var policy = new ManagedNodeEncryptionPolicy(
            new HashSet<string>(["node-a", "node-b"], StringComparer.Ordinal));
        var encryption = new PackageEncryptionService(4096, policy);
        await using var encrypted = new MemoryStream();
        var envelope = await encryption.EncryptAsync(
            new MemoryStream(source),
            encrypted,
            packageId,
            2,
            [new("node-a", nodeA), new("node-b", nodeB)]);
        var createdAt = new DateTimeOffset(2026, 9, 22, 4, 0, 0, TimeSpan.Zero);

        await new PostgresVersionedResourceStore(fixture.DataSource, policy).SaveAsync(
            new VersionedEncryptedResource(
                packageId,
                examId,
                "paper-package",
                2,
                encrypted.ToArray(),
                envelope,
                createdAt,
                "integration-test"));
        var restored = await new PostgresVersionedResourceStore(fixture.DataSource, policy)
            .FindAsync(examId, "paper-package", 2);

        Assert.NotNull(restored);
        Assert.Equal(createdAt, restored.CreatedAt);
        foreach (var (nodeId, certificate) in new[] { ("node-a", nodeA), ("node-b", nodeB) })
        {
            await using var plaintext = new MemoryStream();
            await encryption.DecryptAsync(
                new MemoryStream(restored.EncryptedContent),
                plaintext,
                restored.Envelope,
                nodeId,
                certificate);
            Assert.Equal(source, plaintext.ToArray());
        }
    }

    [Fact]
    public async Task PreparedDownload_RequiresMatchingCurrentPreparationSessionAndDevice()
    {
        using var node = CreateCertificate("node-a");
        var packageId = $"package-{Guid.NewGuid():N}";
        var examId = $"exam-{Guid.NewGuid():N}";
        var candidateId = $"candidate-{Guid.NewGuid():N}";
        var deviceId = $"device-{Guid.NewGuid():N}";
        var sessionId = $"session-{Guid.NewGuid():N}";
        var preparationId = $"preparation-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var content = RandomNumberGenerator.GetBytes(2048);
        var policy = new ManagedNodeEncryptionPolicy(
            new HashSet<string>(["node-a"], StringComparer.Ordinal));
        var encryption = new PackageEncryptionService(4096, policy);
        await using var encrypted = new MemoryStream();
        var envelope = await encryption.EncryptAsync(
            new MemoryStream(content),
            encrypted,
            packageId,
            2,
            [new("node-a", node)]);
        var store = new PostgresVersionedResourceStore(fixture.DataSource, policy);
        await store.SaveAsync(new VersionedEncryptedResource(
            packageId,
            examId,
            "paper-package",
            2,
            encrypted.ToArray(),
            envelope,
            now,
            "integration-test"));

        await SeedPreparationAsync(
            examId,
            candidateId,
            deviceId,
            sessionId,
            preparationId,
            packageId,
            encrypted.Length,
            now);

        var authorized = await store.FindForPreparationAsync(
            packageId,
            preparationId,
            sessionId,
            deviceId);
        var wrongDevice = await store.FindForPreparationAsync(
            packageId,
            preparationId,
            sessionId,
            $"wrong-{deviceId}");

        Assert.NotNull(authorized);
        Assert.Null(wrongDevice);

        await using var update = fixture.DataSource.CreateCommand("""
            UPDATE exam.exam_entry_access
               SET paper_version = 3
             WHERE exam_id = $1;
            """);
        update.Parameters.AddWithValue(examId);
        await update.ExecuteNonQueryAsync();

        Assert.Null(await store.FindForPreparationAsync(
            packageId,
            preparationId,
            sessionId,
            deviceId));

        await using (var beforeWindow = fixture.DataSource.CreateCommand("""
                         UPDATE exam.exam_entry_access
                            SET paper_version = 2, scheduled_start = clock_timestamp() + interval '1 hour',
                                scheduled_end = clock_timestamp() + interval '2 hours'
                          WHERE exam_id = $1;
                         """))
        {
            beforeWindow.Parameters.AddWithValue(examId);
            await beforeWindow.ExecuteNonQueryAsync();
        }

        Assert.Null(await store.FindForPreparationAsync(packageId, preparationId, sessionId, deviceId));

        await using (var end = fixture.DataSource.CreateCommand("""
                         UPDATE exam.exam_entry_access
                            SET lifecycle = 'ended', scheduled_start = clock_timestamp() - interval '3 hours',
                                scheduled_end = clock_timestamp() - interval '1 hour'
                          WHERE exam_id = $1;
                         """))
        {
            end.Parameters.AddWithValue(examId);
            await end.ExecuteNonQueryAsync();
        }

        // Merely having downloaded a preparation before the deadline is not actual participation.
        Assert.Null(await store.FindForPreparationAsync(packageId, preparationId, sessionId, deviceId));

        await using (var actual = fixture.DataSource.CreateCommand("""
                         INSERT INTO exam.participations
                             (participation_id, exam_id, candidate_id, entry_version, business_state,
                              authorized_device_id, authorization_version, version)
                         VALUES ($1, $2, $3, 1, 'answering', $4, 1, 1);
                         """))
        {
            actual.Parameters.AddWithValue($"participation-{Guid.NewGuid():N}");
            actual.Parameters.AddWithValue(examId);
            actual.Parameters.AddWithValue(candidateId);
            actual.Parameters.AddWithValue(deviceId);
            await actual.ExecuteNonQueryAsync();
        }

        Assert.NotNull(await store.FindForPreparationAsync(packageId, preparationId, sessionId, deviceId));

        await using (var revoke = fixture.DataSource.CreateCommand("""
                         UPDATE exam.activation_devices SET activation_status = 'revoked' WHERE device_id = $1;
                         """))
        {
            revoke.Parameters.AddWithValue(deviceId);
            await revoke.ExecuteNonQueryAsync();
        }

        Assert.Null(await store.FindForPreparationAsync(packageId, preparationId, sessionId, deviceId));

        await using (var reactivate = fixture.DataSource.CreateCommand("""
                         UPDATE exam.activation_devices SET activation_status = 'active' WHERE device_id = $1;
                         """))
        {
            reactivate.Parameters.AddWithValue(deviceId);
            await reactivate.ExecuteNonQueryAsync();
        }

        await using (var submit = fixture.DataSource.CreateCommand("""
                         UPDATE exam.participations SET business_state = 'submitted' WHERE exam_id = $1;
                         """))
        {
            submit.Parameters.AddWithValue(examId);
            await submit.ExecuteNonQueryAsync();
        }

        Assert.Null(await store.FindForPreparationAsync(packageId, preparationId, sessionId, deviceId));
    }

    private async Task SeedPreparationAsync(
        string examId,
        string candidateId,
        string deviceId,
        string sessionId,
        string preparationId,
        string packageId,
        long packageLength,
        DateTimeOffset now)
    {
        var clientNumber = (
            BitConverter.ToUInt64(Guid.NewGuid().ToByteArray()) % 90_000_000_000UL +
            10_000_000_000UL).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, """
            INSERT INTO exam.activation_devices
                (device_id, client_number, activation_date, activation_status,
                 protected_credential, credential_version, created_at, version)
            VALUES ($1, $2, $3, 'active', 'test-verifier', 1, $4, 1);
            """, parameters =>
        {
            parameters.AddWithValue(deviceId);
            parameters.AddWithValue(clientNumber);
            parameters.AddWithValue(DateOnly.FromDateTime(now.UtcDateTime));
            parameters.AddWithValue(now);
        });
        await ExecuteAsync(connection, transaction, """
            INSERT INTO exam.candidate_roster_entries
                (exam_id, candidate_id, candidate_name, class_text, admission_no)
            VALUES ($1, $2, '测试考生', '1', '10001');
            """, parameters =>
        {
            parameters.AddWithValue(examId);
            parameters.AddWithValue(candidateId);
        });
        await ExecuteAsync(connection, transaction, """
            INSERT INTO exam.exam_entry_access
                (exam_id, scheduled_start, scheduled_end, lifecycle,
                 package_ready, instructions_ready, encrypted_package_uri,
                 package_length, paper_version, instructions_version)
            VALUES ($1, $2, $3, 'scheduled', true, true, $4, $5, 2, 1);
            """, parameters =>
        {
            parameters.AddWithValue(examId);
            parameters.AddWithValue(now.AddMinutes(-30));
            parameters.AddWithValue(now.AddHours(1));
            parameters.AddWithValue($"/api/v1/resources/{Uri.EscapeDataString(packageId)}");
            parameters.AddWithValue(packageLength);
        });
        await ExecuteAsync(connection, transaction, """
            INSERT INTO exam.student_sessions
                (session_id, device_id, candidate_id, candidate_name, class_text,
                 admission_no, exam_ids, candidate_ids_by_exam, authenticated_at)
            VALUES ($1, $2, $3, '测试考生', '1', '10001', $4, $5, $6);
            """, parameters =>
        {
            parameters.AddWithValue(sessionId);
            parameters.AddWithValue(deviceId);
            parameters.AddWithValue(candidateId);
            parameters.AddWithValue(new[] { examId });
            parameters.AddWithValue(
                NpgsqlDbType.Jsonb,
                JsonSerializer.Serialize(new Dictionary<string, string> { [examId] = candidateId }));
            parameters.AddWithValue(now);
        });
        await ExecuteAsync(connection, transaction, """
            INSERT INTO exam.entry_preparations
                (preparation_id, session_id, exam_id, candidate_id, device_id,
                 paper_version, instructions_version, prepared_at)
            VALUES ($1, $2, $3, $4, $5, 2, 1, $6);
            """, parameters =>
        {
            parameters.AddWithValue(preparationId);
            parameters.AddWithValue(sessionId);
            parameters.AddWithValue(examId);
            parameters.AddWithValue(candidateId);
            parameters.AddWithValue(deviceId);
            parameters.AddWithValue(now);
        });
        await transaction.CommitAsync();
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Action<NpgsqlParameterCollection> addParameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        addParameters(command.Parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static X509Certificate2 CreateCertificate(string name)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={name}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
    }
}
