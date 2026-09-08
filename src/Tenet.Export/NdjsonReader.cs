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
        ReadStreaming(stream, file, d => file.Decls.Add(d));
        return file;
    }

    /// <summary>
    /// Parse the export, keeping the shared tables in <paramref name="file"/> and handing each declaration to
    /// <paramref name="onDecl"/> as soon as it is complete, instead of collecting them.
    /// </summary>
    public static void ReadStreaming(Stream stream, ExportFile file, Action<ExportDecl> onDecl)
    {
        // Read raw bytes and split on newlines; table entries take a forward-only Utf8JsonReader path,
        // declarations (about 1% of lines) go through JsonDocument.
        byte[] buffer = new byte[1 << 20];
        int start = 0;
        int end = 0;
        long lineNo = 0;
        bool first = true;
        while (true)
        {
            int read = stream.Read(buffer, end, buffer.Length - end);
            if (read == 0)
            {
                if (end > start)
                {
                    lineNo++;
                    HandleLine(file, buffer.AsSpan(start, end - start), lineNo, onDecl, ref first);
                }
                return;
            }
            end += read;
            while (true)
            {
                int nl = buffer.AsSpan(start, end - start).IndexOf((byte)'\n');
                if (nl < 0)
                {
                    break;
                }
                lineNo++;
                HandleLine(file, buffer.AsSpan(start, nl), lineNo, onDecl, ref first);
                start += nl + 1;
            }
            if (start == end)
            {
                start = end = 0;
            }
            else if (start > 0)
            {
                Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
                end -= start;
                start = 0;
            }
            if (end == buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
            }
        }
    }

    private static void HandleLine(ExportFile file, ReadOnlySpan<byte> line, long lineNo, Action<ExportDecl> onDecl, ref bool first)
    {
        if (first)
        {
            first = false;
            if (line.StartsWith("\xEF\xBB\xBF"u8))
            {
                line = line[3..];
            }
        }
        if (line.Length > 0 && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }
        if (line.IsEmpty || line.IndexOfAnyExcept((byte)' ', (byte)'\t') < 0)
        {
            return;
        }
        try
        {
            if (TryParseTableLine(file, line, lineNo))
            {
                return;
            }
            using JsonDocument doc = JsonDocument.Parse(line.ToArray());
            ExportDecl? decl = ParseLine(file, doc.RootElement, lineNo);
            if (decl is not null)
            {
                onDecl(decl);
            }
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

    // ---- fast path for names, levels, and expressions ----

    private static int ReadInt(ref Utf8JsonReader r, long lineNo)
    {
        if (!r.Read() || r.TokenType != JsonTokenType.Number)
        {
            throw new ExportFormatException(lineNo, "expected an integer");
        }
        return r.GetInt32();
    }

    private static int ReadIntProperty(ref Utf8JsonReader r, long lineNo, ReadOnlySpan<byte> name)
    {
        if (!r.Read() || r.TokenType != JsonTokenType.PropertyName || !r.ValueTextEquals(name))
        {
            return int.MinValue;
        }
        return ReadInt(ref r, lineNo);
    }

    /// <summary>Read a flat object of integer properties into <paramref name="values"/> by matching <paramref name="keys"/>; returns false on anything else.</summary>
    private static bool ReadIntObject(ref Utf8JsonReader r, long lineNo, scoped ReadOnlySpan<string> keys, scoped Span<int> values)
    {
        if (!r.Read() || r.TokenType != JsonTokenType.StartObject)
        {
            return false;
        }
        values.Fill(int.MinValue);
        while (r.Read())
        {
            if (r.TokenType == JsonTokenType.EndObject)
            {
                return true;
            }
            if (r.TokenType != JsonTokenType.PropertyName)
            {
                return false;
            }
            int k = -1;
            for (int i = 0; i < keys.Length; i++)
            {
                if (r.ValueTextEquals(keys[i]))
                {
                    k = i;
                    break;
                }
            }
            if (k < 0)
            {
                return false;
            }
            values[k] = ReadInt(ref r, lineNo);
        }
        return false;
    }

    private static readonly string[] AppKeys = ["fn", "arg"];
    private static readonly string[] ProjKeys = ["typeName", "idx", "struct"];

    /// <summary>
    /// Parse a table line (name, level, or expression). Returns false if the line is something else, in which
    /// case nothing has been added and the caller falls back to the DOM parser.
    /// </summary>
    private static bool TryParseTableLine(ExportFile file, ReadOnlySpan<byte> line, long lineNo)
    {
        var r = new Utf8JsonReader(line, isFinalBlock: true, default);
        if (!r.Read() || r.TokenType != JsonTokenType.StartObject)
        {
            return false;
        }
        int nameIdx = int.MinValue, levelIdx = int.MinValue, exprIdx = int.MinValue;
        Name? name = null;
        Level? level = null;
        Expr? expr = null;
        Span<int> ints = stackalloc int[5];
        while (r.Read())
        {
            if (r.TokenType == JsonTokenType.EndObject)
            {
                break;
            }
            if (r.TokenType != JsonTokenType.PropertyName)
            {
                return false;
            }
            if (r.ValueTextEquals("in"u8))
            {
                nameIdx = ReadInt(ref r, lineNo);
            }
            else if (r.ValueTextEquals("il"u8))
            {
                levelIdx = ReadInt(ref r, lineNo);
            }
            else if (r.ValueTextEquals("ie"u8))
            {
                exprIdx = ReadInt(ref r, lineNo);
            }
            else if (r.ValueTextEquals("str"u8))
            {
                // {"pre": int, "str": string} in either order
                if (!r.Read() || r.TokenType != JsonTokenType.StartObject)
                {
                    return false;
                }
                int pre = int.MinValue;
                string? str = null;
                while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                {
                    if (r.ValueTextEquals("pre"u8))
                    {
                        pre = ReadInt(ref r, lineNo);
                    }
                    else if (r.ValueTextEquals("str"u8))
                    {
                        r.Read();
                        str = r.GetString();
                    }
                    else
                    {
                        return false;
                    }
                }
                if (pre == int.MinValue || str is null)
                {
                    return false;
                }
                name = NameAt(file, pre, lineNo).Str(str);
            }
            else if (r.ValueTextEquals("num"u8))
            {
                if (!r.Read() || r.TokenType != JsonTokenType.StartObject)
                {
                    return false;
                }
                int pre = int.MinValue;
                ulong? num = null;
                while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                {
                    if (r.ValueTextEquals("pre"u8))
                    {
                        pre = ReadInt(ref r, lineNo);
                    }
                    else if (r.ValueTextEquals("i"u8))
                    {
                        r.Read();
                        num = r.GetUInt64();
                    }
                    else
                    {
                        return false;
                    }
                }
                if (pre == int.MinValue || num is null)
                {
                    return false;
                }
                name = NameAt(file, pre, lineNo).Num(num.Value);
            }
            else if (r.ValueTextEquals("succ"u8))
            {
                level = Level.Succ(LevelAt(file, ReadInt(ref r, lineNo), lineNo));
            }
            else if (r.ValueTextEquals("param"u8))
            {
                level = Level.Param(NameAt(file, ReadInt(ref r, lineNo), lineNo));
            }
            else if (r.ValueTextEquals("max"u8) || r.ValueTextEquals("imax"u8))
            {
                bool isMax = r.ValueTextEquals("max"u8);
                if (!r.Read() || r.TokenType != JsonTokenType.StartArray)
                {
                    return false;
                }
                Level a = LevelAt(file, ReadInt(ref r, lineNo), lineNo);
                Level b = LevelAt(file, ReadInt(ref r, lineNo), lineNo);
                if (!r.Read() || r.TokenType != JsonTokenType.EndArray)
                {
                    return false;
                }
                level = isMax ? Level.MaxRaw(a, b) : Level.IMaxRaw(a, b);
            }
            else if (r.ValueTextEquals("bvar"u8))
            {
                expr = Expr.BVar(ReadInt(ref r, lineNo));
            }
            else if (r.ValueTextEquals("sort"u8))
            {
                expr = Expr.Sort(LevelAt(file, ReadInt(ref r, lineNo), lineNo));
            }
            else if (r.ValueTextEquals("const"u8))
            {
                if (!r.Read() || r.TokenType != JsonTokenType.StartObject)
                {
                    return false;
                }
                Name? cname = null;
                Level[]? us = null;
                while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                {
                    if (r.ValueTextEquals("name"u8))
                    {
                        cname = NameAt(file, ReadInt(ref r, lineNo), lineNo);
                    }
                    else if (r.ValueTextEquals("us"u8))
                    {
                        if (!r.Read() || r.TokenType != JsonTokenType.StartArray)
                        {
                            return false;
                        }
                        var list = new List<Level>();
                        while (r.Read() && r.TokenType == JsonTokenType.Number)
                        {
                            list.Add(LevelAt(file, r.GetInt32(), lineNo));
                        }
                        if (r.TokenType != JsonTokenType.EndArray)
                        {
                            return false;
                        }
                        us = list.Count == 0 ? [] : list.ToArray();
                    }
                    else
                    {
                        return false;
                    }
                }
                if (cname is null || us is null)
                {
                    return false;
                }
                expr = Expr.Const(cname, us);
            }
            else if (r.ValueTextEquals("app"u8))
            {
                if (!ReadIntObject(ref r, lineNo, AppKeys, ints[..2]))
                {
                    return false;
                }
                expr = Expr.App(ExprAt(file, ints[0], lineNo), ExprAt(file, ints[1], lineNo));
            }
            else if (r.ValueTextEquals("lam"u8) || r.ValueTextEquals("forallE"u8))
            {
                bool isLam = r.ValueTextEquals("lam"u8);
                if (!r.Read() || r.TokenType != JsonTokenType.StartObject)
                {
                    return false;
                }
                int bn = int.MinValue, bt = int.MinValue, bb = int.MinValue;
                BinderInfo? bi = null;
                while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                {
                    if (r.ValueTextEquals("name"u8))
                    {
                        bn = ReadInt(ref r, lineNo);
                    }
                    else if (r.ValueTextEquals("type"u8))
                    {
                        bt = ReadInt(ref r, lineNo);
                    }
                    else if (r.ValueTextEquals("body"u8))
                    {
                        bb = ReadInt(ref r, lineNo);
                    }
                    else if (r.ValueTextEquals("binderInfo"u8))
                    {
                        r.Read();
                        bi = r.ValueTextEquals("default"u8) ? BinderInfo.Default
                           : r.ValueTextEquals("implicit"u8) ? BinderInfo.Implicit
                           : r.ValueTextEquals("strictImplicit"u8) ? BinderInfo.StrictImplicit
                           : r.ValueTextEquals("instImplicit"u8) ? BinderInfo.InstImplicit
                           : throw new ExportFormatException(lineNo, "unknown binderInfo");
                    }
                    else
                    {
                        return false;
                    }
                }
                if (bn == int.MinValue || bt == int.MinValue || bb == int.MinValue || bi is null)
                {
                    return false;
                }
                Name n = NameAt(file, bn, lineNo);
                Expr t = ExprAt(file, bt, lineNo);
                Expr b = ExprAt(file, bb, lineNo);
                expr = isLam ? Expr.Lam(n, t, b, bi.Value) : Expr.Pi(n, t, b, bi.Value);
            }
            else if (r.ValueTextEquals("letE"u8))
            {
                if (!r.Read() || r.TokenType != JsonTokenType.StartObject)
                {
                    return false;
                }
                int ln = int.MinValue, lt = int.MinValue, lv = int.MinValue, lb = int.MinValue;
                bool nondep = false;
                while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                {
                    if (r.ValueTextEquals("name"u8))
                    {
                        ln = ReadInt(ref r, lineNo);
                    }
                    else if (r.ValueTextEquals("type"u8))
                    {
                        lt = ReadInt(ref r, lineNo);
                    }
                    else if (r.ValueTextEquals("value"u8))
                    {
                        lv = ReadInt(ref r, lineNo);
                    }
                    else if (r.ValueTextEquals("body"u8))
                    {
                        lb = ReadInt(ref r, lineNo);
                    }
                    else if (r.ValueTextEquals("nondep"u8))
                    {
                        r.Read();
                        nondep = r.GetBoolean();
                    }
                    else
                    {
                        return false;
                    }
                }
                if (ln == int.MinValue || lt == int.MinValue || lv == int.MinValue || lb == int.MinValue)
                {
                    return false;
                }
                expr = Expr.Let(NameAt(file, ln, lineNo), ExprAt(file, lt, lineNo), ExprAt(file, lv, lineNo), ExprAt(file, lb, lineNo), nondep);
            }
            else if (r.ValueTextEquals("proj"u8))
            {
                if (!ReadIntObject(ref r, lineNo, ProjKeys, ints[..3]))
                {
                    return false;
                }
                expr = Expr.Proj(NameAt(file, ints[0], lineNo), ints[1], ExprAt(file, ints[2], lineNo));
            }
            else if (r.ValueTextEquals("natVal"u8))
            {
                r.Read();
                string sv = r.GetString() ?? throw new ExportFormatException(lineNo, "natVal must be a string");
                if (!BigInteger.TryParse(sv, NumberStyles.None, CultureInfo.InvariantCulture, out BigInteger n))
                {
                    throw new ExportFormatException(lineNo, $"invalid natVal '{sv}'");
                }
                expr = Expr.NatLit(n);
            }
            else if (r.ValueTextEquals("strVal"u8))
            {
                r.Read();
                expr = Expr.StrLit(r.GetString() ?? throw new ExportFormatException(lineNo, "strVal must be a string"));
            }
            else
            {
                // meta, mdata, or a declaration: let the DOM parser handle it
                return false;
            }
        }
        if (nameIdx != int.MinValue && name is not null)
        {
            AddAt(file.Names, nameIdx, name, lineNo, "name");
            return true;
        }
        if (levelIdx != int.MinValue && level is not null)
        {
            AddAt(file.Levels, levelIdx, level, lineNo, "level");
            return true;
        }
        if (exprIdx != int.MinValue && expr is not null)
        {
            AddAt(file.Exprs, exprIdx, expr, lineNo, "expression");
            return true;
        }
        return false;
    }

    private static Name NameAt(ExportFile file, int i, long lineNo)
    {
        if (i < 0 || i >= file.Names.Count)
        {
            throw new ExportFormatException(lineNo, $"reference to undefined name {i}");
        }
        return file.Names[i];
    }

    private static Level LevelAt(ExportFile file, int i, long lineNo)
    {
        if (i < 0 || i >= file.Levels.Count)
        {
            throw new ExportFormatException(lineNo, $"reference to undefined level {i}");
        }
        return file.Levels[i];
    }

    private static Expr ExprAt(ExportFile file, int i, long lineNo)
    {
        if (i < 0 || i >= file.Exprs.Count)
        {
            throw new ExportFormatException(lineNo, $"reference to undefined expression {i}");
        }
        return file.Exprs[i];
    }

    private static ExportDecl? ParseLine(ExportFile file, JsonElement root, long lineNo)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ExportFormatException(lineNo, "expected a JSON object");
        }
        if (root.TryGetProperty("meta", out JsonElement meta))
        {
            ParseMeta(file, meta, lineNo);
            return null;
        }
        if (root.TryGetProperty("in", out JsonElement inIdx))
        {
            AddAt(file.Names, inIdx.GetInt32(), ParseName(file, root, lineNo), lineNo, "name");
            return null;
        }
        if (root.TryGetProperty("il", out JsonElement ilIdx))
        {
            AddAt(file.Levels, ilIdx.GetInt32(), ParseLevel(file, root, lineNo), lineNo, "level");
            return null;
        }
        if (root.TryGetProperty("ie", out JsonElement ieIdx))
        {
            AddAt(file.Exprs, ieIdx.GetInt32(), ParseExpr(file, root, lineNo), lineNo, "expression");
            return null;
        }
        return ParseDecl(file, root, lineNo);
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
        int n = arr.GetArrayLength();
        if (n == 0)
        {
            return [];
        }
        var r = new Name[n];
        int k = 0;
        foreach (JsonElement e in arr.EnumerateArray())
        {
            r[k++] = NameAt(file, e, lineNo);
        }
        return r;
    }

    private static Level[] LevelsAt(ExportFile file, JsonElement arr, long lineNo)
    {
        int n = arr.GetArrayLength();
        if (n == 0)
        {
            return [];
        }
        var r = new Level[n];
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
