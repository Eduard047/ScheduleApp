namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

public sealed record GlobalCatchUpReservationReleasePlan(
    bool ContinueCatchUp,
    bool RequestAtomicRepair,
    bool AllowAtomicRepairRetry,
    bool ResetSearchLimitDiagnostics);

public sealed record GlobalCatchUpAtomicRepairCompletionPlan(
    bool ClearRepairRequest,
    bool MarkRepairAttempted,
    bool ContinueCatchUp);

// Зберігає спільну квоту аварійних одиночних потоків між усіма фазами одного діапазону job.
internal sealed class AutogenRangeEmergencySingletonState
{
    public int CreatedCount { get; set; }
    public int? OwnerGroupId { get; set; }
}

// Узгоджує одноразові переходи глобального відновлення між раундами дозаповнення.
public static class GlobalCatchUpRepairPlanner
{
    public const int MinAtomicSharedTailPlacements = 8;
    public const int MaxAtomicSharedTailPlacementsPerGroup = 3;
    // Резервуємо раунди на атомарний прохід, звільнення резервів і одну фінальну повторну спробу.
    public const int MaxTransitionOnlyRounds = 3;

    public static int CalculateMaxCatchUpRounds(
        int remainingPlacements,
        int minimumProductiveRounds = 1)
    {
        var productiveRoundBudget = Math.Max(
            1,
            Math.Max(remainingPlacements, minimumProductiveRounds));
        return productiveRoundBudget > int.MaxValue - MaxTransitionOnlyRounds
            ? int.MaxValue
            : productiveRoundBudget + MaxTransitionOnlyRounds;
    }

    public static int CalculateEmergencySingletonSharedLectureBudget(
        IEnumerable<int> shareableTopicUsageLimits)
    {
        ArgumentNullException.ThrowIfNull(shareableTopicUsageLimits);
        var budget = 0;
        foreach (var usageLimit in shareableTopicUsageLimits)
        {
            if (usageLimit <= 0)
            {
                continue;
            }
            if (budget > int.MaxValue - usageLimit)
            {
                return int.MaxValue;
            }
            budget += usageLimit;
        }
        return budget;
    }

    public static bool CanSpendEmergencySingletonSharedLectureBudget(
        int createdCount,
        int maximumCount,
        int? ownerGroupId,
        int candidateGroupId)
        => createdCount >= 0
           && maximumCount > 0
           && createdCount < maximumCount
           && candidateGroupId > 0
           && (ownerGroupId is null || ownerGroupId == candidateGroupId);

    public static bool CanRunAtomicSharedTailRepair(
        int remainingPlacements,
        int pendingSharedTailFrontierRows,
        int selectedGroupCount)
    {
        if (remainingPlacements <= 0
            || pendingSharedTailFrontierRows <= 0
            || selectedGroupCount <= 0)
        {
            return false;
        }

        var maxAtomicSharedTailPlacements = Math.Max(
            MinAtomicSharedTailPlacements,
            selectedGroupCount * MaxAtomicSharedTailPlacementsPerGroup);
        // Вартість атомарного пошуку визначає лише поточний потоковий фронтир.
        // Увесь послідовний хвіст може бути значно більшим, хоча для його
        // розблокування достатньо перенести одну або кілька спільних лекцій.
        return pendingSharedTailFrontierRows <= maxAtomicSharedTailPlacements;
    }

    public static bool ShouldRunAtomicRepairAfterGroup(
        int currentGroupId,
        IReadOnlyList<int> groupIdsInCurrentRound,
        bool repairRequested)
        => repairRequested
           && groupIdsInCurrentRound.Count > 0
           && currentGroupId == groupIdsInCurrentRound[^1];

    public static bool ShouldUseInterleavedDateRound(
        bool clearExisting,
        int pendingGroupCount,
        int round,
        int workingDateCount)
        => clearExisting
           && pendingGroupCount > 1
           && round > 0
           && round <= workingDateCount;

    public static bool ShouldRunCrossDateChronologyCompaction(
        int remainingPlacements,
        int processableDateCount,
        bool isFinalProcessableDate)
        => remainingPlacements > 0
           && processableDateCount > 1
           && isFinalProcessableDate;

    public static bool HasPendingInterleavedDateRound(
        bool clearExisting,
        int nextRound,
        IEnumerable<int> pendingWorkingDateCounts)
    {
        ArgumentNullException.ThrowIfNull(pendingWorkingDateCounts);
        var workingDateCounts = pendingWorkingDateCounts.ToList();
        return workingDateCounts.Count > 1
               && workingDateCounts.Any(workingDateCount =>
                   ShouldUseInterleavedDateRound(
                       clearExisting,
                       workingDateCounts.Count,
                       nextRound,
                       workingDateCount));
    }

    public static bool ShouldSkipExhaustedGroupDuringInterleavedDateRounds(
        bool clearExisting,
        int pendingGroupCount,
        int round,
        int groupWorkingDateCount,
        int maxPendingWorkingDateCount)
        => ShouldUseInterleavedDateRound(
               clearExisting,
               pendingGroupCount,
               round,
               maxPendingWorkingDateCount)
           && !ShouldUseInterleavedDateRound(
               clearExisting,
               pendingGroupCount,
               round,
               groupWorkingDateCount);

    public static bool ShouldRearmAtomicRepairAfterProductiveRound(
        bool repairAttemptedBeforeRound,
        int remainingBeforeRound,
        int remainingAfterRound)
        => repairAttemptedBeforeRound
           && remainingBeforeRound >= 0
           && remainingAfterRound >= 0
           && remainingAfterRound < remainingBeforeRound;

    public static GlobalCatchUpReservationReleasePlan PlanAfterReservationRelease(
        bool reservationsReleased,
        int remainingPlacements)
    {
        var retryRequired = reservationsReleased && remainingPlacements > 0;
        return new GlobalCatchUpReservationReleasePlan(
            ContinueCatchUp: retryRequired,
            RequestAtomicRepair: retryRequired,
            AllowAtomicRepairRetry: retryRequired,
            ResetSearchLimitDiagnostics: retryRequired);
    }

    public static GlobalCatchUpAtomicRepairCompletionPlan PlanAfterAtomicRepair(
        bool repairRan)
        => new(
            ClearRepairRequest: repairRan,
            MarkRepairAttempted: repairRan,
            ContinueCatchUp: repairRan);
}
