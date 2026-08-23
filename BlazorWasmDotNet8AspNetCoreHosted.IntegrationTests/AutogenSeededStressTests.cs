using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests.Infrastructure;
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

[Collection("Autogen performance")]
public sealed class AutogenSeededStressTests
{
    private const int SmokeScenarioCount = 100;
    private const int MetamorphicScenarioCount = 24;
    private const int DefaultStressScenarioCount = 10_000;
    private const int DefaultDeficitScenarioCount = 2_000;
    private const int DefaultLifecycleScenarioCount = 500;
    private const int DefaultScaleScenarioCount = 10;
    private const int FirstSeed = 50_000;

    [Fact(Timeout = 180_000)]
    [Trait("Category", "AutogenQuality")]
    public async Task Seeded_synthetic_smoke_matrix_preserves_generation_invariants()
    {
        var summary = await RunFeasibleMatrixAsync(
            FirstSeed,
            SmokeScenarioCount,
            replayStride: 6,
            forcedScale: StressScale.Compact);

        Assert.Equal(SmokeScenarioCount, summary.ScenarioCount);
        Assert.True(summary.TotalCreated > 0);
    }

    [Fact(Timeout = 180_000)]
    [Trait("Category", "AutogenQuality")]
    public async Task Seeded_metamorphic_matrix_is_stable_under_id_remap_and_unused_resources()
    {
        for (var index = 0; index < MetamorphicScenarioCount; index++)
        {
            var seed = FirstSeed + 20_000 + index;
            var scenario = StressScenario.CreateFeasible(
                seed,
                allowLockedSeed: false,
                forcedScale: StressScale.Compact);
            var baseline = await RunFeasibleScenarioAsync(
                seed,
                reverseInputOrder: false,
                scenario);
            var remapped = await RunFeasibleScenarioAsync(
                seed,
                reverseInputOrder: true,
                scenario.RemapIdentifiers(500_000_000),
                addUnusedResources: true);

            Assert.True(
                string.Equals(baseline.ShapeFingerprint, remapped.ShapeFingerprint, StringComparison.Ordinal),
                $"Seed {seed}: перейменування ID та додавання невикористаних ресурсів змінило форму розкладу " +
                $"{baseline.ShapeFingerprint} -> {remapped.ShapeFingerprint}.");
        }
    }

    [Fact(Timeout = 180_000)]
    [Trait("Category", "AutogenQuality")]
    public async Task Confirmed_seed_regressions_remain_gapless()
    {
        foreach (var seed in new[] { 50_060, 51_000, 70_020, 300_001 })
        {
            var scenario = StressScenario.CreateFeasible(
                seed,
                allowLockedSeed: false,
                forcedScale: seed is 51_000 or 300_001
                    ? StressScale.Medium
                    : StressScale.Compact);
            await RunFeasibleScenarioAsync(seed, reverseInputOrder: seed % 2 == 0, scenario);
        }
    }

    [AutogenStressFact(Timeout = 3_600_000)]
    [Trait("Category", "AutogenStress")]
    public async Task Seeded_synthetic_stress_matrix_covers_thousands_of_distinct_inputs()
    {
        var firstSeed = ReadBoundedCount(
            "AUTOGEN_STRESS_FIRST_SEED",
            FirstSeed,
            minimum: 1,
            maximum: 1_000_000);
        var feasibleCount = ReadBoundedCount(
            "AUTOGEN_STRESS_SCENARIOS",
            DefaultStressScenarioCount,
            minimum: 1,
            maximum: 100_000);
        var deficitCount = ReadBoundedCount(
            "AUTOGEN_STRESS_DEFICIT_SCENARIOS",
            DefaultDeficitScenarioCount,
            minimum: 0,
            maximum: 20_000);
        var feasible = await RunFeasibleMatrixAsync(
            firstSeed,
            feasibleCount,
            replayStride: 50,
            forcedScale: StressScale.Compact);
        var deficits = await RunDeficitMatrixAsync(firstSeed + feasibleCount, deficitCount);

        Console.WriteLine(
            $"Стрес-матриця: повних сценаріїв={feasible.ScenarioCount}; " +
            $"дефіцитних preflight-сценаріїв={deficits}; " +
            $"створено чернеток={feasible.TotalCreated}; час={feasible.Runtime.TotalSeconds:F1} с.");
        Assert.Equal(feasibleCount, feasible.ScenarioCount);
        Assert.Equal(deficitCount, deficits);
    }

    [AutogenStressFact(Timeout = 3_600_000)]
    [Trait("Category", "AutogenStress")]
    public async Task Seeded_job_lifecycle_keeps_preview_atomic_apply_idempotent_and_rollback_safe()
    {
        var scenarioCount = ReadBoundedCount(
            "AUTOGEN_STRESS_LIFECYCLE_SCENARIOS",
            DefaultLifecycleScenarioCount,
            minimum: 1,
            maximum: 5_000);

        var firstSeed = ReadBoundedCount(
            "AUTOGEN_STRESS_LIFECYCLE_FIRST_SEED",
            FirstSeed + 10_000,
            minimum: 1,
            maximum: 1_000_000);
        for (var index = 0; index < scenarioCount; index++)
        {
            var seed = firstSeed + index;
            await RunLifecycleScenarioAsync(seed);
        }
    }

    [AutogenStressFact(Timeout = 3_600_000)]
    [Trait("Category", "AutogenStress")]
    public async Task Seeded_scale_matrix_covers_large_multiweek_courses()
    {
        var scenarioCount = ReadBoundedCount(
            "AUTOGEN_STRESS_SCALE_SCENARIOS",
            DefaultScaleScenarioCount,
            minimum: 1,
            maximum: 2_000);
        var firstSeed = ReadBoundedCount(
            "AUTOGEN_STRESS_SCALE_FIRST_SEED",
            FirstSeed + 30_000,
            minimum: 1,
            maximum: 1_000_000);
        var totalCreated = 0;

        for (var index = 0; index < scenarioCount; index++)
        {
            var seed = firstSeed + index;
            var scenario = StressScenario.CreateFeasible(
                seed,
                allowLockedSeed: index % 7 == 0,
                forcedScale: index % 3 == 0 ? StressScale.Large : StressScale.Medium);
            var snapshot = await RunFeasibleScenarioAsync(
                seed,
                reverseInputOrder: index % 2 == 1,
                scenario);
            totalCreated += snapshot.Created;
        }

        Assert.True(totalCreated > scenarioCount);
    }

