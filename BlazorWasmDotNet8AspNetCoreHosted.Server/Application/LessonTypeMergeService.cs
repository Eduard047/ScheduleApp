using System.Buffers;
using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application;

public sealed record LessonTypeMergeResult(
    int SourceTypeId,
    int TargetTypeId,
    int ModuleTopicsUpdated,
    int TeacherDraftsUpdated,
    int ScheduleItemsUpdated,
    int RescheduleKeysUpdated,
    int PlanSnapshotsUpdated,
    int JobPayloadsUpdated);

internal sealed record LessonTypeMergeWorkload(
    int SourceTypeId,
    string SourceCode,
    string SourceName,
    int? TargetTypeId,
    string TargetCode,
    string TargetName,
    long EstimatedDatabaseOperations,
    int HistoricalMutationCount,
    int HistoricalJobCount,
    long HistoricalJsonCharacters,
    long EstimatedRewrittenJsonCharacters);

public sealed class LessonTypeMergeException(string message) : Exception(message);

// Атомарно об'єднує помилковий тип заняття з канонічним без зміни розкладу в часі.
public static class LessonTypeMergeService
{
    internal const int MaxHistoricalJsonRowCount = 12_000;
    internal const int MaxSingleHistoricalJsonCharacters = 2_000_000;
    internal const long MaxHistoricalJsonCharacters = 32_000_000;
    internal const int MaxActivePlanCount = 100;
    internal const long MaxEstimatedDatabaseOperationCount = 50_000;
    private const int FixedMergeOperationCount = 16;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    // Виконує ту саму перевірку сумісності, що й об'єднання, але не змінює дані.
    public static async Task ValidateMergeAsync(
        AppDbContext db,
        int sourceTypeId,
        int targetTypeId,
        CancellationToken cancellationToken = default)
        => _ = await ValidateMergeWorkloadAsync(
            db,
            sourceTypeId,
            targetTypeId,
            cancellationToken);

    internal static async Task<LessonTypeMergeWorkload> ValidateMergeWorkloadAsync(
        AppDbContext db,
        int sourceTypeId,
        int targetTypeId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifiers(sourceTypeId, targetTypeId);
        var lessonTypes = await db.LessonTypes
            .AsNoTracking()
            .Where(type => type.Id == sourceTypeId || type.Id == targetTypeId)
            .OrderBy(type => type.Id)
            .ToListAsync(cancellationToken);
        var (source, target) = ResolveAndValidateTypes(lessonTypes, sourceTypeId, targetTypeId);
        return await ValidateWorkloadAndLifecycleAsync(db, source, target, cancellationToken);
    }

    // Перевіряє об'єднання з ще не збереженим канонічним типом без додавання його до контексту.
    public static async Task ValidateMergeToNewTargetAsync(
        AppDbContext db,
        int sourceTypeId,
        LessonTypeRef targetPrototype,
        CancellationToken cancellationToken = default)
        => _ = await ValidateMergeToNewTargetWorkloadAsync(
            db,
            sourceTypeId,
            targetPrototype,
            cancellationToken);

    internal static async Task<LessonTypeMergeWorkload> ValidateMergeToNewTargetWorkloadAsync(
        AppDbContext db,
        int sourceTypeId,
        LessonTypeRef targetPrototype,
        CancellationToken cancellationToken = default)
    {
        if (sourceTypeId <= 0 || targetPrototype is null)
        {
            throw new LessonTypeMergeException("Вкажіть чинний вихідний і новий канонічний типи занять.");
        }
        var source = await db.LessonTypes
            .AsNoTracking()
            .SingleOrDefaultAsync(type => type.Id == sourceTypeId, cancellationToken)
            ?? throw new LessonTypeMergeException($"Тип заняття #{sourceTypeId} не знайдено.");
        ValidateTargetCompatibility(source, targetPrototype);
        return await ValidateWorkloadAndLifecycleAsync(db, source, targetPrototype, cancellationToken);
    }

    public static Task<LessonTypeMergeResult> MergeAsync(
        AppDbContext db,
        int sourceTypeId,
        int targetTypeId,
        CancellationToken cancellationToken = default)
        => MergeCoreAsync(
            db,
            sourceTypeId,
            targetTypeId,
            validatedWorkload: null,
            cancellationToken);

