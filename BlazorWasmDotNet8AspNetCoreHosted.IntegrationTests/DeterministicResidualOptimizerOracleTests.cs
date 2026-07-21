using BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class DeterministicResidualOptimizerOracleTests
{
    private const int ExactSearchNodeLimit = 1_000_000;

    [Theory]
    [InlineData(17)]
    [InlineData(7_919)]
    [InlineData(104_729)]
    [InlineData(2_147_483_629)]
    public void Optimize_matches_brute_force_oracle_for_seeded_small_matrices(int seed)
    {
        var random = new StableRandom(unchecked((uint)seed));

        for (var scenarioIndex = 0; scenarioIndex < 100; scenarioIndex++)
        {
            var scenario = CreateScenario(random, scenarioIndex);
            var expected = SolveWithBruteForce(
                scenario.Candidates,
                scenario.Capacities,
                scenario.MaxAssignments);

            var actual = DeterministicResidualOptimizer.Optimize(
                scenario.Candidates,
                scenario.Capacities,
                scenario.MaxAssignments,
                ExactSearchNodeLimit,
                TimeSpan.FromSeconds(30));

            Assert.False(
                actual.SearchLimitReached,
                $"Сценарій {scenarioIndex} для початкового значення {seed} несподівано вичерпав бюджет пошуку.");
            AssertMatchesOracle(expected, actual, seed, scenarioIndex);
            AssertRespectsConstraints(actual, scenario, seed, scenarioIndex);
        }
    }

    [Fact]
    public void Optimize_is_identical_for_seeded_input_and_capacity_shuffles()
    {
        var random = new StableRandom(0xC0FFEEu);

        for (var scenarioIndex = 0; scenarioIndex < 128; scenarioIndex++)
        {
            var scenario = CreateScenario(random, scenarioIndex);
            var expected = DeterministicResidualOptimizer.Optimize(
                scenario.Candidates,
                scenario.Capacities,
                scenario.MaxAssignments,
                ExactSearchNodeLimit,
                TimeSpan.FromSeconds(30));

            Assert.False(
                expected.SearchLimitReached,
                $"Еталонний запуск сценарію {scenarioIndex} несподівано вичерпав бюджет пошуку.");

            for (var shuffleIndex = 0; shuffleIndex < 4; shuffleIndex++)
            {
                var shuffledCandidates = scenario.Candidates.ToArray();
                random.Shuffle(shuffledCandidates);

                var shuffledCapacityEntries = scenario.Capacities.ToArray();
                random.Shuffle(shuffledCapacityEntries);
                var shuffledCapacities = shuffledCapacityEntries
                    .ToDictionary(entry => entry.Key, entry => entry.Value);

                var actual = DeterministicResidualOptimizer.Optimize(
                    shuffledCandidates,
                    shuffledCapacities,
                    scenario.MaxAssignments,
                    ExactSearchNodeLimit,
                    TimeSpan.FromSeconds(30));

                AssertEquivalentResults(expected, actual, scenarioIndex, shuffleIndex);
            }
        }
    }

    [Fact]
    public void Optimize_uses_stable_lexicographic_tie_break_after_cardinality_and_cost()
    {
        var candidates = new[]
        {
            new ResidualPlacementCandidate(301, 30, 2, 0),
            new ResidualPlacementCandidate(200, 20, 1, 0),
            new ResidualPlacementCandidate(101, 10, 2, 0),
            new ResidualPlacementCandidate(300, 30, 1, 0),
            new ResidualPlacementCandidate(201, 20, 2, 0),
            new ResidualPlacementCandidate(100, 10, 1, 0)
        };
        var capacities = new Dictionary<int, int>
        {
            [2] = 1,
            [1] = 1
        };

        var result = DeterministicResidualOptimizer.Optimize(
            candidates,
            capacities,
            2,
            ExactSearchNodeLimit,
            TimeSpan.FromSeconds(30));

        Assert.False(result.SearchLimitReached);
        Assert.Equal(2, result.FilledGapCount);
        Assert.Equal(0, result.TotalCost);
        Assert.Equal(new[] { 100, 201 }, result.Placements.Select(placement => placement.CandidateId));
    }

    [Fact]
    public void Optimize_never_exceeds_zero_and_positive_resource_capacities()
    {
        var candidates = Enumerable.Range(1, 6)
            .SelectMany(gapId => new[]
            {
                new ResidualPlacementCandidate(gapId * 10 + 1, gapId, 1, gapId % 3),
                new ResidualPlacementCandidate(gapId * 10 + 2, gapId, 2, 0),
                new ResidualPlacementCandidate(gapId * 10 + 3, gapId, 3, 1)
            })
            .ToArray();
        var capacities = new Dictionary<int, int>
        {
            [1] = 2,
            [2] = 0,
            [3] = 1
        };

        var expected = SolveWithBruteForce(candidates, capacities, 10);
        var actual = DeterministicResidualOptimizer.Optimize(
            candidates,
            capacities,
            10,
            ExactSearchNodeLimit,
            TimeSpan.FromSeconds(30));

        AssertMatchesOracle(expected, actual, 0, 0);
        Assert.Equal(3, actual.FilledGapCount);
        Assert.DoesNotContain(actual.Placements, placement => placement.ResourceId == 2);
        Assert.Equal(2, actual.Placements.Count(placement => placement.ResourceId == 1));
        Assert.Equal(1, actual.Placements.Count(placement => placement.ResourceId == 3));
    }

    [Fact]
    public void Optimize_consumes_shared_node_budget_cumulatively_and_exactly()
    {
        var candidates = new[]
        {
            new ResidualPlacementCandidate(1, 10, 100, 0)
        };
        var capacities = new Dictionary<int, int>
        {
            [100] = 1
        };
        var budget = new DeterministicSearchBudget(5, TimeSpan.FromSeconds(30));

        var first = DeterministicResidualOptimizer.Optimize(candidates, capacities, 1, budget);
        var second = DeterministicResidualOptimizer.Optimize(candidates, capacities, 1, budget);
        var third = DeterministicResidualOptimizer.Optimize(candidates, capacities, 1, budget);

        Assert.False(first.SearchLimitReached);
        Assert.Equal(3, first.VisitedNodes);
        Assert.True(second.NodeLimitReached);
        Assert.False(second.EmergencyLimitReached);
        Assert.Equal(2, second.VisitedNodes);
        Assert.True(third.NodeLimitReached);
        Assert.Equal(0, third.VisitedNodes);
        Assert.Equal(5, budget.VisitedNodes);
        Assert.Equal(first.Placements, second.Placements);
        Assert.Equal(first.Placements, third.Placements);
    }

    [Fact]
    public void Optimize_honors_cancellation_without_consuming_shared_budget()
    {
        var candidates = new[]
        {
            new ResidualPlacementCandidate(1, 10, 100, 0),
            new ResidualPlacementCandidate(2, 20, 100, 0)
        };
        var capacities = new Dictionary<int, int>
        {
            [100] = 2
        };
        var budget = new DeterministicSearchBudget(100, TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            DeterministicResidualOptimizer.Optimize(
                candidates,
                capacities,
                2,
                budget,
                cancellation.Token));
        Assert.Equal(0, budget.VisitedNodes);
        Assert.False(budget.SearchLimitReached);

        var completed = DeterministicResidualOptimizer.Optimize(candidates, capacities, 2, budget);

        Assert.False(completed.SearchLimitReached);
        Assert.Equal(2, completed.FilledGapCount);
        Assert.Equal(completed.VisitedNodes, budget.VisitedNodes);
    }

    [Fact]
    public void Optimize_interrupts_an_active_search_at_the_next_deterministic_node_boundary()
    {
        var candidates = Enumerable.Range(1, 6)
            .SelectMany(gapId => Enumerable.Range(1, 3)
                .Select(resourceId => new ResidualPlacementCandidate(
                    gapId * 10 + resourceId,
                    gapId,
                    resourceId,
                    0)))
            .ToArray();
        var capacities = new Dictionary<int, int>
        {
            [1] = 2,
            [2] = 2,
            [3] = 2
        };
        using var cancellation = new CancellationTokenSource();
        var timeProvider = new CancelingTimeProvider(cancellation, cancelAtTimestampRead: 3);
        var budget = new DeterministicSearchBudget(
            ExactSearchNodeLimit,
            TimeSpan.FromSeconds(30),
            timeProvider);

        Assert.Throws<OperationCanceledException>(() =>
            DeterministicResidualOptimizer.Optimize(
                candidates,
                capacities,
                6,
                budget,
                cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.InRange(budget.VisitedNodes, 0, ExactSearchNodeLimit - 1);
        Assert.False(budget.SearchLimitReached);
        var visitedAfterCancellation = budget.VisitedNodes;

        var resumed = DeterministicResidualOptimizer.Optimize(
            candidates,
            capacities,
            6,
            budget);

        Assert.False(resumed.SearchLimitReached);
        Assert.Equal(6, resumed.FilledGapCount);
        Assert.True(budget.VisitedNodes > visitedAfterCancellation);
    }

    private static MatrixScenario CreateScenario(StableRandom random, int scenarioIndex)
    {
        var gapCount = random.Next(1, 7);
        var resourceCount = random.Next(1, 5);
        var resources = Enumerable.Range(0, resourceCount)
            .Select(index => 101 + index * 17)
            .ToArray();
        var capacities = resources.ToDictionary(
            resourceId => resourceId,
            _ => random.Next(0, Math.Min(3, gapCount) + 1));

        var cells = new List<(int GapId, int ResourceId, int Cost)>();
        for (var gapIndex = 0; gapIndex < gapCount; gapIndex++)
        {
            var gapId = 10 + gapIndex * 13;
            foreach (var resourceId in resources)
            {
                if (random.Next(100) < 64)
                {
                    cells.Add((gapId, resourceId, random.Next(0, 8)));
                }
            }
        }

        var candidateIds = Enumerable.Range(1, cells.Count)
            .Select(value => scenarioIndex * 100 + value)
            .ToArray();
        random.Shuffle(candidateIds);

        var candidates = cells
            .Select((cell, index) => new ResidualPlacementCandidate(
                candidateIds[index],
                cell.GapId,
                cell.ResourceId,
                cell.Cost))
            .ToArray();
        random.Shuffle(candidates);

        return new MatrixScenario(
            candidates,
            capacities,
            random.Next(0, gapCount + 2));
    }

    private static OracleSolution SolveWithBruteForce(
        IReadOnlyList<ResidualPlacementCandidate> candidates,
        IReadOnlyDictionary<int, int> capacities,
        int maxAssignments)
    {
        var gaps = candidates
            .GroupBy(candidate => candidate.GapId)
            .OrderBy(group => group.Key)
            .Select(group => group.OrderBy(candidate => candidate.CandidateId).ToArray())
            .ToArray();
        var assignmentLimit = Math.Min(maxAssignments, gaps.Length);
        var remainingCapacity = capacities.ToDictionary(entry => entry.Key, entry => entry.Value);
        var current = new List<ResidualPlacement>(assignmentLimit);
        var best = new OracleSolution(Array.Empty<ResidualPlacement>());

        void Visit(int gapIndex)
        {
            if (gapIndex >= gaps.Length || current.Count >= assignmentLimit)
            {
                CaptureIfBetter();
                return;
            }

            foreach (var candidate in gaps[gapIndex])
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
                Visit(gapIndex + 1);
                current.RemoveAt(current.Count - 1);
                remainingCapacity[candidate.ResourceId]++;
            }

            Visit(gapIndex + 1);
        }

        void CaptureIfBetter()
        {
            var ordered = current
                .OrderBy(placement => placement.GapId)
                .ThenBy(placement => placement.CandidateId)
                .ToArray();
            var candidate = new OracleSolution(ordered);
            if (IsBetter(candidate, best))
            {
                best = candidate;
            }
        }

        Visit(0);
        return best;
    }

    private static bool IsBetter(OracleSolution candidate, OracleSolution currentBest)
    {
        if (candidate.Placements.Count != currentBest.Placements.Count)
        {
            return candidate.Placements.Count > currentBest.Placements.Count;
        }
        if (candidate.TotalCost != currentBest.TotalCost)
        {
            return candidate.TotalCost < currentBest.TotalCost;
        }

        for (var index = 0; index < candidate.Placements.Count; index++)
        {
            var candidatePlacement = candidate.Placements[index];
            var currentPlacement = currentBest.Placements[index];
            var gapComparison = candidatePlacement.GapId.CompareTo(currentPlacement.GapId);
            if (gapComparison != 0)
            {
                return gapComparison < 0;
            }

            var candidateComparison = candidatePlacement.CandidateId.CompareTo(currentPlacement.CandidateId);
            if (candidateComparison != 0)
            {
                return candidateComparison < 0;
            }
        }

        return false;
    }

    private static void AssertMatchesOracle(
        OracleSolution expected,
        DeterministicResidualOptimizationResult actual,
        int seed,
        int scenarioIndex)
    {
        Assert.True(
            expected.Placements.SequenceEqual(actual.Placements),
            $"Сценарій {scenarioIndex} для початкового значення {seed}: очікувався план "
            + $"[{FormatPlacements(expected.Placements)}], отримано [{FormatPlacements(actual.Placements)}].");
        Assert.Equal(expected.Placements.Count, actual.FilledGapCount);
        Assert.Equal(expected.TotalCost, actual.TotalCost);
    }

    private static void AssertRespectsConstraints(
        DeterministicResidualOptimizationResult actual,
        MatrixScenario scenario,
        int seed,
        int scenarioIndex)
    {
        Assert.True(
            actual.FilledGapCount <= scenario.MaxAssignments,
            $"Сценарій {scenarioIndex} для початкового значення {seed} перевищив загальний ліміт призначень.");
        Assert.True(
            actual.Placements.Select(placement => placement.GapId).Distinct().Count() == actual.FilledGapCount,
            $"Сценарій {scenarioIndex} для початкового значення {seed} призначив одну прогалину більше одного разу.");

        foreach (var (resourceId, capacity) in scenario.Capacities)
        {
            Assert.True(
                actual.Placements.Count(placement => placement.ResourceId == resourceId) <= capacity,
                $"Сценарій {scenarioIndex} для початкового значення {seed} перевищив місткість ресурсу #{resourceId}.");
        }

        var sourceByCandidateId = scenario.Candidates.ToDictionary(candidate => candidate.CandidateId);
        foreach (var placement in actual.Placements)
        {
            Assert.True(
                sourceByCandidateId.TryGetValue(placement.CandidateId, out var source)
                && source.GapId == placement.GapId
                && source.ResourceId == placement.ResourceId
                && source.Cost == placement.Cost,
                $"Сценарій {scenarioIndex} для початкового значення {seed} повернув кандидата, якого не було у вхідній матриці.");
        }
    }

    private static void AssertEquivalentResults(
        DeterministicResidualOptimizationResult expected,
        DeterministicResidualOptimizationResult actual,
        int scenarioIndex,
        int shuffleIndex)
    {
        Assert.True(
            expected.Placements.SequenceEqual(actual.Placements),
            $"Перемішування {shuffleIndex} змінило план сценарію {scenarioIndex}: "
            + $"[{FormatPlacements(expected.Placements)}] проти [{FormatPlacements(actual.Placements)}].");
        Assert.Equal(expected.TotalCost, actual.TotalCost);
        Assert.Equal(expected.VisitedNodes, actual.VisitedNodes);
        Assert.Equal(expected.NodeLimitReached, actual.NodeLimitReached);
        Assert.Equal(expected.EmergencyLimitReached, actual.EmergencyLimitReached);
    }

    private static string FormatPlacements(IEnumerable<ResidualPlacement> placements)
        => string.Join(
            ", ",
            placements.Select(placement =>
                $"кандидат={placement.CandidateId}; прогалина={placement.GapId}; ресурс={placement.ResourceId}; вартість={placement.Cost}"));

    private sealed record MatrixScenario(
        IReadOnlyList<ResidualPlacementCandidate> Candidates,
        IReadOnlyDictionary<int, int> Capacities,
        int MaxAssignments);

    private sealed record OracleSolution(IReadOnlyList<ResidualPlacement> Placements)
    {
        public int TotalCost => Placements.Sum(placement => placement.Cost);
    }

    private sealed class StableRandom
    {
        private uint _state;

        public StableRandom(uint seed)
        {
            _state = seed == 0 ? 0x9E3779B9u : seed;
        }

        public int Next(int exclusiveMaximum)
        {
            if (exclusiveMaximum <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(exclusiveMaximum));
            }

            return (int)(NextUInt32() % (uint)exclusiveMaximum);
        }

        public int Next(int inclusiveMinimum, int exclusiveMaximum)
        {
            if (inclusiveMinimum >= exclusiveMaximum)
            {
                throw new ArgumentOutOfRangeException(nameof(exclusiveMaximum));
            }

            return inclusiveMinimum + Next(exclusiveMaximum - inclusiveMinimum);
        }

        public void Shuffle<T>(T[] values)
        {
            for (var index = values.Length - 1; index > 0; index--)
            {
                var swapIndex = Next(index + 1);
                (values[index], values[swapIndex]) = (values[swapIndex], values[index]);
            }
        }

        private uint NextUInt32()
        {
            var value = _state;
            value ^= value << 13;
            value ^= value >> 17;
            value ^= value << 5;
            _state = value;
            return value;
        }
    }

    private sealed class CancelingTimeProvider(
        CancellationTokenSource cancellation,
        int cancelAtTimestampRead) : TimeProvider
    {
        private int _timestampReads;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            _timestampReads++;
            if (_timestampReads == cancelAtTimestampRead)
            {
                cancellation.Cancel();
            }

            return 0;
        }
    }
}
