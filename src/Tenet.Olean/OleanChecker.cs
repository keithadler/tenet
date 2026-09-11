using System.Text.RegularExpressions;
using System.Diagnostics;
using Tenet.Kernel;
using Environment = Tenet.Kernel.Environment;

namespace Tenet.Olean;

public sealed record OleanCheckFailure(Name Module, Name Name, string Kind, string Message, TimeSpan Elapsed, string? RaisedAt = null);

public sealed record OleanCheckProgress(Name Module, int ModuleIndex, int ModuleCount, int Done, int Total, Name Current, int Failed, TimeSpan Elapsed);

public sealed class OleanCheckOptions
{
    /// <summary>Also check every module the targets import, transitively. Otherwise imports are trusted and decoded on demand.</summary>
    public bool CheckImports { get; init; }
    public bool ContinueOnError { get; init; } = true;
    public bool CompareInductive { get; init; } = true;
    public TimeSpan SlowThreshold { get; init; } = TimeSpan.FromSeconds(1);
    public int Jobs { get; init; } = System.Environment.ProcessorCount;
    public int WorkerStackMb { get; init; } = 512;
    /// <summary>Drop decoded imported constants after each module, bounding memory at the cost of re-decoding.</summary>
    public bool EvictBetweenModules { get; init; } = true;
    public Action<OleanCheckProgress>? Progress { get; init; }
    /// <summary>Called with each unit's name just before it is checked (for locating a declaration that hangs or crashes).</summary>
    public Action<Name>? BeforeUnit { get; init; }
    /// <summary>Check only these constants (their units); everything else in the module is installed unchecked.</summary>
    public HashSet<Name>? Only { get; init; }
}

public sealed class OleanCheckResult
{
    public int ModulesChecked { get; internal set; }
    public int ModulesLoaded { get; internal set; }
    public int Checked { get; internal set; }
    /// <summary>Units not checked because they are helpers of Lean's old code generator (see <c>Replay.Unit.IsOldCodegenHelper</c>).</summary>
    public int SkippedOldCodegen { get; internal set; }
    public List<OleanCheckFailure> Failures { get; } = new();
    public List<(Name Module, Name Name, TimeSpan Elapsed)> Slow { get; } = new();
    public TimeSpan Elapsed { get; internal set; }
    /// <summary>Wall time spent decoding constants out of the mapped files, summed across workers.</summary>
    public TimeSpan DecodeTime { get; internal set; }
    /// <summary>Wall time spent in the kernel, summed across workers. Exceeds Elapsed when several jobs run.</summary>
    public TimeSpan KernelTime { get; internal set; }
    public string LeanVersion { get; internal set; } = "";
    public bool Success => Failures.Count == 0;
}

/// <summary>
/// Checks compiled Lean modules in place. Every module in the import closure is memory-mapped; constants of modules
/// that are not being checked are decoded on demand when the kernel looks them up. A module's own constants are
/// ordered by dependency, installed unchecked, and then checked concurrently with the rule that a constant may only
/// refer to constants earlier in that order or to imported modules, which is exactly the situation Lean's kernel was in
/// when it checked them.
/// </summary>
public sealed class OleanChecker : IDisposable
{
    private readonly LeanSearchPath _search;
    private readonly Dictionary<Name, OleanModule> _modules = new();

    private static readonly Regex UnknownConstant = new(@"^unknown constant '(?<n>[^']*)'$", RegexOptions.Compiled);

    /// <summary>
    /// Add context to a kernel message when the missing constant is in no loaded module. Lean realizes some names on
    /// demand (`.induct`, `.splitter` and other reserved names) and does not always store them, so a declaration that
    /// mentions one cannot be checked from module data by any kernel; that is a property of the files, not of the proof.
    /// </summary>
    private string Explain(string message)
    {
        Match m = UnknownConstant.Match(message);
        if (!m.Success)
        {
            return message;
        }
        Name n = Name.Parse(m.Groups["n"].Value);
        lock (_modules)
        {
            foreach (OleanModule mod in _modules.Values)
            {
                if (mod.Contains(n))
                {
                    return message;
                }
            }
        }
        return message + "\n  no loaded module stores this constant. Lean realizes some names on demand and does not\n"
             + "  always write them to the .olean, so no kernel can check this declaration from module data alone.";
    }
    private readonly Dictionary<Name, Name> _owner = new(); // constant -> module
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Name, byte> _touched = new(); // modules decoded from since the last trim

    public OleanChecker(LeanSearchPath search) => _search = search;

    public IReadOnlyDictionary<Name, OleanModule> Modules => _modules;

    public void Dispose()
    {
        foreach (OleanModule m in _modules.Values)
        {
            m.Dispose();
        }
    }

    /// <summary>Map a module and its import closure; returns the modules in dependency order (imports first).</summary>
    public List<Name> Load(IEnumerable<(Name Module, string Path)> targets)
    {
        var order = new List<Name>();
        var visiting = new HashSet<Name>();
        foreach (var (module, path) in targets)
        {
            Visit(module, path, order, visiting);
        }
        return order;
    }

