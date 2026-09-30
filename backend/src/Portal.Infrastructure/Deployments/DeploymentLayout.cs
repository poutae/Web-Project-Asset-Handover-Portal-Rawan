using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Portal.Infrastructure.Deployments;

/// <summary>What the live-site pointer file says.</summary>
public sealed record LivePointer(string ReleaseKey, bool SpaFallback);

/// <summary>
/// The on-disk layout under <see cref="DeployOptions.Root"/>. Every path segment that comes from outside
/// (site labels, release keys) is validated here, so no caller can build a path that leaves the root.
/// </summary>
public sealed partial class DeploymentLayout
{
    private readonly string _root;

    public DeploymentLayout(IOptions<DeployOptions> options)
    {
        _root = Path.GetFullPath(options.Value.Root);
        Directory.CreateDirectory(_root);
    }

    public string WorkRoot => Path.Combine(_root, "work");

    public static bool IsValidSiteLabel(string label) => SiteLabelPattern().IsMatch(label);

    public static bool IsValidReleaseKey(string key) => ReleaseKeyPattern().IsMatch(key);

    public string WorkDirectory(Guid deploymentId) => Path.Combine(WorkRoot, deploymentId.ToString("N"));

    public string SiteDirectory(string siteLabel)
    {
        Require(IsValidSiteLabel(siteLabel), "site label");
        return Path.Combine(_root, "releases", siteLabel);
    }

    public string ReleaseDirectory(string siteLabel, string releaseKey)
    {
        Require(IsValidReleaseKey(releaseKey), "release key");
        return Path.Combine(SiteDirectory(siteLabel), releaseKey);
    }

    public string PointerFile(string siteLabel)
    {
        Require(IsValidSiteLabel(siteLabel), "site label");
        return Path.Combine(_root, "live", $"{siteLabel}.json");
    }

    public LivePointer? ReadPointer(string siteLabel)
    {
        if (!IsValidSiteLabel(siteLabel))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(PointerFile(siteLabel));
            var pointer = JsonSerializer.Deserialize<LivePointer>(stream);
            return pointer is not null && IsValidReleaseKey(pointer.ReleaseKey) ? pointer : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public DateTime PointerVersion(string siteLabel) =>
        IsValidSiteLabel(siteLabel) ? File.GetLastWriteTimeUtc(PointerFile(siteLabel)) : DateTime.MinValue;

    public void WritePointer(string siteLabel, LivePointer live)
    {
        var path = PointerFile(siteLabel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(live));
        File.Move(temp, path, overwrite: true); // atomic: visitors see the old or the new release, never a mix
    }

    public void DeletePointer(string siteLabel)
    {
        var path = PointerFile(siteLabel);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void Require(bool valid, string what)
    {
        if (!valid)
        {
            throw new ArgumentException($"Invalid {what}.");
        }
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,50}[a-z0-9])?$")]
    private static partial Regex SiteLabelPattern();

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex ReleaseKeyPattern();
}
