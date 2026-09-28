using BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

[Collection("Autogen performance")]
[Trait("Category", "AutogenConstrained")]
public sealed class AutogenConstrainedScenarioTests
{
    public static IEnumerable<object[]> TinyResourceMasks()
    {
        // Усі комбінації вікон трьох викладачів, кожен має щонайменше два слоти.
        foreach (var a in new[] { 3, 5, 6, 7 })
        foreach (var b in new[] { 3, 5, 6, 7 })
        foreach (var c in new[] { 3, 5, 6, 7 })
            yield return new object[] { a, b, c };
    }

    [Theory]
    [MemberData(nameof(TinyResourceMasks))]
    public async Task Tiny_shared_resources_match_independent_exhaustive_capacity(int a, int b, int c)
    {
        var masks = new[] { a, b, c };
        var (optimum, assignments) = FindMaximum(masks);
        await using var fixture = await Scenario.CreateAsync(2, 1, masks, false, false);
        var witness = fixture.TinyWitness(assignments);
        fixture.Db.TeacherDraftItems.AddRange(witness);
        await fixture.Db.SaveChangesAsync();
        await fixture.AssertValidAsync();
        fixture.Db.TeacherDraftItems.RemoveRange(witness);
        await fixture.Db.SaveChangesAsync();
        var result = await fixture.GenerateAsync();
        await fixture.AssertValidAsync();
        var coverage = Assert.IsType<AutoGenCoverageDto>(result.Coverage);
        Assert.Equal(0, coverage.OverplannedLessons);
        Assert.Equal(0, coverage.IncompleteLessons);
        Assert.True(coverage.OccupiedSlots <= optimum,
            $"Маски {a}/{b}/{c}: генерація перевищила незалежний максимум {optimum}.");
        if (optimum == 6)
        {
            var rows = await fixture.Db.TeacherDraftItems.AsNoTracking().OrderBy(row => row.GroupId).ThenBy(row => row.StartTime).ToListAsync();
            Assert.True(coverage.MissingLessons == 0 && coverage.EmptySlots == 0,
                $"Маски {a}/{b}/{c}: існує повне розміщення, отримано {coverage.OccupiedSlots}/6; " +
                $"пропуски={coverage.MissingLessons}; ліміт={coverage.SearchLimitReached}. " +
                string.Join("; ", rows.Select(row => $"g{row.GroupId} m{row.ModuleId} {row.StartTime}")) + " | " +
                string.Join(" | ", result.Warnings.Take(8)));
            Assert.Equal(0, coverage.InternalGapSlots);
        }
        else
        {
            Assert.True(coverage.MissingLessons >= 6 - optimum);
            Assert.NotEqual(AutoGenCoverageStatus.FullyOccupied, coverage.Status);
        }
    }

    public static IEnumerable<object[]> DenseProfiles()
    {
        foreach (var groups in new[] { 2, 4, 6, 7, 8 })
        foreach (var days in new[] { 1, 5, 6 })
        foreach (var locked in new[] { false, true })
            yield return new object[] { groups, days, locked, false };
        foreach (var groups in new[] { 4, 6, 7, 8 })
        foreach (var days in new[] { 5, 6 })
            yield return new object[] { groups, days, true, true };
    }

    [Theory]
    [MemberData(nameof(DenseProfiles))]
    public async Task Planted_dense_resource_contention_fills_every_slot(int groups, int days, bool locked, bool lectures)
    {
        await using var fixture = await Scenario.CreateAsync(groups, days, [7, 7, 7], true, locked, lectures);
        // Перевіряємо свідка існування розкладу до запуску генератора.
        var witness = fixture.Witness();
        fixture.Db.TeacherDraftItems.AddRange(witness);
        await fixture.Db.SaveChangesAsync();
        await fixture.AssertValidAsync();
        fixture.Db.TeacherDraftItems.RemoveRange(witness.Where(row => !row.IsLocked));
        await fixture.Db.SaveChangesAsync();
        var lockedBefore = await fixture.Db.TeacherDraftItems.AsNoTracking()
            .Where(row => row.IsLocked).Select(row => new { row.Id, row.Date, row.StartTime, row.ModuleId, row.TeacherId, row.RoomId })
            .OrderBy(row => row.Id).ToListAsync();

        var result = await fixture.GenerateAsync();
        var coverage = Assert.IsType<AutoGenCoverageDto>(result.Coverage);
        Assert.True(coverage.OccupiedSlots == groups * (days + (lectures ? 1 : 0)) * 3 && coverage.MissingLessons == 0,
            $"Груп={groups}, днів={days}, locked={locked}, lectures={lectures}: {coverage.OccupiedSlots}; " +
            $"пропуски={coverage.MissingLessons}; ліміт={coverage.SearchLimitReached}.");
        Assert.Equal(0, coverage.EmptySlots);
        Assert.Equal(0, coverage.InternalGapSlots);
        Assert.Equal(0, coverage.OverplannedLessons);
        Assert.Equal(0, coverage.IncompleteLessons);
        await fixture.AssertValidAsync();
        var lockedAfter = await fixture.Db.TeacherDraftItems.AsNoTracking()
            .Where(row => row.IsLocked).Select(row => new { row.Id, row.Date, row.StartTime, row.ModuleId, row.TeacherId, row.RoomId })
            .OrderBy(row => row.Id).ToListAsync();
        Assert.Equal(lockedBefore, lockedAfter);
        var refill = await fixture.GenerateAsync();
        Assert.Equal(0, refill.Created);
        Assert.Equal(0, refill.Coverage!.MissingLessons);
    }

