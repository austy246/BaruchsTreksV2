using System.Net;
using System.Net.Sockets;
using BaruchsTreks.Cli;

namespace BaruchsTreks.Tests.Unit;

public class HealthCheckCommandTests
{
    [Fact]
    public void Run_ReturnsOneWhenNothingListens()
    {
        Assert.Equal(1, HealthCheckCommand.Run(["--healthcheck", "http://127.0.0.1:1/healthz"]));
    }

    [Theory]
    [InlineData(200, 0)]
    [InlineData(500, 1)]
    [InlineData(404, 1)]
    public void Run_MapsHttpStatusToExitCode(int status, int expectedExit)
    {
        using var server = StubServer.Start(status);

        Assert.Equal(expectedExit, HealthCheckCommand.Run(["--healthcheck", server.Url]));
    }

    private sealed class StubServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _loop;
        public string Url { get; }

        private StubServer(TcpListener listener, int status)
        {
            _listener = listener;
            Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/healthz";
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        using var client = await listener.AcceptTcpClientAsync();
                        var stream = client.GetStream();
                        var buffer = new byte[4096];
                        _ = await stream.ReadAsync(buffer);
                        var reply = System.Text.Encoding.ASCII.GetBytes(
                            $"HTTP/1.1 {status} X\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(reply);
                    }
                }
                catch (Exception)
                {
                    // Listener zastaven.
                }
            });
        }

        public static StubServer Start(int status)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new StubServer(listener, status);
        }

        public void Dispose()
        {
            _listener.Stop();
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
    }
}
