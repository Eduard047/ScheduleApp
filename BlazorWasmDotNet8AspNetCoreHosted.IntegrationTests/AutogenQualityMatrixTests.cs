using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

[CollectionDefinition("Autogen performance", DisableParallelization = true)]
public sealed class AutogenPerformanceCollection
{
}

[Collection("Autogen performance")]
public sealed class AutogenQualityMatrixTests
{
    private static readonly DateOnly FrozenSemesterStart = new(2030, 2, 4);

    public static TheoryData<QualityProfile> Profiles => new()
    {
        new QualityProfile(
            Name: AutoGenRecommendedProfile.Name,
            Kind: ScenarioKind.BalancedPractice,
            ExpectedCreated: 12,
            ExpectedTotalRows: 12,
            ExpectedLockedRows: 0,
            MaximumGapCount: 0,
            MaximumMissingDemand: 0,
            MaximumSearchLimitWarnings: 0,
            MaximumHardViolationCount: 0,
            MaximumIncompleteCount: 0,
            MinimumScheduledWeekCount: 1,
            MinimumScheduledWeekdayCount: 1,
            MaximumGroupWindowCount: 0,
            MaximumTeacherWindowCount: 2,
            MaximumBuildingTransitionCount: 0,
            MaximumTeacherLoadSpread: 2,
            MaximumRuntime: TimeSpan.FromSeconds(15),
            ExpectedFingerprint: "851cd5f21477053c937e52527c931af07ff2e23ccaa5fec2058359afc54d263e"),
        new QualityProfile(
            Name: "shared-lecture-flow",
            Kind: ScenarioKind.SharedLectureFlow,
            ExpectedCreated: 8,
            ExpectedTotalRows: 8,
            ExpectedLockedRows: 0,
            MaximumGapCount: 0,
            MaximumMissingDemand: 0,
            MaximumSearchLimitWarnings: 0,
            MaximumHardViolationCount: 0,
            MaximumIncompleteCount: 0,
            MinimumScheduledWeekCount: 1,
            MinimumScheduledWeekdayCount: 1,
            MaximumGroupWindowCount: 0,
            MaximumTeacherWindowCount: 0,
            MaximumBuildingTransitionCount: 0,
            MaximumTeacherLoadSpread: 1,
            MaximumRuntime: TimeSpan.FromSeconds(15),
            ExpectedFingerprint: "d60f4c1430ff1778db8d27c5a78b921744677b7371b3f51f21587e1a35a72399"),
        new QualityProfile(
            Name: "constrained-18-week-refill-scale",
            Kind: ScenarioKind.ConstrainedEighteenWeekScale,
            ExpectedCreated: 306,
            ExpectedTotalRows: 324,
            ExpectedLockedRows: 18,
            MaximumGapCount: 0,
            MaximumMissingDemand: 0,
            MaximumSearchLimitWarnings: 0,
            MaximumHardViolationCount: 0,
            MaximumIncompleteCount: 0,
            MinimumScheduledWeekCount: 18,
            MinimumScheduledWeekdayCount: 3,
            MaximumGroupWindowCount: 0,
            MaximumTeacherWindowCount: 0,
            MaximumBuildingTransitionCount: 0,
            MaximumTeacherLoadSpread: 0,
            MaximumRuntime: TimeSpan.FromSeconds(45),
            ExpectedFingerprint: "26ec8a636ac323e39266d026c9646d3d66f0fda9d6ad5b5cf39137bbd08beea5")
    };

    [Theory]
    [MemberData(nameof(Profiles))]
    [Trait("Category", "AutogenQuality")]
    public async Task Frozen_semester_profile_meets_quality_budgets_and_has_stable_fingerprint(
        QualityProfile profile)
    {
        var first = await RunProfileAsync(profile);
        var replay = await RunProfileAsync(profile);

        Console.WriteLine(
            $"Профіль={profile.Name}; створено={first.Created}; пропуски={first.GapCount}; " +
            $"дефіцит-попиту={first.MissingDemand}; тижні={first.ScheduledWeekCount}; " +
            $"дні-тижня={first.ScheduledWeekdayCount}; " +
            $"вікна-груп={first.GroupWindowCount}; вікна-викладачів={first.TeacherWindowCount}; " +
            $"зміни-корпусів={first.BuildingTransitionCount}; розкид-навантаження={first.TeacherLoadSpread}; " +
            $"порушення={first.HardViolationCount}; незавершені={first.IncompleteCount}; " +
            $"час={first.Runtime.TotalMilliseconds:F0} мс; fingerprint={first.Fingerprint}");
        if (first.GapReasons.Count > 0)
        {
            Console.WriteLine($"Причини пропусків: {Describe(first.GapReasons)}");
        }

        AssertSnapshot(profile, first);
        AssertSnapshot(profile, replay);
        Assert.Equal(first.Fingerprint, replay.Fingerprint);
        Assert.Equal(profile.ExpectedFingerprint, first.Fingerprint);
    }

