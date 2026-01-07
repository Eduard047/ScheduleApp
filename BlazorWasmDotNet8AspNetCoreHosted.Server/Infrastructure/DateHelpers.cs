namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;

// Допоміжні методи для роботи з датами.
public static class DateHelpers
{
    // Повертає дату понеділка для тижня вказаної дати.
    public static DateOnly StartOfWeek(DateOnly date)
    {
        var offset = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-offset);
    }
}
