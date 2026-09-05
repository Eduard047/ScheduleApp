namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

public sealed record ConflictComponentCandidate<T>(int Id, int Cost, T Value);
public sealed record ConflictComponentDomain<T>(int EventId, IReadOnlyList<ConflictComponentCandidate<T>> Candidates);
public sealed record ConflictComponentResult<T>(IReadOnlyList<T> Placements, bool SearchLimitReached, int VisitedNodes);

public static class BoundedConflictComponentSolver
{
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
        if (domains.Count is 0 or > 12 || domains.Sum(d => (long)d.Candidates.Count) > 2_048)
            return new(Array.Empty<T>(), true, 0);
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
        bool Visit()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (nodes >= maxNodes || !budget.TryVisitNode()) { limited = true; return false; }
            nodes++;
            return true;
        }
        async Task<bool> Search()
        {
            if (assigned.Count == ordered.Length)
            {
                if (checks++ >= maxCompleteChecks) { limited = true; return false; }
                var proposed = ordered.Select(d => assigned[d.EventId]).ToArray();
                if (!await acceptComplete(proposed)) return false;
                cancellationToken.ThrowIfCancellationRequested();
                solution = proposed;
                return true;
            }
            ConflictComponentDomain<T>? selected = null;
            List<ConflictComponentCandidate<T>>? choices = null;
            foreach (var domain in ordered.Where(d => !assigned.ContainsKey(d.EventId)))
            {
                var available = new List<ConflictComponentCandidate<T>>();
                foreach (var candidate in domain.Candidates)
                {
                    if (!Visit()) return false;
                    if (!assigned.Values.Any(value => conflicts(value, candidate.Value))) available.Add(candidate);
                }
                if (available.Count == 0) return false;
                if (choices is null || available.Count < choices.Count) { selected = domain; choices = available; }
            }
            foreach (var candidate in choices!)
            {
                assigned[selected!.EventId] = candidate.Value;
                try { if (await Search()) return true; }
                finally { assigned.Remove(selected.EventId); }
                if (limited) return false;
            }
            return false;
        }
        await Search();
        return new(solution, limited, nodes);
    }
}
