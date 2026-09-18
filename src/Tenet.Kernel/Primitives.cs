namespace Tenet.Kernel;

/// <summary>
/// The primitives whose meaning the kernel assumes rather than derives.
///
/// Reducing <c>Nat.add 2 2</c> by adding two BigIntegers is a shortcut past the declaration's own body, and the
/// kernel takes it on the strength of the name alone. That is safe for Lean, which ships its prelude and does not
/// support replacing it. It is not safe here: Tenet's whole purpose is checking a file somebody else produced, and
/// an export is free to declare <c>Nat.add</c> as anything of the right type. Declared as <c>fun a b =&gt; a</c>,
/// the honest value of <c>Nat.add 2 2</c> is 2, and a name-dispatched shortcut answers 4. The checker then accepts
/// a false equation and rejects the true one.
///
/// So a shortcut is taken only for a constant whose defining equations have been checked. The equations are stated
/// over free variables, not over literals, which is what makes them worth checking: <c>add x 0 ≡ x</c> and
/// <c>add x (succ y) ≡ succ (add x y)</c> hold of addition and of nothing else on <c>Nat</c>, and neither can be
/// satisfied by an accelerated path, since acceleration needs literal arguments and these have none.
///
/// A constant that fails, or that this file does not know how to state equations for, is not rejected. It is
/// checked the ordinary way, by unfolding its body, which is slower and always right. Rejecting would turn a
/// legitimate but unusual prelude into a failure; declining to take the shortcut cannot.
///
/// The approach is lean4lean's (<c>Lean4Lean.Primitive.checkDef</c>), which validates the same equations and does
/// reject on failure. Lean's own kernel checks none of this, which lean4lean's divergences file states plainly.
/// </summary>
public enum Primitive
{
    /// <summary>The type a numeric literal denotes: the inductive Nat, with zero and succ and nothing else.</summary>
    NatLiteralType,

    /// <summary>The type a string literal denotes, together with the constructor its expansion goes through.</summary>
    StringLiteralType,

    NatSucc, NatPred, NatAdd, NatSub, NatMul, NatPow, NatBeq, NatBle,
    NatDiv, NatMod, NatGcd, NatLand, NatLor, NatXor, NatShiftLeft, NatShiftRight,
}

/// <summary>Whether a primitive may be short-circuited in this environment, decided once and remembered.</summary>
public static class Primitives
{
    /// <summary>Turn the check off and take every shortcut on the name alone, as Lean's kernel does.</summary>
    public static bool Validate { get; set; } =
        System.Environment.GetEnvironmentVariable("TENET_NO_PRIMITIVE_CHECK") is null;

    private static readonly Name Nat = Name.Of("Nat");
    private static readonly Name Bool = Name.Of("Bool");

    private static Name NameOf(Primitive p) => p switch
    {
        Primitive.NatLiteralType => Nat,
        Primitive.StringLiteralType => Name.Of("String"),
        Primitive.NatSucc => Name.Of("Nat", "succ"),
        Primitive.NatPred => Name.Of("Nat", "pred"),
        Primitive.NatAdd => Name.Of("Nat", "add"),
        Primitive.NatSub => Name.Of("Nat", "sub"),
        Primitive.NatMul => Name.Of("Nat", "mul"),
        Primitive.NatPow => Name.Of("Nat", "pow"),
        Primitive.NatBeq => Name.Of("Nat", "beq"),
        Primitive.NatBle => Name.Of("Nat", "ble"),
        Primitive.NatDiv => Name.Of("Nat", "div"),
        Primitive.NatMod => Name.Of("Nat", "mod"),
        Primitive.NatGcd => Name.Of("Nat", "gcd"),
        Primitive.NatLand => Name.Of("Nat", "land"),
        Primitive.NatLor => Name.Of("Nat", "lor"),
        Primitive.NatXor => Name.Of("Nat", "xor"),
        Primitive.NatShiftLeft => Name.Of("Nat", "shiftLeft"),
        Primitive.NatShiftRight => Name.Of("Nat", "shiftRight"),
        _ => throw new KernelException("unknown primitive"),
    };

