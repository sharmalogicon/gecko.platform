using System.Reflection;
using Gecko.Identity;
using Gecko.MasterData;
using Gecko.Notification;
using Gecko.Revenue;
using Gecko.Tos;
using NetArchTest.Rules;

namespace Gecko.Architecture.Tests;

/// <summary>
/// The rules that make this a MODULAR monolith rather than a big ball of mud.
/// Without these tests the boundaries are a folder naming convention, and the
/// first deadline-week shortcut (TOS calling Identity's DbContext) goes in
/// unnoticed. With them, it fails the build.
/// </summary>
public class ModuleBoundaryTests
{
    private static readonly Assembly[] ModuleAssemblies =
    [
        typeof(IdentityModule).Assembly,
        typeof(MasterDataModule).Assembly,
        typeof(RevenueModule).Assembly,
        typeof(TosModule).Assembly,
        typeof(NotificationModule).Assembly,
    ];

    public static TheoryData<string> Modules => new(ModuleAssemblies.Select(a => a.GetName().Name!));

    /// <summary>
    /// A module may use another module's .Contracts, never its internals.
    /// Checked on compiled metadata, so an unused ProjectReference is harmless
    /// but any type actually used from another module is caught.
    /// </summary>
    [Theory]
    [MemberData(nameof(Modules))]
    public void Module_does_not_depend_on_another_modules_internals(string moduleName)
    {
        var module = ModuleAssemblies.Single(a => a.GetName().Name == moduleName);
        var otherModules = ModuleAssemblies.Where(a => a != module).Select(a => a.GetName().Name!).ToHashSet();

        var violations = module.GetReferencedAssemblies()
            .Select(r => r.Name!)
            .Where(otherModules.Contains)
            .ToList();

        Assert.True(violations.Count == 0,
            $"{moduleName} depends on {string.Join(", ", violations)} — reference its .Contracts project instead.");
    }

    /// <summary>Contracts are the public surface: they may not pull in any module, or in EF / ASP.NET.</summary>
    [Theory]
    [MemberData(nameof(Modules))]
    public void Contracts_depend_only_on_shared_kernel(string moduleName)
    {
        var contracts = Assembly.Load(moduleName + ".Contracts");

        // Checked on ASSEMBLY references, not namespaces. NetArchTest matches a
        // namespace by prefix, so "Gecko.MasterData" also matched
        // "Gecko.MasterData.Contracts" and every contract type was reported for
        // depending on its own neighbours — unnoticed while all Contracts
        // projects were empty, found the day the first real contract was added.
        var forbiddenExact = ModuleAssemblies.Select(a => a.GetName().Name!).Append("Gecko.Data").ToHashSet();
        string[] forbiddenPrefixes = ["Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Microsoft.Data.SqlClient"];

        var violations = contracts.GetReferencedAssemblies()
            .Select(r => r.Name!)
            .Where(n => forbiddenExact.Contains(n) || forbiddenPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)))
            .ToList();

        Assert.True(violations.Count == 0, $"{moduleName}.Contracts depends on {string.Join(", ", violations)}.");
    }

    /// <summary>
    /// Inside a module, Domain is pure: no EF Core, no ASP.NET, no Infrastructure.
    /// The 2-project layout gives up compiler-enforced layering; this test is what buys it back.
    /// </summary>
    [Theory]
    [MemberData(nameof(Modules))]
    public void Domain_does_not_depend_on_infrastructure(string moduleName)
    {
        var module = ModuleAssemblies.Single(a => a.GetName().Name == moduleName);

        var result = Types.InAssembly(module)
            .That().ResideInNamespace($"{moduleName}.Domain")
            .ShouldNot()
            .HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore",
                "Microsoft.Data.SqlClient",
                "Gecko.Data",
                $"{moduleName}.Infrastructure")
            .GetResult();

        Assert.True(result.IsSuccessful, Failures(result));
    }

    private static string Failures(NetArchTest.Rules.TestResult result) =>
        "Violating types: " + string.Join(", ", result.FailingTypeNames ?? []);
}
