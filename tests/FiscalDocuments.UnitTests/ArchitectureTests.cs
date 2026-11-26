using System.Reflection;
using System.Xml.Linq;
using FiscalDocuments.Application;
using FiscalDocuments.Domain;
using NUnit.Framework;

namespace FiscalDocuments.UnitTests;

[TestFixture, Category("Architecture")]
public sealed class ArchitectureTests
{
    private string repositoryPath = null!;

    public static IEnumerable<TestCaseData> ProjectRules =>
    [
        new TestCaseData("Domain", Array.Empty<string>()),
        new TestCaseData("Application", new[] { "Domain" }),
        new TestCaseData("Infrastructure", new[] { "Application", "Domain" }),
        new TestCaseData("Api", new[] { "Application", "Domain", "Infrastructure" }),
        new TestCaseData("Worker", new[] { "Application", "Domain", "Infrastructure" })
    ];

    public static IEnumerable<TestCaseData> CoreAssemblyRules =>
    [
        new TestCaseData(typeof(FiscalDocument).Assembly, Array.Empty<string>(),
            new[] { "MediatR", "Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore", "RabbitMQ" })
            .SetName("Domain_assembly_is_independent_of_application_and_infrastructure"),
        new TestCaseData(typeof(ReceiveDocument).Assembly, new[] { "FiscalDocuments.Domain" },
            new[] { "Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore", "RabbitMQ" })
            .SetName("Application_assembly_depends_only_on_domain_and_permitted_libraries")
    ];

    [OneTimeSetUp]
    public void FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
             directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FiscalDocuments.sln")))
            {
                repositoryPath = directory.FullName;
                return;
            }
        }
        Assert.Fail("Não foi possível localizar FiscalDocuments.sln a partir do diretório dos testes.");
    }

    [TestCaseSource(nameof(ProjectRules))]
    public void Project_references_follow_layer_boundaries(string layer, string[] allowedLayers)
    {
        var project = "FiscalDocuments." + layer;
        var projectPath = Path.Combine(repositoryPath, "src", project, project + ".csproj");
        var allowed = allowedLayers.Select(name => "FiscalDocuments." + name).ToHashSet(StringComparer.Ordinal);
        var forbidden = XDocument.Load(projectPath).Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => (string?)element.Attribute("Include") ?? "")
            .Where(reference => !allowed.Contains(Path.GetFileNameWithoutExtension(reference.Replace('\\', '/'))))
            .ToArray();
        Assert.That(forbidden, Is.Empty,
            $"{project} declara referências de projeto proibidas: {string.Join(", ", forbidden)}.");
    }

    [TestCaseSource(nameof(CoreAssemblyRules))]
    public void Core_assemblies_do_not_reference_forbidden_dependencies(Assembly assembly, string[] allowedProjects, string[] forbiddenFamilies)
    {
        var forbidden = assembly.GetReferencedAssemblies().Select(reference => reference.Name!)
            .Where(name => name.StartsWith("FiscalDocuments.", StringComparison.Ordinal)
                    && !allowedProjects.Contains(name, StringComparer.Ordinal)
                || forbiddenFamilies.Any(family => name == family || name.StartsWith(family + ".", StringComparison.Ordinal)))
            .ToArray();
        Assert.That(forbidden, Is.Empty,
            $"{assembly.GetName().Name} depende de assemblies proibidas: {string.Join(", ", forbidden)}.");
    }

    [Test]
    public void Domain_project_has_no_package_references()
    {
        var projectPath = Path.Combine(repositoryPath, "src", "FiscalDocuments.Domain", "FiscalDocuments.Domain.csproj");
        var packages = XDocument.Load(projectPath).Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => element.ToString(SaveOptions.DisableFormatting)).ToArray();
        Assert.That(packages, Is.Empty,
            $"FiscalDocuments.Domain não deve declarar PackageReference: {string.Join(", ", packages)}.");
    }
}
