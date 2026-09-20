using System.Diagnostics;
using BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class AutogenL3SeptemberFirstWeekReproductionTests(ITestOutputHelper output)
{
    [Fact(Timeout = 780_000)]
    public async Task L3_20260901_20260905_preview_places_all_294_requested_lessons()
    {
        // Локальний opt-in сценарій: джерело лише читаємо, пошук працює у тимчасовій SQLite.
        string[] groupNames = ["9301", "9302", "9303", "9304", "9305", "9306", "9307"];
        var hoursByCode = new Dictionary<string, int>
        {
            ["1"] = 7, ["2"] = 7, ["3"] = 4, ["4"] = 10, ["5"] = 2,
            ["6"] = 5, ["8"] = 3, ["10"] = 2, ["13"] = 2
        };
        await using var source = ServerConfigurationFactory.CreateSourceContext();
        var course = await source.Courses.AsNoTracking().SingleAsync(c => c.Name == "L-3");
        var groups = await source.Groups.AsNoTracking()
            .Where(g => g.CourseId == course.Id && groupNames.Contains(g.Name))
            .OrderBy(g => g.Name).Select(g => new { g.Id, g.Name }).ToListAsync();
        Assert.Equal(groupNames, groups.Select(g => g.Name));
        var modules = await source.Modules.AsNoTracking()
            .Where(m => m.CourseId == course.Id || m.ModuleCourses.Any(c => c.CourseId == course.Id))
            .Select(m => new { m.Id, m.Code }).ToListAsync();
        var hours = hoursByCode.ToDictionary(
            entry => modules.Single(m => m.Code.Trim() == entry.Key).Id, entry => entry.Value);
        var codes = modules.ToDictionary(m => m.Id, m => m.Code);
        var names = groups.ToDictionary(g => g.Id, g => g.Name);
        var request = new AutoGenJobRequest(
            Kind: AutoGenJobKind.Generate,
            FromDate: new DateOnly(2026, 9, 1), ToDate: new DateOnly(2026, 9, 5),
            CourseId: course.Id, GroupIds: groups.Select(g => g.Id).ToList(),
            ModuleHours: hours, Days: WeekPreset.MonSat,
            ClearExisting: true, SoftFill: false, PreflightOnly: false,
            AllowIncompleteDrafts: true,
            SoftOptions: AutoGenRecommendedProfile.CreateSoftOptions(),
            PreferredFirstMaxSlotOrderOverride: AutoGenRecommendedProfile.PreferredFirstMaxSlotOrderOverride,
            PreviewOnly: true);

        await using var snapshot = await SqliteSnapshotFile.CreateFromSourceAsync(source, course.Id);
        await using var database = new SqliteTempDatabase(snapshot.Path);
        await using var db = database.CreateContext();
        var capacity = await AutogenCalendarWorkload.MeasureAsync(db, request);
        output.WriteLine($"capacity={capacity.CalendarSlots}; requested={groups.Count * hours.Values.Sum()}");
        Assert.True(capacity.CalendarSlots == 294,
            $"Поточна сітка відрізняється від скриншота: {capacity.CalendarSlots} замість 294 слотів.");
        Assert.Equal(294, groups.Count * hours.Values.Sum());
        Assert.Equal(0, await db.TeacherDraftItems.CountAsync());
        Assert.Equal(0, await db.ScheduleItems.CountAsync());

        var services = new ServiceCollection();
        services.AddScoped(_ => database.CreateContext());
        services.AddScoped<TeacherDraftsAutogenService>();
        services.AddScoped<TeacherDraftsAutogenPlanService>();
        await using var provider = services.BuildServiceProvider();
        var jobs = new TeacherDraftsAutogenJobService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TeacherDraftsAutogenJobService>.Instance);
        var elapsed = Stopwatch.StartNew();
        try
        {
            // Той самий preview-процес, що в UI: спільний бюджет генерації та дозаповнення.
            var started = jobs.Start(request);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
            AutoGenJobStatus? status;
            do
            {
                await Task.Delay(500, timeout.Token);
                status = await jobs.GetAsync(started.JobId);
            } while (status?.State is AutoGenJobState.Queued or AutoGenJobState.Running);
            Assert.True(status?.State == AutoGenJobState.Succeeded,
                $"Генерація завершилася зі станом {status?.State}; elapsed={elapsed.Elapsed.TotalSeconds:F1}s.");
            var result = Assert.IsType<AutoGenResult>(status!.Result);
            var coverage = Assert.IsType<AutoGenCoverageDto>(result.Coverage);
            var summary = $"created={result.Created}; missing={coverage.MissingLessons}; " +
                $"occupied={coverage.OccupiedSlots}/{coverage.AvailableSlots}; empty={coverage.EmptySlots}; " +
                $"windows={coverage.InternalGapSlots}; incomplete={coverage.IncompleteLessons}; " +
                $"searchLimit={coverage.SearchLimitReached}; warnings={result.Warnings.Count}; " +
                $"elapsed={elapsed.Elapsed.TotalSeconds:F1}s";
            output.WriteLine(summary);
            foreach (var gap in result.GapDetails ?? [])
                output.WriteLine($"gap: group={gap.GroupName}; date={gap.Date:yyyy-MM-dd}; " +
                    $"start={gap.Start}; module={(gap.ModuleId is int id ? codes.GetValueOrDefault(id) : "?")}; " +
                    $"reason={gap.ReasonCode}; constraint={gap.ConstraintCode}");
            foreach (var group in coverage.Groups)
                output.WriteLine($"group={names[group.GroupId]}; occupied={group.OccupiedSlots}; " +
                    $"missing={group.MissingLessons}; empty={group.EmptySlots}; windows={group.InternalGapSlots}");

            // Preview має лишити робочі таблиці порожніми навіть за часткового результату.
            Assert.Equal(0, await db.TeacherDraftItems.AsNoTracking().CountAsync());
            Assert.Equal(0, await db.ScheduleItems.AsNoTracking().CountAsync());
            var plan = await jobs.GetPlanAsync(started.JobId);
            Assert.Equal(result.Created, plan.Summary.AddCount);
            Assert.Equal(0, plan.Summary.DeleteCount);
            Assert.Equal(0, plan.Summary.UpdateCount);
            Assert.Equal(0, coverage.OverplannedLessons);
            Assert.Equal(0, coverage.IncompleteLessons);
            // Неповний результат має впасти, а не закріпити 292 як правильну відповідь.
            Assert.True(coverage.OccupiedSlots == 294 && coverage.MissingLessons == 0
                && coverage.EmptySlots == 0 && coverage.InternalGapSlots == 0, summary);

            // Застосування та повторна перевірка відбуваються лише у тимчасовій копії.
            var applied = await jobs.ApplyPlanAsync(started.JobId, new(plan.Summary.Version));
            db.ChangeTracker.Clear();
            var validation = await new TeacherDraftsAutogenHardRuleValidator(db).ValidateAsync(new(
                course.Id, request.GroupIds, request.FromDate, request.ToDate, request.Days,
                AllowIncompleteDrafts: false,
                MaxParallelGroupsPerModuleInSlot: AutoGenRecommendedProfile.MaxParallelGroupsPerModuleInSlot));
            Assert.False(validation.HasViolations, $"Порушень жорстких правил: {validation.Violations.Count}.");
            var persistedCoverage = await new TeacherDraftsAutogenCoverageService(db).MeasureAsync(
                request.GroupIds, request.FromDate, request.ToDate, request.Days, hours, false);
            Assert.Equal(AutoGenCoverageStatus.FullyOccupied, persistedCoverage.Status);
            Assert.Equal(294, persistedCoverage.OccupiedSlots);
            Assert.Equal(0, persistedCoverage.InternalGapSlots);
            Assert.All(persistedCoverage.Groups, group => Assert.Equal(42, group.OccupiedSlots));
            await jobs.RollbackPlanAsync(started.JobId, new(applied.Summary.Version));
            Assert.Equal(0, await db.TeacherDraftItems.AsNoTracking().CountAsync());
            output.WriteLine("apply/validation/rollback: passed; hard-rule violations=0");
        }
        finally
        {
            await jobs.StopAsync(CancellationToken.None);
        }
    }
}
