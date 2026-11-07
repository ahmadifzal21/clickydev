using System.Text.RegularExpressions;
using Clicky.Windows.Companion;

namespace Clicky.Windows.Tests;

/// <summary>E11 T11.6: the prompt's call reference and examples stay in step with what the app actually reads.</summary>
public sealed class CompanionPromptContractTests
{
    private static readonly string Prompt = CompanionSystemPrompts.CompanionVoiceResponseSystemPrompt;

    [Fact]
    public void EveryExampleProgram_ReadsCompletely_WithOnlyCallsTheAppKnows()
    {
        MatchCollection examples = Regex.Matches(Prompt, "```clicky\n(.*?)```", RegexOptions.Singleline);
        Assert.True(examples.Count >= 8, $"only {examples.Count} examples");
        foreach (Match example in examples)
        {
            string[] codeLines = example.Groups[1].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            (IReadOnlyList<ProgramCall> calls, string visibleText) = ReplyProgramReader.ReadAll(example.Value);

            Assert.All(calls, call => Assert.Contains(call.Name, ReplyProgramReader.KnownCallNames));
            Assert.Equal(codeLines.Length, calls.Count + codeLines.Count(line => line.StartsWith("screen(")));
            Assert.NotEmpty(visibleText);
        }
    }

    [Fact]
    public void EveryCallTheAppKnows_IsDocumentedInThePrompt()
    {
        foreach (string callName in ReplyProgramReader.KnownCallNames)
            Assert.Contains($"`{callName}(", Prompt);
    }
}
