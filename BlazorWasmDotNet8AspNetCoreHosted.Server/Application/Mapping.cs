using System.Globalization;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application;

// Допоміжні перетворення доменних сутностей у DTO
public static class Mapping
{
    private static readonly CultureInfo Uk = new("uk-UA");
    // Перетворює доменний елемент розкладу у DTO для клієнта.
    public static ScheduleItemDto ToDto(this ScheduleItem x)
    {
        var lt = x.LessonType;
        var ltCode = (lt?.Code ?? "").ToUpperInvariant();
        var ltName = lt?.Name ?? "";
        string? ltCss =
            lt?.CssKey ??
            (ltCode.Equals("BREAK", StringComparison.OrdinalIgnoreCase) ? "brk" : null);
        var isBreak = ltCode.Equals("BREAK", StringComparison.OrdinalIgnoreCase);
        var isRescheduled = ltCode.Equals("RESCHEDULED", StringComparison.OrdinalIgnoreCase);
        var isCanceled = ltCode.Equals("CANCELED", StringComparison.OrdinalIgnoreCase);
        var requiresRoom = lt?.RequiresRoom ?? true;
        if ((isCanceled || isRescheduled) && x.RoomId is not null)
        {
            requiresRoom = true;
        }
        var roomName = (!isBreak && requiresRoom && x.Room != null) ? x.Room.Name : "";
        var buildingName = (!isBreak && requiresRoom && x.Room?.Building != null) ? x.Room.Building.Name : "";
        return new(
            Id: x.Id,
            Date: x.Date,
            TimeStart: x.StartTime.ToString("HH\\:mm"),
            TimeEnd: x.EndTime.ToString("HH\\:mm"),
            DayName: x.Date.ToDateTime(TimeOnly.MinValue).ToString("dddd", Uk),
            DayNumber: (int)x.DayOfWeek,
            Group: x.Group?.Name ?? "",
            GroupId: x.GroupId,
            Module: isBreak ? "Перерва" : (x.Module?.Title ?? ""),
            ModuleId: x.ModuleId,
            Teacher: isBreak ? "" : (x.Teacher?.FullName ?? ""),
            TeacherId: x.TeacherId,
            Room: roomName,
            RoomId: requiresRoom ? x.RoomId : null,
            Building: buildingName,
            BuildingId: requiresRoom ? x.Room?.BuildingId : null,
            RequiresRoom: requiresRoom,
            LessonTypeId: x.LessonTypeId,
            LessonTypeCode: ltCode,
            LessonTypeName: ltName,
            IsLocked: x.IsLocked,
            LessonTypeCss: ltCss
        );
    }
}
