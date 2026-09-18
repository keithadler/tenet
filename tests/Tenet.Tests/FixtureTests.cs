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

    [Theory]
    [InlineData("Nat.add_succ.ndjson")]
    [InlineData("Nat.add_succ.v3.0.ndjson")]
    public void ParallelModeAgreesWithSequential(string fixture)
    {
        ExportFile file = NdjsonReader.ReadFile(Fixture(fixture));
        CheckResult seq = ExportChecker.Check(file, new CheckOptions { Jobs = 1 });
        CheckResult par = ExportChecker.Check(file, new CheckOptions { Jobs = 4 });
        Assert.True(par.Success, string.Join("\n", par.Failures.Select(f => f.Name + ": " + f.Message)));
        Assert.Equal(seq.Checked, par.Checked);
        Assert.Equal(seq.Environment.Count, par.Environment.Count);
    }

    [Theory]
    [InlineData("Nat.add_succ.ndjson", 1)]
    [InlineData("Nat.add_succ.ndjson", 4)]
    [InlineData("Nat.add_succ.v3.0.ndjson", 4)]
    public void StreamingModeChecks(string fixture, int jobs)
    {
        using FileStream fs = File.OpenRead(Fixture(fixture));
        CheckResult result = ExportChecker.CheckStreaming(fs, new CheckOptions { Jobs = jobs });
        Assert.True(result.Success, string.Join("\n", result.Failures.Select(f => f.Name + ": " + f.Message)));
        Assert.NotNull(result.Stream);
        Assert.Equal(result.Stream!.Declarations, result.Checked);
        Assert.IsType<TheoremInfo>(result.Environment.Get(Name.Of("Nat", "add_succ")));
    }

    [Fact]
    public void StreamingModeRejectsTamperedProof()
    {
        // Rewrite the fixture so that Nat.add_succ claims Nat = Nat, then stream it.
        string[] lines = File.ReadAllLines(Fixture("Nat.add_succ.ndjson"));
        ExportFile file = NdjsonReader.ReadFile(Fixture("Nat.add_succ.ndjson"));
        var thm = (ExportTheorem)file.Decls.First(d => d is ExportTheorem);
        int typeIdx = file.Exprs.IndexOf(thm.Type);
        // find the expression index of `Nat` (the constant) to reuse as a bogus "type"
        int natIdx = file.Exprs.FindIndex(e => e.IsConstOf(Name.Of("Nat")));
        Assert.True(typeIdx >= 0 && natIdx >= 0);
        int thmLine = Array.FindIndex(lines, l => l.Contains("\"thm\"", StringComparison.Ordinal));
        lines[thmLine] = lines[thmLine].Replace($"\"type\":{typeIdx}", $"\"type\":{natIdx}", StringComparison.Ordinal);
        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines)));
        CheckResult result = ExportChecker.CheckStreaming(ms, new CheckOptions { Jobs = 4 });
        Assert.Contains(result.Failures, f => f.Name.Equals(Name.Of("Nat", "add_succ")));
    }

    [Fact]
    public void ParallelModeRejectsForwardReferences()
    {
        ExportFile file = NdjsonReader.ReadFile(Fixture("Nat.add_succ.ndjson"));
        // Move the theorem to the front: it now refers to constants declared after it.
        var reordered = new ExportFile();
        ExportDecl thm = file.Decls.First(d => d is ExportTheorem);
        reordered.Decls.Add(thm);
        reordered.Decls.AddRange(file.Decls.Where(d => !ReferenceEquals(d, thm)));
        CheckResult par = ExportChecker.Check(reordered, new CheckOptions { Jobs = 4 });
        Assert.Contains(par.Failures, f => f.Name.Equals(thm.DisplayName) && f.Message.Contains("declared later", StringComparison.Ordinal));
        CheckResult seq = ExportChecker.Check(reordered, new CheckOptions { Jobs = 1 });
        Assert.Contains(seq.Failures, f => f.Name.Equals(thm.DisplayName));
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

public class ReaderRobustnessTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void TruncatedFileIsAFormatError()
    {
        byte[] bytes = File.ReadAllBytes(Fixture("Nat.add_succ.ndjson"));
        using var ms = new MemoryStream(bytes, 0, bytes.Length - 40);
        var ex = Assert.Throws<ExportFormatException>(() => NdjsonReader.Read(ms));
        Assert.True(ex.Line > 0);
    }

    [Fact]
    public void StreamingChecksEverythingBeforeATruncation()
    {
        byte[] bytes = File.ReadAllBytes(Fixture("Nat.add_succ.ndjson"));
        // cut inside the last line (the theorem), keeping every earlier declaration intact
        using var ms = new MemoryStream(bytes, 0, bytes.Length - 20);
        CheckResult r = ExportChecker.CheckStreaming(ms, new CheckOptions { Jobs = 2 });
        Assert.NotNull(r.ReadError);
        Assert.False(r.Success);
        Assert.Empty(r.Failures);
        Assert.True(r.Checked > 0);
        Assert.NotNull(r.Environment.Find(Name.Of("Nat", "rec")));
    }

    private const string Header = "{\"meta\":{\"exporter\":{\"name\":\"x\",\"version\":\"3.1.0\"},\"lean\":{\"githash\":\"\",\"version\":\"\"},\"format\":{\"version\":\"3.1.0\"}}}\n";

    private static ExportFile ReadText(string body)
    {
        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Header + body));
        return NdjsonReader.Read(ms);
    }

    /// <summary>
    /// The format numbers names, levels and expressions; it does not require the numbers to be dense or to
    /// arrive in order. Every real exporter emits them densely and in order, which is how requiring that went
    /// unnoticed, and this test used to assert the requirement as though it were the rule. The arena's
    /// `sparse-name-index` and `level-index-out-of-order` are exports a checker must accept, and Tenet rejected
    /// both.
    /// </summary>
    [Fact]
    public void SparseAndOutOfOrderIndicesAreLegal()
    {
        // A gap before the entry, which no earlier reader position could have predicted.
        ExportFile sparse = ReadText("{\"in\":4,\"str\":{\"pre\":0,\"str\":\"Nat\"}}\n");
        Assert.Equal(Name.Of("Nat"), sparse.Names[4]);

        // A level defined after the one that refers to it, which is legal because the reference resolves by
        // the time it is used.
        ExportFile ooo = ReadText("{\"il\":2,\"succ\":0}\n{\"il\":1,\"succ\":2}\n");
        Assert.NotNull(ooo.Levels[1]);
        Assert.NotNull(ooo.Levels[2]);
    }

    /// <summary>
    /// Allowing gaps must not turn a reference to a gap into something the reader hands on. Before the second
    /// half of this was in place, an axiom naming an index nobody had defined reached the checker with a null
    /// name and took the process down with an ArgumentNullException rather than an export format error.
    /// </summary>
    [Fact]
    public void AReferenceToAnIndexNobodyDefinedIsAFormatError()
    {
        var ex = Assert.Throws<ExportFormatException>(() => ReadText(
            "{\"in\":5,\"str\":{\"pre\":0,\"str\":\"foo\"}}\n"
            + "{\"ie\":0,\"sort\":0}\n"
            + "{\"axiom\":{\"isUnsafe\":false,\"levelParams\":[],\"name\":3,\"type\":0}}\n"));
        Assert.Contains("undefined name 3", ex.Message, StringComparison.Ordinal);

        var ex2 = Assert.Throws<ExportFormatException>(() => ReadText(
            "{\"in\":1,\"str\":{\"pre\":0,\"str\":\"foo\"}}\n"
            + "{\"ie\":7,\"sort\":0}\n"
            + "{\"axiom\":{\"isUnsafe\":false,\"levelParams\":[],\"name\":1,\"type\":2}}\n"));
        Assert.Contains("undefined expression 2", ex2.Message, StringComparison.Ordinal);
    }

    /// <summary>Filling a gap is fine; defining the same index twice is a file that contradicts itself.</summary>
    [Fact]
    public void AnIndexDefinedTwiceIsAFormatError()
    {
        var ex = Assert.Throws<ExportFormatException>(() => ReadText(
            "{\"in\":1,\"str\":{\"pre\":0,\"str\":\"foo\"}}\n"
            + "{\"in\":1,\"str\":{\"pre\":0,\"str\":\"bar\"}}\n"));
        Assert.Contains("already defined", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedFormatVersionIsRejected()
    {
        string text = "{\"meta\":{\"exporter\":{\"name\":\"x\",\"version\":\"9.0.0\"},\"lean\":{\"githash\":\"\",\"version\":\"\"},\"format\":{\"version\":\"9.0.0\"}}}\n";
        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
        var ex = Assert.Throws<ExportFormatException>(() => NdjsonReader.Read(ms));
        Assert.Contains("unsupported export format", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ForwardReferenceInsideTablesIsAFormatError()
    {
        string text = "{\"meta\":{\"exporter\":{\"name\":\"x\",\"version\":\"3.1.0\"},\"lean\":{\"githash\":\"\",\"version\":\"\"},\"format\":{\"version\":\"3.1.0\"}}}\n"
                    + "{\"ie\":0,\"app\":{\"fn\":5,\"arg\":6}}\n";
        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
        var ex = Assert.Throws<ExportFormatException>(() => NdjsonReader.Read(ms));
        Assert.Contains("undefined expression", ex.Message, StringComparison.Ordinal);
    }
}
