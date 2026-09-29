using System.ComponentModel.DataAnnotations;
using Gecko.Data;
using Gecko.MasterData.Infrastructure.Persistence;
using Gecko.MasterData.Infrastructure.Persistence.Entities;
using Gecko.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.MasterData.Endpoints.Org;

public sealed record YardBlockResponse(
    Guid YardBlockId, Guid YardId, string BlockCode, byte? MaxRows, byte? MaxBays, byte? MaxTiers,
    string? AllocatedToPartyCode, byte? AllocatedSizeFt, bool IsReeferBlock, bool IsDgBlock, bool IsOogBlock,
    int? LayoutX, int? LayoutY, int? LayoutWidth, int? LayoutHeight, short? LayoutRotationDeg, string? DisplayColorHex,
    bool IsActive, int RowCount, int SlotCount, string RowVersion);

public sealed record SaveYardBlockRequest(
    [property: Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9 ._-]{0,19}$", ErrorMessage = "Letters, digits, space, '.', '_' and '-', up to 20.")] string BlockCode,
    [property: Range(1, 255)] byte? MaxRows = null,
    [property: Range(1, 255)] byte? MaxBays = null,
    [property: Range(1, 255)] byte? MaxTiers = null,
    [property: MaxLength(60)] string? AllocatedToPartyCode = null,
    [property: AllowedValues(null, (byte)20, (byte)40, (byte)45)] byte? AllocatedSizeFt = null,
    bool IsReeferBlock = false, bool IsDgBlock = false, bool IsOogBlock = false,
    int? LayoutX = null, int? LayoutY = null, int? LayoutWidth = null, int? LayoutHeight = null,
    [property: Range(-360, 360)] short? LayoutRotationDeg = null,
    [property: RegularExpression("^#[0-9A-Fa-f]{6}$", ErrorMessage = "A colour is #RRGGBB.")] string? DisplayColorHex = null,
    bool IsActive = true,
    string? RowVersion = null);

public sealed record YardRowResponse(
    Guid YardRowId, Guid YardBlockId, string RowLabel, string? DescriptionEn, bool IsReeferRow, bool IsOogRow,
    short? ReeferPlugCount, bool IsBlocked, bool IsActive, int SlotCount, string RowVersion);

public sealed record SaveYardRowRequest(
    [property: Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9-]{0,9}$", ErrorMessage = "Letters, digits and '-', up to 10.")] string RowLabel,
    [property: MaxLength(100)] string? DescriptionEn = null,
    bool IsReeferRow = false, bool IsOogRow = false,
    [property: Range(0, 1000)] short? ReeferPlugCount = null,
    bool IsBlocked = false,
    bool IsActive = true,
    string? RowVersion = null);

