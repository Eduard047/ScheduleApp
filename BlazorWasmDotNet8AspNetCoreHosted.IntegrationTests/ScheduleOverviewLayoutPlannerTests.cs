using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class ScheduleOverviewLayoutPlannerTests
{
    [Fact]
    public void GroupEvents_keeps_parallel_distinct_events_in_the_same_slot()
    {
        var first = Item(1, groupId: 1, moduleId: 10, teacherId: 101, lessonTypeCode: "PRACTICE");
        var second = Item(2, groupId: 2, moduleId: 20, teacherId: 202, lessonTypeCode: "PRACTICE");

        var groups = ScheduleOverviewLayoutPlanner.GroupEvents(new[] { first, second });

        Assert.Equal(2, groups.Count);
        Assert.Equal(new[] { 1, 2 }, groups.Select(group => group.Anchor.Id));
    }

    [Fact]
    public void GroupEvents_merges_shared_lecture_rows_but_keeps_a_co_teacher_visible()
    {
        var firstGroup = Item(1, groupId: 1, moduleId: 10, teacherId: 101, lessonTypeCode: "LECTURE");
        var secondGroup = Item(2, groupId: 2, moduleId: 10, teacherId: 101, lessonTypeCode: "LECTURE");
        var topicSibling = Item(3, groupId: 1, moduleId: 10, teacherId: 101, lessonTypeCode: "LECTURE");
        var coTeacher = Item(4, groupId: 1, moduleId: 10, teacherId: 202, lessonTypeCode: "LECTURE");

        var groups = ScheduleOverviewLayoutPlanner.GroupEvents(
            new[] { firstGroup, secondGroup, topicSibling, coTeacher });

        Assert.Equal(2, groups.Count);
        var merged = Assert.Single(groups, group => group.IsMergedLecture);
        Assert.Equal(new[] { 1, 2, 3 }, merged.Items.Select(item => item.Id));
        Assert.Equal(4, Assert.Single(groups, group => !group.IsMergedLecture).Anchor.Id);
    }

    [Fact]
    public void BuildSlotRows_unions_course_times_and_preserves_an_unconfigured_scheduled_time()
    {
        var slots = new[]
        {
            Slot(courseId: 1, start: "08:30", end: "10:00"),
            Slot(courseId: 2, start: "10:15", end: "11:45"),
            Slot(courseId: 2, start: "08:30", end: "10:00"),
            Slot(courseId: 2, start: "12:00", end: "13:00", isLunch: true),
            Slot(courseId: 2, start: "14:00", end: "15:30", isActive: false)
        };
        var historicalItem = Item(
            1,
            groupId: 1,
            moduleId: 10,
            teacherId: 101,
            lessonTypeCode: "PRACTICE",
            timeStart: "16:00",
            timeEnd: "17:30");

        var rows = ScheduleOverviewLayoutPlanner.BuildSlotRows(slots, new[] { historicalItem });

        Assert.Equal(
            new[]
            {
                new ScheduleOverviewSlotRow("08:30", "10:00"),
                new ScheduleOverviewSlotRow("10:15", "11:45"),
                new ScheduleOverviewSlotRow("16:00", "17:30")
            },
            rows);
    }

    private static TimeSlotDto Slot(
        int courseId,
        string start,
        string end,
        bool isLunch = false,
        bool isActive = true)
        => new()
        {
            CourseId = courseId,
            Start = start,
            End = end,
            IsLunch = isLunch,
            IsActive = isActive
        };

    private static ScheduleItemDto Item(
        int id,
        int groupId,
        int moduleId,
        int? teacherId,
        string lessonTypeCode,
        string timeStart = "08:30",
        string timeEnd = "10:00")
        => new(
            Id: id,
            Date: new DateOnly(2026, 9, 7),
            TimeStart: timeStart,
            TimeEnd: timeEnd,
            DayName: "Понеділок",
            DayNumber: 1,
            Group: $"Група {groupId}",
            GroupId: groupId,
            Module: $"Модуль {moduleId}",
            ModuleId: moduleId,
            Teacher: teacherId is int value ? $"Викладач {value}" : string.Empty,
            TeacherId: teacherId,
            Room: "Аудиторія 1",
            RoomId: 1,
            Building: "Корпус 1",
            BuildingId: 1,
            RequiresRoom: true,
            LessonTypeId: 1,
            LessonTypeCode: lessonTypeCode,
            LessonTypeName: lessonTypeCode == "LECTURE" ? "Лекція" : "Практика",
            IsLocked: false,
            LessonTypeCss: "c1");
}
