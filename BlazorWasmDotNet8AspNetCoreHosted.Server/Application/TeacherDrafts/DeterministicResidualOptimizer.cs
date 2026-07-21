using System.Numerics;

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
    public long TotalCost => Placements.Sum(placement => (long)placement.Cost);
}

public sealed record ResidualPlanApplicationResult(
    bool Committed,
    int AppliedPlacements,
    int CardinalityBefore,
    int CardinalityAfter);

// Лічильник розгорнутих вершин залишкової мережі є основним детермінованим обмеженням пошуку.
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
        if (!CanStartSearch())
        {
            return false;
        }

        VisitedNodes++;
        return true;
    }

    internal bool CanStartSearch()
    {
        if (!CanContinueOperation())
        {
            return false;
        }
        if (VisitedNodes < MaxNodes)
        {
            return true;
        }

        NodeLimitReached = true;
        return false;
    }

    internal bool CanContinueOperation()
    {
        if (SearchLimitReached)
        {
            return false;
        }
        if (_timeProvider.GetElapsedTime(_startedAt, _timeProvider.GetTimestamp()) < _emergencyTimeout)
        {
            return true;
        }

        EmergencyLimitReached = true;
        return false;
    }
}

// Розподіляє залишкові варіанти глобально: максимум заповнених прогалин,
// потім мінімальна вартість і стабільний лексикографічний вибір.
public static class DeterministicResidualOptimizer
{
    private sealed record ResidualGap(
        int GapId,
        ResidualPlacementCandidate[] Candidates);

    private sealed class ResidualFlowEdge
    {
        public ResidualFlowEdge(
            int to,
            int reverseIndex,
            int capacity,
            BigInteger cost,
            ResidualPlacementCandidate? candidate)
        {
            To = to;
            ReverseIndex = reverseIndex;
            Capacity = capacity;
            Cost = cost;
            Candidate = candidate;
        }

        public int To { get; }
        public int ReverseIndex { get; }
        public int Capacity { get; set; }
        public BigInteger Cost { get; }
        public ResidualPlacementCandidate? Candidate { get; }
    }

    private readonly record struct CandidateFlowEdge(
        ResidualPlacementCandidate Candidate,
        ResidualFlowEdge Edge);

    private readonly record struct QueuePriority(
        BigInteger Distance,
        int NodeId);

    private sealed class QueuePriorityComparer : IComparer<QueuePriority>
    {
        public static QueuePriorityComparer Instance { get; } = new();

        public int Compare(QueuePriority left, QueuePriority right)
        {
            var distanceComparison = left.Distance.CompareTo(right.Distance);
            return distanceComparison != 0
                ? distanceComparison
                : left.NodeId.CompareTo(right.NodeId);
        }
    }

    private enum ShortestPathStatus
    {
        Found,
        FoundWithSearchLimit,
        Unreachable,
        SearchLimitReached
    }

    private sealed record ShortestPathResult(
        ShortestPathStatus Status,
        int[] PreviousNodes,
        int[] PreviousEdges,
        BigInteger?[] Distances);

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
        var best = BuildGreedyWarmStart(orderedGaps, capacities, assignmentLimit);
        var visitedNodesBefore = searchBudget.VisitedNodes;
        if (!searchBudget.CanStartSearch())
        {
            return CreateResult(best, searchBudget, visitedNodesBefore);
        }

        var gapsById = orderedGaps
            .OrderBy(gap => gap.GapId)
            .ToArray();
        var resourcesById = capacities.Keys.ToArray();
        var sourceNode = 0;
        var firstGapNode = 1;
        var firstResourceNode = firstGapNode + gapsById.Length;
        var sinkNode = firstResourceNode + resourcesById.Length;
        var graph = Enumerable
            .Range(0, sinkNode + 1)
            .Select(_ => new List<ResidualFlowEdge>())
            .ToArray();
        var gapNodes = gapsById
            .Select((gap, index) => (gap.GapId, Node: firstGapNode + index))
            .ToDictionary(entry => entry.GapId, entry => entry.Node);
        var resourceNodes = resourcesById
            .Select((resourceId, index) => (ResourceId: resourceId, Node: firstResourceNode + index))
            .ToDictionary(entry => entry.ResourceId, entry => entry.Node);

        foreach (var gap in gapsById)
        {
            AddFlowEdge(graph, sourceNode, gapNodes[gap.GapId], 1, BigInteger.Zero);
        }
        foreach (var resourceId in resourcesById)
        {
            AddFlowEdge(
                graph,
                resourceNodes[resourceId],
                sinkNode,
                Math.Min(capacities[resourceId], assignmentLimit),
                BigInteger.Zero);
        }

