using Gecko.Data;
using Gecko.MasterData.Contracts;
using Gecko.SharedKernel;
using Gecko.Tos.Application;
using Gecko.Tos.Domain;
using Gecko.Tos.Infrastructure.Persistence;
using Gecko.Tos.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Endpoints.Vessels;

/// <summary>
/// Vessel calls (PLAN §4.1, batch A). One row per PHYSICAL call; the lines on it
/// carry their own voyages; cut-offs are child rows. Bookings will point at the
/// call and copy nothing (D-2) — which is why every rule Vector broke on its
/// schedule is refused here, on save:
///   ETA = ETD (100% of Vector rows)       → database CHECK, and a 400 first
///   yard cut-off after port cut-off (141) → CutoffRules
///   port cut-off after ETD (495)          → CutoffRules
///   one row per agent × booking type      → one call, N lines
///   IsClosed never set                    → status is derived, never stored
/// </summary>
internal static partial class VesselCallEndpoints
{
    public static RouteGroupBuilder MapVesselCallEndpoints(this RouteGroupBuilder tos)
    {
        // A call is TENANT data: the ship calling Laem Chabang is the same ship for
        // every depot of the tenant, so a branch-scoped grant reads and maintains the
        // whole schedule (PLAN Q11). The one thing it may not touch is ANOTHER
        // branch's cut-off row — see ScopeCutoffs.
        var calls = tos.MapGroup("/vessel-calls").WithTags("TOS — vessel calls");

        calls.MapGet("/", ListAsync).RequireBranchPermission(TosPermissions.VesselView).WithSummary("List vessel calls (the schedule), by ETD");
        calls.MapGet("/{id:guid}", GetAsync).RequireBranchPermission(TosPermissions.VesselView).WithName("GetVesselCall").WithSummary("One call with its lines and cut-offs");
        calls.MapGet("/{id:guid}/effective-cutoffs", EffectiveCutoffsAsync).RequireBranchPermission(TosPermissions.VesselView).WithSummary("The cut-offs that apply to one line at one branch");
        calls.MapPost("/", CreateAsync).RequireBranchPermission(TosPermissions.VesselManage).Validate<SaveVesselCallRequest>().WithSummary("Create a call with its lines and cut-offs");
        calls.MapPut("/{id:guid}", UpdateAsync).RequireBranchPermission(TosPermissions.VesselManage).Validate<SaveVesselCallRequest>().WithSummary("Update the call header (vessel, port, terminal, voyages, estimates)");
        calls.MapPut("/{id:guid}/lines", ReplaceLinesAsync).RequireBranchPermission(TosPermissions.VesselManage).Validate<ReplaceVesselCallLinesRequest>().WithSummary("Replace the lines and voyages on the call");
        calls.MapPut("/{id:guid}/cutoffs", ReplaceCutoffsAsync).RequireBranchPermission(TosPermissions.VesselManage).Validate<ReplaceVesselCallCutoffsRequest>().WithSummary("Replace the cut-offs; a missing yard cut-off is derived from the port's");
        calls.MapPost("/{id:guid}/actuals", RecordActualsAsync).RequireBranchPermission(TosPermissions.VesselManage).Validate<RecordActualsRequest>().WithSummary("Record actual arrival / berthing / departure");
        calls.MapPost("/{id:guid}/cancel", CancelAsync).RequireBranchPermission(TosPermissions.VesselManage).Validate<CancelVesselCallRequest>().WithSummary("Cancel a call that has not arrived");
        calls.MapVesselScheduleImport();

        return tos;
    }

    // ── reads ───────────────────────────────────────────────────────────────

