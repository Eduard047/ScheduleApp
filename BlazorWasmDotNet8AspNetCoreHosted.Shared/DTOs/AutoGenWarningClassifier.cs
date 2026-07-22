namespace BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

public static class AutoGenWarningSeverities
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Error = "error";
}

public static class AutoGenWarningCategories
{
    public const string General = "general";
    public const string Input = "input";
    public const string Configuration = "configuration";
    public const string Resources = "resources";
    public const string Scheduling = "scheduling";
    public const string Optimization = "optimization";
    public const string Diagnostics = "diagnostics";
    public const string Validation = "validation";
    public const string Persistence = "persistence";
    public const string Preview = "preview";
}

public static class AutoGenWarningCodes
{
    public const string General = "autogen-warning";
    public const string InputAdjusted = "input-adjusted";
    public const string ConfigurationMissing = "configuration-missing";
    public const string PreflightDeficit = "preflight-deficit";
    public const string PreflightClear = "preflight-clear";
    public const string PreviewCompleted = "preview-completed";
    public const string GapUnfilled = "gap-unfilled";
    public const string SearchLimit = "search-limit";
    public const string TopicExhausted = "topic-exhausted";
    public const string TopicReused = "topic-reused";
    public const string ResourceUnavailable = "resource-unavailable";
    public const string OptimizationApplied = "optimization-applied";
    public const string Recommendation = "recommendation";
    public const string DiagnosticSummary = "diagnostic-summary";
    public const string IncompleteDrafts = "incomplete-drafts";
    public const string UnsafeDraftRemoved = "unsafe-draft-removed";
    public const string FinalValidationFailed = "final-validation-failed";
    public const string GenerationRolledBack = "generation-rolled-back";
    public const string RangeIncomplete = "range-incomplete";
    public const string PersistenceCleanupFailed = "persistence-cleanup-failed";
    public const string JobFailed = "job-failed";
    public const string JobCanceled = "job-canceled";
}

// Єдина детермінована класифікація старих текстових попереджень автогенерації.
public static class AutoGenWarningClassifier
{
    public static AutoGenWarningDetail Classify(string warning)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(warning);

        var message = warning.Trim();
        var text = message.ToLowerInvariant();

        if (text.StartsWith("помилка автогенерації:", StringComparison.Ordinal))
        {
            return Detail(
                AutoGenWarningCodes.JobFailed,
                AutoGenWarningSeverities.Error,
                AutoGenWarningCategories.General,
                message);
        }

        if (ContainsAny(
                text,
                "операцію скасовано",
                "скасовано користувачем",
                "скасовано під час завершення роботи сервера"))
        {
            return Detail(
                AutoGenWarningCodes.JobCanceled,
                AutoGenWarningSeverities.Info,
                AutoGenWarningCategories.General,
                message);
        }

        if (ContainsAny(
                text,
                "повністю відкочено",
                "повернуто до стану до запуску",
                "жодної нової чернетки не збережено"))
        {
            return Detail(
                AutoGenWarningCodes.GenerationRolledBack,
                AutoGenWarningSeverities.Error,
                AutoGenWarningCategories.Persistence,
                message);
        }

        if (text.StartsWith("фінальна перевірка:", StringComparison.Ordinal)
            || ContainsAny(
                text,
                "не вдалося повністю прибрати небезпечну",
                "фінальна перевірка знайшла ще")
            || text.Contains("фінальна перевірка", StringComparison.Ordinal)
               && ContainsAny(text, "помил", "поруш", "конфлікт"))
        {
            return Detail(
                AutoGenWarningCodes.FinalValidationFailed,
                AutoGenWarningSeverities.Error,
                AutoGenWarningCategories.Validation,
                message);
        }

        if (ContainsAny(
                text,
                "діапазон не згенеровано повністю",
                "сервер не повернув результат автогенерації"))
        {
            return Detail(
                AutoGenWarningCodes.RangeIncomplete,
                AutoGenWarningSeverities.Error,
                AutoGenWarningCategories.Persistence,
                message);
        }

        if (text.Contains("база даних підтвердила збереження", StringComparison.Ordinal)
            && ContainsAny(text, "очищення", "ресурсу повернуло помилку"))
        {
            return Detail(
                AutoGenWarningCodes.PersistenceCleanupFailed,
                AutoGenWarningSeverities.Warning,
                AutoGenWarningCategories.Persistence,
                message);
        }

        if (text.StartsWith("ігноровано ", StringComparison.Ordinal)
            || text.StartsWith("параметр дозволу генерації", StringComparison.Ordinal))
        {
            return Detail(
                AutoGenWarningCodes.InputAdjusted,
                AutoGenWarningSeverities.Warning,
                AutoGenWarningCategories.Input,
                message);
        }

        if (ContainsAny(
                text,
                "типи занять відсутні",
                "групи не знайдено",
                "відсутній модуль із ідентифікатором",
                "не знайдено жодного слоту розкладу"))
        {
            return Detail(
                AutoGenWarningCodes.ConfigurationMissing,
                AutoGenWarningSeverities.Warning,
                AutoGenWarningCategories.Configuration,
                message);
        }

        if (text.StartsWith("попередня перевірка ресурсів не знайшла", StringComparison.Ordinal))
        {
            return Detail(
                AutoGenWarningCodes.PreflightClear,
                AutoGenWarningSeverities.Info,
                AutoGenWarningCategories.Preview,
                message);
        }

