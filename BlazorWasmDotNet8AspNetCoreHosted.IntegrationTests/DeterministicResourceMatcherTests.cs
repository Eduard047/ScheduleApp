using BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class DeterministicResourceMatcherTests
{
    [Fact]
    public void Match_assigns_scarce_resource_to_most_constrained_event()
    {
        var candidates = new Dictionary<int, List<int>>
        {
            [10] = new() { 102, 101, 102 },
            [20] = new() { 101 }
        };

        var matched = DeterministicResourceMatcher.TryMatchAll(candidates, out var assignment);

        Assert.True(matched);
        Assert.Equal(102, assignment[10]);
        Assert.Equal(101, assignment[20]);
    }

    [Fact]
    public void Match_uses_augmenting_path_deterministically()
    {
        var candidates = new Dictionary<int, int[]>
        {
            [10] = new[] { 102, 101 },
            [20] = new[] { 102, 101 }
        };

        var matched = DeterministicResourceMatcher.TryMatchAll(candidates, out var assignment);

        Assert.True(matched);
        Assert.Equal(102, assignment[10]);
        Assert.Equal(101, assignment[20]);
        Assert.Equal(new[] { 10, 20 }, assignment.Keys);
    }

    [Fact]
    public void Match_accepts_empty_event_set()
    {
        var matched = DeterministicResourceMatcher.TryMatchAll(
            new Dictionary<int, int[]>(),
            out var assignment);

        Assert.True(matched);
        Assert.Empty(assignment);
    }

    [Fact]
    public void Match_rejects_event_without_candidates_without_partial_assignment()
    {
        var candidates = new Dictionary<int, int[]>
        {
            [10] = new[] { 101 },
            [20] = Array.Empty<int>()
        };

        var matched = DeterministicResourceMatcher.TryMatchAll(candidates, out var assignment);

        Assert.False(matched);
        Assert.Empty(assignment);
    }

    [Fact]
    public void Match_rejects_incomplete_matching_without_partial_assignment()
    {
        var candidates = new Dictionary<int, int[]>
        {
            [10] = new[] { 101 },
            [20] = new[] { 101 }
        };

        var matched = DeterministicResourceMatcher.TryMatchAll(candidates, out var assignment);

        Assert.False(matched);
        Assert.Empty(assignment);
    }

    [Fact]
    public void Fallback_match_uses_only_preferred_resources_when_strict_solution_exists()
    {
        var preferred = new Dictionary<int, int[]>
        {
            [10] = new[] { 101, 102 },
            [20] = new[] { 101 }
        };
        var all = new Dictionary<int, int[]>
        {
            [10] = new[] { 1, 101, 102 },
            [20] = new[] { 2, 101 }
        };

        var matched = DeterministicResourceMatcher.TryMatchAllMinimizeFallback(
            preferred,
            all,
            100_000,
            out var assignment,
            out var fallbackCount,
            out var searchLimitReached);

        Assert.True(matched);
        Assert.False(searchLimitReached);
        Assert.Equal(0, fallbackCount);
        Assert.Equal(102, assignment[10]);
        Assert.Equal(101, assignment[20]);
    }

    [Fact]
    public void Fallback_match_uses_one_reserve_resource_only_after_strict_hall_failure()
    {
        var preferred = new Dictionary<int, int[]>
        {
            [10] = new[] { 101 },
            [20] = new[] { 101 }
        };
        var all = new Dictionary<int, int[]>
        {
            [10] = new[] { 1, 101 },
            [20] = new[] { 2, 101 }
        };

        var matched = DeterministicResourceMatcher.TryMatchAllMinimizeFallback(
            preferred,
            all,
            100_000,
            out var assignment,
            out var fallbackCount,
            out var searchLimitReached);

        Assert.True(matched);
        Assert.False(searchLimitReached);
        Assert.Equal(1, fallbackCount);
        Assert.Equal(101, assignment[10]);
        Assert.Equal(2, assignment[20]);
    }

    [Fact]
    public void Fallback_match_finds_minimum_when_hall_deficit_requires_two_reserve_resources()
    {
        var preferred = new Dictionary<int, int[]>
        {
            [10] = new[] { 101 },
            [20] = new[] { 101 },
            [30] = new[] { 101 }
        };
        var all = new Dictionary<int, int[]>
        {
            [10] = new[] { 1, 101 },
            [20] = new[] { 2, 101 },
            [30] = new[] { 3, 101 }
        };

        var matched = DeterministicResourceMatcher.TryMatchAllMinimizeFallback(
            preferred,
            all,
            100_000,
            out var assignment,
            out var fallbackCount,
            out var searchLimitReached);

        Assert.True(matched);
        Assert.False(searchLimitReached);
        Assert.Equal(2, fallbackCount);
        Assert.Equal(101, assignment[10]);
        Assert.Equal(2, assignment[20]);
        Assert.Equal(3, assignment[30]);
    }

    [Fact]
    public void Fallback_match_is_deterministic_for_equivalent_inputs()
    {
        var preferred = new Dictionary<int, int[]>
        {
            [20] = new[] { 101 },
            [10] = new[] { 101 }
        };
        var all = new Dictionary<int, int[]>
        {
            [20] = new[] { 2, 1, 101 },
            [10] = new[] { 2, 1, 101 }
        };

        var firstMatched = DeterministicResourceMatcher.TryMatchAllMinimizeFallback(
            preferred,
            all,
            100_000,
            out var firstAssignment,
            out var firstFallbackCount,
            out var firstSearchLimitReached);
        var secondMatched = DeterministicResourceMatcher.TryMatchAllMinimizeFallback(
            preferred,
            all,
            100_000,
            out var secondAssignment,
            out var secondFallbackCount,
            out var secondSearchLimitReached);

        Assert.True(firstMatched);
        Assert.True(secondMatched);
        Assert.False(firstSearchLimitReached);
        Assert.False(secondSearchLimitReached);
        Assert.Equal(1, firstFallbackCount);
        Assert.Equal(firstFallbackCount, secondFallbackCount);
        Assert.Equal(firstAssignment, secondAssignment);
        Assert.Equal(101, firstAssignment[10]);
        Assert.Equal(1, firstAssignment[20]);
    }

    [Fact]
    public void Fallback_match_rejects_mismatched_event_sets()
    {
        var preferred = new Dictionary<int, int[]>
        {
            [10] = new[] { 101 }
        };
        var all = new Dictionary<int, int[]>
        {
            [20] = new[] { 101 }
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            DeterministicResourceMatcher.TryMatchAllMinimizeFallback(
                preferred,
                all,
                100_000,
                out _,
                out _,
                out _));

        Assert.Equal("allResourcesByEvent", exception.ParamName);
    }

    [Fact]
    public void Fallback_match_rejects_preferred_resource_outside_full_pool()
    {
        var preferred = new Dictionary<int, int[]>
        {
            [10] = new[] { 102 }
        };
        var all = new Dictionary<int, int[]>
        {
            [10] = new[] { 101 }
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            DeterministicResourceMatcher.TryMatchAllMinimizeFallback(
                preferred,
                all,
                100_000,
                out _,
                out _,
                out _));

        Assert.Equal("preferredResourcesByEvent", exception.ParamName);
    }

    [Fact]
    public void Fallback_match_reports_search_limit_without_partial_assignment()
    {
        var preferred = new Dictionary<int, int[]>
        {
            [10] = new[] { 101, 102 },
            [20] = new[] { 101, 102 }
        };
        var all = new Dictionary<int, int[]>
        {
            [10] = new[] { 101, 102 },
            [20] = new[] { 101, 102 }
        };

        var matched = DeterministicResourceMatcher.TryMatchAllMinimizeFallback(
            preferred,
            all,
            1,
            out var assignment,
            out var fallbackCount,
            out var searchLimitReached);

        Assert.False(matched);
        Assert.True(searchLimitReached);
        Assert.Empty(assignment);
        Assert.Equal(0, fallbackCount);
    }

    [Fact]
    public void Fallback_match_starts_from_preferred_hall_deficiency_before_search_budget()
    {
        var preferredPool = Enumerable.Range(1, 9).ToArray();
        var preferred = Enumerable.Range(1, 10)
            .ToDictionary(eventId => eventId, _ => preferredPool);
        var all = Enumerable.Range(1, 10)
            .ToDictionary(
                eventId => eventId,
                eventId => preferredPool.Append(100 + eventId).ToArray());

        var matched = DeterministicResourceMatcher.TryMatchAllMinimizeFallback(
            preferred,
            all,
            100_000,
            out var assignment,
            out var fallbackCount,
            out var searchLimitReached);

        Assert.True(matched);
        Assert.False(searchLimitReached);
        Assert.Equal(10, assignment.Count);
        Assert.Equal(1, fallbackCount);
    }
}

