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

    public async Task<HttpResponseMessage> PostAsync(string url, object? body = null)
    {
        await RefreshCsrfTokenAsync();
        return await _http.PostAsJsonAsync(url, body, TestContext.Current.CancellationToken);
    }

    public async Task<HttpResponseMessage> DeleteAsync(string url)
    {
        await RefreshCsrfTokenAsync();
        return await _http.DeleteAsync(url, TestContext.Current.CancellationToken);
    }

    /// <summary>Sends a POST that deliberately omits the CSRF header.</summary>
    public async Task<HttpResponseMessage> PostWithoutCsrfAsync(string url, object? body = null)
    {
        _http.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        return await _http.PostAsJsonAsync(url, body, TestContext.Current.CancellationToken);
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var value = await response.Content.ReadFromJsonAsync<T>(TestContext.Current.CancellationToken);
        return value ?? throw new InvalidOperationException("Response body was empty.");
    }

    public void Dispose() => _http.Dispose();

    private async Task RefreshCsrfTokenAsync()
    {
        using var response = await _http.GetAsync("/api/auth/csrf", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<CsrfResponse>(TestContext.Current.CancellationToken);
        _http.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        _http.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token!.Token);
    }
}
