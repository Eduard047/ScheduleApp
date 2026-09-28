using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class AutoGenGapReasonClassifierTests
{
    [Theory]
    [InlineData(null, AutoGenGapReasonCodes.Unknown)]
    [InlineData("Тема #2 модуля #1 потребує перед собою 2 аудиторних годин лекційної теми #1 (проведено 1, бракує 1).", AutoGenGapReasonCodes.TopicOrder)]
    [InlineData("Немає викладачів для модулів: #1.", AutoGenGapReasonCodes.Teacher)]
    [InlineData("Не знайдено аудиторій для модуля у цьому слоті.", AutoGenGapReasonCodes.Room)]
    [InlineData("Недостатньо часу на перехід до іншого корпусу: доступно 5 хв.", AutoGenGapReasonCodes.Travel)]
    [InlineData("Порушується хронологічний порядок тем модуля.", AutoGenGapReasonCodes.TopicOrder)]
    [InlineData("Модуль ставимо суцільним блоком у межах дня.", AutoGenGapReasonCodes.ModuleBlock)]
    [InlineData("Досягнуто максимум пар на день для групи.", AutoGenGapReasonCodes.Limit)]
    [InlineData("Спільну лекційну тему не створено одиночним потоком.", AutoGenGapReasonCodes.SharedFlow)]
    [InlineData("Пошук зупинено після досягнення безпечного ліміту.", AutoGenGapReasonCodes.SearchLimit)]
    [InlineData("Причину не вдалося визначити автоматично. Перевірте викладачів та аудиторії.", AutoGenGapReasonCodes.Unknown)]
    [InlineData("Не вдалося підібрати комбінацію викладач/аудиторія: усі варіанти зайняті.", AutoGenGapReasonCodes.Other)]
    public void Legacy_reason_is_mapped_consistently(string? reason, string expectedCode)
    {
        var classification = AutoGenGapReasonClassifier.Classify(reason);

        Assert.Equal(expectedCode, classification.Code);
        Assert.False(string.IsNullOrWhiteSpace(classification.Title));
    }

    [Fact]
    public void Structured_diagnostics_have_stable_priority()
    {
        var explicitReason = CreateGap(
            reason: "Немає викладача.",
            reasonCode: AutoGenGapReasonCodes.Room,
            constraintCode: "teacher-availability");
        var searchLimited = CreateGap(
            reason: "Не знайдено безпечного варіанта.",
            searchLimitReached: true);
        var constrained = CreateGap(
            reason: "Не знайдено безпечного варіанта.",
            constraintCode: "topic_sequence");
        var preciseSearchLimited = CreateGap(
            reason: "Немає доступного викладача.",
            reasonCode: AutoGenGapReasonCodes.Teacher,
            constraintCode: "teacher-availability",
            searchLimitReached: true);

        Assert.Equal(AutoGenGapReasonCodes.Room, AutoGenGapReasonClassifier.Classify(explicitReason).Code);
        Assert.Equal(AutoGenGapReasonCodes.SearchLimit, AutoGenGapReasonClassifier.Classify(searchLimited).Code);
        Assert.Equal(AutoGenGapReasonCodes.TopicOrder, AutoGenGapReasonClassifier.Classify(constrained).Code);
        Assert.Equal(AutoGenGapReasonCodes.Teacher, AutoGenGapReasonClassifier.Classify(preciseSearchLimited).Code);
        Assert.True(AutoGenGapReasonClassifier.EnsureStructured(preciseSearchLimited).SearchLimitReached);
    }

    [Theory]
    [InlineData("strict-lecture-prerequisite")]
    [InlineData("module-sequence")]
    public void Curriculum_blockers_are_explained_as_ordering_constraints(string constraint)
    {
        var gap = AutoGenGapReasonClassifier.ExplainShortage(CreateGap(null, constraintCode: constraint));
        Assert.Equal(AutoGenGapReasonCodes.TopicOrder, gap.ReasonCode);
        Assert.Equal("ordering", gap.Diagnostics!["shortageCategory"]);
        Assert.False(gap.SearchLimitReached);
    }

    [Fact]
    public void Structured_codes_are_normalized_to_public_categories()
    {
        var gap = CreateGap(
            reason: "Технічна причина.",
            reasonCode: "ROOM_CAPACITY",
            constraintCode: "ROOM_CAPACITY");

        var enriched = AutoGenGapReasonClassifier.EnsureStructured(gap);

        Assert.Equal(AutoGenGapReasonCodes.Room, enriched.ReasonCode);
        Assert.Equal("room-capacity", enriched.ConstraintCode);
    }

    [Fact]
    public void Old_json_keeps_safe_defaults_and_can_be_enriched()
    {
        const string json = """
            {
              "GroupId": 7,
              "GroupName": "Група 7",
              "Date": "2026-09-01",
              "Start": "08:30:00",
              "End": "09:50:00",
              "SlotLabel": "08:30-09:50",
              "Reason": "Немає викладачів для модуля."
            }
            """;

        var legacy = System.Text.Json.JsonSerializer.Deserialize<AutoGenGapDetail>(json);

        Assert.NotNull(legacy);
        Assert.Null(legacy.ReasonCode);
        Assert.Null(legacy.ConstraintCode);
        Assert.False(legacy.SearchLimitReached);
        Assert.Null(legacy.Diagnostics);

        var enriched = AutoGenGapReasonClassifier.EnsureStructured(legacy);
        Assert.Equal(AutoGenGapReasonCodes.Teacher, enriched.ReasonCode);
        Assert.False(enriched.SearchLimitReached);
    }

    [Theory]
    [InlineData("teacher", false, "resource-availability")]
    [InlineData("room", true, "resource-availability")]
    [InlineData("topic-order", false, "ordering")]
    [InlineData("module-block", true, "ordering")]
    [InlineData("shared-flow", false, "ordering")]
    [InlineData("travel", false, "placement-constraint")]
    [InlineData("limit", false, "placement-constraint")]
    [InlineData("search-limit", true, "search-limit")]
    [InlineData("unknown", false, "unknown")]
    public void Shortage_explanation_separates_observed_blocker_from_search_and_feasibility(
        string code, bool limited, string category)
    {
        var originalDiagnostics = new Dictionary<string, string> { ["visitedNodes"] = "123" };
        var original = CreateGap("Причина", reasonCode: code, searchLimitReached: limited)
            with { Diagnostics = originalDiagnostics };
        var result = AutoGenGapReasonClassifier.ExplainShortage(original);
        Assert.Equal(code, result.ReasonCode);
        Assert.Equal(category, result.Diagnostics!["shortageCategory"]);
        Assert.Equal(limited ? "limited" : "no-limit-reported", result.Diagnostics["searchStatus"]);
        Assert.Equal("not-proven", result.Diagnostics["feasibilityStatus"]);
        Assert.Equal("123", result.Diagnostics["visitedNodes"]);
        Assert.False(string.IsNullOrWhiteSpace(result.Diagnostics["explanation"]));
        Assert.Single(originalDiagnostics);
        var twice = AutoGenGapReasonClassifier.ExplainShortage(result);
        Assert.Equal(result.Diagnostics.OrderBy(pair => pair.Key), twice.Diagnostics!.OrderBy(pair => pair.Key));
    }

    private static AutoGenGapDetail CreateGap(
        string? reason,
        string? reasonCode = null,
        string? constraintCode = null,
        bool searchLimitReached = false)
        => new(
            GroupId: 1,
            GroupName: "Група 1",
            Date: new DateOnly(2026, 9, 1),
            Start: new TimeOnly(8, 30),
            End: new TimeOnly(9, 50),
            SlotLabel: "08:30-09:50",
            Reason: reason,
            ReasonCode: reasonCode,
            ConstraintCode: constraintCode,
            SearchLimitReached: searchLimitReached);
}
