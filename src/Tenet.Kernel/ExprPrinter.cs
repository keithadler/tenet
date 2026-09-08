using System.Text;

namespace Tenet.Kernel;

/// <summary>A compact printer for expressions, for diagnostics. Bound variables are shown by their binder names.</summary>
public static class ExprPrinter
{
    /// <summary>
    /// Output longer than this is cut off. Terms are shared graphs, and printing one as a tree can take
    /// memory exponential in its size, so a diagnostic must never print without a bound.
    /// </summary>
    public static int MaxLength { get; set; } = 20_000;

    public static string Print(Expr e, LocalContext? lctx = null)
    {
        var sb = new StringBuilder();
        var names = new List<Name>();
        Go(e, sb, names, lctx, 0);
        if (sb.Length > MaxLength)
        {
            sb.Length = MaxLength;
            sb.Append(" … (output truncated)");
        }
        return sb.ToString();
    }

    private static void Go(Expr e, StringBuilder sb, List<Name> names, LocalContext? lctx, int prec)
    {
        if (sb.Length > MaxLength)
        {
            return;
        }
        switch (e)
        {
            case BVarExpr b:
                {
                    int i = names.Count - 1 - b.Idx;
                    if (i >= 0)
                    {
                        sb.Append(names[i]);
                    }
                    else
                    {
                        sb.Append('#').Append(b.Idx);
                    }
                    break;
                }
            case FVarExpr f:
                {
                    LocalDecl? d = lctx?.Find(f.Id);
                    sb.Append(d is not null ? d.UserName.ToString() : f.Id.ToString());
                    break;
                }
            case SortExpr s:
                if (s.Level.Kind == LevelKind.Zero)
                {
                    sb.Append("Prop");
                }
                else if (s.Level.Equals(Level.One))
                {
                    sb.Append("Type");
                }
                else
                {
                    string l = s.Level.ToString();
                    sb.Append("Sort ").Append(s.Level is ParamLevel || s.Level.IsExplicit ? l : "(" + l + ")");
                }
                break;
            case ConstExpr c:
                sb.Append(c.Name);
                if (c.Levels.Length > 0)
                {
                    sb.Append(".{").Append(string.Join(", ", c.Levels.Select(l => l.ToString()))).Append('}');
                }
                break;
            case AppExpr:
                {
                    if (prec > 10)
                    {
                        sb.Append('(');
                    }
                    Expr f = e.GetAppArgs(out Expr[] args);
                    Go(f, sb, names, lctx, 10);
                    foreach (Expr a in args)
                    {
                        sb.Append(' ');
                        Go(a, sb, names, lctx, 11);
                    }
                    if (prec > 10)
                    {
                        sb.Append(')');
                    }
                    break;
                }
            case BindingExpr b:
                {
                    if (prec > 0)
                    {
                        sb.Append('(');
                    }
                    if (b.IsLambda)
                    {
                        sb.Append("fun ");
                    }
                    else if (!ExprOps.HasLooseBVar(b.Body, 0))
                    {
                        Go(b.Domain, sb, names, lctx, 1);
                        sb.Append(" → ");
                        names.Add(Name.Of("_"));
                        Go(b.Body, sb, names, lctx, 0);
                        names.RemoveAt(names.Count - 1);
                        if (prec > 0)
                        {
                            sb.Append(')');
                        }
                        break;
                    }
                    var (open, close) = b.Info switch
                    {
                        BinderInfo.Implicit => ("{", "}"),
                        BinderInfo.StrictImplicit => ("⦃", "⦄"),
                        BinderInfo.InstImplicit => ("[", "]"),
                        _ => ("(", ")"),
                    };
                    if (!b.IsLambda)
                    {
                        sb.Append("∀ ");
                    }
                    sb.Append(open).Append(b.BinderName).Append(" : ");
                    Go(b.Domain, sb, names, lctx, 0);
                    sb.Append(close).Append(b.IsLambda ? " => " : ", ");
                    names.Add(b.BinderName);
                    Go(b.Body, sb, names, lctx, 0);
                    names.RemoveAt(names.Count - 1);
                    if (prec > 0)
                    {
                        sb.Append(')');
                    }
                    break;
                }
            case LetExpr l:
                {
                    if (prec > 0)
                    {
                        sb.Append('(');
                    }
                    sb.Append("let ").Append(l.Name).Append(" : ");
                    Go(l.Type, sb, names, lctx, 0);
                    sb.Append(" := ");
                    Go(l.Value, sb, names, lctx, 0);
                    sb.Append("; ");
                    names.Add(l.Name);
                    Go(l.Body, sb, names, lctx, 0);
                    names.RemoveAt(names.Count - 1);
                    if (prec > 0)
                    {
                        sb.Append(')');
                    }
                    break;
                }
            case LitExpr lit:
                sb.Append(lit.Value);
                break;
            case ProjExpr p:
                Go(p.Struct, sb, names, lctx, 11);
                sb.Append('.').Append(p.Idx + 1);
                break;
        }
    }
}
