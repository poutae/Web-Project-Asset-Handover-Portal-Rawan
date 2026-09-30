using System.Net;
using Portal.Api.Contracts;
using Portal.Domain;
using Portal.Tests.Infrastructure;

namespace Portal.Tests;

[Trait("Category", "Integration")]
public sealed class ProjectTests(PortalApiFactory factory) : IClassFixture<PortalApiFactory>
{
    [Fact]
    public async Task Creating_a_project_returns_an_etag_and_makes_the_creator_its_lead()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;

        using var response = await admin.PostAsync("/api/projects", new { name = "Brand refresh", description = "Phase 1" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var project = await ApiClient.ReadAsync<ProjectDto>(response);
        Assert.Equal("Brand refresh", project.Name);
        Assert.Equal(ProjectRole.Lead, project.MyRole);
        Assert.Equal($"\"{project.Version}\"", response.Headers.ETag?.Tag);

        using var members = await admin.GetAsync($"/api/projects/{project.Id}/members");
        Assert.Single(await ApiClient.ReadAsync<List<ProjectMemberDto>>(members));
    }

    [Fact]
    public async Task Project_names_are_validated()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;

        using var response = await admin.PostAsync("/api/projects", new { name = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Another_organization_cannot_see_or_change_a_project()
    {
        var (adminA, _) = await Given.AnOrganizationAsync(factory);
        var (adminB, _) = await Given.AnOrganizationAsync(factory);
        using var _a = adminA;
        using var _b = adminB;
        var project = await Given.AProjectAsync(adminA);

        using var get = await adminB.GetAsync($"/api/projects/{project.Id}");
        using var put = await adminB.PutAsync($"/api/projects/{project.Id}", new { name = "Hijacked", description = "", status = ProjectStatus.Active }, project.Version);
        using var list = await adminB.GetAsync("/api/projects");

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Empty(await ApiClient.ReadAsync<List<ProjectDto>>(list));
    }

    [Fact]
    public async Task Updating_with_the_current_version_succeeds_and_returns_a_new_version()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var project = await Given.AProjectAsync(admin);

        using var response = await admin.PutAsync(
            $"/api/projects/{project.Id}",
            new { name = "Renamed", description = "Updated", status = ProjectStatus.OnHold },
            project.Version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ApiClient.ReadAsync<ProjectDto>(response);
        Assert.Equal("Renamed", updated.Name);
        Assert.Equal(ProjectStatus.OnHold, updated.Status);
        Assert.NotEqual(project.Version, updated.Version);
    }

    [Fact]
    public async Task Updating_without_a_version_is_refused()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var project = await Given.AProjectAsync(admin);

        using var response = await admin.PutAsync(
            $"/api/projects/{project.Id}", new { name = "No version", description = "", status = ProjectStatus.Active }, version: null);

        Assert.Equal((HttpStatusCode)428, response.StatusCode);
    }

    [Fact]
    public async Task Concurrent_edits_conflict_instead_of_overwriting_and_report_the_current_state()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var (colleague, colleagueMe) = await Given.APersonAsync(factory, admin, OrgRole.Member);
        using var _colleague = colleague;
        var project = await Given.AProjectAsync(admin);
        using var add = await admin.PostAsync($"/api/projects/{project.Id}/members", new { userId = colleagueMe.UserId, role = ProjectRole.Lead });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);

        // Both people load the same version, then both edit it.
        using var loadedByColleague = await colleague.GetAsync($"/api/projects/{project.Id}");
        var colleagueCopy = await ApiClient.ReadAsync<ProjectDto>(loadedByColleague);

        using var first = await admin.PutAsync(
            $"/api/projects/{project.Id}", new { name = "Admin's title", description = "", status = ProjectStatus.Active }, project.Version);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await colleague.PutAsync(
            $"/api/projects/{project.Id}", new { name = "Colleague's title", description = "", status = ProjectStatus.Active }, colleagueCopy.Version);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await ApiClient.ReadAsync<ConflictBody>(second);
        Assert.Equal("Admin's title", body.Current.Name);
        Assert.NotEqual(colleagueCopy.Version, body.Current.Version);

        using var reloaded = await admin.GetAsync($"/api/projects/{project.Id}");
        Assert.Equal("Admin's title", (await ApiClient.ReadAsync<ProjectDto>(reloaded)).Name);
    }

