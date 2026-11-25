using System.Text;

namespace Tenet.Kernel;

/// <summary>What a trace step concludes.</summary>
public enum TraceKind : byte
{
    /// <summary>One kernel rule fired: <see cref="TraceStep.Rule"/> names it. Lhs and Rhs are the rule's terms.</summary>
    Rule,

    /// <summary>
    /// Γ ⊢ Lhs : Rhs, computed in infer-only mode: the kernel trusts that Lhs is well typed (it is a subterm or a
    /// reduct of a term already checked) and only computes its type.
    /// </summary>
    Infer,

    /// <summary>Lhs ↝* Rhs by weak head normalization without delta (whnf_core).</summary>
    WhnfCore,

    /// <summary>Lhs ↝* Rhs by full weak head normalization.</summary>
    Whnf,

    /// <summary>Lhs ≡ Rhs. The steps inside its span are the derivation.</summary>
    DefEq,

    /// <summary>A local variable entering the context: Lhs is the free variable, Rhs its type, Value its let-value if any.</summary>
    Local,

    /// <summary>
    /// Γ ⊢ Lhs : Rhs, computed in checking mode: as Infer, and in addition every argument's type was compared with
    /// its domain, every binder domain was checked to be a type, and every universe level was checked against the
    /// declaration's universe parameters. The premises inside its span are those checks.
    /// </summary>
    Check,
}

/// <summary>
/// One recorded step. Steps are stored in the order they finish, so every step's premises come before it. A step's
/// premises are the steps from <see cref="SpanStart"/> up to the step itself; its direct premises are found by walking
/// back from the step before it, jumping to each premise's own <see cref="SpanStart"/>.
/// </summary>
public readonly record struct TraceStep(TraceKind Kind, Rule Rule, Expr Lhs, Expr? Rhs, int SpanStart, Expr? Value = null, int Of = -1);

/// <summary>
/// A record of the steps the kernel took to accept a declaration, for the Lean-to-ZFC translator
/// (github.com/keithadler/tenet-zfc) and for anything else that wants a derivation rather than a verdict.
///
/// Only the derivation that succeeded is kept. Definitional equality backtracks (lazy delta, proof irrelevance,
/// eta), so a check takes a <see cref="Mark"/> on entry and calls <see cref="Rollback"/> when it fails: steps from a
/// failed attempt never reach the trace.
///
/// A trace is attached to the current thread with <see cref="Scope"/>. A type checker created while a scope is open
/// records into it and bypasses its caches, because a cache hit would skip the steps that justify it. Tracing is
/// therefore much slower than checking; it is off unless a scope is open, and costs one null check per step when off.
/// </summary>
public sealed class KernelTrace
{
    private readonly List<TraceStep> _steps = new();

    [ThreadStatic] private static KernelTrace? t_current;

    /// <summary>The trace attached to this thread, or null.</summary>
    public static KernelTrace? Current => t_current;

    public IReadOnlyList<TraceStep> Steps => _steps;
    public int Count => _steps.Count;

    /// <summary>The position to roll back to if the check about to start fails.</summary>
    public int Mark() => _steps.Count;

    /// <summary>Drop every step recorded since <paramref name="mark"/>.</summary>
    public void Rollback(int mark)
    {
        if (mark < _steps.Count)
        {
            _steps.RemoveRange(mark, _steps.Count - mark);
        }
    }

    /// <summary>Discard everything.</summary>
    public void Clear()
    {
        _steps.Clear();
        _known.Clear();
        _knownEq.Clear();
        _normal.Clear();
    }

    /// <summary>Record that <paramref name="rule"/> fired on <paramref name="lhs"/> (and <paramref name="rhs"/>), with premises since <paramref name="spanStart"/>.</summary>
    public void Step(Rule rule, Expr lhs, Expr? rhs, int spanStart = -1) =>
        _steps.Add(new TraceStep(TraceKind.Rule, rule, lhs, rhs, spanStart < 0 ? _steps.Count : spanStart));

    /// <summary>Record a conclusion whose premises are the steps since <paramref name="spanStart"/>.</summary>
    public void Conclude(TraceKind kind, Expr lhs, Expr rhs, int spanStart) =>
        _steps.Add(new TraceStep(kind, default, lhs, rhs, spanStart));

    // ---- recall: the trace's own cache ----
    //
    // A checker's caches would skip the steps that justify a result, so while tracing it does not use them. These
    // take their place: a result is remembered by the index of the step that concluded it, and a later request for
    // the same result records a step that points back to that one ("Of") instead of deriving it again. A rollback can
    // remove the step a memory points to, so every recall first checks that the step is still there and still says
    // the same thing. A term remembered as already normal needs no step at all: it asserts nothing.

