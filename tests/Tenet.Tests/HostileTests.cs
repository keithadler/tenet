using Tenet.Kernel;
using Xunit;
using Environment = Tenet.Kernel.Environment;

namespace Tenet.Tests;

/// <summary>
/// Exports written to attack the checker, rather than valid exports damaged at random.
///
/// The mutation harness takes a real export and breaks it. That finds places where two implementations of one
/// specification drift apart, which is what it is for, and it found both of the real kernel bugs this project has
/// caught. It cannot find a file written on purpose to exploit what the checker assumes, because a mutation of a
/// valid file is not that. Both soundness bugs found in Tenet itself came from files of the second kind, and
/// neither was reachable by mutation.
///
/// The cases here are organized around the attack surface rather than around the bugs: every name the kernel
/// hardcodes is something it takes on trust, and each one deserves a file that abuses it. Run
/// <c>grep -ohE 'Name\.Of\("[^)]*"\)' src/Tenet.Kernel/*.cs</c> to see the current list. Binder names are
/// cosmetic; the rest are assumptions.
///
/// Every case asserts two things: that the attack is refused, and that it succeeds when the defense is switched
/// off. Without the second half a test can pass because the attack was built wrong.
/// </summary>
public class HostileTests
{
    private static readonly Expr Type0 = Expr.Sort(Level.One);

    private static void Ax(Environment env, Name n, Expr ty, Name[]? lps = null) =>
        env.Add(new AxiomDecl(n, lps ?? [], ty, false));

    /// <summary>A genuine two-constructor Nat, so a case can attack something other than the literal's type.</summary>
    private static void AddRealNat(Environment env)
    {
        Expr nat = Expr.Const(Name.Of("Nat"), []);
        env.Add(new InductiveDecl([], 0, [new InductiveType(Name.Of("Nat"), Type0,
            [new Constructor(Name.Of("Nat", "zero"), nat),
             new Constructor(Name.Of("Nat", "succ"), Expr.Arrow(nat, nat))])], false));
    }

    /// <summary>Run an attack with the primitive defenses down, to show the defense is what refuses it.</summary>
    private static T WithoutDefenses<T>(Func<T> f)
    {
        bool saved = Primitives.Validate;
        try
        {
            Primitives.Validate = false;
            return f();
        }
        finally
        {
            Primitives.Validate = saved;
        }
    }

    // ---------------------------------------------------------------- the type a literal denotes

    /// <summary>
    /// `Nat` names whatever the file says it names, and a numeric literal's type is asserted rather than derived.
    /// Point the name at a proposition and the literal becomes a proof of it; point it at `False` and the file has
    /// proved False with no axioms and no `sorry`.
    /// </summary>
    [Fact]
    public void ANumeralIsNotAProofOfWhateverIsCalledNat()
    {
        Environment Build()
        {
            var env = new Environment();
            env.Add(new InductiveDecl([], 0, [new InductiveType(Name.Of("False"), Expr.Prop, [])], false));
            env.Add(new DefinitionDecl(Name.Of("Nat"), [], Expr.Prop, Expr.Const(Name.Of("False"), []),
                ReducibilityHints.Regular(1), DefinitionSafety.Safe));
            return env;
        }
        DefinitionDecl Boom() => new(Name.Of("boom"), [], Expr.Const(Name.Of("False"), []),
            Expr.NatLit(3), ReducibilityHints.Opaque, DefinitionSafety.Safe);

        Environment guarded = Build();
        Assert.Throws<KernelException>(() => guarded.Add(Boom()));
        Assert.Null(guarded.Find(Name.Of("boom")));

        Environment open = WithoutDefenses(() => { var e = Build(); e.Add(Boom()); return e; });
        Assert.NotNull(open.Find(Name.Of("boom")));
        Assert.Empty(Replay.AxiomsOf(open.Find, Name.Of("boom")).Axioms);
    }

    // ---------------------------------------------------------------- the operations the kernel computes itself

