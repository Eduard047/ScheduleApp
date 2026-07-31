using System.Threading;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

namespace BlazorWasmDotNet8AspNetCoreHosted.Client.Services;

// Допоміжні перетворення стану автогенерації для сторінки адміністратора.
public static class AdminScheduleAutoGenState
{
    // Формує стабільний ключ тижня за понеділком незалежно від дня в діапазоні.
    public static string BuildWeekKey(DateOnly date)
    {
        var daysFromMonday = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-daysFromMonday).ToString("yyyy-MM-dd");
    }

    // Не відновлює статус іншого курсу; старі записи без курсу залишає сумісними.
    public static bool CanRestoreStatusForCourse(int? persistedCourseId, int? selectedCourseId)
        => persistedCourseId is null || persistedCourseId == selectedCourseId;

    // Залишає у технічних повідомленнях лише попередження сервера, бо прогалини показуються окремим списком.
    public static List<string> BuildTechnicalWarningMessages(
        IEnumerable<string>? warnings,
        IEnumerable<AutoGenGapDetail>? separatelyRenderedGapDetails)
        => BuildTechnicalWarningDetails(
                warnings,
                structuredWarnings: null,
                separatelyRenderedGapDetails)
            .Select(detail => detail.Message)
            .ToList();

    // Об'єднує текстові та структуровані попередження, не дублюючи прогалини з окремої таблиці.
    public static List<AutoGenWarningDetail> BuildTechnicalWarningDetails(
        IEnumerable<string>? warnings,
        IEnumerable<AutoGenWarningDetail>? structuredWarnings,
        IEnumerable<AutoGenGapDetail>? separatelyRenderedGapDetails)
    {
        var renderedGapPrefixes = separatelyRenderedGapDetails?
            .Select(BuildRenderedGapWarningPrefix)
            .ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var result = new List<AutoGenWarningDetail>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        bool Add(AutoGenWarningDetail detail)
        {
            if (string.IsNullOrWhiteSpace(detail.Message))
            {
                return false;
            }
            var normalized = detail with { Message = detail.Message.Trim() };
            if (renderedGapPrefixes.Any(prefix =>
                    normalized.Message.StartsWith(prefix, StringComparison.Ordinal))
                || !seen.Add(normalized.Message))
            {
                return false;
            }
            result.Add(normalized);
            return true;
        }

        if (structuredWarnings is not null)
        {
            foreach (var detail in structuredWarnings)
            {
                Add(detail);
            }
        }
        foreach (var detail in AutoGenWarningClassifier.ClassifyMany(warnings))
        {
            Add(detail);
        }
        return result;
    }

    private static string BuildRenderedGapWarningPrefix(AutoGenGapDetail gap)
        => $"Автогенерація не заповнила слот {gap.SlotLabel} для групи {gap.GroupName} на {gap.Date:yyyy-MM-dd}.";

    // Формує узгоджений підсумок змін без прив'язки до лічильника лише нових чернеток.
    public static string BuildPlanChangeSummary(AutoGenPlanSummaryDto plan)
        => $"додати {plan.AddCount}, змінити або перемістити {plan.UpdateCount}, видалити {plan.DeleteCount}";

    // Забороняє повторно застосовувати план після конфлікту його версії або вхідних даних.
    public static AutoGenPlanSummaryDto InvalidatePlanAfterConflict(AutoGenPlanSummaryDto plan)
        => plan with
        {
            State = AutoGenPlanState.Expired,
            CanApply = false
        };
}

// Захищає стан сторінки від результату застарілого асинхронного запиту.
public sealed class LatestAsyncRequestGuard
{
    private long _version;

    public long Begin()
        => Interlocked.Increment(ref _version);

    public bool IsCurrent(long version)
        => Volatile.Read(ref _version) == version;

    public void Invalidate()
        => Interlocked.Increment(ref _version);
}
