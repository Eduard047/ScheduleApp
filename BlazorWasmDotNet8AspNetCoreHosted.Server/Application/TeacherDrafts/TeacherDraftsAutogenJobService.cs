using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

public sealed class AutoGenJobValidationException(string message) : Exception(message);

public sealed class AutoGenJobCapacityException(string message) : Exception(message);

public sealed class AutoGenJobConflictException(string message) : Exception(message);

public sealed class AutoGenJobPersistenceException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class AutoGenJobCommitOutcomeUnknownException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class TeacherDraftsAutogenJobService : IHostedService
{
    private const int MaxRangeDays = 370;
    private const int MaxGroupCount = 200;
    private const int MaxModuleHourEntryCount = 200;
    private const int MaxHoursPerModulePerWeek = 500;
    private const int MaxPreferredRoomCountPerGroup = 500;
    private const int MaxPreferredFirstSlotOrderOverride = 64;
    private const int MaxRecentRepeatWindowDays = 31;
    private const int MaxDistinctModulesPerDay = 64;
    private const double MaxSoftPenaltyWeight = 1_000d;
    private const int MaxTitleLength = 256;
    private const int MaxOutstandingJobCount = 8;
    private const int MaxRetainedTerminalJobCount = 200;
    private const string GlobalExecutionLockBaseName = "scheduleapp:teacher-drafts-autogen";
    private const string FullJobRollbackWarning =
        "Усі зміни автогенерації за вибраний період повністю відкочено; жодної нової чернетки не збережено.";
    private static readonly DateOnly MinSupportedDate = new(2000, 1, 1);
    private static readonly DateOnly MaxSupportedDate = new(2100, 12, 24);
    private static readonly TimeSpan DefaultJobLeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultJobHeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultCancellationLeaseGrace = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultTerminalPersistenceHorizon = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SqliteExclusiveExecutionLeaseDuration = TimeSpan.FromHours(6);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TeacherDraftsAutogenJobService> _logger;
    private readonly IHostApplicationLifetime? _applicationLifetime;
    private readonly TimeSpan _jobLeaseDuration;
    private readonly TimeSpan _jobHeartbeatInterval;
    private readonly TimeSpan _cancellationLeaseGrace;
    private readonly TimeSpan _terminalPersistenceHorizon;
    private readonly Func<AppDbContext, DateTime> _databaseUtcNow;
    private readonly Func<AppDbContext, CancellationToken, Task<DateTime>> _databaseUtcNowAsync;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly ConcurrentDictionary<string, AutoGenJobRuntime> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _runningTasks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _sqliteExclusiveExecutions = new(StringComparer.Ordinal);
    private readonly object _startSync = new();
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly SemaphoreSlim _persistenceGate = new(1, 1);
    private int _stopping;
    private static readonly TimeSpan CompletedJobTtl = TimeSpan.FromHours(6);
    private static readonly JsonSerializerOptions PersistenceJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public TeacherDraftsAutogenJobService(
        IServiceScopeFactory scopeFactory,
        ILogger<TeacherDraftsAutogenJobService> logger)
        : this(scopeFactory, logger, null)
    {
    }

    public TeacherDraftsAutogenJobService(
        IServiceScopeFactory scopeFactory,
        ILogger<TeacherDraftsAutogenJobService> logger,
        IHostApplicationLifetime? applicationLifetime)
        : this(
            scopeFactory,
            logger,
            applicationLifetime,
            DefaultJobLeaseDuration,
            DefaultJobHeartbeatInterval,
            DefaultCancellationLeaseGrace,
            DefaultTerminalPersistenceHorizon,
            databaseUtcNowOverride: null)
    {
    }

