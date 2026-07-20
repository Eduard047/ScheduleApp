using BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class LogicalEventRotationPlannerTests
{
    [Fact]
    public void Plan_is_deterministic_and_moves_pivot_to_requested_position()
    {
        var items = new[]
        {
            new LogicalEventRotationItem(1, 10, 101, 0, false),
            new LogicalEventRotationItem(2, 10, 102, 1, true),
            new LogicalEventRotationItem(3, 10, 103, 2, false)
        };

        var firstPlanned = LogicalEventRotationPlanner.TryPlan(
            items,
            2,
            new Dictionary<int, int>(),
            new HashSet<int>(),
            100,
            out var firstPositions);
        var secondPlanned = LogicalEventRotationPlanner.TryPlan(
            items.Reverse().ToArray(),
            2,
            new Dictionary<int, int>(),
            new HashSet<int>(),
            100,
            out var secondPositions);

        Assert.True(firstPlanned);
        Assert.True(secondPlanned);
        Assert.Equal(firstPositions, secondPositions);
        Assert.Equal(2, firstPositions[2]);
        Assert.Equal(new[] { 1, 2, 3 }, firstPositions.Keys);
    }

    [Fact]
    public void Plan_preserves_within_module_and_configured_main_group_order()
    {
        var items = new[]
        {
            new LogicalEventRotationItem(1, 10, 101, 0, false),
            new LogicalEventRotationItem(2, 10, 102, 1, false),
            new LogicalEventRotationItem(3, 10, 103, 2, true),
            new LogicalEventRotationItem(4, 10, 999, 3, false),
            new LogicalEventRotationItem(5, 20, 101, 0, false),
            new LogicalEventRotationItem(6, 20, 102, 1, false),
            new LogicalEventRotationItem(7, 20, 103, 2, true),
            new LogicalEventRotationItem(8, 20, 999, 3, false)
        };
        var moduleOrder = new Dictionary<int, int>
        {
            [101] = 1,
            [102] = 2,
            [103] = 3
        };

        var planned = LogicalEventRotationPlanner.TryPlan(
            items,
            3,
            moduleOrder,
            new HashSet<int> { 999 },
            100,
            out var positions);

        Assert.True(planned);
        Assert.Equal(3, positions[3]);
        Assert.Equal(3, positions[7]);
        foreach (var groupItems in items.GroupBy(item => item.GroupId))
        {
            var configuredOrder = groupItems
                .Where(item => moduleOrder.ContainsKey(item.ModuleId))
                .OrderBy(item => positions[item.ItemId])
                .Select(item => moduleOrder[item.ModuleId])
                .ToArray();
            Assert.Equal(configuredOrder.OrderBy(value => value), configuredOrder);
        }
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 5)]
    public void Plan_rejects_missing_pivot_or_target(bool includePivot, int targetPosition)
    {
        var items = new[]
        {
            new LogicalEventRotationItem(1, 10, 101, 0, false),
            new LogicalEventRotationItem(2, 10, 102, 1, includePivot),
            new LogicalEventRotationItem(3, 10, 103, 2, false)
        };

        var planned = LogicalEventRotationPlanner.TryPlan(
            items,
            targetPosition,
            new Dictionary<int, int>(),
            new HashSet<int>(),
            100,
            out var positions);

        Assert.False(planned);
        Assert.Empty(positions);
    }

    [Fact]
    public void Plan_requires_one_pivot_in_every_group()
    {
        var items = new[]
        {
            new LogicalEventRotationItem(1, 10, 101, 0, true),
            new LogicalEventRotationItem(2, 10, 102, 1, false),
            new LogicalEventRotationItem(3, 20, 101, 0, false),
            new LogicalEventRotationItem(4, 20, 102, 1, false)
        };

        var planned = LogicalEventRotationPlanner.TryPlan(
            items,
            1,
            new Dictionary<int, int>(),
            new HashSet<int>(),
            100,
            out var positions);

        Assert.False(planned);
        Assert.Empty(positions);
    }

    [Fact]
    public void Plan_respects_permutation_bound()
    {
        var items = new[]
        {
            new LogicalEventRotationItem(1, 10, 101, 0, false),
            new LogicalEventRotationItem(2, 10, 102, 1, true),
            new LogicalEventRotationItem(3, 10, 103, 2, false),
            new LogicalEventRotationItem(4, 10, 999, 3, false)
        };
        var moduleOrder = new Dictionary<int, int>
        {
            [101] = 1,
            [102] = 2,
            [103] = 3
        };

        var boundedPlan = LogicalEventRotationPlanner.TryPlan(
            items,
            2,
            moduleOrder,
            new HashSet<int> { 999 },
            1,
            out var boundedPositions);
        var completePlan = LogicalEventRotationPlanner.TryPlan(
            items,
            2,
            moduleOrder,
            new HashSet<int> { 999 },
            2,
            out var completePositions);

        Assert.False(boundedPlan);
        Assert.Empty(boundedPositions);
        Assert.True(completePlan);
        Assert.Equal(2, completePositions[2]);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "batch-1")]
    public void Plan_never_moves_locked_or_batched_items(bool isLocked, string? batchKey)
    {
        var items = new[]
        {
            new LogicalEventRotationItem(1, 10, 101, 0, false, isLocked, batchKey),
            new LogicalEventRotationItem(2, 10, 102, 1, true),
            new LogicalEventRotationItem(3, 10, 103, 2, false)
        };

        var planned = LogicalEventRotationPlanner.TryPlan(
            items,
            2,
            new Dictionary<int, int>(),
            new HashSet<int>(),
            100,
            out var positions);

        Assert.False(planned);
        Assert.Empty(positions);
    }
}
