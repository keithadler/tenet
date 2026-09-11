using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tenet.DiffTest;

/// <summary>
/// Differential testing against Lean's kernel. Takes an export, produces mutated variants (text-level edits that
/// keep the file well formed), runs `tenet check` and the `leancheck` oracle on each, and compares the verdict for
/// every declaration. Two kinds of disagreement matter:
///   SOUNDNESS: Tenet accepts a declaration Lean rejects.
///   STRICT:    Tenet rejects a declaration Lean accepts.
/// </summary>
internal static class Program
{
    private const string Usage = """
        usage: difftest --export FILE --tenet PATH --oracle PATH [--variants N] [--mutations M] [--seed S] [--out DIR] [--keep] [--timeout SECONDS]

          --export     an .ndjson export (small ones such as Init.Prelude work best: each variant is checked twice)
          --tenet      path to the tenet executable
          --oracle     path to the leancheck executable (tools/leancheck)
          --variants   number of mutated files to generate (default 20)
          --mutations  mutations per variant (default 12)
          --seed       random seed (default: time based)
          --out        directory for variants and logs (default: a temp directory)
          --keep       keep variants that produced no disagreement (default: delete them)
          --kinds      restrict to these mutation kinds, comma separated (see --list-kinds)
          --list-kinds print the available mutation kinds and exit
          --timeout    kill a checker run after this many seconds and count it as incomplete (default 1800)
        """;

    private sealed record Verdicts(HashSet<string> Failed, HashSet<string> All, bool Incomplete, string Raw);