    /// <summary>
    /// `Nat.add 2 2` is computed rather than unfolded, on the strength of the name. Declare the name at the right
    /// type with a body that is not addition and the checker answers the opposite of the truth in both directions.
    /// </summary>
    [Fact]
    public void AnOperationIsNotWhateverIsNamedAfterIt()
    {
        Environment Build()
        {
            var env = new Environment();
            AddRealNat(env);
            Expr nat = Expr.Const(Name.Of("Nat"), []);
            env.Add(new DefinitionDecl(Name.Of("Nat", "add"), [], Expr.Arrow(nat, Expr.Arrow(nat, nat)),
                Expr.Lam(Name.Of("a"), nat, Expr.Lam(Name.Of("b"), nat, Expr.BVar(1))),
                ReducibilityHints.Regular(1), DefinitionSafety.Safe));
            return env;
        }
        Expr add22 = Expr.MkApp(Expr.Const(Name.Of("Nat", "add"), []), Expr.NatLit(2), Expr.NatLit(2));

        Environment guarded = Build();
        Assert.False(guarded.PrimitiveOk(Primitive.NatAdd));
        Assert.False(new TypeChecker(guarded).IsDefEq(add22, Expr.NatLit(4)));
        Assert.True(new TypeChecker(guarded).IsDefEq(add22, Expr.NatLit(2)));   // what the file actually says

        WithoutDefenses(() =>
        {
            Environment open = Build();
            Assert.True(new TypeChecker(open).IsDefEq(add22, Expr.NatLit(4)));
            Assert.False(new TypeChecker(open).IsDefEq(add22, Expr.NatLit(2)));
            return 0;
        });
    }

    // ---------------------------------------------------------------- what a string literal expands through

    /// <summary>
    /// A string literal expands to its constructor applied to a list of characters, built from `Char.ofNat`,
    /// `List.cons` and `List.nil`. Those are taken by name too, and a file is free to declare them at other types,
    /// which makes the expansion the kernel produces ill-typed.
    /// </summary>
    [Fact]
    public void AStringExpandsThroughConstantsThatHaveToBeWhatTheyAreNamed()
    {
        Environment Build(bool honest)
        {
            var env = new Environment();
            AddRealNat(env);
            Expr nat = Expr.Const(Name.Of("Nat"), []);
            Ax(env, Name.Of("Char"), Type0);
            Expr ch = Expr.Const(Name.Of("Char"), []);
            // Dishonest: Char.ofNat takes a Char, so the expansion applies it to a numeral and is ill-typed.
            Ax(env, Name.Of("Char", "ofNat"), Expr.Arrow(honest ? nat : ch, ch));
            var u = Name.Of("u");
            Expr typeU = Expr.Sort(Level.Succ(Level.Param(u)));
            Ax(env, Name.Of("List"), Expr.Arrow(typeU, typeU), [u]);
            Expr ListOf(int d) => Expr.App(Expr.Const(Name.Of("List"), [Level.Param(u)]), Expr.BVar(d));
            Ax(env, Name.Of("List", "nil"), Expr.Pi(Name.Of("a"), typeU, ListOf(0)), [u]);
            Ax(env, Name.Of("List", "cons"), Expr.Pi(Name.Of("a"), typeU,
                Expr.Arrow(Expr.BVar(0), Expr.Arrow(ListOf(1), ListOf(2)))), [u]);
            Expr listChar = Expr.App(Expr.Const(Name.Of("List"), [Level.Zero]), ch);
            Expr str = Expr.Const(Name.Of("String"), []);
            env.Add(new InductiveDecl([], 0, [new InductiveType(Name.Of("String"), Type0,
                [new Constructor(Name.Of("String", "mk"), Expr.Arrow(listChar, str))])], false));
            return env;
        }
        DefinitionDecl S() => new(Name.Of("s"), [], Expr.Const(Name.Of("String"), []),
            Expr.StrLit("ab"), ReducibilityHints.Opaque, DefinitionSafety.Safe);

        Environment honestEnv = Build(honest: true);
        Assert.True(honestEnv.PrimitiveOk(Primitive.StringLiteralType));
        honestEnv.Add(S());                                    // an honest file still works

        Environment hostile = Build(honest: false);
        Assert.False(hostile.PrimitiveOk(Primitive.StringLiteralType));
        Assert.Throws<KernelException>(() => hostile.Add(S()));
    }

    // ---------------------------------------------------------------- quotients, which are built rather than read

