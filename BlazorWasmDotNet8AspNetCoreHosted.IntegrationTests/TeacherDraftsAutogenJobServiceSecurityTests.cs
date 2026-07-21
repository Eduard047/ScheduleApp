using System.Reflection;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class TeacherDraftsAutogenJobServiceSecurityTests
{
    [Fact]
    public async Task StopAsync_cancels_and_awaits_running_job_before_returning()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Courses.Add(new Course
            {
                Id = 1,
                Name = "Курс перевірки зупинки",
                DurationWeeks = 52,
                AcademicPeriodStartDate = new DateOnly(2026, 1, 1)
            });
            await db.SaveChangesAsync();
        }
        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(options));
        await using var provider = services.BuildServiceProvider();
        var service = new TeacherDraftsAutogenJobService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new CapturingLogger<TeacherDraftsAutogenJobService>());
        var executionGate = await HoldGateAsync(service, "_executionGate");
        var started = service.Start(CreateValidRequest());
        var persistenceGate = await HoldGateAsync(service, "_persistenceGate");
        Task? stopTask = null;

        try
        {
            stopTask = service.StopAsync(CancellationToken.None);
            await Task.Delay(50);
            Assert.False(stopTask.IsCompleted);
        }
        finally
        {
            persistenceGate.Release();
        }

        Assert.NotNull(stopTask);
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        executionGate.Release();
        var status = service.Get(started.JobId);
        Assert.NotNull(status);
        Assert.Equal(AutoGenJobState.Canceled, status.State);
        Assert.True(status.CancellationRequested);
        Assert.DoesNotContain("користувач", status.CurrentStage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Initial_persistence_failure_does_not_queue_or_expose_a_local_job()
    {
        var service = new TeacherDraftsAutogenJobService(
            new ThrowingScopeFactory(() => new InvalidOperationException("Сховище недоступне.")),
            new CapturingLogger<TeacherDraftsAutogenJobService>());

        Assert.Throws<AutoGenJobPersistenceException>(() => service.Start(CreateValidRequest()));

        Assert.Equal(0, GetPrivateCollectionCount(service, "_jobs"));
        Assert.Equal(0, GetPrivateCollectionCount(service, "_runningTasks"));
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

    private static async Task<SemaphoreSlim> HoldGateAsync(
        TeacherDraftsAutogenJobService service,
        string fieldName)
    {
        var field = typeof(TeacherDraftsAutogenJobService).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        var gate = Assert.IsType<SemaphoreSlim>(field?.GetValue(service));
        await gate.WaitAsync();
        return gate;
    }

    private static int GetPrivateCollectionCount(
        TeacherDraftsAutogenJobService service,
        string fieldName)
    {
        var field = typeof(TeacherDraftsAutogenJobService).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        var collection = field?.GetValue(service);
        var count = collection?.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
        return Assert.IsType<int>(count?.GetValue(collection));
    }

    private sealed class ThrowingScopeFactory(Func<Exception> exceptionFactory) : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
            => throw exceptionFactory();
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
