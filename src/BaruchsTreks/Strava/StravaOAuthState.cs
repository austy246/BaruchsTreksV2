using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace BaruchsTreks.Strava;

/// <summary>
/// Parametr <c>state</c> protokolu OAuth vázaný na prohlížeč. Django ho ukládá do session; tady není session, proto
/// náhodná hodnota jde do chráněné (šifrované a podepsané) cookie spolu s uživatelem a ID výletu. Callback ji musí
/// najít, shodovat se s <c>state</c> z URL i s přihlášeným uživatelem; cookie se spotřebuje při prvním použití.
/// </summary>
public sealed class StravaOAuthState(IDataProtectionProvider protection, IHostEnvironment environment)
{
    public const string CookieName = "baruchstreks_strava_state";
    public const string CookiePath = "/trips/strava/";

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ITimeLimitedDataProtector _protector =
        protection.CreateProtector("BaruchsTreks.Strava.OAuthState").ToTimeLimitedDataProtector();

    private sealed record Payload(string State, string User, string TripId);

    /// <summary>Vygeneruje nový stav, uloží cookie do odpovědi a vrátí hodnotu pro adresu autorizace.</summary>
    public string Issue(HttpContext context, string userName, string tripId)
    {
        var state = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
        var protectedValue = _protector.Protect(JsonSerializer.Serialize(new Payload(state, userName, tripId)), Lifetime);
        context.Response.Cookies.Append(CookieName, protectedValue, Options());
        return state;
    }

    /// <summary>
    /// Ověří <paramref name="providedState"/> proti cookie a cookie smaže (i když je neplatná, jako <c>session.pop</c>).
    /// Vrací ID výletu uložené při zahájení propojení, nebo <c>null</c>, když stav neplatí.
    /// </summary>
    public string? Consume(HttpContext context, string userName, string? providedState)
    {
        var cookie = context.Request.Cookies[CookieName];
        context.Response.Cookies.Delete(CookieName, Options());
        if (string.IsNullOrEmpty(cookie) || string.IsNullOrEmpty(providedState))
            return null;

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(cookie));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            // Cizí, poškozená nebo prošlá cookie.
            return null;
        }

        if (payload is null
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(payload.State), Encoding.UTF8.GetBytes(providedState))
            || !string.Equals(payload.User, userName, StringComparison.Ordinal))
        {
            return null;
        }

        return payload.TripId;
    }

    private CookieOptions Options() => new()
    {
        HttpOnly = true,
        // Lax: callback je přesměrování z jiné domény (strava.com) a cookie musí s takovým GET odejít.
        SameSite = SameSiteMode.Lax,
        Secure = !environment.IsDevelopment(),
        Path = CookiePath,
        MaxAge = Lifetime,
        IsEssential = true,
    };
}
