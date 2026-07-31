namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

public static class DeterministicResourceMatcher
{
    // Шукає повне призначення ресурсів, віддаючи пріоритет подіям із найменшою кількістю варіантів.
    public static bool TryMatchAll<TCandidates>(
        IReadOnlyDictionary<int, TCandidates> candidateResourcesByEvent,
        out IReadOnlyDictionary<int, int> assignmentByEvent)
        where TCandidates : IEnumerable<int>
    {
        ArgumentNullException.ThrowIfNull(candidateResourcesByEvent);

        var candidates = new Dictionary<int, int[]>(candidateResourcesByEvent.Count);
        foreach (var entry in candidateResourcesByEvent)
        {
            if (entry.Value is null)
            {
                throw new ArgumentException(
                    $"Список кандидатів для події #{entry.Key} не може бути null.",
                    nameof(candidateResourcesByEvent));
            }

            candidates[entry.Key] = entry.Value
                .Distinct()
                .OrderBy(resourceId => resourceId)
                .ToArray();
        }

        var orderedEventIds = candidates
            .OrderBy(entry => entry.Value.Length)
            .ThenBy(entry => entry.Key)
            .Select(entry => entry.Key)
            .ToArray();
        var assignedEventByResource = new Dictionary<int, int>();
        var assignedResourceByEvent = new Dictionary<int, int>();

        bool TryAssign(int eventId, HashSet<int> visitedResourceIds)
        {
            foreach (var resourceId in candidates[eventId])
            {
                if (!visitedResourceIds.Add(resourceId))
                {
                    continue;
                }

                if (!assignedEventByResource.TryGetValue(resourceId, out var previousEventId)
                    || TryAssign(previousEventId, visitedResourceIds))
                {
                    assignedEventByResource[resourceId] = eventId;
                    assignedResourceByEvent[eventId] = resourceId;
                    return true;
                }
            }

            return false;
        }

        foreach (var eventId in orderedEventIds)
        {
            if (!TryAssign(eventId, new HashSet<int>()))
            {
                assignmentByEvent = new SortedDictionary<int, int>();
                return false;
            }
        }

        assignmentByEvent = new SortedDictionary<int, int>(assignedResourceByEvent);
        return true;
    }

