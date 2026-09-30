using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Portal.Api.Contracts;

namespace Portal.Tests.Infrastructure;

/// <summary>An HTTP client with its own cookie jar that handles the CSRF handshake like a browser app would.</summary>
public sealed class ApiClient : IDisposable
{
    private readonly HttpClient _http;

    public ApiClient(PortalApiFactory factory)
    {
        _http = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false,
        });
    }

    public Task<HttpResponseMessage> GetAsync(string url) => _http.GetAsync(url, TestContext.Current.CancellationToken);

    /// <summary>POST with a fresh CSRF token and (unless given) a fresh idempotency key.</summary>
    public Task<HttpResponseMessage> PostAsync(string url, object? body = null, string? idempotencyKey = null) =>
        SendAsync(HttpMethod.Post, url, body, idempotencyKey: idempotencyKey);

    public Task<HttpResponseMessage> PutAsync(string url, object? body, string? version, string? idempotencyKey = null) =>
        SendAsync(HttpMethod.Put, url, body, version, idempotencyKey);

    public Task<HttpResponseMessage> DeleteAsync(string url, string? version = null, string? idempotencyKey = null) =>
        SendAsync(HttpMethod.Delete, url, null, version, idempotencyKey);

    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, object? body = null, string? version = null, string? idempotencyKey = null)
    {
        var csrf = await FetchCsrfTokenAsync();
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString("N"));
        if (version is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        }

        return await _http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Sends a POST that deliberately omits the CSRF header.</summary>
    public async Task<HttpResponseMessage> PostWithoutCsrfAsync(string url, object? body = null) =>
        await _http.PostAsJsonAsync(url, body, TestContext.Current.CancellationToken);

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var value = await response.Content.ReadFromJsonAsync<T>(TestContext.Current.CancellationToken);
        return value ?? throw new InvalidOperationException("Response body was empty.");
    }

    public void Dispose() => _http.Dispose();

    private async Task<string> FetchCsrfTokenAsync()
    {
        using var response = await _http.GetAsync("/api/auth/csrf", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<CsrfResponse>(TestContext.Current.CancellationToken);
        return token!.Token;
    }
}
