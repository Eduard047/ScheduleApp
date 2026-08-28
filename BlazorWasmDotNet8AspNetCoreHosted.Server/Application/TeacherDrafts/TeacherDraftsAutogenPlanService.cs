using System.Data;
using System.Text.Json;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

public sealed class AutoGenPlanNotFoundException(string message) : Exception(message);

public class AutoGenPlanConflictException(string message) : Exception(message);

public sealed class AutoGenPlanPersistenceException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class AutoGenPlanCapacityException(string message) : AutoGenPlanConflictException(message);

public sealed class AutoGenPlanValidationException(string message) : Exception(message);

internal sealed record AutoGenDraftSnapshot(
    int Id,
    Guid Revision,
    DateOnly Date,
    DayOfWeek DayOfWeek,
    TimeOnly StartTime,
    TimeOnly EndTime,
    int LessonTypeId,
    string LessonTypeName,
    int GroupId,
    string GroupName,
    int ModuleId,
    string ModuleName,
    int? ModuleTopicId,
    string? TopicCode,
    int? TeacherId,
    string? TeacherName,
    int? RoomId,
    string? RoomName,
    DraftStatus Status,
    int? PublishedItemId,
    string? BatchKey,
    string? ValidationWarnings,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool IsLocked,
    bool IsSelfStudy,
    string? GenerationJobId);

internal sealed record AutoGenDraftPlanMutationPayload(
    int Ordinal,
    AutoGenPlanOperation Operation,
    AutoGenDraftSnapshot? Before,
    AutoGenDraftSnapshot? After);

