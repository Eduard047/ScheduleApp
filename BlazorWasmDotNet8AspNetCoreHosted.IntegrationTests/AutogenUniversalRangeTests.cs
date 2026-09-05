using System.Diagnostics;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

[Collection("Autogen performance")]
public sealed class AutogenUniversalRangeTests
{
    public static IEnumerable<object[]> Ranges()
    {
        var random = new Random(9307);
        for (var i = 0; i < 18; i++)
            yield return new object[] { random.Next(0, 800), random.Next(7, 51), (WeekPreset)(i % 3), 1 + i % 3, 2 };
        yield return new object[] { 361, 366, WeekPreset.MonFri, 2, 2 };
        yield return new object[] { 17, 733, WeekPreset.MonSat, 1, 1 };
        yield return new object[] { 91, 520 * 7, WeekPreset.MonFri, 1, 1 };
    }

    [Theory]
    [MemberData(nameof(Ranges))]
    public async Task Arbitrary_dense_ranges_fill_every_calendar_cell_and_preserve_curriculum(
        int offset, int length, WeekPreset days, int groupCount, int slotsPerDay)
    {
        await using var fixture = await RangeFixture.CreateAsync(offset, length, days, groupCount, slotsPerDay);
        var watch = Stopwatch.StartNew();
        var service = new TeacherDraftsAutogenService(fixture.Db);
        var action = await service.DraftAutoGen(fixture.Request);
        var result = Extract(action);
        var generationTime = watch.Elapsed;
        Assert.Equal(fixture.ExpectedRows, result.Created);
        var coverage = Assert.IsType<AutoGenCoverageDto>(result.Coverage);
        Assert.Equal(fixture.ExpectedRows, coverage.AvailableSlots);
        Assert.Equal(AutoGenCoverageStatus.FullyOccupied, coverage.Status);
        Assert.Equal(0, coverage.EmptySlots);
        Assert.Equal(0, coverage.InternalGapSlots);
        Assert.Equal(0, coverage.MissingLessons);
        Assert.Equal(0, coverage.OverplannedLessons);
        Assert.Equal(0, coverage.IncompleteLessons);
        Assert.False(coverage.SearchLimitReached);
        var rows = await fixture.Db.TeacherDraftItems.AsNoTracking().ToListAsync();
        Assert.Equal(fixture.ExpectedRows, rows.Select(r => (r.GroupId, r.Date, r.StartTime)).Distinct().Count());
        Assert.All(rows, row => Assert.Contains(row.Date, fixture.TeachingDates));
        var validation = await new TeacherDraftsAutogenHardRuleValidator(fixture.Db).ValidateAsync(
            new TeacherDraftsAutogenHardRuleValidationRequest(1, fixture.Request.GroupIds!, fixture.From, fixture.To, days));
        Assert.Empty(validation.Violations);
        var refill = Extract(await service.DraftAutoGen(fixture.Request with { ClearExisting = false, SoftFill = true }));
        Assert.Equal(0, refill.Created);
        Assert.Equal(0, refill.Coverage!.EmptySlots);
        Assert.True(watch.Elapsed < TimeSpan.FromMinutes(2), $"Діапазон {length} днів: генерація {generationTime}, перевірка і Fill {watch.Elapsed - generationTime}.");
    }

