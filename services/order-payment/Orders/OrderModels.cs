using System.Text.Json.Serialization;

namespace TetRail.OrderPayment.Orders;

public sealed record CreateOrderRequest(
    [property: JsonPropertyName("hold_id")] Guid HoldId,
    [property: JsonPropertyName("passengers")] IReadOnlyList<PassengerSnapshot> Passengers,
    [property: JsonPropertyName("fare")] FareSnapshot Fare);

public sealed record PassengerSnapshot(
    [property: JsonPropertyName("passenger_id")] Guid PassengerId,
    [property: JsonPropertyName("full_name")] string FullName,
    [property: JsonPropertyName("date_of_birth")] DateOnly DateOfBirth);

public sealed record FareSnapshot(
    [property: JsonPropertyName("seat_class")] string SeatClass,
    decimal Price,
    string Currency,
    [property: JsonPropertyName("fare_version")] long FareVersion);

public sealed record OrderResponse(
    [property: JsonPropertyName("order_id")] Guid OrderId,
    string State,
    [property: JsonPropertyName("payment_expires_at")] DateTimeOffset PaymentExpiresAt,
    [property: JsonPropertyName("total_amount")] decimal TotalAmount,
    string Currency);

public sealed record BookingHold(
    [property: JsonPropertyName("ID")] string Id,
    [property: JsonPropertyName("OwnerID")] string OwnerId,
    string State,
    [property: JsonPropertyName("ExpiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("segments")] IReadOnlyList<BookingSegment> Segments);
public sealed record BookingSegment(
    [property: JsonPropertyName("trip_id")] string TripId,
    [property: JsonPropertyName("carriage_id")] string CarriageId,
    [property: JsonPropertyName("seat_id")] string SeatId,
    [property: JsonPropertyName("segment_index")] int SegmentIndex);
