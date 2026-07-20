using System.Reflection;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class TeacherDraftsAutogenJobServiceSecurityTests
{
    [Fact]
    public async Task StopAsync_cancels_and_awaits_running_job_before_returning()
    {
        using var scopeFactory = new ShutdownBlockingScopeFactory();
        var service = new TeacherDraftsAutogenJobService(
            scopeFactory,
            new CapturingLogger<TeacherDraftsAutogenJobService>());
        var started = service.Start(CreateValidRequest());
        Task? stopTask = null;

        try
        {
            Assert.True(scopeFactory.WaitUntilJobIsBlocked(TimeSpan.FromSeconds(2)));
            stopTask = service.StopAsync(CancellationToken.None);
            await Task.Delay(50);
            Assert.False(stopTask.IsCompleted);
        }
        finally
        {
            scopeFactory.ReleaseJob();
        }

        Assert.NotNull(stopTask);
        await stopTask.WaitAsync(TimeSpan.FromSeconds(2));
        var status = service.Get(started.JobId);
        Assert.NotNull(status);
        Assert.Equal(AutoGenJobState.Canceled, status.State);
        Assert.True(status.CancellationRequested);
    }

    [Fact]
    public async Task Start_rejects_new_job_after_service_stopping_begins()
    {
        var service = new TeacherDraftsAutogenJobService(
            new ThrowingScopeFactory(() => new InvalidOperationException("Сховище недоступне.")),
            new CapturingLogger<TeacherDraftsAutogenJobService>());
        await service.StopAsync(CancellationToken.None);

        var exception = Assert.Throws<AutoGenJobCapacityException>(
            () => service.Start(CreateValidRequest()));

        Assert.Contains("завершує роботу", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unexpected_exception_is_logged_but_not_exposed_in_job_status()
    {
        const string secret = "mysql://internal-user:internal-password@database/private";
        var logger = new CapturingLogger<TeacherDraftsAutogenJobService>();
        var service = new TeacherDraftsAutogenJobService(
            new ThrowingScopeFactory(() => new InvalidOperationException(secret)),
            logger);
        var runtime = CreateRuntime(CreateValidRequest());

        await InvokeRunAsync(service, runtime).WaitAsync(TimeSpan.FromSeconds(2));

        var status = GetStatus(runtime);
        Assert.Equal(AutoGenJobState.Failed, status.State);
        Assert.NotNull(status.Error);
        Assert.Contains("внутрішня помилка", status.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(status.JobId, status.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, status.Error, StringComparison.Ordinal);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Error
            && entry.Exception is InvalidOperationException
            && entry.Exception.Message == secret
            && entry.Message.Contains(status.JobId, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("validation")]
    [InlineData("capacity")]
    public async Task Expected_failure_preserves_safe_message_and_job_correlation(string failureKind)
    {
        var safeMessage = failureKind == "validation"
            ? "Параметри тестового завдання некоректні."
            : "Черга тестових завдань заповнена.";
        var logger = new CapturingLogger<TeacherDraftsAutogenJobService>();
        var service = new TeacherDraftsAutogenJobService(
            new ThrowingScopeFactory(() => failureKind == "validation"
                ? new AutoGenJobValidationException(safeMessage)
                : new AutoGenJobCapacityException(safeMessage)),
            logger);
        var runtime = CreateRuntime(CreateValidRequest());

        await InvokeRunAsync(service, runtime).WaitAsync(TimeSpan.FromSeconds(2));

        var status = GetStatus(runtime);
        Assert.Equal(AutoGenJobState.Failed, status.State);
        Assert.NotNull(status.Error);
        Assert.Contains(safeMessage, status.Error, StringComparison.Ordinal);
        Assert.Contains(status.JobId, status.Error, StringComparison.Ordinal);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning
            && entry.Exception?.Message == safeMessage
            && entry.Message.Contains(status.JobId, StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Unrequested_cancellation_uses_safe_interruption_message()
    {
        const string internalMessage = "Переривання внутрішнього драйвера з приватними деталями.";
        var logger = new CapturingLogger<TeacherDraftsAutogenJobService>();
        var service = new TeacherDraftsAutogenJobService(
            new ThrowingScopeFactory(() => new OperationCanceledException(internalMessage)),
            logger);
        var runtime = CreateRuntime(CreateValidRequest());

        await InvokeRunAsync(service, runtime).WaitAsync(TimeSpan.FromSeconds(2));

        var status = GetStatus(runtime);
        Assert.Equal(AutoGenJobState.Failed, status.State);
        Assert.False(status.CancellationRequested);
        Assert.NotNull(status.Error);
        Assert.Contains("було перервано", status.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(status.JobId, status.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(internalMessage, status.Error, StringComparison.Ordinal);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning
            && entry.Exception is OperationCanceledException
            && entry.Exception.Message == internalMessage);
    }

    private static AutoGenJobRequest CreateValidRequest()
        => new(
            Kind: AutoGenJobKind.Generate,
            FromDate: new DateOnly(2026, 9, 1),
            ToDate: new DateOnly(2026, 9, 7),
            CourseId: 1,
            GroupIds: new List<int> { 1 },
            ModuleHours: new Dictionary<int, int> { [1] = 1 },
            Days: WeekPreset.MonFri,
            ClearExisting: true,
            SoftFill: false,
            PreflightOnly: false);

    private static object CreateRuntime(AutoGenJobRequest request)
    {
        var runtimeType = typeof(TeacherDraftsAutogenJobService)
            .GetNestedType("AutoGenJobRuntime", BindingFlags.NonPublic);
        Assert.NotNull(runtimeType);
        var runtime = Activator.CreateInstance(
            runtimeType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { request },
            culture: null);
        return Assert.IsAssignableFrom<object>(runtime);
    }

    private static Task InvokeRunAsync(TeacherDraftsAutogenJobService service, object runtime)
    {
        var method = typeof(TeacherDraftsAutogenJobService)
            .GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task>(method.Invoke(service, new[] { runtime }));
    }

    private static AutoGenJobStatus GetStatus(object runtime)
    {
        var method = runtime.GetType().GetMethod("ToDto", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);
        return Assert.IsType<AutoGenJobStatus>(method.Invoke(runtime, null));
    }

    private sealed class ThrowingScopeFactory(Func<Exception> exceptionFactory) : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
            => throw exceptionFactory();
    }

    private sealed class ShutdownBlockingScopeFactory : IServiceScopeFactory, IDisposable
    {
        private readonly ManualResetEventSlim _jobBlocked = new(false);
        private readonly ManualResetEventSlim _releaseJob = new(false);
        private int _createScopeCount;

        public IServiceScope CreateScope()
        {
            var call = Interlocked.Increment(ref _createScopeCount);
            if (call != 3)
            {
                throw new InvalidOperationException("Тестове сховище стану недоступне.");
            }

            _jobBlocked.Set();
            _releaseJob.Wait(TimeSpan.FromSeconds(5));
            throw new OperationCanceledException("Тестове переривання фонового завдання.");
        }

        public bool WaitUntilJobIsBlocked(TimeSpan timeout)
            => _jobBlocked.Wait(timeout);

        public void ReleaseJob()
            => _releaseJob.Set();

        public void Dispose()
        {
            _releaseJob.Set();
            _jobBlocked.Dispose();
            _releaseJob.Dispose();
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<LogEntry> _entries = new();
        private readonly object _sync = new();

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_sync)
                {
                    return _entries.ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel)
            => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_sync)
            {
                _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
            }
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
}
