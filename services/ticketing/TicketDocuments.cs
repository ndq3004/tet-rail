using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using QRCoder;

namespace TetRail.Ticketing;

public interface ITicketDocuments
{
    Task UploadAsync(Ticket ticket, CancellationToken ct);
    Task<string> DownloadUrlAsync(Guid ticketId);
}

public sealed class TicketDocuments(IAmazonS3 storage, IConfiguration configuration) : ITicketDocuments
{
    private string Bucket => configuration["TicketStorage:Bucket"] ?? "tetrail-tickets";

    public async Task UploadAsync(Ticket ticket, CancellationToken ct)
    {
        using var data = QRCodeGenerator.GenerateQrCode(ticket.QrReference.ToString("D"), QRCodeGenerator.ECCLevel.M);
        using var qr = new PdfByteQRCode(data);
        using var stream = new MemoryStream(qr.GetGraphic(16));
        var request = new PutObjectRequest
        {
            BucketName = Bucket, Key = Key(ticket.TicketId), InputStream = stream, AutoCloseStream = false, ContentType = "application/pdf"
        };
        try
        {
            await storage.PutObjectAsync(request, ct);
        }
        catch (AmazonS3Exception error) when (error.ErrorCode == "NoSuchBucket" && configuration.GetValue<bool>("TicketStorage:CreateBucket"))
        {
            try { await storage.PutBucketAsync(new PutBucketRequest { BucketName = Bucket }, ct); }
            catch (AmazonS3Exception conflict) when (conflict.StatusCode == HttpStatusCode.Conflict) { }
            stream.Position = 0;
            await storage.PutObjectAsync(request, ct);
        }
    }

    public Task<string> DownloadUrlAsync(Guid ticketId) => storage.GetPreSignedURLAsync(new GetPreSignedUrlRequest
    {
        BucketName = Bucket, Key = Key(ticketId), Verb = HttpVerb.GET, Expires = DateTime.UtcNow.AddMinutes(5),
        Protocol = configuration["TicketStorage:Endpoint"] is { } endpoint && new Uri(endpoint).Scheme == "http"
            ? Protocol.HTTP : Protocol.HTTPS
    });

    private static string Key(Guid id) => $"tickets/{id:D}.pdf";
}
