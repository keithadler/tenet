using System.Text.RegularExpressions;

namespace Tenet.Kernel;

/// <summary>What the kernel of a given Lean version does, as far as Tenet's verdicts depend on it.</summary>
public static class LeanVersion
{
    private static readonly Regex Shape = new(@"^v?(\d+)\.(\d+)\.(\d+)(?:-(.+))?$", RegexOptions.CultureInvariant);

    /// <summary>
    /// True when Lean's own kernel of this version does not evaluate <c>Lean.reduceBool</c> and <c>Lean.reduceNat</c>.
    /// Lean 4.34 and earlier did (running compiled code); it was removed before 4.35.0-rc1, so from 4.35 on a term such
    /// as <c>Lean.reduceBool b</c> is an ordinary stuck constant application and a proof that needs it is simply a type
    /// mismatch. An unparsable version, or a nightly build, answers false: Tenet then keeps declining those terms
    /// instead of guessing that a kernel it cannot identify rejects them.
    /// </summary>
    public static bool KernelHasNoNativeReduction(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }
        Match m = Shape.Match(version.Trim());
        if (!m.Success)
        {
            return false;
        }
        string suffix = m.Groups[4].Value;
        if (suffix.StartsWith("nightly", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        int major = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        int minor = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        return major > 4 || (major == 4 && minor >= 35);
    }
}
