using Tenet.Export;
using Tenet.Kernel;
using Xunit;

namespace Tenet.Tests;

/// <summary>
/// Integration tests over real Lean exports. They run only when <c>TENET_EXPORTS</c> points at a directory holding the
/// named files (see docs/testing.md for how to generate them) and are skipped otherwise.
/// </summary>
public class LargeExportTests
{
    private static string? Find(string name)
    {
        string? dir = System.Environment.GetEnvironmentVariable("TENET_EXPORTS");
        if (string.IsNullOrEmpty(dir))
        {
            return null;
        }
        string path = Path.Combine(dir, name);
        return File.Exists(path) ? path : null;
    }

    private static void CheckExport(string name)
    {
        string? path = Find(name);
        if (path is null)
        {
            return; // not available: skip quietly
        }
        CheckResult result = RunOnBigStack(() => ExportChecker.Check(NdjsonReader.ReadFile(path)));
        Assert.True(result.Success, $"{result.Failures.Count} failures:\n" + string.Join("\n", result.Failures.Take(10).Select(f => f.Name + ": " + f.Message)));
    }

    private static T RunOnBigStack<T>(Func<T> f)
    {
        T? result = default;
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
        }, 1024 * 1024 * 1024);
        t.Start();
        t.Join();
        if (error is not null)
        {
            throw new InvalidOperationException("check failed", error);
        }
        return result!;
    }

    [Fact]
    public void InitPrelude() => CheckExport("Init.Prelude.ndjson");

    /// <summary>
    /// Non-vacuity: give every theorem in the prelude the proof of the previous theorem. A checker that is
    /// doing its job must reject nearly all of them (a handful may coincide, e.g. proofs of `rfl`).
    /// </summary>
    [Fact]
    public void InitPreludeRejectsSwappedProofs()
    {
        string? path = Find("Init.Prelude.ndjson");
        if (path is null)
        {
            return;
        }
        ExportFile file = NdjsonReader.ReadFile(path);
        var theorems = file.Decls.OfType<ExportTheorem>().ToList();
        Assert.True(theorems.Count > 100, "prelude should contain theorems");
        var swapped = new Dictionary<Name, Expr>();
        for (int i = 1; i < theorems.Count; i++)
        {
            swapped[theorems[i].Name] = theorems[i - 1].Value;
        }
        var tampered = new ExportFile();
        foreach (ExportDecl d in file.Decls)
        {
            tampered.Decls.Add(d is ExportTheorem t && swapped.TryGetValue(t.Name, out Expr? v) ? t with { Value = v } : d);
        }
        CheckResult result = RunOnBigStack(() => ExportChecker.Check(tampered));
        int rejected = result.Failures.Count(f => swapped.ContainsKey(f.Name));
        Assert.True(rejected >= swapped.Count * 95 / 100, $"only {rejected} of {swapped.Count} swapped proofs were rejected");
        // and nothing that was left alone should have failed
        Assert.Empty(result.Failures.Where(f => !swapped.ContainsKey(f.Name)));
    }

    [Fact]
    public void InitCore() => CheckExport("Init.Core.ndjson");
}
