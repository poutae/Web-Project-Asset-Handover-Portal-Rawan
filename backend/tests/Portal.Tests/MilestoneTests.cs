using System.Net;
using Portal.Api.Contracts;
using Portal.Domain;
using Portal.Tests.Infrastructure;

namespace Portal.Tests;

[Trait("Category", "Integration")]
public sealed class MilestoneTests(PortalApiFactory factory) : IClassFixture<PortalApiFactory>
{
    private static object Body(string title, MilestoneStatus status = MilestoneStatus.Planned, string? due = "2027-01-31") =>
        new { title, description = "", dueDate = due, status };

    [Fact]
    public async Task Staff_can_create_list_update_and_delete_milestones()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var project = await Given.AProjectAsync(admin);
        var url = $"/api/projects/{project.Id}/milestones";

        using var created = await admin.PostAsync(url, Body("Design sign-off"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var milestone = await ApiClient.ReadAsync<MilestoneDto>(created);
        Assert.Equal(new DateOnly(2027, 1, 31), milestone.DueDate);

        using var listed = await admin.GetAsync(url);
        Assert.Single(await ApiClient.ReadAsync<List<MilestoneDto>>(listed));

        using var updated = await admin.PutAsync($"{url}/{milestone.Id}", Body("Design sign-off", MilestoneStatus.Done), milestone.Version);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var after = await ApiClient.ReadAsync<MilestoneDto>(updated);
        Assert.Equal(MilestoneStatus.Done, after.Status);

        using var deleted = await admin.DeleteAsync($"{url}/{milestone.Id}", after.Version);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var empty = await admin.GetAsync(url);
        Assert.Empty(await ApiClient.ReadAsync<List<MilestoneDto>>(empty));
    }

    [Fact]
    public async Task Stale_milestone_edits_conflict_and_return_the_current_state()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var project = await Given.AProjectAsync(admin);
        var url = $"/api/projects/{project.Id}/milestones";
        using var created = await admin.PostAsync(url, Body("Launch"));
        var milestone = await ApiClient.ReadAsync<MilestoneDto>(created);

        using var first = await admin.PutAsync($"{url}/{milestone.Id}", Body("Launch day", MilestoneStatus.InProgress), milestone.Version);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var stale = await admin.PutAsync($"{url}/{milestone.Id}", Body("Launch (stale)"), milestone.Version);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var conflict = await ApiClient.ReadAsync<Conflict>(stale);
        Assert.Equal("Launch day", conflict.Current.Title);

        using var staleDelete = await admin.DeleteAsync($"{url}/{milestone.Id}", milestone.Version);
        Assert.Equal(HttpStatusCode.Conflict, staleDelete.StatusCode);
    }

    [Fact]
    public async Task Milestone_validation_rejects_bad_input()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var project = await Given.AProjectAsync(admin);

        using var response = await admin.PostAsync($"/api/projects/{project.Id}/milestones", Body(" "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Clients_can_read_milestones_but_not_change_them_and_outsiders_see_nothing()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var (client, clientMe) = await Given.APersonAsync(factory, admin, OrgRole.Client);
        var (outsider, _) = await Given.APersonAsync(factory, admin, OrgRole.Member);
        using var _client = client;
        using var _outsider = outsider;
        var project = await Given.AProjectAsync(admin);
        using var add = await admin.PostAsync($"/api/projects/{project.Id}/members", new { userId = clientMe.UserId, role = ProjectRole.Client });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        var url = $"/api/projects/{project.Id}/milestones";
        using var created = await admin.PostAsync(url, Body("Phase 1"));
        var milestone = await ApiClient.ReadAsync<MilestoneDto>(created);

        using var clientList = await client.GetAsync(url);
        using var clientCreate = await client.PostAsync(url, Body("Sneaky"));
        using var clientUpdate = await client.PutAsync($"{url}/{milestone.Id}", Body("Sneaky"), milestone.Version);
        using var clientDelete = await client.DeleteAsync($"{url}/{milestone.Id}", milestone.Version);
        using var outsiderList = await outsider.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, clientList.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, clientCreate.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, clientUpdate.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, clientDelete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, outsiderList.StatusCode);
    }

    [Fact]
    public async Task Milestones_are_isolated_between_organizations()
    {
        var (adminA, _) = await Given.AnOrganizationAsync(factory);
        var (adminB, _) = await Given.AnOrganizationAsync(factory);
        using var _a = adminA;
        using var _b = adminB;
        var project = await Given.AProjectAsync(adminA);
        var url = $"/api/projects/{project.Id}/milestones";
        using var created = await adminA.PostAsync(url, Body("Private"));
        var milestone = await ApiClient.ReadAsync<MilestoneDto>(created);

        using var list = await adminB.GetAsync(url);
        using var update = await adminB.PutAsync($"{url}/{milestone.Id}", Body("Stolen"), milestone.Version);
        using var delete = await adminB.DeleteAsync($"{url}/{milestone.Id}", milestone.Version);

        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    private sealed record Conflict(string Title, int Status, MilestoneDto Current);
}
