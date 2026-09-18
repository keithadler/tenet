using System.Text.RegularExpressions;
using Tenet.Kernel;
using Xunit;

namespace Tenet.Tests;

/// <summary>
/// The kernel's trust surface is every name it hardcodes. Each one is a sentence of the form "if the file calls
/// something <c>Nat.add</c>, it is addition", and each such sentence is a thing an export can lie about. Both
/// soundness bugs this project has found in itself were exactly that: a name taken at its word.
///
/// HostileTests attacks those names one file at a time, and says in its own comment to run a grep to see the
/// current list. Nothing checked that anyone did. A name added to the kernel tomorrow is a new assumption with no
/// attack written for it, and the suite would stay green, which is the failure mode that matters: not a test that
/// breaks, but a test that was never written.
///
/// So the list is read from the source and every name has to be accounted for. Adding a hardcoded name to the
/// kernel now fails this test until someone classifies it, and classifying it as an assumption means naming the
/// test that attacks it, which has to exist.
/// </summary>
public class TrustSurfaceTests
{
    /// <summary>Binder and universe-parameter names. They label a bound variable and assert nothing about a file.</summary>
    private static readonly HashSet<string> Cosmetic = new(StringComparer.Ordinal)
    {
        "α", "β", "a", "b", "f", "q", "r", "t", "u", "v", "x", "y", "_", "mk", "motive",
    };

    /// <summary>
    /// Names the kernel invents and never looks up, with the reason a file cannot reach them. These are the ones
    /// worth re-reading when the kernel changes, because "it is only a placeholder" stops being true quietly.
    /// </summary>
    private static readonly Dictionary<string, string> Internal = new(StringComparer.Ordinal)
    {
        ["dontcare"] = "a placeholder pushed onto the substitution only where the body has no loose bound "
                     + "variable, so nothing is ever instantiated with it and no declaration of that name is consulted",
    };

    /// <summary>
    /// Every name the kernel reads from the file and believes something about, mapped to the test that writes a
    /// file abusing it. The method has to exist on HostileTests; a rename that orphans an entry fails below.
    /// </summary>
    private static readonly Dictionary<string, string> Attacked = new(StringComparer.Ordinal)
    {
        ["Nat"] = "ANumeralIsNotAProofOfWhateverIsCalledNat",
        ["Nat.zero"] = "ANumeralIsNotAProofOfWhateverIsCalledNat",
        ["Nat.succ"] = "ANumeralIsNotAProofOfWhateverIsCalledNat",

        ["Nat.add"] = "AnOperationIsNotWhateverIsNamedAfterIt",
        ["Nat.sub"] = "AnOperationIsNotWhateverIsNamedAfterIt",
        ["Nat.mul"] = "AnOperationIsNotWhateverIsNamedAfterIt",
        ["Nat.div"] = "AnOperationIsNotWhateverIsNamedAfterIt",
        ["Nat.mod"] = "AnOperationIsNotWhateverIsNamedAfterIt",
        ["Nat.gcd"] = "AnOperationIsNotWhateverIsNamedAfterIt",
        ["Nat.pow"] = "AnOperationIsNotWhateverIsNamedAfterIt",
        ["Nat.pred"] = "AnOperationIsNotWhateverIsNamedAfterIt",
        ["Nat.land"] = "AnOperationIsNotWhateverIsNamedAfterIt",
        ["Nat.lor"] = "AnOperationIsNotWhateverIsNamedAfterIt",
        ["Nat.xor"] = "AnOperationIsNotWhateverIsNamedAfterIt",
        ["Nat.shiftLeft"] = "AnOperationIsNotWhateverIsNamedAfterIt",
        ["Nat.shiftRight"] = "AnOperationIsNotWhateverIsNamedAfterIt",

        ["Nat.beq"] = "AComparisonCannotReturnSomethingItsBodyDoesNot",
        ["Nat.ble"] = "AComparisonCannotReturnSomethingItsBodyDoesNot",
        ["Bool"] = "AComparisonCannotReturnSomethingItsBodyDoesNot",
        ["Bool.true"] = "AComparisonCannotReturnSomethingItsBodyDoesNot",
        ["Bool.false"] = "AComparisonCannotReturnSomethingItsBodyDoesNot",

        ["String"] = "AStringExpandsThroughConstantsThatHaveToBeWhatTheyAreNamed",
        ["String.mk"] = "AStringExpandsThroughConstantsThatHaveToBeWhatTheyAreNamed",
        ["String.ofList"] = "AStringExpandsThroughConstantsThatHaveToBeWhatTheyAreNamed",
        ["Char"] = "AStringExpandsThroughConstantsThatHaveToBeWhatTheyAreNamed",
        ["Char.ofNat"] = "AStringExpandsThroughConstantsThatHaveToBeWhatTheyAreNamed",
        ["List"] = "AStringExpandsThroughConstantsThatHaveToBeWhatTheyAreNamed",
        ["List.nil"] = "AStringExpandsThroughConstantsThatHaveToBeWhatTheyAreNamed",
        ["List.cons"] = "AStringExpandsThroughConstantsThatHaveToBeWhatTheyAreNamed",

        ["Quot"] = "QuotientReductionNeedsTheRealQuotientBlock",
        ["Quot.mk"] = "QuotientReductionNeedsTheRealQuotientBlock",
        ["Quot.lift"] = "QuotientReductionNeedsTheRealQuotientBlock",
        ["Quot.ind"] = "QuotientReductionNeedsTheRealQuotientBlock",
        ["Eq"] = "QuotientReductionNeedsTheRealQuotientBlock",

        ["_nested"] = "TheNamespaceTheKernelDerivesIntoIsNotAvailable",

        ["outParam"] = "AnAnnotationThatIsNotTheIdentityDoesNotSlipThrough",
        ["semiOutParam"] = "AnAnnotationThatIsNotTheIdentityDoesNotSlipThrough",
        ["optParam"] = "AnAnnotationThatIsNotTheIdentityDoesNotSlipThrough",
        ["autoParam"] = "AnAnnotationThatIsNotTheIdentityDoesNotSlipThrough",

        ["eagerReduce"] = "AnAnnotationThatOnlyChangesEffortCannotChangeAnAnswer",

        ["Lean.reduceBool"] = "CompiledCodeIsRefusedRatherThanBelieved",
        ["Lean.reduceNat"] = "CompiledCodeIsRefusedRatherThanBelieved",
    };