    public static IEnumerable<object[]> UniversityWeekVariants()
    {
        foreach (var groups in new[] { 6, 7, 8 })
        foreach (var variant in new[] { "shift", "holiday", "teacher-absent", "room-closed", "hours" })
            yield return new object[] { groups, variant };
    }

    [Theory]
    [MemberData(nameof(UniversityWeekVariants))]
    public async Task University_week_variants_preserve_capacity_and_hard_rules(int groups, string variant)
    {
        var start = new DateOnly(2030, 2, variant == "shift" ? 5 : 4);
        await using var fixture = await Scenario.CreateAsync(groups, 5, [7, 7, 7], true, false, startDate: start, extraTeachers: variant == "hours" ? 1 : 0);
        var hours = 5;
        if (variant == "holiday")
        {
            fixture.Db.CalendarExceptions.Add(new CalendarException
                { CourseId = 1, Date = start.AddDays(2), IsWorkingDay = false, Name = "Навчальний вихідний" });
            hours = 4;
            fixture.HoursOverride = new() { [1] = hours, [2] = hours, [3] = hours };
        }
        if (variant == "teacher-absent")
        {
            // Відсутність у всьому діапазоні: зв'язок із модулем прибрано лише у тестовій БД.
            fixture.Db.TeacherModules.RemoveRange(await fixture.Db.TeacherModules.Where(row => row.TeacherId == 100).ToListAsync());
        }
        if (variant == "room-closed")
            fixture.Db.ModuleRooms.RemoveRange(await fixture.Db.ModuleRooms.Where(row => row.RoomId == 1).ToListAsync());
        if (variant == "hours")
            fixture.HoursOverride = new() { [1] = 6, [2] = 4, [3] = 5 };
        if (variant == "hours")
            {
            var topic = await fixture.Db.ModuleTopics.SingleAsync(row => row.Id == 1);
            topic.AuditoriumHours = topic.TotalHours = 6;
        }
        await fixture.Db.SaveChangesAsync();
        var result = await fixture.GenerateAsync();
        await fixture.AssertValidAsync();
        var coverage = Assert.IsType<AutoGenCoverageDto>(result.Coverage);
        Assert.Equal(0, coverage.OverplannedLessons);
        Assert.Equal(0, coverage.IncompleteLessons);
        var rows = await fixture.Db.TeacherDraftItems.AsNoTracking().ToListAsync();
        Assert.All(rows, row => Assert.InRange(row.Date, start, start.AddDays(4)));
        if (variant == "holiday") Assert.DoesNotContain(rows, row => row.Date == start.AddDays(2));
        if (variant == "teacher-absent" || variant == "room-closed")
        {
            // Незалежна верхня межа: викладачі модуля або аудиторії × 15 слотів.
            var upperBound = variant == "room-closed"
                ? (groups - 1) * 15
                : groups * 10 + Math.Min(groups * 5, ((groups + 2) / 3 - 1) * 15);
            Assert.True(coverage.OccupiedSlots <= upperBound);
            Assert.True(coverage.MissingLessons >= groups * 15 - upperBound);
            Assert.True(coverage.MissingLessons > 0);
            Assert.NotEqual(AutoGenCoverageStatus.FullyOccupied, coverage.Status);
            Assert.NotEmpty(result.GapDetails!);
            Assert.All(result.GapDetails!, gap =>
            {
                Assert.Equal("not-proven", gap.Diagnostics!["feasibilityStatus"]);
                Assert.Equal(gap.SearchLimitReached ? "limited" : "no-limit-reported", gap.Diagnostics["searchStatus"]);
                Assert.False(string.IsNullOrWhiteSpace(gap.Diagnostics["explanation"]));
            });
        }
        else
        {
            Assert.Equal(groups * hours * 3, coverage.OccupiedSlots);
            Assert.Equal(0, coverage.MissingLessons);
            Assert.Equal(0, coverage.EmptySlots);
            Assert.Equal(0, coverage.InternalGapSlots);
        }
    }

