using Tenet.Export;
using Tenet.Kernel;
using Xunit;
using Environment = Tenet.Kernel.Environment;

namespace Tenet.Tests;

/// <summary>Declaration kinds and safety rules that real exports rarely contain.</summary>
public class SafetyTests
{
    private static Environment LoadFixture()
    {
        ExportFile file = NdjsonReader.ReadFile(Path.Combine(AppContext.BaseDirectory, "fixtures", "Nat.add_succ.ndjson"));
        return ExportChecker.Check(file).Environment;
    }

    private static readonly Expr NatE = Expr.Const(Name.Of("Nat"), []);
    private static Expr NatToNat => Expr.Arrow(NatE, NatE);

    [Fact]
    public void UnsafeMutualDefinitionsMayRecurse()
    {
        Environment env = LoadFixture();
        // unsafe def f : Nat → Nat := fun n => g n ; unsafe def g : Nat → Nat := fun n => f n
        var f = new DefinitionDecl(Name.Of("f"), [], NatToNat, Expr.Lam(Name.Of("n"), NatE, Expr.App(Expr.Const(Name.Of("g"), []), Expr.BVar(0))),
                                   ReducibilityHints.Opaque, DefinitionSafety.Unsafe, [Name.Of("f"), Name.Of("g")]);
        var g = new DefinitionDecl(Name.Of("g"), [], NatToNat, Expr.Lam(Name.Of("n"), NatE, Expr.App(Expr.Const(Name.Of("f"), []), Expr.BVar(0))),
                                   ReducibilityHints.Opaque, DefinitionSafety.Unsafe, [Name.Of("f"), Name.Of("g")]);
        env.Add(new MutualDefinitionDecl([f, g]));
        Assert.True(env.Get(Name.Of("f")).IsUnsafe);
        Assert.True(env.Get(Name.Of("g")).IsUnsafe);
    }

