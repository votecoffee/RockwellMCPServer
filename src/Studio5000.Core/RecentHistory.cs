namespace Studio5000.Core;

internal static class RecentHistory
{
    private readonly record struct Candidate<T>(T Record, DateTimeOffset CreatedAt, long Order);

    internal static T[] Latest<T>(IEnumerable<T> records, Func<T, DateTimeOffset> createdAt, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var selected = new PriorityQueue<Candidate<T>, (DateTimeOffset, long)>();
        long order = 0;
        foreach (var record in records)
        {
            var candidate = new Candidate<T>(record, createdAt(record), order++);
            // Oldest timestamp, then latest encounter, is the first to discard.
            // Read every receipt so malformed history still fails the request.
            var priority = (candidate.CreatedAt, -candidate.Order);
            if (selected.Count < limit) selected.Enqueue(candidate, priority);
            else selected.EnqueueDequeue(candidate, priority);
        }
        return selected.UnorderedItems.Select(item => item.Element)
            .OrderByDescending(item => item.CreatedAt).ThenBy(item => item.Order).Select(item => item.Record).ToArray();
    }
}