    private static int Main(string[] args)
    {
        string? export = null, tenet = null, oracle = null, outDir = null;
        int variants = 20, mutations = 12;
        int seed = Environment.TickCount;
        bool keep = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--export": export = args[++i]; break;
                case "--tenet": tenet = args[++i]; break;
                case "--oracle": oracle = args[++i]; break;
                case "--variants": variants = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--mutations": mutations = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--seed": seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--out": outDir = args[++i]; break;
                case "--keep": keep = true; break;
                case "--timeout": TimeoutSeconds = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--kinds": Mutator.Restrict = new HashSet<string>(args[++i].Split(','), StringComparer.Ordinal); break;
                case "--list-kinds":
                    foreach (string k in Mutator.Names)
                    {
                        Console.WriteLine(Mutator.MustAccept.Contains(k) ? $"{k}   (must be accepted by both)" : k);
                    }
                    return 0;
                default: Console.Error.WriteLine(Usage); return 2;
            }
        }
        if (export is null || tenet is null || oracle is null)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        outDir ??= Path.Combine(Path.GetTempPath(), "tenet-difftest-" + seed);
        Directory.CreateDirectory(outDir);
        Console.WriteLine($"seed {seed}, {variants} variants x {mutations} mutations, output in {outDir}");

        string[] lines = File.ReadAllLines(export);
        var rng = new Random(seed);

        // Baseline: both must accept the untouched export.
        Console.WriteLine("baseline...");
        Verdicts bt = RunTenet(tenet, export, outDir, "baseline");
        Verdicts bo = RunOracle(oracle, export, outDir, "baseline");
        if (bo.Incomplete || bo.All.Count == 0)
        {
            Console.WriteLine("the oracle produced no verdicts; is `lean` on PATH? (leancheck needs it to find the Lean sysroot)");
            Console.WriteLine(bo.Raw.Length > 600 ? bo.Raw[..600] : bo.Raw);
            return 2;
        }
        if (bt.Incomplete)
        {
            Console.WriteLine("tenet could not read the export");
            return 2;
        }
        if (bt.Failed.Count > 0 || bo.Failed.Count > 0)
        {
            Console.WriteLine($"baseline disagreement or failure: tenet failed {bt.Failed.Count}, lean failed {bo.Failed.Count}");
            foreach (string n in bt.Failed.Take(5)) Console.WriteLine("  tenet: " + n);
            foreach (string n in bo.Failed.Take(5)) Console.WriteLine("  lean:  " + n);
            return 1;
        }
        Console.WriteLine($"baseline: both accept all {bo.All.Count} declarations");

        int soundness = 0, strict = 0, agreedRejections = 0;
        var kinds = new Dictionary<string, (int Applied, int Rejected)>();
        for (int v = 0; v < variants; v++)
        {
            string[] mutated = (string[])lines.Clone();
            var applied = new List<string>();
            int attempts = 0;
            while (applied.Count < mutations && attempts++ < mutations * 20)
            {
                string? kind = Mutator.Apply(mutated, rng);
                if (kind is not null) applied.Add(kind);
            }
            string path = Path.Combine(outDir, $"variant-{v:D3}.ndjson");
            File.WriteAllLines(path, mutated);
            Verdicts t = RunTenet(tenet, path, outDir, $"variant-{v:D3}");
            Verdicts o = RunOracle(oracle, path, outDir, $"variant-{v:D3}");
            if (o.Incomplete || t.Incomplete)
            {
                Console.WriteLine($"variant {v:D3}: mutations [{string.Join(", ", applied)}]; a checker could not read the variant (tenet incomplete: {t.Incomplete}, lean incomplete: {o.Incomplete}); kept for inspection");
                continue;
            }
            var tenetAcceptsLeanRejects = o.Failed.Where(n => !t.Failed.Contains(n)).ToList();
            var tenetRejectsLeanAccepts = t.Failed.Where(n => !o.Failed.Contains(n) && o.All.Contains(n)).ToList();
            int agreed = t.Failed.Count(o.Failed.Contains);
            agreedRejections += agreed;
            soundness += tenetAcceptsLeanRejects.Count;
            strict += tenetRejectsLeanAccepts.Count;
            foreach (string k in applied)
            {
                kinds[k] = kinds.TryGetValue(k, out var c) ? (c.Applied + 1, c.Rejected) : (1, 0);
            }
            Console.WriteLine($"variant {v:D3}: mutations [{string.Join(", ", applied)}]; tenet rejects {t.Failed.Count}, lean rejects {o.Failed.Count}, agreed {agreed}" +
                              (t.Incomplete || o.Incomplete ? " (incomplete read)" : ""));
            if (tenetAcceptsLeanRejects.Count > 0)
            {
                Console.WriteLine("  SOUNDNESS: Tenet accepts but Lean rejects:");
                foreach (string n in tenetAcceptsLeanRejects.Take(10)) Console.WriteLine("    " + n + "  |  " + FirstLine(o.Raw, "FAIL " + n));
            }
            if (tenetRejectsLeanAccepts.Count > 0)
            {
                Console.WriteLine("  STRICT: Tenet rejects but Lean accepts:");
                foreach (string n in tenetRejectsLeanAccepts.Take(10)) Console.WriteLine("    " + n + "  |  " + FirstLine(t.Raw, n));
            }
            if ((tenetAcceptsLeanRejects.Count > 0 || tenetRejectsLeanAccepts.Count > 0) && applied.Contains("max-imax-swap"))
            {
                Console.WriteLine("  note: this variant carries a max-imax-swap mutation; disagreements whose universe levels print an `imax` are usually");
                Console.WriteLine("        the reference's pointer-identity level rewriting, not a kernel difference (docs/testing.md, triage)");
            }
            if (tenetAcceptsLeanRejects.Count == 0 && tenetRejectsLeanAccepts.Count == 0 && !keep)
            {
                File.Delete(path);
            }
        }
        Console.WriteLine();
        Console.WriteLine($"done: {variants} variants, {agreedRejections} agreed rejections, {soundness} SOUNDNESS disagreements, {strict} STRICT disagreements");
        Console.WriteLine("mutations applied: " + string.Join(", ", kinds.OrderBy(k => k.Key).Select(k => $"{k.Key} x{k.Value.Applied}")));
        return soundness > 0 ? 1 : strict > 0 ? 3 : 0;
    }

    private static string FirstLine(string raw, string marker)
    {
        int i = raw.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return "";
        int e = raw.IndexOf('\n', i);
        string line = e < 0 ? raw[i..] : raw[i..e];
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    private static Verdicts RunTenet(string tenet, string export, string outDir, string tag)
    {
        string report = Path.Combine(outDir, tag + ".tenet.json");
        string output = Run(tenet, ["check", export, "--quiet", "--report", report], out _);
        var failed = new HashSet<string>();
        bool incomplete = false;
        if (File.Exists(report))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(report));
            foreach (JsonElement f in doc.RootElement.GetProperty("failures").EnumerateArray())
            {
                failed.Add(Normalize(f.GetProperty("name").GetString() ?? ""));
            }
            incomplete = doc.RootElement.TryGetProperty("incomplete", out JsonElement inc) && inc.ValueKind == JsonValueKind.String;
        }
        // Tenet reports only failures; treat every other declaration as accepted.
        return new Verdicts(failed, new HashSet<string>(), incomplete, output);
    }

    private static Verdicts RunOracle(string oracle, string export, string outDir, string tag)
    {
        string output = Run(oracle, [export], out int code);
        File.WriteAllText(Path.Combine(outDir, tag + ".lean.txt"), output);
        var failed = new HashSet<string>();
        var all = new HashSet<string>();
        bool incomplete = false;
        foreach (string line in output.Split('\n'))
        {
            if (line.StartsWith("OK ", StringComparison.Ordinal))
            {
                all.Add(Normalize(line[3..].Trim()));
            }
            else if (line.StartsWith("FAIL ", StringComparison.Ordinal))
            {
                string n = Normalize(OracleName(line[5..]));
                failed.Add(n);
                all.Add(n);
            }
            else if (line.Contains("uncaught exception", StringComparison.Ordinal) || line.Contains("Expr not found", StringComparison.Ordinal))
            {
                incomplete = true;
            }
        }
        // The oracle ends every complete run with a SUMMARY line; anything else means Lean crashed or aborted.
        if (code == 2 || (all.Count == 0 && failed.Count == 0) || !output.Contains("SUMMARY ok=", StringComparison.Ordinal)) incomplete = true;
        return new Verdicts(failed, all, incomplete, output);
    }

    /// <summary>
    /// The name at the start of an oracle FAIL line, up to the ": " separator, skipping colons inside «» escapes
    /// (notation constants such as «term_::_» contain them).
    /// </summary>
    private static string OracleName(string rest)
    {
        int depth = 0;
        for (int i = 0; i < rest.Length; i++)
        {
            char ch = rest[i];
            if (ch == '«') depth++;
            else if (ch == '»') depth = Math.Max(0, depth - 1);
            else if (ch == ':' && depth == 0 && i + 1 < rest.Length && rest[i + 1] == ' ') return rest[..i].Trim();
        }
        return rest.Trim();
    }

    /// <summary>Both tools print Lean names; strip the «» escaping so odd components compare equal.</summary>
    private static string Normalize(string name) => name.Replace("«", "", StringComparison.Ordinal).Replace("»", "", StringComparison.Ordinal);

    /// <summary>A checker run longer than this is killed; Lean's kernel has no unfolding limit, so a mutation can send it into a very long reduction.</summary>
    private static int TimeoutSeconds = 1800;

    private static string Run(string exe, string[] args, out int exitCode)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start " + exe);
        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(TimeSpan.FromSeconds(TimeoutSeconds)))
        {
            p.Kill(entireProcessTree: true);
            p.WaitForExit();
            Console.Error.WriteLine($"  {Path.GetFileName(exe)} killed after {TimeoutSeconds}s; the run counts as incomplete");
            exitCode = -1;
            return sb.ToString(); // without a SUMMARY line, so the caller treats it as incomplete
        }
        p.WaitForExit(); // flush the async readers
        exitCode = p.ExitCode;
        return sb.ToString();
    }
}