    [Fact]
    public void SafeMutualBlockIsRejected()
    {
        Environment env = LoadFixture();
        var f = new DefinitionDecl(Name.Of("f"), [], NatToNat, Expr.Lam(Name.Of("n"), NatE, Expr.BVar(0)), ReducibilityHints.Regular(1), DefinitionSafety.Safe);
        var ex = Assert.Throws<KernelException>(() => env.Add(new MutualDefinitionDecl([f])));
        Assert.Contains("unsafe/partial", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SafeCodeMayNotUseUnsafeCode()
    {
        Environment env = LoadFixture();
        env.Add(new AxiomDecl(Name.Of("bad"), [], NatE, isUnsafe: true));
        var safe = new DefinitionDecl(Name.Of("uses"), [], NatE, Expr.Const(Name.Of("bad"), []), ReducibilityHints.Regular(1), DefinitionSafety.Safe);
        var ex = Assert.Throws<KernelException>(() => env.Add(safe));
        Assert.Contains("unsafe", ex.Message, StringComparison.Ordinal);
        // an unsafe definition may
        var unsafeDef = new DefinitionDecl(Name.Of("uses"), [], NatE, Expr.Const(Name.Of("bad"), []), ReducibilityHints.Regular(1), DefinitionSafety.Unsafe);
        env.Add(unsafeDef);
        Assert.NotNull(env.Find(Name.Of("uses")));
    }

    [Fact]
    public void PartialDefinitionsMayNotDependOnEachOther()
    {
        Environment env = LoadFixture();
        var p1 = new DefinitionDecl(Name.Of("p1"), [], NatE, Expr.NatLit(0), ReducibilityHints.Opaque, DefinitionSafety.Partial);
        env.Add(p1);
        var p2 = new DefinitionDecl(Name.Of("p2"), [], NatE, Expr.Const(Name.Of("p1"), []), ReducibilityHints.Opaque, DefinitionSafety.Partial);
        var ex = Assert.Throws<KernelException>(() => env.Add(p2));
        Assert.Contains("partial", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpaqueValuesAreCheckedButNeverUnfolded()
    {
        Environment env = LoadFixture();
        env.Add(new OpaqueDecl(Name.Of("secret"), [], NatE, Expr.NatLit(42), isUnsafe: false));
        var tc = new TypeChecker(env);
        Assert.False(tc.IsDefEq(Expr.Const(Name.Of("secret"), []), Expr.NatLit(42)));
        Assert.Equal(Expr.Const(Name.Of("secret"), []), tc.Whnf(Expr.Const(Name.Of("secret"), [])));
        // a wrong opaque value is still rejected
        Assert.Throws<KernelException>(() => env.Add(new OpaqueDecl(Name.Of("wrong"), [], NatE, Expr.Prop, isUnsafe: false)));
    }

    [Fact]
    public void NativeReductionIsRefused()
    {
        Environment env = LoadFixture();
        env.Add(new AxiomDecl(Name.Of("Lean", "reduceBool"), [], Expr.Arrow(NatE, NatE), isUnsafe: false));
        env.Add(new DefinitionDecl(Name.Of("c"), [], NatE, Expr.NatLit(1), ReducibilityHints.Regular(1), DefinitionSafety.Safe));
        var tc = new TypeChecker(env);
        Expr app = Expr.App(Expr.Const(Name.Of("Lean", "reduceBool"), []), Expr.Const(Name.Of("c"), []));
        var ex = Assert.Throws<UnsupportedException>(() => tc.Whnf(app));
        Assert.Contains("compiled code", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyFilterChecksJustTheNamedDeclarations()
    {
        ExportFile file = NdjsonReader.ReadFile(Path.Combine(AppContext.BaseDirectory, "fixtures", "Nat.add_succ.ndjson"));
        var options = new CheckOptions { Only = [Name.Of("Nat", "add_succ")] };
        CheckResult r = ExportChecker.Check(file, options);
        Assert.True(r.Success);
        Assert.Equal(1, r.Checked);
        Assert.Equal(file.Decls.Count - 1, r.Skipped);
        CheckResult rp = ExportChecker.Check(file, new CheckOptions { Only = [Name.Of("Nat", "add_succ")], Jobs = 4 });
        Assert.Equal(1, rp.Checked);
    }

    /// <summary>
    /// A term deeper than the stack is rejected rather than taken as a reason to abort the process. Reduction can
    /// grow a term without bound on ill-typed input, and a .NET stack overflow cannot be caught: it would kill
    /// every other declaration being checked and leave no report. This runs on a deliberately small stack so the
    /// limit is reached in a fraction of a second; on a real run the stack is 512 MB.
    /// </summary>
    [Fact]
    public void ATermDeeperThanTheStackIsRejectedRatherThanCrashing()
    {
        // f (f (f ... (f Nat))), deep enough that no plausible stack holds it.
        Expr deep = NatE;
        for (int i = 0; i < 2_000_000; i++)
        {
            deep = Expr.App(NatE, deep);
        }

        Exception? caught = null;
        var t = new Thread(() =>
        {
            try
            {
                ExprOps.ForEach(deep, (_, _) => true);
                caught = new InvalidOperationException("traversal returned without hitting the limit");
            }
            catch (Exception e)
            {
                caught = e;
            }
        }, 1 * 1024 * 1024);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromMinutes(2)), "the traversal neither finished nor hit the limit");
        Assert.IsType<RecursionLimitException>(caught);
        Assert.Contains("too deep", caught!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// FirstDifference answers what one decoder produced against what another did, so it must not call two terms
    /// equal when the kernel's own equality calls them different. It used to do exactly that for a let's nonDep
    /// flag, and to answer "(equal)" for binder-only differences before the comparison naming them could run,
    /// which left crosscheck unable to report either class of reader defect.
    /// </summary>
    [Fact]
    public void FirstDifferenceSeesWhatKernelEqualityIgnoresAndWhatItDoesNot()
    {
        Expr letDep = Expr.Let(Name.Of("x"), NatE, Expr.NatLit(1), Expr.BVar(0), nonDep: false);
        Expr letNon = Expr.Let(Name.Of("x"), NatE, Expr.NatLit(1), Expr.BVar(0), nonDep: true);
        Assert.False(letDep.Equals(letNon));   // the kernel's equality distinguishes the flag
        Assert.Contains("nonDep", ExprOps.FirstDifference(letDep, letNon), StringComparison.Ordinal);

        Expr piA = Expr.Pi(Name.Of("a"), NatE, NatE);
        Expr piB = Expr.Pi(Name.Of("b"), NatE, NatE);
        Assert.True(piA.Equals(piB));          // the kernel ignores binder names, and must keep ignoring them
        Assert.False(Expr.EqStrict(piA, piB));
        Assert.Contains("binder", ExprOps.FirstDifference(piA, piB), StringComparison.Ordinal);

        Expr impl = Expr.Pi(Name.Of("a"), NatE, NatE, BinderInfo.Implicit);
        Assert.True(piA.Equals(impl));
        Assert.Contains("binder", ExprOps.FirstDifference(piA, impl), StringComparison.Ordinal);

        // And nothing is invented where nothing differs.
        Assert.Equal("(equal)", ExprOps.FirstDifference(piA, Expr.Pi(Name.Of("a"), NatE, NatE)));
    }

    /// <summary>
    /// The rule counters have to count, or the coverage table they produce is worse than no table: it would report
    /// a rule as unexercised when it fired, or as exercised when it did not.
    /// </summary>
    [Fact]
    public void RuleCountersCountWhatTheyName()
    {
        bool saved = TypeChecker.Stats.Enabled;
        try
        {
            Rules.Reset();
            TypeChecker.Stats.Enabled = false;
            Environment env = LoadFixture();
            var off = new TypeChecker(env);
            off.Whnf(Expr.App(Expr.Lam(Name.Of("x"), NatE, Expr.BVar(0)), Expr.NatLit(1)));
            Assert.Equal(0, Rules.Count(Rule.Beta));   // off by default: an atomic increment here would serialize workers

            TypeChecker.Stats.Enabled = true;
            Rules.Reset();
            var on = new TypeChecker(env);
            // (fun x => x) 1 reduces by beta, and nothing else in this term does.
            Assert.Equal(Expr.NatLit(1), on.Whnf(Expr.App(Expr.Lam(Name.Of("x"), NatE, Expr.BVar(0)), Expr.NatLit(1))));
            Assert.True(Rules.Count(Rule.Beta) > 0);
            Assert.Equal(0, Rules.Count(Rule.Iota));
            Assert.Equal(0, Rules.Count(Rule.QuotLift));

            // let x := 1; x reduces by zeta.
            Rules.Reset();
            var zeta = new TypeChecker(env);
            zeta.Whnf(Expr.Let(Name.Of("x"), NatE, Expr.NatLit(1), Expr.BVar(0)));
            Assert.True(Rules.Count(Rule.Zeta) > 0);
            Assert.Equal(0, Rules.Count(Rule.Beta));

            Assert.Contains(Rule.NativeReduce, Rules.Unexercised());
        }
        finally
        {
            TypeChecker.Stats.Enabled = saved;
            Rules.Reset();
        }
    }

    /// <summary>
    /// The one rule no corpus reaches, exercised directly.
    ///
    /// `DefEqStringLit` is at zero on `Init` for Lean 4.12, 4.24 and 4.34 alike, and on the edge-case corpus, and
    /// this test says why rather than covering it: `String` is a structure, so eta for structures decides a
    /// literal against its constructor form before the rule named for that case is reached. Lean's kernel places
    /// its own `try_string_lit_expansion` in the same position, after eta, so the same shadowing applies there.
    /// The rule is kept because the reference has it; it is not reachable while `String` is a structure, which is
    /// every real environment and, now that a literal's type is checked, every one this kernel accepts.
    /// </summary>
    [Fact]
    public void StringLiteralAgainstItsConstructorFormIsItsOwnRule()
    {
        bool saved = TypeChecker.Stats.Enabled;
        try
        {
            // A minimal environment shaped like the older Lean, where String.mk is the constructor the kernel
            // keys on. Opaque constants are enough: the rule compares the literal's expansion against the term.
            var env = new Environment();
            Expr type0 = Expr.Sort(Level.One);
            void Ax(Name n, Expr ty, Name[]? lps = null) =>
                env.Add(new AxiomDecl(n, lps ?? [], ty, false));
            Ax(Name.Of("Char"), type0);
            Expr charE = Expr.Const(Name.Of("Char"), []);
            Ax(Name.Of("Nat"), type0);
            Ax(Name.Of("Char", "ofNat"), Expr.Arrow(Expr.Const(Name.Of("Nat"), []), charE));
            var u = Name.Of("u");
            // List.{u} : Type u -> Type u, which is Sort (u+1) -> Sort (u+1); at u := 0 that is Type -> Type.
            Expr typeU = Expr.Sort(Level.Succ(Level.Param(u)));
            Ax(Name.Of("List"), Expr.Arrow(typeU, typeU), [u]);
            Expr listChar = Expr.App(Expr.Const(Name.Of("List"), [Level.Zero]), charE);
            Expr listOf(int deBruijn) =>
                Expr.App(Expr.Const(Name.Of("List"), [Level.Param(u)]), Expr.BVar(deBruijn));
            Ax(Name.Of("List", "nil"), Expr.Pi(Name.Of("α"), typeU, listOf(0)), [u]);
            Ax(Name.Of("List", "cons"), Expr.Pi(Name.Of("α"), typeU,
                Expr.Arrow(Expr.BVar(0), Expr.Arrow(listOf(1), listOf(2)))), [u]);
            // String has to be the inductive a string literal denotes, not merely a constant of that name:
            // the literal's type is asserted by its representation, so the environment has to agree.
            Expr strE = Expr.Const(Name.Of("String"), []);
            env.Add(new InductiveDecl([], 0,
                [new InductiveType(Name.Of("String"), type0,
                    [new Constructor(Name.Of("String", "mk"), Expr.Arrow(listChar, strE))])], false));

            Assert.Equal(Name.Of("String", "mk"), env.StringLiteralConstructor);   // no String.ofList here

            Expr lit = Expr.StrLit("ab");
            Expr ctorForm = Inductive.StringLitToConstructor(env, lit);

            TypeChecker.Stats.Enabled = true;
            Rules.Reset();
            var tc = new TypeChecker(env);
            Assert.True(tc.IsDefEq(lit, ctorForm));

            // Not by the rule named for this case. String is a structure, so eta for structures gets there first:
            // it expands the literal into String.mk of its own field, reducing that projection expands the literal
            // to its character list, and the two sides meet without TryStringLitExpansion being consulted. That
            // holds in every environment where String is a structure, which is every real one and, since the
            // literal's type is now checked, every one this kernel will accept.
            Assert.Equal(0, Rules.Count(Rule.DefEqStringLit));
            Assert.True(Rules.Count(Rule.DefEqEtaStruct) > 0);
            Assert.True(Rules.Count(Rule.StringLitToCtor) > 0);

            // What matters is the verdict, and it holds in both directions.
            Rules.Reset();
            Assert.True(new TypeChecker(env).IsDefEq(ctorForm, lit));

            // A different literal must not be accepted.
            var tc3 = new TypeChecker(env);
            Assert.False(tc3.IsDefEq(Expr.StrLit("ac"), ctorForm));
        }
        finally
        {
            TypeChecker.Stats.Enabled = saved;
            Rules.Reset();
        }
    }

    /// <summary>
    /// The kernel computes <c>Nat.add 2 2</c> by adding two BigIntegers rather than unfolding the declaration, and
    /// it used to decide to do that from the name alone. An export is free to declare <c>Nat.add</c> as anything of
    /// the right type, so a file declaring it as <c>fun a b =&gt; a</c> made the checker answer the opposite of the
    /// truth in both directions: it accepted <c>add 2 2 = 4</c>, which is false of the declaration in front of it,
    /// and rejected <c>add 2 2 = 2</c>, which is true of it.
    ///
    /// Lean's kernel has the same shortcut and the same absence of a check, and gets away with it by shipping its
    /// prelude and not supporting another one. Tenet cannot: reading a file somebody else produced is the whole job.
    /// </summary>
    [Fact]
    public void AnAcceleratedPrimitiveIsCheckedBeforeItIsTrusted()
    {
        Expr type0 = Expr.Sort(Level.One);
        Expr natE = Expr.Const(Name.Of("Nat"), []);

        Environment Fake()
        {
            var env = new Environment();
            var nat = new InductiveType(Name.Of("Nat"), type0,
                [new Constructor(Name.Of("Nat", "zero"), natE),
                 new Constructor(Name.Of("Nat", "succ"), Expr.Arrow(natE, natE))]);
            env.Add(new InductiveDecl([], 0, [nat], false));
            // Right name, right type, and a body that is not addition: it returns its first argument.
            env.Add(new DefinitionDecl(Name.Of("Nat", "add"), [],
                Expr.Arrow(natE, Expr.Arrow(natE, natE)),
                Expr.Lam(Name.Of("a"), natE, Expr.Lam(Name.Of("b"), natE, Expr.BVar(1))),
                ReducibilityHints.Regular(1), DefinitionSafety.Safe));
            return env;
        }

        Expr add22 = Expr.MkApp(Expr.Const(Name.Of("Nat", "add"), []), Expr.NatLit(2), Expr.NatLit(2));
        Environment fake = Fake();

        Assert.False(fake.PrimitiveOk(Primitive.NatAdd));
        // The declaration's own body says 2, so that is the only answer the kernel may give.
        Assert.False(new TypeChecker(fake).IsDefEq(add22, Expr.NatLit(4)));
        Assert.True(new TypeChecker(fake).IsDefEq(add22, Expr.NatLit(2)));

        // And the check is what stops it: with validation off, the shortcut fires on the name and inverts both.
        bool saved = Primitives.Validate;
        try
        {
            Primitives.Validate = false;
            Environment unchecked_ = Fake();
            Assert.True(new TypeChecker(unchecked_).IsDefEq(add22, Expr.NatLit(4)));
            Assert.False(new TypeChecker(unchecked_).IsDefEq(add22, Expr.NatLit(2)));
        }
        finally
        {
            Primitives.Validate = saved;
        }
    }

    /// <summary>
    /// Every accelerated primitive has to validate against the Lean actually in front of us, or the kernel stops
    /// taking the shortcut and the check crawls. Stating an equation the way a newer Lean happens to compile it is
    /// the failure mode: `ble 0 y` with y free does not reduce under the four-clause definition every Lean before
    /// 4.34 uses, which turned a 4 second run into a timeout on three of the olean-compat versions.
    ///
    /// This runs only where a toolchain is present, so it is a local and CI guard rather than a hermetic one.
    /// </summary>
    [Fact]
    public void EveryPrimitiveValidatesAgainstAnInstalledToolchain()
    {
        string home = System.Environment.GetEnvironmentVariable("HOME") ?? "";
        string root = Path.Combine(home, ".elan", "toolchains");
        if (!Directory.Exists(root))
        {
            return;
        }
        var libs = Directory.EnumerateDirectories(root)
            .Select(d => Path.Combine(d, "lib", "lean", "Init.olean"))
            .Where(File.Exists).OrderBy(p => p, StringComparer.Ordinal).ToList();
        if (libs.Count == 0)
        {
            return;
        }

        foreach (string init in libs)
        {
            var search = new Tenet.Olean.LeanSearchPath();
            search.AddFromEnvironment();
            search.AddAroundOleanFile(init);
            using var checker = new Tenet.Olean.OleanChecker(search);
            Name m = search.ModuleNameOf(init);
            checker.Load([(m, init)]);
            search.AddToolchainFor(checker.Modules[m].LeanVersion);
            checker.Load([(m, init)]);

            var env = new Environment();
            var seen = new HashSet<Name>();
            void Add(Name n)
            {
                if (!seen.Add(n) || checker.Resolve(n) is not ConstantInfo ci)
                {
                    return;
                }
                foreach (Name u in Replay.UsedConstants(ci))
                {
                    Add(u);
                }
                if (env.Find(n) is null)
                {
                    env.AddCore(ci);
                }
            }
            foreach (Primitive p in Enum.GetValues<Primitive>())
            {
                if (p is Primitive.NatLiteralType or Primitive.StringLiteralType)
                {
                    continue;   // these name a type, not a Nat operation
                }
                Add(Name.Parse("Nat." + char.ToLowerInvariant(p.ToString()[3]) + p.ToString()[4..]));
            }
            foreach (string n in new[] { "Nat", "Nat.zero", "Nat.succ", "Bool", "Bool.true", "Bool.false",
                                         "String", "String.mk", "String.ofList", "Char", "Char.ofNat",
                                         "List", "List.nil", "List.cons" })
            {
                Add(Name.Parse(n));
            }

            var failing = Enum.GetValues<Primitive>().Where(p => !env.PrimitiveOk(p)).ToList();
            Assert.True(failing.Count == 0,
                $"{Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(init))))}: "
                + $"{string.Join(", ", failing)} did not validate, so the kernel will unfold them instead");
        }
    }

    /// <summary>
    /// A numeric literal's type is asserted by its representation, not derived, so the environment has to agree
    /// that the constant it names is the type the literal means. Without that check a file could declare
    /// <c>Nat</c> as a proposition and hand a literal over as a proof of it. Declaring that proposition to be
    /// <c>False</c> gave a proof of False with no <c>sorry</c> and no axioms, which <c>tenet audit</c> reported as
    /// unconditional.
    /// </summary>
    [Fact]
    public void ALiteralIsNotAProofOfWhateverTheFileCallsNat()
    {
        var env = new Environment();
        env.Add(new InductiveDecl([], 0, [new InductiveType(Name.Of("False"), Expr.Prop, [])], false));
        // A constant named Nat that is the proposition False.
        env.Add(new DefinitionDecl(Name.Of("Nat"), [], Expr.Prop, Expr.Const(Name.Of("False"), []),
            ReducibilityHints.Regular(1), DefinitionSafety.Safe));

        Assert.False(env.PrimitiveOk(Primitive.NatLiteralType));
        var ex = Assert.Throws<KernelException>(() =>
            env.Add(new DefinitionDecl(Name.Of("boom"), [], Expr.Const(Name.Of("False"), []),
                Expr.NatLit(3), ReducibilityHints.Opaque, DefinitionSafety.Safe)));
        Assert.Contains("Nat", ex.Message, StringComparison.Ordinal);
        Assert.Null(env.Find(Name.Of("boom")));

        // And the check is what stops it: without it the literal is accepted at that type and the file proves False.
        bool saved = Primitives.Validate;
        try
        {
            Primitives.Validate = false;
            var env2 = new Environment();
            env2.Add(new InductiveDecl([], 0, [new InductiveType(Name.Of("False"), Expr.Prop, [])], false));
            env2.Add(new DefinitionDecl(Name.Of("Nat"), [], Expr.Prop, Expr.Const(Name.Of("False"), []),
                ReducibilityHints.Regular(1), DefinitionSafety.Safe));
            env2.Add(new DefinitionDecl(Name.Of("boom"), [], Expr.Const(Name.Of("False"), []),
                Expr.NatLit(3), ReducibilityHints.Opaque, DefinitionSafety.Safe));
            Assert.NotNull(env2.Find(Name.Of("boom")));
            Assert.Empty(Replay.AxiomsOf(env2.Find, Name.Of("boom")).Axioms);
        }
        finally
        {
            Primitives.Validate = saved;
        }
    }

    /// <summary>Build f (f (f ... x)) nested in the argument position, which is where each level costs a frame.</summary>
    private static Expr DeepTerm(int depth)
    {
        Expr e = NatE;
        for (int i = 0; i < depth; i++)
        {
            e = Expr.App(NatE, e);
        }
        return e;
    }

    /// <summary>Run <paramref name="body"/> on a deliberately small stack and return what it threw, if anything.</summary>
    private static Exception? OnASmallStack(Action body)
    {
        Exception? caught = null;
        var t = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                caught = e;
            }
        }, 1 * 1024 * 1024);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromMinutes(2)), "the work neither finished nor hit the limit");
        return caught;
    }

    /// <summary>
    /// Structural equality walks two terms at once, and only the child off the spine costs a frame. Two deep terms
    /// that are equal recurse all the way down, so this is a path a deep term reaches without ever being reduced.
    /// </summary>
    [Fact]
    public void ComparingTwoTermsDeeperThanTheStackIsRejected()
    {
        // Built twice, so nothing is reference-equal and the comparison really descends.
        Expr a = DeepTerm(2_000_000), b = DeepTerm(2_000_000);
        Exception? e = OnASmallStack(() => a.Equals(b));
        Assert.IsType<RecursionLimitException>(e);
    }

    /// <summary>
    /// The printer truncates rather than throwing. It is what writes the message for some other error, and
    /// replacing a type mismatch with "expression too deep" would throw away the error being reported.
    /// </summary>
    [Fact]
    public void PrintingATermDeeperThanTheStackTruncates()
    {
        Expr deep = DeepTerm(2_000_000);
        string? printed = null;
        Exception? e = OnASmallStack(() => printed = ExprPrinter.Print(deep));
        Assert.Null(e);
        Assert.Contains("\u2026", printed!, StringComparison.Ordinal);
    }

    /// <summary>A universe level nested deeper than the stack is rejected the same way a term is.</summary>
    [Fact]
    public void NormalizingALevelDeeperThanTheStackIsRejected()
    {
        // Distinct parameters, because MkMax folds max 0 u, max u u and max u (max u v) on the way in; if any of
        // those fired the level would stay shallow and this test would pass without testing anything.
        const int Levels = 200_000;
        Level deep = Level.Param(Name.Of("u0"));
        for (int i = 1; i < Levels; i++)
        {
            deep = Level.MkMax(Level.Param(Name.Of("u" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))), deep);
        }
        Assert.Equal(Levels - 1, deep.Depth);
        Exception? e = OnASmallStack(() => deep.Normalize());
        Assert.IsType<RecursionLimitException>(e);
    }