    private static string? FindKernelSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string p = Path.Combine(dir.FullName, "src", "Tenet.Kernel");
            if (Directory.Exists(p))
            {
                return p;
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>Every <c>Name.Of("a", "b")</c> in the kernel, as a dotted name.</summary>
    private static HashSet<string> HardcodedNames(string dir)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"Name\.Of\(\s*((?:""(?:[^""\\]|\\.)*""\s*,?\s*)+)\)"))
            {
                var parts = Regex.Matches(m.Groups[1].Value, @"""((?:[^""\\]|\\.)*)""")
                    .Select(x => x.Groups[1].Value);
                found.Add(string.Join(".", parts));
            }
        }
        return found;
    }

    [Fact]
    public void EveryNameTheKernelHardcodesIsAccountedFor()
    {
        string? dir = FindKernelSource();
        Assert.NotNull(dir);
        var names = HardcodedNames(dir!);

        // Sanity: if the pattern stops matching, everything below passes for the wrong reason.
        Assert.True(names.Count > 40, $"only {names.Count} hardcoded names found; the source scan is broken");
        Assert.Contains("Nat.succ", names);

        var unclassified = names
            .Where(n => !Cosmetic.Contains(n) && !Internal.ContainsKey(n) && !Attacked.ContainsKey(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        Assert.True(unclassified.Count == 0,
            "the kernel hardcodes names that nothing here accounts for. Each is something an export can lie "
            + "about. Add a case to HostileTests and map it in Attacked, or classify it as Cosmetic or "
            + $"Internal with the reason: {string.Join(", ", unclassified)}");
    }

    [Fact]
    public void EveryClassifiedNameIsStillInTheKernel()
    {
        string? dir = FindKernelSource();
        Assert.NotNull(dir);
        var names = HardcodedNames(dir!);

        // A stale entry is worse than a missing one: it reports an assumption as defended after the assumption,
        // or the defense, has gone.
        var stale = Cosmetic.Concat(Internal.Keys).Concat(Attacked.Keys)
            .Where(n => !names.Contains(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        Assert.True(stale.Count == 0,
            $"classified here but no longer hardcoded by the kernel: {string.Join(", ", stale)}");
    }

    [Fact]
    public void EveryAttackNamedHereExists()
    {
        // The mapping is only worth anything if the tests it names are real. Renaming a test without updating the
        // table would otherwise leave a name looking defended by a method that is gone.
        var methods = typeof(HostileTests).GetMethods().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var missing = Attacked.Values.Distinct()
            .Where(t => !methods.Contains(t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
        Assert.True(missing.Count == 0,
            $"named as the attack for a trusted name, but no such test on HostileTests: {string.Join(", ", missing)}");
    }
}
