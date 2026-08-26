using System.Text.Json;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

public sealed class DraftValidationCapacityException(string message) : Exception(message);

public sealed class DraftValidationTimeoutException(string message) : Exception(message);

// Повторно перевіряє фактичний стан усього тижня без зміни чернеток або розкладу.
public sealed class TeacherDraftsWeekValidationService
{
    public const int MaxWeekDraftRowCount = 5_000;
    public const int MaxStoredScheduleRowCount = 5_000;
    public const int MaxWeekCourseCount = 50;
    public const int MaxAppliedPlanScopeCount = 50;
    public const int MaxAppliedScopeRowCount = 20_000;
    public const int MaxAppliedPlanGroupCount = 200;
    public const int MaxAppliedAggregateGroupCount = 1_000;
    public const int MaxAppliedPlanGroupIdsJsonLength = 4_096;
    public const int MaxAppliedPlanRangeDays = 370;
    private static readonly TimeSpan MaxValidationDuration = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppDbContext _db;

    public TeacherDraftsWeekValidationService(AppDbContext db)
        => _db = db;

    public async Task<DraftValidationReportDto> ValidateAsync(
        DateOnly weekStart,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(MaxValidationDuration);
        try
        {
            return await ValidateCoreAsync(weekStart, deadline.Token);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested
            && deadline.IsCancellationRequested)
        {
            throw new DraftValidationTimeoutException(
                "Перевірка тижня перевищила безпечний час виконання. Зменште обсяг даних або повторіть пізніше.");
        }
    }

    private async Task<DraftValidationReportDto> ValidateCoreAsync(
        DateOnly weekStart,
        CancellationToken cancellationToken)
    {
        var weekEnd = weekStart.AddDays(6);
        var rows = await _db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => item.Date >= weekStart && item.Date <= weekEnd)
            .OrderBy(item => item.Date)
            .ThenBy(item => item.StartTime)
            .ThenBy(item => item.GroupId)
            .ThenBy(item => item.Id)
            .Take(MaxWeekDraftRowCount + 1)
            .Select(item => new TeacherDraftItem
            {
                Id = item.Id,
                Revision = item.Revision,
                Date = item.Date,
                DayOfWeek = item.DayOfWeek,
                StartTime = item.StartTime,
                EndTime = item.EndTime,
                LessonTypeId = item.LessonTypeId,
                GroupId = item.GroupId,
                Group = new Group
                {
                    Id = item.Group.Id,
                    Name = item.Group.Name,
                    StudentsCount = item.Group.StudentsCount,
                    CourseId = item.Group.CourseId
                },
                ModuleId = item.ModuleId,
                ModuleTopicId = item.ModuleTopicId,
                TeacherId = item.TeacherId,
                RoomId = item.RoomId,
                Status = item.Status,
                PublishedItemId = item.PublishedItemId,
                BatchKey = item.BatchKey,
                ValidationWarnings = null,
                CreatedAt = item.CreatedAt,
                UpdatedAt = item.UpdatedAt,
                IsLocked = item.IsLocked,
                IsSelfStudy = item.IsSelfStudy,
                GenerationJobId = item.GenerationJobId
            })
            .ToListAsync(cancellationToken);
        if (rows.Count > MaxWeekDraftRowCount)
        {
            throw new DraftValidationCapacityException(
                $"За один тиждень можна перевірити не більше {MaxWeekDraftRowCount} чернеток.");
        }