public sealed class DeterministicResidualOptimizerTests
{
    [Fact]
    public void Optimize_preserves_scarce_resource_for_the_only_compatible_gap()
    {
        var candidates = new[]
        {
            new ResidualPlacementCandidate(1, 10, 100, 0),
            new ResidualPlacementCandidate(2, 10, 200, 1),
            new ResidualPlacementCandidate(3, 20, 100, 0)
        };
        var capacities = new Dictionary<int, int>
        {
            [100] = 1,
            [200] = 1
        };

        var result = DeterministicResidualOptimizer.Optimize(
            candidates,
            capacities,
            2,
            10_000,
            TimeSpan.FromSeconds(5));

        Assert.False(result.SearchLimitReached);
        Assert.Equal(2, result.FilledGapCount);
        Assert.Collection(
            result.Placements,
            placement =>
            {
                Assert.Equal(10, placement.GapId);
                Assert.Equal(200, placement.ResourceId);
            },
            placement =>
            {
                Assert.Equal(20, placement.GapId);
                Assert.Equal(100, placement.ResourceId);
            });
    }

    [Fact]
    public void Optimize_minimizes_cost_after_maximizing_filled_gaps()
    {
        var candidates = new[]
        {
            new ResidualPlacementCandidate(1, 10, 100, 9),
            new ResidualPlacementCandidate(2, 10, 200, 1),
            new ResidualPlacementCandidate(3, 20, 100, 1),
            new ResidualPlacementCandidate(4, 20, 200, 8)
        };
        var capacities = new Dictionary<int, int>
        {
            [100] = 1,
            [200] = 1
        };

        var result = DeterministicResidualOptimizer.Optimize(
            candidates,
            capacities,
            2,
            10_000,
            TimeSpan.FromSeconds(5));

        Assert.False(result.SearchLimitReached);
        Assert.Equal(2, result.TotalCost);
        Assert.Equal(new[] { 2, 3 }, result.Placements.Select(placement => placement.CandidateId));
    }

