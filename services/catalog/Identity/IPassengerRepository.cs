namespace TetRail.Catalog.Identity;

public interface IPassengerRepository
{
    Task<IReadOnlyList<Passenger>> ListAsync(string subject, CancellationToken cancellationToken);
    Task<Passenger?> GetAsync(string subject, Guid passengerId, CancellationToken cancellationToken);
    Task<Passenger> CreateAsync(string subject, PassengerInput input, string? protectedNationalId, string? lastFour, Guid correlationId, CancellationToken cancellationToken);
    Task<Passenger?> UpdateAsync(string subject, Guid passengerId, PassengerInput input, string? protectedNationalId, string? lastFour, Guid correlationId, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(string subject, Guid passengerId, Guid correlationId, CancellationToken cancellationToken);
}