/// <summary>
/// A yard's layout (Tier 3): blocks, and the rows inside a block. Slots
/// (row × bay × tier) are listed as a count only — generating a slot map and
/// blocking a slot are operational, not master data. KORAKIT locates boxes at
/// yard level, so its yards have no blocks; nothing is back-loaded. Reads need
/// mdm.org.view; changes need mdm.org.manage AT the yard's depot. A block or
/// row that still has slots cannot be deleted.
/// </summary>
internal static class YardLayoutEndpoints
{
    public static RouteGroupBuilder MapYardLayoutEndpoints(this RouteGroupBuilder master)
    {
        var tags = "Master data — organisation";
        master.MapGet("/yards/{yardId:guid}/blocks", ListBlocksAsync).RequireBranchPermission(MasterDataPermissions.OrgView).WithTags(tags).WithSummary("A yard's blocks with their row and slot counts");
        master.MapPost("/yards/{yardId:guid}/blocks", CreateBlockAsync).RequireBranchPermission(MasterDataPermissions.OrgManage).Validate<SaveYardBlockRequest>().WithTags(tags).WithSummary("Add a block to a yard");
        master.MapPut("/yard-blocks/{blockId:guid}", UpdateBlockAsync).RequireBranchPermission(MasterDataPermissions.OrgManage).Validate<SaveYardBlockRequest>().WithTags(tags).WithSummary("Update a block (optimistic concurrency on rowVersion)");
        master.MapDelete("/yard-blocks/{blockId:guid}", DeleteBlockAsync).RequireBranchPermission(MasterDataPermissions.OrgManage).WithTags(tags).WithSummary("Soft-delete a block with no rows or slots (?rowVersion=)");
        master.MapGet("/yard-blocks/{blockId:guid}/rows", ListRowsAsync).RequireBranchPermission(MasterDataPermissions.OrgView).WithTags(tags).WithSummary("A block's rows");
        master.MapPost("/yard-blocks/{blockId:guid}/rows", CreateRowAsync).RequireBranchPermission(MasterDataPermissions.OrgManage).Validate<SaveYardRowRequest>().WithTags(tags).WithSummary("Add a row to a block");
        master.MapPut("/yard-rows/{rowId:guid}", UpdateRowAsync).RequireBranchPermission(MasterDataPermissions.OrgManage).Validate<SaveYardRowRequest>().WithTags(tags).WithSummary("Update a row (optimistic concurrency on rowVersion)");
        master.MapDelete("/yard-rows/{rowId:guid}", DeleteRowAsync).RequireBranchPermission(MasterDataPermissions.OrgManage).WithTags(tags).WithSummary("Soft-delete a row with no slots (?rowVersion=)");
        return master;
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static Task<Guid?> BranchOfYardAsync(MasterDataDbContext db, Guid yardId, CancellationToken ct) =>
        db.Yards.AsNoTracking().Where(y => y.YardId == yardId).Select(y => (Guid?)y.BranchId).SingleOrDefaultAsync(ct);

    private static Task<Guid?> BranchOfBlockAsync(MasterDataDbContext db, Guid blockId, CancellationToken ct) =>
        (from b in db.YardBlocks.AsNoTracking() join y in db.Yards.AsNoTracking() on b.YardId equals y.YardId
         where b.YardBlockId == blockId select (Guid?)y.BranchId).SingleOrDefaultAsync(ct);

    private static IQueryable<YardBlockResponse> Blocks(MasterDataDbContext db, IQueryable<YardBlock> blocks) =>
        from b in blocks
        join p in db.Parties.AsNoTracking() on b.AllocatedToPartyId equals (Guid?)p.PartyId into parties
        from p in parties.DefaultIfEmpty()
        select new YardBlockResponse(
            b.YardBlockId, b.YardId, b.BlockCode, b.MaxRows, b.MaxBays, b.MaxTiers, p == null ? null : p.PartyCode,
            b.AllocatedSizeFt, b.IsReeferBlock, b.IsDgBlock, b.IsOogBlock, b.LayoutX, b.LayoutY, b.LayoutWidth, b.LayoutHeight,
            b.LayoutRotationDeg, b.DisplayColorHex, b.IsActive,
            db.YardRows.Count(r => r.YardBlockId == b.YardBlockId), db.YardSlots.Count(s => s.YardBlockId == b.YardBlockId),
            Convert.ToBase64String(b.RowVersion));

    private static IQueryable<YardRowResponse> Rows(MasterDataDbContext db, IQueryable<YardRow> rows) =>
        rows.Select(r => new YardRowResponse(
            r.YardRowId, r.YardBlockId, r.RowLabel, r.DescriptionEn, r.IsReeferRow, r.IsOogRow, r.ReeferPlugCount, r.IsBlocked, r.IsActive,
            db.YardSlots.Count(s => s.YardBlockId == r.YardBlockId && s.RowLabel == r.RowLabel), Convert.ToBase64String(r.RowVersion)));

    // ── blocks ──────────────────────────────────────────────────────────────

    private static async Task<Results<Ok<IReadOnlyList<YardBlockResponse>>, NotFound>> ListBlocksAsync(Guid yardId, MasterDataDbContext db, CancellationToken ct)
    {
        if (await BranchOfYardAsync(db, yardId, ct) is null) return TypedResults.NotFound();
        return TypedResults.Ok<IReadOnlyList<YardBlockResponse>>(
            await Blocks(db, db.YardBlocks.AsNoTracking().Where(b => b.YardId == yardId).OrderBy(b => b.BlockCode)).ToListAsync(ct));
    }

    private static async Task<(Guid? PartyId, ValidationProblem? Invalid)> CheckBlockAsync(MasterDataDbContext db, SaveYardBlockRequest r, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var partyId = await Logistics.LogisticsSupport.PartyAsync(db, "allocatedToPartyCode", Logistics.LogisticsSupport.Upper(r.AllocatedToPartyCode), lineOnly: false, errors, ct);
        return (partyId, Logistics.LogisticsSupport.Problem(errors));
    }

    private static async Task<Results<Created<YardBlockResponse>, NotFound, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> CreateBlockAsync(
        Guid yardId, SaveYardBlockRequest request, MasterDataDbContext db, ITenantContext caller, ICallerPermissions scope, CancellationToken ct)
    {
        if (await BranchOfYardAsync(db, yardId, ct) is not { } branch) return TypedResults.NotFound();
        if (!scope.HasAt(MasterDataPermissions.OrgManage, branch)) return TypedResults.Forbid();
        var (partyId, invalid) = await CheckBlockAsync(db, request, ct);
        if (invalid is not null) return invalid;
        var code = request.BlockCode.Trim().ToUpperInvariant();
        if (await db.YardBlocks.AnyAsync(b => b.YardId == yardId && b.BlockCode == code, ct))
            return MasterDataSupport.Conflict($"Block '{code}' already exists in this yard.");

        var block = new YardBlock { TenantId = caller.TenantId(), YardId = yardId, BlockCode = code };
        ApplyBlock(block, request, partyId);
        db.YardBlocks.Add(block);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/master/yard-blocks/{block.YardBlockId}",
            await Blocks(db, db.YardBlocks.AsNoTracking().Where(b => b.YardBlockId == block.YardBlockId)).SingleAsync(ct));
    }

