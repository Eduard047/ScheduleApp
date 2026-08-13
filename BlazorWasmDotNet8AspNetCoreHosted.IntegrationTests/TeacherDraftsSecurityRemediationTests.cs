using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class TeacherDraftsSecurityRemediationTests
{
    private static readonly DateOnly Monday = new(2026, 5, 4);

    [Fact]
    public async Task PublishWeek_requires_nonempty_scope_revision_before_any_mutation_and_accepts_current_revision()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.SeedDraftModelAsync(groupCount: 1, draftCount: 1);
        var controller = CreateController(fixture.Db);

        foreach (var missingRevision in new Guid?[] { null, Guid.Empty })
        {
            var rejected = await controller.PublishWeek(new PublishWeekRequest(Monday, null, missingRevision));
            var response = Assert.IsType<ObjectResult>(rejected.Result);
            Assert.Equal(428, response.StatusCode);
            var problem = Assert.IsType<ProblemDetails>(response.Value);
            Assert.Contains("перевір", problem.Title, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, await fixture.Db.TeacherDraftItems.CountAsync());
            Assert.Equal(0, await fixture.Db.ScheduleItems.CountAsync());
        }

        var directServiceRejection = await CreatePublishService(fixture.Db)
            .PublishWeekAsync(new PublishWeekRequest(Monday, null));
        var directResponse = Assert.IsType<ObjectResult>(directServiceRejection.Result);
        Assert.Equal(428, directResponse.StatusCode);
        Assert.Equal(1, await fixture.Db.TeacherDraftItems.CountAsync());
        Assert.Equal(0, await fixture.Db.ScheduleItems.CountAsync());

        var currentRevision = await ReadScopeRevisionAsync(fixture.Db);
        var published = await controller.PublishWeek(new PublishWeekRequest(Monday, null, currentRevision));
        var ok = Assert.IsType<OkObjectResult>(published.Result);
        var payload = Assert.IsType<PublishWeekResults>(ok.Value);
        Assert.Equal(1, payload.Created);
        Assert.Empty(payload.Warnings);
        Assert.Equal(0, await fixture.Db.TeacherDraftItems.CountAsync());
        Assert.Equal(1, await fixture.Db.ScheduleItems.CountAsync());
    }

    [Fact]
    public async Task PublishWeek_keeps_stale_scope_revision_rejection_atomic()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.SeedDraftModelAsync(groupCount: 1, draftCount: 1);
        var staleRevision = await ReadScopeRevisionAsync(fixture.Db);
        var draft = await fixture.Db.TeacherDraftItems.SingleAsync();
        draft.Revision = Guid.NewGuid();
        await fixture.Db.SaveChangesAsync();

        var result = await CreateController(fixture.Db)
            .PublishWeek(new PublishWeekRequest(Monday, null, staleRevision));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<PublishWeekResults>(ok.Value);
        Assert.Equal(0, payload.Created);
        Assert.Contains(payload.Warnings, warning =>
            warning.Contains("змінилися", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, await fixture.Db.TeacherDraftItems.CountAsync());
        Assert.Equal(0, await fixture.Db.ScheduleItems.CountAsync());
    }

    [Fact]
    public async Task Durable_job_cleanup_bounds_only_unreferenced_terminal_history()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedDraftModelAsync(groupCount: 1, draftCount: 1);
        var now = new DateTime(2026, 8, 13, 9, 0, 0, DateTimeKind.Utc);
        var active = CreateRun("active", AutoGenJobState.Running, now.AddDays(-90));
        var planRun = CreateRun("plan", AutoGenJobState.Succeeded, now.AddDays(-90));
        var draftRun = CreateRun("draft", AutoGenJobState.Failed, now.AddDays(-90));
        var stale = CreateRun("stale", AutoGenJobState.Canceled, now.AddDays(-46));
        var recentRuns = Enumerable.Range(0, 205)
            .Select(index => CreateRun(
                $"recent-{index:D3}",
                AutoGenJobState.Succeeded,
                now.AddMinutes(-index)))
            .ToList();
        fixture.Db.AutoGenJobRuns.AddRange(new[] { active, planRun, draftRun, stale }.Concat(recentRuns));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.AutoGenDraftPlans.Add(new AutoGenDraftPlan
        {
            PlanId = planRun.JobId,
            AutoGenJobRunId = planRun.Id,
            State = (int)AutoGenPlanState.Ready,
            Version = 1,
            CourseId = model.CourseId,
            RangeStartDate = Monday,
            RangeEndDate = Monday,
            Days = (int)WeekPreset.MonFri,
            AllowIncompleteDrafts = false,
            GroupIdsJson = $"[{model.GroupIds[0]}]",
            BeforeScopeRevision = Guid.NewGuid(),
            InputFingerprint = new string('a', 64),
            CreatedAtUtc = now.AddDays(-1),
            ExpiresAtUtc = now.AddDays(1)
        });
        var referencedDraft = await fixture.Db.TeacherDraftItems.SingleAsync();
        referencedDraft.GenerationJobId = draftRun.JobId;
        await fixture.Db.SaveChangesAsync();

        var deleted = TeacherDraftsAutogenJobService.CleanupPersistedTerminalJobs(fixture.Db, now);

        Assert.Equal(7, deleted);
        var remaining = await fixture.Db.AutoGenJobRuns
            .AsNoTracking()
            .Select(run => run.JobId)
            .ToListAsync();
        Assert.Contains(active.JobId, remaining);
        Assert.Contains(planRun.JobId, remaining);
        Assert.DoesNotContain(draftRun.JobId, remaining);
        Assert.Contains("recent-000", remaining);
        Assert.DoesNotContain(stale.JobId, remaining);
        Assert.DoesNotContain("recent-204", remaining);
        Assert.Equal(202, remaining.Count);
        Assert.Equal(1, await fixture.Db.AutoGenDraftPlans.CountAsync());
        var retainedDraft = await fixture.Db.TeacherDraftItems.SingleAsync();
        Assert.Equal(draftRun.JobId, retainedDraft.GenerationJobId);
    }

    [Fact]
    public async Task Durable_job_cleanup_deletes_at_most_one_bounded_batch_from_large_history()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var now = new DateTime(2026, 8, 13, 9, 0, 0, DateTimeKind.Utc);
        var staleRuns = Enumerable.Range(0, 1_500)
            .Select(index => CreateRun(
                $"stale-large-{index:D4}",
                AutoGenJobState.Succeeded,
                now.AddDays(-60).AddMinutes(index)))
            .ToList();
        fixture.Db.AutoGenJobRuns.AddRange(staleRuns);
        await fixture.Db.SaveChangesAsync();

        var deleted = TeacherDraftsAutogenJobService.CleanupPersistedTerminalJobs(fixture.Db, now);

        Assert.Equal(200, deleted);
        Assert.Equal(1_300, await fixture.Db.AutoGenJobRuns.CountAsync());
    }

    [Fact]
    public async Task Autogen_start_runs_durable_cleanup_before_persisting_new_job()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var model = await fixture.SeedDraftModelAsync(groupCount: 1, draftCount: 0);
        fixture.Db.AutoGenJobRuns.Add(CreateRun(
            "stale-before-start",
            AutoGenJobState.Succeeded,
            DateTime.UtcNow.AddDays(-60)));
        await fixture.Db.SaveChangesAsync();
        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(fixture.Options));
        await using var provider = services.BuildServiceProvider();
        var service = new TeacherDraftsAutogenJobService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TeacherDraftsAutogenJobService>.Instance);
        var request = CreateValidJobRequest() with
        {
            CourseId = model.CourseId,
            GroupIds = new List<int> { model.GroupIds[0] },
            ClientJobId = Guid.NewGuid().ToString("N")
        };

        var started = service.Start(request);
        await service.StopAsync(CancellationToken.None);

        await using var verification = new AppDbContext(fixture.Options);
        Assert.False(await verification.AutoGenJobRuns.AnyAsync(run => run.JobId == "stale-before-start"));
        Assert.True(await verification.AutoGenJobRuns.AnyAsync(run => run.JobId == started.JobId));
    }

    [Fact]
    public void Autogen_composite_budgets_accept_boundaries_and_reject_multiplicative_maxima_before_persistence()
    {
        var fullRangeBoundary = CreateValidJobRequest() with
        {
            ToDate = new DateOnly(2026, 1, 1).AddDays(369),
            GroupIds = Enumerable.Range(1, 32).ToList()
        };
        AssertReachesPersistence(fullRangeBoundary);
        var requestedHoursBoundary = CreateValidJobRequest() with
        {
            GroupIds = Enumerable.Range(1, 200).ToList(),
            ModuleHours = new Dictionary<int, int> { [1] = 500 }
        };
        AssertReachesPersistence(requestedHoursBoundary);

        var scopeFactory = new CountingThrowingScopeFactory();
        var service = new TeacherDraftsAutogenJobService(
            scopeFactory,
            NullLogger<TeacherDraftsAutogenJobService>.Instance);
        var excessiveGroupDays = CreateValidJobRequest() with
        {
            ToDate = new DateOnly(2026, 1, 1).AddDays(369),
            GroupIds = Enumerable.Range(1, 200).ToList()
        };
        var dayException = Assert.Throws<AutoGenJobValidationException>(() => service.Start(excessiveGroupDays));
        Assert.Contains("днів і груп", dayException.Message, StringComparison.OrdinalIgnoreCase);

        var excessiveGroupHours = CreateValidJobRequest() with
        {
            GroupIds = Enumerable.Range(1, 200).ToList(),
            ModuleHours = Enumerable.Range(1, 200).ToDictionary(moduleId => moduleId, _ => 500)
        };
        var hoursException = Assert.Throws<AutoGenJobValidationException>(() => service.Start(excessiveGroupHours));
        Assert.Contains("груп і сумарних годин", hoursException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, scopeFactory.CreateScopeCalls);
    }

    [Fact]
    public async Task Export_rejects_excessive_draft_rows_with_413_problem_details()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.SeedDraftModelAsync(
            groupCount: 1,
            draftCount: TeacherDraftsExportService.MaxDraftRowCount + 1);

        var result = await CreateController(fixture.Db)
            .Export(Monday, null, null, null);

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(413, response.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(response.Value);
        Assert.Contains("рядків", problem.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Export_rejects_excessive_group_slot_matrix_with_422_problem_details()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.SeedDraftModelAsync(groupCount: 100, draftCount: 100);
        fixture.Db.TimeSlots.AddRange(Enumerable.Range(0, 72).Select(index => new TimeSlot
        {
            CourseId = null,
            DayOfWeek = null,
            Start = new TimeOnly(0, 0).AddMinutes(index),
            End = new TimeOnly(0, 0).AddMinutes(index + 1),
            SortOrder = index + 1,
            IsActive = true
        }));
        await fixture.Db.SaveChangesAsync();

        var result = await CreateController(fixture.Db)
            .Export(Monday, null, null, null);

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(422, response.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(response.Value);
        Assert.Contains("комірок", problem.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Export_streams_normal_workbook_and_honors_cancellation()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.SeedDraftModelAsync(groupCount: 1, draftCount: 1);
        var service = new TeacherDraftsExportService(
            fixture.Db,
            new TeacherDraftsQueryService(fixture.Db));

        var file = await service.ExportAsync(Monday, null, null, null);

        Assert.Equal("Rozklad-20260504.xlsx", file.FileDownloadName);
        Assert.True(file.FileStream.CanRead);
        using (var workbook = new XLWorkbook(file.FileStream))
        {
            var worksheet = workbook.Worksheet("Розклад");
            Assert.Equal("РОЗКЛАД навчальних занять", worksheet.Cell(1, 1).GetString());
            Assert.Equal("День тижня", worksheet.Cell(4, 1).GetString());
            Assert.Equal("Година", worksheet.Cell(4, 2).GetString());
            Assert.Equal("Б-001", worksheet.Cell(4, 3).GetString());
            Assert.Contains("Модуль БЕЗ", worksheet.Cell(5, 3).GetString(), StringComparison.Ordinal);
            Assert.Contains("Самостійне заняття", worksheet.Cell(5, 3).GetString(), StringComparison.Ordinal);
        }
        await file.FileStream.DisposeAsync();

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ExportAsync(Monday, null, null, null, cancellation.Token));
    }

    private static void AssertReachesPersistence(AutoGenJobRequest request)
    {
        var scopeFactory = new CountingThrowingScopeFactory();
        var service = new TeacherDraftsAutogenJobService(
            scopeFactory,
            NullLogger<TeacherDraftsAutogenJobService>.Instance);

        Assert.Throws<AutoGenJobPersistenceException>(() => service.Start(request));
        Assert.Equal(1, scopeFactory.CreateScopeCalls);
    }

    private static AutoGenJobRequest CreateValidJobRequest()
        => new(
            AutoGenJobKind.Generate,
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 1),
            1,
            new List<int> { 1 },
            new Dictionary<int, int> { [1] = 1 },
            WeekPreset.MonFri,
            ClearExisting: true,
            SoftFill: false,
            PreflightOnly: false,
            PreviewOnly: true);

    private static AutoGenJobRun CreateRun(string jobId, AutoGenJobState state, DateTime timestamp)
        => new()
        {
            JobId = jobId,
            RequestHash = new string('a', 64),
            OwnerInstanceId = state is AutoGenJobState.Queued or AutoGenJobState.Running ? "owner" : null,
            Attempt = state is AutoGenJobState.Queued or AutoGenJobState.Running ? 1 : 0,
            LeaseExpiresAtUtc = state is AutoGenJobState.Queued or AutoGenJobState.Running
                ? timestamp.AddMinutes(5)
                : null,
            Version = 1,
            Kind = (int)AutoGenJobKind.Generate,
            State = (int)state,
            Title = $"Завдання {jobId}",
            CurrentStage = "Завершено",
            CreatedAtUtc = timestamp,
            StartedAtUtc = timestamp,
            CompletedAtUtc = state is AutoGenJobState.Queued or AutoGenJobState.Running ? null : timestamp,
            RangeStartDate = Monday,
            RangeEndDate = Monday,
            TotalWeeks = 1,
            RequestJson = "{}",
            StatusJson = "{}",
            UpdatedAtUtc = timestamp
        };

    private static async Task<Guid> ReadScopeRevisionAsync(AppDbContext db)
    {
        var revisions = await db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => item.Date >= Monday && item.Date < Monday.AddDays(7))
            .Select(item => new KeyValuePair<int, Guid>(item.Id, item.Revision))
            .ToListAsync();
        return LogicalRevisionToken.Combine(revisions);
    }

    private static TeacherDraftsController CreateController(AppDbContext db)
    {
        var rules = new RulesService(db);
        var query = new TeacherDraftsQueryService(db);
        return new TeacherDraftsController(
            db,
            rules,
            query,
            new TeacherDraftsExportService(db, query),
            null!,
            null!,
            CreatePublishService(db));
    }

    private static TeacherDraftsPublishService CreatePublishService(AppDbContext db)
        => new(db, new RulesService(db), new AggregatesService(db));

    private sealed class CountingThrowingScopeFactory : IServiceScopeFactory
    {
        public int CreateScopeCalls { get; private set; }

        public IServiceScope CreateScope()
        {
            CreateScopeCalls++;
            throw new InvalidOperationException("Сховище не повинно викликатися для відхиленого запиту.");
        }
    }

    private sealed record SeedModel(int CourseId, IReadOnlyList<int> GroupIds);

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
            var connectionString = $"Data Source=security-remediation-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connectionString)
                .Options;
            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, options, db);
        }

        public async Task<SeedModel> SeedDraftModelAsync(int groupCount, int draftCount)
        {
            var course = new Course
            {
                Name = "Курс перевірки безпеки",
                DurationWeeks = 52,
                AcademicPeriodStartDate = new DateOnly(2026, 1, 1)
            };
            var groups = Enumerable.Range(1, groupCount)
                .Select(index => new Group
                {
                    Name = $"Б-{index:D3}",
                    StudentsCount = 20,
                    Course = course
                })
                .ToList();
            var module = new Module
            {
                Code = "БЕЗ",
                Title = "Модуль перевірки безпеки",
                Credits = 1,
                Course = course
            };
            var lessonType = new LessonTypeRef
            {
                Code = "INDEPENDENT",
                Name = "Самостійне заняття",
                RequiresRoom = false,
                RequiresTeacher = false,
                BlocksRoom = false,
                BlocksTeacher = false
            };
            Db.AddRange(course, module, lessonType);
            Db.Groups.AddRange(groups);
            await Db.SaveChangesAsync();
            Db.TimeSlots.Add(new TimeSlot
            {
                CourseId = course.Id,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(8, 0),
                End = new TimeOnly(9, 0),
                SortOrder = 1,
                IsActive = true
            });
            var drafts = Enumerable.Range(0, draftCount)
                .Select(index => new TeacherDraftItem
                {
                    Date = Monday,
                    DayOfWeek = DayOfWeek.Monday,
                    StartTime = new TimeOnly(8, 0),
                    EndTime = new TimeOnly(9, 0),
                    GroupId = groups[index % groups.Count].Id,
                    ModuleId = module.Id,
                    LessonTypeId = lessonType.Id,
                    Status = DraftStatus.Published
                });
            Db.TeacherDraftItems.AddRange(drafts);
            await Db.SaveChangesAsync();
            return new SeedModel(course.Id, groups.Select(group => group.Id).ToList());
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}

internal static class PublishTestScopeRevision
{
    public static Guid Read(AppDbContext db, DateOnly weekStart)
    {
        var weekEnd = weekStart.AddDays(7);
        var revisions = db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => item.Date >= weekStart && item.Date < weekEnd)
            .Select(item => new { item.Id, item.Revision })
            .ToList()
            .Select(item => new KeyValuePair<int, Guid>(item.Id, item.Revision));
        return LogicalRevisionToken.Combine(revisions);
    }
}
