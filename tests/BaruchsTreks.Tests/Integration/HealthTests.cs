using System.Net;

namespace BaruchsTreks.Tests.Integration;

public class HealthTests(AzuriteFixture azurite)
{
    [Fact]
    public async Task Healthz_ReturnsOk()
    {
        await using var factory = new AppFactory(azurite);
        var client = factory.CreateClient();

        var res = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("{\"status\":\"ok\"}", await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AppFactory_RefusesNonLocalStorage()
    {
        Assert.Throws<InvalidOperationException>(() =>
            AppFactory.AssertLocalStorage("DefaultEndpointsProtocol=https;AccountName=x;EndpointSuffix=core.windows.net"));
        AppFactory.AssertLocalStorage("DefaultEndpointsProtocol=http;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;");
    }
}
