using Tenet.Kernel;
using Xunit;

namespace Tenet.Tests;

public class ExprTests
{
    private static readonly Name A = Name.Of("a");

    [Fact]
    public void EqualityIgnoresBinderNamesAndInfo()
    {
        Expr t1 = Expr.Lam(Name.Of("x"), Expr.Prop, Expr.BVar(0), BinderInfo.Default);
        Expr t2 = Expr.Lam(Name.Of("y"), Expr.Prop, Expr.BVar(0), BinderInfo.Implicit);
        Assert.Equal(t1, t2);
        Assert.Equal(t1.GetHashCode(), t2.GetHashCode());
        Expr t3 = Expr.Lam(Name.Of("x"), Expr.Type0, Expr.BVar(0));
        Assert.NotEqual(t1, t3);
    }

    [Fact]
    public void LooseBVarRange()
    {
        Assert.Equal(0, Expr.Prop.LooseBVarRange);
        Assert.Equal(3, Expr.BVar(2).LooseBVarRange);
        Assert.Equal(2, Expr.Lam(A, Expr.Prop, Expr.BVar(2)).LooseBVarRange);
        Assert.Equal(0, Expr.Lam(A, Expr.Prop, Expr.BVar(0)).LooseBVarRange);
        Assert.Equal(1, Expr.Lam(A, Expr.BVar(0), Expr.BVar(0)).LooseBVarRange);
    }

    [Fact]
    public void InstantiateAndAbstractRoundTrip()
    {
        var lctx = new LocalContext();
        Expr x = lctx.MkLocalDecl(Name.Of("x"), Expr.Prop);
        Expr y = lctx.MkLocalDecl(Name.Of("y"), Expr.Prop);
        // body under two binders: #1 is x (outer), #0 is y (inner)
        Expr body = Expr.App(Expr.BVar(1), Expr.BVar(0));
        Expr inst = ExprOps.InstantiateRev(body, [x, y]);
        Assert.Equal(Expr.App(x, y), inst);
        Expr back = ExprOps.Abstract(inst, [x, y]);
        Assert.Equal(body, back);
        Expr closed = lctx.MkLambda([x, y], inst);
        Assert.False(closed.HasFVar);
        Assert.False(closed.HasLooseBVars);
        Assert.Equal(Expr.Lam(A, Expr.Prop, Expr.Lam(A, Expr.Prop, body)), closed);
    }

    [Fact]
    public void InstantiateLiftsSubstitutedTermsUnderBinders()
    {
        // (fun z => #1) with #0 := (#0)  ==> the substituted loose var must be lifted to #1 under the lambda
        Expr e = Expr.Lam(A, Expr.Prop, Expr.BVar(1));
        Expr r = ExprOps.Instantiate1(e, Expr.BVar(0));
        Assert.Equal(Expr.Lam(A, Expr.Prop, Expr.BVar(1)), r);
        // and closed terms are unchanged
        Expr r2 = ExprOps.Instantiate1(e, Expr.Prop);
        Assert.Equal(Expr.Lam(A, Expr.Prop, Expr.Prop), r2);
    }

    [Fact]
    public void HeadBeta()
    {
        Expr id = Expr.Lam(A, Expr.Type0, Expr.BVar(0));
        Expr app = Expr.App(Expr.App(Expr.Lam(A, Expr.Type0, id), Expr.Prop), Expr.NatType);
        Assert.Equal(Expr.NatType, ExprOps.HeadBetaReduce(app));
    }

    [Fact]
    public void LevelParamInstantiation()
    {
        Name u = Name.Of("u");
        Expr e = Expr.App(Expr.Const(Name.Of("List"), [Level.Param(u)]), Expr.Sort(Level.Param(u)));
        Expr r = ExprOps.InstantiateLevelParams(e, [u], [Level.One]);
        Assert.Equal(Expr.App(Expr.Const(Name.Of("List"), [Level.One]), Expr.Type0), r);
        Assert.False(r.HasLevelParam);
    }

    [Fact]
    public void NamesPrintAndCompare()
    {
        Assert.Equal("Nat.succ", Name.Of("Nat", "succ").ToString());
        Assert.Equal("u_1", Name.Of("u").AppendIndexAfter(1).ToString());
        Assert.Equal(Name.Of("Foo", "rec"), Name.Of("Nat", "rec").ReplacePrefix(Name.Of("Nat"), Name.Of("Foo")));
        Assert.Equal(Name.Of("succ"), Name.Of("Nat", "succ").ReplacePrefix(Name.Of("Nat"), Name.Anonymous));
        Assert.True(Name.Of("_nested").IsPrefixOf(Name.Of("_nested", "List_1")));
        Assert.False(Name.Of("Nat").IsPrefixOf(Name.Of("Int")));
        Assert.Equal("«a b».c", Name.Of("a b", "c").ToString());
    }
}

public class DepthTests
{
    /// <summary>Kernel recursion follows term depth; a few thousand nested binders must not overflow a worker's stack.</summary>
    [Fact]
    public void DeeplyNestedTermsCheck()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var env = new Tenet.Kernel.Environment();
                var tc = new TypeChecker(env);
                const int depth = 5000;
                Expr body = Expr.BVar(depth - 1);
                for (int i = 0; i < depth; i++)
                {
                    body = Expr.Lam(Name.Of("x"), Expr.Prop, body);
                }
                Expr type = tc.Check(body, []);
                Assert.True(type is PiExpr);
                Assert.Equal(depth, type.ToString().Split('→').Length);
            }
            catch (Exception e)
            {
                error = e;
            }
        }, 512 * 1024 * 1024);
        t.Start();
        t.Join();
        Assert.Null(error);
    }
}