    private void Visit(Name module, string? path, List<Name> order, HashSet<Name> visiting)
    {
        if (_modules.ContainsKey(module))
        {
            return;
        }
        if (!visiting.Add(module))
        {
            throw new KernelException($"import cycle through module '{module}'");
        }
        path ??= _search.Find(module) ?? throw new FileNotFoundException($"cannot find module '{module}' in the search path:\n  " + string.Join("\n  ", _search.Roots), module.ToString());
        var m = new OleanModule(path);
        if (_modules.Count == 0)
        {
            _search.AddToolchainFor(m.LeanVersion);
        }
        foreach (Import imp in m.Imports)
        {
            Visit(imp.Module, null, order, visiting);
        }
        _modules[module] = m;
        foreach (Name c in m.ConstantNames)
        {
            _owner.TryAdd(c, module);
        }
        order.Add(module);
        visiting.Remove(module);
    }

    /// <summary>The import closure of <paramref name="targets"/> among the mapped modules, imports first.</summary>
    public List<Name> DependencyOrder(IEnumerable<Name> targets)
    {
        var order = new List<Name>();
        var seen = new HashSet<Name>();
        foreach (Name t in targets)
        {
            Visit(t);
        }
        return order;

        void Visit(Name m)
        {
            if (!seen.Add(m))
            {
                return;
            }
            if (!_modules.TryGetValue(m, out OleanModule? mod))
            {
                throw new KernelException($"module '{m}' is not mapped");
            }
            foreach (Import imp in mod.Imports)
            {
                Visit(imp.Module);
            }
            order.Add(m);
        }
    }

    /// <summary>Decode a constant from whichever loaded module declares it.</summary>
    public ConstantInfo? Resolve(Name n)
    {
        if (!_owner.TryGetValue(n, out Name? m))
        {
            return null;
        }
        _touched[m] = 0;
        return _modules[m].FindConstant(n);
    }

