using Npgsql;

namespace Exam.IntegrationTests.Postgres;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class PostgresConnectionPoolTests(PostgresDatabaseFixture fixture)
{
    [Fact]
    public async Task PoolIsBounded_QueuedRequestsResumeAndOperationsConnectionStillWorks()
    {
        const int maximumConnections = 16;
        var settings = new NpgsqlConnectionStringBuilder(fixture.DataSource.ConnectionString);
        Assert.True(settings.Pooling);
        Assert.Equal(maximumConnections, settings.MaxPoolSize);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var held = new List<NpgsqlConnection>();
        try
        {
            for (var index = 0; index < maximumConnections; index++)
                held.Add(await fixture.DataSource.OpenConnectionAsync(deadline.Token));

            // The business pool must not consume every database connection. A separate
            // non-pooled connection represents health checks and operational tooling.
            var observerSettings = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                Pooling = false,
                ApplicationName = "Exam.IntegrationTests.PoolObserver"
            };
            await using var observer = new NpgsqlConnection(observerSettings.ConnectionString);
            await observer.OpenAsync(deadline.Token);
            await using var count = new NpgsqlCommand("""
                SELECT COUNT(*) FROM pg_stat_activity
                 WHERE datname = current_database() AND application_name = $1
                   AND backend_type = 'client backend';
                """, observer);
            count.Parameters.AddWithValue(Assert.IsType<string>(settings.ApplicationName));
            Assert.Equal((long)maximumConnections, await count.ExecuteScalarAsync(deadline.Token));

            // Cancelling a request waiting for the pool must not leak a slot or create
            // another physical connection. No sleep or server connection limit change.
            using (var cancelled = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
            {
                var waiting = fixture.DataSource.OpenConnectionAsync(cancelled.Token).AsTask();
                Assert.False(waiting.IsCompleted);
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            }

            var next = fixture.DataSource.OpenConnectionAsync(deadline.Token).AsTask();
            Assert.False(next.IsCompleted);
            await held[0].DisposeAsync();
            held.RemoveAt(0);
            await using var resumed = await next;
            Assert.Equal((long)maximumConnections, await count.ExecuteScalarAsync(deadline.Token));
        }
        finally
        {
            foreach (var connection in held)
                await connection.DisposeAsync();
        }
    }
}
