using Microsoft.AspNetCore.DataProtection;

namespace Portal.Api.Deployments;

/// <summary>Encrypts Git access tokens at rest. The plaintext is only ever held in memory while a build runs.</summary>
public interface IAccessTokenProtector
{
    string Protect(string plaintext);

    string? Unprotect(string? protectedValue);
}

public sealed class AccessTokenProtector(IDataProtectionProvider provider) : IAccessTokenProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("Portal.Deployments.AccessToken.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string? Unprotect(string? protectedValue) =>
        string.IsNullOrEmpty(protectedValue) ? null : _protector.Unprotect(protectedValue);
}