    // Keyed by environment too: a nested inductive is checked in an auxiliary environment with constants the
    // declaration's own environment does not have, and a result from one must never be recalled in the other.
    private readonly Dictionary<(Environment, TraceKind, Expr), int> _known = new();
    private readonly Dictionary<(Environment, Expr, Expr), int> _knownEq = new();
    private readonly HashSet<(Environment, TraceKind, Expr)> _normal = new();

    /// <summary>Remember that the last step concluded <paramref name="lhs"/>'s result of this kind.</summary>
    public void Remember(Environment env, TraceKind kind, Expr lhs) => _known[(env, kind, lhs)] = _steps.Count - 1;

    /// <summary>Remember that <paramref name="e"/> is already in normal form of this kind.</summary>
    public void RememberNormal(Environment env, TraceKind kind, Expr e) => _normal.Add((env, kind, e));

    /// <summary>Remember that the last step concluded t ≡ s.</summary>
    public void RememberEq(Environment env, Expr t, Expr s) => _knownEq[(env, t, s)] = _steps.Count - 1;

    /// <summary>
    /// If this result was concluded by a step still in the trace, record a step pointing back to it and return the
    /// result. A term remembered as normal is returned as it is, with no step.
    /// </summary>
    public bool TryRecall(Environment env, TraceKind kind, Expr lhs, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Expr? result)
    {
        if (_normal.Contains((env, kind, lhs)))
        {
            result = lhs;
            return true;
        }
        if (_known.TryGetValue((env, kind, lhs), out int i) && i < _steps.Count && _steps[i].Kind == kind && _steps[i].Lhs.Equals(lhs))
        {
            result = _steps[i].Rhs!;
            _steps.Add(new TraceStep(kind, default, lhs, result, _steps.Count, null, i));
            return true;
        }
        result = null;
        return false;
    }

    /// <summary>As <see cref="TryRecall"/>, for a definitional equality in either order.</summary>
    public bool TryRecallEq(Environment env, Expr t, Expr s)
    {
        foreach ((Expr a, Expr b) in new[] { (t, s), (s, t) })
        {
            if (_knownEq.TryGetValue((env, a, b), out int i) && i < _steps.Count && _steps[i].Kind == TraceKind.DefEq
                && _steps[i].Lhs.Equals(a) && _steps[i].Rhs!.Equals(b))
            {
                _steps.Add(new TraceStep(TraceKind.DefEq, default, t, s, _steps.Count, null, i));
                return true;
            }
        }
        return false;
    }

    /// <summary>Record a local variable entering the context.</summary>
    public void Local(Expr fvar, Expr type, Expr? value) =>
        _steps.Add(new TraceStep(TraceKind.Local, default, fvar, type, _steps.Count, value));

    /// <summary>Record a rule step on the current thread's trace, if there is one. For code that has no checker at hand.</summary>
    public static void Record(Rule rule, Expr lhs, Expr? rhs) => t_current?.Step(rule, lhs, rhs);

    /// <summary>The direct premises of step <paramref name="index"/>, in the order they were recorded.</summary>
    public IEnumerable<int> Premises(int index)
    {
        var r = new List<int>();
        int start = _steps[index].SpanStart;
        for (int j = index - 1; j >= start; j = _steps[j].SpanStart - 1)
        {
            r.Add(j);
        }
        r.Reverse();
        return r;
    }

    /// <summary>Attach a trace to the current thread for the lifetime of the scope.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly KernelTrace? _saved;
        private readonly bool _entered;

        public Scope(KernelTrace trace)
        {
            _saved = t_current;
            _entered = true;
            t_current = trace;
        }

        public void Dispose()
        {
            if (_entered)
            {
                t_current = _saved;
            }
        }
    }
}

/// <summary>
/// Writes traces as newline-delimited JSON. Terms, levels and names are hash-consed across the whole file: each is
/// written once, the first time it is needed, and referred to by number afterwards, so a term shared by a thousand
/// steps costs one line. The format is documented in tenet-zfc's docs/trace-format.md.
/// </summary>
public sealed class TraceWriter
{
    private readonly TextWriter _out;
    private readonly Dictionary<Name, int> _names = new();
    private readonly Dictionary<Level, int> _levels = new();
    private readonly Dictionary<Expr, int> _exprs = new();
    private readonly StringBuilder _sb = new();

    public TraceWriter(TextWriter output)
    {
        _out = output;
        _out.WriteLine("{\"trace\":\"tenet\",\"version\":1}");
    }

