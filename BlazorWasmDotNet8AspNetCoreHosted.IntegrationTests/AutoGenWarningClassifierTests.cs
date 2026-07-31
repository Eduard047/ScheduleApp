using System.Reflection;
using System.Text.Json;
using BlazorWasmDotNet8AspNetCoreHosted.Client.Services;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class AutoGenWarningClassifierTests
{
    [Theory]
    [InlineData(
        "Ігноровано модулі, що не належать курсу #2: 17.",
        AutoGenWarningCodes.InputAdjusted,
        AutoGenWarningSeverities.Warning,
        AutoGenWarningCategories.Input)]
    [InlineData(
        "Попередня перевірка ресурсів не знайшла явних дефіцитів.",
        AutoGenWarningCodes.PreflightClear,
        AutoGenWarningSeverities.Info,
        AutoGenWarningCategories.Preview)]
    [InlineData(
        "[2026-09-01] Група 1: [search-limit] фінальний repair-pass досягнув межі вузлів.",
        AutoGenWarningCodes.SearchLimit,
        AutoGenWarningSeverities.Warning,
        AutoGenWarningCategories.Optimization)]
    [InlineData(
        "Створено 2 неповних чернеток: без викладача — 1, без аудиторії — 1.",
        AutoGenWarningCodes.IncompleteDrafts,
        AutoGenWarningSeverities.Warning,
        AutoGenWarningCategories.Validation)]
    [InlineData(
        "Фінальна перевірка ресурсів прибрала 3 небезпечні чернетки перед збереженням.",
        AutoGenWarningCodes.UnsafeDraftRemoved,
        AutoGenWarningSeverities.Warning,
        AutoGenWarningCategories.Validation)]
    [InlineData(
        "Зміни автогенерації повністю відкочено, тому жодної нової чернетки не збережено.",
        AutoGenWarningCodes.GenerationRolledBack,
        AutoGenWarningSeverities.Error,
        AutoGenWarningCategories.Persistence)]
    [InlineData(
        "Фінальна синхронізація застосувала 4 однозначні переміщення чернеток із repair-проходу.",
        AutoGenWarningCodes.OptimizationApplied,
        AutoGenWarningSeverities.Info,
        AutoGenWarningCategories.Optimization)]
    [InlineData(
        "Помилка автогенерації: сервер не відповідає.",
        AutoGenWarningCodes.JobFailed,
        AutoGenWarningSeverities.Error,
        AutoGenWarningCategories.General)]
    [InlineData(
        "Операцію скасовано користувачем.",
        AutoGenWarningCodes.JobCanceled,
        AutoGenWarningSeverities.Info,
        AutoGenWarningCategories.General)]
    [InlineData(
        "Сформовано попередній план без зміни робочих чернеток. Застосуйте його окремою дією після перегляду.",
        AutoGenWarningCodes.PreviewCompleted,
        AutoGenWarningSeverities.Info,
        AutoGenWarningCategories.Preview)]
    [InlineData(
        "Фінальна перевірка: викладач #5 перетинається з наявним заняттям.",
        AutoGenWarningCodes.FinalValidationFailed,
        AutoGenWarningSeverities.Error,
        AutoGenWarningCategories.Validation)]
    public void Legacy_warning_is_mapped_to_stable_structure(
        string warning,
        string expectedCode,
        string expectedSeverity,
        string expectedCategory)
    {
        var detail = AutoGenWarningClassifier.Classify(warning);

        Assert.Equal(expectedCode, detail.Code);
        Assert.Equal(expectedSeverity, detail.Severity);
        Assert.Equal(expectedCategory, detail.Category);
        Assert.Equal(warning, detail.Message);
        Assert.Null(detail.Context);
    }

    [Fact]
    public void Unknown_warning_is_preserved_and_uses_safe_defaults()
    {
        const string warning = "Нова українська діагностика без відомого шаблону.";

        var detail = AutoGenWarningClassifier.Classify(warning);

        Assert.Equal(AutoGenWarningCodes.General, detail.Code);
        Assert.Equal(AutoGenWarningSeverities.Warning, detail.Severity);
        Assert.Equal(AutoGenWarningCategories.General, detail.Category);
        Assert.Equal(warning, detail.Message);
    }

    [Fact]
    public void Warning_collection_is_deduplicated_deterministically()
    {
        const string first = "Автогенерація не заповнила слот 08:30-09:50 для групи А на 2026-09-01.";
        const string second = "Пробну генерацію завершено без збереження чернеток.";

        var details = AutoGenWarningClassifier.ClassifyMany(new[]
        {
            first,
            "  " + first + "  ",
            string.Empty,
            "   ",
            second
        });

        Assert.Collection(
            details,
            detail =>
            {
                Assert.Equal(first, detail.Message);
                Assert.Equal(AutoGenWarningCodes.GapUnfilled, detail.Code);
            },
            detail =>
            {
                Assert.Equal(second, detail.Message);
                Assert.Equal(AutoGenWarningCodes.PreviewCompleted, detail.Code);
            });
    }

    [Fact]
    public void Report_builder_always_populates_structured_warning_details()
    {
        const string warning = "Для модуля <Фізика> у групі А повторно використано тему Т1.";

        var result = InvokeBuildResult(new[] { warning, warning, " " });

        Assert.Equal(new[] { warning }, result.Warnings);
        var detail = Assert.Single(Assert.IsType<List<AutoGenWarningDetail>>(result.WarningDetails));
        Assert.Equal(AutoGenWarningCodes.TopicReused, detail.Code);
        Assert.Equal(warning, detail.Message);

        var emptyResult = InvokeBuildResult(Array.Empty<string>());
        Assert.NotNull(emptyResult.WarningDetails);
        Assert.Empty(emptyResult.WarningDetails);
    }

    [Fact]
    public void Legacy_result_json_without_warning_details_remains_compatible()
    {
        const string json = """
            {
              "Created": 2,
              "Skipped": 1,
              "Warnings": ["Групи не знайдено."]
            }
            """;

        var result = JsonSerializer.Deserialize<AutoGenResult>(json);

        Assert.NotNull(result);
        Assert.Equal(2, result.Created);
        Assert.Equal(new[] { "Групи не знайдено." }, result.Warnings);
        Assert.Null(result.WarningDetails);
    }

    private static AutoGenResult InvokeBuildResult(IEnumerable<string> warnings)
    {
        var builderType = typeof(TeacherDraftsAutogenService).Assembly.GetType(
            "BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts.TeacherDraftsAutogenReportBuilder",
            throwOnError: true)!;
        var method = builderType.GetMethod(
            "BuildResult",
            BindingFlags.Public | BindingFlags.Static)!;

        return Assert.IsType<AutoGenResult>(method.Invoke(
            null,
            new object[]
            {
                1,
                0,
                warnings,
                Array.Empty<AutoGenGapDetail>(),
                Array.Empty<AutoGenPreflightItem>()
            }));
    }
}

