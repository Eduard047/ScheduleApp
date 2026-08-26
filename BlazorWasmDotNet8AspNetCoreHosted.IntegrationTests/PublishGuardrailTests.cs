using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class PublishGuardrailTests
{
    private static readonly DateOnly Monday = new(2026, 5, 4);

    [Fact]
    public async Task PublishWeek_rejects_overlapping_drafts_as_one_atomic_batch()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync();
        fixture.Db.TeacherDraftItems.AddRange(
            CreateDraft(model, model.IndependentLessonTypeId, new TimeOnly(8, 0), new TimeOnly(9, 0), batchKey: "event-a"),
            CreateDraft(model, model.IndependentLessonTypeId, new TimeOnly(8, 0), new TimeOnly(9, 0), batchKey: "event-b"));
        await fixture.Db.SaveChangesAsync();

        var result = await CreateService(fixture.Db).PublishWeekAsync(new PublishWeekRequest(Monday, null, PublishTestScopeRevision.Read(fixture.Db, Monday)));

        var payload = ReadPayload(result);
        Assert.Equal(0, payload.Created);
        Assert.Equal(2, payload.Skipped);
        Assert.Contains(payload.Warnings, warning => warning.Contains("перетин групи", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, await fixture.Db.ScheduleItems.CountAsync());
        Assert.Equal(2, await fixture.Db.TeacherDraftItems.CountAsync());
    }

    [Fact]
    public async Task PublishWeek_rejects_conflict_with_existing_schedule_without_partial_changes()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync();
        fixture.Db.ScheduleItems.Add(CreateScheduleItem(
            model,
            model.IndependentLessonTypeId,
            new TimeOnly(8, 0),
            new TimeOnly(9, 0)));
        fixture.Db.TeacherDraftItems.AddRange(
            CreateDraft(
                model,
                model.IndependentLessonTypeId,
                new TimeOnly(8, 0),
                new TimeOnly(9, 0)),
            CreateDraft(
                model,
                model.IndependentLessonTypeId,
                new TimeOnly(9, 10),
                new TimeOnly(10, 10)));
        await fixture.Db.SaveChangesAsync();

        var result = await CreateService(fixture.Db).PublishWeekAsync(new PublishWeekRequest(Monday, null, PublishTestScopeRevision.Read(fixture.Db, Monday)));

        var payload = ReadPayload(result);
        Assert.Equal(0, payload.Created);
        Assert.Equal(2, payload.Skipped);
        Assert.Equal(1, await fixture.Db.ScheduleItems.CountAsync());
        Assert.Equal(2, await fixture.Db.TeacherDraftItems.CountAsync());
    }

    [Fact]
    public async Task PublishWeek_rejects_draft_outside_teacher_working_hours()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync();
        var teacher = new Teacher { FullName = "Викладач" };
        fixture.Db.Teachers.Add(teacher);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.TeacherWorkingHours.Add(new TeacherWorkingHour
        {
            TeacherId = teacher.Id,
            DayOfWeek = DayOfWeek.Monday,
            Start = new TimeOnly(10, 0),
            End = new TimeOnly(12, 0)
        });
        fixture.Db.TeacherDraftItems.Add(CreateDraft(
            model,
            model.TeacherLessonTypeId,
            new TimeOnly(8, 0),
            new TimeOnly(9, 0),
            teacherId: teacher.Id));
        await fixture.Db.SaveChangesAsync();

        var result = await CreateService(fixture.Db).PublishWeekAsync(new PublishWeekRequest(Monday, null, PublishTestScopeRevision.Read(fixture.Db, Monday)));

        var payload = ReadPayload(result);
        Assert.Equal(0, payload.Created);
        Assert.Contains(payload.Warnings, warning => warning.Contains("робочі години", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, await fixture.Db.ScheduleItems.CountAsync());
        Assert.Equal(1, await fixture.Db.TeacherDraftItems.CountAsync());
    }

    [Fact]
    public async Task PublishWeek_rejects_impossible_travel_inside_batch()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync();
        var firstBuilding = new Building { Name = "Корпус 1" };
        var secondBuilding = new Building { Name = "Корпус 2" };
        fixture.Db.Buildings.AddRange(firstBuilding, secondBuilding);
        await fixture.Db.SaveChangesAsync();
        var firstRoom = new Room { Name = "1-101", Capacity = 40, BuildingId = firstBuilding.Id };
        var secondRoom = new Room { Name = "2-201", Capacity = 40, BuildingId = secondBuilding.Id };
        fixture.Db.Rooms.AddRange(firstRoom, secondRoom);
        fixture.Db.BuildingTravels.Add(new BuildingTravel
        {
            FromBuildingId = firstBuilding.Id,
            ToBuildingId = secondBuilding.Id,
            Minutes = 30
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.TeacherDraftItems.AddRange(
            CreateDraft(
                model,
                model.RoomLessonTypeId,
                new TimeOnly(8, 0),
                new TimeOnly(9, 0),
                roomId: firstRoom.Id),
            CreateDraft(
                model,
                model.RoomLessonTypeId,
                new TimeOnly(9, 10),
                new TimeOnly(10, 10),
                roomId: secondRoom.Id));
        await fixture.Db.SaveChangesAsync();

        var result = await CreateService(fixture.Db).PublishWeekAsync(new PublishWeekRequest(Monday, null, PublishTestScopeRevision.Read(fixture.Db, Monday)));

        var payload = ReadPayload(result);
        Assert.Equal(0, payload.Created);
        Assert.Contains(payload.Warnings, warning => warning.Contains("перехід між корпусами", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, await fixture.Db.ScheduleItems.CountAsync());
        Assert.Equal(2, await fixture.Db.TeacherDraftItems.CountAsync());
    }

    [Fact]
    public async Task PublishWeek_publishes_valid_batch_and_removes_source_drafts()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync();
        fixture.Db.TeacherDraftItems.Add(CreateDraft(
            model,
            model.IndependentLessonTypeId,
            new TimeOnly(8, 0),
            new TimeOnly(9, 0)));
        await fixture.Db.SaveChangesAsync();

        var result = await CreateService(fixture.Db).PublishWeekAsync(new PublishWeekRequest(Monday, null, PublishTestScopeRevision.Read(fixture.Db, Monday)));

        var payload = ReadPayload(result);
        Assert.Equal(1, payload.Created);
        Assert.Equal(0, payload.Skipped);
        Assert.Empty(payload.Warnings);
        Assert.Equal(1, await fixture.Db.ScheduleItems.CountAsync());
        Assert.Equal(0, await fixture.Db.TeacherDraftItems.CountAsync());
    }

    [Fact]
    public async Task PublishWeek_rejects_fully_duplicate_legacy_rows()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync();
        fixture.Db.TeacherDraftItems.AddRange(
            CreateDraft(model, model.IndependentLessonTypeId, new TimeOnly(8, 0), new TimeOnly(9, 0)),
            CreateDraft(model, model.IndependentLessonTypeId, new TimeOnly(8, 0), new TimeOnly(9, 0)));
        await fixture.Db.SaveChangesAsync();

        var result = await CreateService(fixture.Db).PublishWeekAsync(new PublishWeekRequest(Monday, null, PublishTestScopeRevision.Read(fixture.Db, Monday)));

        var payload = ReadPayload(result);
        Assert.Equal(0, payload.Created);
        Assert.Equal(2, payload.Skipped);
        Assert.Contains(payload.Warnings, warning => warning.Contains("однакові legacy-рядки", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, await fixture.Db.ScheduleItems.CountAsync());
        Assert.Equal(2, await fixture.Db.TeacherDraftItems.CountAsync());
    }

    [Fact]
    public async Task PublishWeek_allows_configured_Saturday_without_calendar_exception()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync();
        var saturday = Monday.AddDays(5);
        fixture.Db.TimeSlots.Add(new TimeSlot
        {
            CourseId = model.CourseId,
            DayOfWeek = DayOfWeek.Saturday,
            Start = new TimeOnly(8, 0),
            End = new TimeOnly(9, 0),
            SortOrder = 1,
            IsActive = true
        });
        var draft = CreateDraft(
            model,
            model.IndependentLessonTypeId,
            new TimeOnly(8, 0),
            new TimeOnly(9, 0));
        draft.Date = saturday;
        draft.DayOfWeek = DayOfWeek.Saturday;
        fixture.Db.TeacherDraftItems.Add(draft);
        await fixture.Db.SaveChangesAsync();

        var result = await CreateService(fixture.Db).PublishWeekAsync(new PublishWeekRequest(Monday, null, PublishTestScopeRevision.Read(fixture.Db, Monday)));

        var payload = ReadPayload(result);
        Assert.Equal(1, payload.Created);
        Assert.Empty(payload.Warnings);
        Assert.Equal(1, await fixture.Db.ScheduleItems.CountAsync());
        Assert.Equal(0, await fixture.Db.TeacherDraftItems.CountAsync());
    }

    [Fact]
    public async Task PublishWeek_rejects_Saturday_marked_non_working_in_calendar()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync();
        var saturday = Monday.AddDays(5);
        fixture.Db.TimeSlots.Add(new TimeSlot
        {
            CourseId = model.CourseId,
            DayOfWeek = DayOfWeek.Saturday,
            Start = new TimeOnly(8, 0),
            End = new TimeOnly(9, 0),
            SortOrder = 1,
            IsActive = true
        });
        var draft = CreateDraft(
            model,
            model.IndependentLessonTypeId,
            new TimeOnly(8, 0),
            new TimeOnly(9, 0));
        draft.Date = saturday;
        draft.DayOfWeek = DayOfWeek.Saturday;
        fixture.Db.TeacherDraftItems.Add(draft);
        fixture.Db.CalendarExceptions.Add(new CalendarException
        {
            Date = saturday,
            IsWorkingDay = false,
            Name = "Неробоча субота",
            CourseId = model.CourseId
        });
        await fixture.Db.SaveChangesAsync();

        var result = await CreateService(fixture.Db).PublishWeekAsync(new PublishWeekRequest(Monday, null, PublishTestScopeRevision.Read(fixture.Db, Monday)));

        var payload = ReadPayload(result);
        Assert.Equal(0, payload.Created);
        Assert.Contains(payload.Warnings, warning => warning.Contains("неробочим", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, await fixture.Db.ScheduleItems.CountAsync());
        Assert.Equal(1, await fixture.Db.TeacherDraftItems.CountAsync());
    }

    [Fact]
    public async Task PublishWeek_validates_and_publishes_candidates_from_multiple_courses()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync();
        var secondCourse = new Course { Name = "Другий курс", DurationWeeks = 52 };
        var secondGroup = new Group { Name = "Т-2", StudentsCount = 15, Course = secondCourse };
        var secondModule = new Module { Code = "М2", Title = "Другий модуль", Credits = 1, Course = secondCourse };
        fixture.Db.AddRange(secondCourse, secondGroup, secondModule);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.TimeSlots.Add(new TimeSlot
        {
            CourseId = secondCourse.Id,
            DayOfWeek = DayOfWeek.Monday,
            Start = new TimeOnly(8, 0),
            End = new TimeOnly(9, 0),
            SortOrder = 1,
            IsActive = true
        });
        fixture.Db.TeacherDraftItems.AddRange(
            CreateDraft(model, model.IndependentLessonTypeId, new TimeOnly(8, 0), new TimeOnly(9, 0)),
            new TeacherDraftItem
            {
                Date = Monday,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(9, 0),
                GroupId = secondGroup.Id,
                ModuleId = secondModule.Id,
                LessonTypeId = model.IndependentLessonTypeId,
                Status = DraftStatus.Published
            });
        await fixture.Db.SaveChangesAsync();

        var result = await CreateService(fixture.Db).PublishWeekAsync(new PublishWeekRequest(Monday, null, PublishTestScopeRevision.Read(fixture.Db, Monday)));

        var payload = ReadPayload(result);
        Assert.Equal(2, payload.Created);
        Assert.Equal(0, payload.Skipped);
        Assert.Empty(payload.Warnings);
        Assert.Equal(2, await fixture.Db.ScheduleItems.CountAsync());
        Assert.Equal(0, await fixture.Db.TeacherDraftItems.CountAsync());
    }

    [Fact]
    public async Task ApproveWeek_approves_and_publishes_full_multi_teacher_logical_event()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync();
        var (firstTopic, secondTopic, firstTeacher, secondTeacher) = await SeedLogicalEventDetailsAsync(fixture.Db, model);
        const string batchKey = "logical-event-approval";
        var firstRow = CreateDraft(
            model,
            model.TeacherLessonTypeId,
            new TimeOnly(8, 0),
            new TimeOnly(9, 0),
            teacherId: firstTeacher.Id,
            moduleTopicId: firstTopic.Id,
            batchKey: batchKey);
        var secondRow = CreateDraft(
            model,
            model.TeacherLessonTypeId,
            new TimeOnly(8, 0),
            new TimeOnly(9, 0),
            teacherId: secondTeacher.Id,
            moduleTopicId: secondTopic.Id,
            batchKey: batchKey);
        firstRow.Status = DraftStatus.Draft;
        secondRow.Status = DraftStatus.Draft;
        fixture.Db.TeacherDraftItems.AddRange(firstRow, secondRow);
        await fixture.Db.SaveChangesAsync();
        var service = CreateService(fixture.Db);

        var approval = await service.ApproveWeekAsync(new ApproveWeekRequest(Monday, firstTeacher.Id));

        Assert.IsType<OkResult>(approval);
        var statuses = await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .OrderBy(item => item.Id)
            .Select(item => item.Status)
            .ToListAsync();
        Assert.Equal(new[] { DraftStatus.Published, DraftStatus.Published }, statuses);

        var publish = await service.PublishWeekAsync(new PublishWeekRequest(Monday, firstTeacher.Id, PublishTestScopeRevision.Read(fixture.Db, Monday)));

        var payload = ReadPayload(publish);
        Assert.Equal(2, payload.Created);
        Assert.Equal(0, payload.Skipped);
        Assert.Empty(payload.Warnings);
        Assert.Equal(0, await fixture.Db.TeacherDraftItems.CountAsync());
        Assert.Equal(2, await fixture.Db.ScheduleItems.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishWeek_rejects_explicit_and_legacy_mixed_status_logical_event_until_full_reapproval(
        bool withBatchKey)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync();
        var (firstTopic, secondTopic, firstTeacher, secondTeacher) = await SeedLogicalEventDetailsAsync(fixture.Db, model);
        var batchKey = withBatchKey ? "logical-event-mixed-status" : null;
        var publishedRow = CreateDraft(
            model,
            model.TeacherLessonTypeId,
            new TimeOnly(8, 0),
            new TimeOnly(9, 0),
            teacherId: firstTeacher.Id,
            moduleTopicId: firstTopic.Id,
            batchKey: batchKey);
        var draftRow = CreateDraft(
            model,
            model.TeacherLessonTypeId,
            new TimeOnly(8, 0),
            new TimeOnly(9, 0),
            teacherId: secondTeacher.Id,
            moduleTopicId: secondTopic.Id,
            batchKey: batchKey);
        draftRow.Status = DraftStatus.Draft;
        fixture.Db.TeacherDraftItems.AddRange(publishedRow, draftRow);
        await fixture.Db.SaveChangesAsync();
        var service = CreateService(fixture.Db);

        var blockedPublish = await service.PublishWeekAsync(new PublishWeekRequest(Monday, firstTeacher.Id, PublishTestScopeRevision.Read(fixture.Db, Monday)));

        var blockedPayload = ReadPayload(blockedPublish);
        Assert.Equal(0, blockedPayload.Created);
        Assert.Equal(2, blockedPayload.Skipped);
        Assert.Contains(
            blockedPayload.Warnings,
            warning => warning.Contains("змішані статуси", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, await fixture.Db.ScheduleItems.CountAsync());
        Assert.Equal(2, await fixture.Db.TeacherDraftItems.CountAsync());

        var approval = await service.ApproveWeekAsync(new ApproveWeekRequest(Monday, firstTeacher.Id));
        Assert.IsType<OkResult>(approval);

        var successfulPublish = await service.PublishWeekAsync(new PublishWeekRequest(Monday, firstTeacher.Id, PublishTestScopeRevision.Read(fixture.Db, Monday)));

        var successfulPayload = ReadPayload(successfulPublish);
        Assert.Equal(2, successfulPayload.Created);
        Assert.Equal(0, successfulPayload.Skipped);
        Assert.Empty(successfulPayload.Warnings);
        Assert.Equal(2, await fixture.Db.ScheduleItems.CountAsync());
        Assert.Equal(0, await fixture.Db.TeacherDraftItems.CountAsync());
    }

    [Fact]
    public async Task PublishWeek_publishes_multi_topic_co_teacher_logical_event_and_allows_resource_update()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync();
        var (firstTopic, secondTopic, firstTeacher, secondTeacher) = await SeedLogicalEventDetailsAsync(fixture.Db, model);
        const string batchKey = "logical-event-publish";
        fixture.Db.TeacherDraftItems.AddRange(
            CreateDraft(
                model,
                model.TeacherLessonTypeId,
                new TimeOnly(8, 0),
                new TimeOnly(9, 0),
                teacherId: firstTeacher.Id,
                moduleTopicId: firstTopic.Id,
                batchKey: batchKey),
            CreateDraft(
                model,
                model.TeacherLessonTypeId,
                new TimeOnly(8, 0),
                new TimeOnly(9, 0),
                teacherId: secondTeacher.Id,
                moduleTopicId: secondTopic.Id,
                batchKey: batchKey));
        await fixture.Db.SaveChangesAsync();

        var result = await CreateService(fixture.Db).PublishWeekAsync(new PublishWeekRequest(Monday, firstTeacher.Id, PublishTestScopeRevision.Read(fixture.Db, Monday)));

        var payload = ReadPayload(result);
        Assert.Equal(2, payload.Created);
        Assert.Equal(0, payload.Skipped);
        Assert.Empty(payload.Warnings);
        Assert.Equal(0, await fixture.Db.TeacherDraftItems.CountAsync());
        var published = await fixture.Db.ScheduleItems
            .AsNoTracking()
            .OrderBy(item => item.ModuleTopicId)
            .ToListAsync();
        Assert.Equal(2, published.Count);
        Assert.All(published, item => Assert.Equal(batchKey, item.BatchKey));

        var first = published[0];
        var updateValidation = await new RulesService(fixture.Db).ValidateUpsertAsync(
            new UpsertScheduleItemRequest(
                first.Id,
                first.Date,
                first.StartTime.ToString("HH:mm"),
                first.EndTime.ToString("HH:mm"),
                first.GroupId,
                first.ModuleId,
                first.TeacherId,
                first.RoomId,
                first.LessonTypeId,
                first.IsLocked));

        Assert.Empty(updateValidation.errors);
    }

    [Fact]
    public async Task PublishWeek_assigns_one_safe_batch_key_to_legacy_exact_signature_rows()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync();
        var (firstTopic, secondTopic, firstTeacher, secondTeacher) = await SeedLogicalEventDetailsAsync(fixture.Db, model);
        fixture.Db.TeacherDraftItems.AddRange(
            CreateDraft(
                model,
                model.TeacherLessonTypeId,
                new TimeOnly(8, 0),
                new TimeOnly(9, 0),
                teacherId: firstTeacher.Id,
                moduleTopicId: firstTopic.Id),
            CreateDraft(
                model,
                model.TeacherLessonTypeId,
                new TimeOnly(8, 0),
                new TimeOnly(9, 0),
                teacherId: secondTeacher.Id,
                moduleTopicId: secondTopic.Id));
        await fixture.Db.SaveChangesAsync();

        var result = await CreateService(fixture.Db).PublishWeekAsync(
            new PublishWeekRequest(Monday, firstTeacher.Id, PublishTestScopeRevision.Read(fixture.Db, Monday)));

        var payload = ReadPayload(result);
        Assert.Equal(2, payload.Created);
        Assert.Equal(0, payload.Skipped);
        var keys = await fixture.Db.ScheduleItems
            .AsNoTracking()
            .Select(item => item.BatchKey)
            .Distinct()
            .ToListAsync();
        var key = Assert.Single(keys);
        Assert.False(string.IsNullOrWhiteSpace(key));
        Assert.True(key!.Length <= 64);
        Assert.Equal(0, await fixture.Db.TeacherDraftItems.CountAsync());
    }

    [Fact]
    public async Task PublishWeek_rejects_external_collision_with_logical_event_as_one_atomic_batch()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync();
        var (firstTopic, secondTopic, firstTeacher, secondTeacher) = await SeedLogicalEventDetailsAsync(fixture.Db, model);
        var outsideTeacher = new Teacher { FullName = "Зовнішній викладач" };
        fixture.Db.Teachers.Add(outsideTeacher);
        await fixture.Db.SaveChangesAsync();
        const string batchKey = "logical-event-collision";
        fixture.Db.TeacherDraftItems.AddRange(
            CreateDraft(
                model,
                model.TeacherLessonTypeId,
                new TimeOnly(8, 0),
                new TimeOnly(9, 0),
                teacherId: firstTeacher.Id,
                moduleTopicId: firstTopic.Id,
                batchKey: batchKey),
            CreateDraft(
                model,
                model.TeacherLessonTypeId,
                new TimeOnly(8, 0),
                new TimeOnly(9, 0),
                teacherId: secondTeacher.Id,
                moduleTopicId: secondTopic.Id,
                batchKey: batchKey),
            CreateDraft(
                model,
                model.TeacherLessonTypeId,
                new TimeOnly(8, 0),
                new TimeOnly(9, 0),
                teacherId: outsideTeacher.Id,
                moduleTopicId: firstTopic.Id,
                batchKey: "outside-event"));
        await fixture.Db.SaveChangesAsync();

        var result = await CreateService(fixture.Db).PublishWeekAsync(new PublishWeekRequest(Monday, null, PublishTestScopeRevision.Read(fixture.Db, Monday)));

        var payload = ReadPayload(result);
        Assert.Equal(0, payload.Created);
        Assert.Equal(3, payload.Skipped);
        Assert.Contains(payload.Warnings, warning => warning.Contains("перетин групи", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, await fixture.Db.ScheduleItems.CountAsync());
        Assert.Equal(3, await fixture.Db.TeacherDraftItems.CountAsync());
    }

    [Fact]
    public async Task PublishWeek_teacher_filter_rejects_same_signature_with_another_batch_key()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync();
        var (firstTopic, secondTopic, firstTeacher, secondTeacher) = await SeedLogicalEventDetailsAsync(fixture.Db, model);
        var outsideTeacher = new Teacher { FullName = "Інший викладач" };
        fixture.Db.Teachers.Add(outsideTeacher);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.TeacherDraftItems.AddRange(
            CreateDraft(
                model,
                model.TeacherLessonTypeId,
                new TimeOnly(8, 0),
                new TimeOnly(9, 0),
                teacherId: firstTeacher.Id,
                moduleTopicId: firstTopic.Id,
                batchKey: "selected-event"),
            CreateDraft(
                model,
                model.TeacherLessonTypeId,
                new TimeOnly(8, 0),
                new TimeOnly(9, 0),
                teacherId: secondTeacher.Id,
                moduleTopicId: secondTopic.Id,
                batchKey: "selected-event"),
            CreateDraft(
                model,
                model.TeacherLessonTypeId,
                new TimeOnly(8, 0),
                new TimeOnly(9, 0),
                teacherId: outsideTeacher.Id,
                moduleTopicId: firstTopic.Id,
                batchKey: "outside-event"));
        await fixture.Db.SaveChangesAsync();

        var result = await CreateService(fixture.Db).PublishWeekAsync(
            new PublishWeekRequest(Monday, firstTeacher.Id, PublishTestScopeRevision.Read(fixture.Db, Monday)));

        var payload = ReadPayload(result);
        Assert.Equal(0, payload.Created);
        Assert.Equal(2, payload.Skipped);
        Assert.Contains(payload.Warnings, warning => warning.Contains("іншим BatchKey", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, await fixture.Db.ScheduleItems.CountAsync());
        Assert.Equal(3, await fixture.Db.TeacherDraftItems.CountAsync());
    }

    [Fact]
    public async Task PublishWeek_rejects_one_logical_event_with_different_rooms()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync();
        var building = new Building { Name = "Корпус" };
        var firstRoom = new Room { Name = "101", Capacity = 40, Building = building };
        var secondRoom = new Room { Name = "102", Capacity = 40, Building = building };
        fixture.Db.AddRange(building, firstRoom, secondRoom);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.TeacherDraftItems.AddRange(
            CreateDraft(
                model,
                model.RoomLessonTypeId,
                new TimeOnly(8, 0),
                new TimeOnly(9, 0),
                roomId: firstRoom.Id,
                batchKey: "one-event"),
            CreateDraft(
                model,
                model.RoomLessonTypeId,
                new TimeOnly(8, 0),
                new TimeOnly(9, 0),
                roomId: secondRoom.Id,
                batchKey: "one-event"));
        await fixture.Db.SaveChangesAsync();

        var result = await CreateService(fixture.Db).PublishWeekAsync(new PublishWeekRequest(Monday, null, PublishTestScopeRevision.Read(fixture.Db, Monday)));

        var payload = ReadPayload(result);
        Assert.Equal(0, payload.Created);
        Assert.Equal(2, payload.Skipped);
        Assert.Contains(payload.Warnings, warning => warning.Contains("різні аудиторії", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(await fixture.Db.ScheduleItems.AsNoTracking().ToListAsync());
        Assert.Equal(2, await fixture.Db.TeacherDraftItems.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task PublishWeek_rejects_minimum_and_maximum_dates_before_week_arithmetic()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var service = CreateService(fixture.Db);

        foreach (var date in new[] { DateOnly.MinValue, DateOnly.MaxValue })
        {
            var result = await service.PublishWeekAsync(new PublishWeekRequest(date, null));

            Assert.IsType<BadRequestObjectResult>(result.Result);
        }
    }

    private static TeacherDraftsPublishService CreateService(AppDbContext db)
        => new(db, new RulesService(db), new AggregatesService(db));

    private static PublishWeekResults ReadPayload(ActionResult<PublishWeekResults> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsType<PublishWeekResults>(ok.Value);
    }

    private static TeacherDraftItem CreateDraft(
        SeedModel model,
        int lessonTypeId,
        TimeOnly start,
        TimeOnly end,
        int? teacherId = null,
        int? roomId = null,
        int? moduleTopicId = null,
        string? batchKey = null)
        => new()
        {
            Date = Monday,
            DayOfWeek = DayOfWeek.Monday,
            StartTime = start,
            EndTime = end,
            GroupId = model.GroupId,
            ModuleId = model.ModuleId,
            LessonTypeId = lessonTypeId,
            TeacherId = teacherId,
            RoomId = roomId,
            ModuleTopicId = moduleTopicId,
            BatchKey = batchKey,
            Status = DraftStatus.Published
        };

    private static async Task<(ModuleTopic FirstTopic, ModuleTopic SecondTopic, Teacher FirstTeacher, Teacher SecondTeacher)>
        SeedLogicalEventDetailsAsync(AppDbContext db, SeedModel model)
    {
        var firstTeacher = new Teacher { FullName = "Перший викладач" };
        var secondTeacher = new Teacher { FullName = "Другий викладач" };
        var firstTopic = new ModuleTopic
        {
            ModuleId = model.ModuleId,
            LessonTypeId = model.TeacherLessonTypeId,
            Order = 1,
            TopicCode = "Т1",
            TotalHours = 1,
            AuditoriumHours = 1
        };
        var secondTopic = new ModuleTopic
        {
            ModuleId = model.ModuleId,
            LessonTypeId = model.TeacherLessonTypeId,
            Order = 2,
            TopicCode = "Т2",
            TotalHours = 1,
            AuditoriumHours = 1
        };
        db.AddRange(firstTeacher, secondTeacher, firstTopic, secondTopic);
        await db.SaveChangesAsync();
        db.TeacherModules.AddRange(
            new TeacherModule { TeacherId = firstTeacher.Id, ModuleId = model.ModuleId },
            new TeacherModule { TeacherId = secondTeacher.Id, ModuleId = model.ModuleId });
        await db.SaveChangesAsync();
        return (firstTopic, secondTopic, firstTeacher, secondTeacher);
    }

    private static ScheduleItem CreateScheduleItem(
        SeedModel model,
        int lessonTypeId,
        TimeOnly start,
        TimeOnly end)
        => new()
        {
            Date = Monday,
            DayOfWeek = DayOfWeek.Monday,
            StartTime = start,
            EndTime = end,
            GroupId = model.GroupId,
            ModuleId = model.ModuleId,
            LessonTypeId = lessonTypeId
        };

    private sealed record SeedModel(
        int CourseId,
        int GroupId,
        int ModuleId,
        int IndependentLessonTypeId,
        int TeacherLessonTypeId,
        int RoomLessonTypeId);

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

        public async Task<SeedModel> SeedAsync()
        {
            var course = new Course { Name = "Тестовий курс", DurationWeeks = 52 };
            var group = new Group { Name = "Т-1", StudentsCount = 20, Course = course };
            var module = new Module { Code = "М1", Title = "Модуль 1", Credits = 1, Course = course };
            var independentLessonType = new LessonTypeRef
            {
                Code = "INDEPENDENT",
                Name = "Самостійне заняття",
                RequiresRoom = false,
                RequiresTeacher = false,
                BlocksRoom = false,
                BlocksTeacher = false
            };
            var teacherLessonType = new LessonTypeRef
            {
                Code = "TEACHER",
                Name = "Заняття з викладачем",
                RequiresRoom = false,
                RequiresTeacher = true,
                BlocksRoom = false,
                BlocksTeacher = true
            };
            var roomLessonType = new LessonTypeRef
            {
                Code = "ROOM",
                Name = "Аудиторне заняття",
                RequiresRoom = true,
                RequiresTeacher = false,
                BlocksRoom = true,
                BlocksTeacher = false
            };
            Db.AddRange(course, group, module, independentLessonType, teacherLessonType, roomLessonType);
            await Db.SaveChangesAsync();
            Db.TimeSlots.AddRange(
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
                    Start = new TimeOnly(9, 10),
                    End = new TimeOnly(10, 10),
                    SortOrder = 2,
                    IsActive = true
                });
            await Db.SaveChangesAsync();

            return new SeedModel(
                course.Id,
                group.Id,
                module.Id,
                independentLessonType.Id,
                teacherLessonType.Id,
                roomLessonType.Id);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
