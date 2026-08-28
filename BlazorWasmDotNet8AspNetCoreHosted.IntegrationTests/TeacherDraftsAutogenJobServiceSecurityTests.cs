using System.Reflection;
using System.Text.Json;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class TeacherDraftsAutogenJobServiceSecurityTests
{
    [Fact]
    public async Task Plan_handoff_gate_rejects_waiters_above_the_global_limit_without_queueing()
    {
        using var gate = new ExpensiveOperationGate();
        var leases = new List<IDisposable>();
        try
        {
            for (var index = 0; index < 8; index++)
            {
                leases.Add(Assert.IsAssignableFrom<IDisposable>(
                    await gate.TryEnterAsync(
                        ExpensiveOperationKind.AutoGenPlanHandoff,
                        CancellationToken.None)));
            }

            Assert.Null(await gate.TryEnterAsync(
                ExpensiveOperationKind.AutoGenPlanHandoff,
                CancellationToken.None));
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }
    }

    [Fact]
    public async Task GetAsync_rejects_noncanonical_job_id_before_storage_access()
    {
        var service = new TeacherDraftsAutogenJobService(
            new ThrowingScopeFactory(() => new InvalidOperationException("Сховище не повинно викликатися.")),
            new CapturingLogger<TeacherDraftsAutogenJobService>());

        var status = await service.GetAsync("../../not-a-job");

        Assert.Null(status);
    }

    [Fact]
    public async Task GetAsync_cancels_while_waiting_for_persistence_gate()
    {
        var service = new TeacherDraftsAutogenJobService(
            new ThrowingScopeFactory(() => new InvalidOperationException("Сховище не повинно викликатися.")),
            new CapturingLogger<TeacherDraftsAutogenJobService>());
        var gate = await HoldGateAsync(service, "_persistenceGate");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.GetAsync(Guid.NewGuid().ToString("N"), cancellation.Token));
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task CancelAsync_rejects_noncanonical_job_id_before_storage_access()
    {
        var service = new TeacherDraftsAutogenJobService(
            new ThrowingScopeFactory(() => new InvalidOperationException("Сховище не повинно викликатися.")),
            new CapturingLogger<TeacherDraftsAutogenJobService>());

        var status = await service.CancelAsync("../../not-a-job");

        Assert.Null(status);
    }

    [Fact]
    public async Task CancelAsync_cancels_while_waiting_for_persistence_gate()
    {
        var service = new TeacherDraftsAutogenJobService(
            new ThrowingScopeFactory(() => new InvalidOperationException("Сховище не повинно викликатися.")),
            new CapturingLogger<TeacherDraftsAutogenJobService>());
        var gate = await HoldGateAsync(service, "_persistenceGate");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.CancelAsync(Guid.NewGuid().ToString("N"), cancellation.Token));
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task GetAsync_rejects_oversized_persisted_status_before_json_deserialization()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        var jobId = Guid.NewGuid().ToString("N");
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.AutoGenJobRuns.Add(new AutoGenJobRun
            {
                JobId = jobId,
                RequestHash = new string('a', 64),
                Version = 1,
                Kind = (int)AutoGenJobKind.Generate,
                State = (int)AutoGenJobState.Succeeded,
                Title = "Перевірка обмеження",
                CurrentStage = "Завершено",
                CreatedAtUtc = DateTime.UtcNow,
                CompletedAtUtc = DateTime.UtcNow,
                RangeStartDate = new DateOnly(2026, 1, 1),
                RangeEndDate = new DateOnly(2026, 1, 1),
                RequestJson = "{}",
                StatusJson = new string('1',
                    TeacherDraftsAutogenJobService.MaxPersistedPayloadCharacters + 1),
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(options));
        await using var provider = services.BuildServiceProvider();
        var service = new TeacherDraftsAutogenJobService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new CapturingLogger<TeacherDraftsAutogenJobService>());

        var exception = await Assert.ThrowsAsync<AutoGenJobPersistenceException>(() =>
            service.GetAsync(jobId));

        Assert.Contains(
            TeacherDraftsAutogenJobService.MaxPersistedPayloadCharacters.ToString(),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopAsync_cancels_and_awaits_running_job_before_returning()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Courses.Add(new Course
            {
                Id = 1,
                Name = "Курс перевірки зупинки",
                DurationWeeks = 52,
                AcademicPeriodStartDate = new DateOnly(2026, 1, 1)
            });
            await db.SaveChangesAsync();
        }
        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(options));
        await using var provider = services.BuildServiceProvider();
        var service = new TeacherDraftsAutogenJobService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new CapturingLogger<TeacherDraftsAutogenJobService>());
        var executionGate = await HoldGateAsync(service, "_executionGate");
        var started = service.Start(CreateValidRequest());
        var persistenceGate = await HoldGateAsync(service, "_persistenceGate");
        Task? stopTask = null;

        try
        {
            stopTask = service.StopAsync(CancellationToken.None);
            await Task.Delay(50);
            Assert.False(stopTask.IsCompleted);
        }
        finally
        {
            persistenceGate.Release();
        }

        Assert.NotNull(stopTask);
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        executionGate.Release();
        var status = await service.GetAsync(started.JobId);
        Assert.NotNull(status);
        Assert.Equal(AutoGenJobState.Canceled, status.State);
        Assert.True(status.CancellationRequested);
        Assert.DoesNotContain("користувач", status.CurrentStage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Initial_persistence_failure_does_not_queue_or_expose_a_local_job()
    {
        var service = new TeacherDraftsAutogenJobService(
            new ThrowingScopeFactory(() => new InvalidOperationException("Сховище недоступне.")),
            new CapturingLogger<TeacherDraftsAutogenJobService>());

        Assert.Throws<AutoGenJobPersistenceException>(() => service.Start(CreateValidRequest()));

        Assert.Equal(0, GetPrivateCollectionCount(service, "_jobs"));
        Assert.Equal(0, GetPrivateCollectionCount(service, "_runningTasks"));
    }

    [Fact]
    public void Start_rejects_null_group_room_preference_before_persistence()
    {
        var service = new TeacherDraftsAutogenJobService(
            new ThrowingScopeFactory(() => new InvalidOperationException("Сховище не повинно викликатися.")),
            new CapturingLogger<TeacherDraftsAutogenJobService>());
        var request = CreateValidRequest() with
        {
            GroupRoomPreferences = new List<GroupRoomPreferenceDto> { null! }
        };

        var exception = Assert.Throws<AutoGenJobValidationException>(() => service.Start(request));

        Assert.Contains("порожні елементи", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, GetPrivateCollectionCount(service, "_jobs"));
        Assert.Equal(0, GetPrivateCollectionCount(service, "_runningTasks"));
    }

    [Fact]
    public void Start_rejects_duplicate_group_room_preferences_before_hash_or_persistence()
    {
        var service = new TeacherDraftsAutogenJobService(
            new ThrowingScopeFactory(() => new InvalidOperationException("Сховище не повинно викликатися.")),
            new CapturingLogger<TeacherDraftsAutogenJobService>());
        var request = CreateValidRequest() with
        {
            GroupRoomPreferences =
            [
                new GroupRoomPreferenceDto(1, 10, [100]),
                new GroupRoomPreferenceDto(1, 20, [200])
            ]
        };

        var exception = Assert.Throws<AutoGenJobValidationException>(() => service.Start(request));

        Assert.Contains("лише одне налаштування", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, GetPrivateCollectionCount(service, "_jobs"));
        Assert.Equal(0, GetPrivateCollectionCount(service, "_runningTasks"));
    }

    [Fact]
    public async Task Start_rejects_new_job_after_service_stopping_begins()
    {
        var service = new TeacherDraftsAutogenJobService(
            new ThrowingScopeFactory(() => new InvalidOperationException("Сховище недоступне.")),
            new CapturingLogger<TeacherDraftsAutogenJobService>());
        await service.StopAsync(CancellationToken.None);

        var exception = Assert.Throws<AutoGenJobCapacityException>(
            () => service.Start(CreateValidRequest()));

        Assert.Contains("завершує роботу", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unexpected_exception_is_logged_but_not_exposed_in_job_status()
    {
        const string secret = "mysql://internal-user:internal-password@database/private";
        var logger = new CapturingLogger<TeacherDraftsAutogenJobService>();
        var service = new TeacherDraftsAutogenJobService(
            new ThrowingScopeFactory(() => new InvalidOperationException(secret)),
            logger);
        var runtime = CreateRuntime(CreateValidRequest());

        await InvokeRunAsync(service, runtime).WaitAsync(TimeSpan.FromSeconds(2));

        var status = GetStatus(runtime);
        Assert.Equal(AutoGenJobState.Failed, status.State);
        Assert.NotNull(status.Error);
        Assert.Contains("внутрішня помилка", status.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(status.JobId, status.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, status.Error, StringComparison.Ordinal);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Error
            && entry.Exception is InvalidOperationException
            && entry.Exception.Message == secret
            && entry.Message.Contains(status.JobId, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("validation")]
    [InlineData("capacity")]
    public async Task Expected_failure_preserves_safe_message_and_job_correlation(string failureKind)
    {
        var safeMessage = failureKind == "validation"
            ? "Параметри тестового завдання некоректні."
            : "Черга тестових завдань заповнена.";
        var logger = new CapturingLogger<TeacherDraftsAutogenJobService>();
        var service = new TeacherDraftsAutogenJobService(
            new ThrowingScopeFactory(() => failureKind == "validation"
                ? new AutoGenJobValidationException(safeMessage)
                : new AutoGenJobCapacityException(safeMessage)),
            logger);
        var runtime = CreateRuntime(CreateValidRequest());

        await InvokeRunAsync(service, runtime).WaitAsync(TimeSpan.FromSeconds(2));

        var status = GetStatus(runtime);
        Assert.Equal(AutoGenJobState.Failed, status.State);
        Assert.NotNull(status.Error);
        Assert.Contains(safeMessage, status.Error, StringComparison.Ordinal);
        Assert.Contains(status.JobId, status.Error, StringComparison.Ordinal);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning
            && entry.Exception?.Message == safeMessage
            && entry.Message.Contains(status.JobId, StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Unrequested_cancellation_uses_safe_interruption_message()
    {
        const string internalMessage = "Переривання внутрішнього драйвера з приватними деталями.";
        var logger = new CapturingLogger<TeacherDraftsAutogenJobService>();
        var service = new TeacherDraftsAutogenJobService(
            new ThrowingScopeFactory(() => new OperationCanceledException(internalMessage)),
            logger);
        var runtime = CreateRuntime(CreateValidRequest());

        await InvokeRunAsync(service, runtime).WaitAsync(TimeSpan.FromSeconds(2));

        var status = GetStatus(runtime);
        Assert.Equal(AutoGenJobState.Failed, status.State);
        Assert.False(status.CancellationRequested);
        Assert.NotNull(status.Error);
        Assert.Contains("було перервано", status.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(status.JobId, status.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(internalMessage, status.Error, StringComparison.Ordinal);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning
            && entry.Exception is OperationCanceledException
            && entry.Exception.Message == internalMessage);
    }

    [Fact]
    public void Integrated_fill_merge_replaces_provisional_gaps_but_preserves_durable_warnings()
    {
        const string preflightDeficit =
            "Попередня перевірка ресурсів: доступні викладачі — 1. Додайте викладача.";
        const string gap =
            "Автогенерація не заповнила слот 08:30-09:50 для групи 9301 на 2026-09-01.";
        const string searchLimit =
            "[2026-09-01] Група 9301: [search-limit] фінальний repair-pass досягнув межі вузлів.";
        const string topicExhausted =
            "Для модуля <Фізика> у групи 9301 вичерпано теми. Пропустили розкладення.";
        const string resourceUnavailable =
            "Фінальний matching не знайшов повного набору обов'язкових ресурсів.";
        const string recommendation =
            "Рекомендація автогенерації: додайте або звільніть викладачів.";
        const string diagnosticSummary =
            "Зведення причин незаповнених слотів: викладач — 1.";
        const string incomplete =
            "Створено 1 неповних чернеток: без викладача — 1, без аудиторії — 0.";
        const string topicReused =
            "Для модуля <Фізика> у групі 9301 повторно використано тему Т1, щоб заповнити слот без порушення жорстких правил.";
        const string departmentFallback =
            "Для групи 9301 використано явний зв'язок викладача з модулем поза кафедрою теми.";
        const string inputAdjusted = "Ігноровано модулі, що не належать курсу #2: 17.";
        const string fillOptimization =
            "Фінальна синхронізація застосувала 1 однозначне переміщення чернетки із repair-проходу.";
        var finalDeficit = new AutoGenPreflightItem(
            "teacher",
            "Викладачі",
            1,
            "Додайте викладача.",
            ["9301"]);
        var generationResult = new AutoGenResult(
            5,
            4,
            [
                preflightDeficit,
                gap,
                searchLimit,
                topicExhausted,
                resourceUnavailable,
                recommendation,
                diagnosticSummary,
                incomplete,
                topicReused,
                departmentFallback,
                inputAdjusted
            ]);
        var fillResult = new AutoGenResult(
            2,
            0,
            [inputAdjusted, fillOptimization],
            [],
            [],
            [finalDeficit]);
        var mergeMethod = typeof(TeacherDraftsAutogenJobService).GetMethod(
            "MergeIntegratedGenerationResult",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(mergeMethod);

        var merged = Assert.IsType<AutoGenResult>(
            mergeMethod.Invoke(null, new object[] { generationResult, fillResult }));

        Assert.Equal(7, merged.Created);
        Assert.Equal(0, merged.Skipped);
        Assert.Equal(
            [incomplete, topicReused, departmentFallback, inputAdjusted, fillOptimization],
            merged.Warnings);
        Assert.Empty(merged.GapDetails ?? []);
        Assert.Empty(merged.GapSummary ?? []);
        Assert.Equal([finalDeficit], merged.Preflight);
        Assert.Equal(
            merged.Warnings,
            (merged.WarningDetails ?? []).Select(item => item.Message));
    }

    [Fact]
    public void Integrated_fill_merge_clears_stale_diagnostics_after_all_gaps_are_closed()
    {
        const string stableWarning = "Ігноровано модулі, що не належать курсу #2: 17.";
        const string preflightDeficit =
            "Попередня перевірка ресурсів: доступні викладачі — 1. Додайте викладача.";
        const string recommendation =
            "Рекомендація автогенерації: додайте або звільніть викладачів.";
        const string diagnosticSummary =
            "Зведення причин незаповнених слотів: викладач — 1.";
        var generationResult = new AutoGenResult(1, 1, [preflightDeficit, recommendation]);
        var fillResult = new AutoGenResult(
            1,
            0,
            [stableWarning, preflightDeficit, recommendation, diagnosticSummary],
            [],
            [],
            [new AutoGenPreflightItem("teacher", "Викладачі", 1, "Додайте викладача.", ["9301"])]);
        var mergeMethod = typeof(TeacherDraftsAutogenJobService).GetMethod(
            "MergeIntegratedGenerationResult",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(mergeMethod);

        var merged = Assert.IsType<AutoGenResult>(
            mergeMethod.Invoke(null, new object[] { generationResult, fillResult }));

        Assert.Equal(2, merged.Created);
        Assert.Equal(0, merged.Skipped);
        Assert.Equal([stableWarning], merged.Warnings);
        Assert.Empty(merged.GapDetails ?? []);
        Assert.Empty(merged.Preflight ?? []);
    }

    [Fact]
    public void Integrated_generation_runtime_exposes_phase_progress_without_completing_the_range()
    {
        var runtime = CreateRuntime(CreateValidRequest() with
        {
            Kind = AutoGenJobKind.Generate,
            PreviewOnly = true
        });
        var date = new DateOnly(2026, 9, 1);
        runtime.GetType().GetMethod("MarkRunning")!.Invoke(runtime, new object[] { 1 });
        runtime.GetType().GetMethod("StartWeek")!.Invoke(
            runtime,
            new object[] { 0, new DateOnly(2026, 8, 31), date, date });
        Assert.Equal(1, GetStatus(runtime).Percent);

        runtime.GetType().GetMethod("StartIntegratedFill")!.Invoke(runtime, new object[] { date, date });
        var fillStatus = GetStatus(runtime);
        Assert.Equal(50, fillStatus.Percent);
        Assert.Contains("Дозаповнюємо", fillStatus.CurrentStage, StringComparison.Ordinal);

        runtime.GetType().GetMethod("StartIntegratedRelaxedRepair")!
            .Invoke(runtime, new object[] { date, date });
        var repairStatus = GetStatus(runtime);
        Assert.Equal(75, repairStatus.Percent);
        Assert.Contains("резервне дозаповнення", repairStatus.CurrentStage, StringComparison.Ordinal);
        Assert.Equal(0, repairStatus.CompletedWeeks);
    }

    [Fact]
    public async Task Persisted_plan_applies_once_and_rolls_back_only_untouched_result()
    {
        await using var fixture = await AutoGenPlanFixture.CreateAsync();

        await using (var db = new AppDbContext(fixture.Options))
        {
            var ready = await new TeacherDraftsAutogenPlanService(db).GetDetailsAsync(fixture.PlanId);
            Assert.Contains(ready.Result.Warnings, warning =>
                warning.Contains("Застосуйте його окремою дією", StringComparison.OrdinalIgnoreCase));
        }

        AutoGenPlanDetailsDto applied;
        await using (var db = new AppDbContext(fixture.Options))
        {
            applied = await new TeacherDraftsAutogenPlanService(db).ApplyAsync(
                fixture.PlanId,
                new AutoGenPlanActionRequest(1));
        }

        Assert.Equal(AutoGenPlanState.Applied, applied.Summary.State);
        Assert.True(applied.Summary.CanRollback);
        Assert.Equal(0, applied.Result.Created);
        Assert.DoesNotContain(applied.Result.Warnings, warning =>
            warning.Contains("Застосуйте його окремою дією", StringComparison.OrdinalIgnoreCase));
        await using (var verification = new AppDbContext(fixture.Options))
        {
            Assert.Empty(await verification.TeacherDraftItems.AsNoTracking().ToListAsync());
            var persisted = await verification.AutoGenDraftPlans.AsNoTracking().SingleAsync();
            Assert.Equal((int)AutoGenPlanState.Applied, persisted.State);
            Assert.Equal(2, persisted.Version);
            var statusJson = await verification.AutoGenJobRuns
                .AsNoTracking()
                .Select(item => item.StatusJson)
                .SingleAsync();
            var persistedStatus = JsonSerializer.Deserialize<AutoGenJobStatus>(
                statusJson,
                AutoGenPlanFixture.JsonOptions);
            Assert.Equal(0, persistedStatus?.WarningCount);
            Assert.DoesNotContain(persistedStatus?.Result?.Warnings ?? [], warning =>
                warning.Contains("Застосуйте його окремою дією", StringComparison.OrdinalIgnoreCase));
        }

        AutoGenPlanDetailsDto repeatedApply;
        await using (var db = new AppDbContext(fixture.Options))
        {
            repeatedApply = await new TeacherDraftsAutogenPlanService(db).ApplyAsync(
                fixture.PlanId,
                new AutoGenPlanActionRequest(1));
        }
        Assert.Equal(applied.Summary.Version, repeatedApply.Summary.Version);

        AutoGenPlanDetailsDto rolledBack;
        await using (var db = new AppDbContext(fixture.Options))
        {
            rolledBack = await new TeacherDraftsAutogenPlanService(db).RollbackAsync(
                fixture.PlanId,
                new AutoGenPlanActionRequest(applied.Summary.Version));
        }

        Assert.Equal(AutoGenPlanState.RolledBack, rolledBack.Summary.State);
        Assert.False(rolledBack.Summary.CanRollback);
        await using (var verification = new AppDbContext(fixture.Options))
        {
            var restored = await verification.TeacherDraftItems.AsNoTracking().SingleAsync();
            Assert.Equal(fixture.OriginalDraftId, restored.Id);
            Assert.NotEqual(fixture.OriginalRevision, restored.Revision);
            Assert.Null(restored.GenerationJobId);
            Assert.Equal(new TimeOnly(9, 0), restored.StartTime);
            Assert.Equal(fixture.OriginalCreatedAt, restored.CreatedAt);
            var statusJson = await verification.AutoGenJobRuns
                .AsNoTracking()
                .Select(item => item.StatusJson)
                .SingleAsync();
            var status = JsonSerializer.Deserialize<AutoGenJobStatus>(statusJson, AutoGenPlanFixture.JsonOptions);
            Assert.Equal(AutoGenPlanState.RolledBack, status?.Plan?.State);
        }

        await using (var db = new AppDbContext(fixture.Options))
        {
            var repeatedRollback = await new TeacherDraftsAutogenPlanService(db).RollbackAsync(
                fixture.PlanId,
                new AutoGenPlanActionRequest(applied.Summary.Version));
            Assert.Equal(rolledBack.Summary.Version, repeatedRollback.Summary.Version);
        }
    }

    [Fact]
    public async Task Persisted_add_plan_applies_and_rolls_back_generated_row()
    {
        await using var fixture = await AutoGenPlanFixture.CreateAsync(AutoGenPlanOperation.Add);

        AutoGenPlanDetailsDto applied;
        await using (var db = new AppDbContext(fixture.Options))
        {
            applied = await new TeacherDraftsAutogenPlanService(db).ApplyAsync(
                fixture.PlanId,
                new AutoGenPlanActionRequest(1));
        }

        await using (var verification = new AppDbContext(fixture.Options))
        {
            var created = await verification.TeacherDraftItems.AsNoTracking().SingleAsync();
            Assert.Equal(fixture.PlanId, created.GenerationJobId);
            Assert.NotEqual(Guid.Empty, created.Revision);
            Assert.Equal(new TimeOnly(9, 0), created.StartTime);
        }

        await using (var db = new AppDbContext(fixture.Options))
        {
            var rolledBack = await new TeacherDraftsAutogenPlanService(db).RollbackAsync(
                fixture.PlanId,
                new AutoGenPlanActionRequest(applied.Summary.Version));
            Assert.Equal(AutoGenPlanState.RolledBack, rolledBack.Summary.State);
        }

        await using (var verification = new AppDbContext(fixture.Options))
        {
            Assert.Empty(await verification.TeacherDraftItems.AsNoTracking().ToListAsync());
        }
    }

    [Fact]
    public async Task Persisted_add_plan_rejects_rollback_after_manual_edit()
    {
        await using var fixture = await AutoGenPlanFixture.CreateAsync(AutoGenPlanOperation.Add);
        AutoGenPlanDetailsDto applied;
        await using (var db = new AppDbContext(fixture.Options))
        {
            applied = await new TeacherDraftsAutogenPlanService(db).ApplyAsync(
                fixture.PlanId,
                new AutoGenPlanActionRequest(1));
        }
        await using (var db = new AppDbContext(fixture.Options))
        {
            var created = await db.TeacherDraftItems.SingleAsync();
            created.ValidationWarnings = "Ручна зміна після застосування.";
            await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(fixture.Options))
        {
            await Assert.ThrowsAsync<AutoGenPlanConflictException>(() =>
                new TeacherDraftsAutogenPlanService(db).RollbackAsync(
                    fixture.PlanId,
                    new AutoGenPlanActionRequest(applied.Summary.Version)));
        }

        await using (var verification = new AppDbContext(fixture.Options))
        {
            Assert.Single(await verification.TeacherDraftItems.AsNoTracking().ToListAsync());
            var plan = await verification.AutoGenDraftPlans.AsNoTracking().SingleAsync();
            Assert.Equal((int)AutoGenPlanState.Applied, plan.State);
        }
    }

    [Fact]
    public async Task Persisted_update_plan_applies_and_restores_original_values()
    {
        await using var fixture = await AutoGenPlanFixture.CreateAsync(AutoGenPlanOperation.Update);

        AutoGenPlanDetailsDto applied;
        Guid appliedRevision;
        await using (var db = new AppDbContext(fixture.Options))
        {
            applied = await new TeacherDraftsAutogenPlanService(db).ApplyAsync(
                fixture.PlanId,
                new AutoGenPlanActionRequest(1));
        }
        await using (var verification = new AppDbContext(fixture.Options))
        {
            var updated = await verification.TeacherDraftItems.AsNoTracking().SingleAsync();
            Assert.Equal(fixture.OriginalDraftId, updated.Id);
            Assert.Equal(new TimeOnly(10, 0), updated.StartTime);
            Assert.Equal(fixture.PlanId, updated.GenerationJobId);
            Assert.NotEqual(fixture.OriginalRevision, updated.Revision);
            appliedRevision = updated.Revision;
        }

        await using (var db = new AppDbContext(fixture.Options))
        {
            await new TeacherDraftsAutogenPlanService(db).RollbackAsync(
                fixture.PlanId,
                new AutoGenPlanActionRequest(applied.Summary.Version));
        }
        await using (var verification = new AppDbContext(fixture.Options))
        {
            var restored = await verification.TeacherDraftItems.AsNoTracking().SingleAsync();
            Assert.Equal(fixture.OriginalDraftId, restored.Id);
            Assert.Equal(new TimeOnly(9, 0), restored.StartTime);
            Assert.Null(restored.GenerationJobId);
            Assert.NotEqual(appliedRevision, restored.Revision);
            Assert.Equal(fixture.OriginalCreatedAt, restored.CreatedAt);
        }
    }

    [Fact]
    public async Task Persisted_plan_rejects_stale_scope_without_partial_changes()
    {
        await using var fixture = await AutoGenPlanFixture.CreateAsync();
        await using (var db = new AppDbContext(fixture.Options))
        {
            var draft = await db.TeacherDraftItems.SingleAsync();
            draft.StartTime = new TimeOnly(10, 0);
            draft.EndTime = new TimeOnly(11, 0);
            await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(fixture.Options))
        {
            var error = await Assert.ThrowsAsync<AutoGenPlanConflictException>(() =>
                new TeacherDraftsAutogenPlanService(db).ApplyAsync(
                    fixture.PlanId,
                    new AutoGenPlanActionRequest(1)));
            Assert.Contains("змінилися", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        await using (var verification = new AppDbContext(fixture.Options))
        {
            var draft = await verification.TeacherDraftItems.AsNoTracking().SingleAsync();
            Assert.Equal(new TimeOnly(10, 0), draft.StartTime);
            var plan = await verification.AutoGenDraftPlans.AsNoTracking().SingleAsync();
            Assert.Equal((int)AutoGenPlanState.Ready, plan.State);
            Assert.Equal(1, plan.Version);
        }
    }

    [Fact]
    public async Task Persisted_plan_rejects_changed_generation_configuration()
    {
        await using var fixture = await AutoGenPlanFixture.CreateAsync(AutoGenPlanOperation.Add);
        await using (var db = new AppDbContext(fixture.Options))
        {
            var slot = await db.TimeSlots.OrderBy(item => item.Id).FirstAsync();
            slot.SortOrder += 10;
            await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(fixture.Options))
        {
            var error = await Assert.ThrowsAsync<AutoGenPlanConflictException>(() =>
                new TeacherDraftsAutogenPlanService(db).ApplyAsync(
                    fixture.PlanId,
                    new AutoGenPlanActionRequest(1)));
            Assert.Contains("налаштування", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        await using (var verification = new AppDbContext(fixture.Options))
        {
            Assert.Empty(await verification.TeacherDraftItems.AsNoTracking().ToListAsync());
            var plan = await verification.AutoGenDraftPlans.AsNoTracking().SingleAsync();
            Assert.Equal((int)AutoGenPlanState.Ready, plan.State);
        }
    }

    [Fact]
    public async Task Input_fingerprint_accepts_section_limit_and_rejects_limit_plus_one()
    {
        await using var fixture = await AutoGenPlanFixture.CreateAsync(AutoGenPlanOperation.Add);
        var request = CreatePlanFingerprintRequest();
        await using var db = new AppDbContext(fixture.Options);
        await InsertLessonTypesAsync(
            db,
            TeacherDraftsAutogenInputFingerprint.MaxRowsPerFingerprintSection - 1,
            idOffset: 1_000);
        var service = new TeacherDraftsAutogenPlanService(db);

        var exactLimitFingerprint = await service.CaptureInputFingerprintAsync(request);

        Assert.Equal(64, exactLimitFingerprint.Length);
        db.LessonTypes.Add(new LessonTypeRef
        {
            Code = "CAPACITY-OVERFLOW",
            Name = "Тип заняття понад ліміт",
            IsActive = true
        });
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<AutoGenPlanCapacityException>(() =>
            service.CaptureInputFingerprintAsync(request));
        Assert.Contains(
            TeacherDraftsAutogenInputFingerprint.MaxRowsPerFingerprintSection.ToString(),
            error.Message,
            StringComparison.Ordinal);
        Assert.IsAssignableFrom<AutoGenPlanConflictException>(error);
    }

    [Fact]
    public async Task Plan_scope_accepts_limit_and_apply_rejects_limit_plus_one_without_changes()
    {
        await using var fixture = await AutoGenPlanFixture.CreateAsync(AutoGenPlanOperation.Add);
        var request = CreatePlanFingerprintRequest();
        await using var db = new AppDbContext(fixture.Options);
        await InsertDraftRowsAsync(
            db,
            TeacherDraftsAutogenPlanService.MaxScopeRowCount,
            idOffset: 1_000);
        var service = new TeacherDraftsAutogenPlanService(db);

        var exactLimitScope = await service.CaptureScopeAsync(request, CancellationToken.None);

        Assert.Equal(TeacherDraftsAutogenPlanService.MaxScopeRowCount, exactLimitScope.Count);
        await InsertDraftRowsAsync(db, count: 1, idOffset: 100_000);
        var captureError = await Assert.ThrowsAsync<AutoGenPlanCapacityException>(() =>
            service.CaptureScopeAsync(request, CancellationToken.None));
        Assert.Contains(
            TeacherDraftsAutogenPlanService.MaxScopeRowCount.ToString(),
            captureError.Message,
            StringComparison.Ordinal);

        var plan = await db.AutoGenDraftPlans.SingleAsync(item => item.PlanId == fixture.PlanId);
        plan.InputFingerprint = await service.CaptureInputFingerprintAsync(request);
        plan.BeforeScopeRevision = LogicalRevisionToken.Combine(await db.TeacherDraftItems
            .AsNoTracking()
            .OrderBy(item => item.Id)
            .Select(item => new KeyValuePair<int, Guid>(item.Id, item.Revision))
            .ToListAsync());
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var applyError = await Assert.ThrowsAsync<AutoGenPlanCapacityException>(() =>
            service.ApplyAsync(fixture.PlanId, new AutoGenPlanActionRequest(1)));
        Assert.Contains(
            TeacherDraftsAutogenPlanService.MaxScopeRowCount.ToString(),
            applyError.Message,
            StringComparison.Ordinal);
        Assert.Equal(
            TeacherDraftsAutogenPlanService.MaxScopeRowCount + 1,
            await db.TeacherDraftItems.AsNoTracking().CountAsync());
        Assert.Equal(
            (int)AutoGenPlanState.Ready,
            await db.AutoGenDraftPlans.AsNoTracking()
                .Where(item => item.PlanId == fixture.PlanId)
                .Select(item => item.State)
                .SingleAsync());
    }

    [Fact]
    public async Task Fingerprint_and_plan_scope_honor_pre_canceled_tokens()
    {
        await using var fixture = await AutoGenPlanFixture.CreateAsync(AutoGenPlanOperation.Add);
        await using var db = new AppDbContext(fixture.Options);
        var service = new TeacherDraftsAutogenPlanService(db);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CaptureInputFingerprintAsync(CreatePlanFingerprintRequest(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CaptureScopeAsync(CreatePlanFingerprintRequest(), cancellation.Token));
    }

    [Fact]
    public async Task Input_fingerprint_ignores_configuration_of_an_unrelated_course()
    {
        await using var fixture = await AutoGenPlanFixture.CreateAsync(AutoGenPlanOperation.Add);
        var request = CreatePlanFingerprintRequest();
        string before;
        await using (var db = new AppDbContext(fixture.Options))
        {
            before = await new TeacherDraftsAutogenPlanService(db)
                .CaptureInputFingerprintAsync(request);
        }

        await using (var db = new AppDbContext(fixture.Options))
        {
            var course = new Course
            {
                Id = 200,
                Name = "Сторонній курс",
                DurationWeeks = 8,
                AcademicPeriodStartDate = request.FromDate
            };
            var group = new Group
            {
                Id = 200,
                Name = "Стороння група",
                StudentsCount = 20,
                CourseId = course.Id,
                Course = course
            };
            var module = new BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities.Module
            {
                Id = 200,
                Code = "OTHER",
                Title = "Сторонній модуль",
                Credits = 2,
                CourseId = course.Id,
                Course = course
            };
            db.AddRange(course, group, module);
            db.TimeSlots.Add(new TimeSlot
            {
                Id = 200,
                CourseId = course.Id,
                Course = course,
                DayOfWeek = DayOfWeek.Tuesday,
                Start = new TimeOnly(12, 0),
                End = new TimeOnly(13, 0),
                SortOrder = 3,
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(fixture.Options))
        {
            var after = await new TeacherDraftsAutogenPlanService(db)
                .CaptureInputFingerprintAsync(request);
            Assert.Equal(before, after);
        }
    }

    [Fact]
    public async Task Input_fingerprint_tracks_shared_teacher_and_room_occupancy_from_another_course()
    {
        await using var fixture = await AutoGenPlanFixture.CreateAsync(AutoGenPlanOperation.Add);
        var request = CreatePlanFingerprintRequest();
        const int teacherId = 210;
        const int roomId = 210;

        await using (var db = new AppDbContext(fixture.Options))
        {
            var module = await db.Modules.SingleAsync(item => item.Id == 1);
            var otherCourse = new Course
            {
                Id = 210,
                Name = "Курс спільних ресурсів",
                DurationWeeks = 8,
                AcademicPeriodStartDate = request.FromDate
            };
            var otherGroup = new Group
            {
                Id = 210,
                Name = "Група спільних ресурсів",
                StudentsCount = 15,
                CourseId = otherCourse.Id,
                Course = otherCourse
            };
            var otherModule = new BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities.Module
            {
                Id = 210,
                Code = "SHARED",
                Title = "Модуль спільних ресурсів",
                Credits = 1,
                CourseId = otherCourse.Id,
                Course = otherCourse
            };
            var teacher = new Teacher { Id = teacherId, FullName = "Спільний викладач" };
            var building = new Building { Id = 210, Name = "Спільний корпус" };
            var room = new Room
            {
                Id = roomId,
                Name = "Спільна аудиторія",
                Capacity = 30,
                BuildingId = building.Id,
                Building = building
            };
            db.AddRange(otherCourse, otherGroup, otherModule, teacher, building, room);
            db.TeacherModules.Add(new TeacherModule
            {
                TeacherId = teacher.Id,
                Teacher = teacher,
                ModuleId = module.Id,
                Module = module
            });
            await db.SaveChangesAsync();
        }

        string before;
        await using (var db = new AppDbContext(fixture.Options))
        {
            before = await new TeacherDraftsAutogenPlanService(db)
                .CaptureInputFingerprintAsync(request);
            db.ScheduleItems.Add(new ScheduleItem
            {
                Date = request.FromDate,
                DayOfWeek = request.FromDate.DayOfWeek,
                StartTime = new TimeOnly(9, 0),
                EndTime = new TimeOnly(10, 0),
                LessonTypeId = 1,
                GroupId = 210,
                ModuleId = 210,
                TeacherId = teacherId,
                RoomId = roomId
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(fixture.Options))
        {
            var after = await new TeacherDraftsAutogenPlanService(db)
                .CaptureInputFingerprintAsync(request);
            Assert.NotEqual(before, after);
        }
    }

    [Theory]
    [InlineData(-180, true)]
    [InlineData(180, true)]
    [InlineData(500, false)]
    public async Task Input_fingerprint_scopes_candidate_teacher_load_to_academic_period(
        int dayOffset,
        bool shouldChange)
    {
        await using var fixture = await AutoGenPlanFixture.CreateAsync(AutoGenPlanOperation.Add);
        var request = CreatePlanFingerprintRequest();
        const int teacherId = 220;

        await using (var db = new AppDbContext(fixture.Options))
        {
            var course = await db.Courses.SingleAsync(item => item.Id == 1);
            course.AcademicPeriodStartDate = request.FromDate.AddDays(-365);
            course.DurationWeeks = 104;
            var module = await db.Modules.SingleAsync(item => item.Id == 1);
            var teacher = new Teacher { Id = teacherId, FullName = "Викладач навантаження" };
            db.Teachers.Add(teacher);
            db.TeacherModules.Add(new TeacherModule
            {
                TeacherId = teacher.Id,
                Teacher = teacher,
                ModuleId = module.Id,
                Module = module
            });
            await db.SaveChangesAsync();
        }

        string before;
        await using (var db = new AppDbContext(fixture.Options))
        {
            before = await new TeacherDraftsAutogenPlanService(db)
                .CaptureInputFingerprintAsync(request);
            var loadDate = request.FromDate.AddDays(dayOffset);
            db.ScheduleItems.Add(new ScheduleItem
            {
                Date = loadDate,
                DayOfWeek = loadDate.DayOfWeek,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(9, 0),
                LessonTypeId = 1,
                GroupId = 1,
                ModuleId = 1,
                TeacherId = teacherId
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(fixture.Options))
        {
            var after = await new TeacherDraftsAutogenPlanService(db)
                .CaptureInputFingerprintAsync(request);
            if (shouldChange)
            {
                Assert.NotEqual(before, after);
            }
            else
            {
                Assert.Equal(before, after);
            }
        }
    }

    [Fact]
    public async Task Expired_plan_cleanup_removes_snapshots_and_keeps_job_history()
    {
        await using var fixture = await AutoGenPlanFixture.CreateAsync();
        await using (var db = new AppDbContext(fixture.Options))
        {
            var plan = await db.AutoGenDraftPlans.SingleAsync();
            plan.ExpiresAtUtc = DateTime.UtcNow.AddDays(-31);
            await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(fixture.Options))
        {
            Assert.Null(await new TeacherDraftsAutogenPlanService(db)
                .GetLatestRollbackableAsync(courseId: null));
        }

        await using (var verification = new AppDbContext(fixture.Options))
        {
            Assert.Empty(await verification.AutoGenDraftPlans.AsNoTracking().ToListAsync());
            Assert.Empty(await verification.AutoGenDraftPlanMutations.AsNoTracking().ToListAsync());
            Assert.Single(await verification.AutoGenJobRuns.AsNoTracking().ToListAsync());
            var planEntity = verification.Model.FindEntityType(typeof(AutoGenDraftPlan));
            var courseForeignKey = planEntity!.GetForeignKeys()
                .Single(key => key.PrincipalEntityType.ClrType == typeof(Course));
            Assert.Equal(DeleteBehavior.Cascade, courseForeignKey.DeleteBehavior);
        }
    }

    [Fact]
    public async Task Latest_rollbackable_plan_is_discoverable_until_rollback()
    {
        await using var fixture = await AutoGenPlanFixture.CreateAsync();
        AutoGenPlanDetailsDto applied;
        await using (var db = new AppDbContext(fixture.Options))
        {
            var service = new TeacherDraftsAutogenPlanService(db);
            applied = await service.ApplyAsync(fixture.PlanId, new AutoGenPlanActionRequest(1));
        }
        await using (var db = new AppDbContext(fixture.Options))
        {
            var service = new TeacherDraftsAutogenPlanService(db);
            var discovered = await service.GetLatestRollbackableAsync(courseId: 1);
            Assert.Equal(fixture.PlanId, discovered?.Summary.PlanId);
            Assert.Null(await service.GetLatestRollbackableAsync(courseId: 999));
        }
        await using (var db = new AppDbContext(fixture.Options))
        {
            await new TeacherDraftsAutogenPlanService(db).RollbackAsync(
                fixture.PlanId,
                new AutoGenPlanActionRequest(applied.Summary.Version));
        }
        await using (var db = new AppDbContext(fixture.Options))
        {
            Assert.Null(await new TeacherDraftsAutogenPlanService(db)
                .GetLatestRollbackableAsync(courseId: 1));
        }
    }

    [Fact]
    public async Task Plan_read_materializes_only_the_requested_bounded_page()
    {
        await using var fixture = await AutoGenPlanFixture.CreateAsync(AutoGenPlanOperation.Add);
        await using (var db = new AppDbContext(fixture.Options))
        {
            var plan = await db.AutoGenDraftPlans
                .Include(item => item.Mutations)
                .SingleAsync(item => item.PlanId == fixture.PlanId);
            var template = Assert.Single(plan.Mutations);
            for (var ordinal = 2; ordinal <= 450; ordinal++)
            {
                plan.Mutations.Add(new AutoGenDraftPlanMutation
                {
                    Ordinal = ordinal,
                    Operation = template.Operation,
                    SourceDraftId = template.SourceDraftId,
                    BeforeRevision = template.BeforeRevision,
                    BeforeJson = template.BeforeJson,
                    AfterJson = template.AfterJson
                });
            }
            plan.AddCount = 450;
            await db.SaveChangesAsync();
        }

        await using var readDb = new AppDbContext(fixture.Options);
        var service = new TeacherDraftsAutogenPlanService(readDb);
        var first = await service.GetDetailsPageAsync(fixture.PlanId, 0, 200);
        var middle = await service.GetDetailsPageAsync(fixture.PlanId, 200, 200);
        var last = await service.GetDetailsPageAsync(fixture.PlanId, 400, 200);

        Assert.Equal(450, first.TotalChanges);
        Assert.Equal(200, first.Changes.Count);
        Assert.True(first.HasMoreChanges);
        Assert.Equal(200, middle.ChangeOffset);
        Assert.Equal(200, middle.Changes.Count);
        Assert.Equal(50, last.Changes.Count);
        Assert.False(last.HasMoreChanges);
        Assert.Equal(Enumerable.Range(401, 50), last.Changes.Select(change => change.Ordinal));
        await Assert.ThrowsAsync<AutoGenPlanValidationException>(() =>
            service.GetDetailsPageAsync(
                fixture.PlanId,
                0,
                TeacherDraftsAutogenPlanService.MaxChangePageSize + 1));
    }

    [Fact]
    public async Task Plan_read_rejects_persisted_count_that_disagrees_with_mutations()
    {
        await using var fixture = await AutoGenPlanFixture.CreateAsync(AutoGenPlanOperation.Add);
        await using (var db = new AppDbContext(fixture.Options))
        {
            var plan = await db.AutoGenDraftPlans.SingleAsync(item => item.PlanId == fixture.PlanId);
            plan.AddCount = 2;
            await db.SaveChangesAsync();
        }

        await using var readDb = new AppDbContext(fixture.Options);
        var service = new TeacherDraftsAutogenPlanService(readDb);

        await Assert.ThrowsAsync<AutoGenPlanPersistenceException>(() =>
            service.GetDetailsPageAsync(fixture.PlanId, 0, 200));
        await Assert.ThrowsAsync<AutoGenPlanPersistenceException>(() =>
            service.GetDetailsAsync(fixture.PlanId));
    }

    [Fact]
    public async Task Plan_read_rejects_oversized_persisted_snapshot_before_deserialization()
    {
        await using var fixture = await AutoGenPlanFixture.CreateAsync(AutoGenPlanOperation.Add);
        await using (var db = new AppDbContext(fixture.Options))
        {
            var mutation = await db.AutoGenDraftPlanMutations.SingleAsync();
            mutation.AfterJson = new string('1', 8_193);
            await db.SaveChangesAsync();
        }

        await using var readDb = new AppDbContext(fixture.Options);
        var service = new TeacherDraftsAutogenPlanService(readDb);

        var error = await Assert.ThrowsAsync<AutoGenPlanPersistenceException>(() =>
            service.GetDetailsPageAsync(fixture.PlanId, 0, 200));
        Assert.Contains("8192", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_read_rejects_oversized_job_payload_before_deserialization()
    {
        await using var fixture = await AutoGenPlanFixture.CreateAsync(AutoGenPlanOperation.Add);
        await using (var db = new AppDbContext(fixture.Options))
        {
            var run = await db.AutoGenJobRuns.SingleAsync();
            run.RequestJson = new string('1',
                TeacherDraftsAutogenJobService.MaxPersistedPayloadCharacters + 1);
            await db.SaveChangesAsync();
        }

        await using var readDb = new AppDbContext(fixture.Options);
        var service = new TeacherDraftsAutogenPlanService(readDb);

        var error = await Assert.ThrowsAsync<AutoGenPlanPersistenceException>(() =>
            service.GetDetailsPageAsync(fixture.PlanId, 0, 200));
        Assert.Contains(
            TeacherDraftsAutogenJobService.MaxPersistedPayloadCharacters.ToString(),
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expired_plan_cleanup_deletes_mutations_in_bounded_batches_before_the_plan()
    {
        await using var fixture = await AutoGenPlanFixture.CreateAsync(AutoGenPlanOperation.Add);
        await using var db = new AppDbContext(fixture.Options);
        var plan = await db.AutoGenDraftPlans
            .Include(item => item.Mutations)
            .SingleAsync(item => item.PlanId == fixture.PlanId);
        plan.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        var template = Assert.Single(plan.Mutations);
        for (var index = 0; index < TeacherDraftsAutogenPlanService.CleanupMutationBatchSize; index++)
        {
            plan.Mutations.Add(new AutoGenDraftPlanMutation
            {
                Ordinal = index + 2,
                Operation = template.Operation,
                SourceDraftId = template.SourceDraftId,
                BeforeRevision = template.BeforeRevision,
                BeforeJson = template.BeforeJson,
                AfterJson = template.AfterJson
            });
        }
        plan.AddCount = plan.Mutations.Count;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var firstDeletedPlans = await TeacherDraftsAutogenPlanService.CleanupExpiredPlansAsync(db);

        Assert.Equal(0, firstDeletedPlans);
        Assert.True(await db.AutoGenDraftPlans.AsNoTracking().AnyAsync(item => item.PlanId == fixture.PlanId));
        Assert.Equal(1, await db.AutoGenDraftPlanMutations.AsNoTracking().CountAsync());

        var secondDeletedPlans = await TeacherDraftsAutogenPlanService.CleanupExpiredPlansAsync(db);

        Assert.Equal(1, secondDeletedPlans);
        Assert.False(await db.AutoGenDraftPlans.AsNoTracking().AnyAsync(item => item.PlanId == fixture.PlanId));
    }

    [Fact]
    public async Task Plan_read_allows_completed_persistence_handoff_before_observer_cleanup()
    {
        await using var fixture = await AutoGenPlanFixture.CreateAsync(AutoGenPlanOperation.Add);
        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(fixture.Options));
        services.AddScoped<TeacherDraftsAutogenPlanService>();
        await using var provider = services.BuildServiceProvider();
        var service = new TeacherDraftsAutogenJobService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new CapturingLogger<TeacherDraftsAutogenJobService>());
        var runtime = CreateRuntime(CreateValidRequest() with { ClientJobId = fixture.PlanId });
        var stateField = runtime.GetType().GetField(
            "_state",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(stateField);
        stateField.SetValue(runtime, AutoGenJobState.Succeeded);
        AddPrivateDictionaryEntry(service, "_jobs", fixture.PlanId, runtime);
        AddPrivateDictionaryEntry(service, "_runningTasks", fixture.PlanId, Task.CompletedTask);

        var plan = await service.GetPlanPageAsync(fixture.PlanId, 0, 200);

        Assert.Equal(fixture.PlanId, plan.Summary.PlanId);
        Assert.Single(plan.Changes);
    }

    [Fact]
    public async Task Plan_action_waits_for_the_same_execution_gate_as_generation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }
        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(options));
        services.AddScoped<TeacherDraftsAutogenPlanService>();
        await using var provider = services.BuildServiceProvider();
        var service = new TeacherDraftsAutogenJobService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new CapturingLogger<TeacherDraftsAutogenJobService>());
        var gate = await HoldGateAsync(service, "_executionGate");
        var applyTask = service.ApplyPlanAsync(
            Guid.NewGuid().ToString("N"),
            new AutoGenPlanActionRequest(1));

        try
        {
            await Task.Delay(75);
            Assert.False(applyTask.IsCompleted);
        }
        finally
        {
            gate.Release();
        }

        await Assert.ThrowsAsync<AutoGenPlanNotFoundException>(() => applyTask);
    }

    private static Task<int> InsertLessonTypesAsync(
        AppDbContext db,
        int count,
        int idOffset)
        => db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH digits(value) AS (
                VALUES (0), (1), (2), (3), (4), (5), (6), (7), (8), (9)
            ),
            numbers(value) AS (
                SELECT ones.value
                     + tens.value * 10
                     + hundreds.value * 100
                     + thousands.value * 1000
                     + ten_thousands.value * 10000
                FROM digits AS ones
                CROSS JOIN digits AS tens
                CROSS JOIN digits AS hundreds
                CROSS JOIN digits AS thousands
                CROSS JOIN digits AS ten_thousands
            )
            INSERT INTO LessonTypes (
                Id,
                Code,
                Name,
                IsActive,
                RequiresRoom,
                RequiresTeacher,
                BlocksRoom,
                BlocksTeacher,
                CountInPlan,
                CountInLoad,
                PreferredFirstInWeek,
                CssKey)
            SELECT value + {idOffset},
                   'CAP-' || (value + {idOffset}),
                   'Тип заняття для перевірки межі',
                   1,
                   0,
                   0,
                   0,
                   0,
                   1,
                   1,
                   0,
                   NULL
            FROM numbers
            WHERE value < {count};
            """);

    private static Task<int> InsertDraftRowsAsync(
        AppDbContext db,
        int count,
        int idOffset)
        => db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH digits(value) AS (
                VALUES (0), (1), (2), (3), (4), (5), (6), (7), (8), (9)
            ),
            numbers(value) AS (
                SELECT ones.value
                     + tens.value * 10
                     + hundreds.value * 100
                     + thousands.value * 1000
                     + ten_thousands.value * 10000
                FROM digits AS ones
                CROSS JOIN digits AS tens
                CROSS JOIN digits AS hundreds
                CROSS JOIN digits AS thousands
                CROSS JOIN digits AS ten_thousands
            )
            INSERT INTO TeacherDraftItems (
                Id,
                Revision,
                Date,
                DayOfWeek,
                StartTime,
                EndTime,
                LessonTypeId,
                GroupId,
                ModuleId,
                ModuleTopicId,
                TeacherId,
                RoomId,
                Status,
                PublishedItemId,
                BatchKey,
                ValidationWarnings,
                CreatedAt,
                UpdatedAt,
                IsLocked,
                IsSelfStudy,
                GenerationJobId)
            SELECT value + {idOffset},
                   printf('00000000-0000-0000-0000-%012d', value + {idOffset}),
                   '2026-07-06',
                   1,
                   '09:00:00',
                   '10:00:00',
                   1,
                   1,
                   1,
                   NULL,
                   NULL,
                   NULL,
                   0,
                   NULL,
                   NULL,
                   NULL,
                   '2026-07-06 09:00:00',
                   '2026-07-06 09:00:00',
                   0,
                   0,
                   NULL
            FROM numbers
            WHERE value < {count};
            """);

    private static AutoGenJobRequest CreateValidRequest()
        => new(
            Kind: AutoGenJobKind.Generate,
            FromDate: new DateOnly(2026, 9, 1),
            ToDate: new DateOnly(2026, 9, 7),
            CourseId: 1,
            GroupIds: new List<int> { 1 },
            ModuleHours: new Dictionary<int, int> { [1] = 1 },
            Days: WeekPreset.MonFri,
            ClearExisting: true,
            SoftFill: false,
            PreflightOnly: false);

    private static AutoGenJobRequest CreatePlanFingerprintRequest()
        => new(
            AutoGenJobKind.Generate,
            new DateOnly(2026, 7, 6),
            new DateOnly(2026, 7, 6),
            1,
            new List<int> { 1 },
            new Dictionary<int, int> { [1] = 1 },
            WeekPreset.MonFri,
            true,
            false,
            false,
            PreviewOnly: true);

    private static object CreateRuntime(AutoGenJobRequest request)
    {
        var runtimeType = typeof(TeacherDraftsAutogenJobService)
            .GetNestedType("AutoGenJobRuntime", BindingFlags.NonPublic);
        Assert.NotNull(runtimeType);
        var runtime = Activator.CreateInstance(
            runtimeType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { request },
            culture: null);
        return Assert.IsAssignableFrom<object>(runtime);
    }

    private static Task InvokeRunAsync(TeacherDraftsAutogenJobService service, object runtime)
    {
        var method = typeof(TeacherDraftsAutogenJobService)
            .GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task>(method.Invoke(service, new[] { runtime }));
    }

    private static AutoGenJobStatus GetStatus(object runtime)
    {
        var method = runtime.GetType().GetMethod("ToDto", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);
        return Assert.IsType<AutoGenJobStatus>(method.Invoke(runtime, null));
    }

    private static async Task<SemaphoreSlim> HoldGateAsync(
        TeacherDraftsAutogenJobService service,
        string fieldName)
    {
        var field = typeof(TeacherDraftsAutogenJobService).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        var gate = Assert.IsType<SemaphoreSlim>(field?.GetValue(service));
        await gate.WaitAsync();
        return gate;
    }

    private static int GetPrivateCollectionCount(
        TeacherDraftsAutogenJobService service,
        string fieldName)
    {
        var field = typeof(TeacherDraftsAutogenJobService).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        var collection = field?.GetValue(service);
        var count = collection?.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
        return Assert.IsType<int>(count?.GetValue(collection));
    }

    private static void AddPrivateDictionaryEntry(
        TeacherDraftsAutogenJobService service,
        string fieldName,
        string key,
        object value)
    {
        var field = typeof(TeacherDraftsAutogenJobService).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        var dictionary = field?.GetValue(service);
        Assert.NotNull(dictionary);
        var tryAdd = dictionary.GetType().GetMethod(
            "TryAdd",
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(tryAdd);
        Assert.True(Assert.IsType<bool>(tryAdd.Invoke(dictionary, new[] { key, value })));
    }

    private sealed class AutoGenPlanFixture : IAsyncDisposable
    {
        public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private AutoGenPlanFixture(
            SqliteConnection connection,
            DbContextOptions<AppDbContext> options,
            string planId,
            int originalDraftId,
            Guid originalRevision,
            DateTime originalCreatedAt)
        {
            Connection = connection;
            Options = options;
            PlanId = planId;
            OriginalDraftId = originalDraftId;
            OriginalRevision = originalRevision;
            OriginalCreatedAt = originalCreatedAt;
        }

        private SqliteConnection Connection { get; }
        public DbContextOptions<AppDbContext> Options { get; }
        public string PlanId { get; }
        public int OriginalDraftId { get; }
        public Guid OriginalRevision { get; }
        public DateTime OriginalCreatedAt { get; }

        public static async Task<AutoGenPlanFixture> CreateAsync(
            AutoGenPlanOperation operation = AutoGenPlanOperation.Delete)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
            await using var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var date = new DateOnly(2026, 7, 6);
            var course = new Course
            {
                Id = 1,
                Name = "Курс плану",
                DurationWeeks = 12,
                AcademicPeriodStartDate = date
            };
            var group = new Group
            {
                Id = 1,
                Name = "Група плану",
                StudentsCount = 10,
                CourseId = course.Id,
                Course = course
            };
            var module = new BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities.Module
            {
                Id = 1,
                Code = "PLAN",
                Title = "Модуль плану",
                Credits = 1,
                CourseId = course.Id,
                Course = course
            };
            var lessonType = new LessonTypeRef
            {
                Id = 1,
                Code = "PLAN",
                Name = "Заняття плану",
                IsActive = true,
                RequiresTeacher = false,
                RequiresRoom = false,
                BlocksTeacher = false,
                BlocksRoom = false,
                CountInPlan = true,
                CountInLoad = true
            };
            db.AddRange(course, group, module, lessonType);
            db.TimeSlots.Add(new TimeSlot
            {
                Id = 1,
                CourseId = course.Id,
                Course = course,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(9, 0),
                End = new TimeOnly(10, 0),
                SortOrder = 1,
                IsActive = true
            });
            db.TimeSlots.Add(new TimeSlot
            {
                Id = 2,
                CourseId = course.Id,
                Course = course,
                DayOfWeek = DayOfWeek.Monday,
                Start = new TimeOnly(10, 0),
                End = new TimeOnly(11, 0),
                SortOrder = 2,
                IsActive = true
            });
            TeacherDraftItem? draft = null;
            if (operation != AutoGenPlanOperation.Add)
            {
                draft = new TeacherDraftItem
                {
                    Date = date,
                    DayOfWeek = DayOfWeek.Monday,
                    StartTime = new TimeOnly(9, 0),
                    EndTime = new TimeOnly(10, 0),
                    LessonTypeId = lessonType.Id,
                    LessonType = lessonType,
                    GroupId = group.Id,
                    Group = group,
                    ModuleId = module.Id,
                    Module = module,
                    Status = DraftStatus.Draft,
                    IsLocked = false,
                    IsSelfStudy = false
                };
                db.TeacherDraftItems.Add(draft);
            }
            await db.SaveChangesAsync();

            var planId = Guid.NewGuid().ToString("N");
            var result = new AutoGenResult(
                operation == AutoGenPlanOperation.Add ? 1 : 0,
                0,
                new List<string>
                {
                    "Сформовано попередній план без зміни робочих чернеток. Застосуйте його окремою дією після перегляду."
                });
            var status = new AutoGenJobStatus(
                planId,
                AutoGenJobState.Succeeded,
                AutoGenJobKind.Generate,
                "Попередній план",
                "Готово.",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                date,
                date,
                1,
                1,
                1,
                date,
                date,
                date,
                result.Created,
                0,
                result.Warnings.Count,
                0,
                0,
                100,
                false,
                Result: result);
            var request = new AutoGenJobRequest(
                AutoGenJobKind.Generate,
                date,
                date,
                course.Id,
                new List<int> { group.Id },
                new Dictionary<int, int> { [module.Id] = 1 },
                WeekPreset.MonFri,
                true,
                false,
                false,
                PreviewOnly: true);
            var run = new AutoGenJobRun
            {
                JobId = planId,
                RequestHash = new string('a', 64),
                Version = 1,
                Kind = (int)AutoGenJobKind.Generate,
                State = (int)AutoGenJobState.Succeeded,
                Title = status.Title,
                CurrentStage = status.CurrentStage,
                CreatedAtUtc = DateTime.UtcNow,
                StartedAtUtc = DateTime.UtcNow,
                CompletedAtUtc = DateTime.UtcNow,
                RangeStartDate = date,
                RangeEndDate = date,
                TotalWeeks = 1,
                CompletedWeeks = 1,
                CurrentWeekNumber = 1,
                CurrentWeekStartDate = date,
                CurrentRangeStartDate = date,
                CurrentRangeEndDate = date,
                Percent = 100,
                RequestJson = JsonSerializer.Serialize(request, JsonOptions),
                StatusJson = JsonSerializer.Serialize(status, JsonOptions),
                ResultJson = JsonSerializer.Serialize(result, JsonOptions),
                UpdatedAtUtc = DateTime.UtcNow
            };
            db.AutoGenJobRuns.Add(run);
            await db.SaveChangesAsync();

            var originalSnapshot = draft is null
                ? null
                : CreateSnapshot(draft, lessonType.Name, group.Name, module.Title);
            FixtureSnapshot? proposedSnapshot = operation switch
            {
                AutoGenPlanOperation.Add => new FixtureSnapshot(
                    0,
                    Guid.Empty,
                    date,
                    DayOfWeek.Monday,
                    new TimeOnly(9, 0),
                    new TimeOnly(10, 0),
                    lessonType.Id,
                    lessonType.Name,
                    group.Id,
                    group.Name,
                    module.Id,
                    module.Title,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    DraftStatus.Draft,
                    null,
                    null,
                    null,
                    DateTime.UtcNow,
                    DateTime.UtcNow,
                    false,
                    false,
                    null),
                AutoGenPlanOperation.Update => originalSnapshot! with
                {
                    Revision = Guid.NewGuid(),
                    StartTime = new TimeOnly(10, 0),
                    EndTime = new TimeOnly(11, 0),
                    UpdatedAt = DateTime.UtcNow
                },
                _ => null
            };
            var inputFingerprint = await new TeacherDraftsAutogenPlanService(db)
                .CaptureInputFingerprintAsync(request);
            var plan = new AutoGenDraftPlan
            {
                PlanId = planId,
                AutoGenJobRunId = run.Id,
                State = (int)AutoGenPlanState.Ready,
                Version = 1,
                CourseId = course.Id,
                RangeStartDate = date,
                RangeEndDate = date,
                Days = (int)WeekPreset.MonFri,
                AllowIncompleteDrafts = false,
                GroupIdsJson = JsonSerializer.Serialize(new[] { group.Id }, JsonOptions),
                BeforeScopeRevision = draft is null
                    ? Guid.Empty
                    : LogicalRevisionToken.Combine(new[]
                    {
                        new KeyValuePair<int, Guid>(draft.Id, draft.Revision)
                    }),
                InputFingerprint = inputFingerprint,
                AddCount = operation == AutoGenPlanOperation.Add ? 1 : 0,
                UpdateCount = operation == AutoGenPlanOperation.Update ? 1 : 0,
                DeleteCount = operation == AutoGenPlanOperation.Delete ? 1 : 0,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            };
            plan.Mutations.Add(new AutoGenDraftPlanMutation
            {
                Ordinal = 1,
                Operation = (int)operation,
                SourceDraftId = originalSnapshot?.Id,
                BeforeRevision = originalSnapshot?.Revision,
                BeforeJson = originalSnapshot is null
                    ? null
                    : JsonSerializer.Serialize(originalSnapshot, JsonOptions),
                AfterJson = proposedSnapshot is null
                    ? null
                    : JsonSerializer.Serialize(proposedSnapshot, JsonOptions)
            });
            db.AutoGenDraftPlans.Add(plan);
            await db.SaveChangesAsync();
            return new AutoGenPlanFixture(
                connection,
                options,
                planId,
                draft?.Id ?? 0,
                draft?.Revision ?? Guid.Empty,
                draft?.CreatedAt ?? default);
        }

        private static FixtureSnapshot CreateSnapshot(
            TeacherDraftItem draft,
            string lessonTypeName,
            string groupName,
            string moduleName)
            => new(
                draft.Id,
                draft.Revision,
                draft.Date,
                draft.DayOfWeek,
                draft.StartTime,
                draft.EndTime,
                draft.LessonTypeId,
                lessonTypeName,
                draft.GroupId,
                groupName,
                draft.ModuleId,
                moduleName,
                draft.ModuleTopicId,
                null,
                draft.TeacherId,
                null,
                draft.RoomId,
                null,
                draft.Status,
                draft.PublishedItemId,
                draft.BatchKey,
                draft.ValidationWarnings,
                draft.CreatedAt,
                draft.UpdatedAt,
                draft.IsLocked,
                draft.IsSelfStudy,
                draft.GenerationJobId);

        private sealed record FixtureSnapshot(
            int Id,
            Guid Revision,
            DateOnly Date,
            DayOfWeek DayOfWeek,
            TimeOnly StartTime,
            TimeOnly EndTime,
            int LessonTypeId,
            string LessonTypeName,
            int GroupId,
            string GroupName,
            int ModuleId,
            string ModuleName,
            int? ModuleTopicId,
            string? TopicCode,
            int? TeacherId,
            string? TeacherName,
            int? RoomId,
            string? RoomName,
            DraftStatus Status,
            int? PublishedItemId,
            string? BatchKey,
            string? ValidationWarnings,
            DateTime CreatedAt,
            DateTime UpdatedAt,
            bool IsLocked,
            bool IsSelfStudy,
            string? GenerationJobId);

        public async ValueTask DisposeAsync()
            => await Connection.DisposeAsync();
    }

    private sealed class ThrowingScopeFactory(Func<Exception> exceptionFactory) : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
            => throw exceptionFactory();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<LogEntry> _entries = new();
        private readonly object _sync = new();

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_sync)
                {
                    return _entries.ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel)
            => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_sync)
            {
                _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
            }
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
}