    /// <summary>
    /// Equations pin the meaning; a type alone does not. Where this file knows the equations, it checks them and a
    /// pass licenses the shortcut. Where it does not, it checks the type only, which catches a constant of the
    /// wrong shape but not one of the right shape and the wrong body. The rest, div and mod and gcd and the
    /// bitwise operations, are defined by recursions whose unfolding is not a pair of clauses, so an equation over
    /// free variables cannot reduce; those are exercised at sampled values instead. That catches a substituted
    /// implementation and does not prove agreement everywhere, which is why the distinction is reported rather
    /// than smoothed over. Stating their clause forms properly is the work that would close it.
    /// </summary>
    public static bool EquationsKnown(Primitive p) => p is
        Primitive.NatSucc or Primitive.NatPred or Primitive.NatAdd or Primitive.NatSub
        or Primitive.NatMul or Primitive.NatPow or Primitive.NatBeq or Primitive.NatBle;

    /// <summary>Check one primitive against its equations. Never throws: a primitive that does not check is one the kernel will not shortcut.</summary>
    internal static bool Check(Environment env, Primitive p)
    {
        try
        {
            return CheckCore(env, p);
        }
        catch (KernelException)
        {
            return false;
        }
    }

    private static bool CheckCore(Environment env, Primitive p)
    {
        if (p == Primitive.StringLiteralType)
        {
            return CheckStringLiteralType(env);
        }
        if (env.Find(Nat) is not InductiveInfo natInd)
        {
            return false;
        }
        // Nat itself has to be the two-constructor type the literal representation assumes. This is what makes a
        // numeric literal mean a number: without it, `3` is a term of whatever the environment happens to call
        // Nat, and if that is a proposition then `3` is a proof of it.
        if (natInd.NumParams != 0 || natInd.NumIndices != 0 || natInd.Ctors.Length != 2
            || !natInd.Ctors[0].Equals(Name.Of("Nat", "zero")) || !natInd.Ctors[1].Equals(Name.Of("Nat", "succ")))
        {
            return false;
        }
        if (env.Find(Name.Of("Nat", "zero")) is not ConstructorInfo z || !z.Type.Equals(Expr.Const(Nat, []))
            || env.Find(Name.Of("Nat", "succ")) is not ConstructorInfo sc
            || !sc.Type.Equals(Expr.Arrow(Expr.Const(Nat, []), Expr.Const(Nat, []))))
        {
            return false;
        }
        if (p == Primitive.NatLiteralType)
        {
            return true;
        }

        Name name = NameOf(p);
        ConstantInfo? info = env.Find(name);
        if (info is null || info.LevelParams.Length != 0)
        {
            return false;
        }

        Expr nat = Expr.Const(Nat, []);
        Expr zero = Expr.Const(Name.Of("Nat", "zero"), []);
        Expr Succ(Expr e) => Expr.App(Expr.Const(Name.Of("Nat", "succ"), []), e);

        // Nat.succ is a constructor, so its shape is the inductive's and there is no body to check.
        if (p == Primitive.NatSucc)
        {
            return info is ConstructorInfo && SameType(env, info.Type, Expr.Arrow(nat, nat));
        }
        if (info is not DefinitionInfo def || def.Safety != DefinitionSafety.Safe)
        {
            return false;
        }

        bool toBool = p is Primitive.NatBeq or Primitive.NatBle;
        Expr result = toBool ? Expr.Const(Bool, []) : nat;
        if (toBool && env.Find(Bool) is null)
        {
            return false;
        }
        Expr expected = p == Primitive.NatPred
            ? Expr.Arrow(nat, nat)
            : Expr.Arrow(nat, Expr.Arrow(nat, result));
        if (!SameType(env, def.Type, expected))
        {
            return false;
        }

        // The equations run with the shortcut off, so a primitive cannot license itself. They are stated over
        // free variables, which no shortcut can fire on anyway, but the flag makes that independent of that fact.
        var tc = new TypeChecker(env) { SkipPrimitives = true };
        Expr f = Expr.Const(name, []);
        Expr x = tc.Lctx.MkLocalDecl(Name.Of("x"), nat);
        Expr y = tc.Lctx.MkLocalDecl(Name.Of("y"), nat);
        Expr Ap1(Expr a) => Expr.App(f, a);
        Expr Ap2(Expr a, Expr b) => Expr.MkApp(f, a, b);

        return p switch
        {
            //  pred 0 ≡ 0                 pred (succ x) ≡ x
            Primitive.NatPred => tc.IsDefEq(Ap1(zero), zero) && tc.IsDefEq(Ap1(Succ(x)), x),

            //  add x 0 ≡ x                add x (succ y) ≡ succ (add x y)
            Primitive.NatAdd => tc.IsDefEq(Ap2(x, zero), x)
                             && tc.IsDefEq(Ap2(x, Succ(y)), Succ(Ap2(x, y))),

            //  sub x 0 ≡ x                sub x (succ y) ≡ pred (sub x y)
            Primitive.NatSub => Ok(env, Primitive.NatPred)
                             && tc.IsDefEq(Ap2(x, zero), x)
                             && tc.IsDefEq(Ap2(x, Succ(y)),
                                    Expr.App(Expr.Const(Name.Of("Nat", "pred"), []), Ap2(x, y))),

            //  mul x 0 ≡ 0                mul x (succ y) ≡ add (mul x y) x
            Primitive.NatMul => Ok(env, Primitive.NatAdd)
                             && tc.IsDefEq(Ap2(x, zero), zero)
                             && tc.IsDefEq(Ap2(x, Succ(y)),
                                    Expr.MkApp(Expr.Const(Name.Of("Nat", "add"), []), Ap2(x, y), x)),

            //  pow x 0 ≡ 1                pow x (succ y) ≡ mul (pow x y) x
            Primitive.NatPow => Ok(env, Primitive.NatMul)
                             && tc.IsDefEq(Ap2(x, zero), Succ(zero))
                             && tc.IsDefEq(Ap2(x, Succ(y)),
                                    Expr.MkApp(Expr.Const(Name.Of("Nat", "mul"), []), Ap2(x, y), x)),

            //  beq 0 0 ≡ true             beq 0 (succ y) ≡ false
            //  beq (succ x) 0 ≡ false     beq (succ x) (succ y) ≡ beq x y
            Primitive.NatBeq => tc.IsDefEq(Ap2(zero, zero), True())
                             && tc.IsDefEq(Ap2(zero, Succ(y)), False())
                             && tc.IsDefEq(Ap2(Succ(x), zero), False())
                             && tc.IsDefEq(Ap2(Succ(x), Succ(y)), Ap2(x, y)),

            //  ble 0 0 ≡ true             ble 0 (succ y) ≡ true
            //  ble (succ x) 0 ≡ false     ble (succ x) (succ y) ≡ ble x y
            //
            //  Four clauses, not three. Lean's definition matches on both arguments in the zero cases, so
            //  `ble 0 y` with y free does not reduce, and stating it that way made this primitive fail on every
            //  Lean before 4.34 and fall back to unfolding, which is where the olean-compat matrix caught it.
            Primitive.NatBle => tc.IsDefEq(Ap2(zero, zero), True())
                             && tc.IsDefEq(Ap2(zero, Succ(y)), True())
                             && tc.IsDefEq(Ap2(Succ(x), zero), False())
                             && tc.IsDefEq(Ap2(Succ(x), Succ(y)), Ap2(x, y)),

            //  No clause form to check: div, mod and gcd are well-founded recursions and the bitwise operations
            //  recurse on a shifted argument, so an equation over free variables cannot reduce. These are
            //  checked at sampled values instead, which is weaker and is labelled as such by EquationsKnown.
            _ => Samples(p).All(t => tc.IsDefEq(Ap2(Expr.NatLit(t.X), Expr.NatLit(t.Y)), Expr.NatLit(t.R))),
        };

        static Expr True() => Expr.Const(Name.Of("Bool", "true"), []);
        static Expr False() => Expr.Const(Name.Of("Bool", "false"), []);
    }

