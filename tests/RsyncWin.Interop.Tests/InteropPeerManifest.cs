using System.Text.Json;
using System.Text.Json.Serialization;

namespace RsyncWin.Interop.Tests;

internal sealed class InteropMatrixManifest
{
    [JsonPropertyName("baseImage")]
    public string BaseImage { get; init; } = "";

    [JsonPropertyName("dockerfile")]
    public string Dockerfile { get; init; } = "";

    [JsonPropertyName("peers")]
    public List<RsyncPeerSpec> Peers { get; init; } = [];

    public static InteropMatrixManifest Load()
    {
        string path = FindManifestPath();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        InteropMatrixManifest manifest = JsonSerializer.Deserialize<InteropMatrixManifest>(
            File.ReadAllText(path), options)
            ?? throw new InvalidDataException($"Interop peer manifest is empty: {path}");

        if (string.IsNullOrWhiteSpace(manifest.BaseImage))
            throw new InvalidDataException($"Interop peer manifest has no baseImage: {path}");
        if (manifest.Peers.Count == 0)
            throw new InvalidDataException($"Interop peer manifest has no peers: {path}");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (RsyncPeerSpec peer in manifest.Peers)
        {
            if (string.IsNullOrWhiteSpace(peer.Id) || !ids.Add(peer.Id))
                throw new InvalidDataException($"Interop peer ids must be unique and non-empty: {path}");
            if (string.IsNullOrWhiteSpace(peer.Version) || string.IsNullOrWhiteSpace(peer.Image))
                throw new InvalidDataException($"Interop peer {peer.Id} has missing version or image: {path}");
            if (string.IsNullOrWhiteSpace(peer.SourceUrl) || string.IsNullOrWhiteSpace(peer.SourceSha256))
                throw new InvalidDataException($"Interop peer {peer.Id} has missing source pin: {path}");
        }

        return manifest;
    }

    private static string FindManifestPath()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "test-fixtures", "interop", "peer-matrix.json");
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate test-fixtures/interop/peer-matrix.json from the test output directory");
    }
}

internal sealed class RsyncPeerSpec
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("version")]
    public string Version { get; init; } = "";

    [JsonPropertyName("image")]
    public string Image { get; init; } = "";

    [JsonPropertyName("sourceUrl")]
    public string SourceUrl { get; init; } = "";

    [JsonPropertyName("packageVersion")]
    public string? PackageVersion { get; init; }

    [JsonPropertyName("sourceSha256")]
    public string SourceSha256 { get; init; } = "";

    public void ValidateVersionOutput(string output)
    {
        string? firstLine = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        string expectedPrefix = $"rsync  version {Version} ";
        if (firstLine is null || !firstLine.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Interop peer {Id} reported unexpected rsync version. " +
                $"Expected {expectedPrefix.TrimEnd()}, got {firstLine ?? "<no output>"}");
        }
    }
}

internal static class InteropPeerSelection
{
    private static readonly Lazy<InteropMatrixManifest> Manifest = new(InteropMatrixManifest.Load);

    public static RsyncPeerSpec Current
    {
        get
        {
            string id = Environment.GetEnvironmentVariable("RSYNCWIN_INTEROP_PEER") ?? "rsync-3.4.3";
            return Manifest.Value.Peers.FirstOrDefault(peer =>
                       string.Equals(peer.Id, id, StringComparison.Ordinal))
                   ?? throw new InvalidOperationException(
                       $"Unknown RSYNCWIN_INTEROP_PEER '{id}'. " +
                       $"Available peers: {string.Join(", ", Manifest.Value.Peers.Select(peer => peer.Id))}");
        }
    }

    public static InteropMatrixManifest CurrentManifest => Manifest.Value;
}
