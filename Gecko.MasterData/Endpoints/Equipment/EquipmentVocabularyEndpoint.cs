using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Gecko.Data;
using Gecko.MasterData.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.MasterData.Endpoints.Equipment;

public sealed record IsoGroupItem(string Code, string Name, bool IsReefer, bool IsOpenTop, bool IsPlatform, bool IsTank);

public sealed record EquipmentVocabularyResponse(
    IReadOnlyList<int> Lengths,
    IReadOnlyList<string> HeightClasses,
    IReadOnlyList<IsoGroupItem> IsoGroups);

/// <summary>
/// What a container-type editor offers, from where the API validates it: lengths
/// and height classes read off the request's own [AllowedValues] (the one copy),
/// ISO type groups from the global ISO 6346 reference.
/// </summary>
internal static class EquipmentVocabularyEndpoint
{
    private static readonly int[] Lengths = Allowed(nameof(CreateEquipmentTypeRequest.LengthFt)).Cast<int>().ToArray();
    private static readonly string[] HeightClasses = Allowed(nameof(CreateEquipmentTypeRequest.HeightClass)).Cast<string>().ToArray();

    public static RouteGroupBuilder MapEquipmentVocabulary(this RouteGroupBuilder master)
    {
        master.MapGet("/vocabulary/equipment", GetAsync)
            .RequirePermission(MasterDataPermissions.EquipmentView)
            .WithTags("Master data — equipment types")
            .WithSummary("Lengths, height classes and ISO type groups a container-type editor may offer");
        return master;
    }

    private static async Task<Ok<EquipmentVocabularyResponse>> GetAsync(MasterDataDbContext db, CancellationToken ct) =>
        TypedResults.Ok(new EquipmentVocabularyResponse(Lengths, HeightClasses,
            await db.IsoTypeGroups.AsNoTracking().OrderBy(g => g.GroupCode)
                .Select(g => new IsoGroupItem(g.GroupCode, g.DescriptionEn, g.IsReefer, g.IsOpenTop, g.IsPlatform, g.IsTank))
                .ToListAsync(ct)));

    private static object?[] Allowed(string property) =>
        typeof(CreateEquipmentTypeRequest).GetProperty(property)!.GetCustomAttribute<AllowedValuesAttribute>()!.Values;
}
