using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
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

public sealed class LessonTypeMergeException(string message) : Exception(message);

// Атомарно об'єднує помилковий тип заняття з канонічним без зміни розкладу в часі.
public static class LessonTypeMergeService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static async Task<LessonTypeMergeResult> MergeAsync(
        AppDbContext db,
        int sourceTypeId,
        int targetTypeId,
        CancellationToken cancellationToken = default)
    {
        if (sourceTypeId <= 0 || targetTypeId <= 0 || sourceTypeId == targetTypeId)
        {
            throw new LessonTypeMergeException("Вкажіть два різні чинні типи занять.");
        }

        // Імпорт DOCX уже виконується у власній транзакції; окремий адмін-виклик створює її тут.
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;
        try
        {
            var lessonTypes = await db.LessonTypes
                .Where(type => type.Id == sourceTypeId || type.Id == targetTypeId)
                .OrderBy(type => type.Id)
                .ToListAsync(cancellationToken);
            var source = lessonTypes.SingleOrDefault(type => type.Id == sourceTypeId)
                ?? throw new LessonTypeMergeException($"Тип заняття #{sourceTypeId} не знайдено.");
            var target = lessonTypes.SingleOrDefault(type => type.Id == targetTypeId)
                ?? throw new LessonTypeMergeException($"Тип заняття #{targetTypeId} не знайдено.");

            if (!target.IsActive)
            {
                throw new LessonTypeMergeException("Канонічний тип заняття має бути активним.");
            }
            if (!HasSamePlacementSemantics(source, target))
            {
                throw new LessonTypeMergeException(
                    "Типи занять мають різні правила аудиторії, викладача або обліку годин, тому автоматичне об'єднання небезпечне.");
            }

            var moduleTopicsUpdated = await db.ModuleTopics
                .Where(topic => topic.LessonTypeId == sourceTypeId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(topic => topic.LessonTypeId, targetTypeId),
                    cancellationToken);
            var teacherDraftsUpdated = await db.TeacherDraftItems
                .Where(item => item.LessonTypeId == sourceTypeId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(item => item.LessonTypeId, targetTypeId),
                    cancellationToken);
            var scheduleItemsUpdated = await db.ScheduleItems
                .Where(item => item.LessonTypeId == sourceTypeId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(item => item.LessonTypeId, targetTypeId),
                    cancellationToken);

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
                item.BatchKey = ReplaceSuffix(item.BatchKey!, sourceSuffix, targetSuffix);
                rescheduleKeysUpdated++;
            }

            var typeIdToken = $"\"lessonTypeId\":{sourceTypeId}";
            var mutations = await db.AutoGenDraftPlanMutations
                .Where(item => (item.BeforeJson != null
                                && (item.BeforeJson.Contains(typeIdToken)
                                    || item.BeforeJson.Contains(source.Name)
                                    || item.BeforeJson.Contains(source.Code)))
                               || (item.AfterJson != null
                                   && (item.AfterJson.Contains(typeIdToken)
                                       || item.AfterJson.Contains(source.Name)
                                       || item.AfterJson.Contains(source.Code))))
                .ToListAsync(cancellationToken);
            var planSnapshotsUpdated = 0;
            foreach (var mutation in mutations)
            {
                var before = RewriteJson(mutation.BeforeJson, source, target, out var beforeChanged);
                var after = RewriteJson(mutation.AfterJson, source, target, out var afterChanged);
                if (!beforeChanged && !afterChanged)
                {
                    continue;
                }
                mutation.BeforeJson = before;
                mutation.AfterJson = after;
                planSnapshotsUpdated++;
            }

            var jobs = await db.AutoGenJobRuns
                .Where(job => job.RequestJson.Contains(typeIdToken)
                              || job.RequestJson.Contains(source.Name)
                              || job.RequestJson.Contains(source.Code)
                              || job.StatusJson.Contains(typeIdToken)
                              || job.StatusJson.Contains(source.Name)
                              || job.StatusJson.Contains(source.Code)
                              || (job.ResultJson != null
                                  && (job.ResultJson.Contains(typeIdToken)
                                      || job.ResultJson.Contains(source.Name)
                                      || job.ResultJson.Contains(source.Code)))
                              || (job.ReportJson != null
                                  && (job.ReportJson.Contains(typeIdToken)
                                      || job.ReportJson.Contains(source.Name)
                                      || job.ReportJson.Contains(source.Code))))
                .ToListAsync(cancellationToken);
            var jobPayloadsUpdated = 0;
            foreach (var job in jobs)
            {
                var changed = false;
                job.RequestJson = RewriteJson(job.RequestJson, source, target, out var requestChanged)!;
                job.StatusJson = RewriteJson(job.StatusJson, source, target, out var statusChanged)!;
                job.ResultJson = RewriteJson(job.ResultJson, source, target, out var resultChanged);
                job.ReportJson = RewriteJson(job.ReportJson, source, target, out var reportChanged);
                changed = requestChanged || statusChanged || resultChanged || reportChanged;
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

            DetachTrackedDependents(db, sourceTypeId);
            db.LessonTypes.Remove(source);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
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
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            throw;
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

    // ExecuteUpdate змінює БД напряму, тому від'єднуємо застарілі відстежувані залежності перед видаленням дубля.
    private static void DetachTrackedDependents(AppDbContext db, int sourceTypeId)
    {
        foreach (var entry in db.ChangeTracker.Entries<ModuleTopic>()
                     .Where(entry => entry.Entity.LessonTypeId == sourceTypeId)
                     .ToList())
        {
            entry.State = EntityState.Detached;
        }
        foreach (var entry in db.ChangeTracker.Entries<TeacherDraftItem>()
                     .Where(entry => entry.Entity.LessonTypeId == sourceTypeId)
                     .ToList())
        {
            entry.State = EntityState.Detached;
        }
        foreach (var entry in db.ChangeTracker.Entries<ScheduleItem>()
                     .Where(entry => entry.Entity.LessonTypeId == sourceTypeId)
                     .ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private static string ReplaceSuffix(string value, string sourceSuffix, string targetSuffix)
        => value[..^sourceSuffix.Length] + targetSuffix;

    internal static string? RewriteJson(
        string? json,
        LessonTypeRef source,
        LessonTypeRef target,
        out bool changed)
    {
        changed = false;
        if (string.IsNullOrWhiteSpace(json))
        {
            return json;
        }
        try
        {
            var node = JsonNode.Parse(json);
            if (node is null)
            {
                return json;
            }
            RewriteNode(node, null, source, target, ref changed);
            return changed ? node.ToJsonString(JsonOptions) : json;
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static void RewriteNode(
        JsonNode node,
        string? propertyName,
        LessonTypeRef source,
        LessonTypeRef target,
        ref bool changed)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj.ToList())
            {
                if (pair.Value is null)
                {
                    continue;
                }
                if (IsLessonTypeIdProperty(pair.Key)
                    && pair.Value is JsonValue idValue
                    && idValue.TryGetValue<int>(out var id)
                    && id == source.Id)
                {
                    obj[pair.Key] = target.Id;
                    changed = true;
                    continue;
                }
                RewriteNode(pair.Value, pair.Key, source, target, ref changed);
            }
            return;
        }
        if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
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
                RewriteNode(item, propertyName, source, target, ref changed);
            }
            return;
        }
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            return;
        }
        var rewritten = text
            .Replace(source.Name, target.Name, StringComparison.CurrentCultureIgnoreCase)
            .Replace(source.Code, target.Code, StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(text, rewritten, StringComparison.Ordinal))
        {
            value.ReplaceWith(JsonValue.Create(rewritten));
            changed = true;
        }
    }

    private static bool IsLessonTypeIdProperty(string? propertyName)
        => propertyName?.EndsWith("LessonTypeId", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsLessonTypeIdsProperty(string? propertyName)
        => propertyName?.EndsWith("LessonTypeIds", StringComparison.OrdinalIgnoreCase) == true;
}
