using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace TetRail.OrderPayment.Payments;

public sealed record SimulatePaymentRequest(string Scenario);

public sealed record SimulatorAttempt(
    [property: JsonPropertyName("provider_transaction_id")] Guid TransactionId,
    [property: JsonPropertyName("due_at")] DateTimeOffset DueAt,
    string Scenario);

public sealed record SimulatorCallback(
    [property: JsonPropertyName("provider_transaction_id")] Guid TransactionId,
    [property: JsonPropertyName("order_id")] Guid OrderId,
    string Outcome);

public sealed class SimulatorSignature(string secret)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(secret);

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public string Sign(byte[] body) => Convert.ToHexString(ComputeHash(body));

    private byte[] ComputeHash(byte[] body) => HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);

    public SimulatorCallback? ReadCallback(byte[] body, string signature)
    {
        if (!Verify(body, signature))
            return null;
        return JsonSerializer.Deserialize<SimulatorCallback>(body, WebJson)
            ?? throw new JsonException("Expected a callback object.");
    }

    public bool Verify(byte[] body, string signature)
    {
        if (!IsConfigured || signature.Length != 64)
            return false;

        Span<byte> supplied = stackalloc byte[32];
        return Convert.FromHexString(signature, supplied, out _, out var written) == System.Buffers.OperationStatus.Done
            && written == supplied.Length
            && CryptographicOperations.FixedTimeEquals(supplied, ComputeHash(body));
    }
}
