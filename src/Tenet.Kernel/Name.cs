using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Tenet.Kernel;

/// <summary>
/// A hierarchical name, as in Lean 4: the anonymous name, or a string or numeric component
/// appended to a prefix. Names are immutable and compare structurally.
/// </summary>
public abstract class Name : IEquatable<Name>, IComparable<Name>
{
    /// <summary>The empty name.</summary>
    public static readonly Name Anonymous = new AnonymousName();

    private readonly int _hash;

    private protected Name(int hash) => _hash = hash;

    /// <summary>The name without its last component. The anonymous name is its own prefix.</summary>
    public abstract Name Prefix { get; }

    public bool IsAnonymous => ReferenceEquals(this, Anonymous);

    /// <summary>The final string component, or null when the name is anonymous or ends in a numeric component.</summary>
    public string? LastString => this is StrName s ? s.Value : null;

    /// <summary>Append a string component.</summary>
    public Name Str(string s) => new StrName(this, s);

    /// <summary>Append a numeric component.</summary>
    public Name Num(ulong n) => new NumName(this, n);

    /// <summary>Build a name from string components: <c>Name.Of("Nat", "succ")</c> is <c>Nat.succ</c>.</summary>
    public static Name Of(params string[] parts)
    {
        Name n = Anonymous;
        foreach (string p in parts)
        {
            n = n.Str(p);
        }
        return n;
    }

    /// <summary>
    /// Build a name from a dotted string with no escaping; an all-digit component is numeric, so that names
    /// printed by <see cref="ToString"/> such as <c>_private.Mathlib.Foo.0.bar</c> round-trip. For internal
    /// constants and command-line arguments.
    /// </summary>
    public static Name Parse(string dotted)
    {
        Name n = Anonymous;
        if (dotted.Length == 0)
        {
            return n;
        }
        foreach (string p in dotted.Split('.'))
        {
            n = p.Length > 0 && p.All(char.IsAsciiDigit) && ulong.TryParse(p, out ulong v) ? n.Num(v) : n.Str(p);
        }
        return n;
    }

    /// <summary>Lean's <c>Name.appendIndexAfter</c>: <c>u</c> becomes <c>u_1</c>.</summary>
    public Name AppendIndexAfter(ulong idx) => this switch
    {
        StrName s => new StrName(s.Prefix, s.Value + "_" + idx.ToString()),
        _ => new StrName(this, "_" + idx.ToString()),
    };

    /// <summary>Lean's <c>Name.appendAfter</c>: appends a suffix to the last string component.</summary>
    public Name AppendAfter(string suffix) => this switch
    {
        StrName s => new StrName(s.Prefix, s.Value + suffix),
        _ => new StrName(this, suffix),
    };

    /// <summary>Lean's <c>Name.replacePrefix</c>.</summary>
    public Name ReplacePrefix(Name query, Name newPrefix)
    {
        if (IsAnonymous)
        {
            return query.IsAnonymous ? newPrefix : this;
        }
        if (Equals(query))
        {
            return newPrefix;
        }
        return this switch
        {
            StrName s => new StrName(s.Prefix.ReplacePrefix(query, newPrefix), s.Value),
            NumName n => new NumName(n.Prefix.ReplacePrefix(query, newPrefix), n.Value),
            _ => throw new InvalidOperationException(),
        };
    }

    /// <summary>True when this name is a (not necessarily proper) prefix of <paramref name="other"/>.</summary>
    public bool IsPrefixOf(Name other)
    {
        for (Name n = other; ; n = n.Prefix)
        {
            if (n.Equals(this))
            {
                return true;
            }
            if (n.IsAnonymous)
            {
                return false;
            }
        }
    }

