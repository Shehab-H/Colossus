using System.Xml.Linq;
using Colossus.Application;
using Colossus.Domain.Model;
using Xunit;

namespace Colossus.Tests;

public class ArchitectureBoundaryTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void BackendProjectReferences_PointInward()
    {
        Assert.Empty(ProjectReferences("Colossus.Domain"));
        Assert.Equal(["Colossus.Domain"], ProjectReferences("Colossus.Application"));
        Assert.Equal(
            ["Colossus.Application", "Colossus.Domain"],
            ProjectReferences("Colossus.Infrastructure").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void DomainAndApplicationAssemblies_DoNotReferenceOuterFrameworks()
    {
        AssertNoAssemblyReferences(typeof(ViewConfig).Assembly,
            "Colossus.Application", "Colossus.Infrastructure", "Microsoft.AspNetCore", "Hangfire");
        AssertNoAssemblyReferences(typeof(BakeViewUseCase).Assembly,
            "Colossus.Infrastructure", "Microsoft.AspNetCore", "Hangfire");
    }

    [Fact]
    public void DomainAndApplication_DoNotCallHostOrFilesystemApisDirectly()
    {
        string[] forbidden =
        [
            "System.IO",
            "File.",
            "new FileInfo",
            "Directory.",
            "Path.",
            "Console.",
            "Environment.",
            "Microsoft.AspNetCore",
            "Hangfire",
            "Colossus.Infrastructure",
        ];

        var violations = new List<string>();
        foreach (var project in new[] { "Colossus.Domain", "Colossus.Application" })
        {
            string root = Path.Combine(RepoRoot, "src", project);
            foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                         .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
            {
                string source = File.ReadAllText(file);
                foreach (string token in forbidden.Where(source.Contains))
                    violations.Add($"{Path.GetRelativePath(RepoRoot, file)} contains '{token}'");
            }
        }

        Assert.Empty(violations);
    }

    private static IReadOnlyList<string> ProjectReferences(string project)
    {
        string projectFile = Path.Combine(RepoRoot, "src", project, project + ".csproj");
        return XDocument.Load(projectFile).Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension((string)e.Attribute("Include")!))
            .ToArray();
    }

    private static void AssertNoAssemblyReferences(System.Reflection.Assembly assembly, params string[] forbidden)
    {
        string[] references = assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToArray();
        Assert.DoesNotContain(references,
            reference => forbidden.Any(prefix => reference.StartsWith(prefix, StringComparison.Ordinal)));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Colossus.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the Colossus repository root.");
    }
}
