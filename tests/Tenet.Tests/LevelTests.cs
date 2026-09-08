using Tenet.Kernel;
using Xunit;

namespace Tenet.Tests;

public class LevelTests
{
    private static readonly Level U = Level.Param(Name.Of("u"));
    private static readonly Level V = Level.Param(Name.Of("v"));

    [Fact]
    public void MaxIsCommutativeUpToNormalization()
    {
        Assert.True(Level.IsEquiv(Level.MaxRaw(U, V), Level.MaxRaw(V, U)));
        Assert.False(Level.MaxRaw(U, V).Equals(Level.MaxRaw(V, U)));
    }

    [Fact]
    public void SuccDistributesOverMax()
    {
        Assert.True(Level.IsEquiv(Level.Succ(Level.MaxRaw(U, V)), Level.MaxRaw(Level.Succ(U), Level.Succ(V))));
    }

    [Fact]
    public void IMaxWithZeroIsZero()
    {
        Assert.True(Level.IsEquiv(Level.IMaxRaw(U, Level.Zero), Level.Zero));
        Assert.True(Level.IMaxRaw(U, Level.Zero).NormalizesToZero());
        Assert.False(Level.IMaxRaw(U, V).NormalizesToZero());
    }

    [Fact]
    public void IMaxWithSuccIsMax()
    {
        Assert.True(Level.IsEquiv(Level.IMaxRaw(U, Level.Succ(V)), Level.MaxRaw(U, Level.Succ(V))));
    }

    [Fact]
    public void ExplicitLevelsCollapse()
    {
        Assert.True(Level.IsEquiv(Level.MaxRaw(Level.One, Level.Succ(Level.One)), Level.Succ(Level.One)));
        Assert.True(Level.IsEquiv(Level.MaxRaw(U, Level.Zero), U));
    }

    [Fact]
    public void GeqCases()
    {
        Assert.True(Level.IsGeq(Level.Succ(U), U));
        Assert.True(Level.IsGeq(Level.MaxRaw(U, V), U));
        Assert.True(Level.IsGeq(Level.MaxRaw(U, V), V));
        Assert.False(Level.IsGeq(U, Level.Succ(U)));
        Assert.False(Level.IsGeq(U, V));
        Assert.True(Level.IsGeq(U, Level.Zero));
        Assert.True(Level.IsGeq(Level.IMaxRaw(U, V), V));
    }

    [Fact]
    public void InstantiateReplacesParams()
    {
        Level l = Level.MaxRaw(Level.Succ(U), V);
        Level r = l.Instantiate([Name.Of("u"), Name.Of("v")], [Level.Zero, Level.One]);
        Assert.True(Level.IsEquiv(r, Level.One));
        Assert.False(r.HasParam);
    }

    [Fact]
    public void UndefParamIsReported()
    {
        Assert.Equal(Name.Of("v"), Level.MaxRaw(U, V).GetUndefParam([Name.Of("u")]));
        Assert.Null(Level.MaxRaw(U, V).GetUndefParam([Name.Of("u"), Name.Of("v")]));
    }

    [Fact]
    public void IsNotZero()
    {
        Assert.True(Level.Succ(U).IsNotZero());
        Assert.True(Level.MaxRaw(U, Level.One).IsNotZero());
        Assert.False(Level.MaxRaw(U, V).IsNotZero());
        Assert.False(Level.IMaxRaw(Level.One, V).IsNotZero());
    }
}

public class LevelReferenceAlgorithmTests
{
    /// <summary>
    /// The reference kernel's level normalization is incomplete on purpose and Tenet must be incomplete in exactly the
    /// same way: `imax s (max r 1)` normalizes to `max s (max 1 r)` while `max s (max r 1)` normalizes to
    /// `max 1 (max r s)`, so the two are NOT judged equivalent even though they denote the same universe.
    /// Found by differential testing (Init.Core, PULift.up.inj).
    /// </summary>
    [Fact]
    public void ImaxThatCollapsesToMaxIsNotReflattened()
    {
        Level r = Level.Param(Name.Of("r"));
        Level s = Level.Param(Name.Of("s"));
        Level maxR1 = Level.MaxRaw(r, Level.One);
        Level a = Level.IMaxRaw(s, maxR1);
        Level b = Level.MaxRaw(s, maxR1);
        Assert.False(Level.IsEquiv(a, b));
        // and the flattened spelling is what the reference produces for b
        Assert.Equal("max 1 (max r s)", b.Normalize().ToString());
        Assert.Equal("max s (max 1 r)", a.Normalize().ToString());
    }
}
