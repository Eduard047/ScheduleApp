namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

public sealed record ConflictComponentCandidate<T>(int Id, int Cost, T Value);
public sealed record ConflictComponentDomain<T>(int EventId, IReadOnlyList<ConflictComponentCandidate<T>> Candidates);
public enum ConflictComponentSearchStopReason
{
    None,
    CandidateDomainLimit,
    LocalNodeLimit,
    LocalCompleteCheckLimit,
    SharedBudgetLimit
}

public static class ConflictComponentSearchDiagnostics
{
    public static string? GetScope(ConflictComponentSearchStopReason reason) => reason switch
    {
        ConflictComponentSearchStopReason.CandidateDomainLimit => "conflict-component-candidate-domain-limit",
        ConflictComponentSearchStopReason.LocalNodeLimit or ConflictComponentSearchStopReason.LocalCompleteCheckLimit
            => "conflict-component-local-solver-cap",
        ConflictComponentSearchStopReason.SharedBudgetLimit => "conflict-component-shared-cap",
        _ => null
    };
}

public sealed record ConflictComponentResult<T>(
    IReadOnlyList<T> Placements,
    bool SearchLimitReached,
    int VisitedNodes,
    ConflictComponentSearchStopReason StopReason = ConflictComponentSearchStopReason.None);

public static class BoundedConflictComponentSolver
{
    public static IReadOnlyList<T> SampleAcrossDomains<T>(
        IReadOnlyList<IReadOnlyList<T>> domains,
        int candidateLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(candidateLimit);
        if (domains.Count == 0) return Array.Empty<T>();

        var availableDomainIndexes = Enumerable.Range(0, domains.Count)
            .Where(index => domains[index].Count > 0)
            .ToArray();
        if (availableDomainIndexes.Length == 0) return Array.Empty<T>();

        var seedCount = Math.Min(availableDomainIndexes.Length, candidateLimit);
        var selectedPositions = SelectIndexesEvenly(availableDomainIndexes.Length, seedCount);
        var selected = selectedPositions
            .Select(position => domains[availableDomainIndexes[position]][0])
            .ToList();

        // Спершу беремо по одному варіанту з кожної доступної комірки. Решту
        // розподіляємо рівними колами, щоб ширина домену не витісняла інші комірки.
        for (var candidateIndex = 1; selected.Count < candidateLimit; candidateIndex++)
        {
            var eligiblePositions = Enumerable.Range(0, availableDomainIndexes.Length)
                .Where(position => domains[availableDomainIndexes[position]].Count > candidateIndex)
                .ToArray();
            if (eligiblePositions.Length == 0) break;

            var takeCount = Math.Min(candidateLimit - selected.Count, eligiblePositions.Length);
            foreach (var position in SelectIndexesEvenly(eligiblePositions.Length, takeCount))
            {
                var domainIndex = availableDomainIndexes[eligiblePositions[position]];
                selected.Add(domains[domainIndex][candidateIndex]);
            }
        }
        return selected;
    }

    private static int[] SelectIndexesEvenly(int itemCount, int selectedCount)
    {
        if (selectedCount <= 0 || itemCount <= 0) return Array.Empty<int>();
        if (selectedCount >= itemCount) return Enumerable.Range(0, itemCount).ToArray();
        if (selectedCount == 1) return [0];
        return Enumerable.Range(0, selectedCount)
            .Select(index => (int)((long)index * (itemCount - 1) / (selectedCount - 1)))
            .ToArray();
    }