    /// <summary>
    /// A string literal is a term of the type named <c>String</c>, and it reduces through the constructor
    /// <see cref="Environment.StringLiteralConstructor"/> applied to a list of characters. Both have to be what
    /// they are named, for the same reason a numeric literal's type does.
    /// </summary>
    private static bool CheckStringLiteralType(Environment env)
    {
        Name str = Name.Of("String");
        if (env.Find(str) is not ConstantInfo sInfo || sInfo is not InductiveInfo ind
            || ind.NumParams != 0 || ind.NumIndices != 0 || ind.LevelParams.Length != 0)
        {
            return false;
        }
        if (env.Find(Name.Of("Char")) is null || env.Find(Name.Of("List")) is null
            || env.Find(Name.Of("Char", "ofNat")) is null
            || env.Find(Name.Of("List", "cons")) is null || env.Find(Name.Of("List", "nil")) is null)
        {
            return false;
        }
        ConstantInfo? ctor = env.Find(env.StringLiteralConstructor);
        if (ctor is null || ctor.LevelParams.Length != 0)
        {
            return false;
        }
        Expr listChar = Expr.App(Expr.Const(Name.Of("List"), [Level.Zero]), Expr.Const(Name.Of("Char"), []));
        if (!SameType(env, ctor.Type, Expr.Arrow(listChar, Expr.Const(str, []))))
        {
            return false;
        }

        // The decisive check, and the one that does not depend on enumerating shapes: build what the kernel would
        // build for a literal and see whether it is a String. This covers Char.ofNat, List.cons and List.nil at
        // once, and covers them as the expansion actually uses them rather than as this file guesses it does.
        try
        {
            var tc = new TypeChecker(env) { SkipPrimitives = true };
            Expr built = Inductive.StringLitToConstructor(env, Expr.StrLit("a"));
            return tc.IsDefEq(tc.Check(built), Expr.Const(str, []));
        }
        catch (KernelException)
        {
            return false;
        }
    }

