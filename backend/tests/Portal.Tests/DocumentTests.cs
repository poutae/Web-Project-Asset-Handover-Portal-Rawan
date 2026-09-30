using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Portal.Api.Contracts;
using Portal.Domain;
using Portal.Tests.Infrastructure;

namespace Portal.Tests;

[Trait("Category", "Integration")]
public sealed class DocumentTests(PortalApiFactory factory) : IClassFixture<PortalApiFactory>
{
    private static readonly byte[] Pdf = [.. "%PDF-1.7\n"u8, .. RandomNumberGenerator.GetBytes(2000)];

    private static MultipartFormDataContent Form(
        byte[] bytes, string fileName, string? title = null, string? visibility = null, string? declaredType = null)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(declaredType ?? "application/octet-stream");
        content.Add(file, "file", fileName);
        if (title is not null)
        {
            content.Add(new StringContent(title), "title");
        }

        if (visibility is not null)
        {
            content.Add(new StringContent(visibility), "visibility");
        }

        return content;
    }

    private int StoredFileCount() =>
        Directory.Exists(factory.StorageRoot) ? Directory.EnumerateFiles(factory.StorageRoot, "*", SearchOption.AllDirectories).Count() : 0;

    private static string Url(ProjectDto project) => $"/api/projects/{project.Id}/documents";

    private static Task<HttpResponseMessage> UploadAsync(
        ApiClient client, ProjectDto project, MultipartFormDataContent form, string? key = null) =>
        client.SendContentAsync(HttpMethod.Post, Url(project), form, key);

    private static async Task<DocumentDto> UploadOkAsync(ApiClient client, ProjectDto project, byte[]? bytes = null, string fileName = "brief.pdf", string? visibility = null)
    {
        using var response = await UploadAsync(client, project, Form(bytes ?? Pdf, fileName, visibility: visibility));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ApiClient.ReadAsync<DocumentDto>(response);
    }

    [Fact]
    public async Task An_upload_is_stored_with_its_size_checksum_and_a_server_assigned_type()
    {
        var (admin, me) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var project = await Given.AProjectAsync(admin);

        // The client claims text/html; the server ignores that and decides from the extension and contents.
        using var response = await UploadAsync(admin, project, Form(Pdf, "Project Brief.pdf", title: "Brief", declaredType: "text/html"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var document = await ApiClient.ReadAsync<DocumentDto>(response);
        Assert.Equal("Brief", document.Title);
        Assert.Equal("Project Brief.pdf", document.FileName);
        Assert.Equal("application/pdf", document.ContentType);
        Assert.Equal(Pdf.Length, document.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Pdf)), document.Sha256);
        Assert.Equal(me.UserId, document.UploadedByUserId);
        Assert.Equal(DocumentVisibility.Internal, document.Visibility);
        Assert.Equal($"\"{document.Version}\"", response.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task A_download_returns_the_exact_bytes_as_a_locked_down_attachment()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var project = await Given.AProjectAsync(admin);
        var document = await UploadOkAsync(admin, project, fileName: "brief.pdf");

        using var response = await admin.GetAsync($"{Url(project)}/{document.Id}/download");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Pdf, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("sandbox", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("page.html", "<html><script>alert(1)</script></html>")]
    [InlineData("vector.svg", "<svg xmlns='http://www.w3.org/2000/svg' onload='alert(1)'/>")]
    [InlineData("tool.exe", "MZ this is a program")]
    [InlineData("script.js", "alert(1)")]
    [InlineData("noextension", "hello")]
    public async Task Types_that_are_not_allowed_are_refused(string fileName, string content)
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var project = await Given.AProjectAsync(admin);

        using var response = await UploadAsync(admin, project, Form(System.Text.Encoding.UTF8.GetBytes(content), fileName));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task A_file_whose_contents_do_not_match_its_extension_is_refused()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var project = await Given.AProjectAsync(admin);

        using var response = await UploadAsync(admin, project, Form("MZ\u0090\u0000 not a pdf"u8.ToArray(), "invoice.pdf"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Files_over_the_limit_are_refused_and_leave_nothing_on_disk()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var project = await Given.AProjectAsync(admin);
        var tooBig = new byte[(int)PortalApiFactory.UploadLimitBytes + 1];
        "%PDF-"u8.CopyTo(tooBig);
        var before = StoredFileCount();

        using var response = await UploadAsync(admin, project, Form(tooBig, "huge.pdf"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(before, StoredFileCount());
    }

    [Fact]
    public async Task Empty_files_and_uploads_without_a_file_are_refused()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var project = await Given.AProjectAsync(admin);

        using var empty = await UploadAsync(admin, project, Form([], "empty.txt"));
        using var none = await UploadAsync(admin, project, new MultipartFormDataContent { { new StringContent("x"), "title" } });

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
    }

    [Fact]
    public async Task A_hostile_file_name_cannot_choose_where_the_file_goes()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var project = await Given.AProjectAsync(admin);

        var document = await UploadOkAsync(admin, project, "hello"u8.ToArray(), fileName: "..\\..\\..\\windows\\evil.txt");

        Assert.Equal("evil.txt", document.FileName);
        var files = Directory.EnumerateFiles(factory.StorageRoot, "*", SearchOption.AllDirectories).ToList();
        Assert.All(files, f => Assert.StartsWith(Path.GetFullPath(factory.StorageRoot), Path.GetFullPath(f), StringComparison.Ordinal));
        Assert.DoesNotContain(files, f => f.Contains("evil", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Retrying_an_upload_with_the_same_key_stores_it_once()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var project = await Given.AProjectAsync(admin);
        var key = Guid.NewGuid().ToString("N");

        using var first = await UploadAsync(admin, project, Form(Pdf, "once.pdf"), key);
        using var retry = await UploadAsync(admin, project, Form(Pdf, "once.pdf"), key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.True(retry.Headers.Contains("Idempotent-Replayed"));
        using var list = await admin.GetAsync(Url(project));
        Assert.Single(await ApiClient.ReadAsync<List<DocumentDto>>(list));
    }

    [Fact]
    public async Task Documents_of_another_organization_or_project_are_not_reachable()
    {
        var (adminA, _) = await Given.AnOrganizationAsync(factory);
        var (adminB, _) = await Given.AnOrganizationAsync(factory);
        using var _a = adminA;
        using var _b = adminB;
        var project = await Given.AProjectAsync(adminA);
        var otherProject = await Given.AProjectAsync(adminA, "Other");
        var document = await UploadOkAsync(adminA, project);

        using var crossTenantDownload = await adminB.GetAsync($"{Url(project)}/{document.Id}/download");
        using var crossTenantList = await adminB.GetAsync(Url(project));
        using var wrongProject = await adminA.GetAsync($"/api/projects/{otherProject.Id}/documents/{document.Id}/download");
        using var crossTenantDelete = await adminB.DeleteAsync($"{Url(project)}/{document.Id}", document.Version);

        Assert.Equal(HttpStatusCode.NotFound, crossTenantDownload.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, crossTenantList.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongProject.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, crossTenantDelete.StatusCode);
    }

    [Fact]
    public async Task Staff_outside_the_project_cannot_download_and_anonymous_users_are_turned_away()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        var (outsider, _) = await Given.APersonAsync(factory, admin, OrgRole.Member);
        using var _admin = admin;
        using var _outsider = outsider;
        using var anonymous = new ApiClient(factory);
        var project = await Given.AProjectAsync(admin);
        var document = await UploadOkAsync(admin, project);

        using var outsiderDownload = await outsider.GetAsync($"{Url(project)}/{document.Id}/download");
        using var anonymousDownload = await anonymous.GetAsync($"{Url(project)}/{document.Id}/download");

        Assert.Equal(HttpStatusCode.NotFound, outsiderDownload.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousDownload.StatusCode);
    }

    [Fact]
    public async Task Clients_only_see_and_download_client_visible_documents_and_upload_as_client_visible()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        var (client, clientMe) = await Given.APersonAsync(factory, admin, OrgRole.Client);
        using var _admin = admin;
        using var _client = client;
        var project = await Given.AProjectAsync(admin);
        using var add = await admin.PostAsync($"/api/projects/{project.Id}/members", new { userId = clientMe.UserId, role = ProjectRole.Client });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        var secret = await UploadOkAsync(admin, project, fileName: "internal.pdf", visibility: "Internal");
        var shared = await UploadOkAsync(admin, project, fileName: "shared.pdf", visibility: "Client");

        using var list = await client.GetAsync(Url(project));
        Assert.Equal(["shared.pdf"], (await ApiClient.ReadAsync<List<DocumentDto>>(list)).Select(d => d.FileName));
        using var secretDownload = await client.GetAsync($"{Url(project)}/{secret.Id}/download");
        using var sharedDownload = await client.GetAsync($"{Url(project)}/{shared.Id}/download");
        Assert.Equal(HttpStatusCode.NotFound, secretDownload.StatusCode);
        Assert.Equal(HttpStatusCode.OK, sharedDownload.StatusCode);

        // A client hands over an asset; asking for "Internal" is overridden, never honoured.
        var asked = await UploadOkAsync(client, project, fileName: "logo.pdf", visibility: "Internal");
        Assert.Equal(DocumentVisibility.Client, asked.Visibility);

        using var clientEditsStaffFile = await client.PutAsync($"{Url(project)}/{shared.Id}", new { title = "mine now", visibility = DocumentVisibility.Client }, shared.Version);
        Assert.Equal(HttpStatusCode.Forbidden, clientEditsStaffFile.StatusCode);
    }

    [Fact]
    public async Task Metadata_edits_use_optimistic_concurrency()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var project = await Given.AProjectAsync(admin);
        var document = await UploadOkAsync(admin, project);
        var url = $"{Url(project)}/{document.Id}";

        using var first = await admin.PutAsync(url, new { title = "Renamed", visibility = DocumentVisibility.Client }, document.Version);
        using var stale = await admin.PutAsync(url, new { title = "Stale", visibility = DocumentVisibility.Internal }, document.Version);
        using var missing = await admin.PutAsync(url, new { title = "No version", visibility = DocumentVisibility.Internal }, version: null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var updated = await ApiClient.ReadAsync<DocumentDto>(first);
        Assert.Equal("Renamed", updated.Title);
        Assert.Equal(DocumentVisibility.Client, updated.Visibility);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_document_removes_its_row_and_its_file()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var project = await Given.AProjectAsync(admin);
        var document = await UploadOkAsync(admin, project);
        var before = StoredFileCount();

        using var withoutVersion = await admin.DeleteAsync($"{Url(project)}/{document.Id}");
        using var stale = await admin.DeleteAsync($"{Url(project)}/{document.Id}", Convert.ToBase64String(new byte[8]));
        using var deleted = await admin.DeleteAsync($"{Url(project)}/{document.Id}", document.Version);
        using var gone = await admin.GetAsync($"{Url(project)}/{document.Id}/download");

        Assert.Equal((HttpStatusCode)428, withoutVersion.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal(before - 1, StoredFileCount());
    }

    [Fact]
    public async Task Only_the_uploader_or_a_lead_can_delete_and_uploads_need_project_access()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        var (uploader, uploaderMe) = await Given.APersonAsync(factory, admin, OrgRole.Member);
        var (other, otherMe) = await Given.APersonAsync(factory, admin, OrgRole.Member);
        var (outsider, _) = await Given.APersonAsync(factory, admin, OrgRole.Member);
        using var _admin = admin;
        using var _uploader = uploader;
        using var _other = other;
        using var _outsider = outsider;
        var project = await Given.AProjectAsync(admin);
        foreach (var person in new[] { uploaderMe, otherMe })
        {
            using var add = await admin.PostAsync($"/api/projects/{project.Id}/members", new { userId = person.UserId, role = ProjectRole.Contributor });
            Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        }

        var document = await UploadOkAsync(uploader, project);

        using var otherDelete = await other.DeleteAsync($"{Url(project)}/{document.Id}", document.Version);
        using var outsiderUpload = await UploadAsync(outsider, project, Form(Pdf, "sneaky.pdf"));
        using var leadDelete = await admin.DeleteAsync($"{Url(project)}/{document.Id}", document.Version);

        Assert.Equal(HttpStatusCode.Forbidden, otherDelete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, outsiderUpload.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, leadDelete.StatusCode);
    }
}
