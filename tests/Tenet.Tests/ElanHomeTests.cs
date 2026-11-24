using Tenet.Olean;
using Xunit;

namespace Tenet.Tests;

/// <summary>These tests set process environment variables, so they run alone rather than beside other tests.</summary>
[CollectionDefinition("process environment", DisableParallelization = true)]
public class ProcessEnvironmentCollection
{
}

/// <summary>
/// Toolchain lookup must follow <c>ELAN_HOME</c> the way elan does. Before it did, a machine whose elan lives
/// elsewhere (CI containers, a shared toolchain directory, a home without <c>~/.elan</c>) failed with
/// "cannot find module 'Init'" although the matching toolchain was installed.
/// </summary>
[Collection("process environment")]
public class ElanHomeTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "olean", name);

    [Fact]
    public void ToolchainUnderElanHomeIsFound()
    {
        string version;
        using (var m = new OleanModule(Fixture("Coe.olean")))
        {
            version = m.LeanVersion;
        }
        // A fake toolchain: AddToolchainFor only reads the header of Init/Prelude.olean to match the version.
        string elanHome = Path.Combine(Path.GetTempPath(), "tenet-elan-home-" + Guid.NewGuid().ToString("N"));
        string lib = Path.Combine(elanHome, "toolchains", "leanprover--lean4---fake", "lib", "lean");
        Directory.CreateDirectory(Path.Combine(lib, "Init"));
        File.Copy(Fixture("Coe.olean"), Path.Combine(lib, "Init", "Prelude.olean"));
        string? saved = System.Environment.GetEnvironmentVariable("ELAN_HOME");
        string? savedSysroot = System.Environment.GetEnvironmentVariable("LEAN_SYSROOT");
        try
        {
            System.Environment.SetEnvironmentVariable("ELAN_HOME", elanHome);
            System.Environment.SetEnvironmentVariable("LEAN_SYSROOT", null);
            Assert.Equal(Path.Combine(elanHome, "toolchains"), LeanSearchPath.ElanToolchains());

            var search = new LeanSearchPath();
            search.AddToolchainFor(version);
            Assert.Contains(Path.GetFullPath(lib), search.Roots);

            var other = new LeanSearchPath();
            other.AddToolchainFor("0.0.0-not-installed");
            Assert.Empty(other.Roots);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("ELAN_HOME", saved);
            System.Environment.SetEnvironmentVariable("LEAN_SYSROOT", savedSysroot);
            Directory.Delete(elanHome, recursive: true);
        }
    }

    [Fact]
    public void WithoutElanHomeTheDefaultIsDotElan()
    {
        string? saved = System.Environment.GetEnvironmentVariable("ELAN_HOME");
        try
        {
            System.Environment.SetEnvironmentVariable("ELAN_HOME", null);
            string home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            Assert.Equal(Path.Combine(home, ".elan", "toolchains"), LeanSearchPath.ElanToolchains());
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("ELAN_HOME", saved);
        }
    }
}
