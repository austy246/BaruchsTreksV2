using BaruchsTreks.Trips;

namespace BaruchsTreks.Tests.Unit;

public class TripListingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Trip Make(string id, string title, string completed = "", int? modifiedDays = null,
        int? createdDays = null, int? completedDays = null) => new()
    {
        Id = id,
        Title = title,
        TripCompletedOn = completed,
        ModifiedAt = modifiedDays is { } m ? T0.AddDays(m) : null,
        RowKeyTime = createdDays is { } c ? T0.AddDays(c) : null,
        CompletedAt = completedDays is { } d ? T0.AddDays(d) : null,
    };

    private static readonly Trip[] Sample =
    [
        Make("a", "Bravo", "2025-01-01", modifiedDays: 5, createdDays: 1, completedDays: 10),
        Make("b", "alpha", "", modifiedDays: 3, createdDays: 3),
        Make("c", "Charlie", "2025-03-01", createdDays: 9, completedDays: 20), // bez ModifiedAt: padá na RowKeyTime
        Make("d", "delta", "2025-02-01"),                                       // bez všech časů: nejstarší
    ];

    private static string Ids(IEnumerable<Trip> trips) => string.Concat(trips.Select(t => t.Id));

    [Fact]
    public void Listing_FilterAndSort_MatchDjango()
    {
        Assert.Equal("acd", Ids(TripListing.Filter(Sample, "completed")));
        Assert.Equal("b", Ids(TripListing.Filter(Sample, "future")));
        Assert.Equal("abcd", Ids(TripListing.Filter(Sample, "all")));
        Assert.Equal("abcd", Ids(TripListing.Filter(Sample, "něco jiného")));

        // modified (výchozí): ModifiedAt ?? RowKeyTime ?? nejstarší; sestupně
        Assert.Equal("cabd", Ids(TripListing.Sort(Sample, "modified", "desc")));
        Assert.Equal("dbac", Ids(TripListing.Sort(Sample, "modified", "asc")));
        Assert.Equal("cabd", Ids(TripListing.Sort(Sample, "neznámý klíč", "desc")));

        // dir jiný než asc znamená sestupně
        Assert.Equal("cabd", Ids(TripListing.Sort(Sample, "modified", "whatever")));

        // created: jen RowKeyTime
        Assert.Equal("cbad", Ids(TripListing.Sort(Sample, "created", "desc")));

        // completed: jen CompletedAt (b a d bez data zůstanou na konci, stabilně)
        Assert.Equal("cabd", Ids(TripListing.Sort(Sample, "completed", "desc")));
        Assert.Equal("bdac", Ids(TripListing.Sort(Sample, "completed", "asc")));

        // name: malá písmena, ordinal
        Assert.Equal("bacd", Ids(TripListing.Sort(Sample, "name", "asc")));
        Assert.Equal("dcab", Ids(TripListing.Sort(Sample, "name", "desc")));
    }

    [Fact]
    public void Sort_TiesKeepOriginalOrder_LikePythonStableSort()
    {
        Trip[] ties = [Make("x", "t"), Make("y", "t"), Make("z", "t")];

        Assert.Equal("xyz", Ids(TripListing.Sort(ties, "name", "desc")));
        Assert.Equal("xyz", Ids(TripListing.Sort(ties, "modified", "desc")));
        Assert.Equal("xyz", Ids(TripListing.Sort(ties, "modified", "asc")));
    }
}
