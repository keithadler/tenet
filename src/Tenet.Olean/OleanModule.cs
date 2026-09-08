using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Text;
using Tenet.Kernel;

namespace Tenet.Olean;

/// <summary>An <c>import</c> recorded in a module.</summary>
public sealed record Import(Name Module, bool ImportAll, bool IsExported, bool IsMeta);

/// <summary>One memory-mapped part of a module: <c>.olean</c>, <c>.olean.private</c>, or <c>.olean.server</c>.</summary>
internal sealed unsafe class Region : IDisposable
{
    public const int HeaderSize = 5 + 1 + 1 + 33 + 40 + 8;

    private readonly MemoryMappedViewAccessor _view;
    public readonly byte* Base;
    public readonly long Length;
    public readonly ulong BaseAddr;
    public readonly bool Gmp;
    public readonly int FormatVersion;
    public readonly string LeanVersion;
    public readonly string GitHash;
    public readonly ulong RootAddr;
    public readonly string Path;

    public Region(string path)
    {
        Path = path;
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("no such .olean file", path);
        }
        Length = info.Length;
        if (Length < HeaderSize + 8)
        {
            throw new OleanFormatException(path, "file is too small to be an .olean");
        }
        // The mapping outlives the file handle; closing the handle at once keeps thousands of modules under the
        // process's open-file limit.
        using (MemoryMappedFile file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read))
        {
            _view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        }
        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        Base = p;
        if (!(p[0] == 'o' && p[1] == 'l' && p[2] == 'e' && p[3] == 'a' && p[4] == 'n'))
        {
            throw new OleanFormatException(path, "missing 'olean' marker");
        }
        FormatVersion = p[5];
        Gmp = (p[6] & 1) != 0;
        LeanVersion = ReadFixedString(7, 33);
        GitHash = ReadFixedString(40, 40);
        BaseAddr = *(ulong*)(p + 80);
        long dataStart = FormatVersion switch
        {
            2 => HeaderSize,
            3 => HeaderSize + 8, // a data_size word precedes the data
            _ => throw new OleanFormatException(path, $"unsupported .olean format version {FormatVersion} (Tenet reads 2 and 3)"),
        };
        RootAddr = *(ulong*)(p + dataStart);
    }

    public bool Contains(ulong addr) => addr >= BaseAddr && addr - BaseAddr < (ulong)Length;

    private string ReadFixedString(int offset, int len)
    {
        int end = offset;
        while (end < offset + len && Base[end] != 0)
        {
            end++;
        }
        return Encoding.ASCII.GetString(Base + offset, end - offset);
    }

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
    }
}

/// <summary>
/// One compiled Lean module: its <c>.olean</c> plus, when present, the <c>.olean.private</c> and <c>.olean.server</c>
/// parts, memory-mapped. Lean writes each part as a compacted graph of runtime objects at a fixed base address, and
/// later parts point into earlier ones, so all parts are mapped together and pointers are resolved across them. This
/// class walks the graph and decodes names, universe levels, expressions, and constants into kernel objects on demand,
/// caching every decoded object by address so the sharing of the original DAG is preserved.
///
/// Under Lean's module system the public part stores theorems without their proofs (as axioms); the private part has
/// the full constants. The module presents the merged view, with the private part taking precedence.
/// </summary>
public sealed unsafe class OleanModule : IDisposable
{
    private const int TagArray = 246;
    private const int TagString = 249;
    private const int TagMpz = 250;
    private const int TagMaxCtor = 243;

    private readonly List<Region> _regions = new();
    // Decoded objects by address. Concurrent: two workers may decode the same object at once and both results are
    // equal, so whichever lands is fine. Cleared by TrimCaches to bound memory.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, Name> _names = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, Level> _levels = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, Expr> _exprs = new();

    // merged constant table: name -> address of its ConstantInfo object (private part wins)
    private readonly Name[] _constNames;
    private readonly Dictionary<Name, ulong> _constAddr;

    public string Path { get; }
    public IReadOnlyList<string> PartPaths => _regions.Select(r => r.Path).ToList();
    public int FormatVersion => _regions[0].FormatVersion;
    public string LeanVersion => _regions[0].LeanVersion;
    public string GitHash => _regions[0].GitHash;
    public bool IsModule { get; }
    public Import[] Imports { get; }
    /// <summary>Number of constants that came from the public part, before merging the private part.</summary>
    public int PublicConstantCount { get; }

    /// <summary>Names of all constants of the module (public and private parts merged), public part's order first.</summary>
    public IReadOnlyList<Name> ConstantNames => _constNames;

