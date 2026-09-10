using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using System.Buffers.Binary;
using System.Data.Common;
using System.Globalization;
using System.IO.Compression;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

internal sealed class CancelOriginalTokenOnCommitInterceptor(CancellationTokenSource source)
    : DbTransactionInterceptor
{
    private bool armed;

    public bool CommitObserved { get; private set; }
    public bool CommitTokenCanBeCanceled { get; private set; }
    public bool RollbackAttemptedAfterCommitStarted { get; private set; }

    public void Arm() => armed = true;

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        if (armed)
        {
            armed = false;
            CommitObserved = true;
            CommitTokenCanBeCanceled = cancellationToken.CanBeCanceled;
            source.Cancel();
        }
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult> TransactionRollingBackAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        if (CommitObserved)
        {
            RollbackAttemptedAfterCommitStarted = true;
        }
        return ValueTask.FromResult(result);
    }
}

internal sealed class InsertAmbiguousCourseBeforeTransactionInterceptor : DbTransactionInterceptor
{
    private int _armed;

    public void Arm() => Volatile.Write(ref _armed, 1);

    public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection,
        TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _armed, 0) == 0)
        {
            return result;
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO \"Courses\" (\"Name\", \"DurationWeeks\") VALUES (@name, @durationWeeks);";
        var name = command.CreateParameter();
        name.ParameterName = "@name";
        name.Value = "КН-1";
        command.Parameters.Add(name);
        var durationWeeks = command.CreateParameter();
        durationWeeks.ParameterName = "@durationWeeks";
        durationWeeks.Value = 52;
        command.Parameters.Add(durationWeeks);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return result;
    }
}

internal sealed class InsertRunningAutogenJobBeforeTransactionInterceptor : DbTransactionInterceptor
{
    public const string JobId = "docx-racing-autogen-job";
    private int _armed;

    public void Arm() => Volatile.Write(ref _armed, 1);

    public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection,
        TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _armed, 0) == 0)
        {
            return result;
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var injectionDb = new AppDbContext(options);
        var nowUtc = DateTime.UtcNow;
        injectionDb.AutoGenJobRuns.Add(new AutoGenJobRun
        {
            JobId = JobId,
            ClientPartitionKey = "docx-tests",
            RequestHash = "docx-racing-autogen-hash",
            OwnerInstanceId = "docx-racing-owner",
            Attempt = 1,
            LeaseExpiresAtUtc = nowUtc.AddMinutes(5),
            Version = 1,
            Kind = (int)AutoGenJobKind.Generate,
            State = (int)AutoGenJobState.Running,
            Title = "Автогенерація, що почалася перед транзакцією DOCX",
            CurrentStage = "Виконується",
            CreatedAtUtc = nowUtc,
            StartedAtUtc = nowUtc,
            RangeStartDate = new DateOnly(2026, 9, 7),
            RangeEndDate = new DateOnly(2026, 9, 7),
            RequestJson = "{malformed",
            StatusJson = "{}",
            UpdatedAtUtc = nowUtc
        });
        await injectionDb.SaveChangesAsync(cancellationToken);
        return result;
    }
}

[CollectionDefinition(DocxImportTestCollection.Name, DisableParallelization = true)]
public sealed class DocxImportTestCollection
{
    public const string Name = "DOCX import tests";
}

[Collection(DocxImportTestCollection.Name)]
public sealed class DocxImportSecurityTests
{
    [Fact]
    public async Task Import_rejects_valid_docx_with_oversized_cell_before_database_matching()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        fixture.Db.Courses.Add(new Course { Name = "КН-1", DurationWeeks = 52 });
        await fixture.Db.SaveChangesAsync();
        var bytes = CreateModuleDocx(new string('А', 20_000));
        await using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", "КН-1.docx");

        var result = await new DocxImportService().ImportAsync(
            file,
            fixture.Db,
            apply: false,
            CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Contains("16384", result.Error, StringComparison.Ordinal);
        Assert.False(result.CourseFound);
        Assert.Empty(result.Modules);
    }

    [Fact]
    public async Task Import_preserves_normal_docx_behavior_with_bounded_parser()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var course = new Course { Name = "КН-1", DurationWeeks = 52 };
        fixture.Db.Courses.Add(course);
        await fixture.Db.SaveChangesAsync();
        var bytes = CreateModuleDocx("Тестовий модуль");
        await using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", "КН-1.docx");

        var result = await new DocxImportService().ImportAsync(
            file,
            fixture.Db,
            apply: false,
            CancellationToken.None);

