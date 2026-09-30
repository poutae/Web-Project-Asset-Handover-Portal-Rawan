using System.Net;
using Microsoft.AspNetCore.SignalR.Client;
using Portal.Api.Contracts;
using Portal.Api.Realtime;
using Portal.Domain;
using Portal.Infrastructure.Persistence;
using Portal.Tests.Infrastructure;

namespace Portal.Tests;

[Trait("Category", "Integration")]
public sealed class RealtimeTests(PortalApiFactory factory) : IClassFixture<PortalApiFactory>
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(1500);

    [Fact]
    public async Task Unauthenticated_connections_are_rejected()
    {
        using var anonymous = new ApiClient(factory);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => RealtimeListener.ConnectAsync(factory, anonymous));
    }

    [Fact]
    public async Task Project_members_and_admins_receive_changes_but_outsiders_and_other_tenants_do_not()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        var (member, memberMe) = await Given.APersonAsync(factory, admin, OrgRole.Member);
        var (outsider, _) = await Given.APersonAsync(factory, admin, OrgRole.Member);
        var (otherTenant, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        using var _member = member;
        using var _outsider = outsider;
        using var _other = otherTenant;
        var project = await Given.AProjectAsync(admin);
        using var add = await admin.PostAsync($"/api/projects/{project.Id}/members", new { userId = memberMe.UserId, role = ProjectRole.Contributor });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);

        await using var adminListener = await RealtimeListener.ConnectAsync(factory, admin);
        await using var memberListener = await RealtimeListener.ConnectAsync(factory, member);
        await using var outsiderListener = await RealtimeListener.ConnectAsync(factory, outsider);
        await using var otherListener = await RealtimeListener.ConnectAsync(factory, otherTenant);

        using var created = await admin.PostAsync($"/api/projects/{project.Id}/milestones", new { title = "Launch", description = "", dueDate = (string?)null, status = MilestoneStatus.Planned });
        var milestone = await ApiClient.ReadAsync<MilestoneDto>(created);

        bool IsThisMilestone(RealtimeEvent e) => e.Kind == RealtimeKinds.Milestone && e.EntityId == milestone.Id;
        var forAdmin = await adminListener.WaitForAsync(IsThisMilestone);
        var forMember = await memberListener.WaitForAsync(IsThisMilestone);

        Assert.NotNull(forAdmin);
        Assert.NotNull(forMember);
        Assert.Equal(RealtimeActions.Created, forMember.Action);
        Assert.Equal(project.Id, forMember.ProjectId);
        Assert.Equal(milestone.Version, forMember.Version);
        Assert.Null(await outsiderListener.WaitForAsync(IsThisMilestone, Quiet));
        Assert.Null(await otherListener.WaitForAsync(IsThisMilestone, Quiet));
    }

    [Fact]
    public async Task Internal_notes_are_not_pushed_to_clients_but_client_visible_ones_are()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        var (client, clientMe) = await Given.APersonAsync(factory, admin, OrgRole.Client);
        using var _admin = admin;
        using var _client = client;
        var project = await Given.AProjectAsync(admin);
        using var add = await admin.PostAsync($"/api/projects/{project.Id}/members", new { userId = clientMe.UserId, role = ProjectRole.Client });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        await using var clientListener = await RealtimeListener.ConnectAsync(factory, client);

        using var secret = await admin.PostAsync($"/api/projects/{project.Id}/notes", new { body = "internal", visibility = NoteVisibility.Internal });
        var secretNote = await ApiClient.ReadAsync<NoteDto>(secret);
        Assert.Null(await clientListener.WaitForAsync(e => e.EntityId == secretNote.Id, Quiet));

        using var shared = await admin.PostAsync($"/api/projects/{project.Id}/notes", new { body = "shared", visibility = NoteVisibility.Client });
        var sharedNote = await ApiClient.ReadAsync<NoteDto>(shared);
        Assert.NotNull(await clientListener.WaitForAsync(e => e.EntityId == sharedNote.Id));

        // Making a shared note internal must still tell the client, so it can drop the note.
        using var hide = await admin.PutAsync($"/api/projects/{project.Id}/notes/{sharedNote.Id}", new { body = "shared", visibility = NoteVisibility.Internal }, sharedNote.Version);
        Assert.Equal(HttpStatusCode.OK, hide.StatusCode);
        var dropped = await clientListener.WaitForAsync(e => e.EntityId == sharedNote.Id && e.Action == RealtimeActions.Updated);
        Assert.NotNull(dropped);
    }

    [Fact]
    public async Task A_removed_member_is_told_so_and_stops_receiving_changes()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        var (member, memberMe) = await Given.APersonAsync(factory, admin, OrgRole.Member);
        using var _admin = admin;
        using var _member = member;
        var project = await Given.AProjectAsync(admin);
        using var add = await admin.PostAsync($"/api/projects/{project.Id}/members", new { userId = memberMe.UserId, role = ProjectRole.Contributor });
        var added = await ApiClient.ReadAsync<ProjectMemberDto>(add);
        await using var memberListener = await RealtimeListener.ConnectAsync(factory, member);

        using var remove = await admin.DeleteAsync($"/api/projects/{project.Id}/members/{memberMe.UserId}", added.Version);
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        Assert.NotNull(await memberListener.WaitForAsync(e => e.Kind == RealtimeKinds.Member && e.Action == RealtimeActions.Deleted));

        using var created = await admin.PostAsync($"/api/projects/{project.Id}/milestones", new { title = "After removal", description = "", dueDate = (string?)null, status = MilestoneStatus.Planned });
        var milestone = await ApiClient.ReadAsync<MilestoneDto>(created);
        Assert.Null(await memberListener.WaitForAsync(e => e.EntityId == milestone.Id, Quiet));
    }

    [Fact]
    public async Task Cross_origin_realtime_connections_are_refused()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        using var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{PortalHub.Path}/negotiate?negotiateVersion=1");
        request.Headers.Add("Cookie", admin.CookieHeader);
        request.Headers.Add("Origin", "https://evil.example");

        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
