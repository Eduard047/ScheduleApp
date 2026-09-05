namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

public sealed record ResourceTimeDemand(int Required, IReadOnlyCollection<int> AvailableResourceSlots);
public sealed record ResourceCapacityAnalysis(int Required, int Matched, bool SearchComplete)
{
    public int? ProvenShortfall => SearchComplete ? Required - Matched : null;
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
        while (matched < required)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parentNode = Enumerable.Repeat(-1, graph.Length).ToArray();
            var parentEdge = new int[graph.Length];
            var queue = new Queue<int>();
            parentNode[0] = 0;
            queue.Enqueue(0);
            while (queue.Count > 0 && parentNode[sink] < 0)
            {
                var node = queue.Dequeue();
                for (var i = 0; i < graph[node].Count; i++)
                {
                    if (++visits > maxEdgeVisits) return new(required, matched, false);
                    if ((visits & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var edge = graph[node][i];
                    if (edge.Capacity == 0 || parentNode[edge.To] >= 0) continue;
                    parentNode[edge.To] = node;
                    parentEdge[edge.To] = i;
                    queue.Enqueue(edge.To);
                }
            }
            if (parentNode[sink] < 0) return new(required, matched, true);
            for (var node = sink; node != 0; node = parentNode[node])
            {
                var edge = graph[parentNode[node]][parentEdge[node]];
                edge.Capacity--;
                graph[node][edge.Reverse].Capacity++;
            }
            matched++;
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
