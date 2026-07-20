using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

public sealed class AutogenL3Week18DiagnosticsTests
{
    private static readonly bool VerboseDiagnosticOutput = false;
    private static readonly IReadOnlyDictionary<string, int> ModuleHoursByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["1"] = 7,
        ["2"] = 7,
        ["3"] = 4,
        ["4"] = 10,
        ["5"] = 2,
        ["6"] = 5,
        ["8"] = 3,
        ["10"] = 2,
        ["13"] = 2
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

    [Fact]
    public async Task L3_week18_source_data_breakdown_diagnostic()
    {
        Console.OutputEncoding = Encoding.UTF8;

        await using var source = ServerConfigurationFactory.CreateSourceContext();
        var scenario = await BuildScenarioAsync(source);
        await WriteSourceCapacityBreakdownAsync(source, scenario);
    }

    [Fact]
    public async Task L3_week18_screen_scenario_recommended_autogen_diagnostic()
    {
        Console.OutputEncoding = Encoding.UTF8;

        await using var source = ServerConfigurationFactory.CreateSourceContext();
        var scenario = await BuildScenarioAsync(source);
        var preferredLimits = (await source.PreferredFirstSlotLimitConfigs
            .AsNoTracking()
            .OrderBy(x => x.CourseId ?? 0)
            .ThenBy(x => x.Id)
            .ToListAsync())
            .Select(x => $"course={x.CourseId?.ToString() ?? "global"} max={x.MaxSlotOrder}")
            .ToList();
        Console.WriteLine("preferred-first-limits: " + (preferredLimits.Count == 0 ? "<none>" : string.Join(", ", preferredLimits)));
        await WriteModuleTopicSummaryAsync(source, scenario);
        var sourceReport = await AnalyzeAsync(source, scenario);
        WriteReport("source-current", new AutoGenResult(0, 0, new List<string>()), sourceReport);
        await WritePublishedSourceSummaryAsync(source, scenario);

        await using var snapshot = await SqliteSnapshotFile.CreateFromSourceAsync(source);

        await using var database = new SqliteTempDatabase(snapshot.Path);
        await using var db = database.CreateContext();
        db.CalendarExceptions.Add(new CalendarException
        {
            Date = scenario.RangeEnd,
            IsWorkingDay = true,
            Name = "Робоча субота для діагностичного сценарію L3",
            CourseId = scenario.CourseId
        });
        await db.SaveChangesAsync();
        var service = new TeacherDraftsAutogenService(db);

        var initialResult = ExtractResult(await service.DraftAutoGen(BuildInitialRequest(scenario)));
        db.ChangeTracker.Clear();
        var initialReport = await AnalyzeAsync(db, scenario);
        WriteReport("initial", initialResult, initialReport);

        var fillResult = ExtractResult(await service.DraftAutoGen(BuildFillRequest(scenario)));
        db.ChangeTracker.Clear();
        var fillReport = await AnalyzeAsync(db, scenario);
        WriteReport("fill", fillResult, fillReport);
        var hardRuleValidation = await new TeacherDraftsAutogenHardRuleValidator(db).ValidateAsync(
            new TeacherDraftsAutogenHardRuleValidationRequest(
                scenario.CourseId,
                scenario.GroupIds,
                scenario.RangeStart,
                scenario.RangeEnd,
                WeekPreset.MonSat,
                AllowIncompleteDrafts: true));

        Assert.Empty(fillResult.GapDetails ?? new List<AutoGenGapDetail>());
        Assert.Empty(fillReport.UnderfilledGroups);
        Assert.Empty(fillReport.ModuleSequenceViolations);
        Assert.Empty(fillReport.TopicSequenceViolations);
        Assert.False(
            hardRuleValidation.HasViolations,
            $"Після дозаповнення не повинно бути порушень жорстких правил: {string.Join(" | ", hardRuleValidation.Violations)}");
        Assert.Empty(fillReport.IncompleteItems);
        var fallbackWarnings = fillResult.Warnings
            .Where(warning => warning.Contains("поза кафедрою теми", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Single(fallbackWarnings);
        var outDepartmentAssignments = await db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => scenario.GroupIds.Contains(item.GroupId)
                           && item.Date >= scenario.RangeStart
                           && item.Date <= scenario.RangeEnd
                           && item.Status == DraftStatus.Draft
                           && item.TeacherId != null
                           && item.ModuleTopicId != null
                           && item.ModuleTopic!.DepartmentId != null
                           && item.ModuleTopic.DepartmentId > 0
                           && (item.Teacher!.DepartmentId == null
                               || item.Teacher.DepartmentId != item.ModuleTopic.DepartmentId))
            .Select(item => new
            {
                item.GroupId,
                item.ModuleId,
                TeacherId = item.TeacherId!.Value,
                item.Date,
                item.StartTime
            })
            .ToListAsync();
        var outDepartmentAssignment = Assert.Single(outDepartmentAssignments);
        Assert.True(await db.TeacherModules.AsNoTracking().AnyAsync(link =>
            link.ModuleId == outDepartmentAssignment.ModuleId
            && link.TeacherId == outDepartmentAssignment.TeacherId));
        Assert.True(
            fillReport.ShareableSingletons.Count <= 3,
            $"Після дозаповнення очікувалось не більше 3 одиночних лекційних хвостів, знайдено {fillReport.ShareableSingletons.Count}: {string.Join(" | ", fillReport.ShareableSingletons.Select(FormatCluster))}");
        Assert.All(fillReport.GroupSummaries, summary =>
            Assert.True(
                summary.EmptySlots == summary.ExpectedEmptySlots,
                $"Для групи {summary.GroupName} очікувалось {summary.ExpectedEmptySlots} порожніх слотів, але знайдено {summary.EmptySlots}."));

        var firstFingerprint = await ReadDraftFingerprintAsync(db, scenario);
        await using var replayDatabase = new SqliteTempDatabase(snapshot.Path);
        await using var replayDb = replayDatabase.CreateContext();
        replayDb.CalendarExceptions.Add(new CalendarException
        {
            Date = scenario.RangeEnd,
            IsWorkingDay = true,
            Name = "Робоча субота для перевірки детермінізму L3",
            CourseId = scenario.CourseId
        });
        await replayDb.SaveChangesAsync();
        var replayService = new TeacherDraftsAutogenService(replayDb);
        _ = ExtractResult(await replayService.DraftAutoGen(BuildInitialRequest(scenario)));
        var replayFillResult = ExtractResult(await replayService.DraftAutoGen(BuildFillRequest(scenario)));
        replayDb.ChangeTracker.Clear();

        Assert.Equal(firstFingerprint, await ReadDraftFingerprintAsync(replayDb, scenario));
        Assert.Single(replayFillResult.Warnings, warning =>
            warning.Contains("поза кафедрою теми", StringComparison.OrdinalIgnoreCase));
    }

    private static DraftAutoGenRequest BuildInitialRequest(Week18Scenario scenario)
        => new(
            WeekStart: new DateOnly(2026, 5, 11),
            ClearExisting: true,
            CourseId: scenario.CourseId,
            GroupIds: scenario.GroupIds.ToList(),
            AllowOnDaysOff: false,
            Days: WeekPreset.MonSat,
            ModuleHours: scenario.ModuleHours.ToDictionary(kv => kv.Key, kv => kv.Value),
            SoftFill: false,
            AllowIncompleteDrafts: true,
            RangeStartDate: scenario.RangeStart,
            RangeEndDate: scenario.RangeEnd,
            PreferredFirstMaxSlotOrderOverride: null);

    private static DraftAutoGenRequest BuildFillRequest(Week18Scenario scenario)
        => new(
            WeekStart: new DateOnly(2026, 5, 11),
            ClearExisting: false,
            CourseId: scenario.CourseId,
            GroupIds: scenario.GroupIds.ToList(),
            AllowOnDaysOff: false,
            Days: WeekPreset.MonSat,
            ModuleHours: scenario.ModuleHours.ToDictionary(kv => kv.Key, kv => kv.Value),
            SoftFill: true,
            AllowIncompleteDrafts: true,
            RangeStartDate: scenario.RangeStart,
            RangeEndDate: scenario.RangeEnd,
            PreferredFirstMaxSlotOrderOverride: null,
            SoftOptions: new DraftAutoGenSoftOptions(
                MaxParallelGroupsPerModuleInSlot: 4,
                RecentRepeatWindowDays: 0,
                PreferredMaxDistinctModulesPerDay: 5,
                MaxDistinctModulesPerDay: 6,
                PreferredFirstPenaltyMultiplier: 0.35));

    private static async Task<Week18Scenario> BuildScenarioAsync(AppDbContext source)
    {
        var course = await source.Courses.AsNoTracking().FirstOrDefaultAsync(x => x.Name == "L-3")
            ?? await source.Courses.AsNoTracking().FirstOrDefaultAsync(x => x.Name == "L3")
            ?? throw new InvalidOperationException("У базі не знайдено курс L-3.");

        var groups = await source.Groups.AsNoTracking()
            .Where(x => x.CourseId == course.Id && (GroupNames.Contains(x.Name) || GroupNames.Contains(x.Id.ToString())))
            .OrderBy(x => x.Name)
            .Select(x => new GroupSnapshot(x.Id, x.Name))
            .ToListAsync();

        var missingGroups = GroupNames
            .Except(groups.Select(x => x.Name), StringComparer.OrdinalIgnoreCase)
            .Except(groups.Select(x => x.Id.ToString()), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missingGroups.Count > 0)
        {
            throw new InvalidOperationException($"Для курсу {course.Name} не знайдено групи: {string.Join(", ", missingGroups)}.");
        }

        var modules = await source.Modules.AsNoTracking()
            .Where(x => x.CourseId == course.Id || x.ModuleCourses.Any(mc => mc.CourseId == course.Id))
            .Select(x => new { x.Id, x.Code })
            .ToListAsync();

        var moduleHours = new Dictionary<int, int>();
        var moduleCodesById = new Dictionary<int, string>();
        var missingModuleCodes = new List<string>();

        foreach (var entry in ModuleHoursByCode)
        {
            var module = modules
                .Where(x => string.Equals(x.Code?.Trim(), entry.Key, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Id)
                .FirstOrDefault();
            if (module is null)
            {
                missingModuleCodes.Add(entry.Key);
                continue;
            }

            moduleHours[module.Id] = entry.Value;
            moduleCodesById[module.Id] = entry.Key;
        }

        if (missingModuleCodes.Count > 0)
        {
            throw new InvalidOperationException($"Для курсу {course.Name} не знайдено модулі з кодами: {string.Join(", ", missingModuleCodes)}.");
        }

        return new Week18Scenario(
            CourseId: course.Id,
            CourseName: course.Name,
            GroupIds: groups.Select(x => x.Id).ToList(),
            GroupNamesById: groups.ToDictionary(x => x.Id, x => x.Name),
            ModuleHours: moduleHours,
            ModuleCodesById: moduleCodesById,
            RangeStart: new DateOnly(2026, 5, 12),
            RangeEnd: new DateOnly(2026, 5, 16));
    }

    private static async Task<Week18Report> AnalyzeAsync(AppDbContext db, Week18Scenario scenario)
    {
        var items = await db.TeacherDraftItems
            .AsNoTracking()
            .Include(x => x.Group)
            .Include(x => x.Module)
            .Include(x => x.ModuleTopic)
            .Include(x => x.LessonType)
            .Include(x => x.Teacher)
            .Include(x => x.Room)
            .Where(x => scenario.GroupIds.Contains(x.GroupId)
                        && x.Date >= scenario.RangeStart
                        && x.Date <= scenario.RangeEnd)
            .ToListAsync();

        var timeSlots = await db.TimeSlots.AsNoTracking()
            .Where(x => x.IsActive && (x.CourseId == null || x.CourseId == scenario.CourseId))
            .ToListAsync();
        var dates = EnumerateDates(scenario.RangeStart, scenario.RangeEnd).ToList();
        var slotsByDate = dates.ToDictionary(
            date => date,
            date => TimeSlotsResolver.ResolveForDay(timeSlots, scenario.CourseId, date.ToDateTime(TimeOnly.MinValue).DayOfWeek).Slots);

        var targetHours = scenario.ModuleHours.Values.Sum();
        var totalSlots = slotsByDate.Values.Sum(x => x.Count);

        var groupSummaries = scenario.GroupIds
            .Select(groupId =>
            {
                var scheduled = items.Count(x => x.GroupId == groupId);
                var emptySlots = slotsByDate.Sum(pair =>
                    pair.Value.Count(slot => !items.Any(item =>
                        item.GroupId == groupId
                        && item.Date == pair.Key
                        && item.StartTime == slot.Start
                        && item.EndTime == slot.End)));
                return new GroupSummary(
                    GroupId: groupId,
                    GroupName: scenario.GroupNamesById[groupId],
                    Scheduled: scheduled,
                    Target: targetHours,
                    EmptySlots: emptySlots,
                    ExpectedEmptySlots: Math.Max(0, totalSlots - targetHours));
            })
            .OrderBy(x => x.GroupName, StringComparer.Ordinal)
            .ToList();

        var underfilledGroups = groupSummaries
            .Where(x => x.Scheduled < x.Target)
            .ToList();
        var moduleGroupSummaries = scenario.GroupIds
            .SelectMany(groupId => scenario.ModuleHours.Keys.Select(moduleId =>
            {
                var scheduled = items.Count(item => item.GroupId == groupId && item.ModuleId == moduleId);
                var target = scenario.ModuleHours[moduleId];
                return new ModuleGroupSummary(
                    groupId,
                    scenario.GroupNamesById[groupId],
                    moduleId,
                    scenario.ModuleCodesById[moduleId],
                    scheduled,
                    target,
                    Math.Max(0, target - scheduled));
            }))
            .OrderBy(summary => summary.GroupName, StringComparer.Ordinal)
            .ThenBy(summary => summary.ModuleCode, StringComparer.Ordinal)
            .ToList();

        var incompleteItems = items
            .Where(x => x.LessonType.RequiresTeacher && x.TeacherId is null
                        || x.LessonType.RequiresRoom && x.RoomId is null)
            .OrderBy(x => x.Date)
            .ThenBy(x => x.StartTime)
            .ThenBy(x => x.Group.Name)
            .Select(FormatItem)
            .ToList();

        var sequenceRanks = await db.ModuleSequenceItems
            .AsNoTracking()
            .Where(x => x.CourseId == scenario.CourseId)
            .Select(x => new { x.ModuleId, x.GroupOrder, x.Order })
            .ToDictionaryAsync(x => x.ModuleId, x => (x.GroupOrder, x.Order));
        var fillerModuleIds = await db.ModuleFillers
            .AsNoTracking()
            .Where(x => x.CourseId == scenario.CourseId)
            .Select(x => x.ModuleId)
            .ToListAsync();
        var fillerModuleSet = fillerModuleIds.ToHashSet();
        var moduleSequenceViolations = new List<string>();
        foreach (var groupItems in items
                     .Where(x => sequenceRanks.ContainsKey(x.ModuleId) && !fillerModuleSet.Contains(x.ModuleId))
                     .GroupBy(x => x.GroupId))
        {
            var maxGroupOrder = 0;
            TeacherDraftItem? maxGroupOrderItem = null;
            foreach (var item in groupItems
                         .OrderBy(x => x.Date)
                         .ThenBy(x => x.StartTime)
                         .ThenBy(x => x.EndTime))
            {
                var rank = sequenceRanks[item.ModuleId];
                if (rank.GroupOrder < maxGroupOrder && maxGroupOrderItem is not null)
                {
                    moduleSequenceViolations.Add(
                        $"{item.Group.Name}: {FormatItem(item)} йде після {FormatItem(maxGroupOrderItem)}, хоча має раніший порядок модуля ({rank.GroupOrder} < {maxGroupOrder}).");
                }
                if (rank.GroupOrder > maxGroupOrder)
                {
                    maxGroupOrder = rank.GroupOrder;
                    maxGroupOrderItem = item;
                }
            }
        }

        var topicSequenceViolations = new List<string>();
        foreach (var groupModuleItems in items
                     .Where(x => x.ModuleTopicId is not null)
                     .GroupBy(x => new { x.GroupId, x.ModuleId }))
        {
            string? maxTopicCode = null;
            TeacherDraftItem? maxTopicItem = null;
            foreach (var item in groupModuleItems
                         .OrderBy(x => x.Date)
                         .ThenBy(x => x.StartTime)
                         .ThenBy(x => x.EndTime))
            {
                var topicCode = string.IsNullOrWhiteSpace(item.ModuleTopic?.TopicCode)
                    ? null
                    : item.ModuleTopic.TopicCode.Trim();
                if (topicCode is null)
                {
                    continue;
                }
                if (maxTopicCode is not null
                    && CompareTopicCodes(topicCode, maxTopicCode) < 0
                    && maxTopicItem is not null)
                {
                    topicSequenceViolations.Add(
                        $"{item.Group.Name}: {FormatItem(item)} йде після {FormatItem(maxTopicItem)}, хоча має раніший код теми ({topicCode} < {maxTopicCode}).");
                }
                if (maxTopicCode is null || CompareTopicCodes(topicCode, maxTopicCode) > 0)
                {
                    maxTopicCode = topicCode;
                    maxTopicItem = item;
                }
            }
        }

        var shareableClusters = items
            .Where(IsShareable)
            .GroupBy(x => new
            {
                x.Date,
                x.StartTime,
                x.EndTime,
                x.ModuleId,
                x.ModuleTopicId,
                x.LessonTypeId,
                x.TeacherId,
                x.RoomId
            })
            .Select(group => new ShareableCluster(
                Date: group.Key.Date,
                Start: group.Key.StartTime,
                End: group.Key.EndTime,
                ModuleCode: scenario.ModuleCodesById.TryGetValue(group.Key.ModuleId, out var code) ? code : group.First().Module.Code,
                TopicCode: group.First().ModuleTopic?.TopicCode,
                LessonType: group.First().LessonType.Name,
                Teacher: group.First().Teacher?.FullName,
                Room: group.First().Room?.Name,
                GroupNames: group.Select(x => x.Group.Name).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList()))
            .OrderBy(x => x.Date)
            .ThenBy(x => x.Start)
            .ThenBy(x => x.ModuleCode, StringComparer.Ordinal)
            .ToList();

        return new Week18Report(
            GroupSummaries: groupSummaries,
            UnderfilledGroups: underfilledGroups,
            ModuleGroupSummaries: moduleGroupSummaries,
            IncompleteItems: incompleteItems,
            ModuleSequenceViolations: moduleSequenceViolations,
            TopicSequenceViolations: topicSequenceViolations,
            ShareableClusters: shareableClusters,
            ShareableSingletons: shareableClusters.Where(x => x.GroupNames.Count == 1).ToList(),
            AllItems: items
                .OrderBy(x => x.Date)
                .ThenBy(x => x.StartTime)
                .ThenBy(x => x.Group.Name)
                .Select(FormatItem)
                .ToList(),
            LastDayItems: items
                .Where(x => x.Date == scenario.RangeEnd)
                .OrderBy(x => x.StartTime)
                .ThenBy(x => x.Group.Name)
                .Select(FormatItem)
                .ToList());
    }

    private static bool IsShareable(TeacherDraftItem item)
    {
        var code = item.LessonType.Code?.Trim().ToUpperInvariant() ?? string.Empty;
        var name = item.LessonType.Name?.Trim().ToUpperInvariant() ?? string.Empty;
        return item.LessonType.PreferredFirstInWeek
               || code is "LECTURE" or "LECT" or "LEC"
               || name.Contains("LECTURE", StringComparison.Ordinal)
               || name.Contains("ЛЕКЦ", StringComparison.Ordinal);
    }

    private static string FormatItem(TeacherDraftItem item)
        => $"{item.Date:yyyy-MM-dd} {item.StartTime:HH\\:mm}-{item.EndTime:HH\\:mm} {item.Group.Name}: M{item.Module.Code} {item.ModuleTopic?.TopicCode ?? "-"} {item.LessonType.Name} {item.Teacher?.FullName ?? "<без викладача>"} ауд. {item.Room?.Name ?? "<без аудиторії>"}";

    private static int CompareTopicCodes(string? left, string? right)
    {
        var leftParts = SplitTopicCode(left);
        var rightParts = SplitTopicCode(right);
        var max = Math.Max(leftParts.Count, rightParts.Count);
        for (var i = 0; i < max; i++)
        {
            var leftValue = i < leftParts.Count ? leftParts[i] : 0;
            var rightValue = i < rightParts.Count ? rightParts[i] : 0;
            var cmp = leftValue.CompareTo(rightValue);
            if (cmp != 0)
            {
                return cmp;
            }
        }
        return string.Compare(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static List<int> SplitTopicCode(string? code)
        => (code ?? string.Empty)
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var number) ? number : 0)
            .ToList();

    private static async Task<IReadOnlyList<string>> ReadDraftFingerprintAsync(
        AppDbContext db,
        Week18Scenario scenario)
    {
        var drafts = await db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => scenario.GroupIds.Contains(item.GroupId)
                           && item.Date >= scenario.RangeStart
                           && item.Date <= scenario.RangeEnd)
            .OrderBy(item => item.Date)
            .ThenBy(item => item.StartTime)
            .ThenBy(item => item.EndTime)
            .ThenBy(item => item.GroupId)
            .ThenBy(item => item.ModuleId)
            .ThenBy(item => item.ModuleTopicId)
            .ThenBy(item => item.LessonTypeId)
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
                item.Status,
                item.IsLocked,
                item.IsSelfStudy,
                item.BatchKey,
                item.ValidationWarnings
            })
            .ToListAsync();