/// <summary>Text-level mutations that keep the NDJSON well formed and every table index valid.</summary>
internal static class Mutator
{
    private static readonly Regex Ie = new("\"ie\":(\\d+)", RegexOptions.Compiled);

    private delegate bool Mutation(string[] lines, Random rng);

    private static readonly (string Name, Mutation Apply)[] Menu =
    [
        ("swap-thm-values", SwapTheoremValues),          // proof of one theorem used for another: must reject
        ("retype-decl", RetypeDeclaration),              // a def/thm gets another declaration's type: must reject (almost always)
        ("bvar-plus-one", BVarPlusOne),                  // de Bruijn index off by one in a shared expression
        ("binder-info", BinderInfoFlip),                 // no semantic effect: both must accept
        ("lam-forall-swap", LamForallSwap),              // lambda becomes Pi or vice versa
        ("natval-plus-one", NatValPlusOne),              // literal changes value
        ("const-levels-swap", ConstLevelsSwap),          // universe arguments permuted
        ("app-swap", AppSwap),                           // function and argument exchanged
        ("rec-rules-swap", RecRulesSwap),                // exported recursor rules permuted: comparison must fail
        ("ctor-numfields", CtorNumFields),               // exported constructor metadata wrong: comparison must fail
        ("rec-k-flip", RecKFlip),                        // exported K flag wrong: comparison must fail
        ("hints-height", HintsHeight),                   // no semantic effect on acceptance: both must accept
        ("proj-idx", ProjIdxPlusOne),                    // projection index off by one
        ("let-swap", LetSwap),                           // let type and value exchanged
        ("sort-level", SortLevel),                       // a sort gets another existing level
        ("const-rename", ConstRename),                   // a constant refers to a different existing name
        ("drop-safety", UnsafeToSafe),                   // an unsafe def relabeled safe (if any)
        ("max-imax-swap", MaxImaxSwap),                  // a universe max becomes imax or vice versa
        ("let-nondep", LetNonDepFlip),                   // no semantic effect in the kernel: both must accept

        // Targeted at kernel features rather than at random structure. The first group is the valuable one:
        // each of these rewrites a term into one that is *equal*, so both kernels must still accept. A rejection
        // from either side is a completeness gap, which is where both of the real kernel bugs found so far lived.
        ("level-max-commute", LevelMaxCommute),          // max u v = max v u: both must accept

        // The rest damage the term on purpose, but aim the damage at one kernel feature at a time.
        ("level-max-idem", LevelMaxIdem),                // max u v becomes max u u: equal only when u = v
        ("level-succ-drop", LevelSuccDrop),              // succ u becomes u: a strictly smaller level
        ("level-imax-commute", LevelImaxCommute),        // imax is not commutative: must reject
        ("natlit-boundary", NatLitBoundary),             // literals at 0, 2^31, 2^64, 2^128: exercises big-number paths
        ("ind-numnested", IndNumNested),                 // nested-inductive metadata: comparison must fail
        ("ind-isrec", IndIsRec),                         // recursiveness metadata: comparison must fail
        ("ind-isreflexive", IndIsReflexive),             // reflexivity metadata: comparison must fail
        ("rec-numminors", RecNumMinors),                 // recursor arity metadata: comparison must fail
        ("rec-numindices", RecNumIndices),               // recursor arity metadata: comparison must fail
        ("ctor-numparams", CtorNumParams),               // constructor metadata: comparison must fail
    ];

