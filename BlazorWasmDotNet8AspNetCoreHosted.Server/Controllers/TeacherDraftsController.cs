using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ClosedXML.Excel;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers;

[ApiController]
[Route("api/teacher-drafts")]
// Контролер для керування чернетками викладачів
public sealed class TeacherDraftsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly RulesService _rules;
    private static readonly JsonSerializerOptions ValidationJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public TeacherDraftsController(AppDbContext db, RulesService rules)
    {
        _db = db;
        _rules = rules;
    }

    public sealed record DraftAutoGenRequest(
        DateOnly WeekStart,
        bool ClearExisting = true,
        int? CourseId = null,
        int? GroupId = null,
        int? TeacherId = null,
        bool AllowOnDaysOff = false,
        WeekPreset Days = WeekPreset.MonFri,
        Dictionary<int, int>? ModuleHours = null
    );

    public sealed record ApproveWeekRequest(DateOnly WeekStart, int TeacherId);
    public sealed record PublishWeekRequest(DateOnly WeekStart, int? TeacherId);
    public sealed record PublishWeekResults(int Created, int Skipped, List<string> Warnings);

    private sealed record BusySlot(
        int GroupId,
        int? TeacherId,
        int? RoomId,
        DateOnly Date,
        TimeOnly StartTime,
        TimeOnly EndTime,
        int? BuildingId,
        int ModuleId,
        int LessonTypeId
    );

    private sealed record PlacementCandidate(
        TimeSlot Slot,
        int TeacherId,
        Room? Room,
        int LessonTypeId,
        ModuleTopic? Topic,
        bool IsSelfStudy,
        double Penalty,
        List<string> Notes);

    private sealed record GroupInfo(int Id, string Name);


    [HttpGet]
    /// <summary>
    /// Повертає перелік чернеток викладачів за тиждень із додатковою інформацією.
    /// </summary>
    public async Task<IReadOnlyList<TeacherDraftItemDto>> Get(
        [FromQuery] DateOnly weekStart,
        [FromQuery] int? teacherId,
        [FromQuery] int? groupId,
        [FromQuery] int? roomId)
    {
        var weekEnd = weekStart.AddDays(7);

        var q = _db.TeacherDraftItems
            .Include(x => x.Group).ThenInclude(g => g.Course)
            .Include(x => x.Module)
            .Include(x => x.ModuleTopic)
            .Include(x => x.Teacher)
            .Include(x => x.Room).ThenInclude(r => r!.Building)
            .Include(x => x.LessonType)
            .Where(x => x.Date >= weekStart && x.Date < weekEnd)
            .AsQueryable();

        if (teacherId is int tid) q = q.Where(x => x.TeacherId == tid);
        if (groupId is int gid) q = q.Where(x => x.GroupId == gid);
        if (roomId is int rid) q = q.Where(x => x.RoomId == rid);

        var items = await q.OrderBy(x => x.Date).ThenBy(x => x.StartTime).ToListAsync();

        var topicIds = items
            .Where(i => i.ModuleTopicId is int)
            .Select(i => i.ModuleTopicId!.Value)
            .Distinct()
            .ToList();
        var topicCodeLookup = topicIds.Count == 0
            ? new Dictionary<int, string>()
            : await _db.ModuleTopics
                .Where(mt => topicIds.Contains(mt.Id))
                .Select(mt => new { mt.Id, mt.TopicCode })
                .ToDictionaryAsync(mt => mt.Id, mt => (mt.TopicCode ?? string.Empty).Trim());

        var rescheduleSourceIds = items
            .Select(i => ParseRescheduleBatchKey(i.BatchKey))
            .Where(info => info.isRescheduled && info.sourceItemId is int)
            .Select(info => info.sourceItemId!.Value)
            .Distinct()
            .ToList();
        var rescheduleTopicLookup = rescheduleSourceIds.Count == 0
            ? new Dictionary<int, (int? topicId, string? topicCode)>()
            : await _db.ScheduleItems
                .AsNoTracking()
                .Where(si => rescheduleSourceIds.Contains(si.Id))
                .Select(si => new
                {
                    si.Id,
                    si.ModuleTopicId,
                    TopicCode = si.ModuleTopic != null ? si.ModuleTopic.TopicCode : null
                })
                .ToDictionaryAsync(
                    x => x.Id,
                    x => (topicId: x.ModuleTopicId, topicCode: string.IsNullOrWhiteSpace(x.TopicCode) ? null : x.TopicCode!.Trim()));

        static string ResolveTeacherGroupKey(TeacherDraftItem item)
        {
            if (!string.IsNullOrWhiteSpace(item.BatchKey))
            {
                return $"batch:{item.BatchKey}";
            }

            var roomPart = item.RoomId.HasValue ? item.RoomId.Value.ToString() : "none";
            return $"slot:{item.Date:yyyyMMdd}|{item.StartTime:HHmm}|{item.EndTime:HHmm}|m{item.ModuleId}|lt{item.LessonTypeId}|r{roomPart}";
        }

        var teacherGroups = items
            .GroupBy(ResolveTeacherGroupKey)
            .ToDictionary(
                g => g.Key,
                g => g.Select(t => t.Teacher?.FullName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .ToList()
            );

        return items.Select(i =>
        {
        var rescheduleInfo = ParseRescheduleBatchKey(i.BatchKey);
        var groupKey = ResolveTeacherGroupKey(i);
        var teacherNames = teacherGroups.TryGetValue(groupKey, out var groupedNames)
            ? groupedNames
            : new List<string>();

            if (teacherNames.Count == 0 && !string.IsNullOrWhiteSpace(i.Teacher?.FullName))
            {
                teacherNames = new List<string> { i.Teacher!.FullName };
            }

        var teacherLabel = teacherNames.Count > 0 ? string.Join(", ", teacherNames) : (i.Teacher?.FullName ?? "");

        var resolvedTopicId = i.ModuleTopicId;
        if (resolvedTopicId is null && rescheduleInfo.sourceItemId is int sid && rescheduleTopicLookup.TryGetValue(sid, out var topicInfoFromSource))
        {
            resolvedTopicId = topicInfoFromSource.topicId;
        }

        var topicCode = BuildModuleTopicCode(i.ModuleTopic);
        if (topicCode is null && resolvedTopicId is int mtId && topicCodeLookup.TryGetValue(mtId, out var resolvedCode) && !string.IsNullOrWhiteSpace(resolvedCode))
        {
            topicCode = resolvedCode;
        }
        if (topicCode is null && rescheduleInfo.sourceItemId is int sourceId && rescheduleTopicLookup.TryGetValue(sourceId, out var reschedTopic) && !string.IsNullOrWhiteSpace(reschedTopic.topicCode))
        {
            topicCode = reschedTopic.topicCode;
        }

            var requiresRoom = i.LessonType.RequiresRoom;

            return new TeacherDraftItemDto(
            Id: i.Id,
            Date: i.Date,
            TimeStart: i.StartTime.ToString("HH:mm"),
            TimeEnd: i.EndTime.ToString("HH:mm"),
            DayNumber: (int)i.DayOfWeek,
            Group: i.Group.Name,
            GroupId: i.GroupId,
            Module: i.Module.Title,
            ModuleId: i.ModuleId,
            TopicCode: topicCode,
            ModuleTopicId: resolvedTopicId,
            Teacher: teacherLabel,
            TeacherId: i.TeacherId,
            Room: requiresRoom && i.Room is not null ? i.Room.Name : "",
            RoomId: requiresRoom ? i.RoomId : null,
            RequiresRoom: requiresRoom,
            LessonTypeId: i.LessonTypeId,
            LessonTypeCode: i.LessonType.Code,
            LessonTypeName: i.LessonType.Name,
            Status: (DraftStatusDto)(int)i.Status,
            PublishedItemId: i.PublishedItemId,
            Warnings: i.ValidationWarnings,
            IsLocked: i.IsLocked,
            IsRescheduled: rescheduleInfo.isRescheduled,
            RescheduledFromLessonTypeId: rescheduleInfo.originalLessonTypeId,
            BatchKey: i.BatchKey,
            TeacherNames: teacherNames,
            LessonTypeCss: i.LessonType.CssKey,
            IsSelfStudy: i.IsSelfStudy
        );
        }).ToList();
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] DateOnly weekStart,
        [FromQuery] int? teacherId,
        [FromQuery] int? groupId,
        [FromQuery] int? roomId)
    {
        var drafts = await Get(weekStart, teacherId, groupId, roomId);

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

        var weekDays = Enumerable.Range(0, 7)
            .Select(offset => weekStart.AddDays(offset))
            .ToList();
        var isoWeek = ISOWeek.GetWeekOfYear(weekStart.ToDateTime(TimeOnly.MinValue));

        var rawSlots = await _db.TimeSlots.AsNoTracking()
            .Where(s => s.CourseId == null)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Start)
            .Select(s => new { s.Start, s.End })
            .ToListAsync();
        var globalSlots = rawSlots.Select(s => (s.Start, s.End)).ToList();

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

        var lookup = enriched.ToDictionary(
            x => (x.Item.Date, x.Start, x.End, x.Item.GroupId),
            x => x.Item);

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
        worksheet.Cell(tableHeaderRow, 2).Value = "Час";
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
                foreach (var slot in slotPeriods)
                {
                    worksheet.Cell(row, 2).Value = $"{slot.Start:HH:mm} - {slot.End:HH:mm}";
                    for (var index = 0; index < groups.Count; index++)
                    {
                        var column = 3 + index;
                        if (lookup.TryGetValue((day, slot.Start, slot.End, groups[index].Id), out var item))
                        {
                            worksheet.Cell(row, column).Value = BuildExportCell(item);
                        }
                    }
                    row++;
                }

                var dayRange = worksheet.Range(dayStartRow, 1, row - 1, 1);
                dayRange.Merge();
                dayRange.Value = $"{GetUkrainianDayName(day)}{Environment.NewLine}{day:dd.MM.yyyy}";
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
        return File(stream.ToArray(), contentType, fileName);
    }

    [HttpGet("week")]
    /// <summary>
    /// Додає коротку кінцеву точку, що делегує основному методу отримання даних.
    /// </summary>
    public Task<IReadOnlyList<TeacherDraftItemDto>> GetWeekAlias(
        [FromQuery] DateOnly weekStart,
        [FromQuery] int? teacherId,
        [FromQuery] int? groupId,
        [FromQuery] int? roomId)
        => Get(weekStart, teacherId, groupId, roomId);

    [HttpDelete("{id:int}")]
    /// <summary>
    /// Видаляє чернетку, якщо запис існує та не заблокований.
    /// </summary>
    public async Task<IActionResult> Delete(int id, [FromQuery] bool confirm = false, [FromQuery] bool unrestricted = false)
    {
        var item = await _db.TeacherDraftItems.FirstOrDefaultAsync(x => x.Id == id);
        if (item is null) return NotFound(new { message = $"TeacherDraftItem {id} not found" });
        if (item.IsLocked && !unrestricted) return Conflict(new { message = "Чернетка заблокована. Увімкніть режим без обмежень, щоб видалити." });

        _db.TeacherDraftItems.Remove(item);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    internal static (bool isRescheduled, int? sourceItemId, int? originalLessonTypeId) ParseRescheduleBatchKey(string? batchKey)
    {
        if (string.IsNullOrWhiteSpace(batchKey)) return (false, null, null);
        if (!batchKey.StartsWith("rescheduled", StringComparison.OrdinalIgnoreCase)) return (false, null, null);

        var parts = batchKey.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int? sourceId = null;
        if (parts.Length >= 2 && int.TryParse(parts[1], out var parsedSource))
        {
            sourceId = parsedSource;
        }

        int? ltId = null;
        if (parts.Length >= 3 && int.TryParse(parts[2], out var parsedLt))
        {
            ltId = parsedLt;
        }

        return (true, sourceId, ltId);
    }
    /// <summary>
    /// Нормалізує код теми модуля перед збереженням.
    /// </summary>
    private static string? BuildModuleTopicCode(ModuleTopic? topic)
    {
        if (topic is null) return null;
        return string.IsNullOrWhiteSpace(topic.TopicCode) ? null : topic.TopicCode.Trim();
    }

    private static string BuildExportCell(TeacherDraftItemDto item)
    {
        if (IsBreakLesson(item.LessonTypeCode) || IsCanceledLesson(item.LessonTypeCode))
        {
            var summary = new List<string> { item.LessonTypeName };
            if (item.IsRescheduled) summary.Add("Перенесено");
            if (item.Status == DraftStatusDto.Published) summary.Add("Опубліковано");
            return string.Join(Environment.NewLine, summary.Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        var entries = new List<string>();
        if (item.IsSelfStudy) entries.Add("Самостійна робота");
        if (!string.IsNullOrWhiteSpace(item.Module)) entries.Add(item.Module);
        if (!string.IsNullOrWhiteSpace(item.TopicCode)) entries.Add(item.TopicCode);
        if (!string.IsNullOrWhiteSpace(item.Teacher)) entries.Add(item.Teacher);

        var lessonLine = item.LessonTypeName;
        if (item.RequiresRoom && !string.IsNullOrWhiteSpace(item.Room))
        {
            lessonLine = $"{lessonLine} (ауд. {item.Room})";
        }
        entries.Add(lessonLine);

        if (item.IsRescheduled) entries.Add("Перенесено");
        if (item.Status == DraftStatusDto.Published) entries.Add("Опубліковано");

        return string.Join(Environment.NewLine, entries.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static bool IsBreakLesson(string? lessonTypeCode)
        => string.Equals(lessonTypeCode, "BREAK", StringComparison.OrdinalIgnoreCase);

    private static bool IsCanceledLesson(string? lessonTypeCode)
        => string.Equals(lessonTypeCode, "CANCELED", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] UkrainianDayNames =
    {
        "Неділя",
        "Понеділок",
        "Вівторок",
        "Середа",
        "Четвер",
        "П'ятниця",
        "Субота"
    };

    private static string GetUkrainianDayName(DateOnly date)
        => UkrainianDayNames[(int)date.DayOfWeek];


    [HttpPost("upsert")]
    /// <summary>
    /// Валідує й створює або оновлює чернетку викладача, повертає її ідентифікатор.
    /// </summary>
    public async Task<ActionResult<int>> Upsert([FromBody] DraftUpsertRequest r)
    {
        var validation = await _rules.ValidateDraftAsync(r);
        if (validation.Errors.Count > 0 && !r.IgnoreValidationErrors)
            return Conflict(new
            {
                message = "Validation failed",
                errors = validation.Errors,
                warnings = validation.Warnings,
                details = validation.Report
            });

        var reportJson = validation.Report.Issues.Count > 0
            ? JsonSerializer.Serialize(validation.Report, ValidationJsonOptions)
            : null;

        var lessonTypeRequiresRoom = await _db.LessonTypes
            .Where(x => x.Id == r.LessonTypeId)
            .Select(x => (bool?)x.RequiresRoom)
            .FirstOrDefaultAsync();
        var normalizedRoomId = lessonTypeRequiresRoom is false ? null : r.RoomId;

        var start = TimeOnly.Parse(r.TimeStart);
        var end = TimeOnly.Parse(r.TimeEnd);

        if (r.Id is int id && id > 0)
        {
            var item = await _db.TeacherDraftItems.FirstOrDefaultAsync(x => x.Id == id);
            if (item is null) return NotFound(new { message = $"TeacherDraftItem {id} not found" });

            ApplyDraftRequest(item, r, start, end, normalizedRoomId, reportJson);
            await _db.SaveChangesAsync();
            return Ok(item.Id);
        }

        var newItem = new TeacherDraftItem();
        ApplyDraftRequest(newItem, r, start, end, normalizedRoomId, reportJson);
        _db.TeacherDraftItems.Add(newItem);
        await _db.SaveChangesAsync();
        return Ok(newItem.Id);
    }

    private static void ApplyDraftRequest(
        TeacherDraftItem item,
        DraftUpsertRequest request,
        TimeOnly start,
        TimeOnly end,
        int? normalizedRoomId,
        string? validationReport)
    {
        item.Date = request.Date;
        item.DayOfWeek = request.Date.ToDateTime(TimeOnly.MinValue).DayOfWeek;
        item.StartTime = start;
        item.EndTime = end;
        item.GroupId = request.GroupId;
        item.ModuleId = request.ModuleId;
        item.ModuleTopicId = request.ModuleTopicId;
        item.TeacherId = request.TeacherId;
        item.RoomId = normalizedRoomId;
        item.LessonTypeId = request.LessonTypeId;
        item.IsLocked = request.IsLocked;
        item.IsSelfStudy = request.IsSelfStudy;
        item.ValidationWarnings = validationReport;
        item.Status = DraftStatus.Draft;
    }

    [HttpPost("clear-week")]
    /// <summary>
    /// Очищає незаблоковані чернетки за вказаний тиждень із можливими додатковими фільтрами.
    /// </summary>
    public async Task<ActionResult<ClearWeekResult>> ClearWeek([FromBody] ClearWeekRequest r)
    {
        var start = r.WeekStart;
        var end = start.AddDays(7);

        var q = _db.TeacherDraftItems.Where(x => x.Date >= start && x.Date < end && !x.IsLocked);
        if (r.CourseId is int cid) q = q.Where(x => x.Group.CourseId == cid);
        if (r.GroupId is int gid) q = q.Where(x => x.GroupId == gid);

        var deleted = await q.ExecuteDeleteAsync();
        return Ok(new ClearWeekResult(deleted));
    }


    [HttpPost("autogen/week")]
    /// <summary>
    /// Викликає автогенерацію чернеток для одного тижня.
    /// </summary>
    public Task<ActionResult<AutoGenResult>> DraftAutoGenWeek([FromBody] DraftAutoGenRequest r)
        => DraftAutoGen(r);

    [HttpPost("autogen/month")]
    /// <summary>
    /// Автоматично генерує чернетки для кожного тижня в межах місяця.
    /// </summary>
    public async Task<ActionResult<AutoGenResult>> AutogenMonth([FromBody] AutogenMonthRequest r)
    {
        var monthStart = r.MonthStart;
        var nextMonth = new DateOnly(monthStart.Year, monthStart.Month, 1).AddMonths(1);
        var template = new DraftAutoGenRequest(
            WeekStart: monthStart,
            ClearExisting: true,
            CourseId: r.CourseId,
            GroupId: r.GroupId,
            TeacherId: r.TeacherId,
            AllowOnDaysOff: r.AllowOnDaysOff,
            Days: r.Days
        );

        return await RunAutoGenForWeeks(template, EnumerateWeekStarts(monthStart, week => week < nextMonth));
    }

    [HttpPost("autogen/course")]
    /// <summary>
    /// Генерує чернетки для курсу в заданому діапазоні тижнів.
    /// </summary>
    public async Task<ActionResult<AutoGenResult>> AutogenCourse([FromBody] AutogenCourseRequest r)
    {
        var template = new DraftAutoGenRequest(
            WeekStart: r.From,
            ClearExisting: true,
            CourseId: r.CourseId,
            GroupId: r.GroupId,
            TeacherId: r.TeacherId,
            AllowOnDaysOff: r.AllowOnDaysOff,
            Days: r.Days
        );

        return await RunAutoGenForWeeks(template, EnumerateWeekStarts(r.From, week => week <= r.To));
    }

    private static IEnumerable<DateOnly> EnumerateWeekStarts(DateOnly reference, Func<DateOnly, bool> shouldInclude)
    {
        for (var week = DateHelpers.StartOfWeek(reference); shouldInclude(week); week = week.AddDays(7))
        {
            yield return week;
        }
    }

    private async Task<ActionResult<AutoGenResult>> RunAutoGenForWeeks(DraftAutoGenRequest template, IEnumerable<DateOnly> weekStarts)
    {
        int created = 0, skipped = 0;
        var warnings = new List<string>();
        var gapDetails = new List<AutoGenGapDetail>();

        foreach (var weekStart in weekStarts)
        {
            var res = await DraftAutoGen(template with { WeekStart = weekStart });
            if (res.Result is not OkObjectResult { Value: AutoGenResult ok }) continue;

            created += ok.Created;
            skipped += ok.Skipped;
            warnings.AddRange(ok.Warnings);
            if (ok.GapDetails is not null)
            {
                gapDetails.AddRange(ok.GapDetails);
            }
        }

        return Ok(new AutoGenResult(created, skipped, warnings, gapDetails));
    }
    [HttpPost("autogen")]
    /// <summary>
    /// Створює чернетки на основі правил і доступних даних для заданого тижня.
    /// </summary>
    public async Task<ActionResult<AutoGenResult>> DraftAutoGen([FromBody] DraftAutoGenRequest r)
    {
        var types = await _db.LessonTypes.AsNoTracking().ToListAsync();
        if (types.Count == 0)
            return BadRequest(new AutoGenResult(0, 0, new() { "Типи занять відсутні або вимкнені." }));
        var typeBreakId = types.FirstOrDefault(t => t.Code.ToUpper() == "BREAK" && t.IsActive)?.Id;
        var typeCanceledId = types.FirstOrDefault(t => t.Code.ToUpper() == "CANCELED")?.Id;
        var excludedTypeIds = new HashSet<int>(new[] { typeBreakId, typeCanceledId }.Where(x => x != null)!.Select(x => x!.Value));
        var typeById = types.ToDictionary(t => t.Id);

        var activeStudyTypes = types.Where(t => t.IsActive && t.CountInPlan).OrderBy(t => t.Id).ToList();
        var preferredFirstTypeId = types.FirstOrDefault(t => t.PreferredFirstInWeek)?.Id ?? 0;

        int ltIndex = 0;
        int NextCyclicLessonTypeId() =>
            activeStudyTypes.Count > 0 ? activeStudyTypes[ltIndex++ % activeStudyTypes.Count].Id : preferredFirstTypeId;

        var weekStart = r.WeekStart;
        var weekEnd = weekStart.AddDays(7);

        bool DayAllowed(DayOfWeek dow)
        {
            var day = dow == DayOfWeek.Sunday ? 7 : (int)dow;
            return r.Days switch
            {
                WeekPreset.MonSun => day is >= 1 and <= 7,
                WeekPreset.MonSat => day is >= 1 and <= 6,
                _ => day is >= 1 and <= 5
            };
        }

        var calendar = await _db.CalendarExceptions
            .Where(c => c.Date >= weekStart && c.Date < weekEnd)
            .ToListAsync();

        bool IsWorking(DateOnly d, Group grp)
        {
            var scoped = ResolveCalendarOverride(calendar, d, grp.CourseId, grp.Id);
            if (scoped.HasValue) return scoped.Value;

            var dow = d.ToDateTime(TimeOnly.MinValue).DayOfWeek;
            if (!DayAllowed(dow) && !r.AllowOnDaysOff) return false;
            if (r.AllowOnDaysOff) return true;

            return dow != DayOfWeek.Saturday && dow != DayOfWeek.Sunday;
        }

        int? courseId = (r.CourseId > 0) ? r.CourseId : null;
        int? groupId = (r.GroupId > 0) ? r.GroupId : null;
        var moduleHoursByModuleId = r.ModuleHours?
            .Where(kv => kv.Value > 0)
            .ToDictionary(kv => kv.Key, kv => kv.Value)
            ?? new Dictionary<int, int>();
        bool hasModuleHourOverrides = moduleHoursByModuleId.Count > 0;
        var initWarnings = new List<string>();
        if (hasModuleHourOverrides && courseId is null)
        {
            return BadRequest(new
            {
                message = "Для генерації за модулями потрібно обрати курс."
            });
        }

        var groups = await _db.Groups
            .Include(x => x.Course)
            .Where(x => courseId == null || x.CourseId == courseId)
            .Where(x => groupId == null || x.Id == groupId)
            .ToListAsync();

        if (groups.Count == 0)
            return Ok(new AutoGenResult(0, 0, new() { "Групи не знайдено." }));

        if (hasModuleHourOverrides)
        {
            var cid = courseId!.Value;
            var allowedModuleIds = await _db.Modules
                .AsNoTracking()
                .Where(m => m.CourseId == cid || m.ModuleCourses.Any(mc => mc.CourseId == cid))
                .Select(m => m.Id)
                .ToListAsync();
            var allowedSet = allowedModuleIds.ToHashSet();
            var invalidModuleIds = moduleHoursByModuleId.Keys
                .Where(mid => !allowedSet.Contains(mid))
                .OrderBy(mid => mid)
                .ToList();
            if (invalidModuleIds.Count > 0)
            {
                foreach (var mid in invalidModuleIds)
                {
                    moduleHoursByModuleId.Remove(mid);
                }
                initWarnings.Add($"Ігноровано модулі, що не належать курсу #{cid}: {string.Join(", ", invalidModuleIds)}.");
            }

            if (moduleHoursByModuleId.Count == 0)
            {
                return BadRequest(new
                {
                    message = "Потрібно вибрати хоча б один модуль з годинами > 0."
                });
            }
        }

        if (r.ClearExisting)
        {
            var gids = groups.Select(g => g.Id).ToList();
            await _db.TeacherDraftItems
                .Where(x => x.Date >= weekStart && x.Date < weekEnd && gids.Contains(x.GroupId) && !x.IsLocked)
                .ExecuteDeleteAsync();
        }

        var lunchesAll = await _db.LunchConfigs.AsNoTracking().ToListAsync();
        var roomsAll = await _db.Rooms.AsNoTracking().ToListAsync();

        var moduleIdsAll = await _db.Modules.Select(m => m.Id).ToListAsync();

        var allowedRoomsByModule = await _db.ModuleRooms
            .Where(mr => moduleIdsAll.Contains(mr.ModuleId))
            .GroupBy(mr => mr.ModuleId)
            .ToDictionaryAsync(g => g.Key, g => g.Select(x => x.RoomId).ToHashSet());

        var allowedBuildingsByModule = await _db.ModuleBuildings
            .Where(mb => moduleIdsAll.Contains(mb.ModuleId))
            .GroupBy(mb => mb.ModuleId)
            .ToDictionaryAsync(g => g.Key, g => g.Select(x => x.BuildingId).ToHashSet());

        const int historyDaysForRepeats = 14;
        var historyStart = weekStart.AddDays(-historyDaysForRepeats);
        var lastWeekStart = weekStart.AddDays(-7);

        var busy = await _db.TeacherDraftItems
            .Include(x => x.Room)
            .Where(x => x.Date >= historyStart && x.Date < weekEnd)
            .Select(x => new BusySlot(
                x.GroupId,
                x.TeacherId,
                x.RoomId,
                x.Date,
                x.StartTime,
                x.EndTime,
                x.Room != null ? (int?)x.Room.BuildingId : null,
                x.ModuleId,
                x.LessonTypeId))
            .ToListAsync();

        var hasPreferred = new HashSet<(int groupId, int moduleId)>(
            busy.Where(b => preferredFirstTypeId != 0 && b.LessonTypeId == preferredFirstTypeId)
                .Select(b => (b.GroupId, b.ModuleId)));

        var lastWeekModulesByGroup = busy
            .Where(b => b.Date >= lastWeekStart
                        && b.Date < weekStart
                        && !excludedTypeIds.Contains(b.LessonTypeId))
            .GroupBy(b => b.GroupId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ModuleId).Distinct().ToHashSet());

        var perDayCount = new Dictionary<(int groupId, DateOnly date), int>();
        foreach (var b in busy.Where(b => !excludedTypeIds.Contains(b.LessonTypeId)))
        {
            var key = (b.GroupId, b.Date);
            perDayCount[key] = perDayCount.TryGetValue(key, out var existing) ? existing + 1 : 1;
        }
        int CountFor(int gid, DateOnly date) => perDayCount.TryGetValue((gid, date), out var c) ? c : 0;
        int CountModuleForDay(int gid, DateOnly date, int moduleId) =>
            busy.Count(x => x.GroupId == gid
                            && x.Date == date
                            && x.ModuleId == moduleId
                            && !excludedTypeIds.Contains(x.LessonTypeId));
        void Inc(int gid, DateOnly date)
        {
            var key = (gid, date);
            perDayCount[key] = CountFor(gid, date) + 1;
        }

        bool HadSameModulePreviousDay(int gid, int mid, DateOnly date)
        {
            var prev = date.AddDays(-1);
            return busy.Any(x => x.GroupId == gid
                                 && x.ModuleId == mid
                                 && x.Date == prev
                                 && !excludedTypeIds.Contains(x.LessonTypeId));
        }
        bool HasRecentModule(int gid, int mid, DateOnly date, int windowDays = 2)
        {
            var from = date.AddDays(-windowDays);
            var to = date.AddDays(windowDays);
            return busy.Any(x => x.GroupId == gid
                                 && x.ModuleId == mid
                                 && x.Date != date
                                 && x.Date >= from
                                 && x.Date <= to
                                 && !excludedTypeIds.Contains(x.LessonTypeId));
        }

        bool UsedLastWeek(int gid, int mid) =>
            lastWeekModulesByGroup.TryGetValue(gid, out var mods) && mods.Contains(mid);

        var teachersForModule = await _db.TeacherModules.AsNoTracking().ToListAsync();
        var supervisorsForModule = await _db.ModuleSupervisors.AsNoTracking().ToListAsync();

        var teacherNames = await _db.Teachers
            .AsNoTracking()
            .ToDictionaryAsync(
                t => t.Id,
                t => string.IsNullOrWhiteSpace(t.FullName) ? $"#{t.Id}" : t.FullName!
            );

        var teacherWorkingHours = await _db.TeacherWorkingHours.AsNoTracking()
            .Select(w => new { w.TeacherId, w.DayOfWeek, w.Start, w.End })
            .ToListAsync();

        var workingHoursByTeacher = teacherWorkingHours
            .GroupBy(w => w.TeacherId)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(x => x.DayOfWeek)
                    .ToDictionary(
                        d => d.Key,
                        d => d.Select(x => (x.Start, x.End)).ToList()
                    )
            );

        bool TeacherFitsWorkingHours(int teacherId, DateOnly date, TimeOnly start, TimeOnly end)
        {
            if (!workingHoursByTeacher.TryGetValue(teacherId, out var dayMap) || dayMap.Count == 0)
                return true;

            var dow = date.ToDateTime(TimeOnly.MinValue).DayOfWeek;
            if (!dayMap.TryGetValue(dow, out var windows) || windows.Count == 0)
                return false;

            return windows.Any(w => w.Start <= start && end <= w.End);
        }

        var courseIds = groups.Select(g => g.CourseId).Distinct().ToList();
        var teacherLoadSchedule = await _db.ScheduleItems
            .Include(si => si.Group)
            .Where(si => si.TeacherId != null && courseIds.Contains(si.Group.CourseId))
            .GroupBy(si => new { si.TeacherId, si.Group.CourseId })
            .Select(g => new { TeacherId = g.Key.TeacherId!.Value, g.Key.CourseId, C = g.Count() })
            .ToListAsync();

        var teacherLoadDrafts = await _db.TeacherDraftItems
            .Include(di => di.Group)
            .Where(di => di.TeacherId != null && courseIds.Contains(di.Group.CourseId))
            .GroupBy(di => new { di.TeacherId, di.Group.CourseId })
            .Select(g => new { TeacherId = g.Key.TeacherId!.Value, g.Key.CourseId, C = g.Count() })
            .ToListAsync();

        var teacherLoadMap = teacherLoadSchedule
            .Concat(teacherLoadDrafts)
            .GroupBy(x => (x.TeacherId, x.CourseId))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.C));
        var activePlans = await _db.ModulePlans.Where(p => courseIds.Contains(p.CourseId) && p.IsActive).ToListAsync();

        int TeacherLoadScore(int teacherId, int courseId)
        {
            if (teacherLoadMap.TryGetValue((teacherId, courseId), out var count))
            {
                return count;
            }
            return 0;
        }

        var moduleIdsForPlans = activePlans.Select(p => p.ModuleId).Distinct().ToList();
        if (hasModuleHourOverrides)
        {
            moduleIdsForPlans = moduleIdsForPlans
                .Concat(moduleHoursByModuleId.Keys)
                .Distinct()
                .ToList();
        }
        var moduleTitles = await _db.Modules.AsNoTracking()
            .Where(m => moduleIdsForPlans.Contains(m.Id))
            .ToDictionaryAsync(
                m => m.Id,
                m => string.IsNullOrWhiteSpace(m.Title) ? $"#{m.Id}" : m.Title.Trim());
        string ModuleTitleLabel(int moduleId) =>
            moduleTitles.TryGetValue(moduleId, out var title) ? title : $"#{moduleId}";

        var topicsAll = await _db.ModuleTopics
            .Where(t => moduleIdsForPlans.Contains(t.ModuleId))
            .OrderBy(t => t.Order)
            .ThenBy(t => t.TopicCode)
            .ToListAsync();
        var interAssemblyOnlyModules = topicsAll
            .GroupBy(t => t.ModuleId)
            .Where(g => g.Any() && g.All(x => x.IsInterAssembly))
            .Select(g => g.Key)
            .ToHashSet();
        var topicsRaw = topicsAll
            .Where(t => !t.IsInterAssembly)
            .ToList();
        var allowedTopicIds = topicsRaw.Select(t => t.Id).ToHashSet();
        var flaggedSelfStudyTopicIds = topicsRaw
            .Where(t => t.SelfStudyBySupervisor && t.SelfStudyHours > 0)
            .Select(t => t.Id)
            .ToHashSet();
        var topicsByModule = topicsRaw
            .GroupBy(t => t.ModuleId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var selfStudyTopicsByModule = topicsByModule
            .ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value
                    .Where(t => t.SelfStudyBySupervisor && t.SelfStudyHours > 0)
                    .OrderBy(t => t.TopicCode, StringComparer.Ordinal)
                    .ToList());
        var topicUsageLimitById = topicsRaw
            .ToDictionary(t => t.Id, t => Math.Max(0, t.AuditoriumHours));
        var moduleAuditoriumHours = topicsByModule
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Sum(t => Math.Max(0, t.AuditoriumHours)));
        var moduleSelfStudyHours = topicsByModule
            .ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value
                    .Where(t => t.SelfStudyBySupervisor)
                    .Sum(t => Math.Max(0, t.SelfStudyHours)));
        var selfStudyAssignmentsDraft = await _db.TeacherDraftItems
            .Where(di => di.IsSelfStudy
                         && di.Date < weekStart
                         && courseIds.Contains(di.Group.CourseId)
                         && (di.ModuleTopicId == null || flaggedSelfStudyTopicIds.Contains(di.ModuleTopicId.Value)))
            .Select(di => new { di.GroupId, di.ModuleId })
            .ToListAsync();
        var selfStudyAssignmentsSchedule = await _db.ScheduleItems
            .Where(si => si.IsSelfStudy
                         && si.Date < weekStart
                         && courseIds.Contains(si.Group.CourseId)
                         && (si.ModuleTopicId == null || flaggedSelfStudyTopicIds.Contains(si.ModuleTopicId.Value)))
            .Select(si => new { si.GroupId, si.ModuleId })
            .ToListAsync();
        var selfStudyTopicAssignmentsDraft = await _db.TeacherDraftItems
            .Where(di => di.IsSelfStudy
                         && di.Date < weekStart
                         && di.ModuleTopicId != null
                         && flaggedSelfStudyTopicIds.Contains(di.ModuleTopicId!.Value)
                         && courseIds.Contains(di.Group.CourseId))
            .GroupBy(di => new { di.GroupId, di.ModuleId, TopicId = di.ModuleTopicId!.Value })
            .Select(g => new { g.Key.GroupId, g.Key.ModuleId, g.Key.TopicId, C = g.Count() })
            .ToListAsync();
        var selfStudyTopicAssignmentsSchedule = await _db.ScheduleItems
            .Where(si => si.IsSelfStudy
                         && si.Date < weekStart
                         && si.ModuleTopicId != null
                         && flaggedSelfStudyTopicIds.Contains(si.ModuleTopicId!.Value)
                         && courseIds.Contains(si.Group.CourseId))
            .GroupBy(si => new { si.GroupId, si.ModuleId, TopicId = si.ModuleTopicId!.Value })
            .Select(g => new { g.Key.GroupId, g.Key.ModuleId, g.Key.TopicId, C = g.Count() })
            .ToListAsync();
        var selfStudyAssignments = selfStudyAssignmentsDraft
            .Concat(selfStudyAssignmentsSchedule)
            .GroupBy(x => (x.GroupId, x.ModuleId))
            .ToDictionary(g => g.Key, g => g.Count());
        var selfStudyTopicAssignments = selfStudyTopicAssignmentsDraft
            .Concat(selfStudyTopicAssignmentsSchedule)
            .GroupBy(x => (x.GroupId, x.ModuleId, x.TopicId))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.C));
        var topicAssignmentsDraft = await _db.TeacherDraftItems
            .Where(di => di.ModuleTopicId != null
                         && di.Date < weekStart
                         && courseIds.Contains(di.Group.CourseId)
                         && allowedTopicIds.Contains(di.ModuleTopicId!.Value))
            .Select(di => new { di.GroupId, di.ModuleId, TopicId = di.ModuleTopicId!.Value })
            .ToListAsync();
        var topicAssignmentsSchedule = await _db.ScheduleItems
            .Where(si => si.ModuleTopicId != null
                         && si.Date < weekStart
                         && courseIds.Contains(si.Group.CourseId)
                         && allowedTopicIds.Contains(si.ModuleTopicId!.Value))
            .Select(si => new { si.GroupId, si.ModuleId, TopicId = si.ModuleTopicId!.Value })
            .ToListAsync();
        var topicAssignments = new Dictionary<(int GroupId, int ModuleId), Dictionary<int, int>>();

        void SeedTopicAssignment(int groupId, int moduleId, int topicId)
        {
            var key = (groupId, moduleId);
            if (!topicAssignments.TryGetValue(key, out var assigned))
            {
                assigned = new Dictionary<int, int>();
                topicAssignments[key] = assigned;
            }

            assigned.TryGetValue(topicId, out var usedCount);
            assigned[topicId] = usedCount + 1;
        }

        foreach (var entry in topicAssignmentsDraft.Concat(topicAssignmentsSchedule))
        {
            SeedTopicAssignment(entry.GroupId, entry.ModuleId, entry.TopicId);
        }
        var topicUsageGlobal = new Dictionary<int, int>();
        foreach (var kvp in topicAssignments)
        {
            foreach (var topicEntry in kvp.Value)
            {
                topicUsageGlobal[topicEntry.Key] = topicUsageGlobal.TryGetValue(topicEntry.Key, out var used)
                    ? used + topicEntry.Value
                    : topicEntry.Value;
            }
        }

        int GetTopicUsageLimit(ModuleTopic topic)
            => topicUsageLimitById.TryGetValue(topic.Id, out var limit) ? limit : Math.Max(0, topic.AuditoriumHours);

        ModuleTopic? SelectLeastUsedTopic(int groupId, int moduleId)
        {
            if (!topicsByModule.TryGetValue(moduleId, out var list) || list.Count == 0)
                return null;

            topicAssignments.TryGetValue((groupId, moduleId), out var assigned);

            var candidates = list
                .Select(t =>
                {
                    var limit = GetTopicUsageLimit(t);
                    var usedByGroup = (assigned != null && assigned.TryGetValue(t.Id, out var usedGroupVal)) ? usedGroupVal : 0;
                    var usedGlobal = topicUsageGlobal.TryGetValue(t.Id, out var usedGlobalVal) ? usedGlobalVal : 0;
                    return new
                    {
                        Topic = t,
                        Limit = limit,
                        UsedByGroup = usedByGroup,
                        UsedGlobal = usedGlobal
                    };
                })
                .Where(x => x.Limit > 0 && x.UsedByGroup < x.Limit)
                .OrderBy(x => x.UsedGlobal)
                .ThenBy(x => x.Topic.Order)
                .ThenBy(x => x.Topic.TopicCode, StringComparer.Ordinal)
                .FirstOrDefault();

            return candidates?.Topic;
        }

        var topicsExhaustedNotified = new HashSet<(int GroupId, int ModuleId)>();
        var missingModulesNotified = new HashSet<int>();
        int created = 0, skipped = 0;
        var warnings = new List<string>();
        if (initWarnings.Count > 0)
        {
            warnings.AddRange(initWarnings);
        }
        var gapDetails = new List<AutoGenGapDetail>();
        var gapWarnings = new HashSet<(int GroupId, DateOnly Date, TimeOnly Start, TimeOnly End)>();
        var slotFailureReasons = new Dictionary<(int GroupId, DateOnly Date, TimeOnly Start, TimeOnly End), HashSet<string>>();


        bool TypeAllowed(int lessonTypeId)
        {
            return typeById.TryGetValue(lessonTypeId, out var lt)
                   && lt.IsActive
                   && lt.CountInPlan
                   && !excludedTypeIds.Contains(lessonTypeId);
        }

        bool IsLectureType(int lessonTypeId) =>
            typeById.TryGetValue(lessonTypeId, out var lt)
            && string.Equals(lt.Code, "LECTURE", StringComparison.OrdinalIgnoreCase);

        bool HasPendingLectureBeforeLunch(int gid)
        {
            foreach (var kvp in topicsByModule)
            {
                var mid = kvp.Key;
                if (RemainingFor(gid, mid) <= 0) continue;

                var topicCandidate = SelectLeastUsedTopic(gid, mid);
                if (topicCandidate is not null && IsLectureType(topicCandidate.LessonTypeId))
                {
                    return true;
                }
            }
            return false;
        }

        void MarkTopicUsed(int groupId, int moduleId, ModuleTopic topic)
        {
            var key = (groupId, moduleId);
            if (!topicAssignments.TryGetValue(key, out var assigned))
            {
                assigned = new Dictionary<int, int>();
                topicAssignments[key] = assigned;
            }

            assigned.TryGetValue(topic.Id, out var used);
            assigned[topic.Id] = used + 1;

            topicUsageGlobal[topic.Id] = topicUsageGlobal.TryGetValue(topic.Id, out var usedGlobal)
                ? usedGlobal + 1
                : 1;
        }

        bool TopicsDepleted(int groupIdCheck, int moduleIdCheck)
        {
            if (!topicsByModule.TryGetValue(moduleIdCheck, out var list) || list.Count == 0)
                return false;

            var key = (groupIdCheck, moduleIdCheck);
            topicAssignments.TryGetValue(key, out var assigned);
            return list.All(topic =>
            {
                var limit = GetTopicUsageLimit(topic);
                if (limit <= 0) return true;
                var usedCount = assigned != null && assigned.TryGetValue(topic.Id, out var count) ? count : 0;
                return usedCount >= limit;
            });
        }

        bool HadSameLessonTypePreviousDay(int gid, int mid, int lessonTypeId, DateOnly date)
        {
            var prev = date.AddDays(-1);
            return busy.Any(x => x.GroupId == gid
                                 && x.ModuleId == mid
                                 && x.Date == prev
                                 && x.LessonTypeId == lessonTypeId
                                 && !excludedTypeIds.Contains(x.LessonTypeId));
        }

        (int LessonTypeId, ModuleTopic? Topic) PickLessonType(int groupIdPick, int courseIdPick, int moduleIdPick, DateOnly date)
        {
            var topicCandidate = SelectLeastUsedTopic(groupIdPick, moduleIdPick);
            if (topicCandidate is not null && TypeAllowed(topicCandidate.LessonTypeId))
            {
                return (topicCandidate.LessonTypeId, topicCandidate);
            }

            if (!hasPreferred.Contains((groupIdPick, moduleIdPick))
                && preferredFirstTypeId != 0
                && TypeAllowed(preferredFirstTypeId)
                && !HadSameLessonTypePreviousDay(groupIdPick, moduleIdPick, preferredFirstTypeId, date))
            {
                return (preferredFirstTypeId, null);
            }

            var cycleCount = activeStudyTypes.Count == 0 ? 1 : activeStudyTypes.Count;
            for (int attempt = 0; attempt < cycleCount; attempt++)
            {
                var candidate = NextCyclicLessonTypeId();
                if (!TypeAllowed(candidate)) continue;
                if (HadSameLessonTypePreviousDay(groupIdPick, moduleIdPick, candidate, date) && cycleCount > 1)
                    continue;

                return (candidate, null);
            }

            var fallbackType = activeStudyTypes.FirstOrDefault()?.Id ?? preferredFirstTypeId;
            if (fallbackType != 0 && TypeAllowed(fallbackType))
            {
                return (fallbackType, null);
            }

            return (types.First().Id, null);
        }
        var sequenceItems = await _db.ModuleSequenceItems
            .Where(x => courseIds.Contains(x.CourseId))
            .OrderBy(x => x.Order)
            .ToListAsync();
        var mainSequenceByCourse = sequenceItems
            .GroupBy(x => x.CourseId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ModuleId).ToList());

        var fillerByCourse = await _db.ModuleFillers
            .Where(x => courseIds.Contains(x.CourseId))
            .GroupBy(x => x.CourseId)
            .ToDictionaryAsync(g => g.Key, g => g.Select(x => x.ModuleId).ToHashSet());

        List<int> BuildCourseModuleOrder(int courseId, List<int> planModules)
        {
            var ordered = new List<int>();
            var seen = new HashSet<int>();
            var planSet = new HashSet<int>(planModules);

            if (mainSequenceByCourse.TryGetValue(courseId, out var mainSequence))
            {
                foreach (var mid in mainSequence)
                {
                    if (planSet.Contains(mid) && seen.Add(mid))
                    {
                        ordered.Add(mid);
                    }
                }
            }

            if (fillerByCourse.TryGetValue(courseId, out var fillerSet) && fillerSet.Count > 0)
            {
                foreach (var mid in planModules)
                {
                    if (fillerSet.Contains(mid) && seen.Add(mid))
                    {
                        ordered.Add(mid);
                    }
                }
            }

            foreach (var mid in planModules)
            {
                if (seen.Add(mid))
                {
                    ordered.Add(mid);
                }
            }

            return ordered;
        }

        var remainingByGroupModule = new Dictionary<(int GroupId, int ModuleId), int>();
        var allGroupsByCourse = await _db.Groups
            .Where(g => courseIds.Contains(g.CourseId))
            .GroupBy(g => g.CourseId)
            .ToDictionaryAsync(g => g.Key, g => g.OrderBy(x => x.Id).ToList());

        var factByGroupModule = await _db.TeacherDraftItems
            .Where(si => !excludedTypeIds.Contains(si.LessonTypeId) && courseIds.Contains(si.Group.CourseId))
            .GroupBy(si => new { si.GroupId, si.ModuleId })
            .Select(g => new { g.Key.GroupId, g.Key.ModuleId, C = g.Count() })
            .ToListAsync();
        var factMap = factByGroupModule.ToDictionary(k => (k.GroupId, k.ModuleId), v => v.C);

        var scheduleByGroupModule = await _db.ScheduleItems
            .Where(si => !excludedTypeIds.Contains(si.LessonTypeId) && courseIds.Contains(si.Group.CourseId))
            .GroupBy(si => new { si.GroupId, si.ModuleId })
            .Select(g => new { g.Key.GroupId, g.Key.ModuleId, C = g.Count() })
            .ToListAsync();
        foreach (var item in scheduleByGroupModule)
        {
            var key = (item.GroupId, item.ModuleId);
            if (factMap.TryGetValue(key, out var existing))
            {
                factMap[key] = existing + item.C;
            }
            else
            {
                factMap[key] = item.C;
            }
        }

        var selfStudyRemainingByGroupModule = new Dictionary<(int GroupId, int ModuleId), int>();
        var selfStudyTopicRemaining = new Dictionary<(int GroupId, int ModuleId, int TopicId), int>();
        if (hasModuleHourOverrides)
        {
            var orderedSelectedModules = moduleHoursByModuleId.Keys.OrderBy(mid => mid).ToList();
            var selectedGroups = groups.OrderBy(g => g.Id).ToList();

            foreach (var moduleId in orderedSelectedModules)
            {
                if (interAssemblyOnlyModules.Contains(moduleId))
                {
                    foreach (var grpRow in selectedGroups)
                    {
                        remainingByGroupModule[(grpRow.Id, moduleId)] = 0;
                    }

                    var moduleLabel = ModuleTitleLabel(moduleId);
                    var groupList = string.Join(", ", selectedGroups.Select(g => g.Name));
                    warnings.Add($"Модуль \"{moduleLabel}\" містить лише міжзборові теми, тому автогенерація пропускає його для груп: {groupList}.");
                    continue;
                }

                var hours = moduleHoursByModuleId[moduleId];
                foreach (var grpRow in selectedGroups)
                {
                    remainingByGroupModule[(grpRow.Id, moduleId)] = Math.Max(0, hours);
                }

                if (!selfStudyTopicsByModule.TryGetValue(moduleId, out var ssTopics) || ssTopics.Count == 0)
                    continue;

                foreach (var grpRow in selectedGroups)
                {
                    var total = 0;
                    foreach (var topic in ssTopics)
                    {
                        var key = (grpRow.Id, moduleId, topic.Id);
                        var factPerTopic = selfStudyTopicAssignments.TryGetValue(key, out var used) ? used : 0;
                        var remaining = Math.Max(0, topic.SelfStudyHours - factPerTopic);
                        if (remaining > 0)
                        {
                            selfStudyTopicRemaining[key] = remaining;
                            total += remaining;
                        }
                    }

                    if (total > 0)
                    {
                        selfStudyRemainingByGroupModule[(grpRow.Id, moduleId)] = total;
                    }
                }
            }
        }
        else
        {
            foreach (var plan in activePlans)
        {
            if (!allGroupsByCourse.TryGetValue(plan.CourseId, out var courseGroups) || courseGroups.Count == 0)
                continue;

            if (interAssemblyOnlyModules.Contains(plan.ModuleId))
            {
                foreach (var grpRow in courseGroups)
                {
                    remainingByGroupModule[(grpRow.Id, plan.ModuleId)] = 0;
                }

                var moduleLabel = ModuleTitleLabel(plan.ModuleId);
                var groupList = string.Join(", ", courseGroups.Select(g => g.Name));
                warnings.Add($"Модуль \"{moduleLabel}\" містить лише міжзборові теми, тому автогенерація пропускає його для груп: {groupList}.");
                continue;
            }

            var excludedSelfStudy = topicsByModule.TryGetValue(plan.ModuleId, out var tlist)
                ? tlist.Where(t => t.SelfStudyHours > 0 && !t.SelfStudyBySupervisor).Sum(t => Math.Max(0, t.SelfStudyHours))
                : 0;
            var effectiveTargetHours = Math.Max(0, plan.TargetHours - excludedSelfStudy);
            int n = courseGroups.Count;
            int baseShare = effectiveTargetHours / n;
            int extra = effectiveTargetHours % n;
            var moduleMinHours =
                (moduleAuditoriumHours.TryGetValue(plan.ModuleId, out var minHours) ? minHours : 0)
                + (moduleSelfStudyHours.TryGetValue(plan.ModuleId, out var ssHours) ? ssHours : 0);

            for (int i = 0; i < n; i++)
            {
                int gid = courseGroups[i].Id;
                int planShare = baseShare + (i < extra ? 1 : 0);
                int target = Math.Max(planShare, moduleMinHours);
                int fact = factMap.TryGetValue((gid, plan.ModuleId), out var c) ? c : 0;
                remainingByGroupModule[(gid, plan.ModuleId)] = Math.Max(0, target - fact);
            }

            if (!selfStudyTopicsByModule.TryGetValue(plan.ModuleId, out var ssTopics) || ssTopics.Count == 0)
                continue;

            foreach (var grpRow in courseGroups)
            {
                var total = 0;
                foreach (var topic in ssTopics)
                {
                    var key = (grpRow.Id, plan.ModuleId, topic.Id);
                    var factPerTopic = selfStudyTopicAssignments.TryGetValue(key, out var used) ? used : 0;
                    var remaining = Math.Max(0, topic.SelfStudyHours - factPerTopic);
                    if (remaining > 0)
                    {
                        selfStudyTopicRemaining[key] = remaining;
                        total += remaining;
                    }
                }

                if (total > 0)
                {
                    selfStudyRemainingByGroupModule[(grpRow.Id, plan.ModuleId)] = total;
                }
            }
        }
        }
        int RemainingFor(int gid, int mid) =>
            remainingByGroupModule.TryGetValue((gid, mid), out var left) ? left : 0;
        int SelfStudyRemaining(int gid, int mid) =>
            selfStudyRemainingByGroupModule.TryGetValue((gid, mid), out var left) ? left : 0;

        ModuleTopic? NextSelfStudyTopic(int gid, int mid)
        {
            if (!selfStudyTopicsByModule.TryGetValue(mid, out var list) || list.Count == 0)
                return null;

            foreach (var t in list)
            {
                var key = (gid, mid, t.Id);
                if (selfStudyTopicRemaining.TryGetValue(key, out var left) && left > 0)
                {
                    selfStudyTopicRemaining[key] = Math.Max(0, left - 1);
                    return t;
                }
            }

            return null;
        }



        foreach (var grp in groups)
        {
            var groupLunch = lunchesAll.FirstOrDefault(l => l.CourseId == grp.CourseId)
                          ?? lunchesAll.FirstOrDefault(l => l.CourseId == null);
            var hasCourseSlots = await _db.TimeSlots.AsNoTracking().AnyAsync(s => s.CourseId == grp.CourseId && s.IsActive);
            var slots = await _db.TimeSlots.AsNoTracking()
                .Where(s => s.IsActive && (hasCourseSlots ? s.CourseId == grp.CourseId : s.CourseId == null))
                .OrderBy(s => s.SortOrder).ThenBy(s => s.Start)
                .ToListAsync();

            if (slots.Count == 0)
            {
                warnings.Add($"Не знайдено жодного слоту розкладу (глобального або для курсу {grp.Course.Name}). Група {grp.Name} пропущена.");
                continue;
            }

            bool SlotFilled(DateOnly date, TimeSlot slot) =>
                busy.Any(b => b.GroupId == grp.Id && b.Date == date && b.StartTime == slot.Start && b.EndTime == slot.End);

            bool DayHasGaps(DateOnly date, out TimeSlot? firstGap)
            {
                foreach (var slot in slots)
                {
                    if (!SlotFilled(date, slot))
                    {
                        firstGap = slot;
                        return true;
                    }

                }

                firstGap = null;
                return false;
            }



            var createdDrafts = new List<TeacherDraftItem>();
            string TeacherLabel(int teacherId) =>
                teacherNames.TryGetValue(teacherId, out var name) && !string.IsNullOrWhiteSpace(name)
                    ? name
                    : $"#{teacherId}";

            void RecordSlotFailureReason(DateOnly date, TimeSlot slot, string reason)
            {
                var key = (grp.Id, date, slot.Start, slot.End);
                if (!slotFailureReasons.TryGetValue(key, out var reasons))
                {
                    reasons = new HashSet<string>();
                    slotFailureReasons[key] = reasons;
                }
                reasons.Add(reason);
            }

            void RecordSlotFailureReasonForAllSlots(DateOnly date, string reason)
            {
                foreach (var slot in slots)
                {
                    RecordSlotFailureReason(date, slot, reason);
                }
            }

            string? ComposeGapReason(DateOnly date, TimeSlot slot)
            {
                var key = (grp.Id, date, slot.Start, slot.End);
                if (slotFailureReasons.TryGetValue(key, out var reasons) && reasons.Count > 0)
                {
                    return string.Join("; ", reasons);
                }

                if (!remainingByGroupModule.Any(entry => entry.Key.GroupId == grp.Id && entry.Value > 0))
                {
                    return $"Для групи {grp.Name} більше не лишилось модулів із невикористаними годинами.";
                }

                return "Причину не вдалося визначити автоматично. Перевірте доступність викладачів, аудиторій, обмеження за типом заняття та дозволені повторення.";
            }

            void WarnGap(DateOnly date, TimeSlot gap)
            {
                var label = $"{gap.Start:HH\\:mm}-{gap.End:HH\\:mm}";
                var key = (grp.Id, date, gap.Start, gap.End);
                if (gapWarnings.Add(key))
                {
                    if (!slotFailureReasons.ContainsKey(key))
                    {
                        var modulesWithHours = remainingByGroupModule
                            .Where(kv => kv.Key.GroupId == grp.Id && kv.Value > 0)
                            .Select(kv => kv.Key.ModuleId)
                            .Distinct()
                            .ToList();

                        if (modulesWithHours.Count == 0)
                        {
                            RecordSlotFailureReason(date, gap, $"Для групи {grp.Name} не лишилось модулів із невикористаними годинами.");
                        }
                        else
                        {
                            var noTeachers = modulesWithHours
                                .Where(mid => !teachersForModule.Any(t => t.ModuleId == mid))
                                .Select(mid => $"#{mid}")
                                .ToList();

                            var noRooms = modulesWithHours
                                .Where(mid => CandidateRoomsFor(mid).Count == 0)
                                .Select(mid => $"#{mid}")
                                .ToList();

                            if (noTeachers.Count > 0)
                            {
                                RecordSlotFailureReason(date, gap, $"Немає викладачів для модулів: {string.Join(", ", noTeachers)}.");
                            }
                            else if (noRooms.Count > 0)
                            {
                                RecordSlotFailureReason(date, gap, $"Немає доступних аудиторій для модулів: {string.Join(", ", noRooms)}.");
                            }
                            else if (CountFor(grp.Id, date) >= slots.Count)
                            {
                                RecordSlotFailureReason(date, gap, $"Досягнуто максимум пар на день для групи {grp.Name} ({slots.Count}).");
                            }
                            else
                            {
                                RecordSlotFailureReason(date, gap, $"Не вдалося підібрати комбінацію модуль/викладач/аудиторія для слоту {label}: усі варіанти зайняті або заборонені правилами.");
                            }
                        }
                    }
                    var reason = ComposeGapReason(date, gap);
                    var reasonSuffix = string.IsNullOrWhiteSpace(reason) ? string.Empty : $" Причина: {reason}";
                    warnings.Add($"Автогенерація не заповнила слот {label} для групи {grp.Name} на {date:yyyy-MM-dd}.{reasonSuffix}");
                    gapDetails.Add(new AutoGenGapDetail(
                        GroupId: grp.Id,
                        GroupName: grp.Name,
                        Date: date,
                        Start: gap.Start,
                        End: gap.End,
                        SlotLabel: label,
                        Reason: reason));
                }
            }

            bool TryShiftGaps(DateOnly date)
            {
                bool moved = false;
                var attempted = new HashSet<TeacherDraftItem>();

                while (DayHasGaps(date, out var gap) && gap is not null)
                {
                    var candidate = createdDrafts
                        .Where(cd => cd.GroupId == grp.Id
                                     && cd.Date == date
                                     && !cd.IsLocked
                                     && cd.StartTime > gap.Start
                                     && !attempted.Contains(cd))
                        .OrderBy(cd => cd.StartTime)
                        .FirstOrDefault();

                    if (candidate is null)
                    {
                        break;
                    }

                    attempted.Add(candidate);

                    if (typeBreakId.HasValue && candidate.LessonTypeId == typeBreakId.Value)
                    {
                        continue;
                    }

                    var s = gap.Start;
                    var e = gap.End;
                    int? tidCandidate = candidate.TeacherId;
                    if (tidCandidate is int tidVal && !TeacherFitsWorkingHours(tidVal, date, s, e))
                    {
                        continue;
                    }

                    bool peopleBusy = busy.Any(x => x.Date == date
                                                    && (x.GroupId == grp.Id || (tidCandidate is int t && x.TeacherId == t))
                                                    && !(x.GroupId == candidate.GroupId
                                                         && x.StartTime == candidate.StartTime
                                                         && x.EndTime == candidate.EndTime
                                                         && x.ModuleId == candidate.ModuleId
                                                         && x.TeacherId == candidate.TeacherId
                                                         && x.RoomId == candidate.RoomId)
                                                    && x.StartTime < e && s < x.EndTime);
                    if (peopleBusy)
                    {
                        continue;
                    }

                    bool requiresRoom = typeById.TryGetValue(candidate.LessonTypeId, out var ltMeta)
                        ? ltMeta.RequiresRoom
                        : true;
                    if (requiresRoom && candidate.RoomId is int rid)
                    {
                        bool roomBusy = busy.Any(x => x.Date == date
                                                      && x.RoomId == rid
                                                      && !(x.GroupId == candidate.GroupId
                                                           && x.StartTime == candidate.StartTime
                                                           && x.EndTime == candidate.EndTime
                                                           && x.ModuleId == candidate.ModuleId
                                                           && x.RoomId == candidate.RoomId)
                                                      && x.StartTime < e && s < x.EndTime);
                        if (roomBusy)
                        {
                            continue;
                        }
                    }

                    var oldStart = candidate.StartTime;
                    var oldEnd = candidate.EndTime;
                    var idx = busy.FindIndex(x =>
                        x.GroupId == candidate.GroupId
                        && x.Date == date
                        && x.StartTime == oldStart
                        && x.EndTime == oldEnd
                        && x.ModuleId == candidate.ModuleId
                        && x.TeacherId == candidate.TeacherId
                        && x.RoomId == candidate.RoomId);
                    if (idx >= 0)
                    {
                        busy.RemoveAt(idx);
                    }

                    var buildingId = candidate.RoomId.HasValue
                        ? roomsAll.FirstOrDefault(r => r.Id == candidate.RoomId)?.BuildingId
                        : null;

                    busy.Add(new BusySlot(
                        candidate.GroupId,
                        candidate.TeacherId,
                        candidate.RoomId,
                        date,
                        s,
                        e,
                        buildingId,
                        candidate.ModuleId,
                        candidate.LessonTypeId));

                    candidate.StartTime = s;
                    candidate.EndTime = e;
                    candidate.DayOfWeek = date.ToDateTime(TimeOnly.MinValue).DayOfWeek;

                    warnings.Add($"[{date:yyyy-MM-dd} {s:HH\\:mm}-{e:HH\\:mm}] {grp.Name}: заняття пересунуто, щоб прибрати розрив.");
                    moved = true;
                }

                return moved;
            }

            if (typeBreakId.HasValue && groupLunch is not null)
            {
                var anyModuleId = await _db.ModuleCourses
                    .Where(mc => mc.CourseId == grp.CourseId)
                    .Select(mc => mc.ModuleId)
                    .FirstOrDefaultAsync();
                if (anyModuleId != 0)
                {
                    var candidateSlots = slots
                        .Where(sl => sl.Start <= groupLunch.Start && groupLunch.End <= sl.End)
                        .ToList();

                    if (candidateSlots.Count == 0)
                    {
                        candidateSlots = slots
                            .Where(sl => sl.Start < groupLunch.End && groupLunch.Start < sl.End)
                            .ToList();
                    }

                    for (int d = 0; d < 7; d++)
                    {
                        var date = weekStart.AddDays(d);
                        if (!IsWorking(date, grp)) continue;

                        foreach (var slot in candidateSlots)
                        {
                            var s = slot.Start;
                            var e = slot.End;

                            var hasBreak = busy.Any(b =>
                                b.GroupId == grp.Id &&
                                b.Date == date &&
                                b.StartTime == s &&
                                b.EndTime == e &&
                                b.LessonTypeId == typeBreakId.Value);
                            if (hasBreak) continue;

                            var breakItem = new TeacherDraftItem
                            {
                                Date = date,
                                DayOfWeek = date.ToDateTime(TimeOnly.MinValue).DayOfWeek,
                                StartTime = s,
                                EndTime = e,
                                GroupId = grp.Id,
                                ModuleId = anyModuleId,
                                LessonTypeId = typeBreakId.Value,
                                Status = DraftStatus.Draft,
                                IsLocked = false
                            };

                            _db.TeacherDraftItems.Add(breakItem);

                            busy.Add(new BusySlot(grp.Id, null, null, date, s, e, null, anyModuleId, typeBreakId.Value));
                        }
                    }
                }
            }

            var modules = hasModuleHourOverrides
                ? remainingByGroupModule
                    .Where(kv => kv.Key.GroupId == grp.Id && kv.Value > 0)
                    .Select(kv => kv.Key.ModuleId)
                    .Distinct()
                    .ToList()
                : activePlans.Where(p => p.CourseId == grp.CourseId).Select(p => p.ModuleId).Distinct().ToList();
            var orderedModules = BuildCourseModuleOrder(grp.CourseId, modules);
            fillerByCourse.TryGetValue(grp.CourseId, out var fillerSetRaw);
            var fillerLookup = fillerSetRaw is not null
                ? new HashSet<int>(fillerSetRaw)
                : new HashSet<int>();
            var fillerModulesOrdered = fillerLookup.OrderBy(x => x).ToList();
            var mainModulesOrdered = orderedModules.Where(mid => !fillerLookup.Contains(mid)).ToList();

            var groupRandom = new Random(HashCode.Combine(weekStart.DayNumber, grp.Id, grp.CourseId));
            var groupRoomUsage = busy
                .Where(b => b.GroupId == grp.Id && b.Date >= weekStart && b.Date < weekEnd && b.RoomId != null)
                .GroupBy(b => b.RoomId!.Value)
                .ToDictionary(g => g.Key, g => g.Count());

            const double penaltySameModulePrevDay = 10.0;
            const double penaltyExtraSameDay = 6.0;
            const double penaltyNonLecturePreLunch = 2.0;
            const double penaltySameSlotPattern = 1.5;
            double TeacherLoadPenalty(int teacherId) =>
                TeacherLoadScore(teacherId, grp.CourseId) * 0.25;

            double BuildingDistancePenalty(int teacherId, Room? room, DateOnly date, TimeOnly start)
            {
                if (room is null || room.BuildingId == 0)
                    return 2.0;

                double score = 0;
                var groupPrev = LastGroupBuilding(date, start);
                var teacherPrev = LastTeacherBuilding(teacherId, date, start);

                if (groupPrev is int gb && gb != room.BuildingId) score += 1.0;
                if (teacherPrev is int tb && tb != room.BuildingId) score += 1.0;

                return score;
            }

            int CountCompletedPrimary()
            {
                int total = 0;
                foreach (var mid in mainModulesOrdered)
                {
                    if (factMap.TryGetValue((grp.Id, mid), out var c))
                    {
                        total += c;
                    }
                }
                return total;
            }

            int nextModuleIndex = mainModulesOrdered.Count > 0
                ? CountCompletedPrimary() % mainModulesOrdered.Count
                : 0;

            bool HasAvailableAlternativeForSlot(int currentModuleId, DateOnly date, TimeOnly start, TimeOnly end)
            {
                foreach (var altModuleId in orderedModules)
                {
                    if (altModuleId == currentModuleId) continue;
                    if (RemainingFor(grp.Id, altModuleId) <= 0) continue;
                    if (HasRecentModule(grp.Id, altModuleId, date, windowDays: 2)) continue;

                    bool altSelfStudy = SelfStudyRemaining(grp.Id, altModuleId) > 0;
                    var altTids = (altSelfStudy
                            ? supervisorsForModule.Where(x => x.ModuleId == altModuleId).Select(x => x.TeacherId)
                            : teachersForModule.Where(x => x.ModuleId == altModuleId).Select(x => x.TeacherId))
                        .Distinct()
                        .OrderBy(id => TeacherLoadPenalty(id))
                        .ThenBy(id => id)
                        .ToList();
                    if (altTids.Count == 0) continue;

                    var altRooms = CandidateRoomsFor(altModuleId);

                    foreach (var tid in altTids)
                    {
                        if (!TeacherFitsWorkingHours(tid, date, start, end))
                            continue;

                        bool peopleBusy = busy.Any(x => x.Date == date
                                                        && (x.GroupId == grp.Id || x.TeacherId == tid)
                                                        && x.StartTime < end && start < x.EndTime);
                        if (peopleBusy) continue;

                        ModuleTopic? altTopic = null;
                        int altLtId;
                        if (altSelfStudy)
                        {
                            altTopic = NextSelfStudyTopic(grp.Id, altModuleId);
                            altLtId = altTopic?.LessonTypeId ?? PickLessonType(grp.Id, grp.CourseId, altModuleId, date).LessonTypeId;
                        }
                        else
                        {
                            var pick = PickLessonType(grp.Id, grp.CourseId, altModuleId, date);
                            altLtId = pick.LessonTypeId;
                            altTopic = pick.Topic;
                        }

                        if (!TypeAllowed(altLtId)) continue;

                        if (groupLunch is not null && IsLectureType(altLtId) && end > groupLunch.Start)
                        {
                            continue;
                        }

                        bool isPreLunchSlot = groupLunch is not null && end <= groupLunch.Start;
                        if (isPreLunchSlot && !IsLectureType(altLtId))
                        {
                            var hasLectureBefore = busy.Any(b =>
                                b.GroupId == grp.Id
                                && b.Date == date
                                && IsLectureType(b.LessonTypeId)
                                && b.EndTime <= groupLunch!.Start
                                && b.StartTime < start);

                            var lecturesPending = HasPendingLectureBeforeLunch(grp.Id);
                            if (hasLectureBefore || lecturesPending)
                            {
                                continue;
                            }
                        }

                        var requiresRoom = (typeById.TryGetValue(altLtId, out var ltMetaAlt) ? ltMetaAlt.RequiresRoom : (bool?)null) ?? true;
                        if (requiresRoom)
                        {
                            if (altRooms.Count == 0) continue;

                            foreach (var rm in altRooms)
                            {
                                bool roomBusy = busy.Any(x => x.Date == date
                                                              && x.RoomId == rm.Id
                                                              && x.StartTime < end && start < x.EndTime);
                                if (roomBusy) continue;
                                return true;
                            }
                        }
                        else
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            IEnumerable<int> PreferNotUsedLastWeek(IEnumerable<int> modules)
            {
                var list = modules.ToList();
                var preferred = list.Where(mid => !UsedLastWeek(grp.Id, mid) && RemainingFor(grp.Id, mid) > 0).ToList();
                var rest = list.Where(mid => !preferred.Contains(mid)).ToList();
                return preferred.Concat(rest);
            }

            List<Room> CandidateRoomsFor(int mid)
            {
                allowedRoomsByModule.TryGetValue(mid, out var allowedRooms);
                allowedBuildingsByModule.TryGetValue(mid, out var allowedBuildings);
                return roomsAll
                    .Where(rm => (allowedBuildings == null || allowedBuildings.Count == 0 || allowedBuildings.Contains(rm.BuildingId))
                                 && (allowedRooms == null || allowedRooms.Count == 0 || allowedRooms.Contains(rm.Id))
                                 && rm.Capacity >= grp.StudentsCount)
                    .OrderBy(rm => groupRoomUsage.TryGetValue(rm.Id, out var used) ? used : 0)
                    .ThenBy(rm => rm.Capacity)
                    .ThenBy(rm => rm.Id)
                    .ToList();
            }

            int? LastGroupBuilding(DateOnly date, TimeOnly start)
            {
                return busy
                    .Where(b => b.GroupId == grp.Id && b.Date == date && b.EndTime <= start && b.RoomId != null)
                    .OrderBy(b => b.EndTime)
                    .LastOrDefault()?.BuildingId;
            }

            int? LastTeacherBuilding(int teacherId, DateOnly date, TimeOnly start)
            {
                return busy
                    .Where(b => b.TeacherId == teacherId && b.Date == date && b.EndTime <= start && b.RoomId != null)
                    .OrderBy(b => b.EndTime)
                    .LastOrDefault()?.BuildingId;
            }

            int? ResolvePrimaryModule()
            {
                if (mainModulesOrdered.Count == 0) return null;

                var preferredPrimary = mainModulesOrdered
                    .FirstOrDefault(mid => RemainingFor(grp.Id, mid) > 0 && !UsedLastWeek(grp.Id, mid));
                if (preferredPrimary != 0)
                {
                    return preferredPrimary;
                }

                for (int offset = 0; offset < mainModulesOrdered.Count; offset++)
                {
                    var idx = (nextModuleIndex + offset) % mainModulesOrdered.Count;
                    var moduleId = mainModulesOrdered[idx];
                    if (RemainingFor(grp.Id, moduleId) <= 0)
                    {
                        continue;
                    }

                    return moduleId;
                }

                return null;
            }

            async Task<bool> TryPlaceModuleAsync(int moduleId, DateOnly date, bool isPrimary, bool allowRepeatPreviousDay = false, bool allowExtraSameDay = false, bool relaxed = false)
            {
                if (CountFor(grp.Id, date) >= slots.Count)
                    return false;

                var remainingKey = (grp.Id, moduleId);
                bool isFiller = fillerLookup.Contains(moduleId);
                string? moduleTitle = null;

                async Task<bool> EnsureModuleTitleAsync()
                {
                    if (moduleTitle is not null)
                    {
                        return true;
                    }

                    moduleTitle = await _db.Modules
                        .Where(m => m.Id == moduleId)
                        .Select(m => m.Title)
                        .FirstOrDefaultAsync();

                    if (moduleTitle is null)
                    {
                        if (missingModulesNotified.Add(moduleId))
                        {
                            warnings.Add($"В базі даних відсутній модуль із ідентифікатором {moduleId}. Автогенерацію для нього пропущено.");
                        }

                        skipped++;
                        return false;
                    }

                    return true;
                }

                string ModuleLabel() => string.IsNullOrWhiteSpace(moduleTitle) ? $"#{moduleId}" : moduleTitle!;

                if (!isFiller && RemainingFor(grp.Id, moduleId) <= 0)
                {
                    return false;
                }

                if (!await EnsureModuleTitleAsync())
                {
                    return false;
                }

                if (TopicsDepleted(grp.Id, moduleId))
                {
                    if (remainingByGroupModule.ContainsKey(remainingKey))
                    {
                        remainingByGroupModule[remainingKey] = 0;
                    }

                    if (topicsExhaustedNotified.Add(remainingKey))
                    {
                        warnings.Add($"Для модуля <{ModuleLabel()}> у групи {grp.Name} вичерпано теми. Пропустили розкладення.");
                    }

                    var topicReason = $"Для модуля <{ModuleLabel()}> у групи {grp.Name} вичерпано теми для цього тижня.";
                    RecordSlotFailureReasonForAllSlots(date, topicReason);
                    return false;
                }

                bool placeSelfStudy = SelfStudyRemaining(grp.Id, moduleId) > 0;
                var tids = (placeSelfStudy
                        ? supervisorsForModule.Where(x => x.ModuleId == moduleId).Select(x => x.TeacherId)
                        : teachersForModule.Where(x => x.ModuleId == moduleId).Select(x => x.TeacherId))
                    .Distinct()
                    .OrderBy(id => TeacherLoadPenalty(id))
                    .ThenBy(id => id)
                    .ToList();
                if (tids.Count == 0)
                {
                    var teacherReason = placeSelfStudy
                        ? $"Не знайдено керівників для модуля <{ModuleLabel()}> (група {grp.Name}). Самостійну годину пропущено."
                        : $"Не знайдено викладачів для модуля <{ModuleLabel()}> (група {grp.Name}).";
                    RecordSlotFailureReasonForAllSlots(date, teacherReason);
                    warnings.Add(teacherReason);
                    if (placeSelfStudy)
                    {
                        var key = (grp.Id, moduleId);
                        if (selfStudyRemainingByGroupModule.ContainsKey(key))
                            selfStudyRemainingByGroupModule[key] = 0;
                    }
                    skipped++;
                    return false;
                }

                var candidateRooms = CandidateRoomsFor(moduleId);

                PlacementCandidate? best = null;

                foreach (var sl in slots)
                {
                    if (CountFor(grp.Id, date) >= slots.Count) break;

                    var s = sl.Start;
                    var e = sl.End;
                    var slotLabel = $"{s:HH\\:mm}-{e:HH\\:mm}";
                    var slotIssues = new HashSet<string>();
                    var slotCandidates = new List<PlacementCandidate>();

                    bool slotBreak = busy.Any(b => b.GroupId == grp.Id && b.Date == date
                                                   && b.LessonTypeId == typeBreakId && b.StartTime == s && b.EndTime == e);
                    if (slotBreak)
                    {
                        RecordSlotFailureReason(date, sl, $"Слот {slotLabel} зайнятий перервою/обідом.");
                        continue;
                    }

                    bool hasRecent = HasRecentModule(grp.Id, moduleId, date, windowDays: 2);
                    if (!allowRepeatPreviousDay && hasRecent && HasAvailableAlternativeForSlot(moduleId, date, s, e))
                    {
                        RecordSlotFailureReason(date, sl, $"Модуль <{ModuleLabel()}> не ставимо у вікні ±2 дні, поки є інші модулі з годинами.");
                        continue;
                    }

                    foreach (var tidCandidate in tids)
                    {
                        if (!TeacherFitsWorkingHours(tidCandidate, date, s, e))
                        {
                            slotIssues.Add($"Викладач {TeacherLabel(tidCandidate)} не працює у слоті {slotLabel}.");
                            continue;
                        }

                        bool peopleBusy = busy.Any(x => x.Date == date
                                                        && (x.GroupId == grp.Id || x.TeacherId == tidCandidate)
                                                        && x.StartTime < e && s < x.EndTime);
                        if (peopleBusy)
                        {
                            slotIssues.Add($"Група {grp.Name} або викладач {TeacherLabel(tidCandidate)} зайняті у слоті {slotLabel}.");
                            continue;
                        }

                        var isSelfStudyPlacement = placeSelfStudy && SelfStudyRemaining(grp.Id, moduleId) > 0;
                        ModuleTopic? topicSelection = null;
                        int ltypeId;
                        if (isSelfStudyPlacement)
                        {
                            topicSelection = NextSelfStudyTopic(grp.Id, moduleId);
                            if (topicSelection is null)
                            {
                                selfStudyRemainingByGroupModule[remainingKey] = 0;
                                continue;
                            }
                            ltypeId = topicSelection?.LessonTypeId ?? PickLessonType(grp.Id, grp.CourseId, moduleId, date).LessonTypeId;
                        }
                        else
                        {
                            var pickResult = PickLessonType(grp.Id, grp.CourseId, moduleId, date);
                            ltypeId = pickResult.LessonTypeId;
                            topicSelection = pickResult.Topic;
                        }
                        if (!TypeAllowed(ltypeId))
                        {
                            if (!typeById.TryGetValue(ltypeId, out var ltInfo))
                            {
                                slotIssues.Add($"Тип заняття #{ltypeId} не знайдено, тому слот {slotLabel} пропущено.");
                            }
                            else if (!ltInfo.IsActive)
                            {
                                slotIssues.Add($"Тип заняття \"{ltInfo.Name}\" неактивний, тому слот {slotLabel} пропущено.");
                            }
                            else if (!ltInfo.CountInPlan)
                            {
                                slotIssues.Add($"Тип заняття \"{ltInfo.Name}\" не враховується у плані (CountInPlan=false), тому слот {slotLabel} пропущено.");
                            }
                            else if (excludedTypeIds.Contains(ltypeId))
                            {
                                slotIssues.Add($"Тип заняття \"{ltInfo.Name}\" виключено з автогенерації, тому слот {slotLabel} пропущено.");
                            }
                            continue;
                        }

                        var penalties = new List<string>();
                        double penaltyScore = 0;

                        var sameDayCount = CountModuleForDay(grp.Id, date, moduleId);
                        if (sameDayCount >= 2)
                        {
                            var multiplier = allowExtraSameDay ? 0.5 : 1.0;
                            penaltyScore += penaltyExtraSameDay * (sameDayCount - 1) * multiplier;
                            penalties.Add("Дозволено більше двох пар модуля в один день");
                        }

                        if (HadSameModulePreviousDay(grp.Id, moduleId, date))
                        {
                            if (allowRepeatPreviousDay)
                            {
                                penaltyScore += penaltySameModulePrevDay * 0.5;
                                penalties.Add("Повтор модуля у сусідні дні дозволено для балансу");
                            }
                            else
                            {
                                penaltyScore += penaltySameModulePrevDay;
                                penalties.Add("Дозволено повторення модуля у сусідні дні");
                            }
                        }

                        var requiresRoom = (typeById.TryGetValue(ltypeId, out var ltMeta) ? ltMeta.RequiresRoom : (bool?)null) ?? true;

                        if (groupLunch is not null && IsLectureType(ltypeId) && sl.End > groupLunch.Start)
                        {
                            if (!relaxed)
                            {
                                var reason = $"Лекції слід ставити до обідньої перерви ({groupLunch.Start:HH\\:mm}), слот {slotLabel} пропущено.";
                                slotIssues.Add(reason);
                                RecordSlotFailureReason(date, sl, reason);
                                continue;
                            }
                            penalties.Add("Лекцію поставлено після обіду (relaxed)");
                        }

                        bool isPreLunchSlot = groupLunch is not null && sl.End <= groupLunch.Start;
                        if (isPreLunchSlot && !IsLectureType(ltypeId))
                        {
                            var hasLectureBefore = busy.Any(b =>
                                b.GroupId == grp.Id
                                && b.Date == date
                                && IsLectureType(b.LessonTypeId)
                                && b.EndTime <= groupLunch!.Start
                                && b.StartTime < sl.Start);

                            var lecturesPending = HasPendingLectureBeforeLunch(grp.Id);

                            if (hasLectureBefore || lecturesPending)
                            {
                                penaltyScore += penaltyNonLecturePreLunch;
                                penalties.Add("Слот до обіду зарезервовано під лекцію");
                            }
                        }

                        penaltyScore += TeacherLoadPenalty(tidCandidate);

                        bool sameSlotPattern = busy.Any(b =>
                            b.GroupId == grp.Id
                            && b.ModuleId == moduleId
                            && b.Date != date
                            && b.StartTime == s
                            && !excludedTypeIds.Contains(b.LessonTypeId));
                        if (sameSlotPattern)
                        {
                            penaltyScore += penaltySameSlotPattern;
                            penalties.Add("Повтор того ж часу в інші дні");
                        }

                        if (requiresRoom)
                        {
                            if (candidateRooms.Count == 0)
                            {
                                var roomReason = $"Не знайдено аудиторій для модуля <{ModuleLabel()}> (група {grp.Name}) у слоті {slotLabel}.";
                                RecordSlotFailureReason(date, sl, roomReason);
                                warnings.Add(roomReason);
                                continue;
                            }

                            foreach (var rm in candidateRooms)
                            {
                                bool roomBusy = busy.Any(x => x.Date == date
                                                              && x.RoomId == rm.Id
                                                              && x.StartTime < e && s < x.EndTime);
                                if (roomBusy)
                                {
                                    slotIssues.Add($"Усі аудиторії для модуля <{ModuleLabel()}> зайняті у слоті {slotLabel}.");
                                    continue;
                                }

                                var totalPenalty = penaltyScore + BuildingDistancePenalty(tidCandidate, rm, date, s);
                                var notes = new List<string>(penalties);
                                var candidate = new PlacementCandidate(sl, tidCandidate, rm, ltypeId, topicSelection, isSelfStudyPlacement, totalPenalty, notes);
                                slotCandidates.Add(candidate);
                            }
                        }
                        else
                        {
                            var notes = new List<string>(penalties);
                            var candidate = new PlacementCandidate(sl, tidCandidate, null, ltypeId, topicSelection, isSelfStudyPlacement, penaltyScore, notes);
                            slotCandidates.Add(candidate);
                        }
                    }

                    if (slotCandidates.Count > 0)
                    {
                        var localBest = slotCandidates
                            .OrderBy(c => c.Penalty)
                            .ThenBy(c => groupRandom.Next())
                            .First();
                        if (best is null || localBest.Penalty < best.Penalty)
                        {
                            best = localBest;
                        }
                    }
                    else if (slotIssues.Count > 0)
                    {
                        foreach (var reason in slotIssues)
                        {
                            RecordSlotFailureReason(date, sl, reason);
                        }
                    }
                    else
                    {
                        var key = (grp.Id, date, sl.Start, sl.End);
                        if (!slotFailureReasons.ContainsKey(key))
                        {
                            RecordSlotFailureReason(date, sl, $"Не знайдено вільної комбінації викладачів/аудиторій для модуля <{ModuleLabel()}> у слоті {slotLabel} (м'які правила повторів/тем/робочих годин).");
                        }
                    }
                }

                if (best is null)
                {
                    return false;
                }

                var selectedSlot = best.Slot;
                var selectedRoom = best.Room;
                var selectedTeacher = best.TeacherId;
                var selectedTopic = best.Topic;
                var selectedLessonTypeId = best.LessonTypeId;
                var startTime = selectedSlot.Start;
                var endTime = selectedSlot.End;

                var item = new TeacherDraftItem
                {
                    Date = date,
                    DayOfWeek = date.ToDateTime(TimeOnly.MinValue).DayOfWeek,
                    StartTime = startTime,
                    EndTime = endTime,
                    GroupId = grp.Id,
                    ModuleId = moduleId,
                    RoomId = selectedRoom?.Id,
                    TeacherId = selectedTeacher,
                    ModuleTopicId = selectedTopic?.Id,
                    LessonTypeId = selectedLessonTypeId,
                    Status = DraftStatus.Draft,
                    IsLocked = false,
                    IsSelfStudy = best.IsSelfStudy
                };
                _db.TeacherDraftItems.Add(item);
                createdDrafts.Add(item);

                if (selectedTopic is not null && !best.IsSelfStudy)
                {
                    MarkTopicUsed(grp.Id, moduleId, selectedTopic);
                }

                busy.Add(new BusySlot(
                    grp.Id,
                    selectedTeacher,
                    selectedRoom?.Id,
                    date,
                    startTime,
                    endTime,
                    selectedRoom?.BuildingId,
                    moduleId,
                    selectedLessonTypeId));

                if (selectedRoom?.Id is int ridSelected)
                {
                    groupRoomUsage[ridSelected] = groupRoomUsage.TryGetValue(ridSelected, out var usedRoom)
                        ? usedRoom + 1
                        : 1;
                }

                created++;
                Inc(grp.Id, date);
                hasPreferred.Add((grp.Id, moduleId));

                if (best.IsSelfStudy && selfStudyRemainingByGroupModule.TryGetValue(remainingKey, out var ssLeft) && ssLeft > 0)
                {
                    selfStudyRemainingByGroupModule[remainingKey] = Math.Max(0, ssLeft - 1);
                }
                if (remainingByGroupModule.TryGetValue(remainingKey, out var leftRemaining) && leftRemaining > 0)
                {
                    leftRemaining--;
                    remainingByGroupModule[remainingKey] = Math.Max(0, leftRemaining);
                }

                if (isPrimary && mainModulesOrdered.Count > 0)
                {
                    var currentIdx = mainModulesOrdered.FindIndex(mid => mid == moduleId);
                    if (currentIdx >= 0)
                    {
                        nextModuleIndex = (currentIdx + 1) % mainModulesOrdered.Count;
                    }
                }

                if (best.Notes.Count > 0)
                {
                    var noteText = string.Join("; ", best.Notes);
                    warnings.Add($"[{date:yyyy-MM-dd} {startTime:HH\\:mm}-{endTime:HH\\:mm}] {grp.Name}: {noteText}");
                }

                return true;
            }

            for (int d = 0; d < 7; d++)
            {
                var date = weekStart.AddDays(d);
                if (!IsWorking(date, grp)) continue;

                int maxPerDay = slots.Count;
                if (maxPerDay == 0) continue;

                var modulesAttemptedToday = new HashSet<int>();

                async Task FillWithRemainingModulesAsync(bool allowRepeatPreviousDay = false, bool allowExtraSameDay = false, bool relaxed = false)
                {
                    foreach (var moduleId in PreferNotUsedLastWeek(orderedModules))
                    {
                        if (CountFor(grp.Id, date) >= maxPerDay)
                        {
                            break;
                        }

                        if (!allowExtraSameDay && modulesAttemptedToday.Contains(moduleId))
                        {
                            continue;
                        }

                        if (RemainingFor(grp.Id, moduleId) <= 0)
                        {
                            continue;
                        }

                        modulesAttemptedToday.Add(moduleId);
                        await TryPlaceModuleAsync(moduleId, date, isPrimary: false, allowRepeatPreviousDay: allowRepeatPreviousDay, allowExtraSameDay: allowExtraSameDay, relaxed: relaxed);
                    }
                }

                var primaryModuleId = ResolvePrimaryModule();
                bool placedPrimary = false;
                if (primaryModuleId.HasValue)
                {
                    modulesAttemptedToday.Add(primaryModuleId.Value);
                    placedPrimary = await TryPlaceModuleAsync(primaryModuleId.Value, date, isPrimary: true);
                    if (placedPrimary
                        && RemainingFor(grp.Id, primaryModuleId.Value) > 0
                        && CountModuleForDay(grp.Id, date, primaryModuleId.Value) < 2
                        && CountFor(grp.Id, date) < maxPerDay)
                    {
                        await TryPlaceModuleAsync(primaryModuleId.Value, date, isPrimary: true);
                    }
                }

                Queue<int> BuildFillerQueueForDay()
                {
                    if (fillerModulesOrdered.Count == 0)
                        return new Queue<int>();

                    var ordered = fillerModulesOrdered
                        .OrderByDescending(mid => RemainingFor(grp.Id, mid))
                        .ThenBy(mid => mid)
                        .ToList();
                    return new Queue<int>(ordered);
                }

                if (fillerModulesOrdered.Count > 0)
                {
                    var fillerQueue = BuildFillerQueueForDay();
                    int fillerAttempts = 0;

                    while (CountFor(grp.Id, date) < maxPerDay)
                    {
                        if (fillerQueue.Count == 0) break;

                        var fillerModuleId = fillerQueue.Dequeue();
                        if (RemainingFor(grp.Id, fillerModuleId) <= 0)
                        {
                            fillerAttempts++;
                            if (fillerAttempts >= fillerModulesOrdered.Count)
                            {
                                break;
                            }

                            if (fillerQueue.Count == 0)
                            {
                                fillerQueue = BuildFillerQueueForDay();
                            }
                            continue;
                        }

                        modulesAttemptedToday.Add(fillerModuleId);
                        var placedFiller = await TryPlaceModuleAsync(fillerModuleId, date, isPrimary: false);
                        if (!placedFiller)
                        {
                            fillerAttempts++;
                            if (fillerAttempts >= fillerModulesOrdered.Count)
                            {
                                break;
                            }

                            if (fillerQueue.Count == 0)
                            {
                                fillerQueue = BuildFillerQueueForDay();
                            }
                            continue;
                        }

                        fillerAttempts = 0;

                        if (fillerQueue.Count == 0 && CountFor(grp.Id, date) < maxPerDay)
                        {
                            fillerQueue = BuildFillerQueueForDay();
                        }
                    }
                }

                if (CountFor(grp.Id, date) < maxPerDay)
                {
                    await FillWithRemainingModulesAsync();
                }

                if (CountFor(grp.Id, date) < maxPerDay)
                {
                    await FillWithRemainingModulesAsync(allowRepeatPreviousDay: false, allowExtraSameDay: true);
                }

                if (CountFor(grp.Id, date) < maxPerDay)
                {
                    await FillWithRemainingModulesAsync(allowRepeatPreviousDay: true, allowExtraSameDay: true, relaxed: true);
                }

                TryShiftGaps(date);

                if (DayHasGaps(date, out var remainingGap) && remainingGap is not null)
                {
                    WarnGap(date, remainingGap);
                }
            }
        }

        await _db.SaveChangesAsync();
        return Ok(new AutoGenResult(created, skipped, warnings, gapDetails));
    }


    [HttpPost("approve-week")]
    /// <summary>
    /// Позначає чернетки викладача за тиждень як затверджені.
    /// </summary>
    public async Task<IActionResult> ApproveWeek([FromBody] ApproveWeekRequest r)
    {
        var start = r.WeekStart;
        var end = start.AddDays(7);

        var rows = await _db.TeacherDraftItems
            .Where(x => x.TeacherId == r.TeacherId && x.Date >= start && x.Date < end)
            .ToListAsync();

        foreach (var x in rows) x.Status = DraftStatus.Published;

        await _db.SaveChangesAsync();
        return Ok();
    }

    [HttpPost("publish-week")]
    /// <summary>
    /// Публікує затверджені чернетки у розкладі та повертає статистику операції.
    /// </summary>
    public async Task<ActionResult<PublishWeekResults>> PublishWeek([FromBody] PublishWeekRequest r)
    {
        var start = r.WeekStart;
        var end = start.AddDays(7);

        var q = _db.TeacherDraftItems.Where(x => x.Date >= start && x.Date < end);
        if (r.TeacherId is int tid) q = q.Where(x => x.TeacherId == tid);

        var drafts = await q
            .Include(x => x.Group)
            .ToListAsync();

        var lessonTypeIds = drafts.Select(d => d.LessonTypeId).Distinct().ToList();
        var lessonTypeRoomMap = await _db.LessonTypes.AsNoTracking()
            .Where(lt => lessonTypeIds.Contains(lt.Id))
            .ToDictionaryAsync(lt => lt.Id, lt => lt.RequiresRoom);

        var calendar = await _db.CalendarExceptions.AsNoTracking()
            .Where(x => x.Date >= start && x.Date < end)
            .ToListAsync();

        int created = 0, skipped = 0;
        var warnings = new List<string>();
        var publishedIds = new List<int>();

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        foreach (var d in drafts)
        {
            var isWeekend = d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var courseId = d.Group?.CourseId;
            var scoped = ResolveCalendarOverride(calendar, d.Date, courseId, d.GroupId);
            var isWorking = scoped ?? !isWeekend;
            var overrideNonWorking = !isWorking;

            var requiresRoom = lessonTypeRoomMap.TryGetValue(d.LessonTypeId, out var reqRoom) ? reqRoom : true;
            var normalizedRoomId = requiresRoom ? d.RoomId : null;

            var req = new UpsertScheduleItemRequest(
                Id: null,
                Date: d.Date,
                TimeStart: d.StartTime.ToString("HH:mm"),
                TimeEnd: d.EndTime.ToString("HH:mm"),
                GroupId: d.GroupId,
                ModuleId: d.ModuleId,
                TeacherId: d.TeacherId,
                RoomId: normalizedRoomId,
                LessonTypeId: d.LessonTypeId,
                IsLocked: false,
                OverrideNonWorkingDay: overrideNonWorking
            );

            var (errors, warn) = await _rules.ValidateUpsertAsync(req);
            if (errors.Count > 0)
            {
                skipped++;
                warnings.Add($"[{d.Date:yyyy-MM-dd} {d.StartTime:HH\\:mm}-{d.EndTime:HH\\:mm}] {string.Join("; ", errors)}");
                continue;
            }

            var item = new ScheduleItem
            {
                Date = d.Date,
                DayOfWeek = d.DayOfWeek,
                StartTime = d.StartTime,
                EndTime = d.EndTime,
                GroupId = d.GroupId,
                ModuleId = d.ModuleId,
                RoomId = normalizedRoomId,
                TeacherId = d.TeacherId,
                ModuleTopicId = d.ModuleTopicId,
                LessonTypeId = d.LessonTypeId,
                IsLocked = false,
                IsSelfStudy = d.IsSelfStudy
            };
            _db.ScheduleItems.Add(item);
            created++;
            publishedIds.Add(d.Id);
        }

        await _db.SaveChangesAsync();

        if (publishedIds.Count > 0)
            await _db.TeacherDraftItems
                .Where(x => publishedIds.Contains(x.Id))
                .ExecuteDeleteAsync();

        var publishedDrafts = drafts.Where(d => publishedIds.Contains(d.Id)).ToList();
        var affectedPlans = publishedDrafts
            .Select(x => new { x.ModuleId, CourseId = x.Group.CourseId })
            .Distinct()
            .Select(x => (x.CourseId, x.ModuleId));
        var affectedLoads = publishedDrafts
            .Where(x => x.TeacherId != null)
            .Select(x => new { TeacherId = x.TeacherId!.Value, CourseId = x.Group.CourseId })
            .Distinct()
            .Select(x => (x.TeacherId, x.CourseId));

        await RecalcAggregatesAsync(affectedPlans, affectedLoads);

        await tx.CommitAsync();
        return Ok(new PublishWeekResults(created, skipped, warnings));
    }
    /// <summary>
    /// Перераховує агреговані плани та навантаження після змін у чернетках.
    /// </summary>


    private async Task RecalcAggregatesAsync(
        IEnumerable<(int CourseId, int ModuleId)> plans,
        IEnumerable<(int TeacherId, int CourseId)> loads)
    {
        var lessonTypes = await _db.LessonTypes
            .Select(lt => new { lt.Id, lt.Code, lt.CountInPlan, lt.CountInLoad })
            .ToListAsync();

        var excludePlanIds = lessonTypes
            .Where(lt =>
                !lt.CountInPlan
                || string.Equals(lt.Code, "CANCELED", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(lt.Code, "RESCHEDULED", System.StringComparison.OrdinalIgnoreCase))
            .Select(lt => lt.Id)
            .ToHashSet();

        var excludeLoadIds = lessonTypes
            .Where(lt =>
                !lt.CountInLoad
                || string.Equals(lt.Code, "CANCELED", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(lt.Code, "RESCHEDULED", System.StringComparison.OrdinalIgnoreCase))
            .Select(lt => lt.Id)
            .ToHashSet();

        var planKeys = plans.Distinct().ToList();
        if (planKeys.Count > 0)
        {
            var cIds = planKeys.Select(k => k.CourseId).Distinct().ToList();
            var mIds = planKeys.Select(k => k.ModuleId).Distinct().ToList();

            var counts = await _db.ScheduleItems
                .Include(si => si.Group)
                .Where(si => !excludePlanIds.Contains(si.LessonTypeId)
                             && cIds.Contains(si.Group.CourseId)
                             && mIds.Contains(si.ModuleId))
                .GroupBy(si => new { CourseId = si.Group.CourseId, si.ModuleId })
                .Select(g => new { g.Key.CourseId, g.Key.ModuleId, GCount = g.Count() })
                .ToListAsync();

            var plansToUpdate = await _db.ModulePlans
                .Where(mp => cIds.Contains(mp.CourseId) && mIds.Contains(mp.ModuleId))
                .ToListAsync();

            foreach (var p in plansToUpdate)
                p.ScheduledHours = counts.FirstOrDefault(c => c.CourseId == p.CourseId && c.ModuleId == p.ModuleId)?.GCount ?? 0;
        }

        var loadKeys = loads.Distinct().ToList();
        if (loadKeys.Count > 0)
        {
            var tIds = loadKeys.Select(k => k.TeacherId).Distinct().ToList();
            var cIds = loadKeys.Select(k => k.CourseId).Distinct().ToList();

            var counts = await _db.ScheduleItems
                .Include(si => si.Group)
                .Where(si => si.TeacherId != null
                             && !excludeLoadIds.Contains(si.LessonTypeId)
                             && tIds.Contains(si.TeacherId!.Value)
                             && cIds.Contains(si.Group.CourseId))
                .GroupBy(si => new { TeacherId = si.TeacherId!.Value, si.Group.CourseId })
                .Select(g => new { g.Key.TeacherId, g.Key.CourseId, GCount = g.Count() })
                .ToListAsync();

            var loadsToUpdate = await _db.TeacherCourseLoads
                .Where(l => l.IsActive && tIds.Contains(l.TeacherId) && cIds.Contains(l.CourseId))
                .ToListAsync();

            foreach (var l in loadsToUpdate)
                l.ScheduledHours = counts.FirstOrDefault(c => c.TeacherId == l.TeacherId && c.CourseId == l.CourseId)?.GCount ?? 0;
        }

        await _db.SaveChangesAsync();
    }

    private static bool? ResolveCalendarOverride(IEnumerable<CalendarException> items, DateOnly date, int? courseId, int? groupId)
    {
        int? normCourse = (courseId is int cid && cid > 0) ? cid : null;
        int? normGroup = (groupId is int gid && gid > 0) ? gid : null;

        var match = items
            .Where(x => x.Date == date)
            .Where(x => normGroup != null ? (x.GroupId == normGroup || x.GroupId == null) : x.GroupId == null)
            .Where(x => normCourse != null ? (x.CourseId == normCourse || x.CourseId == null) : x.CourseId == null)
            .OrderByDescending(x => x.GroupId != null)
            .ThenByDescending(x => x.CourseId != null)
            .FirstOrDefault();

        return match?.IsWorkingDay;
    }
}
