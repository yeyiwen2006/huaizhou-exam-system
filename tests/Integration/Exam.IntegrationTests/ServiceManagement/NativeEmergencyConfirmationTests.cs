using Exam.Contracts.Common;
using Exam.Contracts.HighAvailability;
using Exam.Domain.Common;
using Exam.Infrastructure.HighAvailability;
using Exam.IntegrationTests.Postgres;
using Npgsql;

namespace Exam.IntegrationTests.ServiceManagement;

[Trait("Category", "Postgres")]
[Trait("Category", "PostgresPhysical")]
[Collection(PostgresCollection.Name)]
public sealed class NativeEmergencyConfirmationTests(PostgresDatabaseFixture database)
{
    [NativeConfirmationFact]
    public async Task EmergencyFenceFlushesLocallyWithoutWaitingForAnAbsentSynchronousStandby()
    {
        // Configuration changes are allowed only on the explicitly opted-in, path/port/identity-checked
        // temporary PostgreSQL source. The serialized collection prevents interference with other tests.
        await NativePhysicalPostgresTools.ValidateConfiguredSourceAsync();
        var serverId = "confirmation-review-" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), serverId);
        var resources = Path.Combine(root, "resources");
        var journal = Path.Combine(root, "confirmations");
        Directory.CreateDirectory(resources);
        Directory.CreateDirectory(journal);
        await using var control = new NpgsqlConnection(database.ConnectionString);
        await control.OpenAsync();
        await ExecuteAsync("SET synchronous_commit=local");
        var originalStandbys = await SettingAsync();
        try
        {
            await using (var transaction = await control.BeginTransactionAsync())
            {
                await using var seed = new NpgsqlCommand("""
                    INSERT INTO exam.server_state VALUES (@server,'primary',1,'EmergencySingleNode',1,now());
                    INSERT INTO exam.server_nodes VALUES (@server,'primary','Primary',true,false,0,true,true,now());
                    """, control, transaction);
                seed.Parameters.AddWithValue("server", serverId);
                await seed.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }
            const string unavailableStandby = "FIRST 1 (synthetic_absent_confirmation_peer)";
            await ConfigureAsync(unavailableStandby);
            var store = new FileConfirmationStore(journal);
            var peer = new UnavailablePeer();
            var confirmation = new PostgresBusinessConfirmation(database.DataSource, store,
                serverId, "primary", "standby", 1, resources, peer);
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                await confirmation.ConfirmAsync(deadline.Token);
            Assert.True(store.Read()!.Confirmed);
            Assert.Equal(ProtectionMode.EmergencySingleNode, store.Read()!.Record.Mode);
            Assert.Equal(0, peer.Calls);
            await using (var flush = new NpgsqlCommand("SELECT pg_current_wal_flush_lsn()::text", control))
                Assert.True(FileConfirmationStore.Lsn((string)(await flush.ExecuteScalarAsync())!) >=
                    FileConfirmationStore.Lsn(store.Read()!.Record.FlushLsn));

            // A local WAL fence alone must never turn the same unavailable peer into dual-node success.
            await using (var dual = new NpgsqlCommand("""
                UPDATE exam.server_state SET protection_mode='SynchronousDualNode',state_version=state_version+1
                WHERE server_id=$1
                """, control))
            {
                dual.Parameters.AddWithValue(serverId);
                await dual.ExecuteNonQueryAsync();
            }
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                var error = await Assert.ThrowsAsync<DomainRuleException>(() => confirmation.ConfirmAsync(deadline.Token));
                Assert.Equal(ErrorCodes.ServiceRecovering, error.Code);
                Assert.False(deadline.IsCancellationRequested);
            }
            Assert.Equal(1, peer.Calls);
            Assert.False(store.Read()!.Confirmed);
            Assert.Equal(ProtectionMode.SynchronousDualNode, store.Read()!.Record.Mode);
        }
        finally
        {
            await ConfigureAsync(originalStandbys);
            await using var cleanup = new NpgsqlCommand("DELETE FROM exam.server_state WHERE server_id=$1", control);
            cleanup.Parameters.AddWithValue(serverId);
            await cleanup.ExecuteNonQueryAsync();
            Directory.Delete(root, recursive: true);
        }

        async Task ExecuteAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, control);
            await command.ExecuteNonQueryAsync();
        }
        async Task<string> SettingAsync()
        {
            await using var command = new NpgsqlCommand("SHOW synchronous_standby_names", control);
            return (string)(await command.ExecuteScalarAsync())!;
        }
        async Task ConfigureAsync(string value)
        {
            await ExecuteAsync("ALTER SYSTEM SET synchronous_standby_names = '" + value.Replace("'", "''", StringComparison.Ordinal) + "'");
            await ExecuteAsync("SELECT pg_reload_conf()");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (await SettingAsync() != value) await Task.Delay(20, deadline.Token);
        }
    }

    private sealed class UnavailablePeer : IDurableConfirmationPeer
    {
        public int Calls { get; private set; }
        public Task<DurableConfirmation> ConfirmAsync(DurableConfirmation record, CancellationToken token)
        {
            Calls++;
            throw new IOException("Synthetic standby is deliberately unavailable.");
        }
        public Task<ConfirmationHead?> ReadAsync(CancellationToken token) => throw new NotSupportedException();
    }
}