    // Шукає повне призначення з мінімально можливою кількістю резервних ресурсів.
    public static bool TryMatchAllMinimizeFallback<TPreferredCandidates, TAllCandidates>(
        IReadOnlyDictionary<int, TPreferredCandidates> preferredResourcesByEvent,
        IReadOnlyDictionary<int, TAllCandidates> allResourcesByEvent,
        int maxSearchNodes,
        out IReadOnlyDictionary<int, int> assignmentByEvent,
        out int fallbackCount,
        out bool searchLimitReached,
        CancellationToken cancellationToken = default)
        where TPreferredCandidates : IEnumerable<int>
        where TAllCandidates : IEnumerable<int>
    {
        ArgumentNullException.ThrowIfNull(preferredResourcesByEvent);
        ArgumentNullException.ThrowIfNull(allResourcesByEvent);
        cancellationToken.ThrowIfCancellationRequested();
        if (maxSearchNodes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSearchNodes));
        }

        var preferredEventIds = preferredResourcesByEvent.Keys.OrderBy(eventId => eventId).ToArray();
        var allEventIds = allResourcesByEvent.Keys.OrderBy(eventId => eventId).ToArray();
        if (!preferredEventIds.SequenceEqual(allEventIds))
        {
            throw new ArgumentException(
                "Набори подій у пріоритетному та повному списках ресурсів мають збігатися.",
                nameof(allResourcesByEvent));
        }

        var preferred = new Dictionary<int, HashSet<int>>(preferredResourcesByEvent.Count);
        var orderedCandidates = new Dictionary<int, int[]>(allResourcesByEvent.Count);
        foreach (var eventId in allEventIds)
        {
            var preferredSource = preferredResourcesByEvent[eventId];
            var allSource = allResourcesByEvent[eventId];
            if (preferredSource is null)
            {
                throw new ArgumentException(
                    $"Пріоритетний список ресурсів для події #{eventId} не може бути null.",
                    nameof(preferredResourcesByEvent));
            }
            if (allSource is null)
            {
                throw new ArgumentException(
                    $"Повний список ресурсів для події #{eventId} не може бути null.",
                    nameof(allResourcesByEvent));
            }

            var preferredSet = preferredSource.ToHashSet();
            var allSet = allSource.ToHashSet();
            if (!preferredSet.IsSubsetOf(allSet))
            {
                throw new ArgumentException(
                    $"Пріоритетні ресурси події #{eventId} мають входити до її повного списку.",
                    nameof(preferredResourcesByEvent));
            }

            preferred[eventId] = preferredSet;
            orderedCandidates[eventId] = allSet
                .OrderBy(resourceId => preferredSet.Contains(resourceId) ? 0 : 1)
                .ThenBy(resourceId => resourceId)
                .ToArray();
        }

        if (orderedCandidates.Values.Any(candidates => candidates.Length == 0))
        {
            assignmentByEvent = new SortedDictionary<int, int>();
            fallbackCount = 0;
            searchLimitReached = false;
            return false;
        }

        var preferredEventByResource = new Dictionary<int, int>();
        bool TryAssignPreferred(int eventId, HashSet<int> visitedResourceIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var resourceId in preferred[eventId].OrderBy(resourceId => resourceId))
            {
                if (!visitedResourceIds.Add(resourceId))
                {
                    continue;
                }
                if (!preferredEventByResource.TryGetValue(resourceId, out var previousEventId)
                    || TryAssignPreferred(previousEventId, visitedResourceIds))
                {
                    preferredEventByResource[resourceId] = eventId;
                    return true;
                }
            }
            return false;
        }
        foreach (var eventId in allEventIds
                     .OrderBy(eventId => preferred[eventId].Count)
                     .ThenBy(eventId => eventId))
        {
            _ = TryAssignPreferred(eventId, new HashSet<int>());
        }
        var minimumFallbackBudget = orderedCandidates.Count - preferredEventByResource.Count;

        var visitedNodes = 0;
        var limitReached = false;
        for (var fallbackBudget = minimumFallbackBudget;
             fallbackBudget <= orderedCandidates.Count;
             fallbackBudget++)
        {
            var assigned = new Dictionary<int, int>();
            var usedResources = new HashSet<int>();

            bool Search(int usedFallbackCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (limitReached)
                {
                    return false;
                }
                if (assigned.Count == orderedCandidates.Count)
                {
                    return true;
                }
                if (++visitedNodes > maxSearchNodes)
                {
                    limitReached = true;
                    return false;
                }

                var remaining = orderedCandidates
                    .Where(entry => !assigned.ContainsKey(entry.Key))
                    .Select(entry =>
                    {
                        var viable = entry.Value
                            .Where(resourceId => !usedResources.Contains(resourceId)
                                                 && (preferred[entry.Key].Contains(resourceId)
                                                     || usedFallbackCount < fallbackBudget))
                            .ToArray();
                        var minimumAdditionalFallback = viable.Length == 0
                            ? int.MaxValue
                            : viable.Any(resourceId => preferred[entry.Key].Contains(resourceId)) ? 0 : 1;
                        return new
                        {
                            EventId = entry.Key,
                            Viable = viable,
                            MinimumAdditionalFallback = minimumAdditionalFallback
                        };
                    })
                    .ToArray();
                if (remaining.Any(entry => entry.Viable.Length == 0)
                    || usedFallbackCount + remaining.Sum(entry => (long)entry.MinimumAdditionalFallback) > fallbackBudget)
                {
                    return false;
                }

                var next = remaining
                    .OrderBy(entry => entry.Viable.Length)
                    .ThenBy(entry => entry.EventId)
                    .First();
                foreach (var resourceId in next.Viable)
                {
                    var isFallback = !preferred[next.EventId].Contains(resourceId);
                    var nextFallbackCount = usedFallbackCount + (isFallback ? 1 : 0);
                    if (nextFallbackCount > fallbackBudget)
                    {
                        continue;
                    }

                    assigned[next.EventId] = resourceId;
                    usedResources.Add(resourceId);
                    if (Search(nextFallbackCount))
                    {
                        return true;
                    }
                    usedResources.Remove(resourceId);
                    assigned.Remove(next.EventId);
                    if (limitReached)
                    {
                        return false;
                    }
                }

                return false;
            }

            if (Search(0))
            {
                assignmentByEvent = new SortedDictionary<int, int>(assigned);
                fallbackCount = assigned.Count(entry => !preferred[entry.Key].Contains(entry.Value));
                searchLimitReached = false;
                return true;
            }
            if (limitReached)
            {
                break;
            }
        }

        assignmentByEvent = new SortedDictionary<int, int>();
        fallbackCount = 0;
        searchLimitReached = limitReached;
        return false;
    }

    // Шукає повне призначення з додатковими взаємними обмеженнями між подіями.
    public static bool TrySolveConstrained<TCandidates>(
        IReadOnlyDictionary<int, TCandidates> candidateResourcesByEvent,
        Func<int, int, IReadOnlyDictionary<int, int>, bool> isCompatible,
        int maxSearchNodes,
        out IReadOnlyDictionary<int, int> assignmentByEvent,
        out bool searchLimitReached,
        CancellationToken cancellationToken = default)
        where TCandidates : IEnumerable<int>
    {
        ArgumentNullException.ThrowIfNull(candidateResourcesByEvent);
        ArgumentNullException.ThrowIfNull(isCompatible);
        cancellationToken.ThrowIfCancellationRequested();
        if (maxSearchNodes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSearchNodes));
        }

        var candidates = new Dictionary<int, int[]>(candidateResourcesByEvent.Count);
        foreach (var entry in candidateResourcesByEvent)
        {
            if (entry.Value is null)
            {
                throw new ArgumentException(
                    $"Список кандидатів для події #{entry.Key} не може бути null.",
                    nameof(candidateResourcesByEvent));
            }

            candidates[entry.Key] = entry.Value
                .Distinct()
                .ToArray();
        }

        if (candidates.Values.Any(value => value.Length == 0))
        {
            assignmentByEvent = new SortedDictionary<int, int>();
            searchLimitReached = false;
            return false;
        }

        var assigned = new Dictionary<int, int>();
        var visitedNodes = 0;
        var limitReached = false;

        bool Search()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (limitReached)
            {
                return false;
            }
            if (assigned.Count == candidates.Count)
            {
                return true;
            }
            if (++visitedNodes > maxSearchNodes)
            {
                limitReached = true;
                return false;
            }

            var next = candidates
                .Where(entry => !assigned.ContainsKey(entry.Key))
                .Select(entry => new
                {
                    EventId = entry.Key,
                    Compatible = entry.Value
                        .Where(resourceId => isCompatible(entry.Key, resourceId, assigned))
                        .ToArray()
                })
                .OrderBy(entry => entry.Compatible.Length)
                .ThenBy(entry => entry.EventId)
                .First();
            if (next.Compatible.Length == 0)
            {
                return false;
            }

            foreach (var resourceId in next.Compatible)
            {
                assigned[next.EventId] = resourceId;
                if (Search())
                {
                    return true;
                }
                assigned.Remove(next.EventId);
                if (limitReached)
                {
                    return false;
                }
            }

            return false;
        }

        if (!Search())
        {
            assignmentByEvent = new SortedDictionary<int, int>();
            searchLimitReached = limitReached;
            return false;
        }

        assignmentByEvent = new SortedDictionary<int, int>(assigned);
        searchLimitReached = false;
        return true;
    }
}
