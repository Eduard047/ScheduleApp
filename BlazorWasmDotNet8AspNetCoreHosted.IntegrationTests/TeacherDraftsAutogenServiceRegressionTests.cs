using BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;
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

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