    /// <summary>Open a module from its <c>.olean</c> path; sibling <c>.olean.private</c> and <c>.olean.server</c> parts are mapped too.</summary>
    public OleanModule(string path, bool includePrivate = true)
    {
        Path = path;
        _regions.Add(new Region(path));
        string server = path + ".server";
        string priv = path + ".private";
        // later parts may point into earlier ones; Lean saves the parts in this order
        if (File.Exists(server))
        {
            _regions.Add(new Region(server));
        }
        if (includePrivate && File.Exists(priv))
        {
            _regions.Add(new Region(priv));
        }
        foreach (Region r in _regions.Skip(1))
        {
            if (r.FormatVersion != _regions[0].FormatVersion || r.GitHash != _regions[0].GitHash)
            {
                throw new OleanFormatException(r.Path, "part was produced by a different Lean build than the .olean");
            }
        }

        var names = new List<Name>();
        var addr = new Dictionary<Name, ulong>();
        var imports = new List<Import>();
        bool isModule = false;
        int publicCount = 0;
        foreach (Region region in _regions)
        {
            if (region.Path.EndsWith(".server", StringComparison.Ordinal))
            {
                continue; // language-server data: no constants we need, mapped only for pointer resolution
            }
            ulong root = region.RootAddr;
            if (Tag(root) > TagMaxCtor || NumObjs(root) < 3)
            {
                throw new OleanFormatException(region.Path, "root object is not ModuleData");
            }
            if (ReferenceEquals(region, _regions[0]))
            {
                isModule = NumObjs(root) >= 5 && ScalarU8(root, 0) != 0;
            }
            ulong importsArr = Ptr(Field(root, 0));
            for (long i = 0; i < ArrayLength(importsArr); i++)
            {
                ulong imp = Ptr(ArrayElement(importsArr, i));
                var im = new Import(DecodeName(Field(imp, 0)), ScalarU8(imp, 0) != 0, ScalarU8(imp, 1) != 0, ScalarU8(imp, 2) != 0);
                if (!imports.Any(x => x.Module.Equals(im.Module)))
                {
                    imports.Add(im);
                }
            }
            ulong constNamesArr = Ptr(Field(root, 1));
            ulong constantsArr = Ptr(Field(root, 2));
            long n = ArrayLength(constNamesArr);
            if (ArrayLength(constantsArr) != n)
            {
                throw new OleanFormatException(region.Path, "constNames and constants arrays differ in length");
            }
            for (long i = 0; i < n; i++)
            {
                Name cn = DecodeName(ArrayElement(constNamesArr, i));
                if (!addr.ContainsKey(cn))
                {
                    names.Add(cn);
                }
                addr[cn] = ArrayElement(constantsArr, i); // a later (private) part overrides
            }
            if (ReferenceEquals(region, _regions[0]))
            {
                publicCount = (int)n;
            }
        }
        _constNames = names.ToArray();
        _constAddr = addr;
        Imports = imports.ToArray();
        IsModule = isModule;
        PublicConstantCount = publicCount;
    }

    public void Dispose()
    {
        foreach (Region r in _regions)
        {
            r.Dispose();
        }
    }

    public bool Contains(Name n) => _constAddr.ContainsKey(n);

    /// <summary>Decode the constant with this name, or null if the module does not store it.</summary>
    public ConstantInfo? FindConstant(Name n)
    {
        if (!_constAddr.TryGetValue(n, out ulong a))
        {
            return null;
        }
        return DecodeConstantInfo(Ptr(a));
    }

    /// <summary>Drop the decoded-object caches (the constant name table stays). Objects are decoded again when needed.</summary>
    public void TrimCaches()
    {
        _names.Clear();
        _levels.Clear();
        _exprs.Clear();
    }

    /// <summary>Decode every constant of the module (merged view).</summary>
    public IEnumerable<ConstantInfo> DecodeAll()
    {
        foreach (Name n in _constNames)
        {
            yield return FindConstant(n)!;
        }
    }

    // ------------------------------------------------------------------ raw object access

    private static bool IsScalar(ulong v) => (v & 1) == 1;
    private static ulong Unbox(ulong v) => v >> 1;

    /// <summary>The byte address of an object given its saved pointer value, across all mapped parts.</summary>
    private byte* At(ulong addr)
    {
        foreach (Region r in _regions)
        {
            if (r.Contains(addr))
            {
                return r.Base + (addr - r.BaseAddr);
            }
        }
        throw new OleanFormatException(Path, $"pointer 0x{addr:x} points outside the module's parts ({string.Join(", ", _regions.Select(r => $"0x{r.BaseAddr:x}+{r.Length}"))})");
    }

