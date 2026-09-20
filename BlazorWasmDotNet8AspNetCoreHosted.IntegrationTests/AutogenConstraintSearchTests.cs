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

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

[Collection("Autogen performance")]
public sealed class AutogenConstraintSearchTests
{
    [Fact]
    public void Complete_day_bound_preserves_distinct_modules_shared_catchup_and_complete_assignments()
    {
        var full = new AutogenDayQuality(4, 0, 2, 2, 2, 1, 0, false);
        Assert.True(full.IsAtUpperBound(4));
        Assert.False((full with { PendingSharedCatchUp = 1 }).IsAtUpperBound(4));
        Assert.False((full with { AddedIncomplete = true }).IsAtUpperBound(4));
        Assert.False((full with { ModuleSegments = 3 }).IsAtUpperBound(4));
        Assert.False((full with { DistinctModules = 3, ModuleSegments = 3, Transitions = 2 }).IsAtUpperBound(4));
        Assert.False((full with { FilledSlots = 3 }).IsAtUpperBound(4));
        Assert.True(full.Score > (full with { PendingSharedCatchUp = 1 }).Score);
    }

    [Fact]
    public void Layered_capacity_proves_large_contention_and_identifies_the_competing_demands()
    {
        var common = Enumerable.Range(1, 4_000).ToArray();
        ResourceTimeDemand[] demands = [new(2_001, common), new(2_001, common), new(1, [9_999])];
        var capacity = ResourceDemandCapacityAnalyzer.Analyze(demands);
        Assert.True(capacity.SearchComplete);
        Assert.Equal(2, capacity.ProvenShortfall);
        Assert.Equal(new[] { 0, 1 }, capacity.BottleneckDemandIndexes);
        Assert.Empty(ResourceDemandCapacityAnalyzer.Analyze(demands, maxEdgeVisits: 1).BottleneckDemandIndexes);
    }

