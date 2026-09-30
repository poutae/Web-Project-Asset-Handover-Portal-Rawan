using System.Net;
using Portal.Api.Contracts;
using Portal.Domain;
using Portal.Infrastructure.Persistence;
using Portal.Tests.Infrastructure;

namespace Portal.Tests;

[Trait("Category", "Integration")]
public sealed class InvitationTests(PortalApiFactory factory) : IClassFixture<PortalApiFactory>
{
    [Fact]
    public async Task An_admin_can_invite_and_the_invitee_joins_the_same_organization_with_the_invited_role()
    {
        var (admin, adminMe) = await Given.AnOrganizationAsync(factory);
        using var _ = admin;
        var invitation = await InviteAsync(admin, OrgRole.Client);

        using var invitee = new ApiClient(factory);
        using var accept = await invitee.PostAsync("/api/invitations/accept", new
        {
            token = invitation.Token,
            displayName = "Client Person",
            password = Given.Password,
        });

        Assert.Equal(HttpStatusCode.Created, accept.StatusCode);
        var me = await ApiClient.ReadAsync<MeResponse>(accept);
        Assert.Equal(adminMe.Organization.Id, me.Organization.Id);
        Assert.Equal(OrgRole.Client, me.Role);
        Assert.Equal(invitation.Email, me.Email);

        using var current = await invitee.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);

        using var list = await admin.GetAsync("/api/org/invitations");
        var listed = Assert.Single(await ApiClient.ReadAsync<List<InvitationDto>>(list));
        Assert.Equal(InvitationStatus.Accepted, listed.Status);
    }

    [Fact]
    public async Task An_invitation_token_can_only_be_redeemed_once()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var invitation = await InviteAsync(admin, OrgRole.Member);

        using var first = new ApiClient(factory);
        using var second = new ApiClient(factory);
        var body = new { token = invitation.Token, displayName = "Person", password = Given.Password };

        using var accepted = await first.PostAsync("/api/invitations/accept", body);
        using var replay = await second.PostAsync("/api/invitations/accept", body);

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    [Fact]
    public async Task Racing_redemptions_produce_exactly_one_account()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var invitation = await InviteAsync(admin, OrgRole.Member);
        var body = new { token = invitation.Token, displayName = "Racer", password = Given.Password };

        using var a = new ApiClient(factory);
        using var b = new ApiClient(factory);
        var responses = await Task.WhenAll(a.PostAsync("/api/invitations/accept", body), b.PostAsync("/api/invitations/accept", body));
        using var r0 = responses[0];
        using var r1 = responses[1];

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(
            responses.Where(r => r.StatusCode != HttpStatusCode.Created),
            r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Conflict }));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        var normalized = invitation.Email.ToUpperInvariant();
        Assert.Equal(1, await db.Users.CountAsync(u => u.NormalizedEmail == normalized, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_revoked_invitation_cannot_be_redeemed()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var invitation = await InviteAsync(admin, OrgRole.Member);

        using var revoke = await admin.DeleteAsync($"/api/org/invitations/{invitation.Id}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        using var invitee = new ApiClient(factory);
        using var accept = await invitee.PostAsync("/api/invitations/accept", new { token = invitation.Token, displayName = "Late", password = Given.Password });
        Assert.Equal(HttpStatusCode.BadRequest, accept.StatusCode);
    }

    [Fact]
    public async Task An_expired_invitation_cannot_be_redeemed()
    {
        var (admin, me) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var invitation = await InviteAsync(admin, OrgRole.Member);

        using (var scope = factory.Services.CreateScope())
        {
            var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
            using (tenant.Use(me.Organization.Id))
            {
                var expired = await db.Invitations.SingleAsync(i => i.Id == invitation.Id, TestContext.Current.CancellationToken);
                db.Entry(expired).Property(i => i.ExpiresAt).CurrentValue = DateTimeOffset.UtcNow.AddMinutes(-1);
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }
        }

        using var invitee = new ApiClient(factory);
        using var accept = await invitee.PostAsync("/api/invitations/accept", new { token = invitation.Token, displayName = "Late", password = Given.Password });
        Assert.Equal(HttpStatusCode.BadRequest, accept.StatusCode);
    }

    [Fact]
    public async Task A_guessed_token_is_rejected()
    {
        using var client = new ApiClient(factory);

        using var response = await client.PostAsync("/api/invitations/accept", new
        {
            token = Convert.ToBase64String(new byte[32]).Replace('+', '-').Replace('/', '_').TrimEnd('='),
            displayName = "Guess",
            password = Given.Password,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Inviting_an_existing_account_or_a_pending_email_is_a_conflict()
    {
        var (admin, adminMe) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;

        using var existing = await admin.PostAsync("/api/org/invitations", new { email = adminMe.Email, role = OrgRole.Member });
        Assert.Equal(HttpStatusCode.Conflict, existing.StatusCode);

        var invitation = await InviteAsync(admin, OrgRole.Member);
        using var duplicate = await admin.PostAsync("/api/org/invitations", new { email = invitation.Email, role = OrgRole.Member });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Admins_cannot_invite_other_admins()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;

        using var response = await admin.PostAsync("/api/org/invitations", new { email = $"a-{Guid.NewGuid():N}@example.test", role = OrgRole.Admin });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Only_admins_manage_invitations_and_clients_cannot_list_members()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;

        using var member = await JoinAsync(admin, OrgRole.Member);
        using var client = await JoinAsync(admin, OrgRole.Client);
        var target = $"target-{Guid.NewGuid():N}@example.test";

        using var memberInvite = await member.PostAsync("/api/org/invitations", new { email = target, role = OrgRole.Client });
        using var memberList = await member.GetAsync("/api/org/members");
        using var clientList = await client.GetAsync("/api/org/members");
        using var clientInvites = await client.GetAsync("/api/org/invitations");
        using var anonymous = new ApiClient(factory);
        using var anonymousList = await anonymous.GetAsync("/api/org/members");

        Assert.Equal(HttpStatusCode.Forbidden, memberInvite.StatusCode);
        Assert.Equal(HttpStatusCode.OK, memberList.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, clientList.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, clientInvites.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousList.StatusCode);
    }

    private static async Task<CreatedInvitationDto> InviteAsync(ApiClient admin, OrgRole role)
    {
        using var response = await admin.PostAsync("/api/org/invitations", new
        {
            email = $"invitee-{Guid.NewGuid():N}@example.test",
            role,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ApiClient.ReadAsync<CreatedInvitationDto>(response);
    }

    private async Task<ApiClient> JoinAsync(ApiClient admin, OrgRole role)
    {
        var invitation = await InviteAsync(admin, role);
        var client = new ApiClient(factory);
        using var accept = await client.PostAsync("/api/invitations/accept", new { token = invitation.Token, displayName = $"{role} user", password = Given.Password });
        Assert.Equal(HttpStatusCode.Created, accept.StatusCode);
        return client;
    }
}