        Assert.Null(result.Error);
        Assert.True(result.CourseFound);
        Assert.Equal(course.Id, result.CourseId);
        var module = Assert.Single(result.Modules);
        Assert.Equal("1", module.Code);
        Assert.Equal("Тестовий модуль", module.Title);
    }

    [Fact]
    public async Task Import_rejects_zip_with_excessive_entry_count_before_opening_opc_package()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var bytes = CreateZipWithEmptyEntries(2_049);

        var result = await ImportAsync(fixture, bytes, apply: false);

        Assert.NotNull(result.Error);
        Assert.Contains("ZIP-вміст", result.Error, StringComparison.Ordinal);
        Assert.Empty(result.Modules);
    }

    [Fact]
    public async Task Import_rejects_zip_with_excessive_declared_entry_size_before_opening_opc_package()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var bytes = CreateZipWithDeclaredUncompressedSizes(16 * 1024 * 1024 + 1);

        var result = await ImportAsync(fixture, bytes, apply: false);

        Assert.NotNull(result.Error);
        Assert.Contains("ZIP-вміст", result.Error, StringComparison.Ordinal);
        Assert.Empty(result.Modules);
    }

    [Fact]
    public async Task Import_rejects_zip_with_excessive_aggregate_declared_size_before_opening_opc_package()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var bytes = CreateZipWithDeclaredUncompressedSizes(
            14 * 1024 * 1024,
            14 * 1024 * 1024,
            14 * 1024 * 1024,
            14 * 1024 * 1024,
            14 * 1024 * 1024);

        var result = await ImportAsync(fixture, bytes, apply: false);

        Assert.NotNull(result.Error);
        Assert.Contains("ZIP-вміст", result.Error, StringComparison.Ordinal);
        Assert.Empty(result.Modules);
    }

    [Fact]
    public async Task Import_apply_uses_non_cancelable_commit_after_final_token_check()
    {
        using var cancellation = new CancellationTokenSource();
        var interceptor = new CancelOriginalTokenOnCommitInterceptor(cancellation);
        await using var fixture = await TestDatabase.CreateAsync(interceptor);
        await fixture.SeedCourseAsync();
        var bytes = CreateModuleDocx("Атомарно збережений модуль");
        interceptor.Arm();

        var result = await ImportAsync(fixture, bytes, apply: true, cancellation.Token);

        Assert.Null(result.Error);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(interceptor.CommitObserved);
        Assert.False(interceptor.CommitTokenCanBeCanceled);
        Assert.False(interceptor.RollbackAttemptedAfterCommitStarted);
        Assert.Single(await fixture.Db.Modules.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Import_apply_rejects_course_that_becomes_ambiguous_before_transaction()
    {
        var interceptor = new InsertAmbiguousCourseBeforeTransactionInterceptor();
        await using var fixture = await TestDatabase.CreateAsync(interceptor);
        var course = await fixture.SeedCourseAsync();
        var bytes = CreateModuleDocx("Модуль для перевірки гонки курсу");
        interceptor.Arm();

        var result = await ImportAsync(fixture, bytes, apply: true);

        Assert.NotNull(result.Error);
        Assert.Contains("кілька курсів", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(course.Id, result.CourseId);
        Assert.Empty(await fixture.Db.Modules.AsNoTracking().ToListAsync());
        Assert.Equal(2, await fixture.Db.Courses.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Import_rejects_excessive_semantic_module_count_before_database_mutation()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        fixture.Db.Courses.Add(new Course { Name = "КН-1", DurationWeeks = 52 });
        await fixture.Db.SaveChangesAsync();
        var bytes = CreateModuleDocx(Enumerable.Range(1, 501)
            .Select(index => (index.ToString(), $"Модуль {index}")));
        await using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", "КН-1.docx");

        var result = await new DocxImportService().ImportAsync(
            file,
            fixture.Db,
            apply: true,
            CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Contains("навчальних сутностей", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Db.Modules.ToListAsync());
    }

    [Fact]
    public async Task Import_accepts_structural_table_boundary_for_maximum_modules()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.SeedCourseAsync();
        var bytes = CreateCurriculumWithTopicTablePerModule(
            CurriculumInputLimits.ImportModuleCountMax);

        var result = await ImportAsync(fixture, bytes, apply: false);

        Assert.Null(result.Error);
        Assert.Equal(CurriculumInputLimits.ImportModuleCountMax, result.Modules.Count);
        Assert.Equal(
            CurriculumInputLimits.ImportModuleCountMax,
            result.Modules.Sum(module => module.Topics.Count));
    }

    [Fact]
    public async Task Import_preview_and_apply_return_same_operation_limit_error()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.SeedCourseAsync();
        fixture.Db.Buildings.AddRange(Enumerable.Range(1, 100)
            .Select(index => new Building { Name = $"Корпус {index}" }));
        await fixture.Db.SaveChangesAsync();
        var bytes = CreateModuleDocx(Enumerable.Range(1, CurriculumInputLimits.ImportModuleCountMax)
            .Select(index => (index.ToString(), $"Модуль {index}")));

        var preview = await ImportAsync(fixture, bytes, apply: false);
        var apply = await ImportAsync(fixture, bytes, apply: true);

        Assert.NotNull(preview.Error);
        Assert.Equal(preview.Error, apply.Error);
        Assert.Contains("операцій", preview.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Db.Modules.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Db.ModulePlans.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Import_preview_and_apply_reject_room_associations_above_shared_limit()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.SeedCourseAsync();
        var building = new Building { Name = "Корпус для граничних аудиторій" };
        fixture.Db.Rooms.AddRange(Enumerable.Range(
                1,
                CurriculumInputLimits.ModuleAssociationCountMax + 1)
            .Select(index => new Room
            {
                Name = $"Аудиторія {index}",
                Capacity = 30,
                Building = building
            }));
        await fixture.Db.SaveChangesAsync();
        var bytes = CreateModuleDocx("Модуль із надмірними зв'язками");

        var preview = await ImportAsync(fixture, bytes, apply: false);
        var apply = await ImportAsync(fixture, bytes, apply: true);

        Assert.NotNull(preview.Error);
        Assert.Equal(preview.Error, apply.Error);
        Assert.Contains(
            CurriculumInputLimits.ModuleAssociationCountMax.ToString(CultureInfo.InvariantCulture),
            preview.Error,
            StringComparison.Ordinal);
        Assert.Empty(await fixture.Db.Modules.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Db.ModuleRooms.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Import_preview_and_apply_reject_conflicting_duplicate_module_codes()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.SeedCourseAsync();
        var bytes = CreateModuleDocx(new[]
        {
            ("1", "Перша назва", "1"),
            ("1", "Конфліктна назва", "2")
        });

        var preview = await ImportAsync(fixture, bytes, apply: false);
        var apply = await ImportAsync(fixture, bytes, apply: true);

        Assert.NotNull(preview.Error);
        Assert.Equal(preview.Error, apply.Error);
        Assert.Contains("повторюється", preview.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Db.Modules.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Db.ModulePlans.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_returns_structured_error_for_hour_value_above_safe_range(bool apply)
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.SeedCourseAsync();
        var bytes = CreateCurriculumDocx(
            "1",
            "Безпечний модуль",
            "1",
            new TopicRow("1.1.1 Тема", "Лекція", "2147483648", "1", "0"));

        var result = await ImportAsync(fixture, bytes, apply);

        Assert.NotNull(result.Error);
        Assert.Contains("діапазон", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Db.Modules.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Db.ModuleTopics.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Import_preview_and_apply_reject_negative_fractional_hours_before_rounding()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.SeedCourseAsync();
        var bytes = CreateCurriculumDocx(
            "1",
            "Модуль із від'ємними дробовими годинами",
            "1",
            new TopicRow("1.1.1 Тема", "Лекція", "-0.4", "0", "0"));

        var preview = await ImportAsync(fixture, bytes, apply: false);
        var apply = await ImportAsync(fixture, bytes, apply: true);

        Assert.NotNull(preview.Error);
        Assert.Equal(preview.Error, apply.Error);
        Assert.Contains("діапазон", preview.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Db.Modules.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Db.ModuleTopics.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Import_preview_and_apply_return_same_error_for_invalid_topic_semantics()
    {
        var bytes = CreateCurriculumDocx(
            "1",
            "Модуль із помилкою",
            "1",
            new TopicRow("1.1.1 Тема", "Лекція", "1", "2", "0"));
        await using var previewFixture = await TestDatabase.CreateAsync();
        await previewFixture.SeedCourseAsync();
        await using var applyFixture = await TestDatabase.CreateAsync();
        await applyFixture.SeedCourseAsync();

        var preview = await ImportAsync(previewFixture, bytes, apply: false);
        var apply = await ImportAsync(applyFixture, bytes, apply: true);

        Assert.NotNull(preview.Error);
        Assert.Equal(preview.Error, apply.Error);
        Assert.Contains("розподіл годин", preview.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await applyFixture.Db.Modules.AsNoTracking().ToListAsync());
        Assert.Empty(await applyFixture.Db.LessonTypes.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Import_preview_and_apply_preserve_same_valid_curriculum_semantics()
    {
        var bytes = CreateCurriculumDocx(
            "1",
            "Валідний модуль",
            "1",
            new TopicRow("1.1.1 Тема", "Лекція", "2", "2", "0"));
        await using var previewFixture = await TestDatabase.CreateAsync();
        await previewFixture.SeedCourseAsync();
        await using var applyFixture = await TestDatabase.CreateAsync();
        await applyFixture.SeedCourseAsync();

        var preview = await ImportAsync(previewFixture, bytes, apply: false);
        var apply = await ImportAsync(applyFixture, bytes, apply: true);

        Assert.Null(preview.Error);
        Assert.Null(apply.Error);
        var previewModule = Assert.Single(preview.Modules);
        var appliedModule = Assert.Single(apply.Modules);
        Assert.Equal(previewModule.Code, appliedModule.Code);
        Assert.Equal(previewModule.Title, appliedModule.Title);
        Assert.Equal(previewModule.Credits, appliedModule.Credits);
        Assert.Equal(previewModule.Topics, appliedModule.Topics);
        Assert.Single(await applyFixture.Db.Modules.AsNoTracking().ToListAsync());
        Assert.Single(await applyFixture.Db.ModuleTopics.AsNoTracking().ToListAsync());
        Assert.Single(await applyFixture.Db.LessonTypes.AsNoTracking().ToListAsync());
        Assert.Single(await applyFixture.Db.ModulePlans.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Import_rejects_out_of_range_credits_before_updating_existing_module()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var course = await fixture.SeedCourseAsync();
        var existing = new Module { Code = "1", Title = "Початкова назва", Credits = 1m, CourseId = course.Id };
        fixture.Db.Modules.Add(existing);
        await fixture.Db.SaveChangesAsync();
        var bytes = CreateModuleDocx("1", "Змінена назва", "9999.99");

        var result = await ImportAsync(fixture, bytes, apply: true);

        Assert.NotNull(result.Error);
        fixture.Db.ChangeTracker.Clear();
        var persisted = await fixture.Db.Modules.AsNoTracking().SingleAsync();
        Assert.Equal("Початкова назва", persisted.Title);
        Assert.Equal(1m, persisted.Credits);
        Assert.Empty(await fixture.Db.ModulePlans.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Import_accepts_maximum_credit_that_maps_to_supported_plan_hours()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.SeedCourseAsync();
        var bytes = CreateModuleDocx(
            "1",
            "Граничний модуль",
            CurriculumInputLimits.ModuleCreditsMax.ToString(CultureInfo.InvariantCulture));

        var result = await ImportAsync(fixture, bytes, apply: true);

        Assert.Null(result.Error);
        var module = await fixture.Db.Modules.AsNoTracking().SingleAsync();
        Assert.Equal(CurriculumInputLimits.ModuleCreditsMax, module.Credits);
        var plan = await fixture.Db.ModulePlans.AsNoTracking().SingleAsync();
        Assert.Equal(CurriculumInputLimits.PlanHoursMax, plan.TargetHours);
    }

    [Fact]
    public async Task Import_preview_and_apply_reject_credit_with_more_than_database_scale()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.SeedCourseAsync();
        var bytes = CreateModuleDocx(
            "1",
            "Модуль із надмірною точністю кредитів",
            "1.015");

        var preview = await ImportAsync(fixture, bytes, apply: false);
        var apply = await ImportAsync(fixture, bytes, apply: true);

        Assert.NotNull(preview.Error);
        Assert.Equal(preview.Error, apply.Error);
        Assert.Contains("2 знаків", preview.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Db.Modules.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Db.ModulePlans.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Import_preview_and_apply_return_structured_error_for_hostile_numeric_header()
    {
        var oversizedNumber = new string('9', 100);
        var bytes = CreateCurriculumDocx(
            "1",
            "Модуль із ворожим заголовком",
            "1",
            new[] { oversizedNumber, "2", "3", "4", "5", "6" },
            new TopicRow("1.1.1 Тема", "Лекція", "1", "1", "0"));
        await using var previewFixture = await TestDatabase.CreateAsync();
        await previewFixture.SeedCourseAsync();
        await using var applyFixture = await TestDatabase.CreateAsync();
        await applyFixture.SeedCourseAsync();

        var preview = await ImportAsync(previewFixture, bytes, apply: false);
        var apply = await ImportAsync(applyFixture, bytes, apply: true);

        Assert.NotNull(preview.Error);
        Assert.Equal(preview.Error, apply.Error);
        Assert.Contains("номер колонки", preview.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await applyFixture.Db.Modules.AsNoTracking().ToListAsync());
        Assert.Empty(await applyFixture.Db.ModulePlans.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Import_rejects_distinct_lesson_type_names_that_generate_same_code()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.SeedCourseAsync();
        var bytes = CreateCurriculumDocx(
            "1",
            "Колізійний модуль",
            "1",
            new TopicRow("1.1.1 Перша", "A B", "1", "1", "0"),
            new TopicRow("1.1.2 Друга", "A_B", "1", "1", "0"));

        var result = await ImportAsync(fixture, bytes, apply: true);

        Assert.NotNull(result.Error);
        Assert.Contains("однаковий код", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Db.Modules.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Db.LessonTypes.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Import_rejects_generated_lesson_type_code_collision_with_database_in_preview_and_apply()
    {
        var bytes = CreateCurriculumDocx(
            "1",
            "Колізійний модуль",
            "1",
            new TopicRow("1.1.1 Тема", "A B", "1", "1", "0"));
        await using var previewFixture = await TestDatabase.CreateAsync();
        await previewFixture.SeedCourseWithLessonTypeAsync("A_B", "Інша назва");
        await using var applyFixture = await TestDatabase.CreateAsync();
        await applyFixture.SeedCourseWithLessonTypeAsync("A_B", "Інша назва");

        var preview = await ImportAsync(previewFixture, bytes, apply: false);
        var apply = await ImportAsync(applyFixture, bytes, apply: true);

        Assert.NotNull(preview.Error);
        Assert.Equal(preview.Error, apply.Error);
        Assert.Contains("використовується", preview.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await applyFixture.Db.Modules.AsNoTracking().ToListAsync());
        Assert.Single(await applyFixture.Db.LessonTypes.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Import_preview_and_apply_reject_same_unsafe_lesson_type_merge_semantics()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.SeedCourseAsync();
        fixture.Db.LessonTypes.AddRange(
            new LessonTypeRef
            {
                Code = "REPEATED",
                Name = "Семінар Семінар",
                RequiresRoom = true
            },
            new LessonTypeRef
            {
                Code = "CANONICAL",
                Name = "Семінар",
                RequiresRoom = false
            });
        await fixture.Db.SaveChangesAsync();
        var bytes = CreateCurriculumDocx(
            "1",
            "Модуль із несумісним дублікатом",
            "1",
            new TopicRow("1.1.1 Перша", "Семінар Семінар", "1", "1", "0"),
            new TopicRow("1.1.2 Друга", "Семінар", "1", "1", "0"));

        var preview = await ImportAsync(fixture, bytes, apply: false);
        var apply = await ImportAsync(fixture, bytes, apply: true);

        Assert.NotNull(preview.Error);
        Assert.Equal(preview.Error, apply.Error);
        Assert.Contains("різні правила", preview.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Db.Modules.AsNoTracking().ToListAsync());
        Assert.Equal(2, await fixture.Db.LessonTypes.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Import_preview_and_apply_reject_same_lesson_type_merge_blocked_by_active_plan()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var course = await fixture.SeedCourseAsync();
        var sourceType = new LessonTypeRef { Code = "REPEATED", Name = "Семінар Семінар" };
        fixture.Db.LessonTypes.Add(sourceType);
        await fixture.Db.SaveChangesAsync();
        var nowUtc = DateTime.UtcNow;
        var job = new AutoGenJobRun
        {
            JobId = "docx-merge-preflight",
            ClientPartitionKey = "docx-tests",
            RequestHash = "docx-merge-preflight-hash",
            Attempt = 1,
            Version = 1,
            Kind = 0,
            State = 2,
            Title = "Перевірка DOCX",
            CurrentStage = "completed",
            CreatedAtUtc = nowUtc,
            RangeStartDate = new DateOnly(2026, 9, 7),
            RangeEndDate = new DateOnly(2026, 9, 7),
            RequestJson = "{}",
            StatusJson = "{}",
            UpdatedAtUtc = nowUtc
        };
        var plan = new AutoGenDraftPlan
        {
            PlanId = "docx-merge-preflight-plan",
            AutoGenJobRun = job,
            State = (int)AutoGenPlanState.Ready,
            Version = 1,
            CourseId = course.Id,
            RangeStartDate = new DateOnly(2026, 9, 7),
            RangeEndDate = new DateOnly(2026, 9, 7),
            Days = 1,
            GroupIdsJson = "[]",
            BeforeScopeRevision = Guid.NewGuid(),
            InputFingerprint = "docx-merge-preflight-fingerprint",
            AddCount = 1,
            CreatedAtUtc = nowUtc,
            ExpiresAtUtc = nowUtc.AddHours(1)
        };
        fixture.Db.AutoGenDraftPlanMutations.Add(new AutoGenDraftPlanMutation
        {
            Plan = plan,
            Ordinal = 0,
            Operation = (int)AutoGenPlanOperation.Add,
            AfterJson = $"{{\"lessonTypeId\":{sourceType.Id}}}"
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var bytes = CreateCurriculumDocx(
            "1",
            "Модуль із заблокованим дублікатом",
            "1",
            new TopicRow("1.1.1 Перша", "Семінар Семінар", "1", "1", "0"),
            new TopicRow("1.1.2 Друга", "Семінар", "1", "1", "0"));

        var preview = await ImportAsync(fixture, bytes, apply: false);
        var apply = await ImportAsync(fixture, bytes, apply: true);

        Assert.NotNull(preview.Error);
        Assert.Equal(preview.Error, apply.Error);
        Assert.Contains("Активні плани автогенерації", preview.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Db.Modules.AsNoTracking().ToListAsync());
        Assert.Single(await fixture.Db.LessonTypes.AsNoTracking().ToListAsync());
        Assert.Equal((int)AutoGenPlanState.Ready, await fixture.Db.AutoGenDraftPlans
            .AsNoTracking()
            .Where(item => item.PlanId == plan.PlanId)
            .Select(item => item.State)
            .SingleAsync());
    }

    [Fact]
    public async Task Import_apply_rechecks_nonterminal_job_that_appears_before_transaction()
    {
        var interceptor = new InsertRunningAutogenJobBeforeTransactionInterceptor();
        await using var fixture = await TestDatabase.CreateAsync(interceptor);
        await fixture.SeedCourseAsync();
        var sourceType = new LessonTypeRef { Code = "REPEATED", Name = "Семінар Семінар" };
        fixture.Db.LessonTypes.Add(sourceType);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var bytes = CreateCurriculumDocx(
            "1",
            "Модуль під час активної автогенерації",
            "1",
            new TopicRow("1.1.1 Перша", "Семінар Семінар", "1", "1", "0"),
            new TopicRow("1.1.2 Друга", "Семінар", "1", "1", "0"));

        var preview = await ImportAsync(fixture, bytes, apply: false);
        interceptor.Arm();
        var apply = await ImportAsync(fixture, bytes, apply: true);

        Assert.Null(preview.Error);
        Assert.NotNull(apply.Error);
        Assert.Contains("тимчасово недоступне", apply.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(InsertRunningAutogenJobBeforeTransactionInterceptor.JobId, apply.Error, StringComparison.Ordinal);
        Assert.Empty(await fixture.Db.Modules.AsNoTracking().ToListAsync());
        Assert.True(await fixture.Db.LessonTypes.AsNoTracking().AnyAsync(type => type.Id == sourceType.Id));
        Assert.Equal("{malformed", await fixture.Db.AutoGenJobRuns
            .AsNoTracking()
            .Where(item => item.JobId == InsertRunningAutogenJobBeforeTransactionInterceptor.JobId)
            .Select(item => item.RequestJson)
            .SingleAsync());
    }

    [Fact]
    public async Task Import_preview_and_apply_reject_malformed_historical_merge_payload()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.SeedCourseAsync();
        var sourceType = new LessonTypeRef { Code = "REPEATED", Name = "Семінар Семінар" };
        var nowUtc = DateTime.UtcNow;
        fixture.Db.AddRange(
            sourceType,
            new AutoGenJobRun
            {
                JobId = "docx-malformed-merge-payload",
                ClientPartitionKey = "docx-tests",
                RequestHash = "docx-malformed-hash",
                Attempt = 1,
                Version = 1,
                Kind = 0,
                State = 2,
                Title = "Пошкоджений історичний payload",
                CurrentStage = "completed",
                CreatedAtUtc = nowUtc,
                RangeStartDate = new DateOnly(2026, 9, 7),
                RangeEndDate = new DateOnly(2026, 9, 7),
                RequestJson = "{}",
                StatusJson = "{}",
                ResultJson = "{\"lessonTypeCode\":\"repeated\"",
                UpdatedAtUtc = nowUtc
            });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var bytes = CreateCurriculumDocx(
            "1",
            "Модуль із пошкодженим payload",
            "1",
            new TopicRow("1.1.1 Перша", "Семінар Семінар", "1", "1", "0"),
            new TopicRow("1.1.2 Друга", "Семінар", "1", "1", "0"));

        var preview = await ImportAsync(fixture, bytes, apply: false);
        var apply = await ImportAsync(fixture, bytes, apply: true);

        Assert.NotNull(preview.Error);
        Assert.Equal(preview.Error, apply.Error);
        Assert.Contains("пошкоджений", preview.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Db.Modules.AsNoTracking().ToListAsync());
        Assert.True(await fixture.Db.LessonTypes.AsNoTracking().AnyAsync(type => type.Id == sourceType.Id));
    }

    [Fact]
    public async Task Import_preview_and_apply_reject_numeric_string_historical_lesson_type_id()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.SeedCourseAsync();
        var sourceType = new LessonTypeRef { Code = "REPEATED", Name = "Семінар Семінар" };
        fixture.Db.LessonTypes.Add(sourceType);
        await fixture.Db.SaveChangesAsync();
        var nowUtc = DateTime.UtcNow;
        fixture.Db.AutoGenJobRuns.Add(new AutoGenJobRun
        {
            JobId = "docx-string-lesson-type-id",
            ClientPartitionKey = "docx-tests",
            RequestHash = "docx-string-id-hash",
            Attempt = 1,
            Version = 1,
            Kind = 0,
            State = 2,
            Title = "Некоректний тип ідентифікатора",
            CurrentStage = "completed",
            CreatedAtUtc = nowUtc,
            RangeStartDate = new DateOnly(2026, 9, 7),
            RangeEndDate = new DateOnly(2026, 9, 7),
            RequestJson = "{}",
            StatusJson = "{}",
            ResultJson = $"{{\"lessonTypeId\":\"{sourceType.Id}\"}}",
            UpdatedAtUtc = nowUtc
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var bytes = CreateCurriculumDocx(
            "1",
            "Модуль із рядковим ідентифікатором",
            "1",
            new TopicRow("1.1.1 Перша", "Семінар Семінар", "1", "1", "0"),
            new TopicRow("1.1.2 Друга", "Семінар", "1", "1", "0"));

        var preview = await ImportAsync(fixture, bytes, apply: false);
        var apply = await ImportAsync(fixture, bytes, apply: true);

        Assert.NotNull(preview.Error);
        Assert.Equal(preview.Error, apply.Error);
        Assert.Contains("неочікуваним типом", preview.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Db.Modules.AsNoTracking().ToListAsync());
        Assert.True(await fixture.Db.LessonTypes.AsNoTracking().AnyAsync(type => type.Id == sourceType.Id));
    }

    [Fact]
    public async Task Import_preview_and_apply_include_all_merge_passes_in_operation_budget()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.SeedCourseAsync();
        var sourceType = new LessonTypeRef { Code = "REPEATED", Name = "Семінар Семінар" };
        fixture.Db.LessonTypes.Add(sourceType);
        var nowUtc = DateTime.UtcNow;
        fixture.Db.AutoGenJobRuns.AddRange(Enumerable.Range(1, 2_100)
            .Select(index => new AutoGenJobRun
            {
                JobId = $"docx-merge-fanout-{index}",
                ClientPartitionKey = "docx-tests",
                RequestHash = "docx-merge-fanout-hash",
                Attempt = 1,
                Version = 1,
                Kind = 0,
                State = 2,
                Title = "Історичне посилання",
                CurrentStage = "completed",
                CreatedAtUtc = nowUtc,
                RangeStartDate = new DateOnly(2026, 9, 7),
                RangeEndDate = new DateOnly(2026, 9, 7),
                RequestJson = "{\"lessonTypeCode\":\"repeated\"}",
                StatusJson = "{}",
                UpdatedAtUtc = nowUtc
            }));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var bytes = CreateCurriculumDocx(
            "1",
            "Малий модуль із великим історичним fan-out",
            "1",
            new TopicRow("1.1.1 Перша", "Семінар Семінар", "1", "1", "0"),
            new TopicRow("1.1.2 Друга", "Семінар", "1", "1", "0"));

        var preview = await ImportAsync(fixture, bytes, apply: false);
        var apply = await ImportAsync(fixture, bytes, apply: true);

        Assert.NotNull(preview.Error);
        Assert.Equal(preview.Error, apply.Error);
        Assert.Contains("операцій", preview.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Db.Modules.AsNoTracking().ToListAsync());
        Assert.Single(await fixture.Db.LessonTypes.AsNoTracking().ToListAsync());
        Assert.Equal(2_100, await fixture.Db.AutoGenJobRuns.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Import_preview_and_apply_bound_aggregate_historical_json_traversal()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.SeedCourseAsync();
        fixture.Db.LessonTypes.AddRange(
            new LessonTypeRef { Code = "SEMINAR_REPEATED", Name = "Семінар Семінар" },
            new LessonTypeRef { Code = "LECTURE_REPEATED", Name = "Лекція Лекція" });
        var nowUtc = DateTime.UtcNow;
        var largeValidPayload = $"{{\"padding\":\"{new string('X', 1_400_000)}\"}}";
        fixture.Db.AutoGenJobRuns.Add(new AutoGenJobRun
        {
            JobId = "docx-merge-character-fanout",
            ClientPartitionKey = "docx-tests",
            RequestHash = "docx-merge-character-hash",
            Attempt = 1,
            Version = 1,
            Kind = 0,
            State = 2,
            Title = "Великий історичний payload",
            CurrentStage = "completed",
            CreatedAtUtc = nowUtc,
            RangeStartDate = new DateOnly(2026, 9, 7),
            RangeEndDate = new DateOnly(2026, 9, 7),
            RequestJson = largeValidPayload,
            StatusJson = largeValidPayload,
            ResultJson = largeValidPayload,
            ReportJson = largeValidPayload,
            UpdatedAtUtc = nowUtc
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var bytes = CreateCurriculumDocx(
            "1",
            "Малий модуль із великим JSON fan-out",
            "1",
            new TopicRow("1.1.1 Перша", "Семінар Семінар", "1", "1", "0"),
            new TopicRow("1.1.2 Друга", "Семінар", "1", "1", "0"),
            new TopicRow("1.1.3 Третя", "Лекція Лекція", "1", "1", "0"),
            new TopicRow("1.1.4 Четверта", "Лекція", "1", "1", "0"));

        var preview = await ImportAsync(fixture, bytes, apply: false);
        var apply = await ImportAsync(fixture, bytes, apply: true);

        Assert.NotNull(preview.Error);
        Assert.Equal(preview.Error, apply.Error);
        Assert.Contains("символів", preview.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Db.Modules.AsNoTracking().ToListAsync());
        Assert.Equal(2, await fixture.Db.LessonTypes.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Import_apply_precreates_missing_canonical_target_before_merging_existing_alias()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.SeedCourseAsync();
        var sourceType = new LessonTypeRef { Code = "REPEATED", Name = "Семінар Семінар" };
        fixture.Db.LessonTypes.Add(sourceType);
        await fixture.Db.SaveChangesAsync();
        var sourceTypeId = sourceType.Id;
        var bytes = CreateCurriculumDocx(
            "1",
            "Модуль із новою канонічною назвою",
            "1",
            new TopicRow("1.1.1 Перша", "Семінар Семінар", "1", "1", "0"),
            new TopicRow("1.1.2 Друга", "Семінар", "1", "1", "0"));

        var preview = await ImportAsync(fixture, bytes, apply: false);
        var apply = await ImportAsync(fixture, bytes, apply: true);

        Assert.Null(preview.Error);
        Assert.Null(apply.Error);
        fixture.Db.ChangeTracker.Clear();
        Assert.False(await fixture.Db.LessonTypes.AsNoTracking().AnyAsync(type => type.Id == sourceTypeId));
        var canonicalType = await fixture.Db.LessonTypes.AsNoTracking().SingleAsync();
        Assert.Equal("Семінар", canonicalType.Name);
        Assert.Equal("СЕМІНАР", canonicalType.Code);
        Assert.Equal(2, await fixture.Db.ModuleTopics.AsNoTracking()
            .CountAsync(topic => topic.LessonTypeId == canonicalType.Id));
    }

    [Fact]
    public async Task Import_collapses_multi_step_lesson_type_alias_chain_to_single_canonical_target()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.SeedCourseAsync();
        var bytes = CreateCurriculumDocx(
            "1",
            "Модуль із ланцюжком назв",
            "1",
            new TopicRow("1.1.1 Перша", "Семінар Семінар Семінар", "1", "1", "0"),
            new TopicRow("1.1.2 Друга", "Семінар Семінар", "1", "1", "0"),
            new TopicRow("1.1.3 Третя", "Семінар", "1", "1", "0"));

        var preview = await ImportAsync(fixture, bytes, apply: false);
        var apply = await ImportAsync(fixture, bytes, apply: true);

        Assert.Null(preview.Error);
        Assert.Null(apply.Error);
        var canonicalType = await fixture.Db.LessonTypes.AsNoTracking().SingleAsync();
        Assert.Equal("Семінар", canonicalType.Name);
        Assert.Equal(3, await fixture.Db.ModuleTopics.AsNoTracking()
            .CountAsync(topic => topic.LessonTypeId == canonicalType.Id));
    }

    [Fact]
    public async Task Import_preview_and_apply_reject_same_unsafe_merge_to_missing_canonical_target()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.SeedCourseAsync();
        fixture.Db.LessonTypes.Add(new LessonTypeRef
        {
            Code = "REPEATED",
            Name = "Семінар Семінар",
            RequiresRoom = false
        });
        await fixture.Db.SaveChangesAsync();
        var bytes = CreateCurriculumDocx(
            "1",
            "Модуль із несумісною новою назвою",
            "1",
            new TopicRow("1.1.1 Перша", "Семінар Семінар", "1", "1", "0"),
            new TopicRow("1.1.2 Друга", "Семінар", "1", "1", "0"));

        var preview = await ImportAsync(fixture, bytes, apply: false);
        var apply = await ImportAsync(fixture, bytes, apply: true);

        Assert.NotNull(preview.Error);
        Assert.Equal(preview.Error, apply.Error);
        Assert.Contains("різні правила", preview.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Db.Modules.AsNoTracking().ToListAsync());
        Assert.Single(await fixture.Db.LessonTypes.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Import_rejects_module_title_above_shared_bound()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.SeedCourseAsync();
        var bytes = CreateModuleDocx(
            "1",
            new string('М', CurriculumInputLimits.ModuleTitleMaxLength + 1),
            "1");

        var result = await ImportAsync(fixture, bytes, apply: false);

        Assert.NotNull(result.Error);
        Assert.Contains(CurriculumInputLimits.ModuleTitleMaxLength.ToString(), result.Error, StringComparison.Ordinal);
        Assert.Empty(await fixture.Db.Modules.AsNoTracking().ToListAsync());
    }

    private static byte[] CreateModuleDocx(string moduleTitle)
        => CreateModuleDocx(new[] { ("1", moduleTitle) });

    private static byte[] CreateZipWithEmptyEntries(int entryCount)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var index = 0; index < entryCount; index++)
            {
                archive.CreateEntry($"entry-{index:D4}.xml");
            }
        }
        return stream.ToArray();
    }

    private static byte[] CreateZipWithDeclaredUncompressedSizes(params int[] declaredSizes)
    {
        var bytes = CreateZipWithEmptyEntries(declaredSizes.Length);
        var patchedEntries = 0;
        for (var offset = 0; offset <= bytes.Length - 46 && patchedEntries < declaredSizes.Length; offset++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, sizeof(uint))) != 0x02014b50)
            {
                continue;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(offset + 24, sizeof(uint)),
                checked((uint)declaredSizes[patchedEntries]));
            patchedEntries++;
        }

        Assert.Equal(declaredSizes.Length, patchedEntries);
        return bytes;
    }

    private static byte[] CreateModuleDocx(string moduleCode, string moduleTitle, string credits)
        => CreateModuleDocx(new[] { (moduleCode, moduleTitle, credits) });

    private static byte[] CreateModuleDocx(IEnumerable<(string Code, string Title)> modules)
        => CreateModuleDocx(modules.Select(module => (module.Code, module.Title, "1")));

    private static byte[] CreateModuleDocx(IEnumerable<(string Code, string Title, string Credits)> modules)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var mainPart = document.AddMainDocumentPart();
            var rows = new List<TableRow> { CreateRow("Код", "Назва", "Кредити") };
            rows.AddRange(modules.Select(module => CreateRow(module.Code, module.Title, module.Credits)));
            mainPart.Document = new Document(new Body(new Table(rows)));
            mainPart.Document.Save();
        }
        return stream.ToArray();
    }

    private static byte[] CreateCurriculumDocx(
        string moduleCode,
        string moduleTitle,
        string credits,
        params TopicRow[] topics)
        => CreateCurriculumDocx(
            moduleCode,
            moduleTitle,
            credits,
            new[] { "1", "2", "3", "4", "5", "6" },
            topics);

    private static byte[] CreateCurriculumDocx(
        string moduleCode,
        string moduleTitle,
        string credits,
        IReadOnlyList<string> topicHeader,
        params TopicRow[] topics)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var mainPart = document.AddMainDocumentPart();
            var moduleTable = new Table(
                CreateRow("Код", "Назва", "Кредити"),
                CreateRow(moduleCode, moduleTitle, credits));
            var topicRows = new List<TableRow>
            {
                CreateRow(topicHeader.ToArray()),
                CreateRow($"{moduleCode}.1.1 Тематичний план")
            };
            topicRows.AddRange(topics.Select(topic => CreateRow(
                string.Empty,
                topic.LessonType,
                topic.TotalHours,
                topic.AuditoriumHours,
                topic.SelfStudyHours,
                topic.TopicCell)));
            mainPart.Document = new Document(new Body(moduleTable, new Table(topicRows)));
            mainPart.Document.Save();
        }
        return stream.ToArray();
    }

    private static byte[] CreateCurriculumWithTopicTablePerModule(int moduleCount)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(
                   stream,
                   WordprocessingDocumentType.Document,
                   true))
        {
            var mainPart = document.AddMainDocumentPart();
            var moduleRows = new List<TableRow> { CreateRow("Код", "Назва", "Кредити") };
            moduleRows.AddRange(Enumerable.Range(1, moduleCount)
                .Select(index => CreateRow(
                    index.ToString(CultureInfo.InvariantCulture),
                    $"Модуль {index}",
                    "1")));
            var body = new Body(new Table(moduleRows));
            foreach (var index in Enumerable.Range(1, moduleCount))
            {
                var moduleCode = index.ToString(CultureInfo.InvariantCulture);
                body.Append(new Table(
                    CreateRow("1", "2", "3", "4", "5", "6"),
                    CreateRow($"{moduleCode}.1.1 Тематичний план"),
                    CreateRow(
                        string.Empty,
                        "Лекція",
                        "1",
                        "1",
                        "0",
                        $"{moduleCode}.1.1 Тема")));
            }
            mainPart.Document = new Document(body);
            mainPart.Document.Save();
        }
        return stream.ToArray();
    }

    private static async Task<DocxImportResultDto> ImportAsync(
        TestDatabase fixture,
        byte[] bytes,
        bool apply,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", "КН-1.docx");
        return await new DocxImportService().ImportAsync(
            file,
            fixture.Db,
            apply,
            cancellationToken);
    }

    private static TableRow CreateRow(params string[] values)
        => new(values.Select(value => new TableCell(new Paragraph(new Run(new Text(value))))));

    private sealed record TopicRow(
        string TopicCell,
        string LessonType,
        string TotalHours,
        string AuditoriumHours,
        string SelfStudyHours);

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private TestDatabase(SqliteConnection connection, AppDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public AppDbContext Db { get; }

        public async Task<Course> SeedCourseAsync()
        {
            var course = new Course { Name = "КН-1", DurationWeeks = 52 };
            Db.Courses.Add(course);
            await Db.SaveChangesAsync();
            return course;
        }

        public async Task SeedCourseWithLessonTypeAsync(string code, string name)
        {
            Db.AddRange(
                new Course { Name = "КН-1", DurationWeeks = 52 },
                new LessonTypeRef { Code = code, Name = name });
            await Db.SaveChangesAsync();
        }

        public static async Task<TestDatabase> CreateAsync(IInterceptor? interceptor = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection);
            if (interceptor is not null)
            {
                optionsBuilder.AddInterceptors(interceptor);
            }
            var options = optionsBuilder.Options;
            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
