namespace Source.Features.CiPublish;

public static class CiPublishAuthenticationDefaults
{
    public const string SchemeName = "CiPublish";
    public const string PublishPolicy = "CiPublishOrSuperAdmin";
    public const string CiPublishOnlyPolicy = "CiPublishOnly";
    public const string ClaimType = "ci_publish";
    public const string ClaimValue = "true";
    public const string HeaderName = "X-Ci-Publish-Key";
}
