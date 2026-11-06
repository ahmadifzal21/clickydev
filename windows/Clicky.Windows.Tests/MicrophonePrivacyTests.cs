using Clicky.Windows.Voice;
using Microsoft.Win32;

namespace Clicky.Windows.Tests;

/// <summary>Points the checks at scratch HKCU keys standing in for both registry roots; deleted afterwards.</summary>
public sealed class MicrophonePrivacyTests : IDisposable
{
    private readonly string scratchKeyPath = $@"Software\ClickyTests\{Guid.NewGuid():N}";
    private readonly RegistryKey fakeLocalMachineRoot;
    private readonly RegistryKey fakeCurrentUserRoot;

    public MicrophonePrivacyTests()
    {
        fakeLocalMachineRoot = Registry.CurrentUser.CreateSubKey(scratchKeyPath + @"\LocalMachine");
        fakeCurrentUserRoot = Registry.CurrentUser.CreateSubKey(scratchKeyPath + @"\CurrentUser");
    }

    private static void SetConsent(RegistryKey registryRoot, string keyPath, string consentValue)
    {
        using RegistryKey consentKey = registryRoot.CreateSubKey(keyPath);
        consentKey.SetValue("Value", consentValue);
    }

    [Fact]
    public void NoConsentKeys_IsNotBlocked() =>
        Assert.Null(MicrophonePrivacy.GetBlockedMessage(fakeLocalMachineRoot, fakeCurrentUserRoot));

    [Fact]
    public void AllowedEverywhere_IsNotBlocked()
    {
        SetConsent(fakeLocalMachineRoot, MicrophonePrivacy.ConsentStoreKeyPath, "Allow");
        SetConsent(fakeCurrentUserRoot, MicrophonePrivacy.ConsentStoreKeyPath, "Allow");
        SetConsent(fakeCurrentUserRoot, MicrophonePrivacy.ConsentStoreKeyPath + @"\NonPackaged", "Allow");

        Assert.Null(MicrophonePrivacy.GetBlockedMessage(fakeLocalMachineRoot, fakeCurrentUserRoot));
    }

    [Theory]
    [InlineData(true, "")]
    [InlineData(false, "")]
    [InlineData(false, @"\NonPackaged")]
    public void AnyDeniedSwitch_IsBlocked_WithFixInstructions(bool isLocalMachine, string subKeySuffix)
    {
        SetConsent(isLocalMachine ? fakeLocalMachineRoot : fakeCurrentUserRoot, MicrophonePrivacy.ConsentStoreKeyPath + subKeySuffix, "deny");

        string? blockedMessage = MicrophonePrivacy.GetBlockedMessage(fakeLocalMachineRoot, fakeCurrentUserRoot);

        Assert.NotNull(blockedMessage);
        Assert.Contains(MicrophonePrivacy.FixInstructions, blockedMessage);
    }

    [Fact]
    public void UnreadableRegistry_CountsAsNotBlocked()
    {
        RegistryKey closedRoot = Registry.CurrentUser.OpenSubKey(scratchKeyPath)!;
        closedRoot.Dispose(); // Reading a closed key throws; that must not be reported as "blocked".

        Assert.Null(MicrophonePrivacy.GetBlockedMessage(closedRoot, closedRoot));
    }

    [Fact]
    public void RealRegistry_CanBeRead() =>
        // This PC's real switches: either allowed (null) or a message with the fix.
        Assert.True(MicrophonePrivacy.GetBlockedMessage() is null or { Length: > 0 });

    public void Dispose()
    {
        fakeLocalMachineRoot.Dispose();
        fakeCurrentUserRoot.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(scratchKeyPath, throwOnMissingSubKey: false);
    }
}
