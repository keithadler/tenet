using Tenet.Export;
using Tenet.Kernel;
using Xunit;

namespace Tenet.Tests;

/// <summary>
/// Lean 4.34 and earlier evaluate <c>Lean.reduceBool</c> by running compiled code; 4.35 removed that from its kernel
/// (<c>reduce_native</c> is gone from type_checker.cpp from 4.35.0-rc1). Tenet never evaluates it, so it must say
/// something different about the same file depending on which Lean wrote it: for 4.34 and earlier and for a Lean it
/// cannot identify, "declined" (the reference might accept it, Tenet will not vouch for it); for 4.35 and later,
/// "rejected" (the reference rejects it too, as an ordinary type mismatch).
/// </summary>
public class NativeReductionVersionTests
{
    [Theory]
    [InlineData("4.35.0", true)]
    [InlineData("v4.35.0", true)]
    [InlineData("4.35.0-rc1", true)]
    [InlineData("4.35.0-rc4", true)]
    [InlineData("4.36.0-rc1", true)]
    [InlineData("5.0.0", true)]
    [InlineData("4.34.1", false)]
    [InlineData("4.34.0", false)]
    [InlineData("4.33.1", false)]
    [InlineData("4.12.0", false)]
    [InlineData("4.36.0-nightly-2026-10-01", false)]
    [InlineData("n/a", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("garbage", false)]
    public void TheVersionDecidesWhetherTheReferenceKernelHasNativeReduction(string? version, bool removed) =>
        Assert.Equal(removed, LeanVersion.KernelHasNoNativeReduction(version));

    private static string FixturePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string p = Path.Combine(dir.FullName, "tests", "fixtures", "invalid", "compiled-code.ndjson");
            if (File.Exists(p))
            {
                return p;
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException("tests/fixtures/invalid/compiled-code.ndjson");
    }

    /// <summary>The compiled-code fixture with its meta line claiming a different Lean version.</summary>
    private static CheckResult CheckAs(string leanVersion)
    {
        string text = File.ReadAllText(FixturePath());
        Assert.Contains("\"version\":\"n/a\"", text, StringComparison.Ordinal);
        string tmp = Path.Combine(Path.GetTempPath(), $"tenet-native-{Guid.NewGuid():N}.ndjson");
        try
        {
            File.WriteAllText(tmp, text.Replace("\"lean\":{\"githash\":\"0000000000000000000000000000000000000000\",\"version\":\"n/a\"}",
                $"\"lean\":{{\"githash\":\"0000000000000000000000000000000000000000\",\"version\":\"{leanVersion}\"}}", StringComparison.Ordinal));
            return ExportChecker.Check(NdjsonReader.ReadFile(tmp));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Theory]
    [InlineData("n/a")]
    [InlineData("4.34.1")]
    [InlineData("4.33.1")]
    public void ForLean434AndEarlierOrAnUnknownLeanTheTermIsDeclined(string version)
    {
        CheckResult r = CheckAs(version);
        CheckFailure f = Assert.Single(r.Failures);
        Assert.Equal("bad", f.Name.ToString());
        Assert.True(f.Unsupported, $"Lean {version}: expected declined, got rejected: {f.Message}");
    }

    [Theory]
    [InlineData("4.35.0")]
    [InlineData("4.35.0-rc4")]
    [InlineData("4.36.0-rc1")]
    public void ForLean435AndLaterTheTermIsRejectedLikeTheReferenceDoes(string version)
    {
        CheckResult r = CheckAs(version);
        CheckFailure f = Assert.Single(r.Failures);
        Assert.Equal("bad", f.Name.ToString());
        Assert.False(f.Unsupported, $"Lean {version}: expected rejected, got declined: {f.Message}");
    }

    [Theory]
    [InlineData("4.34.1", 4)]
    [InlineData("4.35.0", 1)]
    public void TheParallelCheckerAgrees(string version, int expectedExitKind)
    {
        // Same file, parallel path: the environment flag must reach every worker's child environment.
        string text = File.ReadAllText(FixturePath());
        string tmp = Path.Combine(Path.GetTempPath(), $"tenet-native-par-{Guid.NewGuid():N}.ndjson");
        try
        {
            File.WriteAllText(tmp, text.Replace("\"version\":\"n/a\"", $"\"version\":\"{version}\"", StringComparison.Ordinal));
            CheckResult r = ExportChecker.Check(NdjsonReader.ReadFile(tmp), new CheckOptions { Jobs = 2 });
            CheckFailure f = Assert.Single(r.Failures);
            Assert.Equal(expectedExitKind == 4, f.Unsupported);
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
