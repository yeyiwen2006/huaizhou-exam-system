using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.HighAvailability;
using Exam.Infrastructure.ServiceManagement;
using Npgsql;

namespace Exam.IntegrationTests.HighAvailability;

public sealed class NativeWalArchiveFactAttribute : FactAttribute
{
    public NativeWalArchiveFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("EXAM_TEST_NATIVE_WAL_ARCHIVE") != "1")
            Skip = "Requires explicit Windows native WAL archive opt-in; never controls existing services.";
    }
}

[Trait("Category", "Postgres")]
[Trait("Category", "PostgresPhysical")]
public sealed class NativeWalArchivingTests
{
    [NativeWalArchiveFact]
    public async Task ControlledRuntimeArchivesAfterRestartAndStandbyPromotion()
    {
        var bin = Environment.GetEnvironmentVariable("EXAM_TEST_NATIVE_POSTGRES_BIN")
            ?? throw new InvalidOperationException("Explicit native PostgreSQL bin directory required.");
        Assert.True(Path.IsPathFullyQualified(bin));
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Huaizhou.ExamSystem.sln"))) repo = repo.Parent;
        Assert.NotNull(repo);
        var root = Path.Combine(Path.GetTempPath(), "native-wal-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var scripts = Path.Combine(repo.FullName, "deploy", "postgresql", "scripts");
        var dataA = Path.Combine(root, "a"); var dataB = Path.Combine(root, "b");
        var portA = Port(); var portB = Port(); while (portB == portA) portB = Port();
        string Connection(int port) => $"Host=127.0.0.1;Port={port};Username=postgres;Database=postgres;SSL Mode=Disable;Pooling=false;Timeout=2;Command Timeout=5";
        var nodes = new Dictionary<string, FailoverNodeDeployment> { ["node_a"] = Node("node_a", portA), ["node_b"] = Node("node_b", portB) };
        var a = Options("node_a", dataA); var b = Options("node_b", dataB);
        var processes = new List<Process>(); var outputs = new List<Task<string>>();
        var runner = new ExternalProcessRunner();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(0);
        try
        {
            await Tool("initdb.exe", ["-D", dataA, "-U", "postgres", "-A", "trust", "--encoding=UTF8", "--no-locale"]);
            await File.WriteAllTextAsync(a.PostgresConfigurationPath, "listen_addresses='127.0.0.1'\nwal_level=replica\nmax_wal_senders=5\n" +
                $"hba_file='{dataA.Replace('\\', '/')}/pg_hba.conf'\n", encoding);
            // Copied or changed automatic settings cannot silently disable archive or inject a library/command.
            await File.WriteAllTextAsync(Path.Combine(dataA, "postgresql.auto.conf"),
                "archive_mode=off\narchive_command='exit 0'\narchive_library='synthetic_untrusted_library'\n", encoding);
            await Start(a);
            Assert.False((await Probe(a)).Passed); // No successful archive receipt yet.
            await Sql(a, "CREATE TABLE synthetic_wal(id integer primary key); INSERT INTO synthetic_wal VALUES(1); SELECT pg_switch_wal();");
            await Wait(async () => (await Probe(a)).Passed);
            Assert.NotEmpty(Directory.GetFiles(a.WalArchiveDirectory));
            await Stop(a);
            await Start(a);
            await Sql(a, "INSERT INTO synthetic_wal VALUES(2); SELECT pg_switch_wal();");
            await Wait(async () => (await Probe(a)).Passed);

            var connection = new NpgsqlConnectionStringBuilder(nodes["node_a"].ConnectionString);
            await Tool("pg_basebackup.exe", ["--pgdata", dataB, "--write-recovery-conf", "--wal-method=stream", "--checkpoint=fast", "--no-password", "--no-clean"],
                new Dictionary<string,string> { ["PGHOST"]="127.0.0.1", ["PGPORT"]=connection.Port.ToString(), ["PGUSER"]="postgres", ["PGSSLMODE"]="disable", ["PGAPPNAME"]="node_b" });
            await File.WriteAllTextAsync(b.PostgresConfigurationPath, "listen_addresses='127.0.0.1'\nwal_level=replica\nmax_wal_senders=5\n" +
                $"hba_file='{dataB.Replace('\\', '/')}/pg_hba.conf'\n", encoding);
            await Start(b);
            await Wait(async () => Convert.ToInt64(await Sql(b, "SELECT count(*) FROM synthetic_wal")) == 2);
            await Stop(a); // Real old database stopped; no service or network fencing claim.
            Assert.Equal(true, await Sql(b, "SELECT pg_promote(true,30)"));
            await Sql(b, "INSERT INTO synthetic_wal VALUES(3); SELECT pg_switch_wal();");
            await Wait(async () => (await Probe(b)).Passed);
            Assert.NotEmpty(Directory.GetFiles(b.WalArchiveDirectory));
            Assert.Equal(3L, await Sql(b, "SELECT count(*) FROM synthetic_wal"));

            var next = (string)(await Sql(b, "SELECT pg_walfile_name(pg_current_wal_lsn())"))!;
            var protectedOriginal = Path.Combine(b.WalArchiveDirectory, next);
            await File.WriteAllTextAsync(protectedOriginal, "synthetic conflicting archive must be preserved");
            await Sql(b, "INSERT INTO synthetic_wal VALUES(4); SELECT pg_switch_wal();");
            await Wait(async () => Convert.ToInt64(await Sql(b, "SELECT failed_count FROM pg_stat_archiver")) > 0);
            Assert.False((await Probe(b)).Passed);
            Assert.Equal("synthetic conflicting archive must be preserved", await File.ReadAllTextAsync(protectedOriginal));
        }
        finally
        {
            foreach (var options in new[] { b, a })
                if (File.Exists(Path.Combine(options.PostgresDataDirectory, "postmaster.pid"))) await Stop(options);
            foreach (var process in processes) { if (!process.HasExited) process.Kill(entireProcessTree:true); process.Dispose(); }
            var captured = await Task.WhenAll(outputs);
            await File.WriteAllTextAsync(Path.Combine(root, "native-processes.log"), string.Join("\n", captured));
            // Keep only this test's synthetic data on failure for local diagnosis; it is ignored by Git.
        }

        FailoverNodeDeployment Node(string id, int port) => new(id, Connection(port), Path.Combine(root, id+"-resources"), "synthetic", root, "https://synthetic.test/");
        HighAvailabilityManagementOptions Options(string id, string data)
        {
            var archive = Path.Combine(root, id+"-archive"); Directory.CreateDirectory(archive);
            return new("synthetic", id, nodes, Path.Combine(root,id+"-selection"), Path.Combine(root,id+"-rebuild"), bin, data,
                "SyntheticPostgres", "SyntheticBusiness", "synthetic", Path.Combine(root,id+".conf"), archive, scripts);
        }
        async Task Tool(string name, IReadOnlyList<string> args, IReadOnlyDictionary<string,string>? environment=null)
        {
            var result = await runner.RunAsync(Path.Combine(bin,name),args,environment??new Dictionary<string,string>(),TimeSpan.FromMinutes(2));
            Assert.True(result.ExitCode==0,result.StandardError);
        }
        async Task Start(HighAvailabilityManagementOptions options)
        {
            _ = PostgresHighAvailabilityManagementPlatform.BuildPostgresServiceCommand(options);
            var start = new ProcessStartInfo(Path.Combine(bin,"postgres.exe")) { UseShellExecute=false,CreateNoWindow=true,
                RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=root };
            start.ArgumentList.Add("-D");start.ArgumentList.Add(options.PostgresDataDirectory);
            foreach(var arg in PostgresHighAvailabilityManagementPlatform.BuildPostgresRuntimeArguments(options))start.ArgumentList.Add(arg);
            var process=Process.Start(start)!;processes.Add(process);
            outputs.Add(process.StandardOutput.ReadToEndAsync());outputs.Add(process.StandardError.ReadToEndAsync());
            await Wait(async()=> { Assert.False(process.HasExited,process.HasExited ? await outputs[^1] : "");
                try { return (int)(await Sql(options,"SELECT 1"))! == 1; } catch(NpgsqlException) { return false; } });
        }
        Task Stop(HighAvailabilityManagementOptions options) => Tool("pg_ctl.exe",["-D",options.PostgresDataDirectory,"-m","fast","-w","-t","30","stop"]);
        async Task<object?> Sql(HighAvailabilityManagementOptions options,string sql)
        {
            await using var conn=new NpgsqlConnection(nodes[options.LocalNodeId].ConnectionString);await conn.OpenAsync();
            await using var command=new NpgsqlCommand(sql,conn);return await command.ExecuteScalarAsync();
        }
        async Task<StartupCheckResult> Probe(HighAvailabilityManagementOptions options) => Assert.Single(await
            new PostgresWalArchiveReadinessProbe(nodes[options.LocalNodeId].ConnectionString,options).RunAsync(
                new("synthetic",options.LocalNodeId,"127.0.0.1",1,root,true,1),default));
    }
    private static int Port(){var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();try{return ((IPEndPoint)listener.LocalEndpoint).Port;}finally{listener.Stop();}}
    private static async Task Wait(Func<Task<bool>> predicate)
    {
        var deadline=DateTime.UtcNow.AddSeconds(45);
        while(!await predicate()){if(DateTime.UtcNow>deadline)throw new TimeoutException("Synthetic native WAL condition was not reached.");await Task.Delay(200);}
    }
}
