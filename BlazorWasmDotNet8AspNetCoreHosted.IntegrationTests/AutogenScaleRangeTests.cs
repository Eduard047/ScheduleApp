using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class AutogenScaleRangeTests
{
    private const int RecommendedPreferredFirstMaxSlotOrderOverride = 6;
    private const int MaxGapsPerGeneratedWeekAfterFill = 71;
    private const int MaxDiagnosticItems = 25;

    private static readonly DraftAutoGenSoftOptions RecommendedSoftFillOptions = new(
        MaxParallelGroupsPerModuleInSlot: 5,
        RecentRepeatWindowDays: 0,
        PreferredMaxDistinctModulesPerDay: 5,
        MaxDistinctModulesPerDay: 6,
        PreferredFirstPenaltyMultiplier: 0.35,
        TeacherLoadPenaltyWeight: 0.0,
        BuildingDistancePenaltyWeight: 0.0);

    [Fact(Timeout = 600_000)]
    public async Task L3_one_week_autogen_preserves_hard_rules_and_gap_budget()
        => await RunScaleMatrixAsync(ScaleRunMode.OneWeekOnly, "autogen-one-week-scale.txt");

    [Fact(Timeout = 600_000)]
    public async Task L3_two_week_autogen_preserves_hard_rules_and_gap_budget()
        => await RunScaleMatrixAsync(ScaleRunMode.TwoWeeksOnly, "autogen-two-week-scale.txt");

    [Fact(Timeout = 180_000)]
    public async Task L3_preflight_reports_deficits_without_writing_drafts()
    {
        await using var source = ServerConfigurationFactory.CreateSourceContext();
        var scenario = await AutogenScenarioCatalog.BuildL3ScenarioAsync(source);
        await using var snapshot = await SqliteSnapshotFile.CreateFromSourceAsync(source);
        await using var database = new SqliteTempDatabase(snapshot.Path);
        await using var db = database.CreateContext();
        var service = new TeacherDraftsAutogenService(db);
        var beforeCount = await db.TeacherDraftItems.CountAsync();
        var request = scenario.BaseRequest with
        {
            ClearExisting = true,
            CourseId = scenario.CourseId,
            GroupIds = scenario.GroupIds.ToList(),
            AllowOnDaysOff = false,
            Days = WeekPreset.MonSat,
            ModuleHours = scenario.ModuleHours.ToDictionary(kv => kv.Key, kv => kv.Value),
            SoftFill = false,
            AllowIncompleteDrafts = false,
            PreferredFirstMaxSlotOrderOverride = RecommendedPreferredFirstMaxSlotOrderOverride,
            PreflightOnly = true
        };

        var result = ExtractOkResult(await service.DraftAutoGen(request), "preflight");
        var afterCount = await db.TeacherDraftItems.CountAsync();

        Assert.Equal(0, result.Created);
        Assert.Equal(beforeCount, afterCount);
        Assert.NotNull(result.Preflight);
    }

    [LongAutogenScaleFact(Timeout = 1_800_000)]
    public async Task L3_range_autogen_scale_matrix_preserves_hard_rules_and_gap_budget()
        => await RunScaleMatrixAsync(ScaleRunMode.RangeMatrix, "autogen-scale-matrix.txt");

    [LongAutogenScaleFact(Timeout = 1_800_000)]
    public async Task L3_full_course_autogen_scale_preserves_hard_rules_and_gap_budget()
        => await RunScaleMatrixAsync(ScaleRunMode.FullCourse, "autogen-full-course-scale.txt");

    private static async Task RunScaleMatrixAsync(ScaleRunMode mode, string reportFileName)
    {
        await using var source = ServerConfigurationFactory.CreateSourceContext();
        var scenario = await AutogenScenarioCatalog.BuildL3ScenarioAsync(source);
        var courseDurationWeeks = await source.Courses
            .AsNoTracking()
            .Where(course => course.Id == scenario.CourseId)
            .Select(course => course.DurationWeeks)
            .SingleAsync();
        await using var snapshot = await SqliteSnapshotFile.CreateFromSourceAsync(source);

        var cases = BuildScaleCases(scenario.BaseRequest.RangeStartDate!.Value, courseDurationWeeks, mode);
        var reports = new List<ScaleCaseReport>();

        foreach (var scaleCase in cases)
        {
            var caseStopwatch = Stopwatch.StartNew();
            await using var database = new SqliteTempDatabase(snapshot.Path);
            await using var db = database.CreateContext();
            var service = new TeacherDraftsAutogenService(db);

            var initialStopwatch = Stopwatch.StartNew();
            var initial = await RunUiRangeAsync(
                service,
                scenario,
                scaleCase,
                clearExisting: true,
                softFill: false);
            initialStopwatch.Stop();

            var fillStopwatch = Stopwatch.StartNew();
            var fill = await RunUiRangeAsync(
                service,
                scenario,
                scaleCase,
                clearExisting: false,
                softFill: true);
            fillStopwatch.Stop();

            var hardRuleCheck = await new TeacherDraftsAutogenHardRuleValidator(db).ValidateAsync(
                new TeacherDraftsAutogenHardRuleValidationRequest(
                    scenario.CourseId,
                    scenario.GroupIds,
                    scaleCase.From,
                    scaleCase.To,
                    WeekPreset.MonSat));
            var travelViolations = await TravelInvariantVerifier.FindViolationsAsync(
                db,
                scenario.CourseId,
                scaleCase.From,
                scaleCase.To);
            var finalDraftRows = await LoadDraftRowsAsync(db, scenario.GroupIds, scaleCase.From, scaleCase.To);
            var lectureOrderViolations = FindLectureOrderViolations(finalDraftRows);
            var lateLectureViolations = await FindLateLectureSlotViolationsAsync(db, finalDraftRows);
            var finalDraftCount = finalDraftRows.Count;
            caseStopwatch.Stop();

            var report = new ScaleCaseReport(
                scaleCase,
                initial.Created,
                initial.GapDetails?.Count ?? 0,
                fill.Created,
                fill.GapDetails?.Count ?? 0,
                finalDraftCount,
                hardRuleCheck.Violations,
                travelViolations,
                lectureOrderViolations,
                lateLectureViolations,
                hardRuleCheck.MaxSharedGroupCount,
                hardRuleCheck.MaxSharedGroupLabel,
                BuildGapReasonSummary(fill.GapDetails),
                initialStopwatch.Elapsed,
                fillStopwatch.Elapsed,
                caseStopwatch.Elapsed);

            reports.Add(report);
            WriteScaleReport(reports, reportFileName);
            Console.WriteLine(FormatReportLine(report));
        }

        WriteScaleReport(reports, reportFileName);

        var hardFailures = reports
            .SelectMany(report => report.HardViolations
                .Concat(report.TravelViolations)
                .Select(violation => $"{report.Case.Name}: {violation}"))
            .ToList();
        Assert.True(
            hardFailures.Count == 0,
            $"Знайдено порушення жорстких правил: {string.Join(" | ", hardFailures.Take(MaxDiagnosticItems))}");

        var lectureOrderFailures = reports
            .SelectMany(report => report.LectureOrderViolations
                .Select(violation => $"{report.Case.Name}: {violation}"))
            .ToList();
        Assert.True(
            lectureOrderFailures.Count == 0,
            $"Лекційні типи мають іти перед іншими заняттями в межах дня: {string.Join(" | ", lectureOrderFailures.Take(MaxDiagnosticItems))}");

        var lateLectureFailures = reports
            .SelectMany(report => report.LateLectureViolations
                .Select(violation => $"{report.Case.Name}: {violation}"))
            .ToList();
        Assert.True(
            lateLectureFailures.Count == 0,
            $"Лекційні типи не мають виходити за слот #{RecommendedPreferredFirstMaxSlotOrderOverride}: {string.Join(" | ", lateLectureFailures.Take(MaxDiagnosticItems))}");

        var emptyCases = reports
            .Where(report => report.FinalDraftCount == 0)
            .Select(report => report.Case.Name)
            .ToList();
        Assert.True(
            emptyCases.Count == 0,
            $"Автогенерація не створила жодної чернетки для діапазонів: {string.Join(", ", emptyCases)}");

        var gapFailures = reports
            .Where(report => report.FillGapCount > report.MaxAllowedFillGaps)
            .Select(report => $"{report.Case.Name}: {report.FillGapCount}/{report.MaxAllowedFillGaps}")
            .ToList();
        Assert.True(
            gapFailures.Count == 0,
            $"Після дозаповнення лишилося забагато порожніх слотів: {string.Join(" | ", gapFailures)}");
    }

    private static IReadOnlyList<ScaleCase> BuildScaleCases(DateOnly generationStart, int courseDurationWeeks, ScaleRunMode mode)
    {
        var weekStart = StartOfWeek(generationStart);
        var monthEnd = new DateOnly(generationStart.Year, generationStart.Month, 1)
            .AddMonths(1)
            .AddDays(-1);
        var normalizedDurationWeeks = Math.Max(1, courseDurationWeeks);

        if (mode == ScaleRunMode.OneWeekOnly)
        {
            return new[] { BuildWeekCase("one-week", generationStart, weekStart, 1) };
        }

        if (mode == ScaleRunMode.TwoWeeksOnly)
        {
            return new[] { BuildWeekCase("two-weeks", generationStart, weekStart, 2) };
        }

        if (mode == ScaleRunMode.FullCourse)
        {
            return new[] { BuildWeekCase("full-course", generationStart, weekStart, normalizedDurationWeeks) };
        }

        var cases = new List<ScaleCase>
        {
            BuildWeekCase("one-week", generationStart, weekStart, 1),
            BuildWeekCase("two-weeks", generationStart, weekStart, 2),
            BuildWeekCase("three-weeks", generationStart, weekStart, 3),
            new ScaleCase("one-month", generationStart, monthEnd)
        };

        return cases;
    }

    private static ScaleCase BuildWeekCase(string name, DateOnly from, DateOnly firstWeekStart, int weeks)
        => new(name, from, firstWeekStart.AddDays(weeks * 7 - 1));

    private static async Task<AutoGenResult> RunUiRangeAsync(
        TeacherDraftsAutogenService service,
        AutogenScenarioDefinition scenario,
        ScaleCase scaleCase,
        bool clearExisting,
        bool softFill)
    {
        var warnings = new List<string>();
        var gaps = new List<AutoGenGapDetail>();
        var created = 0;
        var skipped = 0;

        foreach (var weekStart in EnumerateWeekStarts(scaleCase.From, scaleCase.To))
        {
            var weekEnd = weekStart.AddDays(6);
            var rangeStart = Max(scaleCase.From, weekStart);
            var rangeEnd = Min(scaleCase.To, weekEnd);
            if (rangeEnd < rangeStart)
            {
                continue;
            }

            var request = scenario.BaseRequest with
            {
                WeekStart = weekStart,
                ClearExisting = clearExisting,
                CourseId = scenario.CourseId,
                GroupIds = scenario.GroupIds.ToList(),
                AllowOnDaysOff = false,
                Days = WeekPreset.MonSat,
                ModuleHours = scenario.ModuleHours.ToDictionary(kv => kv.Key, kv => kv.Value),
                SoftFill = softFill,
                AllowIncompleteDrafts = false,
                RangeStartDate = rangeStart,
                RangeEndDate = rangeEnd,
                PreferredFirstMaxSlotOrderOverride = RecommendedPreferredFirstMaxSlotOrderOverride,
                SoftOptions = softFill ? RecommendedSoftFillOptions : null
            };

            var result = ExtractOkResult(
                await service.DraftAutoGen(request),
                $"{scaleCase.Name} {weekStart:yyyy-MM-dd} {(softFill ? "fill" : "initial")}");
            created += result.Created;
            skipped += result.Skipped;
            warnings.AddRange(result.Warnings);
            if (result.GapDetails is not null)
            {
                gaps.AddRange(result.GapDetails);
            }
        }

        return new AutoGenResult(created, skipped, warnings, gaps, null);
    }

    private static AutoGenResult ExtractOkResult(ActionResult<AutoGenResult> action, string label)
    {
        if (action.Result is OkObjectResult { Value: AutoGenResult ok })
        {
            return ok;
        }

        if (action.Value is AutoGenResult direct)
        {
            return direct;
        }

        if (action.Result is ObjectResult { Value: AutoGenResult failed })
        {
            throw new InvalidOperationException(
                $"{label}: автогенерація повернула помилку, created={failed.Created}, skipped={failed.Skipped}, warnings={string.Join(" | ", failed.Warnings)}, gaps={failed.GapDetails?.Count ?? 0}");
        }

        var serialized = action.Result is ObjectResult { Value: { } value }
            ? JsonSerializer.Serialize(value)
            : action.Result?.GetType().Name ?? "<null>";
        throw new InvalidOperationException($"{label}: неочікувана відповідь автогенерації: {serialized}");
    }

    private static async Task<IReadOnlyList<PlacementRow>> LoadDraftRowsAsync(
        AppDbContext db,
        IReadOnlyList<int> groupIds,
        DateOnly from,
        DateOnly to)
        => await db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => groupIds.Contains(item.GroupId)
                           && item.Date >= from
                           && item.Date <= to
                           && item.Status == DraftStatus.Draft)
            .Select(item => new PlacementRow(
                true,
                item.Date,
                item.StartTime,
                item.EndTime,
                item.GroupId,
                item.Group.Name,
                item.Group.CourseId,
                item.Group.StudentsCount,
                item.ModuleId,
                item.LessonTypeId,
                item.LessonType.Code,
                item.LessonType.Name,
                item.LessonType.PreferredFirstInWeek,
                item.ModuleTopicId,
                item.TeacherId,
                item.Teacher != null ? item.Teacher.FullName : null,
                item.RoomId,
                item.Room != null ? item.Room.Name : null,
                item.Room != null ? item.Room.Capacity : null,
                item.LessonType.RequiresTeacher,
                item.LessonType.RequiresRoom,
                item.LessonType.BlocksTeacher,
                item.LessonType.BlocksRoom,
                item.IsSelfStudy))
            .ToListAsync();

    private static IReadOnlyList<string> FindLectureOrderViolations(IReadOnlyList<PlacementRow> draftRows)
    {
        var violations = new List<string>();
        foreach (var dayGroup in draftRows.GroupBy(row => new { row.GroupId, row.GroupName, row.Date }))
        {
            PlacementRow? firstNonLecture = null;
            var ordered = dayGroup
                .OrderBy(row => row.Start)
                .ThenBy(row => row.End)
                .ToList();
            foreach (var row in ordered)
            {
                if (IsLectureFirstType(row))
                {
                    if (firstNonLecture is not null)
                    {
                        violations.Add(
                            $"{row.Date:yyyy-MM-dd} {row.GroupName}: {firstNonLecture.Start:HH\\:mm}-{firstNonLecture.End:HH\\:mm} {firstNonLecture.LessonTypeName} стоїть перед лекційним типом {row.Start:HH\\:mm}-{row.End:HH\\:mm} {row.LessonTypeName}.");
                    }
                    continue;
                }

                firstNonLecture ??= row;
            }
        }

        return violations;
    }

    private static bool IsLectureFirstType(PlacementRow row)
    {
        var code = row.LessonTypeCode.Trim().ToUpperInvariant();
        var name = row.LessonTypeName.Trim().ToUpperInvariant();
        return row.PreferredFirstInWeek
               || code is "LECTURE" or "LECT" or "LEC"
               || code.Contains("LECT", StringComparison.Ordinal)
               || name.Contains("LECTURE", StringComparison.Ordinal)
               || name.Contains("ЛЕКЦ", StringComparison.Ordinal);
    }

    private static async Task<IReadOnlyList<string>> FindLateLectureSlotViolationsAsync(
        AppDbContext db,
        IReadOnlyList<PlacementRow> draftRows)
    {
        var courseIds = draftRows
            .Select(row => row.CourseId)
            .Distinct()
            .ToList();
        var timeSlots = await db.TimeSlots
            .AsNoTracking()
            .Where(slot => slot.IsActive
                           && (slot.CourseId == null || courseIds.Contains(slot.CourseId.Value)))
            .ToListAsync();
        var cache = new Dictionary<(int CourseId, DayOfWeek Day), IReadOnlyList<TimeSlot>>();

        int ResolveSlotOrder(PlacementRow row)
        {
            var key = (row.CourseId, row.Date.DayOfWeek);
            if (!cache.TryGetValue(key, out var slots))
            {
                slots = TimeSlotsResolver.ResolveForDay(timeSlots, row.CourseId, row.Date.DayOfWeek).Slots;
                cache[key] = slots;
            }

            var index = slots
                .Select((slot, slotIndex) => new { slot, slotIndex })
                .FirstOrDefault(item => item.slot.Start == row.Start && item.slot.End == row.End)
                ?.slotIndex;
            return index is int value ? value + 1 : int.MaxValue;
        }

        return draftRows
            .Where(IsLectureFirstType)
            .Select(row => new { Row = row, SlotOrder = ResolveSlotOrder(row) })
            .Where(item => item.SlotOrder > RecommendedPreferredFirstMaxSlotOrderOverride)
            .Select(item => $"{item.Row.Date:yyyy-MM-dd} {item.Row.GroupName}: слот #{item.SlotOrder} {item.Row.Start:HH\\:mm}-{item.Row.End:HH\\:mm} {item.Row.LessonTypeName}.")
            .ToList();
    }

    private static IEnumerable<DateOnly> EnumerateWeekStarts(DateOnly from, DateOnly to)
    {
        for (var week = StartOfWeek(from); week <= to; week = week.AddDays(7))
        {
            yield return week;
        }
    }

    private static DateOnly StartOfWeek(DateOnly date)
    {
        var offset = date.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)date.DayOfWeek - 1;
        return date.AddDays(-offset);
    }

    private static DateOnly Max(DateOnly left, DateOnly right)
        => left >= right ? left : right;

    private static DateOnly Min(DateOnly left, DateOnly right)
        => left <= right ? left : right;

    private static void WriteScaleReport(IReadOnlyList<ScaleCaseReport> reports, string fileName)
    {
        var reportPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "TestResults",
            fileName));
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllLines(reportPath, reports.Select(FormatReportLine));
    }

    private static string FormatReportLine(ScaleCaseReport report)
        => $"{report.Case.Name}: range={report.Case.From:yyyy-MM-dd}..{report.Case.To:yyyy-MM-dd}, weeks={report.Case.WeekCount}, initialCreated={report.InitialCreated}, initialGaps={report.InitialGapCount}, fillCreated={report.FillCreated}, fillGaps={report.FillGapCount}, maxFillGaps={report.MaxAllowedFillGaps}, finalDrafts={report.FinalDraftCount}, hard={report.HardViolations.Count}, travel={report.TravelViolations.Count}, lectureOrder={report.LectureOrderViolations.Count}, lateLecture={report.LateLectureViolations.Count}, elapsed={FormatElapsed(report.TotalElapsed)} (initial={FormatElapsed(report.InitialElapsed)}, fill={FormatElapsed(report.FillElapsed)}), maxSharedGroups={report.MaxSharedGroupCount} ({report.MaxSharedGroupLabel}), gapReasons={report.GapReasonSummary}";

    private static string BuildGapReasonSummary(IReadOnlyList<AutoGenGapDetail>? gaps)
    {
        if (gaps is null || gaps.Count == 0)
        {
            return "none";
        }

        return string.Join(" / ", gaps
            .SelectMany(gap => (gap.Reason ?? "Причину не визначено.")
                .Split("; ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .GroupBy(reason => SimplifyGapReason(reason))
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(8)
            .Select(group => $"{group.Key}={group.Count()}"));
    }

    private static string SimplifyGapReason(string reason)
    {
        var text = reason.ToLowerInvariant();
        if (text.Contains("викладач", StringComparison.Ordinal))
        {
            return "teacher";
        }

        if (text.Contains("аудитор", StringComparison.Ordinal) || text.Contains("кімнат", StringComparison.Ordinal))
        {
            return "room";
        }

        if (text.Contains("переход", StringComparison.Ordinal) || text.Contains("корпус", StringComparison.Ordinal))
        {
            return "travel";
        }

        if (text.Contains("поряд", StringComparison.Ordinal) || text.Contains("тем", StringComparison.Ordinal))
        {
            return "topic-order";
        }

        if (text.Contains("ліміт", StringComparison.Ordinal) || text.Contains("обмеж", StringComparison.Ordinal))
        {
            return "limit";
        }

        if (text.Contains("спільн", StringComparison.Ordinal))
        {
            return "shared-flow";
        }

        if (text.Contains("робоч", StringComparison.Ordinal) || text.Contains("вихідн", StringComparison.Ordinal))
        {
            return "calendar";
        }

        return "other";
    }

    private static string FormatElapsed(TimeSpan elapsed)
        => elapsed.TotalMinutes >= 1
            ? $"{elapsed.TotalMinutes:N1}m"
            : $"{elapsed.TotalSeconds:N1}s";

    private sealed record ScaleCase(string Name, DateOnly From, DateOnly To)
    {
        public int WeekCount => EnumerateWeekStarts(From, To).Count();
    }

    private enum ScaleRunMode
    {
        OneWeekOnly,
        TwoWeeksOnly,
        RangeMatrix,
        FullCourse
    }

    private sealed record ScaleCaseReport(
        ScaleCase Case,
        int InitialCreated,
        int InitialGapCount,
        int FillCreated,
        int FillGapCount,
        int FinalDraftCount,
        IReadOnlyList<string> HardViolations,
        IReadOnlyList<string> TravelViolations,
        IReadOnlyList<string> LectureOrderViolations,
        IReadOnlyList<string> LateLectureViolations,
        int MaxSharedGroupCount,
        string MaxSharedGroupLabel,
        string GapReasonSummary,
        TimeSpan InitialElapsed,
        TimeSpan FillElapsed,
        TimeSpan TotalElapsed)
    {
        public int MaxAllowedFillGaps => Case.WeekCount * MaxGapsPerGeneratedWeekAfterFill;
    }

    private sealed record PlacementRow(
        bool IsDraft,
        DateOnly Date,
        TimeOnly Start,
        TimeOnly End,
        int GroupId,
        string GroupName,
        int CourseId,
        int GroupStudentsCount,
        int ModuleId,
        int LessonTypeId,
        string LessonTypeCode,
        string LessonTypeName,
        bool PreferredFirstInWeek,
        int? ModuleTopicId,
        int? TeacherId,
        string? TeacherName,
        int? RoomId,
        string? RoomName,
        int? RoomCapacity,
        bool RequiresTeacher,
        bool RequiresRoom,
        bool BlocksTeacher,
        bool BlocksRoom,
        bool IsSelfStudy);
}

internal sealed class LongAutogenScaleFactAttribute : FactAttribute
{
    private const string RunLongAutogenScaleEnvFlag = "RUN_LONG_AUTOGEN_SCALE";

    public LongAutogenScaleFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(RunLongAutogenScaleEnvFlag),
                "1",
                StringComparison.Ordinal))
        {
            Skip = $"Довгі сценарії автогенерації вимкнено. Щоб запустити, встановіть {RunLongAutogenScaleEnvFlag}=1.";
        }
    }
}
