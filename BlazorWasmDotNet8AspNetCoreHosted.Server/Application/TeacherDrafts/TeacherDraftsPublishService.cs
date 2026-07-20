using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

// Сервіс публікації чернеток у офіційний розклад.
public sealed class TeacherDraftsPublishService
{
    private readonly AppDbContext _db;
    private readonly RulesService _rules;
    private readonly AggregatesService _aggregates;
    public TeacherDraftsPublishService(AppDbContext db, RulesService rules, AggregatesService aggregates)
    {
        _db = db;
        _rules = rules;
        _aggregates = aggregates;
    }
    // Схвалює вибір викладача разом з усіма рядками кожного логічного заняття.
    public async Task<IActionResult> ApproveWeekAsync(ApproveWeekRequest r)
    {
        if (!DateHelpers.IsSupportedScheduleDate(r.WeekStart))
        {
            return new BadRequestObjectResult(new { message = DateHelpers.SupportedScheduleDateMessage });
        }
        var start = r.WeekStart;
        var end = start.AddDays(7);
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var weekDrafts = await _db.TeacherDraftItems
            .Where(x => x.Date >= start && x.Date < end)
            .OrderBy(x => x.Date)
            .ThenBy(x => x.StartTime)
            .ThenBy(x => x.GroupId)
            .ThenBy(x => x.Id)
            .ToListAsync();
        var rows = ExpandTeacherPublishSelection(weekDrafts, r.TeacherId);
        foreach (var x in rows)
        {
            x.Status = DraftStatus.Published;
        }
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return new OkResult();
    }
    // Публікує чернетки тижня в офіційний розклад із валідацією.
    public async Task<ActionResult<PublishWeekResults>> PublishWeekAsync(PublishWeekRequest r)
    {
        if (!DateHelpers.IsSupportedScheduleDate(r.WeekStart))
        {
            return new BadRequestObjectResult(new { message = DateHelpers.SupportedScheduleDateMessage });
        }
        var start = r.WeekStart;
        var end = start.AddDays(7);
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var weekDrafts = await _db.TeacherDraftItems
            .Where(x => x.Date >= start && x.Date < end)
            .Include(x => x.Group)
            .OrderBy(x => x.Date)
            .ThenBy(x => x.StartTime)
            .ThenBy(x => x.GroupId)
            .ThenBy(x => x.Id)
            .ToListAsync();
        var drafts = r.TeacherId is int teacherId
            ? ExpandTeacherPublishSelection(weekDrafts, teacherId)
            : weekDrafts;
        if (drafts.Count == 0)
        {
            await tx.CommitAsync();
            return new OkObjectResult(new PublishWeekResults(0, 0, new List<string>()));
        }

        var preflightViolations = FindLegacyDuplicateViolations(drafts).ToList();
        preflightViolations.AddRange(FindMixedStatusLogicalEventViolations(drafts));
        if (r.TeacherId is not null)
        {
            preflightViolations.AddRange(FindPartialTeacherSelectionViolations(weekDrafts, drafts));
        }
        if (preflightViolations.Count > 0)
        {
            await tx.RollbackAsync();
            var warnings = new List<string>
            {
                "Публікацію скасовано: вибраний пакет не утворює цілісні логічні події."
            };
            warnings.AddRange(preflightViolations.Distinct(StringComparer.Ordinal));
            return new OkObjectResult(new PublishWeekResults(0, drafts.Count, warnings));
        }

        var lessonTypeIds = drafts.Select(d => d.LessonTypeId).Distinct().ToList();
        var lessonTypeRoomMap = await _db.LessonTypes.AsNoTracking()
            .Where(lt => lessonTypeIds.Contains(lt.Id))
            .ToDictionaryAsync(lt => lt.Id, lt => lt.RequiresRoom);
        var calendar = await _db.CalendarExceptions.AsNoTracking()
            .Where(x => x.Date >= start && x.Date < end)
            .ToListAsync();

        var resolvedBatchKeys = ResolvePublishBatchKeys(drafts);
        var candidates = drafts
            .Select(d => new PublishCandidate(
                d,
                lessonTypeRoomMap.TryGetValue(d.LessonTypeId, out var requiresRoom) && !requiresRoom
                    ? null
                    : d.RoomId,
                resolvedBatchKeys[d.Id]))
            .ToList();
        var violations = FindLogicalEventResourceViolations(candidates).ToList();

        // Спочатку перевіряємо весь пакет, не додаючи жодного запису до офіційного розкладу.
        foreach (var candidate in candidates)
        {
            var d = candidate.Draft;
            var isWeekend = d.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var scoped = TeacherDraftsHelpers.ResolveCalendarOverride(
                calendar,
                d.Date,
                d.Group.CourseId,
                d.GroupId);
            var isWorking = scoped ?? !isWeekend;
            if (!isWorking)
            {
                violations.Add(
                    $"[{d.Date:yyyy-MM-dd} {d.StartTime:HH\\:mm}-{d.EndTime:HH\\:mm}] Публікацію у неробочий день заборонено без явного робочого винятку календаря.");
            }
            var req = new UpsertScheduleItemRequest(
                Id: null,
                Date: d.Date,
                TimeStart: d.StartTime.ToString("HH:mm"),
                TimeEnd: d.EndTime.ToString("HH:mm"),
                GroupId: d.GroupId,
                ModuleId: d.ModuleId,
                TeacherId: d.TeacherId,
                RoomId: candidate.RoomId,
                LessonTypeId: d.LessonTypeId,
                IsLocked: false,
                OverrideNonWorkingDay: false);
            var (errors, _) = await _rules.ValidateUpsertAsync(
                req,
                projectedModuleTopicId: d.ModuleTopicId);
            violations.AddRange(errors.Select(error =>
                $"[{d.Date:yyyy-MM-dd} {d.StartTime:HH\\:mm}-{d.EndTime:HH\\:mm}] {error}"));
        }

        var pendingDrafts = candidates
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
            .ToList();
        var hardRuleResult = await new TeacherDraftsAutogenHardRuleValidator(_db).ValidateAsync(
            new TeacherDraftsAutogenHardRuleValidationRequest(
                CourseId: drafts[0].Group.CourseId,
                GroupIds: drafts.Select(draft => draft.GroupId).Distinct().ToList(),
                From: start,
                To: end.AddDays(-1),
                Days: WeekPreset.MonSun,
                AllowIncompleteDrafts: false,
                PendingDrafts: pendingDrafts,
                IncludeStoredDrafts: false));
        violations.AddRange(hardRuleResult.Violations);

        if (violations.Count > 0)
        {
            await tx.RollbackAsync();
            var warnings = new List<string>
            {
                "Публікацію скасовано: пакет містить порушення обов'язкових правил."
            };
            warnings.AddRange(violations.Distinct(StringComparer.Ordinal));
            return new OkObjectResult(new PublishWeekResults(0, drafts.Count, warnings));
        }

        var scheduleItems = new List<ScheduleItem>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var d = candidate.Draft;
            var item = new ScheduleItem
            {
                Date = d.Date,
                DayOfWeek = d.Date.DayOfWeek,
                StartTime = d.StartTime,
                EndTime = d.EndTime,
                GroupId = d.GroupId,
                ModuleId = d.ModuleId,
                RoomId = candidate.RoomId,
                TeacherId = d.TeacherId,
                ModuleTopicId = d.ModuleTopicId,
                LessonTypeId = d.LessonTypeId,
                BatchKey = candidate.BatchKey,
                IsLocked = false,
                IsSelfStudy = d.IsSelfStudy
            };
            scheduleItems.Add(item);
        }
        _db.ScheduleItems.AddRange(scheduleItems);
        await _db.SaveChangesAsync();
        var publishedIds = drafts.Select(draft => draft.Id).ToList();
        await _db.TeacherDraftItems
            .Where(x => publishedIds.Contains(x.Id))
            .ExecuteDeleteAsync();
        var affectedPlans = drafts
            .Select(x => new { x.ModuleId, CourseId = x.Group.CourseId })
            .Distinct()
            .Select(x => (x.CourseId, x.ModuleId));
        var affectedLoads = drafts
            .Where(x => x.TeacherId != null)
            .Select(x => new { TeacherId = x.TeacherId!.Value, CourseId = x.Group.CourseId })
            .Distinct()
            .Select(x => (x.TeacherId, x.CourseId));
        await _aggregates.RecalcAsync(affectedPlans, affectedLoads);
        await tx.CommitAsync();
        return new OkObjectResult(new PublishWeekResults(scheduleItems.Count, 0, new List<string>()));
    }

    // Розширює вибір викладача лише до рядків того самого BatchKey і сигнатури в межах запитаного тижня.
    private static List<TeacherDraftItem> ExpandTeacherPublishSelection(
        IReadOnlyList<TeacherDraftItem> weekDrafts,
        int teacherId)
    {
        var selected = weekDrafts
            .Where(draft => draft.TeacherId == teacherId)
            .ToList();
        if (selected.Count == 0)
        {
            return new List<TeacherDraftItem>();
        }

        return weekDrafts
            .Where(candidate => selected.Any(source =>
                HasSameEventSignature(source, candidate)
                && HasSameEventKey(source, candidate)))
            .ToList();
    }

    private static IEnumerable<string> FindPartialTeacherSelectionViolations(
        IReadOnlyList<TeacherDraftItem> weekDrafts,
        IReadOnlyList<TeacherDraftItem> selectedDrafts)
    {
        var selectedIds = selectedDrafts.Select(draft => draft.Id).ToHashSet();
        foreach (var outside in weekDrafts.Where(draft => !selectedIds.Contains(draft.Id)))
        {
            var colliding = selectedDrafts.FirstOrDefault(selected => HasSameEventSignature(selected, outside));
            if (colliding is null)
            {
                continue;
            }

            yield return $"{outside.Date:yyyy-MM-dd} {outside.StartTime:HH\\:mm}-{outside.EndTime:HH\\:mm}: знайдено рядок тієї самої сигнатури з іншим BatchKey; вибір за викладачем не може опублікувати подію частково.";
        }
    }

    private static IEnumerable<string> FindLegacyDuplicateViolations(
        IReadOnlyList<TeacherDraftItem> drafts)
    {
        foreach (var duplicateGroup in drafts
                     .Where(draft => string.IsNullOrWhiteSpace(draft.BatchKey))
                     .GroupBy(draft => new
                     {
                         draft.Date,
                         draft.StartTime,
                         draft.EndTime,
                         draft.GroupId,
                         draft.ModuleId,
                         draft.LessonTypeId,
                         draft.ModuleTopicId,
                         draft.TeacherId,
                         draft.RoomId,
                         draft.IsSelfStudy
                     })
                     .Where(group => group.Count() > 1))
        {
            yield return $"{duplicateGroup.Key.Date:yyyy-MM-dd} {duplicateGroup.Key.StartTime:HH\\:mm}-{duplicateGroup.Key.EndTime:HH\\:mm}: знайдено повністю однакові legacy-рядки без BatchKey; видаліть дублікати перед публікацією.";
        }
    }

    // Вимагає повторного цілісного схвалення для legacy-пакетів зі змішаними статусами рядків.
    private static IEnumerable<string> FindMixedStatusLogicalEventViolations(
        IReadOnlyList<TeacherDraftItem> drafts)
    {
        foreach (var mixedEvent in drafts
                     .Where(draft => !string.IsNullOrWhiteSpace(draft.BatchKey))
                     .GroupBy(draft => new
                     {
                         draft.BatchKey,
                         draft.Date,
                         draft.StartTime,
                         draft.EndTime,
                         draft.GroupId,
                         draft.ModuleId,
                         draft.LessonTypeId
                     })
                     .Where(group => group
                         .Select(draft => draft.Status)
                         .Distinct()
                         .Skip(1)
                         .Any()))
        {
            yield return $"{mixedEvent.Key.Date:yyyy-MM-dd} {mixedEvent.Key.StartTime:HH\\:mm}-{mixedEvent.Key.EndTime:HH\\:mm}: логічне заняття має змішані статуси рядків. Повторно схваліть заняття цілісним пакетом перед публікацією.";
        }
    }

    // Забороняє публікацію події, рядки якої мають різні аудиторії або різний режим самостійної роботи.
    private static IEnumerable<string> FindLogicalEventResourceViolations(
        IReadOnlyList<PublishCandidate> candidates)
    {
        foreach (var eventGroup in candidates
                     .Where(candidate => !string.IsNullOrWhiteSpace(candidate.BatchKey))
                     .GroupBy(candidate => new
                     {
                         candidate.BatchKey,
                         candidate.Draft.Date,
                         candidate.Draft.StartTime,
                         candidate.Draft.EndTime,
                         candidate.Draft.GroupId,
                         candidate.Draft.ModuleId,
                         candidate.Draft.LessonTypeId
                     })
                     .Where(group => group
                         .Select(candidate => new
                         {
                             candidate.RoomId,
                             candidate.Draft.IsSelfStudy
                         })
                         .Distinct()
                         .Skip(1)
                         .Any()))
        {
            yield return $"{eventGroup.Key.Date:yyyy-MM-dd} {eventGroup.Key.StartTime:HH\\:mm}-{eventGroup.Key.EndTime:HH\\:mm}: рядки одного логічного заняття мають різні аудиторії або режим самостійної роботи.";
        }
    }

    // Зберігає наявні ключі, а legacy-рядкам однієї багаторядкової події призначає спільний безпечний ключ.
    private static IReadOnlyDictionary<int, string?> ResolvePublishBatchKeys(
        IReadOnlyList<TeacherDraftItem> drafts)
    {
        var result = drafts.ToDictionary(
            draft => draft.Id,
            draft => string.IsNullOrWhiteSpace(draft.BatchKey) ? null : draft.BatchKey);
        foreach (var signatureGroup in drafts.GroupBy(draft => new
                 {
                     draft.Date,
                     draft.StartTime,
                     draft.EndTime,
                     draft.GroupId,
                     draft.ModuleId,
                     draft.LessonTypeId
                 }))
        {
            var legacyRows = signatureGroup
                .Where(draft => string.IsNullOrWhiteSpace(draft.BatchKey))
                .ToList();
            if (legacyRows.Count <= 1)
            {
                continue;
            }

            var sharedKey = CreateSafeBatchKey("publish");
            foreach (var legacyRow in legacyRows)
            {
                result[legacyRow.Id] = sharedKey;
            }
        }
        return result;
    }

    private static bool HasSameEventSignature(TeacherDraftItem left, TeacherDraftItem right)
        => left.Date == right.Date
           && left.StartTime == right.StartTime
           && left.EndTime == right.EndTime
           && left.GroupId == right.GroupId
           && left.ModuleId == right.ModuleId
           && left.LessonTypeId == right.LessonTypeId;

    private static bool HasSameEventKey(TeacherDraftItem left, TeacherDraftItem right)
        => !string.IsNullOrWhiteSpace(left.BatchKey)
            ? string.Equals(left.BatchKey, right.BatchKey, StringComparison.Ordinal)
            : string.IsNullOrWhiteSpace(right.BatchKey);

    private static string CreateSafeBatchKey(string prefix)
        => $"{prefix}:{Guid.NewGuid():N}";

    private sealed record PublishCandidate(TeacherDraftItem Draft, int? RoomId, string? BatchKey);
}
