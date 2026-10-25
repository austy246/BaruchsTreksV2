using System.Text.Json;
using BaruchsTreks.Strava;

namespace BaruchsTreks.Tests;

/// <summary>
/// Falešná Strava: odpovědi se nastavují podle cesty, žádný požadavek nejde ven. Nenastavená cesta spadne
/// výjimkou, takže test, který zapomněl něco nastavit, to hned ukáže. Zaznamenává všechna volání.
/// </summary>
public sealed class FakeStravaClient : IStravaClient
{
    private readonly Dictionary<string, Func<JsonDocument>> _routes = [];
    private Func<JsonDocument> _token = () => throw new InvalidOperationException("FakeStravaClient: token response not set.");

    public List<IReadOnlyDictionary<string, string>> TokenRequests { get; } = [];

    public List<GetCall> GetRequests { get; } = [];

    public sealed record GetCall(string AccessToken, string Path, IReadOnlyDictionary<string, string> Query);

    public FakeStravaClient RespondToken(string json)
    {
        _token = () => JsonDocument.Parse(json);
        return this;
    }

    public FakeStravaClient FailToken(int status, string body = "{\"message\":\"Bad Request\"}")
    {
        _token = () => throw new StravaHttpException(status, body);
        return this;
    }

    public FakeStravaClient Respond(string path, string json)
    {
        _routes[path] = () => JsonDocument.Parse(json);
        return this;
    }

    public FakeStravaClient Fail(string path, int status, string body = "{\"message\":\"error\"}")
    {
        _routes[path] = () => throw new StravaHttpException(status, body);
        return this;
    }

    public Task<JsonDocument> PostTokenAsync(IDictionary<string, string> form)
    {
        TokenRequests.Add(new Dictionary<string, string>(form));
        return Task.FromResult(_token());
    }

    public Task<JsonDocument> GetAsync(string accessToken, string path, IDictionary<string, string>? query)
    {
        GetRequests.Add(new GetCall(accessToken, path, new Dictionary<string, string>(query ?? new Dictionary<string, string>())));
        if (!_routes.TryGetValue(path, out var route))
            throw new InvalidOperationException($"FakeStravaClient: no response set for GET {path}.");
        return Task.FromResult(route());
    }
}
