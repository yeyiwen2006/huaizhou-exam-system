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
            cancellationToken.ThrowIfCancellationRequested();
            if (checks is null || checks.Count == 0 || checks.Any(c => c is null || string.IsNullOrWhiteSpace(c.Name)))
            {
                throw new InvalidOperationException("启动探针未返回结果，不能视为检查通过。");
            }

            results.AddRange(checks);
        }

        if (results.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != results.Count)
            throw new InvalidOperationException("启动检查名称重复，不能合并为有效就绪结果。");
        return results.AsReadOnly();
    }
}
