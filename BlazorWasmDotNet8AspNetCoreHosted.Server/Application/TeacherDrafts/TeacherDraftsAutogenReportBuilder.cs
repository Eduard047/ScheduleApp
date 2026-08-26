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
        var warningDetails = AutoGenWarningClassifier.ClassifyMany(warnings);
        var gaps = gapDetails
            .Select(AutoGenGapReasonClassifier.EnsureStructured)
            .ToList();
        var preflightItems = MergePreflight(preflight);
        return new AutoGenResult(
            created,
            skipped,
            warningDetails.Select(detail => detail.Message).ToList(),
            gaps,
            BuildGapSummary(gaps),
            preflightItems,
            warningDetails);
    }

    public static AutoGenRunReport BuildReport(DateOnly fromDate, DateOnly toDate, int totalWeeks, AutoGenResult result)
    {
        var gaps = (result.GapDetails ?? new())
            .Select(AutoGenGapReasonClassifier.EnsureStructured)
            .ToList();
        var preflight = result.Preflight ?? new();
        var gapSummary = BuildGapSummary(gaps);
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
        var reason = AutoGenGapReasonClassifier.Classify(gap).Title.ToLowerInvariant();
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
            AutoGenGapReasonCodes.Teacher => $"{item.Count} пар не вдалося поставити через викладачів. Перевірте прив'язку до модуля, робочі години та зайнятість.{exampleSuffix}",
            AutoGenGapReasonCodes.Room => $"{item.Count} пар не вдалося поставити через аудиторії. Перевірте місткість, дозволені корпуси та зайнятість у потрібний час.{exampleSuffix}",
            AutoGenGapReasonCodes.Travel => $"{item.Count} пар не вдалося поставити через зміну аудиторії або перехід між корпусами. Залиште сусідні пари в тій самій аудиторії або збільшіть перерву.{exampleSuffix}",
            AutoGenGapReasonCodes.TopicOrder => $"Для {item.Count} пар немає доступних годин тем потрібного типу. Перевірте теми та план модуля.{exampleSuffix}",
            AutoGenGapReasonCodes.ModuleBlock => $"{item.Count} пар не вдалося поставити через правило суцільного модуля. Тримайте пари одного модуля поруч у межах дня.{exampleSuffix}",
            AutoGenGapReasonCodes.Limit => $"{item.Count} пар зупинили денні обмеження. Додайте навчальний час або зменште кількість пар у цьому діапазоні.{exampleSuffix}",
            AutoGenGapReasonCodes.SearchLimit => $"{item.Count} пар лишилися після досягнення безпечної межі пошуку. Перевірте ресурси й повторіть генерацію після виправлення найвужчих обмежень.{exampleSuffix}",
            AutoGenGapReasonCodes.SharedFlow => $"{item.Count} пар пов'язані зі спільним потоком. Вирівняйте тему, викладача й аудиторію для груп потоку.{exampleSuffix}",
            _ => $"{item.Count} пар треба перевірити вручну за групою, модулем і часом.{exampleSuffix}"
        };
    }

    private static string BuildPreflightRecommendation(AutoGenPreflightItem item, string? example)
    {
        var exampleSuffix = string.IsNullOrWhiteSpace(example) ? string.Empty : $" Приклад: {example}";
        return item.Code switch
        {
            "calendar-capacity" => $"{item.Count} пар не поміщаються у вибрані дати. Зробіть додатковий день робочим для потрібного курсу або групи, оберіть довший період чи додайте час до сітки занять.{exampleSuffix}",
            "slot" => $"{item.Count} пар бракує у сітці груп. Додайте навчальний час або звільніть уже зайняті пари.{exampleSuffix}",
            "teacher" => $"{item.Count} пар не вдалося поставити через викладачів. Перевірте прив'язку викладачів, робочі години та зайнятість.{exampleSuffix}",
            "room" => $"{item.Count} пар не вдалося поставити через аудиторії. Додайте або звільніть аудиторії потрібної місткості.{exampleSuffix}",
            "building" => $"{item.Count} пар заблокували налаштування корпусів. Розширте дозволені корпуси або пріоритетні аудиторії групи.{exampleSuffix}",
            "travel" => $"{item.Count} пар не вдалося поставити через переходи. Підберіть ближчі аудиторії або збільшіть перерву.{exampleSuffix}",
            "topic-order" => $"{item.Count} пар не мають доступних тем. Додайте теми потрібного типу або зменште план модуля.{exampleSuffix}",
            _ => $"{item.Title}: {item.Count}. {item.Recommendation}{exampleSuffix}"
        };
    }

    private static List<AutoGenGapSummaryItem> BuildGapSummary(IEnumerable<AutoGenGapDetail> gapDetails)
        => gapDetails
            .GroupBy(AutoGenGapReasonClassifier.Classify)
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