    public bool Equals(Name? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }
        if (other is null || _hash != other._hash)
        {
            return false;
        }
        return (this, other) switch
        {
            (StrName a, StrName b) => a.Value == b.Value && a.Prefix.Equals(b.Prefix),
            (NumName a, NumName b) => a.Value == b.Value && a.Prefix.Equals(b.Prefix),
            (AnonymousName, AnonymousName) => true,
            _ => false,
        };
    }

    public override bool Equals(object? obj) => obj is Name n && Equals(n);

    public override int GetHashCode() => _hash;

    /// <summary>A total order on names. Used where the kernel needs a canonical ordering.</summary>
    public int CompareTo(Name? other)
    {
        if (other is null)
        {
            return 1;
        }
        if (ReferenceEquals(this, other))
        {
            return 0;
        }
        if (IsAnonymous)
        {
            return other.IsAnonymous ? 0 : -1;
        }
        if (other.IsAnonymous)
        {
            return 1;
        }
        int c = Prefix.CompareTo(other.Prefix);
        if (c != 0)
        {
            return c;
        }
        return (this, other) switch
        {
            (StrName a, StrName b) => string.CompareOrdinal(a.Value, b.Value),
            (NumName a, NumName b) => a.Value.CompareTo(b.Value),
            (StrName, NumName) => 1,
            _ => -1,
        };
    }

    public static bool operator ==(Name? a, Name? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(Name? a, Name? b) => !(a == b);

    public override string ToString()
    {
        if (IsAnonymous)
        {
            return "[anonymous]";
        }
        var parts = new List<string>();
        for (Name n = this; !n.IsAnonymous; n = n.Prefix)
        {
            parts.Add(n switch
            {
                StrName s => Escape(s.Value),
                NumName num => num.Value.ToString(),
                _ => throw new InvalidOperationException(),
            });
        }
        parts.Reverse();
        return string.Join('.', parts);
    }

    private static string Escape(string s)
    {
        if (s.Length == 0)
        {
            return "«»";
        }
        bool plain = IsIdFirst(s[0]);
        for (int i = 1; plain && i < s.Length; i++)
        {
            plain = IsIdRest(s[i]);
        }
        return plain ? s : "«" + s + "»";
    }

    private static bool IsIdFirst(char c) => char.IsLetter(c) || c == '_' || IsLetterLike(c);
    private static bool IsIdRest(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '\'' || c == '!' || c == '?' || IsLetterLike(c) || IsSubscript(c);
    private static bool IsLetterLike(char c) =>
        (0x3b1 <= c && c <= 0x3c9 && c != 0x3bb) || (0x391 <= c && c <= 0x3A9 && c != 0x3A0 && c != 0x3A3) ||
        (0x3ca <= c && c <= 0x3fb) || (0x1f00 <= c && c <= 0x1ffe) || (0x2100 <= c && c <= 0x214f) || (0x1d49c <= c && c <= 0x1d59f);
    private static bool IsSubscript(char c) => (0x2080 <= c && c <= 0x2089) || (0x2090 <= c && c <= 0x209c) || (0x1d62 <= c && c <= 0x1d6a);

    private sealed class AnonymousName : Name
    {
        public AnonymousName() : base(0x5eed) { }
        public override Name Prefix => this;
    }

    private sealed class StrName : Name
    {
        public readonly Name Pre;
        public readonly string Value;
        public StrName(Name pre, string value) : base(HashCode.Combine(1, pre._hash, value)) { Pre = pre; Value = value; }
        public override Name Prefix => Pre;
    }

    private sealed class NumName : Name
    {
        public readonly Name Pre;
        public readonly ulong Value;
        public NumName(Name pre, ulong value) : base(HashCode.Combine(2, pre._hash, value)) { Pre = pre; Value = value; }
        public override Name Prefix => Pre;
    }

    /// <summary>Deconstruct a string-component name.</summary>
    public bool TryGetStr([NotNullWhen(true)] out Name? prefix, [NotNullWhen(true)] out string? value)
    {
        if (this is StrName s)
        {
            prefix = s.Pre;
            value = s.Value;
            return true;
        }
        prefix = null;
        value = null;
        return false;
    }

    /// <summary>Deconstruct a numeric-component name.</summary>
    public bool TryGetNum([NotNullWhen(true)] out Name? prefix, out ulong value)
    {
        if (this is NumName n)
        {
            prefix = n.Pre;
            value = n.Value;
            return true;
        }
        prefix = null;
        value = 0;
        return false;
    }

    /// <summary>Compare two arrays of names element-wise.</summary>
    public static bool ListEquals(IReadOnlyList<Name> a, IReadOnlyList<Name> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (!a[i].Equals(b[i]))
            {
                return false;
            }
        }
        return true;
    }
}
