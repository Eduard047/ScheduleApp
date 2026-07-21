namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

public sealed record ResidualPlacementCandidate(
    int CandidateId,
    int GapId,
    int ResourceId,
    int Cost);

public sealed record ResidualPlacement(
    int CandidateId,
    int GapId,
    int ResourceId,
    int Cost);

public sealed record DeterministicResidualOptimizationResult(
    IReadOnlyList<ResidualPlacement> Placements,
    int VisitedNodes,
    bool NodeLimitReached,
    bool EmergencyLimitReached)
{
    public bool SearchLimitReached => NodeLimitReached || EmergencyLimitReached;
    public int FilledGapCount => Placements.Count;
    public int TotalCost => Placements.Sum(placement => placement.Cost);
}

public sealed record ResidualPlanApplicationResult(
    bool Committed,
    int AppliedPlacements,
    int CardinalityBefore,
    int CardinalityAfter);

// Лічильник вузлів є основним детермінованим обмеженням пошуку.
// Часова межа спрацьовує лише як аварійний захист від патологічно повільної операції.
public sealed class DeterministicSearchBudget
{
    private readonly TimeProvider _timeProvider;
    private readonly long _startedAt;
    private readonly TimeSpan _emergencyTimeout;

    public DeterministicSearchBudget(
        int maxNodes,
        TimeSpan emergencyTimeout,
        TimeProvider? timeProvider = null)
    {
        if (maxNodes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxNodes));
        }
        if (emergencyTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(emergencyTimeout));
        }

        MaxNodes = maxNodes;
        _emergencyTimeout = emergencyTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startedAt = _timeProvider.GetTimestamp();
    }

    public int MaxNodes { get; }
    public int VisitedNodes { get; private set; }
    public bool NodeLimitReached { get; private set; }
    public bool EmergencyLimitReached { get; private set; }
    public bool SearchLimitReached => NodeLimitReached || EmergencyLimitReached;

    public bool TryVisitNode()
    {
        if (SearchLimitReached)
        {
            return false;
        }
        if (_timeProvider.GetElapsedTime(_startedAt, _timeProvider.GetTimestamp()) >= _emergencyTimeout)
        {
            EmergencyLimitReached = true;
            return false;
        }
        if (VisitedNodes >= MaxNodes)
        {
            NodeLimitReached = true;
            return false;
        }

        VisitedNodes++;
        return true;
    }
}

// Розподіляє залишкові варіанти глобально: максимум заповнених прогалин,
// потім мінімальна вартість і стабільний лексикографічний вибір.
public static class DeterministicResidualOptimizer
{
    private sealed record ResidualGap(
        int GapId,
        ResidualPlacementCandidate[] Candidates);

    public static DeterministicResidualOptimizationResult Optimize(
        IEnumerable<ResidualPlacementCandidate> sourceCandidates,
        IReadOnlyDictionary<int, int> capacityByResource,
        int maxAssignments,
        int maxSearchNodes,
        TimeSpan emergencyTimeout,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null)
        => Optimize(
            sourceCandidates,
            capacityByResource,
            maxAssignments,
            new DeterministicSearchBudget(maxSearchNodes, emergencyTimeout, timeProvider),
            cancellationToken);

