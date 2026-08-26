namespace BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

public static class AutoGenGapReasonCodes
{
    public const string Unknown = "unknown";
    public const string Teacher = "teacher";
    public const string Room = "room";
    public const string Travel = "travel";
    public const string TopicOrder = "topic-order";
    public const string ModuleBlock = "module-block";
    public const string Limit = "limit";
    public const string SearchLimit = "search-limit";
    public const string SharedFlow = "shared-flow";
    public const string Other = "other";
}

public sealed record AutoGenGapReasonClassification(string Code, string Title);

// Єдина класифікація причин для API, звітів і клієнтських журналів.
public static class AutoGenGapReasonClassifier
{
    public static AutoGenGapReasonClassification Classify(AutoGenGapDetail gap)
    {
        ArgumentNullException.ThrowIfNull(gap);
        return Classify(gap.Reason, gap.ReasonCode, gap.ConstraintCode, gap.SearchLimitReached);
    }

    public static AutoGenGapReasonClassification Classify(
        string? reason,
        string? reasonCode = null,
        string? constraintCode = null,
        bool searchLimitReached = false)
    {
        var explicitCode = ClassifyStructuredCode(reasonCode);
        if (explicitCode is not null)
        {
            return FromCode(explicitCode);
        }

        if (searchLimitReached)
        {
            return FromCode(AutoGenGapReasonCodes.SearchLimit);
        }

        var constraintCategory = ClassifyConstraintCode(constraintCode);
        if (constraintCategory is not null)
        {
            return FromCode(constraintCategory);
        }

        return ClassifyLegacyReason(reason);
    }

    public static AutoGenGapDetail EnsureStructured(AutoGenGapDetail gap)
    {
        ArgumentNullException.ThrowIfNull(gap);
        var classification = Classify(gap);
        return gap with
        {
            ReasonCode = classification.Code,
            ConstraintCode = NormalizeOptionalCode(gap.ConstraintCode),
            SearchLimitReached = gap.SearchLimitReached
                                 || classification.Code == AutoGenGapReasonCodes.SearchLimit
        };
    }

    public static string TitleFor(string? code)
        => FromCode(
            ClassifyStructuredCode(code)
            ?? (string.IsNullOrWhiteSpace(code) ? AutoGenGapReasonCodes.Unknown : AutoGenGapReasonCodes.Other)).Title;

