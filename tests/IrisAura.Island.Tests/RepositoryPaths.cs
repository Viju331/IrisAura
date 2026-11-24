using System.IO;

namespace IrisAura.Island.Tests;

/// <summary>Locates the repository root from the test output directory.</summary>
internal static class RepositoryPaths
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "IrisAura.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find IrisAura.sln above " + AppContext.BaseDirectory);
    }
}