    [Theory]
    [InlineData(6, "resources")]
    [InlineData(7, "resources")]
    [InlineData(8, "resources")]
    [InlineData(6, "names")]
    [InlineData(7, "names")]
    [InlineData(8, "names")]
    [InlineData(6, "order")]
    [InlineData(7, "order")]
    [InlineData(8, "order")]
    public async Task Added_resources_or_renamed_or_reordered_inputs_do_not_reduce_coverage(int groups, string change)
    {
        await using var baseline = await Scenario.CreateAsync(groups, 5, [7, 7, 7], true, false);
        var before = (await baseline.GenerateAsync()).Coverage!;
        await using var changed = await Scenario.CreateAsync(groups, 5, [7, 7, 7], true, false);
        changed.ReverseInputs = change == "order";
        if (change == "names")
        {
            foreach (var group in await changed.Db.Groups.ToListAsync()) group.Name = $"Я-{groups - group.Id}";
            foreach (var teacher in await changed.Db.Teachers.ToListAsync()) teacher.FullName = $"Я-{1000 - teacher.Id}";
            foreach (var room in await changed.Db.Rooms.ToListAsync()) room.Name = $"Я-{groups - room.Id}";
        }
        if (change == "resources")
        {
            changed.Db.Rooms.Add(new Room { Id = 1000, Name = "Додаткова аудиторія", Capacity = 20, BuildingId = 1 });
            for (var module = 1; module <= 3; module++)
            {
                changed.Db.ModuleRooms.Add(new ModuleRoom { ModuleId = module, RoomId = 1000 });
                changed.Db.Teachers.Add(new Teacher { Id = 1000 + module, FullName = $"Додатковий {module}" });
                changed.Db.TeacherModules.Add(new TeacherModule { TeacherId = 1000 + module, ModuleId = module });
                for (var day = 0; day < 5; day++)
                    changed.Db.TeacherWorkingHours.Add(new TeacherWorkingHour { TeacherId = 1000 + module,
                        DayOfWeek = new DateOnly(2030, 2, 4).AddDays(day).DayOfWeek, Start = new TimeOnly(8, 0), End = new TimeOnly(12, 0) });
            }
        }
        await changed.Db.SaveChangesAsync();
        var after = (await changed.GenerateAsync()).Coverage!;
        await baseline.AssertValidAsync();
        await changed.AssertValidAsync();
        Assert.Equal(groups * 15, before.OccupiedSlots);
        Assert.True(after.OccupiedSlots >= before.OccupiedSlots);
        Assert.Equal(0, after.MissingLessons);
        Assert.Equal(0, after.EmptySlots);
        Assert.Equal(0, after.InternalGapSlots);
        Assert.Equal(0, after.OverplannedLessons);
    }

    // Повний перебір незалежний від production solver: 2 групи × 3 модулі,
    // кожен модуль має одного викладача; за один слот він приймає одну групу.
    private static (int Count, int[] Slots) FindMaximum(int[] masks)
    {
        var groupBusy = new bool[2, 3];
        var teacherBusy = new bool[3, 3];
        var best = 0;
        var assignment = Enumerable.Repeat(-1, 6).ToArray();
        var bestAssignment = assignment.ToArray();
        void Search(int lesson, int placed)
        {
            if (placed + 6 - lesson <= best) return;
            if (lesson == 6) { best = placed; bestAssignment = assignment.ToArray(); return; }
            var group = lesson / 3;
            var module = lesson % 3;
            for (var slot = 0; slot < 3; slot++)
            {
                if ((masks[module] & (1 << slot)) == 0 || groupBusy[group, slot] || teacherBusy[module, slot]) continue;
                groupBusy[group, slot] = teacherBusy[module, slot] = true;
                assignment[lesson] = slot;
                Search(lesson + 1, placed + 1);
                assignment[lesson] = -1;
                groupBusy[group, slot] = teacherBusy[module, slot] = false;
            }
            Search(lesson + 1, placed);
        }
        Search(0, 0);
        return (best, bestAssignment);
    }

