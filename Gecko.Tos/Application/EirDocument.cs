using Gecko.Data.Documents;
using Gecko.MasterData.Contracts;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Gecko.Tos.Application;

/// <summary>
/// The printed EIR (PLAN 5.6): the document the driver walks away with, and in
/// Thailand a numbered document — so a VOIDED EIR still prints, with its number
/// and a VOID mark, never as if it had not existed (Q4).
///
/// Everything on it comes from gecko_tos rows written at the barrier, plus party
/// NAMES from MDM (read at print time: a name is a label, not a fact of the move).
/// Times are the BRANCH's clock — a 23:40 gate-in in Laem Chabang is the 23rd there.
///
/// PDPA: the driver's name and the truck plate are on it because they are on the
/// paper EIR today; the licence number is not (only its hash is stored).
/// </summary>
internal sealed class EirDocument(TosDbContext db, IMasterDataReferences master, BranchClock clock)
{
    public sealed record Rendered(string FileName, byte[] Pdf);

    public async Task<Rendered?> RenderAsync(Guid gateTransactionId, CancellationToken ct)
    {
        var row = await (
            from t in db.GateTransactions.AsNoTracking().Where(x => x.GateTransactionId == gateTransactionId)
            join b in db.Bookings on t.BookingId equals b.BookingId
            join v in db.TruckVisits on t.TruckVisitId equals v.TruckVisitId
            select new { g = t, b.OrderNo, b.OrderTypeCode, b.CustomerPartyCode, b.HaulierPartyCode, b.CustomerRef, v.TruckPlate, v.TrailerPlate, v.DriverName, v.VisitNo })
            .SingleOrDefaultAsync(ct);
        if (row is null) return null;
        var g = row.g;

        var seals = await db.GateTransactionSeals.AsNoTracking().Where(s => s.GateTransactionId == g.GateTransactionId)
            .OrderBy(s => s.SealType).Select(s => new { s.SealType, s.SealNo, s.IsIntact }).ToListAsync(ct);
        var survey = await db.Surveys.AsNoTracking().Where(s => s.GateTransactionId == g.GateTransactionId)
            .OrderByDescending(s => s.SurveyedAt).FirstOrDefaultAsync(ct);
        var damages = survey is null ? [] : await db.SurveyDamages.AsNoTracking()
            .Where(d => d.SurveyId == survey.SurveyId).OrderBy(d => d.LineNo).ToListAsync(ct);
        var replaced = g.ReplacesGateTransactionId is { } r
            ? await db.GateTransactions.AsNoTracking().Where(x => x.GateTransactionId == r).Select(x => x.EirNo).SingleOrDefaultAsync(ct)
            : null;

        var parties = await master.PartiesAsync(new[] { g.LinePartyCode, row.CustomerPartyCode, row.HaulierPartyCode }.OfType<string>(), ct);
        string Party(string? code) => code is null ? "—" : parties.TryGetValue(code, out var p) ? $"{p.Name} ({code})" : code;

        var branch = (await clock.BranchesAsync([g.BranchId], ct)).GetValueOrDefault(g.BranchId);
        string Local(DateTimeOffset at) => (branch is null ? at : TimeZoneInfo.ConvertTime(at, branch.Zone)).ToString("dd MMM yyyy  HH:mm");

        GeckoPdf.EnsureInitialised();
        var isIn = g.Direction == "IN";
        var voided = g.Status == "VOIDED";

        var pdf = Document.Create(document => document.Page(page =>
        {
            GeckoPdf.Page(page);

            page.Header().Row(header =>
            {
                header.RelativeItem().Column(c =>
                {
                    c.Item().Text("EQUIPMENT INTERCHANGE RECEIPT").FontSize(15).Bold();
                    c.Item().Text("ใบรับ-ส่งตู้คอนเทนเนอร์ (EIR)").FontSize(10);
                    c.Item().Text($"Depot {branch?.BranchCode ?? g.BranchId.ToString()}").FontColor(Colors.Grey.Darken2);
                });
                header.ConstantItem(200).AlignRight().Column(c =>
                {
                    c.Item().AlignRight().Text(g.EirNo).FontSize(14).Bold();
                    c.Item().AlignRight().Text($"{(isIn ? "GATE IN" : "GATE OUT")} · {g.MovementCode}").FontSize(11).SemiBold()
                        .FontColor(isIn ? Colors.Green.Darken2 : Colors.Blue.Darken2);
                    c.Item().AlignRight().Text(Local(g.TransactionAt));
                    if (voided) c.Item().AlignRight().Text("VOIDED").Bold().FontColor(Colors.Red.Darken2);
                });
            });

            page.Content().PaddingTop(12).Column(body =>
            {
                body.Spacing(10);

                body.Item().Border(1).BorderColor(Colors.Grey.Lighten1).Padding(8).Row(box =>
                {
                    box.RelativeItem(2).Column(c =>
                    {
                        c.Item().Text(Pretty(g.ContainerNo)).FontSize(20).Bold();
                        c.Item().Text(g.IsCheckDigitValid ? "Check digit OK" : $"CHECK DIGIT FAILED — {g.CheckDigitOverrideReason}")
                            .FontSize(7).FontColor(g.IsCheckDigitValid ? Colors.Grey.Darken1 : Colors.Red.Darken2);
                    });
                    box.RelativeItem().Column(c => { c.Field("Type", g.EquipmentTypeCode); c.Field("ISO", g.IsoCode); });
                    box.RelativeItem().Column(c => { c.Field("Full / empty", g.FullEmpty); c.Field("Line", g.LinePartyCode); });
                });

                body.Item().Row(parts =>
                {
                    parts.RelativeItem().Column(c =>
                    {
                        c.Spacing(3);
                        c.Item().Text("BOOKING").FontSize(8).Bold().FontColor(Colors.Grey.Darken2);
                        c.Field("Order", $"{row.OrderNo}  ({row.OrderTypeCode})");
                        c.Field("Customer ref", row.CustomerRef);
                        c.Field("Customer", Party(row.CustomerPartyCode));
                        c.Field("Line", Party(g.LinePartyCode));
                    });
                    parts.ConstantItem(16);
                    parts.RelativeItem().Column(c =>
                    {
                        c.Spacing(3);
                        c.Item().Text("TRUCK").FontSize(8).Bold().FontColor(Colors.Grey.Darken2);
                        c.Field("Plate", row.TrailerPlate is null ? row.TruckPlate : $"{row.TruckPlate} / {row.TrailerPlate}");
                        c.Field("Driver", row.DriverName);
                        c.Field("Haulier", Party(row.HaulierPartyCode));
                        c.Field("Visit", row.VisitNo);
                    });
                });

                body.Item().Row(facts =>
                {
                    facts.RelativeItem().Column(c =>
                    {
                        c.Spacing(3);
                        c.Item().Text("CONDITION").FontSize(8).Bold().FontColor(Colors.Grey.Darken2);
                        c.Field("Condition / grade", $"{g.ConditionCode ?? "—"} / {g.GradeCode ?? "—"}");
                        c.Field("Gross weight", g.GrossWeightKg is { } kg ? $"{kg:N0} kg ({g.WeightSource ?? "declared"})" : null);
                        c.Field("VGM", g.VgmKg is { } vgm ? $"{vgm:N0} kg {g.VgmMethod}" : null);
                        c.Field("Reefer temp", g.TempObservedC is { } t ? $"{t:0.0} °C" : null);
                        c.Field("Yard position", g.PositionText);
                    });
                    facts.ConstantItem(16);
                    facts.RelativeItem().Column(c =>
                    {
                        c.Spacing(3);
                        c.Item().Text("SEALS").FontSize(8).Bold().FontColor(Colors.Grey.Darken2);
                        if (seals.Count == 0) c.Item().Text("No seal recorded");
                        foreach (var s in seals)
                            c.Field(s.SealType, s.IsIntact ? s.SealNo : $"{s.SealNo}  — NOT INTACT");
                        if (g.SealMismatch) c.Item().Text("Seal differs from the one declared on the booking.").FontColor(Colors.Red.Darken2);
                    });
                });

                if (survey is not null)
                    body.Item().Column(c =>
                    {
                        c.Item().Text($"SURVEY · {(survey.IsServiceable ? "serviceable" : "NOT SERVICEABLE")} · {survey.SurveyorName}")
                            .FontSize(8).Bold().FontColor(survey.IsServiceable ? Colors.Grey.Darken2 : Colors.Red.Darken2);
                        if (damages.Count == 0) c.Item().Text("No damage found.");
                        else c.Item().Table(table =>
                        {
                            table.ColumnsDefinition(cols => { cols.ConstantColumn(24); cols.RelativeColumn(); cols.RelativeColumn(); cols.RelativeColumn(); cols.RelativeColumn(); cols.RelativeColumn(2); });
                            table.Header(h =>
                            {
                                foreach (var title in new[] { "#", "Location", "Component", "Damage", "Size (cm)", "Remarks" })
                                    h.Cell().BorderBottom(1).PaddingBottom(2).Text(title).FontSize(7).Bold();
                            });
                            foreach (var d in damages)
                            {
                                table.Cell().Text(d.LineNo.ToString());
                                table.Cell().Text(d.DamageLocationCode ?? "—");
                                table.Cell().Text(d.ComponentCode ?? "—");
                                table.Cell().Text(d.DamageCode + (d.IsPreExisting ? " (existing)" : ""));
                                table.Cell().Text(d.LengthCm is null ? "—" : $"{d.LengthCm:0} × {d.WidthCm:0}");
                                table.Cell().Text(d.Remarks ?? "");
                            }
                        });
                    });

                var notes = new List<string>();
                if (g.IsLate) notes.Add($"Accepted after the {g.CutoffKindApplied} cut-off ({(g.CutoffAtApplied is { } at ? Local(at) : "?")})" +
                                        (g.LateOverrideReason is { } why ? $" — override: {why}" : " — by exception"));
                if (replaced is not null) notes.Add($"Replaces EIR {replaced}.");
                if (voided) notes.Add($"VOIDED {(g.VoidedAt is { } va ? Local(va) : "")} — {g.VoidReason}");
                if (!string.IsNullOrWhiteSpace(g.Remarks)) notes.Add(g.Remarks);
                if (notes.Count > 0)
                    body.Item().Column(c => { foreach (var n in notes) c.Item().Text(n).Italic(); });

                body.Item().PaddingTop(28).Row(sign =>
                {
                    foreach (var who in new[] { "Driver / ผู้ขับรถ", "Gate clerk / เจ้าหน้าที่" })
                    {
                        sign.RelativeItem().Column(c =>
                        {
                            c.Item().PaddingTop(24).BorderTop(1).BorderColor(Colors.Grey.Darken1);
                            c.Item().AlignCenter().Text(who).FontSize(8);
                        });
                        sign.ConstantItem(40);
                    }
                });
            });

            if (voided) page.Foreground().VoidWatermark();

            page.Footer().Row(f =>
            {
                f.RelativeItem().Text($"Recorded {Local(g.RecordedAt)} · GECKO").FontSize(7).FontColor(Colors.Grey.Darken1);
                f.RelativeItem().AlignRight().Text(t => { t.Span("Page ").FontSize(7); t.CurrentPageNumber().FontSize(7); });
            });
        })).GeneratePdf();

        return new Rendered($"{g.EirNo}.pdf", pdf);
    }

    private static string Pretty(string box) => box.Length == 11 ? $"{box[..4]} {box[4..10]} {box[10..]}" : box;
}
