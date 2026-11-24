using Tenet.Export;
using Tenet.Kernel;
using Xunit;

namespace Tenet.Tests;

/// <summary>
/// Lean 4.37.0-rc1 changed its kernel's definitional equality (lean4#15103, #15371, #15373): proof irrelevance is
/// asked before the first weak head normalization, a proof standing as a recursor's major premise is not reduced
/// and is eta-expanded when its type is a subsingleton structure, and structure eta applies to a partially applied
/// constructor. The first three accept more; not reducing a proof accepts less. Tenet has to give the answer of the
/// Lean that wrote the file, so both rule sets stay and the export's version picks one. The fixtures in
/// tests/fixtures/eta437 were written by Lean 4.37.0-rc1 and 4.36.0, and the reference kernel of each version
/// gives the verdicts asserted here.
/// </summary>
public class Eta437Tests
{
    [Theory]
    [InlineData("4.37.0", true)]
    [InlineData("v4.37.0", true)]
    [InlineData("4.37.0-rc1", true)]
    [InlineData("4.37.1", true)]
    [InlineData("4.38.0-rc1", true)]
    [InlineData("5.0.0", true)]
    [InlineData("4.38.0-nightly-2026-12-01", true)]
    [InlineData("4.37.0-nightly-2026-10-21", true)]
    [InlineData("4.37.0-nightly-2026-11-02", true)]
    [InlineData("4.37.0-nightly-2026-10-20", false)]
    [InlineData("4.37.0-nightly-2026-10-15", false)]
    [InlineData("4.37.0-nightly", false)]
    [InlineData("4.36.0", false)]
    [InlineData("4.36.0-rc2", false)]
    [InlineData("4.35.0", false)]
    [InlineData("4.12.0", false)]
    [InlineData("n/a", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("garbage", false)]
    public void TheVersionDecidesWhichRulesTheReferenceKernelHas(string? version, bool has) =>
        Assert.Equal(has, LeanVersion.KernelHasEta437(version));

    private static string Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string p = Path.Combine(dir.FullName, "tests", "fixtures", "eta437", name);
            if (File.Exists(p))
            {
                return p;
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException("tests/fixtures/eta437/" + name);
    }

    /// <summary>The fixture as written, or with its meta line claiming another Lean version.</summary>
    private static CheckResult Check(string name, string writtenBy, string? claim, int jobs)
    {
        string text = File.ReadAllText(Fixture(name));
        string marker = $"\"version\":\"{writtenBy}\"}}}}";
        Assert.Contains(marker, text, StringComparison.Ordinal);
        if (claim is not null)
        {
            text = text.Replace(marker, $"\"version\":\"{claim}\"}}}}", StringComparison.Ordinal);
        }
        string tmp = Path.Combine(Path.GetTempPath(), $"tenet-eta437-{Guid.NewGuid():N}.ndjson");
        try
        {
            File.WriteAllText(tmp, text);
            return ExportChecker.Check(NdjsonReader.ReadFile(tmp), new CheckOptions { Jobs = jobs });
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    private static string[] Failed(CheckResult r) => r.Failures.Select(f => f.Name.ToString()).Order(StringComparer.Ordinal).ToArray();

    [Theory]
    [InlineData(null, 1)]
    [InlineData(null, 2)]
    [InlineData("4.37.0", 1)]
    [InlineData("4.38.0", 1)]
    public void WhatLean437WroteIsAcceptedUnderItsRules(string? claim, int jobs)
    {
        CheckResult r = Check("eta437.ndjson", "4.37.0-rc1", claim, jobs);
        Assert.Empty(Failed(r));
        Assert.Equal(14, r.Checked);
    }

    [Theory]
    [InlineData("4.36.0", 1)]
    [InlineData("4.36.0", 2)]
    [InlineData("4.35.0", 1)]
    [InlineData("n/a", 1)]
    public void TheSameFileIsRejectedUnderTheEarlierRulesExactlyWhereItNeedsTheNewOnes(string claim, int jobs)
    {
        CheckResult r = Check("eta437.ndjson", "4.37.0-rc1", claim, jobs);
        Assert.Equal(new[] { "Eta437.andMajor", "Eta437.partialCtor" }, Failed(r));
        Assert.All(r.Failures, f => Assert.False(f.Unsupported, f.Message));
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(null, 2)]
    [InlineData("4.35.0", 1)]
    public void WhatLean436WroteIsAcceptedUnderItsRules(string? claim, int jobs)
    {
        CheckResult r = Check("acc436.ndjson", "4.36.0", claim, jobs);
        Assert.Empty(Failed(r));
        Assert.Equal(12, r.Checked);
    }

    [Theory]
    [InlineData("4.37.0-rc1", 1)]
    [InlineData("4.37.0-rc1", 2)]
    [InlineData("4.37.0", 1)]
    public void AProofStandingAsAMajorPremiseIsNotReducedUnderTheNewRules(string claim, int jobs)
    {
        CheckResult r = Check("acc436.ndjson", "4.36.0", claim, jobs);
        Assert.Equal(new[] { "Acc436.accReduces" }, Failed(r));
        Assert.All(r.Failures, f => Assert.False(f.Unsupported, f.Message));
    }
}
