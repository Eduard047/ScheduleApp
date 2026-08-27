using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Admin;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TimeSlotEditor;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class AdminControllerGuardrailTests
{
    [Fact]
    public async Task Module_sequence_save_rejects_oversized_collections_before_database_access()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        using var operationGate = new ExpensiveOperationGate();
        var request = new ModuleSequenceSaveRequestDto(
            999,
            Enumerable.Range(1, CurriculumInputLimits.ModuleAssociationCountMax + 1)
                .Select(id => new ModuleSequenceSaveItemDto(id, 1))
                .ToList(),
            new List<int>());

        var result = await new AdminModuleSequenceController(fixture.Db).Save(
            request,
            operationGate,
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains(
            CurriculumInputLimits.ModuleAssociationCountMax.ToString(),
            JsonSerializer.Serialize(badRequest.Value),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Module_sequence_save_rejects_oversized_fillers_before_database_access()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        using var operationGate = new ExpensiveOperationGate();
        var request = new ModuleSequenceSaveRequestDto(
            999,
            new List<ModuleSequenceSaveItemDto>(),
            Enumerable.Range(1, CurriculumInputLimits.ModuleAssociationCountMax + 1).ToList());

        var result = await new AdminModuleSequenceController(fixture.Db).Save(
            request,
            operationGate,
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains(
            CurriculumInputLimits.ModuleAssociationCountMax.ToString(),
            JsonSerializer.Serialize(badRequest.Value),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Module_sequence_save_preserves_valid_deduplicated_configuration()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);
        fixture.Db.ModuleCourses.Add(new ModuleCourse
        {
            CourseId = model.Course.Id,
            ModuleId = model.Module.Id
        });
        await fixture.Db.SaveChangesAsync();
        using var operationGate = new ExpensiveOperationGate();
        var request = new ModuleSequenceSaveRequestDto(
            model.Course.Id,
            new List<ModuleSequenceSaveItemDto>
            {
                new(model.Module.Id, 2),
                new(model.Module.Id, 7)
            },
            new List<int> { model.Module.Id, model.Module.Id });

        var result = await new AdminModuleSequenceController(fixture.Db).Save(
            request,
            operationGate,
            CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        var main = Assert.Single(await fixture.Db.ModuleSequenceItems.AsNoTracking().ToListAsync());
        Assert.Equal(2, main.GroupOrder);
        Assert.Single(await fixture.Db.ModuleFillers.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Module_sequence_save_revalidates_course_membership_inside_serializable_transaction()
    {
        var interceptor = new ModuleCourseMembershipTransactionInterceptor();
        await using var fixture = await TestDatabase.CreateAsync(interceptor: interceptor);
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);
        fixture.Db.ModuleCourses.Add(new ModuleCourse
        {
            CourseId = model.Course.Id,
            ModuleId = model.Module.Id
        });
        await fixture.Db.SaveChangesAsync();
        using var operationGate = new ExpensiveOperationGate();

        var result = await new AdminModuleSequenceController(fixture.Db).Save(
            new ModuleSequenceSaveRequestDto(
                model.Course.Id,
                [new ModuleSequenceSaveItemDto(model.Module.Id, 1)],
                []),
            operationGate,
            CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(IsolationLevel.Serializable, interceptor.ObservedIsolationLevel);
    }
    [Fact]
    public async Task Department_upsert_rejects_name_longer_than_database_limit()
    {
        await using var fixture = await TestDatabase.CreateAsync();

        var result = await new AdminDepartmentsController(fixture.Db).Upsert(
            new DepartmentEditDto(null, new string('К', 257), true));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(await fixture.Db.Departments.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Topic_upsert_rejects_code_longer_than_database_limit()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);

        var result = await new AdminModulesController(fixture.Db).UpsertTopic(
            model.Module.Id,
            new ModuleTopicDto(
                null,
                model.Module.Id,
                1,
                new string('Т', 65),
                model.LessonType.Id,
                null,
                1,
                1,
                0,
                false,
                false));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(await fixture.Db.ModuleTopics.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(300000)]
    public async Task Module_plan_upsert_rejects_hours_outside_supported_range(int targetHours)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);
        var originalCredits = model.Module.Credits;

        var result = await new AdminPlansController(fixture.Db).Upsert(
            model.Module.Id,
            new List<SaveCourseModulePlanDto> { new(targetHours, true) },
            model.Course.Id);

        Assert.IsType<BadRequestObjectResult>(result);
        fixture.Db.ChangeTracker.Clear();
        Assert.Empty(await fixture.Db.ModulePlans.AsNoTracking().ToListAsync());
        Assert.Equal(originalCredits, await fixture.Db.Modules
            .Where(module => module.Id == model.Module.Id)
            .Select(module => module.Credits)
            .SingleAsync());
    }

    [Fact]
    public async Task Module_upsert_rejects_credit_that_would_exceed_supported_plan_hours()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);

        var result = await new AdminModulesController(fixture.Db).Upsert(new ModuleEditDto(
            null,
            "МЕЖА-1",
            "Модуль понад межею кредитів",
            model.Course.Id,
            CurriculumInputLimits.ModuleCreditsMax + 0.01m));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(1, await fixture.Db.Modules.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Module_upsert_rejects_credit_with_more_than_database_scale()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);

        var result = await new AdminModulesController(fixture.Db).Upsert(new ModuleEditDto(
            null,
            "МЕЖА-МАСШТАБ",
            "Модуль із надмірною точністю кредитів",
            model.Course.Id,
            1.015m));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("2", badRequest.Value?.ToString(), StringComparison.Ordinal);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(1, await fixture.Db.Modules.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Module_upsert_rejects_title_longer_than_shared_limit()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);

        var result = await new AdminModulesController(fixture.Db).Upsert(new ModuleEditDto(
            null,
            "МЕЖА-2",
            new string('М', CurriculumInputLimits.ModuleTitleMaxLength + 1),
            model.Course.Id,
            1));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(1, await fixture.Db.Modules.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Module_upsert_rejects_association_collection_above_shared_limit()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);
        var dto = new ModuleEditDto(
            null,
            "МЕЖА-3",
            "Модуль із надмірною кількістю зв'язків",
            model.Course.Id,
            1)
        {
            AllowedRoomIds = Enumerable.Range(1, CurriculumInputLimits.ModuleAssociationCountMax + 1).ToList()
        };

        var result = await new AdminModulesController(fixture.Db).Upsert(dto);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(1, await fixture.Db.Modules.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Lesson_type_upsert_rejects_name_longer_than_shared_limit()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var dto = new LessonTypeEditDto
        {
            Code = "МЕЖА",
            Name = new string('Т', CurriculumInputLimits.LessonTypeNameMaxLength + 1)
        };

        var result = await new AdminTypesController(fixture.Db).LessonUpsert(dto);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(await fixture.Db.LessonTypes.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Group_course_move_preserves_group_calendar_exception_scope()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var firstCourse = new Course { Name = "Перший курс групи", DurationWeeks = 52 };
        var secondCourse = new Course { Name = "Другий курс групи", DurationWeeks = 52 };
        var group = new Group { Name = "Перехідна група", StudentsCount = 20, Course = firstCourse };
        fixture.Db.AddRange(firstCourse, secondCourse, group);
        await fixture.Db.SaveChangesAsync();
        var exception = new CalendarException
        {
            Date = new DateOnly(2026, 9, 7),
            Name = "Вихідний групи",
            IsWorkingDay = false,
            CourseId = firstCourse.Id,
            GroupId = group.Id
        };
        fixture.Db.CalendarExceptions.Add(exception);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminGroupsController(fixture.Db).Upsert(new GroupEditDto(
            group.Id,
            group.Name,
            group.StudentsCount,
            secondCourse.Id));

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(secondCourse.Id, await fixture.Db.CalendarExceptions
            .Where(item => item.Id == exception.Id)
            .Select(item => item.CourseId)
            .SingleAsync());
    }

    [Fact]
    public async Task Group_upsert_checks_required_nonblocking_room_capacity()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(firstGroupStudents: 20, secondGroupStudents: 10, roomCapacity: 20);
        model.LessonType.BlocksRoom = false;
        fixture.Db.ScheduleItems.Add(CreateScheduleItem(model, model.FirstGroup.Id, new DateOnly(2026, 9, 7)));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminGroupsController(fixture.Db).Upsert(new GroupEditDto(
            model.FirstGroup.Id,
            model.FirstGroup.Name,
            21,
            model.Course.Id));

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(20, await fixture.Db.Groups
            .Where(group => group.Id == model.FirstGroup.Id)
            .Select(group => group.StudentsCount)
            .SingleAsync());
    }

    [Fact]
    public async Task Room_upsert_checks_required_nonblocking_draft_capacity()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(firstGroupStudents: 30, secondGroupStudents: 10, roomCapacity: 30);
        model.LessonType.BlocksRoom = false;
        fixture.Db.TeacherDraftItems.Add(CreateDraftItem(model, model.FirstGroup.Id, new DateOnly(2026, 9, 7)));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminRoomsController(fixture.Db).Upsert(new RoomEditDto(
            model.Room.Id,
            model.Room.Name,
            29,
            model.Building.Id));

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(30, await fixture.Db.Rooms
            .Where(room => room.Id == model.Room.Id)
            .Select(room => room.Capacity)
            .SingleAsync());
    }

    [Fact]
    public async Task Group_upsert_rejects_published_shared_slot_capacity_overflow()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(firstGroupStudents: 20, secondGroupStudents: 30, roomCapacity: 50);
        var date = new DateOnly(2026, 9, 7);
        fixture.Db.ScheduleItems.AddRange(
            CreateScheduleItem(model, model.FirstGroup.Id, date),
            CreateScheduleItem(model, model.SecondGroup.Id, date));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminGroupsController(fixture.Db).Upsert(new GroupEditDto(
            model.FirstGroup.Id,
            model.FirstGroup.Name,
            21,
            model.Course.Id));

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(20, await fixture.Db.Groups
            .Where(group => group.Id == model.FirstGroup.Id)
            .Select(group => group.StudentsCount)
            .SingleAsync());
    }

    [Fact]
    public async Task Room_upsert_rejects_draft_shared_slot_capacity_overflow()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(firstGroupStudents: 30, secondGroupStudents: 25, roomCapacity: 60);
        var date = new DateOnly(2026, 9, 7);
        fixture.Db.TeacherDraftItems.AddRange(
            CreateDraftItem(model, model.FirstGroup.Id, date),
            CreateDraftItem(model, model.SecondGroup.Id, date));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminRoomsController(fixture.Db).Upsert(new RoomEditDto(
            model.Room.Id,
            model.Room.Name,
            50,
            model.Building.Id));

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(60, await fixture.Db.Rooms
            .Where(room => room.Id == model.Room.Id)
            .Select(room => room.Capacity)
            .SingleAsync());
    }

    [Fact]
    public async Task Room_upsert_checks_published_and_drafts_as_separate_capacity_sets()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(firstGroupStudents: 30, secondGroupStudents: 30, roomCapacity: 60);
        var date = new DateOnly(2026, 9, 7);
        fixture.Db.ScheduleItems.Add(CreateScheduleItem(model, model.FirstGroup.Id, date));
        fixture.Db.TeacherDraftItems.Add(CreateDraftItem(model, model.SecondGroup.Id, date));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminRoomsController(fixture.Db).Upsert(new RoomEditDto(
            model.Room.Id,
            model.Room.Name,
            40,
            model.Building.Id));

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(40, await fixture.Db.Rooms
            .Where(room => room.Id == model.Room.Id)
            .Select(room => room.Capacity)
            .SingleAsync());
    }

    [Fact]
    public async Task Room_force_delete_rejects_placements_that_require_a_room()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(firstGroupStudents: 20, secondGroupStudents: 20, roomCapacity: 40);
        var date = new DateOnly(2026, 9, 7);
        var schedule = CreateScheduleItem(model, model.FirstGroup.Id, date);
        var draft = CreateDraftItem(model, model.SecondGroup.Id, date);
        fixture.Db.AddRange(schedule, draft);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminRoomsController(fixture.Db).Delete(model.Room.Id, force: true);

        Assert.IsType<ConflictObjectResult>(result);
        fixture.Db.ChangeTracker.Clear();
        Assert.True(await fixture.Db.Rooms.AnyAsync(room => room.Id == model.Room.Id));
        Assert.True(await fixture.Db.ScheduleItems.AnyAsync(item => item.RoomId == model.Room.Id));
        Assert.True(await fixture.Db.TeacherDraftItems.AnyAsync(item => item.RoomId == model.Room.Id));
    }

    [Fact]
    public async Task Teacher_upsert_rejects_working_hours_that_exclude_existing_placement()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedTeacherModelAsync();
        var date = new DateOnly(2026, 9, 7);
        fixture.Db.ScheduleItems.Add(new ScheduleItem
        {
            Date = date,
            DayOfWeek = date.DayOfWeek,
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(10, 0),
            GroupId = model.Group.Id,
            ModuleId = model.Module.Id,
            LessonTypeId = model.LessonType.Id,
            TeacherId = model.Teacher.Id
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var dto = new TeacherEditDto(
            model.Teacher.Id,
            model.Teacher.FullName,
            null,
            null,
            null,
            new List<int>(),
            new List<int>(),
            new List<TeacherLoadDto>(),
            new List<TeacherWorkingHourDto>
            {
                new((int)DayOfWeek.Monday, "10:00", "11:00")
            });

        var result = await new AdminTeachersController(fixture.Db).Upsert(dto);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.False(await fixture.Db.TeacherWorkingHours.AnyAsync(row => row.TeacherId == model.Teacher.Id));
    }

    [Fact]
    public async Task Teacher_force_delete_rejects_required_teacher_placement()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedTeacherModelAsync();
        var date = new DateOnly(2026, 9, 7);
        fixture.Db.TeacherDraftItems.Add(new TeacherDraftItem
        {
            Date = date,
            DayOfWeek = date.DayOfWeek,
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(10, 0),
            GroupId = model.Group.Id,
            ModuleId = model.Module.Id,
            LessonTypeId = model.LessonType.Id,
            TeacherId = model.Teacher.Id
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminTeachersController(fixture.Db).Delete(model.Teacher.Id, force: true);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.True(await fixture.Db.Teachers.AnyAsync(teacher => teacher.Id == model.Teacher.Id));
        Assert.True(await fixture.Db.TeacherDraftItems.AnyAsync(item =>
            item.TeacherId == model.Teacher.Id));
    }

    [Fact]
    public async Task Ensure_course_scope_copies_teacher_and_supervisor_links()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var firstCourse = new Course { Name = "Перший курс", DurationWeeks = 52 };
        var secondCourse = new Course { Name = "Другий курс", DurationWeeks = 52 };
        var teacher = new Teacher { FullName = "Викладач модуля" };
        var module = new Module
        {
            Code = "СП-1",
            Title = "Спільний модуль",
            Credits = 1,
            Course = firstCourse
        };
        fixture.Db.AddRange(firstCourse, secondCourse, teacher, module);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ModuleCourses.AddRange(
            new ModuleCourse { ModuleId = module.Id, CourseId = firstCourse.Id },
            new ModuleCourse { ModuleId = module.Id, CourseId = secondCourse.Id });
        fixture.Db.TeacherModules.Add(new TeacherModule { TeacherId = teacher.Id, ModuleId = module.Id });
        fixture.Db.ModuleSupervisors.Add(new ModuleSupervisor { TeacherId = teacher.Id, ModuleId = module.Id });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminModulesController(fixture.Db)
            .EnsureCourseScope(module.Id, secondCourse.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var clonedModuleId = Assert.IsType<int>(ok.Value);
        Assert.NotEqual(module.Id, clonedModuleId);
        Assert.True(await fixture.Db.TeacherModules.AnyAsync(link =>
            link.ModuleId == clonedModuleId && link.TeacherId == teacher.Id));
        Assert.True(await fixture.Db.ModuleSupervisors.AnyAsync(link =>
            link.ModuleId == clonedModuleId && link.TeacherId == teacher.Id));
    }

    [Fact]
    public async Task Course_force_delete_rehomes_primary_module_with_only_alternative_link()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var firstCourse = new Course { Name = "Курс для видалення", DurationWeeks = 52 };
        var secondCourse = new Course { Name = "Курс-приймач", DurationWeeks = 52 };
        var group = new Group { Name = "Група видалення", StudentsCount = 20, Course = firstCourse };
        var module = new Module
        {
            Code = "СП-2",
            Title = "Модуль без первинного зв'язку",
            Credits = 1,
            Course = firstCourse
        };
        fixture.Db.AddRange(firstCourse, secondCourse, group, module);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ModuleCourses.Add(new ModuleCourse
        {
            ModuleId = module.Id,
            CourseId = secondCourse.Id
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminCoursesController(fixture.Db).Delete(firstCourse.Id, force: true);

        Assert.IsType<NoContentResult>(result);
        Assert.False(await fixture.Db.Courses.AnyAsync(course => course.Id == firstCourse.Id));
        Assert.Equal(secondCourse.Id, await fixture.Db.Modules
            .Where(item => item.Id == module.Id)
            .Select(item => item.CourseId)
            .SingleAsync());
    }

    [Fact]
    public async Task Calendar_upsert_rejects_duplicate_global_scope_for_same_date()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        fixture.Db.CalendarExceptions.Add(new CalendarException
        {
            Date = new DateOnly(2026, 9, 7),
            IsWorkingDay = false,
            Name = "Свято"
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminConfigController(fixture.Db).CalendarUpsert(
            new CalendarExceptionEditDto(null, "2026-09-07", true, "Робочий день"));

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(1, await fixture.Db.CalendarExceptions.CountAsync());
    }

    [Theory]
    [InlineData("0001-01-01")]
    [InlineData("9999-12-31")]
    public async Task Calendar_upsert_rejects_dates_outside_mysql_range(string date)
    {
        await using var fixture = await TestDatabase.CreateAsync();

        var result = await new AdminConfigController(fixture.Db).CalendarUpsert(
            new CalendarExceptionEditDto(null, date, true, "Некоректна дата"));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(await fixture.Db.CalendarExceptions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Lesson_type_upsert_rejects_case_insensitive_duplicate_code()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);
        fixture.Db.ChangeTracker.Clear();
        var dto = new LessonTypeEditDto
        {
            Code = model.LessonType.Code.ToLowerInvariant(),
            Name = "Дублікат",
            IsActive = true
        };

        var result = await new AdminTypesController(fixture.Db).LessonUpsert(dto);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(1, await fixture.Db.LessonTypes.CountAsync());
    }

    [Fact]
    public async Task Lesson_type_upsert_rejects_deactivation_while_topic_uses_type()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);
        fixture.Db.ModuleTopics.Add(new ModuleTopic
        {
            ModuleId = model.Module.Id,
            LessonTypeId = model.LessonType.Id,
            TopicCode = "ACTIVE-1",
            Order = 1,
            TotalHours = 1,
            AuditoriumHours = 1
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var dto = new LessonTypeEditDto
        {
            Id = model.LessonType.Id,
            Code = model.LessonType.Code,
            Name = model.LessonType.Name,
            IsActive = false,
            CssKey = model.LessonType.CssKey,
            RequiresRoom = model.LessonType.RequiresRoom,
            RequiresTeacher = model.LessonType.RequiresTeacher,
            BlocksRoom = model.LessonType.BlocksRoom,
            BlocksTeacher = model.LessonType.BlocksTeacher,
            CountInPlan = model.LessonType.CountInPlan,
            CountInLoad = model.LessonType.CountInLoad,
            PreferredFirstInWeek = model.LessonType.PreferredFirstInWeek
        };

        var result = await new AdminTypesController(fixture.Db).LessonUpsert(dto);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.True(await fixture.Db.LessonTypes
            .Where(type => type.Id == model.LessonType.Id)
            .Select(type => type.IsActive)
            .SingleAsync());
    }

    [Fact]
    public async Task Lesson_type_merge_reassigns_references_and_saved_autogen_snapshots()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);
        var source = new LessonTypeRef
        {
            Code = "ROOM_ROOM",
            Name = "Аудиторне Аудиторне заняття",
            IsActive = true,
            RequiresRoom = model.LessonType.RequiresRoom,
            RequiresTeacher = model.LessonType.RequiresTeacher,
            BlocksRoom = model.LessonType.BlocksRoom,
            BlocksTeacher = model.LessonType.BlocksTeacher,
            CountInPlan = model.LessonType.CountInPlan,
            CountInLoad = model.LessonType.CountInLoad,
            PreferredFirstInWeek = model.LessonType.PreferredFirstInWeek
        };
        var topic = new ModuleTopic
        {
            ModuleId = model.Module.Id,
            LessonType = source,
            TopicCode = "MERGE-1",
            Order = 1,
            TotalHours = 1,
            AuditoriumHours = 1
        };
        fixture.Db.AddRange(source, topic);
        await fixture.Db.SaveChangesAsync();

        var date = new DateOnly(2026, 9, 7);
        var draft = new TeacherDraftItem
        {
            Date = date,
            DayOfWeek = date.DayOfWeek,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(8, 45),
            LessonTypeId = source.Id,
            GroupId = model.FirstGroup.Id,
            ModuleId = model.Module.Id,
            ModuleTopicId = topic.Id,
            RoomId = model.Room.Id,
            BatchKey = $"rescheduled:123:{source.Id}"
        };
        var scheduleItem = new ScheduleItem
        {
            Date = date,
            DayOfWeek = date.DayOfWeek,
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(9, 45),
            LessonTypeId = source.Id,
            GroupId = model.SecondGroup.Id,
            ModuleId = model.Module.Id,
            ModuleTopicId = topic.Id,
            RoomId = model.Room.Id,
            BatchKey = $"rescheduled:456:{source.Id}"
        };
        var ordinaryDraft = new TeacherDraftItem
        {
            Date = date,
            DayOfWeek = date.DayOfWeek,
            StartTime = new TimeOnly(10, 0),
            EndTime = new TimeOnly(10, 45),
            LessonTypeId = source.Id,
            GroupId = model.FirstGroup.Id,
            ModuleId = model.Module.Id,
            ModuleTopicId = topic.Id,
            RoomId = model.Room.Id
        };
        var ordinaryScheduleItem = new ScheduleItem
        {
            Date = date,
            DayOfWeek = date.DayOfWeek,
            StartTime = new TimeOnly(11, 0),
            EndTime = new TimeOnly(11, 45),
            LessonTypeId = source.Id,
            GroupId = model.SecondGroup.Id,
            ModuleId = model.Module.Id,
            ModuleTopicId = topic.Id,
            RoomId = model.Room.Id
        };
        var now = DateTime.UtcNow;
        var job = new AutoGenJobRun
        {
            JobId = "merge-test-job",
            ClientPartitionKey = "merge-test",
            RequestHash = "merge-test-hash",
            Attempt = 1,
            Version = 1,
            Kind = 0,
            State = 2,
            Title = "Перевірка об'єднання",
            CurrentStage = "completed",
            CreatedAtUtc = now,
            RangeStartDate = date,
            RangeEndDate = date,
            RequestJson = $"{{\"lessonTypeIds\":[{source.Id}],\"title\":\"Огляд {source.Code}\"}}",
            StatusJson = $"{{\"lessonTypeName\":\"{source.Name}\",\"currentStage\":\"Етап {source.Name}\"}}",
            ResultJson = $"{{\"lessonTypeId\":{source.Id},\"lessonTypeCode\":\"{source.Code}\",\"message\":\"Код {source.Code} перевірено\"}}",
            UpdatedAtUtc = now
        };
        var formattedJob = new AutoGenJobRun
        {
            JobId = "merge-formatted-job",
            ClientPartitionKey = "merge-test",
            RequestHash = "merge-formatted-hash",
            Attempt = 1,
            Version = 1,
            Kind = 0,
            State = 2,
            Title = "Перевірка форматованого payload",
            CurrentStage = "completed",
            CreatedAtUtc = now,
            RangeStartDate = date,
            RangeEndDate = date,
            RequestJson = $"{{\n  \"LessonTypeIds\" : [ {source.Id} ],\n  \"title\" : \"Форматований payload\"\n}}",
            StatusJson = "{}",
            UpdatedAtUtc = now
        };
        var mixedCaseJob = new AutoGenJobRun
        {
            JobId = "merge-mixed-case-job",
            ClientPartitionKey = "merge-test",
            RequestHash = "merge-mixed-case-hash",
            Attempt = 1,
            Version = 1,
            Kind = 0,
            State = 2,
            Title = "Перевірка регістру payload",
            CurrentStage = "completed",
            CreatedAtUtc = now,
            RangeStartDate = date,
            RangeEndDate = date,
            RequestJson = "{}",
            StatusJson = "{}",
            ResultJson = $"{{\"lessonTypeCode\":\"{source.Code.ToLowerInvariant()}\"}}",
            ReportJson = $"{{\"lessonTypeName\":\"{source.Name.ToLowerInvariant()}\"}}",
            UpdatedAtUtc = now
        };
        var plan = new AutoGenDraftPlan
        {
            PlanId = "merge-test-plan",
            AutoGenJobRun = job,
            State = (int)AutoGenPlanState.RolledBack,
            Version = 1,
            CourseId = model.Course.Id,
            RangeStartDate = date,
            RangeEndDate = date,
            Days = 1,
            GroupIdsJson = $"[{model.FirstGroup.Id}]",
            BeforeScopeRevision = Guid.NewGuid(),
            InputFingerprint = "merge-test-fingerprint",
            AppliedScopeRevision = Guid.NewGuid(),
            AddCount = 1,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(1),
            AppliedAtUtc = now,
            RolledBackAtUtc = now
        };
        var mutation = new AutoGenDraftPlanMutation
        {
            Plan = plan,
            Ordinal = 0,
            Operation = (int)AutoGenPlanOperation.Add,
            AppliedDraftId = draft.Id,
            AppliedRevision = draft.Revision,
            AfterJson = $"{{\"lessonTypeId\":{source.Id},\"lessonTypeName\":\"{source.Name}\",\"lessonTypeCode\":\"{source.Code}\",\"groupName\":\"Група {source.Name}\",\"validationWarnings\":\"Код {source.Code} перевірено\"}}"
        };
        var formattedMutation = new AutoGenDraftPlanMutation
        {
            Plan = plan,
            Ordinal = 1,
            Operation = (int)AutoGenPlanOperation.Add,
            AfterJson = $"{{\n  \"LessonTypeId\" : {source.Id},\n  \"note\" : \"Безпечний payload\"\n}}"
        };
        var mixedCaseMutation = new AutoGenDraftPlanMutation
        {
            Plan = plan,
            Ordinal = 2,
            Operation = (int)AutoGenPlanOperation.Add,
            AfterJson = $"{{\"lessonTypeCode\":\"{source.Code.ToLowerInvariant()}\",\"lessonTypeName\":\"{source.Name.ToLowerInvariant()}\"}}"
        };
        fixture.Db.AddRange(
            draft,
            scheduleItem,
            ordinaryDraft,
            ordinaryScheduleItem,
            mutation,
            formattedMutation,
            mixedCaseMutation,
            formattedJob,
            mixedCaseJob);
        await fixture.Db.SaveChangesAsync();
        var ordinaryDraftRevision = ordinaryDraft.Revision;
        var ordinaryScheduleRevision = ordinaryScheduleItem.Revision;
        fixture.Db.ChangeTracker.Clear();

        var result = await LessonTypeMergeService.MergeAsync(
            fixture.Db,
            source.Id,
            model.LessonType.Id);

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(1, result.ModuleTopicsUpdated);
        Assert.Equal(2, result.TeacherDraftsUpdated);
        Assert.Equal(2, result.ScheduleItemsUpdated);
        Assert.Equal(2, result.RescheduleKeysUpdated);
        Assert.Equal(3, result.PlanSnapshotsUpdated);
        Assert.Equal(3, result.JobPayloadsUpdated);
        Assert.False(await fixture.Db.LessonTypes.AnyAsync(type => type.Id == source.Id));
        Assert.Equal(model.LessonType.Id, await fixture.Db.ModuleTopics
            .Where(item => item.Id == topic.Id)
            .Select(item => item.LessonTypeId)
            .SingleAsync());
        Assert.Equal(model.LessonType.Id, await fixture.Db.TeacherDraftItems
            .Where(item => item.Id == draft.Id)
            .Select(item => item.LessonTypeId)
            .SingleAsync());
        Assert.Equal($"rescheduled:123:{model.LessonType.Id}", await fixture.Db.TeacherDraftItems
            .Where(item => item.Id == draft.Id)
            .Select(item => item.BatchKey)
            .SingleAsync());
        Assert.Equal(model.LessonType.Id, await fixture.Db.ScheduleItems
            .Where(item => item.Id == scheduleItem.Id)
            .Select(item => item.LessonTypeId)
            .SingleAsync());
        Assert.Equal($"rescheduled:456:{model.LessonType.Id}", await fixture.Db.ScheduleItems
            .Where(item => item.Id == scheduleItem.Id)
            .Select(item => item.BatchKey)
            .SingleAsync());
        var savedOrdinaryDraft = await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .SingleAsync(item => item.Id == ordinaryDraft.Id);
        Assert.Equal(model.LessonType.Id, savedOrdinaryDraft.LessonTypeId);
        Assert.NotEqual(ordinaryDraftRevision, savedOrdinaryDraft.Revision);
        var savedOrdinarySchedule = await fixture.Db.ScheduleItems
            .AsNoTracking()
            .SingleAsync(item => item.Id == ordinaryScheduleItem.Id);
        Assert.Equal(model.LessonType.Id, savedOrdinarySchedule.LessonTypeId);
        Assert.NotEqual(ordinaryScheduleRevision, savedOrdinarySchedule.Revision);

        var savedAfterJson = await fixture.Db.AutoGenDraftPlanMutations
            .Where(item => item.Id == mutation.Id)
            .Select(item => item.AfterJson)
            .SingleAsync();
        using var afterDocument = JsonDocument.Parse(savedAfterJson!);
        Assert.Equal(model.LessonType.Id, afterDocument.RootElement.GetProperty("lessonTypeId").GetInt32());
        Assert.Equal(model.LessonType.Name, afterDocument.RootElement.GetProperty("lessonTypeName").GetString());
        Assert.Equal(model.LessonType.Code, afterDocument.RootElement.GetProperty("lessonTypeCode").GetString());
        Assert.Equal($"Група {source.Name}", afterDocument.RootElement.GetProperty("groupName").GetString());
        Assert.Equal($"Код {source.Code} перевірено", afterDocument.RootElement.GetProperty("validationWarnings").GetString());
        var savedFormattedAfterJson = await fixture.Db.AutoGenDraftPlanMutations
            .Where(item => item.Id == formattedMutation.Id)
            .Select(item => item.AfterJson)
            .SingleAsync();
        using var formattedAfterDocument = JsonDocument.Parse(savedFormattedAfterJson!);
        Assert.Equal(model.LessonType.Id, formattedAfterDocument.RootElement.GetProperty("LessonTypeId").GetInt32());
        Assert.Equal("Безпечний payload", formattedAfterDocument.RootElement.GetProperty("note").GetString());
        var savedMixedCaseAfterJson = await fixture.Db.AutoGenDraftPlanMutations
            .Where(item => item.Id == mixedCaseMutation.Id)
            .Select(item => item.AfterJson)
            .SingleAsync();
        using var mixedCaseAfterDocument = JsonDocument.Parse(savedMixedCaseAfterJson!);
        Assert.Equal(model.LessonType.Code, mixedCaseAfterDocument.RootElement.GetProperty("lessonTypeCode").GetString());
        Assert.Equal(model.LessonType.Name, mixedCaseAfterDocument.RootElement.GetProperty("lessonTypeName").GetString());
        var savedJob = await fixture.Db.AutoGenJobRuns.SingleAsync(item => item.Id == job.Id);
        using var requestDocument = JsonDocument.Parse(savedJob.RequestJson);
        using var statusDocument = JsonDocument.Parse(savedJob.StatusJson);
        using var resultDocument = JsonDocument.Parse(savedJob.ResultJson!);
        Assert.Equal(model.LessonType.Id, requestDocument.RootElement.GetProperty("lessonTypeIds")[0].GetInt32());
        Assert.Equal($"Огляд {source.Code}", requestDocument.RootElement.GetProperty("title").GetString());
        Assert.Equal(model.LessonType.Name, statusDocument.RootElement.GetProperty("lessonTypeName").GetString());
        Assert.Equal($"Етап {source.Name}", statusDocument.RootElement.GetProperty("currentStage").GetString());
        Assert.Equal(model.LessonType.Code, resultDocument.RootElement.GetProperty("lessonTypeCode").GetString());
        Assert.Equal($"Код {source.Code} перевірено", resultDocument.RootElement.GetProperty("message").GetString());
        var savedFormattedJob = await fixture.Db.AutoGenJobRuns.SingleAsync(item => item.Id == formattedJob.Id);
        using var formattedRequestDocument = JsonDocument.Parse(savedFormattedJob.RequestJson);
        Assert.Equal(model.LessonType.Id, formattedRequestDocument.RootElement.GetProperty("LessonTypeIds")[0].GetInt32());
        Assert.Equal("Форматований payload", formattedRequestDocument.RootElement.GetProperty("title").GetString());
        var savedMixedCaseJob = await fixture.Db.AutoGenJobRuns.SingleAsync(item => item.Id == mixedCaseJob.Id);
        using var mixedCaseResultDocument = JsonDocument.Parse(savedMixedCaseJob.ResultJson!);
        using var mixedCaseReportDocument = JsonDocument.Parse(savedMixedCaseJob.ReportJson!);
        Assert.Equal(model.LessonType.Code, mixedCaseResultDocument.RootElement.GetProperty("lessonTypeCode").GetString());
        Assert.Equal(model.LessonType.Name, mixedCaseReportDocument.RootElement.GetProperty("lessonTypeName").GetString());
    }

    [Fact]
    public async Task Lesson_type_merge_rejects_oversized_historical_payload_before_mutation()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);
        var source = new LessonTypeRef
        {
            Code = "OVERSIZED_DUPLICATE",
            Name = "Надмірний дублікат",
            IsActive = true,
            RequiresRoom = model.LessonType.RequiresRoom,
            RequiresTeacher = model.LessonType.RequiresTeacher,
            BlocksRoom = model.LessonType.BlocksRoom,
            BlocksTeacher = model.LessonType.BlocksTeacher,
            CountInPlan = model.LessonType.CountInPlan,
            CountInLoad = model.LessonType.CountInLoad,
            PreferredFirstInWeek = model.LessonType.PreferredFirstInWeek
        };
        var now = DateTime.UtcNow;
        fixture.Db.AddRange(
            source,
            new AutoGenJobRun
            {
                JobId = "merge-oversized-payload",
                ClientPartitionKey = "merge-test",
                RequestHash = "merge-oversized-hash",
                Attempt = 1,
                Version = 1,
                Kind = 0,
                State = 2,
                Title = "Перевірка надмірного payload",
                CurrentStage = "completed",
                CreatedAtUtc = now,
                RangeStartDate = new DateOnly(2026, 9, 7),
                RangeEndDate = new DateOnly(2026, 9, 7),
                RequestJson = "{}",
                StatusJson = "{}",
                ResultJson = new string('X', LessonTypeMergeService.MaxSingleHistoricalJsonCharacters + 1),
                UpdatedAtUtc = now
            });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var preflightException = await Assert.ThrowsAsync<LessonTypeMergeException>(() =>
            LessonTypeMergeService.ValidateMergeAsync(fixture.Db, source.Id, model.LessonType.Id));
        var mergeException = await Assert.ThrowsAsync<LessonTypeMergeException>(() =>
            LessonTypeMergeService.MergeAsync(fixture.Db, source.Id, model.LessonType.Id));

        Assert.Contains("символів", preflightException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(preflightException.Message, mergeException.Message);
        fixture.Db.ChangeTracker.Clear();
        Assert.True(await fixture.Db.LessonTypes.AsNoTracking().AnyAsync(type => type.Id == source.Id));
    }

    [Theory]
    [InlineData(AutoGenJobState.Queued, false)]
    [InlineData(AutoGenJobState.Running, true)]
    public async Task Lesson_type_merge_blocks_nonterminal_job_before_payload_traversal_and_allows_terminal_retry(
        AutoGenJobState state,
        bool legacyLease)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);
        var source = CreateCompatibleLessonType(
            model.LessonType,
            $"ACTIVE_{state.ToString().ToUpperInvariant()}",
            $"Активний дублікат {state}");
        var now = DateTime.UtcNow;
        var job = new AutoGenJobRun
        {
            JobId = $"merge-active-{state.ToString().ToLowerInvariant()}",
            ClientPartitionKey = "merge-test",
            RequestHash = $"merge-active-{state.ToString().ToLowerInvariant()}-hash",
            OwnerInstanceId = legacyLease ? null : "active-owner",
            Attempt = legacyLease ? 0 : 1,
            LeaseExpiresAtUtc = legacyLease ? null : now.AddMinutes(5),
            Version = 1,
            Kind = (int)AutoGenJobKind.Generate,
            State = (int)state,
            Title = "Перевірка блокування об'єднання",
            CurrentStage = state == AutoGenJobState.Queued ? "У черзі" : "Виконується",
            CreatedAtUtc = now,
            RangeStartDate = new DateOnly(2026, 9, 7),
            RangeEndDate = new DateOnly(2026, 9, 7),
            RequestJson = "{malformed",
            StatusJson = "{}",
            UpdatedAtUtc = now
        };
        fixture.Db.AddRange(source, job);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var preflightException = await Assert.ThrowsAsync<LessonTypeMergeException>(() =>
            LessonTypeMergeService.ValidateMergeAsync(fixture.Db, source.Id, model.LessonType.Id));
        var mergeException = await Assert.ThrowsAsync<LessonTypeMergeException>(() =>
            LessonTypeMergeService.MergeAsync(fixture.Db, source.Id, model.LessonType.Id));

        Assert.Contains("тимчасово недоступне", preflightException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(job.JobId, preflightException.Message, StringComparison.Ordinal);
        Assert.Contains(
            legacyLease ? "без lease" : "скасуйте завдання",
            preflightException.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(preflightException.Message, mergeException.Message);
        fixture.Db.ChangeTracker.Clear();
        Assert.True(await fixture.Db.LessonTypes.AsNoTracking().AnyAsync(type => type.Id == source.Id));
        Assert.Equal("{malformed", await fixture.Db.AutoGenJobRuns
            .AsNoTracking()
            .Where(item => item.JobId == job.JobId)
            .Select(item => item.RequestJson)
            .SingleAsync());

        var completedJob = await fixture.Db.AutoGenJobRuns.SingleAsync(item => item.JobId == job.JobId);
        completedJob.State = (int)AutoGenJobState.Succeeded;
        completedJob.OwnerInstanceId = null;
        completedJob.LeaseExpiresAtUtc = null;
        completedJob.CompletedAtUtc = now.AddMinutes(1);
        completedJob.CurrentStage = "Завершено";
        completedJob.RequestJson = "{}";
        completedJob.Version++;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var retry = await LessonTypeMergeService.MergeAsync(
            fixture.Db,
            source.Id,
            model.LessonType.Id);

        Assert.Equal(source.Id, retry.SourceTypeId);
        fixture.Db.ChangeTracker.Clear();
        Assert.False(await fixture.Db.LessonTypes.AsNoTracking().AnyAsync(type => type.Id == source.Id));
        Assert.Equal((int)AutoGenJobState.Succeeded, await fixture.Db.AutoGenJobRuns
            .AsNoTracking()
            .Where(item => item.JobId == job.JobId)
            .Select(item => item.State)
            .SingleAsync());
    }

    [Fact]
    public void Lesson_type_merge_json_rewrite_honors_cancellation()
    {
        var source = new LessonTypeRef { Id = 1, Code = "SOURCE", Name = "Вихідний" };
        var target = new LessonTypeRef { Id = 2, Code = "TARGET", Name = "Цільовий" };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => LessonTypeMergeService.RewriteJson(
            "{\"lessonTypeCode\":\"source\"}",
            source,
            target,
            out _,
            cancellation.Token));
    }

    [Fact]
    public async Task Lesson_type_merge_uses_non_cancelable_commit_after_final_token_check()
    {
        using var cancellation = new CancellationTokenSource();
        var interceptor = new CancelOriginalTokenOnCommitInterceptor(cancellation);
        await using var fixture = await TestDatabase.CreateAsync(interceptor: interceptor);
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);
        var source = new LessonTypeRef
        {
            Code = "COMMIT_DUPLICATE",
            Name = "Дублікат для commit",
            IsActive = true,
            RequiresRoom = model.LessonType.RequiresRoom,
            RequiresTeacher = model.LessonType.RequiresTeacher,
            BlocksRoom = model.LessonType.BlocksRoom,
            BlocksTeacher = model.LessonType.BlocksTeacher,
            CountInPlan = model.LessonType.CountInPlan,
            CountInLoad = model.LessonType.CountInLoad,
            PreferredFirstInWeek = model.LessonType.PreferredFirstInWeek
        };
        fixture.Db.LessonTypes.Add(source);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        interceptor.Arm();

        var result = await LessonTypeMergeService.MergeAsync(
            fixture.Db,
            source.Id,
            model.LessonType.Id,
            cancellation.Token);

        Assert.Equal(source.Id, result.SourceTypeId);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(interceptor.CommitObserved);
        Assert.False(interceptor.CommitTokenCanBeCanceled);
        Assert.False(interceptor.RollbackAttemptedAfterCommitStarted);
        fixture.Db.ChangeTracker.Clear();
        Assert.False(await fixture.Db.LessonTypes.AsNoTracking().AnyAsync(type => type.Id == source.Id));
    }

    [Theory]
    [InlineData("id-string")]
    [InlineData("name-number")]
    [InlineData("code-object")]
    [InlineData("ids-string-item")]
    [InlineData("codes-number-item")]
    public async Task Lesson_type_merge_rejects_semantically_invalid_recognized_json_fields(
        string payloadKind)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);
        var source = new LessonTypeRef
        {
            Code = "SEMANTIC_DUPLICATE",
            Name = "Семантично некоректний дублікат",
            IsActive = true,
            RequiresRoom = model.LessonType.RequiresRoom,
            RequiresTeacher = model.LessonType.RequiresTeacher,
            BlocksRoom = model.LessonType.BlocksRoom,
            BlocksTeacher = model.LessonType.BlocksTeacher,
            CountInPlan = model.LessonType.CountInPlan,
            CountInLoad = model.LessonType.CountInLoad,
            PreferredFirstInWeek = model.LessonType.PreferredFirstInWeek
        };
        fixture.Db.LessonTypes.Add(source);
        await fixture.Db.SaveChangesAsync();
        var payload = payloadKind switch
        {
            "id-string" => $"{{\"lessonTypeId\":\"{source.Id}\"}}",
            "name-number" => "{\"lessonTypeName\":42}",
            "code-object" => "{\"lessonTypeCode\":{}}",
            "ids-string-item" => $"{{\"lessonTypeIds\":[\"{source.Id}\"]}}",
            "codes-number-item" => "{\"lessonTypeCodes\":[42]}",
            _ => throw new ArgumentOutOfRangeException(nameof(payloadKind))
        };
        var now = DateTime.UtcNow;
        fixture.Db.AutoGenJobRuns.Add(new AutoGenJobRun
        {
            JobId = $"merge-semantic-{payloadKind}",
            ClientPartitionKey = "merge-test",
            RequestHash = "merge-semantic-hash",
            Attempt = 1,
            Version = 1,
            Kind = 0,
            State = 2,
            Title = "Перевірка семантики JSON",
            CurrentStage = "completed",
            CreatedAtUtc = now,
            RangeStartDate = new DateOnly(2026, 9, 7),
            RangeEndDate = new DateOnly(2026, 9, 7),
            RequestJson = "{}",
            StatusJson = "{}",
            ResultJson = payload,
            UpdatedAtUtc = now
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var preflightException = await Assert.ThrowsAsync<LessonTypeMergeException>(() =>
            LessonTypeMergeService.ValidateMergeAsync(fixture.Db, source.Id, model.LessonType.Id));
        var mergeException = await Assert.ThrowsAsync<LessonTypeMergeException>(() =>
            LessonTypeMergeService.MergeAsync(fixture.Db, source.Id, model.LessonType.Id));

        Assert.Contains("неочікуваним типом", preflightException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(preflightException.Message, mergeException.Message);
        fixture.Db.ChangeTracker.Clear();
        Assert.True(await fixture.Db.LessonTypes.AsNoTracking().AnyAsync(type => type.Id == source.Id));
    }

    [Fact]
    public async Task Lesson_type_merge_rejects_single_payload_whose_rewritten_output_would_expand_past_limit()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);
        model.LessonType.Name = new string('T', 200);
        var source = CreateCompatibleLessonType(model.LessonType, "EXPAND_SOURCE", "A");
        fixture.Db.LessonTypes.Add(source);
        await fixture.Db.SaveChangesAsync();
        var payload = "{\"lessonTypeNames\":[" +
                      string.Join(',', Enumerable.Repeat("\"A\"", 10_000)) +
                      "]}";
        fixture.Db.AutoGenJobRuns.Add(CreateHistoricalJob("merge-expanded-single", payload));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var preflightException = await Assert.ThrowsAsync<LessonTypeMergeException>(() =>
            LessonTypeMergeService.ValidateMergeAsync(fixture.Db, source.Id, model.LessonType.Id));
        var mergeException = await Assert.ThrowsAsync<LessonTypeMergeException>(() =>
            LessonTypeMergeService.MergeAsync(fixture.Db, source.Id, model.LessonType.Id));

        Assert.Contains("після об'єднання", preflightException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(preflightException.Message, mergeException.Message);
        fixture.Db.ChangeTracker.Clear();
        Assert.True(await fixture.Db.LessonTypes.AsNoTracking().AnyAsync(type => type.Id == source.Id));
        Assert.Equal(payload, await fixture.Db.AutoGenJobRuns
            .AsNoTracking()
            .Where(job => job.JobId == "merge-expanded-single")
            .Select(job => job.ResultJson)
            .SingleAsync());
    }

    [Fact]
    public async Task Lesson_type_merge_rejects_aggregate_rewritten_output_before_mutation()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);
        model.LessonType.Name = new string('T', 200);
        var source = CreateCompatibleLessonType(model.LessonType, "EXPAND_AGGREGATE", "A");
        fixture.Db.LessonTypes.Add(source);
        await fixture.Db.SaveChangesAsync();
        var payload = "{\"lessonTypeNames\":[" +
                      string.Join(',', Enumerable.Repeat("\"A\"", 9_000)) +
                      "]}";
        fixture.Db.AutoGenJobRuns.AddRange(Enumerable.Range(1, 18)
            .Select(index => CreateHistoricalJob($"merge-expanded-aggregate-{index}", payload)));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var preflightException = await Assert.ThrowsAsync<LessonTypeMergeException>(() =>
            LessonTypeMergeService.ValidateMergeAsync(fixture.Db, source.Id, model.LessonType.Id));
        var mergeException = await Assert.ThrowsAsync<LessonTypeMergeException>(() =>
            LessonTypeMergeService.MergeAsync(fixture.Db, source.Id, model.LessonType.Id));

        Assert.Contains("Сумарний розмір", preflightException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("після об'єднання", preflightException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(preflightException.Message, mergeException.Message);
        fixture.Db.ChangeTracker.Clear();
        Assert.True(await fixture.Db.LessonTypes.AsNoTracking().AnyAsync(type => type.Id == source.Id));
        Assert.Equal(18, await fixture.Db.AutoGenJobRuns.AsNoTracking().CountAsync());
        Assert.All(
            await fixture.Db.AutoGenJobRuns.AsNoTracking().Select(job => job.ResultJson).ToListAsync(),
            savedPayload => Assert.Equal(payload, savedPayload));
    }

    [Theory]
    [InlineData(AutoGenPlanState.Ready)]
    [InlineData(AutoGenPlanState.Applied)]
    public async Task Lesson_type_merge_fails_closed_for_active_autogen_plan(AutoGenPlanState planState)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);
        var source = new LessonTypeRef
        {
            Code = "ACTIVE_DUPLICATE",
            Name = "Активний дублікат",
            IsActive = true,
            RequiresRoom = model.LessonType.RequiresRoom,
            RequiresTeacher = model.LessonType.RequiresTeacher,
            BlocksRoom = model.LessonType.BlocksRoom,
            BlocksTeacher = model.LessonType.BlocksTeacher,
            CountInPlan = model.LessonType.CountInPlan,
            CountInLoad = model.LessonType.CountInLoad,
            PreferredFirstInWeek = model.LessonType.PreferredFirstInWeek
        };
        fixture.Db.LessonTypes.Add(source);
        await fixture.Db.SaveChangesAsync();
        var date = new DateOnly(2026, 9, 7);
        var draft = new TeacherDraftItem
        {
            Date = date,
            DayOfWeek = date.DayOfWeek,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(8, 45),
            LessonTypeId = source.Id,
            GroupId = model.FirstGroup.Id,
            ModuleId = model.Module.Id,
            RoomId = model.Room.Id
        };
        var scheduleItem = new ScheduleItem
        {
            Date = date,
            DayOfWeek = date.DayOfWeek,
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(9, 45),
            LessonTypeId = source.Id,
            GroupId = model.SecondGroup.Id,
            ModuleId = model.Module.Id,
            RoomId = model.Room.Id
        };
        var now = DateTime.UtcNow;
        var job = new AutoGenJobRun
        {
            JobId = $"active-merge-{planState}",
            ClientPartitionKey = "merge-test",
            RequestHash = "merge-test-hash",
            Attempt = 1,
            Version = 1,
            Kind = 0,
            State = 2,
            Title = "Перевірка активного плану",
            CurrentStage = "completed",
            CreatedAtUtc = now,
            RangeStartDate = date,
            RangeEndDate = date,
            RequestJson = "{}",
            StatusJson = "{}",
            UpdatedAtUtc = now
        };
        var plan = new AutoGenDraftPlan
        {
            PlanId = $"active-merge-plan-{planState}",
            AutoGenJobRun = job,
            State = (int)planState,
            Version = 1,
            CourseId = model.Course.Id,
            RangeStartDate = date,
            RangeEndDate = date,
            Days = 1,
            GroupIdsJson = planState == AutoGenPlanState.Ready
                ? $"[{model.SecondGroup.Id}]"
                : $"[{model.FirstGroup.Id}]",
            BeforeScopeRevision = draft.Revision,
            InputFingerprint = "merge-test-fingerprint",
            AppliedScopeRevision = planState == AutoGenPlanState.Applied ? draft.Revision : null,
            AddCount = 1,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(1),
            AppliedAtUtc = planState == AutoGenPlanState.Applied ? now : null
        };
        var mutation = new AutoGenDraftPlanMutation
        {
            Plan = plan,
            Ordinal = 0,
            Operation = (int)AutoGenPlanOperation.Add,
            AfterJson = $"{{\"lessonTypeId\":{model.LessonType.Id},\"lessonTypeName\":\"{model.LessonType.Name}\"}}"
        };
        fixture.Db.AddRange(draft, scheduleItem, mutation);
        await fixture.Db.SaveChangesAsync();
        var draftRevision = draft.Revision;
        var scheduleRevision = scheduleItem.Revision;
        fixture.Db.ChangeTracker.Clear();

        var preflightException = await Assert.ThrowsAsync<LessonTypeMergeException>(() =>
            LessonTypeMergeService.ValidateMergeAsync(fixture.Db, source.Id, model.LessonType.Id));

        Assert.Contains("Активні плани автогенерації", preflightException.Message, StringComparison.Ordinal);
        Assert.Contains("стануть недійсними", preflightException.Message, StringComparison.Ordinal);
        Assert.False(fixture.Db.ChangeTracker.HasChanges());

        var exception = await Assert.ThrowsAsync<LessonTypeMergeException>(() =>
            LessonTypeMergeService.MergeAsync(fixture.Db, source.Id, model.LessonType.Id));

        Assert.Contains("Активні плани автогенерації", exception.Message, StringComparison.Ordinal);
        fixture.Db.ChangeTracker.Clear();
        Assert.True(await fixture.Db.LessonTypes.AnyAsync(type => type.Id == source.Id));
        var savedDraft = await fixture.Db.TeacherDraftItems.AsNoTracking().SingleAsync(item => item.Id == draft.Id);
        Assert.Equal(source.Id, savedDraft.LessonTypeId);
        Assert.Equal(draftRevision, savedDraft.Revision);
        var savedSchedule = await fixture.Db.ScheduleItems.AsNoTracking().SingleAsync(item => item.Id == scheduleItem.Id);
        Assert.Equal(source.Id, savedSchedule.LessonTypeId);
        Assert.Equal(scheduleRevision, savedSchedule.Revision);
        Assert.Equal((int)planState, await fixture.Db.AutoGenDraftPlans
            .Where(item => item.Id == plan.Id)
            .Select(item => item.State)
            .SingleAsync());
    }

    [Fact]
    public async Task Module_upsert_rejects_case_insensitive_duplicate_code_in_course()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminModulesController(fixture.Db).Upsert(new ModuleEditDto(
            null,
            model.Module.Code.ToLowerInvariant(),
            "Дублікат модуля",
            model.Course.Id,
            1));

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(1, await fixture.Db.Modules.CountAsync());
    }

    [Fact]
    public async Task Module_list_does_not_split_shared_modules()
    {
        await using var fixture = await TestDatabase.CreateAsync(throwOnMultipleCollectionWarning: true);
        var firstCourse = new Course { Name = "Перший курс списку", DurationWeeks = 52 };
        var secondCourse = new Course { Name = "Другий курс списку", DurationWeeks = 52 };
        var module = new Module
        {
            Code = "СПИСОК-1",
            Title = "Спільний модуль списку",
            Credits = 1,
            Course = firstCourse
        };
        fixture.Db.AddRange(firstCourse, secondCourse, module);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ModuleCourses.AddRange(
            new ModuleCourse { ModuleId = module.Id, CourseId = firstCourse.Id },
            new ModuleCourse { ModuleId = module.Id, CourseId = secondCourse.Id });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        await new AdminModulesController(fixture.Db).List();

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(1, await fixture.Db.Modules.CountAsync());
        Assert.Equal(firstCourse.Id, await fixture.Db.Modules
            .Where(item => item.Id == module.Id)
            .Select(item => item.CourseId)
            .SingleAsync());
        Assert.Equal(2, await fixture.Db.ModuleCourses.CountAsync(link => link.ModuleId == module.Id));
    }

    [Fact]
    public async Task Module_upsert_rejects_room_restriction_excluding_published_placement()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);
        var alternativeRoom = new Room
        {
            Name = "Альтернативна аудиторія",
            Capacity = 40,
            BuildingId = model.Building.Id
        };
        fixture.Db.Rooms.Add(alternativeRoom);
        fixture.Db.ModuleRooms.Add(new ModuleRoom { ModuleId = model.Module.Id, RoomId = model.Room.Id });
        fixture.Db.ScheduleItems.Add(CreateScheduleItem(model, model.FirstGroup.Id, new DateOnly(2026, 9, 7)));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminModulesController(fixture.Db).Upsert(new ModuleEditDto(
            model.Module.Id,
            model.Module.Code,
            model.Module.Title,
            model.Course.Id,
            new List<int> { alternativeRoom.Id },
            new List<int>(),
            model.Module.Credits));

        Assert.IsType<ConflictObjectResult>(result.Result);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(new[] { model.Room.Id }, await fixture.Db.ModuleRooms
            .Where(link => link.ModuleId == model.Module.Id)
            .Select(link => link.RoomId)
            .ToArrayAsync());
    }

    [Fact]
    public async Task Module_upsert_rejects_building_restriction_excluding_draft_placement()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);
        var alternativeBuilding = new Building { Name = "Альтернативний корпус" };
        fixture.Db.Buildings.Add(alternativeBuilding);
        fixture.Db.ModuleBuildings.Add(new ModuleBuilding
        {
            ModuleId = model.Module.Id,
            BuildingId = model.Building.Id
        });
        fixture.Db.TeacherDraftItems.Add(CreateDraftItem(model, model.FirstGroup.Id, new DateOnly(2026, 9, 7)));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminModulesController(fixture.Db).Upsert(new ModuleEditDto(
            model.Module.Id,
            model.Module.Code,
            model.Module.Title,
            model.Course.Id,
            new List<int>(),
            new List<int> { alternativeBuilding.Id },
            model.Module.Credits));

        Assert.IsType<ConflictObjectResult>(result.Result);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(new[] { model.Building.Id }, await fixture.Db.ModuleBuildings
            .Where(link => link.ModuleId == model.Module.Id)
            .Select(link => link.BuildingId)
            .ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Topic_lesson_type_change_rejects_used_topic(bool useDraft)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);
        var replacementType = new LessonTypeRef
        {
            Code = "OTHER",
            Name = "Інший тип заняття",
            IsActive = true,
            RequiresRoom = false,
            RequiresTeacher = false,
            BlocksRoom = false,
            BlocksTeacher = false,
            CountInPlan = true,
            CountInLoad = false
        };
        var topic = new ModuleTopic
        {
            ModuleId = model.Module.Id,
            Order = 1,
            TopicCode = "1.1",
            LessonTypeId = model.LessonType.Id,
            TotalHours = 2,
            AuditoriumHours = 2
        };
        fixture.Db.AddRange(replacementType, topic);
        await fixture.Db.SaveChangesAsync();
        if (useDraft)
        {
            var draft = CreateDraftItem(model, model.FirstGroup.Id, new DateOnly(2026, 9, 7));
            draft.ModuleTopicId = topic.Id;
            fixture.Db.TeacherDraftItems.Add(draft);
        }
        else
        {
            var schedule = CreateScheduleItem(model, model.FirstGroup.Id, new DateOnly(2026, 9, 7));
            schedule.ModuleTopicId = topic.Id;
            fixture.Db.ScheduleItems.Add(schedule);
        }
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminModulesController(fixture.Db).UpsertTopic(
            model.Module.Id,
            new ModuleTopicDto(
                topic.Id,
                model.Module.Id,
                topic.Order,
                topic.TopicCode,
                replacementType.Id,
                null,
                topic.TotalHours,
                topic.AuditoriumHours,
                topic.SelfStudyHours,
                topic.IsInterAssembly,
                topic.SelfStudyBySupervisor));

        Assert.IsType<ConflictObjectResult>(result.Result);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(model.LessonType.Id, await fixture.Db.ModuleTopics
            .Where(item => item.Id == topic.Id)
            .Select(item => item.LessonTypeId)
            .SingleAsync());
    }

    [Fact]
    public async Task Topic_code_change_rejects_used_topic_without_reordering_schedule_semantics()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);
        var topic = new ModuleTopic
        {
            ModuleId = model.Module.Id,
            Order = 1,
            TopicCode = "1.1",
            LessonTypeId = model.LessonType.Id,
            TotalHours = 2,
            AuditoriumHours = 2
        };
        fixture.Db.ModuleTopics.Add(topic);
        await fixture.Db.SaveChangesAsync();
        var schedule = CreateScheduleItem(model, model.FirstGroup.Id, new DateOnly(2026, 9, 7));
        schedule.ModuleTopicId = topic.Id;
        fixture.Db.ScheduleItems.Add(schedule);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminModulesController(fixture.Db).UpsertTopic(
            model.Module.Id,
            new ModuleTopicDto(
                topic.Id,
                model.Module.Id,
                topic.Order,
                "9.9",
                model.LessonType.Id,
                null,
                topic.TotalHours,
                topic.AuditoriumHours,
                topic.SelfStudyHours,
                topic.IsInterAssembly,
                topic.SelfStudyBySupervisor));

        Assert.IsType<ConflictObjectResult>(result.Result);
        fixture.Db.ChangeTracker.Clear();
        var persisted = await fixture.Db.ModuleTopics.AsNoTracking().SingleAsync(item => item.Id == topic.Id);
        Assert.Equal("1.1", persisted.TopicCode);
        Assert.Equal(1, persisted.Order);
    }

    [Fact]
    public async Task Topic_upsert_rejects_hour_sum_overflow_before_mutation()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);

        var result = await new AdminModulesController(fixture.Db).UpsertTopic(
            model.Module.Id,
            new ModuleTopicDto(
                null,
                model.Module.Id,
                1,
                "1.1",
                model.LessonType.Id,
                null,
                int.MaxValue,
                int.MaxValue,
                int.MaxValue,
                false,
                false));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("перевищує", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        fixture.Db.ChangeTracker.Clear();
        Assert.Empty(await fixture.Db.ModuleTopics.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Topic_upsert_uses_collision_free_temporary_order_before_reordering()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);
        fixture.Db.ModuleTopics.Add(new ModuleTopic
        {
            ModuleId = model.Module.Id,
            Order = 1,
            TopicCode = "2.1",
            LessonTypeId = model.LessonType.Id,
            TotalHours = 1,
            AuditoriumHours = 1
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminModulesController(fixture.Db).UpsertTopic(
            model.Module.Id,
            new ModuleTopicDto(
                null,
                model.Module.Id,
                1,
                "1.1",
                model.LessonType.Id,
                null,
                1,
                1,
                0,
                false,
                false));

        Assert.IsType<OkObjectResult>(result.Result);
        fixture.Db.ChangeTracker.Clear();
        var topics = await fixture.Db.ModuleTopics
            .AsNoTracking()
            .Where(topic => topic.ModuleId == model.Module.Id)
            .OrderBy(topic => topic.Order)
            .Select(topic => new { topic.TopicCode, topic.Order })
            .ToListAsync();
        Assert.Collection(
            topics,
            topic =>
            {
                Assert.Equal("1.1", topic.TopicCode);
                Assert.Equal(1, topic.Order);
            },
            topic =>
            {
                Assert.Equal("2.1", topic.TopicCode);
                Assert.Equal(2, topic.Order);
            });
    }

    [Fact]
    public async Task Topic_reordering_does_not_collide_with_existing_legacy_temporary_order()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);
        var firstTopic = new ModuleTopic
        {
            ModuleId = model.Module.Id,
            Order = 1,
            TopicCode = "2.1",
            LessonTypeId = model.LessonType.Id,
            TotalHours = 1,
            AuditoriumHours = 1
        };
        var secondTopic = new ModuleTopic
        {
            ModuleId = model.Module.Id,
            Order = 1000,
            TopicCode = "1.1",
            LessonTypeId = model.LessonType.Id,
            TotalHours = 1,
            AuditoriumHours = 1
        };
        fixture.Db.ModuleTopics.AddRange(firstTopic, secondTopic);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminModulesController(fixture.Db).UpsertTopic(
            model.Module.Id,
            new ModuleTopicDto(
                firstTopic.Id,
                model.Module.Id,
                firstTopic.Order,
                firstTopic.TopicCode,
                model.LessonType.Id,
                null,
                1,
                1,
                0,
                false,
                false));

        Assert.IsType<OkObjectResult>(result.Result);
        fixture.Db.ChangeTracker.Clear();
        var topics = await fixture.Db.ModuleTopics
            .AsNoTracking()
            .Where(topic => topic.ModuleId == model.Module.Id)
            .OrderBy(topic => topic.Order)
            .Select(topic => new { topic.TopicCode, topic.Order })
            .ToListAsync();
        Assert.Collection(
            topics,
            topic =>
            {
                Assert.Equal("1.1", topic.TopicCode);
                Assert.Equal(1, topic.Order);
            },
            topic =>
            {
                Assert.Equal("2.1", topic.TopicCode);
                Assert.Equal(2, topic.Order);
            });
    }

    [Fact]
    public async Task Ensure_course_scope_rolls_back_when_topic_maps_do_not_match()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var firstCourse = new Course { Name = "Курс джерела", DurationWeeks = 52 };
        var secondCourse = new Course { Name = "Курс призначення", DurationWeeks = 52 };
        var sourceModule = new Module
        {
            Code = "КАРТА-1",
            Title = "Джерельний модуль",
            Credits = 1,
            Course = firstCourse
        };
        var targetModule = new Module
        {
            Code = "карта-1",
            Title = "Цільовий модуль",
            Credits = 1,
            Course = secondCourse
        };
        var lessonType = new LessonTypeRef
        {
            Code = "MAP",
            Name = "Тип для карти тем",
            IsActive = true,
            RequiresRoom = false,
            RequiresTeacher = false,
            BlocksRoom = false,
            BlocksTeacher = false,
            CountInPlan = true,
            CountInLoad = false
        };
        var group = new Group { Name = "Група призначення", StudentsCount = 20, Course = secondCourse };
        var teacher = new Teacher { FullName = "Викладач для перевірки відкату" };
        fixture.Db.AddRange(firstCourse, secondCourse, sourceModule, targetModule, lessonType, group, teacher);
        await fixture.Db.SaveChangesAsync();
        var sourceTopic = new ModuleTopic
        {
            ModuleId = sourceModule.Id,
            Order = 1,
            TopicCode = "1.1",
            LessonTypeId = lessonType.Id,
            TotalHours = 2,
            AuditoriumHours = 2
        };
        var targetTopic = new ModuleTopic
        {
            ModuleId = targetModule.Id,
            Order = 1,
            TopicCode = "9.9",
            LessonTypeId = lessonType.Id,
            TotalHours = 2,
            AuditoriumHours = 2
        };
        fixture.Db.AddRange(sourceTopic, targetTopic);
        fixture.Db.ModuleCourses.AddRange(
            new ModuleCourse { ModuleId = sourceModule.Id, CourseId = firstCourse.Id },
            new ModuleCourse { ModuleId = sourceModule.Id, CourseId = secondCourse.Id },
            new ModuleCourse { ModuleId = targetModule.Id, CourseId = secondCourse.Id });
        fixture.Db.TeacherModules.Add(new TeacherModule
        {
            TeacherId = teacher.Id,
            ModuleId = sourceModule.Id
        });
        await fixture.Db.SaveChangesAsync();
        var date = new DateOnly(2026, 9, 7);
        var schedule = new ScheduleItem
        {
            Date = date,
            DayOfWeek = date.DayOfWeek,
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(10, 0),
            GroupId = group.Id,
            ModuleId = sourceModule.Id,
            ModuleTopicId = sourceTopic.Id,
            LessonTypeId = lessonType.Id
        };
        var draft = new TeacherDraftItem
        {
            Date = date,
            DayOfWeek = date.DayOfWeek,
            StartTime = new TimeOnly(10, 0),
            EndTime = new TimeOnly(11, 0),
            GroupId = group.Id,
            ModuleId = sourceModule.Id,
            ModuleTopicId = sourceTopic.Id,
            LessonTypeId = lessonType.Id
        };
        fixture.Db.AddRange(schedule, draft);
        await fixture.Db.SaveChangesAsync();
        var scheduleId = schedule.Id;
        var draftId = draft.Id;
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminModulesController(fixture.Db)
            .EnsureCourseScope(sourceModule.Id, secondCourse.Id);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        fixture.Db.ChangeTracker.Clear();
        var storedSchedule = await fixture.Db.ScheduleItems.AsNoTracking()
            .SingleAsync(item => item.Id == scheduleId);
        var storedDraft = await fixture.Db.TeacherDraftItems.AsNoTracking()
            .SingleAsync(item => item.Id == draftId);
        Assert.Equal(sourceModule.Id, storedSchedule.ModuleId);
        Assert.Equal(sourceTopic.Id, storedSchedule.ModuleTopicId);
        Assert.Equal(sourceModule.Id, storedDraft.ModuleId);
        Assert.Equal(sourceTopic.Id, storedDraft.ModuleTopicId);
        Assert.True(await fixture.Db.ModuleCourses.AnyAsync(link =>
            link.ModuleId == sourceModule.Id && link.CourseId == secondCourse.Id));
        Assert.False(await fixture.Db.TeacherModules.AnyAsync(link =>
            link.ModuleId == targetModule.Id && link.TeacherId == teacher.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ensure_course_scope_rejects_topic_lesson_type_mismatch_for_existing_placements(bool useDraft)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var sourceCourse = new Course { Name = "Курс джерела типу", DurationWeeks = 52 };
        var targetCourse = new Course { Name = "Курс призначення типу", DurationWeeks = 52 };
        var sourceModule = new Module
        {
            Code = "ТИП-1",
            Title = "Джерельний модуль типу",
            Credits = 1,
            Course = sourceCourse
        };
        var targetModule = new Module
        {
            Code = "тип-1",
            Title = "Цільовий модуль типу",
            Credits = 1,
            Course = targetCourse
        };
        var sourceType = new LessonTypeRef
        {
            Code = "SOURCE-TYPE",
            Name = "Джерельний тип",
            IsActive = true,
            RequiresRoom = false,
            RequiresTeacher = false,
            BlocksRoom = false,
            BlocksTeacher = false,
            CountInPlan = true,
            CountInLoad = false
        };
        var targetType = new LessonTypeRef
        {
            Code = "TARGET-TYPE",
            Name = "Цільовий тип",
            IsActive = true,
            RequiresRoom = false,
            RequiresTeacher = false,
            BlocksRoom = false,
            BlocksTeacher = false,
            CountInPlan = true,
            CountInLoad = false
        };
        var group = new Group
        {
            Name = "Група призначення типу",
            StudentsCount = 20,
            Course = targetCourse
        };
        fixture.Db.AddRange(sourceCourse, targetCourse, sourceModule, targetModule, sourceType, targetType, group);
        await fixture.Db.SaveChangesAsync();
        var sourceTopic = new ModuleTopic
        {
            ModuleId = sourceModule.Id,
            Order = 1,
            TopicCode = "1.1",
            LessonTypeId = sourceType.Id,
            TotalHours = 2,
            AuditoriumHours = 2
        };
        var targetTopic = new ModuleTopic
        {
            ModuleId = targetModule.Id,
            Order = 1,
            TopicCode = "1.1",
            LessonTypeId = targetType.Id,
            TotalHours = 2,
            AuditoriumHours = 2
        };
        fixture.Db.AddRange(sourceTopic, targetTopic);
        fixture.Db.ModuleCourses.AddRange(
            new ModuleCourse { ModuleId = sourceModule.Id, CourseId = sourceCourse.Id },
            new ModuleCourse { ModuleId = sourceModule.Id, CourseId = targetCourse.Id },
            new ModuleCourse { ModuleId = targetModule.Id, CourseId = targetCourse.Id });
        await fixture.Db.SaveChangesAsync();
        var date = new DateOnly(2026, 9, 7);
        int placementId;
        if (useDraft)
        {
            var draft = new TeacherDraftItem
            {
                Date = date,
                DayOfWeek = date.DayOfWeek,
                StartTime = new TimeOnly(9, 0),
                EndTime = new TimeOnly(10, 0),
                GroupId = group.Id,
                ModuleId = sourceModule.Id,
                ModuleTopicId = sourceTopic.Id,
                LessonTypeId = sourceType.Id
            };
            fixture.Db.TeacherDraftItems.Add(draft);
            await fixture.Db.SaveChangesAsync();
            placementId = draft.Id;
        }
        else
        {
            var schedule = new ScheduleItem
            {
                Date = date,
                DayOfWeek = date.DayOfWeek,
                StartTime = new TimeOnly(9, 0),
                EndTime = new TimeOnly(10, 0),
                GroupId = group.Id,
                ModuleId = sourceModule.Id,
                ModuleTopicId = sourceTopic.Id,
                LessonTypeId = sourceType.Id
            };
            fixture.Db.ScheduleItems.Add(schedule);
            await fixture.Db.SaveChangesAsync();
            placementId = schedule.Id;
        }
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminModulesController(fixture.Db)
            .EnsureCourseScope(sourceModule.Id, targetCourse.Id);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        fixture.Db.ChangeTracker.Clear();
        if (useDraft)
        {
            var stored = await fixture.Db.TeacherDraftItems.AsNoTracking()
                .SingleAsync(item => item.Id == placementId);
            Assert.Equal(sourceModule.Id, stored.ModuleId);
            Assert.Equal(sourceTopic.Id, stored.ModuleTopicId);
            Assert.Equal(sourceType.Id, stored.LessonTypeId);
        }
        else
        {
            var stored = await fixture.Db.ScheduleItems.AsNoTracking()
                .SingleAsync(item => item.Id == placementId);
            Assert.Equal(sourceModule.Id, stored.ModuleId);
            Assert.Equal(sourceTopic.Id, stored.ModuleTopicId);
            Assert.Equal(sourceType.Id, stored.LessonTypeId);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Ensure_course_scope_rejects_target_room_or_building_restriction_for_existing_placements(
        bool useDraft,
        bool restrictByBuilding)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var sourceCourse = new Course { Name = "Курс джерела аудиторії", DurationWeeks = 52 };
        var targetCourse = new Course { Name = "Курс призначення аудиторії", DurationWeeks = 52 };
        var sourceModule = new Module
        {
            Code = "ROOM-SCOPE",
            Title = "Джерельний модуль аудиторії",
            Credits = 1,
            Course = sourceCourse
        };
        var targetModule = new Module
        {
            Code = "room-scope",
            Title = "Цільовий модуль аудиторії",
            Credits = 1,
            Course = targetCourse
        };
        var lessonType = new LessonTypeRef
        {
            Code = "ROOM-SCOPE-TYPE",
            Name = "Аудиторне заняття міграції",
            IsActive = true,
            RequiresRoom = true,
            RequiresTeacher = false,
            BlocksRoom = true,
            BlocksTeacher = false,
            CountInPlan = true,
            CountInLoad = false
        };
        var usedBuilding = new Building { Name = "Корпус наявного заняття" };
        var allowedBuilding = new Building { Name = "Дозволений цільовий корпус" };
        var usedRoom = new Room { Name = "Аудиторія наявного заняття", Capacity = 40, Building = usedBuilding };
        var allowedRoom = new Room { Name = "Дозволена цільова аудиторія", Capacity = 40, Building = allowedBuilding };
        var group = new Group
        {
            Name = "Група призначення аудиторії",
            StudentsCount = 20,
            Course = targetCourse
        };
        fixture.Db.AddRange(
            sourceCourse,
            targetCourse,
            sourceModule,
            targetModule,
            lessonType,
            usedBuilding,
            allowedBuilding,
            usedRoom,
            allowedRoom,
            group);
        await fixture.Db.SaveChangesAsync();
        var sourceTopic = new ModuleTopic
        {
            ModuleId = sourceModule.Id,
            Order = 1,
            TopicCode = "2.1",
            LessonTypeId = lessonType.Id,
            TotalHours = 2,
            AuditoriumHours = 2
        };
        var targetTopic = new ModuleTopic
        {
            ModuleId = targetModule.Id,
            Order = 1,
            TopicCode = "2.1",
            LessonTypeId = lessonType.Id,
            TotalHours = 2,
            AuditoriumHours = 2
        };
        fixture.Db.AddRange(sourceTopic, targetTopic);
        fixture.Db.ModuleCourses.AddRange(
            new ModuleCourse { ModuleId = sourceModule.Id, CourseId = sourceCourse.Id },
            new ModuleCourse { ModuleId = sourceModule.Id, CourseId = targetCourse.Id },
            new ModuleCourse { ModuleId = targetModule.Id, CourseId = targetCourse.Id });
        if (restrictByBuilding)
        {
            fixture.Db.ModuleBuildings.Add(new ModuleBuilding
            {
                ModuleId = targetModule.Id,
                BuildingId = allowedBuilding.Id
            });
        }
        else
        {
            fixture.Db.ModuleRooms.Add(new ModuleRoom
            {
                ModuleId = targetModule.Id,
                RoomId = allowedRoom.Id
            });
        }
        await fixture.Db.SaveChangesAsync();
        var date = new DateOnly(2026, 9, 7);
        int placementId;
        if (useDraft)
        {
            var draft = new TeacherDraftItem
            {
                Date = date,
                DayOfWeek = date.DayOfWeek,
                StartTime = new TimeOnly(9, 0),
                EndTime = new TimeOnly(10, 0),
                GroupId = group.Id,
                ModuleId = sourceModule.Id,
                ModuleTopicId = sourceTopic.Id,
                LessonTypeId = lessonType.Id,
                RoomId = usedRoom.Id
            };
            fixture.Db.TeacherDraftItems.Add(draft);
            await fixture.Db.SaveChangesAsync();
            placementId = draft.Id;
        }
        else
        {
            var schedule = new ScheduleItem
            {
                Date = date,
                DayOfWeek = date.DayOfWeek,
                StartTime = new TimeOnly(9, 0),
                EndTime = new TimeOnly(10, 0),
                GroupId = group.Id,
                ModuleId = sourceModule.Id,
                ModuleTopicId = sourceTopic.Id,
                LessonTypeId = lessonType.Id,
                RoomId = usedRoom.Id
            };
            fixture.Db.ScheduleItems.Add(schedule);
            await fixture.Db.SaveChangesAsync();
            placementId = schedule.Id;
        }
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminModulesController(fixture.Db)
            .EnsureCourseScope(sourceModule.Id, targetCourse.Id);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        fixture.Db.ChangeTracker.Clear();
        if (useDraft)
        {
            var stored = await fixture.Db.TeacherDraftItems.AsNoTracking()
                .SingleAsync(item => item.Id == placementId);
            Assert.Equal(sourceModule.Id, stored.ModuleId);
            Assert.Equal(sourceTopic.Id, stored.ModuleTopicId);
            Assert.Equal(usedRoom.Id, stored.RoomId);
        }
        else
        {
            var stored = await fixture.Db.ScheduleItems.AsNoTracking()
                .SingleAsync(item => item.Id == placementId);
            Assert.Equal(sourceModule.Id, stored.ModuleId);
            Assert.Equal(sourceTopic.Id, stored.ModuleTopicId);
            Assert.Equal(usedRoom.Id, stored.RoomId);
        }
    }

    [Fact]
    public async Task Course_force_delete_rejects_duplicate_normalized_module_code_before_changes()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var firstCourse = new Course { Name = "Курс для захищеного видалення", DurationWeeks = 52 };
        var secondCourse = new Course { Name = "Альтернативний курс", DurationWeeks = 52 };
        var group = new Group { Name = "Група захищеного курсу", StudentsCount = 20, Course = firstCourse };
        var sourceModule = new Module
        {
            Code = " ДУБЛЬ-1 ",
            Title = "Первинний модуль",
            Credits = 1,
            Course = firstCourse
        };
        var targetModule = new Module
        {
            Code = "дубль-1",
            Title = "Наявний модуль",
            Credits = 1,
            Course = secondCourse
        };
        fixture.Db.AddRange(firstCourse, secondCourse, group, sourceModule, targetModule);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ModuleCourses.Add(new ModuleCourse
        {
            ModuleId = sourceModule.Id,
            CourseId = secondCourse.Id
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminCoursesController(fixture.Db).Delete(firstCourse.Id, force: true);

        Assert.IsType<ConflictObjectResult>(result);
        fixture.Db.ChangeTracker.Clear();
        Assert.True(await fixture.Db.Courses.AnyAsync(course => course.Id == firstCourse.Id));
        Assert.True(await fixture.Db.Groups.AnyAsync(item => item.Id == group.Id));
        Assert.Equal(firstCourse.Id, await fixture.Db.Modules
            .Where(item => item.Id == sourceModule.Id)
            .Select(item => item.CourseId)
            .SingleAsync());
        Assert.True(await fixture.Db.ModuleCourses.AnyAsync(link =>
            link.ModuleId == sourceModule.Id && link.CourseId == secondCourse.Id));
    }

    [Fact]
    public async Task Travel_upsert_rejects_increase_for_reverse_group_adjacency_across_schedule_and_draft()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedTravelModelAsync(10);
        var date = new DateOnly(2026, 9, 7);
        fixture.Db.ScheduleItems.Add(CreateTravelScheduleItem(
            model,
            model.FirstGroup.Id,
            model.SecondRoom.Id,
            date,
            new TimeOnly(9, 0),
            new TimeOnly(10, 0)));
        fixture.Db.TeacherDraftItems.Add(CreateTravelDraftItem(
            model,
            model.FirstGroup.Id,
            model.FirstRoom.Id,
            date,
            new TimeOnly(10, 15),
            new TimeOnly(11, 15)));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminBuildingsController(fixture.Db).UpsertTravel(
            new BuildingTravelEditDto(model.FirstBuilding.Id, model.SecondBuilding.Id, 20));

        Assert.IsType<ConflictObjectResult>(result);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(10, await fixture.Db.BuildingTravels
            .Select(item => item.Minutes)
            .SingleAsync());
    }

    [Fact]
    public async Task Travel_upsert_rejects_increase_for_blocking_teacher_adjacency()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedTravelModelAsync(10);
        var date = new DateOnly(2026, 9, 7);
        fixture.Db.ScheduleItems.Add(CreateTravelScheduleItem(
            model,
            model.FirstGroup.Id,
            model.FirstRoom.Id,
            date,
            new TimeOnly(9, 0),
            new TimeOnly(10, 0),
            model.Teacher.Id));
        fixture.Db.TeacherDraftItems.Add(CreateTravelDraftItem(
            model,
            model.SecondGroup.Id,
            model.SecondRoom.Id,
            date,
            new TimeOnly(10, 15),
            new TimeOnly(11, 15),
            model.Teacher.Id));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminBuildingsController(fixture.Db).UpsertTravel(
            new BuildingTravelEditDto(model.FirstBuilding.Id, model.SecondBuilding.Id, 20));

        Assert.IsType<ConflictObjectResult>(result);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(10, await fixture.Db.BuildingTravels
            .Select(item => item.Minutes)
            .SingleAsync());
    }

    [Fact]
    public async Task Travel_upsert_rejects_increase_for_required_nonblocking_teacher_adjacency()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedTravelModelAsync(10);
        model.LessonType.BlocksTeacher = false;
        await fixture.Db.SaveChangesAsync();
        var date = new DateOnly(2026, 9, 7);
        fixture.Db.ScheduleItems.Add(CreateTravelScheduleItem(
            model,
            model.FirstGroup.Id,
            model.FirstRoom.Id,
            date,
            new TimeOnly(9, 0),
            new TimeOnly(10, 0),
            model.Teacher.Id));
        fixture.Db.TeacherDraftItems.Add(CreateTravelDraftItem(
            model,
            model.SecondGroup.Id,
            model.SecondRoom.Id,
            date,
            new TimeOnly(10, 15),
            new TimeOnly(11, 15),
            model.Teacher.Id));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminBuildingsController(fixture.Db).UpsertTravel(
            new BuildingTravelEditDto(model.FirstBuilding.Id, model.SecondBuilding.Id, 20));

        Assert.IsType<ConflictObjectResult>(result);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(10, await fixture.Db.BuildingTravels
            .Select(item => item.Minutes)
            .SingleAsync());
    }

    [Fact]
    public async Task Travel_upsert_rejects_increase_for_approved_draft_adjacency()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedTravelModelAsync(10);
        var date = new DateOnly(2026, 9, 7);
        fixture.Db.ScheduleItems.Add(CreateTravelScheduleItem(
            model,
            model.FirstGroup.Id,
            model.FirstRoom.Id,
            date,
            new TimeOnly(9, 0),
            new TimeOnly(10, 0)));
        var approvedDraft = CreateTravelDraftItem(
            model,
            model.FirstGroup.Id,
            model.SecondRoom.Id,
            date,
            new TimeOnly(10, 15),
            new TimeOnly(11, 15));
        approvedDraft.Status = DraftStatus.Published;
        fixture.Db.TeacherDraftItems.Add(approvedDraft);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminBuildingsController(fixture.Db).UpsertTravel(
            new BuildingTravelEditDto(model.FirstBuilding.Id, model.SecondBuilding.Id, 20));

        Assert.IsType<ConflictObjectResult>(result);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(10, await fixture.Db.BuildingTravels
            .Select(item => item.Minutes)
            .SingleAsync());
    }

    [Fact]
    public async Task Travel_delete_rejects_fallback_increase_without_removing_route()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedTravelModelAsync(10);
        var date = new DateOnly(2026, 9, 7);
        fixture.Db.ScheduleItems.AddRange(
            CreateTravelScheduleItem(
                model,
                model.FirstGroup.Id,
                model.FirstRoom.Id,
                date,
                new TimeOnly(9, 0),
                new TimeOnly(10, 0)),
            CreateTravelScheduleItem(
                model,
                model.FirstGroup.Id,
                model.SecondRoom.Id,
                date,
                new TimeOnly(10, 15),
                new TimeOnly(11, 15)));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminBuildingsController(fixture.Db).DeleteTravel(
            new BuildingTravelEditDto(model.SecondBuilding.Id, model.FirstBuilding.Id, 0));

        Assert.IsType<ConflictObjectResult>(result);
        fixture.Db.ChangeTracker.Clear();
        Assert.True(await fixture.Db.BuildingTravels.AnyAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Calendar_upsert_rejects_new_nonworking_weekend_scope_with_existing_placement(bool useDraft)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);
        var date = new DateOnly(2026, 9, 5);
        if (useDraft)
        {
            fixture.Db.TeacherDraftItems.Add(CreateDraftItem(model, model.FirstGroup.Id, date));
        }
        else
        {
            fixture.Db.ScheduleItems.Add(CreateScheduleItem(model, model.FirstGroup.Id, date));
        }
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminConfigController(fixture.Db).CalendarUpsert(
            new CalendarExceptionEditDto(
                null,
                date.ToString("yyyy-MM-dd"),
                false,
                "Неробочий день",
                model.Course.Id));

        Assert.IsType<ConflictObjectResult>(result.Result);
        fixture.Db.ChangeTracker.Clear();
        Assert.False(await fixture.Db.CalendarExceptions.AnyAsync());
    }

    [Fact]
    public async Task Calendar_upsert_allows_global_nonworking_exception_when_course_override_keeps_placement_working()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);
        var date = new DateOnly(2026, 9, 7);
        fixture.Db.ScheduleItems.Add(CreateScheduleItem(model, model.FirstGroup.Id, date));
        fixture.Db.CalendarExceptions.Add(new CalendarException
        {
            Date = date,
            IsWorkingDay = true,
            Name = "Робочий день курсу",
            CourseId = model.Course.Id
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminConfigController(fixture.Db).CalendarUpsert(
            new CalendarExceptionEditDto(
                null,
                date.ToString("yyyy-MM-dd"),
                false,
                "Глобальний неробочий день"));

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(2, await fixture.Db.CalendarExceptions.CountAsync());
    }

    [Fact]
    public async Task Calendar_upsert_rejects_new_nonworking_scope_with_approved_draft()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);
        var date = new DateOnly(2026, 9, 7);
        var approvedDraft = CreateDraftItem(model, model.FirstGroup.Id, date);
        approvedDraft.Status = DraftStatus.Published;
        fixture.Db.TeacherDraftItems.Add(approvedDraft);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminConfigController(fixture.Db).CalendarUpsert(
            new CalendarExceptionEditDto(
                null,
                date.ToString("yyyy-MM-dd"),
                false,
                "Неробочий день",
                model.Course.Id));

        Assert.IsType<ConflictObjectResult>(result.Result);
        fixture.Db.ChangeTracker.Clear();
        Assert.False(await fixture.Db.CalendarExceptions.AnyAsync());
    }

    [Fact]
    public async Task Calendar_delete_allows_removing_redundant_working_weekend_override()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);
        var date = new DateOnly(2026, 9, 6);
        fixture.Db.TeacherDraftItems.Add(CreateDraftItem(model, model.FirstGroup.Id, date));
        var exception = new CalendarException
        {
            Date = date,
            IsWorkingDay = true,
            Name = "Робоча неділя групи",
            CourseId = model.Course.Id,
            GroupId = model.FirstGroup.Id
        };
        fixture.Db.CalendarExceptions.Add(exception);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminConfigController(fixture.Db).CalendarDelete(exception.Id);

        Assert.IsType<NoContentResult>(result);
        fixture.Db.ChangeTracker.Clear();
        Assert.False(await fixture.Db.CalendarExceptions.AnyAsync(item => item.Id == exception.Id));
        Assert.True(await fixture.Db.TeacherDraftItems.AnyAsync(item => item.GroupId == model.FirstGroup.Id && item.Date == date));
    }

    [Fact]
    public async Task Calendar_delete_allows_removing_course_override_when_group_override_keeps_placement_working()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);
        var date = new DateOnly(2026, 9, 6);
        fixture.Db.ScheduleItems.Add(CreateScheduleItem(model, model.FirstGroup.Id, date));
        var courseException = new CalendarException
        {
            Date = date,
            IsWorkingDay = true,
            Name = "Робоча неділя курсу",
            CourseId = model.Course.Id
        };
        var groupException = new CalendarException
        {
            Date = date,
            IsWorkingDay = true,
            Name = "Робоча неділя групи",
            CourseId = model.Course.Id,
            GroupId = model.FirstGroup.Id
        };
        fixture.Db.CalendarExceptions.AddRange(courseException, groupException);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await new AdminConfigController(fixture.Db).CalendarDelete(courseException.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.False(await fixture.Db.CalendarExceptions.AnyAsync(item => item.Id == courseException.Id));
        Assert.True(await fixture.Db.CalendarExceptions.AnyAsync(item => item.Id == groupException.Id));
    }

    [Fact]
    public void Time_slot_sequence_rules_validate_inactive_rows_and_derive_order_from_position()
    {
        var invalid = TimeSlotSequenceRules.Validate(
            [
                EditorSlot("09:00", "10:00", isActive: false, sortOrder: 99),
                EditorSlot("09:30", "10:30", isActive: false, sortOrder: 5)
            ],
            dayOfWeek: null);

        Assert.False(invalid.IsValid);
        Assert.Contains(invalid.Errors, error => error.Contains("перетинаються", StringComparison.Ordinal));
        Assert.Equal([1, 2], invalid.Slots.Select(slot => slot.SortOrder).ToArray());
    }

    [Fact]
    public void Time_slot_sequence_rules_reject_null_rows_without_throwing()
    {
        var result = TimeSlotSequenceRules.Validate([null!], dayOfWeek: null);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("не може бути порожнім", StringComparison.Ordinal));
    }

    [Fact]
    public void Time_slot_editor_request_defaults_and_lunch_mode_use_explicit_json_contract()
    {
        var request = new TimeSlotSequenceApplyRequestDto();

        Assert.True(request.ApplySlots);
        Assert.Equal(TimeSlotLunchMutationMode.Unchanged, request.LunchMutation);
        Assert.Equal(
            "\"Remove\"",
            JsonSerializer.Serialize(TimeSlotLunchMutationMode.Remove));
    }

    [Fact]
    public async Task Time_slot_editor_course_day_materializes_global_week_before_override()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var course = new Course { Name = "Курс без власного графіка", DurationWeeks = 12 };
        fixture.Db.Courses.Add(course);
        fixture.Db.TimeSlots.AddRange(
            EntitySlot(null, null, "09:00", "10:00"),
            EntitySlot(null, DayOfWeek.Tuesday, "11:00", "12:00"));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var service = new TimeSlotEditorService(fixture.Db);

        var context = AssertSuccess(await service.GetContextAsync(
            TimeSlotEditorTargetMode.Course,
            course.Id,
            (int)DayOfWeek.Monday));
        var request = EditorRequest(
            TimeSlotEditorTargetMode.Course,
            course.Id,
            (int)DayOfWeek.Monday,
            context.CurrentRevision,
            [EditorSlot("10:00", "11:00")]);
        var preview = AssertSuccess(await service.PreviewAsync(request));
        Assert.Equal(1, preview.MaterializedCourseCount);
        request.PreviewToken = preview.PreviewToken;

        var applied = AssertSuccess(await service.ApplyAsync(request));

        Assert.False(applied.NoChanges);
        fixture.Db.ChangeTracker.Clear();
        var courseSlots = await fixture.Db.TimeSlots
            .AsNoTracking()
            .Where(slot => slot.CourseId == course.Id)
            .ToListAsync();
        Assert.Equal(3, courseSlots.Count);
        Assert.Equal(new TimeOnly(10, 0), Assert.Single(courseSlots, slot => slot.DayOfWeek == DayOfWeek.Monday).Start);
        Assert.Equal(new TimeOnly(11, 0), Assert.Single(courseSlots, slot => slot.DayOfWeek == DayOfWeek.Tuesday).Start);
        Assert.Equal(new TimeOnly(9, 0), Assert.Single(courseSlots, slot => slot.DayOfWeek == null).Start);
    }

    [Fact]
    public async Task Time_slot_editor_all_courses_preserves_existing_tuesday_override_when_applying_monday()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var overridden = new Course { Name = "Курс із вівторком", DurationWeeks = 12 };
        var inherited = new Course { Name = "Курс зі спільним графіком", DurationWeeks = 12 };
        fixture.Db.Courses.AddRange(overridden, inherited);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.TimeSlots.AddRange(
            EntitySlot(null, null, "09:00", "10:00"),
            EntitySlot(null, DayOfWeek.Tuesday, "11:00", "12:00"),
            EntitySlot(overridden.Id, DayOfWeek.Tuesday, "13:00", "14:00"));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var service = new TimeSlotEditorService(fixture.Db);

        var context = AssertSuccess(await service.GetContextAsync(
            TimeSlotEditorTargetMode.AllCourses,
            null,
            (int)DayOfWeek.Monday));
        var request = EditorRequest(
            TimeSlotEditorTargetMode.AllCourses,
            null,
            (int)DayOfWeek.Monday,
            context.CurrentRevision,
            [EditorSlot("10:00", "11:00")]);
        var preview = AssertSuccess(await service.PreviewAsync(request));
        Assert.Equal(2, preview.AffectedCourseCount);
        Assert.Equal(0, preview.CourseOverridesToReplace);
        Assert.Equal(0, preview.MaterializedCourseCount);
        request.PreviewToken = preview.PreviewToken;

        AssertSuccess(await service.ApplyAsync(request));

        fixture.Db.ChangeTracker.Clear();
        var stored = await fixture.Db.TimeSlots.AsNoTracking().ToListAsync();
        Assert.Equal(5, stored.Count);
        var overriddenMonday = TimeSlotsResolver.ResolveForDay(
            stored,
            overridden.Id,
            DayOfWeek.Monday,
            activeOnly: false);
        var overriddenTuesday = TimeSlotsResolver.ResolveForDay(
            stored,
            overridden.Id,
            DayOfWeek.Tuesday,
            activeOnly: false);
        var inheritedMonday = TimeSlotsResolver.ResolveForDay(
            stored,
            inherited.Id,
            DayOfWeek.Monday,
            activeOnly: false);
        var inheritedTuesday = TimeSlotsResolver.ResolveForDay(
            stored,
            inherited.Id,
            DayOfWeek.Tuesday,
            activeOnly: false);
        Assert.Equal(new TimeOnly(10, 0), Assert.Single(overriddenMonday.Slots).Start);
        Assert.Equal(new TimeOnly(13, 0), Assert.Single(overriddenTuesday.Slots).Start);
        Assert.Equal(new TimeOnly(10, 0), Assert.Single(inheritedMonday.Slots).Start);
        Assert.Equal(new TimeOnly(11, 0), Assert.Single(inheritedTuesday.Slots).Start);
        Assert.DoesNotContain(stored, slot => slot.CourseId == inherited.Id);
    }

    [Fact]
    public async Task Time_slot_editor_clear_missing_course_override_is_noop_without_materialization()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var course = new Course { Name = "Курс-спадкоємець", DurationWeeks = 12 };
        fixture.Db.Courses.Add(course);
        fixture.Db.TimeSlots.Add(EntitySlot(null, null, "09:00", "10:00"));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var service = new TimeSlotEditorService(fixture.Db);
        var context = AssertSuccess(await service.GetContextAsync(
            TimeSlotEditorTargetMode.Course,
            course.Id,
            (int)DayOfWeek.Monday));
        var request = EditorRequest(
            TimeSlotEditorTargetMode.Course,
            course.Id,
            (int)DayOfWeek.Monday,
            context.CurrentRevision,
            []);
        request.Clear = true;

        var preview = AssertSuccess(await service.PreviewAsync(request));
        Assert.True(preview.NoChanges);
        Assert.Equal(0, preview.MaterializedCourseCount);
        request.PreviewToken = preview.PreviewToken;
        var applied = AssertSuccess(await service.ApplyAsync(request));

        Assert.True(applied.NoChanges);
        Assert.False(await fixture.Db.TimeSlots.AnyAsync(slot => slot.CourseId == course.Id));
    }

    [Fact]
    public async Task Time_slot_editor_rejects_day_scoped_reset_to_global()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var course = new Course { Name = "Курс скидання", DurationWeeks = 12 };
        fixture.Db.Courses.Add(course);
        await fixture.Db.SaveChangesAsync();
        var service = new TimeSlotEditorService(fixture.Db);
        var context = AssertSuccess(await service.GetContextAsync(
            TimeSlotEditorTargetMode.Course,
            course.Id,
            (int)DayOfWeek.Monday));
        var request = EditorRequest(
            TimeSlotEditorTargetMode.Course,
            course.Id,
            (int)DayOfWeek.Monday,
            context.CurrentRevision,
            []);
        request.ResetCourseToGlobal = true;
        request.ApplySlots = false;

        var outcome = await service.PreviewAsync(request);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TimeSlotEditorFailureKind.Validation, outcome.Failure!.Kind);
        Assert.Contains("Усі дні", outcome.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Time_slot_editor_rejects_true_stale_preview_when_current_plan_would_change_state()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var course = new Course { Name = "Курс конкурентності", DurationWeeks = 12 };
        fixture.Db.Courses.Add(course);
        fixture.Db.TimeSlots.Add(EntitySlot(null, null, "09:00", "10:00"));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var service = new TimeSlotEditorService(fixture.Db);
        var context = AssertSuccess(await service.GetContextAsync(
            TimeSlotEditorTargetMode.AllCourses,
            null,
            null));
        var request = EditorRequest(
            TimeSlotEditorTargetMode.AllCourses,
            null,
            null,
            context.CurrentRevision,
            [EditorSlot("09:00", "10:00")]);
        var preview = AssertSuccess(await service.PreviewAsync(request));
        request.PreviewToken = preview.PreviewToken;
        await fixture.Db.TimeSlots
            .Where(slot => slot.CourseId == null && slot.DayOfWeek == null)
            .ExecuteDeleteAsync();
        fixture.Db.TimeSlots.Add(EntitySlot(null, null, "11:00", "12:00"));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var outcome = await service.ApplyAsync(request);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TimeSlotEditorFailureKind.Stale, outcome.Failure!.Kind);
    }

    [Fact]
    public async Task Time_slot_editor_revision_detects_second_precision_changes()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var course = new Course { Name = "Курс секундної точності", DurationWeeks = 12 };
        fixture.Db.Courses.Add(course);
        fixture.Db.TimeSlots.Add(new TimeSlot
        {
            CourseId = null,
            DayOfWeek = null,
            Start = new TimeOnly(9, 0, 30),
            End = new TimeOnly(10, 0, 30),
            SortOrder = 1,
            IsActive = true
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var service = new TimeSlotEditorService(fixture.Db);
        var context = AssertSuccess(await service.GetContextAsync(
            TimeSlotEditorTargetMode.AllCourses,
            null,
            null));
        var normalizationRequest = EditorRequest(
            TimeSlotEditorTargetMode.AllCourses,
            null,
            null,
            context.CurrentRevision,
            [EditorSlot("09:00", "10:00")]);
        var normalizationPreview = AssertSuccess(await service.PreviewAsync(normalizationRequest));
        Assert.False(normalizationPreview.NoChanges);

        var staleRequest = EditorRequest(
            TimeSlotEditorTargetMode.AllCourses,
            null,
            null,
            context.CurrentRevision,
            [EditorSlot("11:00", "12:00")]);
        var stalePreview = AssertSuccess(await service.PreviewAsync(staleRequest));
        staleRequest.PreviewToken = stalePreview.PreviewToken;
        var stored = await fixture.Db.TimeSlots.SingleAsync();
        stored.Start = new TimeOnly(9, 0, 45);
        stored.End = new TimeOnly(10, 0, 45);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var outcome = await service.ApplyAsync(staleRequest);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TimeSlotEditorFailureKind.Stale, outcome.Failure!.Kind);
    }

    [Fact]
    public async Task Time_slot_editor_preferred_first_change_does_not_stale_slot_revision()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var course = new Course { Name = "Курс незалежного ліміту", DurationWeeks = 12 };
        fixture.Db.Courses.Add(course);
        fixture.Db.TimeSlots.Add(EntitySlot(null, null, "09:00", "10:00"));
        fixture.Db.PreferredFirstSlotLimitConfigs.Add(new PreferredFirstSlotLimitConfig
        {
            CourseId = null,
            MaxSlotOrder = 3
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var service = new TimeSlotEditorService(fixture.Db);
        var context = AssertSuccess(await service.GetContextAsync(
            TimeSlotEditorTargetMode.Course,
            course.Id,
            null));
        var request = EditorRequest(
            TimeSlotEditorTargetMode.Course,
            course.Id,
            null,
            context.CurrentRevision,
            [EditorSlot("10:00", "11:00")]);
        var preferred = await fixture.Db.PreferredFirstSlotLimitConfigs.SingleAsync();
        preferred.MaxSlotOrder = 4;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var preview = await service.PreviewAsync(request);

        Assert.True(preview.IsSuccess, preview.Failure?.Message);
    }

    [Fact]
    public async Task Time_slot_editor_all_courses_replaces_course_lunch_overrides_with_one_global_lunch()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var firstCourse = new Course { Name = "Курс із власним обідом", DurationWeeks = 12 };
        var secondCourse = new Course { Name = "Курс зі спільним обідом", DurationWeeks = 12 };
        fixture.Db.Courses.AddRange(firstCourse, secondCourse);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.TimeSlots.AddRange(
            EntitySlot(null, null, "09:00", "10:00", sortOrder: 1),
            EntitySlot(null, null, "11:00", "12:00", sortOrder: 2));
        fixture.Db.LunchConfigs.AddRange(
            new LunchConfig
            {
                CourseId = null,
                Start = new TimeOnly(11, 0),
                End = new TimeOnly(12, 0)
            },
            new LunchConfig
            {
                CourseId = firstCourse.Id,
                Start = new TimeOnly(10, 0),
                End = new TimeOnly(11, 0)
            });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var service = new TimeSlotEditorService(fixture.Db);
        var context = AssertSuccess(await service.GetContextAsync(
            TimeSlotEditorTargetMode.AllCourses,
            null,
            null));
        var request = EditorRequest(
            TimeSlotEditorTargetMode.AllCourses,
            null,
            null,
            context.CurrentRevision,
            [
                EditorSlot("09:00", "10:00", sortOrder: 1),
                EditorSlot("12:00", "13:00", isLunch: true, sortOrder: 2)
            ]);
        request.LunchMutation = TimeSlotLunchMutationMode.Set;
        var preview = AssertSuccess(await service.PreviewAsync(request));
        Assert.Equal(0, preview.MaterializedCourseCount);
        request.PreviewToken = preview.PreviewToken;

        AssertSuccess(await service.ApplyAsync(request));

        fixture.Db.ChangeTracker.Clear();
        var lunch = Assert.Single(await fixture.Db.LunchConfigs.AsNoTracking().ToListAsync());
        Assert.Null(lunch.CourseId);
        Assert.Equal(new TimeOnly(12, 0), lunch.Start);
        Assert.Equal(new TimeOnly(13, 0), lunch.End);
        var storedSlots = await fixture.Db.TimeSlots.AsNoTracking().ToListAsync();
        var storedLunches = await fixture.Db.LunchConfigs.AsNoTracking().ToListAsync();
        var resolved = TimeSlotsResolver.ResolveForDay(
            storedSlots,
            firstCourse.Id,
            DayOfWeek.Monday,
            storedLunches,
            activeOnly: false);
        Assert.Equal(new TimeOnly(9, 0), Assert.Single(resolved.Slots).Start);
        Assert.DoesNotContain(storedSlots, slot => slot.CourseId == firstCourse.Id);
        Assert.DoesNotContain(storedSlots, slot => slot.CourseId == secondCourse.Id);
    }

    [Fact]
    public async Task Time_slot_editor_course_slots_preserve_unmatched_explicit_lunch_when_unchanged()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var course = new Course { Name = "Курс зі збереженим обідом", DurationWeeks = 12 };
        fixture.Db.Courses.Add(course);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.TimeSlots.Add(EntitySlot(null, null, "09:00", "10:00"));
        fixture.Db.LunchConfigs.Add(new LunchConfig
        {
            CourseId = course.Id,
            Start = new TimeOnly(14, 0),
            End = new TimeOnly(15, 0)
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var service = new TimeSlotEditorService(fixture.Db);
        var context = AssertSuccess(await service.GetContextAsync(
            TimeSlotEditorTargetMode.Course,
            course.Id,
            null));
        var request = EditorRequest(
            TimeSlotEditorTargetMode.Course,
            course.Id,
            null,
            context.CurrentRevision,
            [EditorSlot("10:00", "11:00")]);
        var preview = AssertSuccess(await service.PreviewAsync(request));
        request.PreviewToken = preview.PreviewToken;

        AssertSuccess(await service.ApplyAsync(request));

        fixture.Db.ChangeTracker.Clear();
        var lunch = Assert.Single(await fixture.Db.LunchConfigs.AsNoTracking().ToListAsync());
        Assert.Equal(course.Id, lunch.CourseId);
        Assert.Equal(new TimeOnly(14, 0), lunch.Start);
        Assert.Equal(new TimeOnly(15, 0), lunch.End);
    }

    [Fact]
    public async Task Time_slot_editor_course_lunch_remove_does_not_materialize_slots()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var course = new Course { Name = "Курс видалення обіду", DurationWeeks = 12 };
        fixture.Db.Courses.Add(course);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.TimeSlots.Add(EntitySlot(null, null, "09:00", "10:00"));
        fixture.Db.LunchConfigs.AddRange(
            new LunchConfig
            {
                CourseId = null,
                Start = new TimeOnly(12, 0),
                End = new TimeOnly(13, 0)
            },
            new LunchConfig
            {
                CourseId = course.Id,
                Start = new TimeOnly(13, 0),
                End = new TimeOnly(14, 0)
            });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var service = new TimeSlotEditorService(fixture.Db);
        var context = AssertSuccess(await service.GetContextAsync(
            TimeSlotEditorTargetMode.Course,
            course.Id,
            null));
        var request = EditorRequest(
            TimeSlotEditorTargetMode.Course,
            course.Id,
            null,
            context.CurrentRevision,
            []);
        request.ApplySlots = false;
        request.LunchMutation = TimeSlotLunchMutationMode.Remove;
        var preview = AssertSuccess(await service.PreviewAsync(request));
        request.PreviewToken = preview.PreviewToken;

        AssertSuccess(await service.ApplyAsync(request));

        fixture.Db.ChangeTracker.Clear();
        var lunch = Assert.Single(await fixture.Db.LunchConfigs.AsNoTracking().ToListAsync());
        Assert.Null(lunch.CourseId);
        Assert.False(await fixture.Db.TimeSlots.AnyAsync(slot => slot.CourseId == course.Id));
    }

    [Fact]
    public async Task Time_slot_editor_course_lunch_set_only_does_not_materialize_slots()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var course = new Course { Name = "Курс окремого обіду", DurationWeeks = 12 };
        fixture.Db.Courses.Add(course);
        fixture.Db.TimeSlots.Add(EntitySlot(null, null, "09:00", "10:00"));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var service = new TimeSlotEditorService(fixture.Db);
        var context = AssertSuccess(await service.GetContextAsync(
            TimeSlotEditorTargetMode.Course,
            course.Id,
            null));
        var request = EditorRequest(
            TimeSlotEditorTargetMode.Course,
            course.Id,
            null,
            context.CurrentRevision,
            []);
        request.ApplySlots = false;
        request.LunchMutation = TimeSlotLunchMutationMode.Set;
        request.LunchSlot = EditorSlot("12:00", "13:00", isLunch: true);
        var preview = AssertSuccess(await service.PreviewAsync(request));
        request.PreviewToken = preview.PreviewToken;

        AssertSuccess(await service.ApplyAsync(request));

        fixture.Db.ChangeTracker.Clear();
        var lunch = Assert.Single(await fixture.Db.LunchConfigs.AsNoTracking().ToListAsync());
        Assert.Equal(course.Id, lunch.CourseId);
        Assert.Equal(new TimeOnly(12, 0), lunch.Start);
        Assert.False(await fixture.Db.TimeSlots.AnyAsync(slot => slot.CourseId == course.Id));
    }

    [Fact]
    public async Task Time_slot_editor_all_courses_unchanged_lunch_preserves_global_and_course_rows()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var course = new Course { Name = "Курс незмінного обіду", DurationWeeks = 12 };
        fixture.Db.Courses.Add(course);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.TimeSlots.Add(EntitySlot(null, null, "09:00", "10:00"));
        fixture.Db.LunchConfigs.AddRange(
            new LunchConfig
            {
                CourseId = null,
                Start = new TimeOnly(12, 0),
                End = new TimeOnly(13, 0)
            },
            new LunchConfig
            {
                CourseId = course.Id,
                Start = new TimeOnly(13, 0),
                End = new TimeOnly(14, 0)
            });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var service = new TimeSlotEditorService(fixture.Db);
        var context = AssertSuccess(await service.GetContextAsync(
            TimeSlotEditorTargetMode.AllCourses,
            null,
            null));
        var request = EditorRequest(
            TimeSlotEditorTargetMode.AllCourses,
            null,
            null,
            context.CurrentRevision,
            [EditorSlot("10:00", "11:00")]);
        var preview = AssertSuccess(await service.PreviewAsync(request));
        request.PreviewToken = preview.PreviewToken;

        AssertSuccess(await service.ApplyAsync(request));

        fixture.Db.ChangeTracker.Clear();
        var lunches = await fixture.Db.LunchConfigs.AsNoTracking().OrderBy(row => row.CourseId).ToListAsync();
        Assert.Equal(2, lunches.Count);
        Assert.Contains(lunches, row => row.CourseId == null && row.Start == new TimeOnly(12, 0));
        Assert.Contains(lunches, row => row.CourseId == course.Id && row.Start == new TimeOnly(13, 0));
    }

    [Fact]
    public async Task Time_slot_editor_all_courses_lunch_remove_deletes_every_lunch_without_slot_materialization()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var course = new Course { Name = "Курс спільного видалення обіду", DurationWeeks = 12 };
        fixture.Db.Courses.Add(course);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.TimeSlots.Add(EntitySlot(null, null, "09:00", "10:00"));
        fixture.Db.LunchConfigs.AddRange(
            new LunchConfig
            {
                CourseId = null,
                Start = new TimeOnly(12, 0),
                End = new TimeOnly(13, 0)
            },
            new LunchConfig
            {
                CourseId = course.Id,
                Start = new TimeOnly(13, 0),
                End = new TimeOnly(14, 0)
            });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var service = new TimeSlotEditorService(fixture.Db);
        var context = AssertSuccess(await service.GetContextAsync(
            TimeSlotEditorTargetMode.AllCourses,
            null,
            null));
        var request = EditorRequest(
            TimeSlotEditorTargetMode.AllCourses,
            null,
            null,
            context.CurrentRevision,
            []);
        request.ApplySlots = false;
        request.LunchMutation = TimeSlotLunchMutationMode.Remove;
        var preview = AssertSuccess(await service.PreviewAsync(request));
        request.PreviewToken = preview.PreviewToken;

        AssertSuccess(await service.ApplyAsync(request));

        fixture.Db.ChangeTracker.Clear();
        Assert.Empty(await fixture.Db.LunchConfigs.AsNoTracking().ToListAsync());
        Assert.False(await fixture.Db.TimeSlots.AnyAsync(slot => slot.CourseId == course.Id));
        Assert.Equal(new TimeOnly(9, 0), await fixture.Db.TimeSlots.Select(slot => slot.Start).SingleAsync());
    }

    [Fact]
    public async Task Time_slot_editor_lunch_set_collapses_duplicate_global_rows_to_one()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        fixture.Db.LunchConfigs.AddRange(
            new LunchConfig
            {
                CourseId = null,
                Start = new TimeOnly(11, 0),
                End = new TimeOnly(12, 0)
            },
            new LunchConfig
            {
                CourseId = null,
                Start = new TimeOnly(13, 0),
                End = new TimeOnly(14, 0)
            });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var service = new TimeSlotEditorService(fixture.Db);
        var context = AssertSuccess(await service.GetContextAsync(
            TimeSlotEditorTargetMode.AllCourses,
            null,
            null));
        var request = EditorRequest(
            TimeSlotEditorTargetMode.AllCourses,
            null,
            null,
            context.CurrentRevision,
            []);
        request.ApplySlots = false;
        request.LunchMutation = TimeSlotLunchMutationMode.Set;
        request.LunchSlot = EditorSlot("12:00", "13:00", isLunch: true);
        var preview = AssertSuccess(await service.PreviewAsync(request));
        request.PreviewToken = preview.PreviewToken;

        AssertSuccess(await service.ApplyAsync(request));

        fixture.Db.ChangeTracker.Clear();
        var lunch = Assert.Single(await fixture.Db.LunchConfigs.AsNoTracking().ToListAsync());
        Assert.Null(lunch.CourseId);
        Assert.Equal(new TimeOnly(12, 0), lunch.Start);
        Assert.Equal(new TimeOnly(13, 0), lunch.End);
    }

    [Fact]
    public async Task Time_slot_editor_day_clear_removes_only_exact_override()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var course = new Course { Name = "Курс очищення понеділка", DurationWeeks = 12 };
        fixture.Db.Courses.Add(course);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.TimeSlots.AddRange(
            EntitySlot(course.Id, null, "09:00", "10:00"),
            EntitySlot(course.Id, DayOfWeek.Monday, "10:00", "11:00"),
            EntitySlot(course.Id, DayOfWeek.Tuesday, "11:00", "12:00"));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var service = new TimeSlotEditorService(fixture.Db);
        var context = AssertSuccess(await service.GetContextAsync(
            TimeSlotEditorTargetMode.Course,
            course.Id,
            (int)DayOfWeek.Monday));
        Assert.True(context.HasDayOverride);
        var request = EditorRequest(
            TimeSlotEditorTargetMode.Course,
            course.Id,
            (int)DayOfWeek.Monday,
            context.CurrentRevision,
            []);
        request.Clear = true;
        var preview = AssertSuccess(await service.PreviewAsync(request));
        request.PreviewToken = preview.PreviewToken;

        AssertSuccess(await service.ApplyAsync(request));

        fixture.Db.ChangeTracker.Clear();
        var stored = await fixture.Db.TimeSlots.AsNoTracking().ToListAsync();
        Assert.DoesNotContain(stored, slot => slot.CourseId == course.Id && slot.DayOfWeek == DayOfWeek.Monday);
        Assert.Contains(stored, slot => slot.CourseId == course.Id && slot.DayOfWeek == null);
        Assert.Contains(stored, slot => slot.CourseId == course.Id && slot.DayOfWeek == DayOfWeek.Tuesday);
        var monday = TimeSlotsResolver.ResolveForDay(stored, course.Id, DayOfWeek.Monday, activeOnly: false);
        Assert.Equal(new TimeOnly(9, 0), Assert.Single(monday.Slots).Start);
    }

    [Fact]
    public async Task Time_slot_editor_all_courses_day_clear_removes_only_exact_global_and_course_overrides()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var course = new Course { Name = "Курс очищення спільного понеділка", DurationWeeks = 12 };
        fixture.Db.Courses.Add(course);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.TimeSlots.AddRange(
            EntitySlot(null, null, "09:00", "10:00"),
            EntitySlot(null, DayOfWeek.Monday, "10:00", "11:00"),
            EntitySlot(null, DayOfWeek.Tuesday, "11:00", "12:00"),
            EntitySlot(course.Id, null, "12:00", "13:00"),
            EntitySlot(course.Id, DayOfWeek.Monday, "13:00", "14:00"),
            EntitySlot(course.Id, DayOfWeek.Tuesday, "14:00", "15:00"));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var service = new TimeSlotEditorService(fixture.Db);
        var context = AssertSuccess(await service.GetContextAsync(
            TimeSlotEditorTargetMode.AllCourses,
            null,
            (int)DayOfWeek.Monday));
        var request = EditorRequest(
            TimeSlotEditorTargetMode.AllCourses,
            null,
            (int)DayOfWeek.Monday,
            context.CurrentRevision,
            []);
        request.Clear = true;
        var preview = AssertSuccess(await service.PreviewAsync(request));
        Assert.Equal(1, preview.CourseOverridesToReplace);
        request.PreviewToken = preview.PreviewToken;

        AssertSuccess(await service.ApplyAsync(request));

        fixture.Db.ChangeTracker.Clear();
        var stored = await fixture.Db.TimeSlots.AsNoTracking().ToListAsync();
        Assert.DoesNotContain(stored, slot => slot.DayOfWeek == DayOfWeek.Monday);
        Assert.Contains(stored, slot => slot.CourseId == null && slot.DayOfWeek == null);
        Assert.Contains(stored, slot => slot.CourseId == null && slot.DayOfWeek == DayOfWeek.Tuesday);
        Assert.Contains(stored, slot => slot.CourseId == course.Id && slot.DayOfWeek == null);
        Assert.Contains(stored, slot => slot.CourseId == course.Id && slot.DayOfWeek == DayOfWeek.Tuesday);
    }

    [Fact]
    public async Task Time_slot_editor_all_courses_day_skips_redundant_course_override()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var course = new Course { Name = "Курс без зайвого понеділка", DurationWeeks = 12 };
        fixture.Db.Courses.Add(course);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.TimeSlots.AddRange(
            EntitySlot(null, null, "09:00", "10:00"),
            EntitySlot(course.Id, null, "10:00", "11:00"),
            EntitySlot(course.Id, DayOfWeek.Tuesday, "12:00", "13:00"));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var service = new TimeSlotEditorService(fixture.Db);
        var context = AssertSuccess(await service.GetContextAsync(
            TimeSlotEditorTargetMode.AllCourses,
            null,
            (int)DayOfWeek.Monday));
        var request = EditorRequest(
            TimeSlotEditorTargetMode.AllCourses,
            null,
            (int)DayOfWeek.Monday,
            context.CurrentRevision,
            [EditorSlot("10:00", "11:00")]);
        var preview = AssertSuccess(await service.PreviewAsync(request));
        request.PreviewToken = preview.PreviewToken;

        AssertSuccess(await service.ApplyAsync(request));

        fixture.Db.ChangeTracker.Clear();
        Assert.False(await fixture.Db.TimeSlots.AnyAsync(slot =>
            slot.CourseId == course.Id && slot.DayOfWeek == DayOfWeek.Monday));
        Assert.True(await fixture.Db.TimeSlots.AnyAsync(slot =>
            slot.CourseId == course.Id && slot.DayOfWeek == DayOfWeek.Tuesday && slot.Start == new TimeOnly(12, 0)));
    }

    [Fact]
    public async Task Time_slot_editor_context_reports_day_overrides_and_resolved_global_fallback()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var course = new Course { Name = "Курс контексту дня", DurationWeeks = 12 };
        fixture.Db.Courses.Add(course);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.TimeSlots.AddRange(
            EntitySlot(null, null, "09:00", "10:00"),
            EntitySlot(course.Id, null, "10:00", "11:00"),
            EntitySlot(course.Id, DayOfWeek.Tuesday, "11:00", "12:00"));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var service = new TimeSlotEditorService(fixture.Db);

        var courseMonday = AssertSuccess(await service.GetContextAsync(
            TimeSlotEditorTargetMode.Course,
            course.Id,
            (int)DayOfWeek.Monday));
        var courseTuesday = AssertSuccess(await service.GetContextAsync(
            TimeSlotEditorTargetMode.Course,
            course.Id,
            (int)DayOfWeek.Tuesday));
        var allMonday = AssertSuccess(await service.GetContextAsync(
            TimeSlotEditorTargetMode.AllCourses,
            null,
            (int)DayOfWeek.Monday));
        var allTuesday = AssertSuccess(await service.GetContextAsync(
            TimeSlotEditorTargetMode.AllCourses,
            null,
            (int)DayOfWeek.Tuesday));

        Assert.False(courseMonday.HasDayOverride);
        Assert.Empty(courseMonday.ExplicitSlots);
        Assert.Equal(new TimeOnly(10, 0), TimeOnly.Parse(Assert.Single(courseMonday.EffectiveSlots).Start));
        Assert.Equal(new TimeOnly(9, 0), TimeOnly.Parse(Assert.Single(courseMonday.GlobalSlots).Start));
        Assert.True(courseTuesday.HasDayOverride);
        Assert.False(allMonday.HasDayOverride);
        Assert.Empty(allMonday.ExplicitSlots);
        Assert.Equal(new TimeOnly(9, 0), TimeOnly.Parse(Assert.Single(allMonday.GlobalSlots).Start));
        Assert.True(allTuesday.HasDayOverride);
        Assert.Empty(allTuesday.ExplicitSlots);
        Assert.Equal(new TimeOnly(9, 0), TimeOnly.Parse(Assert.Single(allTuesday.GlobalSlots).Start));
    }

    [Fact]
    public async Task Time_slot_editor_rejects_nonempty_slots_when_apply_slots_is_false()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var course = new Course { Name = "Курс суворого наміру", DurationWeeks = 12 };
        fixture.Db.Courses.Add(course);
        await fixture.Db.SaveChangesAsync();
        var service = new TimeSlotEditorService(fixture.Db);
        var context = AssertSuccess(await service.GetContextAsync(
            TimeSlotEditorTargetMode.Course,
            course.Id,
            null));
        var request = EditorRequest(
            TimeSlotEditorTargetMode.Course,
            course.Id,
            null,
            context.CurrentRevision,
            [EditorSlot("12:00", "13:00", isLunch: true)]);
        request.ApplySlots = false;
        request.LunchMutation = TimeSlotLunchMutationMode.Set;

        var outcome = await service.PreviewAsync(request);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TimeSlotEditorFailureKind.Validation, outcome.Failure!.Kind);
    }

    [Fact]
    public async Task Time_slot_editor_rejects_separate_lunch_slot_when_slots_are_applied()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var course = new Course { Name = "Курс обходу обіднього слота", DurationWeeks = 12 };
        fixture.Db.Courses.Add(course);
        await fixture.Db.SaveChangesAsync();
        var service = new TimeSlotEditorService(fixture.Db);
        var context = AssertSuccess(await service.GetContextAsync(
            TimeSlotEditorTargetMode.Course,
            course.Id,
            null));
        var request = EditorRequest(
            TimeSlotEditorTargetMode.Course,
            course.Id,
            null,
            context.CurrentRevision,
            [EditorSlot("09:00", "10:00")]);
        request.LunchMutation = TimeSlotLunchMutationMode.Set;
        request.LunchSlot = EditorSlot("12:00", "13:00", isLunch: true);

        var outcome = await service.PreviewAsync(request);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TimeSlotEditorFailureKind.Validation, outcome.Failure!.Kind);
        Assert.Contains("без застосування слотів", outcome.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Time_slot_editor_rejects_lunch_mutation_for_day_scope()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var course = new Course { Name = "Курс денного обіду", DurationWeeks = 12 };
        fixture.Db.Courses.Add(course);
        await fixture.Db.SaveChangesAsync();
        var service = new TimeSlotEditorService(fixture.Db);
        var context = AssertSuccess(await service.GetContextAsync(
            TimeSlotEditorTargetMode.Course,
            course.Id,
            (int)DayOfWeek.Monday));
        var request = EditorRequest(
            TimeSlotEditorTargetMode.Course,
            course.Id,
            (int)DayOfWeek.Monday,
            context.CurrentRevision,
            [EditorSlot("09:00", "10:00")]);
        request.LunchMutation = TimeSlotLunchMutationMode.Set;
        request.LunchSlot = EditorSlot("12:00", "13:00", isLunch: true);

        var outcome = await service.PreviewAsync(request);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TimeSlotEditorFailureKind.Validation, outcome.Failure!.Kind);
        Assert.Contains("Усі дні", outcome.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Time_slot_editor_rejects_request_without_any_mutation_intent()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var course = new Course { Name = "Курс порожнього запиту", DurationWeeks = 12 };
        fixture.Db.Courses.Add(course);
        await fixture.Db.SaveChangesAsync();
        var service = new TimeSlotEditorService(fixture.Db);
        var context = AssertSuccess(await service.GetContextAsync(
            TimeSlotEditorTargetMode.Course,
            course.Id,
            null));
        var request = EditorRequest(
            TimeSlotEditorTargetMode.Course,
            course.Id,
            null,
            context.CurrentRevision,
            []);
        request.ApplySlots = false;

        var outcome = await service.PreviewAsync(request);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TimeSlotEditorFailureKind.Validation, outcome.Failure!.Kind);
        Assert.Contains("не містить змін", outcome.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Time_slot_editor_repeated_literal_apply_with_same_revision_and_token_is_noop()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var course = new Course { Name = "Курс повтору", DurationWeeks = 12 };
        fixture.Db.Courses.Add(course);
        fixture.Db.TimeSlots.Add(EntitySlot(null, null, "09:00", "10:00"));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var service = new TimeSlotEditorService(fixture.Db);

        var firstContext = AssertSuccess(await service.GetContextAsync(
            TimeSlotEditorTargetMode.AllCourses,
            null,
            null));
        var firstRequest = EditorRequest(
            TimeSlotEditorTargetMode.AllCourses,
            null,
            null,
            firstContext.CurrentRevision,
            [EditorSlot("10:00", "11:00")]);
        var firstPreview = AssertSuccess(await service.PreviewAsync(firstRequest));
        firstRequest.PreviewToken = firstPreview.PreviewToken;
        Assert.False(AssertSuccess(await service.ApplyAsync(firstRequest)).NoChanges);

        fixture.Db.ChangeTracker.Clear();
        var secondApply = AssertSuccess(await service.ApplyAsync(firstRequest));

        Assert.True(secondApply.NoChanges);
    }

    [Fact]
    public async Task Time_slot_editor_literal_replay_rejects_intervening_course_override()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var course = new Course { Name = "Курс втручання між повторами", DurationWeeks = 12 };
        fixture.Db.Courses.Add(course);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.TimeSlots.AddRange(
            EntitySlot(null, null, "09:00", "10:00"),
            EntitySlot(course.Id, null, "10:00", "11:00"));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var service = new TimeSlotEditorService(fixture.Db);
        var context = AssertSuccess(await service.GetContextAsync(
            TimeSlotEditorTargetMode.AllCourses,
            null,
            (int)DayOfWeek.Monday));
        var request = EditorRequest(
            TimeSlotEditorTargetMode.AllCourses,
            null,
            (int)DayOfWeek.Monday,
            context.CurrentRevision,
            [EditorSlot("10:00", "11:00")]);
        var preview = AssertSuccess(await service.PreviewAsync(request));
        request.PreviewToken = preview.PreviewToken;
        AssertSuccess(await service.ApplyAsync(request));
        fixture.Db.ChangeTracker.Clear();
        fixture.Db.TimeSlots.Add(EntitySlot(course.Id, DayOfWeek.Tuesday, "12:00", "13:00"));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var replay = await service.ApplyAsync(request);

        Assert.False(replay.IsSuccess);
        Assert.Equal(TimeSlotEditorFailureKind.Stale, replay.Failure!.Kind);
    }

    [Fact]
    public async Task Time_slot_editor_all_courses_conflict_prevents_every_course_write()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedRoomModelAsync(20, 20, 40);
        var secondCourse = new Course { Name = "Другий курс", DurationWeeks = 12 };
        fixture.Db.Courses.Add(secondCourse);
        fixture.Db.TimeSlots.Add(EntitySlot(null, null, "09:00", "10:00"));
        fixture.Db.ScheduleItems.Add(CreateScheduleItem(
            model,
            model.FirstGroup.Id,
            new DateOnly(2026, 9, 7)));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var service = new TimeSlotEditorService(fixture.Db);
        var context = AssertSuccess(await service.GetContextAsync(
            TimeSlotEditorTargetMode.AllCourses,
            null,
            null));
        var request = EditorRequest(
            TimeSlotEditorTargetMode.AllCourses,
            null,
            null,
            context.CurrentRevision,
            [EditorSlot("10:00", "11:00")]);
        var preview = AssertSuccess(await service.PreviewAsync(request));
        Assert.Equal(1, preview.ScheduleConflictCount);
        Assert.Single(preview.ConflictSamples);
        Assert.False(preview.CanApply);
        request.PreviewToken = preview.PreviewToken;

        var action = await new AdminConfigController(fixture.Db).ApplyTimeSlotSequence(
            request,
            CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(action);
        Assert.Contains("\"code\":\"conflict\"", JsonSerializer.Serialize(conflict.Value), StringComparison.Ordinal);
        fixture.Db.ChangeTracker.Clear();
        Assert.Single(await fixture.Db.TimeSlots.AsNoTracking().ToListAsync());
        Assert.False(await fixture.Db.TimeSlots.AnyAsync(slot => slot.CourseId != null));
        Assert.Equal(new TimeOnly(9, 0), await fixture.Db.TimeSlots.Select(slot => slot.Start).SingleAsync());
    }

    private static T AssertSuccess<T>(TimeSlotEditorOutcome<T> outcome)
    {
        Assert.True(outcome.IsSuccess, outcome.Failure?.Message);
        return Assert.IsType<T>(outcome.Value);
    }

    private static TimeSlotSequenceApplyRequestDto EditorRequest(
        TimeSlotEditorTargetMode targetMode,
        int? courseId,
        int? dayOfWeek,
        string revision,
        List<TimeSlotDto> slots)
        => new()
        {
            TargetMode = targetMode,
            CourseId = courseId,
            DayOfWeek = dayOfWeek,
            CurrentRevision = revision,
            Slots = slots
        };

    private static TimeSlotDto EditorSlot(
        string start,
        string end,
        bool isActive = true,
        bool isLunch = false,
        int sortOrder = 1)
        => new()
        {
            Start = start,
            End = end,
            IsActive = isActive,
            IsLunch = isLunch,
            SortOrder = sortOrder
        };

    private static TimeSlot EntitySlot(
        int? courseId,
        DayOfWeek? day,
        string start,
        string end,
        int sortOrder = 1)
        => new()
        {
            CourseId = courseId,
            DayOfWeek = day,
            Start = TimeOnly.Parse(start),
            End = TimeOnly.Parse(end),
            SortOrder = sortOrder,
            IsActive = true
        };

    private static LessonTypeRef CreateCompatibleLessonType(
        LessonTypeRef target,
        string code,
        string name)
        => new()
        {
            Code = code,
            Name = name,
            IsActive = true,
            RequiresRoom = target.RequiresRoom,
            RequiresTeacher = target.RequiresTeacher,
            BlocksRoom = target.BlocksRoom,
            BlocksTeacher = target.BlocksTeacher,
            CountInPlan = target.CountInPlan,
            CountInLoad = target.CountInLoad,
            PreferredFirstInWeek = target.PreferredFirstInWeek
        };

    private static AutoGenJobRun CreateHistoricalJob(string jobId, string resultJson)
    {
        var now = DateTime.UtcNow;
        return new AutoGenJobRun
        {
            JobId = jobId,
            ClientPartitionKey = "merge-test",
            RequestHash = $"{jobId}-hash",
            Attempt = 1,
            Version = 1,
            Kind = 0,
            State = 2,
            Title = "Перевірка розміру JSON після об'єднання",
            CurrentStage = "completed",
            CreatedAtUtc = now,
            RangeStartDate = new DateOnly(2026, 9, 7),
            RangeEndDate = new DateOnly(2026, 9, 7),
            RequestJson = "{}",
            StatusJson = "{}",
            ResultJson = resultJson,
            UpdatedAtUtc = now
        };
    }

    private static ScheduleItem CreateScheduleItem(RoomModel model, int groupId, DateOnly date)
        => new()
        {
            Date = date,
            DayOfWeek = date.DayOfWeek,
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(10, 0),
            GroupId = groupId,
            ModuleId = model.Module.Id,
            LessonTypeId = model.LessonType.Id,
            RoomId = model.Room.Id
        };

    private static ScheduleItem CreateTravelScheduleItem(
        TravelModel model,
        int groupId,
        int roomId,
        DateOnly date,
        TimeOnly start,
        TimeOnly end,
        int? teacherId = null)
        => new()
        {
            Date = date,
            DayOfWeek = date.DayOfWeek,
            StartTime = start,
            EndTime = end,
            GroupId = groupId,
            ModuleId = model.Module.Id,
            LessonTypeId = model.LessonType.Id,
            TeacherId = teacherId,
            RoomId = roomId
        };

    private static TeacherDraftItem CreateTravelDraftItem(
        TravelModel model,
        int groupId,
        int roomId,
        DateOnly date,
        TimeOnly start,
        TimeOnly end,
        int? teacherId = null)
        => new()
        {
            Date = date,
            DayOfWeek = date.DayOfWeek,
            StartTime = start,
            EndTime = end,
            GroupId = groupId,
            ModuleId = model.Module.Id,
            LessonTypeId = model.LessonType.Id,
            TeacherId = teacherId,
            RoomId = roomId,
            Status = DraftStatus.Draft
        };

    private static TeacherDraftItem CreateDraftItem(RoomModel model, int groupId, DateOnly date)
        => new()
        {
            Date = date,
            DayOfWeek = date.DayOfWeek,
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(10, 0),
            GroupId = groupId,
            ModuleId = model.Module.Id,
            LessonTypeId = model.LessonType.Id,
            RoomId = model.Room.Id
        };

    private sealed record RoomModel(
        Course Course,
        Module Module,
        LessonTypeRef LessonType,
        Building Building,
        Room Room,
        Group FirstGroup,
        Group SecondGroup);

    private sealed record TeacherModel(
        Course Course,
        Module Module,
        LessonTypeRef LessonType,
        Group Group,
        Teacher Teacher);

    private sealed record TravelModel(
        Course Course,
        Module Module,
        LessonTypeRef LessonType,
        Building FirstBuilding,
        Building SecondBuilding,
        Room FirstRoom,
        Room SecondRoom,
        Group FirstGroup,
        Group SecondGroup,
        Teacher Teacher);

    private sealed class ModuleCourseMembershipTransactionInterceptor : DbCommandInterceptor
    {
        public IsolationLevel? ObservedIsolationLevel { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("ModuleCourses", StringComparison.Ordinal)
                && command.CommandText.Contains("CourseId", StringComparison.Ordinal)
                && command.Transaction is not null)
            {
                ObservedIsolationLevel = command.Transaction.IsolationLevel;
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private TestDatabase(SqliteConnection connection, AppDbContext db)
        {
            this.connection = connection;
            Db = db;
        }

        public AppDbContext Db { get; }

        public static async Task<TestDatabase> CreateAsync(
            bool throwOnMultipleCollectionWarning = false,
            IInterceptor? interceptor = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection);
            if (throwOnMultipleCollectionWarning)
            {
                optionsBuilder.ConfigureWarnings(warnings =>
                    warnings.Throw(RelationalEventId.MultipleCollectionIncludeWarning));
            }
            if (interceptor is not null)
            {
                optionsBuilder.AddInterceptors(interceptor);
            }
            var options = optionsBuilder.Options;
            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, db);
        }

        public async Task<RoomModel> SeedRoomModelAsync(
            int firstGroupStudents,
            int secondGroupStudents,
            int roomCapacity)
        {
            var course = new Course { Name = "Курс місткості", DurationWeeks = 52 };
            var module = new Module { Code = "МІСТ-1", Title = "Модуль місткості", Credits = 1, Course = course };
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
                CountInLoad = false
            };
            var building = new Building { Name = "Навчальний корпус" };
            var room = new Room { Name = "Аудиторія 1", Capacity = roomCapacity, Building = building };
            var firstGroup = new Group { Name = "Група 1", StudentsCount = firstGroupStudents, Course = course };
            var secondGroup = new Group { Name = "Група 2", StudentsCount = secondGroupStudents, Course = course };
            Db.AddRange(course, module, lessonType, building, room, firstGroup, secondGroup);
            await Db.SaveChangesAsync();
            return new RoomModel(course, module, lessonType, building, room, firstGroup, secondGroup);
        }

        public async Task<TeacherModel> SeedTeacherModelAsync()
        {
            var course = new Course { Name = "Курс викладача", DurationWeeks = 52 };
            var module = new Module { Code = "ВИК-1", Title = "Модуль викладача", Credits = 1, Course = course };
            var lessonType = new LessonTypeRef
            {
                Code = "TEACHER",
                Name = "Заняття з викладачем",
                IsActive = true,
                RequiresRoom = false,
                RequiresTeacher = true,
                BlocksRoom = false,
                BlocksTeacher = true,
                CountInPlan = true,
                CountInLoad = true
            };
            var group = new Group { Name = "Група викладача", StudentsCount = 20, Course = course };
            var teacher = new Teacher { FullName = "Обов'язковий викладач" };
            Db.AddRange(course, module, lessonType, group, teacher);
            await Db.SaveChangesAsync();
            return new TeacherModel(course, module, lessonType, group, teacher);
        }

        public async Task<TravelModel> SeedTravelModelAsync(int travelMinutes)
        {
            var course = new Course { Name = "Курс переходів", DurationWeeks = 52 };
            var module = new Module { Code = "ПЕР-1", Title = "Модуль переходів", Credits = 1, Course = course };
            var lessonType = new LessonTypeRef
            {
                Code = "TRAVEL",
                Name = "Заняття з переходом",
                IsActive = true,
                RequiresRoom = true,
                RequiresTeacher = true,
                BlocksRoom = true,
                BlocksTeacher = true,
                CountInPlan = true,
                CountInLoad = true
            };
            var firstBuilding = new Building { Name = "Перший корпус" };
            var secondBuilding = new Building { Name = "Другий корпус" };
            var firstRoom = new Room { Name = "Аудиторія А", Capacity = 40, Building = firstBuilding };
            var secondRoom = new Room { Name = "Аудиторія Б", Capacity = 40, Building = secondBuilding };
            var firstGroup = new Group { Name = "Група переходу 1", StudentsCount = 20, Course = course };
            var secondGroup = new Group { Name = "Група переходу 2", StudentsCount = 20, Course = course };
            var teacher = new Teacher { FullName = "Викладач переходів" };
            Db.AddRange(
                course,
                module,
                lessonType,
                firstBuilding,
                secondBuilding,
                firstRoom,
                secondRoom,
                firstGroup,
                secondGroup,
                teacher);
            await Db.SaveChangesAsync();
            var fromBuildingId = Math.Min(firstBuilding.Id, secondBuilding.Id);
            var toBuildingId = Math.Max(firstBuilding.Id, secondBuilding.Id);
            Db.BuildingTravels.Add(new BuildingTravel
            {
                FromBuildingId = fromBuildingId,
                ToBuildingId = toBuildingId,
                Minutes = travelMinutes
            });
            await Db.SaveChangesAsync();
            return new TravelModel(
                course,
                module,
                lessonType,
                firstBuilding,
                secondBuilding,
                firstRoom,
                secondRoom,
                firstGroup,
                secondGroup,
                teacher);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
