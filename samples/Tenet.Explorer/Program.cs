using System.Globalization;
using System.Text;
using Tenet.Kernel;
using Tenet.Olean;
using Environment = Tenet.Kernel.Environment;

namespace Tenet.Explorer;

/// <summary>
/// Turns a Lean declaration into a page you can read: what it says, what it rests on, and the chain from it to
/// each assumption. The same questions <c>tenet why</c> and <c>tenet axioms</c> answer on a terminal, laid out
/// so the shape of a proof's provenance is visible at a glance.
///
/// It exists mainly to show that the packages on nuget.org are usable from an ordinary project. Nothing here
/// reaches into Tenet's internals: it reads a compiled module with Tenet.Olean, walks the dependency graph with
/// Replay, and prints expressions with ExprPrinter, all public API.
/// </summary>
internal static class Program
{
    private const string Usage = """
        usage: tenet-explorer <target> <declaration> [-o out.html]

          <target>       a Module.olean, or a directory to search (a Lake build tree works)
          <declaration>  the fully qualified name, e.g. Nat.add_comm
          -o             where to write the page (default: explorer.html)

        Lean does not need to be installed: the .olean file is read directly.
        """;

    private static int Main(string[] args)
    {
        var positional = args.Where(a => !a.StartsWith('-')).ToList();
        if (positional.Count < 2)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        string outPath = "explorer.html";
        int o = Array.IndexOf(args, "-o");
        if (o >= 0 && o + 1 < args.Length)
        {
            outPath = args[o + 1];
            positional.Remove(outPath);
        }

        string target = positional[0];
        var name = Name.Parse(positional[1]);

        using OleanChecker checker = Open(target);
        ConstantInfo? c = checker.Resolve(name);
        if (c is null)
        {
            Console.Error.WriteLine($"{name} is not in {target} or its imports");
            return 1;
        }

        // Which module holds what, so every step of a chain can say where to look.
        var definedIn = new Dictionary<Name, Name>();
        foreach ((Name m, OleanModule om) in checker.Modules)
        {
            foreach (Name cn in om.ConstantNames)
            {
                definedIn.TryAdd(cn, m);
            }
        }

        var standard = new HashSet<Name> { Name.Of("propext"), Name.Of("Classical", "choice"), Name.Of("Quot", "sound") };
        (SortedSet<Name> axioms, long visited) = Replay.AxiomsOf(checker.Resolve, name);
        var assumptions = axioms.Where(a => !standard.Contains(a)).ToList();

        var chains = new List<(Name Axiom, List<Name> Path)>();
        foreach (Name a in assumptions)
        {
            List<Name>? path = Replay.PathTo(checker.Resolve, name, a);
            if (path is not null)
            {
                chains.Add((a, path));
            }
        }

        string html = Render(c, definedIn, axioms, standard, visited, chains, target);
        File.WriteAllText(outPath, html);
        Console.WriteLine($"{name}: {visited} constants, {axioms.Count} axioms, "
                        + $"{(assumptions.Count == 0 ? "unconditional" : assumptions.Count + " assumption(s)")}");
        Console.WriteLine($"wrote {Path.GetFullPath(outPath)}");
        return 0;
    }

    /// <summary>Open a single module or a whole build tree. Two loads: the second one has the toolchain on the path.</summary>
    private static OleanChecker Open(string target)
    {
        List<string> files = Directory.Exists(target)
            ? Directory.EnumerateFiles(target, "*.olean", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal).ToList()
            : [target];
        if (files.Count == 0)
        {
            throw new InvalidOperationException($"no .olean files under {target}");
        }

        var search = new LeanSearchPath();
        search.AddFromEnvironment();
        search.AddAroundOleanFile(files[0]);
        var checker = new OleanChecker(search);
        var targets = files.Select(f => (Module: search.ModuleNameOf(f), Path: f)).ToList();
        checker.Load(targets);
        search.AddToolchainFor(checker.Modules[targets[0].Module].LeanVersion);
        checker.Load(targets);
        return checker;
    }