    public OleanCheckResult Check(IReadOnlyList<Name> targets, OleanCheckOptions? options = null)
    {
        options ??= new OleanCheckOptions();
        var result = new OleanCheckResult { ModulesLoaded = _modules.Count };
        long kernelTicks = 0, decodeTicks = 0;
        var total = Stopwatch.StartNew();
        var env = new Environment();
        env.SetResolver(Resolve);
        // Quotient reduction is enabled for any environment that contains the quotient constants.
        if (Resolve(Quot.QuotName) is QuotInfo && Resolve(Quot.QuotLift) is QuotInfo)
        {
            env.MarkQuotInitialized();
        }
        if (_modules.Count > 0)
        {
            result.LeanVersion = _modules.Values.First().LeanVersion;
        }

        // Modules to check, in import order.
        List<Name> order = DependencyOrder(targets);
        var toCheck = new HashSet<Name>(targets);
        if (options.CheckImports)
        {
            toCheck.UnionWith(order);
        }
        var modulesInOrder = order.Where(toCheck.Contains).ToList();

        int mi = 0;
        foreach (Name module in modulesInOrder)
        {
            mi++;
            OleanModule m = _modules[module];
            long decodeStart = Stopwatch.GetTimestamp();
            var constants = m.DecodeAll().ToList();
            decodeTicks += Stopwatch.GetTimestamp() - decodeStart;
            List<Replay.Unit> units = Replay.GroupUnits(constants);
            List<Replay.Unit> ordered = OrderByDependency(units, module, out List<Replay.Unit> cyclic);
            var position = new Dictionary<Name, int>();
            for (int i = 0; i < ordered.Count; i++)
            {
                foreach (Name n in ordered[i].Names)
                {
                    position[n] = i;
                }
            }
            foreach (Replay.Unit u in cyclic)
            {
                result.Failures.Add(new OleanCheckFailure(module, u.Name, u.Kind, $"'{u.Name}' is part of a dependency cycle within module '{module}'", TimeSpan.Zero));
            }

            int done = 0;
            int checkedHere = 0;
            int skippedHere = 0;
            var sync = new object();
            using var cts = new CancellationTokenSource();
            int next = -1;
            RunWorkers(options, () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    int i = Interlocked.Increment(ref next);
                    if (i >= ordered.Count)
                    {
                        return;
                    }
                    Replay.Unit unit = ordered[i];
                    if (options.Only is not null && !unit.Names.Any(options.Only.Contains))
                    {
                        continue;
                    }
                    if (unit.IsOldCodegenHelper)
                    {
                        // Uncheckable by construction: see Replay.Unit.IsOldCodegenHelper. Counted, never checked.
                        Interlocked.Increment(ref skippedHere);
                        continue;
                    }
                    options.BeforeUnit?.Invoke(unit.Name);
                    var sw = Stopwatch.StartNew();
                    long kernelStart = Stopwatch.GetTimestamp();
                    try
                    {
                        CheckOrder(unit, i, position, module);
                        Replay.CheckUnit(env, unit, installed: true, compare: options.CompareInductive);
                        Interlocked.Increment(ref checkedHere);
                    }
                    catch (KernelException e)
                    {
                        lock (sync)
                        {
                            result.Failures.Add(new OleanCheckFailure(module, unit.Name, unit.Kind, Explain(e.Message), sw.Elapsed, e.RaisedAt));
                        }
                        if (!options.ContinueOnError)
                        {
                            cts.Cancel();
                        }
                    }
                    sw.Stop();
                    Interlocked.Add(ref kernelTicks, Stopwatch.GetTimestamp() - kernelStart);
                    if (sw.Elapsed >= options.SlowThreshold)
                    {
                        lock (sync)
                        {
                            result.Slow.Add((module, unit.Name, sw.Elapsed));
                        }
                    }
                    int n = Interlocked.Increment(ref done);
                    if (options.Progress is not null)
                    {
                        int failed;
                        lock (sync)
                        {
                            failed = result.Failures.Count;
                        }
                        options.Progress(new OleanCheckProgress(module, mi, modulesInOrder.Count, n, ordered.Count, unit.Name, failed, total.Elapsed));
                    }
                }
            });
            result.Checked += checkedHere;
            result.SkippedOldCodegen += skippedHere;
            result.ModulesChecked++;
            if (options.EvictBetweenModules)
            {
                env.EvictResolved();
                foreach (Name t in _touched.Keys)
                {
                    _modules[t].TrimCaches();
                }
                _touched.Clear();
                m.TrimCaches();
            }
            if (!options.ContinueOnError && result.Failures.Count > 0)
            {
                break;
            }
        }
        result.Elapsed = total.Elapsed;
        result.KernelTime = TimeSpan.FromSeconds((double)kernelTicks / Stopwatch.Frequency);
        result.DecodeTime = TimeSpan.FromSeconds((double)decodeTicks / Stopwatch.Frequency);
        return result;
    }

    /// <summary>
    /// Topologically order a module's units by the constants they use within the module. Units in a dependency cycle
    /// (other than the internal references of an inductive block) are reported separately.
    /// </summary>
    private static List<Replay.Unit> OrderByDependency(List<Replay.Unit> units, Name module, out List<Replay.Unit> cyclic)
    {
        var unitOf = new Dictionary<Name, int>();
        for (int i = 0; i < units.Count; i++)
        {
            foreach (Name n in units[i].Names)
            {
                unitOf[n] = i;
            }
        }
        var deps = new List<int>[units.Count];
        for (int i = 0; i < units.Count; i++)
        {
            var d = new HashSet<int>();
            foreach (ConstantInfo c in units[i].Constants)
            {
                foreach (Name used in Replay.UsedConstants(c))
                {
                    if (unitOf.TryGetValue(used, out int j) && j != i)
                    {
                        d.Add(j);
                    }
                }
            }
            deps[i] = d.ToList();
        }
        var state = new byte[units.Count]; // 0 new, 1 visiting, 2 done
        var order = new List<Replay.Unit>();
        var cyc = new List<Replay.Unit>();
        var onCycle = new HashSet<int>();
        for (int i = 0; i < units.Count; i++)
        {
            Dfs(i);
        }
        cyclic = cyc;
        return order;

        void Dfs(int i)
        {
            if (state[i] == 2)
            {
                return;
            }
            if (state[i] == 1)
            {
                onCycle.Add(i);
                return;
            }
            state[i] = 1;
            foreach (int j in deps[i])
            {
                Dfs(j);
            }
            state[i] = 2;
            if (onCycle.Contains(i))
            {
                cyc.Add(units[i]);
            }
            else
            {
                order.Add(units[i]);
            }
        }
    }

    /// <summary>A unit may use constants of its own module only if they come earlier in the dependency order.</summary>
    private static void CheckOrder(Replay.Unit unit, int index, Dictionary<Name, int> position, Name module)
    {
        foreach (ConstantInfo c in unit.Constants)
        {
            foreach (Name used in Replay.UsedConstants(c))
            {
                if (position.TryGetValue(used, out int p) && p > index)
                {
                    throw new KernelException($"'{unit.Name}' refers to '{used}', which depends on it (module '{module}')");
                }
            }
        }
    }

    private static void RunWorkers(OleanCheckOptions options, Action body)
    {
        int jobs = Math.Max(1, options.Jobs);
        var threads = new Thread[jobs];
        var errors = new List<Exception>();
        for (int w = 0; w < jobs; w++)
        {
            threads[w] = new Thread(() =>
            {
                try
                {
                    body();
                }
                catch (Exception e)
                {
                    lock (errors)
                    {
                        errors.Add(e);
                    }
                }
            }, Math.Max(1, options.WorkerStackMb) * 1024 * 1024)
            { IsBackground = true, Name = "tenet-olean-" + w };
            threads[w].Start();
        }
        foreach (Thread t in threads)
        {
            t.Join();
        }
        if (errors.Count > 0)
        {
            throw errors.Count == 1 ? errors[0] : new AggregateException(errors);
        }
    }
}