    private sealed class Scenario(SqliteConnection connection, AppDbContext db, int groups, int days, bool locked, bool lectures, DateOnly start, int extraTeachers) : IAsyncDisposable
    {
        public AppDbContext Db => db;
        private DateOnly Start => start;
        public Dictionary<int, int>? HoursOverride { get; set; }
        public bool ReverseInputs { get; set; }
        private static TimeOnly SlotStart(int slot) => new TimeOnly(8, 0).AddMinutes(slot * 70);
        private TimeOnly PracticeStart(int day, int slot) => SlotStart(slot + (lectures && day == 0 ? 3 : 0));
        private int RequestedHours => days + (lectures ? 1 : 0);
        private int TeachersPerModule => (groups + 2) / 3 + extraTeachers;
        private DraftAutoGenRequest Request => new(Start, ClearExisting: false, CourseId: 1,
            GroupIds: (ReverseInputs ? Enumerable.Range(1, groups).Reverse() : Enumerable.Range(1, groups)).ToList(), Days: WeekPreset.MonSat,
            ModuleHours: HoursOverride ?? (ReverseInputs
                ? new() { [3] = RequestedHours, [2] = RequestedHours, [1] = RequestedHours }
                : new() { [1] = RequestedHours, [2] = RequestedHours, [3] = RequestedHours }),
            SoftFill: true, AllowIncompleteDrafts: false, RangeStartDate: Start, RangeEndDate: Start.AddDays(days - 1),
            SoftOptions: new(RecentRepeatWindowDays: 0, MaxParallelGroupsPerModuleInSlot: TeachersPerModule));

        public static async Task<Scenario> CreateAsync(int groups, int days, int[] masks, bool travel, bool locked, bool lectures = false, DateOnly? startDate = null, int extraTeachers = 0)
        {
            var Start = startDate ?? new DateOnly(2030, 2, 4);
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var fixture = new Scenario(connection, db, groups, days, locked, lectures, Start, extraTeachers);
            db.Courses.Add(new Course { Id = 1, Name = "Конкуренція ресурсів", DurationWeeks = 1, AcademicPeriodStartDate = Start });
            db.Groups.AddRange(Enumerable.Range(1, groups).Select(id => new Group { Id = id, CourseId = 1, Name = $"К-{id}", StudentsCount = 20 }));
            db.LessonTypes.Add(new LessonTypeRef { Id = 1, Code = "PRACTICE", Name = "Практика" });
            db.Buildings.Add(new Building { Id = 1, Name = "Корпус А" });
            if (travel)
            {
                db.Buildings.Add(new Building { Id = 2, Name = "Корпус Б" });
                db.BuildingTravels.AddRange(new BuildingTravel { FromBuildingId = 1, ToBuildingId = 2, Minutes = 10 },
                    new BuildingTravel { FromBuildingId = 2, ToBuildingId = 1, Minutes = 10 });
            }
            for (var group = 1; group <= groups; group++)
                db.Rooms.Add(new Room { Id = group, Name = $"К-{group}", Capacity = 20, BuildingId = travel ? 1 + group % 2 : 1 });
            for (var module = 1; module <= 3; module++)
            {
                db.Modules.Add(new Module { Id = module, CourseId = 1, Code = $"К{module}", Title = $"Модуль {module}" });
                db.ModulePlans.Add(new ModulePlan { CourseId = 1, ModuleId = module, TargetHours = fixture.RequestedHours, IsActive = true });
                db.ModuleTopics.Add(new ModuleTopic { Id = module, ModuleId = module, Order = 1, TopicCode = $"{module}.1", LessonTypeId = 1, TotalHours = days, AuditoriumHours = days });
                for (var room = 1; room <= groups; room++)
                    db.ModuleRooms.Add(new ModuleRoom { ModuleId = module, RoomId = room });
                for (var teacherIndex = 0; teacherIndex < fixture.TeachersPerModule; teacherIndex++)
                {
                    var teacherId = module * 100 + teacherIndex;
                    db.Teachers.Add(new Teacher { Id = teacherId, FullName = $"Викладач {module}.{teacherIndex}" });
                    db.TeacherModules.Add(new TeacherModule { TeacherId = teacherId, ModuleId = module });
                    for (var day = 0; day < days; day++)
                    for (var slot = 0; slot < 3; slot++)
                        if ((masks[module - 1] & (1 << slot)) != 0)
                            db.TeacherWorkingHours.Add(new TeacherWorkingHour { TeacherId = teacherId, DayOfWeek = Start.AddDays(day).DayOfWeek, Start = fixture.PracticeStart(day, slot), End = fixture.PracticeStart(day, slot).AddHours(1) });
                }
            }
            for (var day = 0; day < days; day++)
            for (var slot = 0; slot < 3; slot++)
                db.TimeSlots.Add(new TimeSlot { CourseId = 1, DayOfWeek = Start.AddDays(day).DayOfWeek, Start = fixture.PracticeStart(day, slot), End = fixture.PracticeStart(day, slot).AddHours(1), SortOrder = slot + 1 + (lectures && day == 0 ? 3 : 0), IsActive = true });
            if (lectures)
            {
                db.LessonTypes.Add(new LessonTypeRef { Id = 2, Code = "LECTURE", Name = "Спільна лекція", PreferredFirstInWeek = true });
                db.Rooms.Add(new Room { Id = 100, Name = "Лекційна зала", Capacity = groups * 20, BuildingId = 1 });
                for (var module = 1; module <= 3; module++)
                {
                    db.ModuleTopics.Add(new ModuleTopic { Id = module + 10, ModuleId = module, Order = 0,
                        TopicCode = $"{module}.Л", LessonTypeId = 2, TotalHours = 1, AuditoriumHours = 1 });
                    db.ModuleRooms.Add(new ModuleRoom { ModuleId = module, RoomId = 100 });
                    db.TimeSlots.Add(new TimeSlot { CourseId = 1, DayOfWeek = Start.DayOfWeek,
                        Start = SlotStart(module - 1), End = SlotStart(module - 1).AddHours(1), SortOrder = module, IsActive = true });
                    db.TeacherWorkingHours.Add(new TeacherWorkingHour { TeacherId = module * 100, DayOfWeek = Start.DayOfWeek,
                        Start = SlotStart(module - 1), End = SlotStart(module - 1).AddHours(1) });
                    for (var group = 1; group <= groups; group++)
                        db.TeacherDraftItems.Add(new TeacherDraftItem { GroupId = group, ModuleId = module,
                            ModuleTopicId = module + 10, LessonTypeId = 2, TeacherId = module * 100, RoomId = 100,
                            Date = Start, DayOfWeek = Start.DayOfWeek, StartTime = SlotStart(module - 1),
                            EndTime = SlotStart(module - 1).AddHours(1), IsLocked = true });
                }
            }
            await db.SaveChangesAsync();
            return fixture;
        }