    private static async Task<StressSummary> RunFeasibleMatrixAsync(
        int firstSeed,
        int scenarioCount,
        int replayStride,
        StressScale? forcedScale = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var totalCreated = 0;
        var reverseFirst = string.Equals(
            Environment.GetEnvironmentVariable("AUTOGEN_STRESS_REVERSE_FIRST"),
            "1",
            StringComparison.Ordinal);
        for (var index = 0; index < scenarioCount; index++)
        {
            var seed = firstSeed + index;
            var scenario = StressScenario.CreateFeasible(seed, forcedScale: forcedScale);
            var snapshot = await RunFeasibleScenarioAsync(
                seed,
                reverseInputOrder: reverseFirst,
                scenario);
            totalCreated += snapshot.Created;

            if (replayStride > 0 && index % replayStride == 0)
            {
                var replay = await RunFeasibleScenarioAsync(
                    seed,
                    reverseInputOrder: !reverseFirst,
                    scenario);
                Assert.True(
                    string.Equals(snapshot.Fingerprint, replay.Fingerprint, StringComparison.Ordinal),
                    $"Seed {seed}: перестановка вхідних колекцій змінила результат " +
                    $"{snapshot.Fingerprint} -> {replay.Fingerprint}.");
            }
        }
        stopwatch.Stop();
        return new StressSummary(scenarioCount, totalCreated, stopwatch.Elapsed);
    }

    private static async Task<StressSnapshot> RunFeasibleScenarioAsync(
        int seed,
        bool reverseInputOrder,
        StressScenario? scenarioOverride = null,
        bool addUnusedResources = false)
    {
        var requestedScenario = scenarioOverride ?? StressScenario.CreateFeasible(seed);
        await using var database = await StressDatabase.CreateAsync(
            requestedScenario,
            reverseInputOrder,
            addUnusedResources);
        var scenario = database.Scenario;
        await using var executionDb = new AppDbContext(database.Options);
        var beforeCount = await executionDb.TeacherDraftItems.CountAsync();
        var autogen = new TeacherDraftsAutogenService(executionDb);
        ActionResult<AutoGenResult> action;
        if (string.Equals(
                Environment.GetEnvironmentVariable("AUTOGEN_STRESS_AMBIENT"),
                "1",
                StringComparison.Ordinal))
        {
            await using var transaction = await executionDb.Database.BeginTransactionAsync();
            action = await autogen.DraftAutoGenInAmbientTransaction(scenario.Request);
            await transaction.CommitAsync();
        }
        else
        {
            action = await autogen.DraftAutoGen(scenario.Request);
        }
        var result = ExtractResult(action, seed);
        executionDb.ChangeTracker.Clear();
        var rows = await executionDb.TeacherDraftItems
            .AsNoTracking()
            .Where(item => scenario.GroupIds.Contains(item.GroupId)
                           && item.Date >= scenario.RangeStart
                           && item.Date <= scenario.RangeEnd)
            .OrderBy(item => item.Date)
            .ThenBy(item => item.StartTime)
            .ThenBy(item => item.GroupId)
            .ThenBy(item => item.ModuleId)
            .ToListAsync();
        var expectedTotal = scenario.GroupIds.Count * scenario.ModuleHours.Values.Sum();
        var traceScenario = string.Equals(
            Environment.GetEnvironmentVariable("AUTOGEN_STRESS_TRACE_SEED"),
            seed.ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

        if (traceScenario)
        {
            Console.WriteLine($"Seed {seed}: {DescribeScenario(scenario)}; {DescribeRows(rows, rows.Count)}");
            Console.WriteLine(string.Join(Environment.NewLine, result.Warnings));
        }

        Assert.True(
            result.GapDetails is null || result.GapDetails.Count == 0,
            $"Seed {seed}: здійсненний сценарій залишив {result.GapDetails?.Count ?? 0} прогалин: " +
            $"{DescribeGaps(result.GapDetails)}. " +
            $"Сценарій: {DescribeScenario(scenario)}. " +
            $"Квоти: {DescribeQuotaDiff(rows, scenario)}. " +
            $"Попередження: {string.Join(" | ", result.Warnings.Take(traceScenario ? result.Warnings.Count : 10))}. " +
            $"Розміщення: {DescribeRows(rows, traceScenario ? rows.Count : 20)}");
        Assert.True(
            rows.Count == expectedTotal,
            $"Seed {seed}: очікувалося {expectedTotal} чернеток, отримано {rows.Count}; " +
            $"створено={result.Created}, до запуску={beforeCount}.");
        Assert.True(
            result.Created == expectedTotal - beforeCount,
            $"Seed {seed}: Created={result.Created}, очікувалося {expectedTotal - beforeCount}.");
        Assert.True(
            rows.All(row => row.TeacherId is not null && row.RoomId is not null),
            $"Seed {seed}: повний сценарій містить незавершені чернетки.");

        foreach (var groupId in scenario.GroupIds)
        {
            foreach (var moduleHours in scenario.ModuleHours)
            {
                var actual = rows.Count(row => row.GroupId == groupId && row.ModuleId == moduleHours.Key);
                Assert.True(
                    actual == moduleHours.Value,
                    $"Seed {seed}: група #{groupId}, модуль #{moduleHours.Key}: " +
                    $"отримано {actual}, очікувалося {moduleHours.Value}.");
            }
        }

        var hardValidation = await new TeacherDraftsAutogenHardRuleValidator(executionDb)
            .ValidateAsync(new TeacherDraftsAutogenHardRuleValidationRequest(
                scenario.CourseId,
                scenario.GroupIds,
                scenario.RangeStart,
                scenario.RangeEnd,
                scenario.Request.Days,
                AllowIncompleteDrafts: false));
        Assert.True(
            hardValidation.Violations.Count == 0,
            $"Seed {seed}: порушено hard rules: {string.Join(" | ", hardValidation.Violations.Take(10))}");
        var travelViolations = await TravelInvariantVerifier.FindViolationsAsync(
            executionDb,
            scenario.CourseId,
            scenario.RangeStart,
            scenario.RangeEnd);
        Assert.True(
            travelViolations.Count == 0,
            $"Seed {seed}: порушено правила переходів: {string.Join(" | ", travelViolations.Take(10))}");
        AssertNoOverlaps(rows, seed);
        AssertNoErrorWarnings(result, seed);

        return new StressSnapshot(
            result.Created,
            BuildFingerprint(rows),
            BuildShapeFingerprint(rows, scenario));
    }

    private static async Task<int> RunDeficitMatrixAsync(int firstSeed, int scenarioCount)
    {
        for (var index = 0; index < scenarioCount; index++)
        {
            var seed = firstSeed + index;
            var shortageKinds = Enum.GetValues<StressShortageKind>();
            var shortage = shortageKinds[index % shortageKinds.Length];
            await using var database = await StressDatabase.CreateAsync(
                StressScenario.CreateDeficit(seed, shortage),
                reverseInputOrder: index % 2 == 1);
            var scenario = database.Scenario;
            await using var executionDb = new AppDbContext(database.Options);
            var before = await executionDb.TeacherDraftItems.AsNoTracking().CountAsync();
            var action = await new TeacherDraftsAutogenService(executionDb).DraftAutoGen(scenario.Request);
            var result = ExtractResult(action, seed);
            executionDb.ChangeTracker.Clear();
            var after = await executionDb.TeacherDraftItems.AsNoTracking().CountAsync();
            var diagnosticCount = (result.Preflight?.Count ?? 0)
                                  + (result.GapDetails?.Count ?? 0)
                                  + result.Warnings.Count;

            Assert.True(
                result.Created == 0 && before == after,
                $"Seed {seed}: preflight змінив чернетки або повідомив про створення: " +
                $"Created={result.Created}, before={before}, after={after}.");
            Assert.True(
                diagnosticCount > 0,
                $"Seed {seed}: дефіцит {shortage} не отримав жодної структурованої діагностики.");
            Assert.True(
                result.GapDetails is null
                || result.GapDetails.All(gap =>
                    AutoGenGapReasonClassifier.Classify(gap).Code is not AutoGenGapReasonCodes.Unknown),
                $"Seed {seed}: дефіцит {shortage} повернув невідому причину прогалини.");
        }

        return scenarioCount;
    }

    private static async Task RunLifecycleScenarioAsync(int seed)
    {
        await using var database = await StressDatabase.CreateAsync(
            StressScenario.CreateFeasible(
                seed,
                allowLockedSeed: false,
                forcedScale: StressScale.Compact),
            reverseInputOrder: seed % 2 == 0);
        var scenario = database.Scenario;
        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(database.Options));
        services.AddScoped<TeacherDraftsAutogenService>();
        services.AddScoped<TeacherDraftsAutogenPlanService>();
        await using var provider = services.BuildServiceProvider();
        var jobService = new TeacherDraftsAutogenJobService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TeacherDraftsAutogenJobService>.Instance);
        var request = new AutoGenJobRequest(
            AutoGenJobKind.Generate,
            scenario.RangeStart,
            scenario.RangeEnd,
            scenario.CourseId,
            scenario.GroupIds.ToList(),
            scenario.ModuleHours.ToDictionary(entry => entry.Key, entry => entry.Value),
            scenario.Request.Days,
            ClearExisting: true,
            SoftFill: true,
            PreflightOnly: false,
            AllowIncompleteDrafts: false,
            GroupRoomPreferences: scenario.Request.GroupRoomPreferences,
            SoftOptions: MapSoftOptions(scenario.Request.SoftOptions),
            PreferredFirstMaxSlotOrderOverride: scenario.Request.PreferredFirstMaxSlotOrderOverride,
            ClientJobId: seed.ToString("x32", CultureInfo.InvariantCulture),
            PreviewOnly: true);