    private static async Task<Results<Ok<YardBlockResponse>, NotFound, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> UpdateBlockAsync(
        Guid blockId, SaveYardBlockRequest request, MasterDataDbContext db, ICallerPermissions scope, CancellationToken ct)
    {
        var block = await db.YardBlocks.SingleOrDefaultAsync(b => b.YardBlockId == blockId, ct);
        if (block is null) return TypedResults.NotFound();
        if (await BranchOfBlockAsync(db, blockId, ct) is not { } branch || !scope.HasAt(MasterDataPermissions.OrgManage, branch)) return TypedResults.Forbid();
        if (db.ExpectVersion(block, request.RowVersion) is { } missing) return missing;
        var (partyId, invalid) = await CheckBlockAsync(db, request, ct);
        if (invalid is not null) return invalid;
        var code = request.BlockCode.Trim().ToUpperInvariant();
        if (await db.YardBlocks.AnyAsync(b => b.YardId == block.YardId && b.BlockCode == code && b.YardBlockId != blockId, ct))
            return MasterDataSupport.Conflict($"Block '{code}' already exists in this yard.");
        var rows = await db.YardRows.CountAsync(r => r.YardBlockId == blockId, ct);
        if (request.MaxRows is { } max && rows > max)
            return MasterDataSupport.InvalidReference("maxRows", $"The block already has {rows} rows.");

        block.BlockCode = code;
        ApplyBlock(block, request, partyId);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.Ok(await Blocks(db, db.YardBlocks.AsNoTracking().Where(b => b.YardBlockId == blockId)).SingleAsync(ct));
    }

    private static async Task<Results<NoContent, NotFound, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> DeleteBlockAsync(
        Guid blockId, string? rowVersion, MasterDataDbContext db, ICallerPermissions scope, CancellationToken ct)
    {
        var block = await db.YardBlocks.SingleOrDefaultAsync(b => b.YardBlockId == blockId, ct);
        if (block is null) return TypedResults.NotFound();
        if (await BranchOfBlockAsync(db, blockId, ct) is not { } branch || !scope.HasAt(MasterDataPermissions.OrgManage, branch)) return TypedResults.Forbid();
        if (db.ExpectVersion(block, rowVersion) is { } missing) return missing;
        if (await db.YardRows.AnyAsync(r => r.YardBlockId == blockId, ct) || await db.YardSlots.AnyAsync(s => s.YardBlockId == blockId, ct))
            return MasterDataSupport.Conflict("The block still has rows or slots.", "Delete its rows first, or set the block inactive.");
        db.YardBlocks.Remove(block);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.NoContent();
    }

    private static void ApplyBlock(YardBlock b, SaveYardBlockRequest r, Guid? partyId)
    {
        b.MaxRows = r.MaxRows; b.MaxBays = r.MaxBays; b.MaxTiers = r.MaxTiers;
        b.AllocatedToPartyId = partyId; b.AllocatedSizeFt = r.AllocatedSizeFt;
        b.IsReeferBlock = r.IsReeferBlock; b.IsDgBlock = r.IsDgBlock; b.IsOogBlock = r.IsOogBlock;
        b.LayoutX = r.LayoutX; b.LayoutY = r.LayoutY; b.LayoutWidth = r.LayoutWidth; b.LayoutHeight = r.LayoutHeight;
        b.LayoutRotationDeg = r.LayoutRotationDeg; b.DisplayColorHex = r.DisplayColorHex?.ToUpperInvariant(); b.IsActive = r.IsActive;
    }

    // ── rows ────────────────────────────────────────────────────────────────

    private static async Task<Results<Ok<IReadOnlyList<YardRowResponse>>, NotFound>> ListRowsAsync(Guid blockId, MasterDataDbContext db, CancellationToken ct)
    {
        if (!await db.YardBlocks.AnyAsync(b => b.YardBlockId == blockId, ct)) return TypedResults.NotFound();
        return TypedResults.Ok<IReadOnlyList<YardRowResponse>>(
            await Rows(db, db.YardRows.AsNoTracking().Where(r => r.YardBlockId == blockId).OrderBy(r => r.RowLabel)).ToListAsync(ct));
    }

