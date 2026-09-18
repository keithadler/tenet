using Tenet.Kernel;
using Xunit;

namespace Tenet.Tests;

/// <summary>
/// The one place two other checkers were measurably ahead.
///
/// `imax u v` is `0` when `v` is and `max u v` otherwise, so normalizing once cannot settle a pair whose meaning
/// turns on that. Lean's kernel does not, and neither did Tenet. con-leche does, and showed it: on a mutated
/// `Init.Core` it accepts `PULift.noConfusion` where Lean rejects it and Tenet rejected it too.
///
/// `Level.CompleteEquality` decides those pairs by case analysis on which parameters can be zero. It is off by
/// default, because deciding more than Lean is a divergence rather than an improvement unless it is asked for.
/// </summary>
public class LevelCompletenessTests
{
    private static readonly Level S = Level.Param(Name.Of("s"));
    private static readonly Level R = Level.Param(Name.Of("r"));
    private static readonly Level U = Level.Param(Name.Of("u"));
    private static readonly Level One = Level.Succ(Level.Zero);

    private static T With<T>(bool complete, Func<T> f)
    {
        bool saved = Level.CompleteEquality;
        try
        {
            Level.CompleteEquality = complete;
            return f();
        }
        finally
        {
            Level.CompleteEquality = saved;
        }
    }

    [Fact]
    public void TheCaseConLecheDecidedAndTenetDidNot()
    {
        // Raw, as the reader builds a level from a file. The smart constructors collapse exactly this case, so a
        // test built with them passes without testing anything, which is how the first version of this went.
        Level a = Level.IMaxRaw(Level.IMaxRaw(S, Level.MaxRaw(R, One)), U);
        Level b = Level.IMaxRaw(Level.MaxRaw(Level.MaxRaw(One, R), S), U);

        // `max r 1` is at least 1 for every assignment, so the inner imax is a max and the two are one universe.
        Assert.False(With(false, () => Level.IsEquiv(a, b)));
        Assert.True(With(true, () => Level.IsEquiv(a, b)));
    }

    [Fact]
    public void ItDoesNotStartAcceptingLevelsThatDiffer()
    {
        With(true, () =>
        {
            Assert.False(Level.IsEquiv(Level.Succ(U), U));
            Assert.False(Level.IsEquiv(Level.MaxRaw(U, S), Level.MaxRaw(U, R)));
            Assert.False(Level.IsEquiv(Level.IMaxRaw(U, S), Level.IMaxRaw(U, R)));
            // imax u s and max u s part company exactly when s is zero, and the case analysis finds that.
            Assert.False(Level.IsEquiv(Level.IMaxRaw(U, S), Level.MaxRaw(U, S)));
            // ...while agreeing when the right-hand side cannot be zero.
            Assert.True(Level.IsEquiv(Level.IMaxRaw(U, Level.Succ(S)), Level.MaxRaw(U, Level.Succ(S))));
            Assert.False(Level.IsEquiv(Level.Zero, One));
            return 0;
        });
    }

    [Fact]
    public void ItOnlyEverAcceptsMore()
    {
        // Whatever the single pass decides, the case analysis decides the same way: it runs only after the fast
        // paths have failed, and every branch must agree before it answers yes.
        Level[] levels =
        [
            Level.Zero, One, U, S, Level.Succ(U), Level.MaxRaw(U, S), Level.IMaxRaw(U, S),
            Level.MaxRaw(S, U), Level.IMaxRaw(S, U), Level.MaxRaw(U, One), Level.IMaxRaw(U, Level.Succ(S)),
            Level.IMaxRaw(Level.MaxRaw(U, S), R),
        ];
        foreach (Level x in levels)
        {
            foreach (Level y in levels)
            {
                if (With(false, () => Level.IsEquiv(x, y)))
                {
                    Assert.True(With(true, () => Level.IsEquiv(x, y)), $"{x} vs {y}");
                }
            }
        }
    }
}
