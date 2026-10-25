using System.Net;
using System.Text;
using BaruchsTreks.Storage;
using BaruchsTreks.Strava;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BaruchsTreks.Tests.Unit;

/// <summary>Tokeny a secret ze Stravy se nesmějí dostat do logu, ani když odpověď není v pořádku.</summary>
public class StravaLoggingTests
{
    private const string AccessToken = "ACCESS-TOKEN-zz91";
    private const string RefreshToken = "REFRESH-TOKEN-qq42";
    private const string ClientSecret = "CLIENT-SECRET-ww77";

    private sealed class CapturingProvider : ILoggerProvider
    {
        private readonly List<string> _lines = [];

        public string All
        {
            get
            {
                lock (_lines)
                    return string.Join('\n', _lines);
            }
        }

        public ILogger CreateLogger(string categoryName) => new Capture(this);

        public void Dispose()
        {
        }

        private sealed class Capture(CapturingProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var text = formatter(state, exception) + (exception is null ? "" : "\n" + exception);
                lock (owner._lines)
                    owner._lines.Add(text);
            }
        }
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private sealed class MemoryConfig : IConfigRepository
    {
        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
        public Task SaveAsync(string key, string json) => Task.CompletedTask;
        public Task DeleteAsync(string key) => Task.CompletedTask;
    }

    private static (StravaService Service, CapturingProvider Logs) Create(HttpStatusCode status, string body)
    {
        var logs = new CapturingProvider();
        var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var client = new StravaClient(new HttpClient(new StubHandler(status, body)));
        var service = new StravaService(
            client, new StravaTokenStore(new MemoryConfig()),
            Options.Create(new StravaOptions { ClientId = "cid", ClientSecret = ClientSecret }),
            TimeProvider.System, factory.CreateLogger<StravaService>());
        return (service, logs);
    }

    private static void AssertNoSecrets(string logs)
    {
        Assert.DoesNotContain("ACCESS-TOKEN", logs);
        Assert.DoesNotContain("REFRESH-TOKEN", logs);
        Assert.DoesNotContain("CLIENT-SECRET", logs);
        Assert.DoesNotContain("access_token", logs);
        Assert.DoesNotContain("refresh_token", logs);
        Assert.DoesNotContain("client_secret", logs);
    }

    [Fact]
    public async Task MalformedOkTokenResponse_DoesNotLogBody()
    {
        var (service, logs) = Create(HttpStatusCode.OK, $"{{\"access_token\":\"{AccessToken}\",\"refresh_token\":\"{RefreshToken}\" <<broken");

        var ex = await Assert.ThrowsAsync<StravaError>(() => service.ExchangeCodeAsync("code"));

        Assert.DoesNotContain(AccessToken, ex.Message);
        Assert.NotEmpty(logs.All);
        AssertNoSecrets(logs.All);
    }

    [Fact]
    public async Task BadRequestTokenResponse_LogsOnlyMessageAndErrorCodes()
    {
        var body = $$"""
            {"message":"Bad Request","errors":[{"resource":"Application","field":"client_id","code":"invalid"}],
             "access_token":"{{AccessToken}}","refresh_token":"{{RefreshToken}}","client_secret":"{{ClientSecret}}"}
            """;
        var (service, logs) = Create(HttpStatusCode.BadRequest, body);

        await Assert.ThrowsAsync<StravaError>(() => service.ExchangeCodeAsync("code"));

        Assert.Contains("400", logs.All);
        Assert.Contains("Bad Request", logs.All);
        Assert.Contains("invalid", logs.All);
        AssertNoSecrets(logs.All);
    }

    [Fact]
    public async Task NonJsonErrorTokenResponse_LogsOnlyStatus()
    {
        var (service, logs) = Create(HttpStatusCode.BadGateway, $"upstream failed access_token={AccessToken}&refresh_token={RefreshToken}&client_secret={ClientSecret}");

        await Assert.ThrowsAsync<StravaError>(() => service.ExchangeCodeAsync("code"));

        Assert.Contains("502", logs.All);
        AssertNoSecrets(logs.All);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    public void HttpException_NeverCarriesBodyOfSuccessfulResponse(int status)
    {
        var ex = new StravaHttpException(status, $"{{\"access_token\":\"{AccessToken}\"}}");

        Assert.Equal("", ex.Body);
    }
}
