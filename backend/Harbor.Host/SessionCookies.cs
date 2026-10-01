namespace Harbor.Host;

public static class SessionCookies
{
    public const string Name = "harbor_session";

    // Host-only. Do not set Domain.
    public static CookieOptions Options(bool secure) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Secure = secure,
        Path = "/",
        IsEssential = true,
    };

    public static void Set(HttpResponse response, Guid sessionId, bool secure, DateTimeOffset expires)
    {
        var options = Options(secure);
        options.Expires = expires;
        response.Cookies.Append(Name, sessionId.ToString("D"), options);
    }

    public static void Clear(HttpResponse response, bool secure)
    {
        response.Cookies.Delete(Name, Options(secure));
    }
}

public sealed record HarborCaller(Guid SessionId, Employee Employee, bool Ready)
{
    public const string ItemKey = "harbor.caller";

    public static HarborCaller? Read(HttpContext http) => http.Items[ItemKey] as HarborCaller;
}
