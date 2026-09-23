using BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class LecturePrerequisitePolicyTests
{
    private static readonly DateOnly Day = new(2026, 9, 7);

    [Fact]
    public void Two_adjacent_canonical_slots_count_as_two_hours()
    {
        var result = Evaluate(
            topics: [Lecture(1, requiredHours: 3), Work(2, order: 2)],
            placements:
            [
                LecturePlacement(1, new(8, 0), new(8, 45)),
                LecturePlacement(1, new(8, 45), new(9, 30)),
                TargetPlacement(2, new(9, 30))
            ]);

        var violation = Assert.Single(result);
        Assert.Equal(2, violation.CompletedHours);
        Assert.Equal(1, violation.MissingHours);
    }

    [Fact]
    public void Duplicate_canonical_slot_counts_once()
    {
        var slot = LecturePlacement(1, new(8, 0), new(8, 45));
        var result = Evaluate(
            topics: [Lecture(1, requiredHours: 2), Work(2, order: 2)],
            placements: [slot, slot, TargetPlacement(2, new(9, 0))]);

        var violation = Assert.Single(result);
        Assert.Equal(1, violation.CompletedHours);
    }

    [Fact]
    public void Partially_overlapping_intervals_do_not_count_as_two_slots()
    {
        var result = Evaluate(
            topics: [Lecture(1, requiredHours: 2), Work(2, order: 2)],
            placements:
            [
                LecturePlacement(1, new(8, 0), new(8, 45)),
                LecturePlacement(1, new(8, 30), new(9, 15)),
                TargetPlacement(2, new(9, 15))
            ]);

        var violation = Assert.Single(result);
        Assert.Equal(1, violation.CompletedHours);
        Assert.Equal(1, violation.MissingHours);
    }

    [Fact]
    public void Later_lecture_does_not_credit_an_earlier_nonlecture_target()
    {
        var result = Evaluate(
            topics:
            [
                Lecture(1, requiredHours: 1),
                Work(2, order: 2),
                Lecture(3, order: 3, requiredHours: 1)
            ],
            placements:
            [
                LecturePlacement(3, new(8, 0), new(8, 45)),
                TargetPlacement(2, new(9, 0))
            ]);

        var violation = Assert.Single(result);
        Assert.Equal(1, violation.Prerequisite.Id);
        Assert.Equal(0, violation.CompletedHours);
    }

    [Fact]
    public void Other_group_lecture_does_not_credit_target_group()
    {
        var result = Evaluate(
            topics: [Lecture(1, requiredHours: 1), Work(2, order: 2)],
            placements:
            [
                LecturePlacement(1, new(8, 0), new(8, 45), groupId: 2),
                TargetPlacement(2, new(9, 0), groupId: 1)
            ]);

        var violation = Assert.Single(result);
        Assert.Equal(0, violation.CompletedHours);
    }

    [Fact]
    public void Nonlecture_topics_can_reorder_after_lectures_are_complete()
    {
        var result = Evaluate(
            topics:
            [
                Lecture(1, requiredHours: 1),
                Work(2, order: 2),
                Work(3, order: 3)
            ],
            placements:
            [
                LecturePlacement(1, new(8, 0), new(8, 45)),
                TargetPlacement(3, new(9, 0)),
                TargetPlacement(2, new(10, 0))
            ]);

        Assert.Empty(result);
    }

    [Fact]
    public void All_lecture_hours_before_target_are_accepted_across_days_and_history()
    {
        var result = Evaluate(
            topics:
            [
                Lecture(1, requiredHours: 1),
                Lecture(2, order: 2, requiredHours: 1),
                Work(3, order: 3)
            ],
            placements:
            [
                LecturePlacement(1, new(8, 0), new(8, 45), date: Day.AddDays(-2)),
                LecturePlacement(2, new(8, 0), new(8, 45), date: Day.AddDays(-1)),
                TargetPlacement(3, new(8, 0), date: Day)
            ]);

        Assert.Empty(result);
    }

    [Fact]
    public void Self_study_lecture_is_not_a_completed_auditorium_slot()
    {
        var result = Evaluate(
            topics: [Lecture(1, requiredHours: 1), Work(2, order: 2)],
            placements:
            [
                LecturePlacement(1, new(8, 0), new(8, 45), isSelfStudy: true),
                TargetPlacement(2, new(9, 0))
            ]);

        var violation = Assert.Single(result);
        Assert.Equal(0, violation.CompletedHours);
    }

    [Fact]
    public void Cancelled_token_stops_policy_evaluation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => Evaluate(
            topics: [Lecture(1, requiredHours: 1), Work(2, order: 2)],
            placements: [TargetPlacement(2, new(9, 0))],
            cancellationToken: cancellation.Token));
    }

    [Theory]
    [InlineData("LECTURE", "", true)]
    [InlineData("LECT", "", true)]
    [InlineData("LEC", "", true)]
    [InlineData("PRACTICE", "Лекція", true)]
    [InlineData("PRACTICE", "Lecture заняття", true)]
    [InlineData("PREFERRED", "Практика", false)]
    public void Lecture_classification_uses_code_or_name_only(
        string code,
        string name,
        bool expected)
    {
        Assert.Equal(expected, LecturePrerequisitePolicy.IsLecture(code, name));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 1)]
    [InlineData(2, 0)]
    public async Task Validator_requires_every_earlier_lecture_hour_from_academic_history(
        int completedHistoryHours,
        int expectedMissingHours)
    {
        await using var fixture = await ValidatorFixture.CreateAsync();
        for (var index = 0; index < completedHistoryHours; index++)
        {
            fixture.Db.TeacherDraftItems.Add(fixture.CreateDraft(
                fixture.LectureTopic,
                Day.AddDays(index),
                new TimeOnly(8, 0),
                new TimeOnly(9, 0)));
        }

        var practice = fixture.CreatePending(
            fixture.PracticeTopic,
            Day.AddDays(7),
            new TimeOnly(8, 0),
            new TimeOnly(9, 0));
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.ValidateAsync(
            Day.AddDays(7),
            Day.AddDays(7),
            [practice]);

        var matchingViolations = result.Violations
            .Where(message => message.Contains("потребує перед собою", StringComparison.Ordinal))
            .ToList();
        if (expectedMissingHours == 0)
        {
            Assert.Empty(matchingViolations);
        }
        else
        {
            var violation = Assert.Single(matchingViolations);
            Assert.Contains($"проведено {completedHistoryHours}", violation, StringComparison.Ordinal);
            Assert.Contains($"бракує {expectedMissingHours}", violation, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Validator_rejects_moving_a_lecture_after_a_protected_in_range_practice()
    {
        await using var fixture = await ValidatorFixture.CreateAsync();
        var oldLecture = fixture.CreateDraft(
            fixture.LectureTopic,
            Day,
            new TimeOnly(8, 0),
            new TimeOnly(9, 0));
        fixture.Db.TeacherDraftItems.Add(oldLecture);
        fixture.Db.ScheduleItems.Add(fixture.CreateScheduleItem(
            fixture.PracticeTopic,
            Day.AddDays(7),
            new TimeOnly(8, 0),
            new TimeOnly(9, 0)));
        await fixture.Db.SaveChangesAsync();

        var movedLecture = fixture.CreatePending(
            fixture.LectureTopic,
            Day.AddDays(7),
            new TimeOnly(9, 0),
            new TimeOnly(10, 0));
        var result = await fixture.ValidateAsync(
            Day.AddDays(7),
            Day.AddDays(7),
            [movedLecture],
            [oldLecture.Id]);

        Assert.Contains(result.Violations, message =>
            message.Contains("потребує перед собою", StringComparison.Ordinal)
            && message.Contains(Day.AddDays(7).ToString("yyyy-MM-dd"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validator_checks_future_practice_when_a_lecture_changes_in_the_selected_range()
    {
        await using var fixture = await ValidatorFixture.CreateAsync();
        var oldLecture = fixture.CreateDraft(
            fixture.LectureTopic,
            Day,
            new TimeOnly(8, 0),
            new TimeOnly(9, 0));
        fixture.Db.TeacherDraftItems.Add(oldLecture);
        fixture.Db.ScheduleItems.Add(fixture.CreateScheduleItem(
            fixture.PracticeTopic,
            Day.AddDays(14),
            new TimeOnly(8, 0),
            new TimeOnly(9, 0)));
        await fixture.Db.SaveChangesAsync();

        var movedLecture = fixture.CreatePending(
            fixture.LectureTopic,
            Day.AddDays(7),
            new TimeOnly(8, 0),
            new TimeOnly(9, 0));
        var result = await fixture.ValidateAsync(
            Day.AddDays(7),
            Day.AddDays(7),
            [movedLecture],
            [oldLecture.Id]);

        Assert.Contains(result.Violations, message =>
            message.Contains("потребує перед собою", StringComparison.Ordinal)
            && message.Contains(Day.AddDays(14).ToString("yyyy-MM-dd"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validator_rejects_deleting_a_lecture_needed_by_future_practice()
    {
        await using var fixture = await ValidatorFixture.CreateAsync();
        var removedLecture = fixture.CreateDraft(
            fixture.LectureTopic,
            Day.AddDays(7),
            new TimeOnly(8, 0),
            new TimeOnly(9, 0));
        fixture.Db.TeacherDraftItems.Add(removedLecture);
        fixture.Db.ScheduleItems.Add(fixture.CreateScheduleItem(
            fixture.PracticeTopic,
            Day.AddDays(14),
            new TimeOnly(8, 0),
            new TimeOnly(9, 0)));
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.ValidateAsync(
            Day.AddDays(7),
            Day.AddDays(7),
            Array.Empty<TeacherDraftsAutogenPendingDraft>(),
            [removedLecture.Id]);

        Assert.Contains(result.Violations, message =>
            message.Contains("потребує перед собою", StringComparison.Ordinal)
            && message.Contains(Day.AddDays(14).ToString("yyyy-MM-dd"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validator_keeps_nonlecture_topic_reordering_after_prerequisite_is_complete()
    {
        await using var fixture = await ValidatorFixture.CreateAsync();
        fixture.Db.TeacherDraftItems.AddRange(
            fixture.CreateDraft(fixture.LectureTopic, Day, new TimeOnly(8, 0), new TimeOnly(9, 0)),
            fixture.CreateDraft(fixture.LectureTopic, Day.AddDays(1), new TimeOnly(8, 0), new TimeOnly(9, 0)));
        var laterTopicFirst = fixture.CreatePending(
            fixture.LaterPracticeTopic,
            Day.AddDays(7),
            new TimeOnly(8, 0),
            new TimeOnly(9, 0));
        var earlierTopicSecond = fixture.CreatePending(
            fixture.PracticeTopic,
            Day.AddDays(7),
            new TimeOnly(9, 0),
            new TimeOnly(10, 0));
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.ValidateAsync(
            Day.AddDays(7),
            Day.AddDays(7),
            [laterTopicFirst, earlierTopicSecond]);

        Assert.DoesNotContain(result.Violations, message =>
            message.Contains("потребує перед собою", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validator_does_not_share_lecture_hours_between_groups()
    {
        await using var fixture = await ValidatorFixture.CreateAsync();
        fixture.Db.TeacherDraftItems.AddRange(
            fixture.CreateDraft(fixture.LectureTopic, Day, new TimeOnly(8, 0), new TimeOnly(9, 0)),
            fixture.CreateDraft(fixture.LectureTopic, Day.AddDays(1), new TimeOnly(8, 0), new TimeOnly(9, 0)));
        var otherGroup = new Group
        {
            Name = "Інша група",
            StudentsCount = 18,
            Course = fixture.Course
        };
        fixture.Db.Groups.Add(otherGroup);
        await fixture.Db.SaveChangesAsync();
        var practice = fixture.CreatePending(
            fixture.PracticeTopic,
            Day.AddDays(7),
            new TimeOnly(8, 0),
            new TimeOnly(9, 0),
            otherGroup.Id);

        var result = await fixture.ValidateAsync(
            Day.AddDays(7),
            Day.AddDays(7),
            [practice]);

        Assert.Contains(result.Violations, message =>
            message.Contains("потребує перед собою", StringComparison.Ordinal)
            && message.Contains($"група #{otherGroup.Id}", StringComparison.Ordinal));
    }

    private static IReadOnlyList<LecturePrerequisitePolicy.Violation> Evaluate(
        IEnumerable<LecturePrerequisitePolicy.Topic> topics,
        IEnumerable<LecturePrerequisitePolicy.Placement> placements,
        CancellationToken cancellationToken = default)
        => LecturePrerequisitePolicy.FindViolations(topics, placements, cancellationToken);

    private static LecturePrerequisitePolicy.Topic Lecture(
        int id,
        int order = 1,
        int requiredHours = 1)
        => new(id, ModuleId, order, IsLecture: true, RequiredHours: requiredHours);

    private static LecturePrerequisitePolicy.Topic Work(int id, int order)
        => new(id, ModuleId, order, IsLecture: false, RequiredHours: 1);

    private static LecturePrerequisitePolicy.Placement LecturePlacement(
        int topicId,
        TimeOnly start,
        TimeOnly end,
        int groupId = 1,
        DateOnly? date = null,
        bool isSelfStudy = false)
        => new(groupId, ModuleId, topicId, date ?? Day, start, end, isSelfStudy);

    private static LecturePrerequisitePolicy.Placement TargetPlacement(
        int topicId,
        TimeOnly start,
        int groupId = 1,
        DateOnly? date = null)
        => new(groupId, ModuleId, topicId, date ?? Day, start, start.AddHours(1), IsSelfStudy: false);

    private const int ModuleId = 10;

    private sealed class ValidatorFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private ValidatorFixture(SqliteConnection connection, AppDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public AppDbContext Db { get; }
        public Course Course { get; private init; } = default!;
        public Group Group { get; private init; } = default!;
        public Module Module { get; private init; } = default!;
        public LessonTypeRef LectureType { get; private init; } = default!;
        public LessonTypeRef PracticeType { get; private init; } = default!;
        public ModuleTopic LectureTopic { get; private init; } = default!;
        public ModuleTopic PracticeTopic { get; private init; } = default!;
        public ModuleTopic LaterPracticeTopic { get; private init; } = default!;

        public static async Task<ValidatorFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();

            var course = new Course
            {
                Name = "Курс тесту лекцій",
                DurationWeeks = 20,
                AcademicPeriodStartDate = Day
            };
            var group = new Group { Name = "Група тесту", StudentsCount = 20, Course = course };
            var module = new Module { Code = "LECT-TEST", Title = "Модуль тесту", Credits = 1, Course = course };
            var lectureType = new LessonTypeRef
            {
                Code = "LECTURE",
                Name = "Лекція",
                RequiresRoom = false,
                RequiresTeacher = false,
                BlocksRoom = false,
                BlocksTeacher = false
            };
            var practiceType = new LessonTypeRef
            {
                Code = "PRACTICE",
                Name = "Практичне заняття",
                RequiresRoom = false,
                RequiresTeacher = false,
                BlocksRoom = false,
                BlocksTeacher = false
            };
            var lectureTopic = new ModuleTopic
            {
                Module = module,
                Order = 1,
                TopicCode = "L1",
                LessonType = lectureType,
                AuditoriumHours = 2,
                TotalHours = 2
            };
            var practiceTopic = new ModuleTopic
            {
                Module = module,
                Order = 2,
                TopicCode = "P1",
                LessonType = practiceType,
                AuditoriumHours = 1,
                TotalHours = 1
            };
            var laterPracticeTopic = new ModuleTopic
            {
                Module = module,
                Order = 3,
                TopicCode = "P2",
                LessonType = practiceType,
                AuditoriumHours = 1,
                TotalHours = 1
            };
            db.AddRange(course, group, module, lectureType, practiceType, lectureTopic, practiceTopic, laterPracticeTopic);
            foreach (var dayOfWeek in new[]
                     {
                         DayOfWeek.Monday,
                         DayOfWeek.Tuesday,
                         DayOfWeek.Wednesday,
                         DayOfWeek.Thursday,
                         DayOfWeek.Friday
                     })
            {
                db.TimeSlots.Add(new TimeSlot
                {
                    Course = course,
                    DayOfWeek = dayOfWeek,
                    Start = new TimeOnly(8, 0),
                    End = new TimeOnly(9, 0),
                    SortOrder = 1,
                    IsActive = true
                });
                db.TimeSlots.Add(new TimeSlot
                {
                    Course = course,
                    DayOfWeek = dayOfWeek,
                    Start = new TimeOnly(9, 0),
                    End = new TimeOnly(10, 0),
                    SortOrder = 2,
                    IsActive = true
                });
            }

            await db.SaveChangesAsync();
            return new ValidatorFixture(connection, db)
            {
                Course = course,
                Group = group,
                Module = module,
                LectureType = lectureType,
                PracticeType = practiceType,
                LectureTopic = lectureTopic,
                PracticeTopic = practiceTopic,
                LaterPracticeTopic = laterPracticeTopic
            };
        }

        public TeacherDraftItem CreateDraft(
            ModuleTopic topic,
            DateOnly date,
            TimeOnly start,
            TimeOnly end,
            bool isSelfStudy = false)
            => new()
            {
                Date = date,
                DayOfWeek = date.DayOfWeek,
                StartTime = start,
                EndTime = end,
                LessonType = topic.LessonType,
                Group = Group,
                Module = Module,
                ModuleTopic = topic,
                Status = DraftStatus.Draft,
                IsLocked = true,
                IsSelfStudy = isSelfStudy
            };

        public ScheduleItem CreateScheduleItem(
            ModuleTopic topic,
            DateOnly date,
            TimeOnly start,
            TimeOnly end)
            => new()
            {
                Date = date,
                DayOfWeek = date.DayOfWeek,
                StartTime = start,
                EndTime = end,
                LessonType = topic.LessonType,
                Group = Group,
                Module = Module,
                ModuleTopic = topic
            };

        public TeacherDraftsAutogenPendingDraft CreatePending(
            ModuleTopic topic,
            DateOnly date,
            TimeOnly start,
            TimeOnly end,
            int? groupId = null)
            => new(
                date,
                start,
                end,
                groupId ?? Group.Id,
                Module.Id,
                topic.LessonTypeId,
                topic.Id,
                TeacherId: null,
                RoomId: null,
                IsSelfStudy: false);

        public Task<TeacherDraftsAutogenHardRuleValidationResult> ValidateAsync(
            DateOnly from,
            DateOnly to,
            IReadOnlyCollection<TeacherDraftsAutogenPendingDraft> pending,
            IReadOnlyCollection<int>? excludedDraftIds = null)
            => new TeacherDraftsAutogenHardRuleValidator(Db).ValidateAsync(
                new TeacherDraftsAutogenHardRuleValidationRequest(
                    Course.Id,
                    pending.Select(item => item.GroupId).Append(Group.Id).Distinct().ToArray(),
                    from,
                    to,
                    WeekPreset.MonFri,
                    AllowIncompleteDrafts: true,
                    PendingDrafts: pending,
                    ExcludedDraftIds: excludedDraftIds));

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