    /// <summary>Mutations whose result is definitionally equal to the original, so both kernels must accept.</summary>
    public static readonly HashSet<string> MustAccept =
        new(StringComparer.Ordinal) { "level-max-commute", "binder-info", "hints-height", "let-nondep" };

    /// <summary>When set, only these mutation names are used (--kinds).</summary>
    public static HashSet<string>? Restrict;

    public static string? Apply(string[] lines, Random rng)
    {
        (string Name, Mutation Apply)[] menu = Restrict is null
            ? Menu
            : Menu.Where(x => Restrict.Contains(x.Name)).ToArray();
        if (menu.Length == 0)
        {
            return null;
        }
        var (name, m) = menu[rng.Next(menu.Length)];
        return m(lines, rng) ? name : null;
    }

    public static IEnumerable<string> Names => Menu.Select(x => x.Name);

    private static readonly Regex MaxArgs = new("\"(i?max)\":\\[(\\d+),(\\d+)\\]", RegexOptions.Compiled);

    /// <summary><c>max u v</c> and <c>max v u</c> are the same level. Both kernels must still accept.</summary>
    private static bool LevelMaxCommute(string[] lines, Random rng)
    {
        int? i = Pick(LinesWith(lines, "\"max\":["), rng);
        return i is int k && ReplaceFirst(lines, k, MaxArgs,
            m => m.Groups[1].Value == "max" ? $"\"max\":[{m.Groups[3].Value},{m.Groups[2].Value}]" : m.Value);
    }

