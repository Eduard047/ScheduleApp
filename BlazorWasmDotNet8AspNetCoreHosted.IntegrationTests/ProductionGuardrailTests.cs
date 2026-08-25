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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore.Storage;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;

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
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task Autogen_MonSat_uses_configured_Saturday_unless_calendar_marks_it_non_working(
        bool addNonWorkingOverride,
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
        if (addNonWorkingOverride)
        {
            fixture.Db.CalendarExceptions.Add(new CalendarException
            {
                Date = saturday,
                IsWorkingDay = false,
                Name = "Неробоча субота групи",
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
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Rules_official_validation_requires_ten_minutes_only_when_room_changes_inside_building(
        bool keepSameRoom,
        bool expectTransitionError)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var seed = await SeedSameBuildingManualRulesModelAsync(fixture);
        fixture.Db.ScheduleItems.Add(new ScheduleItem
        {
            Date = seed.Monday,
            DayOfWeek = seed.Monday.DayOfWeek,
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
                Date: seed.Monday,
                TimeStart: "09:05",
                TimeEnd: "10:05",
                GroupId: seed.Ids.GroupId,
                ModuleId: seed.Ids.ModuleId,
                TeacherId: null,
                RoomId: keepSameRoom ? seed.FirstRoomId : seed.SecondRoomId,
                LessonTypeId: seed.LessonTypeId,
                IsLocked: false));

        if (expectTransitionError)
        {
            Assert.Contains(errors, error =>
                error.Contains("Замало часу на перехід", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            Assert.Empty(errors);
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Rules_draft_validation_requires_ten_minutes_only_when_room_changes_inside_building(
        bool keepSameRoom,
        bool expectTransitionError)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var seed = await SeedSameBuildingManualRulesModelAsync(fixture);
        fixture.Db.TeacherDraftItems.Add(new TeacherDraftItem
        {
            Date = seed.Monday,
            DayOfWeek = seed.Monday.DayOfWeek,
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
                Date: seed.Monday,
                TimeStart: "09:05",
                TimeEnd: "10:05",
                GroupId: seed.Ids.GroupId,
                ModuleId: seed.Ids.ModuleId,
                ModuleTopicId: null,
                TeacherId: null,
                RoomId: keepSameRoom ? seed.FirstRoomId : seed.SecondRoomId,
                RequiresRoom: true,
                LessonTypeId: seed.LessonTypeId));

        if (expectTransitionError)
        {
            Assert.Contains(result.Report.Issues, issue => issue.Code == "travel-draft-before");
        }
        else
        {
            Assert.Empty(result.Errors);
        }
    }

    [Fact]
    public async Task Rules_official_validation_rejects_blocking_teacher_on_day_without_working_window()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var seed = await SeedSameBuildingManualRulesModelAsync(fixture);
        fixture.Db.TeacherWorkingHours.Add(new TeacherWorkingHour
        {
            TeacherId = seed.TeacherId,
            DayOfWeek = DayOfWeek.Monday,
            Start = new TimeOnly(8, 0),
            End = new TimeOnly(18, 0)
        });
        await fixture.Db.SaveChangesAsync();

        var (errors, _) = await new RulesService(fixture.Db).ValidateUpsertAsync(
            new UpsertScheduleItemRequest(
                Id: null,
                Date: seed.Tuesday,
                TimeStart: "08:00",
                TimeEnd: "09:00",
                GroupId: seed.Ids.GroupId,
                ModuleId: seed.Ids.ModuleId,
                TeacherId: seed.TeacherId,
                RoomId: seed.FirstRoomId,
                LessonTypeId: seed.LessonTypeId,
                IsLocked: false));

        Assert.Contains(errors, error =>
            error.Contains("робочих годин", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Rules_draft_validation_warns_for_blocking_teacher_on_day_without_working_window()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var seed = await SeedSameBuildingManualRulesModelAsync(fixture);
        fixture.Db.TeacherWorkingHours.Add(new TeacherWorkingHour
        {
            TeacherId = seed.TeacherId,
            DayOfWeek = DayOfWeek.Monday,
            Start = new TimeOnly(8, 0),
            End = new TimeOnly(18, 0)
        });
        await fixture.Db.SaveChangesAsync();

        var result = await new RulesService(fixture.Db).ValidateDraftAsync(
            new DraftUpsertRequest(
                Id: null,
                Date: seed.Tuesday,
                TimeStart: "08:00",
                TimeEnd: "09:00",
                GroupId: seed.Ids.GroupId,
                ModuleId: seed.Ids.ModuleId,
                ModuleTopicId: null,
                TeacherId: seed.TeacherId,
                RoomId: seed.FirstRoomId,
                RequiresRoom: true,
                LessonTypeId: seed.LessonTypeId));

        Assert.Contains(result.Report.Issues, issue =>
            issue.Code == "teacher-working-hours"
            && string.Equals(issue.Severity, "warning", StringComparison.OrdinalIgnoreCase));
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
    public async Task Draft_autogen_fills_slot_with_non_occupying_marker(string markerCode)
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
                ModuleHours: new Dictionary<int, int> { [ids.ModuleId] = 1 },
                RangeStartDate: date,
                RangeEndDate: date));

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);
        Assert.Equal(1, result.Created);
        Assert.Empty(result.GapDetails ?? new List<AutoGenGapDetail>());

        var generated = await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .SingleAsync(item => item.GroupId == ids.GroupId
                                 && item.ModuleId == ids.ModuleId
                                 && item.Date == date);
        Assert.Equal(ids.LessonTypeId, generated.LessonTypeId);
        Assert.Equal(new TimeOnly(8, 0), generated.StartTime);
        Assert.Equal(new TimeOnly(9, 0), generated.EndTime);
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

    [Fact]
    public async Task Hard_rule_validator_rejects_empty_canonical_slot_inside_lecture_block()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var ids = await fixture.SeedMinimalScheduleModelAsync();
        var date = new DateOnly(2026, 5, 4);
        var lectureType = new LessonTypeRef
        {
            Code = "LECTURE",
            Name = "Лекція",
            IsActive = true,
            RequiresRoom = false,
            RequiresTeacher = false,
            BlocksRoom = false,
            BlocksTeacher = false,
            CountInPlan = true,
            CountInLoad = true
        };
        fixture.Db.LessonTypes.Add(lectureType);
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
            });
        await fixture.Db.SaveChangesAsync();

        fixture.Db.TeacherDraftItems.AddRange(
            new TeacherDraftItem
            {
                Date = date,
                DayOfWeek = date.DayOfWeek,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(9, 0),
                GroupId = ids.GroupId,
                ModuleId = ids.ModuleId,
                LessonTypeId = lectureType.Id
            },
            new TeacherDraftItem
            {
                Date = date,
                DayOfWeek = date.DayOfWeek,
                StartTime = new TimeOnly(10, 0),
                EndTime = new TimeOnly(11, 0),
                GroupId = ids.GroupId,
                ModuleId = ids.ModuleId,
                LessonTypeId = lectureType.Id
            });
        await fixture.Db.SaveChangesAsync();

        var result = await new TeacherDraftsAutogenHardRuleValidator(fixture.Db).ValidateAsync(
            new TeacherDraftsAutogenHardRuleValidationRequest(
                ids.CourseId,
                new[] { ids.GroupId },
                date,
                date,
                WeekPreset.MonFri,
                AllowIncompleteDrafts: true));

        Assert.Contains(result.Violations, violation =>
            violation.Contains("порожнім канонічним слотом 09:00-10:00", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("single", false)]
    [InlineData("contiguous", false)]
    [InlineData("interrupted", true)]
    [InlineData("nonlecture-before-lecture", true)]
    public async Task Hard_rule_validator_enforces_lecture_prefix_without_rejecting_valid_blocks(
        string scenario,
        bool expectLectureOrderViolation)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var ids = await fixture.SeedMinimalScheduleModelAsync();
        var date = new DateOnly(2026, 5, 4);
        var lectureType = new LessonTypeRef
        {
            Code = "LECTURE",
            Name = "Лекція",
            IsActive = true,
            RequiresRoom = false,
            RequiresTeacher = false,
            BlocksRoom = false,
            BlocksTeacher = false,
            CountInPlan = true,
            CountInLoad = true
        };
        fixture.Db.LessonTypes.Add(lectureType);
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
            });
        await fixture.Db.SaveChangesAsync();

        var lessonTypeIds = scenario switch
        {
            "single" => new[] { lectureType.Id },
            "contiguous" => new[] { lectureType.Id, lectureType.Id, ids.LessonTypeId },
            "interrupted" => new[] { lectureType.Id, ids.LessonTypeId, lectureType.Id },
            "nonlecture-before-lecture" => new[] { ids.LessonTypeId, lectureType.Id },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        };
        var slots = new[]
        {
            (Start: new TimeOnly(8, 0), End: new TimeOnly(9, 0)),
            (Start: new TimeOnly(9, 0), End: new TimeOnly(10, 0)),
            (Start: new TimeOnly(10, 0), End: new TimeOnly(11, 0))
        };
        fixture.Db.TeacherDraftItems.AddRange(lessonTypeIds.Select((lessonTypeId, index) =>
            new TeacherDraftItem
            {
                Date = date,
                DayOfWeek = date.DayOfWeek,
                StartTime = slots[index].Start,
                EndTime = slots[index].End,
                GroupId = ids.GroupId,
                ModuleId = ids.ModuleId,
                LessonTypeId = lessonTypeId
            }));
        await fixture.Db.SaveChangesAsync();

        var result = await new TeacherDraftsAutogenHardRuleValidator(fixture.Db).ValidateAsync(
            new TeacherDraftsAutogenHardRuleValidationRequest(
                ids.CourseId,
                new[] { ids.GroupId },
                date,
                date,
                WeekPreset.MonFri,
                AllowIncompleteDrafts: true));

        Assert.Equal(
            expectLectureOrderViolation,
            result.Violations.Any(violation =>
                violation.Contains("лекційний блок розірвано заняттям", StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Hard_rule_validator_accepts_configured_Saturday_unless_calendar_marks_it_non_working(
        bool addNonWorkingOverride,
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
        if (addNonWorkingOverride)
        {
            fixture.Db.CalendarExceptions.Add(new CalendarException
            {
                Date = saturday,
                IsWorkingDay = false,
                Name = "Неробоча субота курсу",
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
                violation.Contains("неробоч", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Hard_rule_validator_rejects_more_than_four_groups_of_same_module_at_once()
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
        var extraGroups = Enumerable.Range(2, 4)
            .Select(index => new Group
            {
                Name = $"П-{index}",
                StudentsCount = 15,
                CourseId = ids.CourseId
            })
            .ToList();
        fixture.Db.Groups.AddRange(extraGroups);
        await fixture.Db.SaveChangesAsync();
        var groupIds = new[] { ids.GroupId }
            .Concat(extraGroups.Select(group => group.Id))
            .ToList();
        fixture.Db.TeacherDraftItems.AddRange(groupIds.Select(groupId => new TeacherDraftItem
        {
            Date = date,
            DayOfWeek = date.DayOfWeek,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(9, 0),
            GroupId = groupId,
            ModuleId = ids.ModuleId,
            LessonTypeId = ids.LessonTypeId
        }));
        await fixture.Db.SaveChangesAsync();

        var result = await new TeacherDraftsAutogenHardRuleValidator(fixture.Db).ValidateAsync(
            new TeacherDraftsAutogenHardRuleValidationRequest(
                ids.CourseId,
                groupIds,
                date,
                date,
                WeekPreset.MonFri,
                AllowIncompleteDrafts: true,
                MaxParallelGroupsPerModuleInSlot: 4));

        Assert.Contains(result.Violations, violation =>
            violation.Contains("одночасно поставлено для 5 груп", StringComparison.OrdinalIgnoreCase));
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
    public void Room_transition_policy_requires_ten_minutes_between_different_rooms()
    {
        var travelMinutes = new Dictionary<(int FromBuildingId, int ToBuildingId), int>();

        Assert.Equal(
            0,
            RoomTransitionPolicy.Resolve(
                travelMinutes,
                fromRoomId: 10,
                fromBuildingId: 1,
                toRoomId: 10,
                toBuildingId: 1));
        Assert.Equal(
            10,
            RoomTransitionPolicy.Resolve(
                travelMinutes,
                fromRoomId: 10,
                fromBuildingId: 1,
                toRoomId: 11,
                toBuildingId: 1));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Hard_rule_validator_allows_five_minute_transition_only_in_same_room(
        bool keepSameRoom,
        bool expectTransitionViolation)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var ids = await fixture.SeedMinimalScheduleModelAsync();
        var date = new DateOnly(2026, 5, 4);
        var building = new Building { Name = "Навчальний корпус" };
        fixture.Db.Buildings.Add(building);
        await fixture.Db.SaveChangesAsync();
        var firstRoom = new Room { Name = "101", Capacity = 40, BuildingId = building.Id };
        var secondRoom = new Room { Name = "102", Capacity = 40, BuildingId = building.Id };
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
        fixture.Db.AddRange(firstRoom, secondRoom, lessonType);
        await fixture.Db.SaveChangesAsync();
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
                Start = new TimeOnly(9, 5),
                End = new TimeOnly(10, 5),
                SortOrder = 2,
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
                StartTime = new TimeOnly(9, 5),
                EndTime = new TimeOnly(10, 5),
                GroupId = ids.GroupId,
                ModuleId = ids.ModuleId,
                LessonTypeId = lessonType.Id,
                RoomId = keepSameRoom ? firstRoom.Id : secondRoom.Id
            });
        await fixture.Db.SaveChangesAsync();

        var result = await new TeacherDraftsAutogenHardRuleValidator(fixture.Db).ValidateAsync(
            new TeacherDraftsAutogenHardRuleValidationRequest(
                ids.CourseId,
                new[] { ids.GroupId },
                date,
                date));

        Assert.Equal(
            expectTransitionViolation,
            result.Violations.Any(violation =>
                violation.Contains("зміну аудиторії", StringComparison.OrdinalIgnoreCase)
                && violation.Contains("потрібно 10 хв", StringComparison.OrdinalIgnoreCase)));
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
    public void Autogen_job_normalization_enforces_kind_flags_and_preserves_generate_soft_fill()
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
        var generate = InvokeNormalizeAutoGenJobRequest(valid with
        {
            Kind = AutoGenJobKind.Generate,
            SoftFill = true,
            PreflightOnly = true,
            PreviewOnly = true
        });

        Assert.True(preflight.PreflightOnly);
        Assert.False(preflight.ClearExisting);
        Assert.True(fill.SoftFill);
        Assert.False(fill.ClearExisting);
        Assert.False(fill.PreflightOnly);
        Assert.True(generate.SoftFill);
        Assert.False(generate.PreflightOnly);
        Assert.True(generate.PreviewOnly);
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

    [Fact]
    public void Autogen_job_run_ranges_keep_multiweek_scope_in_one_shared_segment()
    {
        var ranges = InvokeBuildAutogenRunRanges(
            new DateOnly(2026, 5, 6),
            new DateOnly(2026, 5, 19),
            new[]
            {
                new DateOnly(2026, 5, 4),
                new DateOnly(2026, 5, 11),
                new DateOnly(2026, 5, 18)
            });

        var range = Assert.Single(ranges);
        Assert.Equal((0, new DateOnly(2026, 5, 4), new DateOnly(2026, 5, 6), new DateOnly(2026, 5, 19)), range);
    }

    [Fact]
    public void Autogen_job_run_ranges_preserve_single_week_scope()
    {
        var ranges = InvokeBuildAutogenRunRanges(
            new DateOnly(2026, 5, 6),
            new DateOnly(2026, 5, 8),
            new[] { new DateOnly(2026, 5, 4) });

        var range = Assert.Single(ranges);
        Assert.Equal((0, new DateOnly(2026, 5, 4), new DateOnly(2026, 5, 6), new DateOnly(2026, 5, 8)), range);
    }

    [Fact]
    public async Task Autogen_ambient_transaction_saves_week_changes_without_committing_them()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var scenario = await SeedAtomicAutogenScenarioAsync(fixture.Db);
        var service = new TeacherDraftsAutogenService(fixture.Db);

        await using (var transaction = await fixture.Db.Database.BeginTransactionAsync(
                         System.Data.IsolationLevel.Serializable))
        {
            var action = await InvokeAmbientDraftAutoGenAsync(service, scenario.Request);
            var ok = Assert.IsType<OkObjectResult>(action.Result);
            var result = Assert.IsType<AutoGenResult>(ok.Value);

            Assert.True(
                result.Created == 1,
                $"Створено: {result.Created}. Пропущено: {result.Skipped}. Попередження: {string.Join(" | ", result.Warnings)}. Прогалини: {string.Join(" | ", result.GapDetails?.Select(item => item.Reason) ?? Array.Empty<string>())}.");
            Assert.Equal(1, await fixture.Db.TeacherDraftItems.CountAsync(item =>
                item.GroupId == scenario.Ids.GroupId && item.Date == scenario.Date));
            await transaction.RollbackAsync();
        }

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(0, await fixture.Db.TeacherDraftItems.CountAsync(item =>
            item.GroupId == scenario.Ids.GroupId && item.Date == scenario.Date));
    }

    [Fact]
    public async Task Autogen_owned_transaction_preserves_existing_commit_behavior()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var scenario = await SeedAtomicAutogenScenarioAsync(fixture.Db);

        var action = await new TeacherDraftsAutogenService(fixture.Db).DraftAutoGen(scenario.Request);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);

        Assert.True(
            result.Created == 1,
            $"Створено: {result.Created}. Пропущено: {result.Skipped}. Попередження: {string.Join(" | ", result.Warnings)}. Прогалини: {string.Join(" | ", result.GapDetails?.Select(item => item.Reason) ?? Array.Empty<string>())}.");
        Assert.Null(fixture.Db.Database.CurrentTransaction);
        Assert.Equal(1, await fixture.Db.TeacherDraftItems.CountAsync(item =>
            item.GroupId == scenario.Ids.GroupId && item.Date == scenario.Date));
    }

    [Fact]
    public async Task Autogen_ambient_preflight_keeps_simulation_visible_until_outer_rollback()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var scenario = await SeedAtomicAutogenScenarioAsync(fixture.Db);
        var service = new TeacherDraftsAutogenService(fixture.Db);

        await using (var transaction = await fixture.Db.Database.BeginTransactionAsync(
                         System.Data.IsolationLevel.Serializable))
        {
            var action = await InvokeAmbientDraftAutoGenAsync(
                service,
                scenario.Request with { ClearExisting = false, PreflightOnly = true });
            var ok = Assert.IsType<OkObjectResult>(action.Result);
            var result = Assert.IsType<AutoGenResult>(ok.Value);

            Assert.Equal(0, result.Created);
            Assert.Equal(1, await fixture.Db.TeacherDraftItems.CountAsync(item =>
                item.GroupId == scenario.Ids.GroupId && item.Date == scenario.Date));
            await transaction.RollbackAsync();
        }

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(0, await fixture.Db.TeacherDraftItems.CountAsync(item =>
            item.GroupId == scenario.Ids.GroupId && item.Date == scenario.Date));
    }

    [Fact]
    public async Task Autogen_preview_job_persists_plan_but_keeps_draft_scope_unchanged()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var scenario = await SeedAtomicAutogenScenarioAsync(fixture.Db);
        var course = await fixture.Db.Courses.SingleAsync(item => item.Id == scenario.Ids.CourseId);
        course.AcademicPeriodStartDate = scenario.Date;
        await fixture.Db.SaveChangesAsync();
        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(fixture.Options));
        services.AddScoped<TeacherDraftsAutogenService>();
        services.AddScoped<TeacherDraftsAutogenPlanService>();
        await using var provider = services.BuildServiceProvider();
        var jobService = CreateAutogenJobService(provider.GetRequiredService<IServiceScopeFactory>());
        var request = new AutoGenJobRequest(
            AutoGenJobKind.Generate,
            scenario.Date,
            scenario.Date,
            scenario.Ids.CourseId,
            new List<int> { scenario.Ids.GroupId },
            new Dictionary<int, int> { [scenario.Ids.ModuleId] = 1 },
            WeekPreset.MonFri,
            true,
            false,
            false,
            AllowIncompleteDrafts: true,
            ClientJobId: Guid.NewGuid().ToString("N"),
            PreviewOnly: true);

        var started = jobService.Start(request);
        AutoGenJobStatus? status = started.Status;
        for (var attempt = 0; attempt < 1200 && status?.State is AutoGenJobState.Queued or AutoGenJobState.Running; attempt++)
        {
            await Task.Delay(25);
            status = await jobService.GetAsync(started.JobId);
        }

        Assert.NotNull(status);
        Assert.Equal(AutoGenJobState.Succeeded, status.State);
        Assert.Equal(1, status.Created);
        Assert.NotNull(status.Plan);
        Assert.Equal(AutoGenPlanState.Ready, status.Plan.State);
        Assert.Equal(1, status.Plan.AddCount);
        await jobService.StopAsync(CancellationToken.None);
        await using var verification = new AppDbContext(fixture.Options);
        Assert.Empty(await verification.TeacherDraftItems.AsNoTracking().ToListAsync());
        var plan = await verification.AutoGenDraftPlans
            .AsNoTracking()
            .Include(item => item.Mutations)
            .SingleAsync(item => item.PlanId == started.JobId);
        Assert.Equal(1, plan.AddCount);
        Assert.Single(plan.Mutations);
        Assert.Equal((int)AutoGenPlanOperation.Add, plan.Mutations.Single().Operation);
    }

    [Fact]
    public async Task Autogen_preview_relaxed_repair_rolls_back_when_required_resources_are_missing()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var scenario = await SeedAtomicAutogenScenarioAsync(fixture.Db);
        var course = await fixture.Db.Courses.SingleAsync(item => item.Id == scenario.Ids.CourseId);
        course.AcademicPeriodStartDate = scenario.Date;
        var requiredModule = new Module
        {
            Code = "ATM-MISSING",
            Title = "Модуль без доступних ресурсів",
            Credits = 1,
            Course = course
        };
        var requiredLessonType = new LessonTypeRef
        {
            Code = "ATOMIC_REQUIRED",
            Name = "Аудиторне заняття з обов'язковими ресурсами",
            IsActive = true,
            RequiresTeacher = true,
            RequiresRoom = true,
            BlocksTeacher = true,
            BlocksRoom = true,
            CountInPlan = true,
            CountInLoad = true
        };
        fixture.Db.AddRange(requiredModule, requiredLessonType);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ModuleTopics.AddRange(
            new ModuleTopic
            {
                ModuleId = scenario.Ids.ModuleId,
                LessonTypeId = scenario.Ids.LessonTypeId,
                TopicCode = "ATM.1",
                Order = 1,
                TotalHours = 1,
                AuditoriumHours = 0,
                SelfStudyHours = 1
            },
            new ModuleTopic
            {
                ModuleId = requiredModule.Id,
                LessonTypeId = requiredLessonType.Id,
                TopicCode = "ATM-MISSING.1",
                Order = 1,
                TotalHours = 1,
                AuditoriumHours = 1,
                SelfStudyHours = 0
            });
        fixture.Db.TimeSlots.Add(new TimeSlot
        {
            CourseId = scenario.Ids.CourseId,
            DayOfWeek = scenario.Date.DayOfWeek,
            Start = new TimeOnly(9, 10),
            End = new TimeOnly(10, 10),
            SortOrder = 2,
            IsActive = true
        });
        fixture.Db.TeacherModules.RemoveRange(await fixture.Db.TeacherModules.ToListAsync());
        fixture.Db.Rooms.RemoveRange(await fixture.Db.Rooms.ToListAsync());
        await fixture.Db.SaveChangesAsync();
        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(fixture.Options));
        services.AddScoped<TeacherDraftsAutogenService>();
        services.AddScoped<TeacherDraftsAutogenPlanService>();
        await using var provider = services.BuildServiceProvider();
        var jobService = CreateAutogenJobService(provider.GetRequiredService<IServiceScopeFactory>());
        var request = new AutoGenJobRequest(
            AutoGenJobKind.Generate,
            scenario.Date,
            scenario.Date,
            scenario.Ids.CourseId,
            new List<int> { scenario.Ids.GroupId },
            new Dictionary<int, int>
            {
                [scenario.Ids.ModuleId] = 1,
                [requiredModule.Id] = 1
            },
            WeekPreset.MonFri,
            true,
            false,
            false,
            AllowIncompleteDrafts: false,
            ClientJobId: Guid.NewGuid().ToString("N"),
            PreviewOnly: true);

        var started = jobService.Start(request);
        AutoGenJobStatus? status = started.Status;
        for (var attempt = 0;
             attempt < 1200 && status?.State is AutoGenJobState.Queued or AutoGenJobState.Running;
             attempt++)
        {
            await Task.Delay(25);
            status = await jobService.GetAsync(started.JobId);
        }

        Assert.NotNull(status);
        Assert.Equal(AutoGenJobState.Succeeded, status.State);
        Assert.NotNull(status.Result);
        Assert.Equal(1, status.Result.Created);
        Assert.Single(status.Result.GapDetails ?? []);
        Assert.DoesNotContain(
            status.Result.Warnings,
            warning => AutoGenWarningClassifier.Classify(warning).Code
                       == AutoGenWarningCodes.IncompleteDrafts);
        Assert.NotNull(status.Plan);
        Assert.Equal(AutoGenPlanState.Ready, status.Plan.State);
        Assert.Equal(1, status.Plan.AddCount);
        Assert.Equal(0, status.Plan.UpdateCount);
        Assert.Equal(0, status.Plan.DeleteCount);
        await jobService.StopAsync(CancellationToken.None);
        await using (var previewVerification = new AppDbContext(fixture.Options))
        {
            Assert.Empty(await previewVerification.TeacherDraftItems.AsNoTracking().ToListAsync());
        }
        var readyPlan = await jobService.GetPlanAsync(started.JobId);
        Assert.Single(readyPlan.Changes);
        var appliedPlan = await jobService.ApplyPlanAsync(
            started.JobId,
            new AutoGenPlanActionRequest(readyPlan.Summary.Version));
        Assert.Equal(AutoGenPlanState.Applied, appliedPlan.Summary.State);
        Assert.Single(appliedPlan.Changes);

        await using var verification = new AppDbContext(fixture.Options);
        var appliedDraft = Assert.Single(await verification.TeacherDraftItems
            .AsNoTracking()
            .ToListAsync());
        Assert.Equal(scenario.Ids.ModuleId, appliedDraft.ModuleId);
        Assert.Null(appliedDraft.TeacherId);
        Assert.Null(appliedDraft.RoomId);
        var hardRuleValidation = await new TeacherDraftsAutogenHardRuleValidator(verification)
            .ValidateAsync(
                new TeacherDraftsAutogenHardRuleValidationRequest(
                    scenario.Ids.CourseId,
                    new[] { scenario.Ids.GroupId },
                    scenario.Date,
                    scenario.Date,
                    WeekPreset.MonFri,
                    AllowIncompleteDrafts: false));
        Assert.Empty(hardRuleValidation.Violations);
    }

    [Theory]
    [InlineData(AutoGenJobState.Canceled)]
    [InlineData(AutoGenJobState.Failed)]
    public async Task Autogen_preview_terminal_failure_after_plan_attachment_does_not_persist_ready_plan(
        AutoGenJobState terminalState)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var request = CreateValidAutoGenJobRequest() with
        {
            ClientJobId = Guid.NewGuid().ToString("N"),
            PreviewOnly = true
        };
        await SeedAutogenAcademicPeriodAsync(fixture.Db, request.CourseId, request.FromDate);
        var run = CreatePersistedAutogenJob(
            request.ClientJobId!,
            request,
            AutoGenJobState.Running,
            DateTime.UtcNow.AddMinutes(5));
        fixture.Db.AutoGenJobRuns.Add(run);
        await fixture.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(fixture.Options));
        await using var provider = services.BuildServiceProvider();
        var service = CreateAutogenJobService(provider.GetRequiredService<IServiceScopeFactory>());
        var runtime = CreateAutogenJobRuntime(request);
        AttachAutogenJobRuntimeClaim(
            runtime,
            run.OwnerInstanceId!,
            run.Attempt,
            run.LeaseExpiresAtUtc!.Value);
        AttachAutogenJobRuntimePlan(runtime, request);

        Assert.NotNull(GetAutogenJobRuntimePlanPayload(runtime));
        Assert.Equal(AutoGenPlanState.Ready, GetAutogenJobRuntimeStatus(runtime).Plan?.State);

        MarkAutogenJobRuntimeTerminal(runtime, request, terminalState);

        var terminalStatus = GetAutogenJobRuntimeStatus(runtime);
        Assert.Equal(terminalState, terminalStatus.State);
        Assert.Null(terminalStatus.Plan);
        Assert.Null(GetAutogenJobRuntimePlanPayload(runtime));
        Assert.True(await InvokeTryPersistOwnedSnapshotAsync(service, runtime));

        await using var verification = new AppDbContext(fixture.Options);
        Assert.Empty(await verification.AutoGenDraftPlans.AsNoTracking().ToListAsync());
        var persistedRun = await verification.AutoGenJobRuns
            .AsNoTracking()
            .SingleAsync(item => item.JobId == request.ClientJobId);
        Assert.Equal((int)terminalState, persistedRun.State);
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
            OwnerInstanceId = "orphaned-owner",
            Attempt = 1,
            LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1),
            Version = 3,
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

        var status = await service.GetAsync(jobId);

        Assert.NotNull(status);
        Assert.Equal(AutoGenJobState.Failed, status.State);
        Assert.Equal(100, status.Percent);
        Assert.NotNull(status.CompletedAt);
        Assert.Contains("результат виконання невідомий", status.Error, StringComparison.OrdinalIgnoreCase);

        fixture.Db.ChangeTracker.Clear();
        var persisted = await fixture.Db.AutoGenJobRuns.AsNoTracking().SingleAsync(item => item.JobId == jobId);
        Assert.Equal((int)AutoGenJobState.Failed, persisted.State);
        Assert.Equal(100, persisted.Percent);
        Assert.NotNull(persisted.CompletedAtUtc);
        Assert.Contains("lease", persisted.CurrentStage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Autogen_job_controller_returns_problem_details_for_invalid_scope()
    {
        var service = CreateAutogenJobService(new RejectingScopeFactory());
        var controller = CreateTeacherDraftsController(service);
        var request = CreateValidAutoGenJobRequest() with
        {
            FromDate = new DateOnly(2026, 5, 2),
            ToDate = new DateOnly(2026, 5, 1),
            PreviewOnly = true
        };

        var action = controller.StartAutoGenJob(request);

        var response = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(400, response.StatusCode);
        Assert.IsType<ProblemDetails>(response.Value);
    }

    [Fact]
    public void Autogen_job_controller_rejects_direct_generate_commit()
    {
        var service = CreateAutogenJobService(new RejectingScopeFactory());
        var controller = CreateTeacherDraftsController(service);
        var request = CreateValidAutoGenJobRequest() with { PreviewOnly = false };

        var action = controller.StartAutoGenJob(request);

        var response = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(400, response.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(response.Value);
        Assert.Contains("поперед", problem.Title, StringComparison.OrdinalIgnoreCase);
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
    public async Task Autogen_job_controller_returns_too_many_requests_when_capacity_is_exhausted()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var request = CreateValidAutoGenJobRequest() with { PreviewOnly = true };
        await SeedAutogenAcademicPeriodAsync(fixture.Db, request.CourseId, request.FromDate);
        fixture.Db.AutoGenJobRuns.AddRange(Enumerable.Range(0, 8).Select(_ =>
            CreatePersistedAutogenJob(
                Guid.NewGuid().ToString("N"),
                request,
                AutoGenJobState.Queued,
                DateTime.UtcNow.AddMinutes(5))));
        await fixture.Db.SaveChangesAsync();
        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(fixture.Options));
        await using var provider = services.BuildServiceProvider();
        var service = CreateAutogenJobService(provider.GetRequiredService<IServiceScopeFactory>());
        var controller = CreateTeacherDraftsController(service);

        var action = controller.StartAutoGenJob(request);

        var response = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(429, response.StatusCode);
        Assert.IsType<ProblemDetails>(response.Value);
        Assert.Empty(GetInMemoryAutogenJobStatuses(service));
        Assert.Equal(8, await fixture.Db.AutoGenJobRuns.CountAsync());
    }

    [Fact]
    public async Task Autogen_job_start_requires_academic_period_but_keeps_existing_idempotent_read()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        fixture.Db.Courses.Add(new Course
        {
            Id = 1,
            Name = "Курс перевірки періоду",
            DurationWeeks = 52
        });
        await fixture.Db.SaveChangesAsync();
        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(fixture.Options));
        await using var provider = services.BuildServiceProvider();
        var service = CreateAutogenJobService(provider.GetRequiredService<IServiceScopeFactory>());
        var request = CreateValidAutoGenJobRequest() with { ClientJobId = Guid.NewGuid().ToString("N") };

        var missingPeriod = Assert.Throws<AutoGenJobValidationException>(() => service.Start(request));
        Assert.Contains("навчального періоду", missingPeriod.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Db.AutoGenJobRuns.AsNoTracking().ToListAsync());

        var course = await fixture.Db.Courses.SingleAsync();
        course.AcademicPeriodStartDate = request.FromDate.AddDays(1);
        await fixture.Db.SaveChangesAsync();
        var beforePeriod = Assert.Throws<AutoGenJobValidationException>(() => service.Start(request));
        Assert.Contains("передує", beforePeriod.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Db.AutoGenJobRuns.AsNoTracking().ToListAsync());

        course.AcademicPeriodStartDate = request.FromDate;
        await fixture.Db.SaveChangesAsync();
        var executionGate = await HoldAutogenJobServiceGateAsync(service, "_executionGate");
        try
        {
            var started = service.Start(request);
            var persistenceGate = await HoldAutogenJobServiceGateAsync(service, "_persistenceGate");
            try
            {
                // Оновлення lease працює ще до проходження бар'єра виконання, тому ізолюємо спільне SQLite-з'єднання тесту.
                course.AcademicPeriodStartDate = null;
                await fixture.Db.SaveChangesAsync();

                await using var observerProvider = services.BuildServiceProvider();
                var observer = CreateAutogenJobService(observerProvider.GetRequiredService<IServiceScopeFactory>());
                var repeated = observer.Start(request with { Title = "Повторне читання" });

                Assert.Equal(started.JobId, repeated.JobId);
                Assert.Equal(started.Status.State, repeated.Status.State);
                Assert.Equal(1, await fixture.Db.AutoGenJobRuns.CountAsync());
            }
            finally
            {
                persistenceGate.Release();
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            executionGate.Release();
        }
    }

    [Fact]
    public async Task Autogen_job_revalidates_academic_period_after_waiting_in_queue()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var request = CreateValidAutoGenJobRequest() with { ClientJobId = Guid.NewGuid().ToString("N") };
        await SeedAutogenAcademicPeriodAsync(fixture.Db, request.CourseId, request.FromDate);
        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(fixture.Options));
        services.AddScoped<TeacherDraftsAutogenService>();
        await using var provider = services.BuildServiceProvider();
        var service = CreateAutogenJobService(provider.GetRequiredService<IServiceScopeFactory>());
        var executionGate = await HoldAutogenJobServiceGateAsync(service, "_executionGate");
        var gateReleased = false;

        try
        {
            service.Start(request);
            var course = await fixture.Db.Courses.SingleAsync(item => item.Id == request.CourseId);
            course.AcademicPeriodStartDate = null;
            await fixture.Db.SaveChangesAsync();
            executionGate.Release();
            gateReleased = true;

            await WaitUntilAsync(
                async () => (await service.GetAsync(request.ClientJobId!))?.State == AutoGenJobState.Failed,
                TimeSpan.FromSeconds(5));

            var status = await service.GetAsync(request.ClientJobId!);
            Assert.NotNull(status);
            Assert.Contains("навчального періоду", status.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await fixture.Db.TeacherDraftItems.AsNoTracking().ToListAsync());
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            if (!gateReleased)
            {
                executionGate.Release();
            }
        }
    }

    [Fact]
    public async Task Autogen_job_start_is_idempotent_for_client_job_id()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await SeedAutogenAcademicPeriodAsync(fixture.Db, 1, new DateOnly(2026, 1, 1));
        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(fixture.Options));
        await using var provider = services.BuildServiceProvider();
        var service = CreateAutogenJobService(provider.GetRequiredService<IServiceScopeFactory>());
        var executionGate = await HoldAutogenJobServiceGateAsync(service, "_executionGate");
        var clientJobId = Guid.NewGuid().ToString("N");
        var request = CreateValidAutoGenJobRequest() with
        {
            ClientJobId = clientJobId,
            PreviewOnly = true
        };

        try
        {
            var first = service.Start(request);
            var second = service.Start(request with { Title = "Повторний запит" });

            Assert.Equal(clientJobId, first.JobId);
            Assert.Equal(clientJobId, second.JobId);
            Assert.Equal(first.Status.Title, second.Status.Title);
            Assert.Single(GetInMemoryAutogenJobStatuses(service));
            Assert.Equal(1, await fixture.Db.AutoGenJobRuns.CountAsync(item => item.JobId == clientJobId));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            executionGate.Release();
        }
    }

    [Fact]
    public async Task Autogen_job_start_reuses_persisted_client_job_after_restart()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var clientJobId = Guid.NewGuid().ToString("N");
        var request = CreateValidAutoGenJobRequest() with
        {
            ClientJobId = clientJobId,
            PreviewOnly = true
        };
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
            RequestJson = System.Text.Json.JsonSerializer.Serialize(request),
            StatusJson = string.Empty,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await fixture.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(fixture.Options));
        await using var provider = services.BuildServiceProvider();
        var service = CreateAutogenJobService(provider.GetRequiredService<IServiceScopeFactory>());

        var result = service.Start(request);

        Assert.Equal(clientJobId, result.JobId);
        Assert.Equal(AutoGenJobState.Succeeded, result.Status.State);
        Assert.Empty(GetInMemoryAutogenJobStatuses(service));
        Assert.Equal(1, await fixture.Db.AutoGenJobRuns.CountAsync(item => item.JobId == clientJobId));
    }

    [Fact]
    public async Task Autogen_job_concurrent_duplicate_across_instances_creates_one_run_and_queues_once()
    {
        await using var fixture = await SharedAutogenJobDatabase.CreateAsync();
        await using var firstProvider = fixture.CreateProvider();
        await using var secondProvider = fixture.CreateProvider();
        var firstService = CreateAutogenJobService(firstProvider.GetRequiredService<IServiceScopeFactory>());
        var secondService = CreateAutogenJobService(secondProvider.GetRequiredService<IServiceScopeFactory>());
        var firstExecutionGate = await HoldAutogenJobServiceGateAsync(firstService, "_executionGate");
        var secondExecutionGate = await HoldAutogenJobServiceGateAsync(secondService, "_executionGate");
        var clientJobId = Guid.NewGuid().ToString("N");
        var request = CreateValidAutoGenJobRequest() with
        {
            ClientJobId = clientJobId,
            PreviewOnly = true
        };

        try
        {
            using var startBarrier = new Barrier(2);
            var firstStart = Task.Run(() =>
            {
                Assert.True(startBarrier.SignalAndWait(TimeSpan.FromSeconds(5)));
                return firstService.Start(request);
            });
            var secondStart = Task.Run(() =>
            {
                Assert.True(startBarrier.SignalAndWait(TimeSpan.FromSeconds(5)));
                return secondService.Start(request with { Title = "Повторний запит з іншого вузла" });
            });

            var results = await Task.WhenAll(firstStart, secondStart).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.All(results, result => Assert.Equal(clientJobId, result.JobId));
            Assert.Equal(
                1,
                GetInMemoryAutogenJobStatuses(firstService).Count
                + GetInMemoryAutogenJobStatuses(secondService).Count);
            await using var db = fixture.CreateContext();
            Assert.Equal(1, await db.AutoGenJobRuns.CountAsync(item => item.JobId == clientJobId));
        }
        finally
        {
            await firstService.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            await secondService.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            firstExecutionGate.Release();
            secondExecutionGate.Release();
        }
    }

    [Fact]
    public async Task Autogen_job_rejects_reused_client_id_with_different_payload_across_instances()
    {
        await using var fixture = await SharedAutogenJobDatabase.CreateAsync();
        await using var ownerProvider = fixture.CreateProvider();
        await using var contenderProvider = fixture.CreateProvider();
        var ownerService = CreateAutogenJobService(ownerProvider.GetRequiredService<IServiceScopeFactory>());
        var contenderService = CreateAutogenJobService(contenderProvider.GetRequiredService<IServiceScopeFactory>());
        var ownerExecutionGate = await HoldAutogenJobServiceGateAsync(ownerService, "_executionGate");
        var contenderExecutionGate = await HoldAutogenJobServiceGateAsync(contenderService, "_executionGate");
        var clientJobId = Guid.NewGuid().ToString("N");
        var request = CreateValidAutoGenJobRequest() with
        {
            ClientJobId = clientJobId,
            PreviewOnly = true
        };

        try
        {
            ownerService.Start(request);
            var controller = CreateTeacherDraftsController(contenderService);

            var action = controller.StartAutoGenJob(request with { CourseId = request.CourseId + 1 });

            var response = Assert.IsType<ObjectResult>(action.Result);
            Assert.Equal(409, response.StatusCode);
            Assert.IsType<ProblemDetails>(response.Value);
            Assert.Empty(GetInMemoryAutogenJobStatuses(contenderService));
            await using var db = fixture.CreateContext();
            Assert.Equal(1, await db.AutoGenJobRuns.CountAsync(item => item.JobId == clientJobId));
        }
        finally
        {
            await ownerService.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            await contenderService.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            ownerExecutionGate.Release();
            contenderExecutionGate.Release();
        }
    }

    [Fact]
    public async Task Autogen_job_fresh_remote_get_is_read_only()
    {
        await using var fixture = await SharedAutogenJobDatabase.CreateAsync();
        var request = CreateValidAutoGenJobRequest() with { ClientJobId = Guid.NewGuid().ToString("N") };
        await using (var db = fixture.CreateContext())
        {
            db.AutoGenJobRuns.Add(CreatePersistedAutogenJob(
                request.ClientJobId!,
                request,
                AutoGenJobState.Running,
                DateTime.UtcNow.AddMinutes(5)));
            await db.SaveChangesAsync();
        }

        await using var provider = fixture.CreateProvider();
        var service = CreateAutogenJobService(provider.GetRequiredService<IServiceScopeFactory>());
        AutoGenJobRun before;
        await using (var db = fixture.CreateContext())
        {
            before = await db.AutoGenJobRuns.AsNoTracking().SingleAsync(item => item.JobId == request.ClientJobId);
        }

        var status = await service.GetAsync(request.ClientJobId!);

        Assert.NotNull(status);
        Assert.Equal(AutoGenJobState.Running, status.State);
        Assert.Empty(GetInMemoryAutogenJobStatuses(service));
        await using var verificationDb = fixture.CreateContext();
        var after = await verificationDb.AutoGenJobRuns.AsNoTracking().SingleAsync(item => item.JobId == request.ClientJobId);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.UpdatedAtUtc, after.UpdatedAtUtc);
        Assert.Equal(before.LeaseExpiresAtUtc, after.LeaseExpiresAtUtc);
        Assert.Equal(before.CancellationRequested, after.CancellationRequested);
        Assert.Equal(before.CurrentStage, after.CurrentStage);
        Assert.Equal(before.StatusJson, after.StatusJson);
    }

    [Fact]
    public async Task Autogen_job_remote_cancel_is_persisted_and_owner_polling_cancels_execution()
    {
        await using var fixture = await SharedAutogenJobDatabase.CreateAsync();
        await using var ownerProvider = fixture.CreateProvider();
        await using var remoteProvider = fixture.CreateProvider();
        var ownerService = CreateAutogenJobService(ownerProvider.GetRequiredService<IServiceScopeFactory>());
        var remoteService = CreateAutogenJobService(remoteProvider.GetRequiredService<IServiceScopeFactory>());
        var executionGate = await HoldAutogenJobServiceGateAsync(ownerService, "_executionGate");
        var request = CreateValidAutoGenJobRequest() with { ClientJobId = Guid.NewGuid().ToString("N") };
        var ownerStopped = false;

        try
        {
            ownerService.Start(request);

            var cancellationStatus = await remoteService.CancelAsync(request.ClientJobId!);

            Assert.NotNull(cancellationStatus);
            Assert.True(cancellationStatus.CancellationRequested);
            Assert.NotEqual(AutoGenJobState.Canceled, cancellationStatus.State);
            await WaitUntilAsync(
                async () => (await ownerService.GetAsync(request.ClientJobId!))?.CancellationRequested == true,
                TimeSpan.FromSeconds(7));
            await ownerService.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            ownerStopped = true;

            await using var db = fixture.CreateContext();
            var persisted = await db.AutoGenJobRuns.AsNoTracking().SingleAsync(item => item.JobId == request.ClientJobId);
            Assert.True(persisted.CancellationRequested);
            Assert.Equal((int)AutoGenJobState.Canceled, persisted.State);
            Assert.Null(persisted.LeaseExpiresAtUtc);
        }
        finally
        {
            if (!ownerStopped)
            {
                await ownerService.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }
            executionGate.Release();
        }
    }

    [Fact]
    public async Task Autogen_job_expired_lease_becomes_failed_and_is_not_replayed()
    {
        await using var fixture = await SharedAutogenJobDatabase.CreateAsync();
        var request = CreateValidAutoGenJobRequest() with { ClientJobId = Guid.NewGuid().ToString("N") };
        await using (var db = fixture.CreateContext())
        {
            db.AutoGenJobRuns.Add(CreatePersistedAutogenJob(
                request.ClientJobId!,
                request,
                AutoGenJobState.Running,
                DateTime.UtcNow.AddMinutes(-1)));
            await db.SaveChangesAsync();
        }
        await using var provider = fixture.CreateProvider();
        var service = CreateAutogenJobService(provider.GetRequiredService<IServiceScopeFactory>());

        var recovered = await service.GetAsync(request.ClientJobId!);
        var retry = service.Start(request);

        Assert.NotNull(recovered);
        Assert.Equal(AutoGenJobState.Failed, recovered.State);
        Assert.Contains("результат виконання невідомий", recovered.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AutoGenJobState.Failed, retry.Status.State);
        Assert.Empty(GetInMemoryAutogenJobStatuses(service));
        await using var verificationDb = fixture.CreateContext();
        var persisted = await verificationDb.AutoGenJobRuns.AsNoTracking().SingleAsync(item => item.JobId == request.ClientJobId);
        Assert.Equal((int)AutoGenJobState.Failed, persisted.State);
        Assert.Null(persisted.LeaseExpiresAtUtc);
        Assert.Equal(1, await verificationDb.AutoGenJobRuns.CountAsync(item => item.JobId == request.ClientJobId));
    }

    [Theory]
    [InlineData("other-owner", 1, 5)]
    [InlineData("runtime-owner", 2, 5)]
    [InlineData("runtime-owner", 1, -1)]
    public async Task Autogen_job_terminal_write_requires_current_owner_attempt_and_lease(
        string persistedOwner,
        int persistedAttempt,
        int leaseOffsetMinutes)
    {
        await using var fixture = await SharedAutogenJobDatabase.CreateAsync();
        var request = CreateValidAutoGenJobRequest() with { ClientJobId = Guid.NewGuid().ToString("N") };
        var persistedRun = CreatePersistedAutogenJob(
            request.ClientJobId!,
            request,
            AutoGenJobState.Running,
            DateTime.UtcNow.AddMinutes(leaseOffsetMinutes));
        persistedRun.OwnerInstanceId = persistedOwner;
        persistedRun.Attempt = persistedAttempt;
        await using (var db = fixture.CreateContext())
        {
            db.AutoGenJobRuns.Add(persistedRun);
            await db.SaveChangesAsync();
        }
        await using var provider = fixture.CreateProvider();
        var service = CreateAutogenJobService(provider.GetRequiredService<IServiceScopeFactory>());
        var runtime = CreateAutogenJobRuntime(request);
        AttachAutogenJobRuntimeClaim(runtime, "runtime-owner", 1, DateTime.UtcNow.AddMinutes(5));
        MarkAutogenJobRuntimeSucceeded(runtime, request);

        var persisted = await InvokeTryPersistOwnedSnapshotAsync(service, runtime);

        Assert.False(persisted);
        await using var verificationDb = fixture.CreateContext();
        var unchanged = await verificationDb.AutoGenJobRuns.AsNoTracking().SingleAsync(item => item.JobId == request.ClientJobId);
        Assert.Equal((int)AutoGenJobState.Running, unchanged.State);
        Assert.Equal(persistedOwner, unchanged.OwnerInstanceId);
        Assert.Equal(persistedAttempt, unchanged.Attempt);
        Assert.Equal(7, unchanged.Version);
    }

    [Fact]
    public async Task Autogen_job_upgrade_barrier_keeps_legacy_active_run_read_only_and_blocks_new_ids()
    {
        await using var fixture = await SharedAutogenJobDatabase.CreateAsync();
        var legacyRequest = CreateValidAutoGenJobRequest() with { ClientJobId = Guid.NewGuid().ToString("N") };
        var legacyRun = CreatePersistedAutogenJob(
            legacyRequest.ClientJobId!,
            legacyRequest,
            AutoGenJobState.Running,
            leaseExpiresAtUtc: null);
        legacyRun.OwnerInstanceId = null;
        legacyRun.Attempt = 0;
        legacyRun.Version = 11;
        await using (var db = fixture.CreateContext())
        {
            db.AutoGenJobRuns.Add(legacyRun);
            await db.SaveChangesAsync();
        }
        await using var provider = fixture.CreateProvider();
        var service = CreateAutogenJobService(provider.GetRequiredService<IServiceScopeFactory>());

        var readOnlyStatus = await service.GetAsync(legacyRequest.ClientJobId!);
        var sameId = service.Start(legacyRequest with { Title = "Повтор legacy-запиту" });
        var blocked = Assert.Throws<AutoGenJobPersistenceException>(() => service.Start(
            CreateValidAutoGenJobRequest() with { ClientJobId = Guid.NewGuid().ToString("N") }));

        Assert.NotNull(readOnlyStatus);
        Assert.Equal(AutoGenJobState.Running, readOnlyStatus.State);
        Assert.Equal(AutoGenJobState.Running, sameId.Status.State);
        Assert.Contains("попередньої версії", blocked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(GetInMemoryAutogenJobStatuses(service));
        await using var verificationDb = fixture.CreateContext();
        var unchanged = await verificationDb.AutoGenJobRuns.AsNoTracking().SingleAsync(item => item.JobId == legacyRequest.ClientJobId);
        Assert.Equal((int)AutoGenJobState.Running, unchanged.State);
        Assert.Equal(11, unchanged.Version);
        Assert.Equal(legacyRun.UpdatedAtUtc, unchanged.UpdatedAtUtc);
        Assert.Equal(legacyRun.StatusJson, unchanged.StatusJson);
        Assert.Null(unchanged.OwnerInstanceId);
        Assert.Equal(0, unchanged.Attempt);
        Assert.Null(unchanged.LeaseExpiresAtUtc);
    }

    [Fact]
    public async Task Autogen_job_lease_claim_and_expiry_use_database_utc_under_node_clock_skew()
    {
        var databaseUtcNow = DateTime.SpecifyKind(DateTime.UtcNow.AddHours(2), DateTimeKind.Utc);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        var staleRequest = CreateValidAutoGenJobRequest() with { ClientJobId = Guid.NewGuid().ToString("N") };
        await using (var setupDb = new AppDbContext(options))
        {
            await setupDb.Database.EnsureCreatedAsync();
            await SeedAutogenAcademicPeriodAsync(setupDb, 1, new DateOnly(2026, 1, 1));
            setupDb.AutoGenJobRuns.Add(CreatePersistedAutogenJob(
                staleRequest.ClientJobId!,
                staleRequest,
                AutoGenJobState.Running,
                DateTime.UtcNow.AddMinutes(30)));
            await setupDb.SaveChangesAsync();
        }
        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(options));
        await using var provider = services.BuildServiceProvider();
        var service = CreateAutogenJobServiceWithTiming(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TeacherDraftsAutogenJobService>.Instance,
            applicationLifetime: null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(1),
            databaseUtcNow);

        var expired = await service.GetAsync(staleRequest.ClientJobId!);

        Assert.NotNull(expired);
        Assert.Equal(AutoGenJobState.Failed, expired.State);
        Assert.NotNull(expired.CompletedAt);
        Assert.InRange(expired.CompletedAt.Value.UtcDateTime, databaseUtcNow.AddSeconds(-1), databaseUtcNow.AddSeconds(1));

        var executionGate = await HoldAutogenJobServiceGateAsync(service, "_executionGate");
        var claimRequest = CreateValidAutoGenJobRequest() with { ClientJobId = Guid.NewGuid().ToString("N") };
        SemaphoreSlim? persistenceGate = null;
        try
        {
            service.Start(claimRequest);
            persistenceGate = await HoldAutogenJobServiceGateAsync(service, "_persistenceGate");
            await using var verificationDb = new AppDbContext(options);
            var claimed = await verificationDb.AutoGenJobRuns.AsNoTracking().SingleAsync(item => item.JobId == claimRequest.ClientJobId);
            Assert.NotNull(claimed.LeaseExpiresAtUtc);
            Assert.InRange(
                claimed.LeaseExpiresAtUtc.Value,
                databaseUtcNow.AddSeconds(29),
                databaseUtcNow.AddSeconds(31));
            Assert.InRange(claimed.UpdatedAtUtc, databaseUtcNow.AddSeconds(-1), databaseUtcNow.AddSeconds(1));
        }
        finally
        {
            persistenceGate?.Release();
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            executionGate.Release();
        }
    }

    [Fact]
    public async Task Autogen_job_sqlite_exclusive_transaction_uses_extended_lease_without_self_expiry()
    {
        await using var fixture = await SharedAutogenJobDatabase.CreateAsync();
        var request = CreateValidAutoGenJobRequest() with { ClientJobId = Guid.NewGuid().ToString("N") };
        await using (var setupDb = fixture.CreateContext())
        {
            setupDb.AutoGenJobRuns.Add(CreatePersistedAutogenJob(
                request.ClientJobId!,
                request,
                AutoGenJobState.Running,
                DateTime.UtcNow.AddMinutes(5)));
            await setupDb.SaveChangesAsync();
        }
        await using var provider = fixture.CreateProvider();
        var service = CreateAutogenJobServiceWithTiming(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TeacherDraftsAutogenJobService>.Instance,
            applicationLifetime: null,
            leaseDuration: TimeSpan.FromMilliseconds(80),
            heartbeatInterval: TimeSpan.FromMilliseconds(20),
            cancellationGrace: TimeSpan.FromSeconds(1),
            terminalPersistenceHorizon: TimeSpan.FromSeconds(1));
        var runtime = CreateAutogenJobRuntime(request);
        AttachAutogenJobRuntimeClaim(runtime, "test-owner", 1, DateTime.UtcNow.AddMinutes(5));

        await using var executionDb = fixture.CreateContext();
        Assert.True(await InvokeEnterSqliteExclusiveExecutionLeaseAsync(service, executionDb, runtime));
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = InvokeMaintainLeaseAsync(service, runtime, heartbeatStop.Token);
        await Task.Delay(250);

        Assert.False(GetAutogenJobRuntimeStatus(runtime).CancellationRequested);
        await using var verificationDb = fixture.CreateContext();
        var persisted = await verificationDb.AutoGenJobRuns
            .AsNoTracking()
            .SingleAsync(item => item.JobId == request.ClientJobId);
        Assert.True(persisted.LeaseExpiresAtUtc > DateTime.UtcNow.AddHours(5));

        heartbeatStop.Cancel();
        await heartbeat.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Autogen_job_queued_sqlite_heartbeat_cannot_shorten_exclusive_lease()
    {
        await using var fixture = await SharedAutogenJobDatabase.CreateAsync();
        var request = CreateValidAutoGenJobRequest() with { ClientJobId = Guid.NewGuid().ToString("N") };
        var run = CreatePersistedAutogenJob(
            request.ClientJobId!,
            request,
            AutoGenJobState.Running,
            DateTime.UtcNow.AddMinutes(5));
        await using (var setupDb = fixture.CreateContext())
        {
            setupDb.AutoGenJobRuns.Add(run);
            await setupDb.SaveChangesAsync();
        }
        await using var provider = fixture.CreateProvider();
        var service = CreateAutogenJobServiceWithTiming(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TeacherDraftsAutogenJobService>.Instance,
            applicationLifetime: null,
            leaseDuration: TimeSpan.FromMilliseconds(80),
            heartbeatInterval: TimeSpan.FromMilliseconds(20),
            cancellationGrace: TimeSpan.FromSeconds(1),
            terminalPersistenceHorizon: TimeSpan.FromSeconds(1));
        var runtime = CreateAutogenJobRuntime(request);
        AttachAutogenJobRuntimeClaim(runtime, "test-owner", 1, DateTime.UtcNow.AddMinutes(5));
        var persistenceGate = await HoldAutogenJobServiceGateAsync(service, "_persistenceGate");

        try
        {
            var queuedRenewal = InvokeRenewAutogenJobLeaseAsync(service, runtime);
            var extendedLease = DateTime.UtcNow.AddHours(6);
            await using (var updateDb = fixture.CreateContext())
            {
                await updateDb.AutoGenJobRuns
                    .Where(item => item.JobId == request.ClientJobId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.LeaseExpiresAtUtc, extendedLease));
            }
            AddSqliteExclusiveExecutionMarker(service, request.ClientJobId!);
            persistenceGate.Release();
            persistenceGate = null!;

            await queuedRenewal.WaitAsync(TimeSpan.FromSeconds(2));
            await using var verificationDb = fixture.CreateContext();
            var persistedLease = await verificationDb.AutoGenJobRuns
                .Where(item => item.JobId == request.ClientJobId)
                .Select(item => item.LeaseExpiresAtUtc)
                .SingleAsync();
            Assert.True(persistedLease >= extendedLease.AddSeconds(-1));
        }
        finally
        {
            persistenceGate?.Release();
        }
    }

    [Fact]
    public async Task Autogen_job_commit_phase_ignores_cancellation_that_arrives_after_commit_starts()
    {
        using var cancellation = new CancellationTokenSource();
        var transaction = new RecordingDbContextTransaction(() => cancellation.Cancel());

        await InvokeCommitExecutionTransactionAsync(transaction);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(transaction.CommitCalled);
        Assert.False(transaction.CommitCancellationToken.CanBeCanceled);
    }

    [Theory]
    [InlineData(true, 5)]
    [InlineData(false, -1)]
    public async Task Autogen_job_commit_fence_rejects_remote_cancel_or_expired_lease(
        bool cancellationRequested,
        int leaseOffsetMinutes)
    {
        await using var fixture = await SharedAutogenJobDatabase.CreateAsync();
        var request = CreateValidAutoGenJobRequest() with { ClientJobId = Guid.NewGuid().ToString("N") };
        var run = CreatePersistedAutogenJob(
            request.ClientJobId!,
            request,
            AutoGenJobState.Running,
            DateTime.UtcNow.AddMinutes(leaseOffsetMinutes));
        run.OwnerInstanceId = "commit-fence-owner";
        run.Attempt = 1;
        run.CancellationRequested = cancellationRequested;
        await using (var setupDb = fixture.CreateContext())
        {
            setupDb.AutoGenJobRuns.Add(run);
            await setupDb.SaveChangesAsync();
        }
        await using var provider = fixture.CreateProvider();
        var service = CreateAutogenJobService(provider.GetRequiredService<IServiceScopeFactory>());
        var runtime = CreateAutogenJobRuntime(request);
        AttachAutogenJobRuntimeClaim(runtime, "commit-fence-owner", 1, DateTime.UtcNow.AddMinutes(5));

        await using var executionDb = fixture.CreateContext();
        await using var transaction = await executionDb.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => InvokeEnsureAutogenCommitFenceAsync(service, executionDb, runtime));
        await transaction.RollbackAsync();

        var status = GetAutogenJobRuntimeStatus(runtime);
        Assert.True(status.CancellationRequested);
        Assert.Contains(
            cancellationRequested ? "скасування" : "lease",
            status.CurrentStage,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Autogen_job_local_sqlite_cancel_bypasses_blocked_writer_connection()
    {
        var service = CreateAutogenJobService(new RejectingScopeFactory());
        var request = CreateValidAutoGenJobRequest() with { ClientJobId = Guid.NewGuid().ToString("N") };
        var runtime = CreateAutogenJobRuntime(request);
        AddAutogenJobRuntime(service, request.ClientJobId!, runtime);
        AddSqliteExclusiveExecutionMarker(service, request.ClientJobId!);

        var status = await service.CancelAsync(request.ClientJobId!);

        Assert.NotNull(status);
        Assert.True(status.CancellationRequested);
        Assert.Contains("скасування", status.CurrentStage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Autogen_job_hung_cancellation_stops_renewal_and_requests_host_shutdown()
    {
        await using var fixture = await SharedAutogenJobDatabase.CreateAsync();
        await using var provider = fixture.CreateProvider();
        var lifetime = new TestHostApplicationLifetime();
        var logger = new TestLogger<TeacherDraftsAutogenJobService>();
        var service = CreateAutogenJobServiceWithTiming(
            provider.GetRequiredService<IServiceScopeFactory>(),
            logger,
            lifetime,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(150),
            TimeSpan.FromMilliseconds(300));
        var request = CreateValidAutoGenJobRequest() with { ClientJobId = Guid.NewGuid().ToString("N") };
        var runtime = CreateAutogenJobRuntime(request);
        AttachAutogenJobRuntimeClaim(runtime, "hung-owner", 1, DateTime.UtcNow.AddMinutes(5));
        var run = CreatePersistedAutogenJob(
            request.ClientJobId!,
            request,
            AutoGenJobState.Running,
            DateTime.UtcNow.AddMinutes(5));
        run.OwnerInstanceId = "hung-owner";
        await using (var db = fixture.CreateContext())
        {
            db.AutoGenJobRuns.Add(run);
            await db.SaveChangesAsync();
        }
        RequestAutogenJobRuntimeCancellation(runtime);

        await InvokeMaintainLeaseAsync(service, runtime, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(lifetime.StopRequested);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Critical);
        long versionAfterGrace;
        await using (var db = fixture.CreateContext())
        {
            versionAfterGrace = await db.AutoGenJobRuns
                .Where(item => item.JobId == request.ClientJobId)
                .Select(item => item.Version)
                .SingleAsync();
        }
        await Task.Delay(100);
        await using var verificationDb = fixture.CreateContext();
        Assert.Equal(
            versionAfterGrace,
            await verificationDb.AutoGenJobRuns
                .Where(item => item.JobId == request.ClientJobId)
                .Select(item => item.Version)
                .SingleAsync());
    }

    [Fact]
    public async Task Autogen_job_terminal_persistence_recovers_after_transient_outage_longer_than_two_seconds()
    {
        await using var fixture = await SharedAutogenJobDatabase.CreateAsync();
        await using var provider = fixture.CreateProvider();
        var innerScopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var request = CreateValidAutoGenJobRequest() with { ClientJobId = Guid.NewGuid().ToString("N") };
        var runtime = CreateAutogenJobRuntime(request);
        AttachAutogenJobRuntimeClaim(runtime, "recovery-owner", 1, DateTime.UtcNow.AddMinutes(5));
        MarkAutogenJobRuntimeSucceeded(runtime, request);
        var run = CreatePersistedAutogenJob(
            request.ClientJobId!,
            request,
            AutoGenJobState.Running,
            DateTime.UtcNow.AddMinutes(5));
        run.OwnerInstanceId = "recovery-owner";
        await using (var db = fixture.CreateContext())
        {
            db.AutoGenJobRuns.Add(run);
            await db.SaveChangesAsync();
        }
        var service = CreateAutogenJobServiceWithTiming(
            new DelayedRecoveryScopeFactory(innerScopeFactory, TimeSpan.FromMilliseconds(2_200)),
            NullLogger<TeacherDraftsAutogenJobService>.Instance,
            applicationLifetime: null,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5));
        var timer = System.Diagnostics.Stopwatch.StartNew();

        await InvokePersistTerminalSnapshotAsync(service, runtime, request).WaitAsync(TimeSpan.FromSeconds(7));

        Assert.True(timer.Elapsed >= TimeSpan.FromSeconds(2));
        await using var verificationDb = fixture.CreateContext();
        var persisted = await verificationDb.AutoGenJobRuns.AsNoTracking().SingleAsync(item => item.JobId == request.ClientJobId);
        Assert.Equal((int)AutoGenJobState.Succeeded, persisted.State);
        Assert.Null(persisted.LeaseExpiresAtUtc);
    }

    [Fact]
    public async Task Autogen_job_cancel_returns_authoritative_expired_terminal_and_marks_local_as_infrastructure_loss()
    {
        await using var fixture = await SharedAutogenJobDatabase.CreateAsync();
        await using var ownerProvider = fixture.CreateProvider();
        await using var observerProvider = fixture.CreateProvider();
        var ownerService = CreateAutogenJobServiceWithTiming(
            ownerProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TeacherDraftsAutogenJobService>.Instance,
            applicationLifetime: null,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(300));
        var observerService = CreateAutogenJobService(observerProvider.GetRequiredService<IServiceScopeFactory>());
        var executionGate = await HoldAutogenJobServiceGateAsync(ownerService, "_executionGate");
        var request = CreateValidAutoGenJobRequest() with { ClientJobId = Guid.NewGuid().ToString("N") };
        ownerService.Start(request);
        var runtime = GetAutogenJobRuntime(ownerService, request.ClientJobId!);
        var persistenceGate = await HoldAutogenJobServiceGateAsync(ownerService, "_persistenceGate");

        try
        {
            await using (var db = fixture.CreateContext())
            {
                await db.AutoGenJobRuns
                    .Where(item => item.JobId == request.ClientJobId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.LeaseExpiresAtUtc, DateTime.UtcNow.AddMinutes(-1))
                        .SetProperty(item => item.Version, item => item.Version + 1));
            }
            var expired = await observerService.GetAsync(request.ClientJobId!);
            Assert.NotNull(expired);
            Assert.Equal(AutoGenJobState.Failed, expired.State);
        }
        finally
        {
            persistenceGate.Release();
        }

        var canceled = await ownerService.CancelAsync(request.ClientJobId!);

        Assert.NotNull(canceled);
        Assert.Equal(AutoGenJobState.Failed, canceled.State);
        Assert.Empty(GetInMemoryAutogenJobStatuses(ownerService));
        await ownerService.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(AutoGenJobState.Failed, GetAutogenJobRuntimeStatus(runtime).State);
        Assert.Contains("результат виконання невідомий", GetAutogenJobRuntimeStatus(runtime).Error, StringComparison.OrdinalIgnoreCase);
        executionGate.Release();
    }

    [Fact]
    public void Autogen_job_global_lock_name_is_stable_bounded_and_database_namespaced()
    {
        var first = InvokeBuildAutogenGlobalExecutionLockName("schedule_a");
        var repeat = InvokeBuildAutogenGlobalExecutionLockName("SCHEDULE_A");
        var second = InvokeBuildAutogenGlobalExecutionLockName("schedule_b");

        Assert.Equal(first, repeat);
        Assert.NotEqual(first, second);
        Assert.InRange(first.Length, 1, 64);
        Assert.InRange(second.Length, 1, 64);
    }

    [Fact]
    public async Task Autogen_job_controller_maps_persistence_failures_to_service_unavailable()
    {
        var service = CreateAutogenJobService(new RejectingScopeFactory());
        var controller = CreateTeacherDraftsController(service);
        var request = CreateValidAutoGenJobRequest() with { PreviewOnly = true };

        var start = Assert.IsType<ObjectResult>(controller.StartAutoGenJob(request).Result);
        using var operationGate = new ExpensiveOperationGate();
        var get = Assert.IsType<ObjectResult>((await controller.GetAutoGenJob(
            Guid.NewGuid().ToString("N"),
            operationGate,
            CancellationToken.None)).Result);
        var cancel = Assert.IsType<ObjectResult>((await controller.CancelAutoGenJob(
            Guid.NewGuid().ToString("N"),
            operationGate,
            CancellationToken.None)).Result);

        Assert.Equal(503, start.StatusCode);
        Assert.Equal(503, get.StatusCode);
        Assert.Equal(503, cancel.StatusCode);
        Assert.IsType<ProblemDetails>(start.Value);
        Assert.IsType<ProblemDetails>(get.Value);
        Assert.IsType<ProblemDetails>(cancel.Value);
        Assert.Empty(GetInMemoryAutogenJobStatuses(service));
    }

    [Fact]
    public async Task Autogen_job_mysql_named_lock_serializes_instances_when_configured()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 13)))
            .Options;
        await using var firstDb = new AppDbContext(options);
        await using var secondDb = new AppDbContext(options);
        var firstService = CreateAutogenJobService(new RejectingScopeFactory());
        var secondService = CreateAutogenJobService(new RejectingScopeFactory());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var firstLock = await AcquireAutogenGlobalExecutionLockAsync(firstService, firstDb, timeout.Token);
        var firstLockReleased = false;

        try
        {
            var secondAcquire = AcquireAutogenGlobalExecutionLockAsync(secondService, secondDb, timeout.Token);
            await Task.Delay(250, timeout.Token);
            Assert.False(secondAcquire.IsCompleted);

            await firstLock.DisposeAsync();
            firstLockReleased = true;
            var secondLock = await secondAcquire.WaitAsync(TimeSpan.FromSeconds(10));
            await secondLock.DisposeAsync();
        }
        finally
        {
            if (!firstLockReleased)
            {
                await firstLock.DisposeAsync();
            }
        }
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

    private static async Task SeedAutogenAcademicPeriodAsync(
        AppDbContext db,
        int courseId,
        DateOnly academicPeriodStartDate)
    {
        var course = await db.Courses.SingleOrDefaultAsync(item => item.Id == courseId);
        if (course is null)
        {
            db.Courses.Add(new Course
            {
                Id = courseId,
                Name = $"Курс #{courseId}",
                DurationWeeks = 52,
                AcademicPeriodStartDate = academicPeriodStartDate
            });
        }
        else
        {
            course.AcademicPeriodStartDate = academicPeriodStartDate;
        }
        await db.SaveChangesAsync();
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

    private static TeacherDraftsAutogenJobService CreateAutogenJobServiceWithTiming(
        IServiceScopeFactory scopeFactory,
        ILogger<TeacherDraftsAutogenJobService> logger,
        IHostApplicationLifetime? applicationLifetime,
        TimeSpan leaseDuration,
        TimeSpan heartbeatInterval,
        TimeSpan cancellationGrace,
        TimeSpan terminalPersistenceHorizon,
        DateTime? databaseUtcNowOverride = null)
    {
        var constructor = typeof(TeacherDraftsAutogenJobService)
            .GetConstructors(ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic)
            .Single(item => item.GetParameters().Length == 8);
        Func<AppDbContext, DateTime>? databaseClock = databaseUtcNowOverride is DateTime databaseUtcNow
            ? _ => databaseUtcNow
            : null;
        return Assert.IsType<TeacherDraftsAutogenJobService>(constructor.Invoke(new object?[]
        {
            scopeFactory,
            logger,
            applicationLifetime,
            leaseDuration,
            heartbeatInterval,
            cancellationGrace,
            terminalPersistenceHorizon,
            databaseClock
        }));
    }

    private static TeacherDraftsController CreateTeacherDraftsController(TeacherDraftsAutogenJobService jobService)
        => new(
            db: null!,
            rules: null!,
            queryService: null!,
            exportService: null!,
            autogenService: null!,
            autogenJobService: jobService,
            publishService: null!);

    private static AutoGenJobRun CreatePersistedAutogenJob(
        string jobId,
        AutoGenJobRequest request,
        AutoGenJobState state,
        DateTime? leaseExpiresAtUtc)
    {
        var now = DateTime.UtcNow;
        return new AutoGenJobRun
        {
            JobId = jobId,
            OwnerInstanceId = "test-owner",
            Attempt = 1,
            LeaseExpiresAtUtc = leaseExpiresAtUtc,
            Version = 7,
            Kind = (int)request.Kind,
            State = (int)state,
            Title = request.Title ?? "Тестове завдання автогенерації",
            CurrentStage = state == AutoGenJobState.Running ? "Виконується" : "У черзі",
            CreatedAtUtc = now.AddMinutes(-2),
            StartedAtUtc = state == AutoGenJobState.Running ? now.AddMinutes(-1) : null,
            RangeStartDate = request.FromDate,
            RangeEndDate = request.ToDate,
            TotalWeeks = 1,
            Percent = state == AutoGenJobState.Running ? 35 : 0,
            RequestJson = System.Text.Json.JsonSerializer.Serialize(request),
            StatusJson = string.Empty,
            UpdatedAtUtc = now.AddSeconds(-10)
        };
    }

    private static async Task<SemaphoreSlim> HoldAutogenJobServiceGateAsync(
        TeacherDraftsAutogenJobService service,
        string fieldName)
    {
        var field = typeof(TeacherDraftsAutogenJobService).GetField(
            fieldName,
            ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic);
        var gate = Assert.IsType<SemaphoreSlim>(field?.GetValue(service));
        await gate.WaitAsync();
        return gate;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Умова тесту не виконалася у відведений час.");
            }
            await Task.Delay(25);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (!await condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Умова тесту не виконалася у відведений час.");
            }
            await Task.Delay(25);
        }
    }

    private static async Task<IAsyncDisposable> AcquireAutogenGlobalExecutionLockAsync(
        TeacherDraftsAutogenJobService service,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var method = typeof(TeacherDraftsAutogenJobService).GetMethod(
            "AcquireGlobalExecutionLockAsync",
            ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic);
        Assert.NotNull(method);
        var invocation = Assert.IsAssignableFrom<Task>(method.Invoke(service, new object[] { db, cancellationToken }));
        await invocation;
        var resultProperty = invocation.GetType().GetProperty("Result");
        return Assert.IsAssignableFrom<IAsyncDisposable>(resultProperty?.GetValue(invocation));
    }

    private static string InvokeBuildAutogenGlobalExecutionLockName(string databaseName)
    {
        var method = typeof(TeacherDraftsAutogenJobService).GetMethod(
            "BuildGlobalExecutionLockName",
            ReflectionBindingFlags.Static | ReflectionBindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<string>(method.Invoke(null, new object?[] { databaseName }));
    }

    private static IReadOnlyList<(
        int WeekIndex,
        DateOnly WeekStart,
        DateOnly RangeStartDate,
        DateOnly RangeEndDate)> InvokeBuildAutogenRunRanges(
        DateOnly fromDate,
        DateOnly toDate,
        IReadOnlyList<DateOnly> weekStarts)
    {
        var method = typeof(TeacherDraftsAutogenJobService).GetMethod(
            "BuildRunRanges",
            ReflectionBindingFlags.Static | ReflectionBindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = Assert.IsAssignableFrom<System.Collections.IEnumerable>(
            method.Invoke(null, new object[] { fromDate, toDate, weekStarts }));
        var ranges = new List<(int, DateOnly, DateOnly, DateOnly)>();

        foreach (var item in result)
        {
            Assert.NotNull(item);
            var itemType = item.GetType();
            ranges.Add((
                Assert.IsType<int>(itemType.GetProperty("WeekIndex")?.GetValue(item)),
                Assert.IsType<DateOnly>(itemType.GetProperty("WeekStart")?.GetValue(item)),
                Assert.IsType<DateOnly>(itemType.GetProperty("RangeStartDate")?.GetValue(item)),
                Assert.IsType<DateOnly>(itemType.GetProperty("RangeEndDate")?.GetValue(item))));
        }

        return ranges;
    }

    private static Task<ActionResult<AutoGenResult>> InvokeAmbientDraftAutoGenAsync(
        TeacherDraftsAutogenService service,
        DraftAutoGenRequest request,
        CancellationToken cancellationToken = default)
    {
        var method = typeof(TeacherDraftsAutogenService).GetMethod(
            "DraftAutoGenInAmbientTransaction",
            ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task<ActionResult<AutoGenResult>>>(
            method.Invoke(service, new object[] { request, cancellationToken }));
    }

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

    private static object GetAutogenJobRuntime(
        TeacherDraftsAutogenJobService service,
        string jobId)
    {
        var jobsField = typeof(TeacherDraftsAutogenJobService).GetField(
            "_jobs",
            ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic);
        var jobs = jobsField?.GetValue(service);
        Assert.NotNull(jobs);
        var tryGetValue = jobs.GetType().GetMethod(
            "TryGetValue",
            ReflectionBindingFlags.Instance | ReflectionBindingFlags.Public);
        Assert.NotNull(tryGetValue);
        var arguments = new object?[] { jobId, null };
        Assert.True(Assert.IsType<bool>(tryGetValue.Invoke(jobs, arguments)));
        return Assert.IsAssignableFrom<object>(arguments[1]);
    }

    private static void AttachAutogenJobRuntimeClaim(
        object runtime,
        string ownerInstanceId,
        int attempt,
        DateTime leaseExpiresAtUtc)
    {
        var method = runtime.GetType().GetMethod(
            "AttachDurableClaim",
            ReflectionBindingFlags.Instance | ReflectionBindingFlags.Public);
        Assert.NotNull(method);
        method.Invoke(runtime, new object[] { ownerInstanceId, attempt, leaseExpiresAtUtc });
    }

    private static void AttachAutogenJobRuntimePlan(object runtime, AutoGenJobRequest request)
    {
        var planType = typeof(TeacherDraftsAutogenPlanService).Assembly.GetType(
            "BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts.AutoGenDraftPlanPayload");
        var mutationType = typeof(TeacherDraftsAutogenPlanService).Assembly.GetType(
            "BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts.AutoGenDraftPlanMutationPayload");
        Assert.NotNull(planType);
        Assert.NotNull(mutationType);
        var mutations = Array.CreateInstance(mutationType, 0);
        var nowUtc = DateTime.UtcNow;
        var plan = Activator.CreateInstance(
            planType,
            ReflectionBindingFlags.Instance | ReflectionBindingFlags.Public | ReflectionBindingFlags.NonPublic,
            binder: null,
            args: new object[]
            {
                request.ClientJobId!,
                request.CourseId,
                request.FromDate,
                request.ToDate,
                request.Days,
                request.AllowIncompleteDrafts,
                request.GroupIds,
                Guid.NewGuid(),
                "test-input-fingerprint",
                nowUtc,
                nowUtc.AddHours(24),
                mutations
            },
            culture: null);
        Assert.NotNull(plan);
        var method = runtime.GetType().GetMethod(
            "AttachPlan",
            ReflectionBindingFlags.Instance | ReflectionBindingFlags.Public);
        Assert.NotNull(method);
        method.Invoke(runtime, new[] { plan });
    }

    private static object? GetAutogenJobRuntimePlanPayload(object runtime)
    {
        var property = runtime.GetType().GetProperty(
            "PlanPayload",
            ReflectionBindingFlags.Instance | ReflectionBindingFlags.Public);
        Assert.NotNull(property);
        return property.GetValue(runtime);
    }

    private static void MarkAutogenJobRuntimeTerminal(
        object runtime,
        AutoGenJobRequest request,
        AutoGenJobState terminalState)
    {
        var result = new AutoGenResult(0, 0, new List<string>());
        var report = CreateEmptyAutoGenRunReport(request);
        if (terminalState == AutoGenJobState.Canceled)
        {
            RequestAutogenJobRuntimeCancellation(runtime);
            var canceled = runtime.GetType().GetMethod(
                "MarkCanceled",
                ReflectionBindingFlags.Instance | ReflectionBindingFlags.Public);
            Assert.NotNull(canceled);
            canceled.Invoke(runtime, new object[] { result, report });
            return;
        }

        Assert.Equal(AutoGenJobState.Failed, terminalState);
        var failed = runtime.GetType().GetMethod(
            "MarkFailed",
            ReflectionBindingFlags.Instance | ReflectionBindingFlags.Public);
        Assert.NotNull(failed);
        failed.Invoke(runtime, new object[] { "Тестова помилка після формування плану.", result, report });
    }

    private static void MarkAutogenJobRuntimeSucceeded(object runtime, AutoGenJobRequest request)
    {
        var result = new AutoGenResult(0, 0, new List<string>());
        var report = new AutoGenRunReport(
            DateTimeOffset.UtcNow,
            request.FromDate,
            request.ToDate,
            1,
            0,
            0,
            0,
            0,
            0,
            new List<AutoGenGapSummaryItem>(),
            new List<AutoGenPreflightItem>(),
            new List<AutoGenRunReportGroupItem>(),
            new List<AutoGenRunReportModuleItem>(),
            new List<string>());
        var method = runtime.GetType().GetMethod(
            "MarkSucceeded",
            ReflectionBindingFlags.Instance | ReflectionBindingFlags.Public);
        Assert.NotNull(method);
        method.Invoke(runtime, new object[] { result, report });
    }

    private static async Task<bool> InvokeTryPersistOwnedSnapshotAsync(
        TeacherDraftsAutogenJobService service,
        object runtime)
    {
        var method = typeof(TeacherDraftsAutogenJobService).GetMethod(
            "TryPersistOwnedSnapshotAsync",
            ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic);
        Assert.NotNull(method);
        var invocation = Assert.IsAssignableFrom<Task>(method.Invoke(service, new[] { runtime, "test" }));
        await invocation;
        var resultProperty = invocation.GetType().GetProperty("Result");
        return Assert.IsType<bool>(resultProperty?.GetValue(invocation));
    }

    private static Task InvokeMaintainLeaseAsync(
        TeacherDraftsAutogenJobService service,
        object runtime,
        CancellationToken cancellationToken)
    {
        var method = typeof(TeacherDraftsAutogenJobService).GetMethod(
            "MaintainLeaseAsync",
            ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task>(method.Invoke(service, new object[] { runtime, cancellationToken }));
    }

    private static Task InvokeRenewAutogenJobLeaseAsync(
        TeacherDraftsAutogenJobService service,
        object runtime)
    {
        var method = typeof(TeacherDraftsAutogenJobService).GetMethod(
            "RenewLeaseAndReadCancellationAsync",
            ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task>(method.Invoke(
            service,
            new object[] { runtime, CancellationToken.None }));
    }

    private static void AddSqliteExclusiveExecutionMarker(
        TeacherDraftsAutogenJobService service,
        string jobId)
    {
        var field = typeof(TeacherDraftsAutogenJobService).GetField(
            "_sqliteExclusiveExecutions",
            ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic);
        var executions = Assert.IsType<System.Collections.Concurrent.ConcurrentDictionary<string, byte>>(
            field?.GetValue(service));
        executions[jobId] = 0;
    }

    private static void AddAutogenJobRuntime(
        TeacherDraftsAutogenJobService service,
        string jobId,
        object runtime)
    {
        var field = typeof(TeacherDraftsAutogenJobService).GetField(
            "_jobs",
            ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic);
        var jobs = field?.GetValue(service);
        Assert.NotNull(jobs);
        var tryAdd = jobs.GetType().GetMethods(ReflectionBindingFlags.Instance | ReflectionBindingFlags.Public)
            .Single(method => method.Name == "TryAdd" && method.GetParameters().Length == 2);
        Assert.True(Assert.IsType<bool>(tryAdd.Invoke(jobs, new[] { jobId, runtime })));
    }

    private static async Task<bool> InvokeEnterSqliteExclusiveExecutionLeaseAsync(
        TeacherDraftsAutogenJobService service,
        AppDbContext db,
        object runtime)
    {
        var method = typeof(TeacherDraftsAutogenJobService).GetMethod(
            "EnterSqliteExclusiveExecutionLeaseAsync",
            ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic);
        Assert.NotNull(method);
        var invocation = Assert.IsAssignableFrom<Task>(method.Invoke(
            service,
            new object[] { db, runtime, CancellationToken.None }));
        await invocation;
        var resultProperty = invocation.GetType().GetProperty("Result");
        return Assert.IsType<bool>(resultProperty?.GetValue(invocation));
    }

    private static Task InvokeCommitExecutionTransactionAsync(IDbContextTransaction transaction)
    {
        var method = typeof(TeacherDraftsAutogenJobService).GetMethod(
            "CommitExecutionTransactionAsync",
            ReflectionBindingFlags.Static | ReflectionBindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task>(method.Invoke(null, new object[] { transaction }));
    }

    private static Task InvokeEnsureAutogenCommitFenceAsync(
        TeacherDraftsAutogenJobService service,
        AppDbContext db,
        object runtime)
    {
        var method = typeof(TeacherDraftsAutogenJobService).GetMethod(
            "EnsureCommitFenceAsync",
            ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task>(method.Invoke(
            service,
            new object[] { db, runtime, CancellationToken.None }));
    }

    private static Task InvokePersistTerminalSnapshotAsync(
        TeacherDraftsAutogenJobService service,
        object runtime,
        AutoGenJobRequest request)
    {
        var result = new AutoGenResult(0, 0, new List<string>());
        var report = CreateEmptyAutoGenRunReport(request);
        var method = typeof(TeacherDraftsAutogenJobService).GetMethod(
            "PersistTerminalSnapshotWithRetryAsync",
            ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task>(method.Invoke(
            service,
            new object[] { runtime, "test", result, report }));
    }

    private static AutoGenRunReport CreateEmptyAutoGenRunReport(AutoGenJobRequest request)
        => new(
            DateTimeOffset.UtcNow,
            request.FromDate,
            request.ToDate,
            1,
            0,
            0,
            0,
            0,
            0,
            new List<AutoGenGapSummaryItem>(),
            new List<AutoGenPreflightItem>(),
            new List<AutoGenRunReportGroupItem>(),
            new List<AutoGenRunReportModuleItem>(),
            new List<string>());

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

    private sealed class DelayedRecoveryScopeFactory(
        IServiceScopeFactory inner,
        TimeSpan outageDuration) : IServiceScopeFactory
    {
        private readonly System.Diagnostics.Stopwatch _timer = System.Diagnostics.Stopwatch.StartNew();

        public IServiceScope CreateScope()
            => _timer.Elapsed < outageDuration
                ? throw new InvalidOperationException("Тестове сховище стану тимчасово недоступне.")
                : inner.CreateScope();
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;
        public bool StopRequested => _stopping.IsCancellationRequested;

        public void StopApplication()
            => _stopping.Cancel();
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        private readonly List<TestLogEntry> _entries = new();
        private readonly object _sync = new();

        public IReadOnlyList<TestLogEntry> Entries
        {
            get
            {
                lock (_sync)
                {
                    return _entries.ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel)
            => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_sync)
            {
                _entries.Add(new TestLogEntry(logLevel, formatter(state, exception), exception));
            }
        }
    }

    private sealed record TestLogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class RecordingDbContextTransaction(Action onCommit) : IDbContextTransaction
    {
        public Guid TransactionId { get; } = Guid.NewGuid();
        public bool SupportsSavepoints => false;
        public bool CommitCalled { get; private set; }
        public CancellationToken CommitCancellationToken { get; private set; }

        public void Commit()
        {
            CommitCalled = true;
            onCommit();
        }

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            CommitCalled = true;
            CommitCancellationToken = cancellationToken;
            onCommit();
            return Task.CompletedTask;
        }

        public void Rollback()
        {
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void CreateSavepoint(string name)
            => throw new NotSupportedException();

        public Task CreateSavepointAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromException(new NotSupportedException());

        public void RollbackToSavepoint(string name)
            => throw new NotSupportedException();

        public Task RollbackToSavepointAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromException(new NotSupportedException());

        public void ReleaseSavepoint(string name)
            => throw new NotSupportedException();

        public Task ReleaseSavepointAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromException(new NotSupportedException());

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;
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

    private static async Task<AtomicAutogenScenario> SeedAtomicAutogenScenarioAsync(AppDbContext db)
    {
        var course = new Course { Name = "Курс атомарної автогенерації", DurationWeeks = 52 };
        var group = new Group { Name = "AT-1", StudentsCount = 20, Course = course };
        var module = new Module { Code = "ATM", Title = "Атомарний модуль", Credits = 1, Course = course };
        var lessonType = new LessonTypeRef
        {
            Code = "ATOMIC_SELF",
            Name = "Самостійна робота для атомарного тесту",
            IsActive = true,
            RequiresRoom = false,
            RequiresTeacher = false,
            BlocksRoom = false,
            BlocksTeacher = false,
            CountInPlan = true,
            CountInLoad = false
        };
        var date = new DateOnly(2026, 5, 4);
        var teacher = new Teacher { FullName = "Викладач атомарного тесту" };
        var building = new Building { Name = "Корпус атомарного тесту" };
        var room = new Room
        {
            Name = "АТ-101",
            Capacity = 30,
            Building = building
        };
        db.AddRange(course, group, module, lessonType, teacher, room);
        await db.SaveChangesAsync();
        db.TeacherModules.Add(new TeacherModule
        {
            TeacherId = teacher.Id,
            ModuleId = module.Id
        });
        db.TimeSlots.Add(new TimeSlot
        {
            CourseId = course.Id,
            DayOfWeek = date.DayOfWeek,
            Start = new TimeOnly(8, 0),
            End = new TimeOnly(9, 0),
            SortOrder = 1,
            IsActive = true
        });
        await db.SaveChangesAsync();

        var ids = new SeedIds(course.Id, group.Id, module.Id, lessonType.Id);
        return new AtomicAutogenScenario(
            ids,
            date,
            new DraftAutoGenRequest(
                WeekStart: date,
                ClearExisting: true,
                CourseId: course.Id,
                GroupIds: new List<int> { group.Id },
                Days: WeekPreset.MonFri,
                ModuleHours: new Dictionary<int, int> { [module.Id] = 1 },
                AllowIncompleteDrafts: true,
                RangeStartDate: date,
                RangeEndDate: date));
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

    private static async Task<SameBuildingManualRulesSeed> SeedSameBuildingManualRulesModelAsync(
        TestDatabase fixture)
    {
        var ids = await fixture.SeedMinimalScheduleModelAsync();
        var monday = new DateOnly(2026, 5, 4);
        var tuesday = monday.AddDays(1);
        var lessonType = new LessonTypeRef
        {
            Code = "MANUAL_ROOM",
            Name = "Аудиторне заняття ручної перевірки",
            IsActive = true,
            RequiresRoom = true,
            RequiresTeacher = false,
            BlocksRoom = true,
            BlocksTeacher = true,
            CountInPlan = true,
            CountInLoad = true
        };
        var building = new Building { Name = "Спільний корпус ручної перевірки" };
        var firstRoom = new Room { Name = "РП-101", Capacity = 30, Building = building };
        var secondRoom = new Room { Name = "РП-102", Capacity = 30, Building = building };
        var teacher = new Teacher { FullName = "Викладач ручної перевірки" };
        fixture.Db.AddRange(lessonType, firstRoom, secondRoom, teacher);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.TeacherModules.Add(new TeacherModule
        {
            TeacherId = teacher.Id,
            ModuleId = ids.ModuleId
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
                Start = new TimeOnly(9, 5),
                End = new TimeOnly(10, 5),
                SortOrder = 2,
                IsActive = true
            },
            new TimeSlot
            {
                CourseId = ids.CourseId,
                DayOfWeek = DayOfWeek.Tuesday,
                Start = new TimeOnly(8, 0),
                End = new TimeOnly(9, 0),
                SortOrder = 1,
                IsActive = true
            });
        await fixture.Db.SaveChangesAsync();

        return new SameBuildingManualRulesSeed(
            ids,
            lessonType.Id,
            firstRoom.Id,
            secondRoom.Id,
            teacher.Id,
            monday,
            tuesday);
    }

    private sealed record SeedIds(int CourseId, int GroupId, int ModuleId, int LessonTypeId);

    private sealed record NonBlockingTravelSeed(
        SeedIds Ids,
        int LessonTypeId,
        int FirstRoomId,
        int SecondRoomId,
        int TeacherId,
        DateOnly Date);

    private sealed record SameBuildingManualRulesSeed(
        SeedIds Ids,
        int LessonTypeId,
        int FirstRoomId,
        int SecondRoomId,
        int TeacherId,
        DateOnly Monday,
        DateOnly Tuesday);

    private sealed record AtomicAutogenScenario(
        SeedIds Ids,
        DateOnly Date,
        DraftAutoGenRequest Request);

    private sealed class SharedAutogenJobDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _keeperConnection;

        private SharedAutogenJobDatabase(
            SqliteConnection keeperConnection,
            DbContextOptions<AppDbContext> options)
        {
            _keeperConnection = keeperConnection;
            Options = options;
        }

        public DbContextOptions<AppDbContext> Options { get; }

        public static async Task<SharedAutogenJobDatabase> CreateAsync()
        {
            var databaseName = $"autogen-jobs-{Guid.NewGuid():N}";
            var connectionString = $"Data Source={databaseName};Mode=Memory;Cache=Shared;Default Timeout=5";
            var keeperConnection = new SqliteConnection(connectionString);
            await keeperConnection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connectionString)
                .Options;
            await using var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();
            db.Courses.Add(new Course
            {
                Id = 1,
                Name = "Курс фонової автогенерації",
                DurationWeeks = 52,
                AcademicPeriodStartDate = new DateOnly(2026, 1, 1)
            });
            await db.SaveChangesAsync();
            return new SharedAutogenJobDatabase(keeperConnection, options);
        }

        public AppDbContext CreateContext()
            => new(Options);

        public ServiceProvider CreateProvider()
        {
            var services = new ServiceCollection();
            services.AddScoped(_ => new AppDbContext(Options));
            services.AddScoped<TeacherDraftsAutogenService>();
            return services.BuildServiceProvider();
        }

        public async ValueTask DisposeAsync()
            => await _keeperConnection.DisposeAsync();
    }

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
