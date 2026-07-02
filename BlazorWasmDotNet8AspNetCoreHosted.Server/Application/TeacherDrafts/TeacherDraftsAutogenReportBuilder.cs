using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

internal static class TeacherDraftsAutogenReportBuilder
{
    public static AutoGenResult BuildResult(
        int created,
        int skipped,
        IEnumerable<string> warnings,
        IEnumerable<AutoGenGapDetail> gapDetails,
        IEnumerable<AutoGenPreflightItem> preflight)
    {
        var gaps = gapDetails.ToList();
        var preflightItems = MergePreflight(preflight);
        return new AutoGenResult(
            created,
            skipped,
            warnings.Where(warning => !string.IsNullOrWhiteSpace(warning)).Distinct(StringComparer.Ordinal).ToList(),
            gaps,
            BuildGapSummary(gaps),
            preflightItems);
    }

    public static AutoGenRunReport BuildReport(DateOnly fromDate, DateOnly toDate, int totalWeeks, AutoGenResult result)
    {
        var gaps = result.GapDetails ?? new();
        var preflight = result.Preflight ?? new();
        var gapSummary = result.GapSummary ?? BuildGapSummary(gaps);
        var worstGroups = gaps
            .GroupBy(gap => new { gap.GroupId, gap.GroupName })
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.GroupName, StringComparer.Ordinal)
            .Take(8)
            .Select(group => new AutoGenRunReportGroupItem(
                group.Key.GroupId,
                group.Key.GroupName,
                group.Count(),
                group.Take(4).Select(FormatGapExample).ToList()))
            .ToList();
        var worstModules = gaps
            .GroupBy(gap => new
            {
                gap.ModuleId,
                ModuleName = string.IsNullOrWhiteSpace(gap.ModuleName)
                    ? gap.ModuleId is int moduleId ? $"Модуль #{moduleId}" : "Модуль не визначено"
                    : gap.ModuleName!
            })
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.ModuleName, StringComparer.Ordinal)
            .Take(8)
            .Select(group => new AutoGenRunReportModuleItem(
                group.Key.ModuleId,
                group.Key.ModuleName,
                group.Count(),
                group.Take(4).Select(FormatGapExample).ToList()))
            .ToList();
        return new AutoGenRunReport(
            DateTimeOffset.UtcNow,
            fromDate,
            toDate,
            Math.Max(1, totalWeeks),
            result.Created,
            result.Skipped,
            result.Warnings.Count,
            gaps.Count,
            preflight.Sum(item => item.Count),
            gapSummary,
            preflight,
            worstGroups,
            worstModules,
            BuildRecommendations(gapSummary, preflight, worstGroups, worstModules));
    }

    private static string FormatGapExample(AutoGenGapDetail gap)
        => $"{gap.Date:yyyy-MM-dd} {gap.SlotLabel}, {gap.GroupName}";

    private static List<string> BuildRecommendations(
        IReadOnlyList<AutoGenGapSummaryItem> gapSummary,
        IReadOnlyList<AutoGenPreflightItem> preflight,
        IReadOnlyList<AutoGenRunReportGroupItem> worstGroups,
        IReadOnlyList<AutoGenRunReportModuleItem> worstModules)
    {
        var recommendations = new List<string>();
        foreach (var item in preflight.OrderByDescending(item => item.Count).Take(5))
        {
            var example = item.Examples.FirstOrDefault();
            recommendations.Add(string.IsNullOrWhiteSpace(example)
                ? item.Recommendation
                : $"{item.Recommendation} Приклад: {example}");
        }
        foreach (var item in gapSummary.Take(5))
        {
            recommendations.Add(item.Code switch
            {
                "teacher" => "Є порожні слоти через викладачів: відкрийте робочі години або додайте викладача до проблемного модуля.",
                "room" => "Є порожні слоти через аудиторії: звільніть аудиторію потрібної місткості або розширте дозволені аудиторії/корпуси.",
                "travel" => "Є порожні слоти через переходи: поставте сусідні заняття в один корпус або збільшіть перерву між корпусами.",
                "topic-order" => "Є порожні слоти через порядок тем: додайте раніші слоти для попередніх тем або зменште години модуля в цьому діапазоні.",
                "module-block" => "Є порожні слоти через розрив модуля: залиште поруч кілька слотів під один модуль або перенесіть зайву пару дня.",
                "limit" => "Є порожні слоти через ліміти дня: додайте слоти/дні або зменште години для проблемної групи.",
                _ => "Відкрийте таблицю порожніх слотів нижче: там показано групу, час, модуль і першу дію."
            });
        }
        if (worstGroups.Count > 0)
        {
            recommendations.Add($"Почніть з груп: {string.Join(", ", worstGroups.Take(3).Select(group => $"{group.GroupName} ({group.GapCount})"))}.");
        }
        if (worstModules.Count > 0)
        {
            recommendations.Add($"Першими перевірте модулі: {string.Join(", ", worstModules.Take(3).Select(module => $"{module.ModuleName} ({module.GapCount})"))}.");
        }
        return recommendations
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .ToList();
    }

    private static (string Code, string Title) ClassifyGapReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return ("unknown", "Причину не визначено");
        }
        var text = reason.ToLowerInvariant();
        if (text.Contains("викладач", StringComparison.Ordinal))
        {
            return ("teacher", "Немає доступного викладача");
        }
        if (text.Contains("аудитор", StringComparison.Ordinal))
        {
            return ("room", "Немає доступної аудиторії");
        }
        if (text.Contains("перех", StringComparison.Ordinal) || text.Contains("корпус", StringComparison.Ordinal))
        {
            return ("travel", "Недостатньо часу на перехід");
        }
        if (text.Contains("тем", StringComparison.Ordinal) || text.Contains("хронолог", StringComparison.Ordinal))
        {
            return ("topic-order", "Порядок тем не дозволив слот");
        }
        if (text.Contains("блок", StringComparison.Ordinal))
        {
            return ("module-block", "Модуль має йти суцільним блоком");
        }
        if (text.Contains("ліміт", StringComparison.Ordinal) || text.Contains("обмеж", StringComparison.Ordinal))
        {
            return ("limit", "Спрацювали денні або слотні ліміти");
        }
        if (text.Contains("спільн", StringComparison.Ordinal))
        {
            return ("shared-flow", "Спільний потік не готовий");
        }
        return ("other", "Інші причини");
    }

    private static List<AutoGenGapSummaryItem> BuildGapSummary(IEnumerable<AutoGenGapDetail> gapDetails)
        => gapDetails
            .GroupBy(gap => ClassifyGapReason(gap.Reason))
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.Title, StringComparer.Ordinal)
            .Select(group => new AutoGenGapSummaryItem(
                group.Key.Code,
                group.Key.Title,
                group.Count(),
                group.Take(5).Select(FormatGapExample).ToList()))
            .ToList();

    private static List<AutoGenPreflightItem> MergePreflight(IEnumerable<AutoGenPreflightItem> items)
        => items
            .GroupBy(item => item.Code, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                return new AutoGenPreflightItem(
                    first.Code,
                    first.Title,
                    group.Sum(item => item.Count),
                    first.Recommendation,
                    group.SelectMany(item => item.Examples)
                        .Where(example => !string.IsNullOrWhiteSpace(example))
                        .Distinct(StringComparer.Ordinal)
                        .Take(5)
                        .ToList());
            })
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Title, StringComparer.Ordinal)
            .ToList();
}