    public static DeterministicResidualOptimizationResult Optimize(
        IEnumerable<ResidualPlacementCandidate> sourceCandidates,
        IReadOnlyDictionary<int, int> capacityByResource,
        int maxAssignments,
        DeterministicSearchBudget searchBudget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceCandidates);
        ArgumentNullException.ThrowIfNull(capacityByResource);
        ArgumentNullException.ThrowIfNull(searchBudget);
        cancellationToken.ThrowIfCancellationRequested();
        if (maxAssignments < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAssignments));
        }

        var capacities = new SortedDictionary<int, int>();
        foreach (var (resourceId, capacity) in capacityByResource)
        {
            if (capacity < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(capacityByResource),
                    $"Місткість ресурсу #{resourceId} не може бути від'ємною.");
            }
            capacities[resourceId] = capacity;
        }

        var candidates = sourceCandidates.ToArray();
        if (candidates.Any(candidate => candidate.Cost < 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceCandidates),
                "Вартість залишкового кандидата не може бути від'ємною.");
        }
        var duplicateCandidateId = candidates
            .GroupBy(candidate => candidate.CandidateId)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateCandidateId is not null)
        {
            throw new ArgumentException(
                $"Ідентифікатор залишкового кандидата #{duplicateCandidateId.Key} має бути унікальним.",
                nameof(sourceCandidates));
        }
        var unknownResource = candidates.FirstOrDefault(candidate => !capacities.ContainsKey(candidate.ResourceId));
        if (unknownResource is not null)
        {
            throw new ArgumentException(
                $"Для ресурсу #{unknownResource.ResourceId} не задано місткість.",
                nameof(capacityByResource));
        }

        var orderedGaps = candidates
            .GroupBy(candidate => candidate.GapId)
            .Select(group => new ResidualGap(
                group.Key,
                group
                    .OrderBy(candidate => candidate.Cost)
                    .ThenBy(candidate => candidate.ResourceId)
                    .ThenBy(candidate => candidate.CandidateId)
                    .ToArray()))
            .OrderBy(group => group.Candidates.Length)
            .ThenBy(group => group.GapId)
            .ToArray();

        if (maxAssignments == 0 || orderedGaps.Length == 0)
        {
            return new DeterministicResidualOptimizationResult(
                Array.Empty<ResidualPlacement>(),
                0,
                false,
                false);
        }

        var assignmentLimit = Math.Min(maxAssignments, orderedGaps.Length);
        var remainingCapacity = capacities.ToDictionary(entry => entry.Key, entry => entry.Value);
        var current = new List<ResidualPlacement>(assignmentLimit);
        var best = BuildGreedyWarmStart(orderedGaps, capacities, assignmentLimit);
        var bestCost = best.Sum(placement => placement.Cost);
        var visitedNodesBefore = searchBudget.VisitedNodes;

        bool IsCurrentBetter()
        {
            if (current.Count != best.Count)
            {
                return current.Count > best.Count;
            }

            var currentCost = current.Sum(placement => placement.Cost);
            if (currentCost != bestCost)
            {
                return currentCost < bestCost;
            }

            var currentOrdered = current
                .OrderBy(placement => placement.GapId)
                .ThenBy(placement => placement.CandidateId)
                .ToArray();
            var bestOrdered = best
                .OrderBy(placement => placement.GapId)
                .ThenBy(placement => placement.CandidateId)
                .ToArray();
            for (var index = 0; index < currentOrdered.Length; index++)
            {
                var gapComparison = currentOrdered[index].GapId.CompareTo(bestOrdered[index].GapId);
                if (gapComparison != 0)
                {
                    return gapComparison < 0;
                }
                var candidateComparison = currentOrdered[index].CandidateId.CompareTo(bestOrdered[index].CandidateId);
                if (candidateComparison != 0)
                {
                    return candidateComparison < 0;
                }
            }
            return false;
        }

        void CaptureIfBetter()
        {
            if (!IsCurrentBetter())
            {
                return;
            }

            best = current
                .OrderBy(placement => placement.GapId)
                .ThenBy(placement => placement.CandidateId)
                .ToList();
            bestCost = best.Sum(placement => placement.Cost);
        }

        void Search(int gapIndex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!searchBudget.TryVisitNode())
            {
                return;
            }

            CaptureIfBetter();
            if (gapIndex >= orderedGaps.Length || current.Count >= assignmentLimit)
            {
                return;
            }
            if (current.Count + Math.Min(assignmentLimit - current.Count, orderedGaps.Length - gapIndex) < best.Count)
            {
                return;
            }

            var gap = orderedGaps[gapIndex];
            foreach (var candidate in gap.Candidates)
            {
                if (remainingCapacity[candidate.ResourceId] <= 0)
                {
                    continue;
                }

                remainingCapacity[candidate.ResourceId]--;
                current.Add(new ResidualPlacement(
                    candidate.CandidateId,
                    candidate.GapId,
                    candidate.ResourceId,
                    candidate.Cost));
                Search(gapIndex + 1);
                current.RemoveAt(current.Count - 1);
                remainingCapacity[candidate.ResourceId]++;
                if (searchBudget.SearchLimitReached)
                {
                    return;
                }
            }

            Search(gapIndex + 1);
        }

        Search(0);
        return new DeterministicResidualOptimizationResult(
            best
                .OrderBy(placement => placement.GapId)
                .ThenBy(placement => placement.CandidateId)
                .ToArray(),
            searchBudget.VisitedNodes - visitedNodesBefore,
            searchBudget.NodeLimitReached,
            searchBudget.EmergencyLimitReached);
    }

    // Застосовує весь залишковий план як одну пробну операцію та відкочує
    // частковий результат, якщо хоча б один крок не реалізовано або кількість заповнених слотів не збільшилась.
    public static async Task<ResidualPlanApplicationResult> TryApplyPlanAtomicallyAsync(
        IReadOnlyList<ResidualPlacement> placements,
        Func<ResidualPlacement, CancellationToken, Task<bool>> tryApplyPlacement,
        Func<int> measureCardinality,
        Action rollback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(tryApplyPlacement);
        ArgumentNullException.ThrowIfNull(measureCardinality);
        ArgumentNullException.ThrowIfNull(rollback);
        cancellationToken.ThrowIfCancellationRequested();

        var cardinalityBefore = measureCardinality();
        var appliedPlacements = 0;
        try
        {
            foreach (var placement in placements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await tryApplyPlacement(placement, cancellationToken))
                {
                    rollback();
                    return new ResidualPlanApplicationResult(
                        false,
                        appliedPlacements,
                        cardinalityBefore,
                        measureCardinality());
                }
                appliedPlacements++;
            }

            var cardinalityAfter = measureCardinality();
            if (appliedPlacements == 0
                || cardinalityAfter - cardinalityBefore < appliedPlacements)
            {
                rollback();
                return new ResidualPlanApplicationResult(
                    false,
                    appliedPlacements,
                    cardinalityBefore,
                    measureCardinality());
            }

            return new ResidualPlanApplicationResult(
                true,
                appliedPlacements,
                cardinalityBefore,
                cardinalityAfter);
        }
        catch
        {
            rollback();
            throw;
        }
    }

    private static List<ResidualPlacement> BuildGreedyWarmStart(
        IReadOnlyList<ResidualGap> orderedGaps,
        IReadOnlyDictionary<int, int> capacityByResource,
        int assignmentLimit)
    {
        var remainingCapacity = capacityByResource.ToDictionary(entry => entry.Key, entry => entry.Value);
        var result = new List<ResidualPlacement>(assignmentLimit);
        foreach (var gap in orderedGaps)
        {
            if (result.Count >= assignmentLimit)
            {
                break;
            }

            var candidate = gap.Candidates.FirstOrDefault(candidate => remainingCapacity[candidate.ResourceId] > 0);
            if (candidate is null)
            {
                continue;
            }

            remainingCapacity[candidate.ResourceId]--;
            result.Add(new ResidualPlacement(
                candidate.CandidateId,
                candidate.GapId,
                candidate.ResourceId,
                candidate.Cost));
        }
        return result
            .OrderBy(placement => placement.GapId)
            .ThenBy(placement => placement.CandidateId)
            .ToList();
    }
}
