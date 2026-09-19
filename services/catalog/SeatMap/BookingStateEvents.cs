namespace TetRail.Catalog.SeatMap;

public static class BookingStateEventTypes
{
    public const string SeatHeld = "SeatHeld.v1";
    public const string HoldExpired = "HoldExpired.v1";
    public const string HoldConfirmed = "HoldConfirmed.v1";
}

public sealed record BookingStateEvent(
    Guid EventId,
    string EventType,
    long SourcePosition,
    DateTimeOffset OccurredAt,
    Guid CorrelationId,
    Guid TripId,
    Guid SeatId,
    IReadOnlyList<int> SegmentIndices);

public enum ProjectionEventApplyResult { Applied, Duplicate, Superseded }

public interface ISeatProjectionEventApplier
{
    Task<ProjectionEventApplyResult> ApplyAsync(BookingStateEvent stateEvent, CancellationToken cancellationToken);
}
