using System.IO;

namespace GlassDesk.Dock.Tests;

/// <summary>
/// Locates the dotnet host to launch helper processes with.
/// </summary>
/// <remarks>
/// The original test hardcoded one developer machine's path (D:\dotnet10\dotnet.exe), so the
/// capture fixtures could only ever run on that machine. Resolve it the same way the SDK
/// itself does: honor DOTNET_HOST_PATH / DOTNET_ROOT when set, otherwise fall back to
/// dotnet on PATH.
///
/// The test host is resolved rather than hardcoded so these fixtures can run on any machine,
/// not just the one they were written on.
/// </remarks>
internal static class DotnetHost
{
    internal static string Resolve()
    {
        var hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(hostPath) && File.Exists(hostPath))
        {
            return hostPath;
        }

        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ROOT_X64");
        if (!string.IsNullOrWhiteSpace(root))
        {
            var candidate = Path.Combine(root, "dotnet.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), "dotnet.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not fatal; keep looking.
            }
        }

        throw new InvalidOperationException(
            "Cannot locate dotnet.exe. Set DOTNET_HOST_PATH or DOTNET_ROOT, or put dotnet on PATH.");
    }
}
