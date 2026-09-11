using System.Diagnostics;
using System.Globalization;
using Tenet.Export;
using Tenet.Kernel;
using Environment = Tenet.Kernel.Environment;

namespace Tenet.Cli;

internal static class Program
{
    private const string Usage = """
        tenet - an independent type checker for Lean 4 exports, on .NET

        usage:
          tenet check <file.ndjson> [options]     check every declaration in an export
          tenet check <Module.olean>... [options] check compiled modules in place (imports are memory-mapped and
                                                   decoded on demand; --all checks the whole import closure)
          tenet check <project dir> [options]     check every module of a built Lake project (.lake/build/lib/lean)
          tenet info  <file.ndjson>               print the export's metadata and counts
          tenet show  <file.ndjson> <name>...     print declarations from an export (type, value, metadata)
          tenet show  <Module.olean> <name>...    print declarations from a compiled module or its imports
          tenet axioms <file> <name>...           print the axioms a declaration depends on, transitively
          tenet statement <Module.olean> <name>...  which constants a theorem's statement is built from, and who defines them
          tenet audit <project dir>               which of a project's declarations are complete and which rest on sorry
          --fail-on-axiom NAME on check exits non-zero if anything rests on that axiom (e.g. sorryAx)
          tenet why <Module.olean> <name>         the chain from a declaration to each assumption it rests on
          tenet compare <a.olean> <nameA> <b.olean> <nameB>   are two projects stating the same theorem?

          --json on axioms, audit, compare and crosscheck prints one machine-readable line instead
          --names-out FILE on crosscheck writes every constant compared, so slices can be unioned
          tenet crosscheck <export.ndjson> <olean|dir>  what the .olean reader decodes, against Lean's own exporter
          tenet version

        options for check:
          --only <name>[,<name>...]   check only these declarations; everything else is added unchecked
          --fail-fast                 stop at the first failure
          --no-compare                do not compare derived constructors/recursors with the exporter's
          --quiet                     no progress output
          --stats                     print kernel work counters at the end
          --verbose                   name each declaration before checking it (.olean files)
          --report <file.json>        also write the outcome (counts, failures, slow declarations) as JSON
          --jobs <n>                  check n declarations concurrently (default: number of cores; 1 = sequential)
          --slow <seconds>            report declarations slower than this (default 1)
          --stack-mb <n>              stack size for each checking thread, in MB (default 512)
          --low-memory                use the workstation garbage collector (about a third of the memory, slower)
          --report <file.json>        also write the outcome (counts, failures, slow declarations) as JSON
        options for .olean targets:
          --lib <dir>                 add a library root (Root/A/B.olean for module A.B); repeatable. Lake build trees
                                      around the target, LEAN_PATH, and the matching elan toolchain are found automatically
          --all                       check every module in the import closure, not just the targets

        exit status: 0 all declarations checked, 1 some failed, 2 usage or file error,
                     3 the export is truncated or malformed (declarations before the problem were checked)
        """;

