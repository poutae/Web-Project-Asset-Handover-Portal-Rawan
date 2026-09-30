using System.Net;
using Microsoft.AspNetCore.Mvc;
using Portal.Api.Contracts;
using Portal.Domain;
using Portal.Tests.Infrastructure;

namespace Portal.Tests;

[Trait("Category", "Integration")]
public sealed class AuthTests(PortalApiFactory factory) : IClassFixture<PortalApiFactory>
{
    [Fact]
    public async Task Register_creates_an_organization_and_signs_the_admin_in()
    {
        var (client, me) = await Given.AnOrganizationAsync(factory, "Acme Studio");
        using var _ = client;

        Assert.Equal(OrgRole.Admin, me.Role);
        Assert.Equal("Acme Studio", me.Organization.Name);
        Assert.StartsWith("acme-studio", me.Organization.Slug, StringComparison.Ordinal);

        using var current = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(me.UserId, (await ApiClient.ReadAsync<MeResponse>(current)).UserId);
    }

    [Fact]
    public async Task Me_requires_authentication()
    {
        using var client = new ApiClient(factory);

        using var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task State_changing_requests_without_a_csrf_token_are_rejected()
    {
        using var client = new ApiClient(factory);

        using var response = await client.PostWithoutCsrfAsync("/api/auth/login", new { email = "a@example.test", password = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Register_rejects_a_weak_password_and_leaves_no_organization_behind()
    {
        using var client = new ApiClient(factory);

        using var response = await client.PostAsync("/api/auth/register", new
        {
            organizationName = "Weak Password Org",
            displayName = "Someone",
            email = $"weak-{Guid.NewGuid():N}@example.test",
            password = "short",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Portal.Infrastructure.Persistence.PortalDbContext>();
        Assert.False(db.Organizations.IgnoreQueryFilters().Any(o => o.Name == "Weak Password Org"));
    }

    [Fact]
    public async Task Register_rejects_a_duplicate_email()
    {
        var (first, me) = await Given.AnOrganizationAsync(factory);
        first.Dispose();
        using var second = new ApiClient(factory);

        using var response = await second.PostAsync("/api/auth/register", new
        {
            organizationName = "Another Org",
            displayName = "Copycat",
            email = me.Email.ToUpperInvariant(),
            password = Given.Password,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Login_succeeds_with_the_right_password_and_logout_ends_the_session()
    {
        var (registered, me) = await Given.AnOrganizationAsync(factory);
        registered.Dispose();
        using var client = new ApiClient(factory);

        using var login = await client.PostAsync("/api/auth/login", new { email = me.Email, password = Given.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var authenticated = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);

        using var logout = await client.PostAsync("/api/auth/logout");
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using var afterLogout = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    [Fact]
    public async Task Login_gives_the_same_answer_for_unknown_accounts_and_wrong_passwords()
    {
        var (registered, me) = await Given.AnOrganizationAsync(factory);
        registered.Dispose();
        using var client = new ApiClient(factory);

        using var wrongPassword = await client.PostAsync("/api/auth/login", new { email = me.Email, password = "definitely wrong password" });
        using var unknown = await client.PostAsync("/api/auth/login", new { email = "nobody@example.test", password = "definitely wrong password" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        var wrongPasswordProblem = await ApiClient.ReadAsync<ProblemDetails>(wrongPassword);
        var unknownProblem = await ApiClient.ReadAsync<ProblemDetails>(unknown);
        Assert.Equal(wrongPasswordProblem.Title, unknownProblem.Title);
        Assert.Equal(wrongPasswordProblem.Detail, unknownProblem.Detail);
    }

    [Fact]
    public async Task Repeated_failures_lock_the_account_even_for_the_right_password()
    {
        var (registered, me) = await Given.AnOrganizationAsync(factory);
        registered.Dispose();
        using var client = new ApiClient(factory);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var failed = await client.PostAsync("/api/auth/login", new { email = me.Email, password = "wrong password here" });
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        using var locked = await client.PostAsync("/api/auth/login", new { email = me.Email, password = Given.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
    }
}