    private static async Task<Results<Created<YardRowResponse>, NotFound, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> CreateRowAsync(
        Guid blockId, SaveYardRowRequest request, MasterDataDbContext db, ITenantContext caller, ICallerPermissions scope, CancellationToken ct)
    {
        var block = await db.YardBlocks.AsNoTracking().SingleOrDefaultAsync(b => b.YardBlockId == blockId, ct);
        if (block is null) return TypedResults.NotFound();
        if (await BranchOfBlockAsync(db, blockId, ct) is not { } branch || !scope.HasAt(MasterDataPermissions.OrgManage, branch)) return TypedResults.Forbid();
        var label = request.RowLabel.Trim().ToUpperInvariant();
        if (await db.YardRows.AnyAsync(r => r.YardBlockId == blockId && r.RowLabel == label, ct))
            return MasterDataSupport.Conflict($"Row '{label}' already exists in block {block.BlockCode}.");
        if (block.MaxRows is { } max && await db.YardRows.CountAsync(r => r.YardBlockId == blockId, ct) >= max)
            return MasterDataSupport.InvalidReference("rowLabel", $"Block {block.BlockCode} holds at most {max} rows.");

        var row = new YardRow { TenantId = caller.TenantId(), YardBlockId = blockId, RowLabel = label };
        ApplyRow(row, request);
        db.YardRows.Add(row);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/master/yard-rows/{row.YardRowId}", await Rows(db, db.YardRows.AsNoTracking().Where(r => r.YardRowId == row.YardRowId)).SingleAsync(ct));
    }

    private static async Task<Results<Ok<YardRowResponse>, NotFound, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> UpdateRowAsync(
        Guid rowId, SaveYardRowRequest request, MasterDataDbContext db, ICallerPermissions scope, CancellationToken ct)
    {
        var row = await db.YardRows.SingleOrDefaultAsync(r => r.YardRowId == rowId, ct);
        if (row is null) return TypedResults.NotFound();
        if (await BranchOfBlockAsync(db, row.YardBlockId, ct) is not { } branch || !scope.HasAt(MasterDataPermissions.OrgManage, branch)) return TypedResults.Forbid();
        if (db.ExpectVersion(row, request.RowVersion) is { } missing) return missing;
        var label = request.RowLabel.Trim().ToUpperInvariant();
        if (label != row.RowLabel)
        {
            // Slots name their row by label: renaming a row with slots would orphan them.
            if (await db.YardSlots.AnyAsync(s => s.YardBlockId == row.YardBlockId && s.RowLabel == row.RowLabel, ct))
                return MasterDataSupport.InvalidReference("rowLabel", "The row has slots, which name it by its label; it cannot be renamed.");
            if (await db.YardRows.AnyAsync(r => r.YardBlockId == row.YardBlockId && r.RowLabel == label && r.YardRowId != rowId, ct))
                return MasterDataSupport.Conflict($"Row '{label}' already exists in this block.");
        }
        row.RowLabel = label;
        ApplyRow(row, request);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.Ok(await Rows(db, db.YardRows.AsNoTracking().Where(r => r.YardRowId == rowId)).SingleAsync(ct));
    }

    private static async Task<Results<NoContent, NotFound, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> DeleteRowAsync(
        Guid rowId, string? rowVersion, MasterDataDbContext db, ICallerPermissions scope, CancellationToken ct)
    {
        var row = await db.YardRows.SingleOrDefaultAsync(r => r.YardRowId == rowId, ct);
        if (row is null) return TypedResults.NotFound();
        if (await BranchOfBlockAsync(db, row.YardBlockId, ct) is not { } branch || !scope.HasAt(MasterDataPermissions.OrgManage, branch)) return TypedResults.Forbid();
        if (db.ExpectVersion(row, rowVersion) is { } missing) return missing;
        if (await db.YardSlots.AnyAsync(s => s.YardBlockId == row.YardBlockId && s.RowLabel == row.RowLabel, ct))
            return MasterDataSupport.Conflict("The row still has slots.", "Set it inactive or blocked instead.");
        db.YardRows.Remove(row);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        return TypedResults.NoContent();
    }

    private static void ApplyRow(YardRow r, SaveYardRowRequest q)
    {
        r.DescriptionEn = string.IsNullOrWhiteSpace(q.DescriptionEn) ? null : q.DescriptionEn.Trim();
        r.IsReeferRow = q.IsReeferRow; r.IsOogRow = q.IsOogRow; r.ReeferPlugCount = q.ReeferPlugCount;
        r.IsBlocked = q.IsBlocked; r.IsActive = q.IsActive;
    }
}
