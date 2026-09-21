using Tenet.Kernel;
using Tenet.Olean;
using Xunit;

namespace Tenet.Tests;

/// <summary>
/// A name means what the module under check can see, not whichever module happened to be mapped first.
///
/// Two modules may declare the same name when neither imports the other, which is ordinary in any project
/// holding more than one executable: verso has eight top-level <c>Config</c> structures. Resolving by name
/// alone loses all but the first, and the kernel then compares a term against a type from an unrelated module
/// and reports a type mismatch that does not exist. Tenet 0.11.0 rejected 12 declarations in this fixture and
/// 33 in verso, every one of them sound and every one just compiled by Lean.
///
/// That is the worst way for a checker to be wrong. A slow checker wastes time; one that rejects correct
/// proofs tells someone their work is broken when it is not, and it does so with the full authority of a
/// second opinion.
/// </summary>
public class OleanScopeTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "olean", "collide", name);

    private static OleanChecker Load()
    {
        string dir = Path.GetDirectoryName(Fixture("A.olean"))!;
        var search = new LeanSearchPath();
        search.Add(dir);
        var checker = new OleanChecker(search);
        checker.Load(new[]
        {
            (Name.Parse("A"), Path.Combine(dir, "A.olean")),
            (Name.Parse("B"), Path.Combine(dir, "B.olean")),
        });
        return checker;
    }

    [Fact]
    public void BothModulesCheckCleanlyTogether()
    {
        using OleanChecker checker = Load();
        OleanCheckResult result = checker.Check(
            new[] { Name.Parse("A"), Name.Parse("B") },
            new OleanCheckOptions { CheckImports = false, Jobs = 1 });

        Assert.Empty(result.Failures.Select(f => $"{f.Module}.{f.Name}: {f.Message}"));
        Assert.True(result.Checked > 0, "nothing was checked, so the test proves nothing");
    }

    [Fact]
    public void EachModuleSeesItsOwnDeclaration()
    {
        using OleanChecker checker = Load();

        // Resolution is scoped by the module being checked, so asking outside any scope is not the question
        // this test asks: it checks each module and requires that the other module's Config never intrudes.
        OleanCheckResult a = checker.Check(new[] { Name.Parse("A") },
            new OleanCheckOptions { CheckImports = false, Jobs = 1 });
        OleanCheckResult b = checker.Check(new[] { Name.Parse("B") },
            new OleanCheckOptions { CheckImports = false, Jobs = 1 });

        Assert.Empty(a.Failures);
        Assert.Empty(b.Failures);
    }

    [Fact]
    public void TheFixtureReallyDoesCollide()
    {
        // If the two modules ever stop sharing a name this test passes for the wrong reason, so the collision
        // itself is asserted rather than assumed.
        using var a = new OleanModule(Fixture("A.olean"));
        using var b = new OleanModule(Fixture("B.olean"));
        var shared = new HashSet<Name>(a.ConstantNames);
        shared.IntersectWith(b.ConstantNames);
        Assert.Contains(Name.Parse("Config"), shared);
        Assert.Contains(Name.Parse("Config.mk"), shared);
    }
}
