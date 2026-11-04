using Clicky.Windows.Configuration;
using Clicky.Windows.Tray;

namespace Clicky.Windows.Tests;

public sealed class BackendRoutingPickerRowsTests
{
    [Fact]
    public void Rows_AreAnswerPointingFallback_OnlyFallbackOffersNone()
    {
        Assert.Equal(["Answer", "Pointing", "Fallback"], BackendRoutingPickerRows.All.Select(pickerRow => pickerRow.Title));
        Assert.DoesNotContain(null, BackendRoutingPickerRows.All[0].Options);
        Assert.DoesNotContain(null, BackendRoutingPickerRows.All[1].Options);
        Assert.Contains(null, BackendRoutingPickerRows.All[2].Options);
    }

    [Fact]
    public void EachRow_WritesItsOwnSetting()
    {
        ClickyConfiguration clickyConfiguration = new();

        BackendRoutingPickerRows.All[0].SetSelectedBackend(clickyConfiguration, ChatBackendKind.CloudflareWorker);
        BackendRoutingPickerRows.All[1].SetSelectedBackend(clickyConfiguration, ChatBackendKind.CodexCli);
        BackendRoutingPickerRows.All[2].SetSelectedBackend(clickyConfiguration, null);

        Assert.Equal(ChatBackendKind.CloudflareWorker, clickyConfiguration.JobRouting.Answer);
        Assert.Equal(ChatBackendKind.CodexCli, clickyConfiguration.JobRouting.Pointing);
        Assert.Null(clickyConfiguration.JobRouting.Fallback);
        Assert.Equal(ChatBackendKind.CodexCli, BackendRoutingPickerRows.All[1].GetSelectedBackend(clickyConfiguration));
    }
}
