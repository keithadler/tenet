using Tenet.Export;
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

    [Fact]
    public void InitCore() => CheckExport("Init.Core.ndjson");
}
