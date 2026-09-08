using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Tenet.Kernel;

namespace Tenet.Export;

/// <summary>Parse error in an export file, with the line number.</summary>
public sealed class ExportFormatException : Exception
{
    public long Line { get; }
    public ExportFormatException(long line, string message) : base($"line {line}: {message}") => Line = line;
}

/// <summary>
/// Reader for the Lean 4 NDJSON export format (lean4export format 3.x): a <c>meta</c> line, then a stream of names,
/// levels, expressions, and declarations, each object referring to earlier ones by table index.
/// </summary>
public static class NdjsonReader
{
    public static readonly string[] SupportedFormatMajors = ["3"];

    public static ExportFile ReadFile(string path)
    {
        using FileStream fs = File.OpenRead(path);
        return Read(fs);
    }

    public static ExportFile Read(Stream stream)
    {
        var file = new ExportFile();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);
        long lineNo = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            if (line.Length == 0 || line.AsSpan().IsWhiteSpace())
            {
                continue;
            }
            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                ParseLine(file, doc.RootElement, lineNo);
            }
            catch (JsonException e)
            {
                throw new ExportFormatException(lineNo, "malformed JSON: " + e.Message);
            }
            catch (KernelException e)
            {
                throw new ExportFormatException(lineNo, e.Message);
            }
            catch (KeyNotFoundException e)
            {
                throw new ExportFormatException(lineNo, "missing field: " + e.Message);
            }
            catch (InvalidOperationException e)
            {
                throw new ExportFormatException(lineNo, "unexpected value: " + e.Message);
            }
        }
        return file;
    }

    private static void ParseLine(ExportFile file, JsonElement root, long lineNo)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ExportFormatException(lineNo, "expected a JSON object");
        }
        if (root.TryGetProperty("meta", out JsonElement meta))
        {
            ParseMeta(file, meta, lineNo);
            return;
        }
        if (root.TryGetProperty("in", out JsonElement inIdx))
        {
            AddAt(file.Names, inIdx.GetInt32(), ParseName(file, root, lineNo), lineNo, "name");
            return;
        }
        if (root.TryGetProperty("il", out JsonElement ilIdx))
        {
            AddAt(file.Levels, ilIdx.GetInt32(), ParseLevel(file, root, lineNo), lineNo, "level");
            return;
        }
        if (root.TryGetProperty("ie", out JsonElement ieIdx))
        {
            AddAt(file.Exprs, ieIdx.GetInt32(), ParseExpr(file, root, lineNo), lineNo, "expression");
            return;
        }
        file.Decls.Add(ParseDecl(file, root, lineNo));
    }

    private static void AddAt<T>(List<T> table, int idx, T item, long lineNo, string what)
    {
        if (idx != table.Count)
        {
            throw new ExportFormatException(lineNo, $"{what} index {idx} is out of sequence (expected {table.Count})");
        }
        table.Add(item);
    }

    private static void ParseMeta(ExportFile file, JsonElement meta, long lineNo)
    {
        string exporterName = meta.GetProperty("exporter").GetProperty("name").GetString() ?? "";
        string exporterVersion = meta.GetProperty("exporter").GetProperty("version").GetString() ?? "";
        string leanVersion = meta.GetProperty("lean").GetProperty("version").GetString() ?? "";
        string leanHash = meta.GetProperty("lean").GetProperty("githash").GetString() ?? "";
        string format = meta.GetProperty("format").GetProperty("version").GetString() ?? "";
        string major = format.Split('.')[0];
        if (Array.IndexOf(SupportedFormatMajors, major) < 0)
        {
            throw new ExportFormatException(lineNo, $"unsupported export format version {format} (supported: {string.Join(", ", SupportedFormatMajors.Select(m => m + ".x"))})");
        }
        file.Meta = new ExportMeta(exporterName, exporterVersion, leanVersion, leanHash, format);
    }

    // ---- tables ----

    private static Name NameAt(ExportFile file, JsonElement idx, long lineNo)
    {
        int i = idx.GetInt32();
        if (i < 0 || i >= file.Names.Count)
        {
            throw new ExportFormatException(lineNo, $"reference to undefined name {i}");
        }
        return file.Names[i];
    }

    private static Level LevelAt(ExportFile file, JsonElement idx, long lineNo)
    {
        int i = idx.GetInt32();
        if (i < 0 || i >= file.Levels.Count)
        {
            throw new ExportFormatException(lineNo, $"reference to undefined level {i}");
        }
        return file.Levels[i];
    }

    private static Expr ExprAt(ExportFile file, JsonElement idx, long lineNo)
    {
        int i = idx.GetInt32();
        if (i < 0 || i >= file.Exprs.Count)
        {
            throw new ExportFormatException(lineNo, $"reference to undefined expression {i}");
        }
        return file.Exprs[i];
    }

    private static Name[] NamesAt(ExportFile file, JsonElement arr, long lineNo)
    {
        var r = new Name[arr.GetArrayLength()];
        int k = 0;
        foreach (JsonElement e in arr.EnumerateArray())
        {
            r[k++] = NameAt(file, e, lineNo);
        }
        return r;
    }

    private static Level[] LevelsAt(ExportFile file, JsonElement arr, long lineNo)
    {
        var r = new Level[arr.GetArrayLength()];
        int k = 0;
        foreach (JsonElement e in arr.EnumerateArray())
        {
            r[k++] = LevelAt(file, e, lineNo);
        }
        return r;
    }

    private static Name ParseName(ExportFile file, JsonElement root, long lineNo)
    {
        if (root.TryGetProperty("str", out JsonElement s))
        {
            return NameAt(file, s.GetProperty("pre"), lineNo).Str(s.GetProperty("str").GetString() ?? "");
        }
        if (root.TryGetProperty("num", out JsonElement n))
        {
            return NameAt(file, n.GetProperty("pre"), lineNo).Num(n.GetProperty("i").GetUInt64());
        }
        throw new ExportFormatException(lineNo, "unknown name constructor");
    }

    private static Level ParseLevel(ExportFile file, JsonElement root, long lineNo)
    {
        if (root.TryGetProperty("succ", out JsonElement s))
        {
            return Level.Succ(LevelAt(file, s, lineNo));
        }
        if (root.TryGetProperty("max", out JsonElement m))
        {
            return Level.MaxRaw(LevelAt(file, m[0], lineNo), LevelAt(file, m[1], lineNo));
        }
        if (root.TryGetProperty("imax", out JsonElement im))
        {
            return Level.IMaxRaw(LevelAt(file, im[0], lineNo), LevelAt(file, im[1], lineNo));
        }
        if (root.TryGetProperty("param", out JsonElement p))
        {
            return Level.Param(NameAt(file, p, lineNo));
        }
        throw new ExportFormatException(lineNo, "unknown level constructor");
    }

    private static BinderInfo ParseBinderInfo(JsonElement e, long lineNo) => e.GetString() switch
    {
        "default" => BinderInfo.Default,
        "implicit" => BinderInfo.Implicit,
        "strictImplicit" => BinderInfo.StrictImplicit,
        "instImplicit" => BinderInfo.InstImplicit,
        string other => throw new ExportFormatException(lineNo, $"unknown binderInfo '{other}'"),
        null => throw new ExportFormatException(lineNo, "missing binderInfo"),
    };

    private static Expr ParseExpr(ExportFile file, JsonElement root, long lineNo)
    {
        if (root.TryGetProperty("bvar", out JsonElement bv))
        {
            return Expr.BVar(bv.GetInt32());
        }
        if (root.TryGetProperty("sort", out JsonElement so))
        {
            return Expr.Sort(LevelAt(file, so, lineNo));
        }
        if (root.TryGetProperty("const", out JsonElement c))
        {
            return Expr.Const(NameAt(file, c.GetProperty("name"), lineNo), LevelsAt(file, c.GetProperty("us"), lineNo));
        }
        if (root.TryGetProperty("app", out JsonElement a))
        {
            return Expr.App(ExprAt(file, a.GetProperty("fn"), lineNo), ExprAt(file, a.GetProperty("arg"), lineNo));
        }
        if (root.TryGetProperty("lam", out JsonElement lam))
        {
            return Expr.Lam(NameAt(file, lam.GetProperty("name"), lineNo), ExprAt(file, lam.GetProperty("type"), lineNo),
                            ExprAt(file, lam.GetProperty("body"), lineNo), ParseBinderInfo(lam.GetProperty("binderInfo"), lineNo));
        }
        if (root.TryGetProperty("forallE", out JsonElement pi))
        {
            return Expr.Pi(NameAt(file, pi.GetProperty("name"), lineNo), ExprAt(file, pi.GetProperty("type"), lineNo),
                           ExprAt(file, pi.GetProperty("body"), lineNo), ParseBinderInfo(pi.GetProperty("binderInfo"), lineNo));
        }
        if (root.TryGetProperty("letE", out JsonElement let))
        {
            bool nondep = let.TryGetProperty("nondep", out JsonElement nd) && nd.GetBoolean();
            return Expr.Let(NameAt(file, let.GetProperty("name"), lineNo), ExprAt(file, let.GetProperty("type"), lineNo),
                            ExprAt(file, let.GetProperty("value"), lineNo), ExprAt(file, let.GetProperty("body"), lineNo), nondep);
        }
        if (root.TryGetProperty("proj", out JsonElement pr))
        {
            return Expr.Proj(NameAt(file, pr.GetProperty("typeName"), lineNo), pr.GetProperty("idx").GetInt32(), ExprAt(file, pr.GetProperty("struct"), lineNo));
        }
        if (root.TryGetProperty("natVal", out JsonElement nv))
        {
            string s = nv.GetString() ?? throw new ExportFormatException(lineNo, "natVal must be a string");
            if (!BigInteger.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out BigInteger n))
            {
                throw new ExportFormatException(lineNo, $"invalid natVal '{s}'");
            }
            return Expr.NatLit(n);
        }
        if (root.TryGetProperty("strVal", out JsonElement sv))
        {
            return Expr.StrLit(sv.GetString() ?? throw new ExportFormatException(lineNo, "strVal must be a string"));
        }
        if (root.TryGetProperty("mdata", out JsonElement md))
        {
            // Metadata has no effect on type checking; drop it.
            return ExprAt(file, md.GetProperty("expr"), lineNo);
        }
        throw new ExportFormatException(lineNo, "unknown expression constructor");
    }

    // ---- declarations ----

    private static ReducibilityHints ParseHints(JsonElement h, long lineNo)
    {
        if (h.ValueKind == JsonValueKind.String)
        {
            return h.GetString() switch
            {
                "opaque" => ReducibilityHints.Opaque,
                "abbrev" => ReducibilityHints.Abbrev,
                string other => throw new ExportFormatException(lineNo, $"unknown hints '{other}'"),
                null => throw new ExportFormatException(lineNo, "missing hints"),
            };
        }
        if (h.ValueKind == JsonValueKind.Object && h.TryGetProperty("regular", out JsonElement r))
        {
            return ReducibilityHints.Regular(r.GetUInt32());
        }
        throw new ExportFormatException(lineNo, "malformed hints");
    }

    private static DefinitionSafety ParseSafety(JsonElement s, long lineNo) => s.GetString() switch
    {
        "safe" => DefinitionSafety.Safe,
        "unsafe" => DefinitionSafety.Unsafe,
        "partial" => DefinitionSafety.Partial,
        string other => throw new ExportFormatException(lineNo, $"unknown safety '{other}'"),
        null => throw new ExportFormatException(lineNo, "missing safety"),
    };

    private static QuotKind ParseQuotKind(JsonElement k, long lineNo) => k.GetString() switch
    {
        "type" => QuotKind.Type,
        "ctor" => QuotKind.Ctor,
        "lift" => QuotKind.Lift,
        "ind" => QuotKind.Ind,
        string other => throw new ExportFormatException(lineNo, $"unknown quot kind '{other}'"),
        null => throw new ExportFormatException(lineNo, "missing quot kind"),
    };

    private static bool Bool(JsonElement o, string prop) => o.TryGetProperty(prop, out JsonElement e) && e.GetBoolean();

    private static ExportDecl ParseDecl(ExportFile file, JsonElement root, long lineNo)
    {
        // Format 3.0 wrote `def`, `thm`, `opaque`, and `axiom` as one-element arrays (a mutual block);
        // format 3.1 writes an object carrying an `all` field. Accept both.
        if (root.TryGetProperty("axiom", out JsonElement ax))
        {
            return Single(ax, lineNo, "axiom", a => new ExportAxiom(NameAt(file, a.GetProperty("name"), lineNo), NamesAt(file, a.GetProperty("levelParams"), lineNo),
                                   ExprAt(file, a.GetProperty("type"), lineNo), Bool(a, "isUnsafe")));
        }
        if (root.TryGetProperty("def", out JsonElement def))
        {
            if (def.ValueKind == JsonValueKind.Array && def.GetArrayLength() != 1)
            {
                var defs = new List<ExportDefinition>();
                foreach (JsonElement d in def.EnumerateArray())
                {
                    defs.Add(ParseDefinition(file, d, lineNo));
                }
                return new ExportMutualDefinition(defs.ToArray());
            }
            return Single(def, lineNo, "def", d => ParseDefinition(file, d, lineNo));
        }
        if (root.TryGetProperty("opaque", out JsonElement op))
        {
            return Single(op, lineNo, "opaque", o =>
            {
                Name name = NameAt(file, o.GetProperty("name"), lineNo);
                return new ExportOpaque(name, NamesAt(file, o.GetProperty("levelParams"), lineNo), ExprAt(file, o.GetProperty("type"), lineNo),
                                        ExprAt(file, o.GetProperty("value"), lineNo), Bool(o, "isUnsafe"), AllOr(file, o, name, lineNo));
            });
        }
        if (root.TryGetProperty("thm", out JsonElement thm))
        {
            return Single(thm, lineNo, "thm", t =>
            {
                Name name = NameAt(file, t.GetProperty("name"), lineNo);
                return new ExportTheorem(name, NamesAt(file, t.GetProperty("levelParams"), lineNo), ExprAt(file, t.GetProperty("type"), lineNo),
                                         ExprAt(file, t.GetProperty("value"), lineNo), AllOr(file, t, name, lineNo));
            });
        }
        if (root.TryGetProperty("quot", out JsonElement q))
        {
            return Single(q, lineNo, "quot", qq => new ExportQuot(NameAt(file, qq.GetProperty("name"), lineNo), NamesAt(file, qq.GetProperty("levelParams"), lineNo),
                                  ExprAt(file, qq.GetProperty("type"), lineNo), ParseQuotKind(qq.GetProperty("kind"), lineNo)));
        }
        if (root.TryGetProperty("inductive", out JsonElement ind))
        {
            var types = new List<ExportInductiveVal>();
            foreach (JsonElement t in Either(ind, "types", "inductiveVals", lineNo).EnumerateArray())
            {
                types.Add(new ExportInductiveVal(NameAt(file, t.GetProperty("name"), lineNo), NamesAt(file, t.GetProperty("levelParams"), lineNo),
                    ExprAt(file, t.GetProperty("type"), lineNo), t.GetProperty("numParams").GetInt32(), t.GetProperty("numIndices").GetInt32(),
                    NamesAt(file, t.GetProperty("all"), lineNo), NamesAt(file, t.GetProperty("ctors"), lineNo),
                    t.TryGetProperty("numNested", out JsonElement nn) ? nn.GetInt32() : 0, Bool(t, "isRec"), Bool(t, "isUnsafe"), Bool(t, "isReflexive")));
            }
            var ctors = new List<ExportConstructorVal>();
            foreach (JsonElement c in Either(ind, "ctors", "constructorVals", lineNo).EnumerateArray())
            {
                ctors.Add(new ExportConstructorVal(NameAt(file, c.GetProperty("name"), lineNo), NamesAt(file, c.GetProperty("levelParams"), lineNo),
                    ExprAt(file, c.GetProperty("type"), lineNo), NameAt(file, c.GetProperty("induct"), lineNo), c.GetProperty("cidx").GetInt32(),
                    c.GetProperty("numParams").GetInt32(), c.GetProperty("numFields").GetInt32(), Bool(c, "isUnsafe")));
            }
            var recs = new List<ExportRecursorVal>();
            foreach (JsonElement r in Either(ind, "recs", "recursorVals", lineNo).EnumerateArray())
            {
                var rules = new List<ExportRecursorRule>();
                foreach (JsonElement rule in r.GetProperty("rules").EnumerateArray())
                {
                    rules.Add(new ExportRecursorRule(NameAt(file, rule.GetProperty("ctor"), lineNo), rule.GetProperty("nfields").GetInt32(), ExprAt(file, rule.GetProperty("rhs"), lineNo)));
                }
                recs.Add(new ExportRecursorVal(NameAt(file, r.GetProperty("name"), lineNo), NamesAt(file, r.GetProperty("levelParams"), lineNo),
                    ExprAt(file, r.GetProperty("type"), lineNo), NamesAt(file, r.GetProperty("all"), lineNo), r.GetProperty("numParams").GetInt32(),
                    r.GetProperty("numIndices").GetInt32(), r.GetProperty("numMotives").GetInt32(), r.GetProperty("numMinors").GetInt32(),
                    rules.ToArray(), Bool(r, "k"), Bool(r, "isUnsafe")));
            }
            return new ExportInductive(types.ToArray(), ctors.ToArray(), recs.ToArray());
        }
        throw new ExportFormatException(lineNo, "unknown declaration kind: " + string.Join(", ", root.EnumerateObject().Select(p => p.Name)));
    }

    private static ExportDefinition ParseDefinition(ExportFile file, JsonElement d, long lineNo)
    {
        Name name = NameAt(file, d.GetProperty("name"), lineNo);
        return new ExportDefinition(name, NamesAt(file, d.GetProperty("levelParams"), lineNo), ExprAt(file, d.GetProperty("type"), lineNo),
                                    ExprAt(file, d.GetProperty("value"), lineNo), ParseHints(d.GetProperty("hints"), lineNo),
                                    ParseSafety(d.GetProperty("safety"), lineNo), AllOr(file, d, name, lineNo));
    }

    private static ExportDecl Single(JsonElement e, long lineNo, string kind, Func<JsonElement, ExportDecl> parse)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            return parse(e);
        }
        if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() == 1)
        {
            return parse(e[0]);
        }
        throw new ExportFormatException(lineNo, $"'{kind}' must be an object or a one-element array");
    }

    /// <summary>Older exporters spelled the inductive block's fields differently; accept both.</summary>
    private static JsonElement Either(JsonElement o, string name, string alt, long lineNo)
    {
        if (o.TryGetProperty(name, out JsonElement e) || o.TryGetProperty(alt, out e))
        {
            return e;
        }
        throw new ExportFormatException(lineNo, $"inductive block is missing '{name}'");
    }

    private static Name[] AllOr(ExportFile file, JsonElement o, Name name, long lineNo) =>
        o.TryGetProperty("all", out JsonElement all) ? NamesAt(file, all, lineNo) : [name];
}
