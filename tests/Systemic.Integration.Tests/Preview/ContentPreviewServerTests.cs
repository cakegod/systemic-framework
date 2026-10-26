using System.Net;
using System.Net.Sockets;
using Systemic.Content;
using Systemic.Preview;

namespace Systemic.Tests.Preview;

public sealed class ContentPreviewServerTests
{
    [Fact]
    public async Task Preview_server_is_loopback_only_and_read_only()
    {
        var pack = SystemicTestContent.Load(Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry"));
        var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        using var cancellation = new CancellationTokenSource();
        var serving = Task.Run(() => ContentPreviewServer.RunAsync(pack, port, cancellation.Token));
        using var client = new HttpClient();
        var endpoint = new Uri($"http://127.0.0.1:{port}/");
        try
        {
            HttpResponseMessage? page = null;
            for (var attempt = 0; attempt < 40 && page is null; attempt++)
            {
                try
                {
                    page = await client.GetAsync(endpoint);
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(25);
                }
            }

            Assert.NotNull(page);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Equal("nosniff", page.Headers.GetValues("X-Content-Type-Options").Single());

            var write = await client.PostAsync(endpoint, new StringContent("ignored"));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, write.StatusCode);
        }
        finally
        {
            cancellation.Cancel();
            await serving;
        }
    }
}
