using System.Text.Json.Serialization;

namespace TetRail.Catalog.Identity;

public sealed record PassengerInput(
    [property: JsonPropertyName("full_name")] string? FullName,
    [property: JsonPropertyName("date_of_birth")] DateOnly? DateOfBirth,
    [property: JsonPropertyName("national_id")] string? NationalId);

public sealed record Passenger(
    [property: JsonPropertyName("passenger_id")] Guid PassengerId,
    [property: JsonPropertyName("full_name")] string FullName,
    [property: JsonPropertyName("date_of_birth")] DateOnly DateOfBirth,
    [property: JsonPropertyName("national_id_masked")] string? NationalIdMasked,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record CurrentIdentity(string Subject, IReadOnlyList<string> Roles);
