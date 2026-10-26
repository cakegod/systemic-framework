using System.Net;
using System.Text;
using Systemic.Content;

namespace Systemic.Preview;

/// <summary>Serves a local, read-only content preview over loopback HTTP.</summary>
public static class ContentPreviewServer
{
    public static async Task RunAsync(ContentPack pack, int port, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var html = Encoding.UTF8.GetBytes(ContentPreviewPage.Render(pack));
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            using (context.Response)
            {
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers[HttpResponseHeader.CacheControl] = "no-store";
                context.Response.Headers["X-Frame-Options"] = "DENY";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["Content-Security-Policy"] =
                    "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; connect-src 'none'; base-uri 'none'; form-action 'none'";
                if (!string.Equals(context.Request.HttpMethod, "GET", StringComparison.Ordinal))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                    context.Response.Headers[HttpResponseHeader.Allow] = "GET";
                    context.Response.Close();
                    continue;
                }

                if (context.Request.Url?.AbsolutePath is not ("/" or "/index.html"))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    context.Response.Close();
                    continue;
                }

                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.ContentLength64 = html.Length;
                await context.Response.OutputStream.WriteAsync(html, cancellationToken).ConfigureAwait(false);
                context.Response.Close();
            }
        }
    }
}
