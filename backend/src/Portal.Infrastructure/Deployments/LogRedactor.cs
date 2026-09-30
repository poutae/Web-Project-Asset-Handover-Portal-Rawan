namespace Portal.Infrastructure.Deployments;

/// <summary>Removes secrets from anything that is about to be logged or shown.</summary>
public sealed class LogRedactor(params string?[] secrets)
{
    private readonly string[] _secrets = secrets
        .Where(s => !string.IsNullOrEmpty(s) && s.Length >= 4)
        .Select(s => s!)
        .Distinct()
        .OrderByDescending(s => s.Length)
        .ToArray();

    public string Redact(string text)
    {
        foreach (var secret in _secrets)
        {
            text = text.Replace(secret, "***", StringComparison.Ordinal);
            var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"x-access-token:{secret}"));
            text = text.Replace(encoded, "***", StringComparison.Ordinal);
        }

        return text;
    }
}
