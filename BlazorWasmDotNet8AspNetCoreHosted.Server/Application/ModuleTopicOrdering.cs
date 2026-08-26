using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application;

// Призначає тимчасові значення, які не перетинаються ані з поточним, ані з фінальним порядком тем.
public static class ModuleTopicOrdering
{
    public static int FindUnusedTemporaryOrder(IEnumerable<int> occupiedOrders, int finalOrderCount = 0)
    {
        var allocator = CreateTemporaryOrderAllocator(occupiedOrders, finalOrderCount);
        return allocator.Take();
    }

    public static TemporaryOrderAllocator CreateTemporaryOrderAllocator(
        IEnumerable<int> occupiedOrders,
        int finalOrderCount = 0)
        => new(occupiedOrders, finalOrderCount);

    public static void AssignCollisionFreeTemporaryOrders(IReadOnlyList<ModuleTopic> topics)
    {
        var allocator = CreateTemporaryOrderAllocator(
            topics.Select(topic => topic.Order),
            topics.Count);
        foreach (var topic in topics)
        {
            topic.Order = allocator.Take();
        }
    }

    public sealed class TemporaryOrderAllocator
    {
        private readonly HashSet<int> _occupied;
        private readonly int _finalOrderCount;
        private long _candidate = int.MinValue;

        internal TemporaryOrderAllocator(IEnumerable<int> occupiedOrders, int finalOrderCount)
        {
            _occupied = occupiedOrders.ToHashSet();
            _finalOrderCount = Math.Max(0, finalOrderCount);
        }

        public int Take()
        {
            while (_candidate <= int.MaxValue)
            {
                var candidate = (int)_candidate++;
                if (_occupied.Contains(candidate)
                    || candidate is >= 1 && candidate <= _finalOrderCount)
                {
                    continue;
                }

                _occupied.Add(candidate);
                return candidate;
            }

            throw new InvalidOperationException(
                "Не вдалося підібрати безпечний тимчасовий порядок тем модуля.");
        }
    }
}
