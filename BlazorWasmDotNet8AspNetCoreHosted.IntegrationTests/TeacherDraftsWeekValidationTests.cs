using System.Data.Common;
using System.Text.Json;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class TeacherDraftsWeekValidationTests
{
    private static readonly DateOnly Monday = new(2026, 5, 4);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Validate_week_reports_module_sequence_regression_as_error()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        fixture.Db.ModuleSequenceItems.AddRange(
            new ModuleSequenceItem
            {
                CourseId = fixture.CourseId,
                ModuleId = fixture.FirstModuleId,
                Order = 1,
                GroupOrder = 1
            },
            new ModuleSequenceItem
            {
                CourseId = fixture.CourseId,
                ModuleId = fixture.SecondModuleId,
                Order = 2,
                GroupOrder = 2
            });
        fixture.Db.TeacherDraftItems.AddRange(
            fixture.CreateDraft(fixture.SecondModuleId, new TimeOnly(8, 0), new TimeOnly(9, 0)),
            fixture.CreateDraft(fixture.FirstModuleId, new TimeOnly(9, 0), new TimeOnly(10, 0)));
        await fixture.Db.SaveChangesAsync();

        var report = await new TeacherDraftsWeekValidationService(fixture.Db).ValidateAsync(Monday);

        Assert.Contains(report.Issues, issue =>
            issue.Severity == "error"
            && issue.Code == "week-hard-rule-violation"
            && issue.Description.Contains("блоком послідовності", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Validate_week_reports_module_sequence_regression_against_published_future_week()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        AddTwoBlockSequence(fixture);
        var course = await fixture.Db.Courses.SingleAsync(item => item.Id == fixture.CourseId);
        course.DurationWeeks = 2;
        fixture.Db.TeacherDraftItems.Add(
            fixture.CreateDraft(fixture.SecondModuleId, new TimeOnly(8, 0), new TimeOnly(9, 0)));
        var future = fixture.CreateScheduleItem(
            fixture.FirstModuleId,
            new TimeOnly(8, 0),
            new TimeOnly(9, 0));
        future.Date = Monday.AddDays(7);
        future.DayOfWeek = DayOfWeek.Monday;
        fixture.Db.ScheduleItems.Add(future);
        await fixture.Db.SaveChangesAsync();

        var report = await new TeacherDraftsWeekValidationService(fixture.Db).ValidateAsync(Monday);

        Assert.Contains(report.Issues, issue =>
            issue.Code == "week-hard-rule-violation"
            && issue.Description.Contains("блоком послідовності", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Publish_week_rejects_module_sequence_regression_against_published_future_week()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        AddTwoBlockSequence(fixture);
        var course = await fixture.Db.Courses.SingleAsync(item => item.Id == fixture.CourseId);
        course.DurationWeeks = 2;
        var draft = fixture.CreateDraft(
            fixture.SecondModuleId,
            new TimeOnly(8, 0),
            new TimeOnly(9, 0));
        var future = fixture.CreateScheduleItem(
            fixture.FirstModuleId,
            new TimeOnly(8, 0),
            new TimeOnly(9, 0));
        future.Date = Monday.AddDays(7);
        future.DayOfWeek = DayOfWeek.Monday;
        fixture.Db.AddRange(draft, future);
        await fixture.Db.SaveChangesAsync();

        var action = await new TeacherDraftsPublishService(
                fixture.Db,
                new RulesService(fixture.Db),
                new AggregatesService(fixture.Db))
            .PublishWeekAsync(new PublishWeekRequest(Monday, null, PublishTestScopeRevision.Read(fixture.Db, Monday)));

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var payload = Assert.IsType<PublishWeekResults>(ok.Value);
        Assert.Equal(0, payload.Created);
        Assert.Contains(payload.Warnings, warning =>
            warning.Contains("блоком послідовності", StringComparison.OrdinalIgnoreCase));
        Assert.True(await fixture.Db.TeacherDraftItems.AnyAsync(item => item.Id == draft.Id));
        Assert.Equal(
            new[] { future.Id },
            await fixture.Db.ScheduleItems.Select(item => item.Id).ToArrayAsync());
    }

    [Fact]
    public async Task Validate_week_allows_topic_reordering_against_published_future_week()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        var (firstTopic, secondTopic) = await AddOrderedTopicsAsync(fixture);
        var course = await fixture.Db.Courses.SingleAsync(item => item.Id == fixture.CourseId);
        course.DurationWeeks = 2;
        var draft = fixture.CreateDraft(
            fixture.FirstModuleId,
            new TimeOnly(8, 0),
            new TimeOnly(9, 0));
        draft.ModuleTopicId = secondTopic.Id;
        var future = fixture.CreateScheduleItem(
            fixture.FirstModuleId,
            new TimeOnly(8, 0),
            new TimeOnly(9, 0));
        future.Date = Monday.AddDays(7);
        future.DayOfWeek = DayOfWeek.Monday;
        future.ModuleTopicId = firstTopic.Id;
        fixture.Db.AddRange(draft, future);
        await fixture.Db.SaveChangesAsync();

        var report = await new TeacherDraftsWeekValidationService(fixture.Db).ValidateAsync(Monday);

        Assert.DoesNotContain(report.Issues, issue =>
            issue.Code == "week-hard-rule-violation"
            && issue.Description.Contains("поряд", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Publish_week_allows_topic_reordering_against_published_future_week()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        var (firstTopic, secondTopic) = await AddOrderedTopicsAsync(fixture);
        var course = await fixture.Db.Courses.SingleAsync(item => item.Id == fixture.CourseId);
        course.DurationWeeks = 2;
        var draft = fixture.CreateDraft(
            fixture.FirstModuleId,
            new TimeOnly(8, 0),
            new TimeOnly(9, 0));
        draft.ModuleTopicId = secondTopic.Id;
        var future = fixture.CreateScheduleItem(
            fixture.FirstModuleId,
            new TimeOnly(8, 0),
            new TimeOnly(9, 0));
        future.Date = Monday.AddDays(7);
        future.DayOfWeek = DayOfWeek.Monday;
        future.ModuleTopicId = firstTopic.Id;
        fixture.Db.AddRange(draft, future);
        await fixture.Db.SaveChangesAsync();

        var action = await new TeacherDraftsPublishService(
                fixture.Db,
                new RulesService(fixture.Db),
                new AggregatesService(fixture.Db))
            .PublishWeekAsync(new PublishWeekRequest(Monday, null, PublishTestScopeRevision.Read(fixture.Db, Monday)));

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var payload = Assert.IsType<PublishWeekResults>(ok.Value);
        Assert.Equal(1, payload.Created);
        Assert.DoesNotContain(payload.Warnings, warning =>
            warning.Contains("поряд", StringComparison.OrdinalIgnoreCase));
        Assert.False(await fixture.Db.TeacherDraftItems.AnyAsync(item => item.Id == draft.Id));
        Assert.Equal(2, await fixture.Db.ScheduleItems.CountAsync());
        Assert.True(await fixture.Db.ScheduleItems.AnyAsync(item => item.Id == future.Id));
        Assert.True(await fixture.Db.ScheduleItems.AnyAsync(item =>
            item.Date == Monday
            && item.GroupId == fixture.GroupId
            && item.ModuleTopicId == secondTopic.Id));
    }

    [Fact]
    public async Task Validate_week_ignores_future_sequence_outside_academic_period()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        AddTwoBlockSequence(fixture);
        var course = await fixture.Db.Courses.SingleAsync(item => item.Id == fixture.CourseId);
        course.DurationWeeks = 1;
        fixture.Db.TeacherDraftItems.Add(
            fixture.CreateDraft(fixture.SecondModuleId, new TimeOnly(8, 0), new TimeOnly(9, 0)));
        var outsidePeriod = fixture.CreateScheduleItem(
            fixture.FirstModuleId,
            new TimeOnly(8, 0),
            new TimeOnly(9, 0));
        outsidePeriod.Date = Monday.AddDays(7);
        outsidePeriod.DayOfWeek = DayOfWeek.Monday;
        fixture.Db.ScheduleItems.Add(outsidePeriod);
        await fixture.Db.SaveChangesAsync();

        var report = await new TeacherDraftsWeekValidationService(fixture.Db).ValidateAsync(Monday);

        Assert.DoesNotContain(report.Issues, issue =>
            issue.Description.Contains("блоком послідовності", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Validate_week_ignores_future_topic_outside_academic_period()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        var (firstTopic, secondTopic) = await AddOrderedTopicsAsync(fixture);
        var course = await fixture.Db.Courses.SingleAsync(item => item.Id == fixture.CourseId);
        course.DurationWeeks = 1;
        var draft = fixture.CreateDraft(
            fixture.FirstModuleId,
            new TimeOnly(8, 0),
            new TimeOnly(9, 0));
        draft.ModuleTopicId = secondTopic.Id;
        var outsidePeriod = fixture.CreateScheduleItem(
            fixture.FirstModuleId,
            new TimeOnly(8, 0),
            new TimeOnly(9, 0));
        outsidePeriod.Date = Monday.AddDays(7);
        outsidePeriod.DayOfWeek = DayOfWeek.Monday;
        outsidePeriod.ModuleTopicId = firstTopic.Id;
        fixture.Db.AddRange(draft, outsidePeriod);
        await fixture.Db.SaveChangesAsync();

        var report = await new TeacherDraftsWeekValidationService(fixture.Db).ValidateAsync(Monday);

        Assert.DoesNotContain(report.Issues, issue =>
            issue.Description.Contains("має порядок", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Validate_week_ignores_topic_order_rows_after_academic_period_end()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        var (firstTopic, secondTopic) = await AddOrderedTopicsAsync(fixture);
        var course = await fixture.Db.Courses.SingleAsync(item => item.Id == fixture.CourseId);
        course.DurationWeeks = 1;
        var laterTopic = fixture.CreateDraft(
            fixture.FirstModuleId,
            new TimeOnly(8, 0),
            new TimeOnly(9, 0));
        laterTopic.Date = Monday.AddDays(7);
        laterTopic.ModuleTopicId = secondTopic.Id;
        var earlierTopic = fixture.CreateDraft(
            fixture.FirstModuleId,
            new TimeOnly(9, 0),
            new TimeOnly(10, 0));
        earlierTopic.Date = Monday.AddDays(7);
        earlierTopic.ModuleTopicId = firstTopic.Id;
        fixture.Db.TeacherDraftItems.AddRange(laterTopic, earlierTopic);
        await fixture.Db.SaveChangesAsync();

        var report = await new TeacherDraftsWeekValidationService(fixture.Db)
            .ValidateAsync(Monday.AddDays(7));

        Assert.DoesNotContain(report.Issues, issue =>
            issue.Description.Contains("має порядок", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Validate_week_keeps_draft_identity_when_logical_event_duplicates_schedule_row()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        fixture.Db.ModuleSequenceItems.AddRange(
            new ModuleSequenceItem
            {
                CourseId = fixture.CourseId,
                ModuleId = fixture.FirstModuleId,
                Order = 1,
                GroupOrder = 1
            },
            new ModuleSequenceItem
            {
                CourseId = fixture.CourseId,
                ModuleId = fixture.SecondModuleId,
                Order = 2,
                GroupOrder = 2
            });
        var duplicateDraft = fixture.CreateDraft(
            fixture.SecondModuleId,
            new TimeOnly(8, 0),
            new TimeOnly(9, 0));
        duplicateDraft.BatchKey = "logical-event";
        fixture.Db.TeacherDraftItems.Add(duplicateDraft);
        fixture.Db.ScheduleItems.AddRange(
            fixture.CreateScheduleItem(
                fixture.SecondModuleId,
                new TimeOnly(8, 0),
                new TimeOnly(9, 0),
                "logical-event"),
            fixture.CreateScheduleItem(
                fixture.FirstModuleId,
                new TimeOnly(9, 0),
                new TimeOnly(10, 0)));
        await fixture.Db.SaveChangesAsync();

        var report = await new TeacherDraftsWeekValidationService(fixture.Db).ValidateAsync(Monday);

        Assert.Contains(report.Issues, issue =>
            issue.Description.Contains("блоком послідовності", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Validate_week_ignores_current_rows_before_academic_period_start()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        var periodStart = Monday.AddDays(1);
        var course = await fixture.Db.Courses.SingleAsync(item => item.Id == fixture.CourseId);
        course.AcademicPeriodStartDate = periodStart;
        fixture.Db.ModuleSequenceItems.AddRange(
            new ModuleSequenceItem
            {
                CourseId = fixture.CourseId,
                ModuleId = fixture.FirstModuleId,
                Order = 1,
                GroupOrder = 1
            },
            new ModuleSequenceItem
            {
                CourseId = fixture.CourseId,
                ModuleId = fixture.SecondModuleId,
                Order = 2,
                GroupOrder = 2
            });
        fixture.Db.TimeSlots.Add(new TimeSlot
        {
            CourseId = fixture.CourseId,
            DayOfWeek = DayOfWeek.Tuesday,
            Start = new TimeOnly(8, 0),
            End = new TimeOnly(9, 0),
            SortOrder = 1,
            IsActive = true
        });
        var beforePeriod = fixture.CreateDraft(
            fixture.SecondModuleId,
            new TimeOnly(8, 0),
            new TimeOnly(9, 0));
        var insidePeriod = fixture.CreateDraft(
            fixture.FirstModuleId,
            new TimeOnly(8, 0),
            new TimeOnly(9, 0));
        insidePeriod.Date = periodStart;
        insidePeriod.DayOfWeek = DayOfWeek.Tuesday;
        fixture.Db.TeacherDraftItems.AddRange(beforePeriod, insidePeriod);
        await fixture.Db.SaveChangesAsync();

        var report = await new TeacherDraftsWeekValidationService(fixture.Db).ValidateAsync(Monday);

        Assert.DoesNotContain(report.Issues, issue =>
            issue.Description.Contains("блоком послідовності", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Validate_week_ignores_topic_order_before_academic_period_start()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        var periodStart = Monday.AddDays(1);
        var course = await fixture.Db.Courses.SingleAsync(item => item.Id == fixture.CourseId);
        course.AcademicPeriodStartDate = periodStart;
        fixture.Db.TimeSlots.Add(new TimeSlot
        {
            CourseId = fixture.CourseId,
            DayOfWeek = DayOfWeek.Tuesday,
            Start = new TimeOnly(8, 0),
            End = new TimeOnly(9, 0),
            SortOrder = 1,
            IsActive = true
        });
        var firstTopic = new ModuleTopic
        {
            ModuleId = fixture.FirstModuleId,
            Order = 1,
            TopicCode = "T1",
            LessonTypeId = fixture.LessonTypeId,
            TotalHours = 1,
            AuditoriumHours = 1
        };
        var secondTopic = new ModuleTopic
        {
            ModuleId = fixture.FirstModuleId,
            Order = 2,
            TopicCode = "T2",
            LessonTypeId = fixture.LessonTypeId,
            TotalHours = 1,
            AuditoriumHours = 1
        };
        fixture.Db.ModuleTopics.AddRange(firstTopic, secondTopic);
        await fixture.Db.SaveChangesAsync();
        var beforePeriod = fixture.CreateDraft(
            fixture.FirstModuleId,
            new TimeOnly(8, 0),
            new TimeOnly(9, 0));
        beforePeriod.ModuleTopicId = secondTopic.Id;
        var insidePeriod = fixture.CreateDraft(
            fixture.FirstModuleId,
            new TimeOnly(8, 0),
            new TimeOnly(9, 0));
        insidePeriod.Date = periodStart;
        insidePeriod.DayOfWeek = DayOfWeek.Tuesday;
        insidePeriod.ModuleTopicId = firstTopic.Id;
        fixture.Db.TeacherDraftItems.AddRange(beforePeriod, insidePeriod);
        await fixture.Db.SaveChangesAsync();

        var report = await new TeacherDraftsWeekValidationService(fixture.Db).ValidateAsync(Monday);

        Assert.DoesNotContain(report.Issues, issue =>
            issue.Description.Contains("має порядок", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Validate_week_checks_every_course_present_in_the_week()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        fixture.Db.TeacherDraftItems.Add(
            fixture.CreateDraft(fixture.FirstModuleId, new TimeOnly(8, 0), new TimeOnly(9, 0)));
        var secondCourse = new Course
        {
            Name = "Другий курс",
            DurationWeeks = 10,
            AcademicPeriodStartDate = Monday
        };
        fixture.Db.Courses.Add(secondCourse);
        await fixture.Db.SaveChangesAsync();
        var secondGroup = new Group
        {
            Name = "9401",
            StudentsCount = 20,
            CourseId = secondCourse.Id
        };
        var earlierModule = new Module
        {
            Code = "C2-M1",
            Title = "Перший модуль другого курсу",
            CourseId = secondCourse.Id
        };
        var laterModule = new Module
        {
            Code = "C2-M2",
            Title = "Другий модуль другого курсу",
            CourseId = secondCourse.Id
        };
        fixture.Db.Groups.Add(secondGroup);
        fixture.Db.Modules.AddRange(earlierModule, laterModule);
        fixture.Db.TimeSlots.AddRange(
            new TimeSlot
            {
                CourseId = secondCourse.Id,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(8, 0),
                End = new TimeOnly(9, 0),
                SortOrder = 1,
                IsActive = true
            },
            new TimeSlot
            {
                CourseId = secondCourse.Id,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(9, 0),
                End = new TimeOnly(10, 0),
                SortOrder = 2,
                IsActive = true
            });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ModuleSequenceItems.AddRange(
            new ModuleSequenceItem
            {
                CourseId = secondCourse.Id,
                ModuleId = earlierModule.Id,
                Order = 1,
                GroupOrder = 1
            },
            new ModuleSequenceItem
            {
                CourseId = secondCourse.Id,
                ModuleId = laterModule.Id,
                Order = 2,
                GroupOrder = 2
            });
        fixture.Db.TeacherDraftItems.AddRange(
            new TeacherDraftItem
            {
                Date = Monday,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(9, 0),
                GroupId = secondGroup.Id,
                ModuleId = laterModule.Id,
                LessonTypeId = fixture.LessonTypeId
            },
            new TeacherDraftItem
            {
                Date = Monday,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = new TimeOnly(9, 0),
                EndTime = new TimeOnly(10, 0),
                GroupId = secondGroup.Id,
                ModuleId = earlierModule.Id,
                LessonTypeId = fixture.LessonTypeId
            });
        await fixture.Db.SaveChangesAsync();

        var report = await new TeacherDraftsWeekValidationService(fixture.Db).ValidateAsync(Monday);

        Assert.Contains(report.Issues, issue =>
            issue.Description.Contains("9401", StringComparison.Ordinal)
            && issue.Description.Contains("блоком послідовності", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Validate_week_reports_cross_course_teacher_overlap_once()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        var lessonType = new LessonTypeRef
        {
            Code = "BLOCKING-WORK",
            Name = "Заняття з викладачем",
            RequiresRoom = false,
            RequiresTeacher = true,
            BlocksRoom = false,
            BlocksTeacher = true
        };
        var teacher = new Teacher { FullName = "Спільний викладач" };
        var secondCourse = new Course
        {
            Name = "Другий курс",
            DurationWeeks = 10,
            AcademicPeriodStartDate = Monday
        };
        fixture.Db.LessonTypes.Add(lessonType);
        fixture.Db.Teachers.Add(teacher);
        fixture.Db.Courses.Add(secondCourse);
        await fixture.Db.SaveChangesAsync();
        var secondGroup = new Group
        {
            Name = "9401",
            StudentsCount = 20,
            CourseId = secondCourse.Id
        };
        var secondModule = new Module
        {
            Code = "C2-M1",
            Title = "Модуль другого курсу",
            CourseId = secondCourse.Id
        };
        fixture.Db.Groups.Add(secondGroup);
        fixture.Db.Modules.Add(secondModule);
        fixture.Db.TeacherModules.Add(new TeacherModule
        {
            TeacherId = teacher.Id,
            ModuleId = fixture.FirstModuleId
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.TeacherModules.Add(new TeacherModule
        {
            TeacherId = teacher.Id,
            ModuleId = secondModule.Id
        });
        fixture.Db.TeacherDraftItems.AddRange(
            new TeacherDraftItem
            {
                Date = Monday,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(9, 0),
                GroupId = fixture.GroupId,
                ModuleId = fixture.FirstModuleId,
                LessonTypeId = lessonType.Id,
                TeacherId = teacher.Id
            },
            new TeacherDraftItem
            {
                Date = Monday,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(9, 0),
                GroupId = secondGroup.Id,
                ModuleId = secondModule.Id,
                LessonTypeId = lessonType.Id,
                TeacherId = teacher.Id
            });
        await fixture.Db.SaveChangesAsync();

        var report = await new TeacherDraftsWeekValidationService(fixture.Db).ValidateAsync(Monday);

        var overlaps = report.Issues
            .Where(issue => issue.Description.Contains(
                "перетин викладача Спільний викладач",
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Single(overlaps);
        Assert.Equal("error", overlaps[0].Severity);
    }

    [Fact]
    public async Task Validate_week_allows_modules_inside_the_same_sequence_block()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        fixture.Db.ModuleSequenceItems.AddRange(
            new ModuleSequenceItem
            {
                CourseId = fixture.CourseId,
                ModuleId = fixture.FirstModuleId,
                Order = 1,
                GroupOrder = 1
            },
            new ModuleSequenceItem
            {
                CourseId = fixture.CourseId,
                ModuleId = fixture.SecondModuleId,
                Order = 2,
                GroupOrder = 1
            });
        fixture.Db.TeacherDraftItems.AddRange(
            fixture.CreateDraft(fixture.SecondModuleId, new TimeOnly(8, 0), new TimeOnly(9, 0)),
            fixture.CreateDraft(fixture.FirstModuleId, new TimeOnly(9, 0), new TimeOnly(10, 0)));
        await fixture.Db.SaveChangesAsync();

        var report = await new TeacherDraftsWeekValidationService(fixture.Db).ValidateAsync(Monday);

        Assert.DoesNotContain(report.Issues, issue =>
            issue.Description.Contains("блоком послідовності", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Validate_week_ignores_filler_module_in_sequence_check()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        fixture.Db.ModuleSequenceItems.AddRange(
            new ModuleSequenceItem
            {
                CourseId = fixture.CourseId,
                ModuleId = fixture.FirstModuleId,
                Order = 1,
                GroupOrder = 1
            },
            new ModuleSequenceItem
            {
                CourseId = fixture.CourseId,
                ModuleId = fixture.SecondModuleId,
                Order = 2,
                GroupOrder = 2
            });
        fixture.Db.ModuleFillers.Add(new ModuleFiller
        {
            CourseId = fixture.CourseId,
            ModuleId = fixture.FirstModuleId
        });
        fixture.Db.TeacherDraftItems.AddRange(
            fixture.CreateDraft(fixture.SecondModuleId, new TimeOnly(8, 0), new TimeOnly(9, 0)),
            fixture.CreateDraft(fixture.FirstModuleId, new TimeOnly(9, 0), new TimeOnly(10, 0)));
        await fixture.Db.SaveChangesAsync();

        var report = await new TeacherDraftsWeekValidationService(fixture.Db).ValidateAsync(Monday);

        Assert.DoesNotContain(report.Issues, issue =>
            issue.Description.Contains("блоком послідовності", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Validate_and_publish_reject_mixed_status_rows_in_one_batch()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        var first = fixture.CreateDraft(fixture.FirstModuleId, new TimeOnly(8, 0), new TimeOnly(9, 0));
        var second = fixture.CreateDraft(fixture.FirstModuleId, new TimeOnly(8, 0), new TimeOnly(9, 0));
        first.BatchKey = "mixed-status-event";
        second.BatchKey = first.BatchKey;
        second.Status = DraftStatus.Published;
        fixture.Db.TeacherDraftItems.AddRange(first, second);
        await fixture.Db.SaveChangesAsync();

        var report = await new TeacherDraftsWeekValidationService(fixture.Db).ValidateAsync(Monday);
        var result = await new TeacherDraftsPublishService(
                fixture.Db,
                new RulesService(fixture.Db),
                new AggregatesService(fixture.Db))
            .PublishWeekAsync(new PublishWeekRequest(Monday, null, PublishTestScopeRevision.Read(fixture.Db, Monday)));

        Assert.Contains(report.Issues, issue =>
            issue.Code == "week-publish-package-violation"
            && issue.Description.Contains("змішані статуси", StringComparison.OrdinalIgnoreCase));
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<PublishWeekResults>(ok.Value);
        Assert.Equal(0, payload.Created);
        Assert.Contains(payload.Warnings, warning =>
            warning.Contains("змішані статуси", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Validate_and_publish_reject_logical_event_room_or_self_study_mismatch(
        bool mismatchRoom)
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        var lessonType = new LessonTypeRef
        {
            Code = "ROOM-EVENT",
            Name = "Аудиторне заняття",
            RequiresRoom = true,
            RequiresTeacher = false,
            BlocksRoom = true,
            BlocksTeacher = false
        };
        var building = new Building { Name = "Корпус" };
        fixture.Db.LessonTypes.Add(lessonType);
        fixture.Db.Buildings.Add(building);
        await fixture.Db.SaveChangesAsync();
        var firstRoom = new Room { Name = "101", Capacity = 40, BuildingId = building.Id };
        var secondRoom = new Room { Name = "102", Capacity = 40, BuildingId = building.Id };
        fixture.Db.Rooms.AddRange(firstRoom, secondRoom);
        await fixture.Db.SaveChangesAsync();
        var first = fixture.CreateDraft(fixture.FirstModuleId, new TimeOnly(8, 0), new TimeOnly(9, 0));
        var second = fixture.CreateDraft(fixture.FirstModuleId, new TimeOnly(8, 0), new TimeOnly(9, 0));
        first.LessonTypeId = lessonType.Id;
        second.LessonTypeId = lessonType.Id;
        first.BatchKey = "resource-mismatch-event";
        second.BatchKey = first.BatchKey;
        first.RoomId = firstRoom.Id;
        second.RoomId = mismatchRoom ? secondRoom.Id : firstRoom.Id;
        second.IsSelfStudy = !mismatchRoom;
        fixture.Db.TeacherDraftItems.AddRange(first, second);
        await fixture.Db.SaveChangesAsync();

        var report = await new TeacherDraftsWeekValidationService(fixture.Db).ValidateAsync(Monday);
        var result = await new TeacherDraftsPublishService(
                fixture.Db,
                new RulesService(fixture.Db),
                new AggregatesService(fixture.Db))
            .PublishWeekAsync(new PublishWeekRequest(Monday, null, PublishTestScopeRevision.Read(fixture.Db, Monday)));

        Assert.Contains(report.Issues, issue =>
            issue.Code == "week-publish-rule-violation"
            && issue.Description.Contains("різні аудиторії або режим", StringComparison.OrdinalIgnoreCase));
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<PublishWeekResults>(ok.Value);
        Assert.Equal(0, payload.Created);
        Assert.Contains(payload.Warnings, warning =>
            warning.Contains("різні аудиторії або режим", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Validate_and_publish_reject_module_from_another_course()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        var secondCourse = new Course
        {
            Name = "Другий курс",
            DurationWeeks = 10,
            AcademicPeriodStartDate = Monday
        };
        fixture.Db.Courses.Add(secondCourse);
        await fixture.Db.SaveChangesAsync();
        var secondGroup = new Group
        {
            Name = "9401",
            StudentsCount = 20,
            CourseId = secondCourse.Id
        };
        fixture.Db.Groups.Add(secondGroup);
        fixture.Db.TimeSlots.Add(new TimeSlot
        {
            CourseId = secondCourse.Id,
            DayOfWeek = DayOfWeek.Monday,
            Start = new TimeOnly(8, 0),
            End = new TimeOnly(9, 0),
            SortOrder = 1,
            IsActive = true
        });
        await fixture.Db.SaveChangesAsync();
        var draft = fixture.CreateDraft(fixture.FirstModuleId, new TimeOnly(8, 0), new TimeOnly(9, 0));
        draft.GroupId = secondGroup.Id;
        fixture.Db.TeacherDraftItems.Add(draft);
        await fixture.Db.SaveChangesAsync();

        var report = await new TeacherDraftsWeekValidationService(fixture.Db).ValidateAsync(Monday);
        var result = await new TeacherDraftsPublishService(
                fixture.Db,
                new RulesService(fixture.Db),
                new AggregatesService(fixture.Db))
            .PublishWeekAsync(new PublishWeekRequest(Monday, null, PublishTestScopeRevision.Read(fixture.Db, Monday)));

        Assert.Contains(report.Issues, issue =>
            issue.Code == "week-publish-rule-violation"
            && issue.Description.Contains("не належить курсу групи", StringComparison.OrdinalIgnoreCase));
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<PublishWeekResults>(ok.Value);
        Assert.Equal(0, payload.Created);
        Assert.Contains(payload.Warnings, warning =>
            warning.Contains("не належить курсу групи", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Validate_and_publish_ignore_stale_room_for_no_room_lesson_type()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        var building = new Building { Name = "Корпус" };
        fixture.Db.Buildings.Add(building);
        await fixture.Db.SaveChangesAsync();
        var staleRoom = new Room { Name = "Стара аудиторія", Capacity = 1, BuildingId = building.Id };
        fixture.Db.Rooms.Add(staleRoom);
        await fixture.Db.SaveChangesAsync();
        var draft = fixture.CreateDraft(fixture.FirstModuleId, new TimeOnly(8, 0), new TimeOnly(9, 0));
        draft.RoomId = staleRoom.Id;
        fixture.Db.TeacherDraftItems.Add(draft);
        await fixture.Db.SaveChangesAsync();

        var report = await new TeacherDraftsWeekValidationService(fixture.Db).ValidateAsync(Monday);
        var result = await new TeacherDraftsPublishService(
                fixture.Db,
                new RulesService(fixture.Db),
                new AggregatesService(fixture.Db))
            .PublishWeekAsync(new PublishWeekRequest(Monday, null, report.ScopeRevision));

        Assert.DoesNotContain(report.Issues, issue =>
            string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase));
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<PublishWeekResults>(ok.Value);
        Assert.Equal(1, payload.Created);
        var published = Assert.Single(await fixture.Db.ScheduleItems.AsNoTracking().ToListAsync());
        Assert.Null(published.RoomId);
    }

    [Fact]
    public async Task Bulk_publish_preflight_query_count_does_not_scale_with_row_count()
    {
        var queryCounter = new QueryCountingInterceptor();
        await using var fixture = await WeekValidationFixture.CreateAsync(queryCounter);
        var first = fixture.CreateDraft(fixture.FirstModuleId, new TimeOnly(8, 0), new TimeOnly(9, 0));
        fixture.Db.TeacherDraftItems.Add(first);
        await fixture.Db.SaveChangesAsync();
        queryCounter.Reset();
        _ = await TeacherDraftsPublishService.ValidatePublishCandidatesAsync(
            fixture.Db,
            new RulesService(fixture.Db),
            new[] { first },
            Monday,
            Monday.AddDays(7));
        var singleRowQueryCount = queryCounter.ReaderCommandCount;
        var additional = Enumerable.Range(0, 149)
            .Select(_ => fixture.CreateDraft(
                fixture.FirstModuleId,
                new TimeOnly(8, 0),
                new TimeOnly(9, 0)))
            .ToList();
        fixture.Db.TeacherDraftItems.AddRange(additional);
        await fixture.Db.SaveChangesAsync();
        var allDrafts = new[] { first }.Concat(additional).ToList();
        queryCounter.Reset();

        _ = await TeacherDraftsPublishService.ValidatePublishCandidatesAsync(
            fixture.Db,
            new RulesService(fixture.Db),
            allDrafts,
            Monday,
            Monday.AddDays(7));

        Assert.Equal(singleRowQueryCount, queryCounter.ReaderCommandCount);
        Assert.InRange(singleRowQueryCount, 1, 20);
    }

    [Fact]
    public async Task Validate_week_reports_applied_scope_change_after_manual_deletion()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        var draft = fixture.CreateDraft(fixture.FirstModuleId, new TimeOnly(8, 0), new TimeOnly(9, 0));
        fixture.Db.TeacherDraftItems.Add(draft);
        await fixture.Db.SaveChangesAsync();
        await fixture.AddAppliedPlanAsync(draft);

        fixture.Db.TeacherDraftItems.Remove(draft);
        await fixture.Db.SaveChangesAsync();

        var report = await new TeacherDraftsWeekValidationService(fixture.Db).ValidateAsync(Monday);

        Assert.Contains(report.Issues, issue =>
            issue.Severity == "warning"
            && issue.Code == "autogen-applied-scope-changed");
    }

    [Fact]
    public async Task Validate_week_reports_applied_scope_change_after_manual_update()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        var draft = fixture.CreateDraft(fixture.FirstModuleId, new TimeOnly(8, 0), new TimeOnly(9, 0));
        fixture.Db.TeacherDraftItems.Add(draft);
        await fixture.Db.SaveChangesAsync();
        await fixture.AddAppliedPlanAsync(draft);

        draft.StartTime = new TimeOnly(9, 0);
        draft.EndTime = new TimeOnly(10, 0);
        await fixture.Db.SaveChangesAsync();

        var report = await new TeacherDraftsWeekValidationService(fixture.Db).ValidateAsync(Monday);

        Assert.Contains(report.Issues, issue =>
            issue.Severity == "warning"
            && issue.Code == "autogen-applied-scope-changed");
    }

    [Fact]
    public async Task Validate_week_ignores_expired_applied_plan_scope_change()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        var draft = fixture.CreateDraft(fixture.FirstModuleId, new TimeOnly(8, 0), new TimeOnly(9, 0));
        fixture.Db.TeacherDraftItems.Add(draft);
        await fixture.Db.SaveChangesAsync();
        var plan = await fixture.AddAppliedPlanAsync(draft);
        plan.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        fixture.Db.TeacherDraftItems.Remove(draft);
        await fixture.Db.SaveChangesAsync();

        var report = await new TeacherDraftsWeekValidationService(fixture.Db).ValidateAsync(Monday);

        Assert.DoesNotContain(report.Issues, issue => issue.Code == "autogen-applied-scope-changed");
    }

    [Fact]
    public async Task Validate_week_keeps_change_warning_when_matching_schedule_row_was_not_published_from_scope()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        var draft = fixture.CreateDraft(fixture.FirstModuleId, new TimeOnly(8, 0), new TimeOnly(9, 0));
        fixture.Db.TeacherDraftItems.Add(draft);
        await fixture.Db.SaveChangesAsync();
        await fixture.AddAppliedPlanAsync(draft);

        fixture.Db.ScheduleItems.Add(fixture.CreateScheduleItem(
            draft.ModuleId,
            draft.StartTime,
            draft.EndTime));
        fixture.Db.TeacherDraftItems.Remove(draft);
        await fixture.Db.SaveChangesAsync();

        var report = await new TeacherDraftsWeekValidationService(fixture.Db).ValidateAsync(Monday);

        Assert.Contains(report.Issues, issue => issue.Code == "autogen-applied-scope-changed");
    }

    [Fact]
    public async Task Publishing_applied_draft_expires_plan_and_clears_change_warning()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        var draft = fixture.CreateDraft(fixture.FirstModuleId, new TimeOnly(8, 0), new TimeOnly(9, 0));
        fixture.Db.TeacherDraftItems.Add(draft);
        await fixture.Db.SaveChangesAsync();
        var plan = await fixture.AddAppliedPlanAsync(draft, includeAppliedMutation: true);
        var previousVersion = plan.Version;

        fixture.Db.ScheduleItems.Add(fixture.CreateScheduleItem(
            draft.ModuleId,
            draft.StartTime,
            draft.EndTime));
        fixture.Db.TeacherDraftItems.Remove(draft);
        await fixture.Db.SaveChangesAsync();
        var expiredCount = await TeacherDraftsAutogenPlanService.ExpireAppliedPlansConsumedByPublicationAsync(
            fixture.Db,
            new[] { draft });

        Assert.Equal(1, expiredCount);
        Assert.Equal((int)AutoGenPlanState.Expired, plan.State);
        Assert.Equal(previousVersion + 1, plan.Version);
        var status = JsonSerializer.Deserialize<AutoGenJobStatus>(
            plan.AutoGenJobRun.StatusJson,
            JsonOptions);
        Assert.Equal(AutoGenPlanState.Expired, status?.Plan?.State);
        Assert.False(status?.Plan?.CanRollback ?? true);

        var report = await new TeacherDraftsWeekValidationService(fixture.Db).ValidateAsync(Monday);
        Assert.DoesNotContain(report.Issues, issue => issue.Code == "autogen-applied-scope-changed");
    }

    [Fact]
    public async Task Publish_week_expires_consumed_applied_plan_in_same_flow()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        var draft = fixture.CreateDraft(fixture.FirstModuleId, new TimeOnly(8, 0), new TimeOnly(9, 0));
        fixture.Db.TeacherDraftItems.Add(draft);
        await fixture.Db.SaveChangesAsync();
        var plan = await fixture.AddAppliedPlanAsync(draft, includeAppliedMutation: true);

        var result = await new TeacherDraftsPublishService(
                fixture.Db,
                new RulesService(fixture.Db),
                new AggregatesService(fixture.Db))
            .PublishWeekAsync(new PublishWeekRequest(Monday, null, PublishTestScopeRevision.Read(fixture.Db, Monday)));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<PublishWeekResults>(ok.Value);
        Assert.Equal(1, payload.Created);
        Assert.Empty(await fixture.Db.TeacherDraftItems.AsNoTracking().ToListAsync());
        Assert.Equal((int)AutoGenPlanState.Expired, plan.State);
        var status = JsonSerializer.Deserialize<AutoGenJobStatus>(
            plan.AutoGenJobRun.StatusJson,
            JsonOptions);
        Assert.Equal(AutoGenPlanState.Expired, status?.Plan?.State);
    }

    [Fact]
    public async Task Failed_publish_keeps_applied_plan_and_drafts_unchanged()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        var draft = fixture.CreateDraft(fixture.FirstModuleId, new TimeOnly(10, 0), new TimeOnly(11, 0));
        fixture.Db.TeacherDraftItems.Add(draft);
        await fixture.Db.SaveChangesAsync();
        var plan = await fixture.AddAppliedPlanAsync(draft, includeAppliedMutation: true);

        var result = await new TeacherDraftsPublishService(
                fixture.Db,
                new RulesService(fixture.Db),
                new AggregatesService(fixture.Db))
            .PublishWeekAsync(new PublishWeekRequest(Monday, null, PublishTestScopeRevision.Read(fixture.Db, Monday)));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<PublishWeekResults>(ok.Value);
        Assert.Equal(0, payload.Created);
        Assert.NotEmpty(payload.Warnings);
        Assert.Single(await fixture.Db.TeacherDraftItems.AsNoTracking().ToListAsync());
        Assert.Equal((int)AutoGenPlanState.Applied, plan.State);
    }

    [Fact]
    public async Task Publish_week_rejects_package_changed_after_validation()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        fixture.Db.TeacherDraftItems.Add(
            fixture.CreateDraft(fixture.FirstModuleId, new TimeOnly(8, 0), new TimeOnly(9, 0)));
        await fixture.Db.SaveChangesAsync();
        var report = await new TeacherDraftsWeekValidationService(fixture.Db).ValidateAsync(Monday);
        fixture.Db.TeacherDraftItems.Add(
            fixture.CreateDraft(fixture.SecondModuleId, new TimeOnly(9, 0), new TimeOnly(10, 0)));
        await fixture.Db.SaveChangesAsync();

        var result = await new TeacherDraftsPublishService(
                fixture.Db,
                new RulesService(fixture.Db),
                new AggregatesService(fixture.Db))
            .PublishWeekAsync(new PublishWeekRequest(Monday, null, report.ScopeRevision));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<PublishWeekResults>(ok.Value);
        Assert.Equal(0, payload.Created);
        Assert.Equal(2, payload.Skipped);
        Assert.Contains(payload.Warnings, warning =>
            warning.Contains("змінилися після останньої перевірки", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(await fixture.Db.ScheduleItems.AsNoTracking().ToListAsync());
        Assert.Equal(2, await fixture.Db.TeacherDraftItems.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Publish_week_rejects_module_sequence_regression_in_second_course()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        fixture.Db.TeacherDraftItems.Add(
            fixture.CreateDraft(fixture.FirstModuleId, new TimeOnly(8, 0), new TimeOnly(9, 0)));
        var secondCourse = new Course
        {
            Name = "Другий курс",
            DurationWeeks = 10,
            AcademicPeriodStartDate = Monday
        };
        fixture.Db.Courses.Add(secondCourse);
        await fixture.Db.SaveChangesAsync();
        var secondGroup = new Group
        {
            Name = "9401",
            StudentsCount = 20,
            CourseId = secondCourse.Id
        };
        var firstModule = new Module
        {
            Code = "C2-M1",
            Title = "Перший модуль другого курсу",
            CourseId = secondCourse.Id
        };
        var secondModule = new Module
        {
            Code = "C2-M2",
            Title = "Другий модуль другого курсу",
            CourseId = secondCourse.Id
        };
        fixture.Db.Groups.Add(secondGroup);
        fixture.Db.Modules.AddRange(firstModule, secondModule);
        fixture.Db.TimeSlots.AddRange(
            new TimeSlot
            {
                CourseId = secondCourse.Id,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(8, 0),
                End = new TimeOnly(9, 0),
                SortOrder = 1,
                IsActive = true
            },
            new TimeSlot
            {
                CourseId = secondCourse.Id,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(9, 0),
                End = new TimeOnly(10, 0),
                SortOrder = 2,
                IsActive = true
            });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ModuleSequenceItems.AddRange(
            new ModuleSequenceItem
            {
                CourseId = secondCourse.Id,
                ModuleId = firstModule.Id,
                Order = 1,
                GroupOrder = 1
            },
            new ModuleSequenceItem
            {
                CourseId = secondCourse.Id,
                ModuleId = secondModule.Id,
                Order = 2,
                GroupOrder = 2
            });
        fixture.Db.TeacherDraftItems.AddRange(
            new TeacherDraftItem
            {
                Date = Monday,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(9, 0),
                GroupId = secondGroup.Id,
                ModuleId = secondModule.Id,
                LessonTypeId = fixture.LessonTypeId,
                Status = DraftStatus.Draft
            },
            new TeacherDraftItem
            {
                Date = Monday,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = new TimeOnly(9, 0),
                EndTime = new TimeOnly(10, 0),
                GroupId = secondGroup.Id,
                ModuleId = firstModule.Id,
                LessonTypeId = fixture.LessonTypeId,
                Status = DraftStatus.Draft
            });
        await fixture.Db.SaveChangesAsync();

        var result = await new TeacherDraftsPublishService(
                fixture.Db,
                new RulesService(fixture.Db),
                new AggregatesService(fixture.Db))
            .PublishWeekAsync(new PublishWeekRequest(Monday, null, PublishTestScopeRevision.Read(fixture.Db, Monday)));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<PublishWeekResults>(ok.Value);
        Assert.Equal(0, payload.Created);
        Assert.Contains(payload.Warnings, warning =>
            warning.Contains("блоком послідовності", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(await fixture.Db.ScheduleItems.AsNoTracking().ToListAsync());
        Assert.Equal(3, await fixture.Db.TeacherDraftItems.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Publish_week_keeps_cross_course_teacher_overlap_validation()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        var lessonType = new LessonTypeRef
        {
            Code = "BLOCKING-WORK",
            Name = "Заняття з викладачем",
            RequiresRoom = false,
            RequiresTeacher = true,
            BlocksRoom = false,
            BlocksTeacher = true
        };
        var teacher = new Teacher { FullName = "Спільний викладач" };
        var secondCourse = new Course
        {
            Name = "Другий курс",
            DurationWeeks = 10,
            AcademicPeriodStartDate = Monday
        };
        fixture.Db.LessonTypes.Add(lessonType);
        fixture.Db.Teachers.Add(teacher);
        fixture.Db.Courses.Add(secondCourse);
        await fixture.Db.SaveChangesAsync();
        var secondGroup = new Group
        {
            Name = "9401",
            StudentsCount = 20,
            CourseId = secondCourse.Id
        };
        var secondModule = new Module
        {
            Code = "C2-M1",
            Title = "Модуль другого курсу",
            CourseId = secondCourse.Id
        };
        fixture.Db.Groups.Add(secondGroup);
        fixture.Db.Modules.Add(secondModule);
        fixture.Db.TimeSlots.Add(new TimeSlot
        {
            CourseId = secondCourse.Id,
            DayOfWeek = DayOfWeek.Monday,
            Start = new TimeOnly(8, 0),
            End = new TimeOnly(9, 0),
            SortOrder = 1,
            IsActive = true
        });
        fixture.Db.TeacherModules.Add(new TeacherModule
        {
            TeacherId = teacher.Id,
            ModuleId = fixture.FirstModuleId
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.TeacherModules.Add(new TeacherModule
        {
            TeacherId = teacher.Id,
            ModuleId = secondModule.Id
        });
        fixture.Db.TeacherDraftItems.AddRange(
            new TeacherDraftItem
            {
                Date = Monday,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(9, 0),
                GroupId = fixture.GroupId,
                ModuleId = fixture.FirstModuleId,
                LessonTypeId = lessonType.Id,
                TeacherId = teacher.Id,
                Status = DraftStatus.Draft
            },
            new TeacherDraftItem
            {
                Date = Monday,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(9, 0),
                GroupId = secondGroup.Id,
                ModuleId = secondModule.Id,
                LessonTypeId = lessonType.Id,
                TeacherId = teacher.Id,
                Status = DraftStatus.Draft
            });
        await fixture.Db.SaveChangesAsync();

        var result = await new TeacherDraftsPublishService(
                fixture.Db,
                new RulesService(fixture.Db),
                new AggregatesService(fixture.Db))
            .PublishWeekAsync(new PublishWeekRequest(Monday, null, PublishTestScopeRevision.Read(fixture.Db, Monday)));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<PublishWeekResults>(ok.Value);
        Assert.Equal(0, payload.Created);
        Assert.Contains(payload.Warnings, warning =>
            warning.Contains("перетин викладача Спільний викладач", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(await fixture.Db.ScheduleItems.AsNoTracking().ToListAsync());
        Assert.Equal(2, await fixture.Db.TeacherDraftItems.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Publishing_first_applied_row_expires_plan_even_when_another_plan_row_remains()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        var first = fixture.CreateDraft(fixture.FirstModuleId, new TimeOnly(8, 0), new TimeOnly(9, 0));
        var second = fixture.CreateDraft(fixture.SecondModuleId, new TimeOnly(9, 0), new TimeOnly(10, 0));
        fixture.Db.TeacherDraftItems.AddRange(first, second);
        await fixture.Db.SaveChangesAsync();
        var plan = await fixture.AddAppliedPlanAsync(new[] { first, second }, includeAppliedMutations: true);
        var previousVersion = plan.Version;

        fixture.Db.TeacherDraftItems.Remove(first);
        await fixture.Db.SaveChangesAsync();
        var expiredCount = await TeacherDraftsAutogenPlanService.ExpireAppliedPlansConsumedByPublicationAsync(
            fixture.Db,
            new[] { first });

        Assert.Equal(1, expiredCount);
        Assert.Equal((int)AutoGenPlanState.Expired, plan.State);
        Assert.Equal(previousVersion + 1, plan.Version);
        Assert.True(await fixture.Db.TeacherDraftItems.AsNoTracking().AnyAsync(item => item.Id == second.Id));
        var status = JsonSerializer.Deserialize<AutoGenJobStatus>(
            plan.AutoGenJobRun.StatusJson,
            JsonOptions);
        Assert.Equal(AutoGenPlanState.Expired, status?.Plan?.State);
        Assert.False(status?.Plan?.CanRollback ?? true);
    }

    [Fact]
    public async Task Publishing_unrelated_row_in_same_scope_does_not_expire_applied_plan()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        var planDraft = fixture.CreateDraft(fixture.FirstModuleId, new TimeOnly(8, 0), new TimeOnly(9, 0));
        fixture.Db.TeacherDraftItems.Add(planDraft);
        await fixture.Db.SaveChangesAsync();
        var plan = await fixture.AddAppliedPlanAsync(planDraft, includeAppliedMutation: true);
        fixture.Db.TeacherDraftItems.Remove(planDraft);
        await fixture.Db.SaveChangesAsync();
        var publishedDraft = fixture.CreateDraft(
            fixture.SecondModuleId,
            new TimeOnly(9, 0),
            new TimeOnly(10, 0));
        publishedDraft.GenerationJobId = "plan-unrelated";
        fixture.Db.TeacherDraftItems.Add(publishedDraft);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.TeacherDraftItems.Remove(publishedDraft);
        await fixture.Db.SaveChangesAsync();

        var expiredCount = await TeacherDraftsAutogenPlanService.ExpireAppliedPlansConsumedByPublicationAsync(
            fixture.Db,
            new[] { publishedDraft });

        Assert.Equal(0, expiredCount);
        Assert.Equal((int)AutoGenPlanState.Applied, plan.State);
    }

    [Fact]
    public async Task Publishing_same_scope_row_does_not_expire_deletion_only_plan()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        var deletedDraft = fixture.CreateDraft(fixture.FirstModuleId, new TimeOnly(8, 0), new TimeOnly(9, 0));
        fixture.Db.TeacherDraftItems.Add(deletedDraft);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.TeacherDraftItems.Remove(deletedDraft);
        await fixture.Db.SaveChangesAsync();
        var plan = await fixture.AddDeletionOnlyAppliedPlanAsync(deletedDraft);
        var publishedDraft = fixture.CreateDraft(
            fixture.SecondModuleId,
            new TimeOnly(9, 0),
            new TimeOnly(10, 0));
        publishedDraft.GenerationJobId = plan.PlanId;

        var expiredCount = await TeacherDraftsAutogenPlanService.ExpireAppliedPlansConsumedByPublicationAsync(
            fixture.Db,
            new[] { publishedDraft });

        Assert.Equal(0, expiredCount);
        Assert.Equal((int)AutoGenPlanState.Applied, plan.State);
    }

    [Fact]
    public async Task Publishing_plan_a_row_does_not_expire_plan_b()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        var planADraft = fixture.CreateDraft(fixture.FirstModuleId, new TimeOnly(8, 0), new TimeOnly(9, 0));
        var planBDraft = fixture.CreateDraft(fixture.SecondModuleId, new TimeOnly(9, 0), new TimeOnly(10, 0));
        fixture.Db.TeacherDraftItems.AddRange(planADraft, planBDraft);
        await fixture.Db.SaveChangesAsync();
        var planA = await fixture.AddAppliedPlanAsync(planADraft, includeAppliedMutation: true);
        var planB = await fixture.AddAppliedPlanAsync(planBDraft, includeAppliedMutation: true);

        fixture.Db.TeacherDraftItems.Remove(planADraft);
        await fixture.Db.SaveChangesAsync();
        var expiredCount = await TeacherDraftsAutogenPlanService.ExpireAppliedPlansConsumedByPublicationAsync(
            fixture.Db,
            new[] { planADraft });

        Assert.Equal(1, expiredCount);
        Assert.Equal((int)AutoGenPlanState.Expired, planA.State);
        Assert.Equal((int)AutoGenPlanState.Applied, planB.State);
    }

    [Fact]
    public async Task Publishing_moved_plan_row_outside_original_scope_still_expires_plan()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        var draft = fixture.CreateDraft(fixture.FirstModuleId, new TimeOnly(8, 0), new TimeOnly(9, 0));
        fixture.Db.TeacherDraftItems.Add(draft);
        await fixture.Db.SaveChangesAsync();
        var plan = await fixture.AddAppliedPlanAsync(draft, includeAppliedMutation: true);
        var movedGroup = new Group
        {
            Name = "9306",
            StudentsCount = 20,
            CourseId = fixture.CourseId
        };
        fixture.Db.Groups.Add(movedGroup);
        await fixture.Db.SaveChangesAsync();
        draft.Date = Monday.AddDays(14);
        draft.DayOfWeek = draft.Date.DayOfWeek;
        draft.GroupId = movedGroup.Id;
        draft.Revision = Guid.NewGuid();
        await fixture.Db.SaveChangesAsync();
        fixture.Db.TeacherDraftItems.Remove(draft);
        await fixture.Db.SaveChangesAsync();

        var expiredCount = await TeacherDraftsAutogenPlanService.ExpireAppliedPlansConsumedByPublicationAsync(
            fixture.Db,
            new[] { draft });

        Assert.Equal(1, expiredCount);
        Assert.Equal((int)AutoGenPlanState.Expired, plan.State);
    }

    [Fact]
    public async Task Publish_week_with_corrupt_job_status_still_expires_consumed_plan()
    {
        await using var fixture = await WeekValidationFixture.CreateAsync();
        var draft = fixture.CreateDraft(fixture.FirstModuleId, new TimeOnly(8, 0), new TimeOnly(9, 0));
        fixture.Db.TeacherDraftItems.Add(draft);
        await fixture.Db.SaveChangesAsync();
        var plan = await fixture.AddAppliedPlanAsync(draft, includeAppliedMutation: true);
        var previousVersion = plan.Version;
        var previousExpiry = plan.ExpiresAtUtc;
        plan.AutoGenJobRun.StatusJson = "{";
        await fixture.Db.SaveChangesAsync();

        var result = await new TeacherDraftsPublishService(
                fixture.Db,
                new RulesService(fixture.Db),
                new AggregatesService(fixture.Db))
            .PublishWeekAsync(new PublishWeekRequest(Monday, null, PublishTestScopeRevision.Read(fixture.Db, Monday)));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<PublishWeekResults>(ok.Value);
        Assert.Equal(1, payload.Created);
        Assert.Equal((int)AutoGenPlanState.Expired, plan.State);
        Assert.Equal(previousVersion + 1, plan.Version);
        Assert.True(plan.ExpiresAtUtc < previousExpiry);
        Assert.Equal("{", plan.AutoGenJobRun.StatusJson);
        Assert.Empty(await fixture.Db.TeacherDraftItems.AsNoTracking().ToListAsync());
        Assert.Single(await fixture.Db.ScheduleItems.AsNoTracking().ToListAsync());
    }

    private static void AddTwoBlockSequence(WeekValidationFixture fixture)
    {
        fixture.Db.ModuleSequenceItems.AddRange(
            new ModuleSequenceItem
            {
                CourseId = fixture.CourseId,
                ModuleId = fixture.FirstModuleId,
                Order = 1,
                GroupOrder = 1
            },
            new ModuleSequenceItem
            {
                CourseId = fixture.CourseId,
                ModuleId = fixture.SecondModuleId,
                Order = 2,
                GroupOrder = 2
            });
    }

    private static async Task<(ModuleTopic First, ModuleTopic Second)> AddOrderedTopicsAsync(
        WeekValidationFixture fixture)
    {
        var first = new ModuleTopic
        {
            ModuleId = fixture.FirstModuleId,
            Order = 1,
            TopicCode = $"T1-{Guid.NewGuid():N}",
            LessonTypeId = fixture.LessonTypeId,
            TotalHours = 1,
            AuditoriumHours = 1
        };
        var second = new ModuleTopic
        {
            ModuleId = fixture.FirstModuleId,
            Order = 2,
            TopicCode = $"T2-{Guid.NewGuid():N}",
            LessonTypeId = fixture.LessonTypeId,
            TotalHours = 1,
            AuditoriumHours = 1
        };
        fixture.Db.ModuleTopics.AddRange(first, second);
        await fixture.Db.SaveChangesAsync();
        return (first, second);
    }

    private sealed class WeekValidationFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private WeekValidationFixture(SqliteConnection connection, AppDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public AppDbContext Db { get; }
        public int CourseId { get; private init; }
        public int GroupId { get; private init; }
        public int LessonTypeId { get; private init; }
        public int FirstModuleId { get; private init; }
        public int SecondModuleId { get; private init; }

        public static async Task<WeekValidationFixture> CreateAsync(DbCommandInterceptor? interceptor = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection);
            if (interceptor is not null)
            {
                optionsBuilder.AddInterceptors(interceptor);
            }
            var options = optionsBuilder.Options;
            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var course = new Course
            {
                Name = "Курс",
                DurationWeeks = 10,
                AcademicPeriodStartDate = Monday
            };
            var lessonType = new LessonTypeRef
            {
                Code = "WORKSHOP",
                Name = "Практичне заняття",
                RequiresRoom = false,
                RequiresTeacher = false,
                BlocksRoom = false,
                BlocksTeacher = false
            };
            db.Courses.Add(course);
            db.LessonTypes.Add(lessonType);
            await db.SaveChangesAsync();

            var group = new Group
            {
                Name = "9305",
                StudentsCount = 20,
                CourseId = course.Id
            };
            var firstModule = new Module
            {
                Code = "M1",
                Title = "Перший модуль",
                CourseId = course.Id
            };
            var secondModule = new Module
            {
                Code = "M2",
                Title = "Другий модуль",
                CourseId = course.Id
            };
            db.Groups.Add(group);
            db.Modules.AddRange(firstModule, secondModule);
            db.TimeSlots.AddRange(
                new TimeSlot
                {
                    CourseId = course.Id,
                    DayOfWeek = DayOfWeek.Monday,
                    Start = new TimeOnly(8, 0),
                    End = new TimeOnly(9, 0),
                    SortOrder = 1,
                    IsActive = true
                },
                new TimeSlot
                {
                    CourseId = course.Id,
                    DayOfWeek = DayOfWeek.Monday,
                    Start = new TimeOnly(9, 0),
                    End = new TimeOnly(10, 0),
                    SortOrder = 2,
                    IsActive = true
                });
            await db.SaveChangesAsync();

            return new WeekValidationFixture(connection, db)
            {
                CourseId = course.Id,
                GroupId = group.Id,
                LessonTypeId = lessonType.Id,
                FirstModuleId = firstModule.Id,
                SecondModuleId = secondModule.Id
            };
        }

        public TeacherDraftItem CreateDraft(int moduleId, TimeOnly start, TimeOnly end)
            => new()
            {
                Date = Monday,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = start,
                EndTime = end,
                GroupId = GroupId,
                ModuleId = moduleId,
                LessonTypeId = LessonTypeId,
                Status = DraftStatus.Draft
            };

        public ScheduleItem CreateScheduleItem(
            int moduleId,
            TimeOnly start,
            TimeOnly end,
            string? batchKey = null)
            => new()
            {
                Date = Monday,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = start,
                EndTime = end,
                GroupId = GroupId,
                ModuleId = moduleId,
                LessonTypeId = LessonTypeId,
                BatchKey = batchKey
            };

        public Task<AutoGenDraftPlan> AddAppliedPlanAsync(
            TeacherDraftItem draft,
            bool includeAppliedMutation = false)
            => AddAppliedPlanAsync(new[] { draft }, includeAppliedMutation);

        public async Task<AutoGenDraftPlan> AddAppliedPlanAsync(
            IReadOnlyCollection<TeacherDraftItem> drafts,
            bool includeAppliedMutations = false)
        {
            var now = DateTime.UtcNow;
            var run = new AutoGenJobRun
            {
                JobId = $"job-{Guid.NewGuid():N}",
                RequestHash = new string('a', 64),
                Version = 1,
                Kind = 0,
                State = 0,
                Title = "Перевірка",
                CurrentStage = "Завершено",
                CreatedAtUtc = now,
                CompletedAtUtc = now,
                RangeStartDate = Monday,
                RangeEndDate = Monday.AddDays(6),
                RequestJson = "{}",
                UpdatedAtUtc = now
            };
            var plan = new AutoGenDraftPlan
            {
                PlanId = $"plan-{Guid.NewGuid():N}",
                AutoGenJobRun = run,
                State = (int)AutoGenPlanState.Applied,
                Version = 2,
                CourseId = CourseId,
                RangeStartDate = Monday,
                RangeEndDate = Monday.AddDays(6),
                Days = (int)WeekPreset.MonSun,
                GroupIdsJson = JsonSerializer.Serialize(new[] { GroupId }, JsonOptions),
                BeforeScopeRevision = Guid.Empty,
                AppliedScopeRevision = LogicalRevisionToken.Combine(drafts.Select(draft =>
                    new KeyValuePair<int, Guid>(draft.Id, draft.Revision))),
                InputFingerprint = new string('b', 64),
                CreatedAtUtc = now,
                AppliedAtUtc = now,
                ExpiresAtUtc = now.AddDays(7)
            };
            run.StatusJson = JsonSerializer.Serialize(new AutoGenJobStatus(
                run.JobId,
                AutoGenJobState.Succeeded,
                AutoGenJobKind.Generate,
                run.Title,
                run.CurrentStage,
                now,
                now,
                now,
                run.RangeStartDate,
                run.RangeEndDate,
                1,
                1,
                1,
                run.RangeStartDate,
                run.RangeStartDate,
                run.RangeEndDate,
                drafts.Count,
                0,
                0,
                0,
                0,
                100,
                false),
                JsonOptions);
            if (includeAppliedMutations)
            {
                plan.AddCount = drafts.Count;
                var ordinal = 0;
                foreach (var draft in drafts)
                {
                    draft.GenerationJobId = plan.PlanId;
                    plan.Mutations.Add(new AutoGenDraftPlanMutation
                    {
                        Ordinal = ++ordinal,
                        Operation = (int)AutoGenPlanOperation.Add,
                        AppliedDraftId = draft.Id,
                        AppliedRevision = draft.Revision,
                        AfterJson = SerializeSnapshot(draft, plan.PlanId)
                    });
                }
            }

            Db.AutoGenDraftPlans.Add(plan);
            await Db.SaveChangesAsync();
            return plan;
        }

        public async Task<AutoGenDraftPlan> AddDeletionOnlyAppliedPlanAsync(TeacherDraftItem deletedDraft)
        {
            var plan = await AddAppliedPlanAsync(deletedDraft);
            plan.DeleteCount = 1;
            plan.Mutations.Add(new AutoGenDraftPlanMutation
            {
                Ordinal = 1,
                Operation = (int)AutoGenPlanOperation.Delete,
                SourceDraftId = deletedDraft.Id,
                BeforeRevision = deletedDraft.Revision,
                BeforeJson = SerializeSnapshot(deletedDraft, deletedDraft.GenerationJobId)
            });
            await Db.SaveChangesAsync();
            return plan;
        }

        private string SerializeSnapshot(TeacherDraftItem draft, string? generationJobId)
            => JsonSerializer.Serialize(new
            {
                draft.Id,
                draft.Revision,
                draft.Date,
                draft.DayOfWeek,
                draft.StartTime,
                draft.EndTime,
                draft.LessonTypeId,
                LessonTypeName = "Практичне заняття",
                draft.GroupId,
                GroupName = "9305",
                draft.ModuleId,
                ModuleName = draft.ModuleId == FirstModuleId ? "Перший модуль" : "Другий модуль",
                draft.ModuleTopicId,
                TopicCode = (string?)null,
                draft.TeacherId,
                TeacherName = (string?)null,
                draft.RoomId,
                RoomName = (string?)null,
                draft.Status,
                draft.PublishedItemId,
                draft.BatchKey,
                draft.ValidationWarnings,
                draft.CreatedAt,
                draft.UpdatedAt,
                draft.IsLocked,
                draft.IsSelfStudy,
                GenerationJobId = generationJobId
            }, JsonOptions);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class QueryCountingInterceptor : DbCommandInterceptor
    {
        public int ReaderCommandCount { get; private set; }

        public void Reset()
            => ReaderCommandCount = 0;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            ReaderCommandCount++;
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ReaderCommandCount++;
            return ValueTask.FromResult(result);
        }
    }
}
