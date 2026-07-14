using System.ComponentModel;
using System.Diagnostics;
using Xunit.Sdk;

namespace RsyncWin.Fs.Tests;

internal static class WindowsFsTestSupport
{
    public static string CreateTempDirectory(string prefix)
    {
        RequireWindows();

        string path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        foreach (string childPath in Directory.EnumerateFileSystemEntries(path))
        {
            FileAttributes attributes = File.GetAttributes(childPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                File.SetAttributes(childPath, attributes & ~FileAttributes.ReadOnly);
                if ((attributes & FileAttributes.Directory) != 0)
                    Directory.Delete(childPath, recursive: false);
                else
                    File.Delete(childPath);
            }
            else if ((attributes & FileAttributes.Directory) != 0)
            {
                DeleteDirectory(childPath);
            }
            else
            {
                File.SetAttributes(childPath, attributes & ~FileAttributes.ReadOnly);
                File.Delete(childPath);
            }
        }

        FileAttributes rootAttributes = File.GetAttributes(path);
        File.SetAttributes(path, rootAttributes & ~FileAttributes.ReadOnly);
        Directory.Delete(path, recursive: false);
    }

    public static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Windows NTFS validation requires a Windows test host");
    }

    public static void CreateFileSymlinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw SkipException.ForSkip($"File symbolic links are unavailable on this host: {ex.Message}");
        }
    }

    public static void CreateDirectorySymlinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw SkipException.ForSkip($"Directory symbolic links are unavailable on this host: {ex.Message}");
        }
    }

    public static void CreateDirectoryJunctionOrSkip(string linkPath, string targetPath)
    {
        RequireWindows();

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            process.StartInfo.ArgumentList.Add("/d");
            process.StartInfo.ArgumentList.Add("/c");
            process.StartInfo.ArgumentList.Add("mklink");
            process.StartInfo.ArgumentList.Add("/J");
            process.StartInfo.ArgumentList.Add(linkPath);
            process.StartInfo.ArgumentList.Add(targetPath);

            if (!process.Start())
                throw new InvalidOperationException("cmd.exe did not start");

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(5_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("mklink /J did not exit within 5 seconds");
            }

            if (process.ExitCode != 0 || !Directory.Exists(linkPath))
            {
                string detail = string.IsNullOrWhiteSpace(error) ? output : error;
                throw new UnauthorizedAccessException(
                    $"mklink /J failed with exit code {process.ExitCode}: {detail.Trim()}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException
            or Win32Exception or TimeoutException)
        {
            throw SkipException.ForSkip($"Directory junctions are unavailable on this host: {ex.Message}");
        }
    }
}
