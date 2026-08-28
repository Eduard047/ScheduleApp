using System.Data;
using System.Globalization;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TimeSlotEditor;

public enum TimeSlotEditorFailureKind
{
    Validation,
    NotFound,
    Stale,
    Conflict,
    Busy,
    Timeout,
    Capacity
}

public sealed record TimeSlotEditorFailure(
    TimeSlotEditorFailureKind Kind,
    string Message,
    string? CurrentRevision = null);

public sealed record TimeSlotEditorOutcome<T>(T? Value, TimeSlotEditorFailure? Failure)
{
    public bool IsSuccess => Failure is null;

    public static TimeSlotEditorOutcome<T> Success(T value) => new(value, null);

    public static TimeSlotEditorOutcome<T> Fail(
        TimeSlotEditorFailureKind kind,
        string message,
        string? currentRevision = null)
        => new(default, new TimeSlotEditorFailure(kind, message, currentRevision));
}

// Готує контекст, попередній перегляд і атомарне застосування графіка пар.
public sealed class TimeSlotEditorService
{
    private const int ConflictSampleLimit = 10;
    private const int ExactRangePredicateBatchSize = 384;
    private const int MaxImpactCount = 50_000;
    private static readonly TimeSpan DefaultOperationDeadline = TimeSpan.FromSeconds(20);
    private readonly AppDbContext db;
    private readonly ExpensiveOperationGate? operationGate;
    private readonly TimeSpan operationDeadline;

