using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Admin;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class AutogenLecturePackingTests
{
    [Fact]
    public async Task Draft_autogen_passes_cancellation_token_to_first_database_query()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var interceptor = new FirstQueryCancellationInterceptor();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        interceptor.Arm();
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await new TeacherDraftsAutogenService(db).DraftAutoGen(
                new DraftAutoGenRequest(new DateOnly(2026, 5, 4)),
                cancellation.Token);
        });

        Assert.True(interceptor.FirstQueryTokenCanBeCanceled);
    }

    [Theory]
    [InlineData(DraftStatus.Draft, false, null, true)]
    [InlineData(DraftStatus.Draft, true, null, false)]
    [InlineData(DraftStatus.Draft, false, "protected-batch", false)]
    [InlineData(DraftStatus.Draft, false, "   ", true)]
    [InlineData(DraftStatus.Published, false, null, false)]
    public void Moved_draft_synchronization_scope_excludes_protected_rows(
        DraftStatus status,
        bool isLocked,
        string? batchKey,
        bool expected)
    {
        var date = new DateOnly(2026, 5, 11);
        var draft = new TeacherDraftItem
        {
            Date = date,
            GroupId = 10,
            Status = status,
            IsLocked = isLocked,
            BatchKey = batchKey
        };

        var actual = AutogenDraftMutationPolicy.CanSynchronizeMovedDraft(
            draft,
            new HashSet<int> { draft.GroupId },
            date,
            date.AddDays(1));

        Assert.Equal(expected, actual);
        Assert.Equal(expected, AutogenDraftMutationPolicy.CanMutateInRepair(draft));
    }

    [Fact]
    public async Task Draft_autogen_preflight_reports_aggregate_calendar_capacity_without_weekend_gap()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var data = await SeedAggregateCalendarCapacityScenarioAsync(db);

        var service = new TeacherDraftsAutogenService(db);
        var action = await service.DraftAutoGen(new DraftAutoGenRequest(
            WeekStart: data.RangeStart,
            ClearExisting: true,
            CourseId: data.CourseId,
            GroupIds: new List<int> { data.GroupId },
            Days: WeekPreset.MonSat,
            ModuleHours: data.ModuleIds.ToDictionary(moduleId => moduleId, _ => 4),
            AllowIncompleteDrafts: true,
            RangeStartDate: data.RangeStart,
            RangeEndDate: data.RangeEnd));
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);

        var capacityItem = Assert.Single(result.Preflight!, item => item.Code == "calendar-capacity");
        Assert.Equal(3, capacityItem.Count);
        Assert.Contains("CalendarException", capacityItem.Recommendation, StringComparison.Ordinal);
        Assert.Contains("2026-05-16", capacityItem.Recommendation, StringComparison.Ordinal);
        Assert.Equal(5, result.Created);
        Assert.DoesNotContain(
            result.GapDetails ?? new List<AutoGenGapDetail>(),
            gap => gap.Date == data.RangeEnd);
        var persistedDates = await db.TeacherDraftItems
            .AsNoTracking()
            .OrderBy(item => item.Date)
            .Select(item => item.Date)
            .ToListAsync();
        Assert.Equal(5, persistedDates.Count);
        Assert.All(persistedDates, date => Assert.True(date < data.RangeEnd));
    }

    [Fact]
    public async Task Draft_autogen_soft_fill_reuses_terminal_topic_instead_of_creating_uncoded_lesson()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var data = await SeedTopicOverflowFillScenarioAsync(db);

        var request = new DraftAutoGenRequest(
            WeekStart: data.Date,
            ClearExisting: true,
            CourseId: data.CourseId,
            GroupIds: new List<int> { data.GroupId },
            Days: WeekPreset.MonFri,
            ModuleHours: new Dictionary<int, int> { [data.ModuleId] = 2 },
            SoftFill: true,
            RangeStartDate: data.Date,
            RangeEndDate: data.Date,
            SoftOptions: new DraftAutoGenSoftOptions(RecentRepeatWindowDays: 0));

        var service = new TeacherDraftsAutogenService(db);
        var action = await service.DraftAutoGen(request);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);

        var drafts = await db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => item.GroupId == data.GroupId && item.ModuleId == data.ModuleId)
            .OrderBy(item => item.StartTime)
            .ToListAsync();

        Assert.Equal(2, result.Created);
        Assert.Equal(2, drafts.Count);
        Assert.All(drafts, item => Assert.Equal(data.TopicId, item.ModuleTopicId));
        Assert.Empty(result.GapDetails ?? new List<AutoGenGapDetail>());
        Assert.Contains(result.Warnings, warning => warning.Contains("повторно використано тему", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Draft_autogen_soft_fill_advances_pending_topic_before_terminal_overflow()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var data = await SeedPendingTopicProgressScenarioAsync(db);
        var request = new DraftAutoGenRequest(
            WeekStart: data.StartDate,
            ClearExisting: false,
            CourseId: data.CourseId,
            GroupIds: new List<int> { data.GroupId },
            Days: WeekPreset.MonFri,
            ModuleHours: new Dictionary<int, int> { [data.ModuleId] = 2 },
            SoftFill: true,
            RangeStartDate: data.StartDate,
            RangeEndDate: data.StartDate.AddDays(1),
            SoftOptions: new DraftAutoGenSoftOptions(RecentRepeatWindowDays: 0));

        var service = new TeacherDraftsAutogenService(db);
        var action = await service.DraftAutoGen(request);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);

        var drafts = await db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => item.GroupId == data.GroupId && item.ModuleId == data.ModuleId)
            .OrderBy(item => item.Date)
            .ThenBy(item => item.StartTime)
            .ToListAsync();

        Assert.Equal(1, result.Created);
        Assert.Equal(2, drafts.Count);
        Assert.Single(drafts, item => item.ModuleTopicId == data.FirstTopicId);
        Assert.Single(drafts, item => item.ModuleTopicId == data.SecondTopicId);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("повторно використано тему", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Draft_autogen_shared_topic_uses_feasible_subflows_when_all_groups_have_no_common_gap()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var data = await SeedSharedTopicSubflowScenarioAsync(db);
        var request = new DraftAutoGenRequest(
            WeekStart: data.Date,
            ClearExisting: false,
            CourseId: data.CourseId,
            GroupIds: data.GroupIds,
            Days: WeekPreset.MonFri,
            ModuleHours: new Dictionary<int, int> { [data.ModuleId] = 2 },
            SoftFill: true,
            RangeStartDate: data.Date,
            RangeEndDate: data.Date.AddDays(1),
            SoftOptions: new DraftAutoGenSoftOptions(
                RecentRepeatWindowDays: 0,
                MaxParallelGroupsPerModuleInSlot: data.GroupIds.Count));

        var service = new TeacherDraftsAutogenService(db);
        var action = await service.DraftAutoGen(request);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);

        var sharedDrafts = await db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => data.GroupIds.Contains(item.GroupId)
                           && item.ModuleId == data.ModuleId
                           && item.ModuleTopicId == data.SharedTopicId)
            .ToListAsync();
        var clusters = sharedDrafts
            .GroupBy(item => new
            {
                item.Date,
                item.StartTime,
                item.EndTime,
                item.TeacherId,
                item.RoomId,
                item.ModuleTopicId
            })
            .ToList();

        Assert.Equal(data.GroupIds.Count, result.Created);
        Assert.Equal(data.GroupIds.Count, sharedDrafts.Count);
        Assert.Equal(data.GroupIds.Count, sharedDrafts.Select(item => item.GroupId).Distinct().Count());
        Assert.Equal(2, clusters.Count);
        Assert.Equal(
            new[] { 2, 5 },
            clusters
                .Select(cluster => cluster.Select(item => item.GroupId).Distinct().Count())
                .OrderBy(count => count)
                .ToArray());
        Assert.DoesNotContain(clusters, cluster => cluster.Select(item => item.GroupId).Distinct().Count() == 1);
    }

    [Fact]
    public async Task Draft_autogen_synchronizes_predecessors_after_preplacing_shared_checkpoint()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var course = new Course { Name = "Checkpoint course", DurationWeeks = 1 };
        var module = new Module { Code = "CP", Title = "Checkpoint module", Credits = 1, Course = course };
        var workType = new LessonTypeRef
        {
            Code = "WORK",
            Name = "Практичне заняття",
            IsActive = true,
            CountInPlan = true,
            RequiresTeacher = true,
            RequiresRoom = true,
            BlocksTeacher = true,
            BlocksRoom = true
        };
        var lectureType = new LessonTypeRef
        {
            Code = "LECTURE",
            Name = "Інтерактивна лекція",
            IsActive = true,
            CountInPlan = true,
            PreferredFirstInWeek = true,
            RequiresTeacher = true,
            RequiresRoom = true,
            BlocksTeacher = true,
            BlocksRoom = true
        };
        var building = new Building { Name = "Навчальний корпус" };
        db.AddRange(course, module, workType, lectureType, building);
        await db.SaveChangesAsync();

        var groups = Enumerable.Range(1, 7)
            .Select(index => new Group
            {
                Name = $"CP-{index}",
                StudentsCount = 20,
                CourseId = course.Id
            })
            .ToList();
        var teachers = Enumerable.Range(1, 7)
            .Select(index => new Teacher { FullName = $"Викладач {index}" })
            .ToList();
        var rooms = Enumerable.Range(1, 7)
            .Select(index => new Room
            {
                Name = $"CP-{index}",
                Capacity = 30,
                BuildingId = building.Id
            })
            .Append(new Room
            {
                Name = "Потокова аудиторія",
                Capacity = 500,
                BuildingId = building.Id
            })
            .ToList();
        db.AddRange(groups);
        db.AddRange(teachers);
        db.AddRange(rooms);
        await db.SaveChangesAsync();

        db.TeacherModules.AddRange(teachers.Select(teacher => new TeacherModule
        {
            TeacherId = teacher.Id,
            ModuleId = module.Id
        }));
        var workTopic = new ModuleTopic
        {
            ModuleId = module.Id,
            LessonTypeId = workType.Id,
            Order = 1,
            TopicCode = "CP.1",
            TotalHours = 2,
            AuditoriumHours = 2
        };
        var sharedTopic = new ModuleTopic
        {
            ModuleId = module.Id,
            LessonTypeId = lectureType.Id,
            Order = 2,
            TopicCode = "CP.2",
            TotalHours = 1,
            AuditoriumHours = 1
        };
        db.ModuleTopics.AddRange(workTopic, sharedTopic);
        var date = new DateOnly(2026, 5, 11);
        db.TimeSlots.AddRange(
            new TimeSlot { CourseId = course.Id, DayOfWeek = DayOfWeek.Monday, Start = new TimeOnly(9, 0), End = new TimeOnly(10, 0), SortOrder = 1, IsActive = true },
            new TimeSlot { CourseId = course.Id, DayOfWeek = DayOfWeek.Monday, Start = new TimeOnly(10, 0), End = new TimeOnly(11, 0), SortOrder = 2, IsActive = true },
            new TimeSlot { CourseId = course.Id, DayOfWeek = DayOfWeek.Monday, Start = new TimeOnly(11, 0), End = new TimeOnly(12, 0), SortOrder = 3, IsActive = true });
        await db.SaveChangesAsync();

        var service = new TeacherDraftsAutogenService(db);
        var action = await service.DraftAutoGen(new DraftAutoGenRequest(
            WeekStart: date,
            ClearExisting: true,
            CourseId: course.Id,
            GroupIds: groups.Select(group => group.Id).ToList(),
            ModuleHours: new Dictionary<int, int> { [module.Id] = 3 },
            RangeStartDate: date,
            RangeEndDate: date,
            SoftOptions: new DraftAutoGenSoftOptions(MaxParallelGroupsPerModuleInSlot: 4)));
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);
        var drafts = await db.TeacherDraftItems
            .AsNoTracking()
            .OrderBy(item => item.GroupId)
            .ThenBy(item => item.StartTime)
            .ToListAsync();

        Assert.Equal(21, result.Created);
        Assert.Equal(21, drafts.Count);
        Assert.All(drafts.GroupBy(item => item.GroupId), groupDrafts =>
        {
            Assert.Equal(new int?[] { workTopic.Id, workTopic.Id, sharedTopic.Id }, groupDrafts.Select(item => item.ModuleTopicId).ToArray());
        });
        var sharedRows = drafts.Where(item => item.ModuleTopicId == sharedTopic.Id).ToList();
        Assert.Equal(7, sharedRows.Count);
        Assert.Single(sharedRows.Select(item => item.RoomId).Distinct());
        Assert.All(sharedRows, item => Assert.Equal(rooms[^1].Id, item.RoomId));
        Assert.Equal(new TimeOnly(11, 0), Assert.Single(sharedRows.Select(item => item.StartTime).Distinct()));
        Assert.Empty(result.GapDetails ?? new List<AutoGenGapDetail>());
        var predecessorRows = drafts.Where(item => item.ModuleTopicId == workTopic.Id).ToList();
        Assert.All(predecessorRows.GroupBy(item => item.StartTime), slotRows =>
        {
            Assert.Equal(7, slotRows.Count());
            Assert.Equal(7, slotRows.Select(item => item.TeacherId).Distinct().Count());
            Assert.Equal(7, slotRows.Select(item => item.RoomId).Distinct().Count());
        });
        Assert.Empty(await TravelInvariantVerifier.FindViolationsAsync(db, course.Id, date, date));
    }

    [Fact]
    public async Task Draft_autogen_cross_date_chain_moves_another_module_before_filling_pending_topic()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var data = await SeedCrossDateDisplacementScenarioAsync(db);
        var request = new DraftAutoGenRequest(
            WeekStart: data.StartDate,
            ClearExisting: false,
            CourseId: data.CourseId,
            GroupIds: new List<int> { data.GroupId },
            Days: WeekPreset.MonFri,
            ModuleHours: new Dictionary<int, int>
            {
                [data.TargetModuleId] = 2,
                [data.MovableModuleId] = 1
            },
            SoftFill: true,
            RangeStartDate: data.StartDate,
            RangeEndDate: data.StartDate.AddDays(1),
            SoftOptions: new DraftAutoGenSoftOptions(RecentRepeatWindowDays: 0));

        var service = new TeacherDraftsAutogenService(db);
        var action = await service.DraftAutoGen(request);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);

        var drafts = await db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => item.GroupId == data.GroupId)
            .OrderBy(item => item.Date)
            .ThenBy(item => item.StartTime)
            .ToListAsync();

        Assert.Equal(1, result.Created);
        Assert.Empty(result.GapDetails ?? new List<AutoGenGapDetail>());
        Assert.Equal(3, drafts.Count);

        var movedDraft = Assert.Single(drafts, item => item.ModuleId == data.MovableModuleId);
        Assert.Equal(data.StartDate, movedDraft.Date);
        Assert.Equal(data.EarlyGapStart, movedDraft.StartTime);

        var targetDrafts = drafts
            .Where(item => item.ModuleId == data.TargetModuleId)
            .OrderBy(item => item.Date)
            .ThenBy(item => item.StartTime)
            .ToList();
        Assert.Equal(2, targetDrafts.Count);
        Assert.Equal(data.FirstTargetTopicId, targetDrafts[0].ModuleTopicId);
        Assert.Equal(data.SecondTargetTopicId, targetDrafts[1].ModuleTopicId);
        Assert.Equal(data.StartDate.AddDays(1), targetDrafts[1].Date);

        var hardRuleValidation = await new TeacherDraftsAutogenHardRuleValidator(db).ValidateAsync(
            new TeacherDraftsAutogenHardRuleValidationRequest(
                data.CourseId,
                new[] { data.GroupId },
                data.StartDate,
                data.StartDate.AddDays(1)));
        Assert.False(
            hardRuleValidation.HasViolations,
            $"Після міжденного ущільнення не повинно бути порушень жорстких правил: {string.Join(" | ", hardRuleValidation.Violations)}");
    }

    [Fact]
    public async Task Draft_autogen_strict_mode_reports_topic_capacity_deficit_without_erasing_remaining_hours()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var data = await SeedTopicOverflowFillScenarioAsync(db);
        var request = new DraftAutoGenRequest(
            WeekStart: data.Date,
            ClearExisting: true,
            CourseId: data.CourseId,
            GroupIds: new List<int> { data.GroupId },
            Days: WeekPreset.MonFri,
            ModuleHours: new Dictionary<int, int> { [data.ModuleId] = 2 },
            SoftFill: false,
            RangeStartDate: data.Date,
            RangeEndDate: data.Date,
            SoftOptions: new DraftAutoGenSoftOptions(RecentRepeatWindowDays: 0));

        var service = new TeacherDraftsAutogenService(db);
        var action = await service.DraftAutoGen(request);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);

        Assert.Equal(1, result.Created);
        var gap = Assert.Single(result.GapDetails ?? new List<AutoGenGapDetail>());
        Assert.Equal(data.ModuleId, gap.ModuleId);
        Assert.Contains("вичерпано теми", gap.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Draft_autogen_clear_existing_preserves_published_teacher_drafts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var data = await SeedTopicOverflowFillScenarioAsync(db);
        var lessonTypeId = await db.LessonTypes.Select(item => item.Id).SingleAsync();
        var teacherId = await db.Teachers.Select(item => item.Id).SingleAsync();
        var roomId = await db.Rooms.Select(item => item.Id).SingleAsync();
        var published = new TeacherDraftItem
        {
            Date = data.Date,
            DayOfWeek = data.Date.ToDateTime(TimeOnly.MinValue).DayOfWeek,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(9, 20),
            GroupId = data.GroupId,
            ModuleId = data.ModuleId,
            ModuleTopicId = data.TopicId,
            LessonTypeId = lessonTypeId,
            TeacherId = teacherId,
            RoomId = roomId,
            Status = DraftStatus.Published,
            IsLocked = false
        };
        db.TeacherDraftItems.Add(published);
        await db.SaveChangesAsync();
        var publishedId = published.Id;

        var request = new DraftAutoGenRequest(
            WeekStart: data.Date,
            ClearExisting: true,
            CourseId: data.CourseId,
            GroupIds: new List<int> { data.GroupId },
            Days: WeekPreset.MonFri,
            ModuleHours: new Dictionary<int, int> { [data.ModuleId] = 1 },
            RangeStartDate: data.Date,
            RangeEndDate: data.Date);

        var service = new TeacherDraftsAutogenService(db);
        var action = await service.DraftAutoGen(request);
        Assert.IsType<OkObjectResult>(action.Result);

        var preserved = await db.TeacherDraftItems
            .AsNoTracking()
            .SingleAsync(item => item.Id == publishedId);
        Assert.Equal(DraftStatus.Published, preserved.Status);
        Assert.False(preserved.IsLocked);
        Assert.Equal(data.TopicId, preserved.ModuleTopicId);
    }

    [Fact]
    public async Task Draft_autogen_ignores_future_topic_usage_when_generating_earlier_week()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var data = await SeedTopicOverflowFillScenarioAsync(db);
        var lessonTypeId = await db.LessonTypes.Select(item => item.Id).SingleAsync();
        var teacherId = await db.Teachers.Select(item => item.Id).SingleAsync();
        var roomId = await db.Rooms.Select(item => item.Id).SingleAsync();
        var futureDraft = new TeacherDraftItem
        {
            Date = data.Date.AddDays(7),
            DayOfWeek = DayOfWeek.Monday,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(9, 20),
            GroupId = data.GroupId,
            ModuleId = data.ModuleId,
            ModuleTopicId = data.TopicId,
            LessonTypeId = lessonTypeId,
            TeacherId = teacherId,
            RoomId = roomId,
            Status = DraftStatus.Draft
        };
        db.TeacherDraftItems.Add(futureDraft);
        await db.SaveChangesAsync();
        var futureDraftId = futureDraft.Id;

        var request = new DraftAutoGenRequest(
            WeekStart: data.Date,
            ClearExisting: true,
            CourseId: data.CourseId,
            GroupIds: new List<int> { data.GroupId },
            Days: WeekPreset.MonFri,
            ModuleHours: new Dictionary<int, int> { [data.ModuleId] = 1 },
            RangeStartDate: data.Date,
            RangeEndDate: data.Date);

        var service = new TeacherDraftsAutogenService(db);
        var action = await service.DraftAutoGen(request);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);

        Assert.Equal(1, result.Created);
        Assert.True(await db.TeacherDraftItems.AnyAsync(item => item.Id == futureDraftId));
        Assert.True(await db.TeacherDraftItems.AnyAsync(item => item.Date == data.Date && item.ModuleTopicId == data.TopicId));
    }

    [Fact]
    public async Task Draft_autogen_skips_adjacent_slot_when_students_cannot_reach_next_building()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var data = await SeedStudentTravelGapScenarioAsync(db);

        var request = new DraftAutoGenRequest(
            WeekStart: data.Date,
            ClearExisting: true,
            CourseId: data.CourseId,
            GroupIds: new List<int> { data.GroupId },
            Days: WeekPreset.MonFri,
            ModuleHours: new Dictionary<int, int> { [data.TargetModuleId] = 1 },
            RangeStartDate: data.Date,
            RangeEndDate: data.Date,
            SoftOptions: new DraftAutoGenSoftOptions(RecentRepeatWindowDays: 0));

        var service = new TeacherDraftsAutogenService(db);
        var action = await service.DraftAutoGen(request);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);

        var generated = await db.TeacherDraftItems
            .AsNoTracking()
            .Include(item => item.Room)
            .Where(item => item.GroupId == data.GroupId
                           && item.ModuleId == data.TargetModuleId)
            .SingleAsync();
        var violations = await TravelInvariantVerifier.FindViolationsAsync(db, data.CourseId, data.Date, data.Date);

        Assert.Equal(1, result.Created);
        Assert.Equal(data.ReachableStart, generated.StartTime);
        Assert.Equal(data.TargetBuildingId, generated.Room!.BuildingId);
        Assert.Empty(violations);
    }

    [Fact]
    public async Task Draft_autogen_keeps_seven_group_topic_lecture_in_one_large_room()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var data = await SeedSevenGroupLectureScenarioAsync(db);

        var request = new DraftAutoGenRequest(
            WeekStart: new DateOnly(2026, 4, 27),
            ClearExisting: true,
            CourseId: data.CourseId,
            GroupIds: data.GroupIds,
            Days: WeekPreset.MonFri,
            ModuleHours: new Dictionary<int, int>
            {
                [data.LectureModuleId] = 1,
                [data.WorkModuleId] = 1
            },
            RangeStartDate: new DateOnly(2026, 4, 27),
            RangeEndDate: new DateOnly(2026, 4, 27),
            SoftOptions: new DraftAutoGenSoftOptions(MaxParallelGroupsPerModuleInSlot: 7));

        var service = new TeacherDraftsAutogenService(db);
        var action = await service.DraftAutoGen(request);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);

        var lectureDrafts = await db.TeacherDraftItems
            .AsNoTracking()
            .Include(item => item.Group)
            .Include(item => item.Room)
            .Where(item => data.GroupIds.Contains(item.GroupId)
                           && item.ModuleId == data.LectureModuleId
                           && item.ModuleTopicId == data.LectureTopicId)
            .ToListAsync();

        var clusters = lectureDrafts
            .GroupBy(item => new
            {
                item.Date,
                item.StartTime,
                item.EndTime,
                item.LessonTypeId,
                item.TeacherId,
                item.RoomId,
                item.ModuleTopicId
            })
            .ToList();

        var cluster = Assert.Single(clusters);
        Assert.Equal(data.GroupIds.Count, lectureDrafts.Select(item => item.GroupId).Distinct().Count());
        Assert.Equal(data.GroupIds.Count, cluster.Select(item => item.GroupId).Distinct().Count());
        Assert.True(
            cluster.Sum(item => item.Group.StudentsCount) <= cluster.First().Room!.Capacity,
            "Спільна лекція має поміщатися в обрану аудиторію.");
    }

    [Theory]
    [InlineData(DraftStatus.Published, false, null)]
    [InlineData(DraftStatus.Draft, true, null)]
    [InlineData(DraftStatus.Draft, false, "protected-batch")]
    public async Task Draft_autogen_does_not_join_protected_shared_occurrence(
        DraftStatus status,
        bool isLocked,
        string? batchKey)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var data = await SeedSevenGroupLectureScenarioAsync(db);
        var date = new DateOnly(2026, 4, 27);
        var lectureTypeId = await db.ModuleTopics
            .Where(topic => topic.Id == data.LectureTopicId)
            .Select(topic => topic.LessonTypeId)
            .SingleAsync();
        var protectedDraft = new TeacherDraftItem
        {
            Date = date,
            DayOfWeek = date.DayOfWeek,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(9, 20),
            GroupId = data.GroupIds[0],
            ModuleId = data.LectureModuleId,
            ModuleTopicId = data.LectureTopicId,
            LessonTypeId = lectureTypeId,
            TeacherId = 1,
            RoomId = 1,
            Status = status,
            IsLocked = isLocked,
            BatchKey = batchKey
        };
        db.TeacherDraftItems.Add(protectedDraft);
        await db.SaveChangesAsync();

        var service = new TeacherDraftsAutogenService(db);
        var action = await service.DraftAutoGen(new DraftAutoGenRequest(
            WeekStart: date,
            ClearExisting: false,
            CourseId: data.CourseId,
            GroupIds: data.GroupIds,
            Days: WeekPreset.MonFri,
            ModuleHours: new Dictionary<int, int> { [data.LectureModuleId] = 1 },
            RangeStartDate: date,
            RangeEndDate: date,
            SoftOptions: new DraftAutoGenSoftOptions(MaxParallelGroupsPerModuleInSlot: 7)));

        Assert.IsType<OkObjectResult>(action.Result);
        var protectedSignatureSiblings = await db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => item.Id != protectedDraft.Id
                           && item.Date == protectedDraft.Date
                           && item.StartTime == protectedDraft.StartTime
                           && item.EndTime == protectedDraft.EndTime
                           && item.ModuleId == protectedDraft.ModuleId
                           && item.ModuleTopicId == protectedDraft.ModuleTopicId
                           && item.LessonTypeId == protectedDraft.LessonTypeId
                           && item.TeacherId == protectedDraft.TeacherId
                           && item.RoomId == protectedDraft.RoomId)
            .ToListAsync();
        Assert.Empty(protectedSignatureSiblings);
        var persistedProtected = await db.TeacherDraftItems
            .AsNoTracking()
            .SingleAsync(item => item.Id == protectedDraft.Id);
        Assert.Equal(status, persistedProtected.Status);
        Assert.Equal(isLocked, persistedProtected.IsLocked);
        Assert.Equal(batchKey, persistedProtected.BatchKey);
    }

    [Fact]
    public async Task Draft_autogen_splits_twenty_one_l3_groups_between_large_rooms_without_singletons()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var data = await SeedLecturePackingScenarioAsync(db);

        var request = new DraftAutoGenRequest(
            WeekStart: new DateOnly(2026, 4, 6),
            ClearExisting: true,
            CourseId: data.CourseId,
            GroupIds: data.GroupIds,
            Days: WeekPreset.MonFri,
            ModuleHours: new Dictionary<int, int> { [data.ModuleId] = 1 },
            RangeStartDate: new DateOnly(2026, 4, 6),
            RangeEndDate: new DateOnly(2026, 4, 6));

        var service = new TeacherDraftsAutogenService(db);
        var action = await service.DraftAutoGen(request);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);

        var drafts = await db.TeacherDraftItems
            .AsNoTracking()
            .Include(item => item.Group)
            .Include(item => item.Room)
            .Where(item => data.GroupIds.Contains(item.GroupId))
            .ToListAsync();

        var clusters = drafts
            .GroupBy(item => new
            {
                item.Date,
                item.StartTime,
                item.EndTime,
                item.ModuleId,
                item.LessonTypeId,
                item.TeacherId,
                item.RoomId
            })
            .Select(group => new LectureCluster(
                RoomId: group.Key.RoomId!.Value,
                RoomName: group.First().Room!.Name,
                GroupCount: group.Select(item => item.GroupId).Distinct().Count(),
                Students: group.Sum(item => item.Group.StudentsCount),
                RoomCapacity: group.First().Room!.Capacity))
            .OrderByDescending(cluster => cluster.GroupCount)
            .ThenBy(cluster => cluster.RoomId)
            .ToList();

        Assert.Equal(data.GroupIds.Count, result.Created);
        Assert.Equal(data.GroupIds.Count, drafts.Select(item => item.GroupId).Distinct().Count());
        Assert.Equal(2, clusters.Count);
        Assert.Contains(clusters, cluster => cluster.RoomId == data.BigRoomId && cluster.RoomName == "Актова зала" && cluster.GroupCount == 16 && cluster.Students == 480);
        Assert.Contains(clusters, cluster => cluster.RoomId == data.SmallRoomId && cluster.RoomName == "5/203" && cluster.GroupCount == 5 && cluster.Students == 150);
        Assert.All(clusters, cluster => Assert.True(
            cluster.Students <= cluster.RoomCapacity,
            $"Потік на {cluster.GroupCount} груп має {cluster.Students} слухачів, але аудиторія вміщує лише {cluster.RoomCapacity}."));
        Assert.DoesNotContain(clusters, cluster => cluster.GroupCount == 1);
    }

    [Fact]
    public async Task Draft_autogen_keeps_preferred_first_lessons_within_configured_slot_limit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var data = await SeedPreferredFirstLimitScenarioAsync(db);

        var request = new DraftAutoGenRequest(
            WeekStart: new DateOnly(2026, 5, 4),
            ClearExisting: true,
            CourseId: data.CourseId,
            GroupIds: data.GroupIds,
            Days: WeekPreset.MonFri,
            ModuleHours: new Dictionary<int, int>
            {
                [data.LectureModuleId] = 4
            },
            RangeStartDate: new DateOnly(2026, 5, 4),
            RangeEndDate: new DateOnly(2026, 5, 5),
            SoftOptions: new DraftAutoGenSoftOptions(RecentRepeatWindowDays: 0));

        var service = new TeacherDraftsAutogenService(db);
        var action = await service.DraftAutoGen(request);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);

        var drafts = await db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => data.GroupIds.Contains(item.GroupId))
            .OrderBy(item => item.Date)
            .ThenBy(item => item.StartTime)
            .ToListAsync();

        var timeSlots = await db.TimeSlots
            .AsNoTracking()
            .Where(slot => slot.CourseId == data.CourseId)
            .ToListAsync();
        var slotOrders = timeSlots
            .GroupBy(slot => slot.DayOfWeek!.Value)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(slot => slot.SortOrder)
                    .ThenBy(slot => slot.Start)
                    .Select((slot, index) => new { slot.Start, slot.End, Order = index + 1 })
                    .ToDictionary(slot => (slot.Start, slot.End), slot => slot.Order));

        var lectureDrafts = drafts
            .Where(item => item.LessonTypeId == data.LectureTypeId)
            .ToList();
        var draftSummary = string.Join(
            " | ",
            drafts.Select(item => $"{item.Date:yyyy-MM-dd} {item.StartTime:HH\\:mm}-{item.EndTime:HH\\:mm} M{item.ModuleId} LT{item.LessonTypeId} T{item.ModuleTopicId?.ToString() ?? "-"}"));

        Assert.True(
            lectureDrafts.Count == data.GroupIds.Count * 4,
            $"Очікували {data.GroupIds.Count * 4} лекцій, створено {lectureDrafts.Count}. Drafts: {draftSummary}. Result: created={result.Created}, skipped={result.Skipped}, warnings={string.Join(" | ", result.Warnings)}.");
        Assert.Equal(2, lectureDrafts.Select(item => item.Date).Distinct().Count());
        var lectureClusters = lectureDrafts
            .GroupBy(item => new { item.Date, item.StartTime, item.EndTime, item.ModuleTopicId, item.TeacherId, item.RoomId })
            .ToList();
        Assert.Equal(4, lectureClusters.Count);
        Assert.All(lectureClusters, cluster => Assert.Equal(data.GroupIds.Count, cluster.Select(item => item.GroupId).Distinct().Count()));
        var clusterSlotOrdersByDate = lectureClusters
            .GroupBy(cluster => cluster.Key.Date)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(cluster =>
                    {
                        var day = cluster.Key.Date.ToDateTime(TimeOnly.MinValue).DayOfWeek;
                        return slotOrders[day][(cluster.Key.StartTime, cluster.Key.EndTime)];
                    })
                    .OrderBy(order => order)
                    .ToList());
        Assert.All(clusterSlotOrdersByDate.Values, orders => Assert.Equal(new[] { 1, 2 }, orders));
        Assert.All(lectureDrafts, item =>
        {
            var day = item.Date.ToDateTime(TimeOnly.MinValue).DayOfWeek;
            var order = slotOrders[day][(item.StartTime, item.EndTime)];
            Assert.InRange(order, 1, data.MaxPreferredSlotOrder);
        });
    }

    [Fact]
    public async Task Draft_soft_fill_keeps_repeated_topic_hours_adjacent_when_possible()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var data = await SeedPreferredFirstLimitScenarioAsync(db);

        var request = new DraftAutoGenRequest(
            WeekStart: new DateOnly(2026, 5, 4),
            ClearExisting: true,
            CourseId: data.CourseId,
            GroupIds: data.GroupIds,
            Days: WeekPreset.MonFri,
            ModuleHours: new Dictionary<int, int>
            {
                [data.LectureModuleId] = 4
            },
            SoftFill: true,
            RangeStartDate: new DateOnly(2026, 5, 4),
            RangeEndDate: new DateOnly(2026, 5, 5),
            SoftOptions: new DraftAutoGenSoftOptions(RecentRepeatWindowDays: 0));

        var service = new TeacherDraftsAutogenService(db);
        var action = await service.DraftAutoGen(request);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);

        var lectureDrafts = await db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => data.GroupIds.Contains(item.GroupId)
                           && item.LessonTypeId == data.LectureTypeId)
            .OrderBy(item => item.Date)
            .ThenBy(item => item.StartTime)
            .ToListAsync();
        var draftSummary = string.Join(
            " | ",
            lectureDrafts.Select(item => $"{item.Date:yyyy-MM-dd} {item.StartTime:HH\\:mm}-{item.EndTime:HH\\:mm} G{item.GroupId} T{item.ModuleTopicId?.ToString() ?? "-"}"));

        Assert.True(
            lectureDrafts.Count == data.GroupIds.Count * 4,
            $"Очікували {data.GroupIds.Count * 4} лекцій у м'якому режимі, створено {lectureDrafts.Count}. Drafts: {draftSummary}. Result: created={result.Created}, skipped={result.Skipped}, warnings={string.Join(" | ", result.Warnings)}.");

        var timeSlots = await db.TimeSlots
            .AsNoTracking()
            .Where(slot => slot.CourseId == data.CourseId)
            .ToListAsync();
        var slotOrders = timeSlots
            .GroupBy(slot => slot.DayOfWeek!.Value)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(slot => slot.SortOrder)
                    .ThenBy(slot => slot.Start)
                    .Select((slot, index) => new { slot.Start, slot.End, Order = index + 1 })
                    .ToDictionary(slot => (slot.Start, slot.End), slot => slot.Order));
        var clusterSlotOrdersByDate = lectureDrafts
            .GroupBy(item => new { item.Date, item.StartTime, item.EndTime, item.ModuleTopicId, item.TeacherId, item.RoomId })
            .GroupBy(cluster => cluster.Key.Date)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(cluster =>
                    {
                        var day = cluster.Key.Date.ToDateTime(TimeOnly.MinValue).DayOfWeek;
                        return slotOrders[day][(cluster.Key.StartTime, cluster.Key.EndTime)];
                    })
                    .OrderBy(order => order)
                    .ToList());

        Assert.Equal(2, clusterSlotOrdersByDate.Count);
        Assert.All(clusterSlotOrdersByDate.Values, orders => Assert.Equal(new[] { 1, 2 }, orders));
    }

    [Fact]
    public async Task Draft_autogen_rejected_singleton_candidate_does_not_reassign_generated_room_blocker()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var data = await SeedRejectedCandidateSideEffectScenarioAsync(db);

        var request = new DraftAutoGenRequest(
            WeekStart: data.Date,
            ClearExisting: false,
            CourseId: data.CourseId,
            GroupIds: new List<int> { data.BlockerGroupId, data.RejectedGroupId },
            Days: WeekPreset.MonFri,
            ModuleHours: new Dictionary<int, int>
            {
                [data.BlockerModuleId] = 1,
                [data.RejectedLectureModuleId] = 1
            },
            SoftFill: true,
            RangeStartDate: data.Date,
            RangeEndDate: data.Date,
            SoftOptions: new DraftAutoGenSoftOptions(RecentRepeatWindowDays: 0));

        var service = new TeacherDraftsAutogenService(db);
        var action = await service.DraftAutoGen(request);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);

        var generatedBlocker = await db.TeacherDraftItems
            .AsNoTracking()
            .SingleAsync(item => item.GroupId == data.BlockerGroupId
                                 && item.ModuleId == data.BlockerModuleId);
        var rejectedLectureCreated = await db.TeacherDraftItems
            .AsNoTracking()
            .AnyAsync(item => item.GroupId == data.RejectedGroupId
                              && item.ModuleId == data.RejectedLectureModuleId);

        Assert.Equal(1, result.Created);
        Assert.Equal(data.FirstSlotStart, generatedBlocker.StartTime);
        Assert.Equal(data.BlockingRoomId, generatedBlocker.RoomId);
        Assert.False(rejectedLectureCreated);
        Assert.Contains(
            result.GapDetails ?? new List<AutoGenGapDetail>(),
            gap => gap.GroupId == data.RejectedGroupId && gap.Start == data.FirstSlotStart);
    }

    [Fact]
    public async Task Draft_autogen_repair_fills_slot_freed_by_moving_persisted_draft()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var data = await SeedPersistedMoveRepairScenarioAsync(db);

        var request = new DraftAutoGenRequest(
            WeekStart: data.Date,
            ClearExisting: false,
            CourseId: data.CourseId,
            GroupIds: new List<int> { data.GroupId },
            Days: WeekPreset.MonFri,
            ModuleHours: new Dictionary<int, int> { [data.TargetModuleId] = 1 },
            SoftFill: true,
            RangeStartDate: data.Date,
            RangeEndDate: data.Date,
            SoftOptions: new DraftAutoGenSoftOptions(RecentRepeatWindowDays: 0));

        var service = new TeacherDraftsAutogenService(db);
        var action = await service.DraftAutoGen(request);
        var failedResult = (action.Result as ObjectResult)?.Value as AutoGenResult;
        Assert.True(
            action.Result is OkObjectResult,
            $"Очікувався успішний repair-pass. Попередження: {string.Join(" | ", failedResult?.Warnings ?? new List<string>())}. Прогалини: {string.Join(" | ", failedResult?.GapDetails ?? new List<AutoGenGapDetail>())}");
        var ok = (OkObjectResult)action.Result!;
        var result = Assert.IsType<AutoGenResult>(ok.Value);

        var movedDraft = await db.TeacherDraftItems
            .AsNoTracking()
            .SingleAsync(item => item.Id == data.MovableDraftId);
        var generated = await db.TeacherDraftItems
            .AsNoTracking()
            .SingleAsync(item => item.GroupId == data.GroupId && item.ModuleId == data.TargetModuleId);

        Assert.Equal(1, result.Created);
        Assert.Equal(data.FirstSlotStart, movedDraft.StartTime);
        Assert.Equal(data.SecondSlotStart, generated.StartTime);
        Assert.True(movedDraft.UpdatedAt > new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Empty(result.GapDetails ?? new List<AutoGenGapDetail>());
    }

    [Fact]
    public async Task Draft_autogen_prioritizes_group_with_single_feasible_room_slot()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var data = await SeedGroupRegretScenarioAsync(db);

        var request = new DraftAutoGenRequest(
            WeekStart: data.Date,
            ClearExisting: true,
            CourseId: data.CourseId,
            GroupIds: new List<int> { data.ConstrainedGroupId, data.FlexibleGroupId },
            Days: WeekPreset.MonFri,
            ModuleHours: new Dictionary<int, int> { [data.TargetModuleId] = 1 },
            SoftFill: false,
            RangeStartDate: data.Date,
            RangeEndDate: data.Date,
            GroupRoomPreferences: new List<GroupRoomPreferenceDto>
            {
                new(data.ConstrainedGroupId, RoomIds: new List<int> { data.ScarceRoomId })
            },
            SoftOptions: new DraftAutoGenSoftOptions(RecentRepeatWindowDays: 0));

        var service = new TeacherDraftsAutogenService(db);
        var action = await service.DraftAutoGen(request);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);

        var generated = await db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => (item.GroupId == data.FlexibleGroupId || item.GroupId == data.ConstrainedGroupId)
                           && item.ModuleId == data.TargetModuleId
                           && item.LessonTypeId == data.TargetLessonTypeId)
            .OrderBy(item => item.GroupId)
            .ToListAsync();

        Assert.Equal(2, result.Created);
        Assert.Equal(2, generated.Count);

        var constrainedDraft = Assert.Single(generated, item => item.GroupId == data.ConstrainedGroupId);
        Assert.Equal(data.FirstSlotStart, constrainedDraft.StartTime);
        Assert.Equal(data.ScarceRoomId, constrainedDraft.RoomId);

        var flexibleDraft = Assert.Single(generated, item => item.GroupId == data.FlexibleGroupId);
        Assert.Equal(data.SecondSlotStart, flexibleDraft.StartTime);
        Assert.Equal(data.FlexibleRoomId, flexibleDraft.RoomId);

        var lockedBlocker = await db.TeacherDraftItems
            .AsNoTracking()
            .SingleAsync(item => item.Id == data.LockedBlockerId);
        Assert.True(lockedBlocker.IsLocked);
        Assert.Equal(data.SecondSlotStart, lockedBlocker.StartTime);

        var hardRuleValidation = await new TeacherDraftsAutogenHardRuleValidator(db).ValidateAsync(
            new TeacherDraftsAutogenHardRuleValidationRequest(
                data.CourseId,
                new[] { data.FlexibleGroupId, data.ConstrainedGroupId },
                data.Date,
                data.Date));
        Assert.False(
            hardRuleValidation.HasViolations,
            $"Після автогенерації не повинно бути порушень жорстких правил: {string.Join(" | ", hardRuleValidation.Violations)}");
    }

    [Fact]
    public async Task Draft_autogen_keeps_hard_feasible_work_before_forced_late_lecture_without_gaps()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var data = await SeedForcedLateLectureScenarioAsync(db, includeUnfillableMiddleSlot: false);

        var request = new DraftAutoGenRequest(
            WeekStart: data.Date,
            ClearExisting: true,
            CourseId: data.CourseId,
            GroupIds: data.GroupIds,
            Days: WeekPreset.MonFri,
            ModuleHours: new Dictionary<int, int>
            {
                [data.LectureModuleId] = 1,
                [data.WorkModuleId] = 1
            },
            SoftFill: false,
            RangeStartDate: data.Date,
            RangeEndDate: data.Date,
            SoftOptions: new DraftAutoGenSoftOptions(RecentRepeatWindowDays: 0));

        var service = new TeacherDraftsAutogenService(db);
        var action = await service.DraftAutoGen(request);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);

        var drafts = await db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => data.GroupIds.Contains(item.GroupId) && item.Date == data.Date)
            .OrderBy(item => item.StartTime)
            .ToListAsync();

        Assert.Equal(data.GroupIds.Count * 2, result.Created);
        Assert.Equal(data.GroupIds.Count * 2, drafts.Count);
        Assert.Empty(result.GapDetails ?? new List<AutoGenGapDetail>());

        Assert.All(data.GroupIds, groupId =>
        {
            var work = Assert.Single(drafts, item => item.GroupId == groupId && item.ModuleId == data.WorkModuleId);
            Assert.Equal(data.FirstSlotStart, work.StartTime);
            Assert.Equal(data.WorkLessonTypeId, work.LessonTypeId);

            var lecture = Assert.Single(drafts, item => item.GroupId == groupId && item.ModuleId == data.LectureModuleId);
            Assert.Equal(data.LateLectureStart, lecture.StartTime);
            Assert.Equal(data.LectureLessonTypeId, lecture.LessonTypeId);
        });

        var hardRuleValidation = await new TeacherDraftsAutogenHardRuleValidator(db).ValidateAsync(
            new TeacherDraftsAutogenHardRuleValidationRequest(
                data.CourseId,
                data.GroupIds,
                data.Date,
                data.Date));
        Assert.False(
            hardRuleValidation.HasViolations,
            $"Після автогенерації не повинно бути порушень жорстких правил: {string.Join(" | ", hardRuleValidation.Violations)}");
    }

    [Fact]
    public async Task Draft_autogen_reports_each_final_empty_slot_once()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var data = await SeedForcedLateLectureScenarioAsync(db, includeUnfillableMiddleSlot: true);

        var request = new DraftAutoGenRequest(
            WeekStart: data.Date,
            ClearExisting: true,
            CourseId: data.CourseId,
            GroupIds: data.GroupIds,
            Days: WeekPreset.MonFri,
            ModuleHours: new Dictionary<int, int>
            {
                [data.LectureModuleId] = 1,
                [data.WorkModuleId] = 1
            },
            SoftFill: false,
            RangeStartDate: data.Date,
            RangeEndDate: data.Date,
            SoftOptions: new DraftAutoGenSoftOptions(RecentRepeatWindowDays: 0));

        var service = new TeacherDraftsAutogenService(db);
        var action = await service.DraftAutoGen(request);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);

        var finalDrafts = await db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => data.GroupIds.Contains(item.GroupId) && item.Date == data.Date)
            .OrderBy(item => item.StartTime)
            .ToListAsync();
        var finalDraftSummary = string.Join(
            " | ",
            finalDrafts.Select(item => $"{item.StartTime:HH\\:mm}-{item.EndTime:HH\\:mm} M{item.ModuleId} LT{item.LessonTypeId}"));
        Assert.True(
            result.Created == data.GroupIds.Count * 2,
            $"Очікували {data.GroupIds.Count * 2} створені пари, отримано {result.Created}. Чернетки: {finalDraftSummary}. Попередження: {string.Join(" | ", result.Warnings)}.");

        var configuredSlots = await db.TimeSlots
            .AsNoTracking()
            .Where(slot => slot.IsActive
                           && slot.CourseId == data.CourseId
                           && slot.DayOfWeek == data.Date.ToDateTime(TimeOnly.MinValue).DayOfWeek)
            .OrderBy(slot => slot.SortOrder)
            .ThenBy(slot => slot.Start)
            .ToListAsync();
        var draftBusySlots = await db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => data.GroupIds.Contains(item.GroupId) && item.Date == data.Date)
            .Select(item => new { item.GroupId, item.Date, Start = item.StartTime, End = item.EndTime })
            .ToListAsync();
        var scheduleBusySlots = await db.ScheduleItems
            .AsNoTracking()
            .Where(item => data.GroupIds.Contains(item.GroupId) && item.Date == data.Date)
            .Select(item => new { item.GroupId, item.Date, Start = item.StartTime, End = item.EndTime })
            .ToListAsync();
        var occupiedSlotKeys = draftBusySlots
            .Concat(scheduleBusySlots)
            .Select(item => (item.GroupId, item.Date, item.Start, item.End))
            .ToHashSet();
        var expectedGapKeys = data.GroupIds
            .SelectMany(groupId => configuredSlots
                .Where(slot => !occupiedSlotKeys.Contains((groupId, data.Date, slot.Start, slot.End)))
                .Select(slot => (groupId, data.Date, slot.Start, slot.End)))
            .ToHashSet();
        var reportedGapKeys = (result.GapDetails ?? new List<AutoGenGapDetail>())
            .Select(gap => (gap.GroupId, gap.Date, gap.Start, gap.End))
            .ToList();

        Assert.Equal(reportedGapKeys.Count, reportedGapKeys.Distinct().Count());
        Assert.True(
            expectedGapKeys.SetEquals(reportedGapKeys),
            $"Фінальні порожні слоти мають точно відповідати GapDetails. Очікувалося: {string.Join(" | ", expectedGapKeys)}. Отримано: {string.Join(" | ", reportedGapKeys)}.");
        Assert.Equal(data.GroupIds.Count, reportedGapKeys.Count);
        Assert.All(reportedGapKeys, gap => Assert.Equal(data.MiddleSlotStart, gap.Start));
    }

    private static async Task<PersistedMoveRepairSeed> SeedPersistedMoveRepairScenarioAsync(AppDbContext db)
    {
        const int courseId = 500;
        const int groupId = 501;
        const int movableModuleId = 501;
        const int targetModuleId = 502;
        const int lessonTypeId = 501;
        const int movableTeacherId = 501;
        const int targetTeacherId = 502;
        const int buildingId = 501;
        const int movableRoomId = 501;
        const int targetRoomId = 502;
        var date = new DateOnly(2026, 6, 8);
        var firstSlotStart = new TimeOnly(8, 0);
        var firstSlotEnd = new TimeOnly(9, 20);
        var secondSlotStart = new TimeOnly(9, 30);
        var secondSlotEnd = new TimeOnly(10, 50);

        db.Courses.Add(new Course
        {
            Id = courseId,
            Name = "Repair зі збереженою чернеткою",
            DurationWeeks = 1
        });
        db.Groups.Add(new Group
        {
            Id = groupId,
            Name = "Група repair-pass",
            StudentsCount = 20,
            CourseId = courseId
        });
        db.LessonTypes.Add(new LessonTypeRef
        {
            Id = lessonTypeId,
            Code = "WORK",
            Name = "Практичне заняття",
            IsActive = true,
            RequiresRoom = true,
            RequiresTeacher = true,
            BlocksRoom = true,
            BlocksTeacher = true,
            CountInPlan = true,
            CountInLoad = true
        });
        db.Modules.AddRange(
            new Module
            {
                Id = movableModuleId,
                Code = "MOVABLE",
                Title = "Чернетка, яку можна пересунути",
                Credits = 1,
                CourseId = courseId
            },
            new Module
            {
                Id = targetModuleId,
                Code = "TARGET",
                Title = "Модуль із єдиним доступним слотом",
                Credits = 1,
                CourseId = courseId
            });
        db.ModulePlans.Add(new ModulePlan
        {
            CourseId = courseId,
            ModuleId = movableModuleId,
            TargetHours = 1,
            ScheduledHours = 1,
            IsActive = true
        });
        db.ModuleTopics.AddRange(
            new ModuleTopic
            {
                Id = 501,
                ModuleId = movableModuleId,
                Order = 1,
                TopicCode = "1.1.1.1",
                LessonTypeId = lessonTypeId,
                TotalHours = 1,
                AuditoriumHours = 1,
                SelfStudyHours = 0
            },
            new ModuleTopic
            {
                Id = 502,
                ModuleId = targetModuleId,
                Order = 1,
                TopicCode = "2.1.1.1",
                LessonTypeId = lessonTypeId,
                TotalHours = 1,
                AuditoriumHours = 1,
                SelfStudyHours = 0
            });
        db.Buildings.Add(new Building
        {
            Id = buildingId,
            Name = "Головний корпус"
        });
        db.Rooms.AddRange(
            new Room
            {
                Id = movableRoomId,
                Name = "Аудиторія рухомої пари",
                Capacity = 30,
                BuildingId = buildingId
            },
            new Room
            {
                Id = targetRoomId,
                Name = "Аудиторія цільової пари",
                Capacity = 30,
                BuildingId = buildingId
            });
        db.ModuleRooms.AddRange(
            new ModuleRoom
            {
                ModuleId = movableModuleId,
                RoomId = movableRoomId
            },
            new ModuleRoom
            {
                ModuleId = targetModuleId,
                RoomId = targetRoomId
            });
        db.Teachers.AddRange(
            new Teacher
            {
                Id = movableTeacherId,
                FullName = "Викладач рухомої пари"
            },
            new Teacher
            {
                Id = targetTeacherId,
                FullName = "Викладач цільової пари"
            });
        db.TeacherModules.AddRange(
            new TeacherModule
            {
                TeacherId = movableTeacherId,
                ModuleId = movableModuleId
            },
            new TeacherModule
            {
                TeacherId = targetTeacherId,
                ModuleId = targetModuleId
            });
        db.TeacherWorkingHours.AddRange(
            new TeacherWorkingHour
            {
                TeacherId = movableTeacherId,
                DayOfWeek = DayOfWeek.Monday,
                Start = firstSlotStart,
                End = secondSlotEnd
            },
            new TeacherWorkingHour
            {
                TeacherId = targetTeacherId,
                DayOfWeek = DayOfWeek.Monday,
                Start = secondSlotStart,
                End = secondSlotEnd
            });
        db.TimeSlots.AddRange(
            new TimeSlot
            {
                Id = 501,
                CourseId = courseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = firstSlotStart,
                End = firstSlotEnd,
                SortOrder = 1,
                IsActive = true
            },
            new TimeSlot
            {
                Id = 502,
                CourseId = courseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = secondSlotStart,
                End = secondSlotEnd,
                SortOrder = 2,
                IsActive = true
            });

        var movableDraft = new TeacherDraftItem
        {
            Date = date,
            DayOfWeek = DayOfWeek.Monday,
            StartTime = secondSlotStart,
            EndTime = secondSlotEnd,
            GroupId = groupId,
            ModuleId = movableModuleId,
            ModuleTopicId = 501,
            LessonTypeId = lessonTypeId,
            TeacherId = movableTeacherId,
            RoomId = movableRoomId,
            Status = DraftStatus.Draft,
            IsLocked = false,
            UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        db.TeacherDraftItems.Add(movableDraft);

        await db.SaveChangesAsync();
        return new PersistedMoveRepairSeed(
            courseId,
            groupId,
            targetModuleId,
            movableDraft.Id,
            date,
            firstSlotStart,
            secondSlotStart);
    }

    private static async Task<RejectedCandidateSideEffectSeed> SeedRejectedCandidateSideEffectScenarioAsync(AppDbContext db)
    {
        const int courseId = 400;
        const int blockerGroupId = 401;
        const int rejectedGroupId = 402;
        const int blockerModuleId = 401;
        const int rejectedLectureModuleId = 402;
        const int workLessonTypeId = 401;
        const int lectureLessonTypeId = 402;
        const int workTeacherId = 401;
        const int lectureTeacherId = 402;
        const int buildingId = 401;
        const int blockingRoomId = 401;
        const int alternativeRoomId = 402;
        var date = new DateOnly(2026, 6, 1);
        var firstSlotStart = new TimeOnly(8, 0);
        var firstSlotEnd = new TimeOnly(9, 20);
        var secondSlotStart = new TimeOnly(9, 30);
        var secondSlotEnd = new TimeOnly(10, 50);

        db.Courses.Add(new Course
        {
            Id = courseId,
            Name = "Відхилений кандидат без побічних змін",
            DurationWeeks = 1
        });
        db.Groups.AddRange(
            new Group
            {
                Id = blockerGroupId,
                Name = "Група з дефіцитною аудиторією",
                StudentsCount = 30,
                CourseId = courseId
            },
            new Group
            {
                Id = rejectedGroupId,
                Name = "Група відхиленої лекції",
                StudentsCount = 20,
                CourseId = courseId
            });
        db.LessonTypes.AddRange(
            new LessonTypeRef
            {
                Id = workLessonTypeId,
                Code = "WORK",
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
                Id = lectureLessonTypeId,
                Code = "LECTURE",
                Name = "Лекція",
                IsActive = true,
                RequiresRoom = true,
                RequiresTeacher = true,
                BlocksRoom = true,
                BlocksTeacher = true,
                CountInPlan = true,
                CountInLoad = true
            });
        db.Modules.AddRange(
            new Module
            {
                Id = blockerModuleId,
                Code = "ROOM-BLOCKER",
                Title = "Заняття-блокувальник аудиторії",
                Credits = 1,
                CourseId = courseId
            },
            new Module
            {
                Id = rejectedLectureModuleId,
                Code = "REJECTED-LECTURE",
                Title = "Одиночна лекція, яку слід відхилити",
                Credits = 1,
                CourseId = courseId
            });
        db.ModuleTopics.AddRange(
            new ModuleTopic
            {
                Id = 401,
                ModuleId = blockerModuleId,
                Order = 1,
                TopicCode = "1.1.1.1",
                LessonTypeId = workLessonTypeId,
                TotalHours = 1,
                AuditoriumHours = 1,
                SelfStudyHours = 0
            },
            new ModuleTopic
            {
                Id = 402,
                ModuleId = rejectedLectureModuleId,
                Order = 1,
                TopicCode = "2.1.1.1",
                LessonTypeId = lectureLessonTypeId,
                TotalHours = 1,
                AuditoriumHours = 1,
                SelfStudyHours = 0
            });
        db.Buildings.Add(new Building
        {
            Id = buildingId,
            Name = "Головний корпус"
        });
        db.Rooms.AddRange(
            new Room
            {
                Id = blockingRoomId,
                Name = "Точна аудиторія",
                Capacity = 30,
                BuildingId = buildingId
            },
            new Room
            {
                Id = alternativeRoomId,
                Name = "Запасна аудиторія",
                Capacity = 60,
                BuildingId = buildingId
            });
        db.ModuleRooms.AddRange(
            new ModuleRoom
            {
                ModuleId = blockerModuleId,
                RoomId = blockingRoomId
            },
            new ModuleRoom
            {
                ModuleId = blockerModuleId,
                RoomId = alternativeRoomId
            },
            new ModuleRoom
            {
                ModuleId = rejectedLectureModuleId,
                RoomId = blockingRoomId
            });
        db.Teachers.AddRange(
            new Teacher
            {
                Id = workTeacherId,
                FullName = "Викладач блокувальника"
            },
            new Teacher
            {
                Id = lectureTeacherId,
                FullName = "Викладач відхиленої лекції"
            });
        db.TeacherModules.AddRange(
            new TeacherModule
            {
                TeacherId = workTeacherId,
                ModuleId = blockerModuleId
            },
            new TeacherModule
            {
                TeacherId = lectureTeacherId,
                ModuleId = rejectedLectureModuleId
            });
        db.TeacherWorkingHours.AddRange(
            new TeacherWorkingHour
            {
                TeacherId = workTeacherId,
                DayOfWeek = DayOfWeek.Monday,
                Start = firstSlotStart,
                End = firstSlotEnd
            },
            new TeacherWorkingHour
            {
                TeacherId = lectureTeacherId,
                DayOfWeek = DayOfWeek.Monday,
                Start = firstSlotStart,
                End = secondSlotEnd
            });
        db.TimeSlots.AddRange(
            new TimeSlot
            {
                Id = 401,
                CourseId = courseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = firstSlotStart,
                End = firstSlotEnd,
                SortOrder = 1,
                IsActive = true
            },
            new TimeSlot
            {
                Id = 402,
                CourseId = courseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = secondSlotStart,
                End = secondSlotEnd,
                SortOrder = 2,
                IsActive = true
            });

        // Історичні пари в діапазоні залишають кожній групі лише її цільовий модуль.
        db.ScheduleItems.AddRange(
            new ScheduleItem
            {
                Date = date,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = new TimeOnly(6, 0),
                EndTime = new TimeOnly(7, 0),
                GroupId = blockerGroupId,
                ModuleId = rejectedLectureModuleId,
                ModuleTopicId = 402,
                LessonTypeId = lectureLessonTypeId,
                TeacherId = lectureTeacherId,
                RoomId = blockingRoomId,
                IsLocked = true
            },
            new ScheduleItem
            {
                Date = date,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = new TimeOnly(6, 0),
                EndTime = new TimeOnly(7, 0),
                GroupId = rejectedGroupId,
                ModuleId = blockerModuleId,
                ModuleTopicId = 401,
                LessonTypeId = workLessonTypeId,
                TeacherId = workTeacherId,
                RoomId = alternativeRoomId,
                IsLocked = true
            });

        await db.SaveChangesAsync();
        return new RejectedCandidateSideEffectSeed(
            courseId,
            blockerGroupId,
            rejectedGroupId,
            blockerModuleId,
            rejectedLectureModuleId,
            blockingRoomId,
            date,
            firstSlotStart);
    }

    private static async Task<ForcedLateLectureSeed> SeedForcedLateLectureScenarioAsync(
        AppDbContext db,
        bool includeUnfillableMiddleSlot)
    {
        const int courseId = 300;
        var groupIds = new List<int> { 301, 302 };
        const int lectureModuleId = 301;
        const int workModuleId = 302;
        const int lectureLessonTypeId = 301;
        const int workLessonTypeId = 302;
        const int lectureTeacherId = 301;
        const int firstWorkTeacherId = 302;
        const int secondWorkTeacherId = 303;
        const int buildingId = 301;
        const int largeRoomId = 301;
        const int smallRoomId = 302;
        var date = new DateOnly(2026, 5, 18);
        var firstSlotStart = new TimeOnly(8, 0);
        var firstSlotEnd = new TimeOnly(9, 20);
        var middleSlotStart = new TimeOnly(9, 30);
        var middleSlotEnd = new TimeOnly(10, 50);
        var lateLectureStart = new TimeOnly(11, 0);
        var lateLectureEnd = new TimeOnly(12, 20);

        db.Courses.Add(new Course
        {
            Id = courseId,
            Name = "Вимушена пізня лекція",
            DurationWeeks = 1
        });
        db.Groups.AddRange(
            new Group
            {
                Id = groupIds[0],
                Name = "Тестова група 1",
                StudentsCount = 20,
                CourseId = courseId
            },
            new Group
            {
                Id = groupIds[1],
                Name = "Тестова група 2",
                StudentsCount = 20,
                CourseId = courseId
            });
        db.LessonTypes.AddRange(
            new LessonTypeRef
            {
                Id = lectureLessonTypeId,
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
            },
            new LessonTypeRef
            {
                Id = workLessonTypeId,
                Code = "WORK",
                Name = "Практичне заняття",
                IsActive = true,
                RequiresRoom = true,
                RequiresTeacher = true,
                BlocksRoom = true,
                BlocksTeacher = true,
                CountInPlan = true,
                CountInLoad = true
            });
        db.Modules.AddRange(
            new Module
            {
                Id = lectureModuleId,
                Code = "LATE-LECTURE",
                Title = "Лекційний модуль",
                Credits = 1,
                CourseId = courseId
            },
            new Module
            {
                Id = workModuleId,
                Code = "EARLY-WORK",
                Title = "Практичний модуль",
                Credits = 1,
                CourseId = courseId
            });
        db.ModuleTopics.AddRange(
            new ModuleTopic
            {
                Id = 301,
                ModuleId = lectureModuleId,
                Order = 1,
                TopicCode = "1.1.1.1",
                LessonTypeId = lectureLessonTypeId,
                TotalHours = 1,
                AuditoriumHours = 1,
                SelfStudyHours = 0
            },
            new ModuleTopic
            {
                Id = 302,
                ModuleId = workModuleId,
                Order = 1,
                TopicCode = "2.1.1.1",
                LessonTypeId = workLessonTypeId,
                TotalHours = 1,
                AuditoriumHours = 1,
                SelfStudyHours = 0
            });
        db.ModuleSequenceItems.AddRange(
            new ModuleSequenceItem
            {
                CourseId = courseId,
                ModuleId = lectureModuleId,
                GroupOrder = 1,
                Order = 1
            },
            new ModuleSequenceItem
            {
                CourseId = courseId,
                ModuleId = workModuleId,
                GroupOrder = 2,
                Order = 2
            });
        db.Buildings.Add(new Building
        {
            Id = buildingId,
            Name = "Головний корпус"
        });
        db.Rooms.AddRange(
            new Room
            {
                Id = largeRoomId,
                Name = "Велика аудиторія",
                Capacity = 50,
                BuildingId = buildingId
            },
            new Room
            {
                Id = smallRoomId,
                Name = "Мала аудиторія",
                Capacity = 25,
                BuildingId = buildingId
            });
        db.Teachers.AddRange(
            new Teacher
            {
                Id = lectureTeacherId,
                FullName = "Викладач лекції"
            },
            new Teacher
            {
                Id = firstWorkTeacherId,
                FullName = "Викладач практики 1"
            },
            new Teacher
            {
                Id = secondWorkTeacherId,
                FullName = "Викладач практики 2"
            });
        db.TeacherModules.AddRange(
            new TeacherModule
            {
                TeacherId = lectureTeacherId,
                ModuleId = lectureModuleId
            },
            new TeacherModule
            {
                TeacherId = firstWorkTeacherId,
                ModuleId = workModuleId
            },
            new TeacherModule
            {
                TeacherId = secondWorkTeacherId,
                ModuleId = workModuleId
            });
        db.TeacherWorkingHours.AddRange(
            new TeacherWorkingHour
            {
                TeacherId = firstWorkTeacherId,
                DayOfWeek = DayOfWeek.Monday,
                Start = firstSlotStart,
                End = firstSlotEnd
            },
            new TeacherWorkingHour
            {
                TeacherId = secondWorkTeacherId,
                DayOfWeek = DayOfWeek.Monday,
                Start = firstSlotStart,
                End = firstSlotEnd
            },
            new TeacherWorkingHour
            {
                TeacherId = lectureTeacherId,
                DayOfWeek = DayOfWeek.Monday,
                Start = lateLectureStart,
                End = lateLectureEnd
            });
        db.TimeSlots.Add(new TimeSlot
        {
            Id = 301,
            CourseId = courseId,
            DayOfWeek = DayOfWeek.Monday,
            Start = firstSlotStart,
            End = firstSlotEnd,
            SortOrder = 1,
            IsActive = true
        });
        if (includeUnfillableMiddleSlot)
        {
            db.TimeSlots.Add(new TimeSlot
            {
                Id = 302,
                CourseId = courseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = middleSlotStart,
                End = middleSlotEnd,
                SortOrder = 2,
                IsActive = true
            });
        }
        db.TimeSlots.Add(new TimeSlot
        {
            Id = 303,
            CourseId = courseId,
            DayOfWeek = DayOfWeek.Monday,
            Start = lateLectureStart,
            End = lateLectureEnd,
            SortOrder = includeUnfillableMiddleSlot ? 3 : 2,
            IsActive = true
        });

        await db.SaveChangesAsync();
        return new ForcedLateLectureSeed(
            courseId,
            groupIds,
            lectureModuleId,
            workModuleId,
            lectureLessonTypeId,
            workLessonTypeId,
            date,
            firstSlotStart,
            middleSlotStart,
            lateLectureStart);
    }

    private static async Task<GroupRegretSeed> SeedGroupRegretScenarioAsync(AppDbContext db)
    {
        const int courseId = 200;
        const int flexibleGroupId = 201;
        const int constrainedGroupId = 202;
        const int externalGroupId = 203;
        const int targetModuleId = 201;
        const int blockerModuleId = 202;
        const int targetLessonTypeId = 201;
        const int breakLessonTypeId = 202;
        const int firstTeacherId = 201;
        const int secondTeacherId = 202;
        const int buildingId = 201;
        const int scarceRoomId = 201;
        const int flexibleRoomId = 202;
        var date = new DateOnly(2026, 5, 11);
        var firstSlotStart = new TimeOnly(8, 0);
        var firstSlotEnd = new TimeOnly(9, 20);
        var secondSlotStart = new TimeOnly(9, 30);
        var secondSlotEnd = new TimeOnly(10, 50);

        db.Courses.Add(new Course
        {
            Id = courseId,
            Name = "Group regret",
            DurationWeeks = 1
        });
        db.Groups.AddRange(
            new Group
            {
                Id = flexibleGroupId,
                Name = "Flexible group",
                StudentsCount = 20,
                CourseId = courseId
            },
            new Group
            {
                Id = constrainedGroupId,
                Name = "Constrained group",
                StudentsCount = 20,
                CourseId = courseId
            },
            new Group
            {
                Id = externalGroupId,
                Name = "External group",
                StudentsCount = 20,
                CourseId = courseId
            });
        db.LessonTypes.AddRange(
            new LessonTypeRef
            {
                Id = targetLessonTypeId,
                Code = "WORK",
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
                Id = breakLessonTypeId,
                Code = "BREAK",
                Name = "Службове блокування",
                IsActive = true,
                RequiresRoom = false,
                RequiresTeacher = false,
                BlocksRoom = true,
                BlocksTeacher = false,
                CountInPlan = false,
                CountInLoad = false
            });
        db.Modules.AddRange(
            new Module
            {
                Id = targetModuleId,
                Code = "REGRET",
                Title = "Дефіцитний ресурс",
                Credits = 1,
                CourseId = courseId
            },
            new Module
            {
                Id = blockerModuleId,
                Code = "BLOCK",
                Title = "Службове блокування",
                Credits = 1,
                CourseId = courseId
            });
        db.ModuleTopics.Add(new ModuleTopic
        {
            Id = 201,
            ModuleId = targetModuleId,
            Order = 1,
            TopicCode = "1.1.1.1",
            LessonTypeId = targetLessonTypeId,
            TotalHours = 1,
            AuditoriumHours = 1,
            SelfStudyHours = 0
        });
        db.Buildings.Add(new Building
        {
            Id = buildingId,
            Name = "Main"
        });
        db.Rooms.AddRange(
            new Room
            {
                Id = scarceRoomId,
                Name = "R1",
                Capacity = 40,
                BuildingId = buildingId
            },
            new Room
            {
                Id = flexibleRoomId,
                Name = "R2",
                Capacity = 40,
                BuildingId = buildingId
            });
        db.Teachers.AddRange(
            new Teacher
            {
                Id = firstTeacherId,
                FullName = "Teacher One"
            },
            new Teacher
            {
                Id = secondTeacherId,
                FullName = "Teacher Two"
            });
        db.TeacherModules.AddRange(
            new TeacherModule
            {
                TeacherId = firstTeacherId,
                ModuleId = targetModuleId
            },
            new TeacherModule
            {
                TeacherId = secondTeacherId,
                ModuleId = targetModuleId
            });
        db.TimeSlots.AddRange(
            new TimeSlot
            {
                Id = 201,
                CourseId = courseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = firstSlotStart,
                End = firstSlotEnd,
                SortOrder = 1,
                IsActive = true
            },
            new TimeSlot
            {
                Id = 202,
                CourseId = courseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = secondSlotStart,
                End = secondSlotEnd,
                SortOrder = 2,
                IsActive = true
            });

        var lockedBlocker = new TeacherDraftItem
        {
            Date = date,
            DayOfWeek = DayOfWeek.Monday,
            StartTime = secondSlotStart,
            EndTime = secondSlotEnd,
            GroupId = constrainedGroupId,
            ModuleId = blockerModuleId,
            LessonTypeId = breakLessonTypeId,
            Status = DraftStatus.Draft,
            IsLocked = true
        };
        db.TeacherDraftItems.Add(lockedBlocker);
        db.ScheduleItems.AddRange(
            new ScheduleItem
            {
                Date = date,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = firstSlotStart,
                EndTime = firstSlotEnd,
                GroupId = externalGroupId,
                ModuleId = blockerModuleId,
                LessonTypeId = breakLessonTypeId,
                RoomId = flexibleRoomId,
                IsLocked = true
            },
            new ScheduleItem
            {
                Date = date,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = secondSlotStart,
                EndTime = secondSlotEnd,
                GroupId = externalGroupId,
                ModuleId = blockerModuleId,
                LessonTypeId = breakLessonTypeId,
                RoomId = scarceRoomId,
                IsLocked = true
            });

        await db.SaveChangesAsync();
        return new GroupRegretSeed(
            courseId,
            flexibleGroupId,
            constrainedGroupId,
            targetModuleId,
            targetLessonTypeId,
            scarceRoomId,
            flexibleRoomId,
            lockedBlocker.Id,
            date,
            firstSlotStart,
            secondSlotStart);
    }

    private static async Task<LecturePackingSeed> SeedLecturePackingScenarioAsync(AppDbContext db)
    {
        const int courseId = 1;
        const int moduleId = 1;
        const int lectureTypeId = 1;
        const int bigRoomId = 1;
        const int smallRoomId = 2;

        db.Courses.Add(new Course
        {
            Id = courseId,
            Name = "L-3",
            DurationWeeks = 1
        });
        db.LessonTypes.Add(new LessonTypeRef
        {
            Id = lectureTypeId,
            Code = "LECTURE",
            Name = "Lecture",
            IsActive = true,
            RequiresRoom = true,
            RequiresTeacher = true,
            BlocksRoom = true,
            BlocksTeacher = true,
            CountInPlan = true,
            CountInLoad = true
        });
        db.Modules.Add(new Module
        {
            Id = moduleId,
            Code = "2",
            Title = "Shared lecture",
            Credits = 1,
            CourseId = courseId
        });
        db.Buildings.Add(new Building
        {
            Id = 1,
            Name = "Main"
        });
        db.Rooms.AddRange(
            new Room
            {
                Id = bigRoomId,
                Name = "Актова зала",
                Capacity = 500,
                BuildingId = 1
            },
            new Room
            {
                Id = smallRoomId,
                Name = "5/203",
                Capacity = 250,
                BuildingId = 1
            });
        db.Teachers.AddRange(
            new Teacher
            {
                Id = 1,
                FullName = "Teacher One"
            },
            new Teacher
            {
                Id = 2,
                FullName = "Teacher Two"
            });
        db.TeacherModules.AddRange(
            new TeacherModule
            {
                TeacherId = 1,
                ModuleId = moduleId
            },
            new TeacherModule
            {
                TeacherId = 2,
                ModuleId = moduleId
            });
        db.TimeSlots.Add(new TimeSlot
        {
            Id = 1,
            CourseId = courseId,
            DayOfWeek = DayOfWeek.Monday,
            Start = new TimeOnly(8, 0),
            End = new TimeOnly(9, 20),
            SortOrder = 1,
            IsActive = true
        });

        var groupIds = Enumerable.Range(9301, 21).ToList();
        foreach (var groupId in groupIds)
        {
            db.Groups.Add(new Group
            {
                Id = groupId,
                Name = groupId.ToString(),
                StudentsCount = 30,
                CourseId = courseId
            });
        }

        await db.SaveChangesAsync();
        return new LecturePackingSeed(courseId, moduleId, bigRoomId, smallRoomId, groupIds);
    }

    private static async Task<AggregateCalendarCapacitySeed> SeedAggregateCalendarCapacityScenarioAsync(AppDbContext db)
    {
        const int courseId = 9000;
        const int groupId = 9000;
        const int lessonTypeId = 9000;
        const int buildingId = 9000;
        const int roomId = 9000;
        var moduleIds = new[] { 9001, 9002 };
        var rangeStart = new DateOnly(2026, 5, 11);
        var rangeEnd = new DateOnly(2026, 5, 16);

        db.Courses.Add(new Course { Id = courseId, Name = "Calendar capacity", DurationWeeks = 1 });
        db.Groups.Add(new Group
        {
            Id = groupId,
            Name = "CAL-1",
            StudentsCount = 20,
            CourseId = courseId
        });
        db.LessonTypes.Add(new LessonTypeRef
        {
            Id = lessonTypeId,
            Code = "WORK",
            Name = "Практичне заняття",
            IsActive = true,
            RequiresRoom = true,
            RequiresTeacher = true,
            BlocksRoom = true,
            BlocksTeacher = true,
            CountInPlan = true,
            CountInLoad = true
        });
        db.Buildings.Add(new Building { Id = buildingId, Name = "Main" });
        db.Rooms.Add(new Room
        {
            Id = roomId,
            Name = "CAL-101",
            Capacity = 30,
            BuildingId = buildingId
        });
        foreach (var (moduleId, index) in moduleIds.Select((moduleId, index) => (moduleId, index)))
        {
            var teacherId = 9001 + index;
            db.Modules.Add(new Module
            {
                Id = moduleId,
                Code = $"CAL-{index + 1}",
                Title = $"Календарний модуль {index + 1}",
                Credits = 1,
                CourseId = courseId
            });
            db.ModuleTopics.Add(new ModuleTopic
            {
                Id = moduleId,
                ModuleId = moduleId,
                Order = 1,
                TopicCode = $"CAL.{index + 1}",
                LessonTypeId = lessonTypeId,
                TotalHours = 4,
                AuditoriumHours = 4
            });
            db.Teachers.Add(new Teacher
            {
                Id = teacherId,
                FullName = $"Викладач календаря {index + 1}"
            });
            db.TeacherModules.Add(new TeacherModule
            {
                TeacherId = teacherId,
                ModuleId = moduleId
            });
        }
        db.TimeSlots.AddRange(Enumerable.Range(0, 6).Select(index => new TimeSlot
        {
            Id = 9000 + index,
            CourseId = courseId,
            DayOfWeek = rangeStart.AddDays(index).DayOfWeek,
            Start = new TimeOnly(9, 0),
            End = new TimeOnly(10, 0),
            SortOrder = 1,
            IsActive = true
        }));

        await db.SaveChangesAsync();
        return new AggregateCalendarCapacitySeed(courseId, groupId, moduleIds, rangeStart, rangeEnd);
    }

    private static async Task<TopicOverflowFillSeed> SeedTopicOverflowFillScenarioAsync(AppDbContext db)
    {
        const int courseId = 100;
        const int groupId = 100;
        const int moduleId = 100;
        const int topicId = 100;
        const int lessonTypeId = 100;
        const int teacherId = 100;
        const int buildingId = 100;
        const int roomId = 100;
        var date = new DateOnly(2026, 5, 4);

        db.Courses.Add(new Course
        {
            Id = courseId,
            Name = "Topic overflow",
            DurationWeeks = 1
        });
        db.Groups.Add(new Group
        {
            Id = groupId,
            Name = "9301",
            StudentsCount = 24,
            CourseId = courseId
        });
        db.LessonTypes.Add(new LessonTypeRef
        {
            Id = lessonTypeId,
            Code = "DISCUSSION",
            Name = "Дискусія",
            IsActive = true,
            RequiresRoom = true,
            RequiresTeacher = true,
            BlocksRoom = true,
            BlocksTeacher = true,
            CountInPlan = true,
            CountInLoad = true
        });
        db.Modules.Add(new Module
        {
            Id = moduleId,
            Code = "13",
            Title = "Дослідницький проєкт",
            Credits = 1,
            CourseId = courseId
        });
        db.ModuleTopics.Add(new ModuleTopic
        {
            Id = topicId,
            ModuleId = moduleId,
            Order = 1,
            TopicCode = "13.1.1.1",
            LessonTypeId = lessonTypeId,
            TotalHours = 1,
            AuditoriumHours = 1,
            SelfStudyHours = 0,
            IsInterAssembly = false,
            SelfStudyBySupervisor = false
        });
        db.Buildings.Add(new Building
        {
            Id = buildingId,
            Name = "Main"
        });
        db.Rooms.Add(new Room
        {
            Id = roomId,
            Name = "3/301",
            Capacity = 30,
            BuildingId = buildingId
        });
        db.Teachers.Add(new Teacher
        {
            Id = teacherId,
            FullName = "Teacher One"
        });
        db.TeacherModules.Add(new TeacherModule
        {
            TeacherId = teacherId,
            ModuleId = moduleId
        });
        db.TimeSlots.AddRange(
            new TimeSlot
            {
                Id = 100,
                CourseId = courseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(8, 0),
                End = new TimeOnly(9, 20),
                SortOrder = 1,
                IsActive = true
            },
            new TimeSlot
            {
                Id = 101,
                CourseId = courseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(9, 30),
                End = new TimeOnly(10, 50),
                SortOrder = 2,
                IsActive = true
            });

        await db.SaveChangesAsync();
        return new TopicOverflowFillSeed(courseId, groupId, moduleId, topicId, date);
    }

    private static async Task<PendingTopicProgressSeed> SeedPendingTopicProgressScenarioAsync(AppDbContext db)
    {
        const int courseId = 700;
        const int groupId = 700;
        const int moduleId = 700;
        const int firstTopicId = 700;
        const int secondTopicId = 701;
        const int lessonTypeId = 700;
        const int teacherId = 700;
        const int buildingId = 700;
        const int roomId = 700;
        var startDate = new DateOnly(2026, 5, 4);

        db.Courses.Add(new Course
        {
            Id = courseId,
            Name = "Pending topic progress",
            DurationWeeks = 1
        });
        db.Groups.Add(new Group
        {
            Id = groupId,
            Name = "9301",
            StudentsCount = 20,
            CourseId = courseId
        });
        db.LessonTypes.Add(new LessonTypeRef
        {
            Id = lessonTypeId,
            Code = "WORK",
            Name = "Практичне заняття",
            IsActive = true,
            RequiresRoom = true,
            RequiresTeacher = true,
            BlocksRoom = true,
            BlocksTeacher = true,
            CountInPlan = true,
            CountInLoad = true
        });
        db.Modules.Add(new Module
        {
            Id = moduleId,
            Code = "2",
            Title = "Послідовність тем",
            Credits = 1,
            CourseId = courseId
        });
        db.ModuleTopics.AddRange(
            new ModuleTopic
            {
                Id = firstTopicId,
                ModuleId = moduleId,
                Order = 1,
                TopicCode = "2.1",
                LessonTypeId = lessonTypeId,
                TotalHours = 1,
                AuditoriumHours = 1
            },
            new ModuleTopic
            {
                Id = secondTopicId,
                ModuleId = moduleId,
                Order = 2,
                TopicCode = "2.2",
                LessonTypeId = lessonTypeId,
                TotalHours = 1,
                AuditoriumHours = 1
            });
        db.Buildings.Add(new Building
        {
            Id = buildingId,
            Name = "Main"
        });
        db.Rooms.Add(new Room
        {
            Id = roomId,
            Name = "Room 700",
            Capacity = 30,
            BuildingId = buildingId
        });
        db.Teachers.Add(new Teacher
        {
            Id = teacherId,
            FullName = "Topic Teacher"
        });
        db.TeacherModules.Add(new TeacherModule
        {
            TeacherId = teacherId,
            ModuleId = moduleId
        });
        db.TimeSlots.AddRange(
            new TimeSlot
            {
                Id = 700,
                CourseId = courseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(8, 0),
                End = new TimeOnly(9, 0),
                SortOrder = 1,
                IsActive = true
            },
            new TimeSlot
            {
                Id = 701,
                CourseId = courseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(9, 10),
                End = new TimeOnly(10, 10),
                SortOrder = 2,
                IsActive = true
            },
            new TimeSlot
            {
                Id = 702,
                CourseId = courseId,
                DayOfWeek = DayOfWeek.Tuesday,
                Start = new TimeOnly(8, 0),
                End = new TimeOnly(9, 0),
                SortOrder = 1,
                IsActive = true
            });
        db.TeacherDraftItems.Add(new TeacherDraftItem
        {
            Date = startDate,
            DayOfWeek = DayOfWeek.Monday,
            StartTime = new TimeOnly(9, 10),
            EndTime = new TimeOnly(10, 10),
            GroupId = groupId,
            ModuleId = moduleId,
            ModuleTopicId = firstTopicId,
            LessonTypeId = lessonTypeId,
            TeacherId = teacherId,
            RoomId = roomId,
            Status = DraftStatus.Draft,
            IsLocked = true
        });

        await db.SaveChangesAsync();
        return new PendingTopicProgressSeed(courseId, groupId, moduleId, firstTopicId, secondTopicId, startDate);
    }

    private static async Task<CrossDateDisplacementSeed> SeedCrossDateDisplacementScenarioAsync(AppDbContext db)
    {
        const int courseId = 720;
        const int groupId = 720;
        const int targetModuleId = 720;
        const int movableModuleId = 721;
        const int firstTargetTopicId = 720;
        const int secondTargetTopicId = 721;
        const int movableTopicId = 722;
        const int lessonTypeId = 720;
        const int teacherId = 720;
        const int buildingId = 720;
        const int roomId = 720;
        var startDate = new DateOnly(2026, 5, 4);
        var earlyGapStart = new TimeOnly(8, 0);

        db.Courses.Add(new Course
        {
            Id = courseId,
            Name = "Cross-date displacement",
            DurationWeeks = 1
        });
        db.Groups.Add(new Group
        {
            Id = groupId,
            Name = "9301",
            StudentsCount = 20,
            CourseId = courseId
        });
        db.LessonTypes.Add(new LessonTypeRef
        {
            Id = lessonTypeId,
            Code = "WORK",
            Name = "Практичне заняття",
            IsActive = true,
            RequiresRoom = true,
            RequiresTeacher = true,
            BlocksRoom = true,
            BlocksTeacher = true,
            CountInPlan = true,
            CountInLoad = true
        });
        db.Modules.AddRange(
            new Module
            {
                Id = targetModuleId,
                Code = "T",
                Title = "Послідовна тема",
                Credits = 1,
                CourseId = courseId
            },
            new Module
            {
                Id = movableModuleId,
                Code = "M",
                Title = "Пересувне заняття",
                Credits = 1,
                CourseId = courseId
            });
        db.ModuleTopics.AddRange(
            new ModuleTopic
            {
                Id = firstTargetTopicId,
                ModuleId = targetModuleId,
                Order = 1,
                TopicCode = "T.1",
                LessonTypeId = lessonTypeId,
                TotalHours = 1,
                AuditoriumHours = 1
            },
            new ModuleTopic
            {
                Id = secondTargetTopicId,
                ModuleId = targetModuleId,
                Order = 2,
                TopicCode = "T.2",
                LessonTypeId = lessonTypeId,
                TotalHours = 1,
                AuditoriumHours = 1
            },
            new ModuleTopic
            {
                Id = movableTopicId,
                ModuleId = movableModuleId,
                Order = 1,
                TopicCode = "M.1",
                LessonTypeId = lessonTypeId,
                TotalHours = 1,
                AuditoriumHours = 1
            });
        db.Buildings.Add(new Building
        {
            Id = buildingId,
            Name = "Main"
        });
        db.Rooms.Add(new Room
        {
            Id = roomId,
            Name = "Room 720",
            Capacity = 30,
            BuildingId = buildingId
        });
        db.Teachers.Add(new Teacher
        {
            Id = teacherId,
            FullName = "Cross-date Teacher"
        });
        db.TeacherModules.AddRange(
            new TeacherModule
            {
                TeacherId = teacherId,
                ModuleId = targetModuleId
            },
            new TeacherModule
            {
                TeacherId = teacherId,
                ModuleId = movableModuleId
            });
        db.TimeSlots.AddRange(
            new TimeSlot
            {
                Id = 720,
                CourseId = courseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = earlyGapStart,
                End = new TimeOnly(9, 0),
                SortOrder = 1,
                IsActive = true
            },
            new TimeSlot
            {
                Id = 721,
                CourseId = courseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(9, 10),
                End = new TimeOnly(10, 10),
                SortOrder = 2,
                IsActive = true
            },
            new TimeSlot
            {
                Id = 722,
                CourseId = courseId,
                DayOfWeek = DayOfWeek.Tuesday,
                Start = earlyGapStart,
                End = new TimeOnly(9, 0),
                SortOrder = 1,
                IsActive = true
            });
        db.TeacherDraftItems.AddRange(
            new TeacherDraftItem
            {
                Date = startDate,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = new TimeOnly(9, 10),
                EndTime = new TimeOnly(10, 10),
                GroupId = groupId,
                ModuleId = targetModuleId,
                ModuleTopicId = firstTargetTopicId,
                LessonTypeId = lessonTypeId,
                TeacherId = teacherId,
                RoomId = roomId,
                Status = DraftStatus.Draft,
                IsLocked = true
            },
            new TeacherDraftItem
            {
                Date = startDate.AddDays(1),
                DayOfWeek = DayOfWeek.Tuesday,
                StartTime = earlyGapStart,
                EndTime = new TimeOnly(9, 0),
                GroupId = groupId,
                ModuleId = movableModuleId,
                ModuleTopicId = movableTopicId,
                LessonTypeId = lessonTypeId,
                TeacherId = teacherId,
                RoomId = roomId,
                Status = DraftStatus.Draft,
                IsLocked = false
            });

        await db.SaveChangesAsync();
        return new CrossDateDisplacementSeed(
            courseId,
            groupId,
            targetModuleId,
            movableModuleId,
            firstTargetTopicId,
            secondTargetTopicId,
            startDate,
            earlyGapStart);
    }

    private static async Task<SharedTopicSubflowSeed> SeedSharedTopicSubflowScenarioAsync(AppDbContext db)
    {
        const int courseId = 710;
        const int targetModuleId = 710;
        const int blockerModuleId = 711;
        const int workTypeId = 710;
        const int lectureTypeId = 711;
        const int firstTopicId = 710;
        const int sharedTopicId = 711;
        const int buildingId = 710;
        const int auditoriumId = 710;
        const int lectureTeacherId = 710;
        var date = new DateOnly(2026, 5, 4);
        var groupIds = Enumerable.Range(7101, 7).ToList();

        db.Courses.Add(new Course
        {
            Id = courseId,
            Name = "Shared topic subflows",
            DurationWeeks = 1
        });
        db.LessonTypes.AddRange(
            new LessonTypeRef
            {
                Id = workTypeId,
                Code = "WORK",
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
                Id = lectureTypeId,
                Code = "LECTURE",
                Name = "Лекція",
                IsActive = true,
                RequiresRoom = true,
                RequiresTeacher = true,
                BlocksRoom = true,
                BlocksTeacher = true,
                CountInPlan = true,
                CountInLoad = true
            });
        db.Modules.AddRange(
            new Module
            {
                Id = targetModuleId,
                Code = "2",
                Title = "Потокова тема",
                Credits = 1,
                CourseId = courseId
            },
            new Module
            {
                Id = blockerModuleId,
                Code = "B",
                Title = "Зафіксоване заняття",
                Credits = 1,
                CourseId = courseId
            });
        db.ModuleTopics.AddRange(
            new ModuleTopic
            {
                Id = firstTopicId,
                ModuleId = targetModuleId,
                Order = 1,
                TopicCode = "2.1",
                LessonTypeId = workTypeId,
                TotalHours = 1,
                AuditoriumHours = 1
            },
            new ModuleTopic
            {
                Id = sharedTopicId,
                ModuleId = targetModuleId,
                Order = 2,
                TopicCode = "2.2",
                LessonTypeId = lectureTypeId,
                TotalHours = 1,
                AuditoriumHours = 1
            });
        db.Buildings.Add(new Building
        {
            Id = buildingId,
            Name = "Main"
        });
        db.Rooms.Add(new Room
        {
            Id = auditoriumId,
            Name = "Auditorium",
            Capacity = 200,
            BuildingId = buildingId
        });
        db.Teachers.Add(new Teacher
        {
            Id = lectureTeacherId,
            FullName = "Lecture Teacher"
        });
        db.TeacherModules.Add(new TeacherModule
        {
            TeacherId = lectureTeacherId,
            ModuleId = targetModuleId
        });
        db.TimeSlots.AddRange(
            new TimeSlot
            {
                Id = 710,
                CourseId = courseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(8, 0),
                End = new TimeOnly(9, 0),
                SortOrder = 1,
                IsActive = true
            },
            new TimeSlot
            {
                Id = 711,
                CourseId = courseId,
                DayOfWeek = DayOfWeek.Tuesday,
                Start = new TimeOnly(8, 0),
                End = new TimeOnly(9, 0),
                SortOrder = 1,
                IsActive = true
            },
            new TimeSlot
            {
                Id = 712,
                CourseId = courseId,
                DayOfWeek = DayOfWeek.Tuesday,
                Start = new TimeOnly(9, 10),
                End = new TimeOnly(10, 10),
                SortOrder = 2,
                IsActive = true
            },
            new TimeSlot
            {
                Id = 713,
                CourseId = courseId,
                DayOfWeek = DayOfWeek.Tuesday,
                Start = new TimeOnly(10, 20),
                End = new TimeOnly(11, 20),
                SortOrder = 3,
                IsActive = true
            });

        for (var index = 0; index < groupIds.Count; index++)
        {
            var groupId = groupIds[index];
            var teacherId = 720 + index;
            var roomId = 720 + index;
            db.Groups.Add(new Group
            {
                Id = groupId,
                Name = groupId.ToString(),
                StudentsCount = 20,
                CourseId = courseId
            });
            db.Teachers.Add(new Teacher
            {
                Id = teacherId,
                FullName = $"Work Teacher {index + 1}"
            });
            db.TeacherModules.AddRange(
                new TeacherModule
                {
                    TeacherId = teacherId,
                    ModuleId = targetModuleId
                },
                new TeacherModule
                {
                    TeacherId = teacherId,
                    ModuleId = blockerModuleId
                });
            db.Rooms.Add(new Room
            {
                Id = roomId,
                Name = $"Work Room {index + 1}",
                Capacity = 30,
                BuildingId = buildingId
            });
            db.TeacherDraftItems.Add(new TeacherDraftItem
            {
                Date = date,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(9, 0),
                GroupId = groupId,
                ModuleId = targetModuleId,
                ModuleTopicId = firstTopicId,
                LessonTypeId = workTypeId,
                TeacherId = teacherId,
                RoomId = roomId,
                Status = DraftStatus.Draft,
                IsLocked = true
            });
            var blockerStarts = index switch
            {
                0 => new[] { new TimeOnly(8, 0), new TimeOnly(10, 20) },
                <= 4 => new[] { new TimeOnly(10, 20) },
                _ => new[] { new TimeOnly(9, 10) }
            };
            foreach (var blockerStart in blockerStarts)
            {
                db.TeacherDraftItems.Add(new TeacherDraftItem
                {
                    Date = date.AddDays(1),
                    DayOfWeek = DayOfWeek.Tuesday,
                    StartTime = blockerStart,
                    EndTime = blockerStart.AddHours(1),
                    GroupId = groupId,
                    ModuleId = blockerModuleId,
                    LessonTypeId = workTypeId,
                    TeacherId = teacherId,
                    RoomId = roomId,
                    Status = DraftStatus.Draft,
                    IsLocked = true
                });
            }
        }

        await db.SaveChangesAsync();
        return new SharedTopicSubflowSeed(courseId, targetModuleId, sharedTopicId, groupIds, date);
    }

    private static async Task<StudentTravelGapSeed> SeedStudentTravelGapScenarioAsync(AppDbContext db)
    {
        const int courseId = 1;
        const int groupId = 1;
        const int lessonTypeId = 1;
        const int fixedModuleId = 1;
        const int targetModuleId = 2;
        const int sourceBuildingId = 1;
        const int targetBuildingId = 2;
        const int sourceRoomId = 1;
        const int targetRoomId = 2;
        const int fixedTeacherId = 1;
        const int targetTeacherId = 2;
        var date = new DateOnly(2026, 5, 4);
        var blockedStart = new TimeOnly(16, 25);
        var reachableStart = new TimeOnly(18, 0);

        db.Courses.Add(new Course
        {
            Id = courseId,
            Name = "Student travel gap",
            DurationWeeks = 1
        });
        db.Groups.Add(new Group
        {
            Id = groupId,
            Name = "Travel group",
            StudentsCount = 24,
            CourseId = courseId
        });
        db.LessonTypes.Add(new LessonTypeRef
        {
            Id = lessonTypeId,
            Code = "WORK",
            Name = "Практичне заняття",
            IsActive = true,
            RequiresRoom = true,
            RequiresTeacher = true,
            BlocksRoom = true,
            BlocksTeacher = true,
            CountInPlan = true,
            CountInLoad = true
        });
        db.Modules.AddRange(
            new Module
            {
                Id = fixedModuleId,
                Code = "M1",
                Title = "Existing module",
                Credits = 1,
                CourseId = courseId
            },
            new Module
            {
                Id = targetModuleId,
                Code = "M2",
                Title = "Target module",
                Credits = 1,
                CourseId = courseId
            });
        db.ModuleTopics.Add(new ModuleTopic
        {
            Id = 1,
            ModuleId = targetModuleId,
            Order = 1,
            TopicCode = "2.1.1.1",
            LessonTypeId = lessonTypeId,
            TotalHours = 1,
            AuditoriumHours = 1
        });
        db.Buildings.AddRange(
            new Building
            {
                Id = sourceBuildingId,
                Name = "Корпус A"
            },
            new Building
            {
                Id = targetBuildingId,
                Name = "Корпус B"
            });
        db.BuildingTravels.Add(new BuildingTravel
        {
            Id = 1,
            FromBuildingId = sourceBuildingId,
            ToBuildingId = targetBuildingId,
            Minutes = 20
        });
        db.Rooms.AddRange(
            new Room
            {
                Id = sourceRoomId,
                Name = "A-101",
                Capacity = 40,
                BuildingId = sourceBuildingId
            },
            new Room
            {
                Id = targetRoomId,
                Name = "B-201",
                Capacity = 40,
                BuildingId = targetBuildingId
            });
        db.ModuleRooms.Add(new ModuleRoom
        {
            ModuleId = targetModuleId,
            RoomId = targetRoomId
        });
        db.Teachers.AddRange(
            new Teacher
            {
                Id = fixedTeacherId,
                FullName = "Fixed Teacher"
            },
            new Teacher
            {
                Id = targetTeacherId,
                FullName = "Target Teacher"
            });
        db.TeacherModules.Add(new TeacherModule
        {
            TeacherId = targetTeacherId,
            ModuleId = targetModuleId
        });
        db.TimeSlots.AddRange(
            new TimeSlot
            {
                Id = 1,
                CourseId = courseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(15, 0),
                End = new TimeOnly(16, 20),
                SortOrder = 7,
                IsActive = true
            },
            new TimeSlot
            {
                Id = 2,
                CourseId = courseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = blockedStart,
                End = new TimeOnly(17, 45),
                SortOrder = 8,
                IsActive = true
            },
            new TimeSlot
            {
                Id = 3,
                CourseId = courseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = reachableStart,
                End = new TimeOnly(19, 20),
                SortOrder = 9,
                IsActive = true
            });
        db.TeacherDraftItems.Add(new TeacherDraftItem
        {
            Date = date,
            DayOfWeek = DayOfWeek.Monday,
            StartTime = new TimeOnly(15, 0),
            EndTime = new TimeOnly(16, 20),
            GroupId = groupId,
            ModuleId = fixedModuleId,
            LessonTypeId = lessonTypeId,
            TeacherId = fixedTeacherId,
            RoomId = sourceRoomId,
            Status = DraftStatus.Draft,
            IsLocked = true
        });

        await db.SaveChangesAsync();
        return new StudentTravelGapSeed(courseId, groupId, targetModuleId, targetBuildingId, date, blockedStart, reachableStart);
    }

    private static async Task<SevenGroupLectureSeed> SeedSevenGroupLectureScenarioAsync(AppDbContext db)
    {
        const int courseId = 1;
        const int lectureTypeId = 1;
        const int workTypeId = 2;
        const int lectureModuleId = 4;
        const int workModuleId = 8;
        const int lectureTopicId = 1;

        db.Courses.Add(new Course
        {
            Id = courseId,
            Name = "Seven Group Lecture",
            DurationWeeks = 1
        });
        db.LessonTypes.AddRange(
            new LessonTypeRef
            {
                Id = lectureTypeId,
                Code = "LECTURE",
                Name = "Lecture",
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
                Id = workTypeId,
                Code = "WORK",
                Name = "Syndicate work",
                IsActive = true,
                RequiresRoom = true,
                RequiresTeacher = true,
                BlocksRoom = true,
                BlocksTeacher = true,
                CountInPlan = true,
                CountInLoad = true
            });
        db.Modules.AddRange(
            new Module
            {
                Id = lectureModuleId,
                Code = "3",
                Title = "Operations art",
                Credits = 1,
                CourseId = courseId
            },
            new Module
            {
                Id = workModuleId,
                Code = "5",
                Title = "Project work",
                Credits = 1,
                CourseId = courseId
            });
        db.ModuleTopics.AddRange(
            new ModuleTopic
            {
                Id = lectureTopicId,
                ModuleId = lectureModuleId,
                Order = 1,
                TopicCode = "3.1.1.3",
                LessonTypeId = lectureTypeId,
                TotalHours = 1,
                AuditoriumHours = 1
            },
            new ModuleTopic
            {
                Id = 2,
                ModuleId = workModuleId,
                Order = 1,
                TopicCode = "5.1.1.2",
                LessonTypeId = workTypeId,
                TotalHours = 1,
                AuditoriumHours = 1
            });
        db.ModuleSequenceItems.AddRange(
            new ModuleSequenceItem
            {
                CourseId = courseId,
                ModuleId = workModuleId,
                GroupOrder = 1,
                Order = 1
            },
            new ModuleSequenceItem
            {
                CourseId = courseId,
                ModuleId = lectureModuleId,
                GroupOrder = 2,
                Order = 2
            });
        db.Buildings.Add(new Building
        {
            Id = 1,
            Name = "Main"
        });
        db.Rooms.Add(new Room
        {
            Id = 1,
            Name = "Auditorium",
            Capacity = 500,
            BuildingId = 1
        });
        for (var index = 0; index < 7; index++)
        {
            db.Rooms.Add(new Room
            {
                Id = index + 10,
                Name = $"Work room {index + 1}",
                Capacity = 40,
                BuildingId = 1
            });
        }
        db.Teachers.Add(new Teacher
        {
            Id = 1,
            FullName = "Lecture Teacher"
        });
        db.TeacherModules.Add(new TeacherModule
        {
            TeacherId = 1,
            ModuleId = lectureModuleId
        });
        for (var index = 0; index < 7; index++)
        {
            var teacherId = index + 10;
            db.Teachers.Add(new Teacher
            {
                Id = teacherId,
                FullName = $"Work Teacher {index + 1}"
            });
            db.TeacherModules.Add(new TeacherModule
            {
                TeacherId = teacherId,
                ModuleId = workModuleId
            });
        }
        db.TimeSlots.AddRange(
            new TimeSlot
            {
                Id = 1,
                CourseId = courseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(8, 0),
                End = new TimeOnly(9, 20),
                SortOrder = 1,
                IsActive = true
            },
            new TimeSlot
            {
                Id = 2,
                CourseId = courseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(9, 40),
                End = new TimeOnly(11, 0),
                SortOrder = 2,
                IsActive = true
            });

        var groupIds = Enumerable.Range(1, 7).Select(index => index + 9300).ToList();
        foreach (var groupId in groupIds)
        {
            db.Groups.Add(new Group
            {
                Id = groupId,
                Name = groupId.ToString(),
                StudentsCount = 30,
                CourseId = courseId
            });
        }

        await db.SaveChangesAsync();
        return new SevenGroupLectureSeed(courseId, lectureModuleId, workModuleId, lectureTopicId, groupIds);
    }

    private static async Task<PreferredFirstLimitSeed> SeedPreferredFirstLimitScenarioAsync(AppDbContext db)
    {
        const int courseId = 1;
        const int lectureTypeId = 1;
        const int workTypeId = 2;
        const int lectureModuleId = 1;
        const int workModuleId = 2;
        const int maxPreferredSlotOrder = 2;
        var groupIds = new List<int> { 9301, 9302 };

        db.Courses.Add(new Course
        {
            Id = courseId,
            Name = "Preferred-first limit",
            DurationWeeks = 1
        });
        foreach (var groupId in groupIds)
        {
            db.Groups.Add(new Group
            {
                Id = groupId,
                Name = groupId.ToString(),
                StudentsCount = 30,
                CourseId = courseId
            });
        }
        db.LessonTypes.AddRange(
            new LessonTypeRef
            {
                Id = lectureTypeId,
                Code = "LECTURE",
                Name = "Lecture",
                IsActive = true,
                RequiresRoom = true,
                RequiresTeacher = true,
                BlocksRoom = true,
                BlocksTeacher = true,
                CountInPlan = true,
                CountInLoad = true,
                PreferredFirstInWeek = true
            },
            new LessonTypeRef
            {
                Id = workTypeId,
                Code = "WORK",
                Name = "Syndicate work",
                IsActive = true,
                RequiresRoom = true,
                RequiresTeacher = true,
                BlocksRoom = true,
                BlocksTeacher = true,
                CountInPlan = true,
                CountInLoad = true
            });
        db.PreferredFirstSlotLimitConfigs.Add(new PreferredFirstSlotLimitConfig
        {
            CourseId = courseId,
            MaxSlotOrder = maxPreferredSlotOrder
        });
        db.Modules.AddRange(
            new Module
            {
                Id = lectureModuleId,
                Code = "1",
                Title = "Lecture module",
                Credits = 1,
                CourseId = courseId
            },
            new Module
            {
                Id = workModuleId,
                Code = "2",
                Title = "Work module",
                Credits = 1,
                CourseId = courseId
            });
        db.ModuleTopics.AddRange(
            new ModuleTopic
            {
                Id = 1,
                ModuleId = lectureModuleId,
                Order = 1,
                TopicCode = "1.1.1.1",
                LessonTypeId = lectureTypeId,
                TotalHours = 4,
                AuditoriumHours = 4
            },
            new ModuleTopic
            {
                Id = 2,
                ModuleId = workModuleId,
                Order = 1,
                TopicCode = "2.1.1.1",
                LessonTypeId = workTypeId,
                TotalHours = 2,
                AuditoriumHours = 2
            });
        db.ModuleSequenceItems.AddRange(
            new ModuleSequenceItem
            {
                CourseId = courseId,
                ModuleId = lectureModuleId,
                GroupOrder = 1,
                Order = 1
            },
            new ModuleSequenceItem
            {
                CourseId = courseId,
                ModuleId = workModuleId,
                GroupOrder = 2,
                Order = 2
            });
        db.ModuleFillers.Add(new ModuleFiller
        {
            CourseId = courseId,
            ModuleId = workModuleId
        });
        db.Buildings.Add(new Building
        {
            Id = 1,
            Name = "Main"
        });
        db.Rooms.Add(new Room
        {
            Id = 1,
            Name = "Room 1",
            Capacity = 100,
            BuildingId = 1
        });
        db.Teachers.AddRange(
            new Teacher
            {
                Id = 1,
                FullName = "Lecture Teacher"
            },
            new Teacher
            {
                Id = 2,
                FullName = "Work Teacher"
            });
        db.TeacherModules.AddRange(
            new TeacherModule
            {
                TeacherId = 1,
                ModuleId = lectureModuleId
            },
            new TeacherModule
            {
                TeacherId = 2,
                ModuleId = workModuleId
            });

        var slotId = 1;
        foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday })
        {
            db.TimeSlots.AddRange(
                new TimeSlot
                {
                    Id = slotId++,
                    CourseId = courseId,
                    DayOfWeek = day,
                    Start = new TimeOnly(8, 0),
                    End = new TimeOnly(9, 20),
                    SortOrder = 1,
                    IsActive = true
                },
                new TimeSlot
                {
                    Id = slotId++,
                    CourseId = courseId,
                    DayOfWeek = day,
                    Start = new TimeOnly(9, 40),
                    End = new TimeOnly(11, 0),
                    SortOrder = 2,
                    IsActive = true
                },
                new TimeSlot
                {
                    Id = slotId++,
                    CourseId = courseId,
                    DayOfWeek = day,
                    Start = new TimeOnly(11, 20),
                    End = new TimeOnly(12, 40),
                    SortOrder = 3,
                    IsActive = true
                });
        }

        await db.SaveChangesAsync();
        return new PreferredFirstLimitSeed(courseId, groupIds, lectureModuleId, workModuleId, lectureTypeId, workTypeId, maxPreferredSlotOrder);
    }

    [Fact]
    public async Task Draft_autogen_clear_existing_by_teacher_removes_all_draft_logical_event_siblings()
    {
        await using var fixture = await ControllerValidationFixture.CreateAsync();
        var eventIds = await fixture.AddLogicalEventAsync(
            "autogen-clear-event",
            secondStatus: DraftStatus.Draft,
            secondLocked: false);
        var service = new TeacherDraftsAutogenService(fixture.Db);

        var action = await service.DraftAutoGen(new DraftAutoGenRequest(
            WeekStart: new DateOnly(2026, 5, 11),
            ClearExisting: true,
            CourseId: fixture.CourseId,
            GroupIds: new List<int> { fixture.GroupId },
            TeacherId: fixture.TeacherId,
            RangeStartDate: new DateOnly(2026, 5, 11),
            RangeEndDate: new DateOnly(2026, 5, 11)));

        Assert.IsType<OkObjectResult>(action.Result);
        Assert.Equal(0, await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .CountAsync(item => eventIds.Contains(item.Id)));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Draft_autogen_clear_existing_by_teacher_rejects_non_deletable_logical_event_atomically(
        bool secondPublished,
        bool secondLocked)
    {
        await using var fixture = await ControllerValidationFixture.CreateAsync();
        await fixture.AddDraftAsync();
        var eventIds = await fixture.AddLogicalEventAsync(
            "autogen-protected-event",
            secondStatus: secondPublished ? DraftStatus.Published : DraftStatus.Draft,
            secondLocked: secondLocked);
        var service = new TeacherDraftsAutogenService(fixture.Db);

        var action = await service.DraftAutoGen(new DraftAutoGenRequest(
            WeekStart: new DateOnly(2026, 5, 11),
            ClearExisting: true,
            CourseId: fixture.CourseId,
            GroupIds: new List<int> { fixture.GroupId },
            TeacherId: fixture.TeacherId,
            RangeStartDate: new DateOnly(2026, 5, 11),
            RangeEndDate: new DateOnly(2026, 5, 11)));

        Assert.IsType<ConflictObjectResult>(action.Result);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(3, await fixture.Db.TeacherDraftItems.AsNoTracking().CountAsync());
        Assert.Equal(2, await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .CountAsync(item => eventIds.Contains(item.Id)));
    }

    [Theory]
    [InlineData("bad", "10:00")]
    [InlineData("09:00", "bad")]
    public async Task Validate_upsert_returns_error_for_malformed_time(string start, string end)
    {
        await using var fixture = await ControllerValidationFixture.CreateAsync();
        var rules = new RulesService(fixture.Db);

        var request = new UpsertScheduleItemRequest(
            Id: null,
            Date: new DateOnly(2026, 5, 11),
            TimeStart: start,
            TimeEnd: end,
            GroupId: fixture.GroupId,
            ModuleId: fixture.ModuleId,
            TeacherId: fixture.TeacherId,
            RoomId: fixture.RoomId,
            LessonTypeId: fixture.LessonTypeId,
            IsLocked: false);

        var result = await rules.ValidateUpsertAsync(request);

        Assert.Contains(result.errors, message => message.Contains("формат часу", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Validate_draft_returns_error_for_malformed_time()
    {
        await using var fixture = await ControllerValidationFixture.CreateAsync();
        var rules = new RulesService(fixture.Db);

        var request = new DraftUpsertRequest(
            Id: null,
            Date: new DateOnly(2026, 5, 11),
            TimeStart: "09:bad",
            TimeEnd: "10:00",
            GroupId: fixture.GroupId,
            ModuleId: fixture.ModuleId,
            ModuleTopicId: null,
            TeacherId: fixture.TeacherId,
            RoomId: fixture.RoomId,
            RequiresRoom: true,
            LessonTypeId: fixture.LessonTypeId);

        var result = await rules.ValidateDraftAsync(request);

        Assert.Contains(result.Errors, message => message.Contains("HH:mm", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Report.Issues, issue => issue.Code == "time-format-invalid");
    }

    [Fact]
    public async Task Validate_draft_rejects_overlap_with_approved_draft()
    {
        await using var fixture = await ControllerValidationFixture.CreateAsync();
        await fixture.AddDraftAsync(status: DraftStatus.Published);
        var rules = new RulesService(fixture.Db);

        var request = new DraftUpsertRequest(
            Id: null,
            Date: new DateOnly(2026, 5, 11),
            TimeStart: "09:30",
            TimeEnd: "10:30",
            GroupId: fixture.GroupId,
            ModuleId: fixture.ModuleId,
            ModuleTopicId: null,
            TeacherId: fixture.TeacherId,
            RoomId: fixture.RoomId,
            RequiresRoom: true,
            LessonTypeId: fixture.LessonTypeId);

        var result = await rules.ValidateDraftAsync(request);

        Assert.Contains(result.Report.Issues, issue => issue.Code == "conflict-draft-group");
    }

    [Fact]
    public async Task Draft_upsert_rejects_bad_time_even_when_validation_bypass_is_requested()
    {
        await using var fixture = await ControllerValidationFixture.CreateAsync();
        var controller = new TeacherDraftsController(
            fixture.Db,
            new RulesService(fixture.Db),
            queryService: null!,
            exportService: null!,
            autogenService: null!,
            autogenJobService: null!,
            publishService: null!);

        var request = new DraftUpsertRequest(
            Id: null,
            Date: new DateOnly(2026, 5, 11),
            TimeStart: "bad",
            TimeEnd: "10:00",
            GroupId: fixture.GroupId,
            ModuleId: fixture.ModuleId,
            ModuleTopicId: null,
            TeacherId: fixture.TeacherId,
            RoomId: fixture.RoomId,
            RequiresRoom: true,
            LessonTypeId: fixture.LessonTypeId,
            IgnoreValidationErrors: true);

        var result = await controller.Upsert(request);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Draft_upsert_does_not_bypass_hard_validation_errors()
    {
        await using var fixture = await ControllerValidationFixture.CreateAsync();
        var controller = new TeacherDraftsController(
            fixture.Db,
            new RulesService(fixture.Db),
            queryService: null!,
            exportService: null!,
            autogenService: null!,
            autogenJobService: null!,
            publishService: null!);

        var request = new DraftUpsertRequest(
            Id: null,
            Date: new DateOnly(2026, 5, 11),
            TimeStart: "09:00",
            TimeEnd: "10:00",
            GroupId: fixture.GroupId,
            ModuleId: fixture.ModuleId,
            ModuleTopicId: null,
            TeacherId: fixture.TeacherId,
            RoomId: 404,
            RequiresRoom: true,
            LessonTypeId: fixture.LessonTypeId,
            IgnoreValidationErrors: true);

        var result = await controller.Upsert(request);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.False(await fixture.Db.TeacherDraftItems.AnyAsync());
    }

    [Fact]
    public async Task Draft_delete_rejects_locked_draft_unless_confirmed_unrestricted_override_is_complete()
    {
        await using var fixture = await ControllerValidationFixture.CreateAsync();
        var draftId = await fixture.AddDraftAsync(isLocked: true);
        var controller = new TeacherDraftsController(
            fixture.Db,
            new RulesService(fixture.Db),
            queryService: null!,
            exportService: null!,
            autogenService: null!,
            autogenJobService: null!,
            publishService: null!);
        var revision = await fixture.Db.TeacherDraftItems
            .Where(item => item.Id == draftId)
            .Select(item => item.Revision)
            .SingleAsync();

        var withoutFlags = await controller.Delete(draftId, revision);
        var confirmationOnly = await controller.Delete(draftId, revision, confirm: true);
        var unrestrictedOnly = await controller.Delete(draftId, revision, unrestricted: true);

        Assert.IsType<ConflictObjectResult>(withoutFlags);
        Assert.IsType<ConflictObjectResult>(confirmationOnly);
        Assert.IsType<ConflictObjectResult>(unrestrictedOnly);
        Assert.True(await fixture.Db.TeacherDraftItems.AnyAsync(item => item.Id == draftId));
    }

    [Fact]
    public async Task Draft_delete_accepts_locked_draft_with_confirmed_unrestricted_override()
    {
        await using var fixture = await ControllerValidationFixture.CreateAsync();
        var draftId = await fixture.AddDraftAsync(isLocked: true);
        var controller = new TeacherDraftsController(
            fixture.Db,
            new RulesService(fixture.Db),
            queryService: null!,
            exportService: null!,
            autogenService: null!,
            autogenJobService: null!,
            publishService: null!);
        var revision = await fixture.Db.TeacherDraftItems
            .Where(item => item.Id == draftId)
            .Select(item => item.Revision)
            .SingleAsync();

        var result = await controller.Delete(draftId, revision, confirm: true, unrestricted: true);

        Assert.IsType<NoContentResult>(result);
        Assert.False(await fixture.Db.TeacherDraftItems.AnyAsync(item => item.Id == draftId));
    }

    [Fact]
    public async Task Draft_clear_week_requires_course_or_group_scope()
    {
        await using var fixture = await ControllerValidationFixture.CreateAsync();
        var controller = new TeacherDraftsController(
            fixture.Db,
            new RulesService(fixture.Db),
            queryService: null!,
            exportService: null!,
            autogenService: null!,
            autogenJobService: null!,
            publishService: null!);

        var result = await controller.ClearWeek(new ClearWeekRequest(new DateOnly(2026, 5, 11)));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Theory]
    [InlineData("", 20)]
    [InlineData("   ", 20)]
    [InlineData("L-3", -1)]
    public async Task Group_upsert_rejects_invalid_input(string name, int studentsCount)
    {
        await using var fixture = await ControllerValidationFixture.CreateAsync();
        var controller = new AdminGroupsController(fixture.Db);

        var result = await controller.Upsert(new GroupEditDto(null, name, studentsCount, fixture.CourseId));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(await fixture.Db.Groups.Where(group => group.Name == name).ToListAsync());
    }

    [Fact]
    public async Task Group_upsert_returns_not_found_for_missing_course()
    {
        await using var fixture = await ControllerValidationFixture.CreateAsync();
        var controller = new AdminGroupsController(fixture.Db);

        var result = await controller.Upsert(new GroupEditDto(null, "L-404", 20, 404));

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task Group_upsert_returns_not_found_for_missing_group_update()
    {
        await using var fixture = await ControllerValidationFixture.CreateAsync();
        var controller = new AdminGroupsController(fixture.Db);

        var result = await controller.Upsert(new GroupEditDto(404, "L-404", 20, fixture.CourseId));

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task Group_upsert_trims_group_name()
    {
        await using var fixture = await ControllerValidationFixture.CreateAsync();
        var controller = new AdminGroupsController(fixture.Db);

        var result = await controller.Upsert(new GroupEditDto(null, "  L-3  ", 20, fixture.CourseId));

        Assert.IsType<OkObjectResult>(result.Result);
        var group = Assert.Single(await fixture.Db.Groups.Where(group => group.Name == "L-3").ToListAsync());
        Assert.Equal(20, group.StudentsCount);
    }

    [Fact]
    public async Task Group_delete_returns_conflict_when_group_has_only_drafts()
    {
        await using var fixture = await ControllerValidationFixture.CreateAsync();
        await fixture.AddDraftAsync();
        var controller = new AdminGroupsController(fixture.Db);

        var result = await controller.Delete(fixture.GroupId);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.True(await fixture.Db.Groups.AnyAsync(group => group.Id == fixture.GroupId));
        Assert.True(await fixture.Db.TeacherDraftItems.AnyAsync(item => item.GroupId == fixture.GroupId));
    }

    [Fact]
    public async Task Group_force_delete_removes_drafts_before_group()
    {
        await using var fixture = await ControllerValidationFixture.CreateAsync();
        await fixture.AddDraftAsync();
        var controller = new AdminGroupsController(fixture.Db);

        var result = await controller.Delete(fixture.GroupId, force: true);

        Assert.IsType<NoContentResult>(result);
        Assert.False(await fixture.Db.TeacherDraftItems.AnyAsync(item => item.GroupId == fixture.GroupId));
        Assert.False(await fixture.Db.Groups.AnyAsync(group => group.Id == fixture.GroupId));
    }

    [Fact]
    public async Task Schedule_upsert_rejects_locked_item_update()
    {
        await using var fixture = await ControllerValidationFixture.CreateAsync();
        var itemId = await fixture.AddScheduleItemAsync(isLocked: true);
        var controller = new ScheduleController(fixture.Db, new RulesService(fixture.Db), new AggregatesService(fixture.Db));
        var expectedRevision = await fixture.Db.ScheduleItems
            .AsNoTracking()
            .Where(item => item.Id == itemId)
            .Select(item => item.Revision)
            .SingleAsync();

        var request = new UpsertScheduleItemRequest(
            Id: itemId,
            Date: new DateOnly(2026, 5, 11),
            TimeStart: "09:00",
            TimeEnd: "10:00",
            GroupId: fixture.GroupId,
            ModuleId: fixture.ModuleId,
            TeacherId: fixture.TeacherId,
            RoomId: fixture.RoomId,
            LessonTypeId: fixture.LessonTypeId,
            IsLocked: false,
            ExpectedRevision: expectedRevision);

        var result = await controller.Upsert(request);

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Schedule_delete_rejects_locked_item()
    {
        await using var fixture = await ControllerValidationFixture.CreateAsync();
        var itemId = await fixture.AddScheduleItemAsync(isLocked: true);
        var controller = new ScheduleController(fixture.Db, new RulesService(fixture.Db), new AggregatesService(fixture.Db));
        var expectedRevision = await fixture.Db.ScheduleItems
            .AsNoTracking()
            .Where(item => item.Id == itemId)
            .Select(item => item.Revision)
            .SingleAsync();

        var result = await controller.Delete(itemId, expectedRevision);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.True(await fixture.Db.ScheduleItems.AnyAsync(item => item.Id == itemId));
    }

    [Fact]
    public async Task Schedule_clear_week_requires_course_or_group_scope()
    {
        await using var fixture = await ControllerValidationFixture.CreateAsync();
        var controller = new ScheduleController(fixture.Db, new RulesService(fixture.Db), new AggregatesService(fixture.Db));

        var result = await controller.ClearWeek(new ClearWeekRequest(new DateOnly(2026, 5, 11)));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    private sealed class ControllerValidationFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private ControllerValidationFixture(SqliteConnection connection, AppDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public AppDbContext Db { get; }
        public int CourseId { get; private set; }
        public int GroupId { get; private set; }
        public int ModuleId { get; private set; }
        public int TeacherId { get; private set; }
        public int RoomId { get; private set; }
        public int LessonTypeId { get; private set; }

        public static async Task<ControllerValidationFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;

            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var course = new Course { Name = "Course", DurationWeeks = 1 };
            var lessonType = new LessonTypeRef
            {
                Code = "LECTURE",
                Name = "Lecture",
                RequiresRoom = true,
                RequiresTeacher = true
            };
            var building = new Building { Name = "Main" };
            var teacher = new Teacher { FullName = "Teacher" };
            db.Courses.Add(course);
            db.LessonTypes.Add(lessonType);
            db.Buildings.Add(building);
            db.Teachers.Add(teacher);
            await db.SaveChangesAsync();

            var group = new Group { Name = "Seed", StudentsCount = 10, CourseId = course.Id };
            var module = new Module { Code = "M1", Title = "Module", CourseId = course.Id };
            var room = new Room { Name = "101", Capacity = 30, BuildingId = building.Id };
            db.Groups.Add(group);
            db.Modules.Add(module);
            db.Rooms.Add(room);
            await db.SaveChangesAsync();

            return new ControllerValidationFixture(connection, db)
            {
                CourseId = course.Id,
                GroupId = group.Id,
                ModuleId = module.Id,
                TeacherId = teacher.Id,
                RoomId = room.Id,
                LessonTypeId = lessonType.Id
            };
        }

        public async Task<int> AddDraftAsync(
            bool isLocked = false,
            DraftStatus status = DraftStatus.Draft)
        {
            var item = new TeacherDraftItem
            {
                Date = new DateOnly(2026, 5, 11),
                DayOfWeek = DayOfWeek.Monday,
                StartTime = new TimeOnly(9, 0),
                EndTime = new TimeOnly(10, 0),
                GroupId = GroupId,
                ModuleId = ModuleId,
                TeacherId = TeacherId,
                RoomId = RoomId,
                LessonTypeId = LessonTypeId,
                IsLocked = isLocked,
                Status = status
            };
            Db.TeacherDraftItems.Add(item);
            await Db.SaveChangesAsync();
            return item.Id;
        }

        public async Task<List<int>> AddLogicalEventAsync(
            string batchKey,
            DraftStatus secondStatus,
            bool secondLocked)
        {
            var secondTeacher = new Teacher { FullName = "Другий викладач" };
            Db.Teachers.Add(secondTeacher);
            await Db.SaveChangesAsync();
            var first = new TeacherDraftItem
            {
                Date = new DateOnly(2026, 5, 11),
                DayOfWeek = DayOfWeek.Monday,
                StartTime = new TimeOnly(11, 0),
                EndTime = new TimeOnly(12, 0),
                GroupId = GroupId,
                ModuleId = ModuleId,
                TeacherId = TeacherId,
                LessonTypeId = LessonTypeId,
                BatchKey = batchKey,
                Status = DraftStatus.Draft
            };
            var second = new TeacherDraftItem
            {
                Date = first.Date,
                DayOfWeek = first.DayOfWeek,
                StartTime = first.StartTime,
                EndTime = first.EndTime,
                GroupId = first.GroupId,
                ModuleId = first.ModuleId,
                TeacherId = secondTeacher.Id,
                LessonTypeId = first.LessonTypeId,
                BatchKey = batchKey,
                Status = secondStatus,
                IsLocked = secondLocked
            };
            Db.TeacherDraftItems.AddRange(first, second);
            await Db.SaveChangesAsync();
            return new List<int> { first.Id, second.Id };
        }

        public async Task<int> AddScheduleItemAsync(bool isLocked = false)
        {
            var item = new ScheduleItem
            {
                Date = new DateOnly(2026, 5, 11),
                DayOfWeek = DayOfWeek.Monday,
                StartTime = new TimeOnly(9, 0),
                EndTime = new TimeOnly(10, 0),
                GroupId = GroupId,
                ModuleId = ModuleId,
                TeacherId = TeacherId,
                RoomId = RoomId,
                LessonTypeId = LessonTypeId,
                IsLocked = isLocked
            };
            Db.ScheduleItems.Add(item);
            await Db.SaveChangesAsync();
            return item.Id;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed record LecturePackingSeed(
        int CourseId,
        int ModuleId,
        int BigRoomId,
        int SmallRoomId,
        List<int> GroupIds);

    private sealed record AggregateCalendarCapacitySeed(
        int CourseId,
        int GroupId,
        IReadOnlyList<int> ModuleIds,
        DateOnly RangeStart,
        DateOnly RangeEnd);

    private sealed record SevenGroupLectureSeed(
        int CourseId,
        int LectureModuleId,
        int WorkModuleId,
        int LectureTopicId,
        List<int> GroupIds);

    private sealed record TopicOverflowFillSeed(
        int CourseId,
        int GroupId,
        int ModuleId,
        int TopicId,
        DateOnly Date);

    private sealed record PendingTopicProgressSeed(
        int CourseId,
        int GroupId,
        int ModuleId,
        int FirstTopicId,
        int SecondTopicId,
        DateOnly StartDate);

    private sealed record SharedTopicSubflowSeed(
        int CourseId,
        int ModuleId,
        int SharedTopicId,
        List<int> GroupIds,
        DateOnly Date);

    private sealed record CrossDateDisplacementSeed(
        int CourseId,
        int GroupId,
        int TargetModuleId,
        int MovableModuleId,
        int FirstTargetTopicId,
        int SecondTargetTopicId,
        DateOnly StartDate,
        TimeOnly EarlyGapStart);

    private sealed record PreferredFirstLimitSeed(
        int CourseId,
        List<int> GroupIds,
        int LectureModuleId,
        int WorkModuleId,
        int LectureTypeId,
        int WorkTypeId,
        int MaxPreferredSlotOrder);

    private sealed record StudentTravelGapSeed(
        int CourseId,
        int GroupId,
        int TargetModuleId,
        int TargetBuildingId,
        DateOnly Date,
        TimeOnly BlockedStart,
        TimeOnly ReachableStart);

    private sealed record RejectedCandidateSideEffectSeed(
        int CourseId,
        int BlockerGroupId,
        int RejectedGroupId,
        int BlockerModuleId,
        int RejectedLectureModuleId,
        int BlockingRoomId,
        DateOnly Date,
        TimeOnly FirstSlotStart);

    private sealed record PersistedMoveRepairSeed(
        int CourseId,
        int GroupId,
        int TargetModuleId,
        int MovableDraftId,
        DateOnly Date,
        TimeOnly FirstSlotStart,
        TimeOnly SecondSlotStart);

    private sealed class FirstQueryCancellationInterceptor : DbCommandInterceptor
    {
        private bool _armed;

        public bool FirstQueryTokenCanBeCanceled { get; private set; }

        public void Arm()
            => _armed = true;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!_armed)
            {
                return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
            }

            _armed = false;
            FirstQueryTokenCanBeCanceled = cancellationToken.CanBeCanceled;
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed record ForcedLateLectureSeed(
        int CourseId,
        List<int> GroupIds,
        int LectureModuleId,
        int WorkModuleId,
        int LectureLessonTypeId,
        int WorkLessonTypeId,
        DateOnly Date,
        TimeOnly FirstSlotStart,
        TimeOnly MiddleSlotStart,
        TimeOnly LateLectureStart);

    private sealed record GroupRegretSeed(
        int CourseId,
        int FlexibleGroupId,
        int ConstrainedGroupId,
        int TargetModuleId,
        int TargetLessonTypeId,
        int ScarceRoomId,
        int FlexibleRoomId,
        int LockedBlockerId,
        DateOnly Date,
        TimeOnly FirstSlotStart,
        TimeOnly SecondSlotStart);

    private sealed record LectureCluster(
        int RoomId,
        string RoomName,
        int GroupCount,
        int Students,
        int RoomCapacity);
}