    private bool Gmp => _regions[0].Gmp;

    /// <summary>Validate that a pointer field value is an object pointer (not a boxed scalar) and return it.</summary>
    private ulong Ptr(ulong v)
    {
        if (IsScalar(v))
        {
            throw new OleanFormatException(Path, "expected an object pointer but found a boxed scalar");
        }
        At(v);
        return v;
    }

    private byte Tag(ulong a) => At(a)[7];
    private int NumObjs(ulong a) => At(a)[6];
    private ulong Field(ulong a, int i) => *(ulong*)(At(a) + 8 + 8L * i);
    private byte* ScalarBase(ulong a) => At(a) + 8 + 8L * NumObjs(a);
    private byte ScalarU8(ulong a, int byteOffset) => ScalarBase(a)[byteOffset];
    private uint ScalarU32(ulong a, int byteOffset) => *(uint*)(ScalarBase(a) + byteOffset);

    private long ArrayLength(ulong a)
    {
        if (Tag(a) != TagArray)
        {
            throw new OleanFormatException(Path, $"expected an Array object at 0x{a:x}, found tag {Tag(a)}");
        }
        return (long)*(ulong*)(At(a) + 8);
    }

    private ulong ArrayElement(ulong a, long i) => *(ulong*)(At(a) + 24 + 8 * i);

    private string DecodeString(ulong v)
    {
        ulong a = Ptr(v);
        if (Tag(a) != TagString)
        {
            throw new OleanFormatException(Path, $"expected a String object at 0x{a:x}, found tag {Tag(a)}");
        }
        byte* p = At(a);
        long size = (long)*(ulong*)(p + 8); // includes the NUL terminator
        return Encoding.UTF8.GetString(p + 32, (int)(size - 1));
    }

    private BigInteger DecodeNat(ulong v)
    {
        if (IsScalar(v))
        {
            return Unbox(v);
        }
        ulong a = Ptr(v);
        if (Tag(a) != TagMpz)
        {
            throw new OleanFormatException(Path, $"expected a Nat at 0x{a:x}, found tag {Tag(a)}");
        }
        byte* p = At(a);
        if (Gmp)
        {
            // __mpz_struct { int alloc; int size; limb* d } then the 64-bit limbs follow the struct
            int size = *(int*)(p + 12);
            byte* limbs = At(*(ulong*)(p + 16));
            var bytes = new byte[Math.Abs(size) * 8 + 1];
            new ReadOnlySpan<byte>(limbs, Math.Abs(size) * 8).CopyTo(bytes);
            var r = new BigInteger(bytes);
            return size < 0 ? -r : r;
        }
        else
        {
            // mpz { bool sign; size_t size; digit* digits } with 32-bit digits following the struct
            bool sign = p[8] != 0;
            long size = (long)*(ulong*)(p + 16);
            byte* digits = At(*(ulong*)(p + 24));
            var bytes = new byte[size * 4 + 1];
            new ReadOnlySpan<byte>(digits, (int)(size * 4)).CopyTo(bytes);
            var r = new BigInteger(bytes);
            return sign ? -r : r;
        }
    }

    private int DecodeSmallNat(ulong v, string what)
    {
        BigInteger n = DecodeNat(v);
        if (n < 0 || n > int.MaxValue)
        {
            throw new OleanFormatException(Path, $"{what} {n} is out of range");
        }
        return (int)n;
    }

    private List<T> DecodeList<T>(ulong v, Func<ulong, T> elem)
    {
        var r = new List<T>();
        while (!IsScalar(v))
        {
            r.Add(elem(Field(v, 0)));
            v = Field(v, 1);
        }
        return r;
    }

    // ------------------------------------------------------------------ names, levels, expressions

    public Name DecodeName(ulong v)
    {
        if (IsScalar(v))
        {
            return Name.Anonymous;
        }
        ulong off = Ptr(v);
        if (_names.TryGetValue(off, out Name? cached))
        {
            return cached;
        }
        Name pre = DecodeName(Field(off, 0));
        Name r = Tag(off) switch
        {
            1 => pre.Str(DecodeString(Field(off, 1))),
            2 => pre.Num((ulong)DecodeNat(Field(off, 1))),
            _ => throw new OleanFormatException(Path, $"unexpected Name constructor tag {Tag(off)} at 0x{off:x}"),
        };
        _names[off] = r;
        return r;
    }

