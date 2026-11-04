using Studio5000.Core;
using Xunit;
using Xunit.Abstractions;

namespace Studio5000.Tests;

public sealed class RecentHistoryTests(ITestOutputHelper output)
{
    private sealed record Receipt(int Id, DateTimeOffset CreatedAt, byte[] Result);

    [Fact]
    public void ListingDoesNotRootDiscardedResultsWhileReadingHistory()
    {
        const int limit = 3;
        var references = new List<WeakReference<Receipt>>();
        var retainedAtEnd = -1;
        IEnumerable<Receipt> ReadHistory()
        {
            for (var i = 0; i < 32; i++)
            {
                // Equal newest timestamps also verify the existing stable tie ordering.
                var item = new Receipt(i, DateTimeOffset.UnixEpoch.AddSeconds(i < 2 ? 0 : -i), new byte[64 * 1024]);
                references.Add(new(item));
                yield return item;
            }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            retainedAtEnd = references.Count(reference => reference.TryGetTarget(out _));
        }

        var latest = RecentHistory.Latest(ReadHistory(), item => item.CreatedAt, limit);

        Assert.Equal(new[] { 0, 1, 2 }, latest.Select(item => item.Id));
        Assert.All(latest, item => Assert.Equal(64 * 1024, item.Result.Length));
        output.WriteLine($"Retained result graphs at enumeration end: {retainedAtEnd}; returned limit: {limit}; input records: 32.");
        Assert.True(retainedAtEnd <= limit + 2,
            $"Listing retained {retainedAtEnd} result graphs while enumerating history; only the {limit} returned records and current enumeration state may remain rooted.");

        // Newer records arrive late; ties at both the newest timestamp and the
        // selection cutoff must preserve their original encounter order.
        var shuffled = new[] { latest[0], latest[1], latest[2], latest[0] with { Id = 3, CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(1) },
            latest[0] with { Id = 4, CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(1) } };
        Assert.Equal(new[] { 3, 4, 0 }, RecentHistory.Latest(shuffled, item => item.CreatedAt, limit).Select(item => item.Id));

        var disposed = false;
        IEnumerable<Receipt> MalformedHistory()
        {
            try { foreach (var item in latest) yield return item; throw new InvalidDataException("Malformed old receipt."); }
            finally { disposed = true; }
        }
        Assert.Throws<InvalidDataException>(() => RecentHistory.Latest(MalformedHistory(), item => item.CreatedAt, 1));
        Assert.True(disposed);
    }
}