    /// <summary><c>max u v</c> becomes <c>max u u</c>. Equal only when the two arguments already denote the same
    /// level, so this is usually a rejection test. Not classified as must-accept for exactly that reason.</summary>
    private static bool LevelMaxIdem(string[] lines, Random rng)
    {
        int? i = Pick(LinesWith(lines, "\"max\":["), rng);
        return i is int k && ReplaceFirst(lines, k, MaxArgs,
            m => m.Groups[1].Value == "max" ? $"\"max\":[{m.Groups[2].Value},{m.Groups[2].Value}]" : m.Value);
    }

    /// <summary><c>succ u</c> becomes <c>max u u</c>, which is just <c>u</c>: a strictly smaller universe, so this
    /// must be rejected wherever the level is load-bearing. Exercises level normalization on the rejection path.</summary>
    private static bool LevelSuccDrop(string[] lines, Random rng)
    {
        int? i = Pick(LinesWith(lines, "\"succ\":"), rng);
        return i is int k && ReplaceFirst(lines, k, new Regex("\"succ\":(\\d+)"),
            m => $"\"max\":[{m.Groups[1].Value},{m.Groups[1].Value}]");
    }

    /// <summary><c>imax u v</c> is not commutative: swapping is a real change and must be rejected.</summary>
    private static bool LevelImaxCommute(string[] lines, Random rng)
    {
        int? i = Pick(LinesWith(lines, "\"imax\":["), rng);
        return i is int k && ReplaceFirst(lines, k, MaxArgs,
            m => m.Groups[1].Value == "imax" ? $"\"imax\":[{m.Groups[3].Value},{m.Groups[2].Value}]" : m.Value);
    }

    private static readonly string[] Boundaries =
        ["0", "1", "2147483648", "9223372036854775807", "18446744073709551616", "340282366920938463463374607431768211456"];

    /// <summary>Move a literal to a boundary value, exercising the kernel's big-number and overflow paths.</summary>
    private static bool NatLitBoundary(string[] lines, Random rng)
    {
        int? i = Pick(LinesWith(lines, "\"natVal\":"), rng);
        string v = Boundaries[rng.Next(Boundaries.Length)];
        return i is int k && ReplaceFirst(lines, k, new Regex("\"natVal\":\"(\\d+)\""),
            m => m.Groups[1].Value == v ? m.Value : $"\"natVal\":\"{v}\"");
    }

    private static bool BumpInt(string[] lines, Random rng, string needle, string field)
    {
        int? i = Pick(LinesWith(lines, needle), rng);
        return i is int k && ReplaceFirst(lines, k, new Regex($"\"{field}\":(\\d+)"),
            m => $"\"{field}\":{int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + 1}");
    }

    private static bool FlipBool(string[] lines, Random rng, string needle, string field)
    {
        int? i = Pick(LinesWith(lines, needle), rng);
        return i is int k && ReplaceFirst(lines, k, new Regex($"\"{field}\":(true|false)"),
            m => $"\"{field}\":{(m.Groups[1].Value == "true" ? "false" : "true")}");
    }

