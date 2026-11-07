using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace Clicky.Windows.Companion;

/// <summary>
/// The things a reply can do for the user besides talking and drawing (E11 T11.25): copy text, open a web page or a Windows
/// settings page, and show a result card. The model's arguments are data from a reply: <see cref="IsAllowedLink"/> only lets
/// http, https and ms-settings links through, so a reply can never start a program or open a file.
/// </summary>
internal static class CompanionActions
{
    /// <summary>Puts <paramref name="text"/> on the clipboard (UI thread); the clipboard can be briefly locked by another app.</summary>
    public static bool Copy(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                Trace.WriteLine($"Copied {text.Length} characters to the clipboard.");
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(40);
            }
        }
        Trace.TraceWarning("Couldn't copy to the clipboard: another app kept it locked.");
        return false;
    }

    public static bool IsAllowedLink(string link, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out Uri? parsed)) return false;
        if (parsed.Scheme is not ("http" or "https" or "ms-settings")) return false;
        uri = parsed;
        return true;
    }

    /// <summary>Opens an allowed link in its default handler (the browser, or the Settings app); anything else is refused.</summary>
    public static bool Open(string link)
    {
        if (!IsAllowedLink(link, out Uri? uri))
        {
            Trace.TraceWarning($"Refused to open \"{link}\": only http, https and ms-settings links are allowed.");
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo(uri!.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            Trace.WriteLine($"Opened {uri.AbsoluteUri}.");
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Trace.TraceWarning($"Couldn't open {uri!.AbsoluteUri}: {exception.Message}");
            return false;
        }
    }
}