    private static void AssertSnapshot(QualityProfile profile, QualitySnapshot snapshot)
    {
        Assert.Equal(profile.ExpectedCreated, snapshot.Created);
        Assert.Equal(profile.ExpectedTotalRows, snapshot.TotalRows);
        Assert.Equal(profile.ExpectedLockedRows, snapshot.LockedRows);
        Assert.True(
            snapshot.GapCount <= profile.MaximumGapCount,
            $"Профіль {profile.Name}: пропусків {snapshot.GapCount}, бюджет {profile.MaximumGapCount}. " +
            $"Причини: {Describe(snapshot.GapReasons)}");
        Assert.True(
            snapshot.MissingDemand <= profile.MaximumMissingDemand,
            $"Профіль {profile.Name}: незаповнений попит {snapshot.MissingDemand}, " +
            $"бюджет {profile.MaximumMissingDemand}.");
        Assert.True(
            snapshot.SearchLimitWarningCount <= profile.MaximumSearchLimitWarnings,
            $"Профіль {profile.Name}: попереджень про межу пошуку {snapshot.SearchLimitWarningCount}, " +
            $"бюджет {profile.MaximumSearchLimitWarnings}.");
        Assert.True(
            snapshot.HardViolationCount <= profile.MaximumHardViolationCount,
            $"Профіль {profile.Name}: порушень жорстких правил {snapshot.HardViolationCount}, " +
            $"бюджет {profile.MaximumHardViolationCount}. Порушення: {Describe(snapshot.HardViolations)}");
        Assert.True(
            snapshot.IncompleteCount <= profile.MaximumIncompleteCount,
            $"Профіль {profile.Name}: незавершених занять {snapshot.IncompleteCount}, " +
            $"бюджет {profile.MaximumIncompleteCount}.");
        Assert.True(
            snapshot.ScheduledWeekCount >= profile.MinimumScheduledWeekCount,
            $"Профіль {profile.Name}: розклад охопив {snapshot.ScheduledWeekCount} тижн., " +
            $"мінімум {profile.MinimumScheduledWeekCount}.");
        Assert.True(
            snapshot.ScheduledWeekdayCount >= profile.MinimumScheduledWeekdayCount,
            $"Профіль {profile.Name}: використано {snapshot.ScheduledWeekdayCount} дн. тижня, " +
            $"мінімум {profile.MinimumScheduledWeekdayCount}.");
        Assert.True(
            snapshot.GroupWindowCount <= profile.MaximumGroupWindowCount,
            $"Профіль {profile.Name}: вікон у груп {snapshot.GroupWindowCount}, " +
            $"бюджет {profile.MaximumGroupWindowCount}.");
        Assert.True(
            snapshot.TeacherWindowCount <= profile.MaximumTeacherWindowCount,
            $"Профіль {profile.Name}: вікон у викладачів {snapshot.TeacherWindowCount}, " +
            $"бюджет {profile.MaximumTeacherWindowCount}.");
        Assert.True(
            snapshot.BuildingTransitionCount <= profile.MaximumBuildingTransitionCount,
            $"Профіль {profile.Name}: переходів між корпусами {snapshot.BuildingTransitionCount}, " +
            $"бюджет {profile.MaximumBuildingTransitionCount}.");
        Assert.True(
            snapshot.TeacherLoadSpread <= profile.MaximumTeacherLoadSpread,
            $"Профіль {profile.Name}: розкид навантаження {snapshot.TeacherLoadSpread}, " +
            $"бюджет {profile.MaximumTeacherLoadSpread}.");
        Assert.True(
            snapshot.Runtime <= profile.MaximumRuntime,
            $"Профіль {profile.Name}: генерація тривала {snapshot.Runtime.TotalMilliseconds:F0} мс, " +
            $"бюджет {profile.MaximumRuntime.TotalMilliseconds:F0} мс.");
    }

    private static string Describe(IReadOnlyCollection<string> values, int limit = 10)
    {
        var preview = string.Join(" | ", values.Take(limit));
        return values.Count <= limit ? preview : $"{preview} | … ще {values.Count - limit}";
    }

    private static async Task<QualitySnapshot> RunProfileAsync(QualityProfile profile)
    {
        await using var database = await QualityDatabase.CreateAsync();
        var seed = profile.Kind switch
        {
            ScenarioKind.BalancedPractice => await database.SeedBalancedPracticeAsync(),
            ScenarioKind.SharedLectureFlow => await database.SeedSharedLectureFlowAsync(),
            ScenarioKind.ConstrainedEighteenWeekScale => await database.SeedConstrainedEighteenWeekScaleAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile.Kind, null)
        };
        if (profile.Name == AutoGenRecommendedProfile.Name)
        {
            AssertUiRecommendedProfileContract(seed.Request);
        }

        var stopwatch = Stopwatch.StartNew();
        var action = await new TeacherDraftsAutogenService(database.Db).DraftAutoGen(seed.Request);
        stopwatch.Stop();

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var result = Assert.IsType<AutoGenResult>(ok.Value);
        database.Db.ChangeTracker.Clear();
        var rangeStart = seed.Request.RangeStartDate ?? seed.Request.WeekStart;
        var rangeEnd = seed.Request.RangeEndDate ?? seed.Request.WeekStart.AddDays(6);