        var started = jobService.Start(request);
        var replayedStart = jobService.Start(request);
        Assert.True(
            string.Equals(started.JobId, replayedStart.JobId, StringComparison.Ordinal),
            $"Seed {seed}: повторний Start з тим самим ClientJobId створив інше завдання.");
        var status = await WaitForTerminalStatusAsync(jobService, started.JobId, seed);
        Assert.True(
            status.State == AutoGenJobState.Succeeded,
            $"Seed {seed}: job завершився зі станом {status.State}: {status.Error}");
        Assert.NotNull(status.Plan);
        Assert.True(
            status.Plan.State == AutoGenPlanState.Ready,
            $"Seed {seed}: preview-план має стан {status.Plan.State} замість Ready.");
        var expectedTotal = scenario.GroupIds.Count * scenario.ModuleHours.Values.Sum();
        await using (var previewCheck = new AppDbContext(database.Options))
        {
            Assert.True(
                await previewCheck.TeacherDraftItems.AsNoTracking().CountAsync() == 0,
                $"Seed {seed}: preview змінив робочі чернетки.");
        }

        var ready = await jobService.GetPlanAsync(started.JobId);
        Assert.True(
            ready.Changes.Count == expectedTotal,
            $"Seed {seed}: preview містить {ready.Changes.Count} змін замість {expectedTotal}. " +
            $"Зміни: {string.Join(", ", ready.Changes.Select(change => $"g{change.After?.GroupId}/m{change.After?.ModuleId}@{change.After?.Date:yyyy-MM-dd}"))}. " +
            $"Попередження: {string.Join(" | ", status.Result?.Warnings.TakeLast(12) ?? [])}");
        await Assert.ThrowsAsync<AutoGenPlanConflictException>(() => jobService.ApplyPlanAsync(
            started.JobId,
            new AutoGenPlanActionRequest(ready.Summary.Version + 1)));
        await using (var staleApplyCheck = new AppDbContext(database.Options))
        {
            Assert.True(
                await staleApplyCheck.TeacherDraftItems.AsNoTracking().CountAsync() == 0,
                $"Seed {seed}: Apply із застарілою версією змінив робочі чернетки.");
        }
        var applied = await jobService.ApplyPlanAsync(
            started.JobId,
            new AutoGenPlanActionRequest(ready.Summary.Version));
        Assert.True(
            applied.Summary.State == AutoGenPlanState.Applied,
            $"Seed {seed}: план не перейшов у Applied.");
        var repeatedApply = await jobService.ApplyPlanAsync(
            started.JobId,
            new AutoGenPlanActionRequest(ready.Summary.Version));
        Assert.True(
            repeatedApply.Summary.Version == applied.Summary.Version,
            $"Seed {seed}: повторний Apply не був ідемпотентним.");
        await using (var appliedCheck = new AppDbContext(database.Options))
        {
            Assert.True(
                await appliedCheck.TeacherDraftItems.AsNoTracking().CountAsync() == expectedTotal,
                $"Seed {seed}: Apply зберіг неправильну кількість чернеток.");
        }

