using System.Reflection;
using System.Text.Json;
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
