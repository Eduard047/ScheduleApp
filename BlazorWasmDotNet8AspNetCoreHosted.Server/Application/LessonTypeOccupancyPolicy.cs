namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application;

// Відокремлює службові позначки розкладу від занять, які реально займають ресурси.
public static class LessonTypeOccupancyPolicy
{
    public static bool IsNonOccupyingMarker(string? code)
    {
        var normalized = (code ?? string.Empty).Trim();
        return normalized.Equals("CANCELED", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("RESCHEDULED", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsExcludedFromAutogenWorkload(string? code)
    {
        var normalized = (code ?? string.Empty).Trim();
        return IsNonOccupyingMarker(normalized)
               || normalized.Equals("BREAK", StringComparison.OrdinalIgnoreCase);
    }
}
