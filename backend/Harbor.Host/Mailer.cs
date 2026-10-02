using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Harbor.Host;

public sealed record OutboundMail(string To, string Subject, string Text, string Html);

public interface IMailer
{
    Task<bool> SendAsync(OutboundMail mail, CancellationToken ct);
}

public sealed class CloudflareMailer(
    IHttpClientFactory httpClientFactory,
    IOptions<HarborAuthOptions> options,
    ILogger<CloudflareMailer> logger) : IMailer
{
    public async Task<bool> SendAsync(OutboundMail mail, CancellationToken ct)
    {
        var account = options.Value.CloudflareAccountId;
        var token = options.Value.CloudflareEmailToken;
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(token))
        {
            logger.LogInformation("Mail is not configured.");
            return false;
        }

        var from = string.IsNullOrWhiteSpace(options.Value.MailFrom)
            ? "harbor@birbol.com"
            : options.Value.MailFrom.Trim();
        try
        {
            var client = httpClientFactory.CreateClient("cloudflare-email");
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://api.cloudflare.com/client/v4/accounts/{Uri.EscapeDataString(account.Trim())}/email/sending/send");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
            request.Content = JsonContent.Create(new
            {
                to = mail.To,
                from = new { address = from, name = "Harbor" },
                subject = mail.Subject,
                text = mail.Text,
                html = mail.Html,
            });
            using var response = await client.SendAsync(request, ct);
            var payload = await response.Content.ReadFromJsonAsync<CloudflareSendReply>(ct);
            if (response.IsSuccessStatusCode && payload?.Success == true)
            {
                return true;
            }

            logger.LogWarning(
                "Mail send failed with status {Status}. {Message}",
                (int)response.StatusCode,
                payload?.Errors?.FirstOrDefault()?.Message);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Mail send failed: {Message}", ex.Message);
            return false;
        }
    }

    private sealed class CloudflareSendReply
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("errors")]
        public List<CloudflareSendError>? Errors { get; set; }
    }

    private sealed class CloudflareSendError
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}
