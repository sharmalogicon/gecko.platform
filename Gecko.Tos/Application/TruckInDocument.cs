using Gecko.Data.Documents;
using Gecko.MasterData.Contracts;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Gecko.Tos.Application;

/// <summary>
/// The truck-in form (owner D2, GATE_IN_COMPLETION_PLAN A10; Vector TMS_TruckInForm): one page per truck visit —
/// the truck, its driver and haulier, and every box it brought or took with movement, seals and weights. The EIRs
/// are the per-box documents; this is the yard's sheet for the whole truck. A voided move is listed, struck as VOID.
/// Times are the branch's clock; party names are MDM's at print time.
/// </summary>
internal sealed class TruckInDocument(TosDbContext db, IMasterDataReferences master, BranchClock clock)
{
    public sealed record Rendered(string FileName, byte[] Pdf);

    public async Task<Rendered?> RenderAsync(Guid truckVisitId, CancellationToken ct)
    {
        var visit = await db.TruckVisits.AsNoTracking().SingleOrDefaultAsync(v => v.TruckVisitId == truckVisitId, ct);
        if (visit is null) return null;

        var moves = await (
            from g in db.GateTransactions.AsNoTracking().Where(x => x.TruckVisitId == truckVisitId)
            join b in db.Bookings on g.BookingId equals b.BookingId
            orderby g.PositionNo, g.TransactionAt
            select new
            {
                g.GateTransactionId, g.PositionNo, g.ContainerNo, g.EquipmentTypeCode, g.HeightCode, g.Direction, g.MovementCode, g.FullEmpty,
                g.EirNo, g.Status, g.GrossWeightKg, g.TareWeightKg, g.CargoWeightKg, g.MaxGrossWeightKg, g.ConditionCode,
                g.CustomsPermitNo, g.TransactionAt, b.OrderNo, b.CustomerPartyCode, g.LinePartyCode,
            }).ToListAsync(ct);
        var ids = moves.Select(m => m.GateTransactionId).ToList();
        var seals = (await db.GateTransactionSeals.AsNoTracking().Where(s => ids.Contains(s.GateTransactionId) && s.DeletedAt == null)
                .OrderBy(s => s.SealType).ThenBy(s => s.SealNo).Select(s => new { s.GateTransactionId, s.SealNo }).ToListAsync(ct))
            .ToLookup(s => s.GateTransactionId, s => s.SealNo);

        var parties = await master.PartiesAsync(
            moves.SelectMany(m => new[] { m.CustomerPartyCode, m.LinePartyCode }).Append(visit.HaulierPartyCode).OfType<string>().Distinct(), ct);
        string Party(string? code) => code is null ? "—" : parties.TryGetValue(code, out var p) ? $"{p.Name} ({code})" : code;

        var branch = (await clock.BranchesAsync([visit.BranchId], ct)).GetValueOrDefault(visit.BranchId);
        string Local(DateTimeOffset at) => (branch is null ? at : TimeZoneInfo.ConvertTime(at, branch.Zone)).ToString("dd MMM yyyy  HH:mm");
        static string Kg(decimal? kg) => kg is { } v ? $"{v:N0}" : "—";

        GeckoPdf.EnsureInitialised();
        var pdf = Document.Create(document => document.Page(page =>
        {
            GeckoPdf.Page(page);
            page.Size(PageSizes.A4.Landscape());

            page.Header().Row(header =>
            {
                header.RelativeItem().Column(c =>
                {
                    c.Item().Text("TRUCK IN FORM").FontSize(15).Bold();
                    c.Item().Text("ใบรถเข้า-ออก").FontSize(10);
                    c.Item().Text($"Depot {branch?.BranchCode ?? visit.BranchId.ToString()}").FontColor(Colors.Grey.Darken2);
                });
                header.ConstantItem(220).AlignRight().Column(c =>
                {
                    c.Item().AlignRight().Text(visit.VisitNo).FontSize(14).Bold();
                    c.Item().AlignRight().Text($"Arrived {Local(visit.ArrivedAt)}");
                    if (visit.GateOutAt is { } left) c.Item().AlignRight().Text($"Left {Local(left)}");
                });
            });

            page.Content().PaddingTop(12).Column(body =>
            {
                body.Spacing(10);

                body.Item().Border(1).BorderColor(Colors.Grey.Lighten1).Padding(8).Row(truck =>
                {
                    truck.RelativeItem().Column(c =>
                    {
                        c.Field("Plate", visit.TrailerPlate is null ? visit.TruckPlate : $"{visit.TruckPlate} / {visit.TrailerPlate}");
                        c.Field("Truck category", visit.TruckCategoryCode);
                    });
                    truck.RelativeItem().Column(c =>
                    {
                        c.Field("Driver", visit.DriverName);
                        c.Field("Haulier", Party(visit.HaulierPartyCode));
                    });
                    truck.RelativeItem().Column(c =>
                    {
                        c.Field("Lane", visit.LaneCode);
                        c.Field("Boxes", moves.Count(m => m.Status != "VOIDED").ToString());
                    });
                });

                body.Item().Table(table =>
                {
                    table.ColumnsDefinition(cols =>
                    {
                        cols.ConstantColumn(18);   // #
                        cols.RelativeColumn(2.2f); // container
                        cols.RelativeColumn(1.4f); // type / height
                        cols.RelativeColumn(1.8f); // movement
                        cols.RelativeColumn(1.8f); // order
                        cols.RelativeColumn(2.6f); // customer / line
                        cols.RelativeColumn(2.2f); // seals
                        cols.RelativeColumn(1.1f); // gross
                        cols.RelativeColumn(1.1f); // tare
                        cols.RelativeColumn(1.1f); // cargo
                        cols.RelativeColumn(2f);   // EIR
                    });
                    table.Header(h =>
                    {
                        foreach (var title in new[] { "#", "Container", "Type", "Movement", "Order", "Customer / line", "Seals", "Gross kg", "Tare kg", "Cargo kg", "EIR" })
                            h.Cell().BorderBottom(1).PaddingBottom(2).Text(title).FontSize(7).Bold();
                    });
                    foreach (var m in moves)
                    {
                        var voided = m.Status == "VOIDED";
                        void Cell(string? text) => table.Cell().PaddingVertical(2).Text(t =>
                        {
                            var span = t.Span(text ?? "—").FontSize(8);
                            if (voided) span.Strikethrough().FontColor(Colors.Grey.Darken1);
                        });
                        Cell(m.PositionNo.ToString());
                        Cell(m.ContainerNo);
                        Cell(m.HeightCode is null or "STANDARD" ? m.EquipmentTypeCode : $"{m.EquipmentTypeCode} {m.HeightCode}");
                        Cell($"{(m.Direction == "IN" ? "IN" : "OUT")} {m.MovementCode} ({m.FullEmpty})");
                        Cell(m.OrderNo);
                        Cell($"{Party(m.CustomerPartyCode)} / {m.LinePartyCode}");
                        Cell(seals[m.GateTransactionId].Any() ? string.Join(", ", seals[m.GateTransactionId]) : null);
                        Cell(Kg(m.GrossWeightKg));
                        Cell(Kg(m.TareWeightKg));
                        Cell(Kg(m.CargoWeightKg));
                        Cell(voided ? $"{m.EirNo} VOID" : m.EirNo);
                    }
                });

                if (moves.Count == 0) body.Item().Text("No box has been recorded on this truck.").Italic();
                if (!string.IsNullOrWhiteSpace(visit.Remarks)) body.Item().Text(visit.Remarks).Italic();

                body.Item().PaddingTop(24).Row(sign =>
                {
                    foreach (var who in new[] { "Driver / ผู้ขับรถ", "Gate clerk / เจ้าหน้าที่", "Yard / ลานตู้" })
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

            page.Footer().Row(f =>
            {
                f.RelativeItem().Text($"Printed {Local(DateTimeOffset.UtcNow)} · GECKO").FontSize(7).FontColor(Colors.Grey.Darken1);
                f.RelativeItem().AlignRight().Text(t => { t.Span("Page ").FontSize(7); t.CurrentPageNumber().FontSize(7); });
            });
        })).GeneratePdf();

        return new Rendered($"{visit.VisitNo}-truck-in.pdf", pdf);
    }
}
