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
    {
        var module = string.IsNullOrWhiteSpace(gap.ModuleName)
            ? gap.ModuleId is int moduleId ? $"модуль #{moduleId}" : "модуль не визначено"
            : CompactReportText(gap.ModuleName!, 80);
        var reason = ClassifyGapReason(gap.Reason).Title.ToLowerInvariant();
        return $"{gap.Date:yyyy-MM-dd} {gap.SlotLabel}, {gap.GroupName}, {module}: {reason}";
    }

    private static string CompactReportText(string text, int maxLength)
    {
        var normalized = string.IsNullOrWhiteSpace(text) ? "—" : text.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "...";
    }

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
            recommendations.Add(BuildPreflightRecommendation(item, example));
        }
        foreach (var item in gapSummary.Take(5))
        {
            recommendations.Add(BuildGapRecommendation(item));
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

    private static string BuildGapRecommendation(AutoGenGapSummaryItem item)
    {
        var example = item.Examples.FirstOrDefault();
        var exampleSuffix = string.IsNullOrWhiteSpace(example) ? string.Empty : $" Приклад: {example}";
        return item.Code switch
        {
            "teacher" => $"{item.Count} слотів вперлися у викладачів. Перевірте прив'язку до модуля, робочі години та зайнятість.{exampleSuffix}",
            "room" => $"{item.Count} слотів вперлися в аудиторії. Перевірте місткість, дозволені корпуси та зайнятість у потрібний час.{exampleSuffix}",
            "travel" => $"{item.Count} слотів не стали через переходи між корпусами. Поставте сусідні пари ближче або збільшіть перерву.{exampleSuffix}",
            "topic-order" => $"{item.Count} слотів заблокував порядок тем. Перевірте, чи попередні теми вже поставлені перед наступними.{exampleSuffix}",
            "module-block" => $"{item.Count} слотів не стали через правило суцільного модуля. Тримайте пари одного модуля поруч у межах дня.{exampleSuffix}",
            "limit" => $"{item.Count} слотів зупинили денні або слотні ліміти. Додайте навчальний час або зменште години в цьому діапазоні.{exampleSuffix}",
            "shared-flow" => $"{item.Count} слотів пов'язані зі спільним потоком. Вирівняйте тему, викладача й аудиторію для груп потоку.{exampleSuffix}",
            _ => $"{item.Count} слотів треба перевірити вручну за групою, модулем і часом.{exampleSuffix}"
        };
    }

    private static string BuildPreflightRecommendation(AutoGenPreflightItem item, string? example)
    {
        var exampleSuffix = string.IsNullOrWhiteSpace(example) ? string.Empty : $" Приклад: {example}";
        return item.Code switch
        {
            "slot" => $"{item.Count} слотів бракує у сітці груп. Додайте навчальний час або звільніть уже зайняті пари.{exampleSuffix}",
            "teacher" => $"{item.Count} викладацьких слотів бракує. Перевірте прив'язку викладачів, робочі години та зайнятість.{exampleSuffix}",
            "room" => $"{item.Count} аудиторних слотів бракує. Додайте або звільніть аудиторії потрібної місткості.{exampleSuffix}",
            "building" => $"{item.Count} слотів заблокували налаштування корпусів. Розширте дозволені корпуси або пріоритетні аудиторії групи.{exampleSuffix}",
            "travel" => $"{item.Count} слотів відсіяли переходи. Підберіть ближчі аудиторії або збільшіть перерву.{exampleSuffix}",
            "topic-order" => $"{item.Count} годин не мають доступних тем. Додайте теми потрібного типу або зменште години модуля.{exampleSuffix}",
            _ => $"{item.Title}: {item.Count}. {item.Recommendation}{exampleSuffix}"
        };
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
