using Exam.Contracts.Common;

namespace Exam.Server.Runtime;

public static class ExamTransportConfiguration
{
    public static void AddExamServerTransport(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSignalR().AddJsonProtocol(options =>
        {
            // HTTP and SignalR carry the same contracts, including string-valued enums.
            // HTTP JSON options do not apply to hub argument binding.
            options.PayloadSerializerOptions = JsonDefaults.CreateServerTransport();
        });
        services.ConfigureHttpJsonOptions(options =>
        {
            var defaults = JsonDefaults.CreateServerTransport();
            options.SerializerOptions.PropertyNamingPolicy = defaults.PropertyNamingPolicy;
            options.SerializerOptions.PropertyNameCaseInsensitive = defaults.PropertyNameCaseInsensitive;
            foreach (var converter in defaults.Converters)
            {
                options.SerializerOptions.Converters.Add(converter);
            }
        });
    }
}
