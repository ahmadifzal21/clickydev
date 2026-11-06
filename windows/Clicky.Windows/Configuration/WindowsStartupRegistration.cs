using System.IO;
using Microsoft.Win32;

namespace Clicky.Windows.Configuration;

/// <summary>
/// "Start with Windows": a value under HKCU\...\CurrentVersion\Run pointing at the running exe. Per-user, so no admin
/// rights. The registry is the truth (the user may delete the value or move the exe); the config's
/// <see cref="ClickyConfiguration.StartWithWindows"/> follows it on start.
/// </summary>
internal static class WindowsStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string RunValueName = "Clicky";

    /// <summary>The exe this process runs from; a `dotnet Clicky.dll` run has none worth registering.</summary>
    private static string? CurrentExecutablePath =>
        Environment.ProcessPath is { } processPath && processPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && !Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? processPath : null;

    /// <summary>True when the Run value starts this exe (a value left by a copy elsewhere doesn't count).</summary>
    public static bool IsEnabled()
    {
        using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return CurrentExecutablePath is { } executablePath && IsRunCommandFor(runKey?.GetValue(RunValueName) as string, executablePath);
    }

    /// <summary>Writes or deletes the Run value; throws if the registry refuses (policy, permissions).</summary>
    public static void SetEnabled(bool isEnabled)
    {
        using RegistryKey runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (!isEnabled)
        {
            runKey.DeleteValue(RunValueName, throwOnMissingValue: false);
            return;
        }

        string executablePath = CurrentExecutablePath
            ?? throw new InvalidOperationException("Clicky isn't running from its own .exe, so there is nothing to start with Windows.");
        runKey.SetValue(RunValueName, BuildRunCommand(executablePath), RegistryValueKind.String);
    }

    /// <summary>Quoted, so a path with spaces isn't split into a program and arguments.</summary>
    internal static string BuildRunCommand(string executablePath) => $"\"{executablePath}\"";

    internal static bool IsRunCommandFor(string? runCommand, string executablePath)
    {
        if (string.IsNullOrWhiteSpace(runCommand)) return false;
        string trimmedCommand = runCommand.Trim();
        // The program is the quoted part, or everything before the first space when unquoted.
        string program = trimmedCommand.StartsWith('"') ? trimmedCommand[1..].Split('"')[0] : trimmedCommand.Split(' ')[0];
        try
        {
            return string.Equals(Path.GetFullPath(program), Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false; // A hand-edited value that isn't a path.
        }
    }
}
