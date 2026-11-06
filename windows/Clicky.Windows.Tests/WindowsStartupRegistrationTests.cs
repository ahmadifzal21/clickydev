using Clicky.Windows.Configuration;

namespace Clicky.Windows.Tests;

/// <summary>Pure command-line helpers only; the registry itself is never touched by tests.</summary>
public class WindowsStartupRegistrationTests
{
    private const string ExecutablePath = @"C:\Program Files\Clicky\Clicky.exe";

    [Fact]
    public void BuildRunCommand_QuotesThePath() =>
        Assert.Equal("\"C:\\Program Files\\Clicky\\Clicky.exe\"", WindowsStartupRegistration.BuildRunCommand(ExecutablePath));

    [Theory]
    [InlineData("\"C:\\Program Files\\Clicky\\Clicky.exe\"", true)]
    [InlineData("  \"c:\\program files\\clicky\\CLICKY.EXE\" --minimized ", true)]
    [InlineData("C:\\Tools\\Clicky.exe", false)]
    [InlineData("\"D:\\old\\Clicky.exe\"", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("\"\"", false)]
    public void IsRunCommandFor_MatchesOnlyThisExe(string? runCommand, bool expected) =>
        Assert.Equal(expected, WindowsStartupRegistration.IsRunCommandFor(runCommand, ExecutablePath));

    [Fact]
    public void IsRunCommandFor_RoundTripsBuildRunCommand() =>
        Assert.True(WindowsStartupRegistration.IsRunCommandFor(WindowsStartupRegistration.BuildRunCommand(ExecutablePath), ExecutablePath));
}
