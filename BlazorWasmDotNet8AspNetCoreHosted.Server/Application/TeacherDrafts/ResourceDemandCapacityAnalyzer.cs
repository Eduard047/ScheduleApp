namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

public sealed record ResourceTimeDemand(int Required, IReadOnlyCollection<int> AvailableResourceSlots);
public sealed record ResourceCapacityAnalysis(int Required, int Matched, bool SearchComplete)
{
    public int? ProvenShortfall => SearchComplete ? Required - Matched : null;
    public IReadOnlyList<int> BottleneckDemandIndexes { get; init; } = Array.Empty<int>();
}

// Верхня межа спільної місткості: один ресурсний слот не можна порахувати для двох незалежних занять.
public static class ResourceDemandCapacityAnalyzer
{
    public static ResourceCapacityAnalysis Analyze(IReadOnlyList<ResourceTimeDemand> demands,
        CancellationToken cancellationToken = default, int maxEdgeVisits = 2_000_000)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (demands.Any(d => d.Required < 0)) throw new ArgumentOutOfRangeException(nameof(demands));
        var required = checked(demands.Sum(d => d.Required));
        var edges = demands.Sum(d => (long)d.AvailableResourceSlots.Count);
        if (demands.Count > 200 || edges > 250_000 || required > 20_000)
            return new(required, 0, false);
        var resources = demands.SelectMany(d => d.AvailableResourceSlots).Distinct().Order().ToArray();
        var resourceNodes = resources.Select((id, index) => (id, node: demands.Count + index + 1)).ToDictionary(p => p.id, p => p.node);
        var sink = demands.Count + resources.Length + 1;
        var graph = Enumerable.Range(0, sink + 1).Select(_ => new List<Edge>()).ToArray();
        void AddEdge(int from, int to, int capacity)
        {
            graph[from].Add(new Edge(to, graph[to].Count, capacity));
            graph[to].Add(new Edge(from, graph[from].Count - 1, 0));
        }
        for (var i = 0; i < demands.Count; i++)
        {
            AddEdge(0, i + 1, demands[i].Required);
            foreach (var slot in demands[i].AvailableResourceSlots.Distinct().Order()) AddEdge(i + 1, resourceNodes[slot], 1);
        }
        foreach (var node in resourceNodes.Values) AddEdge(node, sink, 1);
        var visits = 0;
        var matched = 0;
        var levels = new int[graph.Length];
        var nextEdges = new int[graph.Length];
        var limitReached = false;
        bool Visit()
        {
            if ((++visits & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            return !(limitReached = visits > maxEdgeVisits);
        }
        bool BuildLevels()
        {
            Array.Fill(levels, -1);
            var queue = new Queue<int>();
            levels[0] = 0;
            queue.Enqueue(0);
            while (queue.TryDequeue(out var node))
                foreach (var edge in graph[node])
                {
                    if (!Visit()) return false;
                    if (edge.Capacity <= 0 || levels[edge.To] >= 0) continue;
                    levels[edge.To] = levels[node] + 1;
                    queue.Enqueue(edge.To);
                }
            return levels[sink] >= 0;
        }
        int Send(int node, int available)
        {
            if (node == sink) return available;
            for (; nextEdges[node] < graph[node].Count; nextEdges[node]++)
            {
                if (!Visit()) return 0;
                var edge = graph[node][nextEdges[node]];
                if (edge.Capacity <= 0 || levels[edge.To] != levels[node] + 1) continue;
                var sent = Send(edge.To, Math.Min(available, edge.Capacity));
                if (limitReached) return 0;
                if (sent == 0) continue;
                edge.Capacity -= sent;
                graph[edge.To][edge.Reverse].Capacity += sent;
                return sent;
            }
            return 0;
        }
        // Один шаруватий прохід обслуговує багато пар без нового BFS та масивів для кожної пари.
        while (matched < required)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!BuildLevels())
            {
                return new(required, matched, !limitReached)
                {
                    BottleneckDemandIndexes = limitReached ? Array.Empty<int>()
                        : Enumerable.Range(0, demands.Count).Where(i => levels[i + 1] >= 0).ToArray()
                };
            }
            Array.Clear(nextEdges);
            while (matched < required)
            {
                var sent = Send(0, required - matched);
                if (limitReached) return new(required, matched, false);
                if (sent == 0) break;
                matched += sent;
            }
        }
        return new(required, matched, true);
    }

    private sealed class Edge(int to, int reverse, int capacity)
    {
        public int To { get; } = to;
        public int Reverse { get; } = reverse;
        public int Capacity { get; set; } = capacity;
    }
}
