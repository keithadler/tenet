namespace Tenet.Kernel;

public enum DefinitionSafety : byte
{
    Unsafe,
    Safe,
    Partial,
}

public enum QuotKind : byte
{
    Type,
    Ctor,
    Lift,
    Ind,
}

/// <summary>Hints that guide lazy delta reduction in the definitional equality checker.</summary>
public abstract record ReducibilityHints
{
    public static readonly ReducibilityHints Opaque = new OpaqueHints();
    public static readonly ReducibilityHints Abbrev = new AbbrevHints();
    public static ReducibilityHints Regular(uint height) => new RegularHints(height);

    public bool IsRegular => this is RegularHints;
    public bool IsAbbrev => this is AbbrevHints;
    public bool IsOpaque => this is OpaqueHints;

    /// <summary>
    /// Negative: unfold the first; positive: unfold the second; zero: unfold both.
    /// Mirrors the Lean kernel's <c>compare</c> on reducibility hints.
    /// </summary>
    public static int Compare(ReducibilityHints h1, ReducibilityHints h2)
    {
        if (h1 is RegularHints r1 && h2 is RegularHints r2)
        {
            if (r1.Height == r2.Height)
            {
                return 0;
            }
            return r1.Height > r2.Height ? -1 : 1;
        }
        if (h1.GetType() == h2.GetType())
        {
            return 0;
        }
        if (h1.IsOpaque)
        {
            return 1;
        }
        if (h2.IsOpaque)
        {
            return -1;
        }
        if (h1.IsAbbrev)
        {
            return -1;
        }
        return 1;
    }

    public sealed record OpaqueHints : ReducibilityHints
    {
        public override string ToString() => "opaque";
    }

    public sealed record AbbrevHints : ReducibilityHints
    {
        public override string ToString() => "abbrev";
    }

    public sealed record RegularHints(uint Height) : ReducibilityHints
    {
        public override string ToString() => "regular " + Height;
    }
}

public sealed record RecursorRule(Name Ctor, int NumFields, Expr Rhs);

/// <summary>A constant as stored in the environment: the checked form of a declaration.</summary>
public abstract class ConstantInfo
{
    public readonly Name Name;
    public readonly Name[] LevelParams;
    public readonly Expr Type;

    private protected ConstantInfo(Name name, Name[] levelParams, Expr type)
    {
        Name = name;
        LevelParams = levelParams;
        Type = type;
    }

    public abstract string KindName { get; }
    public virtual bool IsUnsafe => false;

    /// <summary>Definitions and theorems have a value the kernel may unfold.</summary>
    public virtual Expr? Value => null;
    public bool HasValue => Value is not null;

    public Expr InstantiateTypeLevelParams(Level[] ls)
    {
        if (ls.Length != LevelParams.Length)
        {
            throw new KernelException($"universe level mismatch instantiating type of '{Name}'");
        }
        return ls.Length == 0 ? Type : ExprOps.InstantiateLevelParams(Type, LevelParams, ls);
    }

    public Expr InstantiateValueLevelParams(Level[] ls)
    {
        Expr v = Value ?? throw new KernelException($"'{Name}' has no value");
        if (ls.Length != LevelParams.Length)
        {
            throw new KernelException($"universe level mismatch instantiating value of '{Name}'");
        }
        return ls.Length == 0 ? v : ExprOps.InstantiateLevelParams(v, LevelParams, ls);
    }

    public override string ToString() => KindName + " " + Name;
}

public sealed class AxiomInfo : ConstantInfo
{
    private readonly bool _isUnsafe;
    public AxiomInfo(Name name, Name[] levelParams, Expr type, bool isUnsafe) : base(name, levelParams, type) => _isUnsafe = isUnsafe;
    public override string KindName => "axiom";
    public override bool IsUnsafe => _isUnsafe;
}

public sealed class DefinitionInfo : ConstantInfo
{
    private readonly Expr _value;
    public readonly ReducibilityHints Hints;
    public readonly DefinitionSafety Safety;
    public readonly Name[] All;

    public DefinitionInfo(Name name, Name[] levelParams, Expr type, Expr value, ReducibilityHints hints, DefinitionSafety safety, Name[] all)
        : base(name, levelParams, type)
    {
        _value = value;
        Hints = hints;
        Safety = safety;
        All = all;
    }

    public override string KindName => "def";
    public override bool IsUnsafe => Safety == DefinitionSafety.Unsafe;
    public override Expr Value => _value;
}

public sealed class TheoremInfo : ConstantInfo
{
    private readonly Expr _value;
    public readonly Name[] All;

