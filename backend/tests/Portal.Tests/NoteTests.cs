using System.Net;
using Portal.Api.Contracts;
using Portal.Domain;
using Portal.Tests.Infrastructure;

namespace Portal.Tests;

[Trait("Category", "Integration")]
public sealed class NoteTests(PortalApiFactory factory) : IClassFixture<PortalApiFactory>
{
    private static object Body(string text, NoteVisibility visibility = NoteVisibility.Internal) => new { body = text, visibility };

    [Fact]
    public async Task Staff_can_write_edit_and_delete_notes()
    {
        var (admin, me) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var project = await Given.AProjectAsync(admin);
        var url = $"/api/projects/{project.Id}/notes";

        using var created = await admin.PostAsync(url, Body("Kick-off call went well"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var note = await ApiClient.ReadAsync<NoteDto>(created);
        Assert.Equal(me.UserId, note.AuthorUserId);
        Assert.Equal(me.DisplayName, note.AuthorName);

        using var edited = await admin.PutAsync($"{url}/{note.Id}", Body("Kick-off call notes", NoteVisibility.Client), note.Version);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var after = await ApiClient.ReadAsync<NoteDto>(edited);
        Assert.Equal(NoteVisibility.Client, after.Visibility);

        using var deleted = await admin.DeleteAsync($"{url}/{note.Id}", after.Version);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task Concurrent_note_edits_conflict()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var project = await Given.AProjectAsync(admin);
        var url = $"/api/projects/{project.Id}/notes";
        using var created = await admin.PostAsync(url, Body("Original"));
        var note = await ApiClient.ReadAsync<NoteDto>(created);

        using var first = await admin.PutAsync($"{url}/{note.Id}", Body("First edit"), note.Version);
        using var second = await admin.PutAsync($"{url}/{note.Id}", Body("Second edit"), note.Version);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Clients_only_see_client_visible_notes_and_cannot_write_internal_ones()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var (client, clientMe) = await Given.APersonAsync(factory, admin, OrgRole.Client);
        using var _client = client;
        var project = await Given.AProjectAsync(admin);
        using var add = await admin.PostAsync($"/api/projects/{project.Id}/members", new { userId = clientMe.UserId, role = ProjectRole.Client });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        var url = $"/api/projects/{project.Id}/notes";

        using var internalNote = await admin.PostAsync(url, Body("Budget is tight", NoteVisibility.Internal));
        using var sharedNote = await admin.PostAsync(url, Body("Design v1 ready", NoteVisibility.Client));
        var hidden = await ApiClient.ReadAsync<NoteDto>(internalNote);

        using var list = await client.GetAsync(url);
        var visible = await ApiClient.ReadAsync<List<NoteDto>>(list);
        Assert.Equal(["Design v1 ready"], visible.Select(n => n.Body));

        using var editHidden = await client.PutAsync($"{url}/{hidden.Id}", Body("Tampered", NoteVisibility.Client), hidden.Version);
        Assert.Equal(HttpStatusCode.NotFound, editHidden.StatusCode);

        using var writeInternal = await client.PostAsync(url, Body("Secret", NoteVisibility.Internal));
        Assert.Equal(HttpStatusCode.BadRequest, writeInternal.StatusCode);

        using var comment = await client.PostAsync(url, Body("Looks great!", NoteVisibility.Client));
        Assert.Equal(HttpStatusCode.Created, comment.StatusCode);

        var staffNote = await ApiClient.ReadAsync<NoteDto>(sharedNote);
        using var editStaffNote = await client.PutAsync($"{url}/{staffNote.Id}", Body("Rewritten", NoteVisibility.Client), staffNote.Version);
        Assert.Equal(HttpStatusCode.Forbidden, editStaffNote.StatusCode);
    }

    [Fact]
    public async Task Only_the_author_or_a_lead_can_change_a_note()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var (contributor, contributorMe) = await Given.APersonAsync(factory, admin, OrgRole.Member);
        var (other, otherMe) = await Given.APersonAsync(factory, admin, OrgRole.Member);
        using var _contributor = contributor;
        using var _other = other;
        var project = await Given.AProjectAsync(admin);
        foreach (var person in new[] { contributorMe, otherMe })
        {
            using var add = await admin.PostAsync($"/api/projects/{project.Id}/members", new { userId = person.UserId, role = ProjectRole.Contributor });
            Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        }

        var url = $"/api/projects/{project.Id}/notes";
        using var created = await contributor.PostAsync(url, Body("Mine"));
        var note = await ApiClient.ReadAsync<NoteDto>(created);

        using var otherEdit = await other.PutAsync($"{url}/{note.Id}", Body("Not yours"), note.Version);
        using var leadEdit = await admin.PutAsync($"{url}/{note.Id}", Body("Moderated"), note.Version);

        Assert.Equal(HttpStatusCode.Forbidden, otherEdit.StatusCode);
        Assert.Equal(HttpStatusCode.OK, leadEdit.StatusCode);
    }

    [Fact]
    public async Task Notes_are_isolated_between_organizations_and_projects()
    {
        var (adminA, _) = await Given.AnOrganizationAsync(factory);
        var (adminB, _) = await Given.AnOrganizationAsync(factory);
        using var _a = adminA;
        using var _b = adminB;
        var project = await Given.AProjectAsync(adminA);
        var otherProject = await Given.AProjectAsync(adminA, "Other project");
        var url = $"/api/projects/{project.Id}/notes";
        using var created = await adminA.PostAsync(url, Body("Private"));
        var note = await ApiClient.ReadAsync<NoteDto>(created);

        using var crossTenant = await adminB.GetAsync(url);
        using var wrongProject = await adminA.PutAsync($"/api/projects/{otherProject.Id}/notes/{note.Id}", Body("Moved"), note.Version);

        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongProject.StatusCode);
    }

    [Fact]
    public async Task Empty_notes_are_rejected()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var project = await Given.AProjectAsync(admin);

        using var response = await admin.PostAsync($"/api/projects/{project.Id}/notes", Body("  "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
