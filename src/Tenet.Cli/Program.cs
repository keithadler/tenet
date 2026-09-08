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
        var names = args[1..].Select(Name.Parse).ToList();
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
        var names = args[1..].Select(Name.Parse).ToList();
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
            var doc = new
            {
                tenet = typeof(Program).Assembly.GetName().Version?.ToString(3),
                targets = targets.Select(Path.GetFullPath).ToArray(),
                modules = targetNames.Select(n => n.ToString()).ToArray(),
                lean = result.LeanVersion,
                all,
                success = result.Success,
                modulesChecked = result.ModulesChecked,
                modulesMapped = result.ModulesLoaded,
                @checked = result.Checked,
                failed = result.Failures.Count,
                skippedOldCodegen = result.SkippedOldCodegen,
                jobs,
                seconds = Math.Round(result.Elapsed.TotalSeconds, 2),
                failures = result.Failures.Select(f => new { name = f.Name.ToString(), module = f.Module.ToString(), kind = f.Kind, message = f.Message, seconds = Math.Round(f.Elapsed.TotalSeconds, 3) }).ToArray(),
                slow = result.Slow.OrderByDescending(s => s.Elapsed).Select(s => new { name = s.Name.ToString(), module = s.Module.ToString(), seconds = Math.Round(s.Elapsed.TotalSeconds, 2) }).ToArray(),
                kernelWork = stats ? new { infer = TypeChecker.Stats.Infer, whnf = TypeChecker.Stats.Whnf, whnfCore = TypeChecker.Stats.WhnfCore, defEq = TypeChecker.Stats.DefEq, unfold = TypeChecker.Stats.Unfold, iota = TypeChecker.Stats.Iota, natLit = TypeChecker.Stats.NatLit, faithfulRetries = TypeChecker.Stats.FaithfulRetries } : null,
            };
            File.WriteAllText(report, System.Text.Json.JsonSerializer.Serialize(doc, ReportJsonOptions));
        }
        return result.Success ? 0 : 1;
    }

    private static void WriteReport(string reportPath, string exportPath, CheckResult result, int jobs, bool stats)
    {
        var doc = new
        {
            tenet = typeof(Program).Assembly.GetName().Version?.ToString(3),
            export = Path.GetFullPath(exportPath),
            meta = result.Stream?.Meta is ExportMeta m ? new { exporter = m.ExporterName, exporterVersion = m.ExporterVersion, format = m.FormatVersion, lean = m.LeanVersion, leanGitHash = m.LeanGitHash } : null,
            success = result.Success,
            incomplete = result.ReadError?.Message,
            declarations = result.Stream?.Declarations,
            expressions = result.Stream?.Expressions,
            @checked = result.Checked,
            failed = result.Failures.Count,
            skipped = result.Skipped,
            constants = result.Environment.Count,
            jobs,
            seconds = Math.Round(result.Elapsed.TotalSeconds, 2),
            parseSeconds = result.Stream is StreamInfo si ? Math.Round(si.ParseTime.TotalSeconds, 2) : (double?)null,
            failures = result.Failures.Select(f => new { name = f.Name.ToString(), kind = f.Kind, message = f.Message, seconds = Math.Round(f.Elapsed.TotalSeconds, 3) }).ToArray(),
            slow = result.Slow.OrderByDescending(s => s.Elapsed).Select(s => new { name = s.Name.ToString(), seconds = Math.Round(s.Elapsed.TotalSeconds, 2) }).ToArray(),
            kernelWork = stats ? new { infer = TypeChecker.Stats.Infer, whnf = TypeChecker.Stats.Whnf, whnfCore = TypeChecker.Stats.WhnfCore, defEq = TypeChecker.Stats.DefEq, unfold = TypeChecker.Stats.Unfold, iota = TypeChecker.Stats.Iota, natLit = TypeChecker.Stats.NatLit } : null,
        };
        File.WriteAllText(reportPath, System.Text.Json.JsonSerializer.Serialize(doc, ReportJsonOptions));
    }

    private static readonly System.Text.Json.JsonSerializerOptions ReportJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
