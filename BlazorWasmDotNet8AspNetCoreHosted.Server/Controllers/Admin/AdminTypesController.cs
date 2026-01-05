using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
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
    public async Task<ActionResult<int>> LessonUpsert([FromBody] LessonTypeEditDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Code) || string.IsNullOrWhiteSpace(dto.Name))
            return BadRequest(new { message = "Код та Назва є обовʼязковими" });

        LessonTypeRef e;
        if (dto.Id is int id && id > 0)
        {
            e = await db.LessonTypes.FirstOrDefaultAsync(x => x.Id == id)
                ?? throw new ArgumentException("Тип заняття не знайдено");
        }
        else
        {
            e = new LessonTypeRef();
            db.LessonTypes.Add(e);
        }

        e.Code = dto.Code.Trim();
        e.Name = dto.Name.Trim();
        e.IsActive = dto.IsActive;

        
        var newKey = string.IsNullOrWhiteSpace(dto.CssKey) ? null : dto.CssKey.Trim();

        if (newKey != null)
        {
            if (!Palette.ContainsKey(newKey))
                return BadRequest(new { message = $"Недопустимий CSS-ключ '{newKey}'. Оберіть один із фіксованої палітри." });

            var takenBy = await db.LessonTypes
                .Where(x => x.CssKey == newKey)
                .Select(x => x.Id)
                .FirstOrDefaultAsync();

            if (takenBy != 0 && takenBy != e.Id)
                return Conflict(new { message = $"Колір '{newKey}' уже використовується типом #{takenBy}." });
        }

        e.CssKey = newKey;

        e.RequiresRoom = dto.RequiresRoom;
        e.RequiresTeacher = dto.RequiresTeacher;
        e.BlocksRoom = dto.BlocksRoom;
        e.BlocksTeacher = dto.BlocksTeacher;
        e.CountInPlan = dto.CountInPlan;
        e.CountInLoad = dto.CountInLoad;

        if (dto.PreferredFirstInWeek)
        {
            var taken = await db.LessonTypes
                .AsNoTracking()
                .Where(x => x.PreferredFirstInWeek && x.Id != e.Id)
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
        e.PreferredFirstInWeek = dto.PreferredFirstInWeek;

        await db.SaveChangesAsync();
        return Ok(e.Id);
    }

    [HttpDelete("lesson/{id:int}")]
    [RequireDeletionConfirmation("тип заняття")]
    public async Task<IActionResult> LessonDelete(int id)
    {
        var used = await db.ScheduleItems.AnyAsync(s => s.LessonTypeId == id);
        if (used) return Conflict(new { message = "Тип заняття використовується у розкладі" });
        var rows = await db.LessonTypes.Where(x => x.Id == id).ExecuteDeleteAsync();
        if (rows == 0) return NotFound();
        return NoContent();
    }



}