    private static string Render(
        ConstantInfo c,
        Dictionary<Name, Name> definedIn,
        SortedSet<Name> axioms,
        HashSet<Name> standard,
        long visited,
        List<(Name Axiom, List<Name> Path)> chains,
        string target)
    {
        var assumptions = axioms.Where(a => !standard.Contains(a)).ToList();
        bool unconditional = assumptions.Count == 0;
        string where = definedIn.TryGetValue(c.Name, out Name? m) ? m.ToString() : "unknown module";

        var sb = new StringBuilder();
        sb.Append("""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>
            """);
        sb.Append(Esc(c.Name.ToString()));
        sb.Append("""
            </title>
            <style>
              :root {
                --bg: #ffffff; --fg: #1a1a1a; --dim: #6a6a6a; --line: #e0e0e0;
                --ok: #1a7f37; --warn: #9a6700; --card: #fafafa; --code: #f3f3f3;
              }
              @media (prefers-color-scheme: dark) {
                :root {
                  --bg: #16181a; --fg: #e6e6e6; --dim: #9aa0a6; --line: #2c3033;
                  --ok: #4ac26b; --warn: #d4a72c; --card: #1c1f21; --code: #22262a;
                }
              }
              * { box-sizing: border-box; }
              body { margin: 0; padding: 24px 16px 64px; background: var(--bg); color: var(--fg);
                     font: 15px/1.6 ui-sans-serif, -apple-system, "Segoe UI", system-ui, sans-serif; }
              main { max-width: 900px; margin: 0 auto; }
              h1 { font-size: 20px; margin: 0 0 4px; font-family: ui-monospace, "SF Mono", Menlo, monospace;
                   word-break: break-all; font-weight: 600; }
              h2 { font-size: 13px; text-transform: uppercase; letter-spacing: .08em; color: var(--dim);
                   margin: 32px 0 10px; font-weight: 600; }
              .sub { color: var(--dim); font-size: 13px; margin: 0 0 24px; }
              .verdict { display: inline-block; padding: 4px 10px; border-radius: 999px; font-size: 13px;
                         font-weight: 600; border: 1px solid currentColor; }
              .verdict.ok { color: var(--ok); }
              .verdict.warn { color: var(--warn); }
              pre, code { font-family: ui-monospace, "SF Mono", Menlo, monospace; font-size: 13px; }
              pre { background: var(--code); border: 1px solid var(--line); border-radius: 6px;
                    padding: 12px 14px; overflow-x: auto; white-space: pre-wrap; word-break: break-word; }
              .card { background: var(--card); border: 1px solid var(--line); border-radius: 8px;
                      padding: 14px 16px; margin-bottom: 12px; }
              .chain { list-style: none; margin: 0; padding: 0; }
              .chain li { position: relative; padding: 6px 0 6px 26px; }
              .chain li::before { content: ""; position: absolute; left: 7px; top: 0; bottom: 0;
                                  border-left: 2px solid var(--line); }
              .chain li:first-child::before { top: 14px; }
              .chain li:last-child::before { bottom: calc(100% - 14px); }
              .chain li::after { content: ""; position: absolute; left: 3px; top: 12px; width: 9px; height: 9px;
                                 border-radius: 50%; background: var(--line); }
              .chain li.last::after { background: var(--warn); }
              .nm { font-family: ui-monospace, Menlo, monospace; font-size: 13px; word-break: break-all; }
              .mod { color: var(--dim); font-size: 12px; }
              .stats { display: flex; flex-wrap: wrap; gap: 20px; margin: 0; padding: 0; list-style: none; }
              .stats div { font-size: 12px; color: var(--dim); }
              .stats b { display: block; font-size: 20px; color: var(--fg); font-weight: 600;
                         font-variant-numeric: tabular-nums; }
              footer { margin-top: 48px; padding-top: 16px; border-top: 1px solid var(--line);
                       color: var(--dim); font-size: 12px; }
              footer code { word-break: break-all; }
              a { color: inherit; }
            </style>
            </head>
            <body>
            <main>
            """);

        sb.Append("<h1>").Append(Esc(c.Name.ToString())).Append("</h1>\n");
        sb.Append("<p class=\"sub\">").Append(Esc(c.KindName)).Append(" in <code>").Append(Esc(where))
          .Append("</code></p>\n");

        sb.Append(unconditional
            ? "<span class=\"verdict ok\">rests on nothing beyond propext, Classical.choice and Quot.sound</span>\n"
            : $"<span class=\"verdict warn\">rests on {assumptions.Count} assumption"
              + (assumptions.Count == 1 ? "" : "s") + " beyond the standard three</span>\n");

        sb.Append("<h2>Reach</h2>\n<ul class=\"stats\">")
          .Append(Stat(visited.ToString("N0", CultureInfo.InvariantCulture), "constants reached"))
          .Append(Stat(axioms.Count.ToString(CultureInfo.InvariantCulture), "axioms in total"))
          .Append(Stat(assumptions.Count.ToString(CultureInfo.InvariantCulture), "beyond the standard three"))
          .Append("</ul>\n");

        sb.Append("<h2>Statement</h2>\n<pre>").Append(Esc(ExprPrinter.Print(c.Type))).Append("</pre>\n");

        sb.Append("<h2>Axioms</h2>\n");
        if (axioms.Count == 0)
        {
            sb.Append("<div class=\"card\">none at all: this is a definition, not a proof resting on anything.</div>\n");
        }
        foreach (Name a in axioms)
        {
            bool std = standard.Contains(a);
            sb.Append("<div class=\"card\"><span class=\"nm\">").Append(Esc(a.ToString())).Append("</span> ")
              .Append(std
                  ? "<span class=\"mod\">standard: part of Lean's logic</span>"
                  : "<span class=\"mod\">an assumption this proof carries</span>")
              .Append("</div>\n");
        }

        if (chains.Count > 0)
        {
            sb.Append("<h2>How each assumption got in</h2>\n");
            foreach ((Name axiom, List<Name> path) in chains)
            {
                int steps = path.Count - 1;
                sb.Append("<div class=\"card\"><p class=\"mod\">")
                  .Append(Esc(axiom.ToString())).Append(", ").Append(steps.ToString(CultureInfo.InvariantCulture))
                  .Append(steps == 1 ? " step" : " steps").Append("</p>\n<ul class=\"chain\">\n");
                for (int i = 0; i < path.Count; i++)
                {
                    string cls = i == path.Count - 1 ? " class=\"last\"" : "";
                    string mod = definedIn.TryGetValue(path[i], out Name? pm) ? pm.ToString() : "";
                    sb.Append("<li").Append(cls).Append("><span class=\"nm\">").Append(Esc(path[i].ToString()))
                      .Append("</span>");
                    if (mod.Length > 0)
                    {
                        sb.Append(" <span class=\"mod\">").Append(Esc(mod)).Append("</span>");
                    }
                    sb.Append("</li>\n");
                }
                sb.Append("</ul>\n<p class=\"mod\">The last name above the assumption is the one that invokes it, "
                        + "and the one to act on.</p></div>\n");
            }
        }

        sb.Append("<footer>Read from <code>").Append(Esc(target))
          .Append("</code> with Tenet.Olean, without Lean installed. ")
          .Append("Generated ").Append(DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture))
          .Append(".</footer>\n</main>\n</body>\n</html>\n");
        return sb.ToString();
    }

    private static string Stat(string value, string label) =>
        $"<div><b>{Esc(value)}</b>{Esc(label)}</div>";

    private static string Esc(string s) =>
        s.Replace("&", "&amp;", StringComparison.Ordinal)
         .Replace("<", "&lt;", StringComparison.Ordinal)
         .Replace(">", "&gt;", StringComparison.Ordinal)
         .Replace("\"", "&quot;", StringComparison.Ordinal);
}