public sealed class AdminScheduleAutoGenStateTests
{
    [Theory]
    [InlineData("2026-09-01", "2026-08-31")]
    [InlineData("2026-09-06", "2026-08-31")]
    [InlineData("2026-09-07", "2026-09-07")]
    public void Status_week_key_uses_monday_of_job_range(string date, string expectedWeekKey)
    {
        var result = AdminScheduleAutoGenState.BuildWeekKey(DateOnly.Parse(date));

        Assert.Equal(expectedWeekKey, result);
    }

    [Theory]
    [InlineData(null, 3, true)]
    [InlineData(3, 3, true)]
    [InlineData(3, 4, false)]
    [InlineData(3, null, false)]
    public void Persisted_status_is_not_restored_for_another_course(
        int? persistedCourseId,
        int? selectedCourseId,
        bool expected)
    {
        Assert.Equal(
            expected,
            AdminScheduleAutoGenState.CanRestoreStatusForCourse(
                persistedCourseId,
                selectedCourseId));
    }

    [Fact]
    public void Technical_warnings_do_not_duplicate_separately_rendered_gaps()
    {
        var gaps = Enumerable.Range(1, 49)
            .Select(index => new AutoGenGapDetail(
                GroupId: index,
                GroupName: $"Група {index}",
                Date: new DateOnly(2026, 9, 1),
                Start: new TimeOnly(9, 0),
                End: new TimeOnly(9, 45),
                SlotLabel: "09:00-09:45",
                Reason: "Немає доступного ресурсу."))
            .ToList();

        var duplicatedSlotWarnings = gaps
            .Select(gap =>
                $"Автогенерація не заповнила слот {gap.SlotLabel} для групи {gap.GroupName} на {gap.Date:yyyy-MM-dd}. Причина: {gap.Reason}")
            .Append("Попередження сервера.")
            .ToList();
        var withServerWarnings = AdminScheduleAutoGenState.BuildTechnicalWarningMessages(
            duplicatedSlotWarnings,
            gaps);

        Assert.Equal(new[] { "Попередження сервера." }, withServerWarnings);
    }