        var rolledBack = await jobService.RollbackPlanAsync(
            started.JobId,
            new AutoGenPlanActionRequest(applied.Summary.Version));
        Assert.True(
            rolledBack.Summary.State == AutoGenPlanState.RolledBack,
            $"Seed {seed}: план не перейшов у RolledBack.");
        await using (var rollbackCheck = new AppDbContext(database.Options))
        {
            Assert.True(
                await rollbackCheck.TeacherDraftItems.AsNoTracking().CountAsync() == 0,
                $"Seed {seed}: Rollback не відновив початковий стан.");
        }
        await jobService.StopAsync(CancellationToken.None);
    }

    private static async Task<AutoGenJobStatus> WaitForTerminalStatusAsync(
        TeacherDraftsAutogenJobService service,
        string jobId,
        int seed)
    {
        for (var attempt = 0; attempt < 1_200; attempt++)
        {
            var status = service.Get(jobId);
            if (status?.State is not (AutoGenJobState.Queued or AutoGenJobState.Running))
            {
                return Assert.IsType<AutoGenJobStatus>(status);
            }
            await Task.Delay(25);
        }
        throw new TimeoutException($"Seed {seed}: job {jobId} не завершився за 30 секунд.");
    }

    private static void AssertNoOverlaps(IReadOnlyList<TeacherDraftItem> rows, int seed)
    {
        AssertNoScopeOverlaps(rows.GroupBy(row => (row.Date, Scope: $"group:{row.GroupId}")), seed);
        var logicalEvents = rows
            .GroupBy(row => new
            {
                row.Date,
                row.StartTime,
                row.EndTime,
                row.ModuleId,
                row.ModuleTopicId,
                row.LessonTypeId,
                row.TeacherId,
                row.RoomId
            })
            .Select(group => group.First())
            .ToList();
        AssertNoScopeOverlaps(
            logicalEvents.Where(row => row.TeacherId is not null)
                .GroupBy(row => (row.Date, Scope: $"teacher:{row.TeacherId}")),
            seed);
        AssertNoScopeOverlaps(
            logicalEvents.Where(row => row.RoomId is not null)
                .GroupBy(row => (row.Date, Scope: $"room:{row.RoomId}")),
            seed);
    }

    private static void AssertNoScopeOverlaps(
        IEnumerable<IGrouping<(DateOnly Date, string Scope), TeacherDraftItem>> scopes,
        int seed)
    {
        foreach (var scope in scopes)
        {
            var ordered = scope.OrderBy(row => row.StartTime).ThenBy(row => row.EndTime).ToList();
            for (var index = 1; index < ordered.Count; index++)
            {
                Assert.True(
                    ordered[index - 1].EndTime <= ordered[index].StartTime,
                    $"Seed {seed}: перетин {scope.Key.Scope} на {scope.Key.Date:yyyy-MM-dd}: " +
                    $"{ordered[index - 1].StartTime:HH\\:mm}-{ordered[index - 1].EndTime:HH\\:mm} і " +
                    $"{ordered[index].StartTime:HH\\:mm}-{ordered[index].EndTime:HH\\:mm}.");
            }
        }
    }

    private static void AssertNoErrorWarnings(AutoGenResult result, int seed)
    {
        var errors = (result.WarningDetails ?? [])
            .Where(detail => string.Equals(
                detail.Severity,
                AutoGenWarningSeverities.Error,
                StringComparison.OrdinalIgnoreCase))
            .Select(detail => $"{detail.Code}: {detail.Message}")
            .ToList();
        Assert.True(
            errors.Count == 0,
            $"Seed {seed}: генератор повернув попередження рівня error: {string.Join(" | ", errors)}");
    }

    private static AutoGenResult ExtractResult(ActionResult<AutoGenResult> action, int seed)
    {
        if (action.Result is not OkObjectResult ok)
        {
            var detail = action.Result is ObjectResult { Value: AutoGenResult failedResult }
                ? string.Join(
                    " | ",
                    failedResult.Warnings
                        .Where(warning => warning.Contains("repair", StringComparison.OrdinalIgnoreCase)
                                          || warning.Contains("перен", StringComparison.OrdinalIgnoreCase)
                                          || warning.Contains("Фінальна", StringComparison.OrdinalIgnoreCase))
                        .Concat(failedResult.Warnings.TakeLast(12))
                        .Distinct()
                        .TakeLast(30))
                : action.Result is ObjectResult objectResult
                    ? JsonSerializer.Serialize(objectResult.Value)
                    : action.Result?.ToString() ?? "null";
            throw new InvalidOperationException(
                $"Seed {seed}: генератор повернув {action.Result?.GetType().Name ?? "null"}: {detail}");
        }
        return Assert.IsType<AutoGenResult>(ok.Value);
    }

    private static string BuildFingerprint(IReadOnlyList<TeacherDraftItem> rows)
    {
        var text = string.Join(
            '\n',
            rows.Select(row => string.Join(
                '|',
                row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                row.StartTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                row.EndTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                row.GroupId,
                row.ModuleId,
                row.ModuleTopicId,
                row.LessonTypeId,
                row.TeacherId,
                row.RoomId,
                row.IsLocked,
                row.IsSelfStudy)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static string BuildShapeFingerprint(
        IReadOnlyList<TeacherDraftItem> rows,
        StressScenario scenario)
    {
        var groups = scenario.GroupIds.OrderBy(id => id).ToArray();
        var modules = scenario.ModuleHours.Keys.OrderBy(id => id).ToArray();
        var text = string.Join(
            '\n',
            rows.Select(row =>
            {
                var groupIndex = Array.IndexOf(groups, row.GroupId);
                var moduleIndex = Array.IndexOf(modules, row.ModuleId);
                var topicIndex = row.ModuleTopicId is int topicId
                    ? topicId - scenario.CourseId - 10_000 - moduleIndex * 200
                    : -1;
                return string.Join(
                    '|',
                    row.Date.DayNumber - scenario.RangeStart.DayNumber,
                    row.StartTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                    row.EndTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                    groupIndex,
                    moduleIndex,
                    topicIndex,
                    row.LessonTypeId - scenario.CourseId - 400,
                    row.TeacherId - scenario.CourseId,
                    row.RoomId - scenario.CourseId,
                    row.IsLocked,
                    row.IsSelfStudy);
            }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static string DescribeGaps(IReadOnlyCollection<AutoGenGapDetail>? gaps)
        => gaps is null || gaps.Count == 0
            ? "немає"
            : string.Join(
                " | ",
                gaps.Take(5).Select(gap =>
                    $"{gap.GroupName} {gap.Date:yyyy-MM-dd} {gap.SlotLabel}: " +
                    $"{gap.ReasonCode ?? gap.ConstraintCode ?? gap.Reason}"));

    private static string DescribeRows(IReadOnlyCollection<TeacherDraftItem> rows, int maximumRows = 20)
        => rows.Count == 0
            ? "немає"
            : string.Join(
                " | ",
                rows.Take(maximumRows).Select(row =>
                    $"g{row.GroupId}/m{row.ModuleId} {row.Date:yyyy-MM-dd} " +
                    $"{row.StartTime:HH\\:mm} t{row.TeacherId} r{row.RoomId} topic{row.ModuleTopicId}"));

    private static string DescribeScenario(StressScenario scenario)
        => $"scale={scenario.Scale}, weeks={scenario.WeekCount}, days={scenario.DayCount}, " +
           $"slots={scenario.SlotCount}, groups={scenario.GroupIds.Count}, " +
           $"hours=[{string.Join(",", scenario.ModuleHours.OrderBy(entry => entry.Key).Select(entry => entry.Value))}], " +
           $"parallel={scenario.ParallelSequence}, filler={scenario.HasFiller}, " +
           $"calendar={scenario.CalendarExceptions.Count}";

    private static string DescribeQuotaDiff(
        IReadOnlyCollection<TeacherDraftItem> rows,
        StressScenario scenario)
        => string.Join(
            ", ",
            scenario.GroupIds.OrderBy(id => id).SelectMany(groupId =>
                scenario.ModuleHours.OrderBy(entry => entry.Key).Select(entry =>
                    $"g{Array.IndexOf(scenario.GroupIds.OrderBy(id => id).ToArray(), groupId) + 1}/" +
                    $"m{Array.IndexOf(scenario.ModuleHours.Keys.OrderBy(id => id).ToArray(), entry.Key) + 1}=" +
                    $"{rows.Count(row => row.GroupId == groupId && row.ModuleId == entry.Key)}/{entry.Value}")));

    private static int ReadBoundedCount(string variable, int fallback, int minimum, int maximum)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value < minimum
            || value > maximum)
        {
            throw new InvalidOperationException(
                $"Змінна {variable} повинна бути цілим числом у межах {minimum}..{maximum}.");
        }
        return value;
    }

    private static AutoGenSoftOptionsDto? MapSoftOptions(DraftAutoGenSoftOptions? options)
        => options is null
            ? null
            : new AutoGenSoftOptionsDto(
                options.MaxParallelGroupsPerModuleInSlot,
                options.RecentRepeatWindowDays,
                options.PreferredMaxDistinctModulesPerDay,
                options.MaxDistinctModulesPerDay,
                options.PreferredFirstPenaltyMultiplier,
                options.AdjacentRoomChangePenalty,
                options.TeacherLoadPenaltyWeight,
                options.BuildingDistancePenaltyWeight);

    private sealed record StressSummary(int ScenarioCount, int TotalCreated, TimeSpan Runtime);

    private sealed record StressSnapshot(
        int Created,
        string Fingerprint,
        string ShapeFingerprint);

    private enum StressScale
    {
        Compact,
        Medium,
        Large
    }

    private enum StressShortageKind
    {
        Teacher = 0,
        Room = 1,
        Slot = 2,
        TeacherHours = 3,
        RoomCapacity = 4,
        Calendar = 5
    }

    private sealed record StressCalendarException(
        DateOnly Date,
        bool IsWorkingDay,
        int? GroupId);

    private sealed record StressScenario(
        int CourseId,
        IReadOnlyList<int> GroupIds,
        IReadOnlyDictionary<int, int> ModuleHours,
        StressScale Scale,
        int WeekCount,
        int DayCount,
        int SlotCount,
        DateOnly RangeStart,
        DateOnly RangeEnd,
        DraftAutoGenRequest Request,
        int LockedRowCount,
        IReadOnlyList<StressCalendarException> CalendarExceptions,
        bool ParallelSequence,
        bool HasFiller,
        StressShortageKind? Shortage)
    {
        private static readonly DateOnly[] BoundaryMondays =
        [
            new(2028, 2, 28),
            new(2029, 12, 31),
            new(2030, 2, 4)
        ];

        public static StressScenario CreateFeasible(
            int seed,
            bool allowLockedSeed = true,
            StressScale? forcedScale = null)
            => Create(seed, shortage: null, allowLockedSeed, forcedScale);

        public static StressScenario CreateDeficit(int seed, StressShortageKind shortage)
            => Create(seed, shortage, allowLockedSeed: false, forcedScale: StressScale.Compact);

        public StressScenario RemapIdentifiers(int delta)
        {
            var courseId = checked(CourseId + delta);
            var groupIds = GroupIds.Select(id => checked(id + delta)).ToArray();
            var moduleHours = ModuleHours.ToDictionary(
                entry => checked(entry.Key + delta),
                entry => entry.Value);
            var groupMap = GroupIds.Zip(groupIds).ToDictionary(pair => pair.First, pair => pair.Second);
            var preferences = Request.GroupRoomPreferences?
                .Select(preference => new GroupRoomPreferenceDto(
                    groupMap[preference.GroupId],
                    preference.BuildingId is int buildingId ? checked(buildingId + delta) : null,
                    preference.RoomIds?.Select(roomId => checked(roomId + delta)).ToList()))
                .ToList();
            var calendar = CalendarExceptions
                .Select(exception => exception with
                {
                    GroupId = exception.GroupId is int groupId ? groupMap[groupId] : null
                })
                .ToList();
            var request = Request with
            {
                CourseId = courseId,
                GroupIds = groupIds.ToList(),
                ModuleHours = moduleHours,
                GroupRoomPreferences = preferences
            };
            return this with
            {
                CourseId = courseId,
                GroupIds = groupIds,
                ModuleHours = moduleHours,
                Request = request,
                CalendarExceptions = calendar
            };
        }

        private static StressScenario Create(
            int seed,
            StressShortageKind? shortage,
            bool allowLockedSeed,
            StressScale? forcedScale)
        {
            var random = new StableRandom(unchecked((uint)seed));
            var courseId = 100_000 + seed * 100;
            var scale = forcedScale
                        ?? (seed % 97 == 0
                            ? StressScale.Large
                            : seed % 17 == 0
                                ? StressScale.Medium
                                : StressScale.Compact);
            var groupCount = scale switch
            {
                StressScale.Compact => 1 + random.Next(3),
                StressScale.Medium => 4 + random.Next(5),
                _ => 10 + random.Next(21)
            };
            var weekCount = scale switch
            {
                StressScale.Compact => 1,
                StressScale.Medium => 2 + random.Next(5),
                _ => 8 + random.Next(11)
            };
            var dayCount = scale switch
            {
                StressScale.Compact => 1 + random.Next(5),
                StressScale.Medium => 3 + random.Next(4),
                _ => 5 + random.Next(3)
            };
            var slotCount = scale switch
            {
                StressScale.Compact => 1 + random.Next(4),
                StressScale.Medium => 4 + random.Next(5),
                _ => 6 + random.Next(7)
            };
            var rangeStart = BoundaryMondays[random.Next(BoundaryMondays.Length)];
            var rangeEnd = rangeStart.AddDays((weekCount - 1) * 7 + dayCount - 1);
            var calendarExceptions = new List<StressCalendarException>();
            if (shortage == StressShortageKind.Calendar)
            {
                for (var week = 0; week < weekCount; week++)
                {
                    for (var day = 0; day < dayCount; day++)
                    {
                        calendarExceptions.Add(new StressCalendarException(
                            rangeStart.AddDays(week * 7 + day),
                            IsWorkingDay: false,
                            GroupId: null));
                    }
                }
            }
            else if (shortage is null && scale is not StressScale.Compact && seed % 4 == 0)
            {
                var exceptionDate = rangeStart.AddDays(random.Next(weekCount) * 7 + random.Next(dayCount));
                calendarExceptions.Add(new StressCalendarException(
                    exceptionDate,
                    IsWorkingDay: false,
                    GroupId: seed % 8 == 0 ? groupCount - 1 : null));
            }
            var unavailableCells = calendarExceptions.Count == 0 ? 0 : slotCount;
            var availableCells = Math.Max(1, weekCount * dayCount * slotCount - unavailableCells);
            var requestedModuleCount = scale switch
            {
                StressScale.Compact => 1 + random.Next(3),
                StressScale.Medium => 4 + random.Next(5),
                _ => 8 + random.Next(13)
            };
            var moduleCount = Math.Min(requestedModuleCount, availableCells);
            var hoursMultiplier = scale switch
            {
                StressScale.Compact => 3,
                StressScale.Medium => 5,
                _ => 6
            };
            var maximumDemand = Math.Min(availableCells, moduleCount * hoursMultiplier);
            var totalDemand = moduleCount + random.Next(maximumDemand - moduleCount + 1);
            var hours = Enumerable.Repeat(1, moduleCount).ToArray();
            for (var remaining = totalDemand - moduleCount; remaining > 0; remaining--)
            {
                hours[random.Next(moduleCount)]++;
            }
            var groupIds = Enumerable.Range(0, groupCount)
                .Select(index => courseId + 10 + index)
                .ToArray();
            var moduleIds = Enumerable.Range(0, moduleCount)
                .Select(index => courseId + 30 + index)
                .ToArray();
            var moduleHours = moduleIds
                .Select((moduleId, index) => (moduleId, Hours: hours[index]))
                .ToDictionary(entry => entry.moduleId, entry => entry.Hours);
            var useLockedSeed = shortage is null
                                && allowLockedSeed
                                && seed % 7 == 0
                                && moduleHours[moduleIds[0]] > 0;
            var lockedRows = useLockedSeed ? groupCount : 0;
            var roomIds = groupIds.Select((_, index) => courseId + 300 + index).ToArray();
            var preferences = groupIds
                .Select((groupId, index) => new GroupRoomPreferenceDto(
                    groupId,
                    BuildingId: courseId + 200 + index % 2,
                    RoomIds: [roomIds[index]]))
                .ToList();
            var request = new DraftAutoGenRequest(
                WeekStart: rangeStart,
                ClearExisting: !useLockedSeed,
                CourseId: courseId,
                GroupIds: groupIds.ToList(),
                AllowOnDaysOff: false,
                Days: dayCount <= 5
                    ? WeekPreset.MonFri
                    : dayCount == 6
                        ? WeekPreset.MonSat
                        : WeekPreset.MonSun,
                ModuleHours: moduleHours,
                SoftFill: true,
                AllowIncompleteDrafts: false,
                RangeStartDate: rangeStart,
                RangeEndDate: rangeEnd,
                PreferredFirstMaxSlotOrderOverride: slotCount,
                GroupRoomPreferences: preferences,
                SoftOptions: new DraftAutoGenSoftOptions(
                    MaxParallelGroupsPerModuleInSlot: groupCount,
                    RecentRepeatWindowDays: 0,
                    PreferredMaxDistinctModulesPerDay: moduleCount,
                    MaxDistinctModulesPerDay: moduleCount,
                    PreferredFirstPenaltyMultiplier: seed % 2 == 0 ? 0.35 : 0.0,
                    AdjacentRoomChangePenalty: seed % 3 == 0 ? 4.0 : 0.0,
                    TeacherLoadPenaltyWeight: seed % 5 == 0 ? 0.0 : 1.0,
                    BuildingDistancePenaltyWeight: seed % 11 == 0 ? 0.0 : 1.0),
                PreflightOnly: shortage is not null);
            return new StressScenario(
                courseId,
                groupIds,
                moduleHours,
                scale,
                weekCount,
                dayCount,
                slotCount,
                rangeStart,
                rangeEnd,
                request,
                lockedRows,
                calendarExceptions.Select(exception => exception.GroupId == groupCount - 1
                    ? exception with { GroupId = groupIds[^1] }
                    : exception).ToList(),
                ParallelSequence: shortage is null && scale is not StressScale.Compact && seed % 3 == 0,
                HasFiller: shortage is null && moduleCount > 1 && seed % 5 == 0,
                shortage);
        }
    }

    private sealed class StressDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _anchor;

        private StressDatabase(
            SqliteConnection anchor,
            DbContextOptions<AppDbContext> options,
            AppDbContext db,
            StressScenario scenario)
        {
            _anchor = anchor;
            Options = options;
            Db = db;
            Scenario = scenario;
        }

        public DbContextOptions<AppDbContext> Options { get; }

        public AppDbContext Db { get; }

        public StressScenario Scenario { get; }

        public static async Task<StressDatabase> CreateAsync(
            StressScenario scenario,
            bool reverseInputOrder,
            bool addUnusedResources = false)
        {
            if (reverseInputOrder)
            {
                scenario = scenario with
                {
                    Request = scenario.Request with
                    {
                        GroupIds = scenario.Request.GroupIds!.AsEnumerable().Reverse().ToList(),
                        GroupRoomPreferences = scenario.Request.GroupRoomPreferences!
                            .AsEnumerable()
                            .Reverse()
                            .ToList(),
                        ModuleHours = scenario.Request.ModuleHours!
                            .AsEnumerable()
                            .Reverse()
                            .ToDictionary(entry => entry.Key, entry => entry.Value)
                    }
                };
            }
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = $"autogen-stress-{Guid.NewGuid():N}",
                Mode = SqliteOpenMode.Memory,
                Cache = SqliteCacheMode.Shared
            }.ToString();
            var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connectionString)
                .Options;
            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var database = new StressDatabase(anchor, options, db, scenario);
            await database.SeedAsync(reverseInputOrder, addUnusedResources);
            return database;
        }

        private async Task SeedAsync(bool reverseInputOrder, bool addUnusedResources)
        {
            var scenario = Scenario;
            var stableGroupIds = scenario.GroupIds.OrderBy(id => id).ToArray();
            var groupIds = Order(stableGroupIds, reverseInputOrder).ToArray();
            var stableModuleIds = scenario.ModuleHours.Keys.OrderBy(id => id).ToArray();
            var moduleHours = Order(
                    stableModuleIds.Select(moduleId =>
                        new KeyValuePair<int, int>(moduleId, scenario.ModuleHours[moduleId])),
                    reverseInputOrder)
                .ToArray();
            var buildingIds = new[] { scenario.CourseId + 200, scenario.CourseId + 201 };
            var roomIds = scenario.GroupIds
                .Select((_, index) => scenario.CourseId + 300 + index)
                .ToArray();
            var lessonTypeIds = new[] { scenario.CourseId + 400, scenario.CourseId + 401 };
            var dayCount = scenario.DayCount;
            var slotCount = scenario.SlotCount;
            var starts = Enumerable.Range(0, slotCount)
                .Select(index => new TimeOnly(8, 0).AddMinutes(index * 70))
                .ToArray();

            Db.Courses.Add(new Course
            {
                Id = scenario.CourseId,
                Name = $"Синтетичний stress-курс {scenario.CourseId}",
                DurationWeeks = scenario.WeekCount,
                AcademicPeriodStartDate = scenario.RangeStart
            });
            Db.Groups.AddRange(groupIds.Select(groupId => new Group
            {
                Id = groupId,
                CourseId = scenario.CourseId,
                Name = $"STRESS-{Array.IndexOf(stableGroupIds, groupId) + 1}",
                StudentsCount = 18 + Array.IndexOf(stableGroupIds, groupId)
            }));
            Db.LessonTypes.AddRange(
                new LessonTypeRef
                {
                    Id = lessonTypeIds[0],
                    Code = "PRACTICE",
                    Name = "Практичне заняття",
                    IsActive = true,
                    RequiresRoom = true,
                    RequiresTeacher = true,
                    BlocksRoom = true,
                    BlocksTeacher = true,
                    CountInPlan = true,
                    CountInLoad = true
                },
                new LessonTypeRef
                {
                    Id = lessonTypeIds[1],
                    Code = "LECTURE",
                    Name = "Лекція",
                    IsActive = true,
                    RequiresRoom = true,
                    RequiresTeacher = true,
                    BlocksRoom = true,
                    BlocksTeacher = true,
                    CountInPlan = true,
                    CountInLoad = true,
                    PreferredFirstInWeek = true
                });
            Db.Buildings.AddRange(buildingIds.Select((id, index) => new Building
            {
                Id = id,
                Name = $"Stress-корпус {index + 1}"
            }));
            Db.BuildingTravels.AddRange(
                new BuildingTravel
                {
                    FromBuildingId = buildingIds[0],
                    ToBuildingId = buildingIds[1],
                    Minutes = 10
                },
                new BuildingTravel
                {
                    FromBuildingId = buildingIds[1],
                    ToBuildingId = buildingIds[0],
                    Minutes = 10
                });

            Db.CalendarExceptions.AddRange(scenario.CalendarExceptions.Select((exception, index) =>
                new CalendarException
                {
                    Id = scenario.CourseId + 5_000 + index,
                    Date = exception.Date,
                    IsWorkingDay = exception.IsWorkingDay,
                    Name = $"Stress-виняток {index + 1}",
                    CourseId = exception.GroupId is null ? scenario.CourseId : null,
                    GroupId = exception.GroupId
                }));

            if (scenario.Shortage is not StressShortageKind.Room)
            {
                Db.Rooms.AddRange(roomIds.Select((roomId, index) => new Room
                {
                    Id = roomId,
                    Name = $"STRESS-R-{index + 1}",
                    Capacity = scenario.Shortage == StressShortageKind.RoomCapacity ? 1 : 2_000,
                    BuildingId = buildingIds[index % buildingIds.Length]
                }));
            }

            var teacherIds = new List<int>();
            var topicIds = new Dictionary<int, List<int>>();
            for (var moduleIndex = 0; moduleIndex < moduleHours.Length; moduleIndex++)
            {
                var entry = moduleHours[moduleIndex];
                var stableModuleIndex = Array.IndexOf(stableModuleIds, entry.Key);
                Db.Modules.Add(new BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities.Module
                {
                    Id = entry.Key,
                    CourseId = scenario.CourseId,
                    Code = $"STRESS-M{stableModuleIndex + 1}",
                    Title = $"Stress-модуль {stableModuleIndex + 1}",
                    Credits = 1
                });
                Db.ModulePlans.Add(new ModulePlan
                {
                    CourseId = scenario.CourseId,
                    ModuleId = entry.Key,
                    TargetHours = entry.Value,
                    ScheduledHours = 0,
                    IsActive = true
                });
                if (scenario.Shortage is not StressShortageKind.Room)
                {
                    Db.ModuleRooms.AddRange(roomIds.Select(roomId => new ModuleRoom
                    {
                        ModuleId = entry.Key,
                        RoomId = roomId
                    }));
                }
                var moduleTopicIds = new List<int>();
                for (var topicIndex = 0; topicIndex < entry.Value; topicIndex++)
                {
                    var topicId = scenario.CourseId + 10_000 + stableModuleIndex * 200 + topicIndex;
                    moduleTopicIds.Add(topicId);
                    Db.ModuleTopics.Add(new ModuleTopic
                    {
                        Id = topicId,
                        ModuleId = entry.Key,
                        Order = topicIndex + 1,
                        TopicCode = $"STRESS-M{stableModuleIndex + 1}.{topicIndex + 1}",
                        LessonTypeId = scenario.LockedRowCount == 0
                                       && topicIndex == 0
                                       && (scenario.CourseId + stableModuleIndex) % 4 == 0
                            ? lessonTypeIds[1]
                            : lessonTypeIds[0],
                        TotalHours = 1,
                        AuditoriumHours = 1
                    });
                }
                topicIds[entry.Key] = moduleTopicIds;

                if (scenario.Shortage is not StressShortageKind.Teacher)
                {
                    for (var groupIndex = 0; groupIndex < scenario.GroupIds.Count; groupIndex++)
                    {
                        var teacherId = scenario.CourseId + 100_000 + stableModuleIndex * 100 + groupIndex;
                        teacherIds.Add(teacherId);
                        Db.Teachers.Add(new Teacher
                        {
                            Id = teacherId,
                            FullName = $"Stress-викладач {stableModuleIndex + 1}.{groupIndex + 1}"
                        });
                        Db.TeacherModules.Add(new TeacherModule
                        {
                            TeacherId = teacherId,
                            ModuleId = entry.Key
                        });
                    }
                }
            }

            for (var moduleIndex = 0; moduleIndex < stableModuleIds.Length; moduleIndex++)
            {
                Db.ModuleSequenceItems.Add(new ModuleSequenceItem
                {
                    CourseId = scenario.CourseId,
                    ModuleId = stableModuleIds[moduleIndex],
                    Order = moduleIndex + 1,
                    GroupOrder = scenario.ParallelSequence ? moduleIndex / 2 + 1 : moduleIndex + 1
                });
            }
            if (scenario.HasFiller)
            {
                Db.ModuleFillers.Add(new ModuleFiller
                {
                    CourseId = scenario.CourseId,
                    ModuleId = stableModuleIds[^1]
                });
            }

            if (scenario.Shortage is not StressShortageKind.Slot)
            {
                var slotId = scenario.CourseId + 3_000;
                for (var dayIndex = 0; dayIndex < dayCount; dayIndex++)
                {
                    for (var slotIndex = 0; slotIndex < slotCount; slotIndex++)
                    {
                        Db.TimeSlots.Add(new TimeSlot
                        {
                            Id = slotId++,
                            CourseId = scenario.CourseId,
                            DayOfWeek = (DayOfWeek)((int)DayOfWeek.Monday + dayIndex),
                            Start = starts[slotIndex],
                            End = starts[slotIndex].AddHours(1),
                            SortOrder = slotIndex + 1,
                            IsActive = true
                        });
                    }
                }
            }

            if (scenario.Shortage is not StressShortageKind.TeacherHours)
            {
                foreach (var teacherId in teacherIds)
                {
                    for (var dayIndex = 0; dayIndex < dayCount; dayIndex++)
                    {
                        Db.TeacherWorkingHours.Add(new TeacherWorkingHour
                        {
                            TeacherId = teacherId,
                            DayOfWeek = (DayOfWeek)((int)DayOfWeek.Monday + dayIndex),
                            Start = starts[0],
                            End = starts[^1].AddHours(1)
                        });
                    }
                }
            }

            if (scenario.LockedRowCount > 0)
            {
                var firstModule = stableModuleIds[0];
                for (var groupIndex = 0; groupIndex < scenario.GroupIds.Count; groupIndex++)
                {
                    Db.TeacherDraftItems.Add(new TeacherDraftItem
                    {
                        Date = scenario.RangeStart,
                        DayOfWeek = scenario.RangeStart.DayOfWeek,
                        StartTime = starts[0],
                        EndTime = starts[0].AddHours(1),
                        LessonTypeId = lessonTypeIds[0],
                        GroupId = scenario.GroupIds[groupIndex],
                        ModuleId = firstModule,
                        ModuleTopicId = topicIds[firstModule][0],
                        TeacherId = scenario.CourseId + 100_000 + groupIndex,
                        RoomId = roomIds[groupIndex],
                        Status = DraftStatus.Draft,
                        BatchKey = $"stress-locked-{scenario.CourseId}-{groupIndex}",
                        IsLocked = true
                    });
                }
            }

            if (addUnusedResources)
            {
                var unusedBuildingId = scenario.CourseId + 150;
                Db.Buildings.Add(new Building
                {
                    Id = unusedBuildingId,
                    Name = "Невикористаний stress-корпус"
                });
                Db.Rooms.Add(new Room
                {
                    Id = scenario.CourseId + 151,
                    Name = "Невикористана stress-аудиторія",
                    Capacity = 10_000,
                    BuildingId = unusedBuildingId
                });
                Db.Teachers.Add(new Teacher
                {
                    Id = scenario.CourseId + 152,
                    FullName = "Невикористаний stress-викладач"
                });
            }

            await Db.SaveChangesAsync();
        }

        private static IEnumerable<T> Order<T>(IEnumerable<T> source, bool reverse)
            => reverse ? source.Reverse() : source;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _anchor.DisposeAsync();
        }
    }

    private sealed class StableRandom
    {
        private uint _state;

        public StableRandom(uint seed)
        {
            _state = seed == 0 ? 0x9E3779B9u : seed;
        }

        public int Next(int exclusiveMaximum)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exclusiveMaximum);
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return (int)(_state % (uint)exclusiveMaximum);
        }
    }
}

internal sealed class AutogenStressFactAttribute : FactAttribute
{
    private const string RunAutogenStressEnvFlag = "RUN_AUTOGEN_STRESS";

    public AutogenStressFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(RunAutogenStressEnvFlag),
                "1",
                StringComparison.Ordinal))
        {
            Skip = $"Стрес-сценарії автогенерації вимкнено. Щоб запустити, встановіть {RunAutogenStressEnvFlag}=1.";
        }
    }
}
