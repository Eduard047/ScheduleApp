namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

public sealed record LogicalEventRotationItem(
    int ItemId,
    int GroupId,
    int ModuleId,
    int OriginalPosition,
    bool IsSharedPivot,
    bool IsLocked = false,
    string? BatchKey = null);

public static class LogicalEventRotationPlanner
{
    // Будує детерміноване обертання хвоста дня, не змінюючи порядок занять усередині модуля.
    public static bool TryPlan(
        IReadOnlyCollection<LogicalEventRotationItem> items,
        int targetPosition,
        IReadOnlyDictionary<int, int> moduleGroupOrder,
        IReadOnlySet<int> fillerModuleIds,
        int maxPermutationsPerGroup,
        out IReadOnlyDictionary<int, int> positionByItemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(moduleGroupOrder);
        ArgumentNullException.ThrowIfNull(fillerModuleIds);
        cancellationToken.ThrowIfCancellationRequested();
        if (maxPermutationsPerGroup <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPermutationsPerGroup));
        }
        if (items.Any(item => item.IsLocked || !string.IsNullOrWhiteSpace(item.BatchKey)))
        {
            positionByItemId = new SortedDictionary<int, int>();
            return false;
        }

        var planned = new SortedDictionary<int, int>();
        foreach (var group in items
                     .GroupBy(item => item.GroupId)
                     .OrderBy(group => group.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var groupItems = group
                .OrderBy(item => item.OriginalPosition)
                .ThenBy(item => item.ItemId)
                .ToList();
            var pivots = groupItems.Where(item => item.IsSharedPivot).ToList();
            var positions = groupItems
                .Select(item => item.OriginalPosition)
                .Distinct()
                .OrderBy(position => position)
                .ToArray();
            if (pivots.Count != 1
                || positions.Length != groupItems.Count
                || !positions.Contains(targetPosition))
            {
                positionByItemId = new SortedDictionary<int, int>();
                return false;
            }

            var pivot = pivots[0];
            var movableItems = groupItems.Where(item => !item.IsSharedPivot).ToArray();
            var movablePositions = positions.Where(position => position != targetPosition).ToArray();
            var candidate = new LogicalEventRotationItem[movableItems.Length];
            var used = new bool[movableItems.Length];
            var visitedPermutations = 0;
            Dictionary<int, int>? accepted = null;

            bool IsValid(IReadOnlyDictionary<int, int> assignment)
            {
                foreach (var moduleItems in groupItems.GroupBy(item => item.ModuleId))
                {
                    var originalOrder = moduleItems
                        .OrderBy(item => item.OriginalPosition)
                        .ThenBy(item => item.ItemId)
                        .Select(item => item.ItemId)
                        .ToArray();
                    var rotatedOrder = moduleItems
                        .OrderBy(item => assignment[item.ItemId])
                        .ThenBy(item => item.ItemId)
                        .Select(item => item.ItemId)
                        .ToArray();
                    if (!originalOrder.SequenceEqual(rotatedOrder))
                    {
                        return false;
                    }
                }

                var maxGroupOrder = int.MinValue;
                foreach (var item in groupItems
                             .OrderBy(item => assignment[item.ItemId])
                             .ThenBy(item => item.ItemId))
                {
                    if (fillerModuleIds.Contains(item.ModuleId)
                        || !moduleGroupOrder.TryGetValue(item.ModuleId, out var groupOrder))
                    {
                        continue;
                    }
                    if (groupOrder < maxGroupOrder)
                    {
                        return false;
                    }
                    maxGroupOrder = Math.Max(maxGroupOrder, groupOrder);
                }
                return true;
            }

            bool Search(int depth)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (depth == candidate.Length)
                {
                    if (++visitedPermutations > maxPermutationsPerGroup)
                    {
                        return false;
                    }
                    var assignment = new Dictionary<int, int>
                    {
                        [pivot.ItemId] = targetPosition
                    };
                    for (var index = 0; index < candidate.Length; index++)
                    {
                        assignment[candidate[index].ItemId] = movablePositions[index];
                    }
                    if (!IsValid(assignment))
                    {
                        return false;
                    }
                    accepted = assignment;
                    return true;
                }

                for (var index = 0; index < movableItems.Length; index++)
                {
                    if (used[index])
                    {
                        continue;
                    }
                    used[index] = true;
                    candidate[depth] = movableItems[index];
                    if (Search(depth + 1))
                    {
                        return true;
                    }
                    used[index] = false;
                    if (visitedPermutations >= maxPermutationsPerGroup)
                    {
                        return false;
                    }
                }
                return false;
            }

            if (!Search(0) || accepted is null)
            {
                positionByItemId = new SortedDictionary<int, int>();
                return false;
            }
            foreach (var assignment in accepted)
            {
                planned[assignment.Key] = assignment.Value;
            }
        }

        positionByItemId = planned;
        return true;
    }
}