    private static int Main(string[] args)
    {
        // `tenet <command> --help` answers about that command only. The full usage block lists nine commands
        // and is no longer something anyone reads to find one flag.
        if (args.Length >= 2 && args[1] is "-h" or "--help" && CommandHelp.TryGetValue(args[0], out string? one))
        {
            Console.WriteLine(one.TrimEnd());
            return 0;
        }
        if (args.Length >= 2 && args[0] is "help" && CommandHelp.TryGetValue(args[1], out string? two))
        {
            Console.WriteLine(two.TrimEnd());
            return 0;
        }
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }
        if (Array.IndexOf(args, "--low-memory") >= 0 && System.Environment.GetEnvironmentVariable("TENET_LOW_MEMORY") is null)
        {
            // The GC flavor is fixed at startup: relaunch ourselves with the workstation collector.
            return Relaunch(args);
        }
        try
        {
            return args[0] switch
            {
                "check" => RunOnBigStack(() => Check(args[1..]), ParseStackMb(args)),
                "info" => Info(args[1..]),
                "show" => Show(args[1..]),
                "axioms" => Axioms(args[1..]),
                "statement" => Statement(args[1..]),
                "audit" => Audit(args[1..]),
                "compare" => Compare(args[1..]),
                "why" => Why(args[1..]),
                "crosscheck" => CrossCheck(args[1..]),
                "version" => Version(),
                _ => Fail($"unknown command '{args[0]}'\n\n{Usage}"),
            };
        }
        catch (ExportFormatException e)
        {
            Console.Error.WriteLine("error: not a valid export: " + e.Message);
            return 2;
        }
        catch (IOException e)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 2;
        }
    }

    private static int Relaunch(string[] args)
    {
        string? exe = System.Environment.ProcessPath;
        if (exe is null)
        {
            return Fail("--low-memory: cannot determine the executable to relaunch; set DOTNET_gcServer=0 instead");
        }
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }
        psi.Environment["TENET_LOW_MEMORY"] = "1";
        psi.Environment["DOTNET_gcServer"] = "0";
        psi.Environment["DOTNET_gcConcurrent"] = "0";
        psi.Environment["DOTNET_GCConserveMemory"] = "5";
        using Process p = Process.Start(psi) ?? throw new InvalidOperationException("failed to relaunch");
        p.WaitForExit();
        return p.ExitCode;
    }

    private static int Fail(string msg)
    {
        Console.Error.WriteLine("error: " + msg);
        return 2;
    }

    private static int Version()
    {
        Console.WriteLine("tenet " + (typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"));
        Console.WriteLine("export formats: " + string.Join(", ", NdjsonReader.SupportedFormatMajors.Select(m => m + ".x")));
        return 0;
    }

    private static int ParseStackMb(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--stack-mb" && int.TryParse(args[i + 1], out int mb))
            {
                return mb;
            }
        }
        return 512;
    }

    /// <summary>Kernel recursion follows expression depth; run on a thread with a generous stack.</summary>
    private static int RunOnBigStack(Func<int> f, int stackMb)
    {
        int result = 2;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                result = f();
            }
            catch (Exception e)
            {
                error = e;
            }
        }, stackMb * 1024 * 1024);
        t.Start();
        t.Join();
        if (error is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }
        return result;
    }

    private static int Info(string[] args)
    {
        if (args.Length < 1)
        {
            return Fail("info needs a file");
        }
        if (args[0].Contains(".olean", StringComparison.Ordinal))
        {
            return OleanInfo(args[0]);
        }
        var sw = Stopwatch.StartNew();
        ExportFile file = NdjsonReader.ReadFile(args[0]);
        sw.Stop();
        PrintMeta(file, args[0], sw.Elapsed);
        var kinds = file.Decls.GroupBy(d => d.Kind).OrderByDescending(g => g.Count());
        foreach (var g in kinds)
        {
            Console.WriteLine($"  {g.Key,-10} {g.Count(),8}");
        }
        int consts = file.DeclaredNames().Count();
        Console.WriteLine($"  {"constants",-10} {consts,8}");
        return 0;
    }

    private static int Show(string[] args)
    {
        if (args.Length < 2)
        {
            return Fail("show needs a file and at least one name");
        }
        var names = args[1..].Where(a => !a.StartsWith("--", StringComparison.Ordinal)).Select(Name.Parse).ToList();
        if (args[0].EndsWith(".olean", StringComparison.Ordinal))
        {
            return ShowOlean(args[0], names);
        }
        ExportFile file = NdjsonReader.ReadFile(args[0]);
        var env = new Environment();
        foreach (ExportDecl d in file.Decls)
        {
            ExportChecker.AddUnchecked(env, d);
        }
        int missing = 0;
        foreach (Name n in names)
        {
            ConstantInfo? c = env.Find(n);
            if (c is null)
            {
                Console.WriteLine($"{n}: not in this export");
                missing++;
                continue;
            }
            string lps = c.LevelParams.Length == 0 ? "" : ".{" + string.Join(", ", c.LevelParams.Select(l => l.ToString())) + "}";
            Console.WriteLine($"{c.KindName} {c.Name}{lps} : {c.Type}");
            switch (c)
            {
                case DefinitionInfo def:
                    Console.WriteLine($"  := {def.Value}");
                    Console.WriteLine($"  hints: {def.Hints}, safety: {def.Safety}");
                    break;
                case TheoremInfo thm:
                    Console.WriteLine($"  := {thm.Value}");
                    break;
                case OpaqueInfo op:
                    Console.WriteLine($"  := {op.OpaqueValue}");
                    break;
                case InductiveInfo ind:
                    Console.WriteLine($"  params {ind.NumParams}, indices {ind.NumIndices}, ctors [{string.Join(", ", ind.Ctors.Select(x => x.ToString()))}], rec {(ind.IsRec ? "true" : "false")}, reflexive {(ind.IsReflexive ? "true" : "false")}, nested {ind.NumNested}");
                    break;
                case ConstructorInfo ctor:
                    Console.WriteLine($"  of {ctor.Induct}, index {ctor.Cidx}, params {ctor.NumParams}, fields {ctor.NumFields}");
                    break;
                case RecursorInfo rec:
                    Console.WriteLine($"  params {rec.NumParams}, indices {rec.NumIndices}, motives {rec.NumMotives}, minors {rec.NumMinors}, k {(rec.K ? "true" : "false")}");
                    foreach (RecursorRule r in rec.Rules)
                    {
                        Console.WriteLine($"  rule {r.Ctor} ({r.NumFields} fields) := {r.Rhs}");
                    }
                    break;
                case QuotInfo q:
                    Console.WriteLine($"  quotient {q.Kind}");
                    break;
            }
        }
        return missing == 0 ? 0 : 1;
    }

    /// <summary>
    /// Show a shortest chain from a declaration to each assumption it rests on. An axiom list says what a theorem
    /// depends on; this says which lemma brought the dependency in, which is the part you can act on.
    /// Credit where due: <see href="https://github.com/vince-gonzalez/gonzalgo">gonzalgo</see> did this first, and
    /// goes further by measuring how far an axiom reaches and flagging theorems that may not need it.
    /// </summary>
    private static int Why(string[] args)
    {
        if (args.Length < 2)
        {
            return Fail("why needs an .olean file and a declaration name");
        }
        var search = new Tenet.Olean.LeanSearchPath();
        search.AddFromEnvironment();
        search.AddAroundOleanFile(args[0]);
        using var checker = new Tenet.Olean.OleanChecker(search);
        Name module = search.ModuleNameOf(args[0]);
        checker.Load([(module, args[0])]);
        search.AddToolchainFor(checker.Modules[module].LeanVersion);
        checker.Load([(module, args[0])]);

        // Which module defines what, so each step of the chain can say where to look.
        var definedIn = new Dictionary<Name, Name>();
        foreach ((Name m, Tenet.Olean.OleanModule om) in checker.Modules)
        {
            foreach (Name c in om.ConstantNames)
            {
                definedIn.TryAdd(c, m);
            }
        }
        var standard = new HashSet<Name> { Name.Of("propext"), Name.Of("Classical", "choice"), Name.Of("Quot", "sound") };

        foreach (Name n in args[1..].Where(a => !a.StartsWith("--", StringComparison.Ordinal)).Select(Name.Parse))
        {
            if (checker.Resolve(n) is null)
            {
                Console.WriteLine($"{n}: not in {module} or its imports");
                continue;
            }
            var (axioms, _) = Replay.AxiomsOf(checker.Resolve, n);
            var interesting = axioms.Where(a => !standard.Contains(a)).ToList();
            if (WantsJson(args))
            {
                var chains = new List<object>();
                foreach (Name ax in interesting)
                {
                    List<Name>? path = Replay.PathTo(checker.Resolve, n, ax);
                    chains.Add(new RawJson(Json(
                        ("assumption", ax.ToString()),
                        ("steps", path is null ? 0 : path.Count - 1),
                        ("chain", (path ?? new List<Name>()).Select(x => x.ToString()).ToList()),
                        ("modules", (path ?? new List<Name>())
                            .Select(x => definedIn.TryGetValue(x, out Name? dm) ? dm.ToString() : "").ToList()))));
                }
                Console.WriteLine(Json(
                    ("command", "why"), ("name", n.ToString()),
                    ("unconditional", interesting.Count == 0),
                    ("assumptions", chains)));
                continue;
            }
            Console.WriteLine(n.ToString());
            if (interesting.Count == 0)
            {
                Console.WriteLine("  rests on nothing beyond propext, Classical.choice and Quot.sound");
                Console.WriteLine();
                continue;
            }
            foreach (Name ax in interesting)
            {
                List<Name>? path = Replay.PathTo(checker.Resolve, n, ax);
                Console.WriteLine($"  rests on {ax} by this chain:");
                if (path is null)
                {
                    Console.WriteLine("    (no chain found, which should not happen)");
                    continue;
                }
                for (int i = 0; i < path.Count; i++)
                {
                    string where = definedIn.TryGetValue(path[i], out Name? m) ? $"   [{m}]" : "";
                    string arrow = i == 0 ? "   " : "-> ";
                    Console.WriteLine($"    {arrow}{path[i]}{where}");
                }
                int steps = path.Count - 1;
                Console.WriteLine($"    ({steps} step{(steps == 1 ? "" : "s")}; the last named declaration above the assumption is the one that invokes it)");
            }
            Console.WriteLine();
        }
        return 0;
    }

    /// <summary>
    /// Compare what Tenet reads from Lean's compiled <c>.olean</c> files against what Lean's own exporter wrote for
    /// the same declarations. The kernel comparison against Lean cannot see a reader bug: a reader that quietly drops
    /// a hypothesis produces a different, weaker theorem that both kernels then accept. This is the only check that
    /// covers that path, and it is the weakest link in Tenet's trusted base.
    /// </summary>
    private static int CrossCheck(string[] args)
    {
        if (args.Length < 2)
        {
            return Fail("crosscheck needs an export (.ndjson) and an .olean file or a directory of them");
        }
        int show = 10;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--show" && i + 1 < args.Length && int.TryParse(args[++i], out int v))
            {
                show = v;
            }
        }

        var sw = Stopwatch.StartNew();
        ExportFile file = NdjsonReader.ReadFile(args[0]);
        var exported = new Environment();
        foreach (ExportDecl d in file.Decls)
        {
            ExportChecker.AddUnchecked(exported, d);
        }
        Console.Error.WriteLine($"export: {file.Decls.Count} declarations read in {sw.Elapsed.TotalSeconds:F1}s");

        List<string> files = Directory.Exists(args[1])
            ? Directory.EnumerateFiles(args[1], "*.olean", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList()
            : [args[1]];

        int compared = 0, agreed = 0, absent = 0;
        var comparedNames = new List<Name>();
        var differences = new List<(Name Name, string What)>();
        foreach (string f in files)
        {
            using var m = new Tenet.Olean.OleanModule(f);
            foreach (ConstantInfo c in m.DecodeAll())
            {
                ConstantInfo? e = exported.Find(c.Name);
                if (e is null)
                {
                    absent++;   // the exporter omits unsafe and some compiler-generated declarations
                    continue;
                }
                compared++;
                comparedNames.Add(c.Name);
                string? diff = Replay.Difference(e, c);
                if (diff is null)
                {
                    agreed++;
                }
                else
                {
                    differences.Add((c.Name, diff));
                }
                _ = diff;
            }
        }

        // Classify, because not every difference means the same thing. A binder's name and its implicit/explicit
        // marking are elaboration metadata that the kernel ignores entirely, so they cannot change a verdict. Two
        // private auxiliaries with the same tail and different module prefixes are the same declaration realized in
        // different places. Anything else would be a reader defect.
        var cosmetic = new List<(Name, string)>();
        var realization = new List<(Name, string)>();
        var substantive = new List<(Name, string)>();
        foreach ((Name n, string what) in differences)
        {
            if (what.Contains(": binder ", StringComparison.Ordinal))
            {
                cosmetic.Add((n, what));
            }
            else if (SamePrivateTail(what))
            {
                realization.Add((n, what));
            }
            else
            {
                substantive.Add((n, what));
            }
        }

        // Coverage across several slices only means something if the union is counted, not the sum: two exports
        // of different Mathlib modules share most of their closure.
        int nameIdx = Array.IndexOf(args, "--names-out");
        if (nameIdx >= 0 && nameIdx + 1 < args.Length)
        {
            File.WriteAllLines(args[nameIdx + 1], comparedNames.Select(n => n.ToString()));
        }
        if (WantsJson(args))
        {
            Console.WriteLine(Json(
                ("command", "crosscheck"), ("modules", files.Count), ("compared", compared),
                ("identical", agreed), ("cosmetic", cosmetic.Count), ("realizedElsewhere", realization.Count),
                ("substantive", substantive.Count), ("notInExport", absent),
                ("seconds", Math.Round(sw.Elapsed.TotalSeconds, 1)),
                ("ok", substantive.Count == 0)));
            return substantive.Count == 0 ? 0 : 1;
        }
        Console.WriteLine($"crosscheck: {files.Count} module{(files.Count == 1 ? "" : "s")}, {compared} constants compared against the export in {sw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"  identical:                          {agreed}");
        Console.WriteLine($"  binder names or implicitness only:  {cosmetic.Count}   (elaboration metadata; the kernel ignores it)");
        Console.WriteLine($"  same auxiliary, realized elsewhere: {realization.Count}   (a private name differing only in its module prefix)");
        Console.WriteLine($"  substantive:                        {substantive.Count}");
        Console.WriteLine($"  not in the export:                  {absent}   (the exporter omits unsafe and some compiler-generated declarations)");
        foreach ((Name n, string what) in substantive.Concat(realization).Concat(cosmetic).Take(show))
        {
            Console.WriteLine();
            Console.WriteLine($"  {n}: {what}");
        }
        return substantive.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// True when a difference is two private names that agree after their <c>_private.Module.N.</c> prefix: the same
    /// auxiliary declaration realized in a different module, not a different declaration.
    /// </summary>
    private static bool SamePrivateTail(string what)
    {
        string? a = null, b = null;
        foreach (string line in what.Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith("a: ", StringComparison.Ordinal))
            {
                a = t[3..];
            }
            else if (t.StartsWith("b: ", StringComparison.Ordinal))
            {
                b = t[3..];
            }
        }
        if (a is null || b is null)
        {
            return false;
        }
        // strip a leading `_private.<module path>.<n>.` from each and see whether what is left agrees
        var prefix = new System.Text.RegularExpressions.Regex(@"^_private\.[A-Za-z0-9_.]+?\.\d+\.");
        System.Text.RegularExpressions.Match ma = prefix.Match(a), mb = prefix.Match(b);
        return ma.Success && mb.Success && a[ma.Length..] == b[mb.Length..];
    }

    /// <summary>
    /// Compare two constructor types argument by argument, ignoring the final result type. A structure's fields are
    /// its content; the result type merely names the structure, and two structures declared separately are distinct
    /// types no matter how identically they are written, so including it would answer "different" every time.
    /// </summary>
    private static (bool Syntactic, bool Equal) CompareTelescope(
        Expr ta, Expr tb, LocalContext lctx, Func<Expr, Expr, bool> eq, out string why)
    {
        var diffs = new List<string>();
        bool syntactic = true;
        int i = 0;
        while (ta is PiExpr pa && tb is PiExpr pb)
        {
            if (!pa.Domain.Equals(pb.Domain))
            {
                syntactic = false;
                if (!eq(pa.Domain, pb.Domain))
                {
                    // Keep going rather than stopping here. Which fields differ is the answer; the first one is not.
                    diffs.Add($"field {i} ({pa.BinderName}):\n        A: {Trim(ExprPrinter.Print(pa.Domain))}\n        B: {Trim(ExprPrinter.Print(pb.Domain))}");
                }
            }
            Expr fv = lctx.MkLocalDecl(pa.BinderName, pa.Domain, pa.Info);
            ta = ExprOps.Instantiate1(pa.Body, fv);
            tb = ExprOps.Instantiate1(pb.Body, fv);
            i++;
        }
        if (ta is PiExpr || tb is PiExpr)
        {
            diffs.Add($"different number of fields: one side has more than {i}");
        }
        why = diffs.Count == 0 ? "" : $"{diffs.Count} of {i} fields differ:\n      " + string.Join("\n      ", diffs);
        return (syntactic, diffs.Count == 0);
    }

    private static readonly Dictionary<string, string> CommandHelp = new(StringComparer.Ordinal)
    {
        ["check"] = """
            tenet check <file.ndjson | Module.olean | project dir> [options]

            Re-check every declaration. Exit 0 if all check, 1 if any fail, 2 on a usage or file
            error, 3 if an export is truncated (declarations before the problem are still checked).

              --all                 also check every imported module, not just the targets
              --only a,b            check only these declarations; the rest are added unchecked
              --jobs N              worker threads (default: every core; 1 for sequential)
              --fail-on-axiom NAME  exit non-zero if anything checked rests on that axiom
              --report FILE         write a JSON report, including a hash of every artifact
              --stats               print kernel work counters and the most unfolded definitions
              --slow SECONDS        list declarations slower than this (default 1)
              --verbose             name each declaration before checking it (.olean only)
              --low-memory          workstation collector: about a third the memory, slower
              --fail-fast           stop at the first failure
              --quiet               only the final line
            """,
        ["axioms"] = """
            tenet axioms <file> <name>... [--json]

            The axioms a declaration depends on, transitively, as Lean's `#print axioms` reports
            them. A proof resting on nothing but propext, Classical.choice and Quot.sound is
            complete in Lean's logic; sorryAx marks a hole. Works on exports and .olean files.
            """,
        ["why"] = """
            tenet why <Module.olean> <name>... [--json]

            A shortest chain from a declaration to each assumption it rests on, naming the module
            at every step. An axiom list says what a theorem depends on; the chain says which
            lemma brought the dependency in.
            """,
        ["audit"] = """
            tenet audit <project dir | Module.olean> [--limit N] [--json]

            How much of a project stands unconditionally, and every axiom beyond propext,
            Classical.choice and Quot.sound that the rest carry, with the declarations that
            introduce each hole. A green build proves nothing here: `sorry` is a real term of any
            type, so a project full of holes compiles perfectly.
            """,
        ["statement"] = """
            tenet statement <Module.olean> <name>... [--json]

            Which constants a theorem's statement is built from, split into those the project
            defined itself and those from established libraries. A wrong definition hides in the
            first group. This points; it does not judge.
            """,
        ["compare"] = """
            tenet compare <a.olean> <nameA> <b.olean> <nameB> [--show-types] [--json]

            Whether two separately built projects state the same theorem. A theorem is compared by
            its type, a definition by type and value, a structure field by field. Names carrying
            different content in the two projects are reported rather than silently resolved.
            """,
        ["crosscheck"] = """
            tenet crosscheck <export.ndjson> <olean | dir> [--show N] [--names-out FILE] [--json]

            What the .olean reader decodes, against what Lean's own exporter wrote for the same
            declarations. Comparing verdicts with Lean's kernel cannot find a reader bug, because a
            reader that drops a hypothesis yields a weaker theorem both kernels accept.
            """,
        ["show"] = """
            tenet show <file.ndjson | Module.olean> <name>...

            Print declarations in full: type, value, reducibility hints, constructor and recursor
            data.
            """,
        ["info"] = """
            tenet info <file.ndjson | Module.olean>

            Header, imports and declaration counts, without checking anything.
            """,
    };

    private static string Trim(string s) => s.Length > 200 ? s[..200] + " …" : s;

    /// <summary>Hash of a file, so a report says which artifact produced the verdict rather than only its path.</summary>
    private static string Sha256(string path)
    {
        try
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using FileStream f = File.OpenRead(path);
            return System.Convert.ToHexString(sha.ComputeHash(f)).ToLowerInvariant();
        }
        catch (IOException)
        {
            return "";
        }
    }

    /// <summary>
    /// Minimal JSON emitter for the reporting commands. Every command that answers a question worth acting on should
    /// be able to answer it to a program as well as to a person; parsing our prose is not an interface.
    /// </summary>
    /// <summary>Wraps an already-encoded fragment so <see cref="Json"/> nests it instead of quoting it.</summary>
    private sealed record RawJson(string Text);

    private static string Json(params (string Key, object? Value)[] fields)
    {
        static string Enc(object? v) => v switch
        {
            null => "null",
            RawJson r => r.Text,
            bool b => b ? "true" : "false",
            int or long or double or float => System.Convert.ToString(v, CultureInfo.InvariantCulture)!,
            System.Collections.IEnumerable e and not string =>
                "[" + string.Join(",", e.Cast<object?>().Select(Enc)) + "]",
            _ => "\"" + v.ToString()!
                    .Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "\"",
        };
        return "{" + string.Join(",", fields.Select(f => $"\"{f.Key}\":{Enc(f.Value)}")) + "}";
    }

    private static bool WantsJson(string[] args) => Array.IndexOf(args, "--json") >= 0;


    /// <summary>
    /// Are two theorems, in two separately built projects, the same statement? A kernel checks that a proof proves
    /// the statement written down; it never asks whether that statement is the one intended. The one case a machine
    /// can settle is when somebody else has written the statement independently: then the question becomes whether
    /// the two agree, and definitional equality answers it.
    ///
    /// Both statements are brought into one environment. Where a constant name carries different content in the two
    /// projects, that is reported rather than silently resolved, because a disagreement inside a shared definition is
    /// exactly the thing that would make two statements look alike and mean different things.
    /// </summary>
    private static int Compare(string[] args)
    {
        if (args.Length < 4)
        {
            return Fail("compare needs: <a.olean> <nameA> <b.olean> <nameB>");
        }
        (Tenet.Olean.OleanChecker Checker, Name Name) Side(string path, string decl)
        {
            var search = new Tenet.Olean.LeanSearchPath();
            search.AddFromEnvironment();
            search.AddAroundOleanFile(path);
            var checker = new Tenet.Olean.OleanChecker(search);
            Name module = search.ModuleNameOf(path);
            checker.Load([(module, path)]);
            search.AddToolchainFor(checker.Modules[module].LeanVersion);
            checker.Load([(module, path)]);
            return (checker, Name.Parse(decl));
        }

        var (ca, na) = Side(args[0], args[1]);
        using (ca)
        {
            var (cb, nb) = Side(args[2], args[3]);
            using (cb)
            {
                ConstantInfo? a = ca.Resolve(na), b = cb.Resolve(nb);
                if (a is null)
                {
                    return Fail($"{na} is not in {args[0]} or its imports");
                }
                if (b is null)
                {
                    return Fail($"{nb} is not in {args[2]} or its imports");
                }

                // Pull in everything both statements reach, from whichever side owns it, and note where the two
                // projects disagree about a name they share.
                var env = new Environment();
                var conflicts = new SortedSet<string>(StringComparer.Ordinal);
                var seen = new HashSet<Name>();
                var todo = new Stack<Name>();
                void Seed(Expr e) => ExprOps.ForEach(e, (t, _) =>
                {
                    if (t is ConstExpr k)
                    {
                        todo.Push(k.Name);
                    }
                    return true;
                });
                Seed(a.Type);
                Seed(b.Type);
                if (a.Value is Expr sa) { Seed(sa); }
                if (b.Value is Expr sb) { Seed(sb); }
                while (todo.Count > 0)
                {
                    Name cur = todo.Pop();
                    if (!seen.Add(cur))
                    {
                        continue;
                    }
                    ConstantInfo? fa = ca.Resolve(cur), fb = cb.Resolve(cur);
                    ConstantInfo? pick = fa ?? fb;
                    if (pick is null)
                    {
                        continue;
                    }
                    if (fa is not null && fb is not null && !fa.Type.Equals(fb.Type))
                    {
                        conflicts.Add(cur.ToString());
                    }
                    env.AddCore(pick);
                    foreach (Name u in Replay.UsedConstants(pick))
                    {
                        todo.Push(u);
                    }
                }

                // What carries the meaning depends on the kind of declaration, and comparing the wrong part is
                // worse than not comparing at all because it answers with confidence.
                //
                //   theorem     the type is the statement, so compare types
                //   definition  the type is only a signature, so the value is the content
                //   constructor the fields are the content; the final result type names the structure itself, and
                //               two separately declared structures are different types by construction, so that
                //               last step can never match and must be excluded
                var tc = new TypeChecker(env);
                var lctx = new LocalContext();
                string what;
                bool syntactic, defeq;
                string note = "";

                bool Eq(Expr x, Expr y)
                {
                    try
                    {
                        return tc.IsDefEq(x, y);
                    }
                    catch (KernelException e)
                    {
                        note = "   (the comparison itself failed: " + e.Message.Split('\n')[0] + ")";
                        return false;
                    }
                }

                if (a is ConstructorInfo && b is ConstructorInfo)
                {
                    what = "fields";
                    (syntactic, defeq) = CompareTelescope(a.Type, b.Type, lctx, Eq, out string why);
                    if (!defeq && why.Length > 0)
                    {
                        note = "   (" + why + ")";
                    }
                }
                else if (a is TheoremInfo || b is TheoremInfo)
                {
                    // A theorem's statement is its type. Two different proofs of one statement are both proofs of it,
                    // so comparing the values here would report a difference that does not exist.
                    what = "statement (the type; proofs are not compared)";
                    syntactic = a.Type.Equals(b.Type);
                    defeq = syntactic || Eq(a.Type, b.Type);
                }
                else if (a.Value is Expr va && b.Value is Expr vb)
                {
                    what = "type and value";
                    syntactic = a.Type.Equals(b.Type) && va.Equals(vb);
                    defeq = syntactic || (Eq(a.Type, b.Type) && Eq(va, vb));
                }
                else
                {
                    what = "type";
                    syntactic = a.Type.Equals(b.Type);
                    defeq = syntactic || Eq(a.Type, b.Type);
                }

                if (!WantsJson(args))
                {
                    Console.WriteLine($"A  {na}");
                    Console.WriteLine($"     {args[0]}");
                    Console.WriteLine($"B  {nb}");
                    Console.WriteLine($"     {args[2]}");
                    Console.WriteLine();
                }
                if (WantsJson(args))
                {
                    Console.WriteLine(Json(
                        ("command", "compare"), ("a", na.ToString()), ("b", nb.ToString()),
                        ("compared", what), ("identical", syntactic), ("same", defeq),
                        ("constantsReached", seen.Count), ("conflictingNames", conflicts.Count),
                        ("note", note.Trim())));
                    return defeq ? 0 : 1;
                }
                Console.WriteLine($"compared: {what}");
                Console.WriteLine(syntactic
                    ? "same: yes, identical"
                    : defeq
                        ? "same: yes, definitionally equal though written differently"
                        : "same: NO, not definitionally equal" + note);
                Console.WriteLine($"  constants reached by both statements: {seen.Count}");
                if (conflicts.Count == 0)
                {
                    Console.WriteLine("  shared names that differ between the projects: none");
                }
                else
                {
                    Console.WriteLine($"  shared names that differ between the projects: {conflicts.Count}");
                    Console.WriteLine("    a name meaning two things is how two statements look alike and differ:");
                    foreach (string c in conflicts.Take(20))
                    {
                        Console.WriteLine($"      {c}");
                    }
                }
                if (!syntactic && Array.IndexOf(args, "--show-types") >= 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("A: " + ExprPrinter.Print(a.Type));
                    Console.WriteLine();
                    Console.WriteLine("B: " + ExprPrinter.Print(b.Type));
                }
                else if (!syntactic)
                {
                    Console.WriteLine("  (--show-types prints both in full; they run to thousands of characters)");
                }
                return defeq ? 0 : 1;
            }
        }
    }

    /// <summary>
    /// For every declaration a project defines, say whether it rests on <c>sorryAx</c>. A formalization in progress
    /// compiles cleanly with holes in it: <c>sorry</c> is a real term of any type, so the build is green and the
    /// theorems are vacuous. This separates what is actually proved from what is still assumed, and names the
    /// declarations that introduce the holes rather than the far larger set that merely inherits them.
    /// </summary>
    private static int Audit(string[] args)
    {
        if (args.Length < 1)
        {
            return Fail("audit needs a Lake project directory or an .olean file");
        }
        List<string> files = Directory.Exists(args[0]) ? OleanFilesUnder(args[0]) : [args[0]];
        if (files.Count == 0)
        {
            return Fail($"no .olean files under {args[0]} (is the project built?)");
        }
        int limit = 40;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--limit" && i + 1 < args.Length && int.TryParse(args[++i], out int l))
            {
                limit = l;
            }
        }

        var search = new Tenet.Olean.LeanSearchPath();
        search.AddFromEnvironment();
        search.AddAroundOleanFile(files[0]);
        using var checker = new Tenet.Olean.OleanChecker(search);
        var targets = files.Select(f => (Module: search.ModuleNameOf(f), Path: f)).ToList();
        checker.Load(targets);
        search.AddToolchainFor(checker.Modules[targets[0].Module].LeanVersion);
        checker.Load(targets);

        // Every constant the project itself defines, in the project's own modules only.
        var own = new List<(Name Module, ConstantInfo Info)>();
        var ownNames = new HashSet<Name>();
        foreach ((Name m, string _) in targets)
        {
            if (!checker.Modules.TryGetValue(m, out Tenet.Olean.OleanModule? om))
            {
                continue;
            }
            foreach (Name cn in om.ConstantNames)
            {
                if (ownNames.Add(cn) && checker.Resolve(cn) is ConstantInfo ci)
                {
                    own.Add((m, ci));
                }
            }
        }

        // Anything beyond Lean's three standard axioms is an assumption the project is carrying, whether it is
        // `sorry` or a deliberate named axiom. Both make a theorem conditional; only the first is obvious.
        var standard = new HashSet<Name> { Name.Of("propext"), Name.Of("Classical", "choice"), Name.Of("Quot", "sound") };
        List<ConstantInfo> scope = own.Select(o => o.Info).ToList();

        var assumptions = new SortedDictionary<string, Name>(StringComparer.Ordinal);
        foreach (ConstantInfo ci in scope)
        {
            if (ci is AxiomInfo && !standard.Contains(ci.Name))
            {
                assumptions[ci.Name.ToString()] = ci.Name;
            }
            foreach (Name u in Replay.UsedConstants(ci))
            {
                if (!standard.Contains(u) && checker.Resolve(u) is AxiomInfo)
                {
                    assumptions[u.ToString()] = u;
                }
            }
        }

        var restsOn = new Dictionary<string, HashSet<Name>>(StringComparer.Ordinal);
        var anyAssumption = new HashSet<Name>();
        foreach ((string label, Name ax) in assumptions.Select(kv => (kv.Key, kv.Value)))
        {
            HashSet<Name> hit = Replay.DependentsOf(ax, scope);
            foreach (ConstantInfo ci in scope)
            {
                if (ci.Name.Equals(ax))
                {
                    hit.Add(ci.Name);
                }
            }
            restsOn[label] = hit;
            anyAssumption.UnionWith(hit);
        }

        int total = scope.Count;
        int clean = total - anyAssumption.Count;
        if (WantsJson(args))
        {
            Console.WriteLine(Json(
                ("command", "audit"), ("target", args[0]), ("modules", targets.Count),
                ("declarations", total), ("unconditional", clean), ("restingOnAssumption", anyAssumption.Count),
                ("assumptions", restsOn.OrderByDescending(k => k.Value.Count)
                    .Select(k => new RawJson(Json(("axiom", k.Key), ("declarations", k.Value.Count)))).ToList()),
                ("ok", anyAssumption.Count == 0)));
            return 0;
        }
        Console.WriteLine($"{args[0]}: {total} declarations defined by this project in {targets.Count} modules");
        Console.WriteLine($"  unconditional (nothing beyond propext, Classical.choice, Quot.sound): {clean} ({(total == 0 ? 0 : 100.0 * clean / total):F1}%)");
        Console.WriteLine($"  resting on an assumption: {anyAssumption.Count} ({(total == 0 ? 0 : 100.0 * anyAssumption.Count / total):F1}%)");
        if (assumptions.Count == 0)
        {
            Console.WriteLine("  no assumptions: this project introduces no axioms and no sorry");
            return 0;
        }
        Console.WriteLine();
        Console.WriteLine("  assumptions carried, and how many declarations rest on each:");
        foreach ((string label, HashSet<Name> hit) in restsOn.OrderByDescending(kv => kv.Value.Count))
        {
            string note = label == "sorryAx" ? "   (an unfinished proof)" : "   (a named axiom this project introduces)";
            Console.WriteLine($"    {label,-24} {hit.Count,6} declarations{note}");
        }

        // The declarations that introduce a hole, as opposed to the larger set that merely inherits one.
        var introducers = new SortedDictionary<string, List<Name>>(StringComparer.Ordinal);
        foreach (ConstantInfo ci in scope)
        {
            foreach (Name u in Replay.UsedConstants(ci))
            {
                if (assumptions.ContainsKey(u.ToString()))
                {
                    if (!introducers.TryGetValue(u.ToString(), out List<Name>? l))
                    {
                        introducers[u.ToString()] = l = new List<Name>();
                    }
                    l.Add(ci.Name);
                }
            }
        }
        foreach ((string label, List<Name> list) in introducers)
        {
            Console.WriteLine();
            Console.WriteLine($"  declarations that invoke {label} directly ({Math.Min(limit, list.Count)} of {list.Count} shown, --limit N for more):");
            foreach (Name h in list.OrderBy(h => h.ToString(), StringComparer.Ordinal).Take(limit))
            {
                Console.WriteLine($"    {h}");
            }
        }
        return 0;
    }

    /// <summary>
    /// Print the constants a theorem's *statement* is built from, grouped by the module that defines them.
    /// A kernel cannot tell whether a statement says what its prose comment claims; the usual way that goes wrong
    /// is a definition written for the occasion that reads plausibly and means something weaker. Those definitions
    /// are the ones defined by the project under test rather than by an established library, so this lists them
    /// first: it says where to look, not whether the statement is right.
    /// </summary>
    private static int Statement(string[] args)
    {
        if (args.Length < 2)
        {
            return Fail("statement needs an .olean file and at least one theorem name");
        }
        var search = new Tenet.Olean.LeanSearchPath();
        search.AddFromEnvironment();
        search.AddAroundOleanFile(args[0]);
        using var checker = new Tenet.Olean.OleanChecker(search);
        Name module = search.ModuleNameOf(args[0]);
        checker.Load([(module, args[0])]);
        search.AddToolchainFor(checker.Modules[module].LeanVersion);
        checker.Load([(module, args[0])]);

        // Which module defines each constant, and which of those modules belong to the project rather than a library.
        var definedIn = new Dictionary<Name, Name>();
        foreach ((Name m, Tenet.Olean.OleanModule om) in checker.Modules)
        {
            foreach (Name c in om.ConstantNames)
            {
                definedIn.TryAdd(c, m);
            }
        }
        string root = module.ToString().Split('.')[0];
        static bool IsLibrary(Name m)
        {
            string top = m.ToString().Split('.')[0];
            return top is "Init" or "Lean" or "Std" or "Mathlib" or "Batteries" or "Aesop" or "Qq"
                       or "ImportGraph" or "Plausible" or "ProofWidgets" or "LeanSearchClient" or "Cli";
        }

        foreach (Name n in args[1..].Where(a => !a.StartsWith("--", StringComparison.Ordinal)).Select(Name.Parse))
        {
            ConstantInfo? c = checker.Resolve(n);
            if (c is null)
            {
                Console.WriteLine($"{n}: not in {module} or its imports");
                continue;
            }
            var used = new HashSet<Name>();
            ExprOps.ForEach(c.Type, (t, _) =>
            {
                if (t is ConstExpr k)
                {
                    used.Add(k.Name);
                }
                return true;
            });
            var local = new SortedSet<string>();
            var library = new SortedDictionary<string, int>();
            var unknown = new SortedSet<string>();
            foreach (Name u in used)
            {
                if (!definedIn.TryGetValue(u, out Name? m))
                {
                    unknown.Add(u.ToString());
                }
                else if (IsLibrary(m))
                {
                    string top = m.ToString().Split('.')[0];
                    library[top] = library.GetValueOrDefault(top) + 1;
                }
                else
                {
                    local.Add($"{u}   ({m})");
                }
            }
            if (WantsJson(args))
            {
                Console.WriteLine(Json(
                    ("command", "statement"), ("name", n.ToString()),
                    ("constants", used.Count),
                    ("definedByThisProject", local.Select(l => l.Split("   (")[0]).ToList()),
                    ("fromLibraries", library.Select(kv => new RawJson(Json(("library", kv.Key), ("constants", kv.Value)))).ToList()),
                    ("notFound", unknown.ToList())));
                continue;
            }
            Console.WriteLine($"{n}");
            Console.WriteLine($"  statement built from {used.Count} constants");
            Console.WriteLine($"  defined by this project ({local.Count}) - audit these, a wrong definition hides here:");
            foreach (string l in local)
            {
                Console.WriteLine($"    {l}");
            }
            if (local.Count == 0)
            {
                Console.WriteLine("    (none: the statement uses only established libraries)");
            }
            Console.WriteLine($"  from established libraries: {(library.Count == 0 ? "none" : string.Join(", ", library.Select(kv => $"{kv.Key} {kv.Value}")))}");
            if (unknown.Count > 0)
            {
                Console.WriteLine($"  not found in any loaded module: {string.Join(", ", unknown)}");
            }
            Console.WriteLine();
        }
        return 0;
    }

    /// <summary>
    /// Print the axioms a declaration depends on, transitively, the way Lean's <c>#print axioms</c> does.
    /// A proof that rests on nothing but <c>propext</c>, <c>Classical.choice</c> and <c>Quot.sound</c> is a
    /// complete proof in Lean's logic; anything else, <c>sorryAx</c> above all, is a hole.
    /// </summary>
    private static int Axioms(string[] args)
    {
        if (args.Length < 2)
        {
            return Fail("axioms needs a file and at least one name");
        }
        var names = args[1..].Where(a => !a.StartsWith("--", StringComparison.Ordinal)).Select(Name.Parse).ToList();
        Func<Name, ConstantInfo?> find;
        IDisposable? owner = null;
        string where;
        if (args[0].EndsWith(".olean", StringComparison.Ordinal))
        {
            var search = new Tenet.Olean.LeanSearchPath();
            search.AddFromEnvironment();
            search.AddAroundOleanFile(args[0]);
            var checker = new Tenet.Olean.OleanChecker(search);
            owner = checker;
            Name module = search.ModuleNameOf(args[0]);
            checker.Load([(module, args[0])]);
            search.AddToolchainFor(checker.Modules[module].LeanVersion);
            checker.Load([(module, args[0])]);
            find = checker.Resolve;
            where = $"{module} and its imports";
        }
        else
        {
            ExportFile file = NdjsonReader.ReadFile(args[0]);
            var env = new Environment();
            foreach (ExportDecl d in file.Decls)
            {
                ExportChecker.AddUnchecked(env, d);
            }
            find = env.Find;
            where = "this export";
        }
        using (owner)
        {
            int missing = 0;
            foreach (Name n in names)
            {
                if (find(n) is null)
                {
                    Console.WriteLine($"{n}: not in {where}");
                    missing++;
                    continue;
                }
                var (axioms, visited) = Replay.AxiomsOf(find, n);
                if (WantsJson(args))
                {
                    Console.WriteLine(Json(
                        ("command", "axioms"), ("name", n.ToString()), ("constants", visited),
                        ("axioms", axioms.Select(a => a.ToString()).ToList()),
                        ("hasSorry", axioms.Any(a => a.ToString() == "sorryAx"))));
                    continue;
                }
                Console.WriteLine($"{n} depends on {visited} constants and these axioms:");
                if (axioms.Count == 0)
                {
                    Console.WriteLine("  (none)");
                }
                foreach (Name a in axioms)
                {
                    Console.WriteLine($"  {a}{(a.ToString() == "sorryAx" ? "   <-- an incomplete proof" : "")}");
                }
            }
            return missing == 0 ? 0 : 1;
        }
    }

    /// <summary>Print declarations from a compiled module and the modules it imports.</summary>
    private static int ShowOlean(string path, List<Name> names)
    {
        var search = new Tenet.Olean.LeanSearchPath();
        search.AddFromEnvironment();
        search.AddAroundOleanFile(path);
        using var checker = new Tenet.Olean.OleanChecker(search);
        Name module = search.ModuleNameOf(path);
        checker.Load([(module, path)]);
        search.AddToolchainFor(checker.Modules[module].LeanVersion);
        checker.Load([(module, path)]);
        int missing = 0;
        foreach (Name n in names)
        {
            ConstantInfo? c = checker.Resolve(n);
            if (c is null)
            {
                Console.WriteLine($"{n}: not in {module} or its imports");
                missing++;
                continue;
            }
            PrintConstant(c);
        }
        return missing == 0 ? 0 : 1;
    }

    private static void PrintConstant(ConstantInfo c)
    {
        string lps = c.LevelParams.Length == 0 ? "" : ".{" + string.Join(", ", c.LevelParams.Select(l => l.ToString())) + "}";
        Console.WriteLine($"{c.KindName} {c.Name}{lps} : {c.Type}");
    }

    private static int OleanInfo(string path)
    {
        var sw = Stopwatch.StartNew();
        using var m = new Tenet.Olean.OleanModule(path);
        Console.WriteLine($"{Path.GetFileName(path)}: format {m.FormatVersion}, Lean {m.LeanVersion} ({m.GitHash[..Math.Min(9, m.GitHash.Length)]}), module system: {(m.IsModule ? "yes" : "no")}");
        Console.WriteLine($"  imports ({m.Imports.Length}): {string.Join(", ", m.Imports.Select(i => i.Module + (i.IsExported ? "" : " (private)") + (i.ImportAll ? " all" : "") + (i.IsMeta ? " meta" : "")))}");
        var kinds = new Dictionary<string, int>();
        foreach (ConstantInfo c in m.DecodeAll())
        {
            kinds[c.KindName] = kinds.GetValueOrDefault(c.KindName) + 1;
        }
        Console.WriteLine($"  constants: {m.ConstantNames.Count} (decoded in {sw.Elapsed.TotalSeconds:F2}s)");
        foreach (var k in kinds.OrderByDescending(k => k.Value))
        {
            Console.WriteLine($"    {k.Key,-12} {k.Value,8}");
        }
        return 0;
    }

    private static void PrintMeta(ExportFile file, string path, TimeSpan parseTime)
    {
        Console.WriteLine($"{Path.GetFileName(path)}: {file.Decls.Count} declarations, {file.Exprs.Count} expressions, {file.Names.Count - 1} names, {file.Levels.Count - 1} levels (parsed in {parseTime.TotalSeconds:F1}s)");
        if (file.Meta is ExportMeta m)
        {
            Console.WriteLine($"  exported by {m.ExporterName} {m.ExporterVersion}, format {m.FormatVersion}, Lean {m.LeanVersion} ({m.LeanGitHash[..Math.Min(9, m.LeanGitHash.Length)]})");
        }
    }

    private static int Check(string[] args)
    {
        if (args.Length < 1 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            return Fail("check needs a file\n\n" + Usage);
        }
        if (args[0].EndsWith(".olean", StringComparison.Ordinal) || Directory.Exists(args[0]))
        {
            return CheckOlean(args);
        }
        string path = args[0];
        HashSet<Name>? only = null;
        bool failFast = false;
        bool compare = true;
        bool quiet = false;
        bool stats = false;
        double slow = 1.0;
        int jobs = System.Environment.ProcessorCount;
        string? report = null;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--only":
                    if (++i >= args.Length)
                    {
                        return Fail("--only needs a value");
                    }
                    only = new HashSet<Name>(args[i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Name.Parse));
                    break;
                case "--fail-fast":
                    failFast = true;
                    break;
                case "--no-compare":
                    compare = false;
                    break;
                case "--quiet":
                    quiet = true;
                    break;
                case "--stats":
                    stats = true;
                    break;
                case "--jobs":
                    if (++i >= args.Length || !int.TryParse(args[i], out jobs) || jobs < 1)
                    {
                        return Fail("--jobs needs a positive number");
                    }
                    break;
                case "--slow":
                    if (++i >= args.Length || !double.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out slow))
                    {
                        return Fail("--slow needs a number of seconds");
                    }
                    break;
                case "--stack-mb":
                    i++;
                    break;
                case "--low-memory":
                    break;
                case "--report":
                    if (++i >= args.Length)
                    {
                        return Fail("--report needs a file name");
                    }
                    report = args[i];
                    break;
                default:
                    return Fail($"unknown option '{args[i]}'\n\n{Usage}");
            }
        }

        TypeChecker.Stats.Enabled = stats;
        if (!File.Exists(path))
        {
            return Fail($"no such file: {path}");
        }

        var lastReport = Stopwatch.StartNew();
        var reportLock = new object();
        bool isTty = !Console.IsErrorRedirected;
        var options = new CheckOptions
        {
            Only = only,
            ContinueOnError = !failFast,
            CompareInductive = compare,
            SlowThreshold = TimeSpan.FromSeconds(slow),
            Jobs = jobs,
            WorkerStackMb = ParseStackMb(args),
            Progress = quiet ? null : p =>
            {
                lock (reportLock)
                {
                    if (lastReport.ElapsedMilliseconds < 250 && p.Index != p.Total)
                    {
                        return;
                    }
                    lastReport.Restart();
                    string line = $"  {p.Index}/{p.Total}  {p.Elapsed.TotalSeconds,7:F1}s  failed {p.Failed}  {Truncate(p.Current.ToString(), 60)}";
                    if (isTty)
                    {
                        Console.Error.Write("\r" + line.PadRight(100));
                    }
                    else
                    {
                        Console.Error.WriteLine(line);
                    }
                }
            },
        };
        CheckResult result;
        using (FileStream fs = File.OpenRead(path))
        {
            result = ExportChecker.CheckStreaming(fs, options);
        }
        if (!quiet && isTty)
        {
            Console.Error.WriteLine();
        }
        if (!quiet && result.Stream is StreamInfo si)
        {
            Console.WriteLine($"{Path.GetFileName(path)}: {si.Declarations} declarations, {si.Expressions} expressions, {si.Names} names, {si.Levels} levels (parsed in {si.ParseTime.TotalSeconds:F1}s, overlapped with checking)");
            if (si.Meta is ExportMeta m)
            {
                Console.WriteLine($"  exported by {m.ExporterName} {m.ExporterVersion}, format {m.FormatVersion}, Lean {m.LeanVersion} ({m.LeanGitHash[..Math.Min(9, m.LeanGitHash.Length)]})");
            }
        }

        if (result.ReadError is ExportFormatException re)
        {
            Console.WriteLine($"INCOMPLETE: the export could not be read past {re.Message}; everything before it was checked");
        }
        foreach (CheckFailure f in result.Failures)
        {
            Console.WriteLine($"FAIL {f.Kind} {f.Name} ({f.Elapsed.TotalSeconds:F2}s)");
            foreach (string line in f.Message.Split('\n'))
            {
                Console.WriteLine("    " + line);
            }
            if (f.RaisedAt is not null)
            {
                Console.WriteLine("    raised at:");
                foreach (string line in f.RaisedAt.Split('\n').Where(l => l.Contains("Tenet.", StringComparison.Ordinal)).Take(25))
                {
                    Console.WriteLine("      " + line.Trim());
                }
            }
        }
        if (result.Slow.Count > 0 && !quiet)
        {
            Console.WriteLine($"slow declarations (> {slow:F1}s):");
            foreach (var (name, elapsed) in result.Slow.OrderByDescending(s => s.Elapsed).Take(20))
            {
                Console.WriteLine($"  {elapsed.TotalSeconds,7:F2}s  {name}");
            }
        }
        if (stats)
        {
            Console.WriteLine("kernel work: " + TypeChecker.Stats.Summary);
        }
        if (report is not null)
        {
            WriteReport(report, path, result, jobs, stats);
        }
        string verdict = result.Failures.Count > 0 ? "FAILED" : result.ReadError is not null ? "INCOMPLETE" : "OK";
        Console.WriteLine($"{verdict}: {result.Checked} checked, {result.Failures.Count} failed, {result.Skipped} skipped, {result.Environment.Count} constants, {result.Elapsed.TotalSeconds:F1}s, {jobs} job{(jobs == 1 ? "" : "s")}");
        return result.Failures.Count > 0 ? 1 : result.ReadError is not null ? 3 : 0;
    }

    /// <summary>
    /// The modules of a Lake project directory (everything under its <c>.lake/build/lib/lean</c>, which holds the
    /// project's own modules; dependencies live under <c>.lake/packages</c>), or the <c>.olean</c> files under a plain directory.
    /// </summary>
    private static List<string> OleanFilesUnder(string dir)
    {
        string lib = Path.Combine(dir, ".lake", "build", "lib", "lean");
        string root = Directory.Exists(lib) ? lib : dir;
        return Directory.EnumerateFiles(root, "*.olean", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine(".lake", "packages"), StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    private static int CheckOlean(string[] args)
    {
        var targets = new List<string>();
        var search = new Tenet.Olean.LeanSearchPath();
        bool all = false, failFast = false, compare = true, quiet = false, stats = false, verbose = false;
        Name? failOnAxiom = null;
        string? report = null;
        double slow = 1.0;
        int jobs = System.Environment.ProcessorCount;
        HashSet<Name>? only = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--only":
                    if (++i >= args.Length) return Fail("--only needs a value");
                    only = new HashSet<Name>(args[i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Name.Parse));
                    break;
                case "--lib":
                    if (++i >= args.Length) return Fail("--lib needs a directory");
                    search.Add(args[i]);
                    break;
                case "--all": all = true; break;
                case "--fail-fast": failFast = true; break;
                case "--no-compare": compare = false; break;
                case "--quiet": quiet = true; break;
                case "--stats": stats = true; break;
                case "--verbose": verbose = true; break;
                case "--report":
                    if (++i >= args.Length) return Fail("--report needs a file name");
                    report = args[i];
                    break;
                case "--low-memory": break;
                case "--fail-on-axiom":
                    if (++i >= args.Length) return Fail("--fail-on-axiom needs an axiom name, e.g. sorryAx");
                    failOnAxiom = Name.Parse(args[i]);
                    break;
                case "--slow":
                    if (++i >= args.Length || !double.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out slow)) return Fail("--slow needs a number of seconds");
                    break;
                case "--jobs":
                    if (++i >= args.Length || !int.TryParse(args[i], out jobs) || jobs < 1) return Fail("--jobs needs a positive number");
                    break;
                case "--stack-mb": i++; break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal)) return Fail($"unknown option '{args[i]}'\n\n{Usage}");
                    if (Directory.Exists(args[i]))
                    {
                        List<string> found = OleanFilesUnder(args[i]);
                        if (found.Count == 0) return Fail($"no .olean files under {args[i]} (is the project built? expected .lake/build/lib/lean)");
                        targets.AddRange(found);
                        break;
                    }
                    if (!File.Exists(args[i])) return Fail($"no such file: {args[i]}");
                    targets.Add(args[i]);
                    break;
            }
        }
        TypeChecker.Stats.Enabled = stats;
        search.AddFromEnvironment();
        foreach (string t in targets)
        {
            search.AddAroundOleanFile(t);
        }
        using var checker = new Tenet.Olean.OleanChecker(search);
        var sw = Stopwatch.StartNew();
        var targetNames = new List<Name>();
        try
        {
            foreach (string t in targets)
            {
                Name module = search.ModuleNameOf(t);
                targetNames.Add(module);
                checker.Load([(module, t)]);
            }
        }
        catch (Tenet.Olean.OleanFormatException e)
        {
            return Fail(e.Message);
        }
        catch (FileNotFoundException e)
        {
            return Fail(e.Message);
        }
        if (!quiet)
        {
            var first = checker.Modules[targetNames[0]];
            string shown = targetNames.Count <= 4 ? string.Join(", ", targetNames) : $"{string.Join(", ", targetNames.Take(3))} and {targetNames.Count - 3} more modules";
            Console.WriteLine($"{shown}: Lean {first.LeanVersion} ({first.GitHash[..9]}); {checker.Modules.Count} modules mapped in {sw.Elapsed.TotalSeconds:F1}s{(all ? ", checking all of them" : ", checking the targets")}");
            Console.WriteLine("  library roots: " + string.Join(", ", search.Roots));
        }
        var lastReport = Stopwatch.StartNew();
        var reportLock = new object();
        bool isTty = !Console.IsErrorRedirected;
        Tenet.Olean.OleanCheckResult result;
        try
        {
            result = checker.Check(targetNames, new Tenet.Olean.OleanCheckOptions
            {
                CheckImports = all,
                Only = only,
                BeforeUnit = verbose ? n => Console.Error.WriteLine("  checking " + n) : null,
                ContinueOnError = !failFast,
                CompareInductive = compare,
                SlowThreshold = TimeSpan.FromSeconds(slow),
                Jobs = jobs,
                WorkerStackMb = ParseStackMb(args),
                Progress = quiet ? null : p =>
                {
                    lock (reportLock)
                    {
                        if (lastReport.ElapsedMilliseconds < 250 && p.Done != p.Total) return;
                        lastReport.Restart();
                        string line = $"  [{p.ModuleIndex}/{p.ModuleCount}] {Truncate(p.Module.ToString(), 40)} {p.Done}/{p.Total}  {p.Elapsed.TotalSeconds,7:F1}s  failed {p.Failed}";
                        if (isTty) Console.Error.Write("\r" + line.PadRight(100)); else Console.Error.WriteLine(line);
                    }
                },
            });
        }
        catch (Tenet.Olean.OleanFormatException e)
        {
            // a malformed module found while decoding a constant: a file error, not a kernel verdict
            return Fail(e.Message);
        }
        if (!quiet && isTty) Console.Error.WriteLine();
        foreach (var f in result.Failures)
        {
            Console.WriteLine($"FAIL {f.Kind} {f.Name} in {f.Module} ({f.Elapsed.TotalSeconds:F2}s)");
            foreach (string line in f.Message.Split('\n')) Console.WriteLine("    " + line);
            if (f.RaisedAt is not null)
            {
                Console.WriteLine("    raised at:");
                foreach (string line in f.RaisedAt.Split('\n').Where(l => l.Contains("Tenet.", StringComparison.Ordinal)).Take(25)) Console.WriteLine("      " + line.Trim());
            }
        }
        if (result.Slow.Count > 0 && !quiet)
        {
            Console.WriteLine($"slow declarations (> {slow:F1}s):");
            foreach (var (module, name, elapsed) in result.Slow.OrderByDescending(s => s.Elapsed).Take(20))
                Console.WriteLine($"  {elapsed.TotalSeconds,7:F2}s  {name} ({module})");
        }
        if (stats)
        {
            Console.WriteLine("kernel work: " + TypeChecker.Stats.Summary);
            Console.WriteLine("  " + TypeChecker.Stats.Detail);
            Console.WriteLine("most unfolded definitions:");
            foreach (var (name, count) in TypeChecker.Stats.TopUnfolds(25)) Console.WriteLine($"  {count,9}  {name}");
        }
        string skipped = result.SkippedOldCodegen > 0 ? $", {result.SkippedOldCodegen} old-codegen helpers skipped" : "";
        Console.WriteLine($"{(result.Success ? "OK" : "FAILED")}: {result.Checked} checked in {result.ModulesChecked} module{(result.ModulesChecked == 1 ? "" : "s")}, {result.Failures.Count} failed{skipped}, {result.ModulesLoaded} modules mapped, {result.Elapsed.TotalSeconds:F1}s, {jobs} job{(jobs == 1 ? "" : "s")}");
        if (report is not null)
        {
            File.WriteAllText(report, Json(
                ("tenet", typeof(Program).Assembly.GetName().Version?.ToString(3)),
                ("targets", targets.Select(Path.GetFullPath).ToList()),
                ("modules", targetNames.Select(n => n.ToString()).ToList()),
                ("lean", result.LeanVersion),
                ("all", all),
                ("success", result.Success),
                ("modulesChecked", result.ModulesChecked),
                ("modulesMapped", result.ModulesLoaded),
                ("checked", result.Checked),
                ("failed", result.Failures.Count),
                ("skippedOldCodegen", result.SkippedOldCodegen),
                ("jobs", jobs),
                ("seconds", Math.Round(result.Elapsed.TotalSeconds, 2)),
                ("failures", result.Failures.Select(f => new RawJson(Json(
                    ("name", f.Name.ToString()), ("module", f.Module.ToString()), ("kind", f.Kind),
                    ("message", f.Message), ("seconds", Math.Round(f.Elapsed.TotalSeconds, 3))))).ToList()),
                ("slow", result.Slow.OrderByDescending(x => x.Elapsed).Select(x => new RawJson(Json(
                    ("name", x.Name.ToString()), ("module", x.Module.ToString()),
                    ("seconds", Math.Round(x.Elapsed.TotalSeconds, 2))))).ToList()),
                ("kernelWork", stats ? new RawJson(Json(
                    ("infer", TypeChecker.Stats.Infer), ("whnf", TypeChecker.Stats.Whnf),
                    ("whnfCore", TypeChecker.Stats.WhnfCore), ("defEq", TypeChecker.Stats.DefEq),
                    ("unfold", TypeChecker.Stats.Unfold), ("iota", TypeChecker.Stats.Iota),
                    ("natLit", TypeChecker.Stats.NatLit), ("faithfulRetries", TypeChecker.Stats.FaithfulRetries))) : null),
                // A verdict is only reproducible if you can tell whether the inputs were the same files.
                ("artifacts", targets.Select(t => new RawJson(Json(
                    ("path", Path.GetFullPath(t)), ("sha256", Sha256(t))))).ToList())));
        }

        // A gate for projects that want an assumption kept out of their build. Checking says the proofs are valid;
        // this says they are valid without leaning on something the project has decided not to lean on.
        if (failOnAxiom is Name gate)
        {
            var scope = new List<ConstantInfo>();
            foreach (Name m in targetNames)
            {
                if (checker.Modules.TryGetValue(m, out Tenet.Olean.OleanModule? om))
                {
                    foreach (Name cn in om.ConstantNames)
                    {
                        if (checker.Resolve(cn) is ConstantInfo ci)
                        {
                            scope.Add(ci);
                        }
                    }
                }
            }
            HashSet<Name> hit = Replay.DependentsOf(gate, scope);
            foreach (ConstantInfo ci in scope)
            {
                if (ci.Name.Equals(gate))
                {
                    hit.Add(ci.Name);
                }
            }
            if (hit.Count > 0)
            {
                Console.Error.WriteLine($"FAILED: {hit.Count} declaration{(hit.Count == 1 ? "" : "s")} rest on {gate}");
                foreach (Name n in hit.OrderBy(x => x.ToString(), StringComparer.Ordinal).Take(20))
                {
                    Console.Error.WriteLine($"  {n}");
                }
                if (hit.Count > 20)
                {
                    Console.Error.WriteLine($"  ... and {hit.Count - 20} more");
                }
                return 1;
            }
            if (!quiet)
            {
                Console.Error.WriteLine($"no declaration rests on {gate}");
            }
        }
        return result.Success ? 0 : 1;
    }

    private static void WriteReport(string reportPath, string exportPath, CheckResult result, int jobs, bool stats)
    {
        ExportMeta? m = result.Stream?.Meta;
        File.WriteAllText(reportPath, Json(
            ("tenet", typeof(Program).Assembly.GetName().Version?.ToString(3)),
            ("export", Path.GetFullPath(exportPath)),
            ("meta", m is null ? null : new RawJson(Json(
                ("exporter", m.ExporterName), ("exporterVersion", m.ExporterVersion),
                ("format", m.FormatVersion), ("lean", m.LeanVersion), ("leanGitHash", m.LeanGitHash)))),
            ("success", result.Success),
            ("incomplete", result.ReadError?.Message),
            ("declarations", result.Stream?.Declarations),
            ("expressions", result.Stream?.Expressions),
            ("checked", result.Checked),
            ("failed", result.Failures.Count),
            ("skipped", result.Skipped),
            ("constants", result.Environment.Count),
            ("jobs", jobs),
            ("seconds", Math.Round(result.Elapsed.TotalSeconds, 2)),
            ("parseSeconds", result.Stream is StreamInfo si ? Math.Round(si.ParseTime.TotalSeconds, 2) : null),
            ("failures", result.Failures.Select(f => new RawJson(Json(
                ("name", f.Name.ToString()), ("kind", f.Kind), ("message", f.Message),
                ("seconds", Math.Round(f.Elapsed.TotalSeconds, 3))))).ToList()),
            ("slow", result.Slow.OrderByDescending(x => x.Elapsed).Select(x => new RawJson(Json(
                ("name", x.Name.ToString()), ("seconds", Math.Round(x.Elapsed.TotalSeconds, 2))))).ToList()),
            ("kernelWork", stats ? new RawJson(Json(
                ("infer", TypeChecker.Stats.Infer), ("whnf", TypeChecker.Stats.Whnf),
                ("whnfCore", TypeChecker.Stats.WhnfCore), ("defEq", TypeChecker.Stats.DefEq),
                ("unfold", TypeChecker.Stats.Unfold), ("iota", TypeChecker.Stats.Iota),
                ("natLit", TypeChecker.Stats.NatLit))) : null),
            ("artifacts", new List<object> { new RawJson(Json(
                ("path", Path.GetFullPath(exportPath)), ("sha256", Sha256(exportPath)))) })));
    }


    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
