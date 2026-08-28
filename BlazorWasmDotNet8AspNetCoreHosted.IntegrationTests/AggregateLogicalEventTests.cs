using System.Data.Common;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Admin;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class AggregateLogicalEventTests
{
    private static readonly DateOnly Monday = new(2026, 9, 7);

    [Fact]
    public async Task Plan_aggregation_collapses_batch_coteachers_and_topics_to_one_hour()
    {
        await using var fixture = await AggregateTestDatabase.CreateAsync();
        var model = await fixture.SeedAsync();
        foreach (var topicId in new[] { model.FirstTopicId, model.SecondTopicId })
        {
            foreach (var teacherId in new[] { model.FirstTeacherId, model.SecondTeacherId })
            {
                fixture.Db.ScheduleItems.Add(CreateScheduleItem(
                    model,
                    topicId,
                    teacherId,
                    "batch-plan-event",
                    Monday,
                    new TimeOnly(8, 0),
                    new TimeOnly(9, 0)));
            }
        }
        await fixture.Db.SaveChangesAsync();

        await new AggregatesService(fixture.Db).RecalcAsync(
            new[] { (model.CourseId, model.ModuleId) },
            Array.Empty<(int TeacherId, int CourseId)>());
        fixture.Db.ChangeTracker.Clear();

        var storedPlan = await fixture.Db.ModulePlans.AsNoTracking().SingleAsync();
        Assert.Equal(1, storedPlan.ScheduledHours);
        var action = await new AdminPlansController(fixture.Db)
            .GetByModule(model.ModuleId, model.CourseId);
        var response = Assert.IsType<OkObjectResult>(action.Result);
        var planDto = Assert.Single(Assert.IsType<List<CourseModulePlanDto>>(response.Value));
        Assert.Equal(1, planDto.ScheduledHours);
    }

    [Fact]
    public async Task Plan_aggregation_collapses_legacy_variants_but_keeps_identical_rows_distinct()
    {
        await using var fixture = await AggregateTestDatabase.CreateAsync();
        var model = await fixture.SeedAsync();
        fixture.Db.ScheduleItems.AddRange(
            CreateScheduleItem(
                model,
                model.FirstTopicId,
                model.FirstTeacherId,
                null,
                Monday,
                new TimeOnly(8, 0),
                new TimeOnly(9, 0)),
            CreateScheduleItem(
                model,
                model.SecondTopicId,
                model.SecondTeacherId,
                null,
                Monday,
                new TimeOnly(8, 0),
                new TimeOnly(9, 0)),
            CreateScheduleItem(
                model,
                model.FirstTopicId,
                model.FirstTeacherId,
                null,
                Monday,
                new TimeOnly(10, 0),
                new TimeOnly(11, 0)),
            CreateScheduleItem(
                model,
                model.FirstTopicId,
                model.FirstTeacherId,
                null,
                Monday,
                new TimeOnly(10, 0),
                new TimeOnly(11, 0)));
        await fixture.Db.SaveChangesAsync();

        await new AggregatesService(fixture.Db).RecalcAsync(
            new[] { (model.CourseId, model.ModuleId) },
            Array.Empty<(int TeacherId, int CourseId)>());
        fixture.Db.ChangeTracker.Clear();

        var storedPlan = await fixture.Db.ModulePlans.AsNoTracking().SingleAsync();
        Assert.Equal(3, storedPlan.ScheduledHours);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Teacher_load_aggregation_counts_each_teacher_once_per_explicit_and_legacy_logical_event(
        bool withBatchKey,
        bool scopedRecalculation)
    {
        await using var fixture = await AggregateTestDatabase.CreateAsync();
        var model = await fixture.SeedAsync();
        var batchKey = withBatchKey ? "batch-load-event" : null;
        foreach (var topicId in new[] { model.FirstTopicId, model.SecondTopicId })
        {
            foreach (var teacherId in new[] { model.FirstTeacherId, model.SecondTeacherId })
            {
                fixture.Db.ScheduleItems.Add(CreateScheduleItem(
                    model,
                    topicId,
                    teacherId,
                    batchKey,
                    Monday,
                    new TimeOnly(8, 0),
                    new TimeOnly(9, 0)));
            }
        }
        fixture.Db.TeacherCourseLoads.AddRange(
            new TeacherCourseLoad
            {
                TeacherId = model.FirstTeacherId,
                CourseId = model.CourseId,
                ScheduledHours = 99,
                IsActive = true
            },
            new TeacherCourseLoad
            {
                TeacherId = model.SecondTeacherId,
                CourseId = model.CourseId,
                ScheduledHours = 99,
                IsActive = true
            });
        await fixture.Db.SaveChangesAsync();

        await new AggregatesService(fixture.Db).RecalcAsync(
            Array.Empty<(int CourseId, int ModuleId)>(),
            scopedRecalculation
                ? new[]
                {
                    (model.FirstTeacherId, model.CourseId),
                    (model.SecondTeacherId, model.CourseId)
                }
                : null);
        fixture.Db.ChangeTracker.Clear();

        var loads = await fixture.Db.TeacherCourseLoads
            .AsNoTracking()
            .OrderBy(load => load.TeacherId)
            .Select(load => load.ScheduledHours)
            .ToListAsync();
        Assert.Equal(new[] { 1, 1 }, loads);
    }

    [Fact]
    public async Task Scoped_recalculation_reads_and_updates_only_requested_exact_pairs()
    {
        var mutationCounter = new AggregateMutationCountingInterceptor();
        var commandCounter = new AggregateCommandCountingInterceptor();
        await using var fixture = await AggregateTestDatabase.CreateAsync(mutationCounter, commandCounter);

        var firstCourse = new Course { Name = "Перший курс точної області", DurationWeeks = 52 };
        var secondCourse = new Course { Name = "Другий курс точної області", DurationWeeks = 52 };
        var firstGroup = new Group
        {
            Name = "Перша група точної області",
            StudentsCount = 20,
            Course = firstCourse
        };
        var secondGroup = new Group
        {
            Name = "Друга група точної області",
            StudentsCount = 20,
            Course = secondCourse
        };
        var firstModule = new Module
        {
            Code = "EXACT-1",
            Title = "Перший модуль точної області",
            Credits = 1,
            Course = firstCourse
        };
        var secondModule = new Module
        {
            Code = "EXACT-2",
            Title = "Другий модуль точної області",
            Credits = 1,
            Course = secondCourse
        };
        var firstTeacher = new Teacher { FullName = "Перший викладач точної області" };
        var secondTeacher = new Teacher { FullName = "Другий викладач точної області" };
        var lessonType = new LessonTypeRef
        {
            Code = "EXACT",
            Name = "Заняття точної області",
            RequiresRoom = false,
            RequiresTeacher = true,
            BlocksRoom = false,
            BlocksTeacher = true,
            CountInPlan = true,
            CountInLoad = true
        };
        fixture.Db.AddRange(
            firstCourse,
            secondCourse,
            firstGroup,
            secondGroup,
            firstModule,
            secondModule,
            firstTeacher,
            secondTeacher,
            lessonType);
        await fixture.Db.SaveChangesAsync();

        fixture.Db.ModuleCourses.AddRange(
            new ModuleCourse { ModuleId = firstModule.Id, CourseId = secondCourse.Id },
            new ModuleCourse { ModuleId = secondModule.Id, CourseId = firstCourse.Id });
        foreach (var (courseId, moduleId) in new[]
                 {
                     (firstCourse.Id, firstModule.Id),
                     (firstCourse.Id, secondModule.Id),
                     (secondCourse.Id, firstModule.Id),
                     (secondCourse.Id, secondModule.Id)
                 })
        {
            fixture.Db.ModulePlans.Add(new ModulePlan
            {
                CourseId = courseId,
                ModuleId = moduleId,
                TargetHours = 30,
                ScheduledHours = 99,
                IsActive = true
            });
        }
        foreach (var (teacherId, courseId) in new[]
                 {
                     (firstTeacher.Id, firstCourse.Id),
                     (firstTeacher.Id, secondCourse.Id),
                     (secondTeacher.Id, firstCourse.Id),
                     (secondTeacher.Id, secondCourse.Id)
                 })
        {
            fixture.Db.TeacherCourseLoads.AddRange(
                new TeacherCourseLoad
                {
                    TeacherId = teacherId,
                    CourseId = courseId,
                    ScheduledHours = 99,
                    IsActive = true
                },
                new TeacherCourseLoad
                {
                    TeacherId = teacherId,
                    CourseId = courseId,
                    ScheduledHours = 99,
                    IsActive = false
                });
        }
        fixture.Db.ScheduleItems.AddRange(
            CreateExactScopeScheduleItem(firstGroup.Id, firstModule.Id, firstTeacher.Id, lessonType.Id, 0),
            CreateExactScopeScheduleItem(firstGroup.Id, secondModule.Id, secondTeacher.Id, lessonType.Id, 1),
            CreateExactScopeScheduleItem(secondGroup.Id, firstModule.Id, firstTeacher.Id, lessonType.Id, 2),
            CreateExactScopeScheduleItem(secondGroup.Id, secondModule.Id, secondTeacher.Id, lessonType.Id, 3));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        mutationCounter.Reset();
        commandCounter.Reset();

        var planKeys = new[]
        {
            (CourseId: firstCourse.Id, ModuleId: firstModule.Id),
            (CourseId: secondCourse.Id, ModuleId: secondModule.Id)
        };
        var loadKeys = new[]
        {
            (TeacherId: firstTeacher.Id, CourseId: firstCourse.Id),
            (TeacherId: secondTeacher.Id, CourseId: secondCourse.Id)
        };
        var baselineCourseIds = planKeys.Select(key => key.CourseId).Distinct().ToList();
        var baselineModuleIds = planKeys.Select(key => key.ModuleId).Distinct().ToList();
        _ = await fixture.Db.ScheduleItems
            .AsNoTracking()
            .Where(item => baselineCourseIds.Contains(item.Group.CourseId)
                           && baselineModuleIds.Contains(item.ModuleId))
            .Select(item => item.Id)
            .ToListAsync();
        var baselineTeacherIds = loadKeys.Select(key => key.TeacherId).Distinct().ToList();
        var baselineLoadCourseIds = loadKeys.Select(key => key.CourseId).Distinct().ToList();
        _ = await fixture.Db.ScheduleItems
            .AsNoTracking()
            .Where(item => item.TeacherId != null
                           && baselineTeacherIds.Contains(item.TeacherId.Value)
                           && baselineLoadCourseIds.Contains(item.Group.CourseId))
            .Select(item => item.Id)
            .ToListAsync();
        Assert.Equal(2, commandCounter.ScheduleSelectCount);
        Assert.Equal(8, commandCounter.ScheduleMaterializedRowCount);
        commandCounter.Reset();

        await new AggregatesService(fixture.Db).RecalcAsync(
            planKeys,
            loadKeys);
        fixture.Db.ChangeTracker.Clear();

        Assert.Equal(2, commandCounter.ScheduleSelectCount);
        Assert.Equal(4, commandCounter.ScheduleMaterializedRowCount);
        Assert.Equal(2, mutationCounter.ModifiedModulePlanCount);
        Assert.Equal(2, mutationCounter.ModifiedActiveTeacherCourseLoadCount);
        Assert.Equal(2, commandCounter.InactiveTeacherCourseLoadResetCount);
        var plans = (await fixture.Db.ModulePlans.AsNoTracking().ToListAsync())
            .ToDictionary(plan => (plan.CourseId, plan.ModuleId), plan => plan.ScheduledHours);
        Assert.Equal(1, plans[(firstCourse.Id, firstModule.Id)]);
        Assert.Equal(1, plans[(secondCourse.Id, secondModule.Id)]);
        Assert.Equal(99, plans[(firstCourse.Id, secondModule.Id)]);
        Assert.Equal(99, plans[(secondCourse.Id, firstModule.Id)]);
        var loadRows = await fixture.Db.TeacherCourseLoads.AsNoTracking().ToListAsync();
        var activeLoads = loadRows
            .Where(load => load.IsActive)
            .ToDictionary(load => (load.TeacherId, load.CourseId), load => load.ScheduledHours);
        Assert.Equal(1, activeLoads[(firstTeacher.Id, firstCourse.Id)]);
        Assert.Equal(1, activeLoads[(secondTeacher.Id, secondCourse.Id)]);
        Assert.Equal(99, activeLoads[(firstTeacher.Id, secondCourse.Id)]);
        Assert.Equal(99, activeLoads[(secondTeacher.Id, firstCourse.Id)]);
        var inactiveLoads = loadRows
            .Where(load => !load.IsActive)
            .ToDictionary(load => (load.TeacherId, load.CourseId), load => load.ScheduledHours);
        Assert.Equal(0, inactiveLoads[(firstTeacher.Id, firstCourse.Id)]);
        Assert.Equal(0, inactiveLoads[(secondTeacher.Id, secondCourse.Id)]);
        Assert.Equal(99, inactiveLoads[(firstTeacher.Id, secondCourse.Id)]);
        Assert.Equal(99, inactiveLoads[(secondTeacher.Id, firstCourse.Id)]);
    }

    [Fact]
    public async Task Topic_statistics_collapse_coteachers_per_topic_and_preserve_different_topics()
    {
        await using var fixture = await AggregateTestDatabase.CreateAsync();
        var model = await fixture.SeedAsync();
        foreach (var topicId in new[] { model.FirstTopicId, model.SecondTopicId })
        {
            foreach (var teacherId in new[] { model.FirstTeacherId, model.SecondTeacherId })
            {
                fixture.Db.TeacherDraftItems.Add(CreateDraftItem(
                    model,
                    topicId,
                    teacherId,
                    "batch-planned-topic"));
                fixture.Db.ScheduleItems.Add(CreateScheduleItem(
                    model,
                    topicId,
                    teacherId,
                    "batch-completed-topic",
                    Monday,
                    new TimeOnly(8, 0),
                    new TimeOnly(9, 0)));
            }
        }
        await fixture.Db.SaveChangesAsync();

        var action = await new AdminModulesController(fixture.Db).GetTopics(model.ModuleId);
        var response = Assert.IsType<OkObjectResult>(action.Result);
        var topics = Assert.IsType<List<ModuleTopicViewDto>>(response.Value);

        Assert.Equal(2, topics.Count);
        foreach (var topic in topics)
        {
            var planned = Assert.Single(topic.PlannedGroupsHours!);
            Assert.Equal(1, planned.AuditoriumHours);
            Assert.Equal(0, planned.SelfStudyHours);
            var completed = Assert.Single(topic.CompletedGroupsHours!);
            Assert.Equal(1, completed.AuditoriumHours);
            Assert.Equal(0, completed.SelfStudyHours);
        }
    }

    [Fact]
    public async Task Ensure_course_scope_recalculates_merged_target_plan_hours()
    {
        await using var fixture = await AggregateTestDatabase.CreateAsync();
        var sourceCourse = new Course { Name = "Курс-джерело агрегату", DurationWeeks = 52 };
        var targetCourse = new Course { Name = "Курс-приймач агрегату", DurationWeeks = 52 };
        var targetGroup = new Group
        {
            Name = "Група приймача агрегату",
            StudentsCount = 20,
            Course = targetCourse
        };
        var sourceModule = new Module
        {
            Code = "AGG-SCOPE",
            Title = "Спільний модуль агрегату",
            Credits = 1,
            Course = sourceCourse
        };
        var targetModule = new Module
        {
            Code = "agg-scope",
            Title = "Цільовий модуль агрегату",
            Credits = 1,
            Course = targetCourse
        };
        var lessonType = new LessonTypeRef
        {
            Code = "AGG",
            Name = "Заняття агрегату",
            RequiresRoom = false,
            RequiresTeacher = false,
            BlocksRoom = false,
            BlocksTeacher = false,
            CountInPlan = true,
            CountInLoad = false
        };
        fixture.Db.AddRange(
            sourceCourse,
            targetCourse,
            targetGroup,
            sourceModule,
            targetModule,
            lessonType);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ModuleCourses.Add(new ModuleCourse
        {
            ModuleId = sourceModule.Id,
            CourseId = targetCourse.Id
        });
        fixture.Db.ModulePlans.AddRange(
            new ModulePlan
            {
                CourseId = targetCourse.Id,
                ModuleId = sourceModule.Id,
                TargetHours = 30,
                ScheduledHours = 1,
                IsActive = true
            },
            new ModulePlan
            {
                CourseId = targetCourse.Id,
                ModuleId = targetModule.Id,
                TargetHours = 30,
                ScheduledHours = 1,
                IsActive = true
            });
        fixture.Db.ScheduleItems.AddRange(
            new ScheduleItem
            {
                Date = Monday,
                DayOfWeek = Monday.DayOfWeek,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(9, 0),
                GroupId = targetGroup.Id,
                ModuleId = sourceModule.Id,
                LessonTypeId = lessonType.Id
            },
            new ScheduleItem
            {
                Date = Monday.AddDays(1),
                DayOfWeek = Monday.AddDays(1).DayOfWeek,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(9, 0),
                GroupId = targetGroup.Id,
                ModuleId = targetModule.Id,
                LessonTypeId = lessonType.Id
            });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var action = await new AdminModulesController(fixture.Db)
            .EnsureCourseScope(sourceModule.Id, targetCourse.Id);

        var response = Assert.IsType<OkObjectResult>(action.Result);
        Assert.Equal(targetModule.Id, Assert.IsType<int>(response.Value));
        fixture.Db.ChangeTracker.Clear();
        var targetPlan = await fixture.Db.ModulePlans.AsNoTracking()
            .SingleAsync(plan => plan.CourseId == targetCourse.Id && plan.ModuleId == targetModule.Id);
        Assert.Equal(2, targetPlan.ScheduledHours);
        Assert.False(await fixture.Db.ModulePlans.AsNoTracking().AnyAsync(plan =>
            plan.CourseId == targetCourse.Id && plan.ModuleId == sourceModule.Id));
        Assert.Equal(2, await fixture.Db.ScheduleItems.AsNoTracking().CountAsync(item =>
            item.GroupId == targetGroup.Id && item.ModuleId == targetModule.Id));
        Assert.False(await fixture.Db.ScheduleItems.AsNoTracking().AnyAsync(item =>
            item.GroupId == targetGroup.Id && item.ModuleId == sourceModule.Id));
    }

    private static ScheduleItem CreateScheduleItem(
        AggregateModel model,
        int topicId,
        int teacherId,
        string? batchKey,
        DateOnly date,
        TimeOnly start,
        TimeOnly end)
        => new()
        {
            Date = date,
            DayOfWeek = date.DayOfWeek,
            StartTime = start,
            EndTime = end,
            GroupId = model.GroupId,
            ModuleId = model.ModuleId,
            ModuleTopicId = topicId,
            LessonTypeId = model.LessonTypeId,
            TeacherId = teacherId,
            BatchKey = batchKey
        };

    private static TeacherDraftItem CreateDraftItem(
        AggregateModel model,
        int topicId,
        int teacherId,
        string batchKey)
        => new()
        {
            Date = Monday,
            DayOfWeek = Monday.DayOfWeek,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(9, 0),
            GroupId = model.GroupId,
            ModuleId = model.ModuleId,
            ModuleTopicId = topicId,
            LessonTypeId = model.LessonTypeId,
            TeacherId = teacherId,
            BatchKey = batchKey
        };

    private static ScheduleItem CreateExactScopeScheduleItem(
        int groupId,
        int moduleId,
        int teacherId,
        int lessonTypeId,
        int dayOffset)
        => new()
        {
            Date = Monday.AddDays(dayOffset),
            DayOfWeek = Monday.AddDays(dayOffset).DayOfWeek,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(9, 0),
            GroupId = groupId,
            ModuleId = moduleId,
            TeacherId = teacherId,
            LessonTypeId = lessonTypeId
        };

    private sealed record AggregateModel(
        int CourseId,
        int GroupId,
        int ModuleId,
        int LessonTypeId,
        int FirstTopicId,
        int SecondTopicId,
        int FirstTeacherId,
        int SecondTeacherId);

    private sealed class AggregateTestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private AggregateTestDatabase(SqliteConnection connection, AppDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public AppDbContext Db { get; }

        public static async Task<AggregateTestDatabase> CreateAsync(params IInterceptor[] interceptors)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection);
            if (interceptors.Length > 0)
            {
                optionsBuilder.AddInterceptors(interceptors);
            }
            var options = optionsBuilder.Options;
            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new AggregateTestDatabase(connection, db);
        }

        public async Task<AggregateModel> SeedAsync()
        {
            var course = new Course { Name = "Курс агрегату", DurationWeeks = 52 };
            var group = new Group
            {
                Name = "Група агрегату",
                StudentsCount = 20,
                Course = course
            };
            var module = new Module
            {
                Code = "AGG-1",
                Title = "Модуль агрегату",
                Credits = 1,
                Course = course
            };
            var lessonType = new LessonTypeRef
            {
                Code = "AGG",
                Name = "Заняття агрегату",
                RequiresRoom = false,
                RequiresTeacher = false,
                BlocksRoom = false,
                BlocksTeacher = true,
                CountInPlan = true,
                CountInLoad = true
            };
            var firstTeacher = new Teacher { FullName = "Перший співвикладач агрегату" };
            var secondTeacher = new Teacher { FullName = "Другий співвикладач агрегату" };
            Db.AddRange(course, group, module, lessonType, firstTeacher, secondTeacher);
            await Db.SaveChangesAsync();
            var firstTopic = new ModuleTopic
            {
                ModuleId = module.Id,
                Order = 1,
                TopicCode = "1.1",
                LessonTypeId = lessonType.Id,
                TotalHours = 1,
                AuditoriumHours = 1
            };
            var secondTopic = new ModuleTopic
            {
                ModuleId = module.Id,
                Order = 2,
                TopicCode = "1.2",
                LessonTypeId = lessonType.Id,
                TotalHours = 1,
                AuditoriumHours = 1
            };
            Db.ModuleTopics.AddRange(firstTopic, secondTopic);
            Db.ModulePlans.Add(new ModulePlan
            {
                CourseId = course.Id,
                ModuleId = module.Id,
                TargetHours = 30,
                ScheduledHours = 99,
                IsActive = true
            });
            await Db.SaveChangesAsync();

            return new AggregateModel(
                course.Id,
                group.Id,
                module.Id,
                lessonType.Id,
                firstTopic.Id,
                secondTopic.Id,
                firstTeacher.Id,
                secondTeacher.Id);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class AggregateMutationCountingInterceptor : SaveChangesInterceptor
    {
        public int ModifiedModulePlanCount { get; private set; }
        public int ModifiedActiveTeacherCourseLoadCount { get; private set; }

        public void Reset()
        {
            ModifiedModulePlanCount = 0;
            ModifiedActiveTeacherCourseLoadCount = 0;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is { } context)
            {
                ModifiedModulePlanCount += context.ChangeTracker
                    .Entries<ModulePlan>()
                    .Count(entry => entry.State == EntityState.Modified);
                ModifiedActiveTeacherCourseLoadCount += context.ChangeTracker
                    .Entries<TeacherCourseLoad>()
                    .Count(entry => entry.State == EntityState.Modified && entry.Entity.IsActive);
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class AggregateCommandCountingInterceptor : DbCommandInterceptor
    {
        public int ScheduleSelectCount { get; private set; }
        public int ScheduleReadCount { get; private set; }
        public int InactiveTeacherCourseLoadResetCount { get; private set; }
        public int ScheduleMaterializedRowCount => ScheduleReadCount - ScheduleSelectCount;

        public void Reset()
        {
            ScheduleSelectCount = 0;
            ScheduleReadCount = 0;
            InactiveTeacherCourseLoadResetCount = 0;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("ScheduleItems", StringComparison.Ordinal))
            {
                ScheduleSelectCount++;
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult DataReaderDisposing(
            DbCommand command,
            DataReaderDisposingEventData eventData,
            InterceptionResult result)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("ScheduleItems", StringComparison.Ordinal))
            {
                ScheduleReadCount += eventData.ReadCount;
            }

            return base.DataReaderDisposing(command, eventData, result);
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("TeacherCourseLoads", StringComparison.Ordinal))
            {
                InactiveTeacherCourseLoadResetCount += result;
            }

            return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }
    }
}