    private Level DecodeLevel(ulong v)
    {
        if (IsScalar(v))
        {
            return Unbox(v) == 0 ? Level.Zero : throw new OleanFormatException(Path, $"unexpected boxed Level {Unbox(v)}");
        }
        ulong off = Ptr(v);
        if (_levels.TryGetValue(off, out Level? cached))
        {
            return cached;
        }
        Level r = Tag(off) switch
        {
            0 => Level.Zero,
            1 => Level.Succ(DecodeLevel(Field(off, 0))),
            2 => Level.MaxRaw(DecodeLevel(Field(off, 0)), DecodeLevel(Field(off, 1))),
            3 => Level.IMaxRaw(DecodeLevel(Field(off, 0)), DecodeLevel(Field(off, 1))),
            4 => Level.Param(DecodeName(Field(off, 0))),
            5 => throw new OleanFormatException(Path, "universe metavariable in a stored constant"),
            _ => throw new OleanFormatException(Path, $"unexpected Level constructor tag {Tag(off)} at 0x{off:x}"),
        };
        _levels[off] = r;
        return r;
    }

    private Level[] DecodeLevels(ulong v)
    {
        List<Level> ls = DecodeList(v, DecodeLevel);
        return ls.Count == 0 ? [] : ls.ToArray();
    }

    private static BinderInfo ToBinderInfo(byte b) => b switch
    {
        0 => BinderInfo.Default,
        1 => BinderInfo.Implicit,
        2 => BinderInfo.StrictImplicit,
        3 => BinderInfo.InstImplicit,
        _ => throw new InvalidDataException($"unexpected BinderInfo {b}"),
    };

    private Expr DecodeExpr(ulong v)
    {
        ulong off = Ptr(v);
        if (_exprs.TryGetValue(off, out Expr? cached))
        {
            return cached;
        }
        // scalar area: Expr.Data (u64) first, then any u8 fields
        Expr r;
        switch (Tag(off))
        {
            case 0:
                r = Expr.BVar(DecodeSmallNat(Field(off, 0), "bound variable index"));
                break;
            case 1:
                throw new OleanFormatException(Path, "free variable in a stored constant");
            case 2:
                throw new OleanFormatException(Path, "metavariable in a stored constant");
            case 3:
                r = Expr.Sort(DecodeLevel(Field(off, 0)));
                break;
            case 4:
                r = Expr.Const(DecodeName(Field(off, 0)), DecodeLevels(Field(off, 1)));
                break;
            case 5:
                r = Expr.App(DecodeExpr(Field(off, 0)), DecodeExpr(Field(off, 1)));
                break;
            case 6:
                r = Expr.Lam(DecodeName(Field(off, 0)), DecodeExpr(Field(off, 1)), DecodeExpr(Field(off, 2)), ToBinderInfo(ScalarU8(off, 8)));
                break;
            case 7:
                r = Expr.Pi(DecodeName(Field(off, 0)), DecodeExpr(Field(off, 1)), DecodeExpr(Field(off, 2)), ToBinderInfo(ScalarU8(off, 8)));
                break;
            case 8:
                r = Expr.Let(DecodeName(Field(off, 0)), DecodeExpr(Field(off, 1)), DecodeExpr(Field(off, 2)), DecodeExpr(Field(off, 3)), ScalarU8(off, 8) != 0);
                break;
            case 9:
                {
                    ulong lit = Ptr(Field(off, 0));
                    r = Tag(lit) switch
                    {
                        0 => Expr.NatLit(DecodeNat(Field(lit, 0))),
                        1 => Expr.StrLit(DecodeString(Field(lit, 0))),
                        _ => throw new OleanFormatException(Path, $"unexpected Literal tag {Tag(lit)}"),
                    };
                    break;
                }
            case 10:
                // metadata has no effect on checking
                r = DecodeExpr(Field(off, 1));
                break;
            case 11:
                r = Expr.Proj(DecodeName(Field(off, 0)), DecodeSmallNat(Field(off, 1), "projection index"), DecodeExpr(Field(off, 2)));
                break;
            default:
                throw new OleanFormatException(Path, $"unexpected Expr constructor tag {Tag(off)} at 0x{off:x}");
        }
        _exprs[off] = r;
        return r;
    }

    // ------------------------------------------------------------------ constants

    private (Name Name, Name[] LevelParams, Expr Type) DecodeConstantVal(ulong v)
    {
        ulong off = Ptr(v);
        Name name = DecodeName(Field(off, 0));
        List<Name> lps = DecodeList(Field(off, 1), DecodeName);
        Expr type = DecodeExpr(Field(off, 2));
        return (name, lps.Count == 0 ? [] : lps.ToArray(), type);
    }