    internal static Task<LessonTypeMergeResult> MergeValidatedAsync(
        AppDbContext db,
        int sourceTypeId,
        int targetTypeId,
        LessonTypeMergeWorkload validatedWorkload,
        CancellationToken cancellationToken = default)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Попередньо перевірене об'єднання типів занять дозволене лише всередині транзакції.");
        }
        if (validatedWorkload.SourceTypeId != sourceTypeId)
        {
            throw new InvalidOperationException(
                "Попередня перевірка об'єднання не відповідає вихідному типу заняття.");
        }
        return MergeCoreAsync(
            db,
            sourceTypeId,
            targetTypeId,
            validatedWorkload,
            cancellationToken);
    }

    private static async Task<LessonTypeMergeResult> MergeCoreAsync(
        AppDbContext db,
        int sourceTypeId,
        int targetTypeId,
        LessonTypeMergeWorkload? validatedWorkload,
        CancellationToken cancellationToken)
    {
        ValidateIdentifiers(sourceTypeId, targetTypeId);

        // Імпорт DOCX уже виконується у власній транзакції; окремий адмін-виклик створює її тут.
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;
        var committed = false;
        try
        {
            var lessonTypes = await db.LessonTypes
                .Where(type => type.Id == sourceTypeId || type.Id == targetTypeId)
                .OrderBy(type => type.Id)
                .ToListAsync(cancellationToken);
            var (source, target) = ResolveAndValidateTypes(
                lessonTypes,
                sourceTypeId,
                targetTypeId);

            if (validatedWorkload is null)
            {
                validatedWorkload = await ValidateWorkloadAndLifecycleAsync(
                    db,
                    source,
                    target,
                    cancellationToken);
            }
            else
            {
                // Квиток DOCX сформовано в зовнішній транзакції, але спільна межа
                // об'єднання все одно має повторно закрити гонку з автогенерацією.
                _ = await EnsureNoAutogenExecutionInProgressAsync(db, cancellationToken);
                ValidateWorkloadTicket(validatedWorkload, source, target);
            }

            var sourceDrafts = await db.TeacherDraftItems
                .Where(item => item.LessonTypeId == sourceTypeId)
                .ToListAsync(cancellationToken);

            var moduleTopics = await db.ModuleTopics
                .Where(topic => topic.LessonTypeId == sourceTypeId)
                .ToListAsync(cancellationToken);
            var sourceScheduleItems = await db.ScheduleItems
                .Where(item => item.LessonTypeId == sourceTypeId)
                .ToListAsync(cancellationToken);

            foreach (var topic in moduleTopics)
            {
                cancellationToken.ThrowIfCancellationRequested();
                topic.LessonTypeId = targetTypeId;
                topic.LessonType = target;
            }
            foreach (var draft in sourceDrafts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                draft.LessonTypeId = targetTypeId;
                draft.LessonType = target;
            }
            foreach (var scheduleItem in sourceScheduleItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scheduleItem.LessonTypeId = targetTypeId;
                scheduleItem.LessonType = target;
            }

            var moduleTopicsUpdated = moduleTopics.Count;
            var teacherDraftsUpdated = sourceDrafts.Count;
            var scheduleItemsUpdated = sourceScheduleItems.Count;

            var rescheduleKeysUpdated = 0;
            var sourceSuffix = $":{sourceTypeId}";
            var targetSuffix = $":{targetTypeId}";
            var draftKeys = await db.TeacherDraftItems
                .Where(item => item.BatchKey != null
                               && item.BatchKey.StartsWith("rescheduled:")
                               && item.BatchKey.EndsWith(sourceSuffix))
                .ToListAsync(cancellationToken);
            foreach (var item in draftKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                item.BatchKey = ReplaceSuffix(item.BatchKey!, sourceSuffix, targetSuffix);
                rescheduleKeysUpdated++;
            }
            var scheduleKeys = await db.ScheduleItems
                .Where(item => item.BatchKey != null
                               && item.BatchKey.StartsWith("rescheduled:")
                               && item.BatchKey.EndsWith(sourceSuffix))
                .ToListAsync(cancellationToken);
            foreach (var item in scheduleKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                item.BatchKey = ReplaceSuffix(item.BatchKey!, sourceSuffix, targetSuffix);
                rescheduleKeysUpdated++;
            }

            // Після bounded-preflight читаємо всі історичні payload-и: відбір за підрядком
            // залежить від collation провайдера і може пропустити case-insensitive посилання.
            var rewriteBudget = new HistoricalJsonRewriteBudget();
            var mutations = await db.AutoGenDraftPlanMutations
                .OrderBy(item => item.Id)
                .ToListAsync(cancellationToken);
            var planSnapshotsUpdated = 0;
            foreach (var mutation in mutations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var before = RewriteJson(
                    mutation.BeforeJson,
                    source,
                    target,
                    out var beforeChanged,
                    cancellationToken,
                    rewriteBudget);
                var after = RewriteJson(
                    mutation.AfterJson,
                    source,
                    target,
                    out var afterChanged,
                    cancellationToken,
                    rewriteBudget);
                if (!beforeChanged && !afterChanged)
                {
                    continue;
                }
                mutation.BeforeJson = before;
                mutation.AfterJson = after;
                planSnapshotsUpdated++;
            }

            var jobs = await db.AutoGenJobRuns
                .OrderBy(job => job.Id)
                .ToListAsync(cancellationToken);
            var jobPayloadsUpdated = 0;
            foreach (var job in jobs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                job.RequestJson = RewriteJson(
                    job.RequestJson,
                    source,
                    target,
                    out var requestChanged,
                    cancellationToken,
                    rewriteBudget)!;
                job.StatusJson = RewriteJson(
                    job.StatusJson,
                    source,
                    target,
                    out var statusChanged,
                    cancellationToken,
                    rewriteBudget)!;
                job.ResultJson = RewriteJson(
                    job.ResultJson,
                    source,
                    target,
                    out var resultChanged,
                    cancellationToken,
                    rewriteBudget);
                job.ReportJson = RewriteJson(
                    job.ReportJson,
                    source,
                    target,
                    out var reportChanged,
                    cancellationToken,
                    rewriteBudget);
                var changed = requestChanged || statusChanged || resultChanged || reportChanged;
                if (changed)
                {
                    jobPayloadsUpdated++;
                }
            }

            await db.SaveChangesAsync(cancellationToken);

            var sourceStillUsed = await db.ModuleTopics.AnyAsync(topic => topic.LessonTypeId == sourceTypeId, cancellationToken)
                || await db.TeacherDraftItems.AnyAsync(item => item.LessonTypeId == sourceTypeId, cancellationToken)
                || await db.ScheduleItems.AnyAsync(item => item.LessonTypeId == sourceTypeId, cancellationToken);
            if (sourceStillUsed)
            {
                throw new LessonTypeMergeException("Не всі посилання на помилковий тип заняття вдалося перенести.");
            }

            db.LessonTypes.Remove(source);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await transaction.CommitAsync(CancellationToken.None);
                committed = true;
            }
            return new LessonTypeMergeResult(
                sourceTypeId,
                targetTypeId,
                moduleTopicsUpdated,
                teacherDraftsUpdated,
                scheduleItemsUpdated,
                rescheduleKeysUpdated,
                planSnapshotsUpdated,
                jobPayloadsUpdated);
        }
        catch
        {
            if (transaction is not null && !committed)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            throw;
        }
    }

    private static void ValidateIdentifiers(int sourceTypeId, int targetTypeId)
    {
        if (sourceTypeId <= 0 || targetTypeId <= 0 || sourceTypeId == targetTypeId)
        {
            throw new LessonTypeMergeException("Вкажіть два різні чинні типи занять.");
        }
    }

    private static (LessonTypeRef Source, LessonTypeRef Target) ResolveAndValidateTypes(
        IReadOnlyCollection<LessonTypeRef> lessonTypes,
        int sourceTypeId,
        int targetTypeId)
    {
        var source = lessonTypes.SingleOrDefault(type => type.Id == sourceTypeId)
            ?? throw new LessonTypeMergeException($"Тип заняття #{sourceTypeId} не знайдено.");
        var target = lessonTypes.SingleOrDefault(type => type.Id == targetTypeId)
            ?? throw new LessonTypeMergeException($"Тип заняття #{targetTypeId} не знайдено.");
        ValidateTargetCompatibility(source, target);
        return (source, target);
    }

    private static void ValidateTargetCompatibility(LessonTypeRef source, LessonTypeRef target)
    {
        if (!target.IsActive)
        {
            throw new LessonTypeMergeException("Канонічний тип заняття має бути активним.");
        }
        if (!HasSamePlacementSemantics(source, target))
        {
            throw new LessonTypeMergeException(
                "Типи занять мають різні правила аудиторії, викладача або обліку годин, тому автоматичне об'єднання небезпечне.");
        }
    }

    private static bool HasSamePlacementSemantics(LessonTypeRef source, LessonTypeRef target)
        => source.RequiresRoom == target.RequiresRoom
           && source.RequiresTeacher == target.RequiresTeacher
           && source.BlocksRoom == target.BlocksRoom
           && source.BlocksTeacher == target.BlocksTeacher
           && source.CountInPlan == target.CountInPlan
           && source.CountInLoad == target.CountInLoad
           && source.PreferredFirstInWeek == target.PreferredFirstInWeek;

    private static void ValidateWorkloadTicket(
        LessonTypeMergeWorkload workload,
        LessonTypeRef source,
        LessonTypeRef target)
    {
        if (workload.SourceTypeId != source.Id
            || !string.Equals(workload.SourceCode, source.Code, StringComparison.Ordinal)
            || !string.Equals(workload.SourceName, source.Name, StringComparison.Ordinal)
            || workload.TargetTypeId is int targetTypeId && targetTypeId != target.Id
            || !string.Equals(workload.TargetCode, target.Code, StringComparison.Ordinal)
            || !string.Equals(workload.TargetName, target.Name, StringComparison.Ordinal))
        {
            throw new LessonTypeMergeException(
                "Попередня перевірка об'єднання не відповідає поточним типам занять.");
        }
    }

    private static async Task<LessonTypeMergeWorkload> ValidateWorkloadAndLifecycleAsync(
        AppDbContext db,
        LessonTypeRef source,
        LessonTypeRef target,
        CancellationToken cancellationToken)
    {
        var historicalJobCount = await EnsureNoAutogenExecutionInProgressAsync(
            db,
            cancellationToken);
        var workload = await CalculateWorkloadAsync(
            db,
            source,
            target,
            historicalJobCount,
            cancellationToken);
        await EnsureSourceLifecycleAllowsMergeAsync(db, source.Id, cancellationToken);
        return workload;
    }

    // Легка перевірка станів перед читанням планів і longtext одночасно блокує
    // на час serializable-транзакції видалення старих запусків та додавання нового.
    private static async Task<int> EnsureNoAutogenExecutionInProgressAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var jobs = await db.AutoGenJobRuns
            .AsNoTracking()
            .OrderBy(job => job.Id)
            .Select(job => new
            {
                job.Id,
                job.JobId,
                job.State,
                job.OwnerInstanceId,
                job.Attempt,
                job.LeaseExpiresAtUtc
            })
            .Take(MaxHistoricalJsonRowCount + 1)
            .ToListAsync(cancellationToken);
        if (jobs.Count > MaxHistoricalJsonRowCount)
        {
            throw new LessonTypeMergeException(
                $"Об'єднання потребує перевірки понад {MaxHistoricalJsonRowCount} історичних записів автогенерації, що перевищує безпечний ліміт.");
        }

        var blockingJobs = jobs
            .Where(job => job.State is (int)AutoGenJobState.Queued or (int)AutoGenJobState.Running)
            .Take(6)
            .ToList();
        if (blockingJobs.Count == 0)
        {
            return jobs.Count;
        }

        var shownJobs = blockingJobs
            .Take(5)
            .Select(job => string.IsNullOrWhiteSpace(job.JobId) ? $"#{job.Id}" : job.JobId)
            .ToList();
        var suffix = blockingJobs.Count > shownJobs.Count ? " Є й інші незавершені завдання." : string.Empty;
        var retryInstruction = blockingJobs.Any(job => string.IsNullOrWhiteSpace(job.OwnerInstanceId)
                                                       || job.Attempt <= 0
                                                       || job.LeaseExpiresAtUtc is null)
            ? "Для запису попередньої версії без lease спочатку безпечно завершіть оновлення стану автогенерації, а потім повторіть спробу."
            : "Оновіть статус, дочекайтеся завершення або скасуйте завдання та повторіть спробу.";
        throw new LessonTypeMergeException(
            $"Об'єднання типів занять тимчасово недоступне, доки завдання автогенерації виконується або очікує в черзі: {string.Join(", ", shownJobs)}.{suffix} " +
            retryInstruction);
    }

    // Спочатку рахує потенційний fan-out без завантаження longtext, щоб один тип не міг
    // спричинити необмежене матеріалізування або синхронний обхід історичних JSON.
    private static async Task<LessonTypeMergeWorkload> CalculateWorkloadAsync(
        AppDbContext db,
        LessonTypeRef source,
        LessonTypeRef target,
        int jobCount,
        CancellationToken cancellationToken)
    {
        var sourceTypeId = source.Id;
        cancellationToken.ThrowIfCancellationRequested();
        var mutationCount = await db.AutoGenDraftPlanMutations
            .AsNoTracking()
            .CountAsync(cancellationToken);
        if ((long)mutationCount + jobCount > MaxHistoricalJsonRowCount)
        {
            throw new LessonTypeMergeException(
                $"Об'єднання потребує перевірки {mutationCount + (long)jobCount} історичних записів автогенерації, що перевищує безпечний ліміт {MaxHistoricalJsonRowCount}.");
        }

        var nowUtc = DateTime.UtcNow;
        var activePlanCount = await db.AutoGenDraftPlans
            .AsNoTracking()
            .CountAsync(
                plan => (plan.State == (int)AutoGenPlanState.Ready
                         || plan.State == (int)AutoGenPlanState.Applied)
                        && plan.ExpiresAtUtc > nowUtc,
                cancellationToken);
        if (activePlanCount > MaxActivePlanCount)
        {
            throw new LessonTypeMergeException(
                $"Об'єднання потребує перевірки {activePlanCount} активних планів автогенерації, що перевищує безпечний ліміт {MaxActivePlanCount}.");
        }

        var moduleTopicCount = await db.ModuleTopics
            .AsNoTracking()
            .CountAsync(topic => topic.LessonTypeId == sourceTypeId, cancellationToken);
        var sourceDraftCount = await db.TeacherDraftItems
            .AsNoTracking()
            .CountAsync(item => item.LessonTypeId == sourceTypeId, cancellationToken);
        var sourceScheduleItemCount = await db.ScheduleItems
            .AsNoTracking()
            .CountAsync(item => item.LessonTypeId == sourceTypeId, cancellationToken);
        var sourceSuffix = $":{sourceTypeId}";
        var draftRescheduleKeyCount = await db.TeacherDraftItems
            .AsNoTracking()
            .CountAsync(
                item => item.BatchKey != null
                        && item.BatchKey.StartsWith("rescheduled:")
                        && item.BatchKey.EndsWith(sourceSuffix),
                cancellationToken);
        var scheduleRescheduleKeyCount = await db.ScheduleItems
            .AsNoTracking()
            .CountAsync(
                item => item.BatchKey != null
                        && item.BatchKey.StartsWith("rescheduled:")
                        && item.BatchKey.EndsWith(sourceSuffix),
                cancellationToken);

        // Кожен JSON-поле враховує читання та можливе переписування/збереження.
        var estimatedDatabaseOperations = FixedMergeOperationCount
                                          + (long)moduleTopicCount
                                          + sourceDraftCount
                                          + sourceScheduleItemCount
                                          + draftRescheduleKeyCount
                                          + scheduleRescheduleKeyCount
                                          + activePlanCount
                                          + (long)mutationCount * 4L
                                          + (long)jobCount * 8L;
        if (estimatedDatabaseOperations > MaxEstimatedDatabaseOperationCount)
        {
            throw new LessonTypeMergeException(
                $"Об'єднання потребує орієнтовно {estimatedDatabaseOperations} операцій із базою даних, що перевищує безпечний ліміт {MaxEstimatedDatabaseOperationCount}.");
        }

        var historicalJsonCharacters = 0L;
        var mutationPayloadSizes = await db.AutoGenDraftPlanMutations
            .AsNoTracking()
            .OrderBy(item => item.Id)
            .Select(item => new
            {
                Before = item.BeforeJson == null ? 0 : item.BeforeJson.Length,
                After = item.AfterJson == null ? 0 : item.AfterJson.Length
            })
            .ToListAsync(cancellationToken);
        foreach (var payload in mutationPayloadSizes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddHistoricalPayloadSize(payload.Before, ref historicalJsonCharacters);
            AddHistoricalPayloadSize(payload.After, ref historicalJsonCharacters);
        }

        var jobPayloadSizes = await db.AutoGenJobRuns
            .AsNoTracking()
            .OrderBy(job => job.Id)
            .Select(job => new
            {
                Request = job.RequestJson.Length,
                Status = job.StatusJson.Length,
                Result = job.ResultJson == null ? 0 : job.ResultJson.Length,
                Report = job.ReportJson == null ? 0 : job.ReportJson.Length
            })
            .ToListAsync(cancellationToken);
        foreach (var payload in jobPayloadSizes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddHistoricalPayloadSize(payload.Request, ref historicalJsonCharacters);
            AddHistoricalPayloadSize(payload.Status, ref historicalJsonCharacters);
            AddHistoricalPayloadSize(payload.Result, ref historicalJsonCharacters);
            AddHistoricalPayloadSize(payload.Report, ref historicalJsonCharacters);
        }

        var activePlanGroupPayloadSizes = await db.AutoGenDraftPlans
            .AsNoTracking()
            .Where(plan => (plan.State == (int)AutoGenPlanState.Ready
                            || plan.State == (int)AutoGenPlanState.Applied)
                           && plan.ExpiresAtUtc > nowUtc)
            .OrderBy(plan => plan.Id)
            .Select(plan => plan.GroupIdsJson.Length)
            .ToListAsync(cancellationToken);
        foreach (var payloadSize in activePlanGroupPayloadSizes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddHistoricalPayloadSize(payloadSize, ref historicalJsonCharacters);
        }

        var rewriteTarget = target.Id > 0
            ? target
            : new LessonTypeRef
            {
                Id = int.MaxValue,
                Code = target.Code,
                Name = target.Name
            };
        var estimatedRewrittenJsonCharacters = await ValidateHistoricalJsonSyntaxAsync(
            db,
            source,
            rewriteTarget,
            cancellationToken);

        return new LessonTypeMergeWorkload(
            sourceTypeId,
            source.Code,
            source.Name,
            target.Id > 0 ? target.Id : null,
            target.Code,
            target.Name,
            estimatedDatabaseOperations,
            mutationCount,
            jobCount,
            historicalJsonCharacters,
            estimatedRewrittenJsonCharacters);
    }

    private static async Task<long> ValidateHistoricalJsonSyntaxAsync(
        AppDbContext db,
        LessonTypeRef source,
        LessonTypeRef target,
        CancellationToken cancellationToken)
    {
        var totalRewrittenCharacters = 0L;
        var mutationPayloads = db.AutoGenDraftPlanMutations
            .AsNoTracking()
            .OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.BeforeJson, item.AfterJson })
            .AsAsyncEnumerable();
        await foreach (var payload in mutationPayloads.WithCancellation(cancellationToken))
        {
            AddEstimatedRewrittenPayloadSize(
                ValidateHistoricalJsonSyntax(
                    payload.BeforeJson,
                    $"знімку до зміни #{payload.Id}",
                    source,
                    target,
                    cancellationToken),
                ref totalRewrittenCharacters);
            AddEstimatedRewrittenPayloadSize(
                ValidateHistoricalJsonSyntax(
                    payload.AfterJson,
                    $"знімку після зміни #{payload.Id}",
                    source,
                    target,
                    cancellationToken),
                ref totalRewrittenCharacters);
        }

        var jobPayloads = db.AutoGenJobRuns
            .AsNoTracking()
            .OrderBy(job => job.Id)
            .Select(job => new
            {
                job.Id,
                job.RequestJson,
                job.StatusJson,
                job.ResultJson,
                job.ReportJson
            })
            .AsAsyncEnumerable();
        await foreach (var payload in jobPayloads.WithCancellation(cancellationToken))
        {
            AddEstimatedRewrittenPayloadSize(
                ValidateHistoricalJsonSyntax(
                    payload.RequestJson,
                    $"запиті завдання #{payload.Id}",
                    source,
                    target,
                    cancellationToken),
                ref totalRewrittenCharacters);
            AddEstimatedRewrittenPayloadSize(
                ValidateHistoricalJsonSyntax(
                    payload.StatusJson,
                    $"статусі завдання #{payload.Id}",
                    source,
                    target,
                    cancellationToken),
                ref totalRewrittenCharacters);
            AddEstimatedRewrittenPayloadSize(
                ValidateHistoricalJsonSyntax(
                    payload.ResultJson,
                    $"результаті завдання #{payload.Id}",
                    source,
                    target,
                    cancellationToken),
                ref totalRewrittenCharacters);
            AddEstimatedRewrittenPayloadSize(
                ValidateHistoricalJsonSyntax(
                    payload.ReportJson,
                    $"звіті завдання #{payload.Id}",
                    source,
                    target,
                    cancellationToken),
                ref totalRewrittenCharacters);
        }
        return totalRewrittenCharacters;
    }

    private static long ValidateHistoricalJsonSyntax(
        string? json,
        string payloadDescription,
        LessonTypeRef source,
        LessonTypeRef target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(json))
        {
            return json?.Length ?? 0;
        }
        try
        {
            var node = JsonNode.Parse(json);
            cancellationToken.ThrowIfCancellationRequested();
            if (node is null)
            {
                return json.Length;
            }
            var changed = false;
            RewriteNode(node, null, source, target, ref changed, cancellationToken);
            return changed
                ? GetBoundedSerializedJsonLength(node, cancellationToken)
                : json.Length;
        }
        catch (JsonException)
        {
            throw new LessonTypeMergeException(
                $"Історичний JSON автогенерації у {payloadDescription} пошкоджений; безпечне об'єднання неможливе.");
        }
    }

    private static int EnsureIntegerLessonTypeId(JsonNode node)
    {
        if (node is JsonValue value && value.TryGetValue<int>(out var id))
        {
            return id;
        }
        throw CreateInvalidRecognizedFieldTypeException();
    }

    private static JsonArray EnsureIntegerLessonTypeIdArray(
        JsonNode node,
        CancellationToken cancellationToken)
    {
        if (node is not JsonArray array)
        {
            throw CreateInvalidRecognizedFieldTypeException();
        }
        foreach (var item in array)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is null)
            {
                throw CreateInvalidRecognizedFieldTypeException();
            }
            _ = EnsureIntegerLessonTypeId(item);
        }
        return array;
    }

    private static string EnsureStringLessonTypeValue(JsonNode node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }
        throw CreateInvalidRecognizedFieldTypeException();
    }

    private static JsonArray EnsureStringLessonTypeArray(
        JsonNode node,
        CancellationToken cancellationToken)
    {
        if (node is not JsonArray array)
        {
            throw CreateInvalidRecognizedFieldTypeException();
        }
        foreach (var item in array)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is null)
            {
                throw CreateInvalidRecognizedFieldTypeException();
            }
            _ = EnsureStringLessonTypeValue(item);
        }
        return array;
    }

    private static LessonTypeMergeException CreateInvalidRecognizedFieldTypeException()
        => new(
            "Історичний JSON автогенерації містить поле типу заняття з неочікуваним типом даних; безпечне об'єднання неможливе.");

    private static void AddHistoricalPayloadSize(int payloadSize, ref long totalCharacters)
    {
        if (payloadSize > MaxSingleHistoricalJsonCharacters)
        {
            throw new LessonTypeMergeException(
                $"Історичний JSON автогенерації містить {payloadSize} символів, що перевищує безпечний ліміт {MaxSingleHistoricalJsonCharacters}.");
        }
        totalCharacters += payloadSize;
        if (totalCharacters > MaxHistoricalJsonCharacters)
        {
            throw new LessonTypeMergeException(
                $"Сумарний розмір історичних JSON автогенерації перевищує безпечний ліміт {MaxHistoricalJsonCharacters} символів.");
        }
    }

    private static void AddEstimatedRewrittenPayloadSize(
        long payloadSize,
        ref long totalCharacters)
    {
        if (payloadSize > MaxSingleHistoricalJsonCharacters)
        {
            throw CreateRewrittenPayloadLimitException(payloadSize);
        }
        totalCharacters = checked(totalCharacters + payloadSize);
        if (totalCharacters > MaxHistoricalJsonCharacters)
        {
            throw new LessonTypeMergeException(
                $"Сумарний розмір історичних JSON автогенерації після об'єднання перевищує безпечний ліміт {MaxHistoricalJsonCharacters} символів.");
        }
    }

    private static LessonTypeMergeException CreateRewrittenPayloadLimitException(long payloadSize)
        => new(
            $"Історичний JSON автогенерації після об'єднання потребуватиме щонайменше {payloadSize} символів, що перевищує безпечний ліміт {MaxSingleHistoricalJsonCharacters}.");

    // Рахує серіалізований розмір через bounded IBufferWriter, не створюючи великий рядок.
    // UTF-8 байтів не менше, ніж UTF-16 символів у фінальному рядку, тому оцінка консервативна.
    private static long GetBoundedSerializedJsonLength(
        JsonNode node,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var buffer = new BoundedCountingBufferWriter(MaxSingleHistoricalJsonCharacters);
        using var writer = new Utf8JsonWriter(buffer);
        WriteJsonNode(node, writer, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        writer.Flush();
        cancellationToken.ThrowIfCancellationRequested();
        return buffer.BytesWritten;
    }

    private static void WriteJsonNode(
        JsonNode node,
        Utf8JsonWriter writer,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is JsonObject obj)
        {
            writer.WriteStartObject();
            foreach (var pair in obj)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WritePropertyName(pair.Key);
                if (pair.Value is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    WriteJsonNode(pair.Value, writer, cancellationToken);
                }
            }
            writer.WriteEndObject();
            return;
        }
        if (node is JsonArray array)
        {
            writer.WriteStartArray();
            foreach (var item in array)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    WriteJsonNode(item, writer, cancellationToken);
                }
            }
            writer.WriteEndArray();
            return;
        }
        node.WriteTo(writer, JsonOptions);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task EnsureSourceLifecycleAllowsMergeAsync(
        AppDbContext db,
        int sourceTypeId,
        CancellationToken cancellationToken)
    {
        var sourceDraftScopes = await db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => item.LessonTypeId == sourceTypeId)
            .Select(item => new DraftPlanScope(item.GroupId, item.Date))
            .ToListAsync(cancellationToken);
        await EnsureNoActivePlanReferencesSourceAsync(
            db,
            sourceTypeId,
            sourceDraftScopes,
            cancellationToken);
    }

    private static async Task EnsureNoActivePlanReferencesSourceAsync(
        AppDbContext db,
        int sourceTypeId,
        IReadOnlyCollection<DraftPlanScope> sourceDrafts,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var activePlans = await db.AutoGenDraftPlans
            .AsNoTracking()
            .Include(plan => plan.Mutations)
            .Where(plan => (plan.State == (int)AutoGenPlanState.Ready
                            || plan.State == (int)AutoGenPlanState.Applied)
                           && plan.ExpiresAtUtc > nowUtc)
            .OrderBy(plan => plan.Id)
            .ToListAsync(cancellationToken);
        if (activePlans.Count == 0)
        {
            return;
        }

        var blockingPlanIds = new List<string>();
        var seenPlanIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plan in activePlans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Будь-яке об'єднання змінює глобальний fingerprint довідника для плану в стані Ready.
            var blocksMerge = plan.State == (int)AutoGenPlanState.Ready;
            if (!blocksMerge)
            {
                foreach (var mutation in plan.Mutations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (JsonMayReferenceLessonTypeId(
                            mutation.BeforeJson,
                            sourceTypeId,
                            cancellationToken)
                        || JsonMayReferenceLessonTypeId(
                            mutation.AfterJson,
                            sourceTypeId,
                            cancellationToken))
                    {
                        blocksMerge = true;
                        break;
                    }
                }
            }
            if (!blocksMerge)
            {
                blocksMerge = PlanScopeContainsAnyDraft(plan, sourceDrafts, cancellationToken);
            }
            if (blocksMerge && seenPlanIds.Add(plan.PlanId))
            {
                blockingPlanIds.Add(plan.PlanId);
            }
        }
        if (blockingPlanIds.Count == 0)
        {
            return;
        }

        var shownPlanIds = blockingPlanIds.Take(5).ToList();
        var remainingCount = blockingPlanIds.Count - shownPlanIds.Count;
        var suffix = remainingCount > 0 ? $" Ще планів: {remainingCount}." : string.Empty;
        throw new LessonTypeMergeException(
            $"Активні плани автогенерації стануть недійсними після об'єднання або вже посилаються на вихідний тип заняття: {string.Join(", ", shownPlanIds)}.{suffix} " +
            "Спочатку завершіть доступне застосування або відкіт чи дочекайтеся завершення строку дії плану.");
    }

    private static bool PlanScopeContainsAnyDraft(
        AutoGenDraftPlan plan,
        IReadOnlyCollection<DraftPlanScope> sourceDrafts,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sourceDrafts.Count == 0)
        {
            return false;
        }

        IReadOnlyCollection<int>? groupIds;
        try
        {
            groupIds = JsonSerializer.Deserialize<List<int>>(plan.GroupIdsJson, JsonOptions);
        }
        catch (JsonException)
        {
            return true;
        }
        if (groupIds is null)
        {
            return true;
        }

        var groupIdSet = groupIds.ToHashSet();
        foreach (var draft in sourceDrafts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (draft.Date >= plan.RangeStartDate
                && draft.Date <= plan.RangeEndDate
                && groupIdSet.Contains(draft.GroupId))
            {
                return true;
            }
        }
        return false;
    }

    private static bool JsonMayReferenceLessonTypeId(
        string? json,
        int sourceTypeId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }
        try
        {
            var node = JsonNode.Parse(json);
            cancellationToken.ThrowIfCancellationRequested();
            return node is null || NodeReferencesLessonTypeId(
                node,
                null,
                sourceTypeId,
                cancellationToken);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static bool NodeReferencesLessonTypeId(
        JsonNode node,
        string? propertyName,
        int sourceTypeId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pair.Value is null)
                {
                    continue;
                }
                if (IsLessonTypeIdProperty(pair.Key)
                    && pair.Value is JsonValue idValue
                    && idValue.TryGetValue<int>(out var id)
                    && id == sourceTypeId)
                {
                    return true;
                }
                if (NodeReferencesLessonTypeId(
                        pair.Value,
                        pair.Key,
                        sourceTypeId,
                        cancellationToken))
                {
                    return true;
                }
            }
            return false;
        }
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item is null)
                {
                    continue;
                }
                if (IsLessonTypeIdsProperty(propertyName)
                    && item is JsonValue idValue
                    && idValue.TryGetValue<int>(out var id)
                    && id == sourceTypeId)
                {
                    return true;
                }
                if (NodeReferencesLessonTypeId(
                        item,
                        propertyName,
                        sourceTypeId,
                        cancellationToken))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static string ReplaceSuffix(string value, string sourceSuffix, string targetSuffix)
        => value[..^sourceSuffix.Length] + targetSuffix;

    internal static string? RewriteJson(
        string? json,
        LessonTypeRef source,
        LessonTypeRef target,
        out bool changed,
        CancellationToken cancellationToken = default)
        => RewriteJson(
            json,
            source,
            target,
            out changed,
            cancellationToken,
            rewriteBudget: null);

    private static string? RewriteJson(
        string? json,
        LessonTypeRef source,
        LessonTypeRef target,
        out bool changed,
        CancellationToken cancellationToken,
        HistoricalJsonRewriteBudget? rewriteBudget)
    {
        cancellationToken.ThrowIfCancellationRequested();
        changed = false;
        if (string.IsNullOrWhiteSpace(json))
        {
            rewriteBudget?.Consume(json?.Length ?? 0, cancellationToken);
            return json;
        }
        if (json.Length > MaxSingleHistoricalJsonCharacters)
        {
            throw CreateRewrittenPayloadLimitException(json.Length);
        }
        try
        {
            var node = JsonNode.Parse(json);
            cancellationToken.ThrowIfCancellationRequested();
            if (node is null)
            {
                rewriteBudget?.Consume(json.Length, cancellationToken);
                return json;
            }
            RewriteNode(node, null, source, target, ref changed, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!changed)
            {
                rewriteBudget?.Consume(json.Length, cancellationToken);
                return json;
            }
            var estimatedLength = GetBoundedSerializedJsonLength(node, cancellationToken);
            rewriteBudget?.Consume(estimatedLength, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var rewritten = node.ToJsonString(JsonOptions);
            if (rewritten.Length > MaxSingleHistoricalJsonCharacters)
            {
                throw CreateRewrittenPayloadLimitException(rewritten.Length);
            }
            return rewritten;
        }
        catch (JsonException)
        {
            throw new LessonTypeMergeException(
                "Історичний JSON автогенерації пошкоджений; безпечне об'єднання неможливе.");
        }
    }

    private static void RewriteNode(
        JsonNode node,
        string? propertyName,
        LessonTypeRef source,
        LessonTypeRef target,
        ref bool changed,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is JsonObject obj)
        {
            foreach (var pair in obj.ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pair.Value is null)
                {
                    continue;
                }
                if (IsLessonTypeIdProperty(pair.Key))
                {
                    var id = EnsureIntegerLessonTypeId(pair.Value);
                    if (id == source.Id)
                    {
                        obj[pair.Key] = target.Id;
                        changed = true;
                    }
                    continue;
                }
                if (IsLessonTypeIdsProperty(pair.Key))
                {
                    var ids = EnsureIntegerLessonTypeIdArray(pair.Value, cancellationToken);
                    for (var index = 0; index < ids.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var id = EnsureIntegerLessonTypeId(ids[index]!);
                        if (id == source.Id)
                        {
                            ids[index] = target.Id;
                            changed = true;
                        }
                    }
                    continue;
                }
                if (IsLessonTypeNameProperty(pair.Key)
                    || IsLessonTypeCodeProperty(pair.Key))
                {
                    var currentText = EnsureStringLessonTypeValue(pair.Value);
                    var isCode = IsLessonTypeCodeProperty(pair.Key);
                    var sourceValue = isCode ? source.Code : source.Name;
                    var targetValue = isCode ? target.Code : target.Name;
                    var comparison = isCode
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.CurrentCultureIgnoreCase;
                    if (string.Equals(currentText, sourceValue, comparison)
                        && !string.Equals(currentText, targetValue, StringComparison.Ordinal))
                    {
                        obj[pair.Key] = targetValue;
                        changed = true;
                    }
                    continue;
                }
                if (IsLessonTypeNamesProperty(pair.Key)
                    || IsLessonTypeCodesProperty(pair.Key))
                {
                    var values = EnsureStringLessonTypeArray(pair.Value, cancellationToken);
                    var isCode = IsLessonTypeCodesProperty(pair.Key);
                    var sourceValue = isCode ? source.Code : source.Name;
                    var targetValue = isCode ? target.Code : target.Name;
                    var comparison = isCode
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.CurrentCultureIgnoreCase;
                    for (var index = 0; index < values.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var currentText = EnsureStringLessonTypeValue(values[index]!);
                        if (string.Equals(currentText, sourceValue, comparison)
                            && !string.Equals(currentText, targetValue, StringComparison.Ordinal))
                        {
                            values[index] = targetValue;
                            changed = true;
                        }
                    }
                    continue;
                }
                RewriteNode(
                    pair.Value,
                    pair.Key,
                    source,
                    target,
                    ref changed,
                    cancellationToken);
            }
            return;
        }
        if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = array[index];
                if (item is null)
                {
                    continue;
                }
                if (IsLessonTypeIdsProperty(propertyName)
                    && item is JsonValue idValue
                    && idValue.TryGetValue<int>(out var id)
                    && id == source.Id)
                {
                    array[index] = target.Id;
                    changed = true;
                    continue;
                }
                RewriteNode(
                    item,
                    propertyName,
                    source,
                    target,
                    ref changed,
                    cancellationToken);
            }
            return;
        }
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            return;
        }
        string? rewritten = null;
        if (IsLessonTypeNameProperty(propertyName)
            && string.Equals(text, source.Name, StringComparison.CurrentCultureIgnoreCase))
        {
            rewritten = target.Name;
        }
        else if (IsLessonTypeCodeProperty(propertyName)
                 && string.Equals(text, source.Code, StringComparison.OrdinalIgnoreCase))
        {
            rewritten = target.Code;
        }
        if (rewritten is not null && !string.Equals(text, rewritten, StringComparison.Ordinal))
        {
            value.ReplaceWith(JsonValue.Create(rewritten));
            changed = true;
        }
    }

    private static bool IsLessonTypeIdProperty(string? propertyName)
        => propertyName?.EndsWith("LessonTypeId", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsLessonTypeIdsProperty(string? propertyName)
        => propertyName?.EndsWith("LessonTypeIds", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsLessonTypeNameProperty(string? propertyName)
        => propertyName?.EndsWith("LessonTypeName", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsLessonTypeNamesProperty(string? propertyName)
        => propertyName?.EndsWith("LessonTypeNames", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsLessonTypeCodeProperty(string? propertyName)
        => propertyName?.EndsWith("LessonTypeCode", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsLessonTypeCodesProperty(string? propertyName)
        => propertyName?.EndsWith("LessonTypeCodes", StringComparison.OrdinalIgnoreCase) == true;

    private sealed class HistoricalJsonRewriteBudget
    {
        private long _usedCharacters;

        public void Consume(long characters, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _usedCharacters = checked(_usedCharacters + characters);
            if (_usedCharacters > MaxHistoricalJsonCharacters)
            {
                throw new LessonTypeMergeException(
                    $"Сумарний розмір історичних JSON автогенерації після об'єднання перевищує безпечний ліміт {MaxHistoricalJsonCharacters} символів.");
            }
        }
    }

    private sealed class BoundedCountingBufferWriter(long maxBytes) : IBufferWriter<byte>
    {
        private byte[] _buffer = new byte[4_096];

        public long BytesWritten { get; private set; }

        public void Advance(int count)
        {
            if (count < 0 || count > _buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            var next = checked(BytesWritten + count);
            if (next > maxBytes)
            {
                throw CreateRewrittenPayloadLimitException(next);
            }
            BytesWritten = next;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureBuffer(sizeHint);
            return _buffer;
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureBuffer(sizeHint);
            return _buffer;
        }

        private void EnsureBuffer(int sizeHint)
        {
            if (sizeHint < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeHint));
            }
            if (sizeHint > maxBytes)
            {
                throw CreateRewrittenPayloadLimitException(sizeHint);
            }
            if (sizeHint > _buffer.Length)
            {
                _buffer = new byte[sizeHint];
            }
        }
    }

    private readonly record struct DraftPlanScope(int GroupId, DateOnly Date);
}