    /// <summary>Write one declaration's trace. Step numbers restart at 0 for each declaration.</summary>
    public void WriteDecl(Name decl, KernelTrace trace)
    {
        int name = NameId(decl);
        _out.WriteLine($"{{\"decl\":{name},\"steps\":{trace.Count}}}");
        IReadOnlyList<TraceStep> steps = trace.Steps;
        for (int i = 0; i < steps.Count; i++)
        {
            TraceStep s = steps[i];
            int lhs = ExprId(s.Lhs);
            int rhs = s.Rhs is null ? -1 : ExprId(s.Rhs);
            int val = s.Value is null ? -1 : ExprId(s.Value);
            _sb.Clear();
            _sb.Append("{\"s\":").Append(i).Append(",\"k\":\"").Append(s.Kind).Append('"');
            if (s.Kind == TraceKind.Rule)
            {
                _sb.Append(",\"r\":\"").Append(s.Rule).Append('"');
            }
            _sb.Append(",\"a\":").Append(lhs);
            if (rhs >= 0)
            {
                _sb.Append(",\"b\":").Append(rhs);
            }
            if (val >= 0)
            {
                _sb.Append(",\"v\":").Append(val);
            }
            if (s.SpanStart < i)
            {
                _sb.Append(",\"from\":").Append(s.SpanStart);
            }
            if (s.Of >= 0)
            {
                _sb.Append(",\"of\":").Append(s.Of);
            }
            _sb.Append('}');
            _out.WriteLine(_sb.ToString());
        }
    }

    private int NameId(Name n)
    {
        if (_names.TryGetValue(n, out int id))
        {
            return id;
        }
        string line;
        if (n.IsAnonymous)
        {
            id = _names.Count;
            _names[n] = id;
            _out.WriteLine($"{{\"in\":{id},\"anon\":true}}");
            return id;
        }
        int pre = NameId(n.Prefix);
        id = _names.Count;
        if (n.TryGetStr(out _, out string? str))
        {
            line = $"{{\"in\":{id},\"str\":{{\"pre\":{pre},\"str\":{Json(str)}}}}}";
        }
        else if (n.TryGetNum(out _, out ulong num))
        {
            line = $"{{\"in\":{id},\"num\":{{\"pre\":{pre},\"i\":{num}}}}}";
        }
        else
        {
            throw new InvalidOperationException();
        }
        _names[n] = id;
        _out.WriteLine(line);
        return id;
    }

    private int LevelId(Level l)
    {
        if (_levels.TryGetValue(l, out int id))
        {
            return id;
        }
        string body = l switch
        {
            ZeroLevel => "\"zero\":true",
            SuccLevel s => $"\"succ\":{LevelId(s.Of)}",
            MaxLevel m => $"\"max\":[{LevelId(m.Lhs)},{LevelId(m.Rhs)}]",
            IMaxLevel m => $"\"imax\":[{LevelId(m.Lhs)},{LevelId(m.Rhs)}]",
            ParamLevel p => $"\"param\":{NameId(p.Name)}",
            _ => throw new InvalidOperationException(),
        };
        id = _levels.Count;
        _levels[l] = id;
        _out.WriteLine($"{{\"il\":{id},{body}}}");
        return id;
    }

    private int ExprId(Expr e)
    {
        if (_exprs.TryGetValue(e, out int id))
        {
            return id;
        }
        string body = e switch
        {
            BVarExpr b => $"\"bvar\":{b.Idx}",
            FVarExpr f => $"\"fvar\":{f.Id.Value}",
            SortExpr s => $"\"sort\":{LevelId(s.Level)}",
            ConstExpr c => $"\"const\":{{\"name\":{NameId(c.Name)},\"us\":[{string.Join(",", c.Levels.Select(LevelId))}]}}",
            AppExpr a => $"\"app\":{{\"fn\":{ExprId(a.Fn)},\"arg\":{ExprId(a.Arg)}}}",
            LamExpr b => Binding("lam", b),
            PiExpr b => Binding("forallE", b),
            LetExpr l => $"\"letE\":{{\"name\":{NameId(l.Name)},\"type\":{ExprId(l.Type)},\"value\":{ExprId(l.Value)},\"body\":{ExprId(l.Body)},\"nondep\":{(l.NonDep ? "true" : "false")}}}",
            LitExpr { Value: NatLiteral n } => $"\"natVal\":\"{n.Value}\"",
            LitExpr { Value: StrLiteral s } => $"\"strVal\":{Json(s.Value)}",
            ProjExpr p => $"\"proj\":{{\"typeName\":{NameId(p.StructName)},\"idx\":{p.Idx},\"struct\":{ExprId(p.Struct)}}}",
            _ => throw new InvalidOperationException("unknown expression kind " + e.Kind),
        };
        id = _exprs.Count;
        _exprs[e] = id;
        _out.WriteLine($"{{\"ie\":{id},{body}}}");
        return id;
    }

    private string Binding(string tag, BindingExpr b)
    {
        string info = b.Info switch
        {
            BinderInfo.Implicit => "implicit",
            BinderInfo.StrictImplicit => "strictImplicit",
            BinderInfo.InstImplicit => "instImplicit",
            _ => "default",
        };
        return $"\"{tag}\":{{\"name\":{NameId(b.BinderName)},\"type\":{ExprId(b.Domain)},\"body\":{ExprId(b.Body)},\"binderInfo\":\"{info}\"}}";
    }

    private static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    public void Flush() => _out.Flush();
}