internal sealed record AutoGenDraftPlanPayload(
    string PlanId,
    int CourseId,
    DateOnly RangeStartDate,
    DateOnly RangeEndDate,
    WeekPreset Days,
    bool AllowIncompleteDrafts,
    IReadOnlyList<int> GroupIds,
    Guid BeforeScopeRevision,
    string InputFingerprint,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    IReadOnlyList<AutoGenDraftPlanMutationPayload> Mutations)
{
    public int AddCount => Mutations.Count(item => item.Operation == AutoGenPlanOperation.Add);
    public int UpdateCount => Mutations.Count(item => item.Operation == AutoGenPlanOperation.Update);
    public int DeleteCount => Mutations.Count(item => item.Operation == AutoGenPlanOperation.Delete);

    public AutoGenPlanSummaryDto ToSummary()
        => new(
            PlanId,
            AutoGenPlanState.Ready,
            1,
            new DateTimeOffset(DateTime.SpecifyKind(CreatedAtUtc, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(ExpiresAtUtc, DateTimeKind.Utc)),
            null,
            null,
            AddCount,
            UpdateCount,
            DeleteCount,
            true,
            false);
}

public sealed class TeacherDraftsAutogenPlanService
{
    public const int DefaultChangePageSize = 200;
    public const int MaxChangePageSize = 250;
    public const int MaxMutationsPerPlan = 2_000;
    internal const int MaxScopeRowCount = TeacherDraftsWeekValidationService.MaxAppliedScopeRowCount;
    private const int MaxRetainedPlanCount = 50;
    private const int MaxRetainedMutationCount = 10_000;
    private const int MaxSerializedSnapshotLength = 8_192;
    private const int MaxGroupIdsJsonLength = 4_096;
    internal const int CleanupPlanBatchSize = 50;
    internal const int CleanupMutationBatchSize = 500;
    internal const int MaxPublicationConsumedPlanCount = 50;
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan RollbackLifetime = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly AppDbContext _db;

    public TeacherDraftsAutogenPlanService(AppDbContext db)
        => _db = db;

    public Task<string> CaptureInputFingerprintAsync(
        AutoGenJobRequest request,
        CancellationToken cancellationToken = default)
        => TeacherDraftsAutogenInputFingerprint.CaptureAsync(_db, request, cancellationToken);

    internal async Task<List<AutoGenDraftSnapshot>> CaptureScopeAsync(
        AutoGenJobRequest request,
        CancellationToken cancellationToken)
    {
        var groupIds = request.GroupIds.Distinct().ToList();
        return await LoadBoundedScopeRowsAsync(_db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => item.Date >= request.FromDate
                           && item.Date <= request.ToDate
                           && groupIds.Contains(item.GroupId))
            .OrderBy(item => item.Id)
            .Select(item => new AutoGenDraftSnapshot(
                item.Id,
                item.Revision,
                item.Date,
                item.DayOfWeek,
                item.StartTime,
                item.EndTime,
                item.LessonTypeId,
                item.LessonType.Name,
                item.GroupId,
                item.Group.Name,
                item.ModuleId,
                item.Module.Title,
                item.ModuleTopicId,
                item.ModuleTopic != null ? item.ModuleTopic.TopicCode : null,
                item.TeacherId,
                item.Teacher != null ? item.Teacher.FullName : null,
                item.RoomId,
                item.Room != null ? item.Room.Name : null,
                item.Status,
                item.PublishedItemId,
                item.BatchKey,
                item.ValidationWarnings,
                item.CreatedAt,
                item.UpdatedAt,
                item.IsLocked,
                item.IsSelfStudy,
                item.GenerationJobId)), cancellationToken);
    }

    internal static AutoGenDraftPlanPayload BuildPayload(
        string planId,
        AutoGenJobRequest request,
        IReadOnlyCollection<AutoGenDraftSnapshot> before,
        IReadOnlyCollection<AutoGenDraftSnapshot> after,
        string inputFingerprint)
    {
        var beforeById = before.ToDictionary(item => item.Id);
        var afterById = after.ToDictionary(item => item.Id);
        var unordered = new List<(AutoGenPlanOperation Operation, AutoGenDraftSnapshot? Before, AutoGenDraftSnapshot? After)>();

        foreach (var previous in before.OrderBy(item => item.Id))
        {
            if (!afterById.TryGetValue(previous.Id, out var current))
            {
                unordered.Add((AutoGenPlanOperation.Delete, previous, null));
                EnsureMutationCapacity(unordered.Count);
                continue;
            }

            if (!HasSameMutableContent(previous, current))
            {
                unordered.Add((AutoGenPlanOperation.Update, previous, current));
                EnsureMutationCapacity(unordered.Count);
            }
        }

        foreach (var current in after
                     .Where(item => !beforeById.ContainsKey(item.Id))
                     .OrderBy(item => item.Date)
                     .ThenBy(item => item.StartTime)
                     .ThenBy(item => item.GroupId)
                     .ThenBy(item => item.ModuleId)
                     .ThenBy(item => item.Id))
        {
            unordered.Add((AutoGenPlanOperation.Add, null, current with { Id = 0, Revision = Guid.Empty }));
            EnsureMutationCapacity(unordered.Count);
        }

        var mutations = unordered
            .OrderBy(item => item.Operation)
            .ThenBy(item => item.Before?.Date ?? item.After!.Date)
            .ThenBy(item => item.Before?.StartTime ?? item.After!.StartTime)
            .ThenBy(item => item.Before?.GroupId ?? item.After!.GroupId)
            .ThenBy(item => item.Before?.Id ?? int.MaxValue)
            .Select((item, index) => new AutoGenDraftPlanMutationPayload(
                index + 1,
                item.Operation,
                item.Before,
                item.After))
            .ToList();
        var now = DateTime.UtcNow;
        return new AutoGenDraftPlanPayload(
            planId,
            request.CourseId,
            request.FromDate,
            request.ToDate,
            request.Days,
            request.AllowIncompleteDrafts,
            request.GroupIds.Distinct().OrderBy(id => id).ToList(),
            BuildScopeRevision(before),
            inputFingerprint,
            now,
            now.Add(PreviewLifetime),
            mutations);
    }

    private static void EnsureMutationCapacity(int mutationCount)
    {
        if (mutationCount > MaxMutationsPerPlan)
        {
            throw new AutoGenPlanCapacityException(
                $"План містить понад {MaxMutationsPerPlan} змін, що перевищує безпечний ліміт.");
        }
    }

    internal static async Task AddReadyPlanAsync(
        AppDbContext db,
        AutoGenJobRun run,
        AutoGenDraftPlanPayload payload,
        CancellationToken cancellationToken)
    {
        await CleanupExpiredPlansAsync(db, cancellationToken);
        if (await db.AutoGenDraftPlans.AnyAsync(item => item.AutoGenJobRunId == run.Id, cancellationToken))
        {
            return;
        }
        if (payload.Mutations.Count > MaxMutationsPerPlan)
        {
            throw new AutoGenPlanCapacityException(
                $"План містить {payload.Mutations.Count} змін, що перевищує безпечний ліміт {MaxMutationsPerPlan}.");
        }
        var retainedPlanCount = await db.AutoGenDraftPlans.CountAsync(cancellationToken);
        var retainedMutationCount = await db.AutoGenDraftPlanMutations.CountAsync(cancellationToken);
        if (retainedPlanCount >= MaxRetainedPlanCount
            || retainedMutationCount + payload.Mutations.Count > MaxRetainedMutationCount)
        {
            throw new AutoGenPlanCapacityException(
                "Сховище попередніх планів досягло безпечної квоти. Дочекайтеся штатного очищення старих планів.");
        }

        var plan = new AutoGenDraftPlan
        {
            PlanId = payload.PlanId,
            AutoGenJobRun = run,
            State = (int)AutoGenPlanState.Ready,
            Version = 1,
            CourseId = payload.CourseId,
            RangeStartDate = payload.RangeStartDate,
            RangeEndDate = payload.RangeEndDate,
            Days = (int)payload.Days,
            AllowIncompleteDrafts = payload.AllowIncompleteDrafts,
            GroupIdsJson = JsonSerializer.Serialize(payload.GroupIds, JsonOptions),
            BeforeScopeRevision = payload.BeforeScopeRevision,
            InputFingerprint = payload.InputFingerprint,
            AddCount = payload.AddCount,
            UpdateCount = payload.UpdateCount,
            DeleteCount = payload.DeleteCount,
            CreatedAtUtc = payload.CreatedAtUtc,
            ExpiresAtUtc = payload.ExpiresAtUtc
        };
        foreach (var mutation in payload.Mutations)
        {
            var beforeJson = SerializeSnapshot(mutation.Before);
            var afterJson = SerializeSnapshot(mutation.After);
            if ((beforeJson?.Length ?? 0) > MaxSerializedSnapshotLength
                || (afterJson?.Length ?? 0) > MaxSerializedSnapshotLength)
            {
                throw new AutoGenPlanCapacityException(
                    $"Знімок зміни плану перевищує безпечний ліміт {MaxSerializedSnapshotLength} символів.");
            }
            plan.Mutations.Add(new AutoGenDraftPlanMutation
            {
                Ordinal = mutation.Ordinal,
                Operation = (int)mutation.Operation,
                SourceDraftId = mutation.Before?.Id,
                BeforeRevision = mutation.Before?.Revision,
                BeforeJson = beforeJson,
                AfterJson = afterJson
            });
        }
        db.AutoGenDraftPlans.Add(plan);
    }

    public async Task<AutoGenPlanDetailsDto> GetDetailsAsync(
        string planId,
        CancellationToken cancellationToken = default)
    {
        await CleanupExpiredPlansAsync(_db, cancellationToken);
        var plan = await LoadPlanAsync(
            planId,
            tracking: false,
            includeMutations: true,
            cancellationToken);
        return BuildDetails(plan, DateTime.UtcNow);
    }

    public async Task<AutoGenPlanDetailsDto> GetDetailsPageAsync(
        string planId,
        int changeOffset = 0,
        int changeLimit = DefaultChangePageSize,
        CancellationToken cancellationToken = default)
    {
        EnsurePageBounds(changeOffset, changeLimit);
        await CleanupExpiredPlansAsync(_db, cancellationToken);
        var plan = await LoadPlanAsync(
            planId,
            tracking: false,
            includeMutations: false,
            cancellationToken);
        var totalChanges = await LoadMutationPageAsync(
            plan,
            changeOffset,
            changeLimit,
            cancellationToken);
        return BuildDetails(plan, DateTime.UtcNow, changeOffset, totalChanges);
    }

    public async Task<AutoGenPlanDetailsDto?> GetLatestRollbackableAsync(
        int? courseId,
        CancellationToken cancellationToken = default)
    {
        await CleanupExpiredPlansAsync(_db, cancellationToken);
        var nowUtc = DateTime.UtcNow;
        var query = _db.AutoGenDraftPlans
            .AsNoTracking()
            .Where(item => item.State == (int)AutoGenPlanState.Applied
                           && item.ExpiresAtUtc > nowUtc);
        if (courseId is > 0)
        {
            query = query.Where(item => item.CourseId == courseId.Value);
        }
        var planId = await query
            .OrderByDescending(item => item.AppliedAtUtc)
            .ThenByDescending(item => item.Id)
            .Select(item => item.PlanId)
            .FirstOrDefaultAsync(cancellationToken);
        if (planId is null)
        {
            return null;
        }

        var plan = await LoadPlanAsync(
            planId,
            tracking: false,
            includeMutations: true,
            cancellationToken);
        return BuildDetails(plan, nowUtc);
    }

    public async Task<AutoGenPlanDetailsDto?> GetLatestRollbackablePageAsync(
        int? courseId,
        int changeOffset = 0,
        int changeLimit = DefaultChangePageSize,
        CancellationToken cancellationToken = default)
    {
        EnsurePageBounds(changeOffset, changeLimit);
        await CleanupExpiredPlansAsync(_db, cancellationToken);
        var nowUtc = DateTime.UtcNow;
        var query = _db.AutoGenDraftPlans
            .AsNoTracking()
            .Where(item => item.State == (int)AutoGenPlanState.Applied
                           && item.ExpiresAtUtc > nowUtc);
        if (courseId is > 0)
        {
            query = query.Where(item => item.CourseId == courseId.Value);
        }
        var planId = await query
            .OrderByDescending(item => item.AppliedAtUtc)
            .ThenByDescending(item => item.Id)
            .Select(item => item.PlanId)
            .FirstOrDefaultAsync(cancellationToken);
        if (planId is null)
        {
            return null;
        }

        var plan = await LoadPlanAsync(
            planId,
            tracking: false,
            includeMutations: false,
            cancellationToken);

        var totalChanges = await LoadMutationPageAsync(
            plan,
            changeOffset,
            changeLimit,
            cancellationToken);
        return BuildDetails(plan, nowUtc, changeOffset, totalChanges);
    }

    public async Task<AutoGenPlanDetailsDto> ApplyAsync(
        string planId,
        AutoGenPlanActionRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            await CleanupExpiredPlansAsync(_db, cancellationToken);
            var plan = await LoadPlanAsync(
                planId,
                tracking: true,
                includeMutations: true,
                cancellationToken);
            var state = EffectiveState(plan, DateTime.UtcNow);
            if (state == AutoGenPlanState.Applied)
            {
                await transaction.CommitAsync(cancellationToken);
                return BuildDetails(plan, DateTime.UtcNow);
            }
            if (state != AutoGenPlanState.Ready)
            {
                throw new AutoGenPlanConflictException(
                    state == AutoGenPlanState.Expired
                        ? "Термін дії попереднього плану автогенерації минув. Створіть новий попередній перегляд."
                        : "Цей план автогенерації вже не можна застосувати.");
            }
            EnsureExpectedVersion(plan, request.ExpectedVersion);
            await EnsureInputFingerprintIsCurrentAsync(plan, cancellationToken);

            var groupIds = DeserializeGroupIds(plan);
            var scopeRows = await LoadTrackedScopeRowsAsync(plan, groupIds, cancellationToken);
            EnsureScopeRevision(plan.BeforeScopeRevision, scopeRows, "Чернетки змінилися після попереднього перегляду");
            var scopeById = scopeRows.ToDictionary(item => item.Id);
            var payloads = ReadMutationPayloads(plan);
            EnsureBeforeRowsAreCurrent(payloads, scopeById);
            var pending = payloads
                .Where(item => item.Operation is AutoGenPlanOperation.Add or AutoGenPlanOperation.Update)
                .Select(item => ToPendingDraft(item.After
                    ?? throw CorruptPlan("План застосування не містить нового стану чернетки.")))
                .ToList();
            var excludedIds = payloads
                .Where(item => item.Operation is AutoGenPlanOperation.Update or AutoGenPlanOperation.Delete)
                .Select(item => item.Before!.Id)
                .ToList();
            await ValidateReferencesAndHardRulesAsync(plan, groupIds, payloads.Select(item => item.After).OfType<AutoGenDraftSnapshot>(), pending, excludedIds, cancellationToken);

            var appliedEntities = new Dictionary<long, TeacherDraftItem>();
            foreach (var payload in payloads)
            {
                switch (payload.Operation)
                {
                    case AutoGenPlanOperation.Add:
                        {
                            var entity = new TeacherDraftItem();
                            ApplySnapshot(entity, payload.After!, plan.PlanId, isNew: true);
                            _db.TeacherDraftItems.Add(entity);
                            appliedEntities[payload.Entity.Id] = entity;
                            break;
                        }
                    case AutoGenPlanOperation.Update:
                        {
                            var entity = scopeById[payload.Before!.Id];
                            ApplySnapshot(entity, payload.After!, plan.PlanId, isNew: false);
                            appliedEntities[payload.Entity.Id] = entity;
                            break;
                        }
                    case AutoGenPlanOperation.Delete:
                        _db.TeacherDraftItems.Remove(scopeById[payload.Before!.Id]);
                        break;
                    default:
                        throw CorruptPlan("План містить невідому операцію з чернеткою.");
                }
            }

            await _db.SaveChangesAsync(cancellationToken);
            foreach (var payload in payloads)
            {
                if (!appliedEntities.TryGetValue(payload.Entity.Id, out var entity))
                {
                    payload.Entity.AppliedDraftId = null;
                    payload.Entity.AppliedRevision = null;
                    continue;
                }
                payload.Entity.AppliedDraftId = entity.Id;
                payload.Entity.AppliedRevision = entity.Revision;
            }

            var appliedScope = await LoadScopeRevisionRowsAsync(plan, groupIds, cancellationToken);
            plan.AppliedScopeRevision = BuildScopeRevision(appliedScope);
            plan.State = (int)AutoGenPlanState.Applied;
            plan.AppliedAtUtc = DateTime.UtcNow;
            plan.ExpiresAtUtc = plan.AppliedAtUtc.Value.Add(RollbackLifetime);
            plan.Version++;
            UpdatePersistedJobPlanStatus(plan);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(CancellationToken.None);
            return BuildDetails(plan, DateTime.UtcNow);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new AutoGenPlanConflictException(
                "План або чернетки були змінені паралельно. Оновіть попередній перегляд і повторіть дію.");
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new AutoGenPlanConflictException(
                "Під час застосування дані змінилися або один із довідників став недоступним. План не застосовано.");
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<AutoGenPlanDetailsDto> RollbackAsync(
        string planId,
        AutoGenPlanActionRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            await CleanupExpiredPlansAsync(_db, cancellationToken);
            var plan = await LoadPlanAsync(
                planId,
                tracking: true,
                includeMutations: true,
                cancellationToken);
            var state = EffectiveState(plan, DateTime.UtcNow);
            if (state == AutoGenPlanState.RolledBack)
            {
                await transaction.CommitAsync(cancellationToken);
                return BuildDetails(plan, DateTime.UtcNow);
            }
            if (state != AutoGenPlanState.Applied)
            {
                throw new AutoGenPlanConflictException(
                    state == AutoGenPlanState.Expired
                        ? "Термін безпечного відкоту автогенерації минув."
                        : "Відкотити можна лише застосований план автогенерації.");
            }
            EnsureExpectedVersion(plan, request.ExpectedVersion);
            if (plan.AppliedScopeRevision is not Guid appliedScopeRevision)
            {
                throw CorruptPlan("Застосований план не містить контрольної версії чернеток.");
            }

            var groupIds = DeserializeGroupIds(plan);
            var scopeRows = await LoadTrackedScopeRowsAsync(plan, groupIds, cancellationToken);
            EnsureScopeRevision(appliedScopeRevision, scopeRows, "Чернетки змінилися після застосування плану");
            var scopeById = scopeRows.ToDictionary(item => item.Id);
            var payloads = ReadMutationPayloads(plan);
            EnsureAppliedRowsAreCurrent(plan.PlanId, payloads, scopeById);
            var restoredSnapshots = payloads
                .Where(item => item.Operation is AutoGenPlanOperation.Update or AutoGenPlanOperation.Delete)
                .Select(item => item.Before
                    ?? throw CorruptPlan("План відкоту не містить попереднього стану чернетки."))
                .ToList();
            var pending = restoredSnapshots.Select(ToPendingDraft).ToList();
            var excludedIds = payloads
                .Where(item => item.Operation is AutoGenPlanOperation.Add or AutoGenPlanOperation.Update)
                .Select(item => item.Entity.AppliedDraftId!.Value)
                .ToList();
            await ValidateReferencesAndHardRulesAsync(plan, groupIds, restoredSnapshots, pending, excludedIds, cancellationToken);

            foreach (var payload in payloads)
            {
                switch (payload.Operation)
                {
                    case AutoGenPlanOperation.Add:
                        _db.TeacherDraftItems.Remove(scopeById[payload.Entity.AppliedDraftId!.Value]);
                        break;
                    case AutoGenPlanOperation.Update:
                        {
                            var entity = scopeById[payload.Entity.AppliedDraftId!.Value];
                            ApplySnapshot(entity, payload.Before!, payload.Before!.GenerationJobId, isNew: false);
                            break;
                        }
                    case AutoGenPlanOperation.Delete:
                        {
                            var entity = new TeacherDraftItem { Id = payload.Before!.Id };
                            ApplySnapshot(entity, payload.Before, payload.Before.GenerationJobId, isNew: true);
                            entity.CreatedAt = payload.Before.CreatedAt;
                            _db.TeacherDraftItems.Add(entity);
                            break;
                        }
                    default:
                        throw CorruptPlan("План містить невідому операцію відкоту.");
                }
            }

            await _db.SaveChangesAsync(cancellationToken);
            plan.State = (int)AutoGenPlanState.RolledBack;
            plan.RolledBackAtUtc = DateTime.UtcNow;
            plan.Version++;
            UpdatePersistedJobPlanStatus(plan);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(CancellationToken.None);
            return BuildDetails(plan, DateTime.UtcNow);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new AutoGenPlanConflictException(
                "План або чернетки були змінені паралельно. Автоматичний відкіт не виконано.");
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new AutoGenPlanConflictException(
                "Під час відкоту дані змінилися або один із довідників став недоступним. Автоматичний відкіт не виконано.");
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<AutoGenDraftPlan> LoadPlanAsync(
        string planId,
        bool tracking,
        bool includeMutations,
        CancellationToken cancellationToken)
    {
        var normalized = planId?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 64)
        {
            throw new AutoGenPlanNotFoundException("План автогенерації не знайдено.");
        }

        var plan = await _db.AutoGenDraftPlans
            .AsNoTracking()
            .Where(item => item.PlanId == normalized)
            .Select(item => new AutoGenDraftPlan
            {
                Id = item.Id,
                PlanId = item.PlanId,
                AutoGenJobRunId = item.AutoGenJobRunId,
                AutoGenJobRun = new AutoGenJobRun
                {
                    Id = item.AutoGenJobRun.Id,
                    JobId = item.AutoGenJobRun.JobId,
                    ClientPartitionKey = item.AutoGenJobRun.ClientPartitionKey,
                    RequestHash = item.AutoGenJobRun.RequestHash,
                    Version = item.AutoGenJobRun.Version,
                    Title = item.AutoGenJobRun.Title,
                    CurrentStage = item.AutoGenJobRun.CurrentStage,
                    RequestJson = item.AutoGenJobRun.RequestJson.Substring(
                        0,
                        TeacherDraftsAutogenJobService.MaxPersistedPayloadCharacters + 1),
                    StatusJson = item.AutoGenJobRun.StatusJson.Substring(
                        0,
                        TeacherDraftsAutogenJobService.MaxPersistedPayloadCharacters + 1),
                    ResultJson = item.AutoGenJobRun.ResultJson == null
                        ? null
                        : item.AutoGenJobRun.ResultJson.Substring(
                            0,
                            TeacherDraftsAutogenJobService.MaxPersistedPayloadCharacters + 1),
                    UpdatedAtUtc = item.AutoGenJobRun.UpdatedAtUtc
                },
                State = item.State,
                Version = item.Version,
                CourseId = item.CourseId,
                RangeStartDate = item.RangeStartDate,
                RangeEndDate = item.RangeEndDate,
                Days = item.Days,
                AllowIncompleteDrafts = item.AllowIncompleteDrafts,
                GroupIdsJson = item.GroupIdsJson.Substring(0, MaxGroupIdsJsonLength + 1),
                BeforeScopeRevision = item.BeforeScopeRevision,
                InputFingerprint = item.InputFingerprint,
                AppliedScopeRevision = item.AppliedScopeRevision,
                AddCount = item.AddCount,
                UpdateCount = item.UpdateCount,
                DeleteCount = item.DeleteCount,
                CreatedAtUtc = item.CreatedAtUtc,
                ExpiresAtUtc = item.ExpiresAtUtc,
                AppliedAtUtc = item.AppliedAtUtc,
                RolledBackAtUtc = item.RolledBackAtUtc
            })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new AutoGenPlanNotFoundException("План автогенерації не знайдено.");
        EnsureBoundedPlanRead(plan);
        if (tracking)
        {
            _db.Attach(plan);
        }
        if (!includeMutations)
        {
            return plan;
        }

        plan.Mutations = await LoadBoundedMutationRowsAsync(
            plan.Id,
            changeOffset: 0,
            changeLimit: MaxMutationsPerPlan + 1,
            tracking,
            cancellationToken);
        ValidateMutationTotals(plan, plan.Mutations.Count);
        return plan;
    }

    private static void EnsureBoundedPlanRead(AutoGenDraftPlan plan)
    {
        if (plan.GroupIdsJson.Length > MaxGroupIdsJsonLength)
        {
            throw CorruptPlan(
                $"Склад груп збереженого плану перевищує безпечний ліміт {MaxGroupIdsJsonLength} символів.");
        }

        EnsureBoundedJobPayload(plan.AutoGenJobRun.RequestJson, "запиту");
        EnsureBoundedJobPayload(plan.AutoGenJobRun.StatusJson, "статусу");
        EnsureBoundedJobPayload(plan.AutoGenJobRun.ResultJson, "результату");
    }

    private static void EnsureBoundedJobPayload(string? payload, string label)
    {
        if ((payload?.Length ?? 0) > TeacherDraftsAutogenJobService.MaxPersistedPayloadCharacters)
        {
            throw CorruptPlan(
                $"Збережений JSON {label} автогенерації перевищує безпечний ліміт {TeacherDraftsAutogenJobService.MaxPersistedPayloadCharacters} символів.");
        }
    }

    private async Task<int> LoadMutationPageAsync(
        AutoGenDraftPlan plan,
        int changeOffset,
        int changeLimit,
        CancellationToken cancellationToken)
    {
        var query = _db.AutoGenDraftPlanMutations
            .AsNoTracking()
            .Where(item => item.AutoGenDraftPlanId == plan.Id);
        var actualCount = await query
            .OrderBy(item => item.Id)
            .Take(MaxMutationsPerPlan + 1)
            .CountAsync(cancellationToken);
        ValidateMutationTotals(plan, actualCount);
        plan.Mutations = await LoadBoundedMutationRowsAsync(
            plan.Id,
            changeOffset,
            changeLimit,
            tracking: false,
            cancellationToken);
        return actualCount;
    }

    private async Task<List<AutoGenDraftPlanMutation>> LoadBoundedMutationRowsAsync(
        int planId,
        int changeOffset,
        int changeLimit,
        bool tracking,
        CancellationToken cancellationToken)
    {
        var rows = await _db.AutoGenDraftPlanMutations
            .AsNoTracking()
            .Where(item => item.AutoGenDraftPlanId == planId)
            .OrderBy(item => item.Ordinal)
            .ThenBy(item => item.Id)
            .Skip(changeOffset)
            .Take(changeLimit)
            .Select(item => new MutationReadRow(
                item.Id,
                item.AutoGenDraftPlanId,
                item.Ordinal,
                item.Operation,
                item.SourceDraftId,
                item.AppliedDraftId,
                item.BeforeRevision,
                item.AppliedRevision,
                item.BeforeJson == null
                    ? null
                    : item.BeforeJson.Substring(0, MaxSerializedSnapshotLength + 1),
                item.AfterJson == null
                    ? null
                    : item.AfterJson.Substring(0, MaxSerializedSnapshotLength + 1)))
            .ToListAsync(cancellationToken);
        var mutations = rows.Select(row => new AutoGenDraftPlanMutation
        {
            Id = row.Id,
            AutoGenDraftPlanId = row.AutoGenDraftPlanId,
            Ordinal = row.Ordinal,
            Operation = row.Operation,
            SourceDraftId = row.SourceDraftId,
            AppliedDraftId = row.AppliedDraftId,
            BeforeRevision = row.BeforeRevision,
            AppliedRevision = row.AppliedRevision,
            BeforeJson = row.BeforeJson,
            AfterJson = row.AfterJson
        }).ToList();
        if (tracking && mutations.Count > 0)
        {
            _db.AttachRange(mutations);
        }
        return mutations;
    }

    private static void EnsurePageBounds(int changeOffset, int changeLimit)
    {
        if (changeOffset < 0
            || changeLimit <= 0
            || changeLimit > MaxChangePageSize)
        {
            throw new AutoGenPlanValidationException(
                $"Сторінка змін плану має починатися з невід'ємного індексу та містити від 1 до {MaxChangePageSize} записів.");
        }
    }

    private static int ValidateMutationTotals(AutoGenDraftPlan plan, int actualCount)
    {
        if (plan.AddCount < 0 || plan.UpdateCount < 0 || plan.DeleteCount < 0)
        {
            throw CorruptPlan("Збережений план містить від'ємний лічильник змін.");
        }

        int persistedTotal;
        try
        {
            persistedTotal = checked(plan.AddCount + plan.UpdateCount + plan.DeleteCount);
        }
        catch (OverflowException ex)
        {
            throw new AutoGenPlanPersistenceException(
                "Лічильники змін збереженого плану пошкоджені.",
                ex);
        }

        if (persistedTotal > MaxMutationsPerPlan
            || actualCount > MaxMutationsPerPlan
            || actualCount != persistedTotal)
        {
            throw CorruptPlan(
                $"Збережений план містить неузгоджену кількість змін ({persistedTotal}/{actualCount}).");
        }

        return persistedTotal;
    }

    private async Task<List<TeacherDraftItem>> LoadTrackedScopeRowsAsync(
        AutoGenDraftPlan plan,
        IReadOnlyCollection<int> groupIds,
        CancellationToken cancellationToken)
        => await LoadBoundedScopeRowsAsync(_db.TeacherDraftItems
            .Where(item => item.Date >= plan.RangeStartDate
                           && item.Date <= plan.RangeEndDate
                           && groupIds.Contains(item.GroupId))
            .OrderBy(item => item.Id), cancellationToken);

    private async Task<List<KeyValuePair<int, Guid>>> LoadScopeRevisionRowsAsync(
        AutoGenDraftPlan plan,
        IReadOnlyCollection<int> groupIds,
        CancellationToken cancellationToken)
        => await LoadBoundedScopeRowsAsync(_db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => item.Date >= plan.RangeStartDate
                           && item.Date <= plan.RangeEndDate
                           && groupIds.Contains(item.GroupId))
            .OrderBy(item => item.Id)
            .Select(item => new KeyValuePair<int, Guid>(item.Id, item.Revision)), cancellationToken);

    // Матеріалізує не більше cap + 1 записів, щоб Preview/Apply/Rollback
    // відмовляли без повного завантаження завеликої області чернеток.
    private static async Task<List<T>> LoadBoundedScopeRowsAsync<T>(
        IQueryable<T> query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rows = await query
            .Take(MaxScopeRowCount + 1)
            .ToListAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (rows.Count > MaxScopeRowCount)
        {
            throw new AutoGenPlanCapacityException(
                $"Обсяг чернеток плану автогенерації перевищує безпечний ліміт {MaxScopeRowCount} записів. Створіть новий попередній перегляд для меншого діапазону або меншої кількості груп.");
        }

        return rows;
    }

    private async Task ValidateReferencesAndHardRulesAsync(
        AutoGenDraftPlan plan,
        IReadOnlyCollection<int> groupIds,
        IEnumerable<AutoGenDraftSnapshot> snapshots,
        IReadOnlyCollection<TeacherDraftsAutogenPendingDraft> pending,
        IReadOnlyCollection<int> excludedIds,
        CancellationToken cancellationToken)
    {
        var rows = snapshots.ToList();
        await ValidateReferencesAsync(plan, groupIds, rows, cancellationToken);
        var result = await new TeacherDraftsAutogenHardRuleValidator(_db).ValidateAsync(
            new TeacherDraftsAutogenHardRuleValidationRequest(
                plan.CourseId,
                groupIds,
                plan.RangeStartDate,
                plan.RangeEndDate,
                (WeekPreset)plan.Days,
                plan.AllowIncompleteDrafts,
                PendingDrafts: pending,
                ExcludedDraftIds: excludedIds),
            cancellationToken);
        if (!result.HasViolations)
        {
            return;
        }
        var shown = result.Violations.Take(10).ToList();
        var suffix = result.Violations.Count > shown.Count
            ? $" Ще порушень: {result.Violations.Count - shown.Count}."
            : string.Empty;
        throw new AutoGenPlanConflictException(
            $"План більше не відповідає жорстким правилам: {string.Join(" | ", shown)}.{suffix}");
    }

    private async Task ValidateReferencesAsync(
        AutoGenDraftPlan plan,
        IReadOnlyCollection<int> groupIds,
        IReadOnlyCollection<AutoGenDraftSnapshot> rows,
        CancellationToken cancellationToken)
    {
        var existingGroups = await _db.Groups.AsNoTracking()
            .Where(item => groupIds.Contains(item.Id) && item.CourseId == plan.CourseId)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        if (existingGroups.Count != groupIds.Distinct().Count())
        {
            throw new AutoGenPlanConflictException("Склад груп або їхня належність до курсу змінилися після попереднього перегляду.");
        }

        static List<int> Missing(IEnumerable<int> expected, IEnumerable<int> existing)
        {
            var actual = existing.ToHashSet();
            return expected.Distinct().Where(id => !actual.Contains(id)).OrderBy(id => id).ToList();
        }

        var moduleIds = rows.Select(item => item.ModuleId).Distinct().ToList();
        var existingModules = await _db.Modules.AsNoTracking()
            .Where(item => moduleIds.Contains(item.Id)
                           && (item.CourseId == plan.CourseId || item.ModuleCourses.Any(link => link.CourseId == plan.CourseId)))
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        var lessonTypeIds = rows.Select(item => item.LessonTypeId).Distinct().ToList();
        var existingLessonTypes = await _db.LessonTypes.AsNoTracking()
            .Where(item => lessonTypeIds.Contains(item.Id))
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        var teacherIds = rows.Where(item => item.TeacherId is not null).Select(item => item.TeacherId!.Value).Distinct().ToList();
        var existingTeachers = await _db.Teachers.AsNoTracking()
            .Where(item => teacherIds.Contains(item.Id))
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        var roomIds = rows.Where(item => item.RoomId is not null).Select(item => item.RoomId!.Value).Distinct().ToList();
        var existingRooms = await _db.Rooms.AsNoTracking()
            .Where(item => roomIds.Contains(item.Id))
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        var topicIds = rows.Where(item => item.ModuleTopicId is not null).Select(item => item.ModuleTopicId!.Value).Distinct().ToList();
        var topicMap = await _db.ModuleTopics.AsNoTracking()
            .Where(item => topicIds.Contains(item.Id))
            .Select(item => new { item.Id, item.ModuleId, item.LessonTypeId })
            .ToDictionaryAsync(item => item.Id, cancellationToken);

        var missingMessages = new List<string>();
        AddMissing("модулі", Missing(moduleIds, existingModules));
        AddMissing("типи занять", Missing(lessonTypeIds, existingLessonTypes));
        AddMissing("викладачі", Missing(teacherIds, existingTeachers));
        AddMissing("аудиторії", Missing(roomIds, existingRooms));
        AddMissing("теми", Missing(topicIds, topicMap.Keys));
        foreach (var row in rows.Where(item => item.ModuleTopicId is not null))
        {
            if (topicMap.TryGetValue(row.ModuleTopicId!.Value, out var topic)
                && (topic.ModuleId != row.ModuleId || topic.LessonTypeId != row.LessonTypeId))
            {
                missingMessages.Add($"тема #{topic.Id} більше не відповідає модулю або типу заняття");
            }
        }
        if (missingMessages.Count > 0)
        {
            throw new AutoGenPlanConflictException(
                $"Довідники змінилися після попереднього перегляду: {string.Join("; ", missingMessages)}.");
        }
        return;

        void AddMissing(string title, IReadOnlyCollection<int> ids)
        {
            if (ids.Count > 0)
            {
                missingMessages.Add($"{title}: {string.Join(", ", ids)}");
            }
        }
    }

    private static IReadOnlyList<int> DeserializeGroupIds(AutoGenDraftPlan plan)
    {
        try
        {
            var ids = JsonSerializer.Deserialize<List<int>>(plan.GroupIdsJson, JsonOptions)?
                .Where(id => id > 0)
                .Distinct()
                .OrderBy(id => id)
                .ToList();
            return ids is { Count: > 0 }
                ? ids
                : throw CorruptPlan("План не містить груп для застосування.");
        }
        catch (JsonException ex)
        {
            throw new AutoGenPlanPersistenceException("Не вдалося прочитати склад груп збереженого плану.", ex);
        }
    }

    private static List<ResolvedMutation> ReadMutationPayloads(AutoGenDraftPlan plan)
        => plan.Mutations
            .OrderBy(item => item.Ordinal)
            .Select(item =>
            {
                if (!Enum.IsDefined(typeof(AutoGenPlanOperation), item.Operation))
                {
                    throw CorruptPlan("План містить невідомий тип зміни.");
                }
                return new ResolvedMutation(
                    item,
                    (AutoGenPlanOperation)item.Operation,
                    DeserializeSnapshot(item.BeforeJson),
                    DeserializeSnapshot(item.AfterJson));
            })
            .ToList();

    private static void EnsureBeforeRowsAreCurrent(
        IReadOnlyCollection<ResolvedMutation> payloads,
        IReadOnlyDictionary<int, TeacherDraftItem> scopeById)
    {
        foreach (var payload in payloads.Where(item => item.Operation is AutoGenPlanOperation.Update or AutoGenPlanOperation.Delete))
        {
            var before = payload.Before ?? throw CorruptPlan("План не містить попереднього стану чернетки.");
            if (!scopeById.TryGetValue(before.Id, out var current)
                || current.Revision != before.Revision
                || current.Status != DraftStatus.Draft
                || current.IsLocked)
            {
                throw new AutoGenPlanConflictException(
                    $"Чернетка #{before.Id} змінилася, була заблокована або схвалена після попереднього перегляду.");
            }
        }
    }

    private static void EnsureAppliedRowsAreCurrent(
        string planId,
        IReadOnlyCollection<ResolvedMutation> payloads,
        IReadOnlyDictionary<int, TeacherDraftItem> scopeById)
    {
        foreach (var payload in payloads)
        {
            if (payload.Operation == AutoGenPlanOperation.Delete)
            {
                if (payload.Entity.SourceDraftId is int sourceId && scopeById.ContainsKey(sourceId))
                {
                    throw new AutoGenPlanConflictException(
                        $"Видалена планом чернетка #{sourceId} знову з'явилася. Автоматичний відкіт зупинено.");
                }
                continue;
            }
            if (payload.Entity.AppliedDraftId is not int appliedId
                || payload.Entity.AppliedRevision is not Guid appliedRevision
                || !scopeById.TryGetValue(appliedId, out var current)
                || current.Revision != appliedRevision
                || !string.Equals(current.GenerationJobId, planId, StringComparison.Ordinal))
            {
                throw new AutoGenPlanConflictException(
                    $"Результат плану для чернетки #{payload.Entity.AppliedDraftId?.ToString() ?? "?"} уже змінено вручну. Автоматичний відкіт зупинено.");
            }
        }
    }

    private static void EnsureExpectedVersion(AutoGenDraftPlan plan, long expectedVersion)
    {
        if (expectedVersion <= 0 || plan.Version != expectedVersion)
        {
            throw new AutoGenPlanConflictException(
                $"Версія плану застаріла. Поточна версія: {plan.Version}. Оновіть попередній перегляд.");
        }
    }

    private async Task EnsureInputFingerprintIsCurrentAsync(
        AutoGenDraftPlan plan,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plan.InputFingerprint) || plan.InputFingerprint.Length != 64)
        {
            throw CorruptPlan("Збережений план не містить контрольного відбитка вхідних даних.");
        }
        var request = ReadPlanRequest(plan);
        var current = await CaptureInputFingerprintAsync(request, cancellationToken);
        if (!string.Equals(current, plan.InputFingerprint, StringComparison.Ordinal))
        {
            throw new AutoGenPlanConflictException(
                "Налаштування, довідники або зайнятість розкладу змінилися після попереднього перегляду. Створіть новий план автогенерації.");
        }
    }

    internal static async Task<int> CleanupExpiredPlansAsync(
        AppDbContext db,
        CancellationToken cancellationToken = default)
    {
        var cutoffUtc = DateTime.UtcNow;
        var planIds = await db.AutoGenDraftPlans
            .AsNoTracking()
            .Where(item => item.ExpiresAtUtc < cutoffUtc)
            .OrderBy(item => item.ExpiresAtUtc)
            .ThenBy(item => item.Id)
            .Select(item => item.Id)
            .Take(CleanupPlanBatchSize)
            .ToListAsync(cancellationToken);
        if (planIds.Count == 0)
        {
            return 0;
        }

        var mutationIds = await db.AutoGenDraftPlanMutations
            .AsNoTracking()
            .Where(item => planIds.Contains(item.AutoGenDraftPlanId))
            .OrderBy(item => item.Id)
            .Select(item => item.Id)
            .Take(CleanupMutationBatchSize)
            .ToListAsync(cancellationToken);
        if (mutationIds.Count > 0)
        {
            await db.AutoGenDraftPlanMutations
                .Where(item => mutationIds.Contains(item.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }

        return await db.AutoGenDraftPlans
            .Where(item => planIds.Contains(item.Id)
                           && !db.AutoGenDraftPlanMutations.Any(
                               mutation => mutation.AutoGenDraftPlanId == item.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }

    // Завершує застосований план після публікації першої чернетки, яку створив цей план.
    internal static async Task<int> ExpireAppliedPlansConsumedByPublicationAsync(
        AppDbContext db,
        IReadOnlyCollection<TeacherDraftItem> publishedDrafts,
        CancellationToken cancellationToken = default)
    {
        if (publishedDrafts.Count == 0)
        {
            return 0;
        }

        var publishedPlanIds = publishedDrafts
            .Select(item => item.GenerationJobId)
            .Where(planId => !string.IsNullOrWhiteSpace(planId))
            .Select(planId => planId!)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxPublicationConsumedPlanCount + 1)
            .ToList();
        if (publishedPlanIds.Count == 0)
        {
            return 0;
        }
        if (publishedPlanIds.Count > MaxPublicationConsumedPlanCount)
        {
            throw new DraftValidationCapacityException(
                $"Одна публікація може завершити не більше {MaxPublicationConsumedPlanCount} планів автогенерації.");
        }

        var candidates = await db.AutoGenDraftPlans
            .AsNoTracking()
            .Where(plan => plan.State == (int)AutoGenPlanState.Applied
                           && plan.AppliedScopeRevision != null
                           && (plan.AddCount > 0 || plan.UpdateCount > 0)
                           && publishedPlanIds.Contains(plan.PlanId))
            .OrderBy(plan => plan.Id)
            .Take(MaxPublicationConsumedPlanCount + 1)
            .Select(plan => new AutoGenDraftPlan
            {
                Id = plan.Id,
                PlanId = plan.PlanId,
                AutoGenJobRunId = plan.AutoGenJobRunId,
                AutoGenJobRun = new AutoGenJobRun
                {
                    Id = plan.AutoGenJobRun.Id,
                    JobId = plan.AutoGenJobRun.JobId,
                    ClientPartitionKey = plan.AutoGenJobRun.ClientPartitionKey,
                    RequestHash = plan.AutoGenJobRun.RequestHash,
                    Version = plan.AutoGenJobRun.Version,
                    Title = plan.AutoGenJobRun.Title,
                    CurrentStage = plan.AutoGenJobRun.CurrentStage,
                    RequestJson = string.Empty,
                    StatusJson = plan.AutoGenJobRun.StatusJson.Substring(
                        0,
                        TeacherDraftsAutogenJobService.MaxPersistedPayloadCharacters + 1),
                    UpdatedAtUtc = plan.AutoGenJobRun.UpdatedAtUtc
                },
                State = plan.State,
                Version = plan.Version,
                CourseId = plan.CourseId,
                RangeStartDate = plan.RangeStartDate,
                RangeEndDate = plan.RangeEndDate,
                Days = plan.Days,
                AllowIncompleteDrafts = plan.AllowIncompleteDrafts,
                GroupIdsJson = string.Empty,
                BeforeScopeRevision = plan.BeforeScopeRevision,
                InputFingerprint = plan.InputFingerprint,
                AppliedScopeRevision = plan.AppliedScopeRevision,
                AddCount = plan.AddCount,
                UpdateCount = plan.UpdateCount,
                DeleteCount = plan.DeleteCount,
                CreatedAtUtc = plan.CreatedAtUtc,
                ExpiresAtUtc = plan.ExpiresAtUtc,
                AppliedAtUtc = plan.AppliedAtUtc,
                RolledBackAtUtc = plan.RolledBackAtUtc
            })
            .ToListAsync(cancellationToken);
        if (candidates.Count > MaxPublicationConsumedPlanCount)
        {
            throw new DraftValidationCapacityException(
                $"Одна публікація може завершити не більше {MaxPublicationConsumedPlanCount} планів автогенерації.");
        }
        var publishedPlanIdSet = publishedPlanIds.ToHashSet(StringComparer.Ordinal);
        var nowUtc = DateTime.UtcNow;
        var expiredCount = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var trackedJob = db.AutoGenJobRuns.Local
                .FirstOrDefault(item => item.Id == candidate.AutoGenJobRunId);
            if (trackedJob is null)
            {
                trackedJob = candidate.AutoGenJobRun;
                db.Attach(trackedJob);
            }
            var plan = db.AutoGenDraftPlans.Local
                .FirstOrDefault(item => item.Id == candidate.Id);
            if (plan is null)
            {
                plan = candidate;
                plan.AutoGenJobRun = trackedJob;
                db.Attach(plan);
            }
            else
            {
                plan.AutoGenJobRun = trackedJob;
            }
            if (!publishedPlanIdSet.Contains(plan.PlanId))
            {
                continue;
            }

            var previousVersion = plan.Version;
            plan.State = (int)AutoGenPlanState.Expired;
            plan.Version = previousVersion + 1;
            plan.ExpiresAtUtc = nowUtc;
            try
            {
                EnsureBoundedJobPayload(candidate.AutoGenJobRun.StatusJson, "статусу");
                trackedJob.StatusJson = candidate.AutoGenJobRun.StatusJson;
                UpdatePersistedJobPlanStatus(plan);
            }
            catch (AutoGenPlanPersistenceException)
            {
                // Пошкоджений старий JSON не повинен скасовувати завершення спожитого плану або блокувати публікацію.
            }
            expiredCount++;
        }

        if (expiredCount > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        return expiredCount;
    }

    private static void EnsureScopeRevision(
        Guid expected,
        IEnumerable<TeacherDraftItem> rows,
        string message)
    {
        var actual = LogicalRevisionToken.Combine(rows.Select(item =>
            new KeyValuePair<int, Guid>(item.Id, item.Revision)));
        if (actual != expected)
        {
            throw new AutoGenPlanConflictException($"{message}. План не застосовано.");
        }
    }

    private static Guid BuildScopeRevision(IEnumerable<AutoGenDraftSnapshot> rows)
        => LogicalRevisionToken.Combine(rows.Select(item =>
            new KeyValuePair<int, Guid>(item.Id, item.Revision)));

    private static Guid BuildScopeRevision(IEnumerable<KeyValuePair<int, Guid>> rows)
        => LogicalRevisionToken.Combine(rows);

    private static bool HasSameMutableContent(AutoGenDraftSnapshot left, AutoGenDraftSnapshot right)
        => left.Date == right.Date
           && left.DayOfWeek == right.DayOfWeek
           && left.StartTime == right.StartTime
           && left.EndTime == right.EndTime
           && left.LessonTypeId == right.LessonTypeId
           && left.GroupId == right.GroupId
           && left.ModuleId == right.ModuleId
           && left.ModuleTopicId == right.ModuleTopicId
           && left.TeacherId == right.TeacherId
           && left.RoomId == right.RoomId
           && left.Status == right.Status
           && left.PublishedItemId == right.PublishedItemId
           && string.Equals(left.BatchKey, right.BatchKey, StringComparison.Ordinal)
           && string.Equals(left.ValidationWarnings, right.ValidationWarnings, StringComparison.Ordinal)
           && left.IsLocked == right.IsLocked
           && left.IsSelfStudy == right.IsSelfStudy
           && string.Equals(left.GenerationJobId, right.GenerationJobId, StringComparison.Ordinal);

    private static void ApplySnapshot(
        TeacherDraftItem entity,
        AutoGenDraftSnapshot snapshot,
        string? generationJobId,
        bool isNew)
    {
        entity.Date = snapshot.Date;
        entity.DayOfWeek = snapshot.DayOfWeek;
        entity.StartTime = snapshot.StartTime;
        entity.EndTime = snapshot.EndTime;
        entity.LessonTypeId = snapshot.LessonTypeId;
        entity.GroupId = snapshot.GroupId;
        entity.ModuleId = snapshot.ModuleId;
        entity.ModuleTopicId = snapshot.ModuleTopicId;
        entity.TeacherId = snapshot.TeacherId;
        entity.RoomId = snapshot.RoomId;
        entity.Status = snapshot.Status;
        entity.PublishedItemId = snapshot.PublishedItemId;
        entity.BatchKey = snapshot.BatchKey;
        entity.ValidationWarnings = snapshot.ValidationWarnings;
        entity.CreatedAt = isNew ? DateTime.UtcNow : snapshot.CreatedAt;
        entity.UpdatedAt = DateTime.UtcNow;
        entity.IsLocked = snapshot.IsLocked;
        entity.IsSelfStudy = snapshot.IsSelfStudy;
        entity.GenerationJobId = generationJobId;
        if (isNew)
        {
            entity.Revision = Guid.NewGuid();
        }
    }

    private static TeacherDraftsAutogenPendingDraft ToPendingDraft(AutoGenDraftSnapshot item)
        => new(
            item.Date,
            item.StartTime,
            item.EndTime,
            item.GroupId,
            item.ModuleId,
            item.LessonTypeId,
            item.ModuleTopicId,
            item.TeacherId,
            item.RoomId,
            item.IsSelfStudy,
            item.BatchKey);

    private static string? SerializeSnapshot(AutoGenDraftSnapshot? snapshot)
        => snapshot is null ? null : JsonSerializer.Serialize(snapshot, JsonOptions);

    private static AutoGenDraftSnapshot? DeserializeSnapshot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        if (json.Length > MaxSerializedSnapshotLength)
        {
            throw CorruptPlan(
                $"Знімок зміни плану перевищує безпечний ліміт {MaxSerializedSnapshotLength} символів.");
        }
        try
        {
            return JsonSerializer.Deserialize<AutoGenDraftSnapshot>(json, JsonOptions)
                   ?? throw CorruptPlan("Знімок чернетки у плані порожній.");
        }
        catch (JsonException ex)
        {
            throw new AutoGenPlanPersistenceException("Не вдалося прочитати знімок чернетки зі збереженого плану.", ex);
        }
    }

    private static AutoGenPlanState EffectiveState(AutoGenDraftPlan plan, DateTime nowUtc)
    {
        var state = Enum.IsDefined(typeof(AutoGenPlanState), plan.State)
            ? (AutoGenPlanState)plan.State
            : AutoGenPlanState.Expired;
        return state is AutoGenPlanState.Ready or AutoGenPlanState.Applied
               && plan.ExpiresAtUtc <= nowUtc
            ? AutoGenPlanState.Expired
            : state;
    }

    private sealed record MutationReadRow(
        long Id,
        int AutoGenDraftPlanId,
        int Ordinal,
        int Operation,
        int? SourceDraftId,
        int? AppliedDraftId,
        Guid? BeforeRevision,
        Guid? AppliedRevision,
        string? BeforeJson,
        string? AfterJson);

    private static AutoGenPlanDetailsDto BuildDetails(
        AutoGenDraftPlan plan,
        DateTime nowUtc,
        int changeOffset = 0,
        int? totalChanges = null)
    {
        var state = EffectiveState(plan, nowUtc);
        var summary = BuildSummary(plan, state);
        var validatedTotalChanges = ValidateMutationTotals(
            plan,
            totalChanges ?? plan.Mutations.Count);
        var changes = ReadMutationPayloads(plan)
            .Select(item => new AutoGenPlanChangeDto(
                item.Entity.Ordinal,
                item.Operation,
                ToDraftDto(item.Before, item.Operation == AutoGenPlanOperation.Add ? null : item.Entity.SourceDraftId),
                ToDraftDto(item.After, item.Entity.AppliedDraftId)))
            .ToList();
        var result = AdjustResultForState(
            TryDeserializeResult(plan.AutoGenJobRun.ResultJson)
            ?? new AutoGenResult(0, 0, new List<string>()),
            state);
        return new AutoGenPlanDetailsDto(
            summary,
            changes,
            result,
            changeOffset,
            validatedTotalChanges);
    }

    private static AutoGenResult AdjustResultForState(AutoGenResult result, AutoGenPlanState state)
    {
        if (state == AutoGenPlanState.Ready)
        {
            return result;
        }
        var warnings = result.Warnings
            .Where(message => !IsReadyPlanInstruction(message))
            .ToList();
        var warningDetails = result.WarningDetails?
            .Where(detail => !IsReadyPlanInstruction(detail.Message))
            .ToList();
        return result with
        {
            Warnings = warnings,
            WarningDetails = warningDetails
        };
    }

    private static bool IsReadyPlanInstruction(string? message)
        => !string.IsNullOrWhiteSpace(message)
           && (message.Contains(
                   "Сформовано попередній план без зміни робочих чернеток",
                   StringComparison.OrdinalIgnoreCase)
               || message.Contains(
                   "Застосуйте його окремою дією після перегляду",
                   StringComparison.OrdinalIgnoreCase));

    private static AutoGenJobRequest ReadPlanRequest(AutoGenDraftPlan plan)
    {
        AutoGenJobRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<AutoGenJobRequest>(
                plan.AutoGenJobRun.RequestJson,
                JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new AutoGenPlanPersistenceException(
                "Не вдалося прочитати запит автогенерації для збереженого плану.",
                ex);
        }

        if (request is null)
        {
            throw CorruptPlan("Збережений план не містить запиту автогенерації.");
        }

        var requestGroupIds = (request.GroupIds ?? [])
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        var planGroupIds = DeserializeGroupIds(plan);
        if (!request.PreviewOnly
            || request.CourseId != plan.CourseId
            || request.FromDate != plan.RangeStartDate
            || request.ToDate != plan.RangeEndDate
            || request.Days != (WeekPreset)plan.Days
            || request.AllowIncompleteDrafts != plan.AllowIncompleteDrafts
            || !requestGroupIds.SequenceEqual(planGroupIds))
        {
            throw CorruptPlan("Область збереженого плану не відповідає початковому запиту автогенерації.");
        }

        return request;
    }

    private static AutoGenPlanSummaryDto BuildSummary(AutoGenDraftPlan plan, AutoGenPlanState? forcedState = null)
    {
        var state = forcedState ?? EffectiveState(plan, DateTime.UtcNow);
        return new AutoGenPlanSummaryDto(
            plan.PlanId,
            state,
            plan.Version,
            ToOffset(plan.CreatedAtUtc),
            ToOffset(plan.ExpiresAtUtc),
            ToOffset(plan.AppliedAtUtc),
            ToOffset(plan.RolledBackAtUtc),
            plan.AddCount,
            plan.UpdateCount,
            plan.DeleteCount,
            state == AutoGenPlanState.Ready,
            state == AutoGenPlanState.Applied);
    }

    private static AutoGenPlanDraftDto? ToDraftDto(AutoGenDraftSnapshot? item, int? effectiveId)
        => item is null
            ? null
            : new AutoGenPlanDraftDto(
                effectiveId,
                item.Date,
                item.StartTime.ToString("HH\\:mm"),
                item.EndTime.ToString("HH\\:mm"),
                item.GroupId,
                item.GroupName,
                item.ModuleId,
                item.ModuleName,
                item.LessonTypeId,
                item.LessonTypeName,
                item.ModuleTopicId,
                item.TopicCode,
                item.TeacherId,
                item.TeacherName,
                item.RoomId,
                item.RoomName,
                item.IsSelfStudy,
                item.IsLocked,
                (DraftStatusDto)item.Status,
                item.BatchKey,
                item.ValidationWarnings);

    private static AutoGenResult? TryDeserializeResult(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<AutoGenResult>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new AutoGenPlanPersistenceException("Не вдалося прочитати результат автогенерації для плану.", ex);
        }
    }

    private static void UpdatePersistedJobPlanStatus(AutoGenDraftPlan plan)
    {
        EnsureBoundedJobPayload(plan.AutoGenJobRun.StatusJson, "статусу");
        AutoGenJobStatus? status;
        try
        {
            status = JsonSerializer.Deserialize<AutoGenJobStatus>(plan.AutoGenJobRun.StatusJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new AutoGenPlanPersistenceException("Не вдалося оновити стан завдання після зміни плану.", ex);
        }
        if (status is null)
        {
            throw new AutoGenPlanPersistenceException("Збережений стан завдання автогенерації порожній.");
        }
        var planState = EffectiveState(plan, DateTime.UtcNow);
        var adjustedResult = status.Result is null
            ? null
            : AdjustResultForState(status.Result, planState);
        plan.AutoGenJobRun.StatusJson = JsonSerializer.Serialize(
            status with
            {
                Plan = BuildSummary(plan, planState),
                Result = adjustedResult,
                WarningCount = adjustedResult?.Warnings.Count ?? status.WarningCount
            },
            JsonOptions);
        plan.AutoGenJobRun.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static DateTimeOffset ToOffset(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset? ToOffset(DateTime? value)
        => value is DateTime resolved ? ToOffset(resolved) : null;

    private static AutoGenPlanPersistenceException CorruptPlan(string message)
        => new(message);

    private sealed record ResolvedMutation(
        AutoGenDraftPlanMutation Entity,
        AutoGenPlanOperation Operation,
        AutoGenDraftSnapshot? Before,
        AutoGenDraftSnapshot? After);
}
