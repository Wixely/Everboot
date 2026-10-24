using System.Text.RegularExpressions;

namespace Everboot.Services;

internal static class WinPeStartup
{
    public const string Shell = "[LaunchApps]\r\n\"everboot.cmd\"\r\n";

    public static string Script(string authority, string slug)
    {
        // These values become cmd.exe input: accept only host/share syntax,
        // never shell metacharacters supplied through an HTTP Host header.
        if (!Uri.TryCreate("http://" + authority, UriKind.Absolute, out var uri) ||
            !Regex.IsMatch(uri.Host, @"\A[a-zA-Z0-9.-]+\z") ||
            !Regex.IsMatch(slug, @"\A[a-zA-Z0-9_-]+\z"))
            throw new ArgumentException("WinPE requires a valid IPv4 address or DNS hostname and image slug.");
        return string.Join("\r\n", new[] {
            "@echo off", "title Everboot - Windows network setup", "echo Initializing Windows PE networking...",
            "wpeinit", ":retry", "echo Connecting to the installation source...",
            $"net use Z: \\\\{uri.Host}\\{slug} /persistent:no",
            "if errorlevel 1 goto failed", "if not exist Z:\\setup.exe goto failed",
            "echo Starting Windows Setup from Everboot...", "Z:\\setup.exe",
            "echo Windows Setup has exited.", "goto shell", ":failed",
            "echo Could not open the installation source. Check the network and Everboot SMB service.",
            "echo Type exit to retry after troubleshooting. Installation has not been started.",
            "cmd.exe", "goto retry", ":shell", "cmd.exe", "" });
    }
}