    [Fact]
    public void Optimize_returns_identical_plan_for_shuffled_input()
    {
        var candidates = new[]
        {
            new ResidualPlacementCandidate(30, 30, 300, 1),
            new ResidualPlacementCandidate(11, 10, 100, 1),
            new ResidualPlacementCandidate(21, 20, 200, 1),
            new ResidualPlacementCandidate(10, 10, 200, 1),
            new ResidualPlacementCandidate(20, 20, 300, 1),
            new ResidualPlacementCandidate(31, 30, 100, 1)
        };
        var capacities = new Dictionary<int, int>
        {
            [100] = 1,
            [200] = 1,
            [300] = 1
        };

        var first = DeterministicResidualOptimizer.Optimize(
            candidates,
            capacities,
            3,
            10_000,
            TimeSpan.FromSeconds(5));
        var second = DeterministicResidualOptimizer.Optimize(
            candidates.Reverse(),
            capacities.Reverse().ToDictionary(entry => entry.Key, entry => entry.Value),
            3,
            10_000,
            TimeSpan.FromSeconds(5));

        Assert.False(first.SearchLimitReached);
        Assert.False(second.SearchLimitReached);
        Assert.Equal(first.Placements, second.Placements);
    }

    [Fact]
    public void Optimize_preserves_legacy_lexicographic_tie_break()
    {
        var candidates = new[]
        {
            new ResidualPlacementCandidate(21, 20, 200, 0),
            new ResidualPlacementCandidate(10, 10, 100, 0),
            new ResidualPlacementCandidate(20, 20, 100, 0),
            new ResidualPlacementCandidate(11, 10, 200, 0)
        };
        var capacities = new Dictionary<int, int>
        {
            [100] = 1,
            [200] = 1
        };

        var result = DeterministicResidualOptimizer.Optimize(
            candidates,
            capacities,
            2,
            100,
            TimeSpan.FromSeconds(5));

        Assert.False(result.SearchLimitReached);
        Assert.Equal(new[] { 10, 21 }, result.Placements.Select(placement => placement.CandidateId));
    }

    [Fact]
    public void Optimize_solves_dense_matrix_with_polynomial_node_budget()
    {
        const int gapCount = 20;
        const int resourceCount = 20;
        var candidates = Enumerable
            .Range(0, gapCount)
            .SelectMany(gapIndex => Enumerable
                .Range(0, resourceCount)
                .Select(resourceIndex => new ResidualPlacementCandidate(
                    gapIndex * resourceCount + resourceIndex + 1,
                    gapIndex + 1,
                    resourceIndex + 101,
                    gapIndex == resourceIndex ? 0 : 1)))
            .ToArray();
        var capacities = Enumerable
            .Range(0, resourceCount)
            .ToDictionary(resourceIndex => resourceIndex + 101, _ => 1);

        var result = DeterministicResidualOptimizer.Optimize(
            candidates,
            capacities,
            gapCount,
            1_000,
            TimeSpan.FromSeconds(5));

        Assert.False(result.SearchLimitReached);
        Assert.Equal(gapCount, result.FilledGapCount);
        Assert.Equal(0, result.TotalCost);
        Assert.Equal(
            Enumerable.Range(0, gapCount).Select(index => index * resourceCount + index + 1),
            result.Placements.Select(placement => placement.CandidateId));
        Assert.InRange(result.VisitedNodes, 1, 1_000);
    }

