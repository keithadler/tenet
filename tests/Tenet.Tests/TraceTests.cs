using Tenet.Export;
using Tenet.Kernel;
using Environment = Tenet.Kernel.Environment;
using Xunit;

namespace Tenet.Tests;

/// <summary>
/// Derivation traces (<see cref="KernelTrace"/>), the first milestone of the Lean-to-ZFC translator.
///
/// A trace is only useful if it is the derivation the verdict rests on, so these tests check three things: tracing
/// never changes a verdict, a failed attempt leaves nothing behind, and every trace is closed (each free variable it
/// mentions was introduced by an earlier step, and each step's premises come before it).
/// </summary>
public class TraceTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    public static IEnumerable<object[]> AllExports() =>
        Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "fixtures"), "*.ndjson", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(Path.Combine(AppContext.BaseDirectory, "fixtures"), p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => new object[] { p });

    private static (CheckResult Result, List<(Name Name, KernelTrace Trace)> Traces) CheckTraced(string fixture)
    {
        var traces = new List<(Name, KernelTrace)>();
        CheckResult r = ExportChecker.Check(NdjsonReader.ReadFile(Fixture(fixture)),
            new CheckOptions { TraceDecl = (n, t) => traces.Add((n, t)) });
        return (r, traces);
    }

    [Theory]
    [MemberData(nameof(AllExports))]
    public void TracingDoesNotChangeAVerdict(string fixture)
    {
        CheckResult plain = ExportChecker.Check(NdjsonReader.ReadFile(Fixture(fixture)));
        (CheckResult traced, List<(Name Name, KernelTrace Trace)> traces) = CheckTraced(fixture);

        Assert.Equal(plain.Checked, traced.Checked);
        Assert.Equal(
            plain.Failures.Select(f => f.Name.ToString()).OrderBy(n => n, StringComparer.Ordinal),
            traced.Failures.Select(f => f.Name.ToString()).OrderBy(n => n, StringComparer.Ordinal));
        // One trace per accepted declaration, none for a rejected one.
        Assert.Equal(traced.Checked, traces.Count);
        Assert.DoesNotContain(traces, t => traced.Failures.Any(f => f.Name.Equals(t.Name)));
    }

    [Theory]
    [MemberData(nameof(AllExports))]
    public void EveryTraceIsClosed(string fixture)
    {
        // Some files in the negative corpus hold only the declaration that must be rejected; they have no trace.
        (CheckResult result, List<(Name Name, KernelTrace Trace)> traces) = CheckTraced(fixture);
        Assert.Equal(result.Checked, traces.Count);
        foreach ((Name name, KernelTrace trace) in traces)
        {
            Assert.True(trace.Count > 0, $"{name}: empty trace");
            var declared = new HashSet<FVarId>();
            for (int i = 0; i < trace.Count; i++)
            {
                TraceStep s = trace.Steps[i];
                Assert.True(s.SpanStart >= 0 && s.SpanStart <= i, $"{name} step {i}: premises start at {s.SpanStart}");
                foreach (int p in trace.Premises(i))
                {
                    Assert.True(p < i, $"{name} step {i}: premise {p} does not come first");
                }
                if (s.Kind == TraceKind.Local)
                {
                    AssertDeclared(name, i, s.Rhs!, declared);
                    if (s.Value is not null)
                    {
                        AssertDeclared(name, i, s.Value, declared);
                    }
                    declared.Add(((FVarExpr)s.Lhs).Id);
                    continue;
                }
                AssertDeclared(name, i, s.Lhs, declared);
                if (s.Rhs is not null)
                {
                    AssertDeclared(name, i, s.Rhs, declared);
                }
            }
        }
    }

    private static void AssertDeclared(Name decl, int step, Expr e, HashSet<FVarId> declared)
    {
        foreach (FVarId id in FreeVars(e))
        {
            Assert.True(declared.Contains(id), $"{decl} step {step}: free variable {id.Value} is used before any step introduces it");
        }
    }

    private static IEnumerable<FVarId> FreeVars(Expr e)
    {
        var seen = new HashSet<Expr>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<Expr>();
        stack.Push(e);
        while (stack.Count > 0)
        {
            Expr x = stack.Pop();
            if (!x.HasFVar || !seen.Add(x))
            {
                continue;
            }
            switch (x)
            {
                case FVarExpr f:
                    yield return f.Id;
                    break;
                case AppExpr a:
                    stack.Push(a.Fn);
                    stack.Push(a.Arg);
                    break;
                case BindingExpr b:
                    stack.Push(b.Domain);
                    stack.Push(b.Body);
                    break;
                case LetExpr l:
                    stack.Push(l.Type);
                    stack.Push(l.Value);
                    stack.Push(l.Body);
                    break;
                case ProjExpr p:
                    stack.Push(p.Struct);
                    break;
            }
        }
    }

    private static Environment NatEnv()
    {
        CheckResult r = ExportChecker.Check(NdjsonReader.ReadFile(Fixture("Nat.add_succ.ndjson")));
        Assert.True(r.Success);
        return r.Environment!;
    }

    private static Expr Succ(Expr n) => Expr.App(Expr.Const(Name.Of("Nat", "succ"), []), n);

    [Fact]
    public void AFailedEqualityLeavesNoSteps()
    {
        Environment env = NatEnv();
        var trace = new KernelTrace();
        using (new KernelTrace.Scope(trace))
        {
            var tc = new TypeChecker(env);
            Assert.False(tc.IsDefEq(Expr.NatLit(2), Succ(Succ(Succ(Expr.NatLit(0))))));
        }
        Assert.Equal(0, trace.Count);
    }

    [Fact]
    public void ASuccessfulEqualityEndsInItsConclusion()
    {
        Environment env = NatEnv();
        var trace = new KernelTrace();
        Expr lhs = Expr.NatLit(2);
        Expr rhs = Succ(Succ(Expr.NatLit(0)));
        using (new KernelTrace.Scope(trace))
        {
            var tc = new TypeChecker(env);
            Assert.True(tc.IsDefEq(lhs, rhs));
        }
        Assert.True(trace.Count > 0);
        TraceStep last = trace.Steps[^1];
        Assert.Equal(TraceKind.DefEq, last.Kind);
        Assert.Equal(lhs, last.Lhs);
        Assert.Equal(rhs, last.Rhs);
        Assert.Equal(0, last.SpanStart);   // the whole trace is its derivation
        Assert.Contains(trace.Steps, s => s.Kind == TraceKind.Rule && s.Rule == Rule.DefEqOffset);
    }

    [Fact]
    public void ACheckerOutsideAScopeRecordsNothing()
    {
        Environment env = NatEnv();
        var trace = new KernelTrace();
        var tc = new TypeChecker(env);   // created before the scope: it does not trace
        using (new KernelTrace.Scope(trace))
        {
            Assert.True(tc.IsDefEq(Expr.NatLit(2), Succ(Succ(Expr.NatLit(0)))));
        }
        Assert.Equal(0, trace.Count);
        Assert.Null(KernelTrace.Current);
    }

    [Fact]
    public void ADeclarationIsCheckedNotOnlyInferred()
    {
        // The declaration's own type and value are inferred in checking mode, so the trace says Check for them.
        (_, List<(Name Name, KernelTrace Trace)> traces) = CheckTraced("Nat.add_succ.ndjson");
        (Name _, KernelTrace t) = traces.Single(x => x.Name.Equals(Name.Of("Nat", "add_succ")));
        Assert.Contains(t.Steps, s => s.Kind == TraceKind.Check);
        Assert.Contains(t.Steps, s => s.Kind == TraceKind.Infer);
    }

    [Fact]
    public void TheWriterHashConsesTerms()
    {
        (_, List<(Name Name, KernelTrace Trace)> traces) = CheckTraced("Nat.add_succ.ndjson");
        var sw = new StringWriter();
        var w = new TraceWriter(sw);
        foreach ((Name n, KernelTrace t) in traces)
        {
            w.WriteDecl(n, t);
        }
        string[] lines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("{\"trace\":\"tenet\"", lines[0]);
        int exprLines = lines.Count(l => l.StartsWith("{\"ie\":", StringComparison.Ordinal));
        int stepLines = lines.Count(l => l.StartsWith("{\"s\":", StringComparison.Ordinal));
        Assert.Equal(traces.Sum(t => t.Trace.Count), stepLines);
        // Every expression is written once: no two "ie" lines share a body.
        var bodies = lines.Where(l => l.StartsWith("{\"ie\":", StringComparison.Ordinal))
                          .Select(l => l[(l.IndexOf(',') + 1)..]).ToList();
        Assert.Equal(bodies.Count, bodies.Distinct(StringComparer.Ordinal).Count());
        Assert.True(exprLines > 0);
    }
}
