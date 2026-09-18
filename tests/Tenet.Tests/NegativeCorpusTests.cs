using System.Text.Json;
using Tenet.Export;
using Tenet.Kernel;
using Xunit;

namespace Tenet.Tests;

/// <summary>
/// The corpus that must be rejected, and the reason the acceptance numbers mean anything.
///
/// "Checks all of Mathlib, 0 failures" is a claim about agreement, not about soundness. A kernel whose check
/// returns true reports 0 failures on all of Mathlib as well, in less time. Nothing in a clean run distinguishes
/// the two, so the rejection count belongs next to the acceptance count wherever either is quoted.
///
/// Every file in tests/fixtures/invalid is well formed and is rejected, and the manifest says which defense
/// rejects it. Where a case names a switch that turns its defense off, the same file must be **accepted in full**
/// with that switch set. That half is what stops the corpus going quietly vacuous: a case that is rejected
/// because the file is malformed proves nothing about any defense, passes just as well after the defense has
/// been deleted, and would be counted here as evidence anyway.
/// </summary>
public class NegativeCorpusTests
{
    private sealed record Case(string File, string Why, string Rejects, string? DefenseOff, string Found, string Outcome);

    private static string? FindCorpus()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string p = Path.Combine(dir.FullName, "tests", "fixtures", "invalid");
            if (Directory.Exists(p))
            {
                return p;
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static List<Case> LoadManifest(string dir)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")));
        var cases = new List<Case>();
        foreach (JsonElement c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            cases.Add(new Case(
                c.GetProperty("file").GetString()!,
                c.GetProperty("why").GetString()!,
                c.GetProperty("rejects").GetString()!,
                c.TryGetProperty("defenseOff", out JsonElement d) ? d.GetString() : null,
                c.GetProperty("found").GetString()!,
                c.GetProperty("outcome").GetString()!));
        }
        return cases;
    }

    private static CheckResult Check(string path) =>
        ExportChecker.Check(NdjsonReader.ReadFile(path));

    [Fact]
    public void EveryFileInTheCorpusIsRejected()
    {
        string? dir = FindCorpus();
        Assert.NotNull(dir);
        var cases = LoadManifest(dir!);
        Assert.NotEmpty(cases);

        foreach (Case c in cases)
        {
            string path = Path.Combine(dir!, c.File);
            Assert.True(File.Exists(path), $"{c.File} is in the manifest but not on disk");
            CheckResult r = Check(path);

            // Rejected, and rejected at the declaration the manifest names. A file that fails somewhere else is
            // not testing what it says it tests.
            Assert.False(r.Success, $"{c.File} was accepted; it must be rejected ({c.Why})");
            Assert.Contains(r.Failures, f => f.Name.ToString() == c.Rejects);

            // Rejecting and declining look alike from outside and mean different things. A case marked
            // declined must be refused only because the checker will not vouch for it, and a case marked
            // rejected must not be able to hide behind that.
            bool declined = r.Failures.All(f => f.Unsupported);
            Assert.True(declined == (c.Outcome == "declined"),
                $"{c.File} is marked {c.Outcome} but came back {(declined ? "declined" : "rejected")}");

            // And the file is otherwise sound enough to reach that declaration: it parsed to the end, and
            // nothing before the attack failed. Without this a truncated file would count as a pass.
            Assert.Null(r.ReadError);
            Assert.True(r.Failures.Count == 1,
                $"{c.File} failed {r.Failures.Count} declarations; exactly one, {c.Rejects}, should fail");
        }
    }

    [Fact]
    public void EveryCaseStillLandsWhenItsDefenseIsOff()
    {
        string? dir = FindCorpus();
        Assert.NotNull(dir);

        foreach (Case c in LoadManifest(dir!).Where(x => x.DefenseOff is not null))
        {
            string path = Path.Combine(dir!, c.File);
            bool savedValidate = Primitives.Validate;
            try
            {
                // The switches are read from the environment at startup, so the test sets the flag they set
                // rather than the variable itself.
                Assert.Equal("TENET_NO_PRIMITIVE_CHECK", c.DefenseOff);
                Primitives.Validate = false;
                CheckResult r = Check(path);
                Assert.True(r.Success,
                    $"{c.File} is still rejected with {c.DefenseOff} set, so it is not demonstrating that "
                    + $"defense. It has to be a file the checker would otherwise accept ({c.Why})");
            }
            finally
            {
                Primitives.Validate = savedValidate;
            }
        }
    }

    [Fact]
    public void TheManifestAndTheDirectoryAgree()
    {
        string? dir = FindCorpus();
        Assert.NotNull(dir);
        var listed = LoadManifest(dir!).Select(c => c.File).ToHashSet(StringComparer.Ordinal);
        var present = Directory.EnumerateFiles(dir!, "*.ndjson").Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal!);

        // An unlisted file is a case nobody reviewed and nothing explains; a listed file that is gone is a claim
        // with no evidence behind it. Both make the count printed alongside the Mathlib numbers wrong.
        var unlisted = present.Except(listed).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(unlisted.Count == 0, $"in tests/fixtures/invalid but not in manifest.json: {string.Join(", ", unlisted)}");
        var missing = listed.Except(present!).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, $"in manifest.json but not on disk: {string.Join(", ", missing)}");
    }
}