    [Fact]
    public void Structured_gap_warning_is_removed_but_other_metadata_is_preserved()
    {
        var gap = new AutoGenGapDetail(
            GroupId: 9302,
            GroupName: "9302",
            Date: new DateOnly(2026, 9, 1),
            Start: new TimeOnly(9, 0),
            End: new TimeOnly(9, 45),
            SlotLabel: "09:00-09:45",
            Reason: "Немає доступного ресурсу.");
        var gapMessage =
            $"Автогенерація не заповнила слот {gap.SlotLabel} для групи {gap.GroupName} на {gap.Date:yyyy-MM-dd}. Причина: {gap.Reason}";
        const string serverMessage = "Фінальна перевірка потребує ручної уваги.";
        var expectedContext = new Dictionary<string, string>
        {
            ["group"] = "9302"
        };
        var result = AdminScheduleAutoGenState.BuildTechnicalWarningDetails(
            warnings: new[] { gapMessage, serverMessage },
            structuredWarnings: new[]
            {
                new AutoGenWarningDetail(
                    AutoGenWarningCodes.GapUnfilled,
                    AutoGenWarningSeverities.Warning,
                    AutoGenWarningCategories.Optimization,
                    gapMessage),
                new AutoGenWarningDetail(
                    AutoGenWarningCodes.FinalValidationFailed,
                    AutoGenWarningSeverities.Error,
                    AutoGenWarningCategories.Validation,
                    serverMessage,
                    expectedContext)
            },
            separatelyRenderedGapDetails: new[] { gap });

        var remaining = Assert.Single(result);
        Assert.Equal(serverMessage, remaining.Message);
        Assert.Equal(AutoGenWarningCodes.FinalValidationFailed, remaining.Code);
        Assert.Equal(AutoGenWarningSeverities.Error, remaining.Severity);
        Assert.Equal(AutoGenWarningCategories.Validation, remaining.Category);
        Assert.Same(expectedContext, remaining.Context);
    }

    [Fact]
    public void Slot_warning_without_corresponding_gap_is_preserved()
    {
        const string warning =
            "Автогенерація не заповнила слот 10:00-10:45 для групи Інша на 2026-09-02. Причина: окрема діагностика.";

        var result = AdminScheduleAutoGenState.BuildTechnicalWarningMessages(
            new[] { warning },
            Array.Empty<AutoGenGapDetail>());

        Assert.Equal(new[] { warning }, result);
    }

    [Fact]
    public void Plan_summary_reports_updates_when_created_counter_is_zero()
    {
        var plan = CreatePlan(addCount: 0, updateCount: 7, deleteCount: 0);

        var summary = AdminScheduleAutoGenState.BuildPlanChangeSummary(plan);

        Assert.Equal("додати 0, змінити або перемістити 7, видалити 0", summary);
    }

    [Fact]
    public void Apply_conflict_invalidates_ready_plan()
    {
        var plan = CreatePlan(addCount: 1, updateCount: 2, deleteCount: 0);

        var invalidated = AdminScheduleAutoGenState.InvalidatePlanAfterConflict(plan);

        Assert.Equal(AutoGenPlanState.Expired, invalidated.State);
        Assert.False(invalidated.CanApply);
        Assert.Equal(plan.PlanId, invalidated.PlanId);
        Assert.Equal(plan.Version, invalidated.Version);
    }

    [Fact]
    public void Latest_request_guard_rejects_stale_response_and_explicit_invalidation()
    {
        var guard = new LatestAsyncRequestGuard();

        var first = guard.Begin();
        var second = guard.Begin();

        Assert.False(guard.IsCurrent(first));
        Assert.True(guard.IsCurrent(second));

        guard.Invalidate();

        Assert.False(guard.IsCurrent(second));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Preflight_rollback_is_reported_only_for_failed_execution(bool failed, bool expected)
    {
        var request = CreateJobRequest(AutoGenJobKind.Preflight, preflightOnly: true, previewOnly: false);

        Assert.Equal(expected, InvokeShouldReportExecutionRollback(request, failed));
    }

    [Fact]
    public void Successful_preview_rollback_is_not_reported_as_persistence_error()
    {
        var request = CreateJobRequest(AutoGenJobKind.Fill, preflightOnly: false, previewOnly: true);

        Assert.False(InvokeShouldReportExecutionRollback(request, failed: false));
    }

    private static AutoGenPlanSummaryDto CreatePlan(int addCount, int updateCount, int deleteCount)
    {
        var now = DateTimeOffset.UtcNow;
        return new AutoGenPlanSummaryDto(
            PlanId: "plan-1",
            State: AutoGenPlanState.Ready,
            Version: 3,
            CreatedAt: now,
            ExpiresAt: now.AddMinutes(10),
            AppliedAt: null,
            RolledBackAt: null,
            AddCount: addCount,
            UpdateCount: updateCount,
            DeleteCount: deleteCount,
            CanApply: true,
            CanRollback: false);
    }

    private static AutoGenJobRequest CreateJobRequest(
        AutoGenJobKind kind,
        bool preflightOnly,
        bool previewOnly)
        => new(
            Kind: kind,
            FromDate: new DateOnly(2026, 9, 1),
            ToDate: new DateOnly(2026, 9, 12),
            CourseId: 3,
            GroupIds: new List<int> { 1 },
            ModuleHours: new Dictionary<int, int> { [1] = 1 },
            Days: WeekPreset.MonSat,
            ClearExisting: false,
            SoftFill: kind == AutoGenJobKind.Fill,
            PreflightOnly: preflightOnly,
            PreviewOnly: previewOnly);

    private static bool InvokeShouldReportExecutionRollback(AutoGenJobRequest request, bool failed)
    {
        var method = typeof(TeacherDraftsAutogenJobService).GetMethod(
            "ShouldReportExecutionRollback",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<bool>(method.Invoke(null, new object[] { request, failed }));
    }
}
