namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

public sealed record GlobalCatchUpReservationReleasePlan(
    bool ContinueCatchUp,
    bool RequestAtomicRepair,
    bool AllowAtomicRepairRetry,
    bool ResetExhaustedSearchBudgets);

public sealed record GlobalCatchUpAtomicRepairCompletionPlan(
    bool ClearRepairRequest,
    bool MarkRepairAttempted,
    bool ContinueCatchUp);

// Узгоджує одноразові переходи глобального відновлення між раундами дозаповнення.
public static class GlobalCatchUpRepairPlanner
{
    public const int MinAtomicSharedTailPlacements = 8;
    public const int MaxAtomicSharedTailPlacementsPerGroup = 3;
    // Резервуємо раунди на атомарний прохід, звільнення резервів і одну фінальну повторну спробу.
    public const int MaxTransitionOnlyRounds = 3;

    public static int CalculateMaxCatchUpRounds(int remainingPlacements)
    {
        var productiveRoundBudget = Math.Max(1, remainingPlacements);
        return productiveRoundBudget > int.MaxValue - MaxTransitionOnlyRounds
            ? int.MaxValue
            : productiveRoundBudget + MaxTransitionOnlyRounds;
    }

    public static bool CanRunAtomicSharedTailRepair(
        int remainingPlacements,
        int selectedGroupCount)
    {
        if (selectedGroupCount <= 0)
        {
            return false;
        }

        var maxAtomicSharedTailPlacements = Math.Max(
            MinAtomicSharedTailPlacements,
            selectedGroupCount * MaxAtomicSharedTailPlacementsPerGroup);
        return remainingPlacements is > 0
               && remainingPlacements <= maxAtomicSharedTailPlacements;
    }

    public static bool ShouldRunAtomicRepairAfterGroup(
        int currentGroupId,
        IReadOnlyList<int> groupIdsInCurrentRound,
        bool repairRequested)
        => repairRequested
           && groupIdsInCurrentRound.Count > 0
           && currentGroupId == groupIdsInCurrentRound[^1];

    public static GlobalCatchUpReservationReleasePlan PlanAfterReservationRelease(
        bool reservationsReleased,
        int remainingPlacements)
    {
        var retryRequired = reservationsReleased && remainingPlacements > 0;
        return new GlobalCatchUpReservationReleasePlan(
            ContinueCatchUp: retryRequired,
            RequestAtomicRepair: retryRequired,
            AllowAtomicRepairRetry: retryRequired,
            ResetExhaustedSearchBudgets: retryRequired);
    }

    public static GlobalCatchUpAtomicRepairCompletionPlan PlanAfterAtomicRepair(
        bool repairRan)
        => new(
            ClearRepairRequest: repairRan,
            MarkRepairAttempted: repairRan,
            ContinueCatchUp: repairRan);
}
