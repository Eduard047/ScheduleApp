namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;

// Допоміжні методи для роботи з датами.
public static class DateHelpers
{
    public const int MinSupportedScheduleYear = 1000;
    public const int MaxSupportedScheduleYear = 9998;

    // Обмежує дати діапазоном MySQL DATE та залишає запас для тижневих обчислень.
    public static bool IsSupportedScheduleDate(DateOnly date)
        => date.Year is >= MinSupportedScheduleYear and <= MaxSupportedScheduleYear;

    // Повертає стабільне повідомлення для всіх маршрутів розкладу.
    public static string SupportedScheduleDateMessage
        => $"Дата має бути в діапазоні років {MinSupportedScheduleYear}–{MaxSupportedScheduleYear}.";

    // Повертає дату понеділка для тижня вказаної дати.
    public static DateOnly StartOfWeek(DateOnly date)
    {
        var offset = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-offset);
    }
}
