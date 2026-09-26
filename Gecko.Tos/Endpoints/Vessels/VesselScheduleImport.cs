using System.Security.Cryptography;
using ClosedXML.Excel;
using Gecko.Data;
using Gecko.MasterData.Contracts;
using Gecko.SharedKernel;
using Gecko.Tos.Infrastructure.Persistence;
using Gecko.Tos.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Endpoints.Vessels;

public sealed record ScheduleImportCall(
    string Key, IReadOnlyList<int> Rows, string Action, string? CallRef, string? VesselCode, string? PortCode,
    DateTimeOffset? Eta, DateTimeOffset? Etd, IReadOnlyList<string> Lines, IReadOnlyList<string> Changes, IReadOnlyList<string> Issues);

/// <param name="FileSha256">Confirm with the same file: its hash must match what was previewed.</param>
public sealed record ScheduleImportResponse(
    string FileSha256, bool Applied, int Creates, int Updates, int Unchanged, int Errors, IReadOnlyList<ScheduleImportCall> Calls);

/// <summary>
/// PLAN 3.5 — the vessel schedule from Excel: download the template, fill it (or
/// paste the line's schedule into it), upload for a PREVIEW, then CONFIRM the same
/// file. The preview writes nothing; the confirm applies every call in ONE
/// transaction, or none — a half-loaded schedule is worse than none.
///
/// One row per call per shipping line. Rows with the same call reference — or,
/// without one, the same vessel + port + outbound voyage — are one call. Times are
/// in the PORT's local time (Laem Chabang +07:00), as a schedule is written.
///
/// Every row passes the same checks as the screen (the header, line and cut-off
/// resolvers of <see cref="VesselCallEndpoints"/>). An existing call gets its
/// estimates and remarks updated, lines it lacks added, and the cut-offs given in
/// the file replaced; a line is never removed by an import, because bookings may
/// already point at it.
/// </summary>
internal static partial class VesselCallEndpoints
{
    private static readonly string[] ImportColumns =
    [
        "Call ref", "Vessel", "Port", "Terminal", "Voyage in", "Voyage out", "ETA", "ETB", "ETD",
        "Line", "Agent", "Line voyage in", "Line voyage out", "Service", "Port cut-off (dry)", "Yard cut-off (dry)", "Remarks",
    ];

    public static RouteGroupBuilder MapVesselScheduleImport(this RouteGroupBuilder calls)
    {
        calls.MapGet("/import/template", Template).RequireBranchPermission(TosPermissions.VesselManage)
            .WithSummary("The vessel-schedule Excel template (one row per call per line)");
        calls.MapPost("/import", ImportAsync).RequireBranchPermission(TosPermissions.VesselManage)
            .DisableAntiforgery()
            .WithSummary("Upload the schedule: preview (confirm=false) or apply all-or-nothing (confirm=true + expectedSha256)");
        return calls;
    }

