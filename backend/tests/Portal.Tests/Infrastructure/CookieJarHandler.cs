using System.Collections.Concurrent;

namespace Portal.Tests.Infrastructure;

/// <summary>
/// A minimal cookie jar that, unlike the built-in one, exposes the cookies so they can also be sent by
/// other transports (the SignalR client).
/// </summary>
public sealed class CookieJarHandler : DelegatingHandler
{
    private readonly ConcurrentDictionary<string, string> _cookies = new();

    public string CookieHeader => string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}"));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!_cookies.IsEmpty)
        {
            request.Headers.Remove("Cookie");
            request.Headers.Add("Cookie", CookieHeader);
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var setCookie in setCookies)
            {
                var pair = setCookie.Split(';', 2)[0];
                var index = pair.IndexOf('=', StringComparison.Ordinal);
                if (index <= 0)
                {
                    continue;
                }

                var name = pair[..index];
                var value = pair[(index + 1)..];
                var expired = setCookie.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase);
                if (value.Length == 0 || expired)
                {
                    _cookies.TryRemove(name, out _);
                }
                else
                {
                    _cookies[name] = value;
                }
            }
        }

        return response;
    }
}
