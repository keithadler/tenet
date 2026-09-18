using Tenet.Kernel;
using Xunit;

namespace Tenet.Tests;

/// <summary>
/// The correspondence in docs/specification.md is only worth reading if it is complete. A rule added to the kernel
/// without a row is a decision the document silently does not describe, which is worse than no document: a reader
/// auditing against the reference would conclude the kernel does not make that decision.
/// </summary>
public class SpecificationTests
{
    private static string? FindDoc()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string p = Path.Combine(dir.FullName, "docs", "specification.md");
            if (File.Exists(p))
            {
                return p;
            }
            dir = dir.Parent;
        }
        return null;
    }

    [Fact]
    public void EveryRuleHasARowInTheSpecification()
    {
        string? doc = FindDoc();
        Assert.NotNull(doc);
        string text = File.ReadAllText(doc!);

        var missing = Enum.GetValues<Rule>()
            .Where(r => !text.Contains($"| `{r}` |", StringComparison.Ordinal))
            .Select(r => r.ToString())
            .ToList();
        Assert.True(missing.Count == 0,
            $"docs/specification.md has no row for: {string.Join(", ", missing)}");

        // And the other direction: a row for a rule that no longer exists points a reader at nothing.
        var known = new HashSet<string>(Enum.GetValues<Rule>().Select(r => r.ToString()), StringComparer.Ordinal);
        var stale = System.Text.RegularExpressions.Regex.Matches(text, @"^\| `(\w+)` \|",
                System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .Where(n => !known.Contains(n))
            .ToList();
        Assert.True(stale.Count == 0, $"docs/specification.md describes rules that no longer exist: {string.Join(", ", stale)}");
    }
}
