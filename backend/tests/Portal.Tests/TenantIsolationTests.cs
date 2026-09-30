using System.Net;
using Portal.Api.Contracts;
using Portal.Domain;
using Portal.Infrastructure.Identity;
using Portal.Infrastructure.Persistence;
using Portal.Tests.Infrastructure;

namespace Portal.Tests;

[Trait("Category", "Integration")]
public sealed class TenantIsolationTests(PortalApiFactory factory) : IClassFixture<PortalApiFactory>
{
    [Fact]
    public async Task Members_list_only_contains_the_callers_own_organization()
    {
        var (clientA, meA) = await Given.AnOrganizationAsync(factory);
        var (clientB, meB) = await Given.AnOrganizationAsync(factory);
        using var _a = clientA;
        using var _b = clientB;

        using var response = await clientA.GetAsync("/api/org/members");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var members = await ApiClient.ReadAsync<List<MemberDto>>(response);
        var member = Assert.Single(members);
        Assert.Equal(meA.UserId, member.Id);
        Assert.DoesNotContain(members, m => m.Id == meB.UserId);
    }

    [Fact]
    public async Task Another_organizations_invitation_looks_like_it_does_not_exist()
    {
        var (clientA, _) = await Given.AnOrganizationAsync(factory);
        var (clientB, _) = await Given.AnOrganizationAsync(factory);
        using var _a = clientA;
        using var _b = clientB;

        using var created = await clientA.PostAsync("/api/org/invitations", new { email = $"x-{Guid.NewGuid():N}@example.test", role = OrgRole.Client });
        var invitation = await ApiClient.ReadAsync<CreatedInvitationDto>(created);

        using var revoke = await clientB.DeleteAsync($"/api/org/invitations/{invitation.Id}");
        Assert.Equal(HttpStatusCode.NotFound, revoke.StatusCode);

        using var list = await clientB.GetAsync("/api/org/invitations");
        Assert.Empty(await ApiClient.ReadAsync<List<InvitationDto>>(list));
    }

    [Fact]
    public async Task Queries_without_a_tenant_see_no_tenant_scoped_rows()
    {
        var (client, me) = await Given.AnOrganizationAsync(factory);
        client.Dispose();
        await CreateInvitationAsync(me.Organization.Id);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();

        Assert.Empty(await db.Invitations.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.Organizations.ToListAsync(TestContext.Current.CancellationToken));
        Assert.NotEmpty(await db.Invitations.IgnoreQueryFilters().ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Writes_for_another_tenant_are_rejected()
    {
        var (clientA, meA) = await Given.AnOrganizationAsync(factory);
        var (clientB, meB) = await Given.AnOrganizationAsync(factory);
        clientA.Dispose();
        clientB.Dispose();

        using var scope = factory.Services.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();

        using (tenant.Use(meA.Organization.Id))
        {
            db.Invitations.Add(NewInvitation(meB.Organization.Id));

            await Assert.ThrowsAsync<TenantViolationException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Inserts_without_a_tenant_are_rejected_and_inserts_inside_a_tenant_are_stamped()
    {
        var (client, me) = await Given.AnOrganizationAsync(factory);
        client.Dispose();

        using var scope = factory.Services.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();

        db.Invitations.Add(NewInvitation(Guid.Empty));
        await Assert.ThrowsAsync<TenantViolationException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        db.ChangeTracker.Clear();

        using (tenant.Use(me.Organization.Id))
        {
            var invitation = NewInvitation(Guid.Empty);
            db.Invitations.Add(invitation);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            Assert.Equal(me.Organization.Id, invitation.OrganizationId);
        }
    }

    [Fact]
    public async Task A_rows_tenant_cannot_be_changed_after_creation()
    {
        var (clientA, meA) = await Given.AnOrganizationAsync(factory);
        var (clientB, meB) = await Given.AnOrganizationAsync(factory);
        clientA.Dispose();
        clientB.Dispose();

        using var scope = factory.Services.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();

        using (tenant.Use(meA.Organization.Id))
        {
            var invitation = NewInvitation(Guid.Empty);
            db.Invitations.Add(invitation);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            invitation.OrganizationId = meB.Organization.Id;

            await Assert.ThrowsAsync<TenantViolationException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Every_tenant_scoped_entity_has_a_query_filter()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();

        var tenantScoped = db.Model.GetEntityTypes().Where(t => typeof(ITenantScoped).IsAssignableFrom(t.ClrType)).ToList();

        Assert.NotEmpty(tenantScoped);
        Assert.All(tenantScoped, t => Assert.NotEmpty(t.GetDeclaredQueryFilters()));
        await Task.CompletedTask;
    }

    private async Task<Invitation> CreateInvitationAsync(Guid organizationId)
    {
        using var scope = factory.Services.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        using (tenant.Use(organizationId))
        {
            var invitation = NewInvitation(Guid.Empty);
            db.Invitations.Add(invitation);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return invitation;
        }
    }

    private static Invitation NewInvitation(Guid organizationId)
    {
        var email = $"invitee-{Guid.NewGuid():N}@example.test";
        return new Invitation
        {
            OrganizationId = organizationId,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            Role = OrgRole.Client,
            TokenHash = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            InvitedByUserId = Guid.NewGuid(),
        };
    }
}
