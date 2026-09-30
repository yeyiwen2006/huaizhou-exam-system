namespace Exam.IntegrationTests.ServiceManagement;

public sealed class NativePhysicalWindowsFactAttribute : FactAttribute
{
    public NativePhysicalWindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "本机 PostgreSQL 临时实例适配仅支持 Windows。";
    }
}

public sealed class NativePhysicalPostgresToolsPathTests
{
    [NativePhysicalWindowsFact]
    public void SourceMustBelongToAnExplicitlyNamedTemporaryTestDeployment()
    {
        var approved = Path.Combine(Path.GetTempPath(), "codex-f01-native-synthetic", "postgres-source");
        Assert.Equal(Path.GetFullPath(approved), PhysicalPostgresTestPaths.RequireSourceDirectory(approved));
        Assert.Throws<InvalidOperationException>(() => PhysicalPostgresTestPaths.RequireSourceDirectory(
            Path.Combine(Path.GetTempPath(), "postgres-source")));
        Assert.Throws<InvalidOperationException>(() => PhysicalPostgresTestPaths.RequireSourceDirectory(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "codex-f01-native-synthetic", "postgres-source")));
        Assert.Throws<InvalidOperationException>(() => PhysicalPostgresTestPaths.RequireSourceDirectory(
            Path.Combine(Path.GetTempPath(), "ordinary-database", "postgres-source")));
    }

    [NativePhysicalWindowsFact]
    public void CopyAndCleanupBoundariesExcludeParentAndUnrelatedDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "f01-physical-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(root, PhysicalPostgresTestPaths.RequireWorkingRoot(root));
        Assert.Equal(Path.Combine(root, "working", "slot", "database"),
            PhysicalPostgresTestPaths.RequireChild(root, Path.Combine(root, "working", "slot", "database")));
        Assert.Throws<InvalidOperationException>(() => PhysicalPostgresTestPaths.RequireWorkingRoot(Path.GetTempPath()));
        Assert.Throws<InvalidOperationException>(() => PhysicalPostgresTestPaths.RequireChild(root, root));
        Assert.Throws<InvalidOperationException>(() => PhysicalPostgresTestPaths.RequireChild(root, Path.Combine(root, "..", "original")));
    }
}
