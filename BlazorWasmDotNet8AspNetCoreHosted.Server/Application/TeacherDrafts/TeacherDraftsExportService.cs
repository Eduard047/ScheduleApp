using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

// Сервіс експорту чернеток у формат Excel.
public sealed class TeacherDraftsExportService
{
    private readonly AppDbContext _db;
    private readonly TeacherDraftsQueryService _queryService;
    public TeacherDraftsExportService(AppDbContext db, TeacherDraftsQueryService queryService)
    {
        _db = db;
        _queryService = queryService;
    }
    // Допоміжна модель для груп у звіті.
    private sealed record GroupInfo(int Id, string Name);
    // Формує Excel-файл з розкладом чернеток.
    public async Task<FileContentResult> ExportAsync(
        DateOnly weekStart,
        int? teacherId,
        int? groupId,
        int? roomId)
    {
        var drafts = await _queryService.GetAsync(weekStart, teacherId, groupId, roomId);
        var groups = drafts
            .GroupBy(d => (d.GroupId, d.Group))
            .Select(g => new GroupInfo(g.Key.GroupId, g.Key.Group))
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        string? teacherLabel = null;
        if (teacherId is int tid)
        {
            teacherLabel = await _db.Teachers.AsNoTracking()
                .Where(t => t.Id == tid)
                .Select(t => t.FullName)
                .FirstOrDefaultAsync();
        }
        string? roomLabel = null;
        if (roomId is int rid)
        {
            roomLabel = await _db.Rooms.AsNoTracking()
                .Where(r => r.Id == rid)
                .Select(r => r.Name)
                .FirstOrDefaultAsync();
        }
        string? groupLabel = null;
        if (groupId is int gid)
        {
            groupLabel = await _db.Groups.AsNoTracking()
                .Where(g => g.Id == gid)
                .Select(g => g.Name)
                .FirstOrDefaultAsync();
        }
        if (groupId is int sectionId && !groups.Any() && groupLabel is not null)
        {
            groups.Add(new GroupInfo(sectionId, groupLabel));
        }
        var moduleIds = drafts
            .Select(d => d.ModuleId)
            .Distinct()
            .ToList();
        var moduleCodeLookup = moduleIds.Count == 0
            ? new Dictionary<int, string>()
            : await _db.Modules.AsNoTracking()
                .Where(m => moduleIds.Contains(m.Id))
                .Select(m => new { m.Id, m.Code })
                .ToDictionaryAsync(
                    m => m.Id,
                    m => string.IsNullOrWhiteSpace(m.Code) ? string.Empty : m.Code.Trim());
        var weekDays = Enumerable.Range(0, 7)
            .Select(offset => weekStart.AddDays(offset))
            .ToList();
        var isoWeek = ISOWeek.GetWeekOfYear(weekStart.ToDateTime(TimeOnly.MinValue));
        var rawSlots = await _db.TimeSlots.AsNoTracking()
            .Where(s => s.CourseId == null)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Start)
            .Select(s => new { s.Start, s.End, s.SortOrder })
            .ToListAsync();
        var globalSlots = rawSlots.Select(s => (s.Start, s.End)).ToList();
        var slotNumberLookup = rawSlots
            .GroupBy(s => (s.Start, s.End))
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.SortOrder).FirstOrDefault(number => number > 0));
        var enriched = drafts
            .Select(d => new
            {
                Item = d,
                Start = TimeOnly.ParseExact(d.TimeStart, "HH:mm", CultureInfo.InvariantCulture),
                End = TimeOnly.ParseExact(d.TimeEnd, "HH:mm", CultureInfo.InvariantCulture)
            })
            .ToList();
        var slotPeriods = globalSlots
            .Concat(enriched.Select(e => (e.Start, e.End)))
            .GroupBy(x => (x.Start, x.End))
            .Select(g => g.Key)
            .OrderBy(x => x.Start)
            .ThenBy(x => x.End)
            .ToList();
        var lookup = enriched
            .GroupBy(x => (x.Item.Date, x.Start, x.End, x.Item.GroupId))
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.Item)
                    .OrderBy(x => x.Module, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(x => x.LessonTypeName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(x => x.Teacher, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(x => x.Id)
                    .ToList());
        var filterParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(teacherLabel)) filterParts.Add($"Викладач: {teacherLabel}");
        if (!string.IsNullOrWhiteSpace(groupLabel)) filterParts.Add($"Група: {groupLabel}");
        if (!string.IsNullOrWhiteSpace(roomLabel)) filterParts.Add($"Аудиторія: {roomLabel}");
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Розклад");
        var columnCount = 2 + Math.Max(1, groups.Count);
        var titleRange = worksheet.Range(1, 1, 1, columnCount);
        titleRange.Merge();
        titleRange.Value = "РОЗКЛАД навчальних занять";
        titleRange.Style.Font.Bold = true;
        titleRange.Style.Font.FontSize = 14;
        titleRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        titleRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        var weekInfoRange = worksheet.Range(2, 1, 2, columnCount);
        weekInfoRange.Merge();
        weekInfoRange.Value = $"Тиждень №{isoWeek} | {weekStart:dd.MM.yyyy} - {weekStart.AddDays(6):dd.MM.yyyy}";
        weekInfoRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        weekInfoRange.Style.Font.Italic = true;
        if (filterParts.Count > 0)
        {
            var filterRange = worksheet.Range(3, 1, 3, columnCount);
            filterRange.Merge();
            filterRange.Value = string.Join(" | ", filterParts);
            filterRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }
        const int tableHeaderRow = 4;
        worksheet.Cell(tableHeaderRow, 1).Value = "День тижня";
        worksheet.Cell(tableHeaderRow, 2).Value = "Пара";
        if (groups.Count > 0)
        {
            for (var index = 0; index < groups.Count; index++)
            {
                worksheet.Cell(tableHeaderRow, 3 + index).Value = groups[index].Name;
            }
        }
        else
        {
            worksheet.Cell(tableHeaderRow, 3).Value = "Інформація";
        }
        var headerRowRange = worksheet.Range(tableHeaderRow, 1, tableHeaderRow, columnCount);
        headerRowRange.Style.Font.Bold = true;
        headerRowRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        headerRowRange.Style.Alignment.WrapText = true;
        headerRowRange.Style.Fill.BackgroundColor = XLColor.LightGray;
        var tableStartRow = tableHeaderRow + 1;
        var tableEndRow = tableHeaderRow;
        if (!slotPeriods.Any() || !groups.Any())
        {
            var messageRange = worksheet.Range(tableStartRow, 1, tableStartRow, columnCount);
            messageRange.Merge();
            messageRange.Value = "Немає даних для розкладу на цей тиждень";
            messageRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            messageRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            tableEndRow = tableStartRow;
        }
        else
        {
            var row = tableStartRow;
            foreach (var day in weekDays)
            {
                var dayStartRow = row;
                for (var slotIndex = 0; slotIndex < slotPeriods.Count; slotIndex++)
                {
                    var slot = slotPeriods[slotIndex];
                    var slotNumber = slotNumberLookup.TryGetValue((slot.Start, slot.End), out var mappedSlotNumber) && mappedSlotNumber > 0
                        ? mappedSlotNumber
                        : slotIndex + 1;
                    worksheet.Cell(row, 2).Value = slotNumber;
                    for (var index = 0; index < groups.Count; index++)
                    {
                        var column = 3 + index;
                        if (lookup.TryGetValue((day, slot.Start, slot.End, groups[index].Id), out var itemsForSlot))
                        {
                            var cellParts = itemsForSlot
                                .Select(item =>
                                {
                                    moduleCodeLookup.TryGetValue(item.ModuleId, out var moduleCode);
                                    return TeacherDraftsHelpers.BuildExportCell(item, moduleCode);
                                })
                                .Where(text => !string.IsNullOrWhiteSpace(text))
                                .Distinct(StringComparer.CurrentCulture)
                                .ToList();
                            worksheet.Cell(row, column).Value = string.Join(
                                $"{Environment.NewLine}{Environment.NewLine}",
                                cellParts);
                        }
                    }
                    row++;
                }
                var dayRange = worksheet.Range(dayStartRow, 1, row - 1, 1);
                dayRange.Merge();
                dayRange.Value = $"{TeacherDraftsHelpers.GetUkrainianDayName(day)}{Environment.NewLine}{day:dd.MM.yyyy}";
                dayRange.Style.Alignment.WrapText = true;
                dayRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                dayRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            }
            tableEndRow = row - 1;
        }
        var tableRange = worksheet.Range(tableHeaderRow, 1, tableEndRow, columnCount);
        tableRange.Style.Alignment.WrapText = true;
        tableRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        tableRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        tableRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        worksheet.SheetView.FreezeRows(tableHeaderRow);
        worksheet.Columns(1, columnCount).AdjustToContents();
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        var fileName = $"Rozklad-{weekStart:yyyyMMdd}.xlsx";
        const string contentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        return new FileContentResult(stream.ToArray(), contentType)
        {
            FileDownloadName = fileName
        };
    }
}
