using System.Net.Mail;

namespace Portal.Api.Endpoints;

internal sealed class ValidationErrors
{
    private readonly Dictionary<string, string[]> _errors = [];

    public bool IsValid => _errors.Count == 0;

    public void Add(string field, string message) => _errors[field] = [message];

    public string? Required(string field, string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            Add(field, $"{field} is required.");
            return null;
        }

        if (trimmed.Length > maxLength)
        {
            Add(field, $"{field} must be at most {maxLength} characters.");
            return null;
        }

        return trimmed;
    }

    public string? Email(string field, string? value)
    {
        var trimmed = Required(field, value, 256);
        if (trimmed is null)
        {
            return null;
        }

        if (!MailAddress.TryCreate(trimmed, out var address) || address.Address != trimmed)
        {
            Add(field, $"{field} must be a valid email address.");
            return null;
        }

        return trimmed;
    }

    public IResult ToResult() => Results.ValidationProblem(_errors);
}
