using BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class AutogenL3SeptemberTwoWeekScenarioTests
{
    private static readonly IReadOnlyDictionary<string, int> ModuleHoursByCode =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["1"] = 8,
            ["2"] = 9,
            ["3"] = 13,
            ["4"] = 10,
            ["5"] = 6,
            ["6"] = 8,
            ["7"] = 6,
            ["8"] = 7,
            ["9"] = 6,
            ["10"] = 7,
            ["11"] = 5,
            ["12"] = 5,
            ["13"] = 3
        };

    private static readonly string[] GroupNames =
    {
        "9301",
        "9302",
        "9303",
        "9304",
        "9305",
        "9306",
        "9307"
    };

    [Fact(Timeout = 600_000)]
    public async Task L3_20260901_20260912_uses_module_hours_once_for_the_whole_range()
    {
        await using var source = ServerConfigurationFactory.CreateSourceContext();
        var scenario = await LoadScenarioAsync(source);
        await using var snapshot = await SqliteSnapshotFile.CreateFromSourceAsync(source);
        await using var database = new SqliteTempDatabase(snapshot.Path);
        await using var db = database.CreateContext();

        var request = new DraftAutoGenRequest(
            WeekStart: new DateOnly(2026, 8, 31),
            ClearExisting: true,
            CourseId: scenario.CourseId,
            GroupIds: scenario.GroupIds,
            Days: WeekPreset.MonSat,
            ModuleHours: scenario.ModuleHours,
            SoftFill: false,
            AllowIncompleteDrafts: true,
            RangeStartDate: new DateOnly(2026, 9, 1),
            RangeEndDate: new DateOnly(2026, 9, 12),
            SoftOptions: MapSoftOptions(AutoGenRecommendedProfile.CreateSoftOptions()),
            PreferredFirstMaxSlotOrderOverride: AutoGenRecommendedProfile.PreferredFirstMaxSlotOrderOverride);

        var action = await new TeacherDraftsAutogenService(db).DraftAutoGen(request);
        var result = ExtractResult(action);
        Assert.True(
            action.Result is OkObjectResult,
            string.Join(
                " | ",
                result.Warnings
                    .Where(item => item.Contains("Фінальна перевірка", StringComparison.Ordinal))
                    .Take(50)));
        var fillAction = await new TeacherDraftsAutogenService(db).DraftAutoGen(
            request with
            {
                ClearExisting = false,
                SoftFill = true
            });
        var fillResult = ExtractResult(fillAction);

        var createdByModule = await db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => scenario.GroupIds.Contains(item.GroupId)
                           && item.Date >= new DateOnly(2026, 9, 1)
                           && item.Date <= new DateOnly(2026, 9, 12))
            .GroupBy(item => item.ModuleId)
            .Select(group => new { ModuleId = group.Key, Count = group.Count() })
            .OrderBy(item => item.ModuleId)
            .ToListAsync();
        var createdByDate = await db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => scenario.GroupIds.Contains(item.GroupId)
                           && item.Date >= new DateOnly(2026, 9, 1)
                           && item.Date <= new DateOnly(2026, 9, 12))
            .GroupBy(item => item.Date)
            .Select(group => new { Date = group.Key, Count = group.Count() })
            .OrderBy(item => item.Date)
            .ToListAsync();
        var createdByGroupModule = await db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => scenario.GroupIds.Contains(item.GroupId)
                           && item.Date >= new DateOnly(2026, 9, 1)
                           && item.Date <= new DateOnly(2026, 9, 12))
            .GroupBy(item => new { item.GroupId, item.ModuleId })
            .Select(group => new { group.Key.GroupId, group.Key.ModuleId, Count = group.Count() })
            .OrderBy(item => item.GroupId)
            .ThenBy(item => item.ModuleId)
            .ToListAsync();

        var expected = scenario.GroupIds.Count * scenario.ModuleHours.Values.Sum();
        var diagnostics =
            $"result={action.Result?.GetType().Name ?? "<null>"}; created={result.Created}; skipped={result.Skipped}; " +
            $"fillResult={fillAction.Result?.GetType().Name ?? "<null>"}; fillCreated={fillResult.Created}; " +
            $"gaps={result.GapDetails?.Count ?? 0}; fillGaps={fillResult.GapDetails?.Count ?? 0}; " +
            $"dates={string.Join(", ", createdByDate.Select(item => $"{item.Date:MM-dd}:{item.Count}"))}; " +
            $"modules={string.Join(", ", createdByModule.Select(item =>
                $"{scenario.ModuleIdsByCode.Single(entry => entry.Value == item.ModuleId).Key}:{item.Count}"))}; " +
            $"groupModules={string.Join(", ", createdByGroupModule.Select(item =>
                $"{item.GroupId}/{scenario.ModuleIdsByCode.Single(entry => entry.Value == item.ModuleId).Key}:{item.Count}"))}; " +
            $"gapReasons={string.Join(" | ", (result.GapDetails ?? new List<AutoGenGapDetail>())
                .GroupBy(item => item.Reason ?? "<none>")
                .OrderByDescending(group => group.Count())
                .Select(group => $"{group.Count()}x {group.Key}"))}; " +
            $"fillGapReasons={string.Join(" | ", (fillResult.GapDetails ?? new List<AutoGenGapDetail>())
                .GroupBy(item => item.Reason ?? "<none>")
                .OrderByDescending(group => group.Count())
                .Select(group => $"{group.Count()}x {group.Key}"))}; " +
            $"warnings={string.Join(" | ", result.Warnings
                .Where(item => item.Contains("Фінальна перевірка", StringComparison.Ordinal))
                .Take(50))}; " +
            $"fillWarnings={string.Join(" | ", fillResult.Warnings
                .Where(item => item.Contains("repair-pass", StringComparison.Ordinal)
                               || item.Contains("Фінальна перевірка", StringComparison.Ordinal))
                .Take(100))}";
        Assert.True(action.Result is OkObjectResult, diagnostics);
        Assert.True(fillAction.Result is OkObjectResult, diagnostics);
        Assert.True(result.Created + result.Skipped <= expected, diagnostics);
        Assert.True(result.Created >= 615, diagnostics);
        if (result.Created + result.Skipped < expected)
        {
            Assert.True(fillResult.Created > 0, diagnostics);
        }
        Assert.True(result.Created + fillResult.Created >= 645, diagnostics);
        Assert.Equal(result.Created + fillResult.Created, createdByModule.Sum(item => item.Count));
        Assert.True((result.GapDetails?.Count ?? 0) <= 36, diagnostics);
        Assert.True((fillResult.GapDetails?.Count ?? 0) <= 6, diagnostics);
        Assert.True(
            (fillResult.GapDetails?.Count ?? 0) <= (result.GapDetails?.Count ?? 0),
            diagnostics);
        Assert.DoesNotContain(
            result.Preflight ?? new List<AutoGenPreflightItem>(),
            item => item.Count > 0);
        Assert.DoesNotContain(
            fillResult.Preflight ?? new List<AutoGenPreflightItem>(),
            item => item.Count > 0);

        var selectedDates = Enumerable.Range(0, 12)
            .Select(offset => new DateOnly(2026, 9, 1).AddDays(offset))
            .Where(date => date.DayOfWeek is not DayOfWeek.Sunday)
            .ToList();
        foreach (var date in selectedDates)
        {
            Assert.Contains(createdByDate, item => item.Date == date && item.Count > 0);
        }
        Assert.True(
            createdByDate.Single(item => item.Date == new DateOnly(2026, 9, 1)).Count >= 60,
            "Перший вівторок діапазону знову містить забагато порожніх слотів.");
        Assert.True(
            createdByDate.Single(item => item.Date == new DateOnly(2026, 9, 11)).Count > 0,
            "П'ятниця другого тижня залишилася порожньою.");
        Assert.True(
            createdByDate.Single(item => item.Date == new DateOnly(2026, 9, 12)).Count > 0,
            "Субота другого тижня залишилася порожньою.");

        foreach (var item in createdByModule)
        {
            Assert.True(
                item.Count <= scenario.ModuleHours[item.ModuleId] * scenario.GroupIds.Count,
                $"Модуль #{item.ModuleId} перевищив спільний план діапазону.");
        }
        foreach (var moduleCode in new[] { "9", "10" })
        {
            var moduleId = scenario.ModuleIdsByCode[moduleCode];
            var secondWeekCount = await db.TeacherDraftItems
                .AsNoTracking()
                .CountAsync(item => item.ModuleId == moduleId
                                    && scenario.GroupIds.Contains(item.GroupId)
                                    && item.Date >= new DateOnly(2026, 9, 7)
                                    && item.Date <= new DateOnly(2026, 9, 12));
            Assert.True(secondWeekCount > 0, $"Модуль {moduleCode} не потрапив до другого тижня діапазону.");
        }

        var mergedLectureTopicId = await db.ModuleTopics
            .AsNoTracking()
            .Where(topic => topic.ModuleId == scenario.ModuleIdsByCode["5"]
                            && topic.TopicCode == "5.1.2.1")
            .Select(topic => topic.Id)
            .SingleAsync();
        var mergedLectureOccurrences = await db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => item.ModuleTopicId == mergedLectureTopicId
                           && scenario.GroupIds.Contains(item.GroupId)
                           && item.Date >= new DateOnly(2026, 9, 1)
                           && item.Date <= new DateOnly(2026, 9, 12))
            .GroupBy(item => new
            {
                item.Date,
                item.StartTime,
                item.EndTime,
                item.TeacherId,
                item.RoomId
            })
            .Select(group => new
            {
                GroupCount = group.Select(item => item.GroupId).Distinct().Count()
            })
            .ToListAsync();
        Assert.Single(mergedLectureOccurrences);
        Assert.Equal(scenario.GroupIds.Count, mergedLectureOccurrences[0].GroupCount);

        var hardRuleValidation = await new TeacherDraftsAutogenHardRuleValidator(db).ValidateAsync(
            new TeacherDraftsAutogenHardRuleValidationRequest(
                scenario.CourseId,
                scenario.GroupIds,
                new DateOnly(2026, 9, 1),
                new DateOnly(2026, 9, 12),
                WeekPreset.MonSat,
                AllowIncompleteDrafts: false,
                MaxParallelGroupsPerModuleInSlot: AutoGenRecommendedProfile.MaxParallelGroupsPerModuleInSlot));
        Assert.Empty(hardRuleValidation.Violations);

        var fingerprintBeforeIdempotenceCheck = await LoadScheduleFingerprintAsync(
            db,
            scenario.GroupIds);
        var repeatedFillAction = await new TeacherDraftsAutogenService(db).DraftAutoGen(
            request with
            {
                ClearExisting = false,
                SoftFill = true
            });
        var repeatedFillResult = ExtractResult(repeatedFillAction);
        var fingerprintAfterIdempotenceCheck = await LoadScheduleFingerprintAsync(
            db,
            scenario.GroupIds);
        Assert.IsType<OkObjectResult>(repeatedFillAction.Result);
        Assert.Equal(0, repeatedFillResult.Created);
        Assert.Equal(fingerprintBeforeIdempotenceCheck, fingerprintAfterIdempotenceCheck);
    }

    private static async Task<L3Scenario> LoadScenarioAsync(AppDbContext source)
    {
        var course = await source.Courses
            .AsNoTracking()
            .Where(item => item.Name == "L-3" || item.Name == "L3")
            .OrderBy(item => item.Id)
            .FirstAsync();
        var groups = await source.Groups
            .AsNoTracking()
            .Where(item => item.CourseId == course.Id
                           && (GroupNames.Contains(item.Name) || GroupNames.Contains(item.Id.ToString())))
            .OrderBy(item => item.Name)
            .ToListAsync();
        Assert.Equal(GroupNames.Length, groups.Count);

        var modules = await source.Modules
            .AsNoTracking()
            .Where(item => item.CourseId == course.Id
                           || item.ModuleCourses.Any(link => link.CourseId == course.Id))
            .ToListAsync();
        var moduleHours = new Dictionary<int, int>();
        var moduleIdsByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in ModuleHoursByCode)
        {
            var module = Assert.Single(
                modules,
                item => string.Equals(item.Code?.Trim(), entry.Key, StringComparison.OrdinalIgnoreCase));
            moduleHours[module.Id] = entry.Value;
            moduleIdsByCode[entry.Key] = module.Id;
        }

        return new L3Scenario(
            course.Id,
            groups.Select(item => item.Id).ToList(),
            moduleHours,
            moduleIdsByCode);
    }

    private static AutoGenResult ExtractResult(ActionResult<AutoGenResult> action)
        => action.Result switch
        {
            ObjectResult { Value: AutoGenResult result } => result,
            _ => throw new InvalidOperationException("Автогенерація не повернула очікуваний результат.")
        };

    private static DraftAutoGenSoftOptions MapSoftOptions(AutoGenSoftOptionsDto options)
        => new(
            MaxParallelGroupsPerModuleInSlot: options.MaxParallelGroupsPerModuleInSlot,
            RecentRepeatWindowDays: options.RecentRepeatWindowDays,
            PreferredMaxDistinctModulesPerDay: options.PreferredMaxDistinctModulesPerDay,
            MaxDistinctModulesPerDay: options.MaxDistinctModulesPerDay,
            PreferredFirstPenaltyMultiplier: options.PreferredFirstPenaltyMultiplier,
            AdjacentRoomChangePenalty: options.AdjacentRoomChangePenalty,
            TeacherLoadPenaltyWeight: options.TeacherLoadPenaltyWeight,
            BuildingDistancePenaltyWeight: options.BuildingDistancePenaltyWeight);

    private static async Task<string> LoadScheduleFingerprintAsync(
        AppDbContext db,
        IReadOnlyCollection<int> groupIds)
    {
        var rows = await db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => groupIds.Contains(item.GroupId)
                           && item.Date >= new DateOnly(2026, 9, 1)
                           && item.Date <= new DateOnly(2026, 9, 12))
            .OrderBy(item => item.Date)
            .ThenBy(item => item.StartTime)
            .ThenBy(item => item.EndTime)
            .ThenBy(item => item.GroupId)
            .ThenBy(item => item.ModuleId)
            .ThenBy(item => item.ModuleTopicId)
            .Select(item => new
            {
                item.Date,
                item.StartTime,
                item.EndTime,
                item.GroupId,
                item.ModuleId,
                item.ModuleTopicId,
                item.LessonTypeId,
                item.TeacherId,
                item.RoomId,
                item.IsSelfStudy
            })
            .ToListAsync();
        return string.Join(
            "\n",
            rows.Select(item =>
                $"{item.Date:yyyy-MM-dd}|{item.StartTime:HH\\:mm}|{item.EndTime:HH\\:mm}|"
                + $"{item.GroupId}|{item.ModuleId}|{item.ModuleTopicId}|{item.LessonTypeId}|"
                + $"{item.TeacherId}|{item.RoomId}|{item.IsSelfStudy}"));
    }

    private sealed record L3Scenario(
        int CourseId,
        List<int> GroupIds,
        Dictionary<int, int> ModuleHours,
        Dictionary<string, int> ModuleIdsByCode);
}
