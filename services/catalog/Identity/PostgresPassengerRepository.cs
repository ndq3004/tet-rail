using Npgsql;

namespace TetRail.Catalog.Identity;

public sealed class PostgresPassengerRepository(NpgsqlDataSource dataSource) : IPassengerRepository
{
    public async Task<IReadOnlyList<Passenger>> ListAsync(string subject, CancellationToken cancellationToken)
    {
        const string sql = """SELECT p.id,p.full_name,p.date_of_birth,p.national_id_last_four,p.created_at,p.updated_at FROM catalog.passengers p JOIN catalog.identity_users u ON u.id=p.owner_user_id WHERE u.cognito_subject=@subject ORDER BY p.created_at""";
        await using var command = dataSource.CreateCommand(sql); command.Parameters.AddWithValue("subject", subject);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); var result = new List<Passenger>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(Map(reader)); return result;
    }
    public async Task<Passenger?> GetAsync(string subject, Guid passengerId, CancellationToken cancellationToken)
    {
        const string sql = """SELECT p.id,p.full_name,p.date_of_birth,p.national_id_last_four,p.created_at,p.updated_at FROM catalog.passengers p JOIN catalog.identity_users u ON u.id=p.owner_user_id WHERE u.cognito_subject=@subject AND p.id=@id""";
        await using var command=dataSource.CreateCommand(sql); command.Parameters.AddWithValue("subject",subject); command.Parameters.AddWithValue("id",passengerId);
        await using var reader=await command.ExecuteReaderAsync(cancellationToken); return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }
    public async Task<Passenger> CreateAsync(string subject, PassengerInput input, string? protectedNationalId, string? lastFour, Guid correlationId, CancellationToken cancellationToken)
    {
        await using var connection=await dataSource.OpenConnectionAsync(cancellationToken); await using var transaction=await connection.BeginTransactionAsync(cancellationToken);
        var userId=Guid.NewGuid(); const string userSql="INSERT INTO catalog.identity_users(id,cognito_subject) VALUES(@id,@subject) ON CONFLICT(cognito_subject) DO UPDATE SET cognito_subject=EXCLUDED.cognito_subject RETURNING id";
        await using var user=new NpgsqlCommand(userSql,connection,transaction); user.Parameters.AddWithValue("id",userId); user.Parameters.AddWithValue("subject",subject); userId=(Guid)(await user.ExecuteScalarAsync(cancellationToken))!;
        var id=Guid.NewGuid(); var now=DateTimeOffset.UtcNow; const string insert="INSERT INTO catalog.passengers(id,owner_user_id,full_name,date_of_birth,national_id_protected,national_id_last_four,created_at,updated_at) VALUES(@id,@owner,@name,@dob,@protected,@lastFour,@now,@now)";
        await using (var cmd=new NpgsqlCommand(insert,connection,transaction)){cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("owner",userId);cmd.Parameters.AddWithValue("name",input.FullName!);cmd.Parameters.AddWithValue("dob",input.DateOfBirth!.Value);cmd.Parameters.AddWithValue("protected",(object?)protectedNationalId??DBNull.Value);cmd.Parameters.AddWithValue("lastFour",(object?)lastFour??DBNull.Value);cmd.Parameters.AddWithValue("now",now);await cmd.ExecuteNonQueryAsync(cancellationToken);}
        await using (var audit=new NpgsqlCommand("INSERT INTO catalog.passenger_audit(id,passenger_id,actor_user_id,action,correlation_id) VALUES(@id,@passenger,@actor,'CREATED',@correlation)",connection,transaction)){audit.Parameters.AddWithValue("id",Guid.NewGuid());audit.Parameters.AddWithValue("passenger",id);audit.Parameters.AddWithValue("actor",userId);audit.Parameters.AddWithValue("correlation",correlationId);await audit.ExecuteNonQueryAsync(cancellationToken);} await transaction.CommitAsync(cancellationToken); return new Passenger(id,input.FullName!,input.DateOfBirth!.Value,Mask(lastFour),now,now);
    }
    private static Passenger Map(NpgsqlDataReader r)=>new(r.GetGuid(0),r.GetString(1),r.GetFieldValue<DateOnly>(2),Mask(r.IsDBNull(3)?null:r.GetString(3)),r.GetFieldValue<DateTimeOffset>(4),r.GetFieldValue<DateTimeOffset>(5));
    public static string? Mask(string? lastFour)=>lastFour is null?null:$"****{lastFour}";
}
