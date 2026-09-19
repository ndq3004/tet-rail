namespace TetRail.Catalog;

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context)
    {
        var value = context.Request.Headers.TryGetValue(HeaderName, out var supplied) && Guid.TryParse(supplied, out var parsed)
            ? parsed
            : Guid.NewGuid();
        context.Items[HeaderName] = value;
        context.Response.Headers[HeaderName] = value.ToString();
        await next(context);
    }
}