    private static async Task<Results<Ok<PagedResult<VesselCallSummaryResponse>>, ValidationProblem>> ListAsync(
        [AsParameters] ListQuery query, TosDbContext db, IMasterDataReferences master, CancellationToken ct,
        DateTimeOffset? from = null, DateTimeOffset? to = null, string? status = null,
        string? vesselCode = null, string? lineCode = null)
    {
        var calls = db.VwVesselCallStatuses.AsNoTracking();

        if (from is not null) calls = calls.Where(c => c.Etd >= from);
        if (to is not null) calls = calls.Where(c => c.Etd < to);
        if (status.Clean() is { } s)
        {
            if (!VesselCallStatus.All.Contains(s))
                return TosSupport.Invalid("status", $"Unknown status '{s}'. Use one of: {string.Join(", ", VesselCallStatus.All)}.");
            calls = calls.Where(c => c.CallStatus == s);
        }
        if (vesselCode.Clean() is { } v) calls = calls.Where(c => c.VesselCode == v);
        if (lineCode.Clean() is { } l)
            calls = calls.Where(c => db.VesselCallLines.Any(x => x.VesselCallId == c.VesselCallId && x.LinePartyCode == l));
        if (query.Search.Clean() is { } q)
            calls = calls.Where(c => c.CallRef.Contains(q) || c.VesselCode.Contains(q)
                || db.VesselCalls.Any(x => x.VesselCallId == c.VesselCallId && (x.OperatorVoyageOut!.Contains(q) || x.OperatorVoyageIn!.Contains(q)))
                || db.VesselCallLines.Any(x => x.VesselCallId == c.VesselCallId && (x.VoyageOut!.Contains(q) || x.VoyageIn!.Contains(q))));

        var projected =
            from c in calls
            join vc in db.VesselCalls on c.VesselCallId equals vc.VesselCallId
            orderby c.Etd, c.CallRef
            select new { c, vc.OperatorVoyageIn, vc.OperatorVoyageOut };

        var page = await projected.ToPagedAsync(query.Page, query.PageSize, ct);

        var ids = page.Items.Select(x => x.c.VesselCallId).ToList();
        var lines = (await db.VesselCallLines.AsNoTracking()
                .Where(x => ids.Contains(x.VesselCallId))
                .OrderBy(x => x.LinePartyCode)
                .Select(x => new { x.VesselCallId, x.LinePartyCode, x.VoyageOut, x.VoyageIn })
                .ToListAsync(ct))
            .ToLookup(x => x.VesselCallId, x => $"{x.LinePartyCode} {x.VoyageOut ?? x.VoyageIn}");
        var vessels = await master.VesselsAsync(page.Items.Select(x => x.c.VesselCode), ct);

        return TypedResults.Ok(new PagedResult<VesselCallSummaryResponse>(
            page.Items.Select(x => new VesselCallSummaryResponse(
                x.c.VesselCallId, x.c.CallRef, x.c.VesselCode, vessels.GetValueOrDefault(x.c.VesselCode)?.VesselName,
                x.c.PortCode, x.c.TerminalCode, x.OperatorVoyageIn, x.OperatorVoyageOut,
                x.c.Eta, x.c.Etb, x.c.Etd, x.c.Ata, x.c.Atb, x.c.Atd,
                x.c.LastYardCutoffAt, x.c.CallStatus, lines[x.c.VesselCallId].ToList())).ToList(),
            page.Page, page.PageSize, page.TotalCount));
    }

    private static async Task<Results<Ok<VesselCallDetailResponse>, NotFound>> GetAsync(
        Guid id, TosDbContext db, IMasterDataReferences master, CancellationToken ct) =>
        await DetailAsync(db, master, id, ct) is { } detail ? TypedResults.Ok(detail) : TypedResults.NotFound();

    private static async Task<Results<Ok<IReadOnlyList<EffectiveCutoffResponse>>, NotFound, ValidationProblem>> EffectiveCutoffsAsync(
        Guid id, TosDbContext db, CancellationToken ct, string? lineCode = null, Guid? branchId = null)
    {
        if (!await db.VesselCalls.AnyAsync(c => c.VesselCallId == id, ct)) return TypedResults.NotFound();

        Guid? lineId = null;
        if (lineCode.Clean() is { } l)
        {
            lineId = await db.VesselCallLines.Where(x => x.VesselCallId == id && x.LinePartyCode == l)
                .Select(x => (Guid?)x.LinePartyId).SingleOrDefaultAsync(ct);
            if (lineId is null) return TosSupport.Invalid("lineCode", $"Line {l} is not on this call.");
        }

        var rows = await CutoffLookup.EffectiveAsync(db, id, lineId, branchId, ct);

        return TypedResults.Ok<IReadOnlyList<EffectiveCutoffResponse>>(rows
            .OrderBy(r => r.Kind)
            .Select(r => new EffectiveCutoffResponse(r.Kind, r.At, r.Source, r.AppliesTo))
            .ToList());
    }