    // Стан пошуку містить лише призначення компоненти. Невдала гілка не змінює EF або розклад.
    public static async Task<ConflictComponentResult<T>> SolveAsync<T>(
        IReadOnlyList<ConflictComponentDomain<T>> domains,
        Func<T, T, bool> conflicts,
        Func<IReadOnlyList<T>, Task<bool>> acceptComplete,
        DeterministicSearchBudget budget,
        CancellationToken cancellationToken = default,
        int maxNodes = 40_000,
        int maxCompleteChecks = 64)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maxNodes <= 0 || maxCompleteChecks <= 0) throw new ArgumentOutOfRangeException(nameof(maxNodes));
        if (domains.Count == 0) return new(Array.Empty<T>(), false, 0);
        if (domains.Count > 12 || domains.Sum(d => (long)d.Candidates.Count) > 2_048)
            return new(Array.Empty<T>(), true, 0, ConflictComponentSearchStopReason.CandidateDomainLimit);
        if (domains.Select(d => d.EventId).Distinct().Count() != domains.Count
            || domains.Any(d => d.Candidates.Select(c => c.Id).Distinct().Count() != d.Candidates.Count))
            throw new ArgumentException("Ідентифікатори подій та їхніх варіантів мають бути унікальними.", nameof(domains));
        var ordered = domains.OrderBy(d => d.EventId).Select(d => d with
        { Candidates = d.Candidates.OrderBy(c => c.Cost).ThenBy(c => c.Id).ToArray() }).ToArray();
        var assigned = new Dictionary<int, T>();
        IReadOnlyList<T> solution = Array.Empty<T>();
        var nodes = 0;
        var checks = 0;
        var limited = false;
        var stopReason = ConflictComponentSearchStopReason.None;
        bool Visit()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (nodes >= maxNodes)
            {
                limited = true;
                stopReason = ConflictComponentSearchStopReason.LocalNodeLimit;
                return false;
            }
            if (!budget.TryVisitNode())
            {
                limited = true;
                stopReason = ConflictComponentSearchStopReason.SharedBudgetLimit;
                return false;
            }
            nodes++;
            return true;
        }
        // Кожна гілка успадковує вже відфільтровані варіанти. Не перевіряємо
        // заново відкинуті комбінації проти всіх попередніх призначень.
        async Task<bool> Search(IReadOnlyList<ConflictComponentCandidate<T>>?[] availableDomains)
        {
            if (assigned.Count == ordered.Length)
            {
                if (checks >= maxCompleteChecks)
                {
                    limited = true;
                    stopReason = ConflictComponentSearchStopReason.LocalCompleteCheckLimit;
                    return false;
                }
                checks++;
                var proposed = ordered.Select(d => assigned[d.EventId]).ToArray();
                var accepted = await acceptComplete(proposed);
                cancellationToken.ThrowIfCancellationRequested();
                if (!accepted) return false;
                solution = proposed;
                return true;
            }
            ConflictComponentDomain<T>? selected = null;
            List<ConflictComponentCandidate<T>>? choices = null;
            var selectedIndex = -1;
            for (var index = 0; index < ordered.Length; index++)
            {
                var available = availableDomains[index];
                if (available is null) continue;
                if (available.Count == 0) return false;
                if (choices is null || available.Count < choices.Count)
                { selected = ordered[index]; selectedIndex = index; choices = available.ToList(); }
            }
            foreach (var candidate in choices!)
            {
                if (!Visit()) return false;
                assigned[selected!.EventId] = candidate.Value;
                try
                {
                    var next = (IReadOnlyList<ConflictComponentCandidate<T>>?[])availableDomains.Clone();
                    next[selectedIndex] = null;
                    var feasible = true;
                    for (var index = 0; index < next.Length; index++)
                    {
                        if (next[index] is not { } remaining) continue;
                        var compatible = new List<ConflictComponentCandidate<T>>(remaining.Count);
                        foreach (var other in remaining)
                        {
                            if (!Visit()) return false;
                            if (!conflicts(candidate.Value, other.Value)) compatible.Add(other);
                        }
                        if (compatible.Count == 0) { feasible = false; break; }
                        next[index] = compatible;
                    }
                    if (feasible && await Search(next)) return true;
                }
                finally { assigned.Remove(selected.EventId); }
                if (limited) return false;
            }
            return false;
        }
        var initial = ordered.Select(domain => (IReadOnlyList<ConflictComponentCandidate<T>>?)domain.Candidates).ToArray();
        foreach (var domain in ordered)
        {
            if (domain.Candidates.Count == 0) return new(Array.Empty<T>(), false, nodes);
            foreach (var _ in domain.Candidates)
                if (!Visit()) return new(Array.Empty<T>(), true, nodes, stopReason);
        }
        await Search(initial);
        return new(solution, limited, nodes, stopReason);
    }
}