    /// <summary>
    /// Quotient reduction fires on `Quot.lift` and `Quot.ind` by name, the same shape as the two bugs above. It is
    /// safe for a different reason: the kernel constructs the quotient constants with the types it requires rather
    /// than accepting a file's, and will not reduce at all until it has. A file declaring its own `Quot` gets no
    /// reduction, so this is a test that the existing defense holds rather than a bug that was fixed.
    /// </summary>
    [Fact]
    public void QuotientReductionNeedsTheRealQuotientBlock()
    {
        var env = new Environment();
        Ax(env, Name.Of("A"), Type0);
        Expr a = Expr.Const(Name.Of("A"), []);
        Ax(env, Name.Of("B"), Type0);
        Ax(env, Name.Of("rel"), Expr.Arrow(a, Expr.Arrow(a, Expr.Prop)));
        Ax(env, Name.Of("x"), a);
        Ax(env, Name.Of("g"), Expr.Arrow(a, Expr.Const(Name.Of("B"), [])));
        Ax(env, Name.Of("h"), Expr.Prop);
        Ax(env, Name.Of("Quot"), Expr.Arrow(Expr.Arrow(a, Expr.Arrow(a, Expr.Prop)), Type0));
        Expr quotR = Expr.App(Expr.Const(Name.Of("Quot"), []), Expr.Const(Name.Of("rel"), []));
        Ax(env, Name.Of("Quot", "mk"), Expr.Arrow(a, quotR));
        Ax(env, Name.Of("Quot", "lift"),
            Expr.Arrow(Expr.Arrow(a, Expr.Const(Name.Of("B"), [])),
                Expr.Arrow(Expr.Prop, Expr.Arrow(quotR, Expr.Const(Name.Of("B"), [])))));

        Assert.False(env.QuotInitialized);
        Expr mk = Expr.App(Expr.Const(Name.Of("Quot", "mk"), []), Expr.Const(Name.Of("x"), []));
        Expr lift = Expr.MkApp(Expr.Const(Name.Of("Quot", "lift"), []),
            Expr.Const(Name.Of("g"), []), Expr.Const(Name.Of("h"), []), mk);
        Assert.Equal(lift, new TypeChecker(env).Whnf(lift));   // no iota on a quotient the kernel did not build
    }

    // ---------------------------------------------------------------- names the kernel derives into

    /// <summary>
    /// Eliminating a nested inductive generates auxiliary types under the `_nested` prefix. Lean reserves that
    /// whole namespace against ordinary declarations; Tenet used to reject only a declaration whose *type*
    /// mentioned it, so a file could sit on a name the kernel was about to derive. What that buys an attacker
    /// depends on how the elimination resolves the name it finds, which is exactly the question worth not having:
    /// the namespace is reserved now, and the kernel's own auxiliaries are installed by a path that does not go
    /// through this check, so an honest file loses nothing.
    /// </summary>
    [Fact]
    public void TheNamespaceTheKernelDerivesIntoIsNotAvailable()
    {
        var env = new Environment();
        Ax(env, Name.Of("T"), Type0);
        Expr t = Expr.Const(Name.Of("T"), []);

        foreach (Name n in new[] { Name.Of("_nested"), Name.Of("_nested", "Foo"), Name.Of("_nested", "Foo", "mk") })
        {
            var ex = Assert.Throws<KernelException>(() => env.Add(new AxiomDecl(n, [], t, false)));
            Assert.Contains("reserved", ex.Message, StringComparison.Ordinal);
            Assert.Null(env.Find(n));
        }

        // Including an inductive block, where every name it introduces is vetted, not just the block's own.
        Expr ind = Expr.Const(Name.Of("_nested", "Ind"), []);
        Assert.Throws<KernelException>(() => env.Add(new InductiveDecl([], 0,
            [new InductiveType(Name.Of("_nested", "Ind"), Type0,
                [new Constructor(Name.Of("_nested", "Ind", "mk"), ind)])], false)));

        // A name that merely starts with the same letters is not in the namespace and stays legal.
        env.Add(new AxiomDecl(Name.Of("_nestedish"), [], t, false));
        Assert.NotNull(env.Find(Name.Of("_nestedish")));
    }

    // ---------------------------------------------------------------- binder annotations stripped by name

    /// <summary>
    /// `optParam`, `autoParam`, `outParam` and `semiOutParam` wrap a type and mean nothing to the kernel, so
    /// `ExprOps.ConsumeTypeAnnotations` strips them while declaring an inductive. It strips by name, without
    /// checking that the constant is the identity-in-its-first-argument that Lean declares. A file can therefore
    /// declare `optParam A d` to mean something other than `A` and have the kernel type a parameter at `A`.
    ///
    /// It does not get away with it, and not because the annotation is validated: the stripped parameter type is
    /// compared against the declared one when the constructors are checked, and the mismatch surfaces there. This
    /// pins that, so the defense cannot be removed as a simplification without the case failing.
    /// </summary>
    [Fact]
    public void AnAnnotationThatIsNotTheIdentityDoesNotSlipThrough()
    {
        Environment Build(bool honest)
        {
            var env = new Environment();
            Ax(env, Name.Of("A"), Type0);
            Ax(env, Name.Of("B"), Type0);
            Expr a = Expr.Const(Name.Of("A"), []);
            Ax(env, Name.Of("dflt"), a);
            // (α : Type) → α → Type, honest body `fun α d => α`, hostile body `fun α d => B`.
            env.Add(new DefinitionDecl(Name.Of("optParam"), [],
                Expr.Pi(Name.Of("α"), Type0, Expr.Arrow(Expr.BVar(0), Type0)),
                Expr.Lam(Name.Of("α"), Type0, Expr.Lam(Name.Of("d"), Expr.BVar(0),
                    honest ? Expr.BVar(1) : Expr.Const(Name.Of("B"), []))),
                ReducibilityHints.Regular(1), DefinitionSafety.Safe));
            return env;
        }
        InductiveDecl Ind()
        {
            Expr wrapped = Expr.MkApp(Expr.Const(Name.Of("optParam"), []),
                Expr.Const(Name.Of("A"), []), Expr.Const(Name.Of("dflt"), []));
            Expr self = Expr.App(Expr.Const(Name.Of("I"), []), Expr.BVar(0));
            return new InductiveDecl([], 1, [new InductiveType(Name.Of("I"), Expr.Pi(Name.Of("p"), wrapped, Type0),
                [new Constructor(Name.Of("I", "mk"), Expr.Pi(Name.Of("p"), wrapped, self))])], false);
        }

        Environment honestEnv = Build(honest: true);
        honestEnv.Add(Ind());
        Assert.NotNull(honestEnv.Find(Name.Of("I", "rec")));

        Environment hostile = Build(honest: false);
        Assert.Throws<KernelException>(() => hostile.Add(Ind()));
        Assert.Null(hostile.Find(Name.Of("I")));
    }

