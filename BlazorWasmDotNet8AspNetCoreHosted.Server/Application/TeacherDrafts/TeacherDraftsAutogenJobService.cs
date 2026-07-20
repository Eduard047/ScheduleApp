using System.Collections.Concurrent;
using System.Text.Json;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

public sealed class AutoGenJobValidationException(string message) : Exception(message);

public sealed class AutoGenJobCapacityException(string message) : Exception(message);

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
    private static readonly DateOnly MinSupportedDate = new(2000, 1, 1);
    private static readonly DateOnly MaxSupportedDate = new(2100, 12, 24);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TeacherDraftsAutogenJobService> _logger;
    private readonly ConcurrentDictionary<string, AutoGenJobRuntime> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _runningTasks = new(StringComparer.Ordinal);
    private readonly object _startSync = new();
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly SemaphoreSlim _persistenceGate = new(1, 1);
    private int _stopping;
    private static readonly TimeSpan CompletedJobTtl = TimeSpan.FromHours(6);
    private static readonly JsonSerializerOptions PersistenceJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public TeacherDraftsAutogenJobService(IServiceScopeFactory scopeFactory, ILogger<TeacherDraftsAutogenJobService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public AutoGenJobStartResult Start(AutoGenJobRequest request)
    {
        ThrowIfStopping();
        var normalized = NormalizeRequest(request);
        if (normalized.ClientJobId is { } clientJobId)
        {
            lock (_startSync)
            {
                if (_jobs.TryGetValue(clientJobId, out var existingJob))
                {
                    return new AutoGenJobStartResult(clientJobId, existingJob.ToDto());
                }
            }

            if (TryReadPersistedStatus(clientJobId) is { } persistedStatus)
            {
                return new AutoGenJobStartResult(clientJobId, persistedStatus);
            }
        }

        AutoGenJobRuntime job;
        lock (_startSync)
        {
            ThrowIfStopping();
            CleanupOldJobs();
            if (normalized.ClientJobId is { } queuedJobId
                && _jobs.TryGetValue(queuedJobId, out var existingJob))
            {
                return new AutoGenJobStartResult(queuedJobId, existingJob.ToDto());
            }
            var outstandingJobCount = _jobs.Values.Count(item => !IsTerminalState(item.ToDto().State));
            if (outstandingJobCount >= MaxOutstandingJobCount)
            {
                throw new AutoGenJobCapacityException(
                    $"Черга автогенерації заповнена. Дочекайтеся завершення одного з {MaxOutstandingJobCount} активних завдань і повторіть спробу.");
            }

            job = new AutoGenJobRuntime(normalized);
            _jobs[job.JobId] = job;
            TryPersistSnapshot(job, "створення завдання");
            QueueJob(job);
        }
        return new AutoGenJobStartResult(job.JobId, job.ToDto());
    }

    public Task StartAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

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
                job.RequestCancellation();
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
        => _jobs.TryGetValue(jobId, out var job) ? job.ToDto() : TryReadPersistedStatus(jobId);

    public AutoGenJobStatus? Cancel(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return TryCancelPersistedJob(jobId);
        }
        job.RequestCancellation();
        _ = TryPersistSnapshotAsync(job, "запит скасування");
        return job.ToDto();
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

    private void TryPersistSnapshot(AutoGenJobRuntime job, string operation)
    {
        _persistenceGate.Wait();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var status = job.ToDto();
            var run = db.AutoGenJobRuns.FirstOrDefault(item => item.JobId == job.JobId);
            if (run is not null && IsTerminalState(ToJobState(run.State)) && !IsTerminalState(status.State))
            {
                return;
            }
            if (run is null)
            {
                run = new AutoGenJobRun { JobId = job.JobId };
                db.AutoGenJobRuns.Add(run);
            }
            ApplyJobRun(run, status, job.Request);
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не вдалося зберегти стан завдання автогенерації {JobId} під час етапу \"{Operation}\". Завдання продовжує роботу в пам'яті.", job.JobId, operation);
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    private async Task TryPersistSnapshotAsync(AutoGenJobRuntime job, string operation)
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
                return;
            }
            if (run is null)
            {
                run = new AutoGenJobRun { JobId = job.JobId };
                db.AutoGenJobRuns.Add(run);
            }
            ApplyJobRun(run, status, job.Request);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не вдалося зберегти стан завдання автогенерації {JobId} під час етапу \"{Operation}\". Завдання продовжує роботу в пам'яті.", job.JobId, operation);
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    private AutoGenJobStatus? TryReadPersistedStatus(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return null;
        }

        _persistenceGate.Wait();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            using var transaction = db.Database.BeginTransaction();
            var run = db.AutoGenJobRuns.FirstOrDefault(item => item.JobId == jobId);
            if (run is null)
            {
                return null;
            }

            if (!IsTerminalState(ToJobState(run.State)))
            {
                var now = DateTime.UtcNow;
                run.State = (int)AutoGenJobState.Failed;
                run.CompletedAtUtc ??= now;
                run.CurrentStage = "Перервано через перезапуск сервера.";
                run.Error = "Завдання автогенерації не завершилося, оскільки сервер було перезапущено.";
                run.Percent = 100;
                run.UpdatedAtUtc = now;
                var interruptedStatus = BuildStatusFromColumns(run);
                run.StatusJson = JsonSerializer.Serialize(interruptedStatus, PersistenceJsonOptions);
                db.SaveChanges();
                transaction.Commit();
                return interruptedStatus;
            }

            transaction.Commit();
            return DeserializeStatusOrFallback(run);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не вдалося прочитати стан завдання автогенерації {JobId} з бази.", jobId);
            return null;
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    private AutoGenJobStatus? TryCancelPersistedJob(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return null;
        }

        _persistenceGate.Wait();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var run = db.AutoGenJobRuns.FirstOrDefault(item => item.JobId == jobId);
            if (run is null)
            {
                return null;
            }

            var state = ToJobState(run.State);
            if (IsTerminalState(state))
            {
                return DeserializeStatusOrFallback(run);
            }

            var now = DateTime.UtcNow;
            run.State = (int)AutoGenJobState.Canceled;
            run.CompletedAtUtc ??= now;
            run.CancellationRequested = true;
            run.CurrentStage = "Скасовано після відновлення стану з бази.";
            run.Percent = 100;
            run.UpdatedAtUtc = now;
            var status = BuildStatusFromColumns(run);
            run.StatusJson = JsonSerializer.Serialize(status, PersistenceJsonOptions);
            db.SaveChanges();
            return status;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не вдалося скасувати збережене завдання автогенерації {JobId}.", jobId);
            return null;
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

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
                    $"М'який ліміт паралельних груп має бути від 1 до {MaxGroupCount}.");
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
        if (request.Kind == AutoGenJobKind.Preflight)
        {
            clearExisting = false;
            preflightOnly = true;
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
        var weekStarts = BuildWeekStarts(job.Request.FromDate, job.Request.ToDate);
        var ownsExecutionGate = false;

        try
        {
            await _executionGate.WaitAsync(job.Token);
            ownsExecutionGate = true;
            job.Token.ThrowIfCancellationRequested();
            job.MarkRunning(weekStarts.Count);
            await TryPersistSnapshotAsync(job, "запуск завдання");
            job.Token.ThrowIfCancellationRequested();

            using var scope = _scopeFactory.CreateScope();
            var autogen = scope.ServiceProvider.GetRequiredService<TeacherDraftsAutogenService>();
            var runRanges = new List<(int WeekIndex, DateOnly WeekStart, DateOnly RangeStartDate, DateOnly RangeEndDate)>();
            if (weekStarts.Count > 1)
            {
                runRanges.Add((weekStarts.Count - 1, weekStarts[0], job.Request.FromDate, job.Request.ToDate));
            }
            else
            {
                for (var weekIndex = 0; weekIndex < weekStarts.Count; weekIndex++)
                {
                    var weekStart = weekStarts[weekIndex];
                    var weekEnd = weekStart.AddDays(6);
                    var rangeStartDate = job.Request.FromDate > weekStart ? job.Request.FromDate : weekStart;
                    var rangeEndDate = job.Request.ToDate < weekEnd ? job.Request.ToDate : weekEnd;
                    if (rangeEndDate >= rangeStartDate)
                    {
                        runRanges.Add((weekIndex, weekStart, rangeStartDate, rangeEndDate));
                    }
                }
            }

            foreach (var runRange in runRanges)
            {
                job.Token.ThrowIfCancellationRequested();

                job.StartWeek(runRange.WeekIndex, runRange.WeekStart, runRange.RangeStartDate, runRange.RangeEndDate);
                await TryPersistSnapshotAsync(job, "початок діапазону");
                var request = BuildDraftRequest(job.Request, runRange.WeekStart, runRange.RangeStartDate, runRange.RangeEndDate);
                var action = await autogen.DraftAutoGen(request, job.Token);
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
                await TryPersistSnapshotAsync(job, "завершення діапазону");
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
            await TryPersistSnapshotAsync(job, "завершення завдання");
        }
        catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
        {
            var result = TeacherDraftsAutogenReportBuilder.BuildResult(created, skipped, warnings, gapDetails, preflight);
            var report = TeacherDraftsAutogenReportBuilder.BuildReport(job.Request.FromDate, job.Request.ToDate, Math.Max(1, weekStarts.Count), result);
            job.MarkCanceled(result, report);
            await TryPersistSnapshotAsync(job, "скасування завдання");
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
            await TryPersistSnapshotAsync(job, "помилка завдання");
        }
        finally
        {
            if (ownsExecutionGate)
            {
                _executionGate.Release();
            }
            CleanupOldJobs();
        }
    }

    // Формує безпечний текст статусу без розкриття внутрішніх деталей винятку.
    private static string BuildPublicFailureMessage(Exception exception, string jobId)
        => exception switch
        {
            AutoGenJobValidationException or AutoGenJobCapacityException
                => $"{exception.Message} Код завдання: {jobId}.",
            OperationCanceledException
                => $"Виконання автогенерації було перервано. Код завдання: {jobId}.",
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
        private string? _error;

        public AutoGenJobRuntime(AutoGenJobRequest request)
        {
            Request = request;
            JobId = request.ClientJobId ?? Guid.NewGuid().ToString("N");
            CreatedAt = DateTimeOffset.UtcNow;
        }

        public string JobId { get; }
        public DateTimeOffset CreatedAt { get; }
        public AutoGenJobRequest Request { get; }
        public CancellationToken Token => _cts.Token;

        public void RequestCancellation()
        {
            lock (_sync)
            {
                if (_state is AutoGenJobState.Succeeded or AutoGenJobState.Failed or AutoGenJobState.Canceled)
                {
                    return;
                }
                _currentStage = "Скасування запитано, завершуємо поточний безпечний етап...";
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
                ApplyFinalResult(result, report);
                _currentStage = "Скасовано користувачем.";
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
                    _error);
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
