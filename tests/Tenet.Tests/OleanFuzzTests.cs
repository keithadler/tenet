using Tenet.Kernel;
using Tenet.Olean;
using Xunit;

namespace Tenet.Tests;

/// <summary>
/// Corrupted .olean files must fail with <see cref="OleanFormatException"/> (or, when the damage lands in unused
/// bytes, decode as before). A memory-mapped reader that trusts stored pointers or sizes would instead read outside
/// the mapping and kill the process, or loop on a pointer cycle; every raw read is bounds-checked and every stored
/// pointer must lead to an earlier object, which is what these tests exercise.
/// </summary>
public class OleanFuzzTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "olean", name);

    private static int Iterations =>
        int.TryParse(System.Environment.GetEnvironmentVariable("TENET_FUZZ_ITERATIONS"), out int n) && n > 0 ? n : 400;

    [Fact]
    public void FixtureDecodesCleanly()
    {
        using var m = new OleanModule(Fixture("Coe.olean"));
        Assert.Equal("4.34.0-rc2", m.LeanVersion);
        Assert.True(m.IsModule);
        Assert.True(m.PublicConstantCount > 100);
        int n = 0;
        foreach (ConstantInfo c in m.DecodeAll())
        {
            Assert.NotNull(c.Type);
            n++;
        }
        Assert.Equal(m.ConstantNames.Count, n);
    }

    [Fact]
    public void CorruptedModulesFailCleanly()
    {
        var rng = new Random(2026_09_08);
        string dir = Path.Combine(Path.GetTempPath(), "tenet-fuzz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var failures = new List<string>();
        int clean = 0, rejected = 0;
        try
        {
            byte[] pub = File.ReadAllBytes(Fixture("Coe.olean"));
            byte[] priv = File.ReadAllBytes(Fixture("Coe.olean.private"));
            for (int it = 0; it < Iterations; it++)
            {
                byte[] p = (byte[])pub.Clone();
                byte[] q = (byte[])priv.Clone();
                bool onPublic = rng.Next(10) < 7;
                byte[] target = onPublic ? p : q;
                string how = Corrupt(rng, ref target);
                string path = Path.Combine(dir, $"Coe{it}.olean");
                File.WriteAllBytes(path, onPublic ? target : p);
                File.WriteAllBytes(path + ".private", onPublic ? q : target);
                try
                {
                    using var m = new OleanModule(path);
                    foreach (ConstantInfo c in m.DecodeAll())
                    {
                        _ = c.Type;
                    }
                    clean++;
                }
                catch (OleanFormatException)
                {
                    rejected++;
                }
                catch (Exception ex)
                {
                    failures.Add($"iteration {it} ({how} on {(onPublic ? "public" : "private")} part): {ex.GetType().Name}: {ex.Message}");
                }
                File.Delete(path);
                File.Delete(path + ".private");
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(10)));
        Assert.True(rejected > 0, $"no corruption was detected in {Iterations} iterations ({clean} decoded cleanly)");
    }

    /// <summary>Apply one random corruption; may replace the array with a shorter one.</summary>
    private static string Corrupt(Random rng, ref byte[] data)
    {
        int minOffset = 5; // keep the "olean" marker so the reader gets past the front door
        switch (rng.Next(5))
        {
            case 0:
                {
                    int count = 1 + rng.Next(16);
                    for (int i = 0; i < count; i++)
                    {
                        data[minOffset + rng.Next(data.Length - minOffset)] = (byte)rng.Next(256);
                    }
                    return $"{count} random bytes";
                }
            case 1:
                {
                    int at = minOffset + rng.Next(data.Length - minOffset);
                    int bit = rng.Next(8);
                    data[at] ^= (byte)(1 << bit);
                    return $"bit {bit} at 0x{at:x}";
                }
            case 2:
                {
                    int at = minOffset + rng.Next(data.Length - minOffset - 8);
                    at &= ~7; // a whole stored word: pointers and sizes live on word boundaries
                    ulong v = rng.Next(3) switch
                    {
                        0 => ulong.MaxValue,
                        1 => (ulong)rng.NextInt64(),
                        _ => BitConverter.ToUInt64(data, at) + (ulong)(rng.Next(65) - 32) * 8,
                    };
                    BitConverter.GetBytes(v).CopyTo(data, at);
                    return $"word at 0x{at:x} := 0x{v:x}";
                }
            case 3:
                {
                    int len = minOffset + 1 + rng.Next(data.Length - minOffset - 1);
                    data = data[..len];
                    return $"truncated to {len} bytes";
                }
            default:
                {
                    int at = minOffset + rng.Next(data.Length - minOffset);
                    int len = Math.Min(64, data.Length - at);
                    Array.Clear(data, at, len);
                    return $"{len} zero bytes at 0x{at:x}";
                }
        }
    }
}