        // Двійкові ваги точно відтворюють попередній лексикографічний вибір
        // після максимізації кількості та мінімізації звичайної вартості.
        var candidatesByTieOrder = candidates
            .OrderBy(candidate => candidate.GapId)
            .ThenBy(candidate => candidate.CandidateId)
            .ToArray();
        if (!searchBudget.CanContinueOperation())
        {
            return CreateResult(best, searchBudget, visitedNodesBefore);
        }

        var tieBase = BigInteger.One << candidatesByTieOrder.Length;
        var candidateFlowEdges = new List<CandidateFlowEdge>(candidatesByTieOrder.Length);
        for (var rank = 0; rank < candidatesByTieOrder.Length; rank++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!searchBudget.CanContinueOperation())
            {
                return CreateResult(best, searchBudget, visitedNodesBefore);
            }

            var candidate = candidatesByTieOrder[rank];
            var lexicographicReward = BigInteger.One << (candidatesByTieOrder.Length - rank - 1);
            var compositeCost = (BigInteger)candidate.Cost * tieBase
                                + tieBase
                                - lexicographicReward;
            var edge = AddFlowEdge(
                graph,
                gapNodes[candidate.GapId],
                resourceNodes[candidate.ResourceId],
                1,
                compositeCost,
                candidate);
            candidateFlowEdges.Add(new CandidateFlowEdge(candidate, edge));
        }

        var potentials = new BigInteger[graph.Length];
        var completedAssignments = 0;
        while (completedAssignments < assignmentLimit && !searchBudget.SearchLimitReached)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var shortestPath = FindShortestAugmentingPath(
                graph,
                sourceNode,
                sinkNode,
                potentials,
                searchBudget,
                cancellationToken);
            if (shortestPath.Status is not (ShortestPathStatus.Found or ShortestPathStatus.FoundWithSearchLimit))
            {
                break;
            }

            if (shortestPath.Status == ShortestPathStatus.Found)
            {
                for (var node = 0; node < graph.Length; node++)
                {
                    if (shortestPath.Distances[node] is { } distance)
                    {
                        potentials[node] += distance;
                    }
                }
            }

            var pathNode = sinkNode;
            while (pathNode != sourceNode)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var previousNode = shortestPath.PreviousNodes[pathNode];
                var previousEdge = shortestPath.PreviousEdges[pathNode];
                if (previousNode < 0 || previousEdge < 0)
                {
                    throw new InvalidOperationException(
                        "Знайдений залишковий шлях не містить повного ланцюжка до джерела.");
                }

                var edge = graph[previousNode][previousEdge];
                edge.Capacity--;
                graph[edge.To][edge.ReverseIndex].Capacity++;
                pathNode = previousNode;
            }

            completedAssignments++;
            var current = ExtractPlacements(candidateFlowEdges);
            if (IsPlanBetter(current, best))
            {
                best = current;
            }
        }

        return CreateResult(best, searchBudget, visitedNodesBefore);
    }

    private static DeterministicResidualOptimizationResult CreateResult(
        IEnumerable<ResidualPlacement> placements,
        DeterministicSearchBudget searchBudget,
        int visitedNodesBefore)
        => new(
            placements
                .OrderBy(placement => placement.GapId)
                .ThenBy(placement => placement.CandidateId)
                .ToArray(),
            searchBudget.VisitedNodes - visitedNodesBefore,
            searchBudget.NodeLimitReached,
            searchBudget.EmergencyLimitReached);

    private static ResidualFlowEdge AddFlowEdge(
        IReadOnlyList<List<ResidualFlowEdge>> graph,
        int from,
        int to,
        int capacity,
        BigInteger cost,
        ResidualPlacementCandidate? candidate = null)
    {
        var forward = new ResidualFlowEdge(to, graph[to].Count, capacity, cost, candidate);
        var reverse = new ResidualFlowEdge(from, graph[from].Count, 0, -cost, null);
        graph[from].Add(forward);
        graph[to].Add(reverse);
        return forward;
    }

    private static ShortestPathResult FindShortestAugmentingPath(
        IReadOnlyList<List<ResidualFlowEdge>> graph,
        int sourceNode,
        int sinkNode,
        IReadOnlyList<BigInteger> potentials,
        DeterministicSearchBudget searchBudget,
        CancellationToken cancellationToken)
    {
        var distances = new BigInteger?[graph.Count];
        var previousNodes = Enumerable.Repeat(-1, graph.Count).ToArray();
        var previousEdges = Enumerable.Repeat(-1, graph.Count).ToArray();
        var settled = new bool[graph.Count];
        var queue = new PriorityQueue<int, QueuePriority>(QueuePriorityComparer.Instance);
        distances[sourceNode] = BigInteger.Zero;
        queue.Enqueue(sourceNode, new QueuePriority(BigInteger.Zero, sourceNode));

        ShortestPathStatus SearchLimitStatus()
            => settled[sinkNode]
                ? ShortestPathStatus.FoundWithSearchLimit
                : ShortestPathStatus.SearchLimitReached;

        while (queue.TryDequeue(out var node, out var priority))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (settled[node]
                || distances[node] is not { } distance
                || priority.Distance != distance)
            {
                continue;
            }
            // Сток не є вершиною вибору: його обробка завершує або перенаправляє шлях,
            // тому бюджет рахує лише детерміновані розгортання джерела, прогалин і ресурсів.
            if (node != sinkNode && !searchBudget.TryVisitNode())
            {
                return new ShortestPathResult(
                    SearchLimitStatus(),
                    previousNodes,
                    previousEdges,
                    distances);
            }

            settled[node] = true;
            for (var edgeIndex = 0; edgeIndex < graph[node].Count; edgeIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!searchBudget.CanContinueOperation())
                {
                    return new ShortestPathResult(
                        SearchLimitStatus(),
                        previousNodes,
                        previousEdges,
                        distances);
                }

                var edge = graph[node][edgeIndex];
                if (edge.Capacity <= 0)
                {
                    continue;
                }

                var reducedCost = edge.Cost + potentials[node] - potentials[edge.To];
                if (reducedCost < BigInteger.Zero)
                {
                    throw new InvalidOperationException(
                        "Залишкова мережа містить від'ємну зведену вартість.");
                }
                var nextDistance = distance + reducedCost;
                if (distances[edge.To] is { } knownDistance && nextDistance >= knownDistance)
                {
                    continue;
                }

                distances[edge.To] = nextDistance;
                previousNodes[edge.To] = node;
                previousEdges[edge.To] = edgeIndex;
                queue.Enqueue(edge.To, new QueuePriority(nextDistance, edge.To));
            }
        }

        return new ShortestPathResult(
            distances[sinkNode] is null
                ? ShortestPathStatus.Unreachable
                : ShortestPathStatus.Found,
            previousNodes,
            previousEdges,
            distances);
    }

    private static List<ResidualPlacement> ExtractPlacements(
        IEnumerable<CandidateFlowEdge> candidateFlowEdges)
        => candidateFlowEdges
            .Where(candidateEdge => candidateEdge.Edge.Capacity == 0)
            .Select(candidateEdge => new ResidualPlacement(
                candidateEdge.Candidate.CandidateId,
                candidateEdge.Candidate.GapId,
                candidateEdge.Candidate.ResourceId,
                candidateEdge.Candidate.Cost))
            .OrderBy(placement => placement.GapId)
            .ThenBy(placement => placement.CandidateId)
            .ToList();

    private static bool IsPlanBetter(
        IReadOnlyCollection<ResidualPlacement> candidate,
        IReadOnlyCollection<ResidualPlacement> incumbent)
    {
        if (candidate.Count != incumbent.Count)
        {
            return candidate.Count > incumbent.Count;
        }

        var candidateCost = candidate.Sum(placement => (long)placement.Cost);
        var incumbentCost = incumbent.Sum(placement => (long)placement.Cost);
        if (candidateCost != incumbentCost)
        {
            return candidateCost < incumbentCost;
        }

        var candidateOrdered = candidate
            .OrderBy(placement => placement.GapId)
            .ThenBy(placement => placement.CandidateId)
            .ToArray();
        var incumbentOrdered = incumbent
            .OrderBy(placement => placement.GapId)
            .ThenBy(placement => placement.CandidateId)
            .ToArray();
        for (var index = 0; index < candidateOrdered.Length; index++)
        {
            var gapComparison = candidateOrdered[index].GapId.CompareTo(incumbentOrdered[index].GapId);
            if (gapComparison != 0)
            {
                return gapComparison < 0;
            }
            var candidateComparison = candidateOrdered[index].CandidateId.CompareTo(incumbentOrdered[index].CandidateId);
            if (candidateComparison != 0)
            {
                return candidateComparison < 0;
            }
        }
        return false;
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
