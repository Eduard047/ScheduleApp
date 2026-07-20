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
