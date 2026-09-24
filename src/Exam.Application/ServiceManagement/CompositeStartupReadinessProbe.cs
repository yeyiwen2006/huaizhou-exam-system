using Exam.Contracts.ServiceManagement;

namespace Exam.Application.ServiceManagement;

public sealed class CompositeStartupReadinessProbe : IStartupReadinessProbe
{
    private readonly IStartupReadinessProbe[] _probes;

    public CompositeStartupReadinessProbe(IEnumerable<IStartupReadinessProbe> probes)
    {
        ArgumentNullException.ThrowIfNull(probes);
        _probes = probes.ToArray();
        if (_probes.Length == 0 || _probes.Any(probe => probe is null))
        {
            throw new ArgumentException("启动检查必须包含至少一个有效探针。", nameof(probes));
        }
    }

    public async Task<IReadOnlyList<StartupCheckResult>> RunAsync(
        ServiceConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var results = new List<StartupCheckResult>();
        foreach (var probe in _probes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var checks = await probe.RunAsync(configuration, cancellationToken);
            if (checks.Count == 0)
            {
                throw new InvalidOperationException("启动探针未返回结果，不能视为检查通过。");
            }

            results.AddRange(checks);
        }

        return results;
    }
}