    /// <summary>
    /// Values at which a primitive with no checkable clause form is exercised. This detects a substituted
    /// implementation, which is the attack, but it is a finite sample and not a proof that the constant agrees
    /// with the accelerated path everywhere. <see cref="EquationsKnown"/> reports which primitives got the
    /// stronger check. Points are kept small because they are computed by unfolding the real body.
    /// </summary>
    private static (int X, int Y, int R)[] Samples(Primitive p) => p switch
    {
        //                     x   y   x∘y        including the boundary each definition special-cases
        Primitive.NatDiv => [(17, 5, 3), (5, 17, 0), (12, 3, 4), (7, 1, 7), (9, 0, 0), (0, 4, 0)],
        Primitive.NatMod => [(17, 5, 2), (5, 17, 5), (12, 3, 0), (7, 1, 0), (9, 0, 9), (0, 4, 0)],
        Primitive.NatGcd => [(12, 18, 6), (17, 5, 1), (0, 7, 7), (7, 0, 7), (9, 9, 9)],
        Primitive.NatLand => [(12, 10, 8), (7, 8, 0), (255, 15, 15), (0, 9, 0)],
        Primitive.NatLor => [(12, 10, 14), (7, 8, 15), (0, 9, 9), (5, 5, 5)],
        Primitive.NatXor => [(12, 10, 6), (7, 8, 15), (0, 9, 9), (5, 5, 0)],
        Primitive.NatShiftLeft => [(1, 4, 16), (3, 2, 12), (5, 0, 5), (0, 3, 0)],
        Primitive.NatShiftRight => [(16, 4, 1), (12, 2, 3), (5, 0, 5), (3, 5, 0)],
        _ => [],
    };

    /// <summary>Types are compared up to definitional equality, since a prelude may route one through an alias.</summary>
    private static bool SameType(Environment env, Expr declared, Expr expected)
    {
        try
        {
            return declared.Equals(expected)
                || new TypeChecker(env) { SkipPrimitives = true }.IsDefEq(declared, expected);
        }
        catch (KernelException)
        {
            return false;
        }
    }

    private static bool Ok(Environment env, Primitive p) => env.PrimitiveOk(p);
}
