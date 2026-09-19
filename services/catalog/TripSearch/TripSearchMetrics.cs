using System.Diagnostics.Metrics;

namespace TetRail.Catalog.TripSearch;

public sealed class TripSearchMetrics
{
    private readonly Counter<long> requests;
    private readonly Counter<long> cacheEvents;
    private readonly Histogram<double> duration;

    public TripSearchMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create("TetRail.Catalog.TripSearch");
        requests = meter.CreateCounter<long>("tetrail.catalog.trip_search.requests");
        cacheEvents = meter.CreateCounter<long>("tetrail.catalog.trip_search.cache_events");
        duration = meter.CreateHistogram<double>("tetrail.catalog.trip_search.duration_ms");
    }

    public void RecordRequest(string outcome, double elapsedMilliseconds)
    {
        requests.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        duration.Record(elapsedMilliseconds);
    }

    public void RecordCache(string status) => cacheEvents.Add(1, new KeyValuePair<string, object?>("status", status));
}