    private static bool IndNumNested(string[] lines, Random rng) => BumpInt(lines, rng, "\"numNested\":", "numNested");
    private static bool IndIsRec(string[] lines, Random rng) => FlipBool(lines, rng, "\"isRec\":", "isRec");
    private static bool IndIsReflexive(string[] lines, Random rng) => FlipBool(lines, rng, "\"isReflexive\":", "isReflexive");
    private static bool RecNumMinors(string[] lines, Random rng) => BumpInt(lines, rng, "\"numMinors\":", "numMinors");
    private static bool RecNumIndices(string[] lines, Random rng) => BumpInt(lines, rng, "\"numIndices\":", "numIndices");
    private static bool CtorNumParams(string[] lines, Random rng) => BumpInt(lines, rng, "\"numFields\":", "numParams");

    private static List<int> LinesWith(string[] lines, string needle)
    {
        var r = new List<int>();
        for (int i = 0; i < lines.Length; i++) if (lines[i].Contains(needle, StringComparison.Ordinal)) r.Add(i);
        return r;
    }

    private static int? Pick(List<int> xs, Random rng) => xs.Count == 0 ? null : xs[rng.Next(xs.Count)];

    private static bool ReplaceFirst(string[] lines, int i, Regex re, Func<Match, string> f)
    {
        Match m = re.Match(lines[i]);
        if (!m.Success) return false;
        lines[i] = lines[i][..m.Index] + f(m) + lines[i][(m.Index + m.Length)..];
        return true;
    }

    private static bool SwapTheoremValues(string[] lines, Random rng)
    {
        var thms = LinesWith(lines, "\"thm\":{");
        if (thms.Count < 2) return false;
        int a = thms[rng.Next(thms.Count)], b = thms[rng.Next(thms.Count)];
        if (a == b) return false;
        var re = new Regex("\"value\":(\\d+)");
        Match ma = re.Match(lines[a]), mb = re.Match(lines[b]);
        if (!ma.Success || !mb.Success || ma.Groups[1].Value == mb.Groups[1].Value) return false;
        // only valid if b's value index is already defined before a (indices are defined before use)
        string va = ma.Groups[1].Value, vb = mb.Groups[1].Value;
        int ia = int.Parse(va, CultureInfo.InvariantCulture), ib = int.Parse(vb, CultureInfo.InvariantCulture);
        if (a < b && ib > MaxIeBefore(lines, a)) return false;
        if (b < a && ia > MaxIeBefore(lines, b)) return false;
        lines[a] = lines[a].Replace("\"value\":" + va, "\"value\":" + vb, StringComparison.Ordinal);
        lines[b] = lines[b].Replace("\"value\":" + vb, "\"value\":" + va, StringComparison.Ordinal);
        return true;
    }

