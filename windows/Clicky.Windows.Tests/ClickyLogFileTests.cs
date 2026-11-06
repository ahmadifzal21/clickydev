using System.IO;
using Clicky.Windows.Logging;

namespace Clicky.Windows.Tests;

public class ClickyLogFileTests
{
    [Fact]
    public void GetLogFilesToDelete_KeepsTheNewestSeven()
    {
        string[] logFiles = Enumerable.Range(1, 9).Select(day => Path.Combine("logs", $"clicky-202611{day:00}.log")).Reverse().ToArray();

        Assert.Equal([Path.Combine("logs", "clicky-20261102.log"), Path.Combine("logs", "clicky-20261101.log")],
            ClickyLogFile.GetLogFilesToDelete(logFiles));
        Assert.Empty(ClickyLogFile.GetLogFilesToDelete(logFiles.Take(7)));
    }

    [Fact]
    public void TeeWriter_CopiesToBoth_TimestampingEachFileLine()
    {
        StringWriter console = new(), file = new();
        ClickyLogFile.TimestampedTeeWriter tee = new(console, file, () => new DateTime(2026, 11, 6, 9, 5, 7, 42));

        tee.WriteLine("first");
        tee.Write("second ");
        tee.WriteLine("line");

        string newLine = Environment.NewLine;
        Assert.Equal($"first{newLine}second line{newLine}", console.ToString());
        Assert.Equal($"09:05:07.042 first{newLine}09:05:07.042 second line{newLine}", file.ToString());
    }
}