        if (text.StartsWith("попередня перевірка ресурсів:", StringComparison.Ordinal))
        {
            return Detail(
                AutoGenWarningCodes.PreflightDeficit,
                AutoGenWarningSeverities.Warning,
                AutoGenWarningCategories.Resources,
                message);
        }

        if (ContainsAny(
                text,
                "пробну генерацію завершено без збереження",
                "сформовано попередній план без зміни робочих чернеток"))
        {
            return Detail(
                AutoGenWarningCodes.PreviewCompleted,
                AutoGenWarningSeverities.Info,
                AutoGenWarningCategories.Preview,
                message);
        }

        if (text.StartsWith("рекомендація автогенерації:", StringComparison.Ordinal))
        {
            return Detail(
                AutoGenWarningCodes.Recommendation,
                AutoGenWarningSeverities.Info,
                AutoGenWarningCategories.Diagnostics,
                message);
        }

        if (text.StartsWith("зведення причин незаповнених слотів:", StringComparison.Ordinal))
        {
            return Detail(
                AutoGenWarningCodes.DiagnosticSummary,
                AutoGenWarningSeverities.Info,
                AutoGenWarningCategories.Diagnostics,
                message);
        }

        if (ContainsAny(
                text,
                "[search-limit]",
                "межі пошуку",
                "межу пошуку",
                "ліміт пошуку",
                "ліміту пошуку",
                "безпечної межі пошуку",
                "безпечного ліміту пошуку",
                "бюджет пошуку",
                "search limit",
                "search budget"))
        {
            return Detail(
                AutoGenWarningCodes.SearchLimit,
                AutoGenWarningSeverities.Warning,
                AutoGenWarningCategories.Optimization,
                message);
        }

        if (ContainsAny(text, "створено", "залишено")
            && ContainsAny(text, "неповних чернет", "без викладача", "без аудиторії"))
        {
            return Detail(
                AutoGenWarningCodes.IncompleteDrafts,
                AutoGenWarningSeverities.Warning,
                AutoGenWarningCategories.Validation,
                message);
        }

        if (text.Contains("чернетку прибрано перед збереженням", StringComparison.Ordinal)
            || text.Contains("фінальна перевірка ресурсів прибрала", StringComparison.Ordinal)
               && ContainsAny(text, "небезпечну чернет", "небезпечні чернет"))
        {
            return Detail(
                AutoGenWarningCodes.UnsafeDraftRemoved,
                AutoGenWarningSeverities.Warning,
                AutoGenWarningCategories.Validation,
                message);
        }

        if (text.Contains("повторно використано тему", StringComparison.Ordinal))
        {
            return Detail(
                AutoGenWarningCodes.TopicReused,
                AutoGenWarningSeverities.Warning,
                AutoGenWarningCategories.Scheduling,
                message);
        }

        if (ContainsAny(
                text,
                "вичерпано теми",
                "лише міжзборові теми"))
        {
            return Detail(
                AutoGenWarningCodes.TopicExhausted,
                AutoGenWarningSeverities.Warning,
                AutoGenWarningCategories.Scheduling,
                message);
        }

        if (ContainsAny(
                text,
                "автогенерація не заповнила слот",
                "незаповнених слот",
                "порожніми слотами"))
        {
            return Detail(
                AutoGenWarningCodes.GapUnfilled,
                AutoGenWarningSeverities.Warning,
                AutoGenWarningCategories.Scheduling,
                message);
        }

        if (ContainsAny(
                text,
                "не знайшов повного безпечного призначення",
                "не знайшов повного набору обов'язкових ресурсів",
                "відхилив комбінацію ресурсів",
                "не знайдено керівник",
                "немає доступного викладача",
                "не знайдено викладач",
                "немає доступної аудиторії",
                "не знайдено аудитор",
                "не вдалося підібрати викладача",
                "не вдалося підібрати аудиторію"))
        {
            return Detail(
                AutoGenWarningCodes.ResourceUnavailable,
                AutoGenWarningSeverities.Warning,
                AutoGenWarningCategories.Resources,
                message);
        }

        if (ContainsAny(
                text,
                "repair-pass",
                "фінальний repair",
                "оптимізатор перебудував",
                "заняття пересунуто",
                "фінальна синхронізація застосувала",
                "matching виконав"))
        {
            return Detail(
                AutoGenWarningCodes.OptimizationApplied,
                AutoGenWarningSeverities.Info,
                AutoGenWarningCategories.Optimization,
                message);
        }

        return Detail(
            AutoGenWarningCodes.General,
            AutoGenWarningSeverities.Warning,
            AutoGenWarningCategories.General,
            message);
    }

    public static List<AutoGenWarningDetail> ClassifyMany(IEnumerable<string>? warnings)
    {
        var details = new List<AutoGenWarningDetail>();
        var seenMessages = new HashSet<string>(StringComparer.Ordinal);
        if (warnings is null)
        {
            return details;
        }

        foreach (var warning in warnings)
        {
            if (string.IsNullOrWhiteSpace(warning))
            {
                continue;
            }

            var message = warning.Trim();
            if (seenMessages.Add(message))
            {
                details.Add(Classify(message));
            }
        }

        return details;
    }

    private static AutoGenWarningDetail Detail(
        string code,
        string severity,
        string category,
        string message)
        => new(code, severity, category, message);

    private static bool ContainsAny(string text, params string[] values)
        => values.Any(value => text.Contains(value, StringComparison.Ordinal));
}
