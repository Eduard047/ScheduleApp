using System.Reflection;
using BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class AutogenL3SeptemberTwoWeekScenarioTests
{
    private static readonly IReadOnlyDictionary<string, int> ModuleHoursByCode =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["1"] = 7,
            ["2"] = 9,
            ["3"] = 36,
            ["4"] = 10,
            ["5"] = 6,
            ["6"] = 5,
            ["8"] = 7,
            ["10"] = 5,
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
        await using var snapshot = await SqliteSnapshotFile.CreateFromSourceAsync(source, scenario.CourseId);
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
            $"gapSlots={string.Join(", ", (result.GapDetails ?? [])
                .Select(gap => $"{gap.GroupName}/{gap.Date:MM-dd}/{gap.Start:HH\\:mm}/m{gap.ModuleId}/{gap.ReasonCode}/{gap.ConstraintCode}"))}; " +
            $"gapReasons={string.Join(" | ", (result.GapDetails ?? new List<AutoGenGapDetail>())
                .GroupBy(item => item.Reason ?? "<none>")
                .OrderByDescending(group => group.Count())
                .Take(12)
                .Select(group => $"{group.Count()}x {CompactDiagnostic(group.Key)}"))}; " +
            $"fillGapReasons={string.Join(" | ", (fillResult.GapDetails ?? new List<AutoGenGapDetail>())
                .GroupBy(item => item.Reason ?? "<none>")
                .OrderByDescending(group => group.Count())
                .Take(12)
                .Select(group => $"{group.Count()}x {CompactDiagnostic(group.Key)}"))}; " +
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
        if (result.Created + result.Skipped < expected)
        {
            Assert.True(fillResult.Created > 0, diagnostics);
        }
        Assert.True(result.Created + fillResult.Created == expected, diagnostics);
        Assert.Equal(result.Created + fillResult.Created, createdByModule.Sum(item => item.Count));
        Assert.True((result.GapDetails?.Count ?? 0) <= 36, diagnostics);
        Assert.True((fillResult.GapDetails?.Count ?? 0) == 0, diagnostics);
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

        Assert.Equal(scenario.GroupIds.Count * scenario.ModuleHours.Count, createdByGroupModule.Count);
        foreach (var groupId in scenario.GroupIds)
        {
            foreach (var moduleHours in scenario.ModuleHours)
            {
                var actual = createdByGroupModule.SingleOrDefault(item =>
                    item.GroupId == groupId && item.ModuleId == moduleHours.Key);
                Assert.True(actual?.Count == moduleHours.Value, diagnostics);
            }
        }
        foreach (var moduleCode in new[] { "3", "10" })
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
        await AssertSchedulePolicyAsync(db, scenario, expected, diagnostics);

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
        Assert.Empty(repeatedFillResult.GapDetails ?? new List<AutoGenGapDetail>());
        Assert.Equal(fingerprintBeforeIdempotenceCheck, fingerprintAfterIdempotenceCheck);
    }

    [Fact(Timeout = 600_000)]
    public async Task Generate_preview_automatically_fills_all_slots_and_applies_exact_plan()
    {
        await using var source = ServerConfigurationFactory.CreateSourceContext();
        var scenario = await LoadScenarioAsync(source);
        await using var snapshot = await SqliteSnapshotFile.CreateFromSourceAsync(source, scenario.CourseId);
        await using var database = new SqliteTempDatabase(snapshot.Path);
        var services = new ServiceCollection();
        services.AddScoped(_ => database.CreateContext());
        services.AddScoped<TeacherDraftsAutogenService>();
        services.AddScoped<TeacherDraftsAutogenPlanService>();
        await using var provider = services.BuildServiceProvider();
        var jobService = new TeacherDraftsAutogenJobService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TeacherDraftsAutogenJobService>.Instance);
        int persistedBeforePreview;
        await using (var beforePreviewDb = database.CreateContext())
        {
            persistedBeforePreview = await beforePreviewDb.TeacherDraftItems
                .AsNoTracking()
                .CountAsync(item => scenario.GroupIds.Contains(item.GroupId)
                                    && item.Date >= new DateOnly(2026, 9, 1)
                                    && item.Date <= new DateOnly(2026, 9, 12));
        }
        var request = new AutoGenJobRequest(
            Kind: AutoGenJobKind.Generate,
            FromDate: new DateOnly(2026, 9, 1),
            ToDate: new DateOnly(2026, 9, 12),
            CourseId: scenario.CourseId,
            GroupIds: scenario.GroupIds,
            ModuleHours: scenario.ModuleHours,
            Days: WeekPreset.MonSat,
            ClearExisting: true,
            SoftFill: false,
            PreflightOnly: false,
            AllowIncompleteDrafts: false,
            SoftOptions: AutoGenRecommendedProfile.CreateSoftOptions(),
            PreferredFirstMaxSlotOrderOverride:
                AutoGenRecommendedProfile.PreferredFirstMaxSlotOrderOverride,
            Title: "Точна перевірка єдиного формування плану",
            PreviewOnly: true);
        var runtimeType = typeof(TeacherDraftsAutogenJobService)
            .GetNestedType("AutoGenJobRuntime", BindingFlags.NonPublic);
        Assert.NotNull(runtimeType);
        var runtime = Activator.CreateInstance(
            runtimeType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { request },
            culture: null);
        Assert.NotNull(runtime);
        var runMethod = typeof(TeacherDraftsAutogenJobService)
            .GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(runMethod);
        await Assert.IsAssignableFrom<Task>(runMethod.Invoke(jobService, new[] { runtime }));
        var statusMethod = runtimeType.GetMethod("ToDto", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(statusMethod);
        var status = Assert.IsType<AutoGenJobStatus>(statusMethod.Invoke(runtime, null));
        var result = status.Result;
        var expected = scenario.GroupIds.Count * scenario.ModuleHours.Values.Sum();
        var diagnostics =
            $"state={status.State}; created={result?.Created}; skipped={result?.Skipped}; "
            + $"gaps={result?.GapDetails?.Count ?? -1}; deficits={result?.Preflight?.Sum(item => item.Count) ?? -1}; "
            + $"plan={status.Plan?.State}; error={status.Error ?? "<none>"}; "
            + $"warnings={string.Join(" | ", result?.Warnings.Take(50) ?? [])}";

        Assert.True(status.State == AutoGenJobState.Succeeded, diagnostics);
        Assert.NotNull(result);
        Assert.True(result.Created == expected, diagnostics);
        Assert.True(result.Skipped == 0, diagnostics);
        Assert.True((result.GapDetails?.Count ?? 0) == 0, diagnostics);
        Assert.Empty(result.Preflight ?? []);
        Assert.NotNull(status.Report);
        Assert.True(status.Report.DeficitCount == 0, diagnostics);
        var provisionalWarningCodes = new HashSet<string>(StringComparer.Ordinal)
        {
            AutoGenWarningCodes.PreflightDeficit,
            AutoGenWarningCodes.GapUnfilled,
            AutoGenWarningCodes.SearchLimit,
            AutoGenWarningCodes.TopicExhausted,
            AutoGenWarningCodes.ResourceUnavailable,
            AutoGenWarningCodes.Recommendation,
            AutoGenWarningCodes.DiagnosticSummary
        };
        Assert.DoesNotContain(
            result.Warnings,
            warning => provisionalWarningCodes.Contains(AutoGenWarningClassifier.Classify(warning).Code));
        Assert.NotNull(status.Plan);
        Assert.True(status.Plan.State == AutoGenPlanState.Ready, diagnostics);
        if (persistedBeforePreview == 0)
        {
            Assert.Equal(expected, status.Plan.AddCount);
            Assert.Equal(0, status.Plan.UpdateCount);
            Assert.Equal(0, status.Plan.DeleteCount);
        }

        await using (var afterPreviewDb = database.CreateContext())
        {
            var persistedAfterPreview = await afterPreviewDb.TeacherDraftItems
                .AsNoTracking()
                .CountAsync(item => scenario.GroupIds.Contains(item.GroupId)
                                    && item.Date >= new DateOnly(2026, 9, 1)
                                    && item.Date <= new DateOnly(2026, 9, 12));
            Assert.True(
                persistedAfterPreview == persistedBeforePreview,
                $"{diagnostics}; beforePreview={persistedBeforePreview}; afterPreview={persistedAfterPreview}");
        }

        var readyPlan = await jobService.GetPlanAsync(status.Plan.PlanId, CancellationToken.None);
        Assert.Equal(AutoGenPlanState.Ready, readyPlan.Summary.State);
        Assert.Equal(expected, readyPlan.Result.Created);
        Assert.Equal(0, readyPlan.Result.Skipped);
        Assert.Empty(readyPlan.Result.GapDetails ?? []);
        Assert.Empty(readyPlan.Result.Preflight ?? []);

        AutoGenPlanDetailsDto appliedPlan;
        await using (var applyDb = database.CreateContext())
        {
            appliedPlan = await new TeacherDraftsAutogenPlanService(applyDb).ApplyAsync(
                readyPlan.Summary.PlanId,
                new AutoGenPlanActionRequest(readyPlan.Summary.Version));
        }
        Assert.Equal(AutoGenPlanState.Applied, appliedPlan.Summary.State);
        Assert.Equal(expected, appliedPlan.Result.Created);
        Assert.Equal(0, appliedPlan.Result.Skipped);
        Assert.Empty(appliedPlan.Result.GapDetails ?? []);

        await using var verificationDb = database.CreateContext();
        var persisted = await verificationDb.TeacherDraftItems
            .AsNoTracking()
            .CountAsync(item => scenario.GroupIds.Contains(item.GroupId)
                                && item.Date >= new DateOnly(2026, 9, 1)
                                && item.Date <= new DateOnly(2026, 9, 12));
        Assert.True(persisted == expected, $"{diagnostics}; persisted={persisted}");
        var persistedByGroupAndModule = await verificationDb.TeacherDraftItems
            .AsNoTracking()
            .Where(item => scenario.GroupIds.Contains(item.GroupId)
                           && item.Date >= new DateOnly(2026, 9, 1)
                           && item.Date <= new DateOnly(2026, 9, 12))
            .GroupBy(item => new { item.GroupId, item.ModuleId })
            .Select(group => new { group.Key.GroupId, group.Key.ModuleId, Count = group.Count() })
            .ToListAsync();
        Assert.Equal(scenario.GroupIds.Count * scenario.ModuleHours.Count, persistedByGroupAndModule.Count);
        foreach (var groupId in scenario.GroupIds)
        {
            foreach (var moduleHours in scenario.ModuleHours)
            {
                Assert.Contains(
                    persistedByGroupAndModule,
                    item => item.GroupId == groupId
                            && item.ModuleId == moduleHours.Key
                            && item.Count == moduleHours.Value);
            }
        }
        var hardRuleValidation = await new TeacherDraftsAutogenHardRuleValidator(verificationDb)
            .ValidateAsync(
                new TeacherDraftsAutogenHardRuleValidationRequest(
                    scenario.CourseId,
                    scenario.GroupIds,
                    new DateOnly(2026, 9, 1),
                    new DateOnly(2026, 9, 12),
                    WeekPreset.MonSat,
                    AllowIncompleteDrafts: false,
                    MaxParallelGroupsPerModuleInSlot:
                        AutoGenRecommendedProfile.MaxParallelGroupsPerModuleInSlot));
        Assert.True(
            hardRuleValidation.Violations.Count == 0,
            $"{diagnostics}; violations={string.Join(" | ", hardRuleValidation.Violations)}");
        await AssertSchedulePolicyAsync(verificationDb, scenario, expected, diagnostics);
    }


    private static async Task AssertSchedulePolicyAsync(
        AppDbContext db,
        L3Scenario scenario,
        int expected,
        string diagnostics)
    {
        var persistedRows = await db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => scenario.GroupIds.Contains(item.GroupId)
                           && item.Date >= new DateOnly(2026, 9, 1)
                           && item.Date <= new DateOnly(2026, 9, 12))
            .Select(item => new
            {
                item.GroupId,
                item.ModuleId,
                item.ModuleTopicId,
                item.Date,
                item.StartTime,
                item.EndTime,
                item.TeacherId,
                item.RoomId,
                item.IsSelfStudy,
                item.LessonType.Code,
                item.LessonType.Name,
                item.LessonType.PreferredFirstInWeek,
                item.LessonType.RequiresTeacher,
                item.LessonType.RequiresRoom
            })
            .ToListAsync();
        var rows = persistedRows
            .Select(item => new ScheduledLesson(
                item.GroupId,
                item.ModuleId,
                item.ModuleTopicId,
                item.Date,
                item.StartTime,
                item.EndTime,
                item.TeacherId,
                item.RoomId,
                item.IsSelfStudy,
                item.Code,
                item.Name,
                item.PreferredFirstInWeek,
                item.RequiresTeacher,
                item.RequiresRoom))
            .ToList();
        Assert.True(rows.Count == expected, $"{diagnostics}; persistedRows={rows.Count}");

        foreach (var row in rows)
        {
            if (row.RequiresTeacher)
            {
                Assert.True(row.TeacherId is not null, $"{diagnostics}; missingTeacher={row.GroupId}/{row.Date:yyyy-MM-dd}/{row.Start:HH\\:mm}");
            }
            if (row.RequiresRoom)
            {
                Assert.True(row.RoomId is not null, $"{diagnostics}; missingRoom={row.GroupId}/{row.Date:yyyy-MM-dd}/{row.Start:HH\\:mm}");
            }
        }

        var topicLimits = await db.ModuleTopics
            .AsNoTracking()
            .Where(topic => scenario.ModuleHours.Keys.Contains(topic.ModuleId))
            .ToDictionaryAsync(topic => topic.Id, topic => Math.Max(0, topic.AuditoriumHours));
        foreach (var usage in rows
                     .Where(row => row.ModuleTopicId is not null)
                     .GroupBy(row => new { row.GroupId, TopicId = row.ModuleTopicId!.Value }))
        {
            Assert.True(topicLimits.TryGetValue(usage.Key.TopicId, out var limit), $"{diagnostics}; unknownTopic={usage.Key.TopicId}");
            Assert.True(
                usage.Count() <= limit,
                $"{diagnostics}; topicLimit={usage.Key.GroupId}/{usage.Key.TopicId}:{usage.Count()}/{limit}");
        }

        var timeSlots = await db.TimeSlots
            .AsNoTracking()
            .Where(slot => slot.CourseId == null || slot.CourseId == scenario.CourseId)
            .ToListAsync();
        var lunches = await db.LunchConfigs
            .AsNoTracking()
            .Where(lunch => lunch.CourseId == null || lunch.CourseId == scenario.CourseId)
            .ToListAsync();
        var resolvedSlots = TimeSlotsResolver.ResolveForWeek(timeSlots, scenario.CourseId, lunches);
        foreach (var day in rows.GroupBy(row => new { row.GroupId, row.Date }))
        {
            var slots = resolvedSlots[day.Key.Date.DayOfWeek].Slots;
            var seenNonLecture = false;
            foreach (var row in day.OrderBy(item => item.Start).ThenBy(item => item.End))
            {
                var slotIndex = slots.FindIndex(slot => slot.Start == row.Start && slot.End == row.End);
                Assert.True(slotIndex >= 0, $"{diagnostics}; nonCanonicalSlot={row.GroupId}/{row.Date:yyyy-MM-dd}/{row.Start:HH\\:mm}");
                if (IsLecture(row))
                {
                    Assert.True(
                        slotIndex + 1 <= AutoGenRecommendedProfile.PreferredFirstMaxSlotOrderOverride,
                        $"{diagnostics}; lateLecture={row.GroupId}/{row.Date:yyyy-MM-dd}/pair{slotIndex + 1}");
                    Assert.False(
                        seenNonLecture,
                        $"{diagnostics}; lectureAfterOtherType={row.GroupId}/{row.Date:yyyy-MM-dd}/{row.Start:HH\\:mm}");
                }
                else
                {
                    seenNonLecture = true;
                }
            }
        }

        var moduleThreeId = scenario.ModuleIdsByCode["3"];
        foreach (var groupId in scenario.GroupIds)
        {
            var moduleThreeRows = rows
                .Where(row => row.GroupId == groupId && row.ModuleId == moduleThreeId)
                .ToList();
            Assert.True(moduleThreeRows.Count == 36, $"{diagnostics}; module3={groupId}/{moduleThreeRows.Count}");
            Assert.True(moduleThreeRows.Count(IsLecture) == 26, $"{diagnostics}; module3Lectures={groupId}/{moduleThreeRows.Count(IsLecture)}");
            Assert.True(moduleThreeRows.Count(row => !IsLecture(row)) == 10, $"{diagnostics}; module3Other={groupId}/{moduleThreeRows.Count(row => !IsLecture(row))}");
        }
    }

    private static bool IsLecture(ScheduledLesson row)
    {
        if (row.IsSelfStudy || row.PreferredFirstInWeek)
        {
            return !row.IsSelfStudy;
        }
        var code = row.LessonTypeCode.Trim().ToUpperInvariant();
        if (code is "LECTURE" or "LECT" or "LEC")
        {
            return true;
        }
        var name = row.LessonTypeName.Trim().ToUpperInvariant();
        return name.Contains("LECTURE", StringComparison.Ordinal)
               || name.Contains("ЛЕКЦ", StringComparison.Ordinal)
               || name.Contains("ЛЕКЦІ", StringComparison.Ordinal)
               || name.Contains("ЛЕКЦІЇ", StringComparison.Ordinal);
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
                           && GroupNames.Contains(item.Name))
            .OrderBy(item => item.Name)
            .ToListAsync();
        Assert.Equal(GroupNames.Length, groups.Count);
        Assert.Equal(GroupNames, groups.Select(item => item.Name));

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

    private static string CompactDiagnostic(string value)
        => value.Length <= 300 ? value : $"{value[..300]}…";

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

    private sealed record ScheduledLesson(
        int GroupId,
        int ModuleId,
        int? ModuleTopicId,
        DateOnly Date,
        TimeOnly Start,
        TimeOnly End,
        int? TeacherId,
        int? RoomId,
        bool IsSelfStudy,
        string LessonTypeCode,
        string LessonTypeName,
        bool PreferredFirstInWeek,
        bool RequiresTeacher,
        bool RequiresRoom);
}

