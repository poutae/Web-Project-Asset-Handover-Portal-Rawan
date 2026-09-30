using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Portal.Api.Contracts;
using Portal.Domain;

namespace Portal.Tests;

/// <summary>
/// The frontend speaks enum names, not numbers. This pins the wire format without needing a database; it
/// exists because a number/name mismatch silently broke every write the browser made.
/// </summary>
public sealed class JsonContractTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private JsonSerializerOptions Options()
    {
        using var app = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("ConnectionStrings:Default", "Server=unreachable;Database=none");
            builder.UseSetting("Deploy:WorkerEnabled", "false");
        });
        return app.Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
    }

    [Fact]
    public void Enums_are_written_as_names()
    {
        var json = JsonSerializer.Serialize(
            new MeResponse(Guid.Empty, "a@example.test", "A", OrgRole.Admin, new OrganizationDto(Guid.Empty, "Org", "org")),
            Options());

        Assert.Contains("\"role\":\"Admin\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"Internal\"", NoteVisibility.Internal)]
    [InlineData("\"Client\"", NoteVisibility.Client)]
    [InlineData("2", NoteVisibility.Client)]
    public void Enums_are_read_from_names_and_still_from_numbers(string json, NoteVisibility expected)
    {
        Assert.Equal(expected, JsonSerializer.Deserialize<NoteVisibility>(json, Options()));
    }

    [Fact]
    public void A_note_request_as_the_browser_sends_it_binds()
    {
        var request = JsonSerializer.Deserialize<SaveNoteRequest>("""{"body":"hello","visibility":"Internal"}""", Options());

        Assert.Equal(new SaveNoteRequest("hello", NoteVisibility.Internal), request);
    }
}
