using System.Numerics;
using Tenet.Kernel;

namespace Tenet.Export;

/// <summary>Version information from the export's leading <c>meta</c> line.</summary>
public sealed record ExportMeta(string ExporterName, string ExporterVersion, string LeanVersion, string LeanGitHash, string FormatVersion);

/// <summary>One exported declaration, in the order the exporter wrote it.</summary>
public abstract record ExportDecl
{
    public abstract string Kind { get; }
    /// <summary>The name to show for this declaration in reports.</summary>
    public abstract Name DisplayName { get; }
}

public sealed record ExportAxiom(Name Name, Name[] LevelParams, Expr Type, bool IsUnsafe) : ExportDecl
{
    public override string Kind => "axiom";
    public override Name DisplayName => Name;
}

public sealed record ExportDefinition(Name Name, Name[] LevelParams, Expr Type, Expr Value, ReducibilityHints Hints, DefinitionSafety Safety, Name[] All) : ExportDecl
{
    public override string Kind => "def";
    public override Name DisplayName => Name;
}

/// <summary>A block of mutually recursive (necessarily unsafe or partial) definitions.</summary>
public sealed record ExportMutualDefinition(ExportDefinition[] Definitions) : ExportDecl
{
    public override string Kind => "mutual def";
    public override Name DisplayName => Definitions.Length > 0 ? Definitions[0].Name : Name.Anonymous;
}

public sealed record ExportOpaque(Name Name, Name[] LevelParams, Expr Type, Expr Value, bool IsUnsafe, Name[] All) : ExportDecl
{
    public override string Kind => "opaque";
    public override Name DisplayName => Name;
}

public sealed record ExportTheorem(Name Name, Name[] LevelParams, Expr Type, Expr Value, Name[] All) : ExportDecl
{
    public override string Kind => "thm";
    public override Name DisplayName => Name;
}

public sealed record ExportQuot(Name Name, Name[] LevelParams, Expr Type, QuotKind QuotKind) : ExportDecl
{
    public override string Kind => "quot";
    public override Name DisplayName => Name;
}

public sealed record ExportInductiveVal(Name Name, Name[] LevelParams, Expr Type, int NumParams, int NumIndices, Name[] All, Name[] Ctors, int NumNested, bool IsRec, bool IsUnsafe, bool IsReflexive);

public sealed record ExportConstructorVal(Name Name, Name[] LevelParams, Expr Type, Name Induct, int Cidx, int NumParams, int NumFields, bool IsUnsafe);

public sealed record ExportRecursorRule(Name Ctor, int NumFields, Expr Rhs);

public sealed record ExportRecursorVal(Name Name, Name[] LevelParams, Expr Type, Name[] All, int NumParams, int NumIndices, int NumMotives, int NumMinors, ExportRecursorRule[] Rules, bool K, bool IsUnsafe);

/// <summary>A mutual (possibly nested) inductive block with the exporter's view of the derived constructors and recursors.</summary>
public sealed record ExportInductive(ExportInductiveVal[] Types, ExportConstructorVal[] Ctors, ExportRecursorVal[] Recs) : ExportDecl
{
    public override string Kind => "inductive";
    public override Name DisplayName => Types.Length > 0 ? Types[0].Name : Name.Anonymous;
}

/// <summary>A parsed export: the shared tables and the declarations in order.</summary>
public sealed class ExportFile
{
    public ExportMeta? Meta { get; internal set; }
    public List<Name> Names { get; } = new() { Name.Anonymous };
    public List<Level> Levels { get; } = new() { Level.Zero };
    public List<Expr> Exprs { get; } = new();
    public List<ExportDecl> Decls { get; } = new();

    private readonly Dictionary<LevelsKey, Level[]> _levelArrays = new();

    /// <summary>Constants with the same universe arguments share one array; Mathlib has millions of such constants.</summary>
    internal Level[] InternLevels(List<Level> ls)
    {
        var key = new LevelsKey(ls);
        if (_levelArrays.TryGetValue(key, out Level[]? arr))
        {
            return arr;
        }
        arr = ls.ToArray();
        _levelArrays[new LevelsKey(arr)] = arr;
        return arr;
    }

    private readonly struct LevelsKey : IEquatable<LevelsKey>
    {
        private readonly IReadOnlyList<Level> _ls;
        public LevelsKey(IReadOnlyList<Level> ls) => _ls = ls;
        public bool Equals(LevelsKey other) => Level.ListEquals(_ls, other._ls);
        public override bool Equals(object? obj) => obj is LevelsKey k && Equals(k);
        public override int GetHashCode()
        {
            var h = new HashCode();
            foreach (Level l in _ls)
            {
                h.Add(l.Hash);
            }
            return h.ToHashCode();
        }
    }

    /// <summary>All constant names the file declares, in order.</summary>
    public IEnumerable<Name> DeclaredNames()
    {
        foreach (ExportDecl d in Decls)
        {
            switch (d)
            {
                case ExportMutualDefinition m:
                    foreach (ExportDefinition d2 in m.Definitions)
                    {
                        yield return d2.Name;
                    }
                    break;
                case ExportInductive ind:
                    foreach (ExportInductiveVal t in ind.Types)
                    {
                        yield return t.Name;
                    }
                    foreach (ExportConstructorVal c in ind.Ctors)
                    {
                        yield return c.Name;
                    }
                    foreach (ExportRecursorVal r in ind.Recs)
                    {
                        yield return r.Name;
                    }
                    break;
                default:
                    yield return d.DisplayName;
                    break;
            }
        }
    }
}
