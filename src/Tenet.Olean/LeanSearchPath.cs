using Tenet.Kernel;

namespace Tenet.Olean;

/// <summary>
/// Where compiled modules live: a list of library roots, each laid out as <c>Root/A/B/C.olean</c> for module <c>A.B.C</c>.
/// Built from explicit directories, the <c>LEAN_PATH</c> environment variable, the Lake layout around a given
/// <c>.olean</c> file, and the elan toolchain matching a module's Lean version.
/// </summary>
public sealed class LeanSearchPath
{
    private readonly List<string> _roots = new();

    public IReadOnlyList<string> Roots => _roots;

    public void Add(string root)
    {
        string full = Path.GetFullPath(root);
        if (Directory.Exists(full) && !_roots.Contains(full))
        {
            _roots.Add(full);
        }
    }

    /// <summary>Roots from <c>LEAN_PATH</c> (colon- or semicolon-separated), if set.</summary>
    public void AddFromEnvironment()
    {
        string? lp = System.Environment.GetEnvironmentVariable("LEAN_PATH");
        if (string.IsNullOrEmpty(lp))
        {
            return;
        }
        foreach (string p in lp.Split(Path.PathSeparator == ';' ? ';' : ':', StringSplitOptions.RemoveEmptyEntries))
        {
            Add(p);
        }
    }

    /// <summary>
    /// Given a path inside a Lake build tree (<c>…/.lake/build/lib/lean/A/B.olean</c>), add that library root and the roots of
    /// every package under the workspace's <c>.lake/packages</c>.
    /// </summary>
    public void AddAroundOleanFile(string oleanPath)
    {
        string full = Path.GetFullPath(oleanPath);
        int i = full.IndexOf(Path.Combine(".lake", "build", "lib", "lean"), StringComparison.Ordinal);
        if (i < 0)
        {
            // maybe a toolchain path: lib/lean/A/B.olean; walk up until a directory containing Init/Prelude.olean
            for (string? d = Path.GetDirectoryName(full); d is not null; d = Path.GetDirectoryName(d))
            {
                if (File.Exists(Path.Combine(d, "Init", "Prelude.olean")) || File.Exists(Path.Combine(d, "Lean.olean")) || File.Exists(Path.Combine(d, "Mathlib.olean")))
                {
                    Add(d);
                    break;
                }
            }
            return;
        }
        string libRoot = full[..(i + ".lake/build/lib/lean".Length)];
        Add(libRoot);
        string workspace = full[..i];
        string packages = Path.Combine(workspace, ".lake", "packages");
        if (Directory.Exists(packages))
        {
            foreach (string pkg in Directory.GetDirectories(packages))
            {
                Add(Path.Combine(pkg, ".lake", "build", "lib", "lean"));
            }
        }
        // a package's own workspace may itself be inside another workspace's .lake/packages
        int j = workspace.IndexOf(Path.Combine(".lake", "packages"), StringComparison.Ordinal);
        if (j >= 0)
        {
            string outer = workspace[..j];
            Add(Path.Combine(outer, ".lake", "build", "lib", "lean"));
            string outerPackages = Path.Combine(outer, ".lake", "packages");
            if (Directory.Exists(outerPackages))
            {
                foreach (string pkg in Directory.GetDirectories(outerPackages))
                {
                    Add(Path.Combine(pkg, ".lake", "build", "lib", "lean"));
                }
            }
        }
    }

    /// <summary>The elan toolchain whose Lean version string matches, if installed under <c>~/.elan/toolchains</c>.</summary>
    public void AddToolchainFor(string leanVersion)
    {
        string? sysroot = System.Environment.GetEnvironmentVariable("LEAN_SYSROOT");
        if (!string.IsNullOrEmpty(sysroot))
        {
            Add(Path.Combine(sysroot, "lib", "lean"));
        }
        string home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        string toolchains = Path.Combine(home, ".elan", "toolchains");
        if (!Directory.Exists(toolchains))
        {
            return;
        }
        foreach (string dir in Directory.GetDirectories(toolchains))
        {
            string lib = Path.Combine(dir, "lib", "lean");
            string prelude = Path.Combine(lib, "Init", "Prelude.olean");
            if (!File.Exists(prelude))
            {
                continue;
            }
            // cheap version check from the header, without decoding anything
            try
            {
                using var r = new Region(prelude);
                if (r.LeanVersion == leanVersion)
                {
                    Add(lib);
                    return;
                }
            }
            catch (OleanFormatException)
            {
            }
        }
    }

    /// <summary>Resolve a module name to its <c>.olean</c> file, or null.</summary>
    public string? Find(Name module)
    {
        string rel = ModuleRelativePath(module);
        foreach (string root in _roots)
        {
            string p = Path.Combine(root, rel);
            if (File.Exists(p))
            {
                return p;
            }
        }
        return null;
    }

    public static string ModuleRelativePath(Name module)
    {
        var parts = new List<string>();
        for (Name n = module; !n.IsAnonymous; n = n.Prefix)
        {
            parts.Add(n.TryGetStr(out _, out string? s) ? s : n.ToString().Split('.')[^1]);
        }
        parts.Reverse();
        return Path.Combine(parts.ToArray()) + ".olean";
    }

    /// <summary>Module name for an <c>.olean</c> path under one of the roots, or a best guess from the path.</summary>
    public Name ModuleNameOf(string oleanPath)
    {
        string full = Path.GetFullPath(oleanPath);
        foreach (string root in _roots)
        {
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                string rel = full[(root.Length + 1)..];
                return FromRelative(rel);
            }
        }
        return FromRelative(Path.GetFileName(full));
    }

    private static Name FromRelative(string rel)
    {
        if (rel.EndsWith(".olean", StringComparison.Ordinal))
        {
            rel = rel[..^".olean".Length];
        }
        Name n = Name.Anonymous;
        foreach (string part in rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            n = n.Str(part);
        }
        return n;
    }
}
