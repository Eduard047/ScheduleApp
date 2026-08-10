using System.Text.Json;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

// Повторно перевіряє фактичний стан усього тижня без зміни чернеток або розкладу.
public sealed class TeacherDraftsWeekValidationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppDbContext _db;

    public TeacherDraftsWeekValidationService(AppDbContext db)
        => _db = db;

    public async Task<DraftValidationReportDto> ValidateAsync(
        DateOnly weekStart,
        CancellationToken cancellationToken = default)
    {
        var weekEnd = weekStart.AddDays(6);
        var rows = await _db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => item.Date >= weekStart && item.Date <= weekEnd)
            .Include(item => item.Group)
            .OrderBy(item => item.Date)
            .ThenBy(item => item.StartTime)
            .ThenBy(item => item.GroupId)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);

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
        var publishedGroupIds = rows.Select(item => item.GroupId).Distinct().ToList();
        foreach (var courseId in rows
                     .Select(item => item.Group.CourseId)
                     .Distinct()
                     .OrderBy(id => id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var validation = await hardRuleValidator.ValidateAsync(
                new TeacherDraftsAutogenHardRuleValidationRequest(
                    courseId,
                    publishedGroupIds,
                    weekStart,
                    weekEnd,
                    WeekPreset.MonSun,
                    AllowIncompleteDrafts: false,
                    PendingDrafts: pendingDrafts,
                    IncludeStoredDrafts: false,
                    ScopePendingDraftsToCourse: true),
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
            .ToListAsync(cancellationToken);
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
            .ToListAsync(cancellationToken);
        var scopes = candidates
            .Select(plan => TryResolveScope(plan, out var scope) ? scope : null)
            .OfType<AppliedPlanScope>()
            .GroupBy(scope => scope.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        var warnings = new List<DraftValidationIssueDto>();
        foreach (var scope in scopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentRows = await _db.TeacherDraftItems
                .AsNoTracking()
                .Where(item => item.Date >= scope.Plan.RangeStartDate
                               && item.Date <= scope.Plan.RangeEndDate
                               && scope.GroupIds.Contains(item.GroupId))
                .OrderBy(item => item.Id)
                .Select(item => new KeyValuePair<int, Guid>(item.Id, item.Revision))
                .ToListAsync(cancellationToken);
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

            var key = $"{plan.CourseId}:{plan.RangeStartDate:yyyyMMdd}:{plan.RangeEndDate:yyyyMMdd}:{string.Join(',', groupIds)}";
            scope = new AppliedPlanScope(plan, groupIds, key);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record AppliedPlanScope(
        AutoGenDraftPlan Plan,
        IReadOnlyList<int> GroupIds,
        string Key);
}