    [Fact]
    public void Optimize_reports_node_limit_and_keeps_deterministic_warm_start()
    {
        var candidates = new[]
        {
            new ResidualPlacementCandidate(1, 10, 100, 0),
            new ResidualPlacementCandidate(2, 10, 200, 0),
            new ResidualPlacementCandidate(3, 20, 100, 0),
            new ResidualPlacementCandidate(4, 20, 200, 0)
        };
        var capacities = new Dictionary<int, int>
        {
            [100] = 1,
            [200] = 1
        };

        var first = DeterministicResidualOptimizer.Optimize(
            candidates,
            capacities,
            2,
            1,
            TimeSpan.FromSeconds(5));
        var second = DeterministicResidualOptimizer.Optimize(
            candidates.Reverse(),
            capacities,
            2,
            1,
            TimeSpan.FromSeconds(5));

        Assert.True(first.NodeLimitReached);
        Assert.False(first.EmergencyLimitReached);
        Assert.Equal(2, first.FilledGapCount);
        Assert.Equal(first.Placements, second.Placements);
    }

    [Fact]
    public void Optimize_applies_proven_shortest_path_when_later_expansion_reaches_node_limit()
    {
        var candidates = new[]
        {
            new ResidualPlacementCandidate(1, 10, 100, 100),
            new ResidualPlacementCandidate(2, 20, 100, 0),
            new ResidualPlacementCandidate(3, 20, 200, 0)
        };
        var capacities = new Dictionary<int, int>
        {
            [100] = 1,
            [200] = 1
        };

        var result = DeterministicResidualOptimizer.Optimize(
            candidates,
            capacities,
            1,
            4,
            TimeSpan.FromSeconds(5));

        Assert.True(result.NodeLimitReached);
        Assert.False(result.EmergencyLimitReached);
        Assert.Equal(0, result.TotalCost);
        Assert.Equal(new[] { 2 }, result.Placements.Select(placement => placement.CandidateId));
    }

    [Fact]
    public void Optimize_reports_total_cost_as_long_without_overflow()
    {
        var candidates = new[]
        {
            new ResidualPlacementCandidate(1, 10, 100, int.MaxValue),
            new ResidualPlacementCandidate(2, 20, 200, int.MaxValue)
        };
        var capacities = new Dictionary<int, int>
        {
            [100] = 1,
            [200] = 1
        };

        var result = DeterministicResidualOptimizer.Optimize(
            candidates,
            capacities,
            2,
            100,
            TimeSpan.FromSeconds(5));

        Assert.False(result.SearchLimitReached);
        Assert.Equal(2L * int.MaxValue, result.TotalCost);
    }

    [Fact]
    public void Optimize_stops_composite_cost_construction_on_emergency_timeout()
    {
        const int candidateCount = 128;
        var candidates = Enumerable.Range(1, candidateCount)
            .Select(index => new ResidualPlacementCandidate(index, index, 100, 0))
            .ToArray();
        var capacities = new Dictionary<int, int>
        {
            [100] = candidateCount
        };
        var budget = new DeterministicSearchBudget(
            10_000,
            TimeSpan.FromSeconds(3),
            new AdvancingTimeProvider());

        var result = DeterministicResidualOptimizer.Optimize(
            candidates,
            capacities,
            candidateCount,
            budget);

        Assert.True(result.EmergencyLimitReached);
        Assert.False(result.NodeLimitReached);
        Assert.Equal(0, result.VisitedNodes);
        Assert.Equal(candidateCount, result.FilledGapCount);
    }

    [Fact]
    public void Optimize_consumes_one_shared_budget_across_invocations()
    {
        var candidates = new[]
        {
            new ResidualPlacementCandidate(1, 10, 100, 0)
        };
        var capacities = new Dictionary<int, int>
        {
            [100] = 1
        };
        var budget = new DeterministicSearchBudget(3, TimeSpan.FromSeconds(5));

        var first = DeterministicResidualOptimizer.Optimize(
            candidates,
            capacities,
            1,
            budget);
        var second = DeterministicResidualOptimizer.Optimize(
            candidates,
            capacities,
            1,
            budget);

        Assert.False(first.SearchLimitReached);
        Assert.Equal(3, first.VisitedNodes);
        Assert.True(second.NodeLimitReached);
        Assert.Equal(0, second.VisitedNodes);
        Assert.Equal(3, budget.VisitedNodes);
    }