public sealed class DatabaseSnapshotSecurityTests
{
    [Fact]
    public void Snapshot_allowlist_excludes_working_and_autogen_state()
    {
        Assert.DoesNotContain(typeof(ScheduleItem), DatabaseSnapshotCopier.AllowedTypes);
        Assert.DoesNotContain(typeof(TeacherDraftItem), DatabaseSnapshotCopier.AllowedTypes);
        Assert.DoesNotContain(typeof(AutoGenJobRun), DatabaseSnapshotCopier.AllowedTypes);
        Assert.DoesNotContain(typeof(AutoGenDraftPlan), DatabaseSnapshotCopier.AllowedTypes);
        Assert.DoesNotContain(typeof(AutoGenDraftPlanMutation), DatabaseSnapshotCopier.AllowedTypes);
    }

    [Fact]
    public async Task Snapshot_sanitizes_personal_fields_and_removes_private_temp_directory()
    {
        await using var sourceConnection = new SqliteConnection("Data Source=:memory:");
        await sourceConnection.OpenAsync();
        var sourceOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(sourceConnection)
            .Options;
        await using var source = new AppDbContext(sourceOptions);
        await source.Database.EnsureCreatedAsync();
        var department = new Department { Name = "Кафедра" };
        source.Teachers.Add(new Teacher
        {
            FullName = "Персональне ім'я",
            ScientificDegree = "ступінь",
            AcademicTitle = "звання",
            Department = department
        });
        source.Buildings.Add(new Building
        {
            Name = "Корпус",
            Address = "Приватна адреса"
        });
        await source.SaveChangesAsync();

        var snapshot = await SqliteSnapshotFile.CreateFromSourceAsync(source);
        var snapshotPath = snapshot.Path;
        var snapshotDirectory = Assert.IsType<DirectoryInfo>(Directory.GetParent(snapshotPath)).FullName;
        try
        {
            Assert.True(File.Exists(snapshotPath));
            if (!OperatingSystem.IsWindows())
            {
                var fileMode = File.GetUnixFileMode(snapshotPath);
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    fileMode & (UnixFileMode)0x1FF);
            }

            await using var snapshotConnection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = snapshotPath }.ToString());
            await snapshotConnection.OpenAsync();
            var snapshotOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(snapshotConnection)
                .Options;
            await using var copied = new AppDbContext(snapshotOptions);
            var teacher = await copied.Teachers.AsNoTracking().SingleAsync();
            var building = await copied.Buildings.AsNoTracking().SingleAsync();
            Assert.Equal($"Викладач {teacher.Id}", teacher.FullName);
            Assert.Null(teacher.ScientificDegree);
            Assert.Null(teacher.AcademicTitle);
            Assert.Null(building.Address);
        }
        finally
        {
            await snapshot.DisposeAsync();
        }

        Assert.False(Directory.Exists(snapshotDirectory));
    }
}
