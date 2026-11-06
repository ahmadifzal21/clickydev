using System.Diagnostics;
using Clicky.Windows.Configuration;

namespace Clicky.Windows.Tray;

/// <summary>
/// One backend picker (Answer, Pointing, or Fallback). Shared by the panel's segmented pickers and
/// the tray menu's submenus, so both always offer the same choices and write the same setting.
/// A null option means "None" (only offered for Fallback, where null disables fallback).
/// </summary>
internal sealed record BackendRoutingPickerRow(
    string Title,
    IReadOnlyList<ChatBackendKind?> Options,
    Func<ClickyConfiguration, ChatBackendKind?> GetSelectedBackend,
    Action<ClickyConfiguration, ChatBackendKind?> SetSelectedBackend);

internal static class BackendRoutingPickerRows
{
    private static readonly ChatBackendKind?[] AllBackends =
        [ChatBackendKind.ClaudeCodeCli, ChatBackendKind.CodexCli, ChatBackendKind.CloudflareWorker];

    public static IReadOnlyList<BackendRoutingPickerRow> All { get; } =
    [
        CreateJobRow("Answer", CompanionJobKind.Answer),
        CreateJobRow("Pointing", CompanionJobKind.Pointing),
        new BackendRoutingPickerRow(
            "Fallback",
            [.. AllBackends, null],
            configuration => configuration.JobRouting.Fallback,
            (configuration, selectedBackend) => configuration.JobRouting.Fallback = selectedBackend)
    ];

    /// <summary>Short label that fits a segmented picker; the tray menu uses it too.</summary>
    public static string GetOptionDisplayName(ChatBackendKind? chatBackendKind)
    {
        return chatBackendKind switch
        {
            ChatBackendKind.ClaudeCodeCli => "Claude",
            ChatBackendKind.CodexCli => "Codex",
            ChatBackendKind.CloudflareWorker => "Worker",
            null => "None",
            _ => throw new ArgumentOutOfRangeException(nameof(chatBackendKind), chatBackendKind, "Unknown backend")
        };
    }

    /// <summary>Opens clicky.config.json in the user's .json editor, or Notepad if none is associated.</summary>
    public static void OpenConfigurationFileInEditor(string configurationFilePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo(configurationFilePath) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{configurationFilePath}\"") { UseShellExecute = true });
        }
    }

    private static BackendRoutingPickerRow CreateJobRow(string title, CompanionJobKind companionJobKind)
    {
        return new BackendRoutingPickerRow(
            title,
            AllBackends,
            configuration => configuration.JobRouting.GetBackendForJob(companionJobKind),
            // Job rows never offer "None", so the value is always non-null here.
            (configuration, selectedBackend) => configuration.JobRouting.SetBackendForJob(companionJobKind, selectedBackend!.Value));
    }
}