        var validation = await new TeacherDraftsAutogenHardRuleValidator(database.Db).ValidateAsync(
            new TeacherDraftsAutogenHardRuleValidationRequest(
                seed.CourseId,
                seed.GroupIds,
                rangeStart,
                rangeEnd,
                seed.Request.Days));
        var incompleteCount = await database.Db.TeacherDraftItems
            .AsNoTracking()
            .CountAsync(item => seed.GroupIds.Contains(item.GroupId)
                                && item.Date >= rangeStart
                                && item.Date <= rangeEnd
                                && (item.TeacherId == null || item.RoomId == null));
        var totalRows = await database.Db.TeacherDraftItems
            .AsNoTracking()
            .CountAsync(item => seed.GroupIds.Contains(item.GroupId)
                                && item.Date >= rangeStart
                                && item.Date <= rangeEnd);
        var lockedRows = await database.Db.TeacherDraftItems
            .AsNoTracking()
            .CountAsync(item => seed.GroupIds.Contains(item.GroupId)
                                && item.Date >= rangeStart
                                && item.Date <= rangeEnd
                                && item.IsLocked);
        var coverage = await ReadCoverageAsync(
            database.Db,
            seed.CourseId,
            seed.GroupIds,
            seed.Request.ModuleHours ?? new Dictionary<int, int>(),
            rangeStart,
            rangeEnd);
        var fingerprint = await ReadFingerprintAsync(database.Db, seed.GroupIds, rangeStart, rangeEnd);

