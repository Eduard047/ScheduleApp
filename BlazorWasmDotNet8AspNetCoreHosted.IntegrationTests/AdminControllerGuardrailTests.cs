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

public sealed class AdminControllerGuardrailTests
{
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

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private TestDatabase(SqliteConnection connection, AppDbContext db)
        {
            this.connection = connection;
            Db = db;
        }

        public AppDbContext Db { get; }

        public static async Task<TestDatabase> CreateAsync(bool throwOnMultipleCollectionWarning = false)
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