        public List<TeacherDraftItem> Witness()
        {
            var rows = new List<TeacherDraftItem>();
            for (var day = 0; day < days; day++)
            for (var slot = 0; slot < 3; slot++)
            for (var group = 0; group < groups; group++)
            {
                var module = (group + slot) % 3 + 1;
                rows.Add(new TeacherDraftItem { GroupId = group + 1, ModuleId = module, ModuleTopicId = module,
                    LessonTypeId = 1, TeacherId = module * 100 + group / 3, RoomId = group + 1,
                    Date = Start.AddDays(day), DayOfWeek = Start.AddDays(day).DayOfWeek,
                    StartTime = PracticeStart(day, slot), EndTime = PracticeStart(day, slot).AddHours(1), IsLocked = locked && day == 0 && slot == 0 });
            }
            return rows;
        }

        public List<TeacherDraftItem> TinyWitness(int[] assignments) => assignments
            .Select((slot, lesson) => (slot, lesson)).Where(item => item.slot >= 0)
            .Select(item => new TeacherDraftItem
            {
                GroupId = item.lesson / 3 + 1, ModuleId = item.lesson % 3 + 1,
                ModuleTopicId = item.lesson % 3 + 1, LessonTypeId = 1,
                TeacherId = (item.lesson % 3 + 1) * 100, RoomId = item.lesson / 3 + 1,
                Date = Start, DayOfWeek = Start.DayOfWeek, StartTime = SlotStart(item.slot), EndTime = SlotStart(item.slot).AddHours(1)
            }).ToList();

        public async Task<AutoGenResult> GenerateAsync()
        {
            var action = await new TeacherDraftsAutogenService(db).DraftAutoGen(Request);
            return Assert.IsType<AutoGenResult>(Assert.IsType<OkObjectResult>(action.Result).Value);
        }

        public async Task AssertValidAsync()
        {
            var validation = await new TeacherDraftsAutogenHardRuleValidator(db).ValidateAsync(
                new(1, Request.GroupIds!, Start, Start.AddDays(days - 1), WeekPreset.MonSat,
                    MaxParallelGroupsPerModuleInSlot: TeachersPerModule));
            Assert.Empty(validation.Violations);
            Assert.Empty(await TravelInvariantVerifier.FindViolationsAsync(db, 1, Start, Start.AddDays(days - 1)));
        }

        public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
