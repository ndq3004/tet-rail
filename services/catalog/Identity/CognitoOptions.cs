namespace TetRail.Catalog.Identity;

public sealed class CognitoOptions
{
    public const string SectionName = "Cognito";
    public string Authority { get; init; } = string.Empty;
    public string Audience { get; init; } = string.Empty;
    public string GroupClaimType { get; init; } = "cognito:groups";
}
