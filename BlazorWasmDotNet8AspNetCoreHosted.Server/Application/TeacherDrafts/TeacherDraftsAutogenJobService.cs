using System.Collections.Concurrent;
using System.Text.Json;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

public sealed class TeacherDraftsAutogenJobService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TeacherDraftsAutogenJobService> _logger;
    private readonly ConcurrentDictionary<string, AutoGenJobRuntime> _jobs = new(StringComparer.Ordinal);
    private static readonly TimeSpan CompletedJobTtl = TimeSpan.FromHours(6);

    public TeacherDraftsAutogenJobService(IServiceScopeFactory scopeFactory, ILogger<TeacherDraftsAutogenJobService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public AutoGenJobStartResult Start(AutoGenJobRequest request)
    {
        CleanupOldJobs();
        var normalized = NormalizeRequest(request);
        var job = new AutoGenJobRuntime(normalized);
        _jobs[job.JobId] = job;
        _ = Task.Run(() => RunAsync(job));
        return new AutoGenJobStartResult(job.JobId, job.ToDto());
    }

    public AutoGenJobStatus? Get(string jobId)
        => _jobs.TryGetValue(jobId, out var job) ? job.ToDto() : null;

    public AutoGenJobStatus? Cancel(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return null;
        }
        job.RequestCancellation();
        return job.ToDto();
    }

    private static AutoGenJobRequest NormalizeRequest(AutoGenJobRequest request)
    {
        var fromDate = request.FromDate;
        var toDate = request.ToDate;
        if (toDate < fromDate)
        {
            (fromDate, toDate) = (toDate, fromDate);
        }
        var groupIds = request.GroupIds
            .Where(groupId => groupId > 0)
            .Distinct()
            .ToList();
        var moduleHours = request.ModuleHours
            .Where(entry => entry.Key > 0 && entry.Value > 0)
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        return request with
        {
            FromDate = fromDate,
            ToDate = toDate,
            GroupIds = groupIds,
            ModuleHours = moduleHours,
            Title = string.IsNullOrWhiteSpace(request.Title) ? BuildDefaultTitle(request.Kind) : request.Title!.Trim()
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
        job.MarkRunning(weekStarts.Count);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var autogen = scope.ServiceProvider.GetRequiredService<TeacherDraftsAutogenService>();

            for (var weekIndex = 0; weekIndex < weekStarts.Count; weekIndex++)
            {
                job.Token.ThrowIfCancellationRequested();

                var weekStart = weekStarts[weekIndex];
                var weekEnd = weekStart.AddDays(6);
                var rangeStartDate = job.Request.FromDate > weekStart ? job.Request.FromDate : weekStart;
                var rangeEndDate = job.Request.ToDate < weekEnd ? job.Request.ToDate : weekEnd;
                if (rangeEndDate < rangeStartDate)
                {
                    continue;
                }

                job.StartWeek(weekIndex, weekStart, rangeStartDate, rangeEndDate);
                var request = BuildDraftRequest(job.Request, weekStart, rangeStartDate, rangeEndDate);
                var action = await autogen.DraftAutoGen(request, job.Token);
                var (weekSucceeded, weekResult, fallbackWarning) = ExtractAutoGenResult(action);
                if (!weekSucceeded)
                {
                    failed = true;
                    warnings.Add($"[{weekStart:yyyy-MM-dd}] Тиждень не згенеровано повністю.");
                }
                if (!string.IsNullOrWhiteSpace(fallbackWarning))
                {
                    warnings.Add(fallbackWarning);
                }

                created += weekResult.Created;
                skipped += weekResult.Skipped;
                warnings.AddRange(weekResult.Warnings);
                if (weekResult.GapDetails is { Count: > 0 })
                {
                    gapDetails.AddRange(weekResult.GapDetails);
                }
                if (weekResult.Preflight is { Count: > 0 })
                {
                    preflight.AddRange(weekResult.Preflight);
                }

                var partialResult = BuildResult(created, skipped, warnings, gapDetails, preflight);
                job.CompleteWeek(weekIndex, rangeStartDate, rangeEndDate, weekResult, partialResult);
            }

            var result = BuildResult(created, skipped, warnings, gapDetails, preflight);
            var report = BuildReport(job.Request.FromDate, job.Request.ToDate, weekStarts.Count, result);
            if (failed)
            {
                job.MarkFailed("Один або кілька тижнів завершилися з помилками.", result, report);
            }
            else
            {
                job.MarkSucceeded(result, report);
            }
        }
        catch (OperationCanceledException)
        {
            var result = BuildResult(created, skipped, warnings, gapDetails, preflight);
            var report = BuildReport(job.Request.FromDate, job.Request.ToDate, Math.Max(1, weekStarts.Count), result);
            job.MarkCanceled(result, report);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AutoGen job {JobId} failed.", job.JobId);
            var result = BuildResult(created, skipped, warnings, gapDetails, preflight);
            var report = BuildReport(job.Request.FromDate, job.Request.ToDate, Math.Max(1, weekStarts.Count), result);
            job.MarkFailed(ex.Message, result, report);
        }
    }

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

    private static AutoGenResult BuildResult(
        int created,
        int skipped,
        IEnumerable<string> warnings,
        IEnumerable<AutoGenGapDetail> gapDetails,
        IEnumerable<AutoGenPreflightItem> preflight)
    {
        var gaps = gapDetails.ToList();
        var preflightItems = MergePreflight(preflight);
        return new AutoGenResult(
            created,
            skipped,
            warnings.Where(warning => !string.IsNullOrWhiteSpace(warning)).Distinct(StringComparer.Ordinal).ToList(),
            gaps,
            BuildGapSummary(gaps),
            preflightItems);
    }

    private static AutoGenRunReport BuildReport(DateOnly fromDate, DateOnly toDate, int totalWeeks, AutoGenResult result)
    {
        var gaps = result.GapDetails ?? new();
        var preflight = result.Preflight ?? new();
        var gapSummary = result.GapSummary ?? BuildGapSummary(gaps);
        var worstGroups = gaps
            .GroupBy(gap => new { gap.GroupId, gap.GroupName })
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.GroupName, StringComparer.Ordinal)
            .Take(8)
            .Select(group => new AutoGenRunReportGroupItem(
                group.Key.GroupId,
                group.Key.GroupName,
                group.Count(),
                group.Take(4).Select(FormatGapExample).ToList()))
            .ToList();
        var worstModules = gaps
            .GroupBy(gap => new
            {
                gap.ModuleId,
                ModuleName = string.IsNullOrWhiteSpace(gap.ModuleName)
                    ? gap.ModuleId is int moduleId ? $"Модуль #{moduleId}" : "Модуль не визначено"
                    : gap.ModuleName!
            })
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.ModuleName, StringComparer.Ordinal)
            .Take(8)
            .Select(group => new AutoGenRunReportModuleItem(
                group.Key.ModuleId,
                group.Key.ModuleName,
                group.Count(),
                group.Take(4).Select(FormatGapExample).ToList()))
            .ToList();
        return new AutoGenRunReport(
            DateTimeOffset.UtcNow,
            fromDate,
            toDate,
            Math.Max(1, totalWeeks),
            result.Created,
            result.Skipped,
            result.Warnings.Count,
            gaps.Count,
            preflight.Sum(item => item.Count),
            gapSummary,
            preflight,
            worstGroups,
            worstModules,
            BuildRecommendations(gapSummary, preflight, worstGroups, worstModules));
    }

    private static string FormatGapExample(AutoGenGapDetail gap)
        => $"{gap.Date:yyyy-MM-dd} {gap.SlotLabel}, {gap.GroupName}";

    private static List<string> BuildRecommendations(
        IReadOnlyList<AutoGenGapSummaryItem> gapSummary,
        IReadOnlyList<AutoGenPreflightItem> preflight,
        IReadOnlyList<AutoGenRunReportGroupItem> worstGroups,
        IReadOnlyList<AutoGenRunReportModuleItem> worstModules)
    {
        var recommendations = new List<string>();
        foreach (var item in preflight.OrderByDescending(item => item.Count).Take(5))
        {
            recommendations.Add(item.Recommendation);
        }
        foreach (var item in gapSummary.Take(5))
        {
            recommendations.Add(item.Code switch
            {
                "teacher" => "Додайте або звільніть викладачів для модулів, які найчастіше блокують порожні слоти.",
                "room" => "Розширте доступні аудиторії або корпуси для груп із найбільшою кількістю порожніх слотів.",
                "travel" => "Перевірте переходи між корпусами: частину занять варто рознести або призначити в одному корпусі.",
                "topic-order" => "Перевірте порядок тем і години модулів: автогенерація не може порушувати хронологію тем.",
                "module-block" => "Залишайте поруч кілька слотів для модулів, які мають іти суцільним блоком.",
                "limit" => "Зменште обсяг на діапазон або розширте навчальні дні/слоти для проблемних груп.",
                _ => "Перегляньте приклади порожніх слотів і додайте повторюваний обмежений ресурс."
            });
        }
        if (worstGroups.Count > 0)
        {
            recommendations.Add($"Почніть ручну перевірку з груп: {string.Join(", ", worstGroups.Take(3).Select(group => group.GroupName))}.");
        }
        if (worstModules.Count > 0)
        {
            recommendations.Add($"Найчастіше проблемні модулі: {string.Join(", ", worstModules.Take(3).Select(module => module.ModuleName))}.");
        }
        return recommendations
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .ToList();
    }

    private static (string Code, string Title) ClassifyGapReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return ("unknown", "Причину не визначено");
        }
        var text = reason.ToLowerInvariant();
        if (text.Contains("викладач", StringComparison.Ordinal))
        {
            return ("teacher", "Немає доступного викладача");
        }
        if (text.Contains("аудитор", StringComparison.Ordinal))
        {
            return ("room", "Немає доступної аудиторії");
        }
        if (text.Contains("перех", StringComparison.Ordinal) || text.Contains("корпус", StringComparison.Ordinal))
        {
            return ("travel", "Недостатньо часу на перехід");
        }
        if (text.Contains("тем", StringComparison.Ordinal) || text.Contains("хронолог", StringComparison.Ordinal))
        {
            return ("topic-order", "Порядок тем не дозволив слот");
        }
        if (text.Contains("блок", StringComparison.Ordinal))
        {
            return ("module-block", "Модуль має йти суцільним блоком");
        }
        if (text.Contains("ліміт", StringComparison.Ordinal) || text.Contains("обмеж", StringComparison.Ordinal))
        {
            return ("limit", "Спрацювали денні або слотні ліміти");
        }
        if (text.Contains("спільн", StringComparison.Ordinal))
        {
            return ("shared-flow", "Спільний потік не готовий");
        }
        return ("other", "Інші причини");
    }

    private static List<AutoGenGapSummaryItem> BuildGapSummary(IEnumerable<AutoGenGapDetail> gapDetails)
        => gapDetails
            .GroupBy(gap => ClassifyGapReason(gap.Reason))
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.Title, StringComparer.Ordinal)
            .Select(group => new AutoGenGapSummaryItem(
                group.Key.Code,
                group.Key.Title,
                group.Count(),
                group.Take(5).Select(FormatGapExample).ToList()))
            .ToList();

    private static List<AutoGenPreflightItem> MergePreflight(IEnumerable<AutoGenPreflightItem> items)
        => items
            .GroupBy(item => item.Code, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                return new AutoGenPreflightItem(
                    first.Code,
                    first.Title,
                    group.Sum(item => item.Count),
                    first.Recommendation,
                    group.SelectMany(item => item.Examples)
                        .Where(example => !string.IsNullOrWhiteSpace(example))
                        .Distinct(StringComparer.Ordinal)
                        .Take(5)
                        .ToList());
            })
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Title, StringComparer.Ordinal)
            .ToList();

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
            JobId = Guid.NewGuid().ToString("N");
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
                $"створено {result.Created}",
                $"пропущено {result.Skipped}"
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
