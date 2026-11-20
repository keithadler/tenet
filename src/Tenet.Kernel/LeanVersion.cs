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

    /// <summary>
    /// True when Lean's own kernel of this version has the definitional-equality changes that first shipped in
    /// 4.37.0-rc1 (lean4#15103, #15371, #15373): proof irrelevance is tried before the first weak head normalization,
    /// a recursor's major premise is not reduced when it is a proof and is eta-expanded for a subsingleton structure
    /// such as <c>And</c>, and structure eta applies to a partially applied constructor. They landed on master on
    /// 2026-10-20, so a 4.37 nightly counts from 2026-10-21 on. An unparsable version answers false, which keeps the
    /// rules every earlier release has.
    /// </summary>
    public static bool KernelHasEta437(string? version)
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
        int major = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        int minor = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (major != 4 || minor != 37)
        {
            return major > 4 || (major == 4 && minor > 37);
        }
        string suffix = m.Groups[4].Value;
        Match night = Regex.Match(suffix, @"^nightly-(\d{4})-(\d{2})-(\d{2})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (!night.Success)
        {
            return !suffix.StartsWith("nightly", StringComparison.OrdinalIgnoreCase);
        }
        string date = night.Groups[1].Value + night.Groups[2].Value + night.Groups[3].Value;
        return string.CompareOrdinal(date, "20261021") >= 0;
    }
}
