using BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Admin;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class TeacherDraftsAutogenServiceRegressionTests
{
    [Fact]
    public async Task Meta_course_lookup_exposes_academic_period_start_date()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var academicPeriodStartDate = new DateOnly(2026, 9, 1);
        var course = new Course
        {
            Name = "Курс метаданих навчального періоду",
            DurationWeeks = 52,
            AcademicPeriodStartDate = academicPeriodStartDate
        };
        fixture.Db.Courses.Add(course);
        await fixture.Db.SaveChangesAsync();

        var action = await new MetaController(fixture.Db).Get(weekStart: null);
        var meta = Assert.IsType<MetaResponseDto>(action.Value);

        var courseLookup = Assert.Single(meta.Courses);
        Assert.Equal(course.Id, courseLookup.Id);
        Assert.Equal(academicPeriodStartDate, courseLookup.AcademicPeriodStartDate);
    }

    [Fact]
    public async Task Meta_rejects_unsupported_week_start_before_range_calculation()
    {
        await using var fixture = await TestDatabase.CreateAsync();

        var result = await new MetaController(fixture.Db).Get(DateOnly.MaxValue);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Course_upsert_rejects_academic_period_date_outside_supported_range()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var controller = new AdminCoursesController(fixture.Db);

        foreach (var invalidDate in new[] { new DateOnly(1999, 12, 31), new DateOnly(2101, 1, 1) })
        {
            var action = await controller.Upsert(new CourseEditDto(
                id: null,
                name: "Некоректний навчальний період",
                durationWeeks: 52,
                academicPeriodStartDate: invalidDate));

            Assert.IsType<BadRequestObjectResult>(action.Result);
        }

        Assert.Empty(await fixture.Db.Courses.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Course_upsert_accepts_name_at_maximum_length()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var name = new string('К', CourseEditDto.NameMaxLength);

        var action = await new AdminCoursesController(fixture.Db).Upsert(new CourseEditDto(
            id: null,
            name,
            durationWeeks: 52,
            academicPeriodStartDate: new DateOnly(2026, 9, 1)));

        Assert.IsType<OkObjectResult>(action.Result);
        var persisted = await fixture.Db.Courses.AsNoTracking().SingleAsync();
        Assert.Equal(CourseEditDto.NameMaxLength, persisted.Name.Length);
        Assert.Equal(
            CourseEditDto.NameMaxLength,
            fixture.Db.Model.FindEntityType(typeof(Course))?.FindProperty(nameof(Course.Name))?.GetMaxLength());
    }

    [Fact]
    public async Task Course_upsert_rejects_name_over_maximum_length()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var name = new string('К', CourseEditDto.NameMaxLength + 1);

        var action = await new AdminCoursesController(fixture.Db).Upsert(new CourseEditDto(
            id: null,
            name,
            durationWeeks: 52,
            academicPeriodStartDate: new DateOnly(2026, 9, 1)));

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Contains(CourseEditDto.NameMaxLength.ToString(), badRequest.Value?.ToString(), StringComparison.Ordinal);
        Assert.Empty(await fixture.Db.Courses.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Course_upsert_without_academic_period_does_not_clear_existing_value()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var academicPeriodStartDate = new DateOnly(2026, 9, 1);
        var course = new Course
        {
            Name = "Курс із захищеним періодом",
            DurationWeeks = 52,
            AcademicPeriodStartDate = academicPeriodStartDate
        };
        fixture.Db.Courses.Add(course);
        await fixture.Db.SaveChangesAsync();

        var action = await new AdminCoursesController(fixture.Db).Upsert(
            new CourseEditDto(course.Id, "Оновлена назва", 48, academicPeriodStartDate: null));

        Assert.IsType<BadRequestObjectResult>(action.Result);
        fixture.Db.ChangeTracker.Clear();
        var persisted = await fixture.Db.Courses.AsNoTracking().SingleAsync();
        Assert.Equal("Курс із захищеним періодом", persisted.Name);
        Assert.Equal(academicPeriodStartDate, persisted.AcademicPeriodStartDate);
    }

    [Fact]
    public async Task Department_fallback_uses_explicit_module_link_and_is_deterministic()
    {
        var firstRun = await RunDepartmentFallbackScenarioAsync();
        var secondRun = await RunDepartmentFallbackScenarioAsync();

        Assert.Equal(firstRun.Fingerprint, secondRun.Fingerprint);
        Assert.Equal(2, firstRun.Fingerprint.Count);
        Assert.Single(firstRun.OutOfDepartmentDrafts);
        Assert.True(firstRun.OutOfDepartmentDrafts[0].HasExplicitModuleLink);
        Assert.Single(firstRun.FallbackWarnings);
        Assert.Empty(firstRun.IncompleteDraftIds);
    }

    [Fact]
    public async Task Teacher_without_department_is_used_when_topic_department_is_missing()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var generationDate = new DateOnly(2026, 9, 7);
        var data = await fixture.SeedCurriculumProgressScenarioAsync(
            generationDate,
            academicPeriodStartDate: new DateOnly(2026, 9, 1),
            targetHours: 1,
            topicHours: 1);

        var action = await new TeacherDraftsAutogenService(fixture.Db).DraftAutoGen(
            BuildCurriculumProgressRequest(data));

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);
        Assert.Equal(1, result.Created);
        var generated = await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .SingleAsync(item => item.Date == generationDate);
        Assert.Equal(data.TeacherId, generated.TeacherId);
        Assert.Null(await fixture.Db.Teachers
            .Where(item => item.Id == data.TeacherId)
            .Select(item => item.DepartmentId)
            .SingleAsync());
        Assert.Null(await fixture.Db.ModuleTopics
            .Where(item => item.Id == data.TopicId)
            .Select(item => item.DepartmentId)
            .SingleAsync());
    }

    [Fact]
    public async Task Teacher_without_department_is_used_as_explicit_module_fallback_for_department_topic()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var generationDate = new DateOnly(2026, 9, 7);
        var data = await fixture.SeedCurriculumProgressScenarioAsync(
            generationDate,
            academicPeriodStartDate: new DateOnly(2026, 9, 1),
            targetHours: 1,
            topicHours: 1);
        var department = new Department { Name = "Кафедра теми" };
        fixture.Db.Departments.Add(department);
        await fixture.Db.SaveChangesAsync();
        var topic = await fixture.Db.ModuleTopics.SingleAsync(item => item.Id == data.TopicId);
        topic.DepartmentId = department.Id;
        await fixture.Db.SaveChangesAsync();

        var action = await new TeacherDraftsAutogenService(fixture.Db).DraftAutoGen(
            BuildCurriculumProgressRequest(data));

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);
        Assert.Equal(1, result.Created);
        var generated = await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .SingleAsync(item => item.Date == generationDate);
        Assert.Equal(data.TeacherId, generated.TeacherId);
        Assert.Contains(
            result.Warnings,
            warning => warning.Contains("поза кафедрою теми", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Department_teacher_is_preferred_before_teacher_without_department()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var generationDate = new DateOnly(2026, 9, 7);
        var data = await fixture.SeedCurriculumProgressScenarioAsync(
            generationDate,
            academicPeriodStartDate: new DateOnly(2026, 9, 1),
            targetHours: 1,
            topicHours: 1);
        var department = new Department { Name = "Кафедра теми" };
        var departmentTeacher = new Teacher
        {
            FullName = "Викладач кафедри теми",
            Department = department
        };
        var module = await fixture.Db.Modules.SingleAsync(item => item.Id == data.ModuleId);
        var topic = await fixture.Db.ModuleTopics.SingleAsync(item => item.Id == data.TopicId);
        topic.Department = department;
        fixture.Db.AddRange(
            departmentTeacher,
            new TeacherModule
            {
                Teacher = departmentTeacher,
                Module = module
            },
            new TeacherWorkingHour
            {
                Teacher = departmentTeacher,
                DayOfWeek = generationDate.DayOfWeek,
                Start = data.Start,
                End = data.End
            });
        await fixture.Db.SaveChangesAsync();

        var action = await new TeacherDraftsAutogenService(fixture.Db).DraftAutoGen(
            BuildCurriculumProgressRequest(data));

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);
        Assert.Equal(1, result.Created);
        var generated = await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .SingleAsync(item => item.Date == generationDate);
        Assert.Equal(departmentTeacher.Id, generated.TeacherId);
        Assert.DoesNotContain(
            result.Warnings,
            warning => warning.Contains("поза кафедрою теми", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Teacher_without_department_is_used_when_department_teacher_does_not_work_in_slot()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var generationDate = new DateOnly(2026, 9, 7);
        var data = await fixture.SeedCurriculumProgressScenarioAsync(
            generationDate,
            academicPeriodStartDate: new DateOnly(2026, 9, 1),
            targetHours: 1,
            topicHours: 1);
        var department = new Department { Name = "Кафедра теми" };
        var unavailableDepartmentTeacher = new Teacher
        {
            FullName = "Недоступний викладач кафедри",
            Department = department
        };
        var module = await fixture.Db.Modules.SingleAsync(item => item.Id == data.ModuleId);
        var topic = await fixture.Db.ModuleTopics.SingleAsync(item => item.Id == data.TopicId);
        topic.Department = department;
        fixture.Db.AddRange(
            unavailableDepartmentTeacher,
            new TeacherModule
            {
                Teacher = unavailableDepartmentTeacher,
                Module = module
            },
            new TeacherWorkingHour
            {
                Teacher = unavailableDepartmentTeacher,
                DayOfWeek = DayOfWeek.Tuesday,
                Start = data.Start,
                End = data.End
            });
        await fixture.Db.SaveChangesAsync();

        var action = await new TeacherDraftsAutogenService(fixture.Db).DraftAutoGen(
            BuildCurriculumProgressRequest(data));

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);
        Assert.Equal(1, result.Created);
        var generated = await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .SingleAsync(item => item.Date == generationDate);
        Assert.Equal(data.TeacherId, generated.TeacherId);
        Assert.Contains(
            result.Warnings,
            warning => warning.Contains("поза кафедрою теми", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "protected-sync-batch")]
    public async Task Final_synchronization_does_not_move_locked_or_batched_existing_draft(
        bool isLocked,
        string? batchKey)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var data = await fixture.SeedDepartmentFallbackScenarioAsync();
        var protectedDraft = await fixture.Db.TeacherDraftItems.SingleAsync();
        protectedDraft.IsLocked = isLocked;
        protectedDraft.BatchKey = batchKey;
        await fixture.Db.SaveChangesAsync();
        var expected = new
        {
            protectedDraft.Date,
            protectedDraft.StartTime,
            protectedDraft.EndTime,
            protectedDraft.TeacherId,
            protectedDraft.RoomId,
            protectedDraft.IsLocked,
            protectedDraft.BatchKey
        };

        _ = await new TeacherDraftsAutogenService(fixture.Db).DraftAutoGen(
            new DraftAutoGenRequest(
                WeekStart: data.Date,
                ClearExisting: false,
                CourseId: data.CourseId,
                GroupIds: new List<int> { data.GroupId },
                Days: WeekPreset.MonFri,
                ModuleHours: new Dictionary<int, int> { [data.TargetModuleId] = 1 },
                SoftFill: true,
                AllowIncompleteDrafts: true,
                RangeStartDate: data.Date,
                RangeEndDate: data.Date,
                SoftOptions: new DraftAutoGenSoftOptions(RecentRepeatWindowDays: 0)));

        fixture.Db.ChangeTracker.Clear();
        var actual = await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .SingleAsync(item => item.Id == protectedDraft.Id);
        Assert.Equal(expected.Date, actual.Date);
        Assert.Equal(expected.StartTime, actual.StartTime);
        Assert.Equal(expected.EndTime, actual.EndTime);
        Assert.Equal(expected.TeacherId, actual.TeacherId);
        Assert.Equal(expected.RoomId, actual.RoomId);
        Assert.Equal(expected.IsLocked, actual.IsLocked);
        Assert.Equal(expected.BatchKey, actual.BatchKey);
    }

    [Theory]
    [InlineData(true, DraftStatus.Draft)]
    [InlineData(false, DraftStatus.Published)]
    public async Task ClearExisting_rejects_protected_legacy_logical_event_without_partial_deletion(
        bool protectedRowLocked,
        DraftStatus protectedRowStatus)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var data = await fixture.SeedCurriculumProgressScenarioAsync(
            new DateOnly(2026, 9, 7),
            academicPeriodStartDate: new DateOnly(2026, 9, 1),
            targetHours: 1,
            topicHours: 1);
        var legacyEvent = await SeedLegacyLogicalEventAsync(
            fixture.Db,
            data,
            protectedRowLocked,
            protectedRowStatus);

        var action = await new TeacherDraftsAutogenService(fixture.Db).DraftAutoGen(
            BuildClearExistingRequest(data));

        var conflict = Assert.IsType<ConflictObjectResult>(action.Result);
        Assert.Contains("частково очистити логічне заняття", conflict.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        fixture.Db.ChangeTracker.Clear();
        var persisted = await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => legacyEvent.Ids.Contains(item.Id))
            .OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.Revision, item.Status, item.IsLocked })
            .ToListAsync();
        Assert.Equal(2, persisted.Count);
        Assert.Equal(legacyEvent.Revisions, persisted.ToDictionary(item => item.Id, item => item.Revision));
        Assert.Contains(persisted, item => item.Status == protectedRowStatus && item.IsLocked == protectedRowLocked);
    }

    [Fact]
    public async Task ClearExisting_teacher_filter_removes_complete_legacy_logical_event()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var data = await fixture.SeedCurriculumProgressScenarioAsync(
            new DateOnly(2026, 9, 7),
            academicPeriodStartDate: new DateOnly(2026, 9, 1),
            targetHours: 1,
            topicHours: 1);
        var legacyEvent = await SeedLegacyLogicalEventAsync(
            fixture.Db,
            data,
            protectedRowLocked: false,
            protectedRowStatus: DraftStatus.Draft);

        var action = await new TeacherDraftsAutogenService(fixture.Db).DraftAutoGen(
            BuildClearExistingRequest(data) with { TeacherId = data.TeacherId });

        Assert.IsType<OkObjectResult>(action.Result);
        fixture.Db.ChangeTracker.Clear();
        Assert.False(await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .AnyAsync(item => item.Id == legacyEvent.SecondId
                              && item.Revision == legacyEvent.Revisions[legacyEvent.SecondId]));
    }

    [Fact]
    public async Task Academic_period_start_excludes_earlier_rows_from_topic_and_plan_progress()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var generationDate = new DateOnly(2026, 9, 7);
        var data = await fixture.SeedCurriculumProgressScenarioAsync(
            generationDate,
            academicPeriodStartDate: new DateOnly(2026, 9, 1),
            targetHours: 1,
            topicHours: 1);
        fixture.Db.TeacherDraftItems.Add(new TeacherDraftItem
        {
            Date = new DateOnly(2026, 5, 4),
            DayOfWeek = DayOfWeek.Monday,
            StartTime = data.Start,
            EndTime = data.End,
            GroupId = data.GroupId,
            ModuleId = data.ModuleId,
            ModuleTopicId = data.TopicId,
            LessonTypeId = data.LessonTypeId
        });
        fixture.Db.ScheduleItems.Add(new ScheduleItem
        {
            Date = new DateOnly(2026, 5, 11),
            DayOfWeek = DayOfWeek.Monday,
            StartTime = data.Start,
            EndTime = data.End,
            GroupId = data.GroupId,
            ModuleId = data.ModuleId,
            ModuleTopicId = data.TopicId,
            LessonTypeId = data.LessonTypeId
        });
        await fixture.Db.SaveChangesAsync();

        var action = await new TeacherDraftsAutogenService(fixture.Db).DraftAutoGen(
            BuildCurriculumProgressRequest(data));

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);
        Assert.True(
            result.Created == 1,
            $"Заняття попереднього періоду не мають вичерпувати години й теми. Створено: {result.Created}. " +
            $"Прогалини: {string.Join(" | ", result.GapDetails?.Select(item => item.Reason) ?? Array.Empty<string>())}. " +
            $"Попередження: {string.Join(" | ", result.Warnings)}");
        var generated = await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .SingleAsync(item => item.Date == generationDate);
        Assert.Equal(data.TopicId, generated.ModuleTopicId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Explicit_range_before_academic_period_counts_existing_rows_during_fill(bool useDraft)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var generationDate = new DateOnly(2026, 5, 11);
        var data = await fixture.SeedCurriculumProgressScenarioAsync(
            generationDate,
            academicPeriodStartDate: new DateOnly(2026, 9, 1),
            targetHours: 1,
            topicHours: 1);
        var secondStart = new TimeOnly(9, 0);
        var secondEnd = new TimeOnly(10, 0);
        var roomId = await fixture.Db.Rooms.Select(item => item.Id).SingleAsync();
        var workingHours = await fixture.Db.TeacherWorkingHours.SingleAsync();
        workingHours.End = secondEnd;
        var unrelatedCourse = new Course
        {
            Name = "Сторонній курс",
            DurationWeeks = 52
        };
        var unrelatedGroup = new Group
        {
            Name = "СТР-1",
            StudentsCount = 10,
            Course = unrelatedCourse
        };
        var unrelatedModule = new Module
        {
            Code = "СТР",
            Title = "Сторонній модуль",
            Credits = 1,
            Course = unrelatedCourse
        };
        var unrelatedTeacher = new Teacher { FullName = "Сторонній викладач" };
        fixture.Db.TimeSlots.Add(new TimeSlot
        {
            CourseId = data.CourseId,
            DayOfWeek = generationDate.DayOfWeek,
            Start = secondStart,
            End = secondEnd,
            SortOrder = 2,
            IsActive = true
        });
        fixture.Db.TeacherDraftItems.Add(new TeacherDraftItem
        {
            Date = generationDate,
            DayOfWeek = generationDate.DayOfWeek,
            StartTime = secondStart,
            EndTime = secondEnd,
            Group = unrelatedGroup,
            Module = unrelatedModule,
            LessonTypeId = data.LessonTypeId,
            Teacher = unrelatedTeacher,
            Status = DraftStatus.Draft
        });
        if (useDraft)
        {
            fixture.Db.TeacherDraftItems.Add(new TeacherDraftItem
            {
                Date = generationDate,
                DayOfWeek = generationDate.DayOfWeek,
                StartTime = data.Start,
                EndTime = data.End,
                GroupId = data.GroupId,
                ModuleId = data.ModuleId,
                ModuleTopicId = data.TopicId,
                LessonTypeId = data.LessonTypeId,
                TeacherId = data.TeacherId,
                RoomId = roomId,
                Status = DraftStatus.Draft
            });
        }
        else
        {
            fixture.Db.ScheduleItems.Add(new ScheduleItem
            {
                Date = generationDate,
                DayOfWeek = generationDate.DayOfWeek,
                StartTime = data.Start,
                EndTime = data.End,
                GroupId = data.GroupId,
                ModuleId = data.ModuleId,
                ModuleTopicId = data.TopicId,
                LessonTypeId = data.LessonTypeId,
                TeacherId = data.TeacherId,
                RoomId = roomId
            });
        }
        await fixture.Db.SaveChangesAsync();

        async Task<List<string>> LoadFingerprintAsync()
        {
            var drafts = await fixture.Db.TeacherDraftItems
                .AsNoTracking()
                .Where(item => item.GroupId == data.GroupId && item.Date == generationDate)
                .Select(item => $"draft|{item.Date}|{item.StartTime}|{item.EndTime}|{item.ModuleId}|{item.ModuleTopicId}|{item.TeacherId}|{item.RoomId}")
                .ToListAsync();
            var schedule = await fixture.Db.ScheduleItems
                .AsNoTracking()
                .Where(item => item.GroupId == data.GroupId && item.Date == generationDate)
                .Select(item => $"schedule|{item.Date}|{item.StartTime}|{item.EndTime}|{item.ModuleId}|{item.ModuleTopicId}|{item.TeacherId}|{item.RoomId}")
                .ToListAsync();
            return drafts.Concat(schedule).OrderBy(item => item, StringComparer.Ordinal).ToList();
        }

        var before = await LoadFingerprintAsync();
        var action = await new TeacherDraftsAutogenService(fixture.Db).DraftAutoGen(
            new DraftAutoGenRequest(
                WeekStart: data.GenerationDate,
                ClearExisting: false,
                CourseId: data.CourseId,
                GroupIds: new List<int> { data.GroupId },
                Days: WeekPreset.MonFri,
                SoftFill: true,
                AllowIncompleteDrafts: true,
                RangeStartDate: data.GenerationDate,
                RangeEndDate: data.GenerationDate,
                SoftOptions: new DraftAutoGenSoftOptions(RecentRepeatWindowDays: 0)));

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);
        fixture.Db.ChangeTracker.Clear();
        var after = await LoadFingerprintAsync();
        Assert.Single(before);
        Assert.Equal(0, result.Created);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Soft_fill_ignores_historical_topic_order_regression_before_selected_range()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var generationDate = new DateOnly(2026, 9, 7);
        var data = await fixture.SeedCurriculumProgressScenarioAsync(
            generationDate,
            academicPeriodStartDate: null,
            targetHours: 1,
            topicHours: 1);
        var historicalModule = new Module
        {
            Code = "ІСТ",
            Title = "Історичний модуль",
            Credits = 1,
            CourseId = data.CourseId
        };
        var historicalEarlierTopic = new ModuleTopic
        {
            Module = historicalModule,
            Order = 2,
            TopicCode = "ІСТ-2",
            LessonTypeId = data.LessonTypeId,
            TotalHours = 1,
            AuditoriumHours = 1
        };
        var historicalLaterTopic = new ModuleTopic
        {
            Module = historicalModule,
            Order = 1,
            TopicCode = "ІСТ-1",
            LessonTypeId = data.LessonTypeId,
            TotalHours = 1,
            AuditoriumHours = 1
        };
        fixture.Db.AddRange(historicalModule, historicalEarlierTopic, historicalLaterTopic);
        await fixture.Db.SaveChangesAsync();

        var earlierHistoricalDate = generationDate.AddDays(-14);
        var laterHistoricalDate = generationDate.AddDays(-7);
        fixture.Db.TeacherDraftItems.AddRange(
            new TeacherDraftItem
            {
                Date = earlierHistoricalDate,
                DayOfWeek = earlierHistoricalDate.DayOfWeek,
                StartTime = data.Start,
                EndTime = data.End,
                GroupId = data.GroupId,
                ModuleId = historicalModule.Id,
                ModuleTopicId = historicalEarlierTopic.Id,
                LessonTypeId = data.LessonTypeId
            },
            new TeacherDraftItem
            {
                Date = laterHistoricalDate,
                DayOfWeek = laterHistoricalDate.DayOfWeek,
                StartTime = data.Start,
                EndTime = data.End,
                GroupId = data.GroupId,
                ModuleId = historicalModule.Id,
                ModuleTopicId = historicalLaterTopic.Id,
                LessonTypeId = data.LessonTypeId
            });
        await fixture.Db.SaveChangesAsync();

        var action = await new TeacherDraftsAutogenService(fixture.Db).DraftAutoGen(
            new DraftAutoGenRequest(
                WeekStart: generationDate,
                ClearExisting: false,
                CourseId: data.CourseId,
                GroupIds: new List<int> { data.GroupId },
                Days: WeekPreset.MonFri,
                ModuleHours: new Dictionary<int, int> { [data.ModuleId] = 1 },
                SoftFill: true,
                AllowIncompleteDrafts: true,
                RangeStartDate: generationDate,
                RangeEndDate: generationDate,
                SoftOptions: new DraftAutoGenSoftOptions(RecentRepeatWindowDays: 0)));

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);
        Assert.True(
            result.Created == 1,
            $"Історичний порядок тем поза вибраним діапазоном не повинен відкочувати дозаповнення. " +
            $"Створено: {result.Created}. Прогалини: " +
            $"{string.Join(" | ", result.GapDetails?.Select(item => item.Reason) ?? Array.Empty<string>())}. " +
            $"Попередження: {string.Join(" | ", result.Warnings)}");
        var generated = await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .SingleAsync(item => item.Date == generationDate);
        Assert.Equal(data.ModuleId, generated.ModuleId);
        Assert.Equal(data.TopicId, generated.ModuleTopicId);
    }

    [Fact]
    public async Task Ef_nested_trial_restore_preserves_accepted_move_after_save_and_reload()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var originalDate = new DateOnly(2026, 9, 7);
        var data = await fixture.SeedCurriculumProgressScenarioAsync(
            originalDate,
            academicPeriodStartDate: null,
            targetHours: 1,
            topicHours: 1);
        var roomId = await fixture.Db.Rooms.Select(room => room.Id).SingleAsync();
        var draft = new TeacherDraftItem
        {
            Date = originalDate,
            DayOfWeek = originalDate.DayOfWeek,
            StartTime = data.Start,
            EndTime = data.End,
            GroupId = data.GroupId,
            ModuleId = data.ModuleId,
            ModuleTopicId = data.TopicId,
            LessonTypeId = data.LessonTypeId,
            TeacherId = data.TeacherId,
            RoomId = roomId
        };
        fixture.Db.TeacherDraftItems.Add(draft);
        await fixture.Db.SaveChangesAsync();

        var acceptedDate = originalDate.AddDays(1);
        var acceptedStart = new TimeOnly(9, 10);
        var acceptedEnd = new TimeOnly(10, 10);
        draft.Date = acceptedDate;
        draft.DayOfWeek = acceptedDate.DayOfWeek;
        draft.StartTime = acceptedStart;
        draft.EndTime = acceptedEnd;
        fixture.Db.ChangeTracker.DetectChanges();
        var entry = fixture.Db.Entry(draft);
        Assert.Equal(EntityState.Modified, entry.State);
        var modifiedProperties = entry.Properties
            .Where(property => property.IsModified)
            .Select(property => property.Metadata.Name)
            .ToHashSet(StringComparer.Ordinal);

        var rejectedDate = originalDate.AddDays(2);
        draft.Date = rejectedDate;
        draft.DayOfWeek = rejectedDate.DayOfWeek;
        draft.StartTime = new TimeOnly(10, 20);
        draft.EndTime = new TimeOnly(11, 20);
        fixture.Db.ChangeTracker.DetectChanges();

        entry.State = EntityState.Unchanged;
        draft.Date = acceptedDate;
        draft.DayOfWeek = acceptedDate.DayOfWeek;
        draft.StartTime = acceptedStart;
        draft.EndTime = acceptedEnd;
        foreach (var propertyName in modifiedProperties)
        {
            entry.Property(propertyName).IsModified = true;
        }

        Assert.Equal(EntityState.Modified, entry.State);
        Assert.Equal(acceptedDate, draft.Date);
        Assert.Equal(acceptedStart, draft.StartTime);
        Assert.Equal(acceptedEnd, draft.EndTime);
        await fixture.Db.SaveChangesAsync();

        fixture.Db.ChangeTracker.Clear();
        var persisted = await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .SingleAsync(item => item.Id == draft.Id);
        Assert.Equal(acceptedDate, persisted.Date);
        Assert.Equal(acceptedDate.DayOfWeek, persisted.DayOfWeek);
        Assert.Equal(acceptedStart, persisted.StartTime);
        Assert.Equal(acceptedEnd, persisted.EndTime);
    }

    [Fact]
    public async Task Co_teacher_rows_with_same_batch_key_consume_module_topic_once()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var generationDate = new DateOnly(2026, 9, 14);
        var data = await fixture.SeedCurriculumProgressScenarioAsync(
            generationDate,
            academicPeriodStartDate: new DateOnly(2026, 9, 1),
            targetHours: 2,
            topicHours: 2);
        var firstTeacher = new Teacher { FullName = "Перший співвикладач" };
        var secondTeacher = new Teacher { FullName = "Другий співвикладач" };
        fixture.Db.Teachers.AddRange(firstTeacher, secondTeacher);
        await fixture.Db.SaveChangesAsync();
        const string batchKey = "co-teacher-logical-lesson";
        var previousLessonDate = generationDate.AddDays(-7);
        fixture.Db.TeacherDraftItems.AddRange(
            new TeacherDraftItem
            {
                Date = previousLessonDate,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = data.Start,
                EndTime = data.End,
                GroupId = data.GroupId,
                ModuleId = data.ModuleId,
                ModuleTopicId = data.TopicId,
                LessonTypeId = data.LessonTypeId,
                TeacherId = firstTeacher.Id,
                BatchKey = batchKey
            },
            new TeacherDraftItem
            {
                Date = previousLessonDate,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = data.Start,
                EndTime = data.End,
                GroupId = data.GroupId,
                ModuleId = data.ModuleId,
                ModuleTopicId = data.TopicId,
                LessonTypeId = data.LessonTypeId,
                TeacherId = secondTeacher.Id,
                BatchKey = batchKey
            });
        await fixture.Db.SaveChangesAsync();

        var action = await new TeacherDraftsAutogenService(fixture.Db).DraftAutoGen(
            BuildCurriculumProgressRequest(data));

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);
        Assert.True(
            result.Created == 1,
            $"Два рядки співвикладачів мають рахуватися як одне заняття. Створено: {result.Created}. " +
            $"Прогалини: {string.Join(" | ", result.GapDetails?.Select(item => item.Reason) ?? Array.Empty<string>())}. " +
            $"Попередження: {string.Join(" | ", result.Warnings)}");
        var generated = await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .SingleAsync(item => item.Date == generationDate);
        Assert.Equal(data.TopicId, generated.ModuleTopicId);
        Assert.Equal(3, await fixture.Db.TeacherDraftItems.CountAsync());
    }

    [Fact]
    public async Task Co_teacher_event_with_distinct_topics_consumes_each_topic_once()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var generationDate = new DateOnly(2026, 9, 14);
        var data = await fixture.SeedCurriculumProgressScenarioAsync(
            generationDate,
            academicPeriodStartDate: new DateOnly(2026, 9, 1),
            targetHours: 2,
            topicHours: 1);
        var secondTopic = new ModuleTopic
        {
            ModuleId = data.ModuleId,
            Order = 2,
            TopicCode = "ПЕР-2",
            LessonTypeId = data.LessonTypeId,
            TotalHours = 1,
            AuditoriumHours = 1
        };
        var thirdTopic = new ModuleTopic
        {
            ModuleId = data.ModuleId,
            Order = 3,
            TopicCode = "ПЕР-3",
            LessonTypeId = data.LessonTypeId,
            TotalHours = 1,
            AuditoriumHours = 1
        };
        var firstTeacher = new Teacher { FullName = "Перший викладач різних тем" };
        var secondTeacher = new Teacher { FullName = "Другий викладач різних тем" };
        fixture.Db.AddRange(secondTopic, thirdTopic, firstTeacher, secondTeacher);
        await fixture.Db.SaveChangesAsync();

        const string batchKey = "co-teacher-distinct-topics";
        var previousLessonDate = generationDate.AddDays(-7);
        fixture.Db.TeacherDraftItems.AddRange(
            new TeacherDraftItem
            {
                Date = previousLessonDate,
                DayOfWeek = previousLessonDate.DayOfWeek,
                StartTime = data.Start,
                EndTime = data.End,
                GroupId = data.GroupId,
                ModuleId = data.ModuleId,
                ModuleTopicId = data.TopicId,
                LessonTypeId = data.LessonTypeId,
                TeacherId = firstTeacher.Id,
                BatchKey = batchKey
            },
            new TeacherDraftItem
            {
                Date = previousLessonDate,
                DayOfWeek = previousLessonDate.DayOfWeek,
                StartTime = data.Start,
                EndTime = data.End,
                GroupId = data.GroupId,
                ModuleId = data.ModuleId,
                ModuleTopicId = secondTopic.Id,
                LessonTypeId = data.LessonTypeId,
                TeacherId = secondTeacher.Id,
                BatchKey = batchKey
            });
        await fixture.Db.SaveChangesAsync();

        var action = await new TeacherDraftsAutogenService(fixture.Db).DraftAutoGen(
            BuildCurriculumProgressRequest(data));

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);
        Assert.Equal(1, result.Created);
        var generated = await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .SingleAsync(item => item.Date == generationDate);
        Assert.Equal(thirdTopic.Id, generated.ModuleTopicId);
    }

    [Fact]
    public async Task Full_generator_creates_complete_lesson_without_teacher_module_when_type_does_not_require_teacher()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var date = new DateOnly(2026, 9, 7);
        var course = new Course
        {
            Name = "Курс без викладача",
            DurationWeeks = 18,
            AcademicPeriodStartDate = date
        };
        var group = new Group { Name = "БВ-1", StudentsCount = 20, Course = course };
        var lessonType = new LessonTypeRef
        {
            Code = "UNTUTORED",
            Name = "Заняття без викладача",
            IsActive = true,
            RequiresTeacher = false,
            RequiresRoom = false,
            BlocksTeacher = false,
            BlocksRoom = false,
            CountInPlan = true,
            CountInLoad = false
        };
        var module = new Module
        {
            Code = "БВ",
            Title = "Модуль без викладача",
            Credits = 1,
            Course = course
        };
        var topic = new ModuleTopic
        {
            Module = module,
            Order = 1,
            TopicCode = "БВ-1",
            LessonType = lessonType,
            TotalHours = 1,
            AuditoriumHours = 1
        };
        fixture.Db.AddRange(
            course,
            group,
            lessonType,
            module,
            topic,
            new TimeSlot
            {
                Course = course,
                DayOfWeek = date.DayOfWeek,
                Start = new TimeOnly(8, 0),
                End = new TimeOnly(9, 0),
                SortOrder = 1,
                IsActive = true
            });
        await fixture.Db.SaveChangesAsync();

        var result = await RunAlgorithmScenarioAsync(
            fixture.Db,
            date,
            date,
            course.Id,
            group.Id,
            module.Id,
            hours: 1);

        Assert.Equal(1, result.Created);
        var draft = await fixture.Db.TeacherDraftItems.AsNoTracking().SingleAsync();
        Assert.Null(draft.TeacherId);
        Assert.Null(draft.RoomId);
        Assert.Null(draft.ValidationWarnings);
        Assert.Empty(await fixture.Db.TeacherModules.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Full_generator_allows_resource_overlap_when_either_lesson_type_does_not_block(
        bool existingBlocksResources,
        bool generatedBlocksResources)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var date = new DateOnly(2026, 9, 7);
        var course = new Course
        {
            Name = "Курс симетричної зайнятості",
            DurationWeeks = 18,
            AcademicPeriodStartDate = date
        };
        var targetGroup = new Group { Name = "СЗ-1", StudentsCount = 20, Course = course };
        var occupiedGroup = new Group { Name = "СЗ-2", StudentsCount = 20, Course = course };
        var existingType = CreateResourceLessonType("EXISTING", existingBlocksResources);
        var targetType = CreateResourceLessonType("TARGET", generatedBlocksResources);
        var existingModule = new Module
        {
            Code = "СЗ-Н",
            Title = "Наявний модуль",
            Credits = 1,
            Course = course
        };
        var targetModule = new Module
        {
            Code = "СЗ-Ц",
            Title = "Цільовий модуль",
            Credits = 1,
            Course = course
        };
        var teacher = new Teacher { FullName = "Викладач симетричної зайнятості" };
        var building = new Building { Name = "Корпус симетричної зайнятості" };
        var room = new Room
        {
            Name = "СЗ-101",
            Capacity = 40,
            Building = building
        };
        var start = new TimeOnly(8, 0);
        var end = new TimeOnly(9, 0);
        fixture.Db.AddRange(
            course,
            targetGroup,
            occupiedGroup,
            existingType,
            targetType,
            existingModule,
            targetModule,
            teacher,
            building,
            room);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.AddRange(
            new ModuleTopic
            {
                ModuleId = targetModule.Id,
                Order = 1,
                TopicCode = "СЗ-1",
                LessonTypeId = targetType.Id,
                TotalHours = 1,
                AuditoriumHours = 1
            },
            new TeacherModule { TeacherId = teacher.Id, ModuleId = targetModule.Id },
            new ModuleRoom { ModuleId = targetModule.Id, RoomId = room.Id },
            new TeacherWorkingHour
            {
                TeacherId = teacher.Id,
                DayOfWeek = date.DayOfWeek,
                Start = start,
                End = end
            },
            new TimeSlot
            {
                CourseId = course.Id,
                DayOfWeek = date.DayOfWeek,
                Start = start,
                End = end,
                SortOrder = 1,
                IsActive = true
            },
            new ScheduleItem
            {
                Date = date,
                DayOfWeek = date.DayOfWeek,
                StartTime = start,
                EndTime = end,
                GroupId = occupiedGroup.Id,
                ModuleId = existingModule.Id,
                LessonTypeId = existingType.Id,
                TeacherId = teacher.Id,
                RoomId = room.Id
            });
        await fixture.Db.SaveChangesAsync();

        var result = await RunAlgorithmScenarioAsync(
            fixture.Db,
            date,
            date,
            course.Id,
            targetGroup.Id,
            targetModule.Id,
            hours: 1);

        Assert.Equal(1, result.Created);
        var generated = await fixture.Db.TeacherDraftItems.AsNoTracking().SingleAsync();
        Assert.Equal(teacher.Id, generated.TeacherId);
        Assert.Equal(room.Id, generated.RoomId);
        Assert.Equal(start, generated.StartTime);
    }

    [Fact]
    public async Task Full_generator_balances_dynamic_logical_teacher_load_and_ignores_non_load_types()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var firstDate = new DateOnly(2026, 9, 7);
        var secondDate = firstDate.AddDays(1);
        var course = new Course
        {
            Name = "Курс динамічного навантаження",
            DurationWeeks = 18,
            AcademicPeriodStartDate = firstDate.AddDays(-30)
        };
        var targetGroup = new Group { Name = "ДН-1", StudentsCount = 20, Course = course };
        var sharedGroupA = new Group { Name = "ДН-2", StudentsCount = 20, Course = course };
        var sharedGroupB = new Group { Name = "ДН-3", StudentsCount = 20, Course = course };
        var targetType = CreateResourceLessonType("PRACTICE", blocksResources: true);
        var sharedLoadType = CreateResourceLessonType("LECTURE", blocksResources: true);
        var ignoredLoadType = CreateResourceLessonType("IGNORED", blocksResources: true);
        ignoredLoadType.CountInLoad = false;
        var targetModule = new Module
        {
            Code = "ДН-Ц",
            Title = "Цільовий модуль навантаження",
            Credits = 1,
            Course = course
        };
        var sharedModule = new Module
        {
            Code = "ДН-Л",
            Title = "Спільна лекція",
            Credits = 1,
            Course = course
        };
        var ignoredModule = new Module
        {
            Code = "ДН-І",
            Title = "Невраховані заняття",
            Credits = 1,
            Course = course
        };
        var firstTeacher = new Teacher { FullName = "Перший викладач навантаження" };
        var secondTeacher = new Teacher { FullName = "Другий викладач навантаження" };
        var building = new Building { Name = "Корпус навантаження" };
        var room = new Room { Name = "ДН-101", Capacity = 80, Building = building };
        fixture.Db.AddRange(
            course,
            targetGroup,
            sharedGroupA,
            sharedGroupB,
            targetType,
            sharedLoadType,
            ignoredLoadType,
            targetModule,
            sharedModule,
            ignoredModule,
            firstTeacher,
            secondTeacher,
            building,
            room);
        await fixture.Db.SaveChangesAsync();
        var targetTopic = new ModuleTopic
        {
            ModuleId = targetModule.Id,
            Order = 1,
            TopicCode = "ДН-1",
            LessonTypeId = targetType.Id,
            TotalHours = 2,
            AuditoriumHours = 2
        };
        var sharedTopic = new ModuleTopic
        {
            ModuleId = sharedModule.Id,
            Order = 1,
            TopicCode = "ДН-Л1",
            LessonTypeId = sharedLoadType.Id,
            TotalHours = 1,
            AuditoriumHours = 1
        };
        fixture.Db.AddRange(targetTopic, sharedTopic);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.AddRange(
            new TeacherModule { TeacherId = firstTeacher.Id, ModuleId = targetModule.Id },
            new TeacherModule { TeacherId = secondTeacher.Id, ModuleId = targetModule.Id },
            new ModuleRoom { ModuleId = targetModule.Id, RoomId = room.Id },
            new TimeSlot
            {
                CourseId = course.Id,
                DayOfWeek = firstDate.DayOfWeek,
                Start = new TimeOnly(8, 0),
                End = new TimeOnly(9, 0),
                SortOrder = 1,
                IsActive = true
            },
            new TimeSlot
            {
                CourseId = course.Id,
                DayOfWeek = secondDate.DayOfWeek,
                Start = new TimeOnly(8, 0),
                End = new TimeOnly(9, 0),
                SortOrder = 1,
                IsActive = true
            });
        foreach (var teacher in new[] { firstTeacher, secondTeacher })
        {
            fixture.Db.TeacherWorkingHours.AddRange(
                new TeacherWorkingHour
                {
                    TeacherId = teacher.Id,
                    DayOfWeek = firstDate.DayOfWeek,
                    Start = new TimeOnly(8, 0),
                    End = new TimeOnly(9, 0)
                },
                new TeacherWorkingHour
                {
                    TeacherId = teacher.Id,
                    DayOfWeek = secondDate.DayOfWeek,
                    Start = new TimeOnly(8, 0),
                    End = new TimeOnly(9, 0)
                });
        }
        var sharedDate = firstDate.AddDays(-7);
        fixture.Db.ScheduleItems.AddRange(
            CreateScheduleItem(sharedDate, sharedGroupA, sharedModule, sharedLoadType, firstTeacher, room, sharedTopic.Id),
            CreateScheduleItem(sharedDate, sharedGroupB, sharedModule, sharedLoadType, firstTeacher, room, sharedTopic.Id));
        for (var index = 1; index <= 3; index++)
        {
            fixture.Db.ScheduleItems.Add(CreateScheduleItem(
                firstDate.AddDays(-7 - index),
                targetGroup,
                ignoredModule,
                ignoredLoadType,
                secondTeacher,
                room,
                moduleTopicId: null));
        }
        await fixture.Db.SaveChangesAsync();

        var result = await RunAlgorithmScenarioAsync(
            fixture.Db,
            firstDate,
            secondDate,
            course.Id,
            targetGroup.Id,
            targetModule.Id,
            hours: 2,
            teacherLoadPenaltyWeight: 100);

        Assert.Equal(2, result.Created);
        var generatedTeachers = await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .OrderBy(item => item.Date)
            .Select(item => item.TeacherId)
            .ToListAsync();
        Assert.Equal(new int?[] { secondTeacher.Id, firstTeacher.Id }, generatedTeachers);
    }

    [Fact]
    public async Task Full_generator_includes_future_academic_period_events_in_teacher_load()
    {
        await AssertFutureTeacherLoadScenarioAsync(durationWeeks: 18, futureEventShouldCount: true);
    }

    [Fact]
    public async Task Full_generator_excludes_events_after_academic_period_from_teacher_load()
    {
        await AssertFutureTeacherLoadScenarioAsync(durationWeeks: 3, futureEventShouldCount: false);
    }

    private static async Task AssertFutureTeacherLoadScenarioAsync(int durationWeeks, bool futureEventShouldCount)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var date = new DateOnly(2026, 9, 7);
        var course = new Course
        {
            Name = "Курс майбутнього навантаження",
            DurationWeeks = durationWeeks,
            AcademicPeriodStartDate = date.AddDays(-7)
        };
        var targetGroup = new Group { Name = "МН-1", StudentsCount = 20, Course = course };
        var futureGroup = new Group { Name = "МН-2", StudentsCount = 20, Course = course };
        var lessonType = CreateResourceLessonType("FUTURE-LOAD", blocksResources: true);
        var targetModule = new Module
        {
            Code = "МН-Ц",
            Title = "Цільовий модуль майбутнього навантаження",
            Credits = 1,
            Course = course
        };
        var futureModule = new Module
        {
            Code = "МН-М",
            Title = "Майбутній модуль навантаження",
            Credits = 1,
            Course = course
        };
        var firstTeacher = new Teacher { FullName = "Викладач із майбутнім навантаженням" };
        var secondTeacher = new Teacher { FullName = "Викладач без майбутнього навантаження" };
        var building = new Building { Name = "Корпус майбутнього навантаження" };
        var room = new Room { Name = "МН-101", Capacity = 40, Building = building };
        fixture.Db.AddRange(
            course,
            targetGroup,
            futureGroup,
            lessonType,
            targetModule,
            futureModule,
            firstTeacher,
            secondTeacher,
            building,
            room);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.AddRange(
            new ModuleTopic
            {
                ModuleId = targetModule.Id,
                Order = 1,
                TopicCode = "МН-1",
                LessonTypeId = lessonType.Id,
                TotalHours = 1,
                AuditoriumHours = 1
            },
            new TeacherModule { TeacherId = firstTeacher.Id, ModuleId = targetModule.Id },
            new TeacherModule { TeacherId = secondTeacher.Id, ModuleId = targetModule.Id },
            new ModuleRoom { ModuleId = targetModule.Id, RoomId = room.Id },
            new TimeSlot
            {
                CourseId = course.Id,
                DayOfWeek = date.DayOfWeek,
                Start = new TimeOnly(8, 0),
                End = new TimeOnly(9, 0),
                SortOrder = 1,
                IsActive = true
            },
            new TeacherWorkingHour
            {
                TeacherId = firstTeacher.Id,
                DayOfWeek = date.DayOfWeek,
                Start = new TimeOnly(8, 0),
                End = new TimeOnly(9, 0)
            },
            new TeacherWorkingHour
            {
                TeacherId = secondTeacher.Id,
                DayOfWeek = date.DayOfWeek,
                Start = new TimeOnly(8, 0),
                End = new TimeOnly(9, 0)
            },
            CreateScheduleItem(
                date.AddDays(14),
                futureGroup,
                futureModule,
                lessonType,
                firstTeacher,
                room,
                moduleTopicId: null));
        await fixture.Db.SaveChangesAsync();

        var result = await RunAlgorithmScenarioAsync(
            fixture.Db,
            date,
            date,
            course.Id,
            targetGroup.Id,
            targetModule.Id,
            hours: 1,
            teacherLoadPenaltyWeight: 100);

        Assert.Equal(1, result.Created);
        var generated = await fixture.Db.TeacherDraftItems.AsNoTracking().SingleAsync();
        var expectedTeacherId = futureEventShouldCount ? secondTeacher.Id : firstTeacher.Id;
        Assert.Equal(expectedTeacherId, generated.TeacherId);
    }

    private static LessonTypeRef CreateResourceLessonType(string code, bool blocksResources)
        => new()
        {
            Code = code,
            Name = $"Тип {code}",
            IsActive = true,
            RequiresTeacher = true,
            RequiresRoom = true,
            BlocksTeacher = blocksResources,
            BlocksRoom = blocksResources,
            CountInPlan = true,
            CountInLoad = true
        };

    private static ScheduleItem CreateScheduleItem(
        DateOnly date,
        Group group,
        Module module,
        LessonTypeRef lessonType,
        Teacher teacher,
        Room room,
        int? moduleTopicId)
        => new()
        {
            Date = date,
            DayOfWeek = date.DayOfWeek,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(9, 0),
            GroupId = group.Id,
            ModuleId = module.Id,
            LessonTypeId = lessonType.Id,
            TeacherId = teacher.Id,
            RoomId = room.Id,
            ModuleTopicId = moduleTopicId
        };

    private static async Task<AutoGenResult> RunAlgorithmScenarioAsync(
        AppDbContext db,
        DateOnly rangeStart,
        DateOnly rangeEnd,
        int courseId,
        int groupId,
        int moduleId,
        int hours,
        double? teacherLoadPenaltyWeight = null)
    {
        var action = await new TeacherDraftsAutogenService(db).DraftAutoGen(
            new DraftAutoGenRequest(
                WeekStart: rangeStart,
                ClearExisting: false,
                CourseId: courseId,
                GroupIds: new List<int> { groupId },
                Days: WeekPreset.MonFri,
                ModuleHours: new Dictionary<int, int> { [moduleId] = hours },
                SoftFill: false,
                AllowIncompleteDrafts: false,
                RangeStartDate: rangeStart,
                RangeEndDate: rangeEnd,
                SoftOptions: new DraftAutoGenSoftOptions(
                    RecentRepeatWindowDays: 0,
                    TeacherLoadPenaltyWeight: teacherLoadPenaltyWeight)));
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        return Assert.IsType<AutoGenResult>(ok.Value);
    }

    private static DraftAutoGenRequest BuildCurriculumProgressRequest(CurriculumProgressSeed data)
        => new(
            WeekStart: data.GenerationDate,
            ClearExisting: false,
            CourseId: data.CourseId,
            GroupIds: new List<int> { data.GroupId },
            Days: WeekPreset.MonFri,
            SoftFill: false,
            RangeStartDate: data.GenerationDate,
            RangeEndDate: data.GenerationDate,
            SoftOptions: new DraftAutoGenSoftOptions(RecentRepeatWindowDays: 0));

    private static DraftAutoGenRequest BuildClearExistingRequest(CurriculumProgressSeed data)
        => BuildCurriculumProgressRequest(data) with
        {
            ClearExisting = true,
            ModuleHours = new Dictionary<int, int> { [data.ModuleId] = 1 },
            AllowIncompleteDrafts = true
        };

    private static async Task<LegacyLogicalEventSeed> SeedLegacyLogicalEventAsync(
        AppDbContext db,
        CurriculumProgressSeed data,
        bool protectedRowLocked,
        DraftStatus protectedRowStatus)
    {
        var secondTeacher = new Teacher { FullName = "Другий викладач legacy-події" };
        db.Teachers.Add(secondTeacher);
        await db.SaveChangesAsync();
        var roomId = await db.Rooms.Select(room => room.Id).SingleAsync();
        var firstRow = new TeacherDraftItem
        {
            Date = data.GenerationDate,
            DayOfWeek = data.GenerationDate.DayOfWeek,
            StartTime = data.Start,
            EndTime = data.End,
            GroupId = data.GroupId,
            ModuleId = data.ModuleId,
            ModuleTopicId = data.TopicId,
            LessonTypeId = data.LessonTypeId,
            TeacherId = data.TeacherId,
            RoomId = roomId,
            Status = DraftStatus.Draft,
            IsLocked = false,
            BatchKey = null
        };
        var secondRow = new TeacherDraftItem
        {
            Date = data.GenerationDate,
            DayOfWeek = data.GenerationDate.DayOfWeek,
            StartTime = data.Start,
            EndTime = data.End,
            GroupId = data.GroupId,
            ModuleId = data.ModuleId,
            ModuleTopicId = data.TopicId,
            LessonTypeId = data.LessonTypeId,
            TeacherId = secondTeacher.Id,
            RoomId = roomId,
            Status = protectedRowStatus,
            IsLocked = protectedRowLocked,
            BatchKey = null
        };
        db.TeacherDraftItems.AddRange(firstRow, secondRow);
        await db.SaveChangesAsync();
        return new LegacyLogicalEventSeed(
            new[] { firstRow.Id, secondRow.Id },
            new Dictionary<int, Guid>
            {
                [firstRow.Id] = firstRow.Revision,
                [secondRow.Id] = secondRow.Revision
            },
            secondRow.Id);
    }

    private static async Task<DepartmentFallbackResult> RunDepartmentFallbackScenarioAsync()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var data = await fixture.SeedDepartmentFallbackScenarioAsync();

        var action = await new TeacherDraftsAutogenService(fixture.Db).DraftAutoGen(
            new DraftAutoGenRequest(
                WeekStart: data.Date,
                ClearExisting: false,
                CourseId: data.CourseId,
                GroupIds: new List<int> { data.GroupId },
                Days: WeekPreset.MonFri,
                ModuleHours: new Dictionary<int, int> { [data.TargetModuleId] = 1 },
                SoftFill: true,
                AllowIncompleteDrafts: true,
                RangeStartDate: data.Date,
                RangeEndDate: data.Date,
                SoftOptions: new DraftAutoGenSoftOptions(
                    RecentRepeatWindowDays: 0)));

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);
        var drafts = await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => item.GroupId == data.GroupId)
            .OrderBy(item => item.StartTime)
            .ThenBy(item => item.ModuleId)
            .ToListAsync();
        var teacherDepartments = await fixture.Db.Teachers
            .AsNoTracking()
            .ToDictionaryAsync(item => item.Id, item => item.DepartmentId);
        var explicitLinks = (await fixture.Db.TeacherModules
            .AsNoTracking()
            .Where(item => item.ModuleId == data.MovableModuleId)
            .Select(item => item.TeacherId)
            .ToListAsync())
            .ToHashSet();

        var outOfDepartmentDrafts = drafts
            .Where(item => item.TeacherId is int teacherId
                           && item.ModuleId == data.MovableModuleId
                           && teacherDepartments.GetValueOrDefault(teacherId) != data.TopicDepartmentId)
            .Select(item => new OutOfDepartmentDraft(
                item.Id,
                explicitLinks.Contains(item.TeacherId!.Value)))
            .ToList();
        var fingerprint = drafts
            .Select(item => $"{item.Date:yyyy-MM-dd}|{item.StartTime:HH\\:mm}|{item.EndTime:HH\\:mm}|{item.GroupId}|{item.ModuleId}|{item.ModuleTopicId}|{item.LessonTypeId}|{item.TeacherId}|{item.RoomId}")
            .ToList();

        Assert.True(
            result.Created == 1,
            $"Створено: {result.Created}. Пропуски: {string.Join(" | ", result.GapDetails?.Select(item => item.Reason) ?? Array.Empty<string>())}. Попередження: {string.Join(" | ", result.Warnings)}");
        Assert.Empty(result.GapDetails ?? new List<AutoGenGapDetail>());

        return new DepartmentFallbackResult(
            fingerprint,
            outOfDepartmentDrafts,
            result.Warnings
                .Where(warning => warning.Contains("поза кафедрою теми", StringComparison.OrdinalIgnoreCase))
                .ToList(),
            drafts
                .Where(item => item.TeacherId is null || item.RoomId is null)
                .Select(item => item.Id)
                .ToList());
    }

    private sealed record DepartmentFallbackSeed(
        int CourseId,
        int GroupId,
        int MovableModuleId,
        int TargetModuleId,
        int TopicDepartmentId,
        DateOnly Date);

    private sealed record CurriculumProgressSeed(
        int CourseId,
        int GroupId,
        int ModuleId,
        int TopicId,
        int LessonTypeId,
        int TeacherId,
        DateOnly GenerationDate,
        TimeOnly Start,
        TimeOnly End);

    private sealed record LegacyLogicalEventSeed(
        int[] Ids,
        Dictionary<int, Guid> Revisions,
        int SecondId);

    private sealed record OutOfDepartmentDraft(int DraftId, bool HasExplicitModuleLink);

    private sealed record DepartmentFallbackResult(
        List<string> Fingerprint,
        List<OutOfDepartmentDraft> OutOfDepartmentDrafts,
        List<string> FallbackWarnings,
        List<int> IncompleteDraftIds);

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private TestDatabase(SqliteConnection connection, AppDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public AppDbContext Db { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, db);
        }

        public async Task<DepartmentFallbackSeed> SeedDepartmentFallbackScenarioAsync()
        {
            const int courseId = 940;
            const int movableModuleId = 941;
            const int lessonTypeId = 942;
            const int movableTopicId = 943;
            const int topicDepartmentId = 944;
            const int otherDepartmentId = 945;
            const int groupId = 946;
            const int targetModuleId = 954;
            const int targetTopicId = 955;
            var date = new DateOnly(2026, 5, 4);
            var firstSlotStart = new TimeOnly(8, 0);
            var firstSlotEnd = new TimeOnly(9, 0);
            var secondSlotStart = new TimeOnly(9, 10);
            var secondSlotEnd = new TimeOnly(10, 10);

            Db.Courses.Add(new Course
            {
                Id = courseId,
                Name = "Курс перевірки кафедрального резерву",
                DurationWeeks = 1
            });
            Db.Groups.Add(new Group
            {
                Id = groupId,
                Name = "КР-1",
                StudentsCount = 20,
                CourseId = courseId
            });
            Db.Departments.AddRange(
                new Department
                {
                    Id = topicDepartmentId,
                    Name = "Кафедра теми"
                },
                new Department
                {
                    Id = otherDepartmentId,
                    Name = "Резервна кафедра"
                });
            Db.LessonTypes.Add(new LessonTypeRef
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
            Db.Modules.AddRange(
                new Module
                {
                    Id = movableModuleId,
                    Code = "РУХ",
                    Title = "Рухома чернетка",
                    Credits = 1,
                    CourseId = courseId
                },
                new Module
                {
                    Id = targetModuleId,
                    Code = "ЦІЛЬ",
                    Title = "Цільовий модуль",
                    Credits = 1,
                    CourseId = courseId
                });
            Db.ModulePlans.Add(new ModulePlan
            {
                CourseId = courseId,
                ModuleId = movableModuleId,
                TargetHours = 1,
                ScheduledHours = 1,
                IsActive = true
            });
            Db.ModuleTopics.AddRange(
                new ModuleTopic
                {
                    Id = movableTopicId,
                    ModuleId = movableModuleId,
                    Order = 1,
                    TopicCode = "РУХ-1",
                    LessonTypeId = lessonTypeId,
                    DepartmentId = topicDepartmentId,
                    TotalHours = 1,
                    AuditoriumHours = 1,
                    SelfStudyHours = 0
                },
                new ModuleTopic
                {
                    Id = targetTopicId,
                    ModuleId = targetModuleId,
                    Order = 1,
                    TopicCode = "ЦІЛЬ-1",
                    LessonTypeId = lessonTypeId,
                    TotalHours = 1,
                    AuditoriumHours = 1,
                    SelfStudyHours = 0
                });
            Db.Teachers.AddRange(
                new Teacher
                {
                    Id = 948,
                    FullName = "Викладач резервної кафедри",
                    DepartmentId = otherDepartmentId
                },
                new Teacher
                {
                    Id = 949,
                    FullName = "Викладач цільового модуля",
                    DepartmentId = topicDepartmentId
                });
            Db.TeacherModules.AddRange(
                new TeacherModule { TeacherId = 948, ModuleId = movableModuleId },
                new TeacherModule { TeacherId = 949, ModuleId = targetModuleId });
            Db.TeacherWorkingHours.AddRange(
                new TeacherWorkingHour
                {
                    TeacherId = 948,
                    DayOfWeek = DayOfWeek.Monday,
                    Start = firstSlotStart,
                    End = secondSlotEnd
                },
                new TeacherWorkingHour
                {
                    TeacherId = 949,
                    DayOfWeek = DayOfWeek.Monday,
                    Start = secondSlotStart,
                    End = secondSlotEnd
                });
            Db.Buildings.Add(new Building
            {
                Id = 950,
                Name = "Навчальний корпус"
            });
            Db.Rooms.AddRange(
                new Room
                {
                    Id = 951,
                    Name = "КР-101",
                    Capacity = 30,
                    BuildingId = 950
                },
                new Room
                {
                    Id = 952,
                    Name = "КР-102",
                    Capacity = 30,
                    BuildingId = 950
                });
            Db.ModuleRooms.AddRange(
                new ModuleRoom { ModuleId = movableModuleId, RoomId = 951 },
                new ModuleRoom { ModuleId = targetModuleId, RoomId = 952 });
            Db.TimeSlots.AddRange(
                new TimeSlot
                {
                    Id = 953,
                    CourseId = courseId,
                    DayOfWeek = DayOfWeek.Monday,
                    Start = firstSlotStart,
                    End = firstSlotEnd,
                    SortOrder = 1,
                    IsActive = true
                },
                new TimeSlot
                {
                    Id = 956,
                    CourseId = courseId,
                    DayOfWeek = DayOfWeek.Monday,
                    Start = secondSlotStart,
                    End = secondSlotEnd,
                    SortOrder = 2,
                    IsActive = true
                });
            Db.TeacherDraftItems.Add(new TeacherDraftItem
            {
                Date = date,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = secondSlotStart,
                EndTime = secondSlotEnd,
                GroupId = groupId,
                ModuleId = movableModuleId,
                ModuleTopicId = movableTopicId,
                LessonTypeId = lessonTypeId,
                TeacherId = null,
                RoomId = 951,
                Status = DraftStatus.Draft,
                IsLocked = false
            });

            await Db.SaveChangesAsync();
            return new DepartmentFallbackSeed(
                courseId,
                groupId,
                movableModuleId,
                targetModuleId,
                topicDepartmentId,
                date);
        }

        public async Task<CurriculumProgressSeed> SeedCurriculumProgressScenarioAsync(
            DateOnly generationDate,
            DateOnly? academicPeriodStartDate,
            int targetHours,
            int topicHours)
        {
            var course = new Course
            {
                Name = "Курс перевірки навчального періоду",
                DurationWeeks = 52,
                AcademicPeriodStartDate = academicPeriodStartDate
            };
            var group = new Group
            {
                Name = "ПЕР-1",
                StudentsCount = 20,
                Course = course
            };
            var module = new Module
            {
                Code = "ПЕР",
                Title = "Прогрес навчального періоду",
                Credits = 1,
                Course = course
            };
            var lessonType = new LessonTypeRef
            {
                Code = "WORK",
                Name = "Навчальне заняття",
                IsActive = true,
                RequiresRoom = true,
                RequiresTeacher = true,
                BlocksRoom = true,
                BlocksTeacher = true,
                CountInPlan = true,
                CountInLoad = true
            };
            var topic = new ModuleTopic
            {
                Module = module,
                Order = 1,
                TopicCode = "ПЕР-1",
                LessonType = lessonType,
                TotalHours = topicHours,
                AuditoriumHours = topicHours,
                SelfStudyHours = 0
            };
            var start = new TimeOnly(8, 0);
            var end = new TimeOnly(9, 0);
            var teacher = new Teacher { FullName = "Викладач навчального періоду" };
            var building = new Building { Name = "Корпус навчального періоду" };
            var room = new Room
            {
                Name = "ПЕР-101",
                Capacity = 30,
                Building = building
            };
            Db.AddRange(
                course,
                group,
                module,
                lessonType,
                topic,
                teacher,
                building,
                room,
                new ModulePlan
                {
                    Course = course,
                    Module = module,
                    TargetHours = targetHours,
                    ScheduledHours = 0,
                    IsActive = true
                },
                new TimeSlot
                {
                    Course = course,
                    DayOfWeek = generationDate.DayOfWeek,
                    Start = start,
                    End = end,
                    SortOrder = 1,
                    IsActive = true
                },
                new TeacherModule
                {
                    Teacher = teacher,
                    Module = module
                },
                new TeacherWorkingHour
                {
                    Teacher = teacher,
                    DayOfWeek = generationDate.DayOfWeek,
                    Start = start,
                    End = end
                },
                new ModuleRoom
                {
                    Module = module,
                    Room = room
                });
            await Db.SaveChangesAsync();

            return new CurriculumProgressSeed(
                course.Id,
                group.Id,
                module.Id,
                topic.Id,
                lessonType.Id,
                teacher.Id,
                generationDate,
                start,
                end);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