    [Fact]
    public async Task Preflight_reports_shared_room_shortage_even_when_every_group_individually_fits()
    {
        await using var fixture = await AutogenUniversalRangeTests.RangeFixture.CreateAsync(0, 7, WeekPreset.MonFri, 2, 1);
        fixture.Db.ModuleRooms.RemoveRange(await fixture.Db.ModuleRooms.Where(link => link.RoomId != 1).ToListAsync());
        await fixture.Db.SaveChangesAsync();
        var result = Extract(await new TeacherDraftsAutogenService(fixture.Db).DraftAutoGen(fixture.Request with { PreflightOnly = true }));
        var deficit = Assert.Single(result.Preflight!, item => item.Code == "shared-room-capacity");
        Assert.Contains("Тестова група 1", deficit.Recommendation);
        Assert.Contains("Тестова група 2", deficit.Recommendation);
        Assert.Empty(await fixture.Db.TeacherDraftItems.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Component_search_matches_exhaustive_oracle_and_keeps_inputs_unchanged()
    {
        var random = new Random(4096);
        for (var seed = 0; seed < 150; seed++)
        {
            var domains = Enumerable.Range(0, random.Next(2, 6)).Select(id => new ConflictComponentDomain<(int Teacher, int Room)>(id,
                Enumerable.Range(0, random.Next(1, 5)).Select(index => new ConflictComponentCandidate<(int, int)>(index, 0,
                    (random.Next(5), random.Next(5)))).ToArray())).ToArray();
            static bool Conflict((int Teacher, int Room) a, (int Teacher, int Room) b) => a.Teacher == b.Teacher || a.Room == b.Room;
            bool Accept(IReadOnlyList<(int Teacher, int Room)> placements)
                => seed % 2 == 0 || placements.Sum(placement => placement.Room) % 3 == seed % 3;
            bool Oracle(int index, List<(int Teacher, int Room)> assigned)
            {
                if (index == domains.Length) return Accept(assigned);
                foreach (var candidate in domains[index].Candidates)
                {
                    if (assigned.Any(item => Conflict(item, candidate.Value))) continue;
                    assigned.Add(candidate.Value);
                    if (Oracle(index + 1, assigned)) return true;
                    assigned.RemoveAt(assigned.Count - 1);
                }
                return false;
            }
            var before = domains.SelectMany(d => d.Candidates).ToArray();
            var result = await BoundedConflictComponentSolver.SolveAsync(domains, Conflict, values => Task.FromResult(Accept(values)),
                new DeterministicSearchBudget(100_000, TimeSpan.FromMinutes(1)), maxCompleteChecks: 2_000);
            Assert.False(result.SearchLimitReached);
            Assert.Equal(Oracle(0, []), result.Placements.Count == domains.Length);
            Assert.Equal(before, domains.SelectMany(d => d.Candidates));
        }
    }

    [Fact]
    public async Task Component_search_rejects_invalid_complete_plans_and_reports_limits_and_cancellation()
    {
        ConflictComponentDomain<int>[] domains = [new(0, [new(0, 0, 0), new(1, 1, 1)]), new(1, [new(0, 0, 2)])];
        var calls = 0;
        var result = await BoundedConflictComponentSolver.SolveAsync(domains, (a, b) => a == b,
            values => Task.FromResult(++calls > 1 && values[0] == 1), new(10_000, TimeSpan.FromMinutes(1)));
        Assert.Equal(new[] { 1, 2 }, result.Placements);
        var limited = await BoundedConflictComponentSolver.SolveAsync(domains, (a, b) => false,
            _ => Task.FromResult(false), new(10_000, TimeSpan.FromMinutes(1)), maxCompleteChecks: 1);
        Assert.True(limited.SearchLimitReached);
        Assert.Empty(limited.Placements);
        var exhausted = await BoundedConflictComponentSolver.SolveAsync(domains, (a, b) => false,
            _ => Task.FromResult(true), new(1, TimeSpan.FromMinutes(1)));
        Assert.True(exhausted.SearchLimitReached);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BoundedConflictComponentSolver.SolveAsync(domains, (a, b) => false,
            _ => { cancellation.Cancel(); return Task.FromResult(true); }, new(10_000, TimeSpan.FromMinutes(1)), cancellation.Token));
    }

    [Fact]
    public async Task Component_search_reuses_pruned_domains_to_finish_within_the_same_node_budget()
    {
        var domains = Enumerable.Range(0, 8).Select(id => new ConflictComponentDomain<int>(id,
            Enumerable.Range(0, 16).Select(value => new ConflictComponentCandidate<int>(value, value, value)).ToArray())).ToArray();
        var result = await BoundedConflictComponentSolver.SolveAsync(domains, (a, b) => a == b,
            _ => Task.FromResult(true), new(550, TimeSpan.FromMinutes(1)));
        Assert.False(result.SearchLimitReached);
        Assert.Equal(Enumerable.Range(0, 8), result.Placements);
        Assert.InRange(result.VisitedNodes, 1, 550);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Component_search_cancellation_after_the_final_validation_never_returns_a_result(bool accept)
    {
        ConflictComponentDomain<int>[] domains = [new(1, [new(1, 0, 1)])];
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BoundedConflictComponentSolver.SolveAsync(
            domains, (a, b) => false, _ => { cancellation.Cancel(); return Task.FromResult(accept); },
            new(100, TimeSpan.FromMinutes(1)), cancellation.Token));
        Assert.Equal(1, Assert.Single(Assert.Single(domains).Candidates).Value);
    }

    private sealed record TestPlacement(int Event, int Group, int Day, int Slot, int Teacher, int Room);

    [Fact]
    public async Task Component_repair_coordinates_three_moves_across_groups_and_dates_while_preserving_a_locked_event()
    {
        ConflictComponentDomain<TestPlacement>[] domains =
        [
            new(0, [new(0, 0, new(0, 1, 0, 0, 1, 1))]),
            new(1, [new(0, 0, new(1, 2, 0, 0, 1, 2)), new(1, 1, new(1, 2, 1, 0, 2, 2))]),
            new(2, [new(0, 0, new(2, 2, 1, 0, 2, 2)), new(1, 1, new(2, 2, 1, 1, 3, 3))]),
            new(3, [new(0, 0, new(3, 1, 1, 1, 3, 1)), new(1, 1, new(3, 1, 0, 1, 4, 4))]),
            new(4, [new(0, 0, new(4, 1, 1, 0, 4, 4))])
        ];
        static bool Conflict(TestPlacement a, TestPlacement b) => a.Day == b.Day && a.Slot == b.Slot
            && (a.Group == b.Group || a.Teacher == b.Teacher || a.Room == b.Room);
        var result = await BoundedConflictComponentSolver.SolveAsync(domains, Conflict, _ => Task.FromResult(true), new(10_000, TimeSpan.FromMinutes(1)));
        Assert.False(result.SearchLimitReached);
        Assert.Equal(5, result.Placements.Count);
        Assert.Equal(domains[4].Candidates[0].Value, result.Placements[4]);
        for (var i = 1; i <= 3; i++) Assert.Equal(domains[i].Candidates[1].Value, result.Placements[i]);
        var replay = await BoundedConflictComponentSolver.SolveAsync(domains.Reverse().ToArray(), Conflict, _ => Task.FromResult(true), new(10_000, TimeSpan.FromMinutes(1)));
        Assert.Equal(result.Placements, replay.Placements);
        Assert.Equal(result.VisitedNodes, replay.VisitedNodes);
    }

    [Theory]
    [InlineData(45, 13)]
    [InlineData(366, 71)]
    [InlineData(733, 107)]
    public async Task Mixed_ranges_with_shared_scarce_resources_recover_a_known_complete_schedule(int length, int seed)
    {
        await using var fixture = await MixedFixture.CreateAsync(length, seed);
        var watch = Stopwatch.StartNew();
        var result = Extract(await new TeacherDraftsAutogenService(fixture.Db).DraftAutoGen(fixture.Request));
        Assert.Equal(fixture.ExpectedRows - fixture.LockedCount, result.Created);
        var coverage = Assert.IsType<AutoGenCoverageDto>(result.Coverage);
        Assert.Equal(AutoGenCoverageStatus.FullyOccupied, coverage.Status);
        Assert.Equal(0, coverage.MissingLessons + coverage.OverplannedLessons + coverage.EmptySlots + coverage.InternalGapSlots + coverage.IncompleteLessons);
        Assert.False(coverage.SearchLimitReached);
        Assert.Empty((await fixture.ValidateAsync()).Violations);
        Assert.Equal(fixture.LockedFingerprint, await fixture.ReadLockedAsync());
        var fill = Extract(await new TeacherDraftsAutogenService(fixture.Db).DraftAutoGen(fixture.Request));
        Assert.Equal(0, fill.Created);
        Assert.Equal(AutoGenCoverageStatus.FullyOccupied, fill.Coverage!.Status);
        Console.WriteLine($"Змішаний період {length} днів, seed {seed}: {fixture.ExpectedRows} пар, {watch.Elapsed.TotalSeconds:F2} с.");
        Assert.True(watch.Elapsed < TimeSpan.FromMinutes(2), $"Змішаний період {length} днів: {watch.Elapsed}.");
    }

    private static AutoGenResult Extract(ActionResult<AutoGenResult> action)
    {
        Assert.True(action.Result is OkObjectResult, string.Join(" | ", (action.Result as ObjectResult)?.Value is AutoGenResult failure ? failure.Warnings : []));
        return Assert.IsType<AutoGenResult>(((OkObjectResult)action.Result!).Value);
    }

    [Fact]
    public async Task Preflight_requires_teacher_and_room_to_be_available_at_the_same_time()
    {
        await using var fixture = await AutogenUniversalRangeTests.RangeFixture.CreateAsync(0, 7, WeekPreset.MonFri, 2, 2);
        var db = fixture.Db;
        db.TeacherModules.RemoveRange(await db.TeacherModules.Where(link => link.TeacherId == 2).ToListAsync());
        db.ModuleRooms.RemoveRange(await db.ModuleRooms.Where(link => link.RoomId == 2).ToListAsync());
        foreach (var work in await db.TeacherWorkingHours.Where(work => work.TeacherId == 1).ToListAsync()) work.End = new(8, 45);
        foreach (var day in fixture.TeachingDates)
            db.ScheduleItems.Add(new ScheduleItem { Date = day, DayOfWeek = day.DayOfWeek, GroupId = 2, ModuleId = 1,
                ModuleTopicId = 1, LessonTypeId = 1, TeacherId = 2, RoomId = 1, StartTime = new(8, 0), EndTime = new(8, 45) });
        await db.SaveChangesAsync();
        var publishedBefore = await db.ScheduleItems.AsNoTracking().CountAsync();
        var result = Extract(await new TeacherDraftsAutogenService(db).DraftAutoGen(fixture.Request with
            { GroupIds = [1], ModuleHours = new() { [1] = fixture.TeachingDates.Count }, PreflightOnly = true }));
        Assert.Contains(result.Preflight!, item => item.Code == "shared-teacher-capacity" && item.Count == fixture.TeachingDates.Count);
        Assert.Contains(result.Preflight!, item => item.Code == "shared-room-capacity");
        Assert.Equal(publishedBefore, await db.ScheduleItems.AsNoTracking().CountAsync());
        Assert.Empty(await db.TeacherDraftItems.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fill_repairs_a_persisted_peer_assignment_without_moving_a_locked_lesson(bool peerLocked)
    {
        await using var fixture = await AutogenUniversalRangeTests.RangeFixture.CreateAsync(5, 2, WeekPreset.MonFri, 2, 1);
        var db = fixture.Db;
        db.TeacherModules.RemoveRange(await db.TeacherModules.Where(link => link.TeacherId == 2).ToListAsync());
        db.ModuleRooms.RemoveRange(await db.ModuleRooms.Where(link => link.RoomId == 2).ToListAsync());
        var firstTopic = await db.ModuleTopics.SingleAsync();
        firstTopic.TotalHours = firstTopic.AuditoriumHours = 1;
        (await db.ModulePlans.SingleAsync()).TargetHours = 1;
        db.Modules.Add(new BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities.Module { Id = 2, CourseId = 1, Code = "SECOND", Title = "Другий модуль" });
        db.ModuleTopics.Add(new ModuleTopic { Id = 2, ModuleId = 2, LessonTypeId = 1, Order = 1, TopicCode = "2.1", TotalHours = 1, AuditoriumHours = 1 });
        db.ModulePlans.Add(new ModulePlan { CourseId = 1, ModuleId = 2, TargetHours = 1, IsActive = true });
        db.ModuleSequenceItems.Add(new ModuleSequenceItem { CourseId = 1, ModuleId = 2, Order = 2, GroupOrder = 1 });
        db.TeacherModules.AddRange(new TeacherModule { TeacherId = 1, ModuleId = 2 }, new TeacherModule { TeacherId = 2, ModuleId = 2 });
        db.ModuleRooms.Add(new ModuleRoom { ModuleId = 2, RoomId = 2 });
        var locked = new TeacherDraftItem { GroupId = 1, Date = fixture.To, DayOfWeek = fixture.To.DayOfWeek,
            ModuleId = 2, ModuleTopicId = 2, LessonTypeId = 1, TeacherId = 2, RoomId = 2, StartTime = new(8, 0), EndTime = new(8, 45), IsLocked = true };
        var peer = new TeacherDraftItem { GroupId = 2, Date = fixture.From, DayOfWeek = fixture.From.DayOfWeek,
            ModuleId = 2, ModuleTopicId = 2, LessonTypeId = 1, TeacherId = 1, RoomId = 2, StartTime = new(8, 0), EndTime = new(8, 45), IsLocked = peerLocked };
        db.TeacherDraftItems.AddRange(locked, peer);
        await db.SaveChangesAsync();
        var lockedRevision = locked.Revision;
        var lockedUpdated = locked.UpdatedAt;
        var request = fixture.Request with { ClearExisting = false, ModuleHours = new() { [1] = 1, [2] = 1 } };
        if (!peerLocked)
        {
            var services = new ServiceCollection();
            services.AddScoped(_ => new AppDbContext(fixture.Options));
            services.AddScoped<TeacherDraftsAutogenService>();
            services.AddScoped<TeacherDraftsAutogenPlanService>();
            await using var provider = services.BuildServiceProvider();
            var jobs = new TeacherDraftsAutogenJobService(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<TeacherDraftsAutogenJobService>.Instance);
            try
            {
                var job = jobs.Start(fixture.JobRequest with { Kind = AutoGenJobKind.Fill, ClearExisting = false, ModuleHours = request.ModuleHours! });
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                AutoGenJobStatus? status;
                do { await Task.Delay(50, timeout.Token); status = await jobs.GetAsync(job.JobId); }
                while (status?.State is AutoGenJobState.Queued or AutoGenJobState.Running);
                Assert.True(status?.State == AutoGenJobState.Succeeded, status?.Error);
                Assert.Equal(AutoGenCoverageStatus.FullyOccupied, status!.Result!.Coverage!.Status);
                Assert.Equal(2, await db.TeacherDraftItems.AsNoTracking().CountAsync());
                Assert.Equal(1, (await db.TeacherDraftItems.AsNoTracking().SingleAsync(d => d.Id == peer.Id)).TeacherId);
                var plan = await jobs.GetPlanAsync(job.JobId);
                Assert.Equal(1, plan.Summary.UpdateCount);
                Assert.Equal(2, plan.Summary.AddCount);
                var applied = await jobs.ApplyPlanAsync(job.JobId, new(plan.Summary.Version));
                Assert.Equal(4, await db.TeacherDraftItems.AsNoTracking().CountAsync());
                Assert.Equal(2, (await db.TeacherDraftItems.AsNoTracking().SingleAsync(d => d.Id == peer.Id)).TeacherId);
                await jobs.RollbackPlanAsync(job.JobId, new(applied.Summary.Version));
                Assert.Equal(2, await db.TeacherDraftItems.AsNoTracking().CountAsync());
                Assert.Equal(1, (await db.TeacherDraftItems.AsNoTracking().SingleAsync(d => d.Id == peer.Id)).TeacherId);
                db.ChangeTracker.Clear();
            }
            finally { await jobs.StopAsync(CancellationToken.None); }
        }
        var result = Extract(await new TeacherDraftsAutogenService(db).DraftAutoGen(request));
        Assert.Equal(peerLocked ? 1 : 2, result.Created);
        Assert.Equal(peerLocked ? AutoGenCoverageStatus.Partial : AutoGenCoverageStatus.FullyOccupied, result.Coverage!.Status);
        Assert.Equal(peerLocked ? 1 : 0, result.Coverage.MissingLessons);
        if (!peerLocked) Assert.Contains(result.Warnings, message => message.Contains("узгодив компоненту", StringComparison.Ordinal));
        var rows = await db.TeacherDraftItems.AsNoTracking().OrderBy(d => d.Id).ToListAsync();
        var savedLocked = Assert.Single(rows, row => row.Id == locked.Id);
        Assert.Equal(lockedRevision, savedLocked.Revision);
        Assert.Equal(lockedUpdated, savedLocked.UpdatedAt);
        Assert.Equal(peerLocked ? 1 : 2, Assert.Single(rows, row => row.Id == peer.Id).TeacherId);
        Assert.Empty((await new TeacherDraftsAutogenHardRuleValidator(db).ValidateAsync(new(1, [1, 2], fixture.From, fixture.To))).Violations);
        Assert.Equal(0, Extract(await new TeacherDraftsAutogenService(db).DraftAutoGen(request)).Created);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fill_reorders_module_blocks_and_rematches_day_rooms_without_moving_locked_lessons(bool lockDay)
    {
        await using var fixture = await AutogenUniversalRangeTests.RangeFixture.CreateAsync(0, 1, WeekPreset.MonFri, 2, 6);
        var db = fixture.Db;
        var request = await SeedReorderDaysAsync(fixture);
        if (lockDay)
        {
            foreach (var row in await db.TeacherDraftItems.Where(d => d.GroupId == 1).ToListAsync())
                row.IsLocked = true;
            await db.SaveChangesAsync();
        }
        var protectedBefore = await ReadDraftStateAsync(db, lockDay ? null : 2);
        var result = Extract(await new TeacherDraftsAutogenService(db).DraftAutoGen(request));
        Assert.Equal(lockDay ? 0 : 1, result.Created);
        Assert.Equal(lockDay ? 1 : 0, result.Coverage!.MissingLessons);
        Assert.Equal(lockDay ? 5 : 6, result.Coverage.OccupiedSlots);
        if (!lockDay)
        {
            Assert.Equal(AutoGenCoverageStatus.FullyOccupied, result.Coverage.Status);
            Assert.Contains(result.Warnings, w => w.Contains("узгодив компоненту", StringComparison.Ordinal));
            Assert.All(await db.TeacherDraftItems.AsNoTracking().Where(d => d.GroupId == 1).ToListAsync(),
                draft => Assert.Equal(2, draft.RoomId));
        }
        Assert.Equal(protectedBefore, await ReadDraftStateAsync(db, lockDay ? null : 2));
        Assert.Empty((await new TeacherDraftsAutogenHardRuleValidator(db).ValidateAsync(
            new(1, [1, 2], fixture.From, fixture.To))).Violations);
        Assert.Equal(0, Extract(await new TeacherDraftsAutogenService(db).DraftAutoGen(request)).Created);
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("approved")]
    [InlineData("published")]
    [InlineData("batch")]
    public async Task Fill_cannot_reorder_protected_day_even_when_that_would_close_the_gap(string protection)
    {
        await using var fixture = await AutogenUniversalRangeTests.RangeFixture.CreateAsync(0, 1, WeekPreset.MonFri, 2, 6);
        var request = await SeedReorderDaysAsync(fixture);
        foreach (var row in await fixture.Db.TeacherDraftItems.Where(d => d.GroupId == 1).ToListAsync())
        {
            row.IsLocked = protection == "locked";
            row.Status = protection == "approved" ? DraftStatus.Published : DraftStatus.Draft;
            row.BatchKey = protection == "batch" ? "protected-day" : null;
            if (protection == "published")
            {
                fixture.Db.ScheduleItems.Add(new ScheduleItem
                {
                    Id = 1_000 + row.Id, Date = row.Date, DayOfWeek = row.DayOfWeek,
                    StartTime = row.StartTime, EndTime = row.EndTime, GroupId = row.GroupId,
                    ModuleId = row.ModuleId, ModuleTopicId = row.ModuleTopicId, LessonTypeId = row.LessonTypeId,
                    TeacherId = row.TeacherId, RoomId = row.RoomId
                });
                // Публікація переносить заняття до ScheduleItems і видаляє чернетку.
                fixture.Db.TeacherDraftItems.Remove(row);
            }
        }
        await fixture.Db.SaveChangesAsync();
        var before = await ReadDraftStateAsync(fixture.Db);
        var publishedBefore = await ReadPublishedStateAsync(fixture.Db);

        var result = Extract(await new TeacherDraftsAutogenService(fixture.Db).DraftAutoGen(request));

        Assert.Equal(0, result.Created);
        Assert.Equal(before, await ReadDraftStateAsync(fixture.Db));
        Assert.NotEqual(AutoGenCoverageStatus.FullyOccupied, result.Coverage!.Status);
        Assert.Equal(0, Extract(await new TeacherDraftsAutogenService(fixture.Db).DraftAutoGen(request)).Created);
        Assert.Equal(before, await ReadDraftStateAsync(fixture.Db));
        Assert.Equal(publishedBefore, await ReadPublishedStateAsync(fixture.Db));
        await AssertReorderHardRulesAsync(fixture);
    }

    [Theory]
    [InlineData("room")]
    [InlineData("teacher")]
    [InlineData("working-hours")]
    public async Task Fill_with_no_feasible_resource_assignment_keeps_existing_day_unchanged(string shortage)
    {
        await using var fixture = await AutogenUniversalRangeTests.RangeFixture.CreateAsync(0, 1, WeekPreset.MonFri, 2, 6);
        var request = await SeedReorderDaysAsync(fixture);
        var db = fixture.Db;
        if (shortage == "room")
        {
            db.ModuleRooms.RemoveRange(await db.ModuleRooms.Where(link => link.RoomId == 2).ToListAsync());
        }
        else if (shortage == "teacher")
        {
            // Єдиний викладач зайнятий іншою групою в останньому слоті.
            db.TeacherModules.RemoveRange(await db.TeacherModules.Where(link => link.TeacherId == 2).ToListAsync());
            (await db.TeacherDraftItems.SingleAsync(d => d.GroupId == 2)).TeacherId = 1;
        }
        else
        {
            // Викладачі є, але їхній робочий день закінчується до порожнього слоту.
            var lastStart = await db.TimeSlots.MaxAsync(s => s.Start);
            foreach (var hours in await db.TeacherWorkingHours.Where(h => h.TeacherId == 1).ToListAsync())
                hours.End = lastStart;
            db.TeacherModules.RemoveRange(await db.TeacherModules.Where(link => link.TeacherId == 2 && link.ModuleId != 2).ToListAsync());
        }
        await db.SaveChangesAsync();
        var before = await ReadDraftStateAsync(db);

        var result = Extract(await new TeacherDraftsAutogenService(db).DraftAutoGen(request));

        Assert.Equal(0, result.Created);
        Assert.Equal(1, result.Coverage!.MissingLessons);
        Assert.Equal(1, result.Coverage.EmptySlots);
        Assert.Equal(before, await ReadDraftStateAsync(db));
        Assert.Equal(0, Extract(await new TeacherDraftsAutogenService(db).DraftAutoGen(request)).Created);
        Assert.Equal(before, await ReadDraftStateAsync(db));
        await AssertReorderHardRulesAsync(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fill_repairs_two_days_preserves_peer_rows_and_is_idempotent(bool reverseInsertion)
    {
        await using var fixture = await AutogenUniversalRangeTests.RangeFixture.CreateAsync(0, 2, WeekPreset.MonFri, 2, 6);
        var request = await SeedReorderDaysAsync(fixture, reverseInsertion);
        var peersBefore = await ReadDraftStateAsync(fixture.Db, groupId: 2);

        var result = Extract(await new TeacherDraftsAutogenService(fixture.Db).DraftAutoGen(request));

        Assert.Equal(2, result.Created);
        Assert.Equal(AutoGenCoverageStatus.FullyOccupied, result.Coverage!.Status);
        Assert.Equal(12, result.Coverage.OccupiedSlots);
        Assert.Equal(0, result.Coverage.MissingLessons);
        Assert.Equal(0, result.Coverage.EmptySlots);
        Assert.Equal(0, result.Coverage.InternalGapSlots);
        var rows = await fixture.Db.TeacherDraftItems.AsNoTracking().Where(d => d.GroupId == 1).ToListAsync();
        foreach (var date in fixture.TeachingDates)
        {
            var day = rows.Where(d => d.Date == date).ToList();
            Assert.Equal(6, day.Count);
            Assert.Equal(6, day.Select(d => (d.StartTime, d.EndTime)).Distinct().Count());
            Assert.All(day, row => Assert.Equal(2, row.RoomId));
        }
        foreach (var (module, hours) in request.ModuleHours!)
            Assert.Equal(hours, rows.Count(d => d.ModuleId == module));
        Assert.Equal(peersBefore, await ReadDraftStateAsync(fixture.Db, groupId: 2));
        await AssertReorderHardRulesAsync(fixture);
        var completed = await ReadDraftStateAsync(fixture.Db);
        Assert.Equal(0, Extract(await new TeacherDraftsAutogenService(fixture.Db).DraftAutoGen(request)).Created);
        Assert.Equal(completed, await ReadDraftStateAsync(fixture.Db));
    }

    [Fact]
    public async Task Fill_expands_resource_choices_when_the_only_complete_day_uses_the_tenth_room()
    {
        await using var fixture = await AutogenUniversalRangeTests.RangeFixture.CreateAsync(0, 1, WeekPreset.MonFri, 2, 6);
        var request = await SeedReorderDaysAsync(fixture);
        var db = fixture.Db;
        for (var room = 3; room <= 10; room++)
        {
            db.Rooms.Add(new Room { Id = room, BuildingId = 1, Name = $"Синтетична аудиторія {room}", Capacity = 30 });
            db.ModuleRooms.Add(new ModuleRoom { ModuleId = 1, RoomId = room });
        }
        db.ModuleRooms.RemoveRange(await db.ModuleRooms.Where(link => link.ModuleId != 1 && link.RoomId == 2).ToListAsync());
        db.ModuleRooms.AddRange(new ModuleRoom { ModuleId = 2, RoomId = 10 }, new ModuleRoom { ModuleId = 3, RoomId = 10 });
        await db.SaveChangesAsync();
        var peerBefore = await ReadDraftStateAsync(db, 2);

        // Перехід між аудиторіями за п'ять хвилин неможливий; 10 — єдина
        // спільна аудиторія всіх модулів, вільна також в останньому слоті.
        var result = Extract(await new TeacherDraftsAutogenService(db).DraftAutoGen(request));

        Assert.True(result.Created == 1, string.Join(" | ", result.Warnings));
        Assert.Equal(AutoGenCoverageStatus.FullyOccupied, result.Coverage!.Status);
        Assert.Equal(6, result.Coverage.OccupiedSlots);
        Assert.All(await db.TeacherDraftItems.AsNoTracking().Where(d => d.GroupId == 1).ToListAsync(),
            row => Assert.Equal(10, row.RoomId));
        Assert.Equal(peerBefore, await ReadDraftStateAsync(db, 2));
        await AssertReorderHardRulesAsync(fixture);
    }

    [Fact]
    public void Repair_attempts_reserve_a_turn_for_each_pending_group_and_release_unused_reservations()
    {
        var budget = new ResidualRepairAttemptBudget(8);
        int[] pending = [1, 2, 3];
        for (var i = 0; i < 4; i++) Assert.True(budget.TryStart(1, pending));
        Assert.False(budget.TryStart(1, pending));
        Assert.Equal(4, budget.Used);
        for (var i = 0; i < 2; i++) Assert.True(budget.TryStart(2, pending));
        Assert.False(budget.TryStart(2, pending));
        for (var i = 0; i < 2; i++) Assert.True(budget.TryStart(3, pending));
        Assert.Equal(8, budget.Used);
        Assert.False(budget.TryStart(3, pending));

        var released = new ResidualRepairAttemptBudget(6);
        Assert.True(released.TryStart(1, [1, 2, 3]));
        Assert.True(released.TryStart(1, [1, 2, 3]));
        Assert.False(released.TryStart(1, [1, 2, 3]));
        // Друга й третя групи вже заповнені іншим етапом — резерв доступний першій.
        for (var i = 0; i < 4; i++) Assert.True(released.TryStart(1, [1]));
        Assert.False(released.TryStart(1, [1]));
        Assert.False(new ResidualRepairAttemptBudget(4).TryStart(9, [1, 2]));
    }

    [Fact]
    public async Task Fill_keeps_room_changes_available_when_the_break_matches_the_travel_time()
    {
        await using var fixture = await AutogenUniversalRangeTests.RangeFixture.CreateAsync(0, 1, WeekPreset.MonFri, 2, 6);
        var request = await SeedReorderDaysAsync(fixture);
        var db = fixture.Db;
        var slots = await db.TimeSlots.OrderBy(slot => slot.Start).ToListAsync();
        var rows = await db.TeacherDraftItems.ToListAsync();
        for (var index = 0; index < slots.Count; index++)
        {
            var oldStart = slots[index].Start;
            slots[index].Start = new TimeOnly(8 + index, 0);
            slots[index].End = slots[index].Start.AddMinutes(45);
            foreach (var row in rows.Where(row => row.StartTime == oldStart).ToArray())
            {
                row.StartTime = slots[index].Start;
                row.EndTime = slots[index].End;
            }
        }
        db.ModuleRooms.RemoveRange(await db.ModuleRooms.Where(link =>
            link.ModuleId == 2 && link.RoomId == 2 || link.ModuleId == 3 && link.RoomId == 1).ToListAsync());
        rows.Single(row => row.GroupId == 1 && row.ModuleId == 3).RoomId = 2;
        await db.SaveChangesAsync();
        await AssertReorderHardRulesAsync(fixture);

        var result = Extract(await new TeacherDraftsAutogenService(db).DraftAutoGen(request));

        Assert.Equal(1, result.Created);
        Assert.Equal(AutoGenCoverageStatus.FullyOccupied, result.Coverage!.Status);
        var saved = await db.TeacherDraftItems.AsNoTracking().Where(row => row.GroupId == 1).ToListAsync();
        Assert.Contains(saved, row => row.RoomId == 1);
        Assert.Contains(saved, row => row.RoomId == 2);
        await AssertReorderHardRulesAsync(fixture);
    }

    private static async Task<DraftAutoGenRequest> SeedReorderDaysAsync(
        AutogenUniversalRangeTests.RangeFixture fixture, bool reverseInsertion = false)
    {
        var db = fixture.Db;
        var days = fixture.TeachingDates.Count;
        var slots = await db.TimeSlots.OrderBy(s => s.SortOrder).ToListAsync();
        for (var i = 0; i < slots.Count; i++)
        {
            slots[i].Start = new TimeOnly(8, 0).AddMinutes(i * 50);
            slots[i].End = slots[i].Start.AddMinutes(45);
        }
        var topic = await db.ModuleTopics.SingleAsync();
        topic.TotalHours = topic.AuditoriumHours = 4 * days;
        (await db.ModulePlans.SingleAsync()).TargetHours = 4 * days;
        foreach (var id in new[] { 2, 3 })
        {
            db.Modules.Add(new BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities.Module
                { Id = id, CourseId = 1, Code = $"M{id}", Title = $"Тестовий модуль {id}" });
            db.ModuleTopics.Add(new ModuleTopic { Id = id, ModuleId = id, LessonTypeId = 1,
                Order = 1, TopicCode = $"{id}.1", TotalHours = days, AuditoriumHours = days });
            db.ModulePlans.Add(new ModulePlan { CourseId = 1, ModuleId = id, TargetHours = days, IsActive = true });
            db.ModuleSequenceItems.Add(new ModuleSequenceItem { CourseId = 1, ModuleId = id, Order = id, GroupOrder = 1 });
            db.TeacherModules.AddRange(new TeacherModule { TeacherId = 1, ModuleId = id }, new TeacherModule { TeacherId = 2, ModuleId = id });
            db.ModuleRooms.AddRange(new ModuleRoom { ModuleId = id, RoomId = 1 }, new ModuleRoom { ModuleId = id, RoomId = 2 });
        }
        int[] moduleOrder = [1, 2, 1, 1, 3];
        // А-Б-А-А-В-порожньо: ще одна А створила б третій сегмент.
        // За п'ять хвилин між слотами не можна перейти до іншої аудиторії.
        var rows = new List<TeacherDraftItem>();
        foreach (var date in fixture.TeachingDates)
        {
            rows.AddRange(moduleOrder.Select((module, index) => new TeacherDraftItem
            {
                GroupId = 1, ModuleId = module, ModuleTopicId = module, LessonTypeId = 1,
                Date = date, DayOfWeek = date.DayOfWeek, StartTime = slots[index].Start, EndTime = slots[index].End,
                TeacherId = 1, RoomId = 1
            }));
            rows.Add(new TeacherDraftItem
            {
                GroupId = 2, ModuleId = 2, ModuleTopicId = 2, LessonTypeId = 1,
                Date = date, DayOfWeek = date.DayOfWeek, StartTime = slots[5].Start, EndTime = slots[5].End,
                TeacherId = 2, RoomId = 1, IsLocked = true
            });
        }
        db.TeacherDraftItems.AddRange(reverseInsertion ? rows.AsEnumerable().Reverse() : rows);
        await db.SaveChangesAsync();
        return fixture.Request with { GroupIds = [1], ClearExisting = false,
            ModuleHours = new() { [1] = 4 * days, [2] = days, [3] = days } };
    }

    private static async Task<string> ReadDraftStateAsync(AppDbContext db, int? groupId = null)
    {
        // Порівнюємо всі збережені поля, включно з ревізіями, а не лише кількість рядків.
        var rows = await db.TeacherDraftItems.AsNoTracking().Where(d => groupId == null || d.GroupId == groupId)
            .OrderBy(d => d.Id).Select(d => new
            {
                d.Id, d.Revision, d.Date, d.DayOfWeek, d.StartTime, d.EndTime, d.LessonTypeId,
                d.GroupId, d.ModuleId, d.ModuleTopicId, d.TeacherId, d.RoomId, d.Status, d.PublishedItemId,
                d.BatchKey, d.ValidationWarnings, d.CreatedAt, d.UpdatedAt, d.IsLocked, d.IsSelfStudy, d.GenerationJobId
            }).ToListAsync();
        return System.Text.Json.JsonSerializer.Serialize(rows);
    }

    private static async Task AssertReorderHardRulesAsync(AutogenUniversalRangeTests.RangeFixture fixture)
        => Assert.Empty((await new TeacherDraftsAutogenHardRuleValidator(fixture.Db).ValidateAsync(
            new(1, [1, 2], fixture.From, fixture.To, AllowIncompleteDrafts: false))).Violations);

    private static async Task<string> ReadPublishedStateAsync(AppDbContext db)
        => System.Text.Json.JsonSerializer.Serialize(await db.ScheduleItems.AsNoTracking().OrderBy(d => d.Id)
            .Select(d => new
            {
                d.Id, d.Revision, d.Date, d.DayOfWeek, d.StartTime, d.EndTime, d.LessonTypeId,
                d.GroupId, d.ModuleId, d.ModuleTopicId, d.TeacherId, d.RoomId,
                d.BatchKey, d.IsLocked, d.IsSelfStudy
            }).ToListAsync());

    private sealed class MixedFixture(SqliteConnection connection, AppDbContext db) : IAsyncDisposable
    {
        public AppDbContext Db => db;
        public DraftAutoGenRequest Request { get; private set; } = null!;
        public int ExpectedRows { get; private set; }
        public int LockedCount { get; private set; }
        public string[] LockedFingerprint { get; private set; } = [];

        public static async Task<MixedFixture> CreateAsync(int length, int seed)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var fixture = new MixedFixture(connection, db);
            var from = new DateOnly(2031, 1, 1).AddDays(seed);
            var to = from.AddDays(length - 1);
            var holiday = from.AddDays(9);
            var dates = Enumerable.Range(0, length).Select(from.AddDays)
                .Where(d => d.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday && d != holiday).ToArray();
            var blockDays = (dates.Length - 1) / 2;
            var hours = new Dictionary<int, int> { [1] = blockDays, [2] = blockDays, [3] = dates.Length - 1 - blockDays, [4] = dates.Length - 1 - blockDays, [5] = 2 };
            fixture.ExpectedRows = dates.Length * 4;
            fixture.Request = new(from, ClearExisting: false, CourseId: 1, GroupIds: [1, 2], Days: WeekPreset.MonFri,
                ModuleHours: hours, SoftFill: true, RangeStartDate: from, RangeEndDate: to, PreferredFirstMaxSlotOrderOverride: 2,
                SoftOptions: new(MaxParallelGroupsPerModuleInSlot: 2, RecentRepeatWindowDays: 0, PreferredMaxDistinctModulesPerDay: 2, MaxDistinctModulesPerDay: 2));
            db.Courses.Add(new Course { Id = 1, Name = "Змішаний синтетичний курс", AcademicPeriodStartDate = from, DurationWeeks = (length + 6) / 7 });
            db.Groups.AddRange(Enumerable.Range(1, 2).Select(id => new Group { Id = id, CourseId = 1, Name = $"Тестова група {id}", StudentsCount = 20 }));
            db.Buildings.Add(new Building { Id = 1, Name = "Навчальний корпус" });
            db.LessonTypes.AddRange(new LessonTypeRef { Id = 1, Code = "PRACTICE", Name = "Практика" },
                new LessonTypeRef { Id = 2, Code = "LECTURE", Name = "Лекція", PreferredFirstInWeek = true });
            db.CalendarExceptions.Add(new CalendarException { CourseId = 1, Date = holiday, IsWorkingDay = false, Name = "Виняток календаря" });
            db.LunchConfigs.Add(new LunchConfig { CourseId = 1, Start = new(8, 45), End = new(9, 15) });
            db.TimeSlots.AddRange(new TimeSlot { CourseId = 1, Start = new(8, 0), End = new(8, 45), SortOrder = 1, IsActive = true },
                new TimeSlot { CourseId = 1, Start = new(9, 15), End = new(10, 0), SortOrder = 2, IsActive = true });
            foreach (var id in Enumerable.Range(1, 3))
            {
                db.Teachers.Add(new Teacher { Id = id, FullName = $"Спільний тестовий викладач {id}" });
                db.Rooms.Add(new Room { Id = id, BuildingId = 1, Name = $"Спеціалізована аудиторія {id}", Capacity = id == 3 ? 40 : 20 });
                foreach (var day in Enum.GetValues<DayOfWeek>())
                    db.TeacherWorkingHours.Add(new TeacherWorkingHour { TeacherId = id, DayOfWeek = day, Start = new(8, 0), End = new(10, 0) });
            }
            foreach (var (id, count) in hours)
            {
                var resourceId = id == 5 ? 3 : 1 + (id - 1) % 2;
                db.Modules.Add(new BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities.Module { Id = id, CourseId = 1, Code = $"M{id}", Title = $"Модуль {id}" });
                db.ModuleTopics.Add(new ModuleTopic { Id = id, ModuleId = id, LessonTypeId = id == 5 ? 2 : 1, Order = 1, TopicCode = $"{id}.1", TotalHours = count, AuditoriumHours = count });
                db.ModulePlans.Add(new ModulePlan { CourseId = 1, ModuleId = id, TargetHours = count, IsActive = true });
                db.ModuleSequenceItems.Add(new ModuleSequenceItem { CourseId = 1, ModuleId = id, Order = id == 5 ? 1 : id + 1, GroupOrder = id == 5 ? 1 : 2 + (id - 1) / 2 });
                db.TeacherModules.Add(new TeacherModule { TeacherId = resourceId, ModuleId = id });
                db.ModuleRooms.Add(new ModuleRoom { RoomId = resourceId, ModuleId = id });
            }
            // Спочатку перевіряємо свідка існування повного розкладу незалежним валідатором.
            for (var d = 0; d < dates.Length; d++)
                for (var group = 1; group <= 2; group++)
                    for (var slot = 0; slot < 2; slot++)
                    {
                        var module = d == 0 ? 5 : (d <= blockDays ? 1 : 3) + (slot + group - 1) % 2;
                        var resource = module == 5 ? 3 : 1 + (module - 1) % 2;
                        db.TeacherDraftItems.Add(new TeacherDraftItem { Date = dates[d], DayOfWeek = dates[d].DayOfWeek,
                            StartTime = slot == 0 ? new(8, 0) : new(9, 15), EndTime = slot == 0 ? new(8, 45) : new(10, 0),
                            GroupId = group, ModuleId = module, ModuleTopicId = module, LessonTypeId = module == 5 ? 2 : 1,
                            TeacherId = resource, RoomId = resource, IsLocked = d == 1 && slot == 0, Status = DraftStatus.Draft });
                    }
            await db.SaveChangesAsync();
            Assert.Empty((await fixture.ValidateAsync()).Violations);
            var witness = await new TeacherDraftsAutogenCoverageService(db).MeasureAsync([1, 2], from, to, WeekPreset.MonFri, hours, false);
            Assert.Equal(AutoGenCoverageStatus.FullyOccupied, witness.Status);
            var removable = await db.TeacherDraftItems.Where(d => !d.IsLocked).ToListAsync();
            db.TeacherDraftItems.RemoveRange(removable);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            fixture.LockedFingerprint = await fixture.ReadLockedAsync();
            fixture.LockedCount = fixture.LockedFingerprint.Length;
            return fixture;
        }

        public Task<TeacherDraftsAutogenHardRuleValidationResult> ValidateAsync() => new TeacherDraftsAutogenHardRuleValidator(db)
            .ValidateAsync(new(1, [1, 2], Request.RangeStartDate!.Value, Request.RangeEndDate!.Value, Request.Days, MaxParallelGroupsPerModuleInSlot: 2));
        public async Task<string[]> ReadLockedAsync() => (await db.TeacherDraftItems.AsNoTracking().Where(d => d.IsLocked).OrderBy(d => d.Id).ToListAsync())
            .Select(d => $"{d.Id}|{d.Date}|{d.StartTime}|{d.EndTime}|{d.GroupId}|{d.ModuleId}|{d.TeacherId}|{d.RoomId}|{d.UpdatedAt:O}").ToArray();
        public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