        var issues = new List<DraftValidationIssueDto>();
        issues.AddRange(TeacherDraftsPublishService
            .FindWholeWeekPackageViolations(rows)
            .Select(violation => new DraftValidationIssueDto(
                "error",
                "week-publish-package-violation",
                "Пакет тижня не готовий до публікації",
                violation)));
        TeacherDraftsPublishService.PublishCandidateValidationResult? candidateValidation = null;
        if (rows.Count > 0)
        {
            candidateValidation = await TeacherDraftsPublishService.ValidatePublishCandidatesAsync(
                _db,
                new RulesService(_db),
                rows,
                weekStart,
                weekEnd.AddDays(1),
                cancellationToken);
            issues.AddRange(candidateValidation.Violations.Select(violation =>
                new DraftValidationIssueDto(
                    "error",
                    "week-publish-rule-violation",
                    "Публікація порушує правила розкладу",
                    violation)));
        }
        var hardRuleValidator = new TeacherDraftsAutogenHardRuleValidator(_db);
        var pendingDrafts = candidateValidation?.Candidates
            .Select(candidate => new TeacherDraftsAutogenPendingDraft(
                candidate.Draft.Date,
                candidate.Draft.StartTime,
                candidate.Draft.EndTime,
                candidate.Draft.GroupId,
                candidate.Draft.ModuleId,
                candidate.Draft.LessonTypeId,
                candidate.Draft.ModuleTopicId,
                candidate.Draft.TeacherId,
                candidate.RoomId,
                candidate.Draft.IsSelfStudy,
                candidate.BatchKey))
            .ToList() ?? new List<TeacherDraftsAutogenPendingDraft>();
        var courseIds = rows
            .Select(item => item.Group.CourseId)
            .Distinct()
            .OrderBy(id => id)
            .Take(MaxWeekCourseCount + 1)
            .ToList();
        if (courseIds.Count > MaxWeekCourseCount)
        {
            throw new DraftValidationCapacityException(
                $"За один тиждень можна перевірити не більше {MaxWeekCourseCount} курсів.");
        }
        foreach (var courseId in courseIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var courseGroupIds = rows
                .Where(item => item.Group.CourseId == courseId)
                .Select(item => item.GroupId)
                .Distinct()
                .ToList();
            var validation = await hardRuleValidator.ValidateAsync(
                new TeacherDraftsAutogenHardRuleValidationRequest(
                    courseId,
                    courseGroupIds,
                    weekStart,
                    weekEnd,
                    WeekPreset.MonSun,
                    AllowIncompleteDrafts: false,
                    PendingDrafts: pendingDrafts,
                    IncludeStoredDrafts: false,
                    ScopePendingDraftsToCourse: true,
                    MaxStoredContextRows: MaxStoredScheduleRowCount),
                cancellationToken);
            issues.AddRange(validation.Violations
                .Distinct(StringComparer.Ordinal)
                .Select(violation => new DraftValidationIssueDto(
                    "error",
                    "week-hard-rule-violation",
                    "Порушення обов'язкового правила",
                    violation)));
        }