    [Fact]
    public async Task Coverage_distinguishes_completed_curriculum_from_empty_cells_and_internal_windows()
    {
        await using var fixture = await RangeFixture.CreateAsync(0, 7, WeekPreset.MonFri, 1, 3);
        var date = fixture.TeachingDates.First();
        fixture.Db.TeacherDraftItems.AddRange(fixture.Draft(date, 8), fixture.Draft(date, 10));
        await fixture.Db.SaveChangesAsync();
        var coverage = await fixture.MeasureAsync(new Dictionary<int, int> { [1] = 2 });
        Assert.Equal(0, coverage.MissingLessons);
        Assert.Equal(2, coverage.OccupiedSlots);
        Assert.Equal(fixture.ExpectedRows - 2, coverage.EmptySlots);
        Assert.Equal(1, coverage.InternalGapSlots);
        Assert.Equal(AutoGenCoverageStatus.CurriculumComplete, coverage.Status);
        fixture.Db.TeacherDraftItems.Add(fixture.Draft(date, 9));
        var pending = await fixture.MeasureAsync(new Dictionary<int, int> { [1] = 3 });
        Assert.Equal(0, pending.InternalGapSlots);
        Assert.Equal(3, pending.OccupiedSlots);
        Assert.Equal(2, await fixture.Db.TeacherDraftItems.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Coverage_does_not_offset_one_groups_shortage_with_another_groups_excess()
    {
        await using var fixture = await RangeFixture.CreateAsync(0, 7, WeekPreset.MonFri, 2, 3);
        var date = fixture.TeachingDates.First();
        fixture.Db.TeacherDraftItems.AddRange(fixture.Draft(date, 8), fixture.Draft(date, 9));
        await fixture.Db.SaveChangesAsync();
        var coverage = await fixture.MeasureAsync(new Dictionary<int, int> { [1] = 1 });
        Assert.Equal(2, coverage.ScheduledRequestedLessons);
        Assert.Equal(2, coverage.RequestedLessons);
        Assert.Equal(1, coverage.MissingLessons);
        Assert.Equal(1, coverage.OverplannedLessons);
        Assert.Equal(AutoGenCoverageStatus.Partial, coverage.Status);
    }

    [Fact]
    public async Task Coverage_counts_legacy_coteachers_once_but_keeps_exact_duplicates_and_missing_resources_visible()
    {
        await using var fixture = await RangeFixture.CreateAsync(0, 7, WeekPreset.MonFri, 2, 3);
        var date = fixture.TeachingDates.First();
        var coteacher = fixture.Draft(date, 8);
        coteacher.TeacherId = 2;
        var incomplete = fixture.Draft(date, 10);
        incomplete.TeacherId = null;
        fixture.Db.TeacherDraftItems.AddRange(fixture.Draft(date, 8), coteacher,
            fixture.Draft(date, 9), fixture.Draft(date, 9), incomplete);
        await fixture.Db.SaveChangesAsync();
        var coverage = await fixture.MeasureAsync(new Dictionary<int, int> { [1] = 4 });
        Assert.Equal(4, coverage.ScheduledRequestedLessons);
        Assert.Equal(3, coverage.OccupiedSlots);
        Assert.Equal(1, coverage.IncompleteLessons);
    }

    [Fact]
    public async Task Search_limit_is_not_reported_as_proven_infeasibility()
    {
        await using var fixture = await RangeFixture.CreateAsync(3, 12, WeekPreset.MonSun, 1, 2);
        var coverage = await new TeacherDraftsAutogenCoverageService(fixture.Db).MeasureAsync(
            fixture.Request.GroupIds!, fixture.From, fixture.To, fixture.Request.Days, fixture.Request.ModuleHours, true);
        Assert.Equal(AutoGenCoverageStatus.SearchLimited, coverage.Status);
        Assert.Equal(fixture.ExpectedRows, coverage.MissingLessons);
        Assert.True(coverage.SearchLimitReached);
    }

    [Fact]
    public void Routine_placement_notes_remain_distinct_from_resource_or_unknown_warnings()
    {
        const string note = "[2031-01-01 08:00-08:45] Тестова група: Продовжено суцільний блок модуля; Повтор того ж часу в інші дні";
        Assert.Equal(AutoGenWarningSeverities.Info, AutoGenWarningClassifier.Classify(note).Severity);
        Assert.Equal(AutoGenWarningSeverities.Warning, AutoGenWarningClassifier.Classify(note + "; Невідома умова").Severity);
        Assert.Equal(AutoGenWarningCodes.ResourceUnavailable, AutoGenWarningClassifier.Classify(note + "; Немає доступного викладача").Code);
        var report = TeacherDraftsAutogenReportBuilder.BuildResult(20_000, 0,
            Enumerable.Range(1, 20_000).Select(i => note.Replace("Тестова група", $"Група {i}")), [], []);
        var summary = Assert.Single(report.WarningDetails!);
        Assert.Equal("20000", summary.Context!["occurrences"]);
        Assert.True(System.Text.Json.JsonSerializer.Serialize(report).Length < 20_000);
    }

    [Fact]
    public void Shared_resource_capacity_matches_an_exhaustive_small_graph_oracle()
    {
        var random = new Random(40521);
        for (var sample = 0; sample < 150; sample++)
        {
            var demands = Enumerable.Range(0, random.Next(1, 5)).Select(_ => new ResourceTimeDemand(
                random.Next(1, 3), Enumerable.Range(0, 6).Where(_ => random.Next(2) == 0).ToArray())).ToArray();
            var units = demands.SelectMany(d => Enumerable.Repeat(d.AvailableResourceSlots, d.Required)).ToArray();
            int Oracle(int index, int used)
            {
                if (index == units.Length) return 0;
                var best = Oracle(index + 1, used);
                foreach (var slot in units[index])
                    if ((used & (1 << slot)) == 0) best = Math.Max(best, 1 + Oracle(index + 1, used | (1 << slot)));
                return best;
            }
            var result = ResourceDemandCapacityAnalyzer.Analyze(demands);
            Assert.True(result.SearchComplete);
            Assert.Equal(Oracle(0, 0), result.Matched);
            Assert.Equal(units.Length - result.Matched, result.ProvenShortfall);
        }
        ResourceTimeDemand[] contention = [new(2, new[] { 1, 2, 3 }), new(2, new[] { 1, 2, 3 })];
        Assert.Equal(1, ResourceDemandCapacityAnalyzer.Analyze(contention).ProvenShortfall);
        Assert.Null(ResourceDemandCapacityAnalyzer.Analyze(contention, maxEdgeVisits: 1).ProvenShortfall);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ResourceDemandCapacityAnalyzer.Analyze(contention, canceled.Token));
    }

    [Fact]
    public async Task Preflight_detects_shared_teacher_shortage_between_individually_feasible_groups()
    {
        await using var fixture = await RangeFixture.CreateAsync(0, 7, WeekPreset.MonFri, 2, 2);
        fixture.Db.TeacherModules.RemoveRange(await fixture.Db.TeacherModules.Where(t => t.TeacherId == 2).ToListAsync());
        await fixture.Db.SaveChangesAsync();
        var result = Extract(await new TeacherDraftsAutogenService(fixture.Db).DraftAutoGen(
            fixture.Request with { ClearExisting = true, PreflightOnly = true }));
        Assert.Contains(result.Preflight!, item => item.Code == "shared-teacher-capacity" && item.Count > 0);
        Assert.Equal(0, await fixture.Db.TeacherDraftItems.CountAsync());
    }

    [Fact]
    public async Task Annual_plan_over_two_thousand_changes_preserves_preview_apply_and_rollback()
    {
        await using var fixture = await RangeFixture.CreateAsync(8, 366, WeekPreset.MonFri, 5, 2);
        Assert.True(fixture.ExpectedRows > 2_000);
        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(fixture.Options));
        services.AddScoped<TeacherDraftsAutogenService>();
        services.AddScoped<TeacherDraftsAutogenPlanService>();
        await using var provider = services.BuildServiceProvider();
        var logger = new TestLogger();
        var jobs = new TeacherDraftsAutogenJobService(provider.GetRequiredService<IServiceScopeFactory>(), logger);
        try
        {
            var request = fixture.JobRequest;
            var capacity = await AutogenCalendarWorkload.MeasureAsync(fixture.Db, request);
            Assert.Equal(fixture.ExpectedRows, capacity.CalendarSlots);
            var started = jobs.Start(request);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            AutoGenJobStatus? status;
            do
            {
                await Task.Delay(100, timeout.Token);
                status = await jobs.GetAsync(started.JobId);
            } while (status?.State is AutoGenJobState.Queued or AutoGenJobState.Running);
            Assert.True(status?.State == AutoGenJobState.Succeeded, status?.Error + " " + string.Join(" | ", status?.Result?.Warnings.TakeLast(8) ?? []));
            Assert.Equal(0, await fixture.Db.TeacherDraftItems.AsNoTracking().CountAsync());
            Assert.Equal(0, status!.Result!.Coverage!.EmptySlots);
            AutoGenPlanDetailsDto plan;
            try { plan = await jobs.GetPlanAsync(started.JobId); }
            catch (Exception ex) { throw new InvalidOperationException(string.Join("\n", logger.Messages), ex); }
            Assert.Equal(fixture.ExpectedRows, plan.Summary.AddCount);
            var persisted = await fixture.Db.AutoGenJobRuns.AsNoTracking().SingleAsync(r => r.JobId == started.JobId);
            Assert.True(persisted.StatusJson.Length < 10_000);
            Assert.True(persisted.ResultJson!.Length < TeacherDraftsAutogenJobService.MaxPersistedPayloadCharacters);
            var restoredJobs = new TeacherDraftsAutogenJobService(provider.GetRequiredService<IServiceScopeFactory>(), logger);
            var restored = await restoredJobs.GetAsync(started.JobId);
            Assert.Equal(status.Result.Coverage!.Status, restored!.Result!.Coverage!.Status);
            Assert.Equal(status.Result.Coverage.Groups, restored.Result.Coverage.Groups);
            Assert.Equal(status.Result.Coverage.Weeks, restored.Result.Coverage.Weeks);
            await Assert.ThrowsAsync<AutoGenPlanConflictException>(() => jobs.ApplyPlanAsync(started.JobId, new(plan.Summary.Version + 1)));
            var applied = await jobs.ApplyPlanAsync(started.JobId, new(plan.Summary.Version));
            Assert.Equal(fixture.ExpectedRows, await fixture.Db.TeacherDraftItems.AsNoTracking().CountAsync());
            var replay = await jobs.ApplyPlanAsync(started.JobId, new(plan.Summary.Version));
            Assert.Equal(applied.Summary.Version, replay.Summary.Version);
            await jobs.RollbackPlanAsync(started.JobId, new(applied.Summary.Version));
            Assert.Equal(0, await fixture.Db.TeacherDraftItems.AsNoTracking().CountAsync());
        }
        finally { await jobs.StopAsync(CancellationToken.None); }
    }