    public TheoremInfo(Name name, Name[] levelParams, Expr type, Expr value, Name[] all) : base(name, levelParams, type)
    {
        _value = value;
        All = all;
    }

    public override string KindName => "theorem";
    public override Expr Value => _value;
}

public sealed class OpaqueInfo : ConstantInfo
{
    /// <summary>The value is checked but never unfolded by the kernel.</summary>
    public readonly Expr OpaqueValue;
    private readonly bool _isUnsafe;
    public readonly Name[] All;

    public OpaqueInfo(Name name, Name[] levelParams, Expr type, Expr value, bool isUnsafe, Name[] all) : base(name, levelParams, type)
    {
        OpaqueValue = value;
        _isUnsafe = isUnsafe;
        All = all;
    }

    public override string KindName => "opaque";
    public override bool IsUnsafe => _isUnsafe;
}

public sealed class QuotInfo : ConstantInfo
{
    public readonly QuotKind Kind;
    public QuotInfo(Name name, Name[] levelParams, Expr type, QuotKind kind) : base(name, levelParams, type) => Kind = kind;
    public override string KindName => "quot";
}

public sealed class InductiveInfo : ConstantInfo
{
    public readonly int NumParams;
    public readonly int NumIndices;
    /// <summary>All inductive types in the mutual block, in order.</summary>
    public readonly Name[] All;
    public readonly Name[] Ctors;
    public readonly int NumNested;
    public readonly bool IsRec;
    private readonly bool _isUnsafe;
    public readonly bool IsReflexive;

    public InductiveInfo(Name name, Name[] levelParams, Expr type, int numParams, int numIndices, Name[] all, Name[] ctors,
                         int numNested, bool isRec, bool isUnsafe, bool isReflexive)
        : base(name, levelParams, type)
    {
        NumParams = numParams;
        NumIndices = numIndices;
        All = all;
        Ctors = ctors;
        NumNested = numNested;
        IsRec = isRec;
        _isUnsafe = isUnsafe;
        IsReflexive = isReflexive;
    }

    public override string KindName => "inductive";
    public override bool IsUnsafe => _isUnsafe;
}

public sealed class ConstructorInfo : ConstantInfo
{
    public readonly Name Induct;
    public readonly int Cidx;
    public readonly int NumParams;
    public readonly int NumFields;
    private readonly bool _isUnsafe;

    public ConstructorInfo(Name name, Name[] levelParams, Expr type, Name induct, int cidx, int numParams, int numFields, bool isUnsafe)
        : base(name, levelParams, type)
    {
        Induct = induct;
        Cidx = cidx;
        NumParams = numParams;
        NumFields = numFields;
        _isUnsafe = isUnsafe;
    }

    public override string KindName => "constructor";
    public override bool IsUnsafe => _isUnsafe;
}

public sealed class RecursorInfo : ConstantInfo
{
    public readonly Name[] All;
    public readonly int NumParams;
    public readonly int NumIndices;
    public readonly int NumMotives;
    public readonly int NumMinors;
    public readonly RecursorRule[] Rules;
    /// <summary>Supports K-like reduction (a single constructor with no fields, in Prop).</summary>
    public readonly bool K;
    private readonly bool _isUnsafe;

    public RecursorInfo(Name name, Name[] levelParams, Expr type, Name[] all, int numParams, int numIndices, int numMotives, int numMinors,
                        RecursorRule[] rules, bool k, bool isUnsafe)
        : base(name, levelParams, type)
    {
        All = all;
        NumParams = numParams;
        NumIndices = numIndices;
        NumMotives = numMotives;
        NumMinors = numMinors;
        Rules = rules;
        K = k;
        _isUnsafe = isUnsafe;
    }

    public override string KindName => "recursor";
    public override bool IsUnsafe => _isUnsafe;

    public int MajorIdx => NumParams + NumMotives + NumMinors + NumIndices;

    /// <summary>
    /// The inductive type of the major premise, read off the recursor's type. Like the reference
    /// (<c>recursor_val::get_major_induct</c>, which uses <c>binding_body</c>), this walks lambdas as well as
    /// pis, so a recursor whose type is malformed in that way still names its inductive type here and is
    /// rejected elsewhere for the same reason the reference rejects it.
    /// </summary>
    public Name GetMajorInduct()
    {
        Expr e = Type;
        for (int i = 0; i < MajorIdx; i++)
        {
            e = e is BindingExpr b ? b.Body : throw new KernelException($"malformed recursor type for '{Name}'");
        }
        if (e is BindingExpr major && major.Domain.GetAppFn() is ConstExpr c)
        {
            return c.Name;
        }
        throw new KernelException($"malformed recursor type for '{Name}'");
    }

