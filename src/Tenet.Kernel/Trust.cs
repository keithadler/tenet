namespace Tenet.Kernel;

/// <summary>
/// The names this kernel takes on trust: the ones it hardcodes and believes something about when it finds them
/// in a file. Each is a sentence of the form "if the file calls something <c>Nat.add</c>, it is addition", and
/// each is a thing an export can lie about. Both soundness bugs found in Tenet were exactly that.
///
/// Most are now defended, so a lie about them does not stick. This list exists anyway, for two reasons. It lets
/// <c>tenet audit</c> say that a project declares its own version of one of these, which is a fact about the file
/// worth knowing even when the verdict is sound: the kernel unfolds a primitive whose equations fail rather than
/// refusing it, so the arithmetic in that project is whatever the file says it is, correctly checked. And it
/// gives <c>TrustSurfaceTests</c> something to compare the kernel's source against, so the list and the code
/// cannot drift apart quietly.
///
/// Binder and universe-parameter names are not here. They label a bound variable and assert nothing.
/// </summary>
public static class Trust
{
    /// <summary>What a numeric literal denotes, and the constants its arithmetic is computed with.</summary>
    public static readonly Name[] NatNames =
    [
        Name.Of("Nat"), Name.Of("Nat", "zero"), Name.Of("Nat", "succ"),
        Name.Of("Nat", "add"), Name.Of("Nat", "sub"), Name.Of("Nat", "mul"), Name.Of("Nat", "div"),
        Name.Of("Nat", "mod"), Name.Of("Nat", "gcd"), Name.Of("Nat", "pow"), Name.Of("Nat", "pred"),
        Name.Of("Nat", "land"), Name.Of("Nat", "lor"), Name.Of("Nat", "xor"),
        Name.Of("Nat", "shiftLeft"), Name.Of("Nat", "shiftRight"),
        Name.Of("Nat", "beq"), Name.Of("Nat", "ble"),
    ];

    /// <summary>What a comparison's shortcut hands back.</summary>
    public static readonly Name[] BoolNames =
    [
        Name.Of("Bool"), Name.Of("Bool", "true"), Name.Of("Bool", "false"),
    ];

    /// <summary>What a string literal expands through.</summary>
    public static readonly Name[] StringNames =
    [
        Name.Of("String"), Name.Of("String", "mk"), Name.Of("String", "ofList"),
        Name.Of("Char"), Name.Of("Char", "ofNat"),
        Name.Of("List"), Name.Of("List", "nil"), Name.Of("List", "cons"),
    ];

    /// <summary>The quotient block, and the <c>Eq</c> its types are stated against.</summary>
    public static readonly Name[] QuotNames =
    [
        Name.Of("Quot"), Name.Of("Quot", "mk"), Name.Of("Quot", "lift"), Name.Of("Quot", "ind"), Name.Of("Eq"),
    ];

    /// <summary>Wrappers stripped by name, and the annotation that only changes how hard the checker works.</summary>
    public static readonly Name[] AnnotationNames =
    [
        Name.Of("optParam"), Name.Of("autoParam"), Name.Of("outParam"), Name.Of("semiOutParam"),
        Name.Of("eagerReduce"),
    ];

    /// <summary>Asks to believe the output of compiled code. Tenet refuses rather than reducing.</summary>
    public static readonly Name[] CompiledNames =
    [
        Name.Of("Lean", "reduceBool"), Name.Of("Lean", "reduceNat"),
    ];

    /// <summary>The namespace the kernel derives auxiliary types for nested inductives into.</summary>
    public static readonly Name NestedPrefix = Name.Of("_nested");

    /// <summary>Every name above, in one list.</summary>
    public static IReadOnlyList<Name> Names { get; } =
        [.. NatNames, .. BoolNames, .. StringNames, .. QuotNames, .. AnnotationNames, .. CompiledNames, NestedPrefix];
}