    [Fact]
    public async Task Retrying_a_create_with_the_same_idempotency_key_does_not_create_a_second_project()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var key = Guid.NewGuid().ToString("N");
        var body = new { name = "Only once", description = "" };

        using var first = await admin.PostAsync("/api/projects", body, key);
        using var retry = await admin.PostAsync("/api/projects", body, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.True(retry.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(
            (await ApiClient.ReadAsync<ProjectDto>(first)).Id,
            (await ApiClient.ReadAsync<ProjectDto>(retry)).Id);

        using var list = await admin.GetAsync("/api/projects");
        Assert.Single(await ApiClient.ReadAsync<List<ProjectDto>>(list));
    }

    [Fact]
    public async Task Racing_requests_with_the_same_key_run_the_mutation_once()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var key = Guid.NewGuid().ToString("N");
        var body = new { name = "Race", description = "" };

        var responses = await Task.WhenAll(
            admin.PostAsync("/api/projects", body, key),
            admin.PostAsync("/api/projects", body, key),
            admin.PostAsync("/api/projects", body, key));
        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict });
            }
        }

        using var list = await admin.GetAsync("/api/projects");
        Assert.Single(await ApiClient.ReadAsync<List<ProjectDto>>(list));
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_request_is_rejected()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var key = Guid.NewGuid().ToString("N");

        using var first = await admin.PostAsync("/api/projects", new { name = "One", description = "" }, key);
        using var second = await admin.PostAsync("/api/projects", new { name = "Two", description = "" }, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
    }

    [Fact]
    public async Task Mutations_without_an_idempotency_key_are_refused()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;

        using var response = await admin.SendAsync(HttpMethod.Post, "/api/projects", new { name = "Keyless", description = "" }, idempotencyKey: "");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Staff_only_see_projects_they_belong_to_and_admins_see_everything()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var (member, memberMe) = await Given.APersonAsync(factory, admin, OrgRole.Member);
        using var _member = member;
        var hidden = await Given.AProjectAsync(admin, "Hidden");
        var shared = await Given.AProjectAsync(admin, "Shared");

        using var beforeAdd = await member.GetAsync($"/api/projects/{shared.Id}");
        Assert.Equal(HttpStatusCode.NotFound, beforeAdd.StatusCode);

        using var add = await admin.PostAsync($"/api/projects/{shared.Id}/members", new { userId = memberMe.UserId, role = ProjectRole.Contributor });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);

        using var afterAdd = await member.GetAsync($"/api/projects/{shared.Id}");
        Assert.Equal(HttpStatusCode.OK, afterAdd.StatusCode);
        using var stillHidden = await member.GetAsync($"/api/projects/{hidden.Id}");
        Assert.Equal(HttpStatusCode.NotFound, stillHidden.StatusCode);

        using var memberList = await member.GetAsync("/api/projects");
        var visible = await ApiClient.ReadAsync<List<ProjectDto>>(memberList);
        Assert.Equal(["Shared"], visible.Select(p => p.Name));

        using var adminList = await admin.GetAsync("/api/projects");
        Assert.Equal(2, (await ApiClient.ReadAsync<List<ProjectDto>>(adminList)).Count);
    }

    [Fact]
    public async Task Contributors_and_clients_cannot_edit_the_project_or_its_members()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var (contributor, contributorMe) = await Given.APersonAsync(factory, admin, OrgRole.Member);
        var (client, clientMe) = await Given.APersonAsync(factory, admin, OrgRole.Client);
        using var _contributor = contributor;
        using var _client = client;
        var project = await Given.AProjectAsync(admin);
        using var addContributor = await admin.PostAsync($"/api/projects/{project.Id}/members", new { userId = contributorMe.UserId, role = ProjectRole.Contributor });
        using var addClient = await admin.PostAsync($"/api/projects/{project.Id}/members", new { userId = clientMe.UserId, role = ProjectRole.Client });
        Assert.Equal(HttpStatusCode.Created, addContributor.StatusCode);
        Assert.Equal(HttpStatusCode.Created, addClient.StatusCode);

        foreach (var caller in new[] { contributor, client })
        {
            using var update = await caller.PutAsync(
                $"/api/projects/{project.Id}", new { name = "Nope", description = "", status = ProjectStatus.Active }, project.Version);
            using var addMember = await caller.PostAsync($"/api/projects/{project.Id}/members", new { userId = clientMe.UserId, role = ProjectRole.Client });
            Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, addMember.StatusCode);
        }

        using var clientCreates = await client.PostAsync("/api/projects", new { name = "Client project", description = "" });
        Assert.Equal(HttpStatusCode.Forbidden, clientCreates.StatusCode);
    }

    [Fact]
    public async Task Membership_rules_are_enforced()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        var (otherAdmin, otherMe) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        using var _other = otherAdmin;
        var (client, clientMe) = await Given.APersonAsync(factory, admin, OrgRole.Client);
        var (member, memberMe) = await Given.APersonAsync(factory, admin, OrgRole.Member);
        using var _client = client;
        using var _member = member;
        var project = await Given.AProjectAsync(admin);

        using var crossTenant = await admin.PostAsync($"/api/projects/{project.Id}/members", new { userId = otherMe.UserId, role = ProjectRole.Lead });
        using var clientAsStaff = await admin.PostAsync($"/api/projects/{project.Id}/members", new { userId = clientMe.UserId, role = ProjectRole.Contributor });
        using var staffAsClient = await admin.PostAsync($"/api/projects/{project.Id}/members", new { userId = memberMe.UserId, role = ProjectRole.Client });
        using var ok = await admin.PostAsync($"/api/projects/{project.Id}/members", new { userId = memberMe.UserId, role = ProjectRole.Contributor });
        using var duplicate = await admin.PostAsync($"/api/projects/{project.Id}/members", new { userId = memberMe.UserId, role = ProjectRole.Lead });

        Assert.Equal(HttpStatusCode.BadRequest, crossTenant.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, clientAsStaff.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, staffAsClient.StatusCode);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Members_can_be_promoted_and_removed_with_their_version()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var (member, memberMe) = await Given.APersonAsync(factory, admin, OrgRole.Member);
        using var _member = member;
        var project = await Given.AProjectAsync(admin);
        using var add = await admin.PostAsync($"/api/projects/{project.Id}/members", new { userId = memberMe.UserId, role = ProjectRole.Contributor });
        var added = await ApiClient.ReadAsync<ProjectMemberDto>(add);
        var url = $"/api/projects/{project.Id}/members/{memberMe.UserId}";

        using var stale = await admin.PutAsync(url, new { role = ProjectRole.Lead }, Convert.ToBase64String(new byte[8]));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using var promote = await admin.PutAsync(url, new { role = ProjectRole.Lead }, added.Version);
        Assert.Equal(HttpStatusCode.OK, promote.StatusCode);
        var promoted = await ApiClient.ReadAsync<ProjectMemberDto>(promote);
        Assert.Equal(ProjectRole.Lead, promoted.Role);

        using var removeWithoutVersion = await admin.DeleteAsync(url);
        Assert.Equal((HttpStatusCode)428, removeWithoutVersion.StatusCode);

        using var remove = await admin.DeleteAsync(url, promoted.Version);
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);

        using var gone = await member.GetAsync($"/api/projects/{project.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    private sealed record ConflictBody(string Title, int Status, ProjectDto Current);
}
