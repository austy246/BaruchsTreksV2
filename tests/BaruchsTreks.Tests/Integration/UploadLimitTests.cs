using System.Net;
using System.Net.Sockets;

namespace BaruchsTreks.Tests.Integration;

/// <summary>
/// Limit těla požadavku 50 MB. Skutečný Kestrel (TestServer limity nevynucuje) i middleware, který hlídá
/// limit i bez Kestrelu. Případ se známým Content-Length nad limitem se na Kestrelu netestuje: Kestrel odpoví 413
/// a zavře spojení, takže klient, který ještě odesílá tělo, dostane přerušené spojení místo odpovědi.
/// </summary>
public class UploadLimitTests(AzuriteFixture azurite)
{
    private const long Limit = 52_428_800;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AppFactory CreateKestrelFactory(AzuriteFixture azurite, out Uri address)
    {
        var factory = new AppFactory(azurite);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        factory.UseKestrel(port);
        address = new Uri($"http://localhost:{port}");
        return factory;
    }

    private static async Task<HttpClient> KestrelAdminAsync(AppFactory factory, Uri address)
    {
        var userName = "admin-" + Guid.NewGuid().ToString("N")[..8];
        await factory.CreateUserAsync(userName, AppFactory.TestPassword, isAdmin: true);
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = address };
        var login = await factory.LoginAsync(client, userName, AppFactory.TestPassword);
        Assert.Equal(HttpStatusCode.Found, login.StatusCode);
        return client;
    }

    [Fact]
    public async Task Upload_OverRequestLimit_Returns413()
    {
        await using var factory = CreateKestrelFactory(azurite, out var address);
        using var client = await KestrelAdminAsync(factory, address);
        var token = await client.GetAntiforgeryTokenAsync("/trips/trip/new/", Ct);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent("Příliš velké"), "title" },
            { new UnknownLengthContent(new byte[Limit + 1]), "photos", "big.jpg" },
        };

        var response = await client.PostAsync("/trips/trip/new/", form, Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(await factory.Trips().ListAsync());
    }

    [Fact]
    public async Task Upload_Under50Mb_Works_OnKestrel()
    {
        await using var factory = CreateKestrelFactory(azurite, out var address);
        using var client = await KestrelAdminAsync(factory, address);
        var token = await client.GetAntiforgeryTokenAsync("/trips/trip/new/", Ct);
        // Víc než výchozí limit Kestrelu (~28,6 MB), ale pod 50 MB; platný JPEG s dlouhým koncem.
        var big = TestImages.Jpeg(100, 100).Concat(new byte[40_000_000]).ToArray();
        using var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent("Velká fotka"), "title" },
            { new ByteArrayContent(big), "photos", "big.jpg" },
        };

        var response = await client.PostAsync("/trips/trip/new/", form, Ct);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Single(await factory.Trips().ListAsync());
    }

    [Fact]
    public async Task Upload_DeclaredLengthOverLimit_Returns413_WithoutReadingBody()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        var token = await admin.GetAntiforgeryTokenAsync("/trips/trip/new/", Ct);
        using var content = new DeclaredLengthContent(Limit + 1);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("multipart/form-data")
        {
            Parameters = { new System.Net.Http.Headers.NameValueHeaderValue("boundary", "x") },
        };

        var response = await admin.PostAsync("/trips/trip/new/?t=" + token.Length, content, Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(await factory.Trips().ListAsync());
    }

    /// <summary>Tělo bez známé délky (chunked), aby Kestrel limit vynucoval až při čtení.</summary>
    private sealed class UnknownLengthContent(byte[] payload) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(payload, 0, payload.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>Tvrdí délku nad limitem; server ji odmítne dřív, než by tělo četl.</summary>
    private sealed class DeclaredLengthContent(long length) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long len)
        {
            len = length;
            return true;
        }
    }
}
