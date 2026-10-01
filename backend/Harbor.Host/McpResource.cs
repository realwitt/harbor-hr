namespace Harbor.Host;

public static class McpResource
{
    public const string DefaultBase = "http://localhost:5088";

    public static string BaseUrl(string? configured)
    {
        var value = string.IsNullOrWhiteSpace(configured) ? DefaultBase : configured.Trim();
        return value.TrimEnd('/');
    }

    public static string Url(string? configured) => BaseUrl(configured) + "/mcp";

    public static bool IsHttps(string? configured) =>
        BaseUrl(configured).StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}

public static class McpClaims
{
    public const string ClientRow = "mcp_client_id";
    public const string OAuthClient = "harbor_oauth_client";
}
