using System.ComponentModel;
using System.Reflection;
using Laplace.Cli.Spectre;
using Xunit;

namespace Laplace.Cli.Tests;

public sealed class CliHelpMarkupSafetyTests
{
    [Fact]
    public void CommandDescriptionsContainNoUnescapedSpectreMarkup()
    {
        var offenders = typeof(IngestCommand).Assembly
            .GetTypes()
            .Select(type => (Type: type, Description: type.GetCustomAttribute<DescriptionAttribute>()?.Description))
            .Where(entry => entry.Description is not null && ContainsRawBracket(entry.Description))
            .Select(entry => $"{entry.Type.FullName}: {entry.Description}")
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Command descriptions contain unescaped Spectre markup delimiters:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    private static bool ContainsRawBracket(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '[')
            {
                if (i + 1 < text.Length && text[i + 1] == '[')
                {
                    i++;
                    continue;
                }

                return true;
            }

            if (text[i] == ']')
            {
                if (i + 1 < text.Length && text[i + 1] == ']')
                {
                    i++;
                    continue;
                }

                return true;
            }
        }

        return false;
    }
}