    private static AutoGenResult Extract(ActionResult<AutoGenResult> action)
    {
        Assert.True(action.Result is OkObjectResult,
            string.Join(" | ", (action.Result as ObjectResult)?.Value is AutoGenResult failure ? failure.Warnings : []));
        return Assert.IsType<AutoGenResult>(((OkObjectResult)action.Result!).Value);
    }

    private sealed class TestLogger : ILogger<TeacherDraftsAutogenJobService>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (IsEnabled(level)) lock (Messages) Messages.Add(formatter(state, exception) + " " + exception); }
    }

    internal sealed class RangeFixture(SqliteConnection anchor, DbContextOptions<AppDbContext> options, AppDbContext db) : IAsyncDisposable
    {
        public AppDbContext Db => db;
        public DbContextOptions<AppDbContext> Options => options;
        public DateOnly From { get; private set; }
        public DateOnly To { get; private set; }
        public List<DateOnly> TeachingDates { get; } = new();
        public DraftAutoGenRequest Request { get; private set; } = null!;
        public int ExpectedRows { get; private set; }
        public AutoGenJobRequest JobRequest => new(AutoGenJobKind.Generate, From, To, 1, Request.GroupIds!, Request.ModuleHours!,
            Request.Days, true, true, false, SoftOptions: new(MaxParallelGroupsPerModuleInSlot: Request.GroupIds!.Count, RecentRepeatWindowDays: 0),
            ClientJobId: Guid.NewGuid().ToString("N"), PreviewOnly: true);

        public static async Task<RangeFixture> CreateAsync(int offset, int length, WeekPreset days, int groupCount, int slotCount)
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = $"universal-{Guid.NewGuid():N}", Mode = SqliteOpenMode.Memory, Cache = SqliteCacheMode.Shared }.ToString();
            var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var fixture = new RangeFixture(anchor, options, db) { From = new DateOnly(2031, 1, 1).AddDays(offset) };
            fixture.To = fixture.From.AddDays(length - 1);
            var holiday = fixture.From.AddDays(3);
            for (var date = fixture.From; date <= fixture.To; date = date.AddDays(1))
                if (date != holiday && (days == WeekPreset.MonSun || date.DayOfWeek != DayOfWeek.Sunday)
                    && (days != WeekPreset.MonFri || date.DayOfWeek != DayOfWeek.Saturday)) fixture.TeachingDates.Add(date);
            var hours = fixture.TeachingDates.Count * slotCount;
            var groupIds = Enumerable.Range(1, groupCount).ToList();
            fixture.ExpectedRows = groupCount * hours;
            fixture.Request = new(fixture.From, CourseId: 1, GroupIds: groupIds, Days: days,
                ModuleHours: new() { [1] = hours }, SoftFill: true, RangeStartDate: fixture.From, RangeEndDate: fixture.To,
                SoftOptions: new(MaxParallelGroupsPerModuleInSlot: groupCount, RecentRepeatWindowDays: 0));
            db.Courses.Add(new Course { Id = 1, Name = "Синтетичний довільний курс", DurationWeeks = (length + 6) / 7, AcademicPeriodStartDate = fixture.From });
            db.Groups.AddRange(groupIds.Select(id => new Group { Id = id, CourseId = 1, Name = $"Тестова група {id}", StudentsCount = 20 }));
            db.Buildings.Add(new Building { Id = 1, Name = "Навчальний корпус" });
            db.LessonTypes.Add(new LessonTypeRef { Id = 1, Code = "PRACTICE", Name = "Практичне заняття" });
            db.Modules.Add(new BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities.Module { Id = 1, CourseId = 1, Code = "SYN", Title = "Синтетичний модуль" });
            db.ModuleTopics.Add(new ModuleTopic { Id = 1, ModuleId = 1, LessonTypeId = 1, Order = 1, TopicCode = "1.1", TotalHours = hours, AuditoriumHours = hours });
            db.ModulePlans.Add(new ModulePlan { CourseId = 1, ModuleId = 1, TargetHours = hours, IsActive = true });
            db.ModuleSequenceItems.Add(new ModuleSequenceItem { CourseId = 1, ModuleId = 1, Order = 1, GroupOrder = 1 });
            db.CalendarExceptions.Add(new CalendarException { CourseId = 1, Date = holiday, IsWorkingDay = false, Name = "Синтетичний неробочий день" });
            foreach (var id in groupIds)
            {
                db.Teachers.Add(new Teacher { Id = id, FullName = $"Тестовий викладач {id}" });
                db.TeacherModules.Add(new TeacherModule { TeacherId = id, ModuleId = 1 });
                db.Rooms.Add(new Room { Id = id, BuildingId = 1, Name = $"Аудиторія {id}", Capacity = 30 });
                db.ModuleRooms.Add(new ModuleRoom { ModuleId = 1, RoomId = id });
                foreach (var day in Enum.GetValues<DayOfWeek>())
                    db.TeacherWorkingHours.Add(new TeacherWorkingHour { TeacherId = id, DayOfWeek = day, Start = new(8, 0), End = new(18, 0) });
            }
            for (var i = 0; i < slotCount; i++)
                db.TimeSlots.Add(new TimeSlot { CourseId = 1, SortOrder = i + 1, Start = new(8 + i, 0), End = new(8 + i, 45), IsActive = true });
            await db.SaveChangesAsync();
            return fixture;
        }

        public TeacherDraftItem Draft(DateOnly date, int hour) => new() { GroupId = 1, ModuleId = 1, ModuleTopicId = 1, LessonTypeId = 1, TeacherId = 1, RoomId = 1,
            Date = date, DayOfWeek = date.DayOfWeek, StartTime = new(hour, 0), EndTime = new(hour, 45) };
        public Task<AutoGenCoverageDto> MeasureAsync(Dictionary<int, int> hours) => new TeacherDraftsAutogenCoverageService(Db)
            .MeasureAsync(Request.GroupIds!, From, To, Request.Days, hours, false);
        public async ValueTask DisposeAsync() { await db.DisposeAsync(); await anchor.DisposeAsync(); }
    }
}