    public TimeSlotEditorService(
        AppDbContext db,
        ExpensiveOperationGate? operationGate = null,
        TimeSpan? operationDeadline = null)
    {
        this.db = db;
        this.operationGate = operationGate;
        this.operationDeadline = operationDeadline ?? DefaultOperationDeadline;
        if (this.operationDeadline <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(operationDeadline),
                "Граничний час редактора має бути додатним.");
        }
    }

    public async Task<TimeSlotEditorOutcome<TimeSlotEditorContextDto>> GetContextAsync(
        TimeSlotEditorTargetMode targetMode,
        int? courseId,
        int? dayOfWeek,
        CancellationToken cancellationToken = default)
    {
        var targetError = ValidateTarget(targetMode, courseId, dayOfWeek);
        if (targetError is not null)
        {
            return TimeSlotEditorOutcome<TimeSlotEditorContextDto>.Fail(
                TimeSlotEditorFailureKind.Validation,
                targetError);
        }

        var snapshot = await LoadSnapshotAsync(cancellationToken);
        if (targetMode == TimeSlotEditorTargetMode.Course
            && !snapshot.Courses.Any(course => course.Id == courseId))
        {
            return TimeSlotEditorOutcome<TimeSlotEditorContextDto>.Fail(
                TimeSlotEditorFailureKind.NotFound,
                "Курс не знайдено.");
        }

        var selectedCourseId = targetMode == TimeSlotEditorTargetMode.Course ? courseId : null;
        var day = dayOfWeek is int rawDay ? (DayOfWeek?)rawDay : null;
        var globalRows = ResolveEditorRows(snapshot.Slots, null, day);
        var explicitRows = PickScope(snapshot.Slots, selectedCourseId, day);
        var hasCourseOverride = selectedCourseId is int cid
                                && snapshot.Slots.Any(slot => slot.CourseId == cid);
        var hasDayOverride = day is not null
                             && (explicitRows.Count > 0
                                 || (targetMode == TimeSlotEditorTargetMode.AllCourses
                                     && snapshot.Slots.Any(slot =>
                                         slot.CourseId is not null && slot.DayOfWeek == day)));
        var effectiveRows = ResolveEditorRows(snapshot.Slots, selectedCourseId, day);

        var explicitLunch = snapshot.Lunches
            .Where(lunch => lunch.CourseId == selectedCourseId)
            .OrderBy(lunch => lunch.Id)
            .FirstOrDefault();
        var globalLunch = snapshot.Lunches
            .Where(lunch => lunch.CourseId == null)
            .OrderBy(lunch => lunch.Id)
            .FirstOrDefault();
        var effectiveLunch = selectedCourseId is int selectedId
            ? snapshot.Lunches
                  .Where(lunch => lunch.CourseId == selectedId)
                  .OrderBy(lunch => lunch.Id)
                  .FirstOrDefault() ?? globalLunch
            : globalLunch;

        var explicitPreferred = snapshot.PreferredFirstLimits
            .Where(limit => limit.CourseId == selectedCourseId)
            .OrderBy(limit => limit.Id)
            .FirstOrDefault();
        var globalPreferred = snapshot.PreferredFirstLimits
            .Where(limit => limit.CourseId == null)
            .OrderBy(limit => limit.Id)
            .FirstOrDefault();
        var effectivePreferred = selectedCourseId is int
            ? explicitPreferred ?? globalPreferred
            : globalPreferred;

        return TimeSlotEditorOutcome<TimeSlotEditorContextDto>.Success(new TimeSlotEditorContextDto
        {
            TargetMode = targetMode,
            CourseId = selectedCourseId,
            DayOfWeek = dayOfWeek,
            Courses = snapshot.Courses
                .Select(course => new TimeSlotEditorCourseDto { Id = course.Id, Name = course.Name })
                .ToList(),
            ExplicitSlots = MapSlots(explicitRows, day is null ? explicitLunch : null),
            GlobalSlots = MapSlots(globalRows, day is null ? globalLunch : null),
            EffectiveSlots = MapSlots(effectiveRows, day is null ? effectiveLunch : null),
            HasCourseOverride = hasCourseOverride,
            HasDayOverride = hasDayOverride,
            IsInherited = selectedCourseId is int && !hasCourseOverride,
            ExplicitLunch = MapLunch(explicitLunch),
            EffectiveLunch = MapLunch(effectiveLunch),
            PreferredFirstMaxSlotOrder = effectivePreferred?.MaxSlotOrder ?? 0,
            PreferredFirstInherited = selectedCourseId is int && explicitPreferred is null,
            CourseOverrideCount = snapshot.Slots
                .Where(slot => slot.CourseId is not null)
                .Select(slot => slot.CourseId!.Value)
                .Distinct()
                .Count(),
            CurrentRevision = ComputeRevision(snapshot, targetMode, selectedCourseId)
        });
    }

    public Task<TimeSlotEditorOutcome<TimeSlotSequencePreviewDto>> PreviewAsync(
        TimeSlotSequenceApplyRequestDto request,
        CancellationToken cancellationToken = default)
        => ExecuteBoundedAsync(
            token => PreviewCoreAsync(request, token),
            clearTrackedChangesOnCancellation: false,
            cancellationToken);

    private async Task<TimeSlotEditorOutcome<TimeSlotSequencePreviewDto>> PreviewCoreAsync(
        TimeSlotSequenceApplyRequestDto request,
        CancellationToken cancellationToken)
    {
        var snapshot = await LoadSnapshotAsync(cancellationToken);
        var prepared = PrepareRequest(request, snapshot);
        if (prepared.Failure is not null)
        {
            return TimeSlotEditorOutcome<TimeSlotSequencePreviewDto>.Fail(
                prepared.Failure.Kind,
                prepared.Failure.Message,
                prepared.Failure.CurrentRevision);
        }

        var revision = ComputeRevision(snapshot, request.TargetMode, request.CourseId);
        if (!FixedEquals(request.CurrentRevision, revision))
        {
            return TimeSlotEditorOutcome<TimeSlotSequencePreviewDto>.Fail(
                TimeSlotEditorFailureKind.Stale,
                "Графік уже змінився в іншій вкладці. Оновіть дані та повторіть перевірку.",
                revision);
        }

        var plan = BuildMutationPlan(snapshot, prepared.Request!);
        var impact = plan.NoChanges
            ? MutationImpact.Empty
            : await FindImpactAsync(
                request,
                snapshot.Courses.Select(course => course.Id).ToList(),
                snapshot.Slots,
                snapshot.Lunches,
                plan.AfterSlots,
                plan.AfterLunches,
                cancellationToken);
        var expectedRevision = ComputePlannedRevision(snapshot, plan, request);
        var token = ComputePreviewToken(revision, expectedRevision, prepared.Request!);

        return TimeSlotEditorOutcome<TimeSlotSequencePreviewDto>.Success(
            BuildPreview(request, revision, token, plan, impact));
    }

    public Task<TimeSlotEditorOutcome<TimeSlotSequenceApplyResultDto>> ApplyAsync(
        TimeSlotSequenceApplyRequestDto request,
        CancellationToken cancellationToken = default)
        => ExecuteBoundedAsync(
            token => ApplyCoreAsync(request, token),
            clearTrackedChangesOnCancellation: true,
            cancellationToken);

    private async Task<TimeSlotEditorOutcome<TimeSlotSequenceApplyResultDto>> ApplyCoreAsync(
        TimeSlotSequenceApplyRequestDto request,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            var snapshot = await LoadSnapshotAsync(cancellationToken);
            var prepared = PrepareRequest(request, snapshot);
            if (prepared.Failure is not null)
            {
                return TimeSlotEditorOutcome<TimeSlotSequenceApplyResultDto>.Fail(
                    prepared.Failure.Kind,
                    prepared.Failure.Message,
                    prepared.Failure.CurrentRevision);
            }

            var currentRevision = ComputeRevision(snapshot, request.TargetMode, request.CourseId);
            if (!TryValidatePreviewToken(
                    request.PreviewToken,
                    request.CurrentRevision,
                    prepared.Request!,
                    out var expectedResultRevision))
            {
                return TimeSlotEditorOutcome<TimeSlotSequenceApplyResultDto>.Fail(
                    TimeSlotEditorFailureKind.Stale,
                    "Попередня перевірка більше не відповідає вибраному графіку. Перевірте зміни ще раз.",
                    currentRevision);
            }

            var plan = BuildMutationPlan(snapshot, prepared.Request!);
            var plannedRevision = ComputePlannedRevision(snapshot, plan, request);
            if (!FixedEquals(request.CurrentRevision, currentRevision))
            {
                if (plan.NoChanges && FixedEquals(currentRevision, expectedResultRevision))
                {
                    await transaction.CommitAsync(cancellationToken);
                    return TimeSlotEditorOutcome<TimeSlotSequenceApplyResultDto>.Success(
                        new TimeSlotSequenceApplyResultDto
                        {
                            NoChanges = true,
                            AffectedCourseCount = plan.AffectedCourseCount,
                            PreviousRevision = currentRevision,
                            CurrentRevision = currentRevision
                        });
                }
                return TimeSlotEditorOutcome<TimeSlotSequenceApplyResultDto>.Fail(
                    TimeSlotEditorFailureKind.Stale,
                    "Графік уже змінився після попередньої перевірки. Оновіть дані та повторіть дію.",
                    currentRevision);
            }
            if (!FixedEquals(plannedRevision, expectedResultRevision))
            {
                return TimeSlotEditorOutcome<TimeSlotSequenceApplyResultDto>.Fail(
                    TimeSlotEditorFailureKind.Stale,
                    "Результат попередньої перевірки більше не відповідає поточній області графіка.",
                    currentRevision);
            }
            if (plan.NoChanges)
            {
                await transaction.CommitAsync(cancellationToken);
                return TimeSlotEditorOutcome<TimeSlotSequenceApplyResultDto>.Success(
                    new TimeSlotSequenceApplyResultDto
                    {
                        NoChanges = true,
                        AffectedCourseCount = plan.AffectedCourseCount,
                        PreviousRevision = currentRevision,
                        CurrentRevision = currentRevision
                    });
            }

            var impact = await FindImpactAsync(
                request,
                snapshot.Courses.Select(course => course.Id).ToList(),
                snapshot.Slots,
                snapshot.Lunches,
                plan.AfterSlots,
                plan.AfterLunches,
                cancellationToken);
            if (impact.TotalCount > 0)
            {
                return TimeSlotEditorOutcome<TimeSlotSequenceApplyResultDto>.Fail(
                    TimeSlotEditorFailureKind.Conflict,
                    BuildConflictMessage(impact),
                    currentRevision);
            }

            await ExecutePlanAsync(plan, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            var nextRevision = plannedRevision;
            await transaction.CommitAsync(cancellationToken);

            return TimeSlotEditorOutcome<TimeSlotSequenceApplyResultDto>.Success(
                new TimeSlotSequenceApplyResultDto
                {
                    NoChanges = false,
                    AffectedCourseCount = plan.AffectedCourseCount,
                    PreviousRevision = currentRevision,
                    CurrentRevision = nextRevision
                });
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<TimeSlotEditorOutcome<T>> ExecuteBoundedAsync<T>(
        Func<CancellationToken, Task<TimeSlotEditorOutcome<T>>> operation,
        bool clearTrackedChangesOnCancellation,
        CancellationToken cancellationToken)
    {
        IDisposable? lease = null;
        if (operationGate is not null)
        {
            lease = await operationGate.TryEnterAsync(
                ExpensiveOperationKind.TimeSlotEditorMutation,
                cancellationToken);
            if (lease is null)
            {
                return TimeSlotEditorOutcome<T>.Fail(
                    TimeSlotEditorFailureKind.Busy,
                    "Інша перевірка або зміна графіка вже виконується. Дочекайтеся її завершення та повторіть дію.");
            }
        }

        using (lease)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(operationDeadline);
            try
            {
                return await operation(deadline.Token);
            }
            catch (TimeSlotEditorCapacityException ex)
            {
                if (clearTrackedChangesOnCancellation)
                {
                    db.ChangeTracker.Clear();
                }
                return TimeSlotEditorOutcome<T>.Fail(
                    TimeSlotEditorFailureKind.Capacity,
                    ex.Message);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested
                && deadline.IsCancellationRequested)
            {
                if (clearTrackedChangesOnCancellation)
                {
                    db.ChangeTracker.Clear();
                }
                return TimeSlotEditorOutcome<T>.Fail(
                    TimeSlotEditorFailureKind.Timeout,
                    "Перевірка графіка перевищила безпечний час виконання. Зменште область змін або повторіть пізніше.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (clearTrackedChangesOnCancellation)
                {
                    db.ChangeTracker.Clear();
                }
                throw;
            }
        }
    }

    private async Task<EditorSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        var courses = await db.Courses
            .AsNoTracking()
            .OrderBy(course => course.Name)
            .ThenBy(course => course.Id)
            .Select(course => new CourseRow(course.Id, course.Name))
            .ToListAsync(cancellationToken);
        var slots = await db.TimeSlots
            .AsNoTracking()
            .OrderBy(slot => slot.CourseId)
            .ThenBy(slot => slot.DayOfWeek)
            .ThenBy(slot => slot.SortOrder)
            .ThenBy(slot => slot.Start)
            .ToListAsync(cancellationToken);
        var lunches = await db.LunchConfigs
            .AsNoTracking()
            .OrderBy(lunch => lunch.CourseId)
            .ThenBy(lunch => lunch.Id)
            .ToListAsync(cancellationToken);
        var preferred = await db.PreferredFirstSlotLimitConfigs
            .AsNoTracking()
            .OrderBy(limit => limit.CourseId)
            .ThenBy(limit => limit.Id)
            .ToListAsync(cancellationToken);
        return new EditorSnapshot(courses, slots, lunches, preferred);
    }

    private static PreparedRequest PrepareRequest(
        TimeSlotSequenceApplyRequestDto request,
        EditorSnapshot snapshot)
    {
        var requestedSlots = request.Slots ?? [];
        var targetError = ValidateTarget(request.TargetMode, request.CourseId, request.DayOfWeek);
        if (targetError is not null)
        {
            return PreparedRequest.Fail(TimeSlotEditorFailureKind.Validation, targetError);
        }
        if (string.IsNullOrWhiteSpace(request.CurrentRevision))
        {
            return PreparedRequest.Fail(
                TimeSlotEditorFailureKind.Validation,
                "Спочатку завантажте актуальну версію графіка.");
        }
        if (request.TargetMode == TimeSlotEditorTargetMode.Course
            && !snapshot.Courses.Any(course => course.Id == request.CourseId))
        {
            return PreparedRequest.Fail(TimeSlotEditorFailureKind.NotFound, "Курс не знайдено.");
        }
        if (!Enum.IsDefined(request.LunchMutation))
        {
            return PreparedRequest.Fail(
                TimeSlotEditorFailureKind.Validation,
                "Некоректний режим зміни обідньої перерви.");
        }
        if (request.DayOfWeek is not null
            && request.LunchMutation != TimeSlotLunchMutationMode.Unchanged)
        {
            return PreparedRequest.Fail(
                TimeSlotEditorFailureKind.Validation,
                "Для окремого дня обідню перерву змінювати не можна. Виберіть режим «Усі дні».");
        }
        if (!request.ApplySlots
            && (requestedSlots.Count > 0 || request.Clear || request.DayOfWeek is not null))
        {
            return PreparedRequest.Fail(
                TimeSlotEditorFailureKind.Validation,
                "Без застосування слотів список має бути порожнім, очищення вимкненим, а область — «Усі дні».");
        }
        if (request.ResetCourseToGlobal)
        {
            if (request.TargetMode != TimeSlotEditorTargetMode.Course)
            {
                return PreparedRequest.Fail(
                    TimeSlotEditorFailureKind.Validation,
                    "Повернути спільний графік можна лише для одного курсу.");
            }
            if (request.Clear
                || request.ApplySlots
                || requestedSlots.Count > 0
                || request.LunchMutation != TimeSlotLunchMutationMode.Unchanged
                || request.LunchSlot is not null)
            {
                return PreparedRequest.Fail(
                    TimeSlotEditorFailureKind.Validation,
                    "Повернення до спільного графіка не можна поєднувати з іншими змінами.");
            }
            if (request.DayOfWeek is not null)
            {
                return PreparedRequest.Fail(
                    TimeSlotEditorFailureKind.Validation,
                    "Повернути спільний графік можна лише в режимі «Усі дні».");
            }
            return PreparedRequest.Success(new NormalizedRequest(
                request.TargetMode,
                request.CourseId,
                request.DayOfWeek,
                [],
                ApplySlots: false,
                Clear: false,
                ResetCourseToGlobal: true,
                LunchMutation: TimeSlotLunchMutationMode.Unchanged,
                LunchSlot: null));
        }
        if (request.Clear && (!request.ApplySlots || requestedSlots.Count > 0))
        {
            return PreparedRequest.Fail(
                TimeSlotEditorFailureKind.Validation,
                "Очищення потребує застосування слотів і порожнього списку.");
        }
        if (request.ApplySlots && !request.Clear && requestedSlots.Count == 0)
        {
            return PreparedRequest.Fail(
                TimeSlotEditorFailureKind.Validation,
                "Порожній графік можна застосувати лише після явного підтвердження очищення.");
        }
        if (!request.ApplySlots
            && request.LunchMutation == TimeSlotLunchMutationMode.Unchanged)
        {
            return PreparedRequest.Fail(
                TimeSlotEditorFailureKind.Validation,
                "Запит не містить змін графіка або обідньої перерви.");
        }
        if (request.LunchMutation != TimeSlotLunchMutationMode.Set
            && request.LunchSlot is not null)
        {
            return PreparedRequest.Fail(
                TimeSlotEditorFailureKind.Validation,
                "Окремий слот обіду можна передавати лише в режимі встановлення обідньої перерви.");
        }
        if (request.ApplySlots && request.LunchSlot is not null)
        {
            return PreparedRequest.Fail(
                TimeSlotEditorFailureKind.Validation,
                "Окремий слот обіду дозволено передавати лише без застосування слотів.");
        }

        var normalizedSlots = new List<NormalizedSlot>();
        if (request.ApplySlots)
        {
            var validation = TimeSlotSequenceRules.Validate(requestedSlots, request.DayOfWeek);
            if (!validation.IsValid)
            {
                return PreparedRequest.Fail(
                    TimeSlotEditorFailureKind.Validation,
                    string.Join(" ", validation.Errors));
            }
            normalizedSlots = validation.Slots.Select(row => new NormalizedSlot(
                TimeOnly.ParseExact(row.Start, "HH:mm", CultureInfo.InvariantCulture),
                TimeOnly.ParseExact(row.End, "HH:mm", CultureInfo.InvariantCulture),
                row.IsActive,
                row.IsLunch,
                row.SortOrder)).ToList();
        }

        var lunchCount = normalizedSlots.Count(slot => slot.IsLunch && slot.IsActive);
        NormalizedSlot? normalizedLunch = null;
        if (request.LunchMutation == TimeSlotLunchMutationMode.Set)
        {
            if (request.ApplySlots)
            {
                if (lunchCount != 1)
                {
                    return PreparedRequest.Fail(
                        TimeSlotEditorFailureKind.Validation,
                        "Щоб змінити обідню перерву разом зі слотами, позначте рівно один активний слот як обід.");
                }
                normalizedLunch = normalizedSlots.Single(slot => slot.IsLunch && slot.IsActive);
            }
            else if (request.LunchSlot is not null)
            {
                var lunchValidation = TimeSlotSequenceRules.Validate([request.LunchSlot], dayOfWeek: null);
                if (!lunchValidation.IsValid
                    || lunchValidation.Slots.Count != 1
                    || !lunchValidation.Slots[0].IsLunch
                    || !lunchValidation.Slots[0].IsActive)
                {
                    return PreparedRequest.Fail(
                        TimeSlotEditorFailureKind.Validation,
                        "Щоб змінити обідню перерву, передайте один коректний активний слот обіду.");
                }
                var lunch = lunchValidation.Slots[0];
                normalizedLunch = new NormalizedSlot(
                    TimeOnly.ParseExact(lunch.Start, "HH:mm", CultureInfo.InvariantCulture),
                    TimeOnly.ParseExact(lunch.End, "HH:mm", CultureInfo.InvariantCulture),
                    IsActive: true,
                    IsLunch: true,
                    SortOrder: lunch.SortOrder);
            }

            if (normalizedLunch is null)
            {
                return PreparedRequest.Fail(
                    TimeSlotEditorFailureKind.Validation,
                    "Щоб змінити обідню перерву, позначте рівно один активний слот як обід.");
            }
        }
        if (request.ApplySlots
            && request.LunchMutation == TimeSlotLunchMutationMode.Remove
            && lunchCount > 0)
        {
            return PreparedRequest.Fail(
                TimeSlotEditorFailureKind.Validation,
                "Після видалення обідньої перерви жоден слот не повинен бути позначений як обід.");
        }

        return PreparedRequest.Success(new NormalizedRequest(
            request.TargetMode,
            request.CourseId,
            request.DayOfWeek,
            normalizedSlots,
            request.ApplySlots,
            request.Clear,
            ResetCourseToGlobal: false,
            LunchMutation: request.LunchMutation,
            LunchSlot: normalizedLunch));
    }

    private static string? ValidateTarget(
        TimeSlotEditorTargetMode targetMode,
        int? courseId,
        int? dayOfWeek)
    {
        if (!Enum.IsDefined(targetMode))
        {
            return "Некоректний режим застосування графіка.";
        }
        if (dayOfWeek is < 0 or > 6)
        {
            return "Некоректний день тижня.";
        }
        if (targetMode == TimeSlotEditorTargetMode.Course && courseId is not > 0)
        {
            return "Виберіть курс для застосування графіка.";
        }
        if (targetMode == TimeSlotEditorTargetMode.AllCourses && courseId is not null)
        {
            return "Для режиму «Усі курси» не потрібно передавати ідентифікатор курсу.";
        }
        return null;
    }

    private static MutationPlan BuildMutationPlan(
        EditorSnapshot snapshot,
        NormalizedRequest request)
    {
        var afterSlots = snapshot.Slots.Select(slot => CloneSlot(slot)).ToList();
        var afterLunches = snapshot.Lunches.Select(CloneLunch).ToList();
        var scopeReplacements = new List<SlotScopeReplacement>();
        var fullCourseReplacements = new List<FullCourseReplacement>();
        var lunchReplacements = new List<LunchScopeReplacement>();
        var selectedDay = request.DayOfWeek is int rawDay ? (DayOfWeek?)rawDay : null;
        var materializedCourseCount = 0;
        var courseOverridesToReplace = 0;

        if (request.ResetCourseToGlobal)
        {
            var courseId = request.CourseId!.Value;
            courseOverridesToReplace = afterSlots.Any(slot => slot.CourseId == courseId) ? 1 : 0;
            ReplaceFullCourse(afterSlots, courseId, []);
            ReplaceLunch(afterLunches, courseId, null);
            fullCourseReplacements.Add(new FullCourseReplacement(courseId, []));
            lunchReplacements.Add(new LunchScopeReplacement(courseId, null));
        }
        else if (request.TargetMode == TimeSlotEditorTargetMode.Course)
        {
            var courseId = request.CourseId!.Value;
            var hadCourseConfiguration = snapshot.Slots.Any(slot => slot.CourseId == courseId);
            if (request.ApplySlots)
            {
                courseOverridesToReplace = snapshot.Slots.Any(slot =>
                    slot.CourseId == courseId && slot.DayOfWeek == selectedDay) ? 1 : 0;
                if (!hadCourseConfiguration && request.Clear)
                {
                    // Очищення неіснуючого денного винятку не створює копію глобального графіка.
                }
                else if (!hadCourseConfiguration)
                {
                    var materialized = snapshot.Slots
                        .Where(slot => slot.CourseId == null)
                        .Select(slot => CloneSlot(slot, courseId))
                        .ToList();
                    ReplaceScope(materialized, courseId, selectedDay, ToEntities(request.Slots, courseId, selectedDay));
                    ReplaceFullCourse(afterSlots, courseId, materialized);
                    fullCourseReplacements.Add(new FullCourseReplacement(courseId, materialized));
                    materializedCourseCount = 1;
                }
                else
                {
                    var replacement = ToEntities(request.Slots, courseId, selectedDay);
                    ReplaceScope(afterSlots, courseId, selectedDay, replacement);
                    scopeReplacements.Add(new SlotScopeReplacement(courseId, selectedDay, replacement));
                }
            }

            if (request.LunchMutation != TimeSlotLunchMutationMode.Unchanged)
            {
                var lunch = request.LunchMutation == TimeSlotLunchMutationMode.Set
                    ? ToLunch(request.LunchSlot, courseId)
                    : null;
                ReplaceLunch(afterLunches, courseId, lunch);
                lunchReplacements.Add(new LunchScopeReplacement(courseId, lunch));
            }
        }
        else
        {
            var courseIds = snapshot.Courses.Select(course => course.Id).ToList();
            if (request.ApplySlots)
            {
                var globalReplacement = ToEntities(request.Slots, null, selectedDay);
                ReplaceScope(afterSlots, null, selectedDay, globalReplacement);
                scopeReplacements.Add(new SlotScopeReplacement(null, selectedDay, globalReplacement));

                foreach (var courseId in courseIds)
                {
                    var hadCourseConfiguration = snapshot.Slots.Any(slot => slot.CourseId == courseId);
                    if (hadCourseConfiguration)
                    {
                        var hasExactDayOverride = snapshot.Slots.Any(slot =>
                            slot.CourseId == courseId && slot.DayOfWeek == selectedDay);
                        if (selectedDay is not null
                            && ((request.Clear && !hasExactDayOverride)
                                || (!request.Clear
                                    && SlotsMatch(
                                        ResolveEditorRows(snapshot.Slots, courseId, selectedDay),
                                        request.Slots))))
                        {
                            continue;
                        }
                        if (hasExactDayOverride)
                        {
                            courseOverridesToReplace++;
                        }
                        var replacement = ToEntities(request.Slots, courseId, selectedDay);
                        ReplaceScope(afterSlots, courseId, selectedDay, replacement);
                        scopeReplacements.Add(new SlotScopeReplacement(courseId, selectedDay, replacement));
                    }
                }
            }

            if (request.LunchMutation != TimeSlotLunchMutationMode.Unchanged)
            {
                var globalLunch = request.LunchMutation == TimeSlotLunchMutationMode.Set
                    ? ToLunch(request.LunchSlot, null)
                    : null;
                ReplaceLunch(afterLunches, null, globalLunch);
                lunchReplacements.Add(new LunchScopeReplacement(null, globalLunch));
                foreach (var courseLunchScope in snapshot.Lunches
                             .Where(lunch => lunch.CourseId is not null)
                             .Select(lunch => lunch.CourseId!.Value)
                             .Distinct())
                {
                    ReplaceLunch(afterLunches, courseLunchScope, null);
                    lunchReplacements.Add(new LunchScopeReplacement(courseLunchScope, null));
                }
            }
        }

        var noChanges = FixedEquals(
            ComputeConfigurationFingerprint(snapshot.Slots, snapshot.Lunches),
            ComputeConfigurationFingerprint(afterSlots, afterLunches));
        return new MutationPlan(
            afterSlots,
            afterLunches,
            scopeReplacements,
            fullCourseReplacements,
            lunchReplacements,
            request.TargetMode == TimeSlotEditorTargetMode.Course ? 1 : snapshot.Courses.Count,
            courseOverridesToReplace,
            materializedCourseCount,
            noChanges);
    }

    private async Task<MutationImpact> FindImpactAsync(
        TimeSlotSequenceApplyRequestDto request,
        IReadOnlyCollection<int> courseIds,
        IReadOnlyCollection<TimeSlot> beforeSlots,
        IReadOnlyCollection<LunchConfig> beforeLunches,
        IReadOnlyCollection<TimeSlot> afterSlots,
        IReadOnlyCollection<LunchConfig> afterLunches,
        CancellationToken cancellationToken)
    {
        var invalidatedRanges = FindInvalidatedRanges(
            request,
            courseIds,
            beforeSlots,
            beforeLunches,
            afterSlots,
            afterLunches);
        if (invalidatedRanges.Count == 0)
        {
            return MutationImpact.Empty;
        }

        var scheduleQuery = db.ScheduleItems.AsNoTracking().AsQueryable();
        var draftQuery = db.TeacherDraftItems.AsNoTracking().AsQueryable();
        if (request.TargetMode == TimeSlotEditorTargetMode.Course && request.CourseId is int courseId)
        {
            scheduleQuery = scheduleQuery.Where(item => item.Group.CourseId == courseId);
            draftQuery = draftQuery.Where(item => item.Group.CourseId == courseId);
        }
        if (!request.ResetCourseToGlobal && request.DayOfWeek is int rawDay)
        {
            var day = (DayOfWeek)rawDay;
            scheduleQuery = scheduleQuery.Where(item => item.DayOfWeek == day);
            draftQuery = draftQuery.Where(item => item.DayOfWeek == day);
        }

        var orderedInvalidatedRanges = invalidatedRanges
            .OrderBy(range => range.Start)
            .ThenBy(range => range.End)
            .ToArray();
        var affected = new List<Placement>();
        foreach (var rangeBatch in orderedInvalidatedRanges.Chunk(ExactRangePredicateBatchSize))
        {
            var exactRangePredicate = BuildExactRangePredicate<ScheduleItem>(
                rangeBatch,
                nameof(ScheduleItem.StartTime),
                nameof(ScheduleItem.EndTime));
            var rows = scheduleQuery
                .Where(exactRangePredicate)
                .Select(item => new Placement(
                    "Розклад",
                    false,
                    item.Id,
                    item.Date,
                    item.StartTime,
                    item.EndTime,
                    item.Group.CourseId,
                    item.Group.Name))
                .AsAsyncEnumerable();
            await foreach (var placement in rows.WithCancellation(cancellationToken))
            {
                AddAffectedPlacement(
                    affected,
                    placement,
                    beforeSlots,
                    beforeLunches,
                    afterSlots,
                    afterLunches);
            }
        }
        foreach (var rangeBatch in orderedInvalidatedRanges.Chunk(ExactRangePredicateBatchSize))
        {
            var exactRangePredicate = BuildExactRangePredicate<TeacherDraftItem>(
                rangeBatch,
                nameof(TeacherDraftItem.StartTime),
                nameof(TeacherDraftItem.EndTime));
            var rows = draftQuery
                .Where(exactRangePredicate)
                .Select(item => new Placement(
                    "Чернетка",
                    true,
                    item.Id,
                    item.Date,
                    item.StartTime,
                    item.EndTime,
                    item.Group.CourseId,
                    item.Group.Name))
                .AsAsyncEnumerable();
            await foreach (var placement in rows.WithCancellation(cancellationToken))
            {
                AddAffectedPlacement(
                    affected,
                    placement,
                    beforeSlots,
                    beforeLunches,
                    afterSlots,
                    afterLunches);
            }
        }

        affected = affected
            .OrderBy(placement => placement.IsDraft)
            .ThenBy(placement => placement.Date)
            .ThenBy(placement => placement.Id)
            .ToList();
        return new MutationImpact(
            affected.Count(placement => !placement.IsDraft),
            affected.Count(placement => placement.IsDraft),
            affected.Take(ConflictSampleLimit).Select(placement => new TimeSlotConflictSampleDto
            {
                Source = placement.Source,
                Id = placement.Id,
                CourseId = placement.CourseId,
                GroupName = placement.GroupName,
                Date = placement.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Start = placement.Start.ToString("HH:mm", CultureInfo.InvariantCulture),
                End = placement.End.ToString("HH:mm", CultureInfo.InvariantCulture)
            }).ToList());
    }

    private static Expression<Func<TEntity, bool>> BuildExactRangePredicate<TEntity>(
        IReadOnlyList<SlotRange> ranges,
        string startPropertyName,
        string endPropertyName)
    {
        if (ranges.Count == 0)
        {
            throw new ArgumentException("Потрібна хоча б одна часова пара.", nameof(ranges));
        }

        var item = Expression.Parameter(typeof(TEntity), "item");
        var startProperty = Expression.Property(item, startPropertyName);
        var endProperty = Expression.Property(item, endPropertyName);
        var clauses = ranges
            .Select(range => (Expression)Expression.AndAlso(
                Expression.Equal(startProperty, Expression.Constant(range.Start)),
                Expression.Equal(endProperty, Expression.Constant(range.End))))
            .ToArray();
        return Expression.Lambda<Func<TEntity, bool>>(
            CombineWithBalancedOr(clauses, 0, clauses.Length),
            item);
    }

    private static Expression CombineWithBalancedOr(
        IReadOnlyList<Expression> clauses,
        int offset,
        int count)
    {
        if (count == 1)
        {
            return clauses[offset];
        }
        var leftCount = count / 2;
        return Expression.OrElse(
            CombineWithBalancedOr(clauses, offset, leftCount),
            CombineWithBalancedOr(clauses, offset + leftCount, count - leftCount));
    }

    private static void AddAffectedPlacement(
        List<Placement> affected,
        Placement placement,
        IReadOnlyCollection<TimeSlot> beforeSlots,
        IReadOnlyCollection<LunchConfig> beforeLunches,
        IReadOnlyCollection<TimeSlot> afterSlots,
        IReadOnlyCollection<LunchConfig> afterLunches)
    {
        if (!PlacementFits(placement, beforeSlots, beforeLunches)
            || PlacementFits(placement, afterSlots, afterLunches))
        {
            return;
        }
        affected.Add(placement);
        if (affected.Count > MaxImpactCount)
        {
            throw CreateImpactCapacityException();
        }
    }

    private static HashSet<SlotRange> FindInvalidatedRanges(
        TimeSlotSequenceApplyRequestDto request,
        IReadOnlyCollection<int> courseIds,
        IReadOnlyCollection<TimeSlot> beforeSlots,
        IReadOnlyCollection<LunchConfig> beforeLunches,
        IReadOnlyCollection<TimeSlot> afterSlots,
        IReadOnlyCollection<LunchConfig> afterLunches)
    {
        IEnumerable<int> relevantCourseIds = request.TargetMode == TimeSlotEditorTargetMode.Course
            ? [request.CourseId!.Value]
            : courseIds;
        IEnumerable<DayOfWeek> relevantDays =
            !request.ResetCourseToGlobal && request.DayOfWeek is int rawDay
                ? [(DayOfWeek)rawDay]
                : Enum.GetValues<DayOfWeek>();
        var invalidated = new HashSet<SlotRange>();
        foreach (var courseId in relevantCourseIds)
        {
            foreach (var day in relevantDays)
            {
                var beforeRanges = BuildAllowedRanges(
                    TimeSlotsResolver.ResolveForDay(
                            beforeSlots,
                            courseId,
                            day,
                            beforeLunches)
                        .Slots);
                var afterRanges = BuildAllowedRanges(
                    TimeSlotsResolver.ResolveForDay(
                            afterSlots,
                            courseId,
                            day,
                            afterLunches)
                        .Slots);
                beforeRanges.ExceptWith(afterRanges);
                invalidated.UnionWith(beforeRanges);
            }
        }
        return invalidated;
    }

    private static HashSet<SlotRange> BuildAllowedRanges(IEnumerable<TimeSlot> slots)
    {
        var ordered = slots
            .OrderBy(slot => slot.Start)
            .ThenBy(slot => slot.End)
            .ToList();
        var ranges = new HashSet<SlotRange>();
        for (var startIndex = 0; startIndex < ordered.Count; startIndex++)
        {
            for (var endIndex = startIndex; endIndex < ordered.Count; endIndex++)
            {
                if (endIndex > startIndex
                    && ordered[endIndex - 1].End != ordered[endIndex].Start)
                {
                    break;
                }
                ranges.Add(new SlotRange(ordered[startIndex].Start, ordered[endIndex].End));
            }
        }
        return ranges;
    }

    private static TimeSlotEditorCapacityException CreateImpactCapacityException()
        => new(
            $"Зміна графіка робить недійсними понад {MaxImpactCount} наявних занять. "
            + "Зменште область зміни та повторіть перевірку.");

    private async Task ExecutePlanAsync(MutationPlan plan, CancellationToken cancellationToken)
    {
        foreach (var full in plan.FullCourseReplacements)
        {
            await db.TimeSlots
                .Where(slot => slot.CourseId == full.CourseId)
                .ExecuteDeleteAsync(cancellationToken);
            db.TimeSlots.AddRange(full.Slots.Select(slot => CloneSlot(slot)));
        }
        foreach (var scope in plan.ScopeReplacements)
        {
            await db.TimeSlots
                .Where(slot => slot.CourseId == scope.CourseId && slot.DayOfWeek == scope.Day)
                .ExecuteDeleteAsync(cancellationToken);
            db.TimeSlots.AddRange(scope.Slots.Select(slot => CloneSlot(slot)));
        }
        foreach (var lunchMutation in plan.LunchReplacements)
        {
            await db.LunchConfigs
                .Where(lunch => lunch.CourseId == lunchMutation.CourseId)
                .ExecuteDeleteAsync(cancellationToken);
            if (lunchMutation.Lunch is not null)
            {
                db.LunchConfigs.Add(CloneLunch(lunchMutation.Lunch));
            }
        }
    }

    private static TimeSlotSequencePreviewDto BuildPreview(
        TimeSlotSequenceApplyRequestDto request,
        string revision,
        string token,
        MutationPlan plan,
        MutationImpact impact)
        => new()
        {
            TargetMode = request.TargetMode,
            CourseId = request.CourseId,
            DayOfWeek = request.DayOfWeek,
            AffectedCourseCount = plan.AffectedCourseCount,
            CourseOverridesToReplace = plan.CourseOverridesToReplace,
            MaterializedCourseCount = plan.MaterializedCourseCount,
            ScheduleConflictCount = impact.ScheduleCount,
            DraftConflictCount = impact.DraftCount,
            ConflictSamples = impact.Samples,
            NoChanges = plan.NoChanges,
            CurrentRevision = revision,
            PreviewToken = token
        };

    private static string BuildConflictMessage(MutationImpact impact)
    {
        var samples = string.Join(
            "; ",
            impact.Samples.Select(sample =>
                $"{sample.Source.ToLowerInvariant()} #{sample.Id}, {sample.GroupName}, "
                + $"{sample.Date} {sample.Start}-{sample.End}"));
        return $"Зміна графіка зробить недійсними {impact.TotalCount} наявних занять "
               + $"(розклад: {impact.ScheduleCount}, чернетки: {impact.DraftCount}). "
               + $"Спочатку перенесіть їх у дозволені слоти: {samples}";
    }

    private static bool PlacementFits(
        Placement placement,
        IReadOnlyCollection<TimeSlot> slots,
        IReadOnlyCollection<LunchConfig> lunches)
    {
        var resolved = TimeSlotsResolver.ResolveForDay(
                slots,
                placement.CourseId,
                placement.Date.DayOfWeek,
                lunches)
            .Slots;
        return resolved.Count > 0 && SlotRangeAllowed(placement.Start, placement.End, resolved);
    }

    private static bool SlotRangeAllowed(
        TimeOnly start,
        TimeOnly end,
        IReadOnlyCollection<TimeSlot> slots)
    {
        var ordered = slots.OrderBy(slot => slot.Start).ThenBy(slot => slot.End).ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            if (ordered[index].Start != start)
            {
                continue;
            }
            for (var endIndex = index; endIndex < ordered.Count; endIndex++)
            {
                if (endIndex > index && ordered[endIndex - 1].End != ordered[endIndex].Start)
                {
                    break;
                }
                if (ordered[endIndex].End == end)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static List<TimeSlot> ResolveEditorRows(
        IReadOnlyCollection<TimeSlot> slots,
        int? courseId,
        DayOfWeek? day)
    {
        if (day is DayOfWeek selectedDay)
        {
            return TimeSlotsResolver.ResolveForDay(
                    slots,
                    courseId,
                    selectedDay,
                    lunches: null,
                    activeOnly: false)
                .Slots;
        }
        if (courseId is int selectedCourse)
        {
            var courseRows = slots.Where(slot => slot.CourseId == selectedCourse).ToList();
            return courseRows.Count > 0
                ? PickScope(courseRows, selectedCourse, null)
                : PickScope(slots, null, null);
        }
        return PickScope(slots, null, null);
    }

    private static List<TimeSlot> PickScope(
        IEnumerable<TimeSlot> slots,
        int? courseId,
        DayOfWeek? day)
        => slots
            .Where(slot => slot.CourseId == courseId && slot.DayOfWeek == day)
            .OrderBy(slot => slot.SortOrder)
            .ThenBy(slot => slot.Start)
            .ToList();

    private static List<TimeSlotDto> MapSlots(
        IEnumerable<TimeSlot> slots,
        LunchConfig? lunch)
        => slots.Select(slot => new TimeSlotDto
        {
            Id = slot.Id,
            CourseId = slot.CourseId,
            DayOfWeek = slot.DayOfWeek is DayOfWeek day ? (int)day : null,
            SortOrder = slot.SortOrder,
            Start = slot.Start.ToString("HH:mm", CultureInfo.InvariantCulture),
            End = slot.End.ToString("HH:mm", CultureInfo.InvariantCulture),
            IsActive = slot.IsActive,
            IsLunch = lunch is not null
                      && slot.IsActive
                      && slot.Start == lunch.Start
                      && slot.End == lunch.End
        }).ToList();

    private static LunchConfigEditDto? MapLunch(LunchConfig? lunch)
        => lunch is null
            ? null
            : new LunchConfigEditDto(
                lunch.Id,
                lunch.CourseId,
                lunch.Start.ToString("HH:mm", CultureInfo.InvariantCulture),
                lunch.End.ToString("HH:mm", CultureInfo.InvariantCulture));

    private static List<TimeSlot> ToEntities(
        IEnumerable<NormalizedSlot> slots,
        int? courseId,
        DayOfWeek? day)
        => slots.Select(slot => new TimeSlot
        {
            CourseId = courseId,
            DayOfWeek = day,
            Start = slot.Start,
            End = slot.End,
            SortOrder = slot.SortOrder,
            IsActive = slot.IsActive
        }).ToList();

    private static bool SlotsMatch(
        IEnumerable<TimeSlot> current,
        IReadOnlyList<NormalizedSlot> requested)
    {
        var rows = current
            .OrderBy(slot => slot.SortOrder)
            .ThenBy(slot => slot.Start)
            .ToList();
        if (rows.Count != requested.Count)
        {
            return false;
        }
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index].Start != requested[index].Start
                || rows[index].End != requested[index].End
                || rows[index].IsActive != requested[index].IsActive)
            {
                return false;
            }
        }
        return true;
    }

    private static LunchConfig? ToLunch(NormalizedSlot? lunch, int? courseId)
    {
        return lunch is null
            ? null
            : new LunchConfig
            {
                CourseId = courseId,
                Start = lunch.Start,
                End = lunch.End
            };
    }

    private static void ReplaceScope(
        List<TimeSlot> state,
        int? courseId,
        DayOfWeek? day,
        IEnumerable<TimeSlot> replacement)
    {
        state.RemoveAll(slot => slot.CourseId == courseId && slot.DayOfWeek == day);
        state.AddRange(replacement.Select(slot => CloneSlot(slot)));
    }

    private static void ReplaceFullCourse(
        List<TimeSlot> state,
        int courseId,
        IEnumerable<TimeSlot> replacement)
    {
        state.RemoveAll(slot => slot.CourseId == courseId);
        state.AddRange(replacement.Select(slot => CloneSlot(slot)));
    }

    private static void ReplaceLunch(
        List<LunchConfig> state,
        int? courseId,
        LunchConfig? replacement)
    {
        state.RemoveAll(lunch => lunch.CourseId == courseId);
        if (replacement is not null)
        {
            state.Add(CloneLunch(replacement));
        }
    }

    private static TimeSlot CloneSlot(TimeSlot slot)
        => CloneSlot(slot, slot.CourseId);

    private static TimeSlot CloneSlot(TimeSlot slot, int? courseId)
        => new()
        {
            Id = slot.CourseId == courseId ? slot.Id : 0,
            CourseId = courseId,
            DayOfWeek = slot.DayOfWeek,
            Start = slot.Start,
            End = slot.End,
            SortOrder = slot.SortOrder,
            IsActive = slot.IsActive
        };

    private static LunchConfig CloneLunch(LunchConfig lunch)
        => new()
        {
            Id = lunch.Id,
            CourseId = lunch.CourseId,
            Start = lunch.Start,
            End = lunch.End
        };

    private static string ComputeRevision(
        EditorSnapshot snapshot,
        TimeSlotEditorTargetMode targetMode,
        int? courseId)
    {
        var builder = new StringBuilder("timeslot-editor-v2\n");
        var relevantCourses = targetMode == TimeSlotEditorTargetMode.AllCourses
            ? snapshot.Courses
            : snapshot.Courses.Where(course => course.Id == courseId);
        foreach (var course in relevantCourses.OrderBy(course => course.Id))
        {
            AppendPart(builder, "course", course.Id.ToString(CultureInfo.InvariantCulture));
        }
        var relevantSlots = targetMode == TimeSlotEditorTargetMode.AllCourses
            ? snapshot.Slots
            : snapshot.Slots.Where(slot => slot.CourseId == null || slot.CourseId == courseId);
        var relevantLunches = targetMode == TimeSlotEditorTargetMode.AllCourses
            ? snapshot.Lunches
            : snapshot.Lunches.Where(lunch => lunch.CourseId == null || lunch.CourseId == courseId);
        AppendConfiguration(builder, relevantSlots, relevantLunches);
        return Hash(builder.ToString());
    }

    private static string ComputeConfigurationFingerprint(
        IEnumerable<TimeSlot> slots,
        IEnumerable<LunchConfig> lunches)
    {
        var builder = new StringBuilder("timeslot-config-v2\n");
        AppendConfiguration(builder, slots, lunches);
        return Hash(builder.ToString());
    }

    private static string ComputePlannedRevision(
        EditorSnapshot snapshot,
        MutationPlan plan,
        TimeSlotSequenceApplyRequestDto request)
        => ComputeRevision(
            snapshot with
            {
                Slots = plan.AfterSlots,
                Lunches = plan.AfterLunches
            },
            request.TargetMode,
            request.CourseId);

    private static void AppendConfiguration(
        StringBuilder builder,
        IEnumerable<TimeSlot> slots,
        IEnumerable<LunchConfig> lunches)
    {
        foreach (var slot in slots
                     .OrderBy(slot => slot.CourseId.HasValue ? 1 : 0)
                     .ThenBy(slot => slot.CourseId)
                     .ThenBy(slot => slot.DayOfWeek.HasValue ? 1 : 0)
                     .ThenBy(slot => slot.DayOfWeek)
                     .ThenBy(slot => slot.SortOrder)
                     .ThenBy(slot => slot.Start)
                     .ThenBy(slot => slot.End)
                     .ThenBy(slot => slot.IsActive))
        {
            AppendPart(builder, "slot-scope", Scope(slot.CourseId));
            AppendPart(builder, "slot-day", slot.DayOfWeek is DayOfWeek day
                ? ((int)day).ToString(CultureInfo.InvariantCulture)
                : "*");
            AppendPart(builder, "slot-order", slot.SortOrder.ToString(CultureInfo.InvariantCulture));
            AppendPart(builder, "slot-start-ticks", slot.Start.Ticks.ToString(CultureInfo.InvariantCulture));
            AppendPart(builder, "slot-end-ticks", slot.End.Ticks.ToString(CultureInfo.InvariantCulture));
            AppendPart(builder, "slot-active", slot.IsActive ? "1" : "0");
        }
        foreach (var lunch in lunches
                     .OrderBy(lunch => lunch.CourseId.HasValue ? 1 : 0)
                     .ThenBy(lunch => lunch.CourseId)
                     .ThenBy(lunch => lunch.Start)
                     .ThenBy(lunch => lunch.End))
        {
            AppendPart(builder, "lunch-scope", Scope(lunch.CourseId));
            AppendPart(builder, "lunch-start-ticks", lunch.Start.Ticks.ToString(CultureInfo.InvariantCulture));
            AppendPart(builder, "lunch-end-ticks", lunch.End.Ticks.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static string ComputePreviewToken(
        string revision,
        string expectedResultRevision,
        NormalizedRequest request)
        => $"{expectedResultRevision}.{ComputePreviewSignature(revision, expectedResultRevision, request)}";

    private static bool TryValidatePreviewToken(
        string? token,
        string revision,
        NormalizedRequest request,
        out string expectedResultRevision)
    {
        expectedResultRevision = string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }
        var separator = token.IndexOf('.', StringComparison.Ordinal);
        if (separator <= 0 || separator == token.Length - 1)
        {
            return false;
        }
        expectedResultRevision = token[..separator];
        var suppliedSignature = token[(separator + 1)..];
        var expectedSignature = ComputePreviewSignature(revision, expectedResultRevision, request);
        return FixedEquals(suppliedSignature, expectedSignature);
    }

    private static string ComputePreviewSignature(
        string revision,
        string expectedResultRevision,
        NormalizedRequest request)
    {
        var builder = new StringBuilder("timeslot-preview-v2\n");
        AppendPart(builder, "revision", revision);
        AppendPart(builder, "expected-result-revision", expectedResultRevision);
        AppendPart(builder, "target", request.TargetMode.ToString());
        AppendPart(builder, "course", Scope(request.CourseId));
        AppendPart(builder, "day", request.DayOfWeek?.ToString(CultureInfo.InvariantCulture) ?? "*");
        AppendPart(builder, "apply-slots", request.ApplySlots ? "1" : "0");
        AppendPart(builder, "clear", request.Clear ? "1" : "0");
        AppendPart(builder, "inherit", request.ResetCourseToGlobal ? "1" : "0");
        AppendPart(builder, "lunch-mutation", request.LunchMutation.ToString());
        foreach (var slot in request.Slots)
        {
            AppendPart(builder, "slot-start", slot.Start.ToString("HH:mm", CultureInfo.InvariantCulture));
            AppendPart(builder, "slot-end", slot.End.ToString("HH:mm", CultureInfo.InvariantCulture));
            AppendPart(builder, "slot-active", slot.IsActive ? "1" : "0");
            AppendPart(builder, "slot-lunch", slot.IsLunch ? "1" : "0");
            AppendPart(builder, "slot-order", slot.SortOrder.ToString(CultureInfo.InvariantCulture));
        }
        if (request.LunchSlot is not null)
        {
            AppendPart(builder, "lunch-start", request.LunchSlot.Start.ToString("HH:mm", CultureInfo.InvariantCulture));
            AppendPart(builder, "lunch-end", request.LunchSlot.End.ToString("HH:mm", CultureInfo.InvariantCulture));
        }
        return Hash(builder.ToString());
    }

    private static void AppendPart(StringBuilder builder, string key, string value)
        => builder.Append(key)
            .Append(':')
            .Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('\n');

    private static string Scope(int? courseId)
        => courseId?.ToString(CultureInfo.InvariantCulture) ?? "*";

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool FixedEquals(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return false;
        }
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
               && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private sealed record CourseRow(int Id, string Name);

    private sealed record EditorSnapshot(
        List<CourseRow> Courses,
        List<TimeSlot> Slots,
        List<LunchConfig> Lunches,
        List<PreferredFirstSlotLimitConfig> PreferredFirstLimits);

    private sealed record NormalizedSlot(
        TimeOnly Start,
        TimeOnly End,
        bool IsActive,
        bool IsLunch,
        int SortOrder);

    private sealed record NormalizedRequest(
        TimeSlotEditorTargetMode TargetMode,
        int? CourseId,
        int? DayOfWeek,
        List<NormalizedSlot> Slots,
        bool ApplySlots,
        bool Clear,
        bool ResetCourseToGlobal,
        TimeSlotLunchMutationMode LunchMutation,
        NormalizedSlot? LunchSlot);

    private sealed record PreparedRequest(NormalizedRequest? Request, TimeSlotEditorFailure? Failure)
    {
        public static PreparedRequest Success(NormalizedRequest request) => new(request, null);

        public static PreparedRequest Fail(TimeSlotEditorFailureKind kind, string message)
            => new(null, new TimeSlotEditorFailure(kind, message));
    }

    private sealed record SlotScopeReplacement(
        int? CourseId,
        DayOfWeek? Day,
        List<TimeSlot> Slots);

    private sealed record FullCourseReplacement(int CourseId, List<TimeSlot> Slots);

    private sealed record LunchScopeReplacement(int? CourseId, LunchConfig? Lunch);

    private sealed record MutationPlan(
        List<TimeSlot> AfterSlots,
        List<LunchConfig> AfterLunches,
        List<SlotScopeReplacement> ScopeReplacements,
        List<FullCourseReplacement> FullCourseReplacements,
        List<LunchScopeReplacement> LunchReplacements,
        int AffectedCourseCount,
        int CourseOverridesToReplace,
        int MaterializedCourseCount,
        bool NoChanges);

    private sealed record Placement(
        string Source,
        bool IsDraft,
        int Id,
        DateOnly Date,
        TimeOnly Start,
        TimeOnly End,
        int CourseId,
        string GroupName);

    private sealed record SlotRange(TimeOnly Start, TimeOnly End);

    private sealed class TimeSlotEditorCapacityException(string message) : Exception(message);

    private sealed record MutationImpact(
        int ScheduleCount,
        int DraftCount,
        List<TimeSlotConflictSampleDto> Samples)
    {
        public static MutationImpact Empty { get; } = new(0, 0, []);

        public int TotalCount => ScheduleCount + DraftCount;
    }
}
