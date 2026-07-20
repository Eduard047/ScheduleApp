using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Admin;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class TeacherDraftBatchAtomicityTests
{
    private static readonly DateOnly Monday = new(2026, 5, 4);
    private static readonly DateTime LegacyUpdatedAt = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task UpsertBatch_commits_complete_valid_package()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync(secondDraftLocked: false);
        var controller = CreateController(fixture.Db);
        var request = new TeacherDraftBatchUpsertRequest(new List<DraftUpsertRequest>
        {
            CreateUpsertRequest(model.FirstDraftId, model, "07:00", "08:00") with
            {
                BatchKey = "batch-regression"
            },
            CreateUpsertRequest(model.SecondDraftId, model, "11:00", "12:00")
        });

        var result = await controller.UpsertBatch(request);

        var response = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<TeacherDraftBatchUpsertResult>(response.Value);
        Assert.Equal(2, payload.Processed);
        Assert.Equal(new[] { model.FirstDraftId, model.SecondDraftId }, payload.Ids);
        var persistedDrafts = await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .OrderBy(item => item.Id)
            .ToListAsync();
        Assert.Equal(new[] { new TimeOnly(7, 0), new TimeOnly(11, 0) }, persistedDrafts.Select(item => item.StartTime));
        Assert.Equal("batch-regression", persistedDrafts[0].BatchKey);
        Assert.True(persistedDrafts[0].UpdatedAt > LegacyUpdatedAt);
    }

    [Fact]
    public async Task UpsertBatch_rolls_back_first_update_when_second_item_is_invalid()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync(secondDraftLocked: false);
        var controller = CreateController(fixture.Db);
        var request = new TeacherDraftBatchUpsertRequest(new List<DraftUpsertRequest>
        {
            CreateUpsertRequest(model.FirstDraftId, model, "09:00", "10:00"),
            CreateUpsertRequest(model.SecondDraftId, model, "12:00", "11:00")
        });

        var result = await controller.UpsertBatch(request);

        var failure = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(400, failure.StatusCode);
        var firstDraft = await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .SingleAsync(item => item.Id == model.FirstDraftId);
        Assert.Equal(new TimeOnly(8, 0), firstDraft.StartTime);
        Assert.Equal(new TimeOnly(9, 0), firstDraft.EndTime);
    }

    [Fact]
    public async Task DeleteBatch_rolls_back_first_delete_when_second_item_is_locked()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync(secondDraftLocked: true);
        var controller = CreateController(fixture.Db);
        var request = new TeacherDraftBatchDeleteRequest(
            new List<int> { model.FirstDraftId, model.SecondDraftId });

        var result = await controller.DeleteBatch(request, confirm: true);

        var failure = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(409, failure.StatusCode);
        var remainingIds = await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .OrderBy(item => item.Id)
            .Select(item => item.Id)
            .ToListAsync();
        Assert.Equal(new[] { model.FirstDraftId, model.SecondDraftId }, remainingIds);
    }

    [Fact]
    public async Task UpsertBatch_revalidates_final_state_after_temporary_validation_bypass()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync(secondDraftLocked: false);
        var controller = CreateController(fixture.Db);
        var request = new TeacherDraftBatchUpsertRequest(new List<DraftUpsertRequest>
        {
            CreateUpsertRequest(model.FirstDraftId, model, "09:00", "10:00") with
            {
                IgnoreValidationErrors = true
            },
            CreateUpsertRequest(model.SecondDraftId, model, "09:00", "10:00") with
            {
                IgnoreValidationErrors = true
            }
        });

        var result = await controller.UpsertBatch(request);

        var failure = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(409, failure.StatusCode);
        var persistedTimes = await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .OrderBy(item => item.Id)
            .Select(item => new { item.StartTime, item.EndTime })
            .ToListAsync();
        Assert.Collection(
            persistedTimes,
            first =>
            {
                Assert.Equal(new TimeOnly(8, 0), first.StartTime);
                Assert.Equal(new TimeOnly(9, 0), first.EndTime);
            },
            second =>
            {
                Assert.Equal(new TimeOnly(10, 0), second.StartTime);
                Assert.Equal(new TimeOnly(11, 0), second.EndTime);
            });
    }

    [Fact]
    public async Task MutateBatch_rolls_back_upsert_when_following_delete_is_locked()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync(secondDraftLocked: true);
        var controller = CreateController(fixture.Db);
        var request = new TeacherDraftBatchMutationRequest(
            Upserts: new List<DraftUpsertRequest>
            {
                CreateUpsertRequest(model.FirstDraftId, model, "09:00", "10:00")
            },
            DeleteIds: new List<int> { model.SecondDraftId },
            Confirm: true,
            Unrestricted: false);

        var result = await controller.MutateBatch(request);

        var failure = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(409, failure.StatusCode);
        var drafts = await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .OrderBy(item => item.Id)
            .ToListAsync();
        Assert.Equal(2, drafts.Count);
        Assert.Equal(new TimeOnly(8, 0), drafts[0].StartTime);
        Assert.Equal(new TimeOnly(9, 0), drafts[0].EndTime);
    }

    [Fact]
    public async Task UpsertBatch_accepts_multi_topic_co_teacher_rows_of_one_logical_event()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync(secondDraftLocked: false);
        var controller = CreateController(fixture.Db);
        const string batchKey = "logical-event";
        var request = new TeacherDraftBatchUpsertRequest(new List<DraftUpsertRequest>
        {
            CreateLogicalEventRequest(model, model.FirstTopicId, model.FirstTeacherId, batchKey),
            CreateLogicalEventRequest(model, model.SecondTopicId, model.SecondTeacherId, batchKey)
        });

        var result = await controller.UpsertBatch(request);

        var response = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<TeacherDraftBatchUpsertResult>(response.Value);
        Assert.Equal(2, payload.Processed);
        var logicalRows = await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => item.BatchKey == batchKey)
            .ToListAsync();
        Assert.Equal(2, logicalRows.Count);
        Assert.Equal(2, logicalRows.Select(item => item.ModuleTopicId).Distinct().Count());
        Assert.Equal(2, logicalRows.Select(item => item.TeacherId).Distinct().Count());
    }

    [Fact]
    public async Task Single_mutations_reject_partial_logical_event_but_complete_batch_delete_succeeds()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync(secondDraftLocked: false);
        var controller = CreateController(fixture.Db);
        const string batchKey = "logical-event-mutation-scope";
        var createResult = await controller.UpsertBatch(new TeacherDraftBatchUpsertRequest(
            new List<DraftUpsertRequest>
            {
                CreateLogicalEventRequest(model, model.FirstTopicId, model.FirstTeacherId, batchKey),
                CreateLogicalEventRequest(model, model.SecondTopicId, model.SecondTeacherId, batchKey)
            }));
        var createResponse = Assert.IsType<OkObjectResult>(createResult.Result);
        var created = Assert.IsType<TeacherDraftBatchUpsertResult>(createResponse.Value);
        Assert.Equal(2, created.Ids.Count);

        var partialUpdate = await controller.Upsert(
            CreateLogicalEventRequest(model, model.FirstTopicId, model.FirstTeacherId, batchKey) with
            {
                Id = created.Ids[0]
            });
        var partialDelete = await controller.Delete(
            created.Ids[0],
            confirm: true,
            unrestricted: true);

        Assert.IsType<ConflictObjectResult>(partialUpdate.Result);
        Assert.IsType<ConflictObjectResult>(partialDelete);
        Assert.Equal(2, await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .CountAsync(item => item.BatchKey == batchKey));

        var batchDelete = await controller.DeleteBatch(
            new TeacherDraftBatchDeleteRequest(created.Ids),
            confirm: true);

        var deleteResponse = Assert.IsType<OkObjectResult>(batchDelete.Result);
        Assert.Equal(2, Assert.IsType<TeacherDraftBatchDeleteResult>(deleteResponse.Value).Deleted);
        Assert.Equal(0, await fixture.Db.TeacherDraftItems.CountAsync(item => item.BatchKey == batchKey));
    }

    [Fact]
    public async Task UpsertBatch_does_not_hide_external_collision_behind_logical_event_siblings()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync(secondDraftLocked: false);
        var controller = CreateController(fixture.Db);
        const string batchKey = "colliding-event";
        var first = CreateLogicalEventRequest(model, model.FirstTopicId, model.FirstTeacherId, batchKey) with
        {
            TimeStart = "08:00",
            TimeEnd = "09:00",
            IgnoreValidationErrors = true
        };
        var second = CreateLogicalEventRequest(model, model.SecondTopicId, model.SecondTeacherId, batchKey) with
        {
            TimeStart = "08:00",
            TimeEnd = "09:00",
            IgnoreValidationErrors = true
        };

        var result = await controller.UpsertBatch(new TeacherDraftBatchUpsertRequest(new List<DraftUpsertRequest>
        {
            first,
            second
        }));

        var failure = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(409, failure.StatusCode);
        Assert.Equal(2, await fixture.Db.TeacherDraftItems.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task UpsertBatch_does_not_collapse_same_batch_key_with_different_event_signature()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync(secondDraftLocked: false);
        const string batchKey = "reused-key";
        var existing = await fixture.Db.TeacherDraftItems.SingleAsync(item => item.Id == model.FirstDraftId);
        existing.BatchKey = batchKey;
        await fixture.Db.SaveChangesAsync();
        var controller = CreateController(fixture.Db);
        var request = CreateLogicalEventRequest(model, model.FirstTopicId, model.FirstTeacherId, batchKey) with
        {
            TimeStart = "08:30",
            TimeEnd = "09:30",
            IgnoreValidationErrors = true
        };

        var result = await controller.UpsertBatch(new TeacherDraftBatchUpsertRequest(new List<DraftUpsertRequest>
        {
            request
        }));

        var failure = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(409, failure.StatusCode);
        Assert.Equal(2, await fixture.Db.TeacherDraftItems.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Upsert_rejects_batch_key_longer_than_database_limit()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync(secondDraftLocked: false);
        var controller = CreateController(fixture.Db);
        var request = CreateUpsertRequest(model.FirstDraftId, model, "09:00", "10:00") with
        {
            BatchKey = new string('x', 65)
        };

        var result = await controller.Upsert(request);

        var failure = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(400, failure.StatusCode);
        var persisted = await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .SingleAsync(item => item.Id == model.FirstDraftId);
        Assert.Equal(new TimeOnly(8, 0), persisted.StartTime);
    }

    [Fact]
    public async Task Upsert_rejects_missing_teacher_even_when_validation_bypass_is_requested()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync(secondDraftLocked: false);
        var controller = CreateController(fixture.Db);
        var request = CreateLogicalEventRequest(
            model,
            model.FirstTopicId,
            int.MaxValue,
            "missing-teacher") with
        {
            IgnoreValidationErrors = true
        };

        var result = await controller.Upsert(request);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(2, await fixture.Db.TeacherDraftItems.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task UpsertBatch_rolls_back_when_item_has_missing_topic_even_in_unrestricted_mode()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync(secondDraftLocked: false);
        var controller = CreateController(fixture.Db);
        var request = new TeacherDraftBatchUpsertRequest(
            new List<DraftUpsertRequest>
            {
                CreateUpsertRequest(model.FirstDraftId, model, "09:00", "10:00"),
                CreateUpsertRequest(model.SecondDraftId, model, "12:00", "13:00") with
                {
                    ModuleTopicId = int.MaxValue,
                    IgnoreValidationErrors = true
                }
            },
            Unrestricted: true);

        var result = await controller.UpsertBatch(request);

        var failure = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(409, failure.StatusCode);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(
            new[] { new TimeOnly(8, 0), new TimeOnly(10, 0) },
            await fixture.Db.TeacherDraftItems
                .AsNoTracking()
                .OrderBy(item => item.Id)
                .Select(item => item.StartTime)
                .ToArrayAsync());
    }

    [Fact]
    public async Task Upsert_accepts_module_shared_with_group_course_and_rejects_unrelated_module()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync(secondDraftLocked: false);
        var foreignCourse = new Course { Name = "Інший курс", DurationWeeks = 12 };
        var foreignModule = new Module
        {
            Code = "SHARED",
            Title = "Спільний модуль",
            Credits = 1,
            Course = foreignCourse
        };
        fixture.Db.AddRange(foreignCourse, foreignModule);
        await fixture.Db.SaveChangesAsync();
        var request = CreateUpsertRequest(0, model, "13:00", "14:00") with
        {
            Id = null,
            ModuleId = foreignModule.Id
        };
        var controller = CreateController(fixture.Db);

        var rejected = await controller.Upsert(request);

        Assert.IsType<ConflictObjectResult>(rejected.Result);
        fixture.Db.ModuleCourses.Add(new ModuleCourse
        {
            ModuleId = foreignModule.Id,
            CourseId = await fixture.Db.Groups
                .Where(group => group.Id == model.GroupId)
                .Select(group => group.CourseId)
                .SingleAsync()
        });
        await fixture.Db.SaveChangesAsync();

        var accepted = await controller.Upsert(request);

        Assert.IsType<OkObjectResult>(accepted.Result);
        Assert.Equal(3, await fixture.Db.TeacherDraftItems.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task UpsertBatch_rejects_null_item_without_starting_partial_mutation()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.SeedAsync(secondDraftLocked: false);
        var controller = CreateController(fixture.Db);

        var result = await controller.UpsertBatch(
            new TeacherDraftBatchUpsertRequest(new List<DraftUpsertRequest> { null! }));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(2, await fixture.Db.TeacherDraftItems.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task UpsertBatch_unrestricted_mode_commits_bypassable_conflict_and_stores_report()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync(secondDraftLocked: false);
        var controller = CreateController(fixture.Db);
        var request = new TeacherDraftBatchUpsertRequest(
            new List<DraftUpsertRequest>
            {
                CreateUpsertRequest(model.FirstDraftId, model, "09:00", "10:00"),
                CreateUpsertRequest(model.SecondDraftId, model, "09:00", "10:00")
            },
            Unrestricted: true);

        var result = await controller.UpsertBatch(request);

        Assert.IsType<OkObjectResult>(result.Result);
        var drafts = await fixture.Db.TeacherDraftItems.AsNoTracking().ToListAsync();
        Assert.All(drafts, draft => Assert.False(string.IsNullOrWhiteSpace(draft.ValidationWarnings)));
    }

    [Fact]
    public async Task UpsertBatch_rejects_logical_event_with_different_self_study_mode()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync(secondDraftLocked: false);
        const string batchKey = "inconsistent-event";
        var controller = CreateController(fixture.Db);
        var request = new TeacherDraftBatchUpsertRequest(new List<DraftUpsertRequest>
        {
            CreateLogicalEventRequest(model, model.FirstTopicId, model.FirstTeacherId, batchKey),
            CreateLogicalEventRequest(model, model.SecondTopicId, model.SecondTeacherId, batchKey) with
            {
                IsSelfStudy = true
            }
        });

        var result = await controller.UpsertBatch(request);

        var failure = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(409, failure.StatusCode);
        Assert.Equal(2, await fixture.Db.TeacherDraftItems.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task UpsertBatch_rejects_logical_event_with_different_rooms()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync(secondDraftLocked: false);
        var lessonType = new LessonTypeRef
        {
            Code = "ROOM_EVENT",
            Name = "Аудиторне заняття",
            RequiresRoom = true,
            RequiresTeacher = false,
            BlocksRoom = true,
            BlocksTeacher = false
        };
        var building = new Building { Name = "Корпус" };
        var firstRoom = new Room { Name = "101", Capacity = 40, Building = building };
        var secondRoom = new Room { Name = "102", Capacity = 40, Building = building };
        fixture.Db.AddRange(lessonType, building, firstRoom, secondRoom);
        await fixture.Db.SaveChangesAsync();
        var firstTopic = new ModuleTopic
        {
            ModuleId = model.ModuleId,
            LessonTypeId = lessonType.Id,
            Order = 10,
            TopicCode = "R1",
            TotalHours = 1,
            AuditoriumHours = 1
        };
        var secondTopic = new ModuleTopic
        {
            ModuleId = model.ModuleId,
            LessonTypeId = lessonType.Id,
            Order = 11,
            TopicCode = "R2",
            TotalHours = 1,
            AuditoriumHours = 1
        };
        fixture.Db.AddRange(firstTopic, secondTopic);
        await fixture.Db.SaveChangesAsync();
        const string batchKey = "inconsistent-room-event";
        DraftUpsertRequest Request(int topicId, int roomId) => new(
            Id: null,
            Date: Monday,
            TimeStart: "13:00",
            TimeEnd: "14:00",
            GroupId: model.GroupId,
            ModuleId: model.ModuleId,
            ModuleTopicId: topicId,
            TeacherId: null,
            RoomId: roomId,
            RequiresRoom: true,
            LessonTypeId: lessonType.Id,
            BatchKey: batchKey);
        var controller = CreateController(fixture.Db);

        var result = await controller.UpsertBatch(new TeacherDraftBatchUpsertRequest(
            new List<DraftUpsertRequest>
            {
                Request(firstTopic.Id, firstRoom.Id),
                Request(secondTopic.Id, secondRoom.Id)
            }));

        var failure = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(409, failure.StatusCode);
        Assert.Equal(2, await fixture.Db.TeacherDraftItems.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Upsert_trims_batch_key_before_persisting()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync(secondDraftLocked: false);
        var controller = CreateController(fixture.Db);
        var request = CreateUpsertRequest(model.FirstDraftId, model, "09:00", "10:00") with
        {
            BatchKey = "  normalized-key  "
        };

        var result = await controller.Upsert(request);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(
            "normalized-key",
            await fixture.Db.TeacherDraftItems
                .Where(item => item.Id == model.FirstDraftId)
                .Select(item => item.BatchKey)
                .SingleAsync());
    }

    [Fact]
    public async Task Upsert_rejects_minimum_and_maximum_dates_before_database_access()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync(secondDraftLocked: false);
        var controller = CreateController(fixture.Db);

        foreach (var date in new[] { DateOnly.MinValue, DateOnly.MaxValue })
        {
            var result = await controller.Upsert(
                CreateUpsertRequest(model.FirstDraftId, model, "09:00", "10:00") with
                {
                    Date = date
                });

            Assert.IsType<BadRequestObjectResult>(result.Result);
        }
        Assert.Equal(2, await fixture.Db.TeacherDraftItems.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Approved_draft_rejects_normal_update_and_delete()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync(secondDraftLocked: false);
        var approved = await fixture.Db.TeacherDraftItems.SingleAsync(item => item.Id == model.FirstDraftId);
        approved.Status = DraftStatus.Published;
        await fixture.Db.SaveChangesAsync();
        var controller = CreateController(fixture.Db);

        var update = await controller.Upsert(CreateUpsertRequest(
            model.FirstDraftId,
            model,
            "09:00",
            "10:00"));
        var delete = await controller.Delete(model.FirstDraftId, confirm: true, unrestricted: false);

        Assert.IsType<ConflictObjectResult>(update.Result);
        Assert.IsType<ConflictObjectResult>(delete);
        fixture.Db.ChangeTracker.Clear();
        var persisted = await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .SingleAsync(item => item.Id == model.FirstDraftId);
        Assert.Equal(DraftStatus.Published, persisted.Status);
        Assert.Equal(new TimeOnly(8, 0), persisted.StartTime);
    }

    [Fact]
    public async Task ClearWeek_preserves_unlocked_approved_drafts()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync(secondDraftLocked: false);
        var approved = await fixture.Db.TeacherDraftItems.SingleAsync(item => item.Id == model.FirstDraftId);
        approved.Status = DraftStatus.Published;
        await fixture.Db.SaveChangesAsync();
        var controller = CreateController(fixture.Db);

        var result = await controller.ClearWeek(new ClearWeekRequest(Monday, GroupId: model.GroupId));

        var response = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(1, Assert.IsType<ClearWeekResult>(response.Value).Deleted);
        var remaining = await fixture.Db.TeacherDraftItems.AsNoTracking().SingleAsync();
        Assert.Equal(model.FirstDraftId, remaining.Id);
        Assert.Equal(DraftStatus.Published, remaining.Status);
    }

    [Fact]
    public async Task ClearWeek_removes_complete_all_draft_logical_event_and_preserves_unrelated_approved_rows()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync(secondDraftLocked: false);
        var unrelatedRows = await fixture.Db.TeacherDraftItems.ToListAsync();
        foreach (var row in unrelatedRows)
        {
            row.Status = DraftStatus.Published;
        }
        await fixture.Db.SaveChangesAsync();
        var controller = CreateController(fixture.Db);
        const string batchKey = "clear-complete-event";
        var createResult = await controller.UpsertBatch(new TeacherDraftBatchUpsertRequest(
            new List<DraftUpsertRequest>
            {
                CreateLogicalEventRequest(model, model.FirstTopicId, model.FirstTeacherId, batchKey),
                CreateLogicalEventRequest(model, model.SecondTopicId, model.SecondTeacherId, batchKey)
            }));
        var createResponse = Assert.IsType<OkObjectResult>(createResult.Result);
        var created = Assert.IsType<TeacherDraftBatchUpsertResult>(createResponse.Value);

        var result = await controller.ClearWeek(new ClearWeekRequest(Monday, GroupId: model.GroupId));

        var response = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(2, Assert.IsType<ClearWeekResult>(response.Value).Deleted);
        Assert.Equal(0, await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .CountAsync(item => created.Ids.Contains(item.Id)));
        var remaining = await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .OrderBy(item => item.Id)
            .ToListAsync();
        Assert.Equal(2, remaining.Count);
        Assert.All(remaining, item => Assert.Equal(DraftStatus.Published, item.Status));
    }

    [Fact]
    public async Task ClearWeek_rejects_mixed_status_logical_event_without_deleting_other_drafts()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync(secondDraftLocked: false);
        var controller = CreateController(fixture.Db);
        const string batchKey = "clear-mixed-event";
        var createResult = await controller.UpsertBatch(new TeacherDraftBatchUpsertRequest(
            new List<DraftUpsertRequest>
            {
                CreateLogicalEventRequest(model, model.FirstTopicId, model.FirstTeacherId, batchKey),
                CreateLogicalEventRequest(model, model.SecondTopicId, model.SecondTeacherId, batchKey)
            }));
        var createResponse = Assert.IsType<OkObjectResult>(createResult.Result);
        var created = Assert.IsType<TeacherDraftBatchUpsertResult>(createResponse.Value);
        var publishedSibling = await fixture.Db.TeacherDraftItems
            .SingleAsync(item => item.Id == created.Ids[0]);
        publishedSibling.Status = DraftStatus.Published;
        await fixture.Db.SaveChangesAsync();

        var result = await controller.ClearWeek(new ClearWeekRequest(Monday, GroupId: model.GroupId));

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Contains("змішані статуси", conflict.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(4, await fixture.Db.TeacherDraftItems.AsNoTracking().CountAsync());
        var packageStatuses = await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => item.BatchKey == batchKey)
            .OrderBy(item => item.Id)
            .Select(item => item.Status)
            .ToListAsync();
        Assert.Equal(new[] { DraftStatus.Published, DraftStatus.Draft }, packageStatuses);
    }

    [Fact]
    public async Task UpsertBatch_rolls_back_when_one_target_is_approved()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync(secondDraftLocked: false);
        var approved = await fixture.Db.TeacherDraftItems.SingleAsync(item => item.Id == model.SecondDraftId);
        approved.Status = DraftStatus.Published;
        await fixture.Db.SaveChangesAsync();
        var controller = CreateController(fixture.Db);

        var result = await controller.UpsertBatch(new TeacherDraftBatchUpsertRequest(
            new List<DraftUpsertRequest>
            {
                CreateUpsertRequest(model.FirstDraftId, model, "09:00", "10:00"),
                CreateUpsertRequest(model.SecondDraftId, model, "11:00", "12:00")
            }));

        var failure = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(409, failure.StatusCode);
        fixture.Db.ChangeTracker.Clear();
        var persisted = await fixture.Db.TeacherDraftItems
            .AsNoTracking()
            .OrderBy(item => item.Id)
            .ToListAsync();
        Assert.Equal(new TimeOnly(8, 0), persisted[0].StartTime);
        Assert.Equal(DraftStatus.Published, persisted[1].Status);
        Assert.Equal(new TimeOnly(10, 0), persisted[1].StartTime);
    }

    [Fact]
    public async Task Topic_planning_statistics_include_approved_drafts()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedAsync(secondDraftLocked: false);
        var drafts = await fixture.Db.TeacherDraftItems.OrderBy(item => item.Id).ToListAsync();
        drafts[0].ModuleTopicId = model.FirstTopicId;
        drafts[1].ModuleTopicId = model.FirstTopicId;
        drafts[1].Status = DraftStatus.Published;
        await fixture.Db.SaveChangesAsync();
        var controller = new AdminModulesController(fixture.Db);

        var result = await controller.GetTopics(model.ModuleId);

        var response = Assert.IsType<OkObjectResult>(result.Result);
        var topics = Assert.IsType<List<ModuleTopicViewDto>>(response.Value);
        var topic = topics.Single(item => item.Id == model.FirstTopicId);
        var hours = Assert.Single(topic.PlannedGroupsHours!);
        Assert.Equal(2, hours.AuditoriumHours);
        Assert.Equal(0, hours.SelfStudyHours);
    }

    private static DraftUpsertRequest CreateUpsertRequest(
        int id,
        SeedModel model,
        string start,
        string end)
        => new(
            Id: id,
            Date: Monday,
            TimeStart: start,
            TimeEnd: end,
            GroupId: model.GroupId,
            ModuleId: model.ModuleId,
            ModuleTopicId: null,
            TeacherId: null,
            RoomId: null,
            RequiresRoom: false,
            LessonTypeId: model.LessonTypeId);

    private static DraftUpsertRequest CreateLogicalEventRequest(
        SeedModel model,
        int topicId,
        int teacherId,
        string batchKey)
        => new(
            Id: null,
            Date: Monday,
            TimeStart: "13:00",
            TimeEnd: "14:00",
            GroupId: model.GroupId,
            ModuleId: model.ModuleId,
            ModuleTopicId: topicId,
            TeacherId: teacherId,
            RoomId: null,
            RequiresRoom: false,
            LessonTypeId: model.LessonTypeId,
            BatchKey: batchKey);

    private static TeacherDraftsController CreateController(AppDbContext db)
        => new(
            db,
            new RulesService(db),
            queryService: null!,
            exportService: null!,
            autogenService: null!,
            autogenJobService: null!,
            publishService: null!);

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

        public async Task<SeedModel> SeedAsync(bool secondDraftLocked)
        {
            var course = new Course { Name = "Batch course", DurationWeeks = 12 };
            var group = new Group { Name = "B-1", StudentsCount = 20, Course = course };
            var module = new Module { Code = "B1", Title = "Batch module", Credits = 1, Course = course };
            var lessonType = new LessonTypeRef
            {
                Code = "BATCH",
                Name = "Batch lesson",
                IsActive = true,
                RequiresRoom = false,
                RequiresTeacher = false,
                BlocksRoom = false,
                BlocksTeacher = false,
                CountInPlan = true,
                CountInLoad = false
            };
            var firstTeacher = new Teacher { FullName = "Перший викладач" };
            var secondTeacher = new Teacher { FullName = "Другий викладач" };
            Db.AddRange(course, group, module, lessonType, firstTeacher, secondTeacher);
            await Db.SaveChangesAsync();
            Db.TimeSlots.AddRange(Enumerable.Range(7, 7).Select(hour => new TimeSlot
            {
                CourseId = course.Id,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(hour, 0),
                End = new TimeOnly(hour + 1, 0),
                SortOrder = hour,
                IsActive = true
            }));
            await Db.SaveChangesAsync();

            var firstTopic = new ModuleTopic
            {
                ModuleId = module.Id,
                LessonTypeId = lessonType.Id,
                Order = 1,
                TopicCode = "B1.1",
                TotalHours = 1,
                AuditoriumHours = 1,
                SelfStudyHours = 0
            };
            var secondTopic = new ModuleTopic
            {
                ModuleId = module.Id,
                LessonTypeId = lessonType.Id,
                Order = 2,
                TopicCode = "B1.2",
                TotalHours = 1,
                AuditoriumHours = 1,
                SelfStudyHours = 0
            };
            Db.ModuleTopics.AddRange(firstTopic, secondTopic);
            await Db.SaveChangesAsync();

            var firstDraft = CreateDraft(group.Id, module.Id, lessonType.Id, new TimeOnly(8, 0), new TimeOnly(9, 0), false);
            var secondDraft = CreateDraft(group.Id, module.Id, lessonType.Id, new TimeOnly(10, 0), new TimeOnly(11, 0), secondDraftLocked);
            Db.TeacherDraftItems.AddRange(firstDraft, secondDraft);
            await Db.SaveChangesAsync();

            return new SeedModel(
                group.Id,
                module.Id,
                lessonType.Id,
                firstDraft.Id,
                secondDraft.Id,
                firstTeacher.Id,
                secondTeacher.Id,
                firstTopic.Id,
                secondTopic.Id);
        }

        private static TeacherDraftItem CreateDraft(
            int groupId,
            int moduleId,
            int lessonTypeId,
            TimeOnly start,
            TimeOnly end,
            bool isLocked)
            => new()
            {
                Date = Monday,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = start,
                EndTime = end,
                GroupId = groupId,
                ModuleId = moduleId,
                LessonTypeId = lessonTypeId,
                Status = DraftStatus.Draft,
                IsLocked = isLocked,
                UpdatedAt = LegacyUpdatedAt
            };

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed record SeedModel(
        int GroupId,
        int ModuleId,
        int LessonTypeId,
        int FirstDraftId,
        int SecondDraftId,
        int FirstTeacherId,
        int SecondTeacherId,
        int FirstTopicId,
        int SecondTopicId);
}
