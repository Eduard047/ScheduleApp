using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers;

[ApiController]
[Route("api/teacher-drafts")]
// Контролер для керування чернетками викладачів
public sealed class TeacherDraftsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly RulesService _rules;
    private readonly TeacherDraftsQueryService _queryService;
    private readonly TeacherDraftsExportService _exportService;
    private readonly TeacherDraftsAutogenService _autogenService;
    private readonly TeacherDraftsAutogenJobService _autogenJobService;
    private readonly TeacherDraftsPublishService _publishService;
    private static readonly JsonSerializerOptions ValidationJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
    private static bool TryParseClock(string value, out TimeOnly time)
        => TimeOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    public TeacherDraftsController(
        AppDbContext db,
        RulesService rules,
        TeacherDraftsQueryService queryService,
        TeacherDraftsExportService exportService,
        TeacherDraftsAutogenService autogenService,
        TeacherDraftsAutogenJobService autogenJobService,
        TeacherDraftsPublishService publishService)
    {
        _db = db;
        _rules = rules;
        _queryService = queryService;
        _exportService = exportService;
        _autogenService = autogenService;
        _autogenJobService = autogenJobService;
        _publishService = publishService;
    }
    [HttpGet]
    // Повертає перелік чернеток викладачів за тиждень із додатковою інформацією.
    public Task<IReadOnlyList<TeacherDraftItemDto>> Get(
        [FromQuery] DateOnly weekStart,
        [FromQuery] int? teacherId,
        [FromQuery] int? groupId,
        [FromQuery] int? roomId)
        => _queryService.GetAsync(weekStart, teacherId, groupId, roomId);
    [HttpGet("export")]
    // Експортує чернетки в Excel за фільтрами.
    public async Task<IActionResult> Export(
        [FromQuery] DateOnly weekStart,
        [FromQuery] int? teacherId,
        [FromQuery] int? groupId,
        [FromQuery] int? roomId)
        => await _exportService.ExportAsync(weekStart, teacherId, groupId, roomId);
    [HttpGet("week")]
    // Додає коротку кінцеву точку, що делегує основному методу отримання даних.
    public Task<IReadOnlyList<TeacherDraftItemDto>> GetWeekAlias(
        [FromQuery] DateOnly weekStart,
        [FromQuery] int? teacherId,
        [FromQuery] int? groupId,
        [FromQuery] int? roomId)
        => _queryService.GetAsync(weekStart, teacherId, groupId, roomId);
    [HttpDelete("{id:int}")]
    // Видаляє чернетку, якщо запис існує та не заблокований.
    public async Task<IActionResult> Delete(int id, [FromQuery] bool confirm = false, [FromQuery] bool unrestricted = false)
    {
        var item = await _db.TeacherDraftItems.FirstOrDefaultAsync(x => x.Id == id);
        if (item is null) return NotFound(new { message = $"TeacherDraftItem {id} not found" });
        if (item.IsLocked) return Conflict(new { message = "Чернетка заблокована. Видалення заблокованих чернеток через API заборонено." });
        _db.TeacherDraftItems.Remove(item);
        await _db.SaveChangesAsync();
        return NoContent();
    }
    [HttpPost("upsert")]
    // Валідує й створює або оновлює чернетку викладача, повертає її ідентифікатор.
    public async Task<ActionResult<int>> Upsert([FromBody] DraftUpsertRequest r)
    {
        var request = r;
        if (!TryParseClock(request.TimeStart, out var start) || !TryParseClock(request.TimeEnd, out var end))
        {
            return BadRequest(new { message = "Некоректний формат часу. Використовуйте формат HH:mm." });
        }
        if (end <= start)
        {
            return BadRequest(new { message = "Час завершення має бути більшим за час початку." });
        }
        if (request.LessonTypeId <= 0)
        {
            var noneTypeId = await _db.LessonTypes
                .Where(x => x.Code == "NONE")
                .Select(x => (int?)x.Id)
                .FirstOrDefaultAsync();
            if (noneTypeId is null)
            {
                return Conflict(new { message = "Тип заняття \"Без типу\" не налаштований." });
            }
            request = request with { LessonTypeId = noneTypeId.Value };
        }
        var validation = await _rules.ValidateDraftAsync(request);
        if (validation.Errors.Count > 0)
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
            .Where(x => x.Id == request.LessonTypeId)
            .Select(x => (bool?)x.RequiresRoom)
            .FirstOrDefaultAsync();
        var normalizedRoomId = lessonTypeRequiresRoom is false ? null : request.RoomId;
        if (request.Id is int id && id > 0)
        {
            var item = await _db.TeacherDraftItems.FirstOrDefaultAsync(x => x.Id == id);
            if (item is null) return NotFound(new { message = $"TeacherDraftItem {id} not found" });
            if (item.IsLocked) return Conflict(new { message = "Чернетка заблокована. Зміна заблокованих чернеток через API заборонена." });
            ApplyDraftRequest(item, request, start, end, normalizedRoomId, reportJson);
            await _db.SaveChangesAsync();
            return Ok(item.Id);
        }
        var newItem = new TeacherDraftItem();
        ApplyDraftRequest(newItem, request, start, end, normalizedRoomId, reportJson);
        _db.TeacherDraftItems.Add(newItem);
        await _db.SaveChangesAsync();
        return Ok(newItem.Id);
    }
    // Переносить дані запиту у доменну модель чернетки.
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
    // Очищає незаблоковані чернетки за вказаний тиждень із можливими додатковими фільтрами.
    public async Task<ActionResult<ClearWeekResult>> ClearWeek([FromBody] ClearWeekRequest r)
    {
        if (r.CourseId is null && r.GroupId is null)
        {
            return BadRequest(new { message = "Для очищення тижня потрібно вказати курс або групу." });
        }
        var start = r.WeekStart;
        var end = start.AddDays(7);
        var q = _db.TeacherDraftItems.Where(x => x.Date >= start && x.Date < end && !x.IsLocked);
        if (r.CourseId is int cid) q = q.Where(x => x.Group.CourseId == cid);
        if (r.GroupId is int gid) q = q.Where(x => x.GroupId == gid);
        var deleted = await q.ExecuteDeleteAsync();
        return Ok(new ClearWeekResult(deleted));
    }
    [HttpPost("autogen/week")]
    // Викликає автогенерацію чернеток для одного тижня.
    public Task<ActionResult<AutoGenResult>> DraftAutoGenWeek([FromBody] DraftAutoGenRequest r)
        => _autogenService.DraftAutoGenWeek(r, HttpContext.RequestAborted);
    [HttpPost("autogen/month")]
    // Автоматично генерує чернетки для кожного тижня в межах місяця.
    public async Task<ActionResult<AutoGenResult>> AutogenMonth([FromBody] AutogenMonthRequest r)
        => await _autogenService.AutogenMonth(r, HttpContext.RequestAborted);
    [HttpPost("autogen/course")]
    // Генерує чернетки для курсу в заданому діапазоні тижнів.
    public async Task<ActionResult<AutoGenResult>> AutogenCourse([FromBody] AutogenCourseRequest r)
        => await _autogenService.AutogenCourse(r, HttpContext.RequestAborted);
    [HttpPost("autogen")]
    // Створює чернетки на основі правил і доступних даних для заданого тижня.
    public async Task<ActionResult<AutoGenResult>> DraftAutoGen([FromBody] DraftAutoGenRequest r)
        => await _autogenService.DraftAutoGen(r, HttpContext.RequestAborted);
    [HttpPost("autogen/jobs")]
    public ActionResult<AutoGenJobStartResult> StartAutoGenJob([FromBody] AutoGenJobRequest r)
        => Ok(_autogenJobService.Start(r));
    [HttpGet("autogen/jobs/{jobId}")]
    public ActionResult<AutoGenJobStatus> GetAutoGenJob(string jobId)
        => _autogenJobService.Get(jobId) is { } status
            ? Ok(status)
            : NotFound(new { message = "Задачу автогенерації не знайдено." });
    [HttpPost("autogen/jobs/{jobId}/cancel")]
    public ActionResult<AutoGenJobStatus> CancelAutoGenJob(string jobId)
        => _autogenJobService.Cancel(jobId) is { } status
            ? Ok(status)
            : NotFound(new { message = "Задачу автогенерації не знайдено." });
    [HttpPost("approve-week")]
    // Позначає чернетки викладача за тиждень як затверджені.
    public async Task<IActionResult> ApproveWeek([FromBody] ApproveWeekRequest r)
        => await _publishService.ApproveWeekAsync(r);
    [HttpPost("publish-week")]
    // Публікує затверджені чернетки у розкладі та повертає статистику операції.
    public async Task<ActionResult<PublishWeekResults>> PublishWeek([FromBody] PublishWeekRequest r)
        => await _publishService.PublishWeekAsync(r);
}
