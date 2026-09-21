using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using TetRail.Catalog.TripSearch;

namespace TetRail.Catalog.Identity;

public sealed class TestingAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var subject = Request.Headers["X-Test-Subject"].ToString();
        if (string.IsNullOrWhiteSpace(subject)) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new("sub", subject) };
        foreach (var role in Request.Headers["X-Test-Role"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries)) claims.Add(new Claim("cognito:groups", role));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "Testing")), "Testing")));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var correlationId = Context.Items.TryGetValue(CorrelationIdMiddleware.HeaderName, out var value) && value is Guid id ? id : Guid.NewGuid();
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Response.WriteAsJsonAsync(new ApiError("https://tetrail.local/problems/unauthenticated", "Authentication is required", StatusCodes.Status401Unauthorized, "UNAUTHENTICATED", correlationId));
    }
}
