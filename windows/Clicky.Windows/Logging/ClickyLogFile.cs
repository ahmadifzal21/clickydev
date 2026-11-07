using System.Diagnostics;
using System.IO;
using System.Text;

namespace Clicky.Windows.Logging;

/// <summary>
/// Copies the console log (and Trace warnings) into <c>%LOCALAPPDATA%\Clicky\logs\clicky-yyyyMMdd.log</c>, so a user can send a
/// log from a published exe that has no console. Keeps the newest <see cref="KeptLogFileCount"/> files.
/// </summary>
internal static class ClickyLogFile
{
    internal const int KeptLogFileCount = 7;

    public static string LogsFolderPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clicky", "logs");

    /// <summary>
    /// Tees Console.Out and Trace into today's file. Logging must never stop Clicky starting, so failures are only reported
    /// on the console. shortcut: the file is picked at startup, so a session running past midnight keeps writing to the
    /// day it started; roll over per line if that ever matters.
    /// </summary>
    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(LogsFolderPath);
            string logFilePath = Path.Combine(LogsFolderPath, $"clicky-{DateTime.Now:yyyyMMdd}.log");
            FileStream logFileStream = new(logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            StreamWriter logFileWriter = new(logFileStream, new UTF8Encoding(false)) { AutoFlush = true };
            TextWriter teeWriter = TextWriter.Synchronized(new TimestampedTeeWriter(Console.Out, logFileWriter));
            Console.SetOut(teeWriter);
            Trace.Listeners.Add(new TextWriterTraceListener(teeWriter));
            Trace.AutoFlush = true;

            foreach (string oldLogFilePath in GetLogFilesToDelete(Directory.GetFiles(LogsFolderPath, "clicky-*.log")))
            {
                try { File.Delete(oldLogFilePath); }
                catch (IOException) { } // Open in another Clicky or an editor; try again next start.
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Clicky couldn't open its log file in {LogsFolderPath}: {exception.Message}");
        }
    }

    /// <summary>Everything but the newest <see cref="KeptLogFileCount"/>; yyyyMMdd names sort by date.</summary>
    internal static IEnumerable<string> GetLogFilesToDelete(IEnumerable<string> logFilePaths) =>
        logFilePaths.OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Skip(KeptLogFileCount);

    /// <summary>Writes to the console unchanged and to the file with a time at the start of each line.</summary>
    internal sealed class TimestampedTeeWriter(TextWriter consoleWriter, TextWriter fileWriter, Func<DateTime>? getNow = null) : TextWriter
    {
        private readonly Func<DateTime> getNow = getNow ?? (() => DateTime.Now);
        private bool isAtLineStart = true;

        public override Encoding Encoding => consoleWriter.Encoding;

        public override void Write(char value)
        {
            consoleWriter.Write(value);
            if (isAtLineStart && value != '\r' && value != '\n') fileWriter.Write($"{getNow():HH:mm:ss.fff} ");
            fileWriter.Write(value);
            isAtLineStart = value == '\n';
        }

        public override void Write(string? value)
        {
            foreach (char character in value ?? "") Write(character);
        }

        public override void Flush()
        {
            consoleWriter.Flush();
            fileWriter.Flush();
        }
    }
}
