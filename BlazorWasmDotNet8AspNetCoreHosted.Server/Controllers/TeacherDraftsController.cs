using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers;

[ApiController]
[Route("api/teacher-drafts")]
// Контролер для керування чернетками викладачів
public sealed class TeacherDraftsController : ControllerBase
{
    private const int MaxBatchMutationCount = 500;
    private readonly AppDbContext _db;
    private readonly RulesService _rules;
    private readonly TeacherDraftsQueryService _queryService;
    private readonly TeacherDraftsExportService _exportService;
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
        _autogenJobService = autogenJobService;
        _publishService = publishService;
    }
    [HttpGet]
    // Повертає перелік чернеток викладачів за тиждень із додатковою інформацією.
    public async Task<ActionResult<IReadOnlyList<TeacherDraftItemDto>>> Get(
        [FromQuery] DateOnly weekStart,
        [FromQuery] int? teacherId,
        [FromQuery] int? groupId,
        [FromQuery] int? roomId,
        CancellationToken cancellationToken = default)
    {
        if (!DateHelpers.IsSupportedScheduleDate(weekStart))
        {
            return BadRequest(new { message = DateHelpers.SupportedScheduleDateMessage });
        }
        var rows = await _queryService.GetAsync(
            weekStart,
            teacherId,
            groupId,
            roomId,
            cancellationToken,
            TeacherDraftsWeekValidationService.MaxWeekDraftRowCount + 1);
        if (rows.Count > TeacherDraftsWeekValidationService.MaxWeekDraftRowCount)
        {
            return UnprocessableEntity(new
            {
                message = $"За один тиждень можна завантажити не більше {TeacherDraftsWeekValidationService.MaxWeekDraftRowCount} чернеток."
            });
        }
        return Ok(rows);
    }
    [HttpGet("validate-week")]
    [EnableRateLimiting("week-validation")]
    // Повторно перевіряє всі чернетки тижня незалежно від активних фільтрів клієнта.
    public async Task<ActionResult<DraftValidationReportDto>> ValidateWeek(
        [FromQuery] DateOnly weekStart,
        [FromServices] TeacherDraftsWeekValidationService validationService,
        [FromServices] ExpensiveOperationGate operationGate,
        CancellationToken cancellationToken)
    {
        if (!DateHelpers.IsSupportedScheduleDate(weekStart))
        {
            return BadRequest(new { message = DateHelpers.SupportedScheduleDateMessage });
        }
        using var lease = await operationGate.TryEnterAsync(
            ExpensiveOperationKind.WeekValidation,
            cancellationToken);
        if (lease is null)
        {
            return Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Забагато одночасних перевірок тижня",
                detail: "Дочекайтеся завершення поточних перевірок і повторіть запит.");
        }

        try
        {
            return Ok(await validationService.ValidateAsync(weekStart, cancellationToken));
        }
        catch (DraftValidationCapacityException ex)
        {
            return Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Обсяг тижня перевищує безпечний ліміт",
                detail: ex.Message);
        }
        catch (DraftValidationTimeoutException ex)
        {
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Перевірка тижня не завершилася вчасно",
                detail: ex.Message);
        }
    }
    [HttpGet("export")]
    [EnableRateLimiting("xlsx-export")]
    // Експортує чернетки в Excel за фільтрами.
    public async Task<IActionResult> Export(
        [FromQuery] DateOnly weekStart,
        [FromQuery] int? teacherId,
        [FromQuery] int? groupId,
        [FromQuery] int? roomId,
        CancellationToken cancellationToken = default)
    {
        if (!DateHelpers.IsSupportedScheduleDate(weekStart))
        {
            return BadRequest(new { message = DateHelpers.SupportedScheduleDateMessage });
        }
        try
        {
            return await _exportService.ExportAsync(
                weekStart,
                teacherId,
                groupId,
                roomId,
                cancellationToken);
        }
        catch (TeacherDraftsExportLimitException ex)
        {
            return Problem(
                statusCode: ex.StatusCode,
                title: "Експорт перевищує безпечний ліміт",
                detail: ex.Message);
        }
    }
    [HttpGet("week")]
    // Додає коротку кінцеву точку, що делегує основному методу отримання даних.
    public Task<ActionResult<IReadOnlyList<TeacherDraftItemDto>>> GetWeekAlias(
        [FromQuery] DateOnly weekStart,
        [FromQuery] int? teacherId,
        [FromQuery] int? groupId,
        [FromQuery] int? roomId,
        CancellationToken cancellationToken = default)
        => Get(weekStart, teacherId, groupId, roomId, cancellationToken);
    [HttpDelete("{id:int}")]
    // Видаляє чернетку, якщо запис існує та не заблокований.
    public async Task<IActionResult> Delete(
        int id,
        [FromQuery] Guid? expectedRevision,
        [FromQuery] bool confirm = false,
        [FromQuery] bool unrestricted = false)
    {
        if (expectedRevision is not Guid revision || revision == Guid.Empty)
        {
            return StatusCode(428, new
            {
                message = "Для видалення чернетки потрібна її актуальна версія. Оновіть сторінку та повторіть дію."
            });
        }
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            var action = await DeleteCoreAsync(
                id,
                revision,
                confirm,
                unrestricted,
                allowLogicalEventBatchMutation: false);
            if (action is NoContentResult)
            {
                await transaction.CommitAsync();
            }
            else
            {
                await transaction.RollbackAsync();
            }
            return action;
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            return ConcurrencyConflict("видалення");
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<IActionResult> DeleteCoreAsync(
        int id,
        Guid expectedRevision,
        bool confirm,
        bool unrestricted,
        bool allowLogicalEventBatchMutation)
    {
        if (!allowLogicalEventBatchMutation
            && await FindIncompleteLogicalEventMutationAsync(new[] { id }) is { } logicalEventConflict)
        {
            return logicalEventConflict;
        }
        var item = await _db.TeacherDraftItems.FirstOrDefaultAsync(x => x.Id == id);
        if (item is null) return NotFound(new { message = $"Чернетку #{id} не знайдено." });
        if (item.Revision != expectedRevision)
        {
            return ConcurrencyConflict("видалення");
        }
        if (item.Status != DraftStatus.Draft && !(confirm && unrestricted))
        {
            return Conflict(new
            {
                message = "Схвалену чернетку можна видалити лише після явного підтвердження в режимі без обмежень."
            });
        }
        if (item.IsLocked && !(confirm && unrestricted)) return Conflict(new { message = "Чернетка заблокована. Для видалення потрібні підтвердження та режим без обмежень." });
        _db.TeacherDraftItems.Remove(item);
        await _db.SaveChangesAsync();
        return NoContent();
    }
    [HttpPost("delete-batch")]
    // Видаляє пакет чернеток в одній транзакції або повністю відкочує його за першої помилки.
    public async Task<ActionResult<TeacherDraftBatchDeleteResult>> DeleteBatch(
        [FromBody] TeacherDraftBatchDeleteRequest request,
        [FromQuery] bool confirm = false)
    {
        if (request?.Ids is not { Count: > 0 })
        {
            return BadRequest(new { message = "Пакет видалення має містити щонайменше одну чернетку." });
        }
        if (request.Ids.Count > MaxBatchMutationCount)
        {
            return BadRequest(new { message = $"За один запит можна видалити не більше {MaxBatchMutationCount} чернеток." });
        }
        if (request.Ids.Any(id => id <= 0))
        {
            return BadRequest(new { message = "Ідентифікатори чернеток для видалення мають бути додатними числами." });
        }
        if (request.Ids.Distinct().Count() != request.Ids.Count)
        {
            return BadRequest(new { message = "Пакет видалення містить повторювані ідентифікатори чернеток." });
        }
        if (request.ExpectedRevisions is null
            || request.Ids.Any(id => !request.ExpectedRevisions.TryGetValue(id, out var revision) || revision == Guid.Empty))
        {
            return StatusCode(428, new
            {
                message = "Для пакетного видалення потрібні актуальні версії всіх чернеток. Оновіть сторінку та повторіть дію."
            });
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            var requestedIdSet = request.Ids.ToHashSet();
            var targetRows = await _db.TeacherDraftItems
                .Where(item => requestedIdSet.Contains(item.Id))
                .ToListAsync();
            if (await FindIncompleteLogicalEventMutationAsync(request.Ids, targetRows) is { } logicalEventConflict)
            {
                await transaction.RollbackAsync();
                _db.ChangeTracker.Clear();
                return logicalEventConflict;
            }

            var targetRowsById = targetRows.ToDictionary(item => item.Id);
            for (var index = 0; index < request.Ids.Count; index++)
            {
                var id = request.Ids[index];
                IActionResult? action = null;
                if (!targetRowsById.TryGetValue(id, out var item))
                {
                    action = NotFound(new { message = $"Чернетку #{id} не знайдено." });
                }
                else if (item.Revision != request.ExpectedRevisions[id])
                {
                    action = ConcurrencyConflict("видалення");
                }
                else if (item.Status != DraftStatus.Draft && !(confirm && request.Unrestricted))
                {
                    action = Conflict(new
                    {
                        message = "Схвалену чернетку можна видалити лише після явного підтвердження в режимі без обмежень."
                    });
                }
                else if (item.IsLocked && !(confirm && request.Unrestricted))
                {
                    action = Conflict(new
                    {
                        message = "Чернетка заблокована. Для видалення потрібні підтвердження та режим без обмежень."
                    });
                }

                if (action is null)
                {
                    continue;
                }
                await transaction.RollbackAsync();
                _db.ChangeTracker.Clear();
                return BuildBatchFailure(action, index, "видалення");
            }

            // Усі правила перевірено до першої зміни; EF зберігає маркери версій
            // для кожного DELETE та виконує пакет одним викликом SaveChanges.
            _db.TeacherDraftItems.RemoveRange(request.Ids.Select(id => targetRowsById[id]));
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
            return Ok(new TeacherDraftBatchDeleteResult(request.Ids.Count));
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            return ConcurrencyConflict("пакетного видалення");
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
    }
    [HttpPost("upsert")]
    // Валідує й створює або оновлює чернетку викладача, повертає її ідентифікатор.
    public async Task<ActionResult<int>> Upsert([FromBody] DraftUpsertRequest? r)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            var action = await UpsertCoreAsync(r, allowTransientBatchConflicts: false);
            if (TryReadUpsertId(action, out _))
            {
                await transaction.CommitAsync();
            }
            else
            {
                await transaction.RollbackAsync();
            }
            return action;
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            return ConcurrencyConflict("збереження");
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<ActionResult<int>> UpsertCoreAsync(
        DraftUpsertRequest? r,
        bool allowTransientBatchConflicts)
    {
        if (r is null)
        {
            return BadRequest(new { message = "Запит на збереження чернетки не може бути порожнім." });
        }
        if (!DateHelpers.IsSupportedScheduleDate(r.Date))
        {
            return BadRequest(new { message = DateHelpers.SupportedScheduleDateMessage });
        }
        if (r.Id is > 0 && (r.ExpectedRevision is not Guid revision || revision == Guid.Empty))
        {
            return StatusCode(428, new
            {
                message = "Для оновлення чернетки потрібна її актуальна версія. Оновіть сторінку та повторіть дію."
            });
        }
        var normalizedBatchKey = string.IsNullOrWhiteSpace(r.BatchKey) ? null : r.BatchKey.Trim();
        var request = r with { BatchKey = normalizedBatchKey };
        if (!TryParseClock(request.TimeStart, out var start) || !TryParseClock(request.TimeEnd, out var end))
        {
            return BadRequest(new { message = "Некоректний формат часу. Використовуйте формат HH:mm." });
        }
        if (end <= start)
        {
            return BadRequest(new { message = "Час завершення має бути більшим за час початку." });
        }
        if (request.BatchKey is { Length: > 64 })
        {
            return BadRequest(new { message = "Ключ пакета чернетки не може перевищувати 64 символи." });
        }
        if (request.BatchKey?.StartsWith("rescheduled:", StringComparison.OrdinalIgnoreCase) == true)
        {
            return BadRequest(new
            {
                message = "Префікс ключа пакета «rescheduled:» зарезервований для системного перенесення занять."
            });
        }
        if (!allowTransientBatchConflicts
            && request.Id is int existingId
            && existingId > 0
            && await FindIncompleteLogicalEventMutationAsync(new[] { existingId }) is { } logicalEventConflict)
        {
            return logicalEventConflict;
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
        if (HasBlockingValidationErrors(
                validation,
                request.IgnoreValidationErrors || allowTransientBatchConflicts,
                allowTransientBatchConflicts))
            return Conflict(new
            {
                message = "Чернетка не пройшла перевірку правил.",
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
            if (item is null) return NotFound(new { message = $"Чернетку #{id} не знайдено." });
            if (item.Revision != request.ExpectedRevision)
            {
                return ConcurrencyConflict("збереження");
            }
            if (item.IsLocked) return Conflict(new { message = "Чернетка заблокована. Зміна заблокованих чернеток через API заборонена." });
            if (item.Status != DraftStatus.Draft)
            {
                return Conflict(new
                {
                    message = "Схвалену чернетку не можна змінювати. Опублікуйте її без змін або видаліть після явного підтвердження в режимі без обмежень."
                });
            }
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
    [HttpPost("upsert-batch")]
    // Створює або оновлює пакет чернеток в одній транзакції без часткових змін.
    public async Task<ActionResult<TeacherDraftBatchUpsertResult>> UpsertBatch(
        [FromBody] TeacherDraftBatchUpsertRequest request)
    {
        if (request?.Items is not { Count: > 0 })
        {
            return BadRequest(new { message = "Пакет збереження має містити щонайменше одну чернетку." });
        }
        if (request.Items.Any(item => item is null))
        {
            return BadRequest(new { message = "Пакет збереження не може містити порожні елементи." });
        }
        if (request.Items.Count > MaxBatchMutationCount)
        {
            return BadRequest(new { message = $"За один запит можна зберегти не більше {MaxBatchMutationCount} чернеток." });
        }
        var duplicateExistingId = request.Items
            .Where(item => item.Id is > 0)
            .GroupBy(item => item.Id!.Value)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateExistingId is not null)
        {
            return BadRequest(new
            {
                message = $"Чернетка #{duplicateExistingId.Key} повторюється у пакеті збереження."
            });
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var ids = new List<int>(request.Items.Count);
        try
        {
            var existingIds = request.Items
                .Where(item => item.Id is > 0)
                .Select(item => item.Id!.Value)
                .ToList();
            if (await FindIncompleteLogicalEventMutationAsync(existingIds) is { } logicalEventConflict)
            {
                await transaction.RollbackAsync();
                return logicalEventConflict;
            }

            for (var index = 0; index < request.Items.Count; index++)
            {
                var action = await UpsertCoreAsync(
                    request.Items[index],
                    allowTransientBatchConflicts: true);
                if (TryReadUpsertId(action, out var id))
                {
                    ids.Add(id);
                    continue;
                }

                await transaction.RollbackAsync();
                _db.ChangeTracker.Clear();
                return BuildBatchFailure(action.Result, index, "збереження");
            }

            var finalValidation = await ValidateFinalBatchStateAsync(
                ids,
                request.Items,
                allowBypassableErrors: request.Unrestricted);
            if (finalValidation.Action is not null)
            {
                await transaction.RollbackAsync();
                _db.ChangeTracker.Clear();
                return BuildBatchFailure(finalValidation.Action, finalValidation.ItemIndex, "збереження");
            }

            await _db.SaveChangesAsync();

            await transaction.CommitAsync();
            return Ok(new TeacherDraftBatchUpsertResult(ids, ids.Count));
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            return ConcurrencyConflict("пакетного збереження");
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
    }
    [HttpPost("mutate-batch")]
    // Атомарно поєднує збереження й видалення чернеток в одній транзакції.
    public async Task<ActionResult<TeacherDraftBatchMutationResult>> MutateBatch(
        [FromBody] TeacherDraftBatchMutationRequest request)
    {
        if (request?.Upserts is null || request.DeleteIds is null)
        {
            return BadRequest(new { message = "Спільний пакет змін має містити списки збереження та видалення." });
        }
        if (request.Upserts.Any(item => item is null))
        {
            return BadRequest(new { message = "Список збереження спільного пакета не може містити порожні елементи." });
        }
        var operationCount = (long)request.Upserts.Count + request.DeleteIds.Count;
        if (operationCount == 0)
        {
            return BadRequest(new { message = "Спільний пакет змін не може бути порожнім." });
        }
        if (operationCount > MaxBatchMutationCount)
        {
            return BadRequest(new { message = $"За один запит можна виконати не більше {MaxBatchMutationCount} змін чернеток." });
        }
        if (request.DeleteIds.Any(id => id <= 0))
        {
            return BadRequest(new { message = "Ідентифікатори чернеток для видалення мають бути додатними числами." });
        }
        if (request.DeleteIds.Distinct().Count() != request.DeleteIds.Count)
        {
            return BadRequest(new { message = "Спільний пакет змін містить повторювані ідентифікатори видалення." });
        }
        if (request.DeleteIds.Count > 0
            && (request.DeleteExpectedRevisions is null
                || request.DeleteIds.Any(id => !request.DeleteExpectedRevisions.TryGetValue(id, out var revision) || revision == Guid.Empty)))
        {
            return StatusCode(428, new
            {
                message = "Для видалення у спільному пакеті потрібні актуальні версії всіх чернеток. Оновіть сторінку та повторіть дію."
            });
        }
        var duplicateExistingId = request.Upserts
            .Where(item => item.Id is > 0)
            .GroupBy(item => item.Id!.Value)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateExistingId is not null)
        {
            return BadRequest(new
            {
                message = $"Чернетка #{duplicateExistingId.Key} повторюється у списку збереження спільного пакета."
            });
        }
        var updatedExistingIds = request.Upserts
            .Where(item => item.Id is > 0)
            .Select(item => item.Id!.Value)
            .ToHashSet();
        var updatedAndDeletedId = request.DeleteIds.FirstOrDefault(updatedExistingIds.Contains);
        if (updatedAndDeletedId > 0)
        {
            return BadRequest(new
            {
                message = $"Чернетку #{updatedAndDeletedId} не можна одночасно оновлювати й видаляти в одному пакеті."
            });
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var upsertedIds = new List<int>(request.Upserts.Count);
        try
        {
            var mutationIds = updatedExistingIds
                .Concat(request.DeleteIds)
                .Distinct()
                .ToList();
            if (await FindIncompleteLogicalEventMutationAsync(mutationIds) is { } logicalEventConflict)
            {
                await transaction.RollbackAsync();
                return logicalEventConflict;
            }

            for (var index = 0; index < request.Upserts.Count; index++)
            {
                var action = await UpsertCoreAsync(
                    request.Upserts[index],
                    allowTransientBatchConflicts: true);
                if (TryReadUpsertId(action, out var id))
                {
                    upsertedIds.Add(id);
                    continue;
                }

                await transaction.RollbackAsync();
                _db.ChangeTracker.Clear();
                return BuildBatchFailure(action.Result, index, "спільного збереження");
            }

            for (var index = 0; index < request.DeleteIds.Count; index++)
            {
                var action = await DeleteCoreAsync(
                    request.DeleteIds[index],
                    request.DeleteExpectedRevisions![request.DeleteIds[index]],
                    request.Confirm,
                    request.Unrestricted,
                    allowLogicalEventBatchMutation: true);
                if (action is NoContentResult)
                {
                    continue;
                }

                await transaction.RollbackAsync();
                _db.ChangeTracker.Clear();
                return BuildBatchFailure(action, index, "видалення у спільному пакеті");
            }

            var finalValidation = await ValidateFinalBatchStateAsync(
                upsertedIds,
                request.Upserts,
                allowBypassableErrors: request.Unrestricted);
            if (finalValidation.Action is not null)
            {
                await transaction.RollbackAsync();
                _db.ChangeTracker.Clear();
                return BuildBatchFailure(finalValidation.Action, finalValidation.ItemIndex, "спільного збереження");
            }

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
            return Ok(new TeacherDraftBatchMutationResult(
                upsertedIds,
                upsertedIds.Count,
                request.DeleteIds.Count));
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            return ConcurrencyConflict("спільної пакетної зміни");
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
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
        item.BatchKey = request.BatchKey;
        item.IsLocked = request.IsLocked;
        item.IsSelfStudy = request.IsSelfStudy;
        item.ValidationWarnings = validationReport;
        item.Status = DraftStatus.Draft;
        item.UpdatedAt = DateTime.UtcNow;
    }
    // Відтворює фактично збережений стан для обов'язкової фінальної валідації пакета.
    private static DraftUpsertRequest BuildFinalDraftRequest(
        TeacherDraftItem item,
        DraftUpsertRequest originalRequest)
        => new(
            Id: item.Id,
            Date: item.Date,
            TimeStart: item.StartTime.ToString("HH:mm", CultureInfo.InvariantCulture),
            TimeEnd: item.EndTime.ToString("HH:mm", CultureInfo.InvariantCulture),
            GroupId: item.GroupId,
            ModuleId: item.ModuleId,
            ModuleTopicId: item.ModuleTopicId,
            TeacherId: item.TeacherId,
            RoomId: item.RoomId,
            RequiresRoom: originalRequest.RequiresRoom,
            LessonTypeId: item.LessonTypeId,
            OverrideNonWorkingDay: originalRequest.OverrideNonWorkingDay,
            BatchKey: item.BatchKey,
            IsLocked: item.IsLocked,
            IgnoreValidationErrors: false,
            IsSelfStudy: item.IsSelfStudy);
    // Повторно перевіряє фактичний фінальний стан усіх збережених елементів перед commit.
    private async Task<(IActionResult? Action, int ItemIndex)> ValidateFinalBatchStateAsync(
        IReadOnlyList<int> ids,
        IReadOnlyList<DraftUpsertRequest> originalRequests,
        bool allowBypassableErrors)
    {
        for (var index = 0; index < ids.Count; index++)
        {
            var item = await _db.TeacherDraftItems.FirstOrDefaultAsync(draft => draft.Id == ids[index]);
            if (item is null)
            {
                return (
                    NotFound(new { message = $"Чернетку #{ids[index]} не знайдено після пакетного збереження." }),
                    index);
            }

            var finalRequest = BuildFinalDraftRequest(item, originalRequests[index]);
            var validation = await _rules.ValidateDraftAsync(finalRequest);
            if (HasBlockingValidationErrors(validation, allowBypassableErrors))
            {
                return (
                    Conflict(new
                    {
                        message = "Чернетка не пройшла фінальну перевірку правил.",
                        errors = validation.Errors,
                        warnings = validation.Warnings,
                        details = validation.Report
                    }),
                    index);
            }

            item.ValidationWarnings = validation.Report.Issues.Count > 0
                ? JsonSerializer.Serialize(validation.Report, ValidationJsonOptions)
                : null;
        }

        return (null, -1);
    }
    // Забороняє часткову зміну багаторядкового логічного заняття поза атомарним пакетним запитом.
    private async Task<ConflictObjectResult?> FindIncompleteLogicalEventMutationAsync(
        IReadOnlyCollection<int> mutationIds,
        IReadOnlyCollection<TeacherDraftItem>? preloadedSelectedRows = null)
    {
        var mutationIdSet = mutationIds
            .Where(id => id > 0)
            .ToHashSet();
        if (mutationIdSet.Count == 0)
        {
            return null;
        }

        var selectedRows = preloadedSelectedRows is null
            ? await _db.TeacherDraftItems
                .AsNoTracking()
                .Where(item => mutationIdSet.Contains(item.Id))
                .ToListAsync()
            : preloadedSelectedRows
                .Where(item => mutationIdSet.Contains(item.Id))
                .ToList();
        var batchKeys = selectedRows
            .Where(item => !string.IsNullOrWhiteSpace(item.BatchKey))
            .Select(item => item.BatchKey!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var legacySelectedRows = selectedRows
            .Where(item => string.IsNullOrWhiteSpace(item.BatchKey))
            .ToList();

        List<TeacherDraftItem> candidates;
        if (batchKeys.Count > 0 && legacySelectedRows.Count > 0)
        {
            Expression<Func<TeacherDraftItem, bool>> batchPredicate = item =>
                item.BatchKey != null && batchKeys.Contains(item.BatchKey);
            var candidatePredicate = CombineWithOr(
                batchPredicate,
                BuildLegacyLogicalEventCandidatePredicate(legacySelectedRows));
            candidates = await _db.TeacherDraftItems
                .AsNoTracking()
                .Where(candidatePredicate)
                .ToListAsync();
        }
        else if (batchKeys.Count > 0)
        {
            candidates = await _db.TeacherDraftItems
                .AsNoTracking()
                .Where(item => item.BatchKey != null && batchKeys.Contains(item.BatchKey))
                .ToListAsync();
        }
        else if (legacySelectedRows.Count > 0)
        {
            var candidatePredicate = BuildLegacyLogicalEventCandidatePredicate(legacySelectedRows);
            candidates = await _db.TeacherDraftItems
                .AsNoTracking()
                .Where(candidatePredicate)
                .ToListAsync();
        }
        else
        {
            candidates = new List<TeacherDraftItem>();
        }
        foreach (var logicalEvent in selectedRows
                     .Where(item => !string.IsNullOrWhiteSpace(item.BatchKey))
                     .GroupBy(item => new
                     {
                         BatchKey = item.BatchKey!,
                         item.Date,
                         item.StartTime,
                         item.EndTime,
                         item.GroupId,
                         item.ModuleId,
                         item.LessonTypeId
                     }))
        {
            var missingIds = candidates
                .Where(candidate => string.Equals(
                                        candidate.BatchKey,
                                        logicalEvent.Key.BatchKey,
                                        StringComparison.Ordinal)
                                    && candidate.Date == logicalEvent.Key.Date
                                    && candidate.StartTime == logicalEvent.Key.StartTime
                                    && candidate.EndTime == logicalEvent.Key.EndTime
                                    && candidate.GroupId == logicalEvent.Key.GroupId
                                    && candidate.ModuleId == logicalEvent.Key.ModuleId
                                    && candidate.LessonTypeId == logicalEvent.Key.LessonTypeId
                                    && !mutationIdSet.Contains(candidate.Id))
                .Select(candidate => candidate.Id)
                .OrderBy(id => id)
                .ToList();
            if (missingIds.Count == 0)
            {
                continue;
            }

            return Conflict(new
            {
                message = "Багаторядкове логічне заняття не можна змінювати або видаляти частково. Передайте всі його рядки в одному атомарному пакетному запиті.",
                missingIds
            });
        }

        // Legacy-рядки без BatchKey теж утворюють одну подію, якщо однакова сигнатура
        // розкладена за різними темами або співвикладачами.
        foreach (var logicalEvent in legacySelectedRows
                     .GroupBy(item => new
                     {
                         item.Date,
                         item.StartTime,
                         item.EndTime,
                         item.GroupId,
                         item.ModuleId,
                         item.LessonTypeId
                     }))
        {
            var legacyRows = candidates
                .Where(candidate => string.IsNullOrWhiteSpace(candidate.BatchKey)
                                    && candidate.Date == logicalEvent.Key.Date
                                    && candidate.StartTime == logicalEvent.Key.StartTime
                                    && candidate.EndTime == logicalEvent.Key.EndTime
                                    && candidate.GroupId == logicalEvent.Key.GroupId
                                    && candidate.ModuleId == logicalEvent.Key.ModuleId
                                    && candidate.LessonTypeId == logicalEvent.Key.LessonTypeId)
                .ToList();
            var isLogicalEvent = legacyRows.Count > 1
                                 && legacyRows
                                     .Select(candidate => new
                                     {
                                         candidate.ModuleTopicId,
                                         candidate.TeacherId
                                     })
                                     .Distinct()
                                     .Skip(1)
                                     .Any();
            if (!isLogicalEvent)
            {
                continue;
            }

            var missingIds = legacyRows
                .Where(candidate => !mutationIdSet.Contains(candidate.Id))
                .Select(candidate => candidate.Id)
                .OrderBy(id => id)
                .ToList();
            if (missingIds.Count == 0)
            {
                continue;
            }

            return Conflict(new
            {
                message = "Багаторядкове логічне заняття не можна змінювати або видаляти частково. Передайте всі його рядки в одному атомарному пакетному запиті.",
                missingIds
            });
        }

        return null;
    }

    // Формує точну диз'юнкцію legacy-сигнатур, щоб незалежні IN-набори
    // не матеріалізували декартові комбінації дат, слотів і навчальних вимірів.
    private static Expression<Func<TeacherDraftItem, bool>> BuildLegacyLogicalEventCandidatePredicate(
        IReadOnlyCollection<TeacherDraftItem> selectedRows)
    {
        var signatures = selectedRows
            .Select(item => new LegacyLogicalEventSignature(
                item.Date,
                item.StartTime,
                item.EndTime,
                item.GroupId,
                item.ModuleId,
                item.LessonTypeId))
            .Distinct()
            .ToList();
        var candidate = Expression.Parameter(typeof(TeacherDraftItem), "candidate");
        var scopeMatches = new List<Expression>();
        foreach (var scope in signatures.GroupBy(signature => new
                 {
                     signature.Date,
                     signature.GroupId,
                     signature.ModuleId,
                     signature.LessonTypeId
                 }))
        {
            var timeMatches = new List<Expression>();
            foreach (var signature in scope)
            {
                var timeMatch = Expression.AndAlso(
                    EqualProperty(candidate, nameof(TeacherDraftItem.StartTime), signature.StartTime),
                    EqualProperty(candidate, nameof(TeacherDraftItem.EndTime), signature.EndTime));
                timeMatches.Add(timeMatch);
            }

            var scopeMatch = Expression.AndAlso(
                EqualProperty(candidate, nameof(TeacherDraftItem.Date), scope.Key.Date),
                Expression.AndAlso(
                    EqualProperty(candidate, nameof(TeacherDraftItem.GroupId), scope.Key.GroupId),
                    Expression.AndAlso(
                        EqualProperty(candidate, nameof(TeacherDraftItem.ModuleId), scope.Key.ModuleId),
                        Expression.AndAlso(
                            EqualProperty(
                                candidate,
                                nameof(TeacherDraftItem.LessonTypeId),
                                scope.Key.LessonTypeId),
                            CombineBalancedOr(timeMatches)))));
            scopeMatches.Add(scopeMatch);
        }

        // Перевірку whitespace у BatchKey залишаємо в пам'яті: SQL TRIM у різних
        // провайдерів не охоплює всі символи, які розпізнає .NET IsNullOrWhiteSpace.
        return Expression.Lambda<Func<TeacherDraftItem, bool>>(
            CombineBalancedOr(scopeMatches),
            candidate);
    }

    private static Expression CombineBalancedOr(IReadOnlyList<Expression> expressions)
    {
        if (expressions.Count == 0)
        {
            return Expression.Constant(false);
        }

        var level = expressions.ToList();
        while (level.Count > 1)
        {
            var nextLevel = new List<Expression>((level.Count + 1) / 2);
            for (var index = 0; index < level.Count; index += 2)
            {
                nextLevel.Add(index + 1 < level.Count
                    ? Expression.OrElse(level[index], level[index + 1])
                    : level[index]);
            }
            level = nextLevel;
        }

        return level[0];
    }

    private static BinaryExpression EqualProperty<T>(
        ParameterExpression parameter,
        string propertyName,
        T value)
        => Expression.Equal(
            Expression.Property(parameter, propertyName),
            Expression.Constant(value, typeof(T)));

    private static Expression<Func<T, bool>> CombineWithOr<T>(
        Expression<Func<T, bool>> left,
        Expression<Func<T, bool>> right)
    {
        var parameter = Expression.Parameter(typeof(T), "candidate");
        var leftBody = new ParameterReplacementVisitor(left.Parameters[0], parameter).Visit(left.Body)!;
        var rightBody = new ParameterReplacementVisitor(right.Parameters[0], parameter).Visit(right.Body)!;
        return Expression.Lambda<Func<T, bool>>(Expression.OrElse(leftBody, rightBody), parameter);
    }

    private sealed class ParameterReplacementVisitor(
        ParameterExpression source,
        ParameterExpression target) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
            => node == source ? target : base.VisitParameter(node);
    }

    private readonly record struct LegacyLogicalEventSignature(
        DateOnly Date,
        TimeOnly StartTime,
        TimeOnly EndTime,
        int GroupId,
        int ModuleId,
        int LessonTypeId);

    private ConflictObjectResult ConcurrencyConflict(string operation)
        => Conflict(new
        {
            message = $"Чернетка вже змінилася під час {operation}. Оновіть сторінку та повторіть дію."
        });

    private static bool HasBlockingValidationErrors(
        RulesService.DraftValidationResult validation,
        bool allowBypassableErrors,
        bool allowTransientLogicalEventMismatch = false)
    {
        if (validation.Errors.Count == 0)
        {
            return false;
        }
        if (!allowBypassableErrors)
        {
            return true;
        }

        return validation.Report.Issues.Any(issue =>
            string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase)
            && !RulesService.IsBypassableDraftValidationIssue(issue)
            && !(allowTransientLogicalEventMismatch
                 && string.Equals(
                     issue.Code,
                     "logical-event-resource-mismatch",
                     StringComparison.Ordinal)));
    }
    // Витягує ідентифікатор лише з успішного результату одиночного збереження.
    private static bool TryReadUpsertId(ActionResult<int> action, out int id)
    {
        if (action.Result is OkObjectResult { Value: int resultId } && resultId > 0)
        {
            id = resultId;
            return true;
        }
        if (action.Result is null && action.Value > 0)
        {
            id = action.Value;
            return true;
        }

        id = 0;
        return false;
    }
    // Додає позицію невдалого елемента, зберігаючи початковий HTTP-статус і поля помилки.
    private static ObjectResult BuildBatchFailure(IActionResult? action, int itemIndex, string operation)
    {
        var statusCode = action switch
        {
            ObjectResult { StatusCode: int code } => code,
            StatusCodeResult statusResult => statusResult.StatusCode,
            _ => StatusCodes.Status500InternalServerError
        };
        var payload = new Dictionary<string, object?>
        {
            ["message"] = $"Пакет {operation} відкочено через помилку в елементі №{itemIndex + 1}.",
            ["batchMessage"] = $"Пакет {operation} повністю відкочено.",
            ["itemIndex"] = itemIndex,
            ["itemNumber"] = itemIndex + 1
        };
        if (action is ObjectResult { Value: not null } objectResult)
        {
            var source = JsonSerializer.SerializeToElement(objectResult.Value, ValidationJsonOptions);
            if (source.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in source.EnumerateObject())
                {
                    payload[property.Name] = property.Value.Clone();
                }
                payload["itemIndex"] = itemIndex;
                payload["itemNumber"] = itemIndex + 1;
            }
            else
            {
                payload["error"] = source.Clone();
            }
        }

        return new ObjectResult(payload) { StatusCode = statusCode };
    }
    [HttpPost("clear-week")]
    [RequireDeletionConfirmation("незаблоковані чернетки за тиждень")]
    // Очищає незаблоковані чернетки за вказаний тиждень із можливими додатковими фільтрами.
    public async Task<ActionResult<ClearWeekResult>> ClearWeek([FromBody] ClearWeekRequest r)
    {
        if (!DateHelpers.IsSupportedScheduleDate(r.WeekStart))
        {
            return BadRequest(new { message = DateHelpers.SupportedScheduleDateMessage });
        }
        if (r.CourseId is null && r.GroupId is null)
        {
            return BadRequest(new { message = "Для очищення тижня потрібно вказати курс або групу." });
        }
        var start = r.WeekStart;
        var end = start.AddDays(7);
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var scopeQuery = _db.TeacherDraftItems
            .AsNoTracking()
            .Where(x => x.Date >= start && x.Date < end);
        if (r.CourseId is int cid) scopeQuery = scopeQuery.Where(x => x.Group.CourseId == cid);
        if (r.GroupId is int gid) scopeQuery = scopeQuery.Where(x => x.GroupId == gid);
        var scopedRows = await scopeQuery
            .OrderBy(x => x.Date)
            .ThenBy(x => x.StartTime)
            .ThenBy(x => x.GroupId)
            .ThenBy(x => x.Id)
            .Take(TeacherDraftsWeekValidationService.MaxWeekDraftRowCount + 1)
            .ToListAsync();
        if (scopedRows.Count > TeacherDraftsWeekValidationService.MaxWeekDraftRowCount)
        {
            await transaction.RollbackAsync();
            return Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Забагато чернеток для очищення",
                detail: $"За одну операцію можна очистити не більше {TeacherDraftsWeekValidationService.MaxWeekDraftRowCount} чернеток.");
        }
        if (r.ExpectedScopeRevision is not Guid expectedScopeRevision)
        {
            await transaction.RollbackAsync();
            return Problem(
                statusCode: StatusCodes.Status428PreconditionRequired,
                title: "Потрібна версія тижня",
                detail: "Оновіть чернетки тижня перед очищенням.");
        }
        var actualScopeRevision = LogicalRevisionToken.Combine(scopedRows.Select(item =>
            new KeyValuePair<int, Guid>(item.Id, item.Revision)));
        if (actualScopeRevision != expectedScopeRevision)
        {
            await transaction.RollbackAsync();
            return Conflict(new
            {
                message = "Чернетки тижня змінилися після завантаження. Оновіть сторінку та повторіть очищення."
            });
        }
        var deletionIds = scopedRows
            .Where(x => x.Status == DraftStatus.Draft && !x.IsLocked)
            .Select(x => x.Id)
            .ToHashSet();

        // Не дозволяє масовому очищенню розірвати явну або консервативно розпізнану legacy-подію.
        var logicalEvents = scopedRows
            .Where(x => !string.IsNullOrWhiteSpace(x.BatchKey))
            .GroupBy(x => new
            {
                BatchKey = x.BatchKey!,
                x.Date,
                x.StartTime,
                x.EndTime,
                x.GroupId,
                x.ModuleId,
                x.LessonTypeId
            })
            .Where(group => group.Skip(1).Any())
            .Select(group => group.ToList())
            .ToList();
        logicalEvents.AddRange(scopedRows
            .Where(x => string.IsNullOrWhiteSpace(x.BatchKey))
            .GroupBy(x => new
            {
                x.Date,
                x.StartTime,
                x.EndTime,
                x.GroupId,
                x.ModuleId,
                x.LessonTypeId
            })
            .Where(group => group
                .Select(x => new { x.ModuleTopicId, x.TeacherId })
                .Distinct()
                .Skip(1)
                .Any())
            .Select(group => group.ToList()));
        foreach (var rows in logicalEvents)
        {
            if (rows.Select(x => x.Status).Distinct().Skip(1).Any())
            {
                await transaction.RollbackAsync();
                return Conflict(new
                {
                    message = "Логічне заняття має змішані статуси рядків. Повторно схваліть його цілісним пакетом перед очищенням тижня.",
                    batchKey = rows[0].BatchKey,
                    itemIds = rows.Select(x => x.Id).OrderBy(id => id).ToList()
                });
            }

            var touchesDeletableRow = rows.Any(x => deletionIds.Contains(x.Id));
            if (!touchesDeletableRow)
            {
                continue;
            }
            if (rows.Any(x => x.IsLocked))
            {
                await transaction.RollbackAsync();
                return Conflict(new
                {
                    message = "Логічне заняття містить заблоковані рядки й не може бути частково очищене.",
                    batchKey = rows[0].BatchKey,
                    itemIds = rows.Select(x => x.Id).OrderBy(id => id).ToList()
                });
            }

            deletionIds.UnionWith(rows.Select(x => x.Id));
        }

        var deleted = deletionIds.Count == 0
            ? 0
            : await _db.TeacherDraftItems
                .Where(x => deletionIds.Contains(x.Id))
                .ExecuteDeleteAsync();
        await transaction.CommitAsync();
        return Ok(new ClearWeekResult(deleted));
    }
    [HttpPost("autogen/week")]
    // Застарілий синхронний маршрут вимкнено, щоб усі запуски проходили через контрольовану чергу.
    public ActionResult<AutoGenResult> DraftAutoGenWeek([FromBody] DraftAutoGenRequest r)
        => LegacyAutogenEndpointDisabled();
    [HttpPost("autogen/month")]
    // Застарілий синхронний маршрут вимкнено, щоб усі запуски проходили через контрольовану чергу.
    public ActionResult<AutoGenResult> AutogenMonth([FromBody] AutogenMonthRequest r)
        => LegacyAutogenEndpointDisabled();
    [HttpPost("autogen/course")]
    // Застарілий синхронний маршрут вимкнено, щоб усі запуски проходили через контрольовану чергу.
    public ActionResult<AutoGenResult> AutogenCourse([FromBody] AutogenCourseRequest r)
        => LegacyAutogenEndpointDisabled();
    [HttpPost("autogen")]
    // Застарілий синхронний маршрут вимкнено, щоб усі запуски проходили через контрольовану чергу.
    public ActionResult<AutoGenResult> DraftAutoGen([FromBody] DraftAutoGenRequest r)
        => LegacyAutogenEndpointDisabled();
    [HttpPost("autogen/jobs")]
    [EnableRateLimiting("autogen-start")]
    public ActionResult<AutoGenJobStartResult> StartAutoGenJob([FromBody] AutoGenJobRequest r)
    {
        if (r.Kind is AutoGenJobKind.Generate or AutoGenJobKind.Fill && !r.PreviewOnly)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Потрібен попередній перегляд автогенерації",
                detail: "Генерацію та дозаповнення можна виконати лише через попередній план із подальшим окремим застосуванням.");
        }
        try
        {
            return Ok(_autogenJobService.Start(r, ClientPartitionKey.Resolve(HttpContext)));
        }
        catch (AutoGenJobValidationException ex)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Некоректні параметри автогенерації",
                detail: ex.Message);
        }
        catch (AutoGenJobCapacityException ex)
        {
            return Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Черга автогенерації заповнена",
                detail: ex.Message);
        }
        catch (AutoGenJobConflictException ex)
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Конфлікт ідентифікатора автогенерації",
                detail: ex.Message);
        }
        catch (AutoGenJobPersistenceException ex)
        {
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Сховище стану автогенерації недоступне",
                detail: ex.Message);
        }
    }
    [HttpGet("autogen/jobs/{jobId}")]
    [EnableRateLimiting("autogen-status")]
    public async Task<ActionResult<AutoGenJobStatus>> GetAutoGenJob(
        string jobId,
        [FromServices] ExpensiveOperationGate operationGate,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(jobId?.Trim(), "N", out var parsedJobId))
        {
            return NotFound(new { message = "Завдання автогенерації не знайдено." });
        }

        using var lease = await operationGate.TryEnterAsync(
            ExpensiveOperationKind.AutoGenStatus,
            cancellationToken);
        if (lease is null)
        {
            return Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Забагато одночасних перевірок стану",
                detail: "Дочекайтеся завершення поточних перевірок і повторіть запит.");
        }

        try
        {
            return await _autogenJobService.GetAsync(
                       parsedJobId.ToString("N"),
                       ClientPartitionKey.Resolve(HttpContext),
                       cancellationToken) is { } status
                ? Ok(status)
                : NotFound(new { message = "Завдання автогенерації не знайдено." });
        }
        catch (AutoGenJobPersistenceException ex)
        {
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Сховище стану автогенерації недоступне",
                detail: ex.Message);
        }
    }
    [HttpPost("autogen/jobs/{jobId}/cancel")]
    [EnableRateLimiting("autogen-status")]
    public async Task<ActionResult<AutoGenJobStatus>> CancelAutoGenJob(
        string jobId,
        [FromServices] ExpensiveOperationGate operationGate,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(jobId?.Trim(), "N", out var parsedJobId))
        {
            return NotFound(new { message = "Завдання автогенерації не знайдено." });
        }

        using var lease = await operationGate.TryEnterAsync(
            ExpensiveOperationKind.AutoGenStatus,
            cancellationToken);
        if (lease is null)
        {
            return Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Забагато одночасних запитів скасування",
                detail: "Дочекайтеся завершення поточних операцій і повторіть запит.");
        }

        try
        {
            return await _autogenJobService.CancelAsync(
                       parsedJobId.ToString("N"),
                       ClientPartitionKey.Resolve(HttpContext),
                       cancellationToken) is { } status
                ? Ok(status)
                : NotFound(new { message = "Завдання автогенерації не знайдено." });
        }
        catch (AutoGenJobPersistenceException ex)
        {
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Сховище стану автогенерації недоступне",
                detail: ex.Message);
        }
    }
    [HttpGet("autogen/jobs/{jobId}/plan")]
    [EnableRateLimiting("autogen-plan-read")]
    public async Task<ActionResult<AutoGenPlanDetailsDto>> GetAutoGenPlan(
        string jobId,
        [FromQuery] int changeOffset,
        [FromQuery] int? changeLimit,
        [FromServices] ExpensiveOperationGate operationGate,
        CancellationToken cancellationToken)
    {
        var resolvedChangeLimit = changeLimit
                                  ?? TeacherDraftsAutogenPlanService.DefaultChangePageSize;
        if (changeOffset < 0
            || resolvedChangeLimit <= 0
            || resolvedChangeLimit > TeacherDraftsAutogenPlanService.MaxChangePageSize)
        {
            return BadRequest(new
            {
                message = $"Сторінка змін має містити від 1 до {TeacherDraftsAutogenPlanService.MaxChangePageSize} записів."
            });
        }

        using (var handoffLease = await operationGate.TryEnterAsync(
                   ExpensiveOperationKind.AutoGenPlanHandoff,
                   cancellationToken))
        {
            if (handoffLease is null)
            {
                return Problem(
                    statusCode: StatusCodes.Status429TooManyRequests,
                    title: "Забагато очікувань завершення плану",
                    detail: "Дочекайтеся завершення поточних планів і повторіть запит.");
            }
            try
            {
                await _autogenJobService.PreparePlanReadAsync(
                    jobId,
                    ClientPartitionKey.Resolve(HttpContext),
                    cancellationToken);
            }
            catch (AutoGenPlanConflictException ex)
            {
                return Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "План автогенерації ще не готовий",
                    detail: ex.Message);
            }
        }

        using var lease = await operationGate.TryEnterAsync(
            ExpensiveOperationKind.AutoGenPlanRead,
            cancellationToken);
        if (lease is null)
        {
            return Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Забагато одночасних читань плану",
                detail: "Дочекайтеся завершення поточних читань і повторіть запит.");
        }

        try
        {
            var plan = await _autogenJobService.GetPlanPageAsync(
                jobId,
                ClientPartitionKey.Resolve(HttpContext),
                changeOffset,
                resolvedChangeLimit,
                cancellationToken);
            if (!changeLimit.HasValue && plan.HasMoreChanges)
            {
                return Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Клієнт має підтримувати сторінки плану",
                    detail: "Оновіть клієнт і повторіть запит із параметрами changeOffset та changeLimit.");
            }
            return Ok(plan);
        }
        catch (AutoGenPlanNotFoundException ex)
        {
            return Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "План автогенерації не знайдено",
                detail: ex.Message);
        }
        catch (AutoGenPlanConflictException ex)
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "План автогенерації ще не готовий",
                detail: ex.Message);
        }
        catch (AutoGenPlanPersistenceException ex)
        {
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Не вдалося прочитати план автогенерації",
                detail: ex.Message);
        }
    }
    [HttpGet("autogen/plans/latest-rollbackable")]
    [EnableRateLimiting("autogen-plan-read")]
    public async Task<ActionResult<AutoGenPlanDetailsDto>> GetLatestRollbackableAutoGenPlan(
        [FromQuery] int? courseId,
        [FromQuery] int changeOffset,
        [FromQuery] int? changeLimit,
        [FromServices] ExpensiveOperationGate operationGate,
        CancellationToken cancellationToken)
    {
        var resolvedChangeLimit = changeLimit
                                  ?? TeacherDraftsAutogenPlanService.DefaultChangePageSize;
        if (changeOffset < 0
            || resolvedChangeLimit <= 0
            || resolvedChangeLimit > TeacherDraftsAutogenPlanService.MaxChangePageSize)
        {
            return BadRequest(new
            {
                message = $"Сторінка змін має містити від 1 до {TeacherDraftsAutogenPlanService.MaxChangePageSize} записів."
            });
        }

        using var lease = await operationGate.TryEnterAsync(
            ExpensiveOperationKind.AutoGenPlanRead,
            cancellationToken);
        if (lease is null)
        {
            return Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Забагато одночасних читань плану",
                detail: "Дочекайтеся завершення поточних читань і повторіть запит.");
        }

        try
        {
            var plan = await _autogenJobService.GetLatestRollbackablePlanPageAsync(
                courseId,
                ClientPartitionKey.Resolve(HttpContext),
                changeOffset,
                resolvedChangeLimit,
                cancellationToken);
            if (!changeLimit.HasValue && plan?.HasMoreChanges == true)
            {
                return Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Клієнт має підтримувати сторінки плану",
                    detail: "Оновіть клієнт і повторіть запит із параметрами changeOffset та changeLimit.");
            }
            return plan is null ? NoContent() : Ok(plan);
        }
        catch (AutoGenPlanPersistenceException ex)
        {
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Не вдалося знайти доступний відкіт автогенерації",
                detail: ex.Message);
        }
    }
    [HttpPost("autogen/jobs/{jobId}/apply")]
    [EnableRateLimiting("autogen-plan-action")]
    public async Task<ActionResult<AutoGenPlanDetailsDto>> ApplyAutoGenPlan(
        string jobId,
        [FromBody] AutoGenPlanActionRequest request,
        [FromServices] ExpensiveOperationGate operationGate,
        CancellationToken cancellationToken)
        => await ExecuteAutoGenPlanActionAsync(
            jobId,
            ClientPartitionKey.Resolve(HttpContext),
            clientPartitionKey => _autogenJobService.ApplyPlanAsync(
                jobId,
                request,
                clientPartitionKey,
                cancellationToken),
            operationGate,
            cancellationToken);
    [HttpPost("autogen/jobs/{jobId}/rollback")]
    [EnableRateLimiting("autogen-plan-action")]
    public async Task<ActionResult<AutoGenPlanDetailsDto>> RollbackAutoGenPlan(
        string jobId,
        [FromBody] AutoGenPlanActionRequest request,
        [FromServices] ExpensiveOperationGate operationGate,
        CancellationToken cancellationToken)
        => await ExecuteAutoGenPlanActionAsync(
            jobId,
            ClientPartitionKey.Resolve(HttpContext),
            clientPartitionKey => _autogenJobService.RollbackPlanAsync(
                jobId,
                request,
                clientPartitionKey,
                cancellationToken),
            operationGate,
            cancellationToken);

    private async Task<ActionResult<AutoGenPlanDetailsDto>> ExecuteAutoGenPlanActionAsync(
        string jobId,
        string clientPartitionKey,
        Func<string, Task<AutoGenPlanDetailsDto>> action,
        ExpensiveOperationGate operationGate,
        CancellationToken cancellationToken)
    {
        using (var handoffLease = await operationGate.TryEnterAsync(
                   ExpensiveOperationKind.AutoGenPlanHandoff,
                   cancellationToken))
        {
            if (handoffLease is null)
            {
                return Problem(
                    statusCode: StatusCodes.Status429TooManyRequests,
                    title: "Забагато очікувань завершення плану",
                    detail: "Дочекайтеся завершення поточних планів і повторіть запит.");
            }
            try
            {
                await _autogenJobService.PreparePlanReadAsync(
                    jobId,
                    clientPartitionKey,
                    cancellationToken);
            }
            catch (AutoGenPlanConflictException ex)
            {
                return Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "План автогенерації ще не готовий",
                    detail: ex.Message);
            }
        }

        using var lease = await operationGate.TryEnterAsync(
            ExpensiveOperationKind.AutoGenPlanAction,
            cancellationToken);
        if (lease is null)
        {
            return Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Інша дія з планом уже виконується",
                detail: "Дочекайтеся завершення поточної дії та повторіть запит.");
        }

        try
        {
            return Ok(await action(clientPartitionKey));
        }
        catch (AutoGenPlanNotFoundException ex)
        {
            return Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "План автогенерації не знайдено",
                detail: ex.Message);
        }
        catch (AutoGenPlanConflictException ex)
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "План автогенерації застарів або конфліктує з поточними даними",
                detail: ex.Message);
        }
        catch (AutoGenPlanPersistenceException ex)
        {
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Сховище плану автогенерації недоступне",
                detail: ex.Message);
        }
    }
    [HttpPost("approve-week")]
    [EnableRateLimiting("week-validation")]
    // Позначає чернетки викладача за тиждень як затверджені.
    public async Task<IActionResult> ApproveWeek(
        [FromBody] ApproveWeekRequest r,
        CancellationToken cancellationToken = default)
    {
        if (r.ExpectedScopeRevision is not Guid expectedScopeRevision
            || expectedScopeRevision == Guid.Empty)
        {
            return Problem(
                statusCode: StatusCodes.Status428PreconditionRequired,
                title: "Потрібна актуальна версія тижня",
                detail: "Перед схваленням оновіть тиждень і передайте його актуальну версію.");
        }
        try
        {
            return await _publishService.ApproveWeekAsync(r, cancellationToken);
        }
        catch (DraftValidationCapacityException ex)
        {
            return Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Обсяг тижня перевищує безпечний ліміт",
                detail: ex.Message);
        }
        catch (DraftValidationTimeoutException ex)
        {
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Схвалення тижня перевищило безпечний час",
                detail: ex.Message);
        }
    }
    [HttpPost("publish-week")]
    [EnableRateLimiting("week-validation")]
    // Публікує всі чернетки вибраного тижня у розклад після атомарної пакетної перевірки.
    public async Task<ActionResult<PublishWeekResults>> PublishWeek(
        [FromBody] PublishWeekRequest r,
        CancellationToken cancellationToken = default)
    {
        if (r.ExpectedScopeRevision is not Guid expectedScopeRevision
            || expectedScopeRevision == Guid.Empty)
        {
            return Problem(
                statusCode: StatusCodes.Status428PreconditionRequired,
                title: "Потрібна актуальна перевірка тижня",
                detail: "Перед публікацією повторно перевірте тиждень і передайте його актуальну версію.");
        }
        try
        {
            return await _publishService.PublishWeekAsync(r, cancellationToken);
        }
        catch (DraftValidationCapacityException ex)
        {
            return Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Обсяг тижня перевищує безпечний ліміт",
                detail: ex.Message);
        }
        catch (DraftValidationTimeoutException ex)
        {
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Публікація тижня перевищила безпечний час",
                detail: ex.Message);
        }
    }

    private ObjectResult LegacyAutogenEndpointDisabled()
        => Problem(
            statusCode: StatusCodes.Status410Gone,
            title: "Синхронну автогенерацію вимкнено",
            detail: "Використовуйте маршрут api/teacher-drafts/autogen/jobs, який обмежує розмір запиту та серіалізує виконання.");
}