    private TeacherDraftsAutogenJobService(
        IServiceScopeFactory scopeFactory,
        ILogger<TeacherDraftsAutogenJobService> logger,
        IHostApplicationLifetime? applicationLifetime,
        TimeSpan jobLeaseDuration,
        TimeSpan jobHeartbeatInterval,
        TimeSpan cancellationLeaseGrace,
        TimeSpan terminalPersistenceHorizon,
        Func<AppDbContext, DateTime>? databaseUtcNowOverride)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _applicationLifetime = applicationLifetime;
        _jobLeaseDuration = jobLeaseDuration;
        _jobHeartbeatInterval = jobHeartbeatInterval;
        _cancellationLeaseGrace = cancellationLeaseGrace;
        _terminalPersistenceHorizon = terminalPersistenceHorizon;
        _databaseUtcNow = databaseUtcNowOverride ?? ReadDatabaseUtcNow;
        _databaseUtcNowAsync = databaseUtcNowOverride is null
            ? ReadDatabaseUtcNowAsync
            : (db, _) => Task.FromResult(databaseUtcNowOverride(db));
    }

    public AutoGenJobStartResult Start(AutoGenJobRequest request)
    {
        ThrowIfStopping();
        var normalized = NormalizeRequest(request);
        var job = new AutoGenJobRuntime(normalized);
        lock (_startSync)
        {
            ThrowIfStopping();
            CleanupOldJobs();
            if (_jobs.TryGetValue(job.JobId, out var existingJob))
            {
                EnsureMatchingRequest(job.JobId, job.RequestHash, existingJob.RequestHash);
                return new AutoGenJobStartResult(job.JobId, existingJob.ToDto());
            }
            var persisted = CreateOrReadPersistedJob(job);
            if (!persisted.Created)
            {
                return new AutoGenJobStartResult(job.JobId, persisted.Status);
            }
            _jobs[job.JobId] = job;
            QueueJob(job);
        }
        return new AutoGenJobStartResult(job.JobId, job.ToDto());
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (await HasLegacyNonTerminalRunsAsync(db, cancellationToken))
            {
                _logger.LogCritical(
                    "Виявлено незавершені завдання автогенерації попередньої версії без lease-власника. Нові запуски заблоковано до безпечного завершення оновлення.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Не вдалося виконати стартову перевірку lease-стану автогенерації.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        List<AutoGenJobRuntime> activeJobs;
        Task[] runningTasks;
        lock (_startSync)
        {
            Interlocked.Exchange(ref _stopping, 1);
            activeJobs = _jobs.Values
                .Where(job => !IsTerminalState(job.ToDto().State))
                .ToList();
            foreach (var job in activeJobs)
            {
                job.RequestCancellationFor(AutoGenJobCancellationReason.HostStopping);
            }
            runningTasks = _runningTasks.Values.ToArray();
        }

        if (runningTasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(runningTasks).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Завершення роботи сервера перервало очікування {JobCount} завдань автогенерації.",
                _runningTasks.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Під час очікування завершення завдань автогенерації сталася помилка.");
        }
    }

    public AutoGenJobStatus? Get(string jobId)
        => _jobs.TryGetValue(jobId, out var job) ? job.ToDto() : ReadPersistedStatus(jobId);

    public AutoGenJobStatus? Cancel(string jobId)
    {
        if (_sqliteExclusiveExecutions.ContainsKey(jobId)
            && _jobs.TryGetValue(jobId, out var sqliteLocalJob))
        {
            sqliteLocalJob.RequestCancellationFor(AutoGenJobCancellationReason.UserRequested);
            return sqliteLocalJob.ToDto();
        }

        var persistedStatus = RequestPersistedCancellation(jobId);
        if (persistedStatus is null)
        {
            return null;
        }
        if (_jobs.TryGetValue(jobId, out var job))
        {
            if (IsTerminalState(persistedStatus.State))
            {
                job.RequestCancellationFor(AutoGenJobCancellationReason.LeaseLost);
                _jobs.TryRemove(jobId, out _);
                return persistedStatus;
            }
            job.RequestCancellationFor(AutoGenJobCancellationReason.UserRequested);
            return job.ToDto();
        }
        return persistedStatus;
    }

    public async Task<AutoGenPlanDetailsDto> GetPlanAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var plans = scope.ServiceProvider.GetRequiredService<TeacherDraftsAutogenPlanService>();
        var details = await plans.GetDetailsAsync(jobId, cancellationToken);
        UpdateLocalPlanSummary(jobId, details.Summary);
        return details;
    }

    public async Task<AutoGenPlanDetailsDto?> GetLatestRollbackablePlanAsync(
        int? courseId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var plans = scope.ServiceProvider.GetRequiredService<TeacherDraftsAutogenPlanService>();
        return await plans.GetLatestRollbackableAsync(courseId, cancellationToken);
    }

    public async Task<AutoGenPlanDetailsDto> ApplyPlanAsync(
        string jobId,
        AutoGenPlanActionRequest request,
        CancellationToken cancellationToken = default)
    {
        await _executionGate.WaitAsync(cancellationToken);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var plans = scope.ServiceProvider.GetRequiredService<TeacherDraftsAutogenPlanService>();
            await using var globalExecutionLock = await AcquireGlobalExecutionLockAsync(db, cancellationToken);
            var details = await plans.ApplyAsync(jobId, request, cancellationToken);
            UpdateLocalPlanSummary(jobId, details.Summary);
            return details;
        }
        finally
        {
            _executionGate.Release();
        }
    }

    public async Task<AutoGenPlanDetailsDto> RollbackPlanAsync(
        string jobId,
        AutoGenPlanActionRequest request,
        CancellationToken cancellationToken = default)
    {
        await _executionGate.WaitAsync(cancellationToken);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var plans = scope.ServiceProvider.GetRequiredService<TeacherDraftsAutogenPlanService>();
            await using var globalExecutionLock = await AcquireGlobalExecutionLockAsync(db, cancellationToken);
            var details = await plans.RollbackAsync(jobId, request, cancellationToken);
            UpdateLocalPlanSummary(jobId, details.Summary);
            return details;
        }
        finally
        {
            _executionGate.Release();
        }
    }

    private void UpdateLocalPlanSummary(string jobId, AutoGenPlanSummaryDto summary)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            job.UpdatePlanSummary(summary);
        }
    }

    private void ThrowIfStopping()
    {
        if (Volatile.Read(ref _stopping) != 0)
        {
            throw new AutoGenJobCapacityException(
                "Сервер завершує роботу, тому нові завдання автогенерації тимчасово не приймаються.");
        }
    }

    private void QueueJob(AutoGenJobRuntime job)
    {
        var task = Task.Run(() => RunAsync(job));
        _runningTasks[job.JobId] = task;
        _ = ObserveJobCompletionAsync(job.JobId, task);
    }

    private async Task ObserveJobCompletionAsync(string jobId, Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Необроблена помилка фонового завдання автогенерації {JobId}.", jobId);
        }
        finally
        {
            _runningTasks.TryRemove(jobId, out _);
        }
    }

    private async Task MaintainLeaseAsync(AutoGenJobRuntime job, CancellationToken stopToken)
    {
        var lifetime = Stopwatch.StartNew();
        var lastSuccessfulHeartbeat = lifetime.Elapsed;
        TimeSpan? cancellationObservedAt = null;
        while (!stopToken.IsCancellationRequested)
        {
            if (job.Token.IsCancellationRequested && !IsTerminalState(job.ToDto().State))
            {
                cancellationObservedAt ??= lifetime.Elapsed;
                if (lifetime.Elapsed - cancellationObservedAt.Value >= _cancellationLeaseGrace)
                {
                    EscalateHungCancellation(job);
                    return;
                }
            }
            else
            {
                cancellationObservedAt = null;
            }

            if (_sqliteExclusiveExecutions.ContainsKey(job.JobId))
            {
                lastSuccessfulHeartbeat = lifetime.Elapsed;
                try
                {
                    await Task.Delay(_jobHeartbeatInterval, stopToken);
                }
                catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
                {
                    return;
                }
                continue;
            }

            try
            {
                var heartbeat = await RenewLeaseAndReadCancellationAsync(job, stopToken);
                if (!heartbeat.Owned)
                {
                    if (IsTerminalState(job.ToDto().State))
                    {
                        return;
                    }
                    job.RequestCancellationFor(AutoGenJobCancellationReason.LeaseLost);
                    await WaitForCanceledWorkerOrEscalateAsync(job, stopToken);
                    return;
                }
                lastSuccessfulHeartbeat = lifetime.Elapsed;
                if (heartbeat.CancellationRequested)
                {
                    job.RequestCancellationFor(AutoGenJobCancellationReason.UserRequested);
                    cancellationObservedAt ??= lifetime.Elapsed;
                }
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не вдалося оновити lease завдання автогенерації {JobId}.", job.JobId);
                if (lifetime.Elapsed - lastSuccessfulHeartbeat >= _jobLeaseDuration)
                {
                    job.RequestCancellationFor(AutoGenJobCancellationReason.LeaseLost);
                    await WaitForCanceledWorkerOrEscalateAsync(job, stopToken);
                    return;
                }
            }

            try
            {
                var delay = _jobHeartbeatInterval;
                if (cancellationObservedAt is TimeSpan observedAt)
                {
                    var remainingGrace = _cancellationLeaseGrace - (lifetime.Elapsed - observedAt);
                    if (remainingGrace < delay)
                    {
                        delay = remainingGrace > TimeSpan.Zero ? remainingGrace : TimeSpan.Zero;
                    }
                }
                await Task.Delay(delay, stopToken);
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task WaitForCanceledWorkerOrEscalateAsync(
        AutoGenJobRuntime job,
        CancellationToken stopToken)
    {
        try
        {
            await Task.Delay(_cancellationLeaseGrace, stopToken);
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            return;
        }
        EscalateHungCancellation(job);
    }

    private void EscalateHungCancellation(AutoGenJobRuntime job)
    {
        _logger.LogCritical(
            "Завдання автогенерації {JobId} не завершилося протягом пільгового періоду скасування. Lease більше не оновлюється; застосунок завершує роботу для безпечного закриття DB-сесії та глобального блокування.",
            job.JobId);
        _applicationLifetime?.StopApplication();
    }

    private async Task<LeaseHeartbeatResult> RenewLeaseAndReadCancellationAsync(
        AutoGenJobRuntime job,
        CancellationToken cancellationToken)
    {
        await _persistenceGate.WaitAsync(cancellationToken);
        try
        {
            if (_sqliteExclusiveExecutions.ContainsKey(job.JobId))
            {
                return new LeaseHeartbeatResult(true, false);
            }
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = await _databaseUtcNowAsync(db, cancellationToken);
            var affected = await db.AutoGenJobRuns
                .Where(run => run.JobId == job.JobId
                              && run.OwnerInstanceId == job.OwnerInstanceId
                              && run.Attempt == job.Attempt
                              && run.LeaseExpiresAtUtc != null
                              && run.LeaseExpiresAtUtc > now
                              && (run.State == (int)AutoGenJobState.Queued
                                  || run.State == (int)AutoGenJobState.Running))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(run => run.LeaseExpiresAtUtc, now.Add(_jobLeaseDuration))
                    .SetProperty(run => run.UpdatedAtUtc, now)
                    .SetProperty(run => run.Version, run => run.Version + 1), cancellationToken);
            if (affected != 1)
            {
                return new LeaseHeartbeatResult(false, false);
            }
            var cancellationRequested = await db.AutoGenJobRuns
                .AsNoTracking()
                .Where(run => run.JobId == job.JobId
                              && run.OwnerInstanceId == job.OwnerInstanceId
                              && run.Attempt == job.Attempt)
                .Select(run => run.CancellationRequested)
                .SingleAsync(cancellationToken);
            return new LeaseHeartbeatResult(true, cancellationRequested);
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    private async Task<DatabaseExecutionLock> AcquireGlobalExecutionLockAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var providerName = db.Database.ProviderName ?? string.Empty;
        if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return DatabaseExecutionLock.Noop;
        }
        if (!providerName.Contains("MySql", StringComparison.OrdinalIgnoreCase))
        {
            throw new AutoGenJobPersistenceException(
                "Провайдер бази даних не підтримує глобальне блокування автогенерації.");
        }

        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        var lockName = BuildGlobalExecutionLockName(connection.Database);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT GET_LOCK(@lockName, 5);";
                command.CommandTimeout = 10;
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@lockName";
                parameter.Value = lockName;
                command.Parameters.Add(parameter);
                var result = await command.ExecuteScalarAsync(cancellationToken);
                if (Convert.ToInt32(result) == 1)
                {
                    break;
                }
            }
        }
        catch
        {
            await db.Database.CloseConnectionAsync();
            throw;
        }

        return new DatabaseExecutionLock(async () =>
        {
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT RELEASE_LOCK(@lockName);";
                command.CommandTimeout = 10;
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@lockName";
                parameter.Value = lockName;
                command.Parameters.Add(parameter);
                await command.ExecuteScalarAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не вдалося явно звільнити глобальне блокування автогенерації.");
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        });
    }

    private static string BuildGlobalExecutionLockName(string? databaseName)
    {
        var normalizedDatabase = databaseName?.Trim().ToLowerInvariant() ?? string.Empty;
        var payload = $"{normalizedDatabase}:{GlobalExecutionLockBaseName}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return $"scheduleapp:autogen:{hash[..40]}";
    }

    private sealed record LeaseHeartbeatResult(bool Owned, bool CancellationRequested);

    private sealed record CourseAcademicPeriod(string Name, DateOnly? StartDate);

    private sealed class DatabaseExecutionLock(Func<ValueTask> releaseAsync) : IAsyncDisposable
    {
        private Func<ValueTask>? _releaseAsync = releaseAsync;

        public static DatabaseExecutionLock Noop { get; } = new(() => ValueTask.CompletedTask);

        public async ValueTask DisposeAsync()
        {
            var release = Interlocked.Exchange(ref _releaseAsync, null);
            if (release is not null)
            {
                await release();
            }
        }
    }

    private PersistedStartOutcome CreateOrReadPersistedJob(AutoGenJobRuntime job)
    {
        _persistenceGate.Wait();
        try
        {
            Exception? lastRetryableException = null;
            for (var retry = 0; retry < 3; retry++)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    using var transaction = db.Database.BeginTransaction(IsolationLevel.Serializable);
                    var existing = db.AutoGenJobRuns.FirstOrDefault(item => item.JobId == job.JobId);
                    if (existing is not null)
                    {
                        EnsureMatchingRequest(job.JobId, job.RequestHash, ResolveStoredRequestHash(existing));
                        var status = IsLegacyNonTerminalRun(existing)
                            ? DeserializeStatusOrFallback(existing)
                            : ExpireIfLeaseElapsed(db, existing, _databaseUtcNow(db));
                        transaction.Commit();
                        return new PersistedStartOutcome(false, status);
                    }
                    if (HasLegacyNonTerminalRuns(db))
                    {
                        throw new AutoGenJobPersistenceException(
                            "Виявлено незавершене завдання автогенерації попередньої версії без lease-власника. Нові запуски заблоковано до безпечного завершення оновлення.");
                    }

                    ValidateAcademicPeriod(db, job.Request);

                    var now = _databaseUtcNow(db);
                    var outstandingCount = db.AutoGenJobRuns.Count(item =>
                        (item.State == (int)AutoGenJobState.Queued || item.State == (int)AutoGenJobState.Running)
                        && item.OwnerInstanceId != null
                        && item.Attempt > 0
                        && item.LeaseExpiresAtUtc != null
                        && item.LeaseExpiresAtUtc > now);
                    if (outstandingCount >= MaxOutstandingJobCount)
                    {
                        throw new AutoGenJobCapacityException(
                            $"Черга автогенерації заповнена. Дочекайтеся завершення одного з {MaxOutstandingJobCount} активних завдань і повторіть спробу.");
                    }

                    job.AttachDurableClaim(_instanceId, attempt: 1, now.Add(_jobLeaseDuration));
                    var run = new AutoGenJobRun
                    {
                        JobId = job.JobId,
                        RequestHash = job.RequestHash,
                        OwnerInstanceId = job.OwnerInstanceId,
                        Attempt = job.Attempt,
                        LeaseExpiresAtUtc = job.LeaseExpiresAtUtc,
                        Version = 1
                    };
                    ApplyJobRun(run, job.ToDto(), job.Request);
                    run.UpdatedAtUtc = now;
                    db.AutoGenJobRuns.Add(run);
                    db.SaveChanges();
                    transaction.Commit();
                    return new PersistedStartOutcome(true, job.ToDto());
                }
                catch (Exception ex) when (ex is DbUpdateException or DbException)
                {
                    lastRetryableException = ex;
                    if (retry < 2)
                    {
                        Thread.Sleep(TimeSpan.FromMilliseconds(25 * (retry + 1)));
                    }
                }
            }

            throw new AutoGenJobPersistenceException(
                "Не вдалося надійно зареєструвати завдання автогенерації у сховищі стану.",
                lastRetryableException);
        }
        catch (Exception ex) when (ex is AutoGenJobValidationException
                                      or AutoGenJobCapacityException
                                      or AutoGenJobConflictException
                                      or AutoGenJobPersistenceException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не вдалося надійно зареєструвати завдання автогенерації {JobId}.", job.JobId);
            throw new AutoGenJobPersistenceException(
                "Сховище стану автогенерації тимчасово недоступне, тому завдання не було поставлено в чергу.",
                ex);
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    private static void ValidateAcademicPeriod(AppDbContext db, AutoGenJobRequest request)
    {
        var course = db.Courses
            .AsNoTracking()
            .Where(item => item.Id == request.CourseId)
            .Select(item => new CourseAcademicPeriod(item.Name, item.AcademicPeriodStartDate))
            .SingleOrDefault();
        EnsureAcademicPeriodAllowsRequest(course, request);
    }

    private static async Task ValidateAcademicPeriodAsync(
        AppDbContext db,
        AutoGenJobRequest request,
        CancellationToken cancellationToken)
    {
        var course = await db.Courses
            .AsNoTracking()
            .Where(item => item.Id == request.CourseId)
            .Select(item => new CourseAcademicPeriod(item.Name, item.AcademicPeriodStartDate))
            .SingleOrDefaultAsync(cancellationToken);
        EnsureAcademicPeriodAllowsRequest(course, request);
    }

    private static void EnsureAcademicPeriodAllowsRequest(
        CourseAcademicPeriod? course,
        AutoGenJobRequest request)
    {
        if (course is null)
        {
            throw new AutoGenJobValidationException("Вибраний курс для автогенерації не знайдено.");
        }
        if (course.StartDate is not DateOnly academicPeriodStartDate)
        {
            throw new AutoGenJobValidationException(
                $"Для курсу «{course.Name}» не вказано початок поточного навчального періоду. Налаштуйте курс перед автогенерацією.");
        }
        if (request.FromDate < academicPeriodStartDate)
        {
            throw new AutoGenJobValidationException(
                $"Початок діапазону автогенерації {request.FromDate:yyyy-MM-dd} передує початку навчального періоду {academicPeriodStartDate:yyyy-MM-dd} для курсу «{course.Name}».");
        }
    }

    private async Task<bool> TryPersistSnapshotAsync(AutoGenJobRuntime job, string operation)
        => job.IsDurable
            ? await TryPersistOwnedSnapshotAsync(job, operation)
            : await TryPersistLegacySnapshotAsync(job, operation);

    private async Task<bool> TryPersistLegacySnapshotAsync(AutoGenJobRuntime job, string operation)
    {
        await _persistenceGate.WaitAsync(CancellationToken.None);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var status = job.ToDto();
            var run = await db.AutoGenJobRuns.FirstOrDefaultAsync(item => item.JobId == job.JobId);
            if (run is not null && IsTerminalState(ToJobState(run.State)) && !IsTerminalState(status.State))
            {
                return false;
            }
            if (run is null)
            {
                run = new AutoGenJobRun
                {
                    JobId = job.JobId,
                    RequestHash = job.RequestHash,
                    Version = 1
                };
                db.AutoGenJobRuns.Add(run);
            }
            ApplyJobRun(run, status, job.Request);
            var legacyPlan = status.State == AutoGenJobState.Succeeded
                ? job.PlanPayload
                : null;
            if (legacyPlan is not null)
            {
                await TeacherDraftsAutogenPlanService.AddReadyPlanAsync(
                    db,
                    run,
                    legacyPlan,
                    CancellationToken.None);
            }
            await db.SaveChangesAsync(CancellationToken.None);
            if (legacyPlan is not null)
            {
                job.ReleasePersistedPlanPayload();
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не вдалося зберегти стан тестового або несумісного завдання автогенерації {JobId} під час етапу \"{Operation}\".", job.JobId, operation);
            return true;
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    private async Task<bool> TryPersistOwnedSnapshotAsync(AutoGenJobRuntime job, string operation)
    {
        await _persistenceGate.WaitAsync(CancellationToken.None);
        try
        {
            for (var retry = 0; retry < 3; retry++)
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var run = await db.AutoGenJobRuns.FirstOrDefaultAsync(
                    item => item.JobId == job.JobId,
                    CancellationToken.None);
                if (run is null
                    || !string.Equals(run.OwnerInstanceId, job.OwnerInstanceId, StringComparison.Ordinal)
                    || run.Attempt != job.Attempt)
                {
                    return false;
                }

                var status = job.ToDto();
                var persistedState = ToJobState(run.State);
                if (IsTerminalState(persistedState))
                {
                    return persistedState == status.State;
                }
                var now = await _databaseUtcNowAsync(db, CancellationToken.None);
                if (run.LeaseExpiresAtUtc is not DateTime leaseExpiresAtUtc
                    || leaseExpiresAtUtc <= now)
                {
                    return false;
                }
                if (run.CancellationRequested && !status.CancellationRequested)
                {
                    job.RequestCancellationFor(AutoGenJobCancellationReason.UserRequested);
                    status = job.ToDto();
                }

                ApplyJobRun(run, status, job.Request);
                var plan = status.State == AutoGenJobState.Succeeded
                    ? job.PlanPayload
                    : null;
                if (plan is not null)
                {
                    await TeacherDraftsAutogenPlanService.AddReadyPlanAsync(
                        db,
                        run,
                        plan,
                        CancellationToken.None);
                }
                run.UpdatedAtUtc = now;
                run.RequestHash = job.RequestHash;
                run.OwnerInstanceId = job.OwnerInstanceId;
                run.Attempt = job.Attempt;
                run.LeaseExpiresAtUtc = IsTerminalState(status.State)
                    ? null
                    : now.Add(_jobLeaseDuration);
                run.Version++;
                try
                {
                    await db.SaveChangesAsync(CancellationToken.None);
                    if (plan is not null)
                    {
                        job.ReleasePersistedPlanPayload();
                    }
                    return true;
                }
                catch (DbUpdateConcurrencyException) when (retry < 2)
                {
                    continue;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не вдалося зберегти стан завдання автогенерації {JobId} під час етапу \"{Operation}\".", job.JobId, operation);
            return false;
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    private AutoGenJobStatus? ReadPersistedStatus(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return null;
        }

        _persistenceGate.Wait();
        try
        {
            for (var retry = 0; retry < 3; retry++)
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var run = db.AutoGenJobRuns.FirstOrDefault(item => item.JobId == jobId);
                if (run is null)
                {
                    return null;
                }
                try
                {
                    return ExpireIfLeaseElapsed(db, run, _databaseUtcNow(db));
                }
                catch (DbUpdateConcurrencyException) when (retry < 2)
                {
                    continue;
                }
            }
            throw new AutoGenJobPersistenceException("Не вдалося узгодити стан завдання автогенерації через конкурентне оновлення.");
        }
        catch (Exception ex)
        {
            if (ex is AutoGenJobPersistenceException)
            {
                throw;
            }
            _logger.LogWarning(ex, "Не вдалося прочитати стан завдання автогенерації {JobId} з бази.", jobId);
            throw new AutoGenJobPersistenceException("Сховище стану автогенерації тимчасово недоступне.", ex);
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    private AutoGenJobStatus? RequestPersistedCancellation(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return null;
        }

        _persistenceGate.Wait();
        try
        {
            for (var retry = 0; retry < 3; retry++)
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var run = db.AutoGenJobRuns.FirstOrDefault(item => item.JobId == jobId);
                if (run is null)
                {
                    return null;
                }
                var now = _databaseUtcNow(db);
                var current = ExpireIfLeaseElapsed(db, run, now);
                if (IsTerminalState(current.State))
                {
                    return current;
                }
                if (IsLegacyNonTerminalRun(run))
                {
                    throw new AutoGenJobPersistenceException(
                        "Неможливо надійно скасувати завдання попередньої версії без lease-власника під час оновлення.");
                }

                run.CancellationRequested = true;
                run.CurrentStage = "Скасування запитано, очікуємо безпечної зупинки власника завдання...";
                run.UpdatedAtUtc = now;
                run.Version++;
                var status = BuildStatusFromColumns(run);
                run.StatusJson = JsonSerializer.Serialize(status, PersistenceJsonOptions);
                try
                {
                    db.SaveChanges();
                    return status;
                }
                catch (DbUpdateConcurrencyException) when (retry < 2)
                {
                    continue;
                }
            }
            throw new AutoGenJobPersistenceException("Не вдалося узгодити скасування завдання через конкурентне оновлення.");
        }
        catch (Exception ex)
        {
            if (ex is AutoGenJobPersistenceException)
            {
                throw;
            }
            _logger.LogWarning(ex, "Не вдалося зберегти запит скасування завдання автогенерації {JobId}.", jobId);
            throw new AutoGenJobPersistenceException("Не вдалося надійно зберегти запит скасування автогенерації.", ex);
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    private AutoGenJobStatus ExpireIfLeaseElapsed(
        AppDbContext db,
        AutoGenJobRun run,
        DateTime databaseUtcNow)
    {
        var state = ToJobState(run.State);
        if (IsTerminalState(state) || IsLegacyNonTerminalRun(run))
        {
            return DeserializeStatusOrFallback(run);
        }
        if (run.LeaseExpiresAtUtc is DateTime leaseExpiresAtUtc
            && leaseExpiresAtUtc > databaseUtcNow)
        {
            return DeserializeStatusOrFallback(run);
        }

        run.State = (int)AutoGenJobState.Failed;
        run.CompletedAtUtc ??= databaseUtcNow;
        run.CurrentStage = "Втрачено lease власника завдання.";
        run.Error = "Lease завдання автогенерації завершився. Результат виконання невідомий; автоматичний повтор не запускався.";
        run.Percent = 100;
        run.LeaseExpiresAtUtc = null;
        run.UpdatedAtUtc = databaseUtcNow;
        run.Version++;
        var expiredStatus = BuildStatusFromColumns(run);
        run.StatusJson = JsonSerializer.Serialize(expiredStatus, PersistenceJsonOptions);
        db.SaveChanges();
        return expiredStatus;
    }

    private static bool IsLegacyNonTerminalRun(AutoGenJobRun run)
        => (run.State is (int)AutoGenJobState.Queued or (int)AutoGenJobState.Running)
           && (string.IsNullOrWhiteSpace(run.OwnerInstanceId)
               || run.Attempt <= 0
               || run.LeaseExpiresAtUtc is null);

    private static bool HasLegacyNonTerminalRuns(AppDbContext db)
        => db.AutoGenJobRuns.Any(run =>
            (run.State == (int)AutoGenJobState.Queued || run.State == (int)AutoGenJobState.Running)
            && (run.OwnerInstanceId == null || run.OwnerInstanceId == string.Empty
                || run.Attempt <= 0 || run.LeaseExpiresAtUtc == null));

    private static Task<bool> HasLegacyNonTerminalRunsAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
        => db.AutoGenJobRuns.AnyAsync(run =>
            (run.State == (int)AutoGenJobState.Queued || run.State == (int)AutoGenJobState.Running)
            && (run.OwnerInstanceId == null || run.OwnerInstanceId == string.Empty
                || run.Attempt <= 0 || run.LeaseExpiresAtUtc == null),
            cancellationToken);

    private static DateTime ReadDatabaseUtcNow(AppDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
        {
            db.Database.OpenConnection();
        }
        try
        {
            using var command = CreateDatabaseUtcNowCommand(db, connection);
            return ConvertDatabaseUtcNow(command.ExecuteScalar());
        }
        finally
        {
            if (closeConnection)
            {
                db.Database.CloseConnection();
            }
        }
    }

    private static async Task<DateTime> ReadDatabaseUtcNowAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
        }
        try
        {
            await using var command = CreateDatabaseUtcNowCommand(db, connection);
            return ConvertDatabaseUtcNow(await command.ExecuteScalarAsync(cancellationToken));
        }
        finally
        {
            if (closeConnection)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    private static DbCommand CreateDatabaseUtcNowCommand(AppDbContext db, DbConnection connection)
    {
        var providerName = db.Database.ProviderName ?? string.Empty;
        var command = connection.CreateCommand();
        command.CommandTimeout = 10;
        command.CommandText = providerName.Contains("MySql", StringComparison.OrdinalIgnoreCase)
            ? "SELECT UTC_TIMESTAMP(6) /* scheduleapp-db-utc-now */;"
            : providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)
                ? "SELECT strftime('%Y-%m-%dT%H:%M:%fZ', 'now') /* scheduleapp-db-utc-now */;"
                : throw new AutoGenJobPersistenceException(
                    "Провайдер бази даних не підтримує авторитетний UTC-час для lease автогенерації.");
        if (db.Database.CurrentTransaction is { } transaction)
        {
            command.Transaction = transaction.GetDbTransaction();
        }
        return command;
    }

    private static DateTime ConvertDatabaseUtcNow(object? value)
        => value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset.UtcDateTime,
            DateTime dateTime when dateTime.Kind == DateTimeKind.Utc => dateTime,
            DateTime dateTime when dateTime.Kind == DateTimeKind.Local => dateTime.ToUniversalTime(),
            DateTime dateTime => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
            string text when DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed) => parsed.UtcDateTime,
            _ => throw new AutoGenJobPersistenceException(
                "База даних не повернула коректний UTC-час для lease автогенерації.")
        };

    private string ResolveStoredRequestHash(AutoGenJobRun run)
    {
        if (!string.IsNullOrWhiteSpace(run.RequestHash))
        {
            return run.RequestHash;
        }
        if (string.IsNullOrWhiteSpace(run.RequestJson))
        {
            return string.Empty;
        }
        try
        {
            var request = JsonSerializer.Deserialize<AutoGenJobRequest>(run.RequestJson, PersistenceJsonOptions);
            return request is null ? string.Empty : ComputeRequestHash(NormalizeRequest(request));
        }
        catch (Exception ex) when (ex is JsonException or AutoGenJobValidationException or ArgumentException)
        {
            _logger.LogWarning(ex, "Не вдалося відновити hash запиту legacy-завдання автогенерації {JobId}.", run.JobId);
            return string.Empty;
        }
    }

    private static void EnsureMatchingRequest(string jobId, string requestedHash, string storedHash)
    {
        if (string.Equals(requestedHash, storedHash, StringComparison.Ordinal))
        {
            return;
        }
        throw new AutoGenJobConflictException(
            $"Ідентифікатор завдання {jobId} вже використано для іншого набору параметрів автогенерації.");
    }

    private static string ComputeRequestHash(AutoGenJobRequest request)
    {
        var canonicalPayload = new
        {
            request.Kind,
            request.FromDate,
            request.ToDate,
            request.CourseId,
            GroupIds = request.GroupIds.OrderBy(value => value).ToArray(),
            ModuleHours = request.ModuleHours
                .OrderBy(entry => entry.Key)
                .Select(entry => new { ModuleId = entry.Key, Hours = entry.Value })
                .ToArray(),
            request.Days,
            request.ClearExisting,
            request.SoftFill,
            request.PreflightOnly,
            request.PreviewOnly,
            request.AllowIncompleteDrafts,
            GroupRoomPreferences = request.GroupRoomPreferences?
                .Select(preference => new
                {
                    preference.GroupId,
                    preference.BuildingId,
                    RoomIds = preference.RoomIds?
                        .Where(roomId => roomId > 0)
                        .Distinct()
                        .OrderBy(roomId => roomId)
                        .ToArray()
                })
                .OrderBy(preference => preference.GroupId)
                .ThenBy(preference => preference.BuildingId)
                .ToArray(),
            request.SoftOptions,
            request.PreferredFirstMaxSlotOrderOverride
        };
        var json = JsonSerializer.Serialize(canonicalPayload, PersistenceJsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    private sealed record PersistedStartOutcome(bool Created, AutoGenJobStatus Status);

    private static void ApplyJobRun(AutoGenJobRun run, AutoGenJobStatus status, AutoGenJobRequest request)
    {
        run.JobId = status.JobId;
        run.Kind = (int)status.Kind;
        run.State = (int)status.State;
        run.Title = Limit(status.Title, 256);
        run.CurrentStage = Limit(status.CurrentStage, 512);
        run.CreatedAtUtc = ToUtc(status.CreatedAt);
        run.StartedAtUtc = ToUtc(status.StartedAt);
        run.CompletedAtUtc = ToUtc(status.CompletedAt);
        run.RangeStartDate = status.RangeStartDate;
        run.RangeEndDate = status.RangeEndDate;
        run.TotalWeeks = Math.Max(1, status.TotalWeeks);
        run.CompletedWeeks = status.CompletedWeeks;
        run.CurrentWeekNumber = status.CurrentWeekNumber;
        run.CurrentWeekStartDate = status.CurrentWeekStartDate;
        run.CurrentRangeStartDate = status.CurrentRangeStartDate;
        run.CurrentRangeEndDate = status.CurrentRangeEndDate;
        run.CreatedCount = status.Created;
        run.SkippedCount = status.Skipped;
        run.WarningCount = status.WarningCount;
        run.GapCount = status.GapCount;
        run.DeficitCount = status.DeficitCount;
        run.Percent = Math.Clamp(status.Percent, 0, 100);
        run.CancellationRequested = status.CancellationRequested;
        run.LastCompletedMessage = LimitOptional(status.LastCompletedMessage, 1024);
        run.Error = status.Error;
        run.RequestJson = JsonSerializer.Serialize(request, PersistenceJsonOptions);
        run.StatusJson = JsonSerializer.Serialize(status, PersistenceJsonOptions);
        run.ResultJson = status.Result is null ? null : JsonSerializer.Serialize(status.Result, PersistenceJsonOptions);
        run.ReportJson = status.Report is null ? null : JsonSerializer.Serialize(status.Report, PersistenceJsonOptions);
        run.UpdatedAtUtc = DateTime.UtcNow;
    }

    private AutoGenJobStatus DeserializeStatusOrFallback(AutoGenJobRun run)
    {
        if (!string.IsNullOrWhiteSpace(run.StatusJson))
        {
            try
            {
                var status = JsonSerializer.Deserialize<AutoGenJobStatus>(run.StatusJson, PersistenceJsonOptions);
                if (status is not null)
                {
                    return status;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Не вдалося прочитати JSON статусу завдання автогенерації {JobId}.", run.JobId);
            }
        }

        return BuildStatusFromColumns(run);
    }

    private AutoGenJobStatus BuildStatusFromColumns(AutoGenJobRun run)
        => new(
            run.JobId,
            ToJobState(run.State),
            ToJobKind(run.Kind),
            run.Title,
            run.CurrentStage,
            FromUtc(run.CreatedAtUtc),
            FromUtc(run.StartedAtUtc),
            FromUtc(run.CompletedAtUtc),
            run.RangeStartDate,
            run.RangeEndDate,
            Math.Max(1, run.TotalWeeks),
            run.CompletedWeeks,
            run.CurrentWeekNumber,
            run.CurrentWeekStartDate,
            run.CurrentRangeStartDate,
            run.CurrentRangeEndDate,
            run.CreatedCount,
            run.SkippedCount,
            run.WarningCount,
            run.GapCount,
            run.DeficitCount,
            Math.Clamp(run.Percent, 0, 100),
            run.CancellationRequested,
            run.LastCompletedMessage,
            TryDeserializePayload<AutoGenResult>(run.ResultJson, run.JobId, "результату"),
            TryDeserializePayload<AutoGenRunReport>(run.ReportJson, run.JobId, "звіту"),
            run.Error);

    private T? TryDeserializePayload<T>(string? json, string jobId, string payloadName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, PersistenceJsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Не вдалося прочитати JSON {PayloadName} завдання автогенерації {JobId}.", payloadName, jobId);
            return default;
        }
    }

    private static AutoGenJobKind ToJobKind(int kind)
        => Enum.IsDefined(typeof(AutoGenJobKind), kind) ? (AutoGenJobKind)kind : AutoGenJobKind.Generate;

    private static AutoGenJobState ToJobState(int state)
        => Enum.IsDefined(typeof(AutoGenJobState), state) ? (AutoGenJobState)state : AutoGenJobState.Failed;

    private static bool IsTerminalState(AutoGenJobState state)
        => state is AutoGenJobState.Succeeded or AutoGenJobState.Failed or AutoGenJobState.Canceled;

    private static DateTime ToUtc(DateTimeOffset value)
        => value.UtcDateTime;

    private static DateTime? ToUtc(DateTimeOffset? value)
        => value?.UtcDateTime;

    private static DateTimeOffset FromUtc(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset? FromUtc(DateTime? value)
        => value is null ? null : FromUtc(value.Value);

    private static string Limit(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static string? LimitOptional(string? value, int maxLength)
        => string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];

    private static AutoGenJobRequest NormalizeRequest(AutoGenJobRequest request)
    {
        if (!Enum.IsDefined(request.Kind))
        {
            throw new AutoGenJobValidationException("Невідомий тип завдання автогенерації.");
        }
        if (request.CourseId <= 0)
        {
            throw new AutoGenJobValidationException("Потрібно вибрати коректний курс для автогенерації.");
        }
        if (!Enum.IsDefined(request.Days))
        {
            throw new AutoGenJobValidationException("Невідомий набір робочих днів для автогенерації.");
        }
        var fromDate = request.FromDate;
        var toDate = request.ToDate;
        if (fromDate == default || toDate == default)
        {
            throw new AutoGenJobValidationException("Потрібно вказати початок і кінець діапазону автогенерації.");
        }
        if (fromDate < MinSupportedDate || toDate > MaxSupportedDate)
        {
            throw new AutoGenJobValidationException(
                $"Діапазон автогенерації має бути в межах {MinSupportedDate:yyyy-MM-dd} — {MaxSupportedDate:yyyy-MM-dd}.");
        }
        if (toDate < fromDate)
        {
            throw new AutoGenJobValidationException("Кінець діапазону автогенерації не може бути раніше за початок.");
        }
        var rangeDays = toDate.DayNumber - fromDate.DayNumber + 1;
        if (rangeDays > MaxRangeDays)
        {
            throw new AutoGenJobValidationException(
                $"Діапазон автогенерації не може перевищувати {MaxRangeDays} днів.");
        }
        if (request.GroupIds is null)
        {
            throw new AutoGenJobValidationException("Список груп для автогенерації відсутній.");
        }
        if (request.GroupIds.Count > MaxGroupCount)
        {
            throw new AutoGenJobValidationException(
                $"За один запуск можна передати не більше {MaxGroupCount} груп.");
        }
        var groupIds = request.GroupIds
            .Where(groupId => groupId > 0)
            .Distinct()
            .ToList();
        if (groupIds.Count == 0)
        {
            throw new AutoGenJobValidationException("Потрібно вибрати щонайменше одну коректну групу.");
        }
        if (request.ModuleHours is null)
        {
            throw new AutoGenJobValidationException("Перелік годин модулів для автогенерації відсутній.");
        }
        if (request.ModuleHours.Count > MaxModuleHourEntryCount)
        {
            throw new AutoGenJobValidationException(
                $"За один запуск можна передати не більше {MaxModuleHourEntryCount} записів годин модулів.");
        }
        if (request.ModuleHours.Any(entry => entry.Key <= 0 || entry.Value <= 0))
        {
            throw new AutoGenJobValidationException("Ідентифікатори модулів і кількість годин мають бути додатними числами.");
        }
        if (request.ModuleHours.Any(entry => entry.Value > MaxHoursPerModulePerWeek))
        {
            throw new AutoGenJobValidationException(
                $"Для одного модуля можна вказати не більше {MaxHoursPerModulePerWeek} годин на тиждень.");
        }
        var moduleHours = request.ModuleHours
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        if (moduleHours.Count == 0)
        {
            throw new AutoGenJobValidationException("Потрібно вибрати щонайменше один модуль із годинами.");
        }
        if (request.GroupRoomPreferences is { Count: > MaxGroupCount })
        {
            throw new AutoGenJobValidationException(
                $"За один запуск можна передати не більше {MaxGroupCount} налаштувань аудиторій груп.");
        }
        if (request.GroupRoomPreferences is { } roomPreferences
            && roomPreferences.Any(preference => preference.RoomIds is { Count: > MaxPreferredRoomCountPerGroup }))
        {
            throw new AutoGenJobValidationException(
                $"Для однієї групи можна вибрати не більше {MaxPreferredRoomCountPerGroup} пріоритетних аудиторій.");
        }
        if (request.PreferredFirstMaxSlotOrderOverride is int preferredFirstSlotOrder
            && (preferredFirstSlotOrder < 0 || preferredFirstSlotOrder > MaxPreferredFirstSlotOrderOverride))
        {
            throw new AutoGenJobValidationException(
                $"Ліміт пріоритетного слоту має бути від 0 до {MaxPreferredFirstSlotOrderOverride}.");
        }
        if (request.SoftOptions is { } softOptions)
        {
            if (softOptions.MaxParallelGroupsPerModuleInSlot is int maxParallelGroups
                && (maxParallelGroups <= 0 || maxParallelGroups > MaxGroupCount))
            {
                throw new AutoGenJobValidationException(
                    $"Межа паралельних груп має бути від 1 до {MaxGroupCount}.");
            }
            if (softOptions.RecentRepeatWindowDays is int repeatWindow
                && (repeatWindow < 0
                    || repeatWindow > MaxRecentRepeatWindowDays
                    || repeatWindow > fromDate.DayNumber
                    || repeatWindow > DateOnly.MaxValue.DayNumber - toDate.DayNumber))
            {
                throw new AutoGenJobValidationException(
                    $"Вікно близьких повторів має бути від 0 до {MaxRecentRepeatWindowDays} днів і не виходити за межі календаря.");
            }
            if (softOptions.PreferredMaxDistinctModulesPerDay is int preferredDistinctModules
                && (preferredDistinctModules <= 0 || preferredDistinctModules > MaxDistinctModulesPerDay))
            {
                throw new AutoGenJobValidationException(
                    $"Бажана кількість різних модулів на день має бути від 1 до {MaxDistinctModulesPerDay}.");
            }
            if (softOptions.MaxDistinctModulesPerDay is int maxDistinctModules
                && (maxDistinctModules <= 0 || maxDistinctModules > MaxDistinctModulesPerDay))
            {
                throw new AutoGenJobValidationException(
                    $"Максимальна кількість різних модулів на день має бути від 1 до {MaxDistinctModulesPerDay}.");
            }
            if (softOptions.PreferredMaxDistinctModulesPerDay is int preferredDistinct
                && softOptions.MaxDistinctModulesPerDay is int maximumDistinct
                && preferredDistinct > maximumDistinct)
            {
                throw new AutoGenJobValidationException(
                    "Бажана кількість різних модулів на день не може перевищувати максимальну.");
            }

            static bool IsInvalidPenalty(double? value)
                => value is double resolved
                   && (!double.IsFinite(resolved) || resolved < 0 || resolved > MaxSoftPenaltyWeight);

            if (IsInvalidPenalty(softOptions.PreferredFirstPenaltyMultiplier)
                || IsInvalidPenalty(softOptions.AdjacentRoomChangePenalty)
                || IsInvalidPenalty(softOptions.TeacherLoadPenaltyWeight)
                || IsInvalidPenalty(softOptions.BuildingDistancePenaltyWeight))
            {
                throw new AutoGenJobValidationException(
                    $"Ваги м'яких штрафів мають бути скінченними числами від 0 до {MaxSoftPenaltyWeight:0}.");
            }
        }
        var clearExisting = request.ClearExisting;
        var softFill = request.SoftFill;
        var preflightOnly = request.PreflightOnly;
        var previewOnly = request.PreviewOnly;
        if (request.Kind == AutoGenJobKind.Preflight)
        {
            clearExisting = false;
            preflightOnly = true;
            previewOnly = false;
        }
        else if (request.Kind == AutoGenJobKind.Fill)
        {
            clearExisting = false;
            softFill = true;
            preflightOnly = false;
        }
        var title = string.IsNullOrWhiteSpace(request.Title)
            ? BuildDefaultTitle(request.Kind)
            : request.Title.Trim();
        if (title.Length > MaxTitleLength)
        {
            throw new AutoGenJobValidationException(
                $"Назва завдання автогенерації не може перевищувати {MaxTitleLength} символів.");
        }
        string? clientJobId = null;
        if (!string.IsNullOrWhiteSpace(request.ClientJobId))
        {
            if (!Guid.TryParseExact(request.ClientJobId.Trim(), "N", out var parsedClientJobId))
            {
                throw new AutoGenJobValidationException(
                    "Ідентифікатор клієнтського завдання автогенерації має бути GUID у форматі N.");
            }
            clientJobId = parsedClientJobId.ToString("N");
        }
        return request with
        {
            FromDate = fromDate,
            ToDate = toDate,
            GroupIds = groupIds,
            ModuleHours = moduleHours,
            ClearExisting = clearExisting,
            SoftFill = softFill,
            PreflightOnly = preflightOnly,
            PreviewOnly = previewOnly,
            Title = title,
            ClientJobId = clientJobId
        };
    }

    private static string BuildDefaultTitle(AutoGenJobKind kind)
        => kind switch
        {
            AutoGenJobKind.Preflight => "Попередня перевірка ресурсів",
            AutoGenJobKind.Fill => "Заповнення порожніх слотів",
            _ => "Автогенерація у чернетки"
        };

    private async Task RunAsync(AutoGenJobRuntime job)
    {
        var warnings = new List<string>();
        var gapDetails = new List<AutoGenGapDetail>();
        var preflight = new List<AutoGenPreflightItem>();
        var created = 0;
        var skipped = 0;
        var failed = false;
        var executionRolledBack = false;
        var executionCommitted = false;
        AutoGenDraftPlanPayload? planPayload = null;
        var weekStarts = BuildWeekStarts(job.Request.FromDate, job.Request.ToDate);
        var ownsExecutionGate = false;
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeatTask = job.IsDurable
            ? MaintainLeaseAsync(job, heartbeatStop.Token)
            : Task.CompletedTask;

        try
        {
            await _executionGate.WaitAsync(job.Token);
            ownsExecutionGate = true;
            job.Token.ThrowIfCancellationRequested();
            job.MarkRunning(weekStarts.Count);
            await PersistProgressOrCancelAsync(job, "запуск завдання");
            job.Token.ThrowIfCancellationRequested();

            using var scope = _scopeFactory.CreateScope();
            var executionDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var autogen = scope.ServiceProvider.GetRequiredService<TeacherDraftsAutogenService>();
            await using var globalExecutionLock = job.IsDurable
                ? await AcquireGlobalExecutionLockAsync(
                    executionDb,
                    job.Token)
                : DatabaseExecutionLock.Noop;
            var runRanges = BuildRunRanges(job.Request.FromDate, job.Request.ToDate, weekStarts);
            var persistIntermediateProgress = CanPersistProgressDuringExecutionTransaction(executionDb);
            var sqliteExclusiveLease = await EnterSqliteExclusiveExecutionLeaseAsync(executionDb, job, job.Token);

            try
            {
                await using (var executionTransaction = await executionDb.Database.BeginTransactionAsync(
                                 IsolationLevel.Serializable,
                                 job.Token))
                {
                    var transactionFinalized = false;
                    var commitStarted = false;
                    try
                    {
                        await ValidateAcademicPeriodAsync(executionDb, job.Request, job.Token);
                        var plans = scope.ServiceProvider.GetRequiredService<TeacherDraftsAutogenPlanService>();
                        var previewInputFingerprint = job.Request.PreviewOnly
                            ? await plans.CaptureInputFingerprintAsync(job.Request, job.Token)
                            : null;
                        var beforePreviewScope = job.Request.PreviewOnly
                            ? await plans.CaptureScopeAsync(job.Request, job.Token)
                            : null;
                        foreach (var runRange in runRanges)
                        {
                            job.Token.ThrowIfCancellationRequested();

                            job.StartWeek(runRange.WeekIndex, runRange.WeekStart, runRange.RangeStartDate, runRange.RangeEndDate);
                            if (persistIntermediateProgress)
                            {
                                await PersistProgressOrCancelAsync(job, "початок діапазону");
                            }
                            var request = BuildDraftRequest(job.Request, runRange.WeekStart, runRange.RangeStartDate, runRange.RangeEndDate);
                            var action = await autogen.DraftAutoGenInAmbientTransaction(request, job.Token);
                            var (rangeSucceeded, rangeResult, fallbackWarning) = ExtractAutoGenResult(action);
                            if (!rangeSucceeded)
                            {
                                failed = true;
                                warnings.Add($"[{runRange.RangeStartDate:yyyy-MM-dd} – {runRange.RangeEndDate:yyyy-MM-dd}] Діапазон не згенеровано повністю.");
                            }
                            if (!string.IsNullOrWhiteSpace(fallbackWarning))
                            {
                                warnings.Add(fallbackWarning);
                            }

                            created += rangeResult.Created;
                            skipped += rangeResult.Skipped;
                            warnings.AddRange(rangeResult.Warnings);
                            if (rangeResult.GapDetails is { Count: > 0 })
                            {
                                gapDetails.AddRange(rangeResult.GapDetails);
                            }
                            if (rangeResult.Preflight is { Count: > 0 })
                            {
                                preflight.AddRange(rangeResult.Preflight);
                            }

                            var partialResult = TeacherDraftsAutogenReportBuilder.BuildResult(created, skipped, warnings, gapDetails, preflight);
                            job.CompleteWeek(runRange.WeekIndex, runRange.RangeStartDate, runRange.RangeEndDate, rangeResult, partialResult);
                            if (persistIntermediateProgress)
                            {
                                await PersistProgressOrCancelAsync(job, "завершення діапазону");
                            }
                            if (!rangeSucceeded)
                            {
                                break;
                            }
                        }

                        job.Token.ThrowIfCancellationRequested();
                        if (!failed && job.Request.PreviewOnly)
                        {
                            var afterPreviewScope = await plans.CaptureScopeAsync(job.Request, job.Token);
                            planPayload = TeacherDraftsAutogenPlanService.BuildPayload(
                                job.JobId,
                                job.Request,
                                beforePreviewScope!,
                                afterPreviewScope,
                                previewInputFingerprint
                                ?? throw new InvalidOperationException("Контрольний відбиток попереднього плану не сформовано."));
                        }
                        if (failed || job.Request.PreflightOnly || job.Request.PreviewOnly)
                        {
                            await executionTransaction.RollbackAsync(CancellationToken.None);
                            transactionFinalized = true;
                            if (job.Request.PreviewOnly && !failed)
                            {
                                job.AttachPlan(planPayload
                                    ?? throw new InvalidOperationException("Попередній план автогенерації не сформовано."));
                                warnings.Add("Сформовано попередній план без зміни робочих чернеток. Застосуйте його окремою дією після перегляду.");
                            }
                            else
                            {
                                MarkExecutionRolledBack();
                            }
                        }
                        else
                        {
                            job.Token.ThrowIfCancellationRequested();
                            await EnsureCommitFenceAsync(executionDb, job, job.Token);
                            commitStarted = true;
                            try
                            {
                                await CommitExecutionTransactionAsync(executionTransaction);
                                executionCommitted = true;
                                transactionFinalized = true;
                            }
                            catch (Exception ex)
                            {
                                transactionFinalized = true;
                                throw new AutoGenJobCommitOutcomeUnknownException(
                                    "База даних не підтвердила результат commit автогенерації; фактичний результат виконання невідомий.",
                                    ex);
                            }
                        }
                    }
                    catch
                    {
                        if (!transactionFinalized && !commitStarted)
                        {
                            await TryRollbackExecutionTransactionAsync(executionTransaction, job.JobId);
                            MarkExecutionRolledBack();
                        }
                        throw;
                    }
                }
            }
            finally
            {
                if (sqliteExclusiveLease)
                {
                    _sqliteExclusiveExecutions.TryRemove(job.JobId, out _);
                }
            }

            if (!executionCommitted)
            {
                job.Token.ThrowIfCancellationRequested();
            }
            var result = TeacherDraftsAutogenReportBuilder.BuildResult(created, skipped, warnings, gapDetails, preflight);
            var report = TeacherDraftsAutogenReportBuilder.BuildReport(job.Request.FromDate, job.Request.ToDate, weekStarts.Count, result);
            if (failed)
            {
                job.MarkFailed("Один або кілька тижнів завершилися з помилками.", result, report);
            }
            else
            {
                job.MarkSucceeded(result, report);
            }
            await PersistTerminalSnapshotWithRetryAsync(job, "завершення завдання", result, report);
        }
        catch (Exception ex) when (executionCommitted)
        {
            _logger.LogWarning(
                ex,
                "Після підтвердженого commit завдання автогенерації {JobId} сталася помилка завершального очищення; збережений результат залишається успішним.",
                job.JobId);
            warnings.Add(
                "База даних підтвердила збереження чернеток, але завершальне очищення ресурсу повернуло помилку. Результат генерації збережено.");
            var result = TeacherDraftsAutogenReportBuilder.BuildResult(created, skipped, warnings, gapDetails, preflight);
            var report = TeacherDraftsAutogenReportBuilder.BuildReport(
                job.Request.FromDate,
                job.Request.ToDate,
                Math.Max(1, weekStarts.Count),
                result);
            job.MarkSucceeded(result, report);
            await PersistTerminalSnapshotWithRetryAsync(job, "завершення після підтвердженого commit", result, report);
        }
        catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
        {
            var result = TeacherDraftsAutogenReportBuilder.BuildResult(created, skipped, warnings, gapDetails, preflight);
            var report = TeacherDraftsAutogenReportBuilder.BuildReport(job.Request.FromDate, job.Request.ToDate, Math.Max(1, weekStarts.Count), result);
            if (job.CancellationReason == AutoGenJobCancellationReason.LeaseLost)
            {
                job.MarkFailed(
                    "Втрачено lease завдання автогенерації. Результат виконання невідомий; автоматичний повтор не запускався.",
                    result,
                    report);
                await PersistTerminalSnapshotWithRetryAsync(job, "втрата lease завдання", result, report);
            }
            else
            {
                job.MarkCanceled(result, report);
                await PersistTerminalSnapshotWithRetryAsync(job, "скасування завдання", result, report);
            }
        }
        catch (Exception ex)
        {
            if (ex is AutoGenJobValidationException or AutoGenJobCapacityException or OperationCanceledException)
            {
                _logger.LogWarning(ex, "Завдання автогенерації {JobId} завершилося очікуваною помилкою.", job.JobId);
            }
            else
            {
                _logger.LogError(ex, "Завдання автогенерації {JobId} завершилося внутрішньою помилкою.", job.JobId);
            }
            var result = TeacherDraftsAutogenReportBuilder.BuildResult(created, skipped, warnings, gapDetails, preflight);
            var report = TeacherDraftsAutogenReportBuilder.BuildReport(job.Request.FromDate, job.Request.ToDate, Math.Max(1, weekStarts.Count), result);
            job.MarkFailed(BuildPublicFailureMessage(ex, job.JobId), result, report);
            await PersistTerminalSnapshotWithRetryAsync(job, "помилка завдання", result, report);
        }
        finally
        {
            heartbeatStop.Cancel();
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException)
            {
            }
            if (ownsExecutionGate)
            {
                _executionGate.Release();
            }
            CleanupOldJobs();
        }

        void MarkExecutionRolledBack()
        {
            if (executionRolledBack)
            {
                return;
            }
            executionRolledBack = true;
            created = 0;
            warnings.Add(FullJobRollbackWarning);
        }
    }

    private async Task TryRollbackExecutionTransactionAsync(
        IDbContextTransaction transaction,
        string jobId)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(
                ex,
                "Не вдалося явно відкотити транзакцію завдання автогенерації {JobId}; з'єднання буде закрито без commit.",
                jobId);
        }
    }

    private static Task CommitExecutionTransactionAsync(IDbContextTransaction transaction)
        => transaction.CommitAsync(CancellationToken.None);

    private async Task EnsureCommitFenceAsync(
        AppDbContext db,
        AutoGenJobRuntime job,
        CancellationToken cancellationToken)
    {
        if (!job.IsDurable)
        {
            return;
        }

        var now = await _databaseUtcNowAsync(db, cancellationToken);
        var leaseExpiresAtUtc = now.Add(_jobLeaseDuration);
        var affected = await db.AutoGenJobRuns
            .Where(run => run.JobId == job.JobId
                          && run.OwnerInstanceId == job.OwnerInstanceId
                          && run.Attempt == job.Attempt
                          && !run.CancellationRequested
                          && run.LeaseExpiresAtUtc != null
                          && run.LeaseExpiresAtUtc > now
                          && (run.State == (int)AutoGenJobState.Queued
                              || run.State == (int)AutoGenJobState.Running))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(run => run.LeaseExpiresAtUtc, leaseExpiresAtUtc)
                .SetProperty(run => run.UpdatedAtUtc, now)
                .SetProperty(run => run.Version, run => run.Version + 1), cancellationToken);
        if (affected == 1)
        {
            job.AttachDurableClaim(job.OwnerInstanceId!, job.Attempt, leaseExpiresAtUtc);
            return;
        }

        var authoritativeState = await db.AutoGenJobRuns
            .AsNoTracking()
            .Where(run => run.JobId == job.JobId)
            .Select(run => new { run.CancellationRequested })
            .SingleOrDefaultAsync(CancellationToken.None);
        var reason = authoritativeState?.CancellationRequested == true
            ? AutoGenJobCancellationReason.UserRequested
            : AutoGenJobCancellationReason.LeaseLost;
        job.RequestCancellationFor(reason);
        throw new OperationCanceledException(
            reason == AutoGenJobCancellationReason.UserRequested
                ? "Скасування завдання підтверджено базою даних до commit."
                : "База даних не підтвердила право цього власника на commit автогенерації.",
            job.Token);
    }

    private async Task<bool> EnterSqliteExclusiveExecutionLeaseAsync(
        AppDbContext db,
        AutoGenJobRuntime job,
        CancellationToken cancellationToken)
    {
        if (!job.IsDurable || !IsSqliteProvider(db))
        {
            return false;
        }

        await _persistenceGate.WaitAsync(cancellationToken);
        try
        {
            var now = await _databaseUtcNowAsync(db, cancellationToken);
            var leaseExpiresAtUtc = now.Add(SqliteExclusiveExecutionLeaseDuration);
            var affected = await db.AutoGenJobRuns
                .Where(run => run.JobId == job.JobId
                              && run.OwnerInstanceId == job.OwnerInstanceId
                              && run.Attempt == job.Attempt
                              && run.LeaseExpiresAtUtc != null
                              && run.LeaseExpiresAtUtc > now
                              && !run.CancellationRequested
                              && (run.State == (int)AutoGenJobState.Queued
                                  || run.State == (int)AutoGenJobState.Running))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(run => run.LeaseExpiresAtUtc, leaseExpiresAtUtc)
                    .SetProperty(run => run.UpdatedAtUtc, now)
                    .SetProperty(run => run.Version, run => run.Version + 1), cancellationToken);
            if (affected != 1)
            {
                job.RequestCancellationFor(AutoGenJobCancellationReason.LeaseLost);
                throw new OperationCanceledException(
                    "Не вдалося зарезервувати lease для атомарної SQLite-транзакції автогенерації.",
                    job.Token);
            }

            job.AttachDurableClaim(job.OwnerInstanceId!, job.Attempt, leaseExpiresAtUtc);
            _sqliteExclusiveExecutions[job.JobId] = 0;
            return true;
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    private async Task PersistProgressOrCancelAsync(AutoGenJobRuntime job, string operation)
    {
        if (await TryPersistSnapshotAsync(job, operation))
        {
            return;
        }
        job.RequestCancellationFor(AutoGenJobCancellationReason.LeaseLost);
        throw new OperationCanceledException(
            "Втрачено DB lease завдання автогенерації.",
            job.Token);
    }

    private async Task PersistTerminalSnapshotWithRetryAsync(
        AutoGenJobRuntime job,
        string operation,
        AutoGenResult result,
        AutoGenRunReport report)
    {
        var persistenceTimer = Stopwatch.StartNew();
        var retryDelay = TimeSpan.FromMilliseconds(250);
        while (true)
        {
            if (await TryPersistSnapshotAsync(job, operation))
            {
                return;
            }
            var remaining = _terminalPersistenceHorizon - persistenceTimer.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }
            await Task.Delay(retryDelay < remaining ? retryDelay : remaining, CancellationToken.None);
            retryDelay = TimeSpan.FromMilliseconds(Math.Min(retryDelay.TotalMilliseconds * 2, 2_000));
        }

        if (job.ToDto().State == AutoGenJobState.Succeeded)
        {
            job.MarkFailed(
                "Генерація завершила роботу, але сховище не підтвердило термінальний статус; результат виконання невідомий.",
                result,
                report);
            if (await TryPersistSnapshotAsync(job, "фіксація невідомого результату"))
            {
                return;
            }
        }

        _jobs.TryRemove(job.JobId, out _);
        _logger.LogError(
            "Не вдалося підтвердити термінальний стан завдання автогенерації {JobId}; локальний результат вилучено, результат виконання невідомий.",
            job.JobId);
    }

    // Формує безпечний текст статусу без розкриття внутрішніх деталей винятку.
    private static string BuildPublicFailureMessage(Exception exception, string jobId)
        => exception switch
        {
            AutoGenJobValidationException or AutoGenJobCapacityException
                => $"{exception.Message} Код завдання: {jobId}.",
            OperationCanceledException
                => $"Виконання автогенерації було перервано. Код завдання: {jobId}.",
            AutoGenJobCommitOutcomeUnknownException
                => $"База даних не підтвердила commit автогенерації; результат виконання невідомий. Код завдання: {jobId}.",
            _ => $"Під час автогенерації сталася внутрішня помилка. Код завдання: {jobId}. Передайте цей код адміністратору."
        };

    private static DraftAutoGenRequest BuildDraftRequest(
        AutoGenJobRequest request,
        DateOnly weekStart,
        DateOnly rangeStartDate,
        DateOnly rangeEndDate)
        => new(
            WeekStart: weekStart,
            ClearExisting: request.ClearExisting,
            CourseId: request.CourseId,
            GroupId: null,
            GroupIds: request.GroupIds,
            TeacherId: null,
            AllowOnDaysOff: false,
            Days: request.Days,
            ModuleHours: request.ModuleHours,
            SoftFill: request.SoftFill,
            AllowIncompleteDrafts: request.AllowIncompleteDrafts,
            RangeStartDate: rangeStartDate,
            RangeEndDate: rangeEndDate,
            PreferredFirstMaxSlotOrderOverride: request.PreferredFirstMaxSlotOrderOverride,
            GroupRoomPreferences: request.GroupRoomPreferences,
            SoftOptions: MapSoftOptions(request.SoftOptions),
            PreflightOnly: request.PreflightOnly);

    private static DraftAutoGenSoftOptions? MapSoftOptions(AutoGenSoftOptionsDto? dto)
        => dto is null
            ? null
            : new DraftAutoGenSoftOptions(
                MaxParallelGroupsPerModuleInSlot: dto.MaxParallelGroupsPerModuleInSlot,
                RecentRepeatWindowDays: dto.RecentRepeatWindowDays,
                PreferredMaxDistinctModulesPerDay: dto.PreferredMaxDistinctModulesPerDay,
                MaxDistinctModulesPerDay: dto.MaxDistinctModulesPerDay,
                PreferredFirstPenaltyMultiplier: dto.PreferredFirstPenaltyMultiplier,
                AdjacentRoomChangePenalty: dto.AdjacentRoomChangePenalty,
                TeacherLoadPenaltyWeight: dto.TeacherLoadPenaltyWeight,
                BuildingDistancePenaltyWeight: dto.BuildingDistancePenaltyWeight);

    private static List<DateOnly> BuildWeekStarts(DateOnly fromDate, DateOnly toDate)
    {
        var fromWeekStart = DateHelpers.StartOfWeek(fromDate);
        var toWeekStart = DateHelpers.StartOfWeek(toDate);
        var weekStarts = new List<DateOnly>();
        for (var week = fromWeekStart; week <= toWeekStart; week = week.AddDays(7))
        {
            weekStarts.Add(week);
        }
        return weekStarts;
    }

    private static IReadOnlyList<AutoGenJobRunRange> BuildRunRanges(
        DateOnly fromDate,
        DateOnly toDate,
        IReadOnlyList<DateOnly> weekStarts)
    {
        var ranges = new List<AutoGenJobRunRange>(weekStarts.Count);
        for (var weekIndex = 0; weekIndex < weekStarts.Count; weekIndex++)
        {
            var weekStart = weekStarts[weekIndex];
            var weekEnd = weekStart.AddDays(6);
            var rangeStartDate = fromDate > weekStart ? fromDate : weekStart;
            var rangeEndDate = toDate < weekEnd ? toDate : weekEnd;
            if (rangeEndDate >= rangeStartDate)
            {
                ranges.Add(new AutoGenJobRunRange(
                    weekIndex,
                    weekStart,
                    rangeStartDate,
                    rangeEndDate));
            }
        }
        return ranges;
    }

    private static bool CanPersistProgressDuringExecutionTransaction(AppDbContext db)
        => !IsSqliteProvider(db);

    private static bool IsSqliteProvider(AppDbContext db)
        => string.Equals(
            db.Database.ProviderName,
            "Microsoft.EntityFrameworkCore.Sqlite",
            StringComparison.OrdinalIgnoreCase);

    private static (bool Succeeded, AutoGenResult Result, string? Warning) ExtractAutoGenResult(ActionResult<AutoGenResult> action)
    {
        if (action.Result is OkObjectResult { Value: AutoGenResult ok })
        {
            return (true, ok, null);
        }
        if (action.Result is ObjectResult { Value: AutoGenResult failedResult })
        {
            return (false, failedResult, null);
        }
        if (action.Result is ObjectResult { Value: { } value })
        {
            return (false, new AutoGenResult(0, 0, new()), JsonSerializer.Serialize(value));
        }
        return (false, new AutoGenResult(0, 0, new()), "Сервер не повернув результат автогенерації.");
    }

    private void CleanupOldJobs()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _jobs)
        {
            var status = entry.Value.ToDto();
            if (status.CompletedAt is not DateTimeOffset completedAt)
            {
                continue;
            }
            if (now - completedAt > CompletedJobTtl)
            {
                _jobs.TryRemove(entry.Key, out _);
            }
        }

        var excessTerminalJobs = _jobs
            .Select(entry => new { entry.Key, Status = entry.Value.ToDto() })
            .Where(entry => IsTerminalState(entry.Status.State))
            .OrderByDescending(entry => entry.Status.CompletedAt ?? entry.Status.CreatedAt)
            .ThenByDescending(entry => entry.Status.CreatedAt)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Skip(MaxRetainedTerminalJobCount)
            .Select(entry => entry.Key)
            .ToList();
        foreach (var jobId in excessTerminalJobs)
        {
            _jobs.TryRemove(jobId, out _);
        }
    }

    private enum AutoGenJobCancellationReason
    {
        None,
        HostStopping,
        UserRequested,
        LeaseLost
    }

    private sealed record AutoGenJobRunRange(
        int WeekIndex,
        DateOnly WeekStart,
        DateOnly RangeStartDate,
        DateOnly RangeEndDate);

    private sealed class AutoGenJobRuntime
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _cts = new();
        private AutoGenJobState _state = AutoGenJobState.Queued;
        private string _currentStage = "Очікує запуску...";
        private DateTimeOffset? _startedAt;
        private DateTimeOffset? _completedAt;
        private int _totalWeeks = 1;
        private int _completedWeeks;
        private int _currentWeekNumber;
        private DateOnly? _currentWeekStartDate;
        private DateOnly? _currentRangeStartDate;
        private DateOnly? _currentRangeEndDate;
        private int _created;
        private int _skipped;
        private int _warningCount;
        private int _gapCount;
        private int _deficitCount;
        private string? _lastCompletedMessage;
        private AutoGenResult? _result;
        private AutoGenRunReport? _report;
        private AutoGenDraftPlanPayload? _planPayload;
        private AutoGenPlanSummaryDto? _planSummary;
        private string? _error;
        private AutoGenJobCancellationReason _cancellationReason;

        public AutoGenJobRuntime(AutoGenJobRequest request)
        {
            Request = request;
            JobId = request.ClientJobId ?? Guid.NewGuid().ToString("N");
            RequestHash = ComputeRequestHash(request);
            CreatedAt = DateTimeOffset.UtcNow;
        }

        public string JobId { get; }
        public DateTimeOffset CreatedAt { get; }
        public AutoGenJobRequest Request { get; }
        public string RequestHash { get; }
        public string? OwnerInstanceId { get; private set; }
        public int Attempt { get; private set; }
        public DateTime? LeaseExpiresAtUtc { get; private set; }
        public bool IsDurable => OwnerInstanceId is not null && Attempt > 0;
        public AutoGenDraftPlanPayload? PlanPayload
        {
            get
            {
                lock (_sync)
                {
                    return _planPayload;
                }
            }
        }
        public CancellationToken Token => _cts.Token;
        public AutoGenJobCancellationReason CancellationReason
        {
            get
            {
                lock (_sync)
                {
                    return _cancellationReason;
                }
            }
        }

        public void AttachDurableClaim(string ownerInstanceId, int attempt, DateTime leaseExpiresAtUtc)
        {
            OwnerInstanceId = ownerInstanceId;
            Attempt = attempt;
            LeaseExpiresAtUtc = leaseExpiresAtUtc;
        }

        public void AttachPlan(AutoGenDraftPlanPayload payload)
        {
            lock (_sync)
            {
                _planPayload = payload;
                _planSummary = payload.ToSummary();
            }
        }

        public void UpdatePlanSummary(AutoGenPlanSummaryDto summary)
        {
            lock (_sync)
            {
                _planSummary = summary;
            }
        }

        public void ReleasePersistedPlanPayload()
        {
            lock (_sync)
            {
                _planPayload = null;
            }
        }

        public void RequestCancellation()
            => RequestCancellationFor(AutoGenJobCancellationReason.UserRequested);

        public void RequestCancellationFor(AutoGenJobCancellationReason reason)
        {
            lock (_sync)
            {
                if (_state is AutoGenJobState.Succeeded or AutoGenJobState.Failed or AutoGenJobState.Canceled)
                {
                    return;
                }
                if (reason > _cancellationReason)
                {
                    _cancellationReason = reason;
                }
                _currentStage = _cancellationReason switch
                {
                    AutoGenJobCancellationReason.LeaseLost => "Втрачено lease, аварійно зупиняємо виконання...",
                    AutoGenJobCancellationReason.HostStopping => "Сервер завершує роботу, зупиняємо завдання...",
                    _ => "Скасування запитано, завершуємо поточний безпечний етап..."
                };
            }
            _cts.Cancel();
        }

        public void MarkRunning(int totalWeeks)
        {
            lock (_sync)
            {
                _state = AutoGenJobState.Running;
                _startedAt = DateTimeOffset.UtcNow;
                _totalWeeks = Math.Max(1, totalWeeks);
                _currentStage = "Підготовка автогенерації...";
            }
        }

        public void StartWeek(int weekIndex, DateOnly weekStart, DateOnly rangeStartDate, DateOnly rangeEndDate)
        {
            lock (_sync)
            {
                _currentWeekNumber = weekIndex + 1;
                _currentWeekStartDate = weekStart;
                _currentRangeStartDate = rangeStartDate;
                _currentRangeEndDate = rangeEndDate;
                _currentStage = $"{BuildStageVerb(Request.Kind)} {rangeStartDate:dd.MM.yyyy} – {rangeEndDate:dd.MM.yyyy}";
            }
        }

        public void CompleteWeek(int weekIndex, DateOnly rangeStartDate, DateOnly rangeEndDate, AutoGenResult weekResult, AutoGenResult partialResult)
        {
            lock (_sync)
            {
                _completedWeeks = Math.Max(_completedWeeks, weekIndex + 1);
                _created = partialResult.Created;
                _skipped = partialResult.Skipped;
                _warningCount = partialResult.Warnings.Count;
                _gapCount = partialResult.GapDetails?.Count ?? 0;
                _deficitCount = partialResult.Preflight?.Sum(item => item.Count) ?? 0;
                _result = partialResult;
                _lastCompletedMessage = BuildCompletedMessage(rangeStartDate, rangeEndDate, weekResult);
                _currentStage = _completedWeeks >= _totalWeeks
                    ? "Формуємо фінальний звіт..."
                    : "Підготовка наступного тижня...";
            }
        }

        public void MarkSucceeded(AutoGenResult result, AutoGenRunReport report)
        {
            lock (_sync)
            {
                _state = AutoGenJobState.Succeeded;
                _completedAt = DateTimeOffset.UtcNow;
                _completedWeeks = _totalWeeks;
                ApplyFinalResult(result, report);
                _currentStage = "Готово.";
            }
        }

        public void MarkFailed(string error, AutoGenResult result, AutoGenRunReport report)
        {
            lock (_sync)
            {
                _state = AutoGenJobState.Failed;
                _completedAt = DateTimeOffset.UtcNow;
                _error = error;
                DiscardUnpersistedPlan();
                ApplyFinalResult(result, report);
                _currentStage = "Завершено з помилками.";
            }
        }

        public void MarkCanceled(AutoGenResult result, AutoGenRunReport report)
        {
            lock (_sync)
            {
                _state = AutoGenJobState.Canceled;
                _completedAt = DateTimeOffset.UtcNow;
                DiscardUnpersistedPlan();
                ApplyFinalResult(result, report);
                _currentStage = _cancellationReason == AutoGenJobCancellationReason.HostStopping
                    ? "Скасовано під час завершення роботи сервера."
                    : "Скасовано користувачем.";
            }
        }

        public AutoGenJobStatus ToDto()
        {
            lock (_sync)
            {
                return new AutoGenJobStatus(
                    JobId,
                    _state,
                    Request.Kind,
                    Request.Title ?? BuildDefaultTitle(Request.Kind),
                    _currentStage,
                    CreatedAt,
                    _startedAt,
                    _completedAt,
                    Request.FromDate,
                    Request.ToDate,
                    _totalWeeks,
                    _completedWeeks,
                    _currentWeekNumber,
                    _currentWeekStartDate,
                    _currentRangeStartDate,
                    _currentRangeEndDate,
                    _created,
                    _skipped,
                    _warningCount,
                    _gapCount,
                    _deficitCount,
                    CalculatePercent(),
                    _cts.IsCancellationRequested,
                    _lastCompletedMessage,
                    _result,
                    _report,
                    _error,
                    _planSummary);
            }
        }

        private void ApplyFinalResult(AutoGenResult result, AutoGenRunReport report)
        {
            _result = result;
            _report = report;
            _created = result.Created;
            _skipped = result.Skipped;
            _warningCount = result.Warnings.Count;
            _gapCount = result.GapDetails?.Count ?? 0;
            _deficitCount = result.Preflight?.Sum(item => item.Count) ?? 0;
        }

        private void DiscardUnpersistedPlan()
        {
            _planPayload = null;
            _planSummary = null;
        }

        private int CalculatePercent()
        {
            if (_state == AutoGenJobState.Queued)
            {
                return 0;
            }
            if (_state is AutoGenJobState.Succeeded or AutoGenJobState.Failed or AutoGenJobState.Canceled)
            {
                return 100;
            }
            var total = Math.Max(1, _totalWeeks);
            var completed = Math.Clamp(_completedWeeks, 0, total);
            var minimum = _currentWeekNumber > 0 ? 1 : 0;
            return Math.Clamp((int)Math.Floor((double)completed / total * 100), minimum, 99);
        }

        private static string BuildStageVerb(AutoGenJobKind kind)
            => kind switch
            {
                AutoGenJobKind.Preflight => "Перевіряємо ресурси",
                AutoGenJobKind.Fill => "Заповнюємо порожні слоти",
                _ => "Генеруємо чернетки"
            };

        private static string BuildCompletedMessage(DateOnly rangeStartDate, DateOnly rangeEndDate, AutoGenResult result)
        {
            var parts = new List<string>
            {
                $"Готово {rangeStartDate:dd.MM.yyyy} – {rangeEndDate:dd.MM.yyyy}",
                $"створено {result.Created}"
            };
            var gapCount = result.GapDetails?.Count ?? 0;
            var deficitCount = result.Preflight?.Sum(item => item.Count) ?? 0;
            if (gapCount > 0)
            {
                parts.Add($"порожніх слотів {gapCount}");
            }
            if (deficitCount > 0)
            {
                parts.Add($"дефіцитів {deficitCount}");
            }
            return string.Join(", ", parts) + ".";
        }
    }
}
