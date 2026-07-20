using ReflectionBindingFlags = System.Reflection.BindingFlags;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Admin;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure.Seed;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class ProductionGuardrailTests
{
    [Fact]
    public void Lesson_type_occupancy_policy_keeps_break_blocking_but_out_of_workload()
    {
        Assert.False(LessonTypeOccupancyPolicy.IsNonOccupyingMarker("BREAK"));
        Assert.True(LessonTypeOccupancyPolicy.IsExcludedFromAutogenWorkload("BREAK"));
        Assert.True(LessonTypeOccupancyPolicy.IsNonOccupyingMarker("CANCELED"));
        Assert.True(LessonTypeOccupancyPolicy.IsNonOccupyingMarker("RESCHEDULED"));
    }

    [Fact]
    public async Task Default_lesson_type_seeder_preserves_custom_reserved_css_key_without_retry_loop()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        fixture.Db.LessonTypes.Add(new LessonTypeRef
        {
            Code = "CUSTOM",
            Name = "Користувацький тип",
            CssKey = "brk",
            IsActive = true
        });
        await fixture.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(fixture.Options));
        await using var provider = services.BuildServiceProvider();

        await DefaultLessonTypesSeeder.SeedAsync(provider);

        fixture.Db.ChangeTracker.Clear();
        var lessonTypes = await fixture.Db.LessonTypes.AsNoTracking().ToListAsync();
        Assert.Equal("brk", lessonTypes.Single(item => item.Code == "CUSTOM").CssKey);
        Assert.Null(lessonTypes.Single(item => item.Code == "BREAK").CssKey);
        Assert.Contains(lessonTypes, item => item.Code == "CANCELED");
        Assert.Contains(lessonTypes, item => item.Code == "RESCHEDULED");
    }

    [Fact]
    public async Task AutogenMonth_does_not_clear_drafts_before_requested_month()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var ids = await fixture.SeedMinimalScheduleModelAsync();
        var draftDate = new DateOnly(2026, 4, 28);

        fixture.Db.TeacherDraftItems.Add(new TeacherDraftItem
        {
            Date = draftDate,
            DayOfWeek = draftDate.DayOfWeek,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(9, 0),
            GroupId = ids.GroupId,
            ModuleId = ids.ModuleId,
            LessonTypeId = ids.LessonTypeId,
            IsLocked = false
        });
        await fixture.Db.SaveChangesAsync();

        var service = new TeacherDraftsAutogenService(fixture.Db);
        await service.AutogenMonth(new AutogenMonthRequest(
            MonthStart: new DateOnly(2026, 5, 1),
            CourseId: ids.CourseId,
            GroupId: ids.GroupId,
            TeacherId: null,
            AllowOnDaysOff: false,
            Days: WeekPreset.MonFri));

        var preserved = await fixture.Db.TeacherDraftItems
            .AnyAsync(x => x.Date == draftDate && x.GroupId == ids.GroupId);

        Assert.True(preserved, "AutogenMonth не має видаляти чернетки до початку вибраного місяця.");
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task Autogen_MonSat_uses_Saturday_only_with_effective_working_calendar_override(
        bool addWorkingOverride,
        int expectedCreated)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var ids = await fixture.SeedMinimalScheduleModelAsync();
        var saturday = new DateOnly(2026, 5, 9);
        var teacher = new Teacher { FullName = "Викладач суботнього тесту" };
        var building = new Building { Name = "Корпус суботнього тесту" };
        var room = new Room
        {
            Name = "Субота-101",
            Capacity = 30,
            Building = building
        };
        fixture.Db.AddRange(teacher, room);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.TeacherModules.Add(new TeacherModule
        {
            TeacherId = teacher.Id,
            ModuleId = ids.ModuleId
        });
        fixture.Db.TimeSlots.Add(new TimeSlot
        {
            CourseId = ids.CourseId,
            DayOfWeek = DayOfWeek.Saturday,
            Start = new TimeOnly(8, 0),
            End = new TimeOnly(9, 0),
            SortOrder = 1,
            IsActive = true
        });
        if (addWorkingOverride)
        {
            fixture.Db.CalendarExceptions.Add(new CalendarException
            {
                Date = saturday,
                IsWorkingDay = true,
                Name = "Робоча субота групи",
                CourseId = ids.CourseId,
                GroupId = ids.GroupId
            });
        }
        await fixture.Db.SaveChangesAsync();

        var action = await new TeacherDraftsAutogenService(fixture.Db).DraftAutoGen(
            new DraftAutoGenRequest(
                WeekStart: new DateOnly(2026, 5, 4),
                ClearExisting: true,
                CourseId: ids.CourseId,
                GroupIds: new List<int> { ids.GroupId },
                AllowOnDaysOff: false,
                Days: WeekPreset.MonSat,
                ModuleHours: new Dictionary<int, int> { [ids.ModuleId] = 1 },
                AllowIncompleteDrafts: true,
                RangeStartDate: saturday,
                RangeEndDate: saturday));

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);
        Assert.True(
            result.Created == expectedCreated,
            $"Очікувалось створених чернеток: {expectedCreated}, фактично: {result.Created}. " +
            $"Пропуски: {string.Join(" | ", result.GapDetails?.Select(item => item.Reason) ?? Array.Empty<string>())}. " +
            $"Попередження: {string.Join(" | ", result.Warnings)}");
        Assert.Equal(expectedCreated, await fixture.Db.TeacherDraftItems.CountAsync(item =>
            item.GroupId == ids.GroupId && item.Date == saturday));
    }

    [Fact]
    public async Task Rules_reject_slot_range_that_skips_gap_between_configured_slots()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var ids = await fixture.SeedMinimalScheduleModelAsync();

        fixture.Db.TimeSlots.AddRange(
            new TimeSlot
            {
                CourseId = ids.CourseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(8, 0),
                End = new TimeOnly(9, 0),
                SortOrder = 1,
                IsActive = true
            },
            new TimeSlot
            {
                CourseId = ids.CourseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(10, 0),
                End = new TimeOnly(11, 0),
                SortOrder = 2,
                IsActive = true
            });
        await fixture.Db.SaveChangesAsync();

        var rules = new RulesService(fixture.Db);
        var (errors, _) = await rules.ValidateUpsertAsync(new UpsertScheduleItemRequest(
            Id: null,
            Date: new DateOnly(2026, 5, 4),
            TimeStart: "08:00",
            TimeEnd: "11:00",
            GroupId: ids.GroupId,
            ModuleId: ids.ModuleId,
            TeacherId: null,
            RoomId: null,
            LessonTypeId: ids.LessonTypeId,
            IsLocked: false,
            OverrideNonWorkingDay: false));

        Assert.NotEmpty(errors);
    }

    [Fact]
    public async Task Rules_rejects_a_slot_reserved_for_lunch()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var ids = await fixture.SeedMinimalScheduleModelAsync();
        fixture.Db.TimeSlots.AddRange(
            new TimeSlot
            {
                CourseId = ids.CourseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(8, 0),
                End = new TimeOnly(9, 0),
                SortOrder = 1,
                IsActive = true
            },
            new TimeSlot
            {
                CourseId = ids.CourseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(9, 0),
                End = new TimeOnly(10, 0),
                SortOrder = 2,
                IsActive = true
            });
        fixture.Db.LunchConfigs.Add(new LunchConfig
        {
            CourseId = ids.CourseId,
            Start = new TimeOnly(9, 0),
            End = new TimeOnly(10, 0)
        });
        await fixture.Db.SaveChangesAsync();

        var rules = new RulesService(fixture.Db);
        var (errors, _) = await rules.ValidateUpsertAsync(new UpsertScheduleItemRequest(
            Id: null,
            Date: new DateOnly(2026, 5, 4),
            TimeStart: "09:00",
            TimeEnd: "10:00",
            GroupId: ids.GroupId,
            ModuleId: ids.ModuleId,
            TeacherId: null,
            RoomId: null,
            LessonTypeId: ids.LessonTypeId,
            IsLocked: false,
            OverrideNonWorkingDay: false));

        Assert.Contains(errors, error => error.Contains("дозволених слотів", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Rules_official_validation_checks_travel_for_required_nonblocking_room()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var seed = await SeedNonBlockingTravelModelAsync(fixture);
        fixture.Db.ScheduleItems.Add(new ScheduleItem
        {
            Date = seed.Date,
            DayOfWeek = seed.Date.DayOfWeek,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(9, 0),
            GroupId = seed.Ids.GroupId,
            ModuleId = seed.Ids.ModuleId,
            LessonTypeId = seed.LessonTypeId,
            RoomId = seed.FirstRoomId
        });
        await fixture.Db.SaveChangesAsync();

        var (errors, _) = await new RulesService(fixture.Db).ValidateUpsertAsync(
            new UpsertScheduleItemRequest(
                Id: null,
                Date: seed.Date,
                TimeStart: "09:10",
                TimeEnd: "10:10",
                GroupId: seed.Ids.GroupId,
                ModuleId: seed.Ids.ModuleId,
                TeacherId: null,
                RoomId: seed.SecondRoomId,
                LessonTypeId: seed.LessonTypeId,
                IsLocked: false));

        Assert.Contains(errors, error =>
            error.Contains("Замало часу на перехід", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Rules_official_validation_rejects_aggregate_nonblocking_room_overflow()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var seed = await SeedNonBlockingTravelModelAsync(fixture);
        var secondGroup = new Group
        {
            Name = "T-2",
            StudentsCount = 20,
            CourseId = seed.Ids.CourseId
        };
        fixture.Db.Groups.Add(secondGroup);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ScheduleItems.Add(new ScheduleItem
        {
            Date = seed.Date,
            DayOfWeek = seed.Date.DayOfWeek,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(9, 0),
            GroupId = seed.Ids.GroupId,
            ModuleId = seed.Ids.ModuleId,
            LessonTypeId = seed.LessonTypeId,
            RoomId = seed.SecondRoomId,
            BatchKey = "shared-official-capacity"
        });
        await fixture.Db.SaveChangesAsync();

        var (errors, _) = await new RulesService(fixture.Db).ValidateUpsertAsync(
            new UpsertScheduleItemRequest(
                Id: null,
                Date: seed.Date,
                TimeStart: "08:00",
                TimeEnd: "09:00",
                GroupId: secondGroup.Id,
                ModuleId: seed.Ids.ModuleId,
                TeacherId: null,
                RoomId: seed.SecondRoomId,
                LessonTypeId: seed.LessonTypeId,
                IsLocked: false));

        Assert.Contains(errors, error =>
            error.Contains("охоплює 40 студентів", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Rules_draft_validation_checks_travel_for_required_nonblocking_room()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var seed = await SeedNonBlockingTravelModelAsync(fixture);
        fixture.Db.TeacherDraftItems.Add(new TeacherDraftItem
        {
            Date = seed.Date,
            DayOfWeek = seed.Date.DayOfWeek,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(9, 0),
            GroupId = seed.Ids.GroupId,
            ModuleId = seed.Ids.ModuleId,
            LessonTypeId = seed.LessonTypeId,
            RoomId = seed.FirstRoomId
        });
        await fixture.Db.SaveChangesAsync();

        var result = await new RulesService(fixture.Db).ValidateDraftAsync(
            new DraftUpsertRequest(
                Id: null,
                Date: seed.Date,
                TimeStart: "09:10",
                TimeEnd: "10:10",
                GroupId: seed.Ids.GroupId,
                ModuleId: seed.Ids.ModuleId,
                ModuleTopicId: null,
                TeacherId: null,
                RoomId: seed.SecondRoomId,
                RequiresRoom: true,
                LessonTypeId: seed.LessonTypeId));

        Assert.Contains(result.Report.Issues, issue => issue.Code == "travel-draft-before");
    }

    [Theory]
    [InlineData("CANCELED")]
    [InlineData("RESCHEDULED")]
    public async Task Service_markers_do_not_occupy_autogen_slot_or_create_phantom_travel(string markerCode)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var seed = await SeedNonBlockingTravelModelAsync(fixture);
        var markerType = new LessonTypeRef
        {
            Code = markerCode,
            Name = markerCode == "CANCELED" ? "Скасовано" : "Перенесено",
            IsActive = true,
            RequiresRoom = false,
            RequiresTeacher = false,
            BlocksRoom = false,
            BlocksTeacher = false,
            CountInPlan = false,
            CountInLoad = false
        };
        var firstTopic = new ModuleTopic
        {
            ModuleId = seed.Ids.ModuleId,
            LessonTypeId = seed.LessonTypeId,
            TopicCode = "M1.1",
            Order = 1,
            TotalHours = 1,
            AuditoriumHours = 1,
            SelfStudyHours = 0
        };
        var laterTopic = new ModuleTopic
        {
            ModuleId = seed.Ids.ModuleId,
            LessonTypeId = seed.LessonTypeId,
            TopicCode = "M1.2",
            Order = 2,
            TotalHours = 1,
            AuditoriumHours = 1,
            SelfStudyHours = 0
        };
        fixture.Db.AddRange(markerType, firstTopic, laterTopic);
        fixture.Db.ModuleRooms.Add(new ModuleRoom
        {
            ModuleId = seed.Ids.ModuleId,
            RoomId = seed.SecondRoomId
        });
        var firstSlot = await fixture.Db.TimeSlots.SingleAsync(slot =>
            slot.CourseId == seed.Ids.CourseId && slot.Start == new TimeOnly(8, 0));
        firstSlot.IsActive = false;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ScheduleItems.AddRange(
            new ScheduleItem
            {
                Date = seed.Date,
                DayOfWeek = seed.Date.DayOfWeek,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(9, 0),
                GroupId = seed.Ids.GroupId,
                ModuleId = seed.Ids.ModuleId,
                ModuleTopicId = laterTopic.Id,
                LessonTypeId = markerType.Id,
                TeacherId = seed.TeacherId,
                RoomId = seed.SecondRoomId,
                BatchKey = "rescheduled:source:1"
            },
            new ScheduleItem
            {
                Date = seed.Date,
                DayOfWeek = seed.Date.DayOfWeek,
                StartTime = new TimeOnly(9, 10),
                EndTime = new TimeOnly(10, 10),
                GroupId = seed.Ids.GroupId,
                ModuleId = seed.Ids.ModuleId,
                ModuleTopicId = firstTopic.Id,
                LessonTypeId = markerType.Id,
                TeacherId = seed.TeacherId,
                RoomId = seed.SecondRoomId,
                BatchKey = "rescheduled:source:2"
            });
        await fixture.Db.SaveChangesAsync();

        var action = await new TeacherDraftsAutogenService(fixture.Db).DraftAutoGen(
            new DraftAutoGenRequest(
                WeekStart: seed.Date,
                ClearExisting: true,
                CourseId: seed.Ids.CourseId,
                GroupIds: new List<int> { seed.Ids.GroupId },
                Days: WeekPreset.MonFri,
                ModuleHours: new Dictionary<int, int> { [seed.Ids.ModuleId] = 1 },
                AllowIncompleteDrafts: true,
                RangeStartDate: seed.Date,
                RangeEndDate: seed.Date));

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);
        Assert.Equal(1, result.Created);
        fixture.Db.ChangeTracker.Clear();
        var generated = await fixture.Db.TeacherDraftItems.AsNoTracking().SingleAsync(item =>
            item.GroupId == seed.Ids.GroupId && item.Date == seed.Date);
        Assert.Equal(new TimeOnly(9, 10), generated.StartTime);
        Assert.Equal(firstTopic.Id, generated.ModuleTopicId);
        Assert.Equal(seed.TeacherId, generated.TeacherId);
        Assert.Equal(seed.SecondRoomId, generated.RoomId);

        var hardResult = await new TeacherDraftsAutogenHardRuleValidator(fixture.Db).ValidateAsync(
            new TeacherDraftsAutogenHardRuleValidationRequest(
                seed.Ids.CourseId,
                new[] { seed.Ids.GroupId },
                seed.Date,
                seed.Date));
        Assert.Empty(hardResult.Violations);
        Assert.Empty(await TravelInvariantVerifier.FindViolationsAsync(
            fixture.Db,
            seed.Ids.CourseId,
            seed.Date,
            seed.Date));

        var (errors, _) = await new RulesService(fixture.Db).ValidateUpsertAsync(
            new UpsertScheduleItemRequest(
                Id: null,
                Date: seed.Date,
                TimeStart: "09:10",
                TimeEnd: "10:10",
                GroupId: seed.Ids.GroupId,
                ModuleId: seed.Ids.ModuleId,
                TeacherId: generated.TeacherId,
                RoomId: seed.SecondRoomId,
                LessonTypeId: seed.LessonTypeId,
                IsLocked: false),
            projectedModuleTopicId: firstTopic.Id);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Draft_autogen_treats_spanning_schedule_item_as_filling_each_overlapped_slot()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var ids = await fixture.SeedMinimalScheduleModelAsync();
        var date = new DateOnly(2026, 5, 4);
        fixture.Db.TimeSlots.AddRange(
            new TimeSlot
            {
                CourseId = ids.CourseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(8, 0),
                End = new TimeOnly(9, 0),
                SortOrder = 1,
                IsActive = true
            },
            new TimeSlot
            {
                CourseId = ids.CourseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(9, 0),
                End = new TimeOnly(10, 0),
                SortOrder = 2,
                IsActive = true
            });
        fixture.Db.ScheduleItems.Add(new ScheduleItem
        {
            Date = date,
            DayOfWeek = DayOfWeek.Monday,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(10, 0),
            GroupId = ids.GroupId,
            ModuleId = ids.ModuleId,
            LessonTypeId = ids.LessonTypeId
        });
        await fixture.Db.SaveChangesAsync();

        var action = await new TeacherDraftsAutogenService(fixture.Db).DraftAutoGen(
            new DraftAutoGenRequest(
                WeekStart: date,
                ClearExisting: false,
                CourseId: ids.CourseId,
                GroupIds: new List<int> { ids.GroupId },
                Days: WeekPreset.MonFri,
                ModuleHours: new Dictionary<int, int> { [ids.ModuleId] = 1 },
                RangeStartDate: date,
                RangeEndDate: date));

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);
        Assert.Equal(0, result.Created);
        Assert.Empty(result.GapDetails ?? new List<AutoGenGapDetail>());
    }

    [Theory]
    [InlineData("CANCELED")]
    [InlineData("RESCHEDULED")]
    public async Task Draft_autogen_reports_gap_hidden_only_by_non_occupying_marker(string markerCode)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var ids = await fixture.SeedMinimalScheduleModelAsync();
        var date = new DateOnly(2026, 5, 4);
        var markerType = new LessonTypeRef
        {
            Code = markerCode,
            Name = markerCode,
            IsActive = true,
            RequiresRoom = false,
            RequiresTeacher = false,
            BlocksRoom = false,
            BlocksTeacher = false,
            CountInPlan = false,
            CountInLoad = false
        };
        fixture.Db.LessonTypes.Add(markerType);
        fixture.Db.TimeSlots.Add(new TimeSlot
        {
            CourseId = ids.CourseId,
            DayOfWeek = DayOfWeek.Monday,
            Start = new TimeOnly(8, 0),
            End = new TimeOnly(9, 0),
            SortOrder = 1,
            IsActive = true
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ScheduleItems.Add(new ScheduleItem
        {
            Date = date,
            DayOfWeek = DayOfWeek.Monday,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(9, 0),
            GroupId = ids.GroupId,
            ModuleId = ids.ModuleId,
            LessonTypeId = markerType.Id,
            BatchKey = $"marker:{markerCode.ToLowerInvariant()}"
        });
        await fixture.Db.SaveChangesAsync();

        var action = await new TeacherDraftsAutogenService(fixture.Db).DraftAutoGen(
            new DraftAutoGenRequest(
                WeekStart: date,
                ClearExisting: false,
                CourseId: ids.CourseId,
                GroupIds: new List<int> { ids.GroupId },
                Days: WeekPreset.MonFri,
                RangeStartDate: date,
                RangeEndDate: date));

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);
        Assert.Equal(0, result.Created);
        var gap = Assert.Single(result.GapDetails ?? new List<AutoGenGapDetail>());
        Assert.Equal(new TimeOnly(8, 0), gap.Start);
        Assert.Equal(new TimeOnly(9, 0), gap.End);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Draft_autogen_counts_keyed_and_legacy_multirow_events_once_without_merging_independent_rows(
        bool useBatchKey)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var ids = await fixture.SeedMinimalScheduleModelAsync();
        var date = new DateOnly(2026, 5, 4);
        fixture.Db.TimeSlots.AddRange(
            new TimeSlot
            {
                CourseId = ids.CourseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(8, 0),
                End = new TimeOnly(9, 0),
                SortOrder = 1,
                IsActive = true
            },
            new TimeSlot
            {
                CourseId = ids.CourseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(9, 0),
                End = new TimeOnly(10, 0),
                SortOrder = 2,
                IsActive = true
            },
            new TimeSlot
            {
                CourseId = ids.CourseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(10, 0),
                End = new TimeOnly(11, 0),
                SortOrder = 3,
                IsActive = true
            },
            new TimeSlot
            {
                CourseId = ids.CourseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(11, 0),
                End = new TimeOnly(12, 0),
                SortOrder = 4,
                IsActive = true
            });
        var firstTeacher = new Teacher { FullName = "Перший викладач логічної події" };
        var secondTeacher = new Teacher { FullName = "Другий викладач логічної події" };
        var room = new Room
        {
            Name = "Аудиторія логічної події",
            Capacity = 30,
            Building = new Building { Name = "Корпус логічної події" }
        };
        fixture.Db.AddRange(firstTeacher, secondTeacher, room);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.TeacherModules.AddRange(
            new TeacherModule { TeacherId = firstTeacher.Id, ModuleId = ids.ModuleId },
            new TeacherModule { TeacherId = secondTeacher.Id, ModuleId = ids.ModuleId });
        fixture.Db.ModuleRooms.Add(new ModuleRoom
        {
            ModuleId = ids.ModuleId,
            RoomId = room.Id
        });
        var logicalEventBatchKey = useBatchKey ? "logical-event" : null;
        fixture.Db.TeacherDraftItems.AddRange(
            new TeacherDraftItem
            {
                Date = date,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(9, 0),
                GroupId = ids.GroupId,
                ModuleId = ids.ModuleId,
                LessonTypeId = ids.LessonTypeId,
                TeacherId = firstTeacher.Id,
                BatchKey = logicalEventBatchKey
            },
            new TeacherDraftItem
            {
                Date = date,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(9, 0),
                GroupId = ids.GroupId,
                ModuleId = ids.ModuleId,
                LessonTypeId = ids.LessonTypeId,
                TeacherId = secondTeacher.Id,
                BatchKey = logicalEventBatchKey
            },
            new TeacherDraftItem
            {
                Date = date,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = new TimeOnly(9, 0),
                EndTime = new TimeOnly(10, 0),
                GroupId = ids.GroupId,
                ModuleId = ids.ModuleId,
                LessonTypeId = ids.LessonTypeId,
                TeacherId = firstTeacher.Id
            });
        await fixture.Db.SaveChangesAsync();

        var action = await new TeacherDraftsAutogenService(fixture.Db).DraftAutoGen(
            new DraftAutoGenRequest(
                WeekStart: date,
                ClearExisting: false,
                CourseId: ids.CourseId,
                GroupIds: new List<int> { ids.GroupId },
                Days: WeekPreset.MonFri,
                ModuleHours: new Dictionary<int, int> { [ids.ModuleId] = 4 },
                RangeStartDate: date,
                RangeEndDate: date));

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);
        Assert.True(
            result.Created == 2,
            $"Очікувалось дві нові чернетки, фактично: {result.Created}. " +
            $"Прогалини: {string.Join(" | ", result.GapDetails?.Select(item => item.Reason) ?? Array.Empty<string>())}. " +
            $"Попередження: {string.Join(" | ", result.Warnings)}");
        fixture.Db.ChangeTracker.Clear();
        var persisted = await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => item.GroupId == ids.GroupId
                           && item.ModuleId == ids.ModuleId
                           && item.Date == date)
            .ToListAsync();
        Assert.Equal(5, persisted.Count);
        Assert.Equal(4, persisted.Select(item => (item.StartTime, item.EndTime)).Distinct().Count());
        Assert.Empty(result.GapDetails ?? new List<AutoGenGapDetail>());
    }

    [Fact]
    public void Time_slot_resolver_does_not_fall_back_to_global_when_course_override_is_inactive()
    {
        const int courseId = 7;
        var slots = new[]
        {
            new TimeSlot
            {
                CourseId = null,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(8, 0),
                End = new TimeOnly(9, 0),
                SortOrder = 1,
                IsActive = true
            },
            new TimeSlot
            {
                CourseId = courseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(10, 0),
                End = new TimeOnly(11, 0),
                SortOrder = 1,
                IsActive = false
            }
        };

        var resolved = TimeSlotsResolver.ResolveForDay(slots, courseId, DayOfWeek.Monday);

        Assert.True(resolved.UsingCourseSpecific);
        Assert.Empty(resolved.Slots);
    }

    [Fact]
    public async Task Official_manual_create_rejects_topicless_lesson_for_module_with_topic_plan()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var ids = await fixture.SeedMinimalScheduleModelAsync();
        fixture.Db.ModuleTopics.Add(new ModuleTopic
        {
            ModuleId = ids.ModuleId,
            LessonTypeId = ids.LessonTypeId,
            TopicCode = "M1.1",
            Order = 1,
            TotalHours = 1,
            AuditoriumHours = 1,
            SelfStudyHours = 0
        });
        fixture.Db.TimeSlots.Add(new TimeSlot
        {
            CourseId = ids.CourseId,
            DayOfWeek = DayOfWeek.Monday,
            Start = new TimeOnly(8, 0),
            End = new TimeOnly(9, 0),
            SortOrder = 1,
            IsActive = true
        });
        await fixture.Db.SaveChangesAsync();

        var rules = new RulesService(fixture.Db);
        var (errors, _) = await rules.ValidateUpsertAsync(new UpsertScheduleItemRequest(
            Id: null,
            Date: new DateOnly(2026, 5, 4),
            TimeStart: "08:00",
            TimeEnd: "09:00",
            GroupId: ids.GroupId,
            ModuleId: ids.ModuleId,
            TeacherId: null,
            RoomId: null,
            LessonTypeId: ids.LessonTypeId,
            IsLocked: false));

        Assert.Contains(errors, error =>
            error.Contains("тематичний план", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Hard_rule_validator_rejects_a_draft_in_lunch_break()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var ids = await fixture.SeedMinimalScheduleModelAsync();
        var date = new DateOnly(2026, 5, 4);
        fixture.Db.TimeSlots.AddRange(
            new TimeSlot
            {
                CourseId = ids.CourseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(8, 0),
                End = new TimeOnly(9, 0),
                SortOrder = 1,
                IsActive = true
            },
            new TimeSlot
            {
                CourseId = ids.CourseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(9, 0),
                End = new TimeOnly(10, 0),
                SortOrder = 2,
                IsActive = true
            });
        fixture.Db.LunchConfigs.Add(new LunchConfig
        {
            CourseId = ids.CourseId,
            Start = new TimeOnly(9, 0),
            End = new TimeOnly(10, 0)
        });
        fixture.Db.TeacherDraftItems.Add(new TeacherDraftItem
        {
            Date = date,
            DayOfWeek = date.DayOfWeek,
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(10, 0),
            GroupId = ids.GroupId,
            ModuleId = ids.ModuleId,
            LessonTypeId = ids.LessonTypeId
        });
        await fixture.Db.SaveChangesAsync();

        var result = await new TeacherDraftsAutogenHardRuleValidator(fixture.Db).ValidateAsync(
            new TeacherDraftsAutogenHardRuleValidationRequest(
                ids.CourseId,
                new[] { ids.GroupId },
                date,
                date));

        Assert.Contains(result.Violations, violation =>
            violation.Contains("слот не відповідає", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Hard_rule_validator_matches_publish_calendar_semantics_for_Saturday(
        bool addWorkingOverride,
        bool expectNonWorkingViolation)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var ids = await fixture.SeedMinimalScheduleModelAsync();
        var saturday = new DateOnly(2026, 5, 9);
        fixture.Db.TimeSlots.Add(new TimeSlot
        {
            CourseId = ids.CourseId,
            DayOfWeek = DayOfWeek.Saturday,
            Start = new TimeOnly(8, 0),
            End = new TimeOnly(9, 0),
            SortOrder = 1,
            IsActive = true
        });
        fixture.Db.TeacherDraftItems.Add(new TeacherDraftItem
        {
            Date = saturday,
            DayOfWeek = DayOfWeek.Saturday,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(9, 0),
            GroupId = ids.GroupId,
            ModuleId = ids.ModuleId,
            LessonTypeId = ids.LessonTypeId
        });
        if (addWorkingOverride)
        {
            fixture.Db.CalendarExceptions.Add(new CalendarException
            {
                Date = saturday,
                IsWorkingDay = true,
                Name = "Робоча субота курсу",
                CourseId = ids.CourseId
            });
        }
        await fixture.Db.SaveChangesAsync();

        var result = await new TeacherDraftsAutogenHardRuleValidator(fixture.Db).ValidateAsync(
            new TeacherDraftsAutogenHardRuleValidationRequest(
                ids.CourseId,
                new[] { ids.GroupId },
                saturday,
                saturday,
                WeekPreset.MonSat,
                AllowIncompleteDrafts: true));

        Assert.Equal(
            expectNonWorkingViolation,
            result.Violations.Any(violation =>
                violation.Contains("неробочий день", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Hard_rule_validator_rejects_overlap_with_approved_draft()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var ids = await fixture.SeedMinimalScheduleModelAsync();
        var date = new DateOnly(2026, 5, 4);
        fixture.Db.TimeSlots.Add(new TimeSlot
        {
            CourseId = ids.CourseId,
            DayOfWeek = DayOfWeek.Monday,
            Start = new TimeOnly(8, 0),
            End = new TimeOnly(9, 0),
            SortOrder = 1,
            IsActive = true
        });
        fixture.Db.TeacherDraftItems.AddRange(
            new TeacherDraftItem
            {
                Date = date,
                DayOfWeek = date.DayOfWeek,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(9, 0),
                GroupId = ids.GroupId,
                ModuleId = ids.ModuleId,
                LessonTypeId = ids.LessonTypeId,
                Status = DraftStatus.Draft
            },
            new TeacherDraftItem
            {
                Date = date,
                DayOfWeek = date.DayOfWeek,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(9, 0),
                GroupId = ids.GroupId,
                ModuleId = ids.ModuleId,
                LessonTypeId = ids.LessonTypeId,
                Status = DraftStatus.Published
            });
        await fixture.Db.SaveChangesAsync();

        var result = await new TeacherDraftsAutogenHardRuleValidator(fixture.Db).ValidateAsync(
            new TeacherDraftsAutogenHardRuleValidationRequest(
                ids.CourseId,
                new[] { ids.GroupId },
                date,
                date));

        Assert.Contains(result.Violations, violation =>
            violation.Contains("перетин групи", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Hard_rule_validator_sums_shared_event_capacity_when_room_does_not_block()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var ids = await fixture.SeedMinimalScheduleModelAsync();
        var date = new DateOnly(2026, 5, 4);
        var secondGroup = new Group
        {
            Name = "T-2",
            StudentsCount = 20,
            CourseId = ids.CourseId
        };
        var lessonType = new LessonTypeRef
        {
            Code = "SHARED_ROOM",
            Name = "Спільне аудиторне заняття",
            IsActive = true,
            RequiresRoom = true,
            RequiresTeacher = false,
            BlocksRoom = false,
            BlocksTeacher = false,
            CountInPlan = true,
            CountInLoad = true
        };
        var building = new Building { Name = "Корпус спільної події" };
        var room = new Room
        {
            Name = "Потік-30",
            Capacity = 30,
            Building = building
        };
        fixture.Db.AddRange(secondGroup, lessonType, room);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.TimeSlots.Add(new TimeSlot
        {
            CourseId = ids.CourseId,
            DayOfWeek = DayOfWeek.Monday,
            Start = new TimeOnly(8, 0),
            End = new TimeOnly(9, 0),
            SortOrder = 1,
            IsActive = true
        });
        fixture.Db.TeacherDraftItems.AddRange(
            new TeacherDraftItem
            {
                Date = date,
                DayOfWeek = date.DayOfWeek,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(9, 0),
                GroupId = ids.GroupId,
                ModuleId = ids.ModuleId,
                LessonTypeId = lessonType.Id,
                RoomId = room.Id,
                BatchKey = "shared-capacity"
            },
            new TeacherDraftItem
            {
                Date = date,
                DayOfWeek = date.DayOfWeek,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(9, 0),
                GroupId = secondGroup.Id,
                ModuleId = ids.ModuleId,
                LessonTypeId = lessonType.Id,
                RoomId = room.Id,
                BatchKey = "shared-capacity"
            });
        await fixture.Db.SaveChangesAsync();

        var result = await new TeacherDraftsAutogenHardRuleValidator(fixture.Db).ValidateAsync(
            new TeacherDraftsAutogenHardRuleValidationRequest(
                ids.CourseId,
                new[] { ids.GroupId, secondGroup.Id },
                date,
                date));

        Assert.Contains(result.Violations, violation =>
            violation.Contains("30 місць для 40 студентів", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Travel_invariant_verifier_includes_approved_drafts()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var ids = await fixture.SeedMinimalScheduleModelAsync();
        var date = new DateOnly(2026, 5, 4);
        var firstBuilding = new Building { Name = "Перший корпус перевірки" };
        var secondBuilding = new Building { Name = "Другий корпус перевірки" };
        var firstRoom = new Room { Name = "П-101", Capacity = 30, Building = firstBuilding };
        var secondRoom = new Room { Name = "Д-201", Capacity = 30, Building = secondBuilding };
        var physicalLessonType = new LessonTypeRef
        {
            Code = "PHYSICAL_TRAVEL",
            Name = "Фізичне заняття для перевірки",
            IsActive = true,
            RequiresRoom = true,
            RequiresTeacher = false,
            BlocksRoom = false,
            BlocksTeacher = false,
            CountInPlan = true,
            CountInLoad = true
        };
        fixture.Db.AddRange(firstRoom, secondRoom, physicalLessonType);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.BuildingTravels.Add(new BuildingTravel
        {
            FromBuildingId = firstBuilding.Id,
            ToBuildingId = secondBuilding.Id,
            Minutes = 20
        });
        fixture.Db.TeacherDraftItems.AddRange(
            new TeacherDraftItem
            {
                Date = date,
                DayOfWeek = date.DayOfWeek,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(9, 0),
                GroupId = ids.GroupId,
                ModuleId = ids.ModuleId,
                LessonTypeId = physicalLessonType.Id,
                RoomId = firstRoom.Id,
                Status = DraftStatus.Draft
            },
            new TeacherDraftItem
            {
                Date = date,
                DayOfWeek = date.DayOfWeek,
                StartTime = new TimeOnly(9, 10),
                EndTime = new TimeOnly(10, 10),
                GroupId = ids.GroupId,
                ModuleId = ids.ModuleId,
                LessonTypeId = physicalLessonType.Id,
                RoomId = secondRoom.Id,
                Status = DraftStatus.Published
            });
        await fixture.Db.SaveChangesAsync();

        var violations = await TravelInvariantVerifier.FindViolationsAsync(
            fixture.Db,
            ids.CourseId,
            date,
            date);

        Assert.Contains(violations, violation =>
            violation.Contains("потрібно 20 хв", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Hard_rule_validator_rejects_draft_outside_teacher_working_hours()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var ids = await fixture.SeedMinimalScheduleModelAsync();
        var date = new DateOnly(2026, 5, 4);
        var teacher = new Teacher { FullName = "Викладач робочого часу" };
        var lessonType = new LessonTypeRef
        {
            Code = "WORK",
            Name = "Практичне заняття",
            IsActive = true,
            RequiresRoom = false,
            RequiresTeacher = true,
            BlocksRoom = false,
            BlocksTeacher = true,
            CountInPlan = true,
            CountInLoad = true
        };
        fixture.Db.AddRange(teacher, lessonType);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.TeacherWorkingHours.Add(new TeacherWorkingHour
        {
            TeacherId = teacher.Id,
            DayOfWeek = DayOfWeek.Monday,
            Start = new TimeOnly(10, 0),
            End = new TimeOnly(12, 0)
        });
        fixture.Db.TimeSlots.Add(new TimeSlot
        {
            CourseId = ids.CourseId,
            DayOfWeek = DayOfWeek.Monday,
            Start = new TimeOnly(8, 0),
            End = new TimeOnly(9, 0),
            SortOrder = 1,
            IsActive = true
        });
        fixture.Db.TeacherDraftItems.Add(new TeacherDraftItem
        {
            Date = date,
            DayOfWeek = DayOfWeek.Monday,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(9, 0),
            GroupId = ids.GroupId,
            ModuleId = ids.ModuleId,
            LessonTypeId = lessonType.Id,
            TeacherId = teacher.Id
        });
        await fixture.Db.SaveChangesAsync();

        var result = await new TeacherDraftsAutogenHardRuleValidator(fixture.Db).ValidateAsync(
            new TeacherDraftsAutogenHardRuleValidationRequest(
                ids.CourseId,
                new[] { ids.GroupId },
                date,
                date));

        Assert.Contains(result.Violations, violation => violation.Contains("робочі години", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Hard_rule_validator_rejects_impossible_transition_between_buildings()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var ids = await fixture.SeedMinimalScheduleModelAsync();
        var date = new DateOnly(2026, 5, 4);
        var firstBuilding = new Building { Name = "Корпус 1" };
        var secondBuilding = new Building { Name = "Корпус 2" };
        fixture.Db.Buildings.AddRange(firstBuilding, secondBuilding);
        await fixture.Db.SaveChangesAsync();
        var firstRoom = new Room { Name = "1-101", Capacity = 40, BuildingId = firstBuilding.Id };
        var secondRoom = new Room { Name = "2-201", Capacity = 40, BuildingId = secondBuilding.Id };
        var lessonType = new LessonTypeRef
        {
            Code = "ROOM",
            Name = "Аудиторне заняття",
            IsActive = true,
            RequiresRoom = true,
            RequiresTeacher = false,
            BlocksRoom = true,
            BlocksTeacher = false,
            CountInPlan = true,
            CountInLoad = true
        };
        var roomlessLessonType = new LessonTypeRef
        {
            Code = "REMOTE",
            Name = "Заняття без аудиторії",
            IsActive = true,
            RequiresRoom = false,
            RequiresTeacher = false,
            BlocksRoom = false,
            BlocksTeacher = false,
            CountInPlan = false,
            CountInLoad = false
        };
        fixture.Db.AddRange(firstRoom, secondRoom, lessonType, roomlessLessonType);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.BuildingTravels.Add(new BuildingTravel
        {
            FromBuildingId = firstBuilding.Id,
            ToBuildingId = secondBuilding.Id,
            Minutes = 30
        });
        fixture.Db.TimeSlots.AddRange(
            new TimeSlot
            {
                CourseId = ids.CourseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(8, 0),
                End = new TimeOnly(9, 0),
                SortOrder = 1,
                IsActive = true
            },
            new TimeSlot
            {
                CourseId = ids.CourseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(9, 0),
                End = new TimeOnly(9, 5),
                SortOrder = 2,
                IsActive = true
            },
            new TimeSlot
            {
                CourseId = ids.CourseId,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(9, 10),
                End = new TimeOnly(10, 10),
                SortOrder = 3,
                IsActive = true
            });
        fixture.Db.TeacherDraftItems.AddRange(
            new TeacherDraftItem
            {
                Date = date,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(9, 0),
                GroupId = ids.GroupId,
                ModuleId = ids.ModuleId,
                LessonTypeId = lessonType.Id,
                RoomId = firstRoom.Id
            },
            new TeacherDraftItem
            {
                Date = date,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = new TimeOnly(9, 0),
                EndTime = new TimeOnly(9, 5),
                GroupId = ids.GroupId,
                ModuleId = ids.ModuleId,
                LessonTypeId = roomlessLessonType.Id
            },
            new TeacherDraftItem
            {
                Date = date,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = new TimeOnly(9, 10),
                EndTime = new TimeOnly(10, 10),
                GroupId = ids.GroupId,
                ModuleId = ids.ModuleId,
                LessonTypeId = lessonType.Id,
                RoomId = secondRoom.Id
            });
        await fixture.Db.SaveChangesAsync();

        var result = await new TeacherDraftsAutogenHardRuleValidator(fixture.Db).ValidateAsync(
            new TeacherDraftsAutogenHardRuleValidationRequest(
                ids.CourseId,
                new[] { ids.GroupId },
                date,
                date));

        Assert.Contains(result.Violations, violation => violation.Contains("перехід між корпусами", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Module_delete_with_draft_returns_conflict_and_preserves_dependencies()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var ids = await fixture.SeedMinimalScheduleModelAsync();
        await SeedDestructiveDependencyGraphAsync(fixture, ids);

        var result = await new AdminModulesController(fixture.Db).Delete(ids.ModuleId, force: true);

        Assert.IsType<ConflictObjectResult>(result);
        await AssertDestructiveDependencyGraphPreservedAsync(fixture, ids);
    }

    [Fact]
    public async Task Module_clear_all_with_draft_returns_conflict_and_preserves_dependencies()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var ids = await fixture.SeedMinimalScheduleModelAsync();
        await SeedDestructiveDependencyGraphAsync(fixture, ids);

        var result = await new AdminModulesController(fixture.Db).ClearAll();

        Assert.IsType<ConflictObjectResult>(result);
        await AssertDestructiveDependencyGraphPreservedAsync(fixture, ids);
    }

    [Fact]
    public async Task Course_delete_with_draft_returns_conflict_and_preserves_dependencies()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var ids = await fixture.SeedMinimalScheduleModelAsync();
        await SeedDestructiveDependencyGraphAsync(fixture, ids);

        var result = await new AdminCoursesController(fixture.Db).Delete(ids.CourseId, force: true);

        Assert.IsType<ConflictObjectResult>(result);
        await AssertDestructiveDependencyGraphPreservedAsync(fixture, ids);
    }

    [Fact]
    public void Autogen_job_service_rejects_invalid_and_excessive_scope()
    {
        var service = CreateAutogenJobService(new RejectingScopeFactory());
        var valid = CreateValidAutoGenJobRequest();
        var invalidRequests = new[]
        {
            valid with { CourseId = 0 },
            valid with { FromDate = default },
            valid with { FromDate = new DateOnly(1999, 12, 31) },
            valid with { ToDate = new DateOnly(2100, 12, 25) },
            valid with { FromDate = new DateOnly(9999, 12, 24), ToDate = new DateOnly(9999, 12, 24) },
            valid with { FromDate = valid.ToDate.AddDays(1) },
            valid with { ToDate = valid.FromDate.AddDays(370) },
            valid with { GroupIds = Enumerable.Range(1, 201).ToList() },
            valid with { GroupIds = new List<int>() },
            valid with { GroupIds = new List<int> { 0 } },
            valid with { ModuleHours = Enumerable.Range(1, 201).ToDictionary(id => id, _ => 1) },
            valid with { ModuleHours = new Dictionary<int, int>() },
            valid with { ModuleHours = new Dictionary<int, int> { [0] = 1 } },
            valid with { ModuleHours = new Dictionary<int, int> { [1] = 501 } },
            valid with { Title = new string('x', 257) },
            valid with { ClientJobId = "not-a-guid" }
        };

        foreach (var request in invalidRequests)
        {
            var error = Assert.Throws<AutoGenJobValidationException>(() => service.Start(request));
            Assert.False(string.IsNullOrWhiteSpace(error.Message));
        }
    }

    [Fact]
    public void Autogen_job_normalization_enforces_kind_flags()
    {
        var valid = CreateValidAutoGenJobRequest();

        var preflight = InvokeNormalizeAutoGenJobRequest(valid with
        {
            Kind = AutoGenJobKind.Preflight,
            ClearExisting = true,
            PreflightOnly = false
        });
        var fill = InvokeNormalizeAutoGenJobRequest(valid with
        {
            Kind = AutoGenJobKind.Fill,
            ClearExisting = true,
            SoftFill = false,
            PreflightOnly = true
        });

        Assert.True(preflight.PreflightOnly);
        Assert.False(preflight.ClearExisting);
        Assert.True(fill.SoftFill);
        Assert.False(fill.ClearExisting);
        Assert.False(fill.PreflightOnly);
    }

    [Fact]
    public void Autogen_job_normalization_rejects_unbounded_soft_options()
    {
        var service = CreateAutogenJobService(new RejectingScopeFactory());
        var valid = CreateValidAutoGenJobRequest();
        var invalidRequests = new[]
        {
            valid with { PreferredFirstMaxSlotOrderOverride = int.MaxValue },
            valid with { SoftOptions = new AutoGenSoftOptionsDto(MaxParallelGroupsPerModuleInSlot: int.MaxValue) },
            valid with { SoftOptions = new AutoGenSoftOptionsDto(RecentRepeatWindowDays: int.MaxValue) },
            valid with { SoftOptions = new AutoGenSoftOptionsDto(PreferredMaxDistinctModulesPerDay: 7, MaxDistinctModulesPerDay: 6) },
            valid with { SoftOptions = new AutoGenSoftOptionsDto(MaxDistinctModulesPerDay: int.MaxValue) },
            valid with { SoftOptions = new AutoGenSoftOptionsDto(PreferredFirstPenaltyMultiplier: double.PositiveInfinity) },
            valid with { SoftOptions = new AutoGenSoftOptionsDto(AdjacentRoomChangePenalty: double.NaN) },
            valid with { SoftOptions = new AutoGenSoftOptionsDto(TeacherLoadPenaltyWeight: double.NegativeInfinity) },
            valid with { SoftOptions = new AutoGenSoftOptionsDto(BuildingDistancePenaltyWeight: double.MaxValue) },
            valid with
            {
                FromDate = DateOnly.MinValue.AddDays(1),
                ToDate = DateOnly.MinValue.AddDays(1),
                SoftOptions = new AutoGenSoftOptionsDto(RecentRepeatWindowDays: 2)
            }
        };

        foreach (var request in invalidRequests)
        {
            var error = Assert.Throws<AutoGenJobValidationException>(() => service.Start(request));
            Assert.False(string.IsNullOrWhiteSpace(error.Message));
        }
    }

    [Fact]
    public void Autogen_job_normalization_preserves_supported_l3_profile()
    {
        var request = CreateValidAutoGenJobRequest() with
        {
            PreferredFirstMaxSlotOrderOverride = 6,
            SoftOptions = new AutoGenSoftOptionsDto(
                MaxParallelGroupsPerModuleInSlot: 4,
                RecentRepeatWindowDays: 0,
                PreferredMaxDistinctModulesPerDay: 5,
                MaxDistinctModulesPerDay: 6,
                PreferredFirstPenaltyMultiplier: 0.35,
                TeacherLoadPenaltyWeight: 0,
                BuildingDistancePenaltyWeight: 0)
        };

        var normalized = InvokeNormalizeAutoGenJobRequest(request);

        Assert.Equal(6, normalized.PreferredFirstMaxSlotOrderOverride);
        Assert.Equal(request.SoftOptions, normalized.SoftOptions);
    }

    [Theory]
    [InlineData(AutoGenJobState.Queued)]
    [InlineData(AutoGenJobState.Running)]
    public async Task Autogen_job_get_marks_orphaned_persisted_execution_as_failed(AutoGenJobState persistedState)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var jobId = Guid.NewGuid().ToString("N");
        fixture.Db.AutoGenJobRuns.Add(new AutoGenJobRun
        {
            JobId = jobId,
            Kind = (int)AutoGenJobKind.Generate,
            State = (int)persistedState,
            Title = "Перерване тестове завдання",
            CurrentStage = persistedState == AutoGenJobState.Queued ? "У черзі" : "Виконується",
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            StartedAtUtc = persistedState == AutoGenJobState.Running ? DateTime.UtcNow.AddMinutes(-4) : null,
            RangeStartDate = new DateOnly(2026, 5, 1),
            RangeEndDate = new DateOnly(2026, 5, 7),
            TotalWeeks = 1,
            Percent = persistedState == AutoGenJobState.Running ? 40 : 0,
            RequestJson = "{}",
            StatusJson = string.Empty,
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-1)
        });
        await fixture.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(fixture.Options));
        using var provider = services.BuildServiceProvider();
        var service = CreateAutogenJobService(provider.GetRequiredService<IServiceScopeFactory>());

        var status = service.Get(jobId);

        Assert.NotNull(status);
        Assert.Equal(AutoGenJobState.Failed, status.State);
        Assert.Equal(100, status.Percent);
        Assert.NotNull(status.CompletedAt);
        Assert.Contains("перезапущено", status.Error, StringComparison.OrdinalIgnoreCase);

        fixture.Db.ChangeTracker.Clear();
        var persisted = await fixture.Db.AutoGenJobRuns.AsNoTracking().SingleAsync(item => item.JobId == jobId);
        Assert.Equal((int)AutoGenJobState.Failed, persisted.State);
        Assert.Equal(100, persisted.Percent);
        Assert.NotNull(persisted.CompletedAtUtc);
        Assert.Contains("перезапуск", persisted.CurrentStage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Autogen_job_controller_returns_problem_details_for_invalid_scope()
    {
        var service = CreateAutogenJobService(new RejectingScopeFactory());
        var controller = CreateTeacherDraftsController(service);
        var request = CreateValidAutoGenJobRequest() with
        {
            FromDate = new DateOnly(2026, 5, 2),
            ToDate = new DateOnly(2026, 5, 1)
        };

        var action = controller.StartAutoGenJob(request);

        var response = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(400, response.StatusCode);
        Assert.IsType<ProblemDetails>(response.Value);
    }

    [Fact]
    public void Legacy_synchronous_autogen_endpoint_is_disabled()
    {
        var service = CreateAutogenJobService(new RejectingScopeFactory());
        var controller = CreateTeacherDraftsController(service);

        var action = controller.DraftAutoGen(new DraftAutoGenRequest(
            WeekStart: new DateOnly(2026, 5, 4),
            CourseId: 1,
            GroupIds: new List<int> { 1 },
            ModuleHours: new Dictionary<int, int> { [1] = 1 }));

        var response = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(410, response.StatusCode);
        Assert.IsType<ProblemDetails>(response.Value);
    }

    [Fact]
    public void Autogen_job_controller_returns_too_many_requests_when_capacity_is_exhausted()
    {
        var scopeFactory = new RejectingScopeFactory();
        var service = CreateAutogenJobService(scopeFactory);
        var request = CreateValidAutoGenJobRequest();
        AddQueuedAutogenJobs(service, request, count: 8);
        var controller = CreateTeacherDraftsController(service);

        var action = controller.StartAutoGenJob(request);

        var response = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(429, response.StatusCode);
        Assert.IsType<ProblemDetails>(response.Value);
        Assert.Equal(0, scopeFactory.CreateScopeCallCount);
    }

    [Fact]
    public void Autogen_job_start_is_idempotent_for_client_job_id()
    {
        var service = CreateAutogenJobService(new RejectingScopeFactory());
        var clientJobId = Guid.NewGuid().ToString("N");
        var request = CreateValidAutoGenJobRequest() with { ClientJobId = clientJobId };

        var first = service.Start(request);
        var second = service.Start(request with { Title = "Повторний запит" });

        Assert.Equal(clientJobId, first.JobId);
        Assert.Equal(clientJobId, second.JobId);
        Assert.Equal(first.Status.Title, second.Status.Title);
        Assert.Single(GetInMemoryAutogenJobStatuses(service));
    }

    [Fact]
    public async Task Autogen_job_start_reuses_persisted_client_job_after_restart()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var clientJobId = Guid.NewGuid().ToString("N");
        fixture.Db.AutoGenJobRuns.Add(new AutoGenJobRun
        {
            JobId = clientJobId,
            Kind = (int)AutoGenJobKind.Generate,
            State = (int)AutoGenJobState.Succeeded,
            Title = "Завершене завдання",
            CurrentStage = "Готово.",
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-2),
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            CompletedAtUtc = DateTime.UtcNow,
            RangeStartDate = new DateOnly(2026, 5, 1),
            RangeEndDate = new DateOnly(2026, 5, 7),
            TotalWeeks = 1,
            CompletedWeeks = 1,
            Percent = 100,
            RequestJson = "{}",
            StatusJson = string.Empty,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await fixture.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(fixture.Options));
        await using var provider = services.BuildServiceProvider();
        var service = CreateAutogenJobService(provider.GetRequiredService<IServiceScopeFactory>());

        var result = service.Start(CreateValidAutoGenJobRequest() with { ClientJobId = clientJobId });

        Assert.Equal(clientJobId, result.JobId);
        Assert.Equal(AutoGenJobState.Succeeded, result.Status.State);
        Assert.Empty(GetInMemoryAutogenJobStatuses(service));
        Assert.Equal(1, await fixture.Db.AutoGenJobRuns.CountAsync(item => item.JobId == clientJobId));
    }

    [Fact]
    public void Autogen_job_cleanup_bounds_terminal_memory_without_removing_active_jobs()
    {
        var service = CreateAutogenJobService(new RejectingScopeFactory());
        var request = CreateValidAutoGenJobRequest();
        var terminalJobIds = AddAutogenJobs(service, request, count: 205, terminal: true);
        var activeJobIds = AddAutogenJobs(service, request, count: 3, terminal: false);

        InvokeAutogenJobCleanup(service);

        var statuses = GetInMemoryAutogenJobStatuses(service);
        Assert.Equal(203, statuses.Count);
        Assert.Equal(200, statuses.Values.Count(status => status.State == AutoGenJobState.Succeeded));
        Assert.Equal(3, statuses.Values.Count(status => status.State == AutoGenJobState.Queued));
        Assert.All(activeJobIds, jobId => Assert.Contains(jobId, statuses));
        Assert.All(terminalJobIds.Take(5), jobId => Assert.DoesNotContain(jobId, statuses));
        Assert.All(terminalJobIds.Skip(5), jobId => Assert.Contains(jobId, statuses));
    }

    [Fact]
    public async Task Autogen_jobs_execute_one_at_a_time_and_waiting_job_can_be_canceled()
    {
        var scopeFactory = new BlockingScopeFactory();
        var service = CreateAutogenJobService(scopeFactory);
        var request = CreateValidAutoGenJobRequest();
        var firstRuntime = CreateAutogenJobRuntime(request);
        var secondRuntime = CreateAutogenJobRuntime(request);
        var secondInvocationReturned = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var firstRun = Task.Run(async () => await InvokeRunAsync(service, firstRuntime));
        Assert.True(scopeFactory.WaitUntilFirstScopeCreated(TimeSpan.FromSeconds(2)));

        var secondRun = Task.Run(async () =>
        {
            var run = InvokeRunAsync(service, secondRuntime);
            secondInvocationReturned.SetResult();
            await run;
        });

        try
        {
            await secondInvocationReturned.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(scopeFactory.WaitUntilSecondScopeCreated(TimeSpan.FromMilliseconds(250)));
            Assert.Equal(AutoGenJobState.Running, GetAutogenJobRuntimeStatus(firstRuntime).State);
            Assert.Equal(AutoGenJobState.Queued, GetAutogenJobRuntimeStatus(secondRuntime).State);

            RequestAutogenJobRuntimeCancellation(secondRuntime);
            scopeFactory.ReleaseFirstScope();
            await secondRun.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(AutoGenJobState.Canceled, GetAutogenJobRuntimeStatus(secondRuntime).State);
        }
        finally
        {
            scopeFactory.ReleaseFirstScope();
        }

        await firstRun.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AutoGenJobState.Failed, GetAutogenJobRuntimeStatus(firstRuntime).State);
    }

    [Fact]
    public async Task Autogen_job_marks_unrequested_operation_cancellation_as_failed()
    {
        var service = CreateAutogenJobService(new OperationCanceledScopeFactory());
        var runtime = CreateAutogenJobRuntime(CreateValidAutoGenJobRequest());

        await InvokeRunAsync(service, runtime).WaitAsync(TimeSpan.FromSeconds(2));

        var status = GetAutogenJobRuntimeStatus(runtime);
        Assert.Equal(AutoGenJobState.Failed, status.State);
        Assert.False(status.CancellationRequested);
        Assert.NotNull(status.CompletedAt);
        Assert.Contains("було перервано", status.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(status.JobId, status.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("тестове переривання", status.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Autogen_job_waits_for_canceled_terminal_snapshot_before_completing()
    {
        var service = CreateAutogenJobService(new RejectingScopeFactory());
        var runtime = CreateAutogenJobRuntime(CreateValidAutoGenJobRequest());
        var gateField = typeof(TeacherDraftsAutogenJobService).GetField(
            "_persistenceGate",
            ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic);
        var persistenceGate = Assert.IsType<SemaphoreSlim>(gateField?.GetValue(service));
        await persistenceGate.WaitAsync();
        Task run;

        try
        {
            RequestAutogenJobRuntimeCancellation(runtime);
            run = InvokeRunAsync(service, runtime);
            await Task.Yield();
            Assert.False(run.IsCompleted);
        }
        finally
        {
            persistenceGate.Release();
        }

        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AutoGenJobState.Canceled, GetAutogenJobRuntimeStatus(runtime).State);
    }

    private static AutoGenJobRequest CreateValidAutoGenJobRequest()
        => new(
            Kind: AutoGenJobKind.Generate,
            FromDate: new DateOnly(2026, 5, 1),
            ToDate: new DateOnly(2026, 5, 7),
            CourseId: 1,
            GroupIds: new List<int> { 1 },
            ModuleHours: new Dictionary<int, int> { [1] = 1 },
            Days: WeekPreset.MonFri,
            ClearExisting: true,
            SoftFill: false,
            PreflightOnly: false);

    private static TeacherDraftsAutogenJobService CreateAutogenJobService(IServiceScopeFactory scopeFactory)
        => new(scopeFactory, NullLogger<TeacherDraftsAutogenJobService>.Instance);

    private static TeacherDraftsController CreateTeacherDraftsController(TeacherDraftsAutogenJobService jobService)
        => new(
            db: null!,
            rules: null!,
            queryService: null!,
            exportService: null!,
            autogenService: null!,
            autogenJobService: jobService,
            publishService: null!);

    private static AutoGenJobRequest InvokeNormalizeAutoGenJobRequest(AutoGenJobRequest request)
    {
        var method = typeof(TeacherDraftsAutogenJobService).GetMethod(
            "NormalizeRequest",
            ReflectionBindingFlags.Static | ReflectionBindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<AutoGenJobRequest>(method.Invoke(null, new object[] { request }));
    }

    private static void AddQueuedAutogenJobs(
        TeacherDraftsAutogenJobService service,
        AutoGenJobRequest request,
        int count)
    {
        var serviceType = typeof(TeacherDraftsAutogenJobService);
        var runtimeType = serviceType.GetNestedType("AutoGenJobRuntime", ReflectionBindingFlags.NonPublic);
        var jobsField = serviceType.GetField("_jobs", ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic);
        Assert.NotNull(runtimeType);
        Assert.NotNull(jobsField);
        var jobs = jobsField.GetValue(service);
        Assert.NotNull(jobs);
        var tryAdd = jobs.GetType().GetMethods()
            .Single(method => method.Name == "TryAdd" && method.GetParameters().Length == 2);
        var jobIdProperty = runtimeType.GetProperty("JobId", ReflectionBindingFlags.Instance | ReflectionBindingFlags.Public);
        Assert.NotNull(jobIdProperty);

        for (var index = 0; index < count; index++)
        {
            var runtime = Activator.CreateInstance(
                runtimeType,
                ReflectionBindingFlags.Instance | ReflectionBindingFlags.Public | ReflectionBindingFlags.NonPublic,
                binder: null,
                args: new object[] { request },
                culture: null);
            Assert.NotNull(runtime);
            var jobId = Assert.IsType<string>(jobIdProperty.GetValue(runtime));
            Assert.True(Assert.IsType<bool>(tryAdd.Invoke(jobs, new[] { jobId, runtime })));
        }
    }

    private static IReadOnlyList<string> AddAutogenJobs(
        TeacherDraftsAutogenJobService service,
        AutoGenJobRequest request,
        int count,
        bool terminal)
    {
        var serviceType = typeof(TeacherDraftsAutogenJobService);
        var runtimeType = serviceType.GetNestedType("AutoGenJobRuntime", ReflectionBindingFlags.NonPublic);
        var jobsField = serviceType.GetField("_jobs", ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic);
        Assert.NotNull(runtimeType);
        Assert.NotNull(jobsField);
        var jobs = jobsField.GetValue(service);
        Assert.NotNull(jobs);
        var tryAdd = jobs.GetType().GetMethods()
            .Single(method => method.Name == "TryAdd" && method.GetParameters().Length == 2);
        var jobIdProperty = runtimeType.GetProperty("JobId", ReflectionBindingFlags.Instance | ReflectionBindingFlags.Public);
        var stateField = runtimeType.GetField("_state", ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic);
        var completedAtField = runtimeType.GetField("_completedAt", ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic);
        Assert.NotNull(jobIdProperty);
        Assert.NotNull(stateField);
        Assert.NotNull(completedAtField);
        var ids = new List<string>(count);
        var now = DateTimeOffset.UtcNow;

        for (var index = 0; index < count; index++)
        {
            var runtime = Activator.CreateInstance(
                runtimeType,
                ReflectionBindingFlags.Instance | ReflectionBindingFlags.Public | ReflectionBindingFlags.NonPublic,
                binder: null,
                args: new object[] { request },
                culture: null);
            Assert.NotNull(runtime);
            if (terminal)
            {
                stateField.SetValue(runtime, AutoGenJobState.Succeeded);
                completedAtField.SetValue(runtime, now.AddMinutes(index - count));
            }
            var jobId = Assert.IsType<string>(jobIdProperty.GetValue(runtime));
            Assert.True(Assert.IsType<bool>(tryAdd.Invoke(jobs, new[] { jobId, runtime })));
            ids.Add(jobId);
        }

        return ids;
    }

    private static void InvokeAutogenJobCleanup(TeacherDraftsAutogenJobService service)
    {
        var method = typeof(TeacherDraftsAutogenJobService).GetMethod(
            "CleanupOldJobs",
            ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(service, null);
    }

    private static IReadOnlyDictionary<string, AutoGenJobStatus> GetInMemoryAutogenJobStatuses(
        TeacherDraftsAutogenJobService service)
    {
        var jobsField = typeof(TeacherDraftsAutogenJobService).GetField(
            "_jobs",
            ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic);
        Assert.NotNull(jobsField);
        var jobs = Assert.IsAssignableFrom<System.Collections.IEnumerable>(jobsField.GetValue(service));
        var result = new Dictionary<string, AutoGenJobStatus>(StringComparer.Ordinal);

        foreach (var entry in jobs)
        {
            Assert.NotNull(entry);
            var entryType = entry.GetType();
            var key = Assert.IsType<string>(entryType.GetProperty("Key")?.GetValue(entry));
            var runtime = entryType.GetProperty("Value")?.GetValue(entry);
            Assert.NotNull(runtime);
            var toDto = runtime.GetType().GetMethod(
                "ToDto",
                ReflectionBindingFlags.Instance | ReflectionBindingFlags.Public);
            Assert.NotNull(toDto);
            result[key] = Assert.IsType<AutoGenJobStatus>(toDto.Invoke(runtime, null));
        }

        return result;
    }

    private static object CreateAutogenJobRuntime(AutoGenJobRequest request)
    {
        var runtimeType = typeof(TeacherDraftsAutogenJobService)
            .GetNestedType("AutoGenJobRuntime", ReflectionBindingFlags.NonPublic);
        Assert.NotNull(runtimeType);
        var runtime = Activator.CreateInstance(
            runtimeType,
            ReflectionBindingFlags.Instance | ReflectionBindingFlags.Public | ReflectionBindingFlags.NonPublic,
            binder: null,
            args: new object[] { request },
            culture: null);
        return Assert.IsAssignableFrom<object>(runtime);
    }

    private static Task InvokeRunAsync(TeacherDraftsAutogenJobService service, object runtime)
    {
        var method = typeof(TeacherDraftsAutogenJobService)
            .GetMethod("RunAsync", ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task>(method.Invoke(service, new[] { runtime }));
    }

    private static AutoGenJobStatus GetAutogenJobRuntimeStatus(object runtime)
    {
        var method = runtime.GetType().GetMethod("ToDto", ReflectionBindingFlags.Instance | ReflectionBindingFlags.Public);
        Assert.NotNull(method);
        return Assert.IsType<AutoGenJobStatus>(method.Invoke(runtime, null));
    }

    private static void RequestAutogenJobRuntimeCancellation(object runtime)
    {
        var method = runtime.GetType().GetMethod(
            "RequestCancellation",
            ReflectionBindingFlags.Instance | ReflectionBindingFlags.Public);
        Assert.NotNull(method);
        method.Invoke(runtime, null);
    }

    private sealed class RejectingScopeFactory : IServiceScopeFactory
    {
        public int CreateScopeCallCount { get; private set; }

        public IServiceScope CreateScope()
        {
            CreateScopeCallCount++;
            throw new InvalidOperationException("Цей тест не повинен створювати фоновий scope.");
        }
    }

    private sealed class BlockingScopeFactory : IServiceScopeFactory
    {
        private readonly ManualResetEventSlim _firstScopeCreated = new();
        private readonly ManualResetEventSlim _secondScopeCreated = new();
        private readonly ManualResetEventSlim _releaseFirstScope = new();
        private int _createScopeCallCount;

        public IServiceScope CreateScope()
        {
            var callNumber = Interlocked.Increment(ref _createScopeCallCount);
            if (callNumber == 1)
            {
                _firstScopeCreated.Set();
                _releaseFirstScope.Wait(TimeSpan.FromSeconds(5));
            }
            else
            {
                _secondScopeCreated.Set();
            }

            throw new InvalidOperationException("Тестова фабрика зупиняє виконання після входу у фоновий scope.");
        }

        public bool WaitUntilFirstScopeCreated(TimeSpan timeout)
            => _firstScopeCreated.Wait(timeout);

        public bool WaitUntilSecondScopeCreated(TimeSpan timeout)
            => _secondScopeCreated.Wait(timeout);

        public void ReleaseFirstScope()
            => _releaseFirstScope.Set();
    }

    private sealed class OperationCanceledScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
            => throw new OperationCanceledException("Тестове переривання без запиту користувача.");
    }

    private static async Task SeedDestructiveDependencyGraphAsync(TestDatabase fixture, SeedIds ids)
    {
        var topic = new ModuleTopic
        {
            ModuleId = ids.ModuleId,
            Order = 1,
            TopicCode = "M1.1",
            LessonTypeId = ids.LessonTypeId,
            TotalHours = 1,
            AuditoriumHours = 0,
            SelfStudyHours = 1
        };
        fixture.Db.ModuleTopics.Add(topic);
        fixture.Db.ModulePlans.Add(new ModulePlan
        {
            CourseId = ids.CourseId,
            ModuleId = ids.ModuleId,
            TargetHours = 1,
            ScheduledHours = 0,
            IsActive = true
        });
        fixture.Db.ModuleCourses.Add(new ModuleCourse
        {
            CourseId = ids.CourseId,
            ModuleId = ids.ModuleId
        });
        fixture.Db.TeacherDraftItems.Add(new TeacherDraftItem
        {
            Date = new DateOnly(2026, 5, 4),
            DayOfWeek = DayOfWeek.Monday,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(9, 0),
            GroupId = ids.GroupId,
            ModuleId = ids.ModuleId,
            ModuleTopic = topic,
            LessonTypeId = ids.LessonTypeId
        });
        await fixture.Db.SaveChangesAsync();
    }

    private static async Task AssertDestructiveDependencyGraphPreservedAsync(TestDatabase fixture, SeedIds ids)
    {
        Assert.True(await fixture.Db.Modules.AnyAsync(module => module.Id == ids.ModuleId));
        Assert.True(await fixture.Db.ModuleTopics.AnyAsync(topic => topic.ModuleId == ids.ModuleId));
        Assert.True(await fixture.Db.ModulePlans.AnyAsync(plan =>
            plan.CourseId == ids.CourseId && plan.ModuleId == ids.ModuleId));
        Assert.True(await fixture.Db.ModuleCourses.AnyAsync(link =>
            link.CourseId == ids.CourseId && link.ModuleId == ids.ModuleId));
        Assert.True(await fixture.Db.TeacherDraftItems.AnyAsync(draft => draft.ModuleId == ids.ModuleId));
    }

    private static async Task<NonBlockingTravelSeed> SeedNonBlockingTravelModelAsync(TestDatabase fixture)
    {
        var ids = await fixture.SeedMinimalScheduleModelAsync();
        var date = new DateOnly(2026, 5, 4);
        var lessonType = new LessonTypeRef
        {
            Code = "ROOM_NONBLOCKING",
            Name = "Аудиторне заняття без блокування",
            IsActive = true,
            RequiresRoom = true,
            RequiresTeacher = false,
            BlocksRoom = false,
            BlocksTeacher = false,
            CountInPlan = true,
            CountInLoad = true
        };
        var firstBuilding = new Building { Name = "Перший корпус правил" };
        var secondBuilding = new Building { Name = "Другий корпус правил" };
        var firstRoom = new Room { Name = "ПР-101", Capacity = 30, Building = firstBuilding };
        var secondRoom = new Room { Name = "ДР-201", Capacity = 30, Building = secondBuilding };
        var teacher = new Teacher { FullName = "Викладач правил переходу" };
        fixture.Db.AddRange(lessonType, firstRoom, secondRoom, teacher);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.TeacherModules.Add(new TeacherModule
        {
            TeacherId = teacher.Id,
            ModuleId = ids.ModuleId
        });
        fixture.Db.BuildingTravels.Add(new BuildingTravel
        {
            FromBuildingId = firstBuilding.Id,
            ToBuildingId = secondBuilding.Id,
            Minutes = 20
        });
        fixture.Db.TimeSlots.AddRange(
            new TimeSlot
            {
                CourseId = ids.CourseId,
                DayOfWeek = date.DayOfWeek,
                Start = new TimeOnly(8, 0),
                End = new TimeOnly(9, 0),
                SortOrder = 1,
                IsActive = true
            },
            new TimeSlot
            {
                CourseId = ids.CourseId,
                DayOfWeek = date.DayOfWeek,
                Start = new TimeOnly(9, 10),
                End = new TimeOnly(10, 10),
                SortOrder = 2,
                IsActive = true
            });
        await fixture.Db.SaveChangesAsync();

        return new NonBlockingTravelSeed(ids, lessonType.Id, firstRoom.Id, secondRoom.Id, teacher.Id, date);
    }

    private sealed record SeedIds(int CourseId, int GroupId, int ModuleId, int LessonTypeId);

    private sealed record NonBlockingTravelSeed(
        SeedIds Ids,
        int LessonTypeId,
        int FirstRoomId,
        int SecondRoomId,
        int TeacherId,
        DateOnly Date);

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private TestDatabase(
            SqliteConnection connection,
            DbContextOptions<AppDbContext> options,
            AppDbContext db)
        {
            _connection = connection;
            Options = options;
            Db = db;
        }

        public AppDbContext Db { get; }
        public DbContextOptions<AppDbContext> Options { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, options, db);
        }

        public async Task<SeedIds> SeedMinimalScheduleModelAsync()
        {
            var course = new Course { Name = "Test course", DurationWeeks = 52 };
            var group = new Group { Name = "T-1", StudentsCount = 20, Course = course };
            var module = new Module { Code = "M1", Title = "Module 1", Credits = 1, Course = course };
            var lessonType = new LessonTypeRef
            {
                Code = "SELF",
                Name = "Self study",
                IsActive = true,
                RequiresRoom = false,
                RequiresTeacher = false,
                BlocksRoom = false,
                BlocksTeacher = false,
                CountInPlan = true,
                CountInLoad = false
            };

            Db.AddRange(course, group, module, lessonType);
            await Db.SaveChangesAsync();

            return new SeedIds(course.Id, group.Id, module.Id, lessonType.Id);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
