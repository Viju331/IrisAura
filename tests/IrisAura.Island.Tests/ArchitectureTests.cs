using System.IO;
using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;

namespace IrisAura.Island.Tests;

/// <summary>
/// Enforces the Release 1 dependency rule (BLUEPRINT §6): IrisAura Island must install and run
/// without any IrisAura Face component, so no Island project may reference a Face project.
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly string[] IslandAllowedProjectReferences =
    [
        "IrisAura.Contracts",
        "IrisAura.Theme",
        "IrisAura.Island.Providers",
        "IrisAura.Island.Sdk",
    ];

    public static TheoryData<string> IslandProjects() =>
        new(Directory.GetDirectories(Path.Combine(RepositoryPaths.Root, "src"), "IrisAura.Island*")
            .Select(Path.GetFileName)
            .OfType<string>());

    [Theory]
    [MemberData(nameof(IslandProjects))]
    public void Island_projects_reference_only_shared_and_island_projects(string projectName)
    {
        var csproj = Path.Combine(RepositoryPaths.Root, "src", projectName, projectName + ".csproj");
        var references = XDocument.Load(csproj)
            .Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension((string?)e.Attribute("Include") ?? string.Empty))
            .ToList();

        references.Should().NotContain(r => r.StartsWith("IrisAura.Face", StringComparison.OrdinalIgnoreCase),
            "IrisAura Island must ship without Face components (BLUEPRINT §6)");
        references.Should().OnlyContain(r => IslandAllowedProjectReferences.Contains(r),
            "Island projects may reference only Contracts, Theme and Island projects");
    }

    [Fact]
    public void Island_assemblies_do_not_load_face_assemblies()
    {
        Assembly[] islandAssemblies =
        [
            typeof(App).Assembly,
            Assembly.Load("IrisAura.Island.Providers"),
            Assembly.Load("IrisAura.Island.Sdk"),
            Assembly.Load("irisaura-island"),
        ];

        foreach (var assembly in islandAssemblies)
        {
            assembly.GetReferencedAssemblies()
                .Should().NotContain(a => a.Name != null && a.Name.StartsWith("IrisAura.Face", StringComparison.OrdinalIgnoreCase),
                    $"{assembly.GetName().Name} must not depend on IrisAura Face");
        }
    }

    [Fact]
    public void Theme_references_no_other_irisaura_project()
    {
        var csproj = Path.Combine(RepositoryPaths.Root, "src", "IrisAura.Theme", "IrisAura.Theme.csproj");
        XDocument.Load(csproj).Descendants("ProjectReference").Should().BeEmpty(
            "IrisAura.Theme is a leaf library shared by the Island and Face Lab");
    }
}
