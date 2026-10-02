namespace Harbor.Host;

public sealed class HarborAuthOptions
{
    public string RelyingPartyId { get; set; } = "localhost";
    public string ServerName { get; set; } = "Harbor";
    public bool CookieSecure { get; set; }
    public string MailWorkerUrl { get; set; } = "";
    public string CloudflareAccountId { get; set; } = "";
    public string CloudflareEmailToken { get; set; } = "";
    public string MailFrom { get; set; } = "harbor@birbol.com";
    public string JoinNotifyEmail { get; set; } = "ew@eliaswitt.com";
    public string TurnstileSecret { get; set; } = "";
    public string PublicBaseUrl { get; set; } = McpResource.DefaultBase;
    public string[] Origins { get; set; } = ["http://localhost:5190", "http://localhost:5088"];
}
