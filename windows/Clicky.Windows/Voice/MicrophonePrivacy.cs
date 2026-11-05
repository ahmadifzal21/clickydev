using Microsoft.Win32;

namespace Clicky.Windows.Voice;

/// <summary>
/// Reads the Windows microphone privacy switches. When they block desktop apps, WinMM either
/// refuses to open the device or hands over silent (all-zero) audio, so Clicky checks these to
/// explain the real cause instead of reporting "nothing was said".
/// </summary>
internal static class MicrophonePrivacy
{
    public const string FixInstructions =
        "Open Settings > Privacy & security > Microphone and turn on \"Microphone access\" and \"Let desktop apps access your microphone\".";

    internal const string ConsentStoreKeyPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// <summary>A message naming the switch that's off, or null if none of them reads "Deny".</summary>
    public static string? GetBlockedMessage() => GetBlockedMessage(Registry.LocalMachine, Registry.CurrentUser);

    /// <summary>Same, reading from the given roots so tests can point it at a scratch registry key.</summary>
    internal static string? GetBlockedMessage(RegistryKey localMachineRoot, RegistryKey currentUserRoot)
    {
        bool isDeviceAccessDenied = IsDenied(localMachineRoot, ConsentStoreKeyPath);
        bool isUserAccessDenied = IsDenied(currentUserRoot, ConsentStoreKeyPath);
        bool isDesktopAppAccessDenied = IsDenied(currentUserRoot, ConsentStoreKeyPath + @"\NonPackaged");

        return isDeviceAccessDenied || isUserAccessDenied || isDesktopAppAccessDenied
            ? "Windows is blocking Clicky from the microphone. " + FixInstructions
            : null;
    }

    private static bool IsDenied(RegistryKey registryRoot, string keyPath)
    {
        try
        {
            using RegistryKey? consentKey = registryRoot.OpenSubKey(keyPath);
            return string.Equals(consentKey?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Unreadable means unknown, not blocked; the caller still reports the underlying failure.
            return false;
        }
    }
}
