using System.Net;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BaruchsTreks.Tests.Integration;

public class AuthTests(AzuriteFixture azurite)
{
    private const string AuthCookie = "baruchstreks_auth";

    private static WebApplicationFactoryClientOptions NoRedirect => new() { AllowAutoRedirect = false };

    [Fact]
    public async Task Login_ValidCredentials_RedirectsToNextOrTrips()
    {
        await using var factory = new AppFactory(azurite);
        await factory.CreateUserAsync("austy", "pw-1", isAdmin: true);
        var ct = TestContext.Current.CancellationToken;

        var withNext = factory.CreateClient(NoRedirect);
        var res = await factory.LoginAsync(withNext, "austy", "pw-1", next: "/trips/trip/new/");
        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal("/trips/trip/new/", res.Headers.Location?.OriginalString);
        Assert.NotNull(AuthCookieOf(res));
        Assert.Equal("austy", await withNext.GetStringAsync("/__whoami", ct));

        var withoutNext = factory.CreateClient(NoRedirect);
        res = await factory.LoginAsync(withoutNext, "AUSTY", "pw-1");
        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal("/trips/", res.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("//evil.example/path")]
    public async Task Login_ExternalNext_RedirectsToTrips(string next)
    {
        await using var factory = new AppFactory(azurite);
        await factory.CreateUserAsync("austy", "pw-1", isAdmin: true);

        var res = await factory.LoginAsync(factory.CreateClient(NoRedirect), "austy", "pw-1", next);

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal("/trips/", res.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("austy", "wrong", "Password is incorrect. Please try again.")]
    [InlineData("nobody", "pw-1", "Username does not exist. Please check your username.")]
    public async Task Login_WrongPassword_ShowsError(string userName, string password, string message)
    {
        await using var factory = new AppFactory(azurite);
        await factory.CreateUserAsync("austy", "pw-1", isAdmin: true);

        var res = await factory.LoginAsync(factory.CreateClient(NoRedirect), userName, password);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Your username and password didn't match. Please try again.", html);
        Assert.Contains(message, html);
        Assert.Null(AuthCookieOf(res));
    }

    [Fact]
    public async Task Login_Page_ShowsFormAndNextHint()
    {
        await using var factory = new AppFactory(azurite);
        var client = factory.CreateClient(NoRedirect);

        var html = await client.GetStringAsync("/accounts/login/?next=/trips/trip/new/", TestContext.Current.CancellationToken);

        Assert.Contains("Please login to access this page.", html);
        Assert.Contains("name=\"username\"", html);
        Assert.Contains("name=\"password\"", html);
        Assert.Contains("name=\"next\" value=\"/trips/trip/new/\"", html);
    }

    [Fact]
    public async Task Login_PostWithoutAntiforgeryToken_IsRejected()
    {
        await using var factory = new AppFactory(azurite);
        await factory.CreateUserAsync("austy", "pw-1", isAdmin: true);

        var res = await factory.CreateClient(NoRedirect).PostAsync("/accounts/login/", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["username"] = "austy", ["password"] = "pw-1" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task AdminPage_Anonymous_RedirectsToLoginWithNext()
    {
        await using var factory = new AppFactory(azurite);

        var res = await factory.CreateClient(NoRedirect).GetAsync("/__admin", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal("/accounts/login/?next=%2F__admin", res.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task AdminPage_NonAdmin_RedirectsToLogin()
    {
        await using var factory = new AppFactory(azurite);
        var client = await factory.CreateUserClientAsync(isAdmin: false);

        var res = await client.GetAsync("/__admin", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal("/accounts/login/?next=%2F__admin", res.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task AdminPage_Admin_IsAllowed()
    {
        await using var factory = new AppFactory(azurite);
        var client = await factory.CreateUserClientAsync(isAdmin: true);

        var res = await client.GetAsync("/__admin", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Logout_ClearsSession()
    {
        await using var factory = new AppFactory(azurite);
        var client = await factory.CreateUserClientAsync(isAdmin: true);
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/__whoami", ct)).StatusCode);

        var token = await client.GetAntiforgeryTokenAsync("/accounts/login/", ct);
        var res = await client.PostAsync("/accounts/logout/", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
        }), ct);

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal("/trips/", res.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/__whoami", ct)).StatusCode);
    }

    [Fact]
    public async Task Logout_Get_NotAllowedAndKeepsSession()
    {
        await using var factory = new AppFactory(azurite);
        var client = await factory.CreateUserClientAsync(isAdmin: true);
        var ct = TestContext.Current.CancellationToken;

        var res = await client.GetAsync("/accounts/logout/", ct);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, res.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/__whoami", ct)).StatusCode);
    }

    [Fact]
    public async Task DataProtection_KeysPersistedInBlob()
    {
        await using var factory = new AppFactory(azurite);
        await factory.CreateUserAsync("austy", "pw-1", isAdmin: true);
        var ct = TestContext.Current.CancellationToken;
        await factory.LoginAsync(factory.CreateClient(NoRedirect), "austy", "pw-1");

        var container = new BlobServiceClient(azurite.ConnectionString).GetBlobContainerClient(factory.KeysContainer);
        var blobs = new List<string>();
        await foreach (var blob in container.GetBlobsAsync(cancellationToken: ct))
            blobs.Add(blob.Name);

        var keys = Assert.Single(blobs);
        Assert.Equal("keys.xml", keys);
        var xml = (await container.GetBlobClient(keys).DownloadContentAsync(ct)).Value.Content.ToString();
        Assert.Contains("<key ", xml);
    }

    [Fact]
    public async Task AuthCookie_SurvivesRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var first = new AppFactory(azurite);
        await first.CreateUserAsync("austy", "pw-1", isAdmin: true);
        var cookie = AuthCookieOf(await first.LoginAsync(first.CreateClient(NoRedirect), "austy", "pw-1"));
        Assert.NotNull(cookie);

        // Nová instance nad stejným úložištěm (včetně kontejneru s klíči) cookie přijme.
        await using var second = new AppFactory(azurite, sharedStorageWith: first);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__whoami");
        request.Headers.Add("Cookie", $"{AuthCookie}={cookie}");
        var res = await second.CreateClient().SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("austy", await res.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task AuthCookie_IsRejectedByInstanceWithDifferentKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var first = new AppFactory(azurite);
        await first.CreateUserAsync("austy", "pw-1", isAdmin: true);
        var cookie = AuthCookieOf(await first.LoginAsync(first.CreateClient(NoRedirect), "austy", "pw-1"));
        Assert.NotNull(cookie);

        await using var unrelated = new AppFactory(azurite);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__whoami");
        request.Headers.Add("Cookie", $"{AuthCookie}={cookie}");
        var res = await unrelated.CreateClient().SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Theory]
    [InlineData("Development", false)]
    [InlineData("Production", true)]
    public async Task Login_Cookie_HasExpectedFlags(string environment, bool secure)
    {
        await using var baseFactory = new AppFactory(azurite);
        await baseFactory.CreateUserAsync("austy", "pw-1", isAdmin: true);
        await using var factory = baseFactory.WithWebHostBuilder(b => b.UseEnvironment(environment));
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri(secure ? "https://localhost" : "http://localhost"),
        });

        var res = await baseFactory.LoginAsync(client, "austy", "pw-1");

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        var setCookie = res.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(AuthCookie + "=", StringComparison.Ordinal));
        Assert.Equal(secure, setCookie.Contains("secure", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Hodnota auth cookie ze Set-Cookie odpovědi, nebo <c>null</c>.</summary>
    private static string? AuthCookieOf(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
            return null;
        var prefix = AuthCookie + "=";
        return values
            .Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
            .Select(v => v[prefix.Length..].Split(';')[0])
            .FirstOrDefault(v => v.Length > 0);
    }
}
