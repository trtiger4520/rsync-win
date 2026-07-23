namespace RsyncWin.Fs;

/// <summary>
/// Client-local progress reporting for a running transfer (<c>--progress</c> /
/// <c>--info=progress2</c>). Lives in <c>RsyncWin.Fs</c> because it is the one project referenced by
/// both consumers — <c>RsyncWin.Engine</c> (pull/push) and <c>RsyncWin.Fs</c> (local copy) — without
/// dragging a display concern into the pure protocol core. The pure core
/// (<see cref="RsyncWin.Protocol.Delta.FileReceiver"/>) takes only a bare <c>Action&lt;long&gt;</c>
/// byte-advance delegate, never this interface, so it stays I/O-free.
///
/// See <c>docs/progress-spec.md</c>. Exchanges no wire bytes — purely a rendering signal.
/// </summary>
public interface ITransferProgressSink
{
    /// <summary>Called once before any file. <paramref name="totalBytes"/> is the summed size of the
    /// regular files under consideration (the progress2 denominator); <paramref name="totalFiles"/>
    /// is their count (the <c>to-chk</c> denominator).</summary>
    void Begin(long totalBytes, int totalFiles);

    /// <summary>Called when a file's transfer starts. <paramref name="name"/> is the wire name
    /// (<c>/</c>-separated); <paramref name="size"/> is the file's byte length (the per-file percent
    /// denominator).</summary>
    void BeginFile(string name, long size);

    /// <summary>Called as bytes are written for the current file — per block on pull/local, once per
    /// file on push. <paramref name="bytes"/> is the delta since the last call.</summary>
    void Advance(long bytes);

    /// <summary>Called when the current file's transfer completes.</summary>
    void EndFile();

    /// <summary>Called once after the last file.</summary>
    void End();
}

/// <summary>A sink that does nothing — the default when no progress flag is given, so callers never
/// need to null-check.</summary>
public sealed class NullProgressSink : ITransferProgressSink
{
    public static readonly NullProgressSink Instance = new();

    private NullProgressSink() { }

    public void Begin(long totalBytes, int totalFiles) { }
    public void BeginFile(string name, long size) { }
    public void Advance(long bytes) { }
    public void EndFile() { }
    public void End() { }
}
