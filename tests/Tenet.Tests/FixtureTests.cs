using Tenet.Export;
using Tenet.Kernel;
using Environment = Tenet.Kernel.Environment;
using Xunit;

namespace Tenet.Tests;

public class FixtureTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void NatAddSuccExportParses()
    {
        ExportFile file = NdjsonReader.ReadFile(Fixture("Nat.add_succ.ndjson"));
        Assert.NotNull(file.Meta);
        Assert.Equal("3.1.0", file.Meta!.FormatVersion);
        Assert.Contains(file.Decls, d => d is ExportInductive i && i.Types[0].Name.Equals(Name.Of("Nat")));
        Assert.Contains(file.Decls, d => d is ExportTheorem t && t.Name.Equals(Name.Of("Nat", "add_succ")));
    }

    [Theory]
    [InlineData("Nat.add_succ.ndjson")]
    [InlineData("Nat.add_succ.v3.0.ndjson")]
    public void NatAddSuccExportChecks(string fixture)
    {
        ExportFile file = NdjsonReader.ReadFile(Fixture(fixture));
        CheckResult result = ExportChecker.Check(file);
        Assert.True(result.Success, string.Join("\n", result.Failures.Select(f => f.Name + ": " + f.Message)));
        Assert.Equal(file.Decls.Count, result.Checked);
        Assert.IsType<RecursorInfo>(result.Environment.Get(Name.Of("Nat", "rec")));
        Assert.IsType<TheoremInfo>(result.Environment.Get(Name.Of("Nat", "add_succ")));
    }

    [Fact]
    public void DerivedRecursorMatchesLeanExactly()
    {
        ExportFile file = NdjsonReader.ReadFile(Fixture("Nat.add_succ.ndjson"));
        var env = new Environment();
        var ind = (ExportInductive)file.Decls.First(d => d is ExportInductive i && i.Types[0].Name.Equals(Name.Of("Nat")));
        env.Add(ExportChecker.ToInductiveDecl(ind));
        var rec = (RecursorInfo)env.Get(Name.Of("Nat", "rec"));
        ExportRecursorVal exported = ind.Recs[0];
        Assert.Equal(exported.Type, rec.Type);
        Assert.Equal(exported.LevelParams, rec.LevelParams);
        Assert.Equal(exported.Rules.Length, rec.Rules.Length);
        for (int i = 0; i < rec.Rules.Length; i++)
        {
            Assert.Equal(exported.Rules[i].Rhs, rec.Rules[i].Rhs);
        }
    }

    [Fact]
    public void ATamperedProofIsRejected()
    {
        ExportFile file = NdjsonReader.ReadFile(Fixture("Nat.add_succ.ndjson"));
        var env = new Environment();
        foreach (ExportDecl d in file.Decls)
        {
            if (d is ExportTheorem t && t.Name.Equals(Name.Of("Nat", "add_succ")))
            {
                // Claim the theorem proves a different statement: swap in the type of some other Prop... here, `True`-free:
                // use the statement `Nat = Nat` which the proof term does not prove.
                Expr eqNat = Expr.MkApp(Expr.Const(Name.Of("Eq"), [Level.Succ(Level.One)]), Expr.Type0, Expr.NatType, Expr.NatType);
                var ex = Assert.Throws<KernelException>(() => env.Add(new TheoremDecl(t.Name, t.LevelParams, eqNat, t.Value, t.All)));
                Assert.Contains("mismatch", ex.Message, StringComparison.Ordinal);
                return;
            }
            ExportChecker.CheckDecl(env, d);
        }
        Assert.Fail("fixture does not contain Nat.add_succ");
    }

    [Fact]
    public void AWrongRecursorRuleIsDetected()
    {
        ExportFile file = NdjsonReader.ReadFile(Fixture("Nat.add_succ.ndjson"));
        var ind = (ExportInductive)file.Decls.First(d => d is ExportInductive i && i.Types[0].Name.Equals(Name.Of("Nat")));
        // Swap the rhs of the two rules: the kernel's derivation must disagree with the export.
        ExportRecursorVal rec = ind.Recs[0];
        var swapped = rec with { Rules = [rec.Rules[0] with { Rhs = rec.Rules[1].Rhs }, rec.Rules[1] with { Rhs = rec.Rules[0].Rhs }] };
        var tampered = ind with { Recs = [swapped] };
        var env = new Environment();
        var ex = Assert.Throws<KernelException>(() => ExportChecker.CheckDecl(env, tampered));
        Assert.Contains("rule 0 rhs", ex.Message, StringComparison.Ordinal);
    }
}
