using Exam.Server.Diagnostics;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Exam.Server.Capacity;

public static class CapacityRuntimeRegistration
{
    public static void AddExamCapacity(this IServiceCollection services, IConfiguration configuration,
        DatabaseCapacityOptions database)
    {
        var request = RequestCapacityOptions.Read(configuration, database.MaximumPoolSize);
        services.AddSingleton(database);
        services.AddSingleton<DatabaseCapacityProbe>();
        services.AddHostedService<DatabaseCapacityStartupCheck>();
        services.AddSingleton(request);
        services.AddSingleton<RequestAdmissionControl>();
        services.AddSingleton<ExamRuntimeMetrics>();
        services.AddTransient<IStartupFilter, CapacityStartupFilter>();
        services.Configure<KestrelServerOptions>(options =>
        {
            options.Limits.MaxConcurrentConnections = request.TransportConnectionLimit;
            options.Limits.MaxConcurrentUpgradedConnections = request.TransportConnectionLimit;
        });
    }
}

internal sealed class CapacityStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        // Before device/operator authentication and active-node database work. Rejections perform no DB access,
        // disclose no request data, and include the same server identity envelope as business errors.
        app.UseMiddleware<RequestAdmissionMiddleware>();
        next(app);
    };
}