    [Fact]
    public async Task TryApplyPlanAtomicallyAsync_commits_only_after_every_placement_improves_cardinality()
    {
        var placements = new[]
        {
            new ResidualPlacement(1, 10, 100, 0),
            new ResidualPlacement(2, 20, 200, 0)
        };
        var filledGaps = new HashSet<int>();
        var rollbackCalled = false;

        var result = await DeterministicResidualOptimizer.TryApplyPlanAtomicallyAsync(
            placements,
            (placement, _) => Task.FromResult(filledGaps.Add(placement.GapId)),
            () => filledGaps.Count,
            () => rollbackCalled = true);

        Assert.True(result.Committed);
        Assert.Equal(2, result.AppliedPlacements);
        Assert.Equal(0, result.CardinalityBefore);
        Assert.Equal(2, result.CardinalityAfter);
        Assert.False(rollbackCalled);
        Assert.Equal(new[] { 10, 20 }, filledGaps.OrderBy(gapId => gapId));
    }

    [Fact]
    public async Task TryApplyPlanAtomicallyAsync_rolls_back_partial_application()
    {
        var placements = new[]
        {
            new ResidualPlacement(1, 10, 100, 0),
            new ResidualPlacement(2, 20, 200, 0)
        };
        var filledGaps = new List<int> { 5 };
        var snapshot = filledGaps.ToArray();
        var rollbackCalled = false;

        var result = await DeterministicResidualOptimizer.TryApplyPlanAtomicallyAsync(
            placements,
            (placement, _) =>
            {
                if (placement.GapId == 20)
                {
                    return Task.FromResult(false);
                }
                filledGaps.Add(placement.GapId);
                return Task.FromResult(true);
            },
            () => filledGaps.Count,
            () =>
            {
                rollbackCalled = true;
                filledGaps.Clear();
                filledGaps.AddRange(snapshot);
            });

        Assert.False(result.Committed);
        Assert.Equal(1, result.AppliedPlacements);
        Assert.Equal(1, result.CardinalityBefore);
        Assert.Equal(1, result.CardinalityAfter);
        Assert.True(rollbackCalled);
        Assert.Equal(snapshot, filledGaps);
    }

    [Fact]
    public async Task TryApplyPlanAtomicallyAsync_rolls_back_when_success_does_not_improve_cardinality()
    {
        var placements = new[]
        {
            new ResidualPlacement(1, 10, 100, 0),
            new ResidualPlacement(2, 20, 200, 0)
        };
        var cardinality = 1;
        var rollbackCalled = false;

        var result = await DeterministicResidualOptimizer.TryApplyPlanAtomicallyAsync(
            placements,
            (_, _) => Task.FromResult(true),
            () => cardinality,
            () => rollbackCalled = true);

        Assert.False(result.Committed);
        Assert.Equal(2, result.AppliedPlacements);
        Assert.Equal(1, result.CardinalityBefore);
        Assert.Equal(1, result.CardinalityAfter);
        Assert.True(rollbackCalled);
    }

    [Fact]
    public void Search_budget_stops_at_exact_deterministic_node_limit()
    {
        var budget = new DeterministicSearchBudget(2, TimeSpan.FromSeconds(5));

        Assert.True(budget.TryVisitNode());
        Assert.True(budget.TryVisitNode());
        Assert.False(budget.TryVisitNode());
        Assert.Equal(2, budget.VisitedNodes);
        Assert.True(budget.NodeLimitReached);
        Assert.False(budget.EmergencyLimitReached);
    }

    [Fact]
    public void Search_budget_reports_emergency_timeout_separately()
    {
        var timeProvider = new ManualTimeProvider();
        var budget = new DeterministicSearchBudget(
            10,
            TimeSpan.FromSeconds(1),
            timeProvider);

        Assert.True(budget.TryVisitNode());
        timeProvider.Advance(TimeSpan.FromSeconds(2));

        Assert.False(budget.TryVisitNode());
        Assert.False(budget.NodeLimitReached);
        Assert.True(budget.EmergencyLimitReached);
        Assert.Equal(1, budget.VisitedNodes);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan value)
        {
            _timestamp += value.Ticks;
        }
    }

    private sealed class AdvancingTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            var current = _timestamp;
            _timestamp += TimeSpan.TicksPerSecond;
            return current;
        }
    }
}