    private static AutoGenGapReasonClassification ClassifyLegacyReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return FromCode(AutoGenGapReasonCodes.Unknown);
        }

        var text = reason.Trim().ToLowerInvariant();
        if (text.Contains("причину не вдалося визначити", StringComparison.Ordinal))
        {
            return FromCode(AutoGenGapReasonCodes.Unknown);
        }

        if (ContainsAny(
                text,
                "межі пошуку",
                "межу пошуку",
                "ліміт пошуку",
                "ліміту пошуку",
                "пошук зупинено",
                "бюджет пошуку",
                "безпечного ліміту",
                "search limit",
                "search budget",
                "node budget"))
        {
            return FromCode(AutoGenGapReasonCodes.SearchLimit);
        }

        if (ContainsAny(text, "спільн", "поток"))
        {
            return FromCode(AutoGenGapReasonCodes.SharedFlow);
        }

        if (ContainsAny(
                text,
                "хронолог",
                "порядок тем",
                "порядком тем",
                "попередньої тем",
                "попередні тем",
                "кодом теми",
                "коду теми",
                "вичерпано теми",
                "планові теми"))
        {
            return FromCode(AutoGenGapReasonCodes.TopicOrder);
        }

        if (ContainsAny(
                text,
                "суцільним блоком",
                "суцільний блок",
                "розриває блок",
                "розірвати блок",
                "сегментів модуля",
                "сегменти модуля"))
        {
            return FromCode(AutoGenGapReasonCodes.ModuleBlock);
        }

        if (ContainsAny(text, "перехід", "переход", "переміщення між корпус")
            || text.Contains("корпус", StringComparison.Ordinal)
               && ContainsAny(text, "хв", "перерв", "відстан"))
        {
            return FromCode(AutoGenGapReasonCodes.Travel);
        }

        var mentionsTeacher = ContainsAny(text, "викладач", "кафедр");
        var mentionsRoom = ContainsAny(text, "аудитор", "місткіст", "приміщенн");
        if (mentionsTeacher && mentionsRoom)
        {
            return FromCode(AutoGenGapReasonCodes.Other);
        }

        if (mentionsTeacher)
        {
            return FromCode(AutoGenGapReasonCodes.Teacher);
        }

        if (mentionsRoom)
        {
            return FromCode(AutoGenGapReasonCodes.Room);
        }

        if (ContainsAny(
                text,
                "ліміт",
                "обмеж",
                "максимум пар",
                "більше двох",
                "максимум модулів",
                "слот до першого"))
        {
            return FromCode(AutoGenGapReasonCodes.Limit);
        }

        return FromCode(AutoGenGapReasonCodes.Other);
    }

    private static string? ClassifyStructuredCode(string? code)
    {
        var normalized = NormalizeOptionalCode(code);
        if (normalized is null)
        {
            return null;
        }

        return normalized switch
        {
            AutoGenGapReasonCodes.Unknown => AutoGenGapReasonCodes.Unknown,
            AutoGenGapReasonCodes.Teacher => AutoGenGapReasonCodes.Teacher,
            AutoGenGapReasonCodes.Room => AutoGenGapReasonCodes.Room,
            AutoGenGapReasonCodes.Travel => AutoGenGapReasonCodes.Travel,
            AutoGenGapReasonCodes.TopicOrder => AutoGenGapReasonCodes.TopicOrder,
            AutoGenGapReasonCodes.ModuleBlock => AutoGenGapReasonCodes.ModuleBlock,
            AutoGenGapReasonCodes.Limit => AutoGenGapReasonCodes.Limit,
            AutoGenGapReasonCodes.SearchLimit => AutoGenGapReasonCodes.SearchLimit,
            AutoGenGapReasonCodes.SharedFlow => AutoGenGapReasonCodes.SharedFlow,
            AutoGenGapReasonCodes.Other => AutoGenGapReasonCodes.Other,
            _ => ClassifyConstraintCode(normalized) ?? AutoGenGapReasonCodes.Other
        };
    }

    private static string? ClassifyConstraintCode(string? constraintCode)
    {
        var code = NormalizeOptionalCode(constraintCode);
        if (code is null)
        {
            return null;
        }

        if (ContainsAny(code, "search-limit", "search-budget", "node-limit", "node-budget", "trial-limit", "time-limit"))
        {
            return AutoGenGapReasonCodes.SearchLimit;
        }

        if (ContainsAny(code, "shared-flow", "shared-lecture", "stream"))
        {
            return AutoGenGapReasonCodes.SharedFlow;
        }

        if (ContainsAny(code, "topic-order", "topic-sequence", "chronolog"))
        {
            return AutoGenGapReasonCodes.TopicOrder;
        }

        if (ContainsAny(code, "module-block", "module-segment", "contiguous"))
        {
            return AutoGenGapReasonCodes.ModuleBlock;
        }

        if (ContainsAny(code, "travel", "transition"))
        {
            return AutoGenGapReasonCodes.Travel;
        }

        if (ContainsAny(code, "teacher", "department"))
        {
            return AutoGenGapReasonCodes.Teacher;
        }

        if (ContainsAny(code, "room", "capacity", "building"))
        {
            return AutoGenGapReasonCodes.Room;
        }

        if (ContainsAny(code, "daily-limit", "day-limit", "slot-limit", "lesson-limit", "module-limit", "max-lessons"))
        {
            return AutoGenGapReasonCodes.Limit;
        }

        return null;
    }

    private static AutoGenGapReasonClassification FromCode(string code)
        => code switch
        {
            AutoGenGapReasonCodes.Unknown => new(code, "Причину не визначено"),
            AutoGenGapReasonCodes.Teacher => new(code, "Немає доступного викладача"),
            AutoGenGapReasonCodes.Room => new(code, "Немає доступної аудиторії"),
            AutoGenGapReasonCodes.Travel => new(code, "Недостатньо часу на перехід"),
            AutoGenGapReasonCodes.TopicOrder => new(code, "Немає доступних годин тем"),
            AutoGenGapReasonCodes.ModuleBlock => new(code, "Модуль має йти суцільним блоком"),
            AutoGenGapReasonCodes.Limit => new(code, "Спрацювали денні або слотні ліміти"),
            AutoGenGapReasonCodes.SearchLimit => new(code, "Досягнуто безпечної межі пошуку"),
            AutoGenGapReasonCodes.SharedFlow => new(code, "Спільний потік не готовий"),
            _ => new(AutoGenGapReasonCodes.Other, "Інші причини")
        };

    private static bool ContainsAny(string text, params string[] values)
        => values.Any(value => text.Contains(value, StringComparison.Ordinal));

    private static string NormalizeCode(string code)
        => code.Trim()
            .ToLowerInvariant()
            .Replace('_', '-')
            .Replace(' ', '-');

    private static string? NormalizeOptionalCode(string? code)
        => string.IsNullOrWhiteSpace(code) ? null : NormalizeCode(code);
}
