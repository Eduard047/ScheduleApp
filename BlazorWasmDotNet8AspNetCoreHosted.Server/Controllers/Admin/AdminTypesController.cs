using System.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Admin;

[ApiController]
[Route("api/admin/types")]
// Контролер адміністратора для типів занять
public class AdminTypesController(AppDbContext db) : ControllerBase
{

    private sealed record PaletteItem(string Name, string Hex);
    private static readonly IReadOnlyDictionary<string, PaletteItem> Palette =
        new Dictionary<string, PaletteItem>
        {
            ["c1"] = new("Небесний", "#C9E6FF"),
            ["c2"] = new("Смарагдовий", "#B6F4D2"),
            ["c3"] = new("Пісочний", "#FFE2A9"),
            ["c4"] = new("Ліловий", "#E6D0FF"),
            ["brk"] = new("Стальний (перерва)", "#E4E9F2"),
            ["can"] = new("Рожевий (скасовано)", "#FFC7D4"),
            ["res"] = new("Бірюзовий (перенесено)", "#B0EBFF"),
            ["c7"] = new("Лазурний", "#CDE3FF"),
            ["c8"] = new("Мʼята", "#C3F7E3"),
            ["c9"] = new("Банан", "#FFE9A6"),
            ["c10"] = new("Лаванда", "#D8C3FF"),
            ["c11"] = new("Корал", "#FFB8A8"),
            ["c12"] = new("Півонія", "#F7C6FF"),
            ["c13"] = new("Льодяний", "#A9E7FF"),
            ["c14"] = new("Лайм", "#D6F5A3"),
            ["c15"] = new("Персик", "#FFD2B3"),
            ["c16"] = new("Стальний-2", "#CED7E5"),
            ["c17"] = new("Янтар", "#FFC872"),
            ["c18"] = new("Пастельно-рожевий", "#FFD1DC"),
            ["c19"] = new("Оливковий", "#CFE3B4"),
            ["c20"] = new("Морська хвиля", "#B7E4E0"),
            ["c21"] = new("Світла слива", "#E6C2E9"),
            ["c22"] = new("Світлий графіт", "#D3DAE3"),
            ["c23"] = new("М'ята-лайм", "#CDEFB8"),
            ["c24"] = new("Сонячний", "#FFE6B5"),
        };
    [HttpGet("lesson/palette")]
    // Повертає палітру кольорів із позначенням зайнятих.
    public async Task<IReadOnlyList<LessonColorDto>> LessonPalette()
    {
        var used = await db.LessonTypes
            .Where(x => x.CssKey != null && x.CssKey != "")
            .Select(x => new { x.Id, CssKey = x.CssKey! })
            .ToListAsync();
        var usedMap = used.GroupBy(x => x.CssKey).ToDictionary(g => g.Key, g => g.First().Id);
        return Palette.Select(p =>
        {
            usedMap.TryGetValue(p.Key, out var usedById);
            return new LessonColorDto(
                Key: p.Key,
                Name: p.Value.Name,
                Hex: p.Value.Hex,
                IsUsed: usedById != 0,
                UsedByTypeId: usedById == 0 ? null : usedById
            );
        }).ToList();
    }
    [HttpGet("lesson")]
    // Повертає список типів занять.
    public async Task<IReadOnlyList<LessonTypeEditDto>> LessonList() =>
        await db.LessonTypes.AsNoTracking()
            .OrderBy(x => x.Id)
            .Select(x => new LessonTypeEditDto
            {
                Id = x.Id,
                Code = x.Code,
                Name = x.Name,
                IsActive = x.IsActive,
                CssKey = x.CssKey,
                RequiresRoom = x.RequiresRoom,
                RequiresTeacher = x.RequiresTeacher,
                BlocksRoom = x.BlocksRoom,
                BlocksTeacher = x.BlocksTeacher,
                CountInPlan = x.CountInPlan,
                CountInLoad = x.CountInLoad,
                PreferredFirstInWeek = x.PreferredFirstInWeek
            })
            .ToListAsync();
    [HttpPost("lesson/upsert")]
    // Створює або оновлює тип заняття з перевіркою палітри.
    public async Task<ActionResult<int>> LessonUpsert([FromBody] LessonTypeEditDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Code) || string.IsNullOrWhiteSpace(dto.Name))
            return BadRequest(new { message = "Код та назва є обов'язковими." });
        var code = dto.Code.Trim().ToUpperInvariant();
        var name = dto.Name.Trim();
        if (code.Length > 64)
            return BadRequest(new { message = "Код типу заняття не може перевищувати 64 символи." });
        if (name.Length > 200)
            return BadRequest(new { message = "Назва типу заняття не може перевищувати 200 символів." });

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            LessonTypeRef lessonType;
            if (dto.Id is int id && id > 0)
            {
                var existingLessonType = await db.LessonTypes.FirstOrDefaultAsync(x => x.Id == id);
                if (existingLessonType is null) return NotFound(new { message = "Тип заняття не знайдено." });
                lessonType = existingLessonType;
            }
            else
            {
                lessonType = new LessonTypeRef();
                db.LessonTypes.Add(lessonType);
            }

            var codeIsTaken = await db.LessonTypes.AnyAsync(existing =>
                existing.Id != lessonType.Id && existing.Code.ToUpper() == code);
            if (codeIsTaken)
                return Conflict(new { message = $"Тип заняття з кодом '{code}' вже існує." });

            var newKey = string.IsNullOrWhiteSpace(dto.CssKey) ? null : dto.CssKey.Trim();
            if (newKey != null)
            {
                if (!Palette.ContainsKey(newKey))
                    return BadRequest(new { message = $"Недопустимий CSS-ключ '{newKey}'. Оберіть один із фіксованої палітри." });
                var takenBy = await db.LessonTypes
                    .Where(x => x.CssKey == newKey && x.Id != lessonType.Id)
                    .Select(x => x.Id)
                    .FirstOrDefaultAsync();
                if (takenBy != 0)
                    return Conflict(new { message = $"Колір '{newKey}' вже використовується типом #{takenBy}." });
            }

            if (dto.PreferredFirstInWeek)
            {
                var taken = await db.LessonTypes
                    .AsNoTracking()
                    .Where(x => x.PreferredFirstInWeek && x.Id != lessonType.Id)
                    .Select(x => new { x.Id, x.Code, x.Name })
                    .FirstOrDefaultAsync();
                if (taken is not null)
                {
                    var label = string.IsNullOrWhiteSpace(taken.Code)
                        ? $"#{taken.Id}"
                        : $"{taken.Code} (#{taken.Id})";
                    return Conflict(new { message = $"Прапорець \"Бажано першим у тижні\" вже встановлено для типу {label}. Спочатку зніміть його там." });
                }
            }

            var changesPlacementSemantics = lessonType.Id > 0
                && (!string.Equals(lessonType.Code, code, StringComparison.OrdinalIgnoreCase)
                    || (lessonType.IsActive && !dto.IsActive)
                    || lessonType.RequiresRoom != dto.RequiresRoom
                    || lessonType.RequiresTeacher != dto.RequiresTeacher
                    || lessonType.BlocksRoom != dto.BlocksRoom
                    || lessonType.BlocksTeacher != dto.BlocksTeacher
                    || lessonType.CountInPlan != dto.CountInPlan
                    || lessonType.CountInLoad != dto.CountInLoad);
            if (changesPlacementSemantics)
            {
                var isUsed = await db.ScheduleItems.AnyAsync(item => item.LessonTypeId == lessonType.Id)
                    || await db.TeacherDraftItems.AnyAsync(item => item.LessonTypeId == lessonType.Id)
                    || await db.ModuleTopics.AnyAsync(topic => topic.LessonTypeId == lessonType.Id);
                if (isUsed)
                {
                    return Conflict(new
                    {
                        message = "Неможливо деактивувати або змінити код чи правила типу заняття, доки він використовується у розкладі, чернетках чи темах. Створіть новий тип і перенесіть залежні записи."
                    });
                }
            }

            lessonType.Code = code;
            lessonType.Name = name;
            lessonType.IsActive = dto.IsActive;
            lessonType.CssKey = newKey;
            lessonType.RequiresRoom = dto.RequiresRoom;
            lessonType.RequiresTeacher = dto.RequiresTeacher;
            lessonType.BlocksRoom = dto.BlocksRoom;
            lessonType.BlocksTeacher = dto.BlocksTeacher;
            lessonType.CountInPlan = dto.CountInPlan;
            lessonType.CountInLoad = dto.CountInLoad;
            lessonType.PreferredFirstInWeek = dto.PreferredFirstInWeek;
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Ok(lessonType.Id);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
    [HttpDelete("lesson/{id:int}")]
    [RequireDeletionConfirmation("тип заняття")]
    // Видаляє тип заняття, якщо він не використовується.
    public async Task<IActionResult> LessonDelete(int id)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            if (!await db.LessonTypes.AnyAsync(type => type.Id == id)) return NotFound();
            var usedInSchedule = await db.ScheduleItems.AnyAsync(item => item.LessonTypeId == id);
            var usedInDrafts = await db.TeacherDraftItems.AnyAsync(item => item.LessonTypeId == id);
            var usedInTopics = await db.ModuleTopics.AnyAsync(topic => topic.LessonTypeId == id);
            if (usedInSchedule || usedInDrafts || usedInTopics)
            {
                return Conflict(new
                {
                    message = "Тип заняття використовується у розкладі, чернетках або темах модулів. Спочатку змініть пов'язані записи."
                });
            }
            var rows = await db.LessonTypes.Where(x => x.Id == id).ExecuteDeleteAsync();
            if (rows == 0) return NotFound();
            await tx.CommitAsync();
            return NoContent();
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    [HttpPost("lesson/{sourceId:int}/merge/{targetId:int}")]
    [RequireDeletionConfirmation(
        "дубль типу заняття",
        TargetArgumentName = nameof(sourceId),
        Message = "Підтвердіть об'єднання: усі залежні записи буде перенесено до канонічного типу, а дубль буде остаточно видалено.")]
    // Об'єднує дубль типу заняття з канонічним типом в одній транзакції.
    public async Task<ActionResult<LessonTypeMergeResult>> LessonMerge(
        int sourceId,
        int targetId,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await LessonTypeMergeService.MergeAsync(
                db,
                sourceId,
                targetId,
                cancellationToken));
        }
        catch (LessonTypeMergeException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

}