    // ---------------------------------------------------------------- what a comparison returns

    /// <summary>
    /// `Nat.beq` and `Nat.ble` are computed directly and the kernel hands back the constants named `Bool.true` and
    /// `Bool.false`. Those names are taken on trust too, but a lie about them cannot be made to stick: the
    /// equations checked before the shortcut is allowed are stated against those very constants, so a body that
    /// answers differently from the shortcut fails them and the operation is unfolded instead.
    /// </summary>
    [Fact]
    public void AComparisonCannotReturnSomethingItsBodyDoesNot()
    {
        var env = new Environment();
        AddRealNat(env);
        Expr nat = Expr.Const(Name.Of("Nat"), []);
        Expr boolE = Expr.Const(Name.Of("Bool"), []);
        env.Add(new InductiveDecl([], 0, [new InductiveType(Name.Of("Bool"), Type0,
            [new Constructor(Name.Of("Bool", "false"), boolE),
             new Constructor(Name.Of("Bool", "true"), boolE)])], false));

        // beq that always answers true, at exactly the right type.
        env.Add(new DefinitionDecl(Name.Of("Nat", "beq"), [], Expr.Arrow(nat, Expr.Arrow(nat, boolE)),
            Expr.Lam(Name.Of("a"), nat, Expr.Lam(Name.Of("b"), nat, Expr.Const(Name.Of("Bool", "true"), []))),
            ReducibilityHints.Regular(1), DefinitionSafety.Safe));

        Assert.False(env.PrimitiveOk(Primitive.NatBeq));   // beq 0 (succ y) ≡ false does not hold of that body

        // So the kernel unfolds it, and answers what the file actually says rather than what BigInteger would.
        Expr beq01 = Expr.MkApp(Expr.Const(Name.Of("Nat", "beq"), []), Expr.NatLit(0), Expr.NatLit(1));
        Assert.True(new TypeChecker(env).IsDefEq(beq01, Expr.Const(Name.Of("Bool", "true"), [])));
        Assert.False(new TypeChecker(env).IsDefEq(beq01, Expr.Const(Name.Of("Bool", "false"), [])));

        WithoutDefenses(() =>
        {
            // With the check off the shortcut fires on the name and contradicts the body it was given.
            Assert.True(new TypeChecker(env).IsDefEq(beq01, Expr.Const(Name.Of("Bool", "false"), [])));
            return 0;
        });
    }

    // ---------------------------------------------------------------- compiled code the checker refuses to trust

    /// <summary>
    /// `Lean.reduceBool` asks the kernel to believe the output of a compiled program. Tenet refuses rather than
    /// trusting it, which is a rejection and not a reduction, so a file cannot launder a claim through it.
    /// </summary>
    [Fact]
    public void CompiledCodeIsRefusedRatherThanBelieved()
    {
        var env = new Environment();
        Ax(env, Name.Of("Bool"), Type0);
        Expr boolE = Expr.Const(Name.Of("Bool"), []);
        Ax(env, Name.Of("Lean", "reduceBool"), Expr.Arrow(boolE, boolE));
        Ax(env, Name.Of("b"), boolE);
        Expr e = Expr.App(Expr.Const(Name.Of("Lean", "reduceBool"), []), Expr.Const(Name.Of("b"), []));
        var ex = Assert.Throws<UnsupportedException>(() => new TypeChecker(env).Whnf(e));
        Assert.Contains("compiled code", ex.Message, StringComparison.Ordinal);
    }
}
