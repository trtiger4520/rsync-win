namespace RsyncWin.Protocol.Tests;

/// <summary>Locates checked-in golden vectors by walking up from the test binary to the repo root.</summary>
public static class TestFixtures
{
    private static readonly Lazy<string> Root = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "test-fixtures", "vectors")))
                return Path.Combine(dir.FullName, "test-fixtures", "vectors");
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "test-fixtures/vectors not found above the test binary. Run tests from within the repo; " +
            "regenerate vectors with test-fixtures/capture/capture.sh if the directory is missing.");
    });

    public static string PathOf(params string[] parts) =>
        Path.Combine([Root.Value, .. parts]);

    public static byte[] Bytes(params string[] parts) => File.ReadAllBytes(PathOf(parts));

    public static string[] Lines(params string[] parts) => File.ReadAllLines(PathOf(parts));
}