        return drafts.Select(item =>
                $"{item.Date:yyyy-MM-dd}|{item.StartTime:HH\\:mm}|{item.EndTime:HH\\:mm}|g{item.GroupId}|m{item.ModuleId}|t{item.ModuleTopicId}|l{item.LessonTypeId}|p{item.TeacherId}|r{item.RoomId}|s{(int)item.Status}|locked={item.IsLocked}|self={item.IsSelfStudy}|batch={item.BatchKey}|warnings={item.ValidationWarnings}")
            .ToList();
    }

    private static void WriteReport(string label, AutoGenResult result, Week18Report report)
    {
        Console.WriteLine($"=== {label} ===");
        Console.WriteLine($"created={result.Created}; skipped={result.Skipped}; gaps={result.GapDetails?.Count ?? 0}; warnings={result.Warnings.Count}");
        Console.WriteLine("groups:");
        foreach (var summary in report.GroupSummaries)
        {
            Console.WriteLine($"{summary.GroupName}: scheduled={summary.Scheduled}/{summary.Target}; empty={summary.EmptySlots}; expected-empty={summary.ExpectedEmptySlots}");
            var moduleBreakdown = report.ModuleGroupSummaries
                .Where(module => module.GroupId == summary.GroupId)
                .Select(module => $"M{module.ModuleCode}={module.Scheduled}/{module.Target}(-{module.Residual})");
            Console.WriteLine("  modules: " + string.Join(", ", moduleBreakdown));
        }

        Console.WriteLine($"underfilled={report.UnderfilledGroups.Count}; incomplete={report.IncompleteItems.Count}; shareable-clusters={report.ShareableClusters.Count}; shareable-singletons={report.ShareableSingletons.Count}");
        if (result.GapDetails is { Count: > 0 })
        {
            Console.WriteLine("gaps:");
            foreach (var gap in result.GapDetails)
            {
                var residual = report.ModuleGroupSummaries
                    .Where(module => module.GroupId == gap.GroupId && module.Residual > 0)
                    .Select(module => $"M{module.ModuleCode}:{module.Residual}");
                Console.WriteLine($"{gap.Date:yyyy-MM-dd} {gap.Start:HH\\:mm}-{gap.End:HH\\:mm} {gap.GroupName}: {gap.Reason}; residual={string.Join(",", residual)}");
            }
        }

        if (VerboseDiagnosticOutput && result.Warnings.Count > 0)
        {
            Console.WriteLine("warnings:");
            foreach (var warning in result.Warnings.Take(40))
            {
                Console.WriteLine(warning);
            }
        }

        foreach (var warning in result.Warnings.Where(warning =>
                     warning.Contains("matching", StringComparison.OrdinalIgnoreCase)
                     || warning.Contains("безпечної межі потоку", StringComparison.OrdinalIgnoreCase)
                     || warning.Contains("переніс спільний потік", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"matching-warning: {warning}");
        }

        if (report.IncompleteItems.Count > 0)
        {
            Console.WriteLine("incomplete items:");
            foreach (var incompleteItem in report.IncompleteItems)
            {
                Console.WriteLine(incompleteItem);
            }
        }

        if (report.ModuleSequenceViolations.Count > 0)
        {
            Console.WriteLine("module sequence violations:");
            foreach (var violation in report.ModuleSequenceViolations)
            {
                Console.WriteLine(violation);
            }
        }

        if (report.TopicSequenceViolations.Count > 0)
        {
            Console.WriteLine("topic sequence violations:");
            foreach (var violation in report.TopicSequenceViolations)
            {
                Console.WriteLine(violation);
            }
        }

        foreach (var singleton in report.ShareableSingletons.Take(20))
        {
            Console.WriteLine($"singleton: {FormatCluster(singleton)}");
        }

        if (VerboseDiagnosticOutput)
        {
            Console.WriteLine("shareable clusters:");
            foreach (var cluster in report.ShareableClusters)
            {
                Console.WriteLine(FormatCluster(cluster));
            }

            Console.WriteLine("all items:");
            foreach (var item in report.AllItems)
            {
                Console.WriteLine(item);
            }

            Console.WriteLine("last day:");
            foreach (var item in report.LastDayItems)
            {
                Console.WriteLine(item);
            }
        }
    }

    private static async Task WritePublishedSourceSummaryAsync(AppDbContext db, Week18Scenario scenario)
    {
        var counts = await db.ScheduleItems
            .AsNoTracking()
            .Where(x => scenario.GroupIds.Contains(x.GroupId)
                        && x.Date >= scenario.RangeStart
                        && x.Date <= scenario.RangeEnd)
            .GroupBy(x => x.GroupId)
            .Select(g => new { GroupId = g.Key, Count = g.Count() })
            .ToListAsync();
        Console.WriteLine("source-current-published:");
        foreach (var groupId in scenario.GroupIds.OrderBy(id => scenario.GroupNamesById[id], StringComparer.Ordinal))
        {
            var count = counts.FirstOrDefault(x => x.GroupId == groupId)?.Count ?? 0;
            Console.WriteLine($"{scenario.GroupNamesById[groupId]}: published={count}");
        }
    }

    private static async Task WriteSourceCapacityBreakdownAsync(AppDbContext db, Week18Scenario scenario)
    {
        var excludedTypeIds = (await db.LessonTypes
                .AsNoTracking()
                .Select(type => new { type.Id, type.Code })
                .ToListAsync())
            .Where(type => LessonTypeOccupancyPolicy.IsExcludedFromAutogenWorkload(type.Code))
            .Select(type => type.Id)
            .ToHashSet();
        var groupIds = scenario.GroupIds.ToHashSet();
        var moduleIds = scenario.ModuleHours.Keys.ToHashSet();
        var planningWeekEndExclusive = new DateOnly(2026, 5, 18);

        var drafts = await db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => groupIds.Contains(item.GroupId)
                           && moduleIds.Contains(item.ModuleId)
                           && !excludedTypeIds.Contains(item.LessonTypeId)
                           && item.Date < planningWeekEndExclusive)
            .Select(item => new
            {
                item.GroupId,
                item.ModuleId,
                item.ModuleTopicId,
                item.Date,
                item.Status,
                item.IsLocked
            })
            .ToListAsync();
        var published = await db.ScheduleItems
            .AsNoTracking()
            .Where(item => groupIds.Contains(item.GroupId)
                           && moduleIds.Contains(item.ModuleId)
                           && !excludedTypeIds.Contains(item.LessonTypeId)
                           && item.Date < planningWeekEndExclusive)
            .Select(item => new
            {
                item.GroupId,
                item.ModuleId,
                item.ModuleTopicId,
                item.Date
            })
            .ToListAsync();

        var clearedDrafts = drafts
            .Where(item => item.Date >= scenario.RangeStart
                           && item.Date <= scenario.RangeEnd
                           && item.Status == DraftStatus.Draft
                           && !item.IsLocked)
            .ToList();
        var retainedDrafts = drafts.Except(clearedDrafts).ToList();
        var rangePublished = published
            .Where(item => item.Date >= scenario.RangeStart && item.Date <= scenario.RangeEnd)
            .ToList();
        var rangeRetainedDrafts = retainedDrafts
            .Where(item => item.Date >= scenario.RangeStart && item.Date <= scenario.RangeEnd)
            .ToList();

        Console.WriteLine($"source-clear: removable-drafts={clearedDrafts.Count}; retained-range-drafts={rangeRetainedDrafts.Count}; published-range={rangePublished.Count}");
        Console.WriteLine("source-range-coverage:");
        foreach (var groupId in scenario.GroupIds.OrderBy(id => scenario.GroupNamesById[id], StringComparer.Ordinal))
        {
            var target = scenario.ModuleHours.Values.Sum();
            var retained = rangeRetainedDrafts.Count(item => item.GroupId == groupId);
            var official = rangePublished.Count(item => item.GroupId == groupId);
            Console.WriteLine($"{scenario.GroupNamesById[groupId]}: target={target}; retained-draft={retained}; official={official}; combined-existing={retained + official}; generation-need={Math.Max(0, target - retained - official)}");
            foreach (var moduleId in scenario.ModuleHours.Keys.OrderBy(id => scenario.ModuleCodesById[id], StringComparer.Ordinal))
            {
                var requested = scenario.ModuleHours[moduleId];
                var retainedModule = rangeRetainedDrafts.Count(item => item.GroupId == groupId && item.ModuleId == moduleId);
                var officialModule = rangePublished.Count(item => item.GroupId == groupId && item.ModuleId == moduleId);
                Console.WriteLine($"  M{scenario.ModuleCodesById[moduleId]}: requested={requested}; retained-draft={retainedModule}; official={officialModule}; generation-need={Math.Max(0, requested - retainedModule - officialModule)}");
            }
        }

        var topics = await db.ModuleTopics
            .AsNoTracking()
            .Include(topic => topic.LessonType)
            .Where(topic => moduleIds.Contains(topic.ModuleId) && !topic.IsInterAssembly)
            .OrderBy(topic => topic.ModuleId)
            .ThenBy(topic => topic.Order)
            .ThenBy(topic => topic.TopicCode)
            .Select(topic => new
            {
                topic.Id,
                topic.ModuleId,
                topic.TopicCode,
                topic.AuditoriumHours,
                LessonTypeCode = topic.LessonType.Code
            })
            .ToListAsync();

        Console.WriteLine("source-topic-capacity:");
        foreach (var moduleId in scenario.ModuleHours.Keys.OrderBy(id => scenario.ModuleCodesById[id], StringComparer.Ordinal))
        {
            var moduleTopics = topics.Where(topic => topic.ModuleId == moduleId).ToList();
            var allHours = moduleTopics.Sum(topic => Math.Max(0, topic.AuditoriumHours));
            var nonMarkerHours = moduleTopics
                .Where(topic => !LessonTypeOccupancyPolicy.IsExcludedFromAutogenWorkload(topic.LessonTypeCode))
                .Sum(topic => Math.Max(0, topic.AuditoriumHours));
            Console.WriteLine($"M{scenario.ModuleCodesById[moduleId]}: requested={scenario.ModuleHours[moduleId]}; topic-hours={allHours}; non-marker-topic-hours={nonMarkerHours}");

            foreach (var groupId in scenario.GroupIds.OrderBy(id => scenario.GroupNamesById[id], StringComparer.Ordinal))
            {
                var usedByTopic = retainedDrafts
                    .Where(item => item.GroupId == groupId && item.ModuleId == moduleId && item.ModuleTopicId is not null)
                    .Select(item => item.ModuleTopicId!.Value)
                    .Concat(published
                        .Where(item => item.GroupId == groupId && item.ModuleId == moduleId && item.ModuleTopicId is not null)
                        .Select(item => item.ModuleTopicId!.Value))
                    .GroupBy(topicId => topicId)
                    .ToDictionary(group => group.Key, group => group.Count());
                var residualAll = moduleTopics.Sum(topic =>
                    Math.Max(0, Math.Max(0, topic.AuditoriumHours) - usedByTopic.GetValueOrDefault(topic.Id)));
                var residualNonMarker = moduleTopics
                    .Where(topic => !LessonTypeOccupancyPolicy.IsExcludedFromAutogenWorkload(topic.LessonTypeCode))
                    .Sum(topic => Math.Max(0, Math.Max(0, topic.AuditoriumHours) - usedByTopic.GetValueOrDefault(topic.Id)));
                Console.WriteLine($"  {scenario.GroupNamesById[groupId]}: residual-topic-hours={residualAll}; residual-non-marker-hours={residualNonMarker}");
            }
        }

        var timeSlots = await db.TimeSlots
            .AsNoTracking()
            .Where(slot => slot.IsActive && (slot.CourseId == null || slot.CourseId == scenario.CourseId))
            .ToListAsync();
        var calendar = await db.CalendarExceptions
            .AsNoTracking()
            .Where(item => item.Date >= scenario.RangeStart && item.Date <= scenario.RangeEnd)
            .ToListAsync();
        Console.WriteLine("source-calendar-capacity:");
        foreach (var date in EnumerateDates(scenario.RangeStart, scenario.RangeEnd))
        {
            var slots = TimeSlotsResolver.ResolveForDay(
                timeSlots,
                scenario.CourseId,
                date.ToDateTime(TimeOnly.MinValue).DayOfWeek).Slots;
            var overrides = calendar
                .Where(item => item.Date == date)
                .Select(item => $"id={item.Id}/course={item.CourseId?.ToString() ?? "global"}/group={item.GroupId?.ToString() ?? "all"}/working={item.IsWorkingDay}")
                .ToList();
            Console.WriteLine($"{date:yyyy-MM-dd} {date.DayOfWeek}: slots={slots.Count}; overrides={(overrides.Count == 0 ? "<none>" : string.Join(", ", overrides))}");
        }

        await WriteModuleResourceBoundsAsync(db, scenario, excludedTypeIds, timeSlots);
    }

    private static async Task WriteModuleResourceBoundsAsync(
        AppDbContext db,
        Week18Scenario scenario,
        IReadOnlySet<int> excludedTypeIds,
        IReadOnlyList<TimeSlot> timeSlots)
    {
        var groups = await db.Groups
            .AsNoTracking()
            .Where(group => scenario.GroupIds.Contains(group.Id))
            .ToListAsync();
        var teachers = await db.TeacherModules
            .AsNoTracking()
            .Include(link => link.Teacher)
            .Where(link => scenario.ModuleHours.Keys.Contains(link.ModuleId))
            .ToListAsync();
        var workingHours = await db.TeacherWorkingHours.AsNoTracking().ToListAsync();
        var rooms = await db.Rooms.AsNoTracking().ToListAsync();
        var moduleRooms = await db.ModuleRooms.AsNoTracking().ToListAsync();
        var moduleBuildings = await db.ModuleBuildings.AsNoTracking().ToListAsync();
        var topics = await db.ModuleTopics
            .AsNoTracking()
            .Include(topic => topic.LessonType)
            .Where(topic => scenario.ModuleHours.Keys.Contains(topic.ModuleId)
                            && !topic.IsInterAssembly
                            && topic.AuditoriumHours > 0
                            && !excludedTypeIds.Contains(topic.LessonTypeId))
            .OrderBy(topic => topic.ModuleId)
            .ThenBy(topic => topic.Order)
            .ThenBy(topic => topic.TopicCode)
            .ToListAsync();
        var busyTeachers = await db.ScheduleItems
            .AsNoTracking()
            .Where(item => item.TeacherId != null
                           && item.Date >= scenario.RangeStart
                           && item.Date <= scenario.RangeEnd
                           && !excludedTypeIds.Contains(item.LessonTypeId))
            .Select(item => new { item.TeacherId, item.Date, item.StartTime, item.EndTime })
            .ToListAsync();
        var busyRooms = await db.ScheduleItems
            .AsNoTracking()
            .Where(item => item.RoomId != null
                           && item.Date >= scenario.RangeStart
                           && item.Date <= scenario.RangeEnd
                           && !excludedTypeIds.Contains(item.LessonTypeId))
            .Select(item => new { item.RoomId, item.Date, item.StartTime, item.EndTime })
            .ToListAsync();
        var datesAndSlots = EnumerateDates(scenario.RangeStart, scenario.RangeEnd)
            .SelectMany(date => TimeSlotsResolver.ResolveForDay(
                    timeSlots,
                    scenario.CourseId,
                    date.ToDateTime(TimeOnly.MinValue).DayOfWeek).Slots
                .Select(slot => (Date: date, Slot: slot)))
            .ToList();

        bool IsShareable(LessonTypeRef lessonType)
        {
            var code = lessonType.Code?.Trim().ToUpperInvariant() ?? string.Empty;
            var name = lessonType.Name?.Trim().ToUpperInvariant() ?? string.Empty;
            return lessonType.PreferredFirstInWeek
                   || code is "LECTURE" or "LECT" or "LEC"
                   || name.Contains("LECTURE", StringComparison.Ordinal)
                   || name.Contains("ЛЕКЦ", StringComparison.Ordinal);
        }
        bool TeacherFits(int teacherId, DateOnly date, TimeSlot slot)
        {
            var teacherHours = workingHours.Where(hours => hours.TeacherId == teacherId).ToList();
            if (teacherHours.Count == 0)
            {
                return true;
            }
            return teacherHours.Any(hours => hours.DayOfWeek == date.DayOfWeek
                                             && hours.Start <= slot.Start
                                             && slot.End <= hours.End);
        }

        Console.WriteLine("source-resource-bounds:");
        foreach (var moduleId in scenario.ModuleHours.Keys
                     .Where(moduleId => scenario.ModuleCodesById[moduleId] is "2" or "4")
                     .OrderBy(moduleId => scenario.ModuleCodesById[moduleId], StringComparer.Ordinal))
        {
            var allowedRoomIds = moduleRooms
                .Where(link => link.ModuleId == moduleId)
                .Select(link => link.RoomId)
                .ToHashSet();
            var allowedBuildingIds = moduleBuildings
                .Where(link => link.ModuleId == moduleId)
                .Select(link => link.BuildingId)
                .ToHashSet();
            var candidateRooms = rooms
                .Where(room => (allowedRoomIds.Count == 0 || allowedRoomIds.Contains(room.Id))
                               && (allowedBuildingIds.Count == 0 || allowedBuildingIds.Contains(room.BuildingId)))
                .OrderByDescending(room => room.Capacity)
                .ThenBy(room => room.Id)
                .ToList();
            var moduleTeachers = teachers
                .Where(link => link.ModuleId == moduleId)
                .Select(link => link.Teacher)
                .DistinctBy(teacher => teacher.Id)
                .OrderBy(teacher => teacher.Id)
                .ToList();
            var requestedLeft = scenario.ModuleHours[moduleId];
            var requestedTopics = new List<(ModuleTopic Topic, int Hours)>();
            foreach (var topic in topics.Where(topic => topic.ModuleId == moduleId))
            {
                if (requestedLeft <= 0)
                {
                    break;
                }
                var used = Math.Min(requestedLeft, Math.Max(0, topic.AuditoriumHours));
                if (used > 0)
                {
                    requestedTopics.Add((topic, used));
                    requestedLeft -= used;
                }
            }

            var totalStudents = groups.Sum(group => group.StudentsCount);
            var largestRoom = candidateRooms.FirstOrDefault();
            Console.WriteLine($"M{scenario.ModuleCodesById[moduleId]}: teachers={moduleTeachers.Count}; rooms={candidateRooms.Count}; largest-room={largestRoom?.Id.ToString() ?? "none"}/{largestRoom?.Capacity.ToString() ?? "0"}; all-students={totalStudents}; all-groups-fit={largestRoom is not null && largestRoom.Capacity >= totalStudents}");
            Console.WriteLine("  teachers: " + string.Join(", ", moduleTeachers.Select(teacher => $"#{teacher.Id}/{teacher.FullName}/dep={teacher.DepartmentId?.ToString() ?? "none"}")));
            Console.WriteLine("  rooms: " + string.Join(", ", candidateRooms.Select(room => $"#{room.Id}/cap={room.Capacity}/b={room.BuildingId}")));

            var nonSharedDemand = 0;
            var sharedDemand = 0;
            foreach (var (topic, hours) in requestedTopics)
            {
                var shareable = IsShareable(topic.LessonType);
                var eligibleTeachers = moduleTeachers
                    .Where(teacher => topic.DepartmentId is null or <= 0 || teacher.DepartmentId == topic.DepartmentId)
                    .ToList();
                var teacherSlotCapacity = datesAndSlots.Sum(pair => eligibleTeachers.Count(teacher =>
                    TeacherFits(teacher.Id, pair.Date, pair.Slot)
                    && !busyTeachers.Any(item => item.TeacherId == teacher.Id
                                                 && item.Date == pair.Date
                                                 && item.StartTime < pair.Slot.End
                                                 && pair.Slot.Start < item.EndTime)));
                if (shareable)
                {
                    sharedDemand += hours * groups.Count;
                }
                else
                {
                    nonSharedDemand += hours * groups.Count;
                }
                Console.WriteLine($"  topic={topic.TopicCode}; hours={hours}; shareable={shareable}; dep={topic.DepartmentId?.ToString() ?? "none"}; eligible-teachers={eligibleTeachers.Count}; teacher-slot-capacity={teacherSlotCapacity}");
            }

            var roomSlotCapacity = datesAndSlots.Sum(pair => candidateRooms.Count(room =>
                !busyRooms.Any(item => item.RoomId == room.Id
                                       && item.Date == pair.Date
                                       && item.StartTime < pair.Slot.End
                                       && pair.Slot.Start < item.EndTime)));
            var allTeacherSlotCapacity = datesAndSlots.Sum(pair => moduleTeachers.Count(teacher =>
                TeacherFits(teacher.Id, pair.Date, pair.Slot)
                && !busyTeachers.Any(item => item.TeacherId == teacher.Id
                                             && item.Date == pair.Date
                                             && item.StartTime < pair.Slot.End
                                             && pair.Slot.Start < item.EndTime)));
            Console.WriteLine($"  demand: non-shared-group-sessions={nonSharedDemand}; shared-group-hours={sharedDemand}; all-teacher-slot-capacity={allTeacherSlotCapacity}; room-slot-capacity={roomSlotCapacity}");
            foreach (var date in EnumerateDates(scenario.RangeStart, scenario.RangeEnd))
            {
                var dayPairs = datesAndSlots.Where(pair => pair.Date == date).ToList();
                var teacherCapacity = dayPairs.Sum(pair => moduleTeachers.Count(teacher =>
                    TeacherFits(teacher.Id, pair.Date, pair.Slot)
                    && !busyTeachers.Any(item => item.TeacherId == teacher.Id
                                                 && item.Date == pair.Date
                                                 && item.StartTime < pair.Slot.End
                                                 && pair.Slot.Start < item.EndTime)));
                var roomCapacity = dayPairs.Sum(pair => candidateRooms.Count(room =>
                    !busyRooms.Any(item => item.RoomId == room.Id
                                           && item.Date == pair.Date
                                           && item.StartTime < pair.Slot.End
                                           && pair.Slot.Start < item.EndTime)));
                Console.WriteLine($"  {date:yyyy-MM-dd}: teacher-capacity={teacherCapacity}; room-capacity={roomCapacity}");
            }
        }
    }

    private static async Task WriteModuleTopicSummaryAsync(AppDbContext db, Week18Scenario scenario)
    {
        var topics = await db.ModuleTopics
            .AsNoTracking()
            .Include(x => x.Module)
            .Include(x => x.LessonType)
            .Where(x => scenario.ModuleHours.Keys.Contains(x.ModuleId))
            .OrderBy(x => x.Module.Code)
            .ThenBy(x => x.TopicCode)
            .Select(x => new
            {
                ModuleCode = x.Module.Code,
                x.TopicCode,
                LessonType = x.LessonType.Name,
                x.LessonType.PreferredFirstInWeek,
                x.AuditoriumHours,
                x.SelfStudyHours
            })
            .ToListAsync();

        Console.WriteLine("module topics:");
        foreach (var group in topics.GroupBy(x => x.ModuleCode).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var preferredHours = group
                .Where(x => x.PreferredFirstInWeek)
                .Sum(x => Math.Max(0, x.AuditoriumHours));
            var totalHours = group.Sum(x => Math.Max(0, x.AuditoriumHours));
            Console.WriteLine($"M{group.Key}: preferred={preferredHours}; auditorium={totalHours}; requested={scenario.ModuleHours.FirstOrDefault(x => scenario.ModuleCodesById[x.Key] == group.Key).Value}");
            if (!VerboseDiagnosticOutput)
            {
                continue;
            }
            foreach (var topic in group)
            {
                Console.WriteLine($"  {topic.TopicCode}: {topic.LessonType}; pref={topic.PreferredFirstInWeek}; aud={topic.AuditoriumHours}; ss={topic.SelfStudyHours}");
            }
        }
    }

    private static string FormatCluster(ShareableCluster cluster)
        => $"{cluster.Date:yyyy-MM-dd} {cluster.Start:HH\\:mm}-{cluster.End:HH\\:mm} M{cluster.ModuleCode} {cluster.TopicCode ?? "-"} {cluster.LessonType} {cluster.Teacher ?? "<без викладача>"} ауд. {cluster.Room ?? "<без аудиторії>"} groups={string.Join(",", cluster.GroupNames)}";

    private static IEnumerable<DateOnly> EnumerateDates(DateOnly start, DateOnly end)
    {
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            yield return date;
        }
    }

    private static AutoGenResult ExtractResult(ActionResult<AutoGenResult> action)
    {
        if (action.Result is OkObjectResult { Value: AutoGenResult ok })
        {
            return ok;
        }

        if (action.Result is ObjectResult { Value: AutoGenResult direct })
        {
            return direct;
        }

        throw new InvalidOperationException("Автоген не повернув очікуваний результат.");
    }

    private sealed record GroupSnapshot(int Id, string Name);

    private sealed record Week18Scenario(
        int CourseId,
        string CourseName,
        IReadOnlyList<int> GroupIds,
        IReadOnlyDictionary<int, string> GroupNamesById,
        IReadOnlyDictionary<int, int> ModuleHours,
        IReadOnlyDictionary<int, string> ModuleCodesById,
        DateOnly RangeStart,
        DateOnly RangeEnd);

    private sealed record GroupSummary(
        int GroupId,
        string GroupName,
        int Scheduled,
        int Target,
        int EmptySlots,
        int ExpectedEmptySlots);

    private sealed record ShareableCluster(
        DateOnly Date,
        TimeOnly Start,
        TimeOnly End,
        string ModuleCode,
        string? TopicCode,
        string LessonType,
        string? Teacher,
        string? Room,
        IReadOnlyList<string> GroupNames);

    private sealed record ModuleGroupSummary(
        int GroupId,
        string GroupName,
        int ModuleId,
        string ModuleCode,
        int Scheduled,
        int Target,
        int Residual);

    private sealed record Week18Report(
        IReadOnlyList<GroupSummary> GroupSummaries,
        IReadOnlyList<GroupSummary> UnderfilledGroups,
        IReadOnlyList<ModuleGroupSummary> ModuleGroupSummaries,
        IReadOnlyList<string> IncompleteItems,
        IReadOnlyList<string> ModuleSequenceViolations,
        IReadOnlyList<string> TopicSequenceViolations,
        IReadOnlyList<ShareableCluster> ShareableClusters,
        IReadOnlyList<ShareableCluster> ShareableSingletons,
        IReadOnlyList<string> AllItems,
        IReadOnlyList<string> LastDayItems);
}
