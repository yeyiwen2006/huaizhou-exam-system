using Exam.Infrastructure.ServiceManagement;

namespace Exam.Server.Runtime;

/// <summary>Only fixed metadata crosses into the protected file; messages and exceptions are discarded.</summary>
public sealed class ProtectedRuntimeLogProvider(string path) : ILoggerProvider
{
    private readonly ProtectedRuntimeLogWriter _writer = new(path);
    public ILogger CreateLogger(string categoryName) => new MetadataLogger(_writer, categoryName);
    public void Dispose() { }

    private sealed class MetadataLogger(ProtectedRuntimeLogWriter writer, string category) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning &&
            ProtectedRuntimeLogWriter.ValidCategory(category);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) writer.Write(category, logLevel.ToString(), eventId.Id);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
