using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

public sealed class TeacherDraftsExportLimitException(
    int statusCode,
    string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

// Сервіс експорту чернеток у формат Excel.
public sealed class TeacherDraftsExportService
{
    internal const int MaxDraftRowCount = 5_000;
    internal const int MaxMatrixCellCount = 50_000;
    private static readonly TimeSpan ExportDeadline = TimeSpan.FromSeconds(45);
    private static readonly SemaphoreSlim ExportConcurrencyGate = new(2, 2);
    private readonly AppDbContext _db;
    private readonly TeacherDraftsQueryService _queryService;
    public TeacherDraftsExportService(AppDbContext db, TeacherDraftsQueryService queryService)
    {
        _db = db;
        _queryService = queryService;
    }
    // Допоміжна модель для груп у звіті.
    private sealed record GroupInfo(int Id, string Name);
    private static bool IsLectureTypeForGroupMerge(TeacherDraftItemDto item)
    {
        if (item.IsSelfStudy
            || string.Equals(item.LessonTypeCode, "BREAK", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.LessonTypeCode, "CANCELED", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var code = item.LessonTypeCode?.Trim();
        if (!string.IsNullOrWhiteSpace(code))
        {
            if (string.Equals(code, "LECTURE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "LECT", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "LEC", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        var name = item.LessonTypeName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }
        return name.Contains("ЛЕКЦ", StringComparison.CurrentCultureIgnoreCase)
               || name.Contains("LECTURE", StringComparison.OrdinalIgnoreCase);
    }
    private static bool HasSameTeacherForGroupMerge(TeacherDraftItemDto left, TeacherDraftItemDto right)
    {
        if (left.TeacherId.HasValue || right.TeacherId.HasValue)
        {
            return left.TeacherId == right.TeacherId;
        }
        var leftTeacher = left.Teacher?.Trim();
        var rightTeacher = right.Teacher?.Trim();
        return string.Equals(leftTeacher, rightTeacher, StringComparison.CurrentCultureIgnoreCase);
    }
    private static bool CanMergeLectureAcrossGroups(TeacherDraftItemDto left, TeacherDraftItemDto right)
    {
        if (!IsLectureTypeForGroupMerge(left) || !IsLectureTypeForGroupMerge(right))
        {
            return false;
        }
        return left.ModuleId == right.ModuleId
               && left.LessonTypeId == right.LessonTypeId
               && left.RoomId == right.RoomId
               && left.ModuleTopicId == right.ModuleTopicId
               && HasSameTeacherForGroupMerge(left, right);
    }
    private static string BuildExportCellText(
        IReadOnlyList<TeacherDraftItemDto> itemsForSlot,
        IReadOnlyDictionary<int, string> moduleCodeLookup)
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
        return string.Join($"{Environment.NewLine}{Environment.NewLine}", cellParts);
    }
    private static TeacherDraftItemDto SelectMergeAnchor(IReadOnlyList<TeacherDraftItemDto> itemsForSlot)
        => itemsForSlot
            .OrderBy(item => item.Id)
            .First();
    private static int CountDisplayLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 1;
        }
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Length;
    }
    private static double ResolveRowHeightByLineCount(int lineCount)
    {
        const double baseLineHeight = 15d;
        var normalizedLines = Math.Max(1, lineCount);
        return baseLineHeight * normalizedLines;
    }
    private static int ResolveGroupMergeSpan(
        DateOnly day,
        TimeOnly start,
        TimeOnly end,
        IReadOnlyList<GroupInfo> groups,
        int startGroupIndex,
        IReadOnlyDictionary<(DateOnly Date, TimeOnly Start, TimeOnly End, int GroupId), TeacherDraftItemDto> mergeAnchorLookup,
        IReadOnlyDictionary<(DateOnly Date, TimeOnly Start, TimeOnly End, int GroupId), string> cellTextLookup)
    {
        if (startGroupIndex < 0 || startGroupIndex >= groups.Count)
        {
            return 1;
        }
        var anchorKey = (day, start, end, groups[startGroupIndex].Id);
        if (!mergeAnchorLookup.TryGetValue(anchorKey, out var anchorItem)
            || !cellTextLookup.TryGetValue(anchorKey, out var anchorText)
            || string.IsNullOrWhiteSpace(anchorText)
            || !IsLectureTypeForGroupMerge(anchorItem))
        {
            return 1;
        }
        var span = 1;
        for (var index = startGroupIndex + 1; index < groups.Count; index++)
        {
            var nextKey = (day, start, end, groups[index].Id);
            if (!mergeAnchorLookup.TryGetValue(nextKey, out var nextItem)
                || !CanMergeLectureAcrossGroups(anchorItem, nextItem)
                || !cellTextLookup.TryGetValue(nextKey, out var nextText)
                || string.IsNullOrWhiteSpace(nextText))
            {
                break;
            }
            span++;
        }
        return span;
    }
    // Формує Excel-файл з розкладом чернеток.
    public async Task<FileStreamResult> ExportAsync(
        DateOnly weekStart,
        int? teacherId,
        int? groupId,
        int? roomId,
        CancellationToken cancellationToken = default)
    {
        if (!await ExportConcurrencyGate.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            throw new TeacherDraftsExportLimitException(
                StatusCodes.Status429TooManyRequests,
                "Одночасно вже виконується максимальна кількість експортів. Повторіть спробу пізніше.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ExportDeadline);
        try
        {
            return await ExportCoreAsync(weekStart, teacherId, groupId, roomId, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TeacherDraftsExportLimitException(
                StatusCodes.Status408RequestTimeout,
                "Експорт не завершився у безпечний час. Звузьте фільтри й повторіть спробу.");
        }
        finally
        {
            ExportConcurrencyGate.Release();
        }
    }

    private async Task<FileStreamResult> ExportCoreAsync(
        DateOnly weekStart,
        int? teacherId,
        int? groupId,
        int? roomId,
        CancellationToken cancellationToken)
    {
        var drafts = await _queryService.GetAsync(
            weekStart,
            teacherId,
            groupId,
            roomId,
            cancellationToken,
            MaxDraftRowCount + 1);
        if (drafts.Count > MaxDraftRowCount)
        {
            throw new TeacherDraftsExportLimitException(
                StatusCodes.Status413PayloadTooLarge,
                $"Експорт містить понад {MaxDraftRowCount} рядків чернеток. Звузьте фільтри й повторіть спробу.");
        }
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
                .FirstOrDefaultAsync(cancellationToken);
        }
        string? roomLabel = null;
        if (roomId is int rid)
        {
            roomLabel = await _db.Rooms.AsNoTracking()
                .Where(r => r.Id == rid)
                .Select(r => r.Name)
                .FirstOrDefaultAsync(cancellationToken);
        }
        string? groupLabel = null;
        if (groupId is int gid)
        {
            groupLabel = await _db.Groups.AsNoTracking()
                .Where(g => g.Id == gid)
                .Select(g => g.Name)
                .FirstOrDefaultAsync(cancellationToken);
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
                    m => string.IsNullOrWhiteSpace(m.Code) ? string.Empty : m.Code.Trim(),
                    cancellationToken);
        var weekDays = Enumerable.Range(0, 7)
            .Select(offset => weekStart.AddDays(offset))
            .ToList();
        var isoWeek = ISOWeek.GetWeekOfYear(weekStart.ToDateTime(TimeOnly.MinValue));
        var rawSlots = await _db.TimeSlots.AsNoTracking()
            .Where(s => s.CourseId == null)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Start)
            .Select(s => new { s.Start, s.End, s.SortOrder })
            .Take(MaxMatrixCellCount + 1)
            .ToListAsync(cancellationToken);
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
        var matrixCellCount = checked((long)weekDays.Count
                                      * Math.Max(1, groups.Count)
                                      * Math.Max(1, slotPeriods.Count));
        if (matrixCellCount > MaxMatrixCellCount)
        {
            throw new TeacherDraftsExportLimitException(
                StatusCodes.Status422UnprocessableEntity,
                $"Таблиця експорту потребує {matrixCellCount} комірок, що перевищує безпечний ліміт {MaxMatrixCellCount}. Звузьте групи або часові слоти й повторіть спробу.");
        }
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
        var cellTextLookup = lookup.ToDictionary(
            pair => pair.Key,
            pair => BuildExportCellText(pair.Value, moduleCodeLookup));
        var mergeAnchorLookup = lookup.ToDictionary(
            pair => pair.Key,
            pair => SelectMergeAnchor(pair.Value));
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
        worksheet.Cell(tableHeaderRow, 2).Value = "Година";
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
                cancellationToken.ThrowIfCancellationRequested();
                var dayStartRow = row;
                for (var slotIndex = 0; slotIndex < slotPeriods.Count; slotIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var slot = slotPeriods[slotIndex];
                    var slotNumber = slotNumberLookup.TryGetValue((slot.Start, slot.End), out var mappedSlotNumber) && mappedSlotNumber > 0
                        ? mappedSlotNumber
                        : slotIndex + 1;
                    worksheet.Cell(row, 2).Value = slotNumber;
                    var maxDisplayLinesInRow = 1;
                    for (var index = 0; index < groups.Count;)
                    {
                        var column = 3 + index;
                        var key = (day, slot.Start, slot.End, groups[index].Id);
                        var mergeSpan = ResolveGroupMergeSpan(
                            day,
                            slot.Start,
                            slot.End,
                            groups,
                            index,
                            mergeAnchorLookup,
                            cellTextLookup);
                        if (cellTextLookup.TryGetValue(key, out var cellText) && !string.IsNullOrWhiteSpace(cellText))
                        {
                            worksheet.Cell(row, column).Value = cellText;
                            maxDisplayLinesInRow = Math.Max(maxDisplayLinesInRow, CountDisplayLines(cellText));
                        }
                        if (mergeSpan > 1)
                        {
                            var mergeRange = worksheet.Range(row, column, row, column + mergeSpan - 1);
                            mergeRange.Merge();
                        }
                        index += mergeSpan;
                    }
                    worksheet.Row(row).Height = ResolveRowHeightByLineCount(maxDisplayLinesInRow);
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
        cancellationToken.ThrowIfCancellationRequested();
        var stream = new MemoryStream();
        try
        {
            workbook.SaveAs(stream);
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = 0;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
        var fileName = $"Rozklad-{weekStart:yyyyMMdd}.xlsx";
        const string contentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        return new FileStreamResult(stream, contentType)
        {
            FileDownloadName = fileName
        };
    }
}
