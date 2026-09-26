using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Gecko.Data;
using Gecko.MasterData.Contracts;
using Gecko.Revenue.Application;
using Gecko.Revenue.Domain;
using Gecko.Revenue.Endpoints.Tariffs;
using Gecko.Revenue.Infrastructure.Persistence;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Gecko.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Endpoints.Imports;

public sealed record ImportIssueView(string? Column, string Severity, string Code, string Message);

public sealed record ImportRowView(string Sheet, int RowNo, string? Action, string Status, Guid? RateKey, IReadOnlyList<ImportIssueView> Issues);

public sealed record ImportPreview(
    Guid ImportBatchId, Guid ScheduleId, string Status, string FileName, bool ScheduleChangedSinceExport,
    int RowsTotal, int RowsOk, int RowsWarning, int RowsError,
    int RowsInsert, int RowsUpdate, int RowsDelete, int RowsUnchanged,
    string? FailureMessage, IReadOnlyList<ImportRowView> Rows);

/// <summary>
/// Excel in and out for a tariff (ROADMAP 2.7, decisions 5 and 6).
///
///   GET  /tariffs/{id}/template     download the tariff as a workbook (records the token)
///   POST /tariffs/{id}/imports      upload it → parsed, validated, previewed; NOTHING applied
///   GET  /imports/{batchId}         the preview again
///   POST /imports/{batchId}/confirm apply it — only if still error-free and the draft has not moved
///   POST /imports/{batchId}/cancel
///
/// The preview is durable (import.*): a user can upload at 17:00 and confirm
/// at 09:00, and the accountant can later see exactly which file changed which
/// rate (tos_rate.import_row_id).
/// </summary>
internal static class ImportEndpoints
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string Sheet = TariffWorkbook.RatesSheet;
    private const string RemovedSheet = "(removed)";

    public static RouteGroupBuilder MapImportEndpoints(this RouteGroupBuilder revenue)
    {
        var tariffs = revenue.MapGroup("/tariffs").WithTags("Revenue — Excel import");
        tariffs.MapGet("/{scheduleId:guid}/template", TemplateAsync).RequirePermission(RevenuePermissions.ImportManage)
            .WithSummary("Download the tariff as an Excel workbook to edit and upload back");
        tariffs.MapPost("/{scheduleId:guid}/imports", UploadAsync).RequirePermission(RevenuePermissions.ImportManage)
            .DisableAntiforgery()
            .WithSummary("Upload an edited workbook: validated and previewed, not applied");

        var imports = revenue.MapGroup("/imports").WithTags("Revenue — Excel import");
        imports.MapGet("/{batchId:guid}", PreviewAsync).RequirePermission(RevenuePermissions.ImportManage).WithName("GetImport")
            .WithSummary("The preview of an upload");
        imports.MapPost("/{batchId:guid}/confirm", ConfirmAsync).RequirePermission(RevenuePermissions.ImportManage)
            .WithSummary("Apply a previewed upload to the draft tariff");
        imports.MapPost("/{batchId:guid}/cancel", CancelAsync).RequirePermission(RevenuePermissions.ImportManage)
            .WithSummary("Abandon an upload");
        return revenue;
    }

    // ── download ────────────────────────────────────────────────────────────

    private static async Task<Results<FileContentHttpResult, NotFound>> TemplateAsync(
        Guid scheduleId, RevenueDbContext db, IMasterDataReferences masterData, ITenantContext caller, CancellationToken ct)
    {
        var schedule = await db.Schedules.AsNoTracking().SingleOrDefaultAsync(s => s.ScheduleId == scheduleId, ct);
        if (schedule is null) return TypedResults.NotFound();

        var current = await CurrentRatesAsync(db, scheduleId, ct);
        var lines = current.Select(c => new TariffWorkbook.Line(
            c.Rate.TosRateId, c.Rate.ChargeCode, c.Rate.BillTo, c.Rate.PaymentTermCode, c.Rate.CreditTermDays,
            c.Rate.OrderTypeCode, c.Rate.MovementCode, c.Rate.EquipmentTypeCode, c.Rate.EquipmentSize,
            c.Rate.CargoCategoryCode, c.Rate.TruckCategoryCode, c.Rate.BillingUnitCode,
            c.Rate.PricingMethod, c.Rate.TierBasis, c.Rate.Rate, TierText.Format(c.Tiers),
            string.Join(" | ", c.Conditions.Select(ConditionEvaluator.Describe)))).ToList();

        var token = Guid.NewGuid();
        db.TemplateExports.Add(new TemplateExport
        {
            TemplateExportId = Guid.CreateVersion7(),
            TenantId = caller.TenantId(),
            Token = token,
            ScheduleId = scheduleId,
            ScheduleRowVersion = schedule.RowVersion,
            ModuleCode = schedule.ModuleCode,
            TemplateKind = "TOS_RATE",
            TemplateVersion = (short)TariffWorkbook.LayoutVersion,
            RowCount = lines.Count,
            GeneratedBy = caller.UserId,
        });
        await db.SaveChangesAsync(ct);

        var equipment = await masterData.ActiveEquipmentTypesAsync(ct);
        var lists = new TariffWorkbook.Lists(
            await masterData.ChargeCodesForModuleAsync(schedule.ModuleCode, ct),
            await db.BillToRoles.AsNoTracking().Where(b => b.IsActive).OrderBy(b => b.SortOrder).Select(b => b.Code).ToListAsync(ct),
            await db.PaymentTerms.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.DisplayOrder).Select(p => p.Code).ToListAsync(ct),
            await db.BillingUnits.AsNoTracking().Where(b => b.IsActive).OrderBy(b => b.DisplayOrder).Select(b => b.Code).ToListAsync(ct),
            await masterData.ActiveOrderTypeCodesAsync(ct),
            await masterData.MovementCodesForModuleAsync(schedule.ModuleCode, ct),
            equipment.Select(e => e.TypeCode).ToList(),
            equipment.Select(e => e.SizeCode).Distinct().OrderBy(z => int.TryParse(z, out var n) ? n : int.MaxValue).ToList(),
            await masterData.CodeListAsync("CARGO_CATEGORY", ct),
            await masterData.CodeListAsync("TRUCK_CATEGORY", ct));

        var bytes = TariffWorkbook.Write(
            new TariffWorkbook.Header(token, scheduleId, schedule.ScheduleNo, schedule.VersionNo, TariffWorkbook.LayoutVersion),
            schedule.Name, lines, lists);
        return TypedResults.File(bytes, XlsxContentType, $"{schedule.ScheduleNo.Replace('/', '-')}-v{schedule.VersionNo}.xlsx");
    }

    // ── upload → preview ────────────────────────────────────────────────────

    private static async Task<Results<CreatedAtRoute<ImportPreview>, NotFound, ValidationProblem, ProblemHttpResult>> UploadAsync(
        Guid scheduleId, IFormFile file, RevenueDbContext db, RateSetValidator validator, ITenantContext caller, CancellationToken ct)
    {
        var schedule = await db.Schedules.AsNoTracking().SingleOrDefaultAsync(s => s.ScheduleId == scheduleId, ct);
        if (schedule is null) return TypedResults.NotFound();
        if (ScheduleEndpoints.NotEditable(schedule) is { } locked) return locked;
        if (file.Length == 0 || file.Length > MaxFileBytes)
            return RevenueSupport.Invalid("file", $"Upload an .xlsx file of at most {MaxFileBytes / 1024 / 1024} MB.");

        await using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var sha = SHA256.HashData(buffer.ToArray());
        buffer.Position = 0;

        var read = TariffWorkbook.Read(buffer);
        if (read.Header is null) return RevenueSupport.Invalid("file", read.Error!);

        var export = await db.TemplateExports.AsNoTracking()
            .SingleOrDefaultAsync(e => e.Token == read.Header.Token, ct);
        if (export is null || export.ScheduleId != scheduleId)
            return RevenueSupport.Invalid("file",
                $"This workbook was downloaded for {read.Header.ScheduleNo} v{read.Header.VersionNo}, not for {schedule.ScheduleNo} v{schedule.VersionNo}. Download this tariff's template.");
        if (read.Header.LayoutVersion != TariffWorkbook.LayoutVersion)
            return RevenueSupport.Invalid("file", "This workbook uses an older layout. Download a fresh template.");
        if (read.Error is not null) return RevenueSupport.Invalid("file", read.Error);

        if (await db.ImportBatches.AnyAsync(b => b.ScheduleId == scheduleId && b.FileSha256 == sha && b.Status == "APPLIED", ct))
            return RevenueSupport.Conflict("This exact file has already been applied to this tariff.");

        var batch = new ImportBatch
        {
            ImportBatchId = Guid.CreateVersion7(),
            TenantId = caller.TenantId(),
            TemplateExportId = export.TemplateExportId,
            ScheduleId = scheduleId,
            TemplateKind = "TOS_RATE",
            FileName = Path.GetFileName(file.FileName),
            FileSha256 = sha,
            FileSizeBytes = (int)file.Length,
            UploadedBy = caller.UserId,
            ScheduleRowVersion = schedule.RowVersion,
            ScheduleChangedSinceExport = !export.ScheduleRowVersion.SequenceEqual(schedule.RowVersion),
        };

        var plan = await PlanAsync(db, validator, schedule, read.Rows, batch, ct);
        db.ImportBatches.Add(batch);
        db.ImportRows.AddRange(plan.Rows);
        db.ImportRowIssues.AddRange(plan.Issues);
        await db.SaveChangesAsync(ct);

        return TypedResults.CreatedAtRoute(await PreviewOfAsync(db, batch.ImportBatchId, ct), "GetImport", new { batchId = batch.ImportBatchId });
    }

    private sealed record Plan(List<ImportRow> Rows, List<ImportRowIssue> Issues);

    /// <summary>Parses every line, validates the whole set, and decides what each line would do.</summary>
    private static async Task<Plan> PlanAsync(
        RevenueDbContext db, RateSetValidator validator, Schedule schedule,
        IReadOnlyList<TariffWorkbook.ReadRow> lines, ImportBatch batch, CancellationToken ct)
    {
        var current = (await CurrentRatesAsync(db, schedule.ScheduleId, ct)).ToDictionary(c => c.Rate.TosRateId);
        var rows = new List<ImportRow>();
        var issues = new List<ImportRowIssue>();
        var items = new List<(ImportRow Row, RateItem Item)>();
        var seenKeys = new HashSet<Guid>();

        void Issue(ImportRow row, string? column, string code, string message, string severity = "ERROR")
        {
            issues.Add(new ImportRowIssue
            {
                ImportRowIssueId = Guid.CreateVersion7(), TenantId = batch.TenantId, ImportRowId = row.ImportRowId,
                ColumnName = column, Severity = severity, IssueCode = code, Message = message,
            });
            if (severity == "ERROR") row.Status = "ERROR";
            else if (row.Status == "OK") row.Status = "WARNING";
        }

        foreach (var line in lines)
        {
            var c = line.Cells;
            var row = new ImportRow
            {
                ImportRowId = Guid.CreateVersion7(), TenantId = batch.TenantId, ImportBatchId = batch.ImportBatchId,
                SheetName = Sheet, RowNo = line.RowNo, Status = "OK",
                RawJson = JsonSerializer.Serialize(c, JsonSerializerOptions.Web),
            };
            rows.Add(row);

            // the key
            if (c["RateKey"] is { } keyText)
            {
                if (!Guid.TryParse(keyText, out var key) || !current.ContainsKey(key))
                    Issue(row, "RateKey", "UNKNOWN_RATE_KEY", "This RateKey does not belong to this tariff. Leave it blank for a new line — never type or copy it.");
                else if (!seenKeys.Add(key))
                    // A copied row carries the (hidden) key of the row it was copied from.
                    // The top-most occurrence stays the edit of that rate; this one is new.
                    Issue(row, "RateKey", "COPIED_ROW", "Copied row — added as a new rate.", "WARNING");
                else
                    row.TargetId = key;
            }

            // typed cells
            decimal? Number(string column)
            {
                if (c[column] is not { } text) return null;
                if (decimal.TryParse(text.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var n)) return n;
                Issue(row, column, "NOT_A_NUMBER", $"'{text}' is not a number.");
                return null;
            }
            var credit = Number("CreditDays");
            var rate = Number("Rate");
            var (tiers, tierError) = TierText.Parse(c["Tiers"]);
            if (tierError is not null) Issue(row, "Tiers", "BAD_TIERS", tierError);
            foreach (var required in new[] { "ChargeCode", "BillTo", "PaymentTerm" })
                if (c[required] is null) Issue(row, required, "REQUIRED", $"{required} is required.");

            if (row.Status == "ERROR") continue;

            // Q9: conditions are not edited in Excel; a line that keeps its key keeps them.
            var kept = row.TargetId is { } k && current[k].Conditions.Count > 0
                ? current[k].Conditions.Select(x => new ConditionItem(x.Axis, x.Op, x.ModifierOp, x.ModifierValue,
                    x.Values.ToList(), x.Number, x.Flag, x.Label)).ToList()
                : null;

            items.Add((row, new RateItem(
                c["ChargeCode"]!, c["BillTo"]!, c["PaymentTerm"]!,
                credit is null ? null : (short)credit.Value,
                c["OrderType"], c["Movement"], c["EquipmentType"], c["Size"], c["CargoCategory"], c["TruckCategory"],
                c["BillingUnit"], c["PricingMethod"] ?? PricingMethods.Flat, c["TierBasis"], rate,
                tiers.Select(t => new TierItem(t.FromQty, t.ToQty, t.Rate)).ToList(), kept)));
        }

        // the whole set, as the rate PUT would see it
        var result = await validator.ValidateAsync(schedule, items.Select(i => i.Item).ToList(), ct);
        foreach (var (key, messages) in result.Errors)
        {
            var index = int.Parse(key[(key.IndexOf('[') + 1)..key.IndexOf(']')], CultureInfo.InvariantCulture);
            var column = key.Contains('.') ? ColumnFor(key[(key.IndexOf('.') + 1)..]) : null;
            foreach (var message in messages) Issue(items[index].Row, column, "INVALID", message);
        }

        foreach (var valid in result.Rates)
        {
            var (row, item) = items[valid.Index];
            row.ResolvedJson = JsonSerializer.Serialize(item, JsonSerializerOptions.Web);
            row.Action = row.TargetId is { } key
                ? Canonical(valid.Rate, valid.Tiers.Select(t => new Tier(t.FromQty, t.ToQty, t.Rate)))
                  == Canonical(current[key].Rate, current[key].Tiers) ? "UNCHANGED" : "UPDATE"
                : "INSERT";
        }

        // lines that disappeared
        var removedNo = 0;
        foreach (var gone in current.Values.Where(v => !seenKeys.Contains(v.Rate.TosRateId)))
            rows.Add(new ImportRow
            {
                ImportRowId = Guid.CreateVersion7(), TenantId = batch.TenantId, ImportBatchId = batch.ImportBatchId,
                SheetName = RemovedSheet, RowNo = ++removedNo, TargetId = gone.Rate.TosRateId, Action = "DELETE", Status = "OK",
                RawJson = JsonSerializer.Serialize(new { gone.Rate.ChargeCode, gone.Rate.BillTo, gone.Rate.PaymentTermCode, gone.Rate.AxisSignature }, JsonSerializerOptions.Web),
            });

        if (batch.ScheduleChangedSinceExport && rows.FirstOrDefault(r => r.SheetName == Sheet) is { } first)
            Issue(first, null, "SCHEDULE_CHANGED", "The tariff changed after this workbook was downloaded. Lines you did not touch will overwrite those changes.", "WARNING");

        var sheetRows = rows.Where(r => r.SheetName == Sheet).ToList();
        batch.RowsTotal = sheetRows.Count;
        batch.RowsOk = sheetRows.Count(r => r.Status == "OK");
        batch.RowsWarning = sheetRows.Count(r => r.Status == "WARNING");
        batch.RowsError = sheetRows.Count(r => r.Status == "ERROR");
        batch.RowsInsert = rows.Count(r => r.Action == "INSERT");
        batch.RowsUpdate = rows.Count(r => r.Action == "UPDATE");
        batch.RowsUnchanged = rows.Count(r => r.Action == "UNCHANGED");
        batch.RowsDelete = rows.Count(r => r.Action == "DELETE");
        batch.Status = "VALIDATED";
        return new Plan(rows, issues);
    }

    private static string ColumnFor(string field) => field.Split('.', '[')[0] switch
    {
        "chargeCode" => "ChargeCode", "billTo" => "BillTo", "paymentTermCode" => "PaymentTerm",
        "orderTypeCode" => "OrderType", "movementCode" => "Movement", "equipmentTypeCode" => "EquipmentType",
        "equipmentSize" => "Size", "cargoCategoryCode" => "CargoCategory", "truckCategoryCode" => "TruckCategory",
        "billingUnitCode" => "BillingUnit", "tierBasis" => "TierBasis", "rate" => "Rate", "tiers" => "Tiers",
        "conditions" => "Conditions", var other => other,
    };

    private static string Canonical(TosRate r, IEnumerable<Tier> tiers) => string.Join('|',
        r.ChargeCode, r.BillTo, r.PaymentTermCode, r.CreditTermDays, r.OrderTypeCode, r.MovementCode,
        r.EquipmentTypeCode, r.EquipmentSize, r.CargoCategoryCode, r.TruckCategoryCode, r.BillingUnitCode,
        r.PricingMethod, r.TierBasis, r.Rate?.ToString("0.####", CultureInfo.InvariantCulture), TierText.Format(tiers));

    // ── confirm / cancel / read ─────────────────────────────────────────────

    private static async Task<Results<Ok<ImportPreview>, NotFound, ValidationProblem, ProblemHttpResult>> ConfirmAsync(
        Guid batchId, RevenueDbContext db, RateSetValidator validator, ITenantContext caller, TimeProvider clock, CancellationToken ct)
    {
        var batch = await db.ImportBatches.SingleOrDefaultAsync(b => b.ImportBatchId == batchId, ct);
        if (batch is null) return TypedResults.NotFound();
        if (batch.Status != "VALIDATED") return RevenueSupport.Conflict($"This upload is {batch.Status}.");
        if (batch.RowsError > 0) return RevenueSupport.Conflict($"{batch.RowsError} line(s) have errors. Fix the workbook and upload it again.");

        var schedule = await db.Schedules.SingleAsync(s => s.ScheduleId == batch.ScheduleId, ct);
        if (ScheduleEndpoints.NotEditable(schedule) is { } locked) return locked;
        if (batch.ScheduleRowVersion is null || !batch.ScheduleRowVersion.SequenceEqual(schedule.RowVersion))
            return RevenueSupport.Conflict("The tariff was edited after this upload was checked.", "Upload the workbook again to see a current preview.");

        var rows = await db.ImportRows.AsNoTracking()
            .Where(r => r.ImportBatchId == batchId && r.SheetName == Sheet && r.ResolvedJson != null)
            .OrderBy(r => r.RowNo)
            .ToListAsync(ct);
        var items = rows.Select(r => JsonSerializer.Deserialize<RateItem>(r.ResolvedJson!, JsonSerializerOptions.Web)!).ToList();

        // Master data may have moved since the preview; check again rather than trust it.
        var result = await validator.ValidateAsync(schedule, items, ct);
        if (!result.IsValid)
            return RevenueSupport.Conflict("The upload no longer validates (master data changed since the preview).",
                string.Join(" ", result.Errors.SelectMany(e => e.Value).Distinct()));

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.TrySetExpectedVersion(schedule, Convert.ToBase64String(batch.ScheduleRowVersion));
        await RateSetWriter.RemoveRatesAsync(db, schedule.ScheduleId, ct);
        schedule.UpdatedAt = clock.GetUtcNow();
        if (await db.SaveOrConflictAsync(ct) is { } conflict) return conflict;

        foreach (var valid in result.Rates)
        {
            valid.Rate.Source = "IMPORTED";
            valid.Rate.ImportRowId = rows[valid.Index].ImportRowId;
        }
        RateSetWriter.Add(db, result.Rates);

        var now = clock.GetUtcNow();
        batch.Status = "APPLIED";
        batch.ConfirmedAt = now;
        batch.ConfirmedBy = caller.UserId;
        batch.AppliedAt = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return TypedResults.Ok(await PreviewOfAsync(db, batchId, ct));
    }

    private static async Task<Results<Ok<ImportPreview>, NotFound, ProblemHttpResult>> CancelAsync(
        Guid batchId, RevenueDbContext db, CancellationToken ct)
    {
        var batch = await db.ImportBatches.SingleOrDefaultAsync(b => b.ImportBatchId == batchId, ct);
        if (batch is null) return TypedResults.NotFound();
        if (batch.Status is "APPLIED" or "CANCELLED") return RevenueSupport.Conflict($"This upload is already {batch.Status}.");
        batch.Status = "CANCELLED";
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await PreviewOfAsync(db, batchId, ct));
    }

    private static async Task<Results<Ok<ImportPreview>, NotFound>> PreviewAsync(Guid batchId, RevenueDbContext db, CancellationToken ct) =>
        await db.ImportBatches.AnyAsync(b => b.ImportBatchId == batchId, ct)
            ? TypedResults.Ok(await PreviewOfAsync(db, batchId, ct))
            : TypedResults.NotFound();

    private static async Task<ImportPreview> PreviewOfAsync(RevenueDbContext db, Guid batchId, CancellationToken ct)
    {
        var b = await db.ImportBatches.AsNoTracking().SingleAsync(x => x.ImportBatchId == batchId, ct);
        var rows = await db.ImportRows.AsNoTracking().Where(r => r.ImportBatchId == batchId)
            .OrderBy(r => r.SheetName == Sheet ? 0 : 1).ThenBy(r => r.RowNo).ToListAsync(ct);
        var rowIds = rows.Select(r => r.ImportRowId).ToList();
        var issues = (await db.ImportRowIssues.AsNoTracking().Where(i => rowIds.Contains(i.ImportRowId)).ToListAsync(ct))
            .ToLookup(i => i.ImportRowId);

        return new ImportPreview(b.ImportBatchId, b.ScheduleId, b.Status, b.FileName, b.ScheduleChangedSinceExport,
            b.RowsTotal, b.RowsOk, b.RowsWarning, b.RowsError, b.RowsInsert, b.RowsUpdate, b.RowsDelete, b.RowsUnchanged,
            b.FailureMessage,
            rows.Select(r => new ImportRowView(r.SheetName, r.RowNo, r.Action, r.Status, r.TargetId,
                issues[r.ImportRowId].Select(i => new ImportIssueView(i.ColumnName, i.Severity, i.IssueCode, i.Message)).ToList())).ToList());
    }

    // ── shared ──────────────────────────────────────────────────────────────

    private sealed record CurrentRate(TosRate Rate, IReadOnlyList<Tier> Tiers, IReadOnlyList<ConditionSpec> Conditions);

    private static async Task<List<CurrentRate>> CurrentRatesAsync(RevenueDbContext db, Guid scheduleId, CancellationToken ct)
    {
        var rates = await db.TosRates.AsNoTracking().Where(r => r.ScheduleId == scheduleId)
            .OrderBy(r => r.ChargeCode).ThenBy(r => r.BillTo).ThenBy(r => r.PaymentTermCode).ThenByDescending(r => r.Specificity)
            .ToListAsync(ct);
        var ids = rates.Select(r => r.TosRateId).ToList();
        var tiers = (await db.RateTiers.AsNoTracking().Where(t => t.OwnerType == "TOS_RATE" && ids.Contains(t.OwnerId)).ToListAsync(ct))
            .ToLookup(t => t.OwnerId);
        var conditions = await db.RateConditions.AsNoTracking().Where(c => c.OwnerType == "TOS_RATE" && ids.Contains(c.OwnerId))
            .OrderBy(c => c.SequenceNo).ToListAsync(ct);
        var conditionIds = conditions.Select(c => c.RateConditionId).ToList();
        var values = (await db.RateConditionValues.AsNoTracking().Where(v => conditionIds.Contains(v.RateConditionId)).ToListAsync(ct))
            .ToLookup(v => v.RateConditionId, v => v.ValueCode);
        var byRate = conditions.ToLookup(c => c.OwnerId);

        return rates.Select(r => new CurrentRate(
            r,
            tiers[r.TosRateId].Select(t => new Tier(t.FromQty, t.ToQty, t.Rate)).ToList(),
            byRate[r.TosRateId].Select(c => new ConditionSpec(c.SequenceNo, c.Axis, c.Op, values[c.RateConditionId].ToList(),
                c.NumberValue, c.BoolValue, c.ModifierOp, c.ModifierValue, c.Label)).ToList())).ToList();
    }
}
