using System.Data;
using Exam.Contracts.HighAvailability;
using Exam.Infrastructure.Postgres.HighAvailability;
using Exam.IntegrationTests.Postgres;

namespace Exam.IntegrationTests.HighAvailability;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class PostgresFailoverStateStoreTests(PostgresDatabaseFixture fixture)
{
    [Fact]
    public async Task StateAuditAndEpoch_SurviveNewStoreInstance_AndRejectStaleWrite()
    {
        var serverId = $"server-{Guid.NewGuid():N}";
        var observedAt = new DateTimeOffset(2026, 9, 22, 5, 0, 0, TimeSpan.Zero);
        var first = CreateState(serverId, "node-a", 3, 1, observedAt);
        var store = new PostgresFailoverStateStore(fixture.DataSource);
        await store.SaveAsync(first);

        var promoted = CreateState(serverId, "node-b", 4, 2, observedAt.AddMinutes(1)) with
        {
            ProtectionMode = ProtectionMode.EmergencySingleNode,
            Audit =
            [
                new FailoverAuditEntry(
                    "Takeover",
                    "operator",
                    "primary isolated",
                    "node-b",
                    4,
                    observedAt.AddMinutes(1))
            ]
        };
        await store.SaveAsync(promoted);
        var restored = await new PostgresFailoverStateStore(fixture.DataSource).LoadAsync(serverId);

        Assert.NotNull(restored);
        Assert.Equal("node-b", restored.ActiveNodeId);
        Assert.Equal(4, restored.ServerEpoch);
        Assert.Equal(2, restored.Version);
        Assert.Single(restored.Audit);
        await Assert.ThrowsAsync<DBConcurrencyException>(() => store.SaveAsync(first));
    }

    private static ServerDeploymentState CreateState(
        string serverId,
        string activeNodeId,
        long epoch,
        long version,
        DateTimeOffset observedAt)
    {
        var nodes = new Dictionary<string, NodeState>(StringComparer.Ordinal)
        {
            ["node-a"] = new(
                "node-a",
                activeNodeId == "node-a" ? DeploymentNodeRole.Primary : DeploymentNodeRole.Isolated,
                true,
                false,
                0,
                true,
                true,
                observedAt),
            ["node-b"] = new(
                "node-b",
                activeNodeId == "node-b" ? DeploymentNodeRole.Primary : DeploymentNodeRole.Standby,
                true,
                activeNodeId != "node-b",
                0,
                true,
                true,
                observedAt)
        };
        return new ServerDeploymentState(
            serverId,
            activeNodeId,
            epoch,
            ProtectionMode.SynchronousDualNode,
            nodes,
            version,
            []);
    }
}