    [Fact]
    public void NonTerminatingUnsafeDefinitionHitsTheUnfoldLimit()
    {
        Environment env = LoadFixture();
        // unsafe def loop : Nat := loop
        env.Add(new DefinitionDecl(Name.Of("loop"), [], NatE, Expr.Const(Name.Of("loop"), []), ReducibilityHints.Regular(1), DefinitionSafety.Unsafe));
        long saved = TypeChecker.MaxUnfolds;
        TypeChecker.MaxUnfolds = 10_000;
        try
        {
            var tc = new TypeChecker(env, safety: DefinitionSafety.Unsafe);
            var ex = Assert.Throws<DeterministicTimeoutException>(() => tc.Whnf(Expr.Const(Name.Of("loop"), [])));
            Assert.Contains("deterministic timeout", ex.Message, StringComparison.Ordinal);
            var tc2 = new TypeChecker(env, safety: DefinitionSafety.Unsafe);
            Assert.Throws<DeterministicTimeoutException>(() => tc2.IsDefEq(Expr.Const(Name.Of("loop"), []), Expr.NatLit(3)));
            // A declaration that hits the limit is rejected with this exception type, which Environment.Add
            // excludes from the faithful re-check (re-running without the failure cache would only repeat the work).
            // unsafe def : Eq Nat loop 3 := Eq.refl Nat 3   forces loop =?= 3, which unfolds forever
            Expr eq = Expr.MkApp(Expr.Const(Name.Of("Eq"), [Level.One]), NatE, Expr.Const(Name.Of("loop"), []), Expr.NatLit(3));
            Expr refl = Expr.MkApp(Expr.Const(Name.Of("Eq", "refl"), [Level.One]), NatE, Expr.NatLit(3));
            Assert.Throws<DeterministicTimeoutException>(() => env.Add(new DefinitionDecl(Name.Of("loop2"), [], eq, refl, ReducibilityHints.Regular(1), DefinitionSafety.Unsafe)));
            Assert.Null(env.Find(Name.Of("loop2")));
        }
        finally
        {
            TypeChecker.MaxUnfolds = saved;
        }
    }

    [Fact]
    public void NegativeIndicesAreKernelErrors()
    {
        Assert.Throws<KernelException>(() => Expr.BVar(-1));
        Assert.Throws<KernelException>(() => Expr.Proj(Name.Of("Prod"), -1, Expr.Prop));
    }
}