    private Name[] DecodeNames(ulong v)
    {
        List<Name> ns = DecodeList(v, DecodeName);
        return ns.Count == 0 ? [] : ns.ToArray();
    }

    private ReducibilityHints DecodeHints(ulong v)
    {
        if (IsScalar(v))
        {
            return Unbox(v) switch
            {
                0 => ReducibilityHints.Opaque,
                1 => ReducibilityHints.Abbrev,
                ulong other => throw new OleanFormatException(Path, $"unexpected ReducibilityHints {other}"),
            };
        }
        ulong off = Ptr(v);
        if (Tag(off) != 2)
        {
            throw new OleanFormatException(Path, $"unexpected ReducibilityHints tag {Tag(off)}");
        }
        return ReducibilityHints.Regular(ScalarU32(off, 0));
    }

    private ConstantInfo DecodeConstantInfo(ulong off)
    {
        int tag = Tag(off);
        ulong val = Ptr(Field(off, 0));
        var (name, lps, type) = DecodeConstantVal(Field(val, 0));
        switch (tag)
        {
            case 0: // axiomInfo: [cv] isUnsafe
                return new AxiomInfo(name, lps, type, ScalarU8(val, 0) != 0);
            case 1: // defnInfo: [cv, value, hints, all] safety
                {
                    Expr value = DecodeExpr(Field(val, 1));
                    ReducibilityHints hints = DecodeHints(Field(val, 2));
                    Name[] all = DecodeNames(Field(val, 3));
                    DefinitionSafety safety = ScalarU8(val, 0) switch
                    {
                        0 => DefinitionSafety.Unsafe,
                        1 => DefinitionSafety.Safe,
                        2 => DefinitionSafety.Partial,
                        byte other => throw new OleanFormatException(Path, $"unexpected DefinitionSafety {other}"),
                    };
                    return new DefinitionInfo(name, lps, type, value, hints, safety, all);
                }
            case 2: // thmInfo: [cv, value, all]
                return new TheoremInfo(name, lps, type, DecodeExpr(Field(val, 1)), DecodeNames(Field(val, 2)));
            case 3: // opaqueInfo: [cv, value, all] isUnsafe
                return new OpaqueInfo(name, lps, type, DecodeExpr(Field(val, 1)), ScalarU8(val, 0) != 0, DecodeNames(Field(val, 2)));
            case 4: // quotInfo: [cv] kind
                return new QuotInfo(name, lps, type, (QuotKind)ScalarU8(val, 0));
            case 5: // inductInfo: [cv, numParams, numIndices, all, ctors, numNested] isRec isUnsafe isReflexive
                return new InductiveInfo(name, lps, type,
                    DecodeSmallNat(Field(val, 1), "numParams"), DecodeSmallNat(Field(val, 2), "numIndices"),
                    DecodeNames(Field(val, 3)), DecodeNames(Field(val, 4)), DecodeSmallNat(Field(val, 5), "numNested"),
                    ScalarU8(val, 0) != 0, ScalarU8(val, 1) != 0, ScalarU8(val, 2) != 0);
            case 6: // ctorInfo: [cv, induct, cidx, numParams, numFields] isUnsafe
                return new ConstructorInfo(name, lps, type, DecodeName(Field(val, 1)),
                    DecodeSmallNat(Field(val, 2), "cidx"), DecodeSmallNat(Field(val, 3), "numParams"), DecodeSmallNat(Field(val, 4), "numFields"),
                    ScalarU8(val, 0) != 0);
            case 7: // recInfo: [cv, all, numParams, numIndices, numMotives, numMinors, rules] k isUnsafe
                {
                    Name[] all = DecodeNames(Field(val, 1));
                    var rules = DecodeList(Field(val, 6), rv =>
                    {
                        ulong r = Ptr(rv);
                        return new RecursorRule(DecodeName(Field(r, 0)), DecodeSmallNat(Field(r, 1), "nfields"), DecodeExpr(Field(r, 2)));
                    });
                    return new RecursorInfo(name, lps, type, all,
                        DecodeSmallNat(Field(val, 2), "numParams"), DecodeSmallNat(Field(val, 3), "numIndices"),
                        DecodeSmallNat(Field(val, 4), "numMotives"), DecodeSmallNat(Field(val, 5), "numMinors"),
                        rules.ToArray(), ScalarU8(val, 0) != 0, ScalarU8(val, 1) != 0);
                }
            default:
                throw new OleanFormatException(Path, $"unexpected ConstantInfo tag {tag} at 0x{off:x}");
        }
    }
}