        return new QualitySnapshot(
            result.Created,
            totalRows,
            lockedRows,
            result.GapDetails?.Count ?? 0,
            coverage.MissingDemand,
            result.Warnings.Count(warning => warning.Contains("[search-limit]", StringComparison.Ordinal))
            + (result.GapDetails?.Count(gap => gap.SearchLimitReached) ?? 0),
            validation.Violations.Count,
            incompleteCount,
            coverage.ScheduledWeekCount,
            coverage.ScheduledWeekdayCount,
            coverage.GroupWindowCount,
            coverage.TeacherWindowCount,
            coverage.BuildingTransitionCount,
            coverage.TeacherLoadSpread,
            stopwatch.Elapsed,
            fingerprint,
            result.GapDetails?.Select(item => item.Reason ?? item.SlotLabel).ToList() ?? [],
            validation.Violations);
    }

    private static void AssertUiRecommendedProfileContract(DraftAutoGenRequest request)
    {
        var expected = AutoGenRecommendedProfile.CreateSoftOptions();
        var options = Assert.IsType<DraftAutoGenSoftOptions>(request.SoftOptions);
        Assert.True(request.SoftFill);
        Assert.Equal(AutoGenRecommendedProfile.PreferredFirstMaxSlotOrderOverride, request.PreferredFirstMaxSlotOrderOverride);
        Assert.Equal(expected.MaxParallelGroupsPerModuleInSlot, options.MaxParallelGroupsPerModuleInSlot);
        Assert.Equal(expected.RecentRepeatWindowDays, options.RecentRepeatWindowDays);
        Assert.Equal(expected.PreferredMaxDistinctModulesPerDay, options.PreferredMaxDistinctModulesPerDay);
        Assert.Equal(expected.MaxDistinctModulesPerDay, options.MaxDistinctModulesPerDay);
        Assert.Equal(expected.PreferredFirstPenaltyMultiplier, options.PreferredFirstPenaltyMultiplier);
        Assert.Equal(expected.TeacherLoadPenaltyWeight, options.TeacherLoadPenaltyWeight);
        Assert.Equal(expected.BuildingDistancePenaltyWeight, options.BuildingDistancePenaltyWeight);
    }

    private static DraftAutoGenSoftOptions MapSoftOptions(AutoGenSoftOptionsDto options)
        => new(
            MaxParallelGroupsPerModuleInSlot: options.MaxParallelGroupsPerModuleInSlot,
            RecentRepeatWindowDays: options.RecentRepeatWindowDays,
            PreferredMaxDistinctModulesPerDay: options.PreferredMaxDistinctModulesPerDay,
            MaxDistinctModulesPerDay: options.MaxDistinctModulesPerDay,
            PreferredFirstPenaltyMultiplier: options.PreferredFirstPenaltyMultiplier,
            AdjacentRoomChangePenalty: options.AdjacentRoomChangePenalty,
            TeacherLoadPenaltyWeight: options.TeacherLoadPenaltyWeight,
            BuildingDistancePenaltyWeight: options.BuildingDistancePenaltyWeight);

    private static async Task<string> ReadFingerprintAsync(
        AppDbContext db,
        IReadOnlyCollection<int> groupIds,
        DateOnly rangeStart,
        DateOnly rangeEnd)
    {
        var rows = await db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => groupIds.Contains(item.GroupId)
                           && item.Date >= rangeStart
                           && item.Date <= rangeEnd)
            .OrderBy(item => item.Date)
            .ThenBy(item => item.StartTime)
            .ThenBy(item => item.EndTime)
            .ThenBy(item => item.GroupId)
            .ThenBy(item => item.ModuleId)
            .ThenBy(item => item.ModuleTopicId)
            .ThenBy(item => item.TeacherId)
            .ThenBy(item => item.RoomId)
            .Select(item => new
            {
                item.Date,
                item.StartTime,
                item.EndTime,
                item.GroupId,
                item.ModuleId,
                item.ModuleTopicId,
                item.LessonTypeId,
                item.TeacherId,
                item.RoomId,
                item.IsSelfStudy,
                item.BatchKey,
                item.ValidationWarnings,
                item.Status,
                item.IsLocked
            })
            .ToListAsync();
        var payload = string.Join('\n', rows.Select(item => string.Join('|',
            item.Date.ToString("yyyy-MM-dd"),
            item.StartTime.ToString("HH:mm"),
            item.EndTime.ToString("HH:mm"),
            item.GroupId,
            item.ModuleId,
            item.ModuleTopicId,
            item.LessonTypeId,
            item.TeacherId,
            item.RoomId,
            item.IsSelfStudy,
            item.BatchKey ?? string.Empty,
            item.ValidationWarnings ?? string.Empty,
            (int)item.Status,
            item.IsLocked)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static async Task<CoverageSnapshot> ReadCoverageAsync(
        AppDbContext db,
        int courseId,
        IReadOnlyCollection<int> groupIds,
        IReadOnlyDictionary<int, int> moduleHours,
        DateOnly rangeStart,
        DateOnly rangeEnd)
    {
        var rows = await db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => groupIds.Contains(item.GroupId)
                           && item.Date >= rangeStart
                           && item.Date <= rangeEnd)
            .Select(item => new
            {
                item.GroupId,
                item.ModuleId,
                item.ModuleTopicId,
                item.LessonTypeId,
                item.TeacherId,
                item.Date,
                item.StartTime,
                item.EndTime,
                BuildingId = item.RoomId == null ? null : (int?)item.Room!.BuildingId
            })
            .ToListAsync();
        var configuredSlots = await db.TimeSlots
            .AsNoTracking()
            .Where(slot => slot.CourseId == courseId
                           && slot.IsActive
                           && slot.DayOfWeek != null)
            .Select(slot => new
            {
                slot.DayOfWeek,
                slot.Start,
                slot.End,
                slot.SortOrder
            })
            .ToListAsync();
        var slotIndexByTime = new Dictionary<(DayOfWeek Day, TimeOnly Start, TimeOnly End), int>();
        foreach (var daySlots in configuredSlots.GroupBy(slot => slot.DayOfWeek))
        {
            var index = 0;
            foreach (var slot in daySlots
                         .OrderBy(slot => slot.SortOrder)
                         .ThenBy(slot => slot.Start)
                         .ThenBy(slot => slot.End))
            {
                slotIndexByTime[(slot.DayOfWeek!.Value, slot.Start, slot.End)] = index++;
            }
        }
        var actualHours = rows
            .GroupBy(item => (item.GroupId, item.ModuleId))
            .ToDictionary(group => group.Key, group => group.Count());
        var planningWeekStart = StartOfWeek(rangeStart);
        var planningWeekEndExclusive = StartOfWeek(rangeEnd).AddDays(7);
        var rangeWeekCount = Math.Max(
            1,
            (planningWeekEndExclusive.DayNumber - planningWeekStart.DayNumber) / 7);
        var missingDemand = groupIds.Sum(groupId => moduleHours.Sum(module =>
            Math.Max(
                0,
                module.Value * rangeWeekCount - actualHours.GetValueOrDefault((groupId, module.Key)))));
        var scheduledWeekCount = rows
            .Select(item => (item.Date.DayNumber - rangeStart.DayNumber) / 7)
            .Distinct()
            .Count();
        var scheduledWeekdayCount = rows
            .Select(item => item.Date.DayOfWeek)
            .Distinct()
            .Count();
        var groupWindowCount = rows
            .GroupBy(item => (item.GroupId, item.Date))
            .Sum(group =>
            {
                var indexes = group
                    .Select(item => slotIndexByTime.TryGetValue(
                        (item.Date.DayOfWeek, item.StartTime, item.EndTime),
                        out var index)
                            ? index
                            : -1)
                    .Where(index => index >= 0)
                    .Distinct()
                    .OrderBy(index => index)
                    .ToList();
                return indexes.Count < 2
                    ? 0
                    : indexes[^1] - indexes[0] + 1 - indexes.Count;
            });
        var teacherWindowCount = rows
            .Where(item => item.TeacherId is not null)
            .GroupBy(item => (TeacherId: item.TeacherId!.Value, item.Date))
            .Sum(group =>
            {
                var indexes = group
                    .Select(item => slotIndexByTime.TryGetValue(
                        (item.Date.DayOfWeek, item.StartTime, item.EndTime),
                        out var index)
                            ? index
                            : -1)
                    .Where(index => index >= 0)
                    .Distinct()
                    .OrderBy(index => index)
                    .ToList();
                return indexes.Count < 2
                    ? 0
                    : indexes[^1] - indexes[0] + 1 - indexes.Count;
            });
        var buildingTransitionCount = rows
            .GroupBy(item => (item.GroupId, item.Date))
            .Sum(group =>
            {
                var buildings = group
                    .GroupBy(item => (item.StartTime, item.EndTime))
                    .OrderBy(item => item.Key.StartTime)
                    .ThenBy(item => item.Key.EndTime)
                    .Select(item => item
                        .Select(row => row.BuildingId)
                        .FirstOrDefault(buildingId => buildingId is not null))
                    .Where(buildingId => buildingId is not null)
                    .Select(buildingId => buildingId!.Value)
                    .ToList();
                return buildings
                    .Skip(1)
                    .Where((buildingId, index) => buildingId != buildings[index])
                    .Count();
            });
        var scheduledModuleIds = rows
            .Select(item => item.ModuleId)
            .Distinct()
            .ToList();
        var eligibleTeacherIds = await db.TeacherModules
            .AsNoTracking()
            .Where(link => scheduledModuleIds.Contains(link.ModuleId))
            .Select(link => link.TeacherId)
            .Distinct()
            .ToListAsync();
        var teacherLoadById = rows
            .Where(item => item.TeacherId is not null)
            .Select(item => new
            {
                TeacherId = item.TeacherId!.Value,
                item.Date,
                item.StartTime,
                item.EndTime,
                item.ModuleId,
                item.ModuleTopicId,
                item.LessonTypeId
            })
            .Distinct()
            .GroupBy(item => item.TeacherId)
            .ToDictionary(group => group.Key, group => group.Count());
        var teacherLoads = eligibleTeacherIds
            .Select(teacherId => teacherLoadById.GetValueOrDefault(teacherId))
            .ToList();
        var teacherLoadSpread = teacherLoads.Count == 0
            ? 0
            : teacherLoads.Max() - teacherLoads.Min();

        return new CoverageSnapshot(
            missingDemand,
            scheduledWeekCount,
            scheduledWeekdayCount,
            groupWindowCount,
            teacherWindowCount,
            buildingTransitionCount,
            teacherLoadSpread);
    }

    private static DateOnly StartOfWeek(DateOnly date)
    {
        var offset = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-offset);
    }

    public sealed record QualityProfile(
        string Name,
        ScenarioKind Kind,
        int ExpectedCreated,
        int ExpectedTotalRows,
        int ExpectedLockedRows,
        int MaximumGapCount,
        int MaximumMissingDemand,
        int MaximumSearchLimitWarnings,
        int MaximumHardViolationCount,
        int MaximumIncompleteCount,
        int MinimumScheduledWeekCount,
        int MinimumScheduledWeekdayCount,
        int MaximumGroupWindowCount,
        int MaximumTeacherWindowCount,
        int MaximumBuildingTransitionCount,
        int MaximumTeacherLoadSpread,
        TimeSpan MaximumRuntime,
        string ExpectedFingerprint)
    {
        public override string ToString() => Name;
    }

    public enum ScenarioKind
    {
        BalancedPractice,
        SharedLectureFlow,
        ConstrainedEighteenWeekScale
    }

    private sealed record QualitySnapshot(
        int Created,
        int TotalRows,
        int LockedRows,
        int GapCount,
        int MissingDemand,
        int SearchLimitWarningCount,
        int HardViolationCount,
        int IncompleteCount,
        int ScheduledWeekCount,
        int ScheduledWeekdayCount,
        int GroupWindowCount,
        int TeacherWindowCount,
        int BuildingTransitionCount,
        int TeacherLoadSpread,
        TimeSpan Runtime,
        string Fingerprint,
        IReadOnlyList<string> GapReasons,
        IReadOnlyList<string> HardViolations);

    private sealed record CoverageSnapshot(
        int MissingDemand,
        int ScheduledWeekCount,
        int ScheduledWeekdayCount,
        int GroupWindowCount,
        int TeacherWindowCount,
        int BuildingTransitionCount,
        int TeacherLoadSpread);

    private sealed record QualitySeed(
        int CourseId,
        IReadOnlyCollection<int> GroupIds,
        DraftAutoGenRequest Request);

    private sealed class QualityDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private QualityDatabase(SqliteConnection connection, AppDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public AppDbContext Db { get; }

        public static async Task<QualityDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new QualityDatabase(connection, db);
        }

        public async Task<QualitySeed> SeedBalancedPracticeAsync()
        {
            const int courseId = 20000;
            const int practiceTypeId = 20001;
            const int buildingId = 20002;
            var groupIds = Enumerable.Range(0, 3).Select(index => 20100 + index).ToArray();
            var moduleIds = new[] { 20200, 20201 };
            var teacherIds = Enumerable.Range(0, 6).Select(index => 20300 + index).ToArray();
            var roomIds = Enumerable.Range(0, 3).Select(index => 20400 + index).ToArray();

            AddCourse(courseId, "Синтетичний семестр: практичні заняття");
            AddGroups(courseId, groupIds, "SYN-P");
            AddLessonType(practiceTypeId, "PRACTICE", "Практичне заняття", preferredFirstInWeek: false);
            Db.Buildings.Add(new Building { Id = buildingId, Name = "Синтетичний корпус" });

            for (var index = 0; index < moduleIds.Length; index++)
            {
                var moduleId = moduleIds[index];
                Db.Modules.Add(new Module
                {
                    Id = moduleId,
                    CourseId = courseId,
                    Code = $"SYN-P{index + 1}",
                    Title = $"Синтетичний практичний модуль {index + 1}",
                    Credits = 1
                });
                Db.ModulePlans.Add(new ModulePlan
                {
                    CourseId = courseId,
                    ModuleId = moduleId,
                    TargetHours = 2,
                    ScheduledHours = 0,
                    IsActive = true
                });
                var moduleTeacherIds = teacherIds.Skip(index * groupIds.Length).Take(groupIds.Length).ToList();
                Db.Teachers.AddRange(moduleTeacherIds.Select((teacherId, teacherIndex) => new Teacher
                {
                    Id = teacherId,
                    FullName = $"Синтетичний викладач {index + 1}.{teacherIndex + 1}"
                }));
                Db.TeacherModules.AddRange(moduleTeacherIds.Select(teacherId => new TeacherModule
                {
                    TeacherId = teacherId,
                    ModuleId = moduleId
                }));
                Db.ModuleTopics.AddRange(
                    new ModuleTopic
                    {
                        Id = 20500 + index * 10,
                        ModuleId = moduleId,
                        Order = 1,
                        TopicCode = $"SYN-P{index + 1}.1",
                        LessonTypeId = practiceTypeId,
                        TotalHours = 1,
                        AuditoriumHours = 1
                    },
                    new ModuleTopic
                    {
                        Id = 20501 + index * 10,
                        ModuleId = moduleId,
                        Order = 2,
                        TopicCode = $"SYN-P{index + 1}.2",
                        LessonTypeId = practiceTypeId,
                        TotalHours = 1,
                        AuditoriumHours = 1
                    });
            }

            Db.Rooms.AddRange(roomIds.Select((roomId, index) => new Room
            {
                Id = roomId,
                Name = $"SYN-{index + 1}",
                Capacity = 30,
                BuildingId = buildingId
            }));
            Db.ModuleRooms.AddRange(moduleIds.SelectMany(moduleId => roomIds.Select(roomId => new ModuleRoom
            {
                ModuleId = moduleId,
                RoomId = roomId
            })));
            AddSlots(courseId, 4);
            AddTeacherWorkingHours(teacherIds);
            await Db.SaveChangesAsync();

            return new QualitySeed(
                courseId,
                groupIds,
                BuildRequest(
                    courseId,
                    groupIds,
                    moduleIds.ToDictionary(id => id, _ => 2),
                    MapSoftOptions(AutoGenRecommendedProfile.CreateSoftOptions()),
                    preferredFirstMaxSlotOrderOverride: AutoGenRecommendedProfile.PreferredFirstMaxSlotOrderOverride,
                    groupRoomPreferences: groupIds
                        .Select((groupId, index) => new GroupRoomPreferenceDto(
                            groupId,
                            RoomIds: [roomIds[index]]))
                        .ToList()));
        }

        public async Task<QualitySeed> SeedSharedLectureFlowAsync()
        {
            const int courseId = 21000;
            const int practiceTypeId = 21001;
            const int lectureTypeId = 21002;
            const int buildingId = 21003;
            const int moduleId = 21200;
            const int lectureRoomId = 21404;
            var groupIds = Enumerable.Range(0, 4).Select(index => 21100 + index).ToArray();
            var teacherIds = Enumerable.Range(0, 4).Select(index => 21300 + index).ToArray();
            var practiceRoomIds = Enumerable.Range(0, 4).Select(index => 21400 + index).ToArray();

            AddCourse(courseId, "Синтетичний семестр: лекційний потік");
            AddGroups(courseId, groupIds, "SYN-L");
            AddLessonType(practiceTypeId, "PRACTICE", "Практичне заняття", preferredFirstInWeek: false);
            AddLessonType(lectureTypeId, "LECTURE", "Лекція", preferredFirstInWeek: true);
            Db.Buildings.Add(new Building { Id = buildingId, Name = "Синтетичний лекційний корпус" });
            Db.Modules.Add(new Module
            {
                Id = moduleId,
                CourseId = courseId,
                Code = "SYN-L1",
                Title = "Синтетичний модуль із лекційним потоком",
                Credits = 1
            });
            Db.ModulePlans.Add(new ModulePlan
            {
                CourseId = courseId,
                ModuleId = moduleId,
                TargetHours = 2,
                ScheduledHours = 0,
                IsActive = true
            });
            Db.Teachers.AddRange(teacherIds.Select((teacherId, index) => new Teacher
            {
                Id = teacherId,
                FullName = $"Синтетичний потоковий викладач {index + 1}"
            }));
            Db.TeacherModules.AddRange(teacherIds.Select(teacherId => new TeacherModule
            {
                TeacherId = teacherId,
                ModuleId = moduleId
            }));
            Db.Rooms.AddRange(practiceRoomIds
                .Select((roomId, index) => new Room
                {
                    Id = roomId,
                    Name = $"SYN-PRACTICE-{index + 1}",
                    Capacity = 30,
                    BuildingId = buildingId
                })
                .Append(new Room
                {
                    Id = lectureRoomId,
                    Name = "SYN-LECTURE",
                    Capacity = 120,
                    BuildingId = buildingId
                }));
            Db.ModuleRooms.AddRange(practiceRoomIds
                .Append(lectureRoomId)
                .Select(roomId => new ModuleRoom { ModuleId = moduleId, RoomId = roomId }));
            Db.ModuleTopics.AddRange(
                new ModuleTopic
                {
                    Id = 21500,
                    ModuleId = moduleId,
                    Order = 1,
                    TopicCode = "SYN-L1.1",
                    LessonTypeId = lectureTypeId,
                    TotalHours = 1,
                    AuditoriumHours = 1
                },
                new ModuleTopic
                {
                    Id = 21501,
                    ModuleId = moduleId,
                    Order = 2,
                    TopicCode = "SYN-L1.2",
                    LessonTypeId = practiceTypeId,
                    TotalHours = 1,
                    AuditoriumHours = 1
                });

            AddSlots(courseId, 2);
            AddTeacherWorkingHours(teacherIds);
            await Db.SaveChangesAsync();

            return new QualitySeed(
                courseId,
                groupIds,
                BuildRequest(
                    courseId,
                    groupIds,
                    new Dictionary<int, int> { [moduleId] = 2 },
                    new DraftAutoGenSoftOptions(
                        MaxParallelGroupsPerModuleInSlot: groupIds.Length,
                        RecentRepeatWindowDays: 0)));
        }

        public async Task<QualitySeed> SeedConstrainedEighteenWeekScaleAsync()
        {
            const int courseId = 22000;
            const int practiceTypeId = 22001;
            const int buildingId = 22002;
            const int hoursPerWeek = 2;
            const int hoursPerModule = hoursPerWeek * 18;
            var groupIds = Enumerable.Range(0, 3).Select(index => 22100 + index).ToArray();
            var moduleIds = Enumerable.Range(0, 3).Select(index => 22200 + index).ToArray();
            var teacherIds = Enumerable.Range(0, 3).Select(index => 22300 + index).ToArray();
            var roomIds = Enumerable.Range(0, 3).Select(index => 22400 + index).ToArray();
            DayOfWeek[] teachingDays = [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday];

            AddCourse(courseId, "Синтетичний 18-тижневий семестр з дефіцитними ресурсами");
            AddGroups(courseId, groupIds, "SYN-18");
            AddLessonType(practiceTypeId, "PRACTICE", "Практичне заняття", preferredFirstInWeek: false);
            Db.Buildings.Add(new Building { Id = buildingId, Name = "Синтетичний масштабний корпус" });
            Db.Rooms.AddRange(roomIds.Select((roomId, index) => new Room
            {
                Id = roomId,
                Name = $"SYN-18-{index + 1}",
                Capacity = 30,
                BuildingId = buildingId
            }));

            for (var moduleIndex = 0; moduleIndex < moduleIds.Length; moduleIndex++)
            {
                var moduleId = moduleIds[moduleIndex];
                var teacherId = teacherIds[moduleIndex];
                Db.Modules.Add(new Module
                {
                    Id = moduleId,
                    CourseId = courseId,
                    Code = $"SYN-18-M{moduleIndex + 1}",
                    Title = $"Синтетичний масштабний модуль {moduleIndex + 1}",
                    Credits = 3
                });
                Db.ModulePlans.Add(new ModulePlan
                {
                    CourseId = courseId,
                    ModuleId = moduleId,
                    TargetHours = hoursPerModule,
                    ScheduledHours = 0,
                    IsActive = true
                });
                Db.Teachers.Add(new Teacher
                {
                    Id = teacherId,
                    FullName = $"Синтетичний масштабний викладач {moduleIndex + 1}"
                });
                Db.TeacherModules.Add(new TeacherModule
                {
                    TeacherId = teacherId,
                    ModuleId = moduleId
                });
                Db.ModuleRooms.AddRange(roomIds.Select(roomId => new ModuleRoom
                {
                    ModuleId = moduleId,
                    RoomId = roomId
                }));
                Db.ModuleTopics.AddRange(Enumerable.Range(0, hoursPerModule).Select(topicIndex => new ModuleTopic
                {
                    Id = 22500 + moduleIndex * 100 + topicIndex,
                    ModuleId = moduleId,
                    Order = topicIndex + 1,
                    TopicCode = $"SYN-18-M{moduleIndex + 1}.{topicIndex + 1}",
                    LessonTypeId = practiceTypeId,
                    TotalHours = 1,
                    AuditoriumHours = 1
                }));
                AddTeacherWorkingHours(teacherId, teachingDays);
            }

            AddSlots(
                courseId,
                teachingDays,
                count: 2);
            var seededTopicUses = new Dictionary<(int GroupId, int ModuleId), int>();
            var seededDates = teachingDays.Select((day, index) => FrozenSemesterStart.AddDays(index * 2)).ToArray();
            var starts = new[] { new TimeOnly(8, 0), new TimeOnly(9, 10) };
            for (var dayIndex = 0; dayIndex < seededDates.Length; dayIndex++)
            {
                for (var slotIndex = 0; slotIndex < starts.Length; slotIndex++)
                {
                    var position = dayIndex * starts.Length + slotIndex;
                    for (var groupIndex = 0; groupIndex < groupIds.Length; groupIndex++)
                    {
                        var moduleIndex = (groupIndex + position) % moduleIds.Length;
                        var moduleId = moduleIds[moduleIndex];
                        var topicUseKey = (groupIds[groupIndex], moduleId);
                        var topicIndex = seededTopicUses.GetValueOrDefault(topicUseKey);
                        seededTopicUses[topicUseKey] = topicIndex + 1;
                        Db.TeacherDraftItems.Add(new TeacherDraftItem
                        {
                            Date = seededDates[dayIndex],
                            DayOfWeek = seededDates[dayIndex].DayOfWeek,
                            StartTime = starts[slotIndex],
                            EndTime = starts[slotIndex].AddHours(1),
                            LessonTypeId = practiceTypeId,
                            GroupId = groupIds[groupIndex],
                            ModuleId = moduleId,
                            ModuleTopicId = 22500 + moduleIndex * 100 + topicIndex,
                            TeacherId = teacherIds[moduleIndex],
                            RoomId = roomIds[groupIndex],
                            Status = DraftStatus.Draft,
                            BatchKey = $"quality-seed-{position}-{groupIndex}",
                            IsLocked = true,
                            IsSelfStudy = false
                        });
                    }
                }
            }
            await Db.SaveChangesAsync();

            return new QualitySeed(
                courseId,
                groupIds,
                BuildRequest(
                    courseId,
                    groupIds,
                    moduleIds.ToDictionary(id => id, _ => hoursPerWeek),
                    MapSoftOptions(AutoGenRecommendedProfile.CreateSoftOptions()),
                    rangeEndDate: FrozenSemesterStart.AddDays(18 * 7 - 1),
                    clearExisting: false));
        }

        private void AddCourse(int courseId, string name)
            => Db.Courses.Add(new Course { Id = courseId, Name = name, DurationWeeks = 18 });

        private void AddGroups(int courseId, IEnumerable<int> groupIds, string prefix)
            => Db.Groups.AddRange(groupIds.Select((id, index) => new Group
            {
                Id = id,
                CourseId = courseId,
                Name = $"{prefix}-{index + 1}",
                StudentsCount = 20
            }));

        private void AddLessonType(
            int id,
            string code,
            string name,
            bool preferredFirstInWeek)
            => Db.LessonTypes.Add(new LessonTypeRef
            {
                Id = id,
                Code = code,
                Name = name,
                IsActive = true,
                RequiresRoom = true,
                RequiresTeacher = true,
                BlocksRoom = true,
                BlocksTeacher = true,
                CountInPlan = true,
                CountInLoad = true,
                PreferredFirstInWeek = preferredFirstInWeek
            });

        private void AddSlots(int courseId, int count)
            => AddSlots(courseId, [DayOfWeek.Monday], count);

        private void AddSlots(
            int courseId,
            IReadOnlyCollection<DayOfWeek> days,
            int count)
        {
            var starts = new[]
            {
                new TimeOnly(8, 0),
                new TimeOnly(9, 10),
                new TimeOnly(10, 20),
                new TimeOnly(11, 30)
            };
            var id = courseId + 600;
            foreach (var day in days.OrderBy(value => value))
            {
                for (var index = 0; index < count; index++)
                {
                    Db.TimeSlots.Add(new TimeSlot
                    {
                        Id = id++,
                        CourseId = courseId,
                        DayOfWeek = day,
                        Start = starts[index],
                        End = starts[index].AddHours(1),
                        SortOrder = index + 1,
                        IsActive = true
                    });
                }
            }
        }

        private void AddTeacherWorkingHours(IEnumerable<int> teacherIds)
        {
            foreach (var teacherId in teacherIds)
            {
                foreach (var day in Enumerable.Range(1, 5).Select(value => (DayOfWeek)value))
                {
                    Db.TeacherWorkingHours.Add(new TeacherWorkingHour
                    {
                        TeacherId = teacherId,
                        DayOfWeek = day,
                        Start = new TimeOnly(8, 0),
                        End = new TimeOnly(13, 0)
                    });
                }
            }
        }

        private void AddTeacherWorkingHours(int teacherId, IEnumerable<DayOfWeek> days)
        {
            foreach (var day in days)
            {
                Db.TeacherWorkingHours.Add(new TeacherWorkingHour
                {
                    TeacherId = teacherId,
                    DayOfWeek = day,
                    Start = new TimeOnly(8, 0),
                    End = new TimeOnly(12, 0)
                });
            }
        }

        private static DraftAutoGenRequest BuildRequest(
            int courseId,
            IReadOnlyCollection<int> groupIds,
            Dictionary<int, int> moduleHours,
            DraftAutoGenSoftOptions? softOptions = null,
            int? preferredFirstMaxSlotOrderOverride = null,
            List<GroupRoomPreferenceDto>? groupRoomPreferences = null,
            DateOnly? rangeEndDate = null,
            bool clearExisting = true)
            => new(
                WeekStart: FrozenSemesterStart,
                ClearExisting: clearExisting,
                CourseId: courseId,
                GroupIds: groupIds.ToList(),
                Days: WeekPreset.MonFri,
                ModuleHours: moduleHours,
                SoftFill: true,
                AllowIncompleteDrafts: false,
                RangeStartDate: FrozenSemesterStart,
                RangeEndDate: rangeEndDate ?? FrozenSemesterStart.AddDays(4),
                PreferredFirstMaxSlotOrderOverride: preferredFirstMaxSlotOrderOverride,
                GroupRoomPreferences: groupRoomPreferences,
                SoftOptions: softOptions ?? new DraftAutoGenSoftOptions(RecentRepeatWindowDays: 0));

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