    private static FileContentHttpResult Template()
    {
        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet("Calls");
        for (var i = 0; i < ImportColumns.Length; i++)
        {
            sheet.Cell(1, i + 1).Value = ImportColumns[i];
            sheet.Cell(1, i + 1).Style.Font.Bold = true;
        }
        object[] example = ["", "BLUEMERIDIAN", "THLCH", "", "2640E", "2640W", new DateTime(2026, 10, 3, 6, 0, 0),
            new DateTime(2026, 10, 3, 8, 0, 0), new DateTime(2026, 10, 4, 18, 0, 0), "MAEU", "", "", "2640W", "AE7",
            new DateTime(2026, 10, 3, 12, 0, 0), new DateTime(2026, 10, 2, 18, 0, 0), "Example — delete this row"];
        for (var i = 0; i < example.Length; i++) sheet.Cell(2, i + 1).Value = XLCellValue.FromObject(example[i]);
        foreach (var col in new[] { 7, 8, 9, 15, 16 }) sheet.Column(col).Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
        sheet.Columns().AdjustToContents();
        sheet.SheetView.FreezeRows(1);

        var help = book.AddWorksheet("How to fill");
        string[] notes =
        [
            "One row per call per shipping line. Rows with the same Call ref (or, without one, the same Vessel + Port + Voyage out) are ONE call.",
            "Vessel, Port, Terminal, Line and Agent are master-data codes. Times are the PORT's local time.",
            "ETA and ETD are required; ETD after ETA. Cut-offs are optional; a yard cut-off left empty is derived from the port's.",
            "Upload once to PREVIEW (nothing is written), then confirm the SAME file. Every call applies, or none does.",
            "An existing call: estimates and remarks are updated, missing lines are added, cut-offs in the file replace those kinds. Lines are never removed by an import.",
        ];
        for (var i = 0; i < notes.Length; i++) help.Cell(i + 1, 1).Value = notes[i];
        help.Column(1).Width = 140;

        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return TypedResults.File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "vessel-schedule-template.xlsx");
    }

    private sealed record ImportRow(int Row, string? CallRef, string? Vessel, string? Port, string? Terminal, string? VoyageIn, string? VoyageOut,
        DateTime? Eta, DateTime? Etb, DateTime? Etd, string? Line, string? Agent, string? LineVoyageIn, string? LineVoyageOut, string? Service,
        DateTime? PortCutoff, DateTime? YardCutoff, string? Remarks, List<string> Issues);

    private sealed record PlannedCall(ScheduleImportCall Report, Func<CancellationToken, Task>? Apply);

    private static async Task<Results<Ok<ScheduleImportResponse>, ValidationProblem, ProblemHttpResult>> ImportAsync(
        IFormFile? file, bool? confirm, string? expectedSha256,
        TosDbContext db, IMasterDataReferences master, ITenantContext caller, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return TosSupport.Invalid(new Dictionary<string, List<string>> { ["file"] = ["Upload the filled template (.xlsx)."] });
        if (file.Length > 5 * 1024 * 1024) return TosSupport.Invalid(new Dictionary<string, List<string>> { ["file"] = ["At most 5 MB."] });

        byte[] bytes;
        using (var buffer = new MemoryStream()) { await file.CopyToAsync(buffer, ct); bytes = buffer.ToArray(); }
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        List<ImportRow> rows;
        try { rows = ReadRows(bytes); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return TosSupport.Invalid(new Dictionary<string, List<string>> { ["file"] = [$"Not a readable vessel-schedule workbook: {e.Message}"] });
        }
        if (rows.Count == 0) return TosSupport.Invalid(new Dictionary<string, List<string>> { ["file"] = ["The Calls sheet has no rows."] });

        var planned = new List<PlannedCall>();
        foreach (var group in rows.GroupBy(r => r.CallRef?.ToUpperInvariant() ?? $"{r.Vessel}|{r.Port}|{r.VoyageOut}".ToUpperInvariant()))
            planned.Add(await PlanAsync(group.Key, group.ToList(), db, master, caller.TenantId(), ct));

        // Two groups aiming at one call (e.g. a ref on some rows, none on others) would fight.
        foreach (var dup in planned.Where(p => p.Report.CallRef is not null).GroupBy(p => p.Report.CallRef).Where(g => g.Count() > 1))
            foreach (var p in dup) ((List<string>)p.Report.Issues).Add($"Call {dup.Key} appears in more than one group of rows.");

        var calls = planned.Select(p => p.Report.Issues.Count > 0 ? p.Report with { Action = "ERROR" } : p.Report).ToList();
        ScheduleImportResponse Response(bool applied) => new(sha, applied,
            calls.Count(c => c.Action == "CREATE"), calls.Count(c => c.Action == "UPDATE"),
            calls.Count(c => c.Action == "UNCHANGED"), calls.Count(c => c.Action == "ERROR"), calls);

        if (confirm != true) return TypedResults.Ok(Response(false));

        if (!string.Equals(expectedSha256, sha, StringComparison.OrdinalIgnoreCase))
            return TosSupport.Conflict("This is not the file you previewed.", "Preview it again, then confirm the same file.");
        if (calls.Any(c => c.Action == "ERROR"))
            return TosSupport.Conflict("The schedule has errors; nothing was applied.", "Fix the rows marked ERROR in the preview and upload again.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var p in planned.Where(p => p.Apply is not null)) await p.Apply!(ct);
        await tx.CommitAsync(ct);
        return TypedResults.Ok(Response(true));
    }

    private static List<ImportRow> ReadRows(byte[] bytes)
    {
        using var book = new XLWorkbook(new MemoryStream(bytes));
        var sheet = book.Worksheets.FirstOrDefault(w => w.Name.Equals("Calls", StringComparison.OrdinalIgnoreCase)) ?? book.Worksheet(1);
        var headers = sheet.Row(1).CellsUsed().ToDictionary(c => c.GetString().Trim(), c => c.Address.ColumnNumber, StringComparer.OrdinalIgnoreCase);
        var missing = ImportColumns.Take(10).Where(c => c != "Call ref" && c != "Terminal" && c != "Voyage in" && c != "ETB" && !headers.ContainsKey(c)).ToList();
        if (missing.Count > 0) throw new InvalidDataException($"missing columns: {string.Join(", ", missing)}");

        var rows = new List<ImportRow>();
        foreach (var r in sheet.RowsUsed().Where(r => r.RowNumber() > 1))
        {
            var issues = new List<string>();
            string? Text(string column) => headers.TryGetValue(column, out var c) && r.Cell(c).GetString().Trim() is { Length: > 0 } v ? v.ToUpperInvariant() : null;
            string? Raw(string column) => headers.TryGetValue(column, out var c) && r.Cell(c).GetString().Trim() is { Length: > 0 } v ? v : null;
            DateTime? When(string column)
            {
                if (!headers.TryGetValue(column, out var c) || r.Cell(c).IsEmpty()) return null;
                var cell = r.Cell(c);
                if (cell.DataType == XLDataType.DateTime) return cell.GetDateTime();
                if (DateTime.TryParse(cell.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)) return d;
                issues.Add($"{column}: '{cell.GetString()}' is not a date and time.");
                return null;
            }
            rows.Add(new ImportRow(r.RowNumber(), Text("Call ref"), Text("Vessel"), Text("Port"), Text("Terminal"), Text("Voyage in"), Text("Voyage out"),
                When("ETA"), When("ETB"), When("ETD"), Text("Line"), Text("Agent"), Text("Line voyage in"), Text("Line voyage out"), Text("Service"),
                When("Port cut-off (dry)"), When("Yard cut-off (dry)"), Raw("Remarks"), issues));
        }
        return rows;
    }

    private static async Task<PlannedCall> PlanAsync(string key, List<ImportRow> rows, TosDbContext db, IMasterDataReferences master, Guid tenantId, CancellationToken ct)
    {
        var first = rows[0];
        var issues = rows.SelectMany(r => r.Issues.Select(i => $"row {r.Row}: {i}")).ToList();
        var changes = new List<string>();
        if (first.Vessel is null) issues.Add("Vessel is required.");
        if (first.Port is null) issues.Add("Port is required.");
        if (first.Eta is null || first.Etd is null) issues.Add("ETA and ETD are required.");
        foreach (var r in rows.Skip(1).Where(r => r.Vessel != first.Vessel || r.Port != first.Port || r.Eta != first.Eta || r.Etd != first.Etd))
            issues.Add($"row {r.Row}: vessel, port, ETA and ETD must match the first row of the call (row {first.Row}).");

        var lineCodes = rows.Select(r => r.Line).OfType<string>().Distinct().ToList();
        ScheduleImportCall Report(string action, DateTimeOffset? eta = null, DateTimeOffset? etd = null, string? callRef = null) =>
            new(key, rows.Select(r => r.Row).ToList(), action, callRef ?? first.CallRef, first.Vessel, first.Port, eta, etd, lineCodes, changes, issues);

        if (issues.Count > 0) return new PlannedCall(Report("ERROR"), null);

        // The port's clock turns the sheet's wall-clock times into instants.
        var port = (await master.PortsAsync([first.Port!], ct)).GetValueOrDefault(first.Port!);
        var zone = port?.TimeZone is { Length: > 0 } z && TimeZoneInfo.TryFindSystemTimeZoneById(z, out var found)
            ? found : TimeZoneInfo.FindSystemTimeZoneById(BranchClockZone);
        DateTimeOffset? At(DateTime? local) => local is { } l ? new DateTimeOffset(l, zone.GetUtcOffset(l)) : null;

        var cutoffs = new List<VesselCallCutoffItem>();
        if (rows.Select(r => r.PortCutoff).FirstOrDefault(c => c is not null) is { } pc) cutoffs.Add(new VesselCallCutoffItem("PORT_DRY", At(pc)));
        if (rows.Select(r => r.YardCutoff).FirstOrDefault(c => c is not null) is { } yc) cutoffs.Add(new VesselCallCutoffItem("YARD_DRY", At(yc)));

        var lineItems = rows.Where(r => r.Line is not null).DistinctBy(r => r.Line)
            .Select(r => new VesselCallLineItem(r.Line!, r.Agent, r.LineVoyageIn, r.LineVoyageOut, r.Service)).ToList();
        var request = new SaveVesselCallRequest(first.Vessel!, first.Port!, At(first.Eta), At(first.Etd), first.CallRef, first.Terminal,
            first.VoyageIn, first.VoyageOut, At(first.Etb), first.Remarks, lineItems, cutoffs);

        var errors = new Dictionary<string, List<string>>();
        var header = await ResolveHeaderAsync(request, master, errors, ct);
        if (request.Etd <= request.Eta) errors.Add("etd", "ETD must be after ETA.");
        if (header is null || errors.Count > 0)
        {
            issues.AddRange(errors.SelectMany(e => e.Value));
            return new PlannedCall(Report("ERROR", request.Eta, request.Etd), null);
        }

        var existing = first.CallRef is { } reference
            ? await db.VesselCalls.SingleOrDefaultAsync(c => c.CallRef == reference, ct)
            : first.VoyageOut is { } voyage
                ? await db.VesselCalls.SingleOrDefaultAsync(c => c.VesselId == header.Vessel.VesselId && c.PortId == header.Port.PortId && c.OperatorVoyageOut == voyage, ct)
                : null;

        if (existing is null)
        {
            if (lineItems.Count == 0) errors.Add("lines", "A new call needs at least one line.");
            var lines = await ResolveLinesAsync(db, master, lineItems, header.Port.PortId, excludeCallId: null, errors, ct);
            var resolvedCutoffs = await ResolveCutoffsAsync(master, cutoffs, lines.Select(l => l.LinePartyCode), request.Etd, errors, ct);
            var callRef = request.CallRef.Clean() ?? DefaultCallRef(header.Vessel.VesselCode, request);
            if (await db.VesselCalls.AnyAsync(c => c.CallRef == callRef, ct)) errors.Add("callRef", $"Call reference '{callRef}' is already used by another call.");
            if (errors.Count > 0)
            {
                issues.AddRange(errors.SelectMany(e => e.Value));
                return new PlannedCall(Report("ERROR", request.Eta, request.Etd, callRef), null);
            }

            changes.Add($"new call with {lines.Count} line(s) and {resolvedCutoffs.Count} cut-off(s)");
            return new PlannedCall(Report("CREATE", request.Eta, request.Etd, callRef), async token =>
            {
                var call = new VesselCall { TenantId = tenantId, CallRef = callRef, Source = "IMPORT" };
                ApplyHeader(call, request, header);
                db.VesselCalls.Add(call);
                await db.SaveChangesAsync(token);
                foreach (var line in lines) { line.TenantId = tenantId; line.VesselCallId = call.VesselCallId; db.VesselCallLines.Add(line); }
                await AddCutoffsAsync(db, master, call, resolvedCutoffs, tenantId, token);
                await db.SaveChangesAsync(token);
            });
        }

        if (existing.IsCancelled)
        {
            issues.Add($"Call {existing.CallRef} is cancelled; an import does not revive it.");
            return new PlannedCall(Report("ERROR", request.Eta, request.Etd, existing.CallRef), null);
        }

        var onCall = await db.VesselCallLines.Where(l => l.VesselCallId == existing.VesselCallId).Select(l => l.LinePartyCode).ToListAsync(ct);
        var newItems = lineItems.Where(i => !onCall.Contains(i.LineCode, StringComparer.OrdinalIgnoreCase)).ToList();
        var newLines = await ResolveLinesAsync(db, master, newItems, header.Port.PortId, excludeCallId: existing.VesselCallId, errors, ct);
        var resolved = await ResolveCutoffsAsync(master, cutoffs, onCall.Concat(newLines.Select(l => l.LinePartyCode)), request.Etd, errors, ct);
        var kept = await db.VesselCallCutoffs.Where(c => c.VesselCallId == existing.VesselCallId && c.CutoffAt > request.Etd
            && !cutoffs.Select(x => x.Kind).Contains(c.CutoffKind)).Select(c => c.CutoffKind).ToListAsync(ct);
        if (kept.Count > 0) errors.Add("etd", $"The new ETD is before these cut-offs already on the call: {string.Join(", ", kept)}.");
        if (errors.Count > 0)
        {
            issues.AddRange(errors.SelectMany(e => e.Value));
            return new PlannedCall(Report("ERROR", request.Eta, request.Etd, existing.CallRef), null);
        }

        if (existing.Eta != request.Eta) changes.Add($"ETA {existing.Eta:dd MMM HH:mm} → {request.Eta:dd MMM HH:mm}");
        if (existing.Etd != request.Etd) changes.Add($"ETD {existing.Etd:dd MMM HH:mm} → {request.Etd:dd MMM HH:mm}");
        if (existing.Etb != request.Etb) changes.Add("ETB changed");
        if ((existing.Remarks ?? "") != (request.Remarks?.Trim() ?? "")) changes.Add("remarks changed");
        if (newLines.Count > 0) changes.Add($"adds line(s) {string.Join(", ", newLines.Select(l => l.LinePartyCode))}");
        var current = await db.VesselCallCutoffs.Where(c => c.VesselCallId == existing.VesselCallId && c.LinePartyCode == null && c.BranchId == null)
            .ToDictionaryAsync(c => c.CutoffKind, c => c.CutoffAt, ct);
        foreach (var c in cutoffs.Where(c => !current.TryGetValue(c.Kind, out var at) || at != c.At))
            changes.Add($"{c.Kind} → {c.At:dd MMM HH:mm}");

        if (changes.Count == 0) return new PlannedCall(Report("UNCHANGED", request.Eta, request.Etd, existing.CallRef), null);

        var callId = existing.VesselCallId;
        return new PlannedCall(Report("UPDATE", request.Eta, request.Etd, existing.CallRef), async token =>
        {
            var call = await db.VesselCalls.SingleAsync(c => c.VesselCallId == callId, token);
            ApplyHeader(call, request with { CallRef = call.CallRef }, header);
            foreach (var line in newLines) { line.TenantId = tenantId; line.VesselCallId = callId; db.VesselCallLines.Add(line); }
            if (resolved.Count > 0)
            {
                var kinds = resolved.Select(r => r.Rule.Kind).ToList();
                db.VesselCallCutoffs.RemoveRange(await db.VesselCallCutoffs
                    .Where(c => c.VesselCallId == callId && c.LinePartyCode == null && c.BranchId == null && kinds.Contains(c.CutoffKind)).ToListAsync(token));
                await db.SaveChangesAsync(token);
                await AddCutoffsAsync(db, master, call, resolved, tenantId, token);
            }
            await db.SaveChangesAsync(token);
        });
    }

    private const string BranchClockZone = "Asia/Bangkok";
}
