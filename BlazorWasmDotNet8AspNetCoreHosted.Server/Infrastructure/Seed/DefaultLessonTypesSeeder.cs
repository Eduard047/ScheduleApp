using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure.Seed;

public static class DefaultLessonTypesSeeder
{
    // Опис типу заняття для стартового наповнення.
    private sealed record SeedLessonType(
        string Code,
        string Name,
        string? CssKey,
        bool IsActive,
        bool RequiresRoom,
        bool RequiresTeacher,
        bool BlocksRoom,
        bool BlocksTeacher,
        bool CountInPlan,
        bool CountInLoad,
        bool PreferredFirstInWeek);
    // Заповнює довідник типів занять типовими значеннями.
    public static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var defaults = new[]
        {
            new SeedLessonType("BREAK", "Перерва", "brk", true, false, false, false, false, false, false, false),
            new SeedLessonType("CANCELED", "Скасовано", "can", true, false, false, false, false, false, false, false),
            new SeedLessonType("RESCHEDULED", "Перенесено", "res", true, false, false, false, false, false, false, false),
            new SeedLessonType("NONE", "Без типу", null, true, true, true, true, true, false, false, false),
            new SeedLessonType("EXAM", "Екзамен", null, true, true, true, true, true, true, true, false),
            new SeedLessonType("CREDIT", "Залік", null, true, true, true, true, true, true, true, false),
        };
        var existing = await db.LessonTypes.ToListAsync(cancellationToken);
        var usedCssKeys = existing
            .Where(item => !string.IsNullOrWhiteSpace(item.CssKey))
            .Select(item => item.CssKey!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var d in defaults)
        {
            var entity = existing.FirstOrDefault(x => string.Equals(x.Code, d.Code, StringComparison.OrdinalIgnoreCase));
            var availableCssKey = !string.IsNullOrWhiteSpace(d.CssKey)
                && !usedCssKeys.Contains(d.CssKey)
                    ? d.CssKey
                    : null;
            if (entity is null)
            {
                // Додаємо новий тип заняття, якщо його ще немає.
                db.LessonTypes.Add(new LessonTypeRef
                {
                    Code = d.Code,
                    Name = d.Name,
                    CssKey = availableCssKey,
                    IsActive = d.IsActive,
                    RequiresRoom = d.RequiresRoom,
                    RequiresTeacher = d.RequiresTeacher,
                    BlocksRoom = d.BlocksRoom,
                    BlocksTeacher = d.BlocksTeacher,
                    CountInPlan = d.CountInPlan,
                    CountInLoad = d.CountInLoad,
                    PreferredFirstInWeek = d.PreferredFirstInWeek
                });
                if (availableCssKey is not null)
                {
                    usedCssKeys.Add(availableCssKey);
                }
                changed = true;
            }
            else
            {
                // Доповнюємо відсутні поля в існуючих записах.
                if (string.IsNullOrWhiteSpace(entity.Name))
                {
                    entity.Name = d.Name;
                    changed = true;
                }
                if (string.IsNullOrWhiteSpace(entity.CssKey))
                {
                    if (availableCssKey is not null)
                    {
                        entity.CssKey = availableCssKey;
                        usedCssKeys.Add(availableCssKey);
                        changed = true;
                    }
                }
            }
        }
        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