    // ── writes ──────────────────────────────────────────────────────────────

    private static async Task<Results<CreatedAtRoute<VesselCallDetailResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        SaveVesselCallRequest request, TosDbContext db, IMasterDataReferences master, ITenantContext caller,
        ICallerPermissions scope, CancellationToken ct)
    {
        var errors = new Dictionary<string, List<string>>();
        var header = await ResolveHeaderAsync(request, master, errors, ct);

        var lineItems = request.Lines ?? [];
        if (lineItems.Count == 0) errors.Add("lines", "A call needs at least one line — nobody can book against a call with no lines.");
        var lines = await ResolveLinesAsync(db, master, lineItems, header?.Port.PortId, excludeCallId: null, errors, ct);

        var cutoffItems = request.Cutoffs ?? [];
        if (OutsideScope(scope, cutoffItems) is { } refused) return refused;
        var cutoffs = await ResolveCutoffsAsync(master, cutoffItems, lines.Select(x => x.LinePartyCode), request.Etd, errors, ct);

        if (errors.Count > 0 || header is null) return TosSupport.Invalid(errors);

        var callRef = request.CallRef.Clean() ?? DefaultCallRef(header.Vessel.VesselCode, request);
        if (await db.VesselCalls.AnyAsync(c => c.CallRef == callRef, ct))
            return TosSupport.Conflict($"Call reference '{callRef}' is already used.", "Give this call its own reference.");
        if (request.OperatorVoyageOut.Clean() is { } voyage && await db.VesselCalls.AnyAsync(c =>
                c.VesselId == header.Vessel.VesselId && c.PortId == header.Port.PortId && c.OperatorVoyageOut == voyage, ct))
            return TosSupport.Conflict($"{header.Vessel.VesselCode} voyage {voyage} already calls {header.Port.PortCode}.",
                "One physical call is one row — add the line to that call instead of creating a second one.");

        var tenantId = caller.TenantId();
        var call = new VesselCall { TenantId = tenantId, CallRef = callRef, Source = "MANUAL" };
        ApplyHeader(call, request, header);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.VesselCalls.Add(call);
        await db.SaveChangesAsync(ct);   // the call id comes from NEWSEQUENTIALID()

        foreach (var line in lines) { line.TenantId = tenantId; line.VesselCallId = call.VesselCallId; db.VesselCallLines.Add(line); }
        await AddCutoffsAsync(db, master, call, cutoffs, tenantId, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return TypedResults.CreatedAtRoute((await DetailAsync(db, master, call.VesselCallId, ct))!, "GetVesselCall", new { id = call.VesselCallId });
    }

    private static async Task<Results<Ok<VesselCallDetailResponse>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid id, SaveVesselCallRequest request, TosDbContext db, IMasterDataReferences master, CancellationToken ct)
    {
        var call = await db.VesselCalls.SingleOrDefaultAsync(c => c.VesselCallId == id, ct);
        if (call is null) return TypedResults.NotFound();
        if (call.IsCancelled) return CancelledIsReadOnly(call);
        if (!db.TrySetExpectedVersion(call, request.RowVersion))
            return TosSupport.Invalid("rowVersion", "Send the rowVersion you received when reading the call.");

        var errors = new Dictionary<string, List<string>>();
        var header = await ResolveHeaderAsync(request, master, errors, ct);
        if (request.Lines is { Count: > 0 } || request.Cutoffs is { Count: > 0 })
            errors.Add("lines", "PUT changes the header only. Replace lines on …/lines and cut-offs on …/cutoffs.");

        // A new ETD must still be after every cut-off already on the call.
        if (request.Etd is { } etd)
        {
            var late = await db.VesselCallCutoffs.Where(c => c.VesselCallId == id && c.CutoffAt > etd)
                .Select(c => c.CutoffKind).ToListAsync(ct);
            if (late.Count > 0)
                errors.Add("etd", $"The new ETD is before these cut-offs: {string.Join(", ", late)}. Move them first.");
        }
        if (errors.Count > 0 || header is null) return TosSupport.Invalid(errors);

        var callRef = request.CallRef.Clean() ?? call.CallRef;
        if (callRef != call.CallRef && await db.VesselCalls.AnyAsync(c => c.CallRef == callRef && c.VesselCallId != id, ct))
            return TosSupport.Conflict($"Call reference '{callRef}' is already used.");
        if (request.OperatorVoyageOut.Clean() is { } voyage && await db.VesselCalls.AnyAsync(c => c.VesselCallId != id &&
                c.VesselId == header.Vessel.VesselId && c.PortId == header.Port.PortId && c.OperatorVoyageOut == voyage, ct))
            return TosSupport.Conflict($"{header.Vessel.VesselCode} voyage {voyage} already calls {header.Port.PortCode}.");

        call.CallRef = callRef;
        ApplyHeader(call, request, header);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;

        return TypedResults.Ok((await DetailAsync(db, master, id, ct))!);
    }

    /// <summary>
    /// Replaced as a set, like MDM's order-type steps. The filtered unique index
    /// (one row per line per call) makes a partial edit hazardous.
    /// </summary>
    private static async Task<Results<Ok<VesselCallDetailResponse>, NotFound, ValidationProblem, ProblemHttpResult>> ReplaceLinesAsync(
        Guid id, ReplaceVesselCallLinesRequest request, TosDbContext db, IMasterDataReferences master, CancellationToken ct)
    {
        var call = await db.VesselCalls.AsNoTracking().SingleOrDefaultAsync(c => c.VesselCallId == id, ct);
        if (call is null) return TypedResults.NotFound();
        if (call.IsCancelled) return CancelledIsReadOnly(call);

        var errors = new Dictionary<string, List<string>>();
        var lines = await ResolveLinesAsync(db, master, request.Lines, call.PortId, excludeCallId: id, errors, ct);

        // A line-specific cut-off for a line that is leaving the call would be a
        // CUTOFF_LINE_NOT_ON_CALL defect the moment this saves.
        var kept = lines.Select(l => l.LinePartyCode).ToHashSet();
        var orphaned = await db.VesselCallCutoffs
            .Where(c => c.VesselCallId == id && c.LinePartyCode != null && !kept.Contains(c.LinePartyCode))
            .Select(c => c.CutoffKind + " for " + c.LinePartyCode).ToListAsync(ct);
        if (orphaned.Count > 0)
            errors.Add("lines", $"These cut-offs belong to a line you are removing: {string.Join(", ", orphaned)}. Replace the cut-offs first.");
        if (errors.Count > 0) return TosSupport.Invalid(errors);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var existing = await db.VesselCallLines.Where(l => l.VesselCallId == id).ToListAsync(ct);
        db.VesselCallLines.RemoveRange(existing);
        if (existing.Count > 0) await db.SaveChangesAsync(ct);   // flush past the filtered unique index

        foreach (var line in lines) { line.TenantId = call.TenantId; line.VesselCallId = id; db.VesselCallLines.Add(line); }
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        await tx.CommitAsync(ct);

        return TypedResults.Ok((await DetailAsync(db, master, id, ct))!);
    }

    private static async Task<Results<Ok<VesselCallDetailResponse>, NotFound, ValidationProblem, ProblemHttpResult>> ReplaceCutoffsAsync(
        Guid id, ReplaceVesselCallCutoffsRequest request, TosDbContext db, IMasterDataReferences master,
        ICallerPermissions scope, CancellationToken ct)
    {
        var call = await db.VesselCalls.SingleOrDefaultAsync(c => c.VesselCallId == id, ct);
        if (call is null) return TypedResults.NotFound();
        if (call.IsCancelled) return CancelledIsReadOnly(call);

        if (OutsideScope(scope, request.Cutoffs) is { } refused) return refused;

        var lineCodes = await db.VesselCallLines.Where(l => l.VesselCallId == id).Select(l => l.LinePartyCode).ToListAsync(ct);
        var errors = new Dictionary<string, List<string>>();
        var cutoffs = await ResolveCutoffsAsync(master, request.Cutoffs, lineCodes, call.Etd, errors, ct);
        if (errors.Count > 0) return TosSupport.Invalid(errors);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var existing = await db.VesselCallCutoffs.Where(c => c.VesselCallId == id).ToListAsync(ct);

        // "Replace the set" replaces the set the caller MAY write. A branch-scoped
        // caller cannot delete Bangkok's local yard cut-off by leaving it out of a
        // payload they were never allowed to send it in.
        var mine = scope.BranchFilter(TosPermissions.VesselManage);
        if (mine is not null) existing = existing.Where(c => c.BranchId is null || mine.Contains(c.BranchId.Value)).ToList();

        db.VesselCallCutoffs.RemoveRange(existing);
        if (existing.Count > 0) await db.SaveChangesAsync(ct);

        await AddCutoffsAsync(db, master, call, cutoffs, call.TenantId, ct);
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;
        await tx.CommitAsync(ct);

        return TypedResults.Ok((await DetailAsync(db, master, id, ct))!);
    }

    private static async Task<Results<Ok<VesselCallDetailResponse>, NotFound, ValidationProblem, ProblemHttpResult>> RecordActualsAsync(
        Guid id, RecordActualsRequest request, TosDbContext db, IMasterDataReferences master, TimeProvider clock, CancellationToken ct)
    {
        var call = await db.VesselCalls.SingleOrDefaultAsync(c => c.VesselCallId == id, ct);
        if (call is null) return TypedResults.NotFound();
        if (call.IsCancelled) return CancelledIsReadOnly(call);
        if (!db.TrySetExpectedVersion(call, request.RowVersion))
            return TosSupport.Invalid("rowVersion", "Send the rowVersion you received when reading the call.");

        var errors = new Dictionary<string, List<string>>();
        var soon = clock.GetUtcNow().AddHours(1);
        foreach (var (field, value) in new[] { ("ata", request.Ata), ("atb", request.Atb), ("atd", request.Atd) })
            if (value > soon) errors.Add(field, "An actual time cannot be in the future.");
        if (request.Atb is not null && (request.Ata is null || request.Atb < request.Ata))
            errors.Add("atb", "Berthing needs an arrival, and cannot be before it.");
        if (request.Atd is not null && (request.Ata is null || request.Atd <= (request.Atb ?? request.Ata)))
            errors.Add("atd", "Departure needs an arrival, and must be after berthing (or arrival).");
        if (errors.Count > 0) return TosSupport.Invalid(errors);

        call.Ata = request.Ata;
        call.Atb = request.Atb;
        call.Atd = request.Atd;
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;

        return TypedResults.Ok((await DetailAsync(db, master, id, ct))!);
    }

    private static async Task<Results<Ok<VesselCallDetailResponse>, NotFound, ValidationProblem, ProblemHttpResult>> CancelAsync(
        Guid id, CancelVesselCallRequest request, TosDbContext db, IMasterDataReferences master, ITenantContext caller,
        TimeProvider clock, CancellationToken ct)
    {
        var call = await db.VesselCalls.SingleOrDefaultAsync(c => c.VesselCallId == id, ct);
        if (call is null) return TypedResults.NotFound();
        if (call.IsCancelled) return TosSupport.Conflict("This call is already cancelled.");
        if (call.Ata is not null)
            return TosSupport.Conflict("The ship has arrived — a call that happened cannot be cancelled.",
                "Record its departure instead.");
        if (!db.TrySetExpectedVersion(call, request.RowVersion))
            return TosSupport.Invalid("rowVersion", "Send the rowVersion you received when reading the call.");

        // Bookings on the call (batch B) are not touched: they show up as
        // "booking on a cancelled call" in vw_booking_defects for someone to re-point.
        call.IsCancelled = true;
        call.CancelledAt = clock.GetUtcNow();
        call.CancelledBy = caller.UserId();
        call.CancelReason = request.Reason.Trim();
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;

        return TypedResults.Ok((await DetailAsync(db, master, id, ct))!);
    }

    // ── resolution: codes → MDM ids, and the rules ──────────────────────────

    private sealed record Header(VesselRef Vessel, PortRef Port, CodeRef? Terminal);

    private static async Task<Header?> ResolveHeaderAsync(
        SaveVesselCallRequest request, IMasterDataReferences master, Dictionary<string, List<string>> errors, CancellationToken ct)
    {
        var vesselCode = request.VesselCode.Clean()!;
        var portCode = request.PortCode.Clean()!;
        var terminalCode = request.TerminalCode.Clean();

        var vessel = (await master.VesselsAsync([vesselCode], ct)).GetValueOrDefault(vesselCode);
        if (vessel is null) errors.Add("vesselCode", $"Unknown vessel '{vesselCode}'. Add it in master data first.");
        else if (!vessel.IsActive) errors.Add("vesselCode", $"Vessel {vesselCode} is inactive.");

        var port = (await master.PortsAsync([portCode], ct)).GetValueOrDefault(portCode);
        if (port is null) errors.Add("portCode", $"Unknown port '{portCode}'.");
        else if (!port.IsActive) errors.Add("portCode", $"Port {portCode} is inactive.");

        CodeRef? terminal = null;
        if (terminalCode is not null)
        {
            terminal = (await master.TerminalsAsync([terminalCode], ct)).GetValueOrDefault(terminalCode);
            if (terminal is null) errors.Add("terminalCode", $"'{terminalCode}' is not a terminal (a location of type TERMINAL).");
            else if (!terminal.IsActive) errors.Add("terminalCode", $"Terminal {terminalCode} is inactive.");
        }

        if (request.Eta is { } eta && request.Etd is { } etd)
        {
            // V-5: ETA = ETD on every Vector row. The CHECK refuses it too; this says why.
            if (etd <= eta) errors.Add("etd", "ETD must be after ETA — a call that leaves when it arrives is not a schedule.");
            if (request.Etb is { } etb && (etb < eta || etb > etd)) errors.Add("etb", "ETB must fall between ETA and ETD.");
        }

        return vessel is null || port is null ? null : new Header(vessel, port, terminal);
    }

    private static void ApplyHeader(VesselCall call, SaveVesselCallRequest request, Header header)
    {
        call.VesselId = header.Vessel.VesselId;
        call.VesselCode = header.Vessel.VesselCode;
        call.PortId = header.Port.PortId;
        call.PortCode = header.Port.PortCode;
        call.TerminalLocationId = header.Terminal?.Id;
        call.TerminalCode = header.Terminal?.Code;
        call.OperatorVoyageIn = request.OperatorVoyageIn.Clean();
        call.OperatorVoyageOut = request.OperatorVoyageOut.Clean();
        call.Eta = request.Eta!.Value;
        call.Etb = request.Etb;
        call.Etd = request.Etd!.Value;
        call.Remarks = string.IsNullOrWhiteSpace(request.Remarks) ? null : request.Remarks.Trim();
    }

    /// <summary>Vector's own habit, kept because depots already say it: VESSEL-VOYAGE.</summary>
    private static string DefaultCallRef(string vesselCode, SaveVesselCallRequest request)
    {
        var voyage = request.OperatorVoyageOut.Clean() ?? request.Lines?.Select(l => l.VoyageOut.Clean()).FirstOrDefault(v => v is not null)
                     ?? request.Etd!.Value.ToString("yyMMdd");
        var reference = $"{vesselCode}-{voyage}";
        return reference.Length <= 30 ? reference : reference[..30];
    }

    private static async Task<List<VesselCallLine>> ResolveLinesAsync(
        TosDbContext db, IMasterDataReferences master, IReadOnlyList<VesselCallLineItem> items, Guid? portId, Guid? excludeCallId,
        Dictionary<string, List<string>> errors, CancellationToken ct)
    {
        var parties = await master.PartiesAsync(items.SelectMany(i => new[] { i.LineCode, i.AgentCode ?? "" }), ct);
        var result = new List<VesselCallLine>();

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var key = $"lines[{i}]";
            var code = item.LineCode.Clean();
            if (code is null) { errors.Add($"{key}.lineCode", "Line code is required."); continue; }

            var line = parties.GetValueOrDefault(code);
            if (line is null) { errors.Add($"{key}.lineCode", $"Unknown party '{code}'."); continue; }
            if (!line.IsShippingLine) errors.Add($"{key}.lineCode", $"{code} is not a shipping line.");
            if (!line.IsActive) errors.Add($"{key}.lineCode", $"{code} is inactive.");

            PartyRef? agent = null;
            if (item.AgentCode.Clean() is { } agentCode)
            {
                agent = parties.GetValueOrDefault(agentCode);
                if (agent is null) errors.Add($"{key}.agentCode", $"Unknown party '{agentCode}'.");
            }

            var voyageIn = item.VoyageIn.Clean();
            var voyageOut = item.VoyageOut.Clean();
            if (voyageIn is null && voyageOut is null)
                errors.Add($"{key}.voyageOut", "Give the line's voyage (in, out or both) — bookings and EDI match on it.");
            foreach (var (field, value, max) in new[] { ("voyageIn", voyageIn, 20), ("voyageOut", voyageOut, 20), ("serviceCode", item.ServiceCode.Clean(), 20) })
                if (value?.Length > max) errors.Add($"{key}.{field}", $"At most {max} characters.");

            if (result.Any(r => r.LinePartyCode == line.PartyCode))
                errors.Add($"{key}.lineCode", $"{code} is on the call twice. One row per line; a line with two voyages is two calls.");

            result.Add(new VesselCallLine
            {
                LinePartyId = line.PartyId,
                LinePartyCode = line.PartyCode,
                AgentPartyId = agent?.PartyId,
                AgentPartyCode = agent?.PartyCode,
                VoyageIn = voyageIn,
                VoyageOut = voyageOut,
                ServiceCode = item.ServiceCode.Clean(),
            });
        }

        // A line never reuses an outbound voyage at the same port — the rule the
        // index cannot hold because a line row has no port (DUPLICATE_LINE_VOYAGE).
        if (portId is not null)
        {
            for (var i = 0; i < result.Count; i++)
            {
                var r = result[i];
                if (r.VoyageOut is null) continue;
                var clash = await (
                    from l in db.VesselCallLines
                    join c in db.VesselCalls on l.VesselCallId equals c.VesselCallId
                    where l.LinePartyId == r.LinePartyId && l.VoyageOut == r.VoyageOut && c.PortId == portId
                          && (excludeCallId == null || c.VesselCallId != excludeCallId)
                    select c.CallRef).FirstOrDefaultAsync(ct);
                if (clash is not null)
                    errors.Add($"lines[{i}].voyageOut", $"{r.LinePartyCode} voyage {r.VoyageOut} already calls this port on {clash}.");
            }
        }

        return result;
    }

    private sealed record ResolvedCutoff(CutoffRules.Cutoff Rule, Guid? LinePartyId, string? Remarks);

    private static async Task<List<ResolvedCutoff>> ResolveCutoffsAsync(
        IMasterDataReferences master, IReadOnlyList<VesselCallCutoffItem> items, IEnumerable<string> lineCodesOnCall, DateTimeOffset? etd,
        Dictionary<string, List<string>> errors, CancellationToken ct)
    {
        var parties = await master.PartiesAsync(items.Select(i => i.LineCode ?? ""), ct);
        var branchIds = items.Where(i => i.BranchId is not null).Select(i => i.BranchId!.Value).ToList();
        IReadOnlyDictionary<string, BranchRef> branches = branchIds.Count == 0 ? new Dictionary<string, BranchRef>() : await master.BranchesAsync(branchIds, ct);

        var result = new List<ResolvedCutoff>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var key = $"cutoffs[{i}]";
            if (item.At is null) { errors.Add($"{key}.at", "The cut-off time is required."); continue; }
            if (item.BranchId is { } b && !branches.ContainsKey(b.ToString()))
                errors.Add($"{key}.branchId", "Unknown branch.");
            if (item.Remarks?.Length > 300) errors.Add($"{key}.remarks", "At most 300 characters.");

            var lineCode = item.LineCode.Clean();
            result.Add(new ResolvedCutoff(
                new CutoffRules.Cutoff(item.Kind.Clean() ?? "", lineCode, item.BranchId, item.At.Value),
                lineCode is null ? null : parties.GetValueOrDefault(lineCode)?.PartyId,
                string.IsNullOrWhiteSpace(item.Remarks) ? null : item.Remarks.Trim()));
        }

        if (etd is not null)
            foreach (var (index, message) in CutoffRules.Validate(
                         result.Select(r => r.Rule).ToList(),
                         lineCodesOnCall.Select(c => c.ToUpperInvariant()).ToHashSet(), etd.Value))
                errors.Add($"cutoffs[{index}]", message);

        return result;
    }

    /// <summary>
    /// A cut-off row scoped to a branch belongs to that depot. A caller who holds
    /// <c>tos.vessel.manage</c> only at their own branches may not write another's —
    /// the whole-call rows (branchId null) stay open to anyone with the permission,
    /// because those are the schedule everybody works from.
    /// </summary>
    private static ProblemHttpResult? OutsideScope(ICallerPermissions scope, IReadOnlyList<VesselCallCutoffItem> items)
    {
        var mine = scope.BranchFilter(TosPermissions.VesselManage);
        if (mine is null) return null;

        var foreign = items.Where(i => i.BranchId is { } b && !mine.Contains(b)).Select(i => i.BranchId!.Value).Distinct().ToList();
        return foreign.Count == 0
            ? null
            : TosScope.OutsideYourBranches(
                $"{foreign.Count} cut-off row(s) name a branch you do not cover. You may set whole-call cut-offs and your own branch's, not another depot's.");
    }

    private static async Task AddCutoffsAsync(
        TosDbContext db, IMasterDataReferences master, VesselCall call, List<ResolvedCutoff> cutoffs, Guid tenantId, CancellationToken ct)
    {
        foreach (var c in cutoffs)
            db.VesselCallCutoffs.Add(new VesselCallCutoff
            {
                TenantId = tenantId,
                VesselCallId = call.VesselCallId,
                CutoffKind = c.Rule.Kind,
                LinePartyId = c.LinePartyId,
                LinePartyCode = c.Rule.LineCode,
                BranchId = c.Rule.BranchId,
                CutoffAt = c.Rule.At,
                Source = "MANUAL",
                Remarks = c.Remarks,
            });

        var lead = await master.GetIntSettingAsync(TosSettingKeys.YardCutoffLeadHours, null, 24, ct);
        foreach (var d in CutoffRules.Derive(cutoffs.Select(c => c.Rule).ToList(), lead))
            db.VesselCallCutoffs.Add(new VesselCallCutoff
            {
                TenantId = tenantId,
                VesselCallId = call.VesselCallId,
                CutoffKind = d.Kind,
                CutoffAt = d.At,
                Source = "DERIVED",
                DerivedLeadHours = (short)lead,
                Remarks = $"Derived: {CutoffRules.PortKindFor(d.Kind)} minus {lead} h",
            });
    }

    private static ProblemHttpResult CancelledIsReadOnly(VesselCall call) =>
        TosSupport.Conflict($"{call.CallRef} is cancelled and can no longer be changed.", call.CancelReason);

    // ── projection ──────────────────────────────────────────────────────────

    private static async Task<VesselCallDetailResponse?> DetailAsync(TosDbContext db, IMasterDataReferences master, Guid id, CancellationToken ct)
    {
        var row = await (
            from vc in db.VesselCalls.AsNoTracking()
            join s in db.VwVesselCallStatuses on vc.VesselCallId equals s.VesselCallId
            where vc.VesselCallId == id
            select new { vc, s.CallStatus }).SingleOrDefaultAsync(ct);
        if (row is null) return null;

        var c = row.vc;
        var vessel = (await master.VesselsAsync([c.VesselCode], ct)).GetValueOrDefault(c.VesselCode);

        var lines = await db.VesselCallLines.AsNoTracking()
            .Where(l => l.VesselCallId == id).OrderBy(l => l.LinePartyCode)
            .Select(l => new VesselCallLineResponse(l.VesselCallLineId, l.LinePartyCode, l.AgentPartyCode, l.VoyageIn, l.VoyageOut, l.ServiceCode))
            .ToListAsync(ct);

        var cutoffs = await db.VesselCallCutoffs.AsNoTracking()
            .Where(x => x.VesselCallId == id).OrderBy(x => x.CutoffAt).ThenBy(x => x.CutoffKind)
            .Select(x => new VesselCallCutoffResponse(x.VesselCallCutoffId, x.CutoffKind, x.LinePartyCode, x.BranchId,
                x.CutoffAt, x.Source, x.DerivedLeadHours, x.Remarks))
            .ToListAsync(ct);

        return new VesselCallDetailResponse(
            new VesselCallResponse(
                c.VesselCallId, c.CallRef, c.VesselCode, vessel?.VesselName, c.PortCode, c.TerminalCode,
                c.OperatorVoyageIn, c.OperatorVoyageOut, c.Eta, c.Etb, c.Etd, c.Ata, c.Atb, c.Atd,
                row.CallStatus, c.IsCancelled, c.CancelledAt, c.CancelReason, c.Source, c.Remarks,
                Convert.ToBase64String(c.RowVersion)),
            lines, cutoffs);
    }
}
