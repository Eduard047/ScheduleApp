using System.Text;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

[Collection(DocxImportTestCollection.Name)]
public sealed class ScheduleEndpointCorrectnessTests
{
    private static readonly DateOnly Monday = new(2026, 5, 4);

    [Fact]
    public async Task Upsert_recalculates_both_old_and_new_module_plans()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedScheduleAsync();
        var controller = CreateController(fixture.Db);

        var result = await controller.Upsert(new UpsertScheduleItemRequest(
            Id: model.ScheduleItemId,
            Date: Monday,
            TimeStart: "08:00",
            TimeEnd: "09:00",
            GroupId: model.GroupId,
            ModuleId: model.NewModuleId,
            TeacherId: null,
            RoomId: null,
            LessonTypeId: model.LessonTypeId,
            IsLocked: false,
            OverrideNonWorkingDay: false,
            ExpectedRevision: await GetScheduleItemRevisionAsync(fixture.Db, model.ScheduleItemId)));

        Assert.IsType<OkObjectResult>(result.Result);
        fixture.Db.ChangeTracker.Clear();
        var oldPlan = await fixture.Db.ModulePlans.SingleAsync(plan => plan.ModuleId == model.OldModuleId);
        var newPlan = await fixture.Db.ModulePlans.SingleAsync(plan => plan.ModuleId == model.NewModuleId);
        Assert.Equal(0, oldPlan.ScheduledHours);
        Assert.Equal(1, newPlan.ScheduledHours);
    }

    [Fact]
    public async Task Upsert_updates_all_published_logical_siblings_and_preserves_topics_and_co_teachers()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedLogicalEventAsync();
        fixture.Db.TimeSlots.Add(new TimeSlot
        {
            CourseId = model.CourseId,
            DayOfWeek = DayOfWeek.Monday,
            Start = new TimeOnly(9, 10),
            End = new TimeOnly(10, 10),
            SortOrder = 2,
            IsActive = true
        });
        await fixture.Db.SaveChangesAsync();
        var controller = CreateController(fixture.Db);

        var result = await controller.Upsert(new UpsertScheduleItemRequest(
            Id: model.FirstItemId,
            Date: Monday,
            TimeStart: "09:10",
            TimeEnd: "10:10",
            GroupId: model.GroupId,
            ModuleId: model.ModuleId,
            TeacherId: model.FirstTeacherId,
            RoomId: null,
            LessonTypeId: model.OriginalLessonTypeId,
            IsLocked: false,
            OverrideNonWorkingDay: false,
            ExpectedRevision: await GetScheduleItemRevisionAsync(fixture.Db, model.FirstItemId)));

        Assert.IsType<OkObjectResult>(result.Result);
        fixture.Db.ChangeTracker.Clear();
        var rows = await fixture.Db.ScheduleItems
            .AsNoTracking()
            .OrderBy(item => item.Id)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, item => Assert.Equal(new TimeOnly(9, 10), item.StartTime));
        Assert.All(rows, item => Assert.Equal(model.OriginalLessonTypeId, item.LessonTypeId));
        Assert.All(rows, item => Assert.Equal(model.BatchKey, item.BatchKey));
        Assert.Equal(
            new int?[] { model.FirstTopicId, model.SecondTopicId },
            rows.Select(item => item.ModuleTopicId));
        Assert.Equal(
            new int?[] { model.FirstTeacherId, model.SecondTeacherId },
            rows.Select(item => item.TeacherId));
    }

    [Fact]
    public async Task Upsert_legacy_logical_event_uses_fallback_scope_and_backfills_one_batch_key()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedLogicalEventAsync(withBatchKey: false);
        fixture.Db.TimeSlots.Add(new TimeSlot
        {
            CourseId = model.CourseId,
            DayOfWeek = DayOfWeek.Monday,
            Start = new TimeOnly(9, 10),
            End = new TimeOnly(10, 10),
            SortOrder = 2,
            IsActive = true
        });
        await fixture.Db.SaveChangesAsync();
        var controller = CreateController(fixture.Db);

        var result = await controller.Upsert(new UpsertScheduleItemRequest(
            Id: model.FirstItemId,
            Date: Monday,
            TimeStart: "09:10",
            TimeEnd: "10:10",
            GroupId: model.GroupId,
            ModuleId: model.ModuleId,
            TeacherId: model.FirstTeacherId,
            RoomId: null,
            LessonTypeId: model.OriginalLessonTypeId,
            IsLocked: false,
            OverrideNonWorkingDay: false,
            ExpectedRevision: await GetScheduleItemRevisionAsync(fixture.Db, model.FirstItemId)));

        Assert.IsType<OkObjectResult>(result.Result);
        fixture.Db.ChangeTracker.Clear();
        var keys = await fixture.Db.ScheduleItems
            .AsNoTracking()
            .Select(item => item.BatchKey)
            .Distinct()
            .ToListAsync();
        var key = Assert.Single(keys);
        Assert.False(string.IsNullOrWhiteSpace(key));
        Assert.True(key!.Length <= 64);
    }

    [Fact]
    public async Task Upsert_rescheduled_creates_all_sibling_drafts_once_with_one_batch_key()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedLogicalEventAsync();
        var rescheduledType = new LessonTypeRef
        {
            Code = "RESCHEDULED",
            Name = "Перенесено",
            RequiresRoom = false,
            RequiresTeacher = false,
            BlocksRoom = false,
            BlocksTeacher = false,
            CountInPlan = false,
            CountInLoad = false
        };
        fixture.Db.LessonTypes.Add(rescheduledType);
        await fixture.Db.SaveChangesAsync();
        var controller = CreateController(fixture.Db);

        async Task<ActionResult<int>> ChangeTypeAsync(int lessonTypeId)
            => await controller.Upsert(new UpsertScheduleItemRequest(
                Id: model.FirstItemId,
                Date: Monday,
                TimeStart: "08:00",
                TimeEnd: "09:00",
                GroupId: model.GroupId,
                ModuleId: model.ModuleId,
                TeacherId: model.FirstTeacherId,
                RoomId: null,
                LessonTypeId: lessonTypeId,
                IsLocked: false,
                OverrideNonWorkingDay: false,
                ExpectedRevision: await GetScheduleItemRevisionAsync(fixture.Db, model.FirstItemId)));

        Assert.IsType<OkObjectResult>((await ChangeTypeAsync(rescheduledType.Id)).Result);
        fixture.Db.ChangeTracker.Clear();
        var firstPackage = await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .OrderBy(item => item.ModuleTopicId)
            .ToListAsync();
        Assert.Equal(2, firstPackage.Count);
        Assert.All(firstPackage, item => Assert.True(item.IsLocked));
        Assert.Equal(
            new int?[] { model.FirstTopicId, model.SecondTopicId },
            firstPackage.Select(item => item.ModuleTopicId));
        Assert.Equal(
            new int?[] { model.FirstTeacherId, model.SecondTeacherId },
            firstPackage.Select(item => item.TeacherId));
        var packageKey = Assert.Single(firstPackage.Select(item => item.BatchKey).Distinct());
        Assert.False(string.IsNullOrWhiteSpace(packageKey));

        Assert.IsType<OkObjectResult>((await ChangeTypeAsync(model.OriginalLessonTypeId)).Result);
        Assert.IsType<OkObjectResult>((await ChangeTypeAsync(rescheduledType.Id)).Result);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(2, await fixture.Db.TeacherDraftItems.CountAsync());
        Assert.All(
            await fixture.Db.TeacherDraftItems.AsNoTracking().ToListAsync(),
            item => Assert.Equal(packageKey, item.BatchKey));
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("extra")]
    [InlineData("mismatched")]
    public async Task Upsert_rescheduled_rejects_incomplete_or_mismatched_existing_package(string packageShape)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedLogicalEventAsync();
        var rescheduledType = new LessonTypeRef
        {
            Code = "RESCHEDULED",
            Name = "Перенесено",
            RequiresRoom = false,
            RequiresTeacher = false,
            BlocksRoom = false,
            BlocksTeacher = false,
            CountInPlan = false,
            CountInLoad = false
        };
        fixture.Db.LessonTypes.Add(rescheduledType);
        await fixture.Db.SaveChangesAsync();
        var sourceRows = await fixture.Db.ScheduleItems
            .AsNoTracking()
            .Where(item => item.BatchKey == model.BatchKey)
            .OrderBy(item => item.Id)
            .ToListAsync();
        Assert.Equal(2, sourceRows.Count);
        var packageKey = $"rescheduled:{sourceRows.Min(item => item.Id)}:{model.OriginalLessonTypeId}";
        var replacementDate = Monday.AddDays(7);
        var plantedRows = sourceRows.Select(item => new TeacherDraftItem
        {
            Date = replacementDate,
            DayOfWeek = replacementDate.DayOfWeek,
            StartTime = item.StartTime,
            EndTime = item.EndTime,
            GroupId = item.GroupId,
            ModuleId = item.ModuleId,
            ModuleTopicId = item.ModuleTopicId,
            TeacherId = item.TeacherId,
            RoomId = item.RoomId,
            LessonTypeId = item.LessonTypeId,
            BatchKey = packageKey,
            Status = DraftStatus.Draft,
            IsLocked = true,
            IsSelfStudy = item.IsSelfStudy
        }).ToList();
        switch (packageShape)
        {
            case "partial":
                plantedRows.RemoveAt(1);
                break;
            case "extra":
                plantedRows.Add(new TeacherDraftItem
                {
                    Date = plantedRows[0].Date,
                    DayOfWeek = plantedRows[0].DayOfWeek,
                    StartTime = plantedRows[0].StartTime,
                    EndTime = plantedRows[0].EndTime,
                    GroupId = plantedRows[0].GroupId,
                    ModuleId = plantedRows[0].ModuleId,
                    ModuleTopicId = plantedRows[0].ModuleTopicId,
                    TeacherId = plantedRows[0].TeacherId,
                    RoomId = plantedRows[0].RoomId,
                    LessonTypeId = plantedRows[0].LessonTypeId,
                    BatchKey = packageKey,
                    Status = DraftStatus.Draft,
                    IsLocked = true,
                    IsSelfStudy = plantedRows[0].IsSelfStudy
                });
                break;
            case "mismatched":
                plantedRows[1].ModuleTopicId = plantedRows[0].ModuleTopicId;
                plantedRows[1].TeacherId = plantedRows[0].TeacherId;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(packageShape));
        }
        fixture.Db.TeacherDraftItems.AddRange(plantedRows);
        await fixture.Db.SaveChangesAsync();
        var originalRevision = await GetScheduleItemRevisionAsync(fixture.Db, model.FirstItemId);

        var result = await CreateController(fixture.Db).Upsert(new UpsertScheduleItemRequest(
            Id: model.FirstItemId,
            Date: Monday,
            TimeStart: "08:00",
            TimeEnd: "09:00",
            GroupId: model.GroupId,
            ModuleId: model.ModuleId,
            TeacherId: model.FirstTeacherId,
            RoomId: null,
            LessonTypeId: rescheduledType.Id,
            IsLocked: false,
            OverrideNonWorkingDay: false,
            ExpectedRevision: originalRevision));

        Assert.IsType<ConflictObjectResult>(result.Result);
        fixture.Db.ChangeTracker.Clear();
        var persistedSourceRows = await fixture.Db.ScheduleItems
            .AsNoTracking()
            .Where(item => item.BatchKey == model.BatchKey)
            .ToListAsync();
        Assert.Equal(2, persistedSourceRows.Count);
        Assert.All(persistedSourceRows, item => Assert.Equal(model.OriginalLessonTypeId, item.LessonTypeId));
        Assert.Equal(plantedRows.Count, await fixture.Db.TeacherDraftItems.CountAsync());
    }

    [Fact]
    public async Task Upsert_rescheduled_rejects_room_required_logical_event_with_mixed_rooms()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedLogicalEventAsync();
        var originalType = await fixture.Db.LessonTypes
            .SingleAsync(item => item.Id == model.OriginalLessonTypeId);
        originalType.RequiresRoom = true;
        originalType.BlocksRoom = true;
        var rescheduledType = new LessonTypeRef
        {
            Code = "RESCHEDULED",
            Name = "Перенесено",
            RequiresRoom = false,
            RequiresTeacher = false,
            BlocksRoom = false,
            BlocksTeacher = false,
            CountInPlan = false,
            CountInLoad = false
        };
        var building = new Building { Name = "Корпус для перенесення" };
        var firstRoom = new Room { Name = "Аудиторія 1", Capacity = 40, Building = building };
        var secondRoom = new Room { Name = "Аудиторія 2", Capacity = 40, Building = building };
        fixture.Db.AddRange(rescheduledType, building, firstRoom, secondRoom);
        await fixture.Db.SaveChangesAsync();
        var sourceRows = await fixture.Db.ScheduleItems
            .Where(item => item.BatchKey == model.BatchKey)
            .OrderBy(item => item.Id)
            .ToListAsync();
        sourceRows[0].RoomId = firstRoom.Id;
        sourceRows[1].RoomId = secondRoom.Id;
        await fixture.Db.SaveChangesAsync();
        var originalRevision = await GetScheduleItemRevisionAsync(fixture.Db, model.FirstItemId);

        var result = await CreateController(fixture.Db).Upsert(new UpsertScheduleItemRequest(
            Id: model.FirstItemId,
            Date: Monday,
            TimeStart: "08:00",
            TimeEnd: "09:00",
            GroupId: model.GroupId,
            ModuleId: model.ModuleId,
            TeacherId: model.FirstTeacherId,
            RoomId: firstRoom.Id,
            LessonTypeId: rescheduledType.Id,
            IsLocked: false,
            OverrideNonWorkingDay: false,
            ExpectedRevision: originalRevision));

        Assert.IsType<ConflictObjectResult>(result.Result);
        fixture.Db.ChangeTracker.Clear();
        var persistedSourceRows = await fixture.Db.ScheduleItems
            .AsNoTracking()
            .Where(item => item.BatchKey == model.BatchKey)
            .OrderBy(item => item.Id)
            .ToListAsync();
        Assert.Equal(2, persistedSourceRows.Count);
        Assert.All(persistedSourceRows, item => Assert.Equal(model.OriginalLessonTypeId, item.LessonTypeId));
        Assert.Equal(new int?[] { firstRoom.Id, secondRoom.Id }, persistedSourceRows.Select(item => item.RoomId));
        Assert.False(await fixture.Db.TeacherDraftItems.AnyAsync());
    }

    [Fact]
    public async Task Upsert_rescheduled_rolls_back_when_no_replacement_slot_is_available()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedLogicalEventAsync();
        var rescheduledType = new LessonTypeRef
        {
            Code = "RESCHEDULED",
            Name = "Перенесено",
            RequiresRoom = false,
            RequiresTeacher = false,
            BlocksRoom = false,
            BlocksTeacher = false,
            CountInPlan = false,
            CountInLoad = false
        };
        fixture.Db.LessonTypes.Add(rescheduledType);
        var nextWeekStart = Monday.AddDays(7);
        for (var offset = 0; offset < 5; offset++)
        {
            fixture.Db.CalendarExceptions.Add(new CalendarException
            {
                Date = nextWeekStart.AddDays(offset),
                IsWorkingDay = false,
                Name = "Тестовий неробочий день",
                CourseId = model.CourseId
            });
        }
        await fixture.Db.SaveChangesAsync();
        var originalRevision = await GetScheduleItemRevisionAsync(fixture.Db, model.FirstItemId);
        var originalRowRevision = await fixture.Db.ScheduleItems
            .AsNoTracking()
            .Where(item => item.Id == model.FirstItemId)
            .Select(item => item.Revision)
            .SingleAsync();
        var controller = CreateController(fixture.Db);

        var result = await controller.Upsert(new UpsertScheduleItemRequest(
            Id: model.FirstItemId,
            Date: Monday,
            TimeStart: "08:00",
            TimeEnd: "09:00",
            GroupId: model.GroupId,
            ModuleId: model.ModuleId,
            TeacherId: model.FirstTeacherId,
            RoomId: null,
            LessonTypeId: rescheduledType.Id,
            IsLocked: false,
            OverrideNonWorkingDay: false,
            ExpectedRevision: originalRevision));

        Assert.IsType<ConflictObjectResult>(result.Result);
        fixture.Db.ChangeTracker.Clear();
        var persistedRows = await fixture.Db.ScheduleItems
            .AsNoTracking()
            .Where(item => item.BatchKey == model.BatchKey)
            .ToListAsync();
        Assert.Equal(2, persistedRows.Count);
        Assert.All(persistedRows, item => Assert.Equal(model.OriginalLessonTypeId, item.LessonTypeId));
        Assert.Contains(persistedRows, item => item.Id == model.FirstItemId && item.Revision == originalRowRevision);
        Assert.False(await fixture.Db.TeacherDraftItems.AnyAsync());
    }

    [Fact]
    public async Task Upsert_rejects_external_collision_and_keeps_all_logical_siblings_unchanged()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedLogicalEventAsync();
        fixture.Db.TimeSlots.Add(new TimeSlot
        {
            CourseId = model.CourseId,
            DayOfWeek = DayOfWeek.Monday,
            Start = new TimeOnly(9, 10),
            End = new TimeOnly(10, 10),
            SortOrder = 2,
            IsActive = true
        });
        fixture.Db.ScheduleItems.Add(new ScheduleItem
        {
            Date = Monday,
            DayOfWeek = DayOfWeek.Monday,
            StartTime = new TimeOnly(9, 10),
            EndTime = new TimeOnly(10, 10),
            GroupId = model.GroupId,
            ModuleId = model.ExternalModuleId,
            LessonTypeId = model.OriginalLessonTypeId
        });
        await fixture.Db.SaveChangesAsync();
        var controller = CreateController(fixture.Db);

        var result = await controller.Upsert(new UpsertScheduleItemRequest(
            Id: model.FirstItemId,
            Date: Monday,
            TimeStart: "09:10",
            TimeEnd: "10:10",
            GroupId: model.GroupId,
            ModuleId: model.ModuleId,
            TeacherId: model.FirstTeacherId,
            RoomId: null,
            LessonTypeId: model.OriginalLessonTypeId,
            IsLocked: false,
            OverrideNonWorkingDay: false,
            ExpectedRevision: await GetScheduleItemRevisionAsync(fixture.Db, model.FirstItemId)));

        Assert.IsType<ConflictObjectResult>(result.Result);
        fixture.Db.ChangeTracker.Clear();
        var logicalRows = await fixture.Db.ScheduleItems
            .AsNoTracking()
            .Where(item => item.ModuleId == model.ModuleId)
            .OrderBy(item => item.Id)
            .ToListAsync();
        Assert.Equal(2, logicalRows.Count);
        Assert.All(logicalRows, item =>
        {
            Assert.Equal(new TimeOnly(8, 0), item.StartTime);
            Assert.Equal(new TimeOnly(9, 0), item.EndTime);
            Assert.Equal(model.OriginalLessonTypeId, item.LessonTypeId);
        });
        Assert.Equal(3, await fixture.Db.ScheduleItems.CountAsync());
    }

    [Fact]
    public async Task Upsert_rolls_back_every_logical_sibling_when_aggregate_recalculation_fails()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedLogicalEventAsync();
        fixture.Db.TimeSlots.Add(new TimeSlot
        {
            CourseId = model.CourseId,
            DayOfWeek = DayOfWeek.Monday,
            Start = new TimeOnly(9, 0),
            End = new TimeOnly(10, 0),
            SortOrder = 2,
            IsActive = true
        });
        await fixture.Db.SaveChangesAsync();
        await fixture.AddFailingModulePlanUpdateTriggerAsync();
        var controller = CreateController(fixture.Db);
        var expectedRevision = await GetScheduleItemRevisionAsync(fixture.Db, model.FirstItemId);

        await Assert.ThrowsAsync<DbUpdateException>(() => controller.Upsert(new UpsertScheduleItemRequest(
            Id: model.FirstItemId,
            Date: Monday,
            TimeStart: "08:00",
            TimeEnd: "10:00",
            GroupId: model.GroupId,
            ModuleId: model.ModuleId,
            TeacherId: model.FirstTeacherId,
            RoomId: null,
            LessonTypeId: model.OriginalLessonTypeId,
            IsLocked: false,
            OverrideNonWorkingDay: false,
            ExpectedRevision: expectedRevision)));

        fixture.Db.ChangeTracker.Clear();
        var lessonTypeIds = await fixture.Db.ScheduleItems
            .AsNoTracking()
            .OrderBy(item => item.Id)
            .Select(item => item.LessonTypeId)
            .ToListAsync();
        Assert.Equal(new[] { model.OriginalLessonTypeId, model.OriginalLessonTypeId }, lessonTypeIds);
        Assert.All(
            await fixture.Db.ScheduleItems.AsNoTracking().ToListAsync(),
            item => Assert.Equal(new TimeOnly(9, 0), item.EndTime));
    }

    [Fact]
    public async Task Delete_removes_all_published_logical_siblings_but_keeps_external_row()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedLogicalEventAsync();
        var external = new ScheduleItem
        {
            Date = Monday,
            DayOfWeek = DayOfWeek.Monday,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(9, 0),
            GroupId = model.GroupId,
            ModuleId = model.ExternalModuleId,
            LessonTypeId = model.OriginalLessonTypeId
        };
        fixture.Db.ScheduleItems.Add(external);
        await fixture.Db.SaveChangesAsync();
        var controller = CreateController(fixture.Db);

        var result = await controller.Delete(
            model.FirstItemId,
            await GetScheduleItemRevisionAsync(fixture.Db, model.FirstItemId));

        Assert.IsType<NoContentResult>(result);
        fixture.Db.ChangeTracker.Clear();
        var remaining = await fixture.Db.ScheduleItems.AsNoTracking().SingleAsync();
        Assert.Equal(external.Id, remaining.Id);
    }

    [Fact]
    public async Task Delete_rolls_back_schedule_change_when_aggregate_recalculation_fails()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedScheduleAsync();
        await fixture.AddFailingModulePlanUpdateTriggerAsync();
        var controller = CreateController(fixture.Db);

        await Assert.ThrowsAsync<DbUpdateException>(() => controller.Delete(
            model.ScheduleItemId,
            model.ScheduleItemRevision));

        fixture.Db.ChangeTracker.Clear();
        Assert.True(await fixture.Db.ScheduleItems.AnyAsync(item => item.Id == model.ScheduleItemId));
        Assert.Equal(
            1,
            await fixture.Db.ModulePlans
                .Where(plan => plan.ModuleId == model.OldModuleId)
                .Select(plan => plan.ScheduledHours)
                .SingleAsync());
    }

    [Fact]
    public async Task ClearWeek_rolls_back_all_deletions_when_aggregate_recalculation_fails()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedScheduleAsync();
        await fixture.AddFailingModulePlanUpdateTriggerAsync();
        var controller = CreateController(fixture.Db);

        await Assert.ThrowsAsync<DbUpdateException>(() => controller.ClearWeek(
            new ClearWeekRequest(Monday, CourseId: model.CourseId)));

        fixture.Db.ChangeTracker.Clear();
        Assert.True(await fixture.Db.ScheduleItems.AnyAsync(item => item.Id == model.ScheduleItemId));
        Assert.Equal(
            1,
            await fixture.Db.ModulePlans
                .Where(plan => plan.ModuleId == model.OldModuleId)
                .Select(plan => plan.ScheduledHours)
                .SingleAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClearWeek_rejects_mixed_lock_logical_event_without_partial_deletion(bool withBatchKey)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedLogicalEventAsync(withBatchKey);
        var logicalRows = await fixture.Db.ScheduleItems
            .Where(item => item.BatchKey == model.BatchKey)
            .OrderBy(item => item.Id)
            .ToListAsync();
        Assert.Equal(2, logicalRows.Count);
        logicalRows[0].IsLocked = true;
        await fixture.Db.SaveChangesAsync();
        var originalIds = logicalRows.Select(item => item.Id).ToArray();
        var controller = CreateController(fixture.Db);

        var result = await controller.ClearWeek(
            new ClearWeekRequest(Monday, CourseId: model.CourseId));

        Assert.IsType<ConflictObjectResult>(result.Result);
        fixture.Db.ChangeTracker.Clear();
        var persistedRows = await fixture.Db.ScheduleItems
            .AsNoTracking()
            .Where(item => item.BatchKey == model.BatchKey)
            .OrderBy(item => item.Id)
            .ToListAsync();
        Assert.Equal(originalIds, persistedRows.Select(item => item.Id));
        Assert.Contains(persistedRows, item => item.IsLocked);
        Assert.Contains(persistedRows, item => !item.IsLocked);
    }

    [Fact]
    public async Task Upsert_rejects_stale_revision_without_overwriting_newer_schedule_change()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedScheduleAsync();
        var staleRevision = model.ScheduleItemRevision;
        var changedByAnotherEditor = await fixture.Db.ScheduleItems
            .SingleAsync(item => item.Id == model.ScheduleItemId);
        changedByAnotherEditor.StartTime = new TimeOnly(7, 30);
        changedByAnotherEditor.EndTime = new TimeOnly(8, 30);
        await fixture.Db.SaveChangesAsync();
        var freshRevision = changedByAnotherEditor.Revision;
        Assert.NotEqual(staleRevision, freshRevision);
        fixture.Db.ChangeTracker.Clear();
        var controller = CreateController(fixture.Db);

        var result = await controller.Upsert(new UpsertScheduleItemRequest(
            Id: model.ScheduleItemId,
            Date: Monday,
            TimeStart: "09:00",
            TimeEnd: "10:00",
            GroupId: model.GroupId,
            ModuleId: model.NewModuleId,
            TeacherId: null,
            RoomId: null,
            LessonTypeId: model.LessonTypeId,
            IsLocked: false,
            OverrideNonWorkingDay: false,
            ExpectedRevision: staleRevision));

        Assert.IsType<ConflictObjectResult>(result.Result);
        fixture.Db.ChangeTracker.Clear();
        var persisted = await fixture.Db.ScheduleItems
            .AsNoTracking()
            .SingleAsync(item => item.Id == model.ScheduleItemId);
        Assert.Equal(new TimeOnly(7, 30), persisted.StartTime);
        Assert.Equal(new TimeOnly(8, 30), persisted.EndTime);
        Assert.Equal(model.OldModuleId, persisted.ModuleId);
        Assert.Equal(freshRevision, persisted.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_schedule_mutation_requires_revision(bool delete)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedScheduleAsync();
        var controller = CreateController(fixture.Db);

        IActionResult action;
        if (delete)
        {
            action = await controller.Delete(model.ScheduleItemId, expectedRevision: null);
        }
        else
        {
            var result = await controller.Upsert(new UpsertScheduleItemRequest(
                Id: model.ScheduleItemId,
                Date: Monday,
                TimeStart: "09:00",
                TimeEnd: "10:00",
                GroupId: model.GroupId,
                ModuleId: model.NewModuleId,
                TeacherId: null,
                RoomId: null,
                LessonTypeId: model.LessonTypeId,
                IsLocked: false,
                OverrideNonWorkingDay: false,
                ExpectedRevision: null));
            action = Assert.IsAssignableFrom<IActionResult>(result.Result);
        }

        var failure = Assert.IsType<ObjectResult>(action);
        Assert.Equal(428, failure.StatusCode);
        Assert.True(await fixture.Db.ScheduleItems.AnyAsync(item => item.Id == model.ScheduleItemId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logical_event_mutation_rejects_stale_revision_when_only_sibling_changed(bool delete)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedLogicalEventAsync();
        var staleRevision = await GetScheduleItemRevisionAsync(fixture.Db, model.FirstItemId);
        var originalSourceRevision = await fixture.Db.ScheduleItems
            .AsNoTracking()
            .Where(item => item.Id == model.FirstItemId)
            .Select(item => item.Revision)
            .SingleAsync();
        var sibling = await fixture.Db.ScheduleItems
            .SingleAsync(item => item.ModuleTopicId == model.SecondTopicId);
        sibling.TeacherId = model.FirstTeacherId;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(
            originalSourceRevision,
            await fixture.Db.ScheduleItems
                .AsNoTracking()
                .Where(item => item.Id == model.FirstItemId)
                .Select(item => item.Revision)
                .SingleAsync());
        Assert.NotEqual(staleRevision, await GetScheduleItemRevisionAsync(fixture.Db, model.FirstItemId));
        var controller = CreateController(fixture.Db);

        IActionResult action;
        if (delete)
        {
            action = await controller.Delete(model.FirstItemId, staleRevision);
        }
        else
        {
            var result = await controller.Upsert(new UpsertScheduleItemRequest(
                Id: model.FirstItemId,
                Date: Monday,
                TimeStart: "08:00",
                TimeEnd: "09:00",
                GroupId: model.GroupId,
                ModuleId: model.ModuleId,
                TeacherId: model.FirstTeacherId,
                RoomId: null,
                LessonTypeId: model.TargetLessonTypeId,
                IsLocked: false,
                OverrideNonWorkingDay: false,
                ExpectedRevision: staleRevision));
            action = Assert.IsAssignableFrom<IActionResult>(result.Result);
        }

        Assert.IsType<ConflictObjectResult>(action);
        fixture.Db.ChangeTracker.Clear();
        var persistedRows = await fixture.Db.ScheduleItems
            .AsNoTracking()
            .Where(item => item.BatchKey == model.BatchKey)
            .ToListAsync();
        Assert.Equal(2, persistedRows.Count);
        Assert.All(persistedRows, item => Assert.Equal(model.OriginalLessonTypeId, item.LessonTypeId));
        Assert.Contains(persistedRows, item =>
            item.ModuleTopicId == model.SecondTopicId && item.TeacherId == model.FirstTeacherId);
    }

    [Fact]
    public async Task Delete_rejects_stale_revision_without_removing_newer_schedule_change()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedScheduleAsync();
        var staleRevision = model.ScheduleItemRevision;
        var changedByAnotherEditor = await fixture.Db.ScheduleItems
            .SingleAsync(item => item.Id == model.ScheduleItemId);
        changedByAnotherEditor.StartTime = new TimeOnly(7, 30);
        changedByAnotherEditor.EndTime = new TimeOnly(8, 30);
        await fixture.Db.SaveChangesAsync();
        var freshRevision = changedByAnotherEditor.Revision;
        Assert.NotEqual(staleRevision, freshRevision);
        fixture.Db.ChangeTracker.Clear();
        var controller = CreateController(fixture.Db);

        var result = await controller.Delete(model.ScheduleItemId, staleRevision);

        Assert.IsType<ConflictObjectResult>(result);
        fixture.Db.ChangeTracker.Clear();
        var persisted = await fixture.Db.ScheduleItems
            .AsNoTracking()
            .SingleAsync(item => item.Id == model.ScheduleItemId);
        Assert.Equal(new TimeOnly(7, 30), persisted.StartTime);
        Assert.Equal(new TimeOnly(8, 30), persisted.EndTime);
        Assert.Equal(freshRevision, persisted.Revision);
    }

    [Fact]
    public async Task Upsert_rejects_missing_teacher_without_changing_schedule()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedScheduleAsync();
        var controller = CreateController(fixture.Db);

        var result = await controller.Upsert(new UpsertScheduleItemRequest(
            Id: model.ScheduleItemId,
            Date: Monday,
            TimeStart: "08:00",
            TimeEnd: "09:00",
            GroupId: model.GroupId,
            ModuleId: model.OldModuleId,
            TeacherId: int.MaxValue,
            RoomId: null,
            LessonTypeId: model.LessonTypeId,
            IsLocked: false,
            OverrideNonWorkingDay: false,
            ExpectedRevision: await GetScheduleItemRevisionAsync(fixture.Db, model.ScheduleItemId)));

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Null(await fixture.Db.ScheduleItems
            .Where(item => item.Id == model.ScheduleItemId)
            .Select(item => item.TeacherId)
            .SingleAsync());
    }

    [Fact]
    public async Task Upsert_accepts_shared_module_and_rejects_module_from_unrelated_course()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedScheduleAsync();
        var foreignCourse = new Course { Name = "Інший курс", DurationWeeks = 12 };
        var sharedModule = new Module
        {
            Code = "SHARED",
            Title = "Спільний модуль",
            Credits = 1,
            Course = foreignCourse
        };
        fixture.Db.AddRange(foreignCourse, sharedModule);
        await fixture.Db.SaveChangesAsync();
        var request = new UpsertScheduleItemRequest(
            Id: model.ScheduleItemId,
            Date: Monday,
            TimeStart: "08:00",
            TimeEnd: "09:00",
            GroupId: model.GroupId,
            ModuleId: sharedModule.Id,
            TeacherId: null,
            RoomId: null,
            LessonTypeId: model.LessonTypeId,
            IsLocked: false,
            OverrideNonWorkingDay: false,
            ExpectedRevision: await GetScheduleItemRevisionAsync(fixture.Db, model.ScheduleItemId));
        var controller = CreateController(fixture.Db);

        var rejected = await controller.Upsert(request);

        Assert.IsType<ConflictObjectResult>(rejected.Result);
        fixture.Db.ModuleCourses.Add(new ModuleCourse
        {
            ModuleId = sharedModule.Id,
            CourseId = model.CourseId
        });
        await fixture.Db.SaveChangesAsync();

        var accepted = await controller.Upsert(request);

        Assert.IsType<OkObjectResult>(accepted.Result);
        Assert.Equal(
            sharedModule.Id,
            await fixture.Db.ScheduleItems
                .Where(item => item.Id == model.ScheduleItemId)
                .Select(item => item.ModuleId)
                .SingleAsync());
    }

    [Fact]
    public async Task Upsert_rejects_teacher_working_hours_violation()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedScheduleAsync();
        var teacher = new Teacher { FullName = "Викладач" };
        var teacherLessonType = new LessonTypeRef
        {
            Code = "TEACHER_REQUIRED",
            Name = "З викладачем",
            RequiresRoom = false,
            RequiresTeacher = true,
            BlocksRoom = false,
            BlocksTeacher = true
        };
        fixture.Db.AddRange(teacher, teacherLessonType);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.TeacherWorkingHours.Add(new TeacherWorkingHour
        {
            TeacherId = teacher.Id,
            DayOfWeek = DayOfWeek.Monday,
            Start = new TimeOnly(10, 0),
            End = new TimeOnly(12, 0)
        });
        await fixture.Db.SaveChangesAsync();
        var controller = CreateController(fixture.Db);

        var result = await controller.Upsert(new UpsertScheduleItemRequest(
            Id: model.ScheduleItemId,
            Date: Monday,
            TimeStart: "08:00",
            TimeEnd: "09:00",
            GroupId: model.GroupId,
            ModuleId: model.OldModuleId,
            TeacherId: teacher.Id,
            RoomId: null,
            LessonTypeId: teacherLessonType.Id,
            IsLocked: false,
            OverrideNonWorkingDay: false,
            ExpectedRevision: await GetScheduleItemRevisionAsync(fixture.Db, model.ScheduleItemId)));

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(
            model.LessonTypeId,
            await fixture.Db.ScheduleItems
                .Where(item => item.Id == model.ScheduleItemId)
                .Select(item => item.LessonTypeId)
                .SingleAsync());
    }

    [Fact]
    public async Task Upsert_requires_explicit_override_for_non_working_day()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedScheduleAsync();
        fixture.Db.CalendarExceptions.Add(new CalendarException
        {
            Date = Monday,
            IsWorkingDay = false,
            Name = "Неробочий день",
            CourseId = model.CourseId
        });
        await fixture.Db.SaveChangesAsync();
        var controller = CreateController(fixture.Db);
        var expectedRevision = await GetScheduleItemRevisionAsync(fixture.Db, model.ScheduleItemId);
        UpsertScheduleItemRequest Request(bool allow) => new(
            Id: model.ScheduleItemId,
            Date: Monday,
            TimeStart: "08:00",
            TimeEnd: "09:00",
            GroupId: model.GroupId,
            ModuleId: model.OldModuleId,
            TeacherId: null,
            RoomId: null,
            LessonTypeId: model.LessonTypeId,
            IsLocked: false,
            OverrideNonWorkingDay: allow,
            ExpectedRevision: expectedRevision);

        var rejected = await controller.Upsert(Request(false));
        var accepted = await controller.Upsert(Request(true));

        Assert.IsType<ConflictObjectResult>(rejected.Result);
        Assert.IsType<OkObjectResult>(accepted.Result);
    }

    [Fact]
    public async Task Upsert_rejects_incompatible_persisted_topic_for_new_lesson_type()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedLogicalEventAsync();
        var controller = CreateController(fixture.Db);

        var result = await controller.Upsert(new UpsertScheduleItemRequest(
            Id: model.FirstItemId,
            Date: Monday,
            TimeStart: "08:00",
            TimeEnd: "09:00",
            GroupId: model.GroupId,
            ModuleId: model.ModuleId,
            TeacherId: model.FirstTeacherId,
            RoomId: null,
            LessonTypeId: model.TargetLessonTypeId,
            IsLocked: false,
            OverrideNonWorkingDay: false,
            ExpectedRevision: await GetScheduleItemRevisionAsync(fixture.Db, model.FirstItemId)));

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.All(
            await fixture.Db.ScheduleItems.AsNoTracking().ToListAsync(),
            item => Assert.Equal(model.OriginalLessonTypeId, item.LessonTypeId));
    }

    [Fact]
    public async Task Upsert_rejects_minimum_and_maximum_dates_before_database_access()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedScheduleAsync();
        var controller = CreateController(fixture.Db);

        foreach (var date in new[] { DateOnly.MinValue, DateOnly.MaxValue })
        {
            var result = await controller.Upsert(new UpsertScheduleItemRequest(
                Id: null,
                Date: date,
                TimeStart: "08:00",
                TimeEnd: "09:00",
                GroupId: model.GroupId,
                ModuleId: model.OldModuleId,
                TeacherId: null,
                RoomId: null,
                LessonTypeId: model.LessonTypeId,
                IsLocked: false,
                OverrideNonWorkingDay: false));

            Assert.IsType<BadRequestObjectResult>(result.Result);
        }
        Assert.Single(await fixture.Db.ScheduleItems.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Docx_import_rejects_file_larger_than_limit_before_reading_it()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        const long oversizedLength = 10L * 1024 * 1024 + 1;
        var file = new FormFile(Stream.Null, 0, oversizedLength, "file", "КН-1.docx");

        var result = await new DocxImportService().ImportAsync(file, fixture.Db, apply: false, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Contains("10 МБ", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Docx_import_returns_validation_error_for_invalid_package()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var bytes = Encoding.UTF8.GetBytes("це не DOCX");
        await using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", "КН-1.docx");

        var result = await new DocxImportService().ImportAsync(file, fixture.Db, apply: false, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Contains("коректним DOCX", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Docx_import_rejects_compressed_package_with_oversized_xml_part()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var bytes = CreateDocxWithText(new string('А', 2_100_000));
        await using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", "КН-1.docx");

        var result = await new DocxImportService().ImportAsync(file, fixture.Db, apply: false, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Contains("надто великий", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Docx_import_matches_exact_course_code_without_numeric_prefix_collision()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var longerCodeCourse = new Course { Name = "КН-10 — Старший курс", DurationWeeks = 52 };
        var requestedCourse = new Course { Name = "КН-1 — Основний курс", DurationWeeks = 52 };
        fixture.Db.Courses.AddRange(longerCodeCourse, requestedCourse);
        await fixture.Db.SaveChangesAsync();
        var bytes = CreateMinimalImportDocx();
        await using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", "КН-1.docx");

        var result = await new DocxImportService().ImportAsync(
            file,
            fixture.Db,
            apply: false,
            CancellationToken.None);

        Assert.Null(result.Error);
        Assert.True(result.CourseFound);
        Assert.Equal(requestedCourse.Id, result.CourseId);
        Assert.Equal(requestedCourse.Name, result.CourseName);
    }

    [Fact]
    public async Task Docx_import_prefers_exact_normalized_name_over_ambiguous_code_matches()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var exactNameCourse = new Course { Name = "КН - 1", DurationWeeks = 52 };
        fixture.Db.Courses.AddRange(
            exactNameCourse,
            new Course { Name = "КН-1 — Розширений курс", DurationWeeks = 52 });
        await fixture.Db.SaveChangesAsync();
        var bytes = CreateMinimalImportDocx();
        await using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", "КН-1.docx");

        var result = await new DocxImportService().ImportAsync(
            file,
            fixture.Db,
            apply: false,
            CancellationToken.None);

        Assert.Null(result.Error);
        Assert.True(result.CourseFound);
        Assert.Equal(exactNameCourse.Id, result.CourseId);
        Assert.Equal(exactNameCourse.Name, result.CourseName);
    }

    [Fact]
    public async Task Docx_import_does_not_treat_longer_numeric_course_code_as_match()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        fixture.Db.Courses.Add(new Course { Name = "КН-10 — Інший курс", DurationWeeks = 52 });
        await fixture.Db.SaveChangesAsync();
        var bytes = CreateMinimalImportDocx();
        await using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", "КН-1.docx");

        var result = await new DocxImportService().ImportAsync(
            file,
            fixture.Db,
            apply: false,
            CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.False(result.CourseFound);
        Assert.Null(result.CourseId);
        Assert.Contains("Не знайдено", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Docx_import_rejects_ambiguous_exact_course_code_matches()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        fixture.Db.Courses.AddRange(
            new Course { Name = "КН-1 — Перший курс", DurationWeeks = 52 },
            new Course { Name = "Навчальний курс КН-1", DurationWeeks = 52 });
        await fixture.Db.SaveChangesAsync();
        var bytes = CreateMinimalImportDocx();
        await using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", "КН-1.docx");

        var result = await new DocxImportService().ImportAsync(
            file,
            fixture.Db,
            apply: false,
            CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.False(result.CourseFound);
        Assert.Null(result.CourseId);
        Assert.Contains("кілька курсів", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Docx_apply_reuses_existing_lesson_type_after_accidental_adjacent_word_repeat()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var course = new Course { Name = "КН-1", DurationWeeks = 52 };
        var canonicalType = new LessonTypeRef
        {
            Code = "СОКРАТІВСЬКИЙ_СЕМІНАР",
            Name = "Сократівський семінар",
            IsActive = true
        };
        fixture.Db.AddRange(course, canonicalType);
        await fixture.Db.SaveChangesAsync();
        var bytes = CreateTopicTypeChangeImportDocx("Сократівський Сократівський семінар");
        await using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", "КН-1.docx");

        var result = await new DocxImportService().ImportAsync(
            file,
            fixture.Db,
            apply: true,
            CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("повторене сусіднє слово", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, await fixture.Db.LessonTypes.CountAsync());
        Assert.All(
            await fixture.Db.ModuleTopics.AsNoTracking().ToListAsync(),
            topic => Assert.Equal(canonicalType.Id, topic.LessonTypeId));
    }

    [Fact]
    public async Task Docx_apply_on_empty_database_normalizes_repeat_even_when_canonical_name_comes_later()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var course = new Course { Name = "КН-1", DurationWeeks = 52 };
        fixture.Db.Courses.Add(course);
        await fixture.Db.SaveChangesAsync();
        var bytes = CreateTopicImportDocx(
            new[] { "1", "Сократівський Сократівський семінар", "1", "1", "0", "1.1 Перша тема" },
            new[] { "2", "Сократівський семінар", "1", "1", "0", "1.2 Друга тема" });
        await using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", "КН-1.docx");

        var result = await new DocxImportService().ImportAsync(
            file,
            fixture.Db,
            apply: true,
            CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("повторене сусіднє слово", StringComparison.OrdinalIgnoreCase));
        var lessonType = Assert.Single(await fixture.Db.LessonTypes.AsNoTracking().ToListAsync());
        Assert.Equal("Сократівський семінар", lessonType.Name);
        Assert.Equal("СОКРАТІВСЬКИЙ_СЕМІНАР", lessonType.Code);
        Assert.Equal(2, await fixture.Db.ModuleTopics.CountAsync());
        Assert.All(
            await fixture.Db.ModuleTopics.AsNoTracking().ToListAsync(),
            topic => Assert.Equal(lessonType.Id, topic.LessonTypeId));
    }

    [Fact]
    public async Task Docx_apply_merges_existing_repeat_without_recreating_populated_database()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var course = new Course { Name = "КН-1", DurationWeeks = 52 };
        var group = new Group { Name = "КН-1", StudentsCount = 20, Course = course };
        var module = new Module { Code = "1", Title = "Заповнений модуль", Credits = 1, Course = course };
        var canonicalType = new LessonTypeRef
        {
            Code = "СОКРАТІВСЬКИЙ_СЕМІНАР",
            Name = "Сократівський семінар",
            IsActive = true,
            RequiresRoom = true,
            RequiresTeacher = true,
            BlocksRoom = true,
            BlocksTeacher = true,
            CountInPlan = true,
            CountInLoad = true
        };
        var duplicateType = new LessonTypeRef
        {
            Code = "СОКРАТІВСЬКИЙ_СОКРАТІВСЬКИЙ_СЕМІНАР",
            Name = "Сократівський Сократівський семінар",
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
            LessonType = duplicateType,
            TopicCode = "1.1",
            Order = 1,
            TotalHours = 1,
            AuditoriumHours = 1
        };
        var draft = new TeacherDraftItem
        {
            Date = new DateOnly(2026, 9, 7),
            DayOfWeek = DayOfWeek.Monday,
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(9, 45),
            LessonType = duplicateType,
            Group = group,
            Module = module,
            ModuleTopic = topic
        };
        var scheduleItem = new ScheduleItem
        {
            Date = new DateOnly(2026, 9, 7),
            DayOfWeek = DayOfWeek.Monday,
            StartTime = new TimeOnly(10, 0),
            EndTime = new TimeOnly(10, 45),
            LessonType = duplicateType,
            Group = group,
            Module = module,
            ModuleTopic = topic
        };
        fixture.Db.AddRange(canonicalType, draft, scheduleItem);
        await fixture.Db.SaveChangesAsync();
        var draftRevision = draft.Revision;
        var scheduleRevision = scheduleItem.Revision;
        var bytes = CreateTopicImportDocx(
            new[] { "1", duplicateType.Name, "1", "1", "0", "1.1 Перша тема" },
            new[] { "2", canonicalType.Name, "1", "1", "0", "1.2 Друга тема" });
        await using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", "КН-1.docx");

        var result = await new DocxImportService().ImportAsync(
            file,
            fixture.Db,
            apply: true,
            CancellationToken.None);

        Assert.Null(result.Error);
        fixture.Db.ChangeTracker.Clear();
        Assert.False(await fixture.Db.LessonTypes.AnyAsync(type => type.Id == duplicateType.Id));
        Assert.Equal(canonicalType.Id, await fixture.Db.TeacherDraftItems
            .Where(item => item.Id == draft.Id)
            .Select(item => item.LessonTypeId)
            .SingleAsync());
        Assert.NotEqual(draftRevision, await fixture.Db.TeacherDraftItems
            .Where(item => item.Id == draft.Id)
            .Select(item => item.Revision)
            .SingleAsync());
        var staleDraftDelete = await new TeacherDraftsController(
                fixture.Db,
                new RulesService(fixture.Db),
                queryService: null!,
                exportService: null!,
                autogenService: null!,
                autogenJobService: null!,
                publishService: null!)
            .Delete(draft.Id, draftRevision);
        Assert.IsType<ConflictObjectResult>(staleDraftDelete);
        Assert.True(await fixture.Db.TeacherDraftItems.AnyAsync(item => item.Id == draft.Id));
        Assert.Equal(canonicalType.Id, await fixture.Db.ScheduleItems
            .Where(item => item.Id == scheduleItem.Id)
            .Select(item => item.LessonTypeId)
            .SingleAsync());
        Assert.NotEqual(scheduleRevision, await fixture.Db.ScheduleItems
            .Where(item => item.Id == scheduleItem.Id)
            .Select(item => item.Revision)
            .SingleAsync());
        var staleDelete = await CreateController(fixture.Db).Delete(scheduleItem.Id, scheduleRevision);
        Assert.IsType<ConflictObjectResult>(staleDelete);
        Assert.True(await fixture.Db.ScheduleItems.AnyAsync(item => item.Id == scheduleItem.Id));
        Assert.All(
            await fixture.Db.ModuleTopics.AsNoTracking().ToListAsync(),
            importedTopic => Assert.Equal(canonicalType.Id, importedTopic.LessonTypeId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Docx_apply_rejects_lesson_type_change_for_used_topic(bool useDraft)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var course = new Course { Name = "КН-1", DurationWeeks = 52 };
        var group = new Group { Name = "КН-1", StudentsCount = 20, Course = course };
        var module = new Module { Code = "1", Title = "Початковий модуль", Credits = 1, Course = course };
        var originalType = new LessonTypeRef
        {
            Code = "LECTURE",
            Name = "Лекція",
            IsActive = true,
            RequiresRoom = false,
            RequiresTeacher = false,
            BlocksRoom = false,
            BlocksTeacher = false,
            CountInPlan = true
        };
        var importedType = new LessonTypeRef
        {
            Code = "PRACTICE",
            Name = "Практика",
            IsActive = true,
            RequiresRoom = false,
            RequiresTeacher = false,
            BlocksRoom = false,
            BlocksTeacher = false,
            CountInPlan = true
        };
        fixture.Db.AddRange(course, group, module, originalType, importedType);
        await fixture.Db.SaveChangesAsync();
        var topic = new ModuleTopic
        {
            ModuleId = module.Id,
            TopicCode = "1.1",
            Order = 1,
            LessonTypeId = originalType.Id,
            TotalHours = 1,
            AuditoriumHours = 1
        };
        fixture.Db.ModuleTopics.Add(topic);
        await fixture.Db.SaveChangesAsync();
        if (useDraft)
        {
            fixture.Db.TeacherDraftItems.Add(new TeacherDraftItem
            {
                Date = Monday,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(9, 0),
                GroupId = group.Id,
                ModuleId = module.Id,
                ModuleTopicId = topic.Id,
                LessonTypeId = originalType.Id
            });
        }
        else
        {
            fixture.Db.ScheduleItems.Add(new ScheduleItem
            {
                Date = Monday,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(9, 0),
                GroupId = group.Id,
                ModuleId = module.Id,
                ModuleTopicId = topic.Id,
                LessonTypeId = originalType.Id
            });
        }
        await fixture.Db.SaveChangesAsync();
        var bytes = CreateTopicTypeChangeImportDocx(importedType.Name);
        await using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", "КН-1.docx");

        var result = await new DocxImportService().ImportAsync(
            file,
            fixture.Db,
            apply: true,
            CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Contains("не можна змінити", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalType.Id, await fixture.Db.ModuleTopics
            .AsNoTracking()
            .Where(item => item.Id == topic.Id)
            .Select(item => item.LessonTypeId)
            .SingleAsync());
        Assert.Equal("Початковий модуль", await fixture.Db.Modules
            .AsNoTracking()
            .Where(item => item.Id == module.Id)
            .Select(item => item.Title)
            .SingleAsync());
    }

    [Fact]
    public async Task Docx_apply_rolls_back_earlier_saves_when_later_step_fails()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        fixture.Db.Courses.Add(new Course { Name = "КН-1", DurationWeeks = 52 });
        await fixture.Db.SaveChangesAsync();
        await fixture.AddFailingModulePlanInsertTriggerAsync();
        var bytes = CreateMinimalImportDocx();
        await using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", "КН-1.docx");

        var result = await new DocxImportService().ImportAsync(
            file,
            fixture.Db,
            apply: true,
            CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Contains("Імпорт скасовано", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(fixture.Db.ChangeTracker.HasChanges());
        Assert.Empty(await fixture.Db.Modules.ToListAsync());
        Assert.Empty(await fixture.Db.ModuleCourses.ToListAsync());
        Assert.Empty(await fixture.Db.ModulePlans.ToListAsync());
    }

    [Fact]
    public async Task Docx_apply_rejects_topic_hours_that_exceed_total_before_mutation()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        fixture.Db.Courses.Add(new Course { Name = "КН-1", DurationWeeks = 52 });
        await fixture.Db.SaveChangesAsync();
        var bytes = CreateTopicImportDocx(
            new[] { "1", "Лекція", "1", "1", "1", "1.1 Тестова тема" });
        await using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", "КН-1.docx");

        var result = await new DocxImportService().ImportAsync(
            file,
            fixture.Db,
            apply: true,
            CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Contains("некоректний розподіл годин", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(fixture.Db.ChangeTracker.HasChanges());
        Assert.Empty(await fixture.Db.Modules.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Db.ModuleCourses.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Db.ModulePlans.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Db.ModuleTopics.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Db.LessonTypes.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Docx_apply_rejects_duplicate_topic_codes_before_mutation()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        fixture.Db.Courses.Add(new Course { Name = "КН-1", DurationWeeks = 52 });
        await fixture.Db.SaveChangesAsync();
        var bytes = CreateTopicImportDocx(
            new[] { "1", "Лекція", "1", "1", "0", "Т.1.1 Перша тема" },
            new[] { "2", "Практика", "1", "1", "0", "т.1.1 Друга тема" });
        await using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", "КН-1.docx");

        var result = await new DocxImportService().ImportAsync(
            file,
            fixture.Db,
            apply: true,
            CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Contains("повторюється", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(fixture.Db.ChangeTracker.HasChanges());
        Assert.Empty(await fixture.Db.Modules.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Db.ModuleCourses.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Db.ModulePlans.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Db.ModuleTopics.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Db.LessonTypes.AsNoTracking().ToListAsync());
    }

    private static ScheduleController CreateController(AppDbContext db)
        => new(db, new RulesService(db), new AggregatesService(db));

    private static async Task<Guid> GetScheduleItemRevisionAsync(AppDbContext db, int id)
    {
        var date = await db.ScheduleItems
            .AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => item.Date)
            .SingleAsync();
        var result = await CreateController(db).Get(
            DateHelpers.StartOfWeek(date),
            courseId: null,
            groupId: null,
            teacherId: null,
            roomId: null);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var items = Assert.IsAssignableFrom<IReadOnlyList<ScheduleItemDto>>(ok.Value);
        return Assert.Single(items, item => item.Id == id).Revision;
    }

    private static byte[] CreateMinimalImportDocx()
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var mainPart = document.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(
                new Table(
                    CreateTableRow("Код", "Назва", "Кредити"),
                    CreateTableRow("1", "Тестовий модуль", "1"))));
            mainPart.Document.Save();
        }
        return stream.ToArray();
    }

    private static byte[] CreateTopicTypeChangeImportDocx(string lessonTypeName)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var mainPart = document.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(
                new Table(
                    CreateTableRow("Код", "Назва", "Кредити"),
                    CreateTableRow("1", "Імпортований модуль", "1")),
                new Table(
                    CreateTableRow("1", "2", "3", "4", "5", "6"),
                    CreateTableRow("1", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty),
                    CreateTableRow("1", lessonTypeName, "1", "1", "0", "1.1 Тестова тема"))));
            mainPart.Document.Save();
        }
        return stream.ToArray();
    }

    private static byte[] CreateTopicImportDocx(params string[][] topicRows)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var mainPart = document.AddMainDocumentPart();
            var rows = new List<TableRow>
            {
                CreateTableRow("1", "2", "3", "4", "5", "6"),
                CreateTableRow("1", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty)
            };
            rows.AddRange(topicRows.Select(CreateTableRow));
            mainPart.Document = new Document(new Body(
                new Table(
                    CreateTableRow("Код", "Назва", "Кредити"),
                    CreateTableRow("1", "Тестовий модуль", "1")),
                new Table(rows)));
            mainPart.Document.Save();
        }
        return stream.ToArray();
    }

    private static byte[] CreateDocxWithText(string text)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var mainPart = document.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(new Paragraph(new Run(new Text(text)))));
            mainPart.Document.Save();
        }
        return stream.ToArray();
    }

    private static TableRow CreateTableRow(params string[] values)
        => new(values.Select(value => new TableCell(new Paragraph(new Run(new Text(value))))));

    private sealed record SeedModel(
        int CourseId,
        int GroupId,
        int OldModuleId,
        int NewModuleId,
        int LessonTypeId,
        int ScheduleItemId,
        Guid ScheduleItemRevision);

    private sealed record LogicalEventSeedModel(
        int CourseId,
        int GroupId,
        int ModuleId,
        int ExternalModuleId,
        int OriginalLessonTypeId,
        int TargetLessonTypeId,
        int FirstItemId,
        int FirstTopicId,
        int SecondTopicId,
        int FirstTeacherId,
        int SecondTeacherId,
        string? BatchKey);

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

        public async Task<SeedModel> SeedScheduleAsync()
        {
            var course = new Course { Name = "Тестовий курс", DurationWeeks = 52 };
            var group = new Group { Name = "Т-1", StudentsCount = 20, Course = course };
            var oldModule = new Module { Code = "М1", Title = "Старий модуль", Credits = 1, Course = course };
            var newModule = new Module { Code = "М2", Title = "Новий модуль", Credits = 1, Course = course };
            var lessonType = new LessonTypeRef
            {
                Code = "INDEPENDENT",
                Name = "Самостійне заняття",
                RequiresRoom = false,
                RequiresTeacher = false,
                BlocksRoom = false,
                BlocksTeacher = false,
                CountInPlan = true,
                CountInLoad = false
            };
            Db.AddRange(course, group, oldModule, newModule, lessonType);
            await Db.SaveChangesAsync();
            var oldPlan = new ModulePlan
            {
                CourseId = course.Id,
                ModuleId = oldModule.Id,
                TargetHours = 10,
                ScheduledHours = 1,
                IsActive = true
            };
            var newPlan = new ModulePlan
            {
                CourseId = course.Id,
                ModuleId = newModule.Id,
                TargetHours = 10,
                ScheduledHours = 0,
                IsActive = true
            };
            var item = new ScheduleItem
            {
                Date = Monday,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(9, 0),
                GroupId = group.Id,
                ModuleId = oldModule.Id,
                LessonTypeId = lessonType.Id
            };
            Db.ModulePlans.AddRange(oldPlan, newPlan);
            Db.ScheduleItems.Add(item);
            Db.TimeSlots.Add(new TimeSlot
            {
                CourseId = course.Id,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(8, 0),
                End = new TimeOnly(9, 0),
                SortOrder = 1,
                IsActive = true
            });
            await Db.SaveChangesAsync();

            return new SeedModel(
                course.Id,
                group.Id,
                oldModule.Id,
                newModule.Id,
                lessonType.Id,
                item.Id,
                item.Revision);
        }

        public async Task<LogicalEventSeedModel> SeedLogicalEventAsync(bool withBatchKey = true)
        {
            var model = await SeedScheduleAsync();
            var firstTeacher = new Teacher { FullName = "Перший викладач" };
            var secondTeacher = new Teacher { FullName = "Другий викладач" };
            var targetLessonType = new LessonTypeRef
            {
                Code = "ALTERNATE",
                Name = "Інший тип",
                RequiresRoom = false,
                RequiresTeacher = false,
                BlocksRoom = false,
                BlocksTeacher = false,
                CountInPlan = true,
                CountInLoad = false
            };
            var firstTopic = new ModuleTopic
            {
                ModuleId = model.OldModuleId,
                LessonTypeId = model.LessonTypeId,
                Order = 1,
                TopicCode = "Т1",
                TotalHours = 1,
                AuditoriumHours = 1
            };
            var secondTopic = new ModuleTopic
            {
                ModuleId = model.OldModuleId,
                LessonTypeId = model.LessonTypeId,
                Order = 2,
                TopicCode = "Т2",
                TotalHours = 1,
                AuditoriumHours = 1
            };
            Db.AddRange(firstTeacher, secondTeacher, targetLessonType, firstTopic, secondTopic);
            await Db.SaveChangesAsync();

            var firstItem = await Db.ScheduleItems.SingleAsync(item => item.Id == model.ScheduleItemId);
            var batchKey = withBatchKey ? "official-logical-event" : null;
            firstItem.ModuleTopicId = firstTopic.Id;
            firstItem.TeacherId = firstTeacher.Id;
            firstItem.BatchKey = batchKey;
            var secondItem = new ScheduleItem
            {
                Date = firstItem.Date,
                DayOfWeek = firstItem.DayOfWeek,
                StartTime = firstItem.StartTime,
                EndTime = firstItem.EndTime,
                GroupId = firstItem.GroupId,
                ModuleId = firstItem.ModuleId,
                ModuleTopicId = secondTopic.Id,
                TeacherId = secondTeacher.Id,
                LessonTypeId = firstItem.LessonTypeId,
                BatchKey = batchKey
            };
            Db.ScheduleItems.Add(secondItem);
            await Db.SaveChangesAsync();

            return new LogicalEventSeedModel(
                model.CourseId,
                model.GroupId,
                model.OldModuleId,
                model.NewModuleId,
                model.LessonTypeId,
                targetLessonType.Id,
                firstItem.Id,
                firstTopic.Id,
                secondTopic.Id,
                firstTeacher.Id,
                secondTeacher.Id,
                batchKey);
        }

        public Task AddFailingModulePlanUpdateTriggerAsync()
            => Db.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER fail_module_plan_update BEFORE UPDATE ON ModulePlans " +
                "BEGIN SELECT RAISE(ABORT, 'Примусовий збій перерахунку'); END;");

        public Task AddFailingModulePlanInsertTriggerAsync()
            => Db.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER fail_module_plan_insert BEFORE INSERT ON ModulePlans " +
                "BEGIN SELECT RAISE(ABORT, 'Примусовий збій імпорту'); END;");

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