    /// <summary>Largest expression index defined on a line before <paramref name="line"/>.</summary>
    private static int MaxIeBefore(string[] lines, int line)
    {
        for (int i = line - 1; i >= 0; i--)
        {
            Match m = Ie.Match(lines[i]);
            if (m.Success) return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        return -1;
    }

    private static bool RetypeDeclaration(string[] lines, Random rng)
    {
        var decls = LinesWith(lines, "\"thm\":{").Concat(LinesWith(lines, "\"def\":{")).ToList();
        if (decls.Count < 2) return false;
        int a = decls[rng.Next(decls.Count)], b = decls[rng.Next(decls.Count)];
        if (a == b) return false;
        var re = new Regex("\"type\":(\\d+)");
        Match ma = re.Match(lines[a]), mb = re.Match(lines[b]);
        if (!ma.Success || !mb.Success) return false;
        int tb = int.Parse(mb.Groups[1].Value, CultureInfo.InvariantCulture);
        if (tb > MaxIeBefore(lines, a)) return false;
        return ReplaceFirst(lines, a, re, _ => "\"type\":" + tb);
    }

    private static bool BVarPlusOne(string[] lines, Random rng)
    {
        int? i = Pick(LinesWith(lines, "\"bvar\":"), rng);
        return i is int k && ReplaceFirst(lines, k, new Regex("\"bvar\":(\\d+)"), m => "\"bvar\":" + (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + 1));
    }

    private static bool BinderInfoFlip(string[] lines, Random rng)
    {
        int? i = Pick(LinesWith(lines, "\"binderInfo\":"), rng);
        if (i is not int k) return false;
        string[] infos = ["default", "implicit", "strictImplicit", "instImplicit"];
        return ReplaceFirst(lines, k, new Regex("\"binderInfo\":\"(\\w+)\""), m =>
        {
            string cur = m.Groups[1].Value;
            string next = infos.First(x => x != cur);
            return "\"binderInfo\":\"" + next + "\"";
        });
    }

    private static bool LamForallSwap(string[] lines, Random rng)
    {
        var xs = LinesWith(lines, "\"lam\":{").Concat(LinesWith(lines, "\"forallE\":{")).ToList();
        int? i = Pick(xs, rng);
        if (i is not int k) return false;
        lines[k] = lines[k].Contains("\"lam\":{", StringComparison.Ordinal)
            ? lines[k].Replace("\"lam\":{", "\"forallE\":{", StringComparison.Ordinal)
            : lines[k].Replace("\"forallE\":{", "\"lam\":{", StringComparison.Ordinal);
        return true;
    }

    private static bool NatValPlusOne(string[] lines, Random rng)
    {
        int? i = Pick(LinesWith(lines, "\"natVal\":"), rng);
        return i is int k && ReplaceFirst(lines, k, new Regex("\"natVal\":\"(\\d+)\""), m =>
            "\"natVal\":\"" + (System.Numerics.BigInteger.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + 1) + "\"");
    }

    private static bool ConstLevelsSwap(string[] lines, Random rng)
    {
        var xs = LinesWith(lines, "\"us\":[").Where(l => Regex.IsMatch(lines[l], "\"us\":\\[(\\d+),(\\d+)")).ToList();
        int? i = Pick(xs, rng);
        return i is int k && ReplaceFirst(lines, k, new Regex("\"us\":\\[(\\d+),(\\d+)"), m =>
            m.Groups[1].Value == m.Groups[2].Value ? m.Value : "\"us\":[" + m.Groups[2].Value + "," + m.Groups[1].Value);
    }

    private static bool AppSwap(string[] lines, Random rng)
    {
        int? i = Pick(LinesWith(lines, "\"app\":{"), rng);
        return i is int k && ReplaceFirst(lines, k, new Regex("\"app\":\\{\"arg\":(\\d+),\"fn\":(\\d+)\\}"), m =>
            "\"app\":{\"arg\":" + m.Groups[2].Value + ",\"fn\":" + m.Groups[1].Value + "}");
    }

    private static bool RecRulesSwap(string[] lines, Random rng)
    {
        int? i = Pick(LinesWith(lines, "\"rules\":[{").Where(l => Regex.Count(lines[l], "\"rhs\":\\d+") >= 2).ToList(), rng);
        if (i is not int k) return false;
        var rhs = Regex.Matches(lines[k], "\"rhs\":(\\d+)").Select(m => m.Groups[1].Value).Distinct().ToList();
        if (rhs.Count < 2) return false;
        string a = rhs[0], b = rhs[1];
        lines[k] = lines[k].Replace("\"rhs\":" + a, "\"rhs\":@", StringComparison.Ordinal)
                           .Replace("\"rhs\":" + b, "\"rhs\":" + a, StringComparison.Ordinal)
                           .Replace("\"rhs\":@", "\"rhs\":" + b, StringComparison.Ordinal);
        return true;
    }

    private static bool CtorNumFields(string[] lines, Random rng)
    {
        int? i = Pick(LinesWith(lines, "\"numFields\":"), rng);
        return i is int k && ReplaceFirst(lines, k, new Regex("\"numFields\":(\\d+)"), m => "\"numFields\":" + (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + 1));
    }

    private static bool RecKFlip(string[] lines, Random rng)
    {
        int? i = Pick(LinesWith(lines, "\"k\":"), rng);
        return i is int k && ReplaceFirst(lines, k, new Regex("\"k\":(true|false)"), m => m.Groups[1].Value == "true" ? "\"k\":false" : "\"k\":true");
    }

    private static bool HintsHeight(string[] lines, Random rng)
    {
        int? i = Pick(LinesWith(lines, "\"regular\":"), rng);
        return i is int k && ReplaceFirst(lines, k, new Regex("\"regular\":(\\d+)"), m => "\"regular\":" + (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + 5));
    }

    private static bool ProjIdxPlusOne(string[] lines, Random rng)
    {
        int? i = Pick(LinesWith(lines, "\"proj\":{"), rng);
        return i is int k && ReplaceFirst(lines, k, new Regex("\"idx\":(\\d+)"), m => "\"idx\":" + (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + 1));
    }

    private static bool LetSwap(string[] lines, Random rng)
    {
        int? i = Pick(LinesWith(lines, "\"letE\":{"), rng);
        if (i is not int k) return false;
        var re = new Regex("\"type\":(\\d+),\"value\":(\\d+)");
        return ReplaceFirst(lines, k, re, m => m.Groups[1].Value == m.Groups[2].Value ? m.Value : "\"type\":" + m.Groups[2].Value + ",\"value\":" + m.Groups[1].Value);
    }

    private static bool SortLevel(string[] lines, Random rng)
    {
        int? i = Pick(LinesWith(lines, "\"sort\":"), rng);
        if (i is not int k) return false;
        // any level index defined before this line is valid
        int maxIl = -1;
        for (int j = k - 1; j >= 0 && maxIl < 0; j--)
        {
            Match m = Regex.Match(lines[j], "\"il\":(\\d+)");
            if (m.Success) maxIl = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        if (maxIl < 1) return false;
        int target = rng.Next(0, maxIl + 1);
        return ReplaceFirst(lines, k, new Regex("\"sort\":(\\d+)"), m => m.Groups[1].Value == target.ToString(CultureInfo.InvariantCulture) ? m.Value : "\"sort\":" + target);
    }

    private static bool ConstRename(string[] lines, Random rng)
    {
        int? i = Pick(LinesWith(lines, "\"const\":{"), rng);
        if (i is not int k) return false;
        int maxIn = -1;
        for (int j = k - 1; j >= 0 && maxIn < 0; j--)
        {
            Match m = Regex.Match(lines[j], "\"in\":(\\d+)");
            if (m.Success) maxIn = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        if (maxIn < 1) return false;
        int target = rng.Next(1, maxIn + 1);
        return ReplaceFirst(lines, k, new Regex("\"name\":(\\d+)"), m => m.Groups[1].Value == target.ToString(CultureInfo.InvariantCulture) ? m.Value : "\"name\":" + target);
    }

    private static bool MaxImaxSwap(string[] lines, Random rng)
    {
        var xs = LinesWith(lines, "\"max\":[").Concat(LinesWith(lines, "\"imax\":[")).ToList();
        int? i = Pick(xs, rng);
        if (i is not int k) return false;
        lines[k] = lines[k].Contains("\"imax\":[", StringComparison.Ordinal)
            ? lines[k].Replace("\"imax\":[", "\"max\":[", StringComparison.Ordinal)
            : lines[k].Replace("\"max\":[", "\"imax\":[", StringComparison.Ordinal);
        return true;
    }

    private static bool LetNonDepFlip(string[] lines, Random rng)
    {
        int? i = Pick(LinesWith(lines, "\"nondep\":"), rng);
        return i is int k && ReplaceFirst(lines, k, new Regex("\"nondep\":(true|false)"), m => m.Groups[1].Value == "true" ? "\"nondep\":false" : "\"nondep\":true");
    }

    private static bool UnsafeToSafe(string[] lines, Random rng)
    {
        int? i = Pick(LinesWith(lines, "\"safety\":\"unsafe\""), rng);
        if (i is not int k) return false;
        lines[k] = lines[k].Replace("\"safety\":\"unsafe\"", "\"safety\":\"safe\"", StringComparison.Ordinal);
        return true;
    }
}