        issues.AddRange(await FindAppliedPlanScopeWarningsAsync(
            weekStart,
            weekEnd,
            cancellationToken));
        var scopeRevision = LogicalRevisionToken.Combine(rows.Select(item =>
            new KeyValuePair<int, Guid>(item.Id, item.Revision)));
        var finalRows = await _db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => item.Date >= weekStart && item.Date <= weekEnd)
            .Select(item => new KeyValuePair<int, Guid>(item.Id, item.Revision))
            .Take(MaxWeekDraftRowCount + 1)
            .ToListAsync(cancellationToken);
        if (finalRows.Count > MaxWeekDraftRowCount)
        {
            throw new DraftValidationCapacityException(
                $"За один тиждень можна перевірити не більше {MaxWeekDraftRowCount} чернеток.");
        }
        var finalScopeRevision = LogicalRevisionToken.Combine(finalRows);
        if (finalScopeRevision != scopeRevision)
        {
            issues.Add(new DraftValidationIssueDto(
                "error",
                "week-changed-during-validation",
                "Чернетки змінилися під час перевірки",
                "Оновіть дані та повторіть перевірку всього тижня перед публікацією."));
            scopeRevision = finalScopeRevision;
        }
        return new DraftValidationReportDto(
            DateTimeOffset.UtcNow,
            issues.Distinct().ToList(),
            scopeRevision);
    }

    private async Task<IReadOnlyList<DraftValidationIssueDto>> FindAppliedPlanScopeWarningsAsync(
        DateOnly weekStart,
        DateOnly weekEnd,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var candidates = await _db.AutoGenDraftPlans
            .AsNoTracking()
            .Where(plan => plan.State == (int)AutoGenPlanState.Applied
                           && plan.AppliedScopeRevision != null
                           && plan.ExpiresAtUtc > nowUtc
                           && plan.RangeStartDate <= weekEnd
                           && plan.RangeEndDate >= weekStart)
            .OrderByDescending(plan => plan.AppliedAtUtc)
            .ThenByDescending(plan => plan.Id)
            .Take(MaxAppliedPlanScopeCount + 1)
            .Select(plan => new AutoGenDraftPlan
            {
                Id = plan.Id,
                PlanId = plan.PlanId,
                CourseId = plan.CourseId,
                RangeStartDate = plan.RangeStartDate,
                RangeEndDate = plan.RangeEndDate,
                GroupIdsJson = plan.GroupIdsJson == null
                    ? string.Empty
                    : plan.GroupIdsJson.Substring(0, MaxAppliedPlanGroupIdsJsonLength + 1),
                AppliedScopeRevision = plan.AppliedScopeRevision
            })
            .ToListAsync(cancellationToken);
        if (candidates.Count > MaxAppliedPlanScopeCount)
        {
            throw new DraftValidationCapacityException(
                $"Для перевірки тижня враховується не більше {MaxAppliedPlanScopeCount} актуальних планів.");
        }
        var scopes = candidates
            .Select(plan => TryResolveScope(plan, out var scope) ? scope : null)
            .OfType<AppliedPlanScope>()
            .GroupBy(scope => scope.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        if (scopes.Count == 0)
        {
            return Array.Empty<DraftValidationIssueDto>();
        }

        var minDate = scopes.Min(scope => scope.Plan.RangeStartDate);
        var maxDate = scopes.Max(scope => scope.Plan.RangeEndDate);
        var groupIds = scopes
            .SelectMany(scope => scope.GroupIds)
            .Distinct()
            .ToList();
        if (groupIds.Count > MaxAppliedAggregateGroupCount)
        {
            throw new DraftValidationCapacityException(
                $"Для перевірки застосованих планів можна врахувати не більше {MaxAppliedAggregateGroupCount} різних груп.");
        }
        var scopeRows = await _db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => item.Date >= minDate
                           && item.Date <= maxDate
                           && groupIds.Contains(item.GroupId))
            .OrderBy(item => item.Id)
            .Select(item => new AppliedScopeRevisionRow(
                item.Date,
                item.GroupId,
                item.Id,
                item.Revision))
            .Take(MaxAppliedScopeRowCount + 1)
            .ToListAsync(cancellationToken);
        if (scopeRows.Count > MaxAppliedScopeRowCount)
        {
            throw new DraftValidationCapacityException(
                $"Обсяг чернеток для перевірки застосованих планів перевищує безпечний ліміт {MaxAppliedScopeRowCount}.");
        }

        var warnings = new List<DraftValidationIssueDto>();
        foreach (var scope in scopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentRows = scopeRows
                .Where(item => item.Date >= scope.Plan.RangeStartDate
                               && item.Date <= scope.Plan.RangeEndDate
                               && scope.GroupIds.Contains(item.GroupId))
                .Select(item => new KeyValuePair<int, Guid>(item.Id, item.Revision));
            var currentRevision = LogicalRevisionToken.Combine(currentRows);
            if (currentRevision == scope.Plan.AppliedScopeRevision)
            {
                continue;
            }

            warnings.Add(new DraftValidationIssueDto(
                "warning",
                "autogen-applied-scope-changed",
                "Чернетки відрізняються від застосованого плану",
                $"Після застосування плану {scope.Plan.RangeStartDate:dd.MM.yyyy}–{scope.Plan.RangeEndDate:dd.MM.yyyy} "
                + "склад або порядок чернеток змінився вручну чи іншим планом. Перевірте порядок і повноту занять перед публікацією."));
        }

        return warnings;
    }

    private static bool TryResolveScope(AutoGenDraftPlan plan, out AppliedPlanScope? scope)
    {
        scope = null;
        if (string.IsNullOrWhiteSpace(plan.GroupIdsJson))
        {
            return false;
        }
        if (plan.GroupIdsJson.Length > MaxAppliedPlanGroupIdsJsonLength)
        {
            throw new DraftValidationCapacityException(
                $"Збережена область плану перевищує безпечний розмір {MaxAppliedPlanGroupIdsJsonLength} символів.");
        }

        var rangeDays = plan.RangeEndDate.DayNumber - plan.RangeStartDate.DayNumber + 1;
        if (rangeDays is <= 0 or > MaxAppliedPlanRangeDays)
        {
            throw new DraftValidationCapacityException(
                $"Збережена область плану має містити від 1 до {MaxAppliedPlanRangeDays} днів.");
        }

        try
        {
            var groupIds = JsonSerializer.Deserialize<List<int>>(plan.GroupIdsJson, JsonOptions)?
                .Where(id => id > 0)
                .Distinct()
                .OrderBy(id => id)
                .ToList();
            if (groupIds is not { Count: > 0 })
            {
                return false;
            }
            if (groupIds.Count > MaxAppliedPlanGroupCount)
            {
                throw new DraftValidationCapacityException(
                    $"Збережена область плану може містити не більше {MaxAppliedPlanGroupCount} груп.");
            }

            var key = $"{plan.CourseId}:{plan.RangeStartDate:yyyyMMdd}:{plan.RangeEndDate:yyyyMMdd}:{string.Join(',', groupIds)}";
            scope = new AppliedPlanScope(plan, groupIds.ToHashSet(), key);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record AppliedPlanScope(
        AutoGenDraftPlan Plan,
        IReadOnlySet<int> GroupIds,
        string Key);

    private sealed record AppliedScopeRevisionRow(
        DateOnly Date,
        int GroupId,
        int Id,
        Guid Revision);
}