    public RecursorRule? GetRuleFor(Expr major)
    {
        if (major.GetAppFn() is not ConstExpr c)
        {
            return null;
        }
        foreach (RecursorRule r in Rules)
        {
            if (r.Ctor.Equals(c.Name))
            {
                return r;
            }
        }
        return null;
    }
}

// ---------------------------------------------------------------------------------------------
// Declarations: the input to Environment.Add. The kernel checks them and produces ConstantInfos.
// ---------------------------------------------------------------------------------------------

public abstract class Declaration
{
    public abstract string KindName { get; }
}

public sealed class AxiomDecl : Declaration
{
    public readonly Name Name;
    public readonly Name[] LevelParams;
    public readonly Expr Type;
    public readonly bool IsUnsafe;

    public AxiomDecl(Name name, Name[] levelParams, Expr type, bool isUnsafe)
    {
        Name = name;
        LevelParams = levelParams;
        Type = type;
        IsUnsafe = isUnsafe;
    }

    public override string KindName => "axiom";
    public AxiomInfo ToInfo() => new(Name, LevelParams, Type, IsUnsafe);
}

public sealed class DefinitionDecl : Declaration
{
    public readonly Name Name;
    public readonly Name[] LevelParams;
    public readonly Expr Type;
    public readonly Expr Value;
    public readonly ReducibilityHints Hints;
    public readonly DefinitionSafety Safety;
    public readonly Name[] All;

    public DefinitionDecl(Name name, Name[] levelParams, Expr type, Expr value, ReducibilityHints hints, DefinitionSafety safety, Name[]? all = null)
    {
        Name = name;
        LevelParams = levelParams;
        Type = type;
        Value = value;
        Hints = hints;
        Safety = safety;
        All = all ?? [name];
    }

    public override string KindName => "def";
    public DefinitionInfo ToInfo() => new(Name, LevelParams, Type, Value, Hints, Safety, All);
}

public sealed class TheoremDecl : Declaration
{
    public readonly Name Name;
    public readonly Name[] LevelParams;
    public readonly Expr Type;
    public readonly Expr Value;
    public readonly Name[] All;

    public TheoremDecl(Name name, Name[] levelParams, Expr type, Expr value, Name[]? all = null)
    {
        Name = name;
        LevelParams = levelParams;
        Type = type;
        Value = value;
        All = all ?? [name];
    }

    public override string KindName => "theorem";
    public TheoremInfo ToInfo() => new(Name, LevelParams, Type, Value, All);
}

public sealed class OpaqueDecl : Declaration
{
    public readonly Name Name;
    public readonly Name[] LevelParams;
    public readonly Expr Type;
    public readonly Expr Value;
    public readonly bool IsUnsafe;
    public readonly Name[] All;

    public OpaqueDecl(Name name, Name[] levelParams, Expr type, Expr value, bool isUnsafe, Name[]? all = null)
    {
        Name = name;
        LevelParams = levelParams;
        Type = type;
        Value = value;
        IsUnsafe = isUnsafe;
        All = all ?? [name];
    }

    public override string KindName => "opaque";
    public OpaqueInfo ToInfo() => new(Name, LevelParams, Type, Value, IsUnsafe, All);
}

/// <summary>Adds the four quotient constants; carries no data because their types are fixed.</summary>
public sealed class QuotDecl : Declaration
{
    public override string KindName => "quot";
}

/// <summary>A (possibly unsafe) block of mutual definitions, checked after all headers are added.</summary>
public sealed class MutualDefinitionDecl : Declaration
{
    public readonly DefinitionDecl[] Definitions;
    public MutualDefinitionDecl(DefinitionDecl[] definitions) => Definitions = definitions;
    public override string KindName => "mutual def";
}

public sealed record Constructor(Name Name, Expr Type);

public sealed record InductiveType(Name Name, Expr Type, Constructor[] Ctors);

/// <summary>A (mutual, possibly nested) inductive declaration: the kernel derives constructors' metadata and the recursors.</summary>
public sealed class InductiveDecl : Declaration
{
    public readonly Name[] LevelParams;
    public readonly int NumParams;
    public readonly InductiveType[] Types;
    public readonly bool IsUnsafe;

    public InductiveDecl(Name[] levelParams, int numParams, InductiveType[] types, bool isUnsafe)
    {
        LevelParams = levelParams;
        NumParams = numParams;
        Types = types;
        IsUnsafe = isUnsafe;
    }

    public override string KindName => "inductive";
}
