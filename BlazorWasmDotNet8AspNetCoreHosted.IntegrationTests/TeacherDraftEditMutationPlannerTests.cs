using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class TeacherDraftEditMutationPlannerTests
{
    [Fact]
    public void SelectLogicalEventRows_requires_batch_key_and_complete_original_signature()
    {
        var source = Item(1, topicId: 10, teacherId: 101, batchKey: "event-1");
        var sameEventCoTeacher = Item(2, topicId: 20, teacherId: 202, batchKey: "event-1");
        var differentBatch = Item(3, topicId: 10, teacherId: 101, batchKey: "event-2");
        var differentDate = Item(4, topicId: 10, teacherId: 101, batchKey: "event-1", date: new DateOnly(2026, 9, 8));
        var differentTime = Item(5, topicId: 10, teacherId: 101, batchKey: "event-1", timeStart: "10:00");
        var differentGroup = Item(6, topicId: 10, teacherId: 101, batchKey: "event-1", groupId: 2);
        var differentModule = Item(7, topicId: 10, teacherId: 101, batchKey: "event-1", moduleId: 2);
        var differentLessonType = Item(8, topicId: 10, teacherId: 101, batchKey: "event-1", lessonTypeId: 2);

        var selected = TeacherDraftEditMutationPlanner.SelectLogicalEventRows(
            new[]
            {
                source,
                sameEventCoTeacher,
                differentBatch,
                differentDate,
                differentTime,
                differentGroup,
                differentModule,
                differentLessonType
            },
            source);

        Assert.Equal(new[] { 1, 2 }, selected.Select(row => row.Id));
    }

    [Fact]
    public void SelectLogicalEventRows_without_batch_key_returns_only_source_row()
    {
        var source = Item(1, topicId: 10, teacherId: 101, batchKey: null);
        var sameSignature = Item(2, topicId: 20, teacherId: 202, batchKey: null);

        var selected = TeacherDraftEditMutationPlanner.SelectLogicalEventRows(
            new[] { source, sameSignature },
            source);

        Assert.Equal(new[] { 1 }, selected.Select(row => row.Id));
    }

    [Fact]
    public void BuildPlan_reuses_co_teacher_rows_and_deletes_removed_or_duplicate_topics()
    {
        var existing = new[]
        {
            new TeacherDraftEditExistingRow(1, 10, 101),
            new TeacherDraftEditExistingRow(2, 10, 202),
            new TeacherDraftEditExistingRow(3, 20, 101),
            new TeacherDraftEditExistingRow(4, 20, 202),
            new TeacherDraftEditExistingRow(5, 20, 202),
            new TeacherDraftEditExistingRow(6, 30, 101)
        };

        var plan = TeacherDraftEditMutationPlanner.BuildPlan(
            existing,
            new int?[] { 10, 20, 40 },
            originalPrimaryTeacherId: 101,
            desiredPrimaryTeacherId: 303);

        Assert.Equal(
            new[]
            {
                new TeacherDraftEditTargetRow(1, 10, 303),
                new TeacherDraftEditTargetRow(2, 10, 202),
                new TeacherDraftEditTargetRow(3, 20, 303),
                new TeacherDraftEditTargetRow(4, 20, 202),
                new TeacherDraftEditTargetRow(null, 40, 303),
                new TeacherDraftEditTargetRow(null, 40, 202)
            },
            plan.TargetRows);
        Assert.Equal(new[] { 5, 6 }, plan.DeleteIds);
    }

    [Fact]
    public void BuildPlan_collapses_primary_teacher_into_existing_co_teacher_without_duplicates()
    {
        var existing = new[]
        {
            new TeacherDraftEditExistingRow(1, 10, 101),
            new TeacherDraftEditExistingRow(2, 10, 202)
        };

        var plan = TeacherDraftEditMutationPlanner.BuildPlan(
            existing,
            new int?[] { 10 },
            originalPrimaryTeacherId: 101,
            desiredPrimaryTeacherId: 202);

        Assert.Equal(new[] { new TeacherDraftEditTargetRow(2, 10, 202) }, plan.TargetRows);
        Assert.Equal(new[] { 1 }, plan.DeleteIds);
    }

    [Fact]
    public void BuildRelocationRequests_preserves_every_logical_row_and_self_study_flag()
    {
        var source = new[]
        {
            Item(2, topicId: 20, teacherId: 202, batchKey: "event-1") with
            {
                RequiresRoom = false,
                RoomId = null,
                IsSelfStudy = true
            },
            Item(1, topicId: 10, teacherId: 101, batchKey: "event-1")
        };
        var target = new TeacherDraftRelocationTarget(
            new DateOnly(2026, 9, 10),
            "10:15",
            "11:45",
            7);

        var requests = TeacherDraftEditMutationPlanner.BuildRelocationRequests(
            source,
            target,
            ignoreValidationErrors: true);

        Assert.Equal(new int?[] { 1, 2 }, requests.Select(request => request.Id).ToArray());
        Assert.All(requests, request =>
        {
            Assert.Equal(target.Date, request.Date);
            Assert.Equal(target.TimeStart, request.TimeStart);
            Assert.Equal(target.TimeEnd, request.TimeEnd);
            Assert.Equal(target.GroupId, request.GroupId);
            Assert.True(request.IgnoreValidationErrors);
            Assert.Equal("event-1", request.BatchKey);
        });
        Assert.False(requests[0].IsSelfStudy);
        Assert.True(requests[1].IsSelfStudy);
        Assert.Null(requests[1].RoomId);
        Assert.False(requests[1].RequiresRoom);
    }

    [Fact]
    public void BuildReplacementPlan_reuses_full_topic_teacher_matrix_and_removes_extras()
    {
        var existing = new[]
        {
            new TeacherDraftEditExistingRow(1, 10, 101),
            new TeacherDraftEditExistingRow(2, 10, 202),
            new TeacherDraftEditExistingRow(3, 20, 101),
            new TeacherDraftEditExistingRow(4, 20, 202),
            new TeacherDraftEditExistingRow(5, 30, 303)
        };

        var plan = TeacherDraftEditMutationPlanner.BuildReplacementPlan(
            existing,
            new int?[] { 10, 20 },
            new int?[] { 202, 404 });

        Assert.Equal(
            new[]
            {
                new TeacherDraftEditTargetRow(2, 10, 202),
                new TeacherDraftEditTargetRow(1, 10, 404),
                new TeacherDraftEditTargetRow(4, 20, 202),
                new TeacherDraftEditTargetRow(3, 20, 404)
            },
            plan.TargetRows);
        Assert.Equal(new[] { 5 }, plan.DeleteIds);
    }

    private static TeacherDraftItemDto Item(
        int id,
        int? topicId,
        int? teacherId,
        string? batchKey,
        DateOnly? date = null,
        string timeStart = "08:30",
        int groupId = 1,
        int moduleId = 1,
        int lessonTypeId = 1)
        => new(
            Id: id,
            Date: date ?? new DateOnly(2026, 9, 7),
            TimeStart: timeStart,
            TimeEnd: "10:00",
            DayNumber: 1,
            Group: $"Група {groupId}",
            GroupId: groupId,
            Module: $"Модуль {moduleId}",
            ModuleId: moduleId,
            TopicCode: topicId?.ToString(),
            ModuleTopicId: topicId,
            Teacher: teacherId?.ToString() ?? "Без викладача",
            TeacherId: teacherId,
            Room: "Аудиторія 1",
            RoomId: 1,
            RequiresRoom: true,
            MissingTeacherAssignment: teacherId is null,
            MissingRoomAssignment: false,
            LessonTypeId: lessonTypeId,
            LessonTypeCode: "LECTURE",
            LessonTypeName: "Лекція",
            Status: DraftStatusDto.Draft,
            PublishedItemId: null,
            Warnings: null,
            BatchKey: batchKey);
}
