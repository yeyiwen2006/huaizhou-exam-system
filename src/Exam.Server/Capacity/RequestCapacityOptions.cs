namespace Exam.Server.Capacity;

public sealed record RequestCapacityOptions(
    int CriticalPermits,
    int ApiPermits,
    int ResourcePermits,
    int QueueLimit = 300,
    int QueueTimeoutMilliseconds = 5000,
    int TransportConnectionLimit = 2048)
{
    public static RequestCapacityOptions Read(IConfiguration configuration, int poolMaximum)
    {
        var resources = Math.Clamp(poolMaximum / 8, 1, 8);
        var api = Math.Max(1, poolMaximum / 2);
        var result = new RequestCapacityOptions(
            configuration.GetValue("RequestCapacity:CriticalPermits", Math.Max(1, poolMaximum - resources - api - 1)),
            configuration.GetValue("RequestCapacity:ApiPermits", api),
            configuration.GetValue("RequestCapacity:ResourcePermits", resources),
            configuration.GetValue("RequestCapacity:QueueLimit", 300),
            configuration.GetValue("RequestCapacity:QueueTimeoutMilliseconds", 5000),
            configuration.GetValue("RequestCapacity:TransportConnectionLimit", 2048));
        result.Validate(poolMaximum);
        return result;
    }

    public void Validate(int poolMaximum)
    {
        if (CriticalPermits is < 1 or > 1024 || ApiPermits is < 1 or > 1024 || ResourcePermits is < 1 or > 256 ||
            QueueLimit is < 0 or > 4096 || QueueTimeoutMilliseconds is < 1 or > 120000 ||
            TransportConnectionLimit is < 300 or > 16384)
            throw new InvalidOperationException("RequestCapacity 的并发、排队、等待时间或传输连接上限不在允许范围内。");
        if (ResourcePermits >= poolMaximum || (long)ApiPermits + ResourcePermits >= poolMaximum)
            throw new InvalidOperationException("普通 API 与资源下载并发合计必须小于数据库连接池上限，为交卷、指令和后台任务保留连接空间。");
    }
}

public enum RequestWorkload { Unrestricted, Critical, Api, Resource }

public static class RequestWorkloadClassifier
{
    public static RequestWorkload Classify(HttpRequest request)
    {
        var path = new PathString(request.Path.Value?.TrimEnd('/'));
        if (path.StartsWithSegments("/hubs/exam-events"))
            return path.Value?.EndsWith("/negotiate", StringComparison.OrdinalIgnoreCase) == true
                ? RequestWorkload.Api : RequestWorkload.Unrestricted;
        if (path.Equals(new PathString("/health/ready"))) return RequestWorkload.Critical;
        if (!path.StartsWithSegments("/api")) return RequestWorkload.Unrestricted;
        if (HttpMethods.IsGet(request.Method) && path.StartsWithSegments("/api/v1/resources")) return RequestWorkload.Resource;
        if (path.StartsWithSegments("/api/v1/service-management/backups") ||
            path.StartsWithSegments("/api/v1/service-management/restores")) return RequestWorkload.Resource;
        if (path.Equals(new PathString("/api/v1/submissions/final")) ||
            path.Equals(new PathString("/api/v1/submissions/receipts")) ||
            path.Equals(new PathString("/api/v1/resources/unlock")) ||
            path.Equals(new PathString("/api/v1/participation/device-decisions")) ||
            (path.StartsWithSegments("/api/v1/commands") && !path.Equals(new PathString("/api/v1/commands/time"))))
            return RequestWorkload.Critical;
        return RequestWorkload.Api;
    }
}
