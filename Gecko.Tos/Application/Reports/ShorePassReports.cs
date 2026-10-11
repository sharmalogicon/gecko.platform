using Gecko.Data.Documents;
using Gecko.MasterData.Contracts;
using Gecko.Tos.Domain;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static Gecko.Tos.Application.Reports.GateInOutReport;
using static Gecko.Tos.Application.Reports.OperationReportKit;

namespace Gecko.Tos.Application.Reports;

/// <summary>
/// Vector's Shore Pass #1 / #2 / #3 (TMS.Operation.OutBound.ShorePass1-3, one proc Report.usp_OutBound_ShorePass): the full
/// gate-ins on the depot days asked for (the To day included; Vector's proc lost it at midnight), any booking type, in gate
/// order, laid out on one terminal's request form each — #1 Evergreen B.2, #2 LCMT, #3 Kerry Siam Seaport — with the forms'
/// own texts kept word for word (the depot ticks the boxes by hand). The form's vessel, voyage, ETA and agent are the first
/// row's, as the RDLs took First().
/// </summary>
internal static class ShorePassReports
{
    private sealed class Row
    {
        public Guid GateTransactionId { get; init; }
        public DateTimeOffset At { get; init; }
        public string? EirNo { get; init; }
        public string ContainerNo { get; init; } = "";
        public string? EquipmentTypeCode { get; init; }
        public string LineCode { get; init; } = "";
        public string? CustomerCode { get; init; }
        public string? CarrierRef { get; init; }
        public string? CustomerRef { get; init; }
        public string? DirectionCode { get; init; }
        public string? CommodityCode { get; init; }
        public string? Remarks { get; init; }
        public string? PodPortCode { get; init; }
        public string? FpdPortCode { get; init; }
        public string? Imdg { get; init; }
        public string? UnNumber { get; init; }
        public decimal? TempC { get; init; }
        public decimal? VentPct { get; init; }
        public string? VesselCode { get; init; }
        public string? VoyageIn { get; init; }
        public string? VoyageOut { get; init; }
        public DateTimeOffset? Eta { get; init; }
    }

    private sealed record Data(List<Row> Rows, IReadOnlyDictionary<string, EquipmentTypeRef> Types, IReadOnlyDictionary<string, PartyRef> Parties,
        IReadOnlyDictionary<string, VesselRef> Vessels, IReadOnlyDictionary<string, PortRef> Ports, IReadOnlyDictionary<string, CommodityRef> Commodities,
        ILookup<Guid, string> AgentSeals)
    {
        public (string? Size, string? Type) SizeType(Row r) => GateInOutReport.SizeType(Types, r.EquipmentTypeCode);
        public string? Party(string? code) => code is null ? null : Parties.GetValueOrDefault(code)?.Name;
        public string? Port(string? code) => code is null ? null : Ports.GetValueOrDefault(code)?.Name ?? code;
        public string? Vessel(Row? r) => r?.VesselCode is { } v ? Vessels.GetValueOrDefault(v)?.VesselName ?? v : null;
        public string? Commodity(Row r) => r.CommodityCode is { } c ? Commodities.GetValueOrDefault(c)?.Description : null;
        public string? Seal(Row r) => AgentSeals[r.GateTransactionId].FirstOrDefault();
        public static string? Voyage(Row? r) => r is null ? null : GateInOutReport.Voyage(r.DirectionCode ?? "", r.VoyageIn, r.VoyageOut);
        public static string? BookingNo(Row? r) => r is null ? null : BookingBlNo(r.CarrierRef, r.CustomerRef);
    }

    private static async Task<Data> DataAsync(TosDbContext db, IMasterDataReferences master, BranchClock.Branch branch,
        DateOnly from, DateOnly to, string? agentCode, CancellationToken ct)
    {
        var start = BranchClock.StartOf(branch, from);
        var end = BranchClock.StartOf(branch, to.AddDays(1));
        var query =
            from g in db.GateTransactions.AsNoTracking()
            where g.BranchId == branch.BranchId && g.Status == "COMPLETED" && g.DeletedAt == null && g.TransactionAt >= start && g.TransactionAt < end
                  && g.Direction == GateRules.In && g.FullEmpty == GateRules.Full
            join b in db.Bookings on g.BookingId equals b.BookingId
            join x in db.BookingContainers on g.BookingContainerId equals x.BookingContainerId
            join r in db.EquipmentRequirements on x.EquipmentRequirementId equals r.EquipmentRequirementId
            join c in db.VesselCalls on b.VesselCallId equals c.VesselCallId into calls
            from c in calls.DefaultIfEmpty()
            select new Row
            {
                GateTransactionId = g.GateTransactionId, At = g.TransactionAt, EirNo = g.EirNo, ContainerNo = g.ContainerNo,
                EquipmentTypeCode = g.EquipmentTypeCode, LineCode = b.LinePartyCode, CustomerCode = b.CustomerPartyCode,
                CarrierRef = b.CarrierRef, CustomerRef = b.CustomerRef, DirectionCode = b.DirectionCode, CommodityCode = b.CommodityCode,
                Remarks = b.Remarks, PodPortCode = b.PodPortCode, FpdPortCode = b.FpdPortCode,
                Imdg = x.ImdgClass ?? r.ImdgClass, UnNumber = x.UnNumber ?? r.UnNumber,
                TempC = x.ReeferSetTempC ?? r.ReeferSetTempC, VentPct = x.ReeferVentPct ?? r.ReeferVentPct,
                VesselCode = c == null ? null : c.VesselCode, VoyageIn = c == null ? null : c.OperatorVoyageIn,
                VoyageOut = c == null ? null : c.OperatorVoyageOut, Eta = c == null ? null : c.Eta,
            };
        if (Clean(agentCode) is { } agent) query = query.Where(r => r.LineCode == agent);
        var rows = (await query.ToListAsync(ct)).OrderBy(r => r.At).ThenBy(r => r.EirNo, StringComparer.Ordinal).ToList();

        var seals = new List<(Guid Move, DateTimeOffset At, string SealNo, string SealType)>();
        foreach (var slice in rows.Select(r => r.GateTransactionId).Chunk(2000))
            seals.AddRange((await db.GateTransactionSeals.AsNoTracking().Where(s => slice.Contains(s.GateTransactionId) && s.DeletedAt == null)
                    .Select(s => new { s.GateTransactionId, s.CreatedAt, s.SealNo, s.SealType }).ToListAsync(ct))
                .Select(s => (s.GateTransactionId, s.CreatedAt, s.SealNo, s.SealType)));
        return new Data(rows,
            await TypesAsync(master, rows.Select(r => r.EquipmentTypeCode), ct),
            await master.PartiesAsync(rows.Select(r => r.LineCode).Concat(rows.Select(r => r.CustomerCode).OfType<string>()).Distinct(), ct),
            await master.VesselsAsync(rows.Select(r => r.VesselCode).OfType<string>().Distinct(), ct),
            await master.PortsAsync(rows.SelectMany(r => new[] { r.PodPortCode, r.FpdPortCode }).OfType<string>().Distinct(), ct),
            await master.CommoditiesAsync(rows.Select(r => r.CommodityCode).OfType<string>().Distinct(), ct),
            seals.Where(s => IsAgentSeal(s.SealType)).OrderBy(s => s.At).ToLookup(s => s.Move, s => s.SealNo));
    }

    private static string Eta(BranchClock.Branch branch, Row? r) => r?.Eta is { } eta ? Stamp(Local(branch, eta), "dd/MM/yyyy HH:mm") : "";

    /// <summary>Shore Pass #1 — the Evergreen Container Terminal B.2 request form.</summary>
    public static async Task<TabularReport> EvergreenAsync(
        TosDbContext db, IMasterDataReferences master, ReportHeading h, DateOnly from, DateOnly to, string? agentCode, CancellationToken ct)
    {
        var d = await DataAsync(db, master, h.Branch, from, to, agentCode, ct);
        var first = d.Rows.FirstOrDefault();
        var rows = d.Rows.Select((r, i) =>
        {
            var (size, type) = d.SizeType(r);
            return new TabularRow([i + 1, r.ContainerNo, size, type, d.Commodity(r), d.Seal(r), r.FpdPortCode, r.Remarks, r.Imdg, r.UnNumber,
                r.TempC, r.VentPct, Data.BookingNo(r)]);
        }).ToList();
        return new TabularReport(
            FileName: $"ShorePass1_{from:yyyyMMdd}-{to:yyyyMMdd}",
            Page: ReportPage.A4Landscape,
            MarginCm: 0.5,
            Heading:
            [
                new("บริษัท เอเวอร์กรีน คอนเทนเนอร์ เทอร์มินัล (ประเทศไทย) จำกัด", 10, Bold: true),
                new("คำร้องขอนำตู้คอนเทนเนอร์  ออกนอก - เข้าเขต บริษัท เอเวอร์กรีนฯ ท่า B.2", 10, Bold: true),
            ],
            HeadingRight: [],
            Preamble:
            [
                new($"เอเย่นต์ {d.Party(first?.LineCode)}          วันที่ {Stamp(h.PrintedAt, "dd/MM/yyyy HH:mm")}", 10),
                new("IN", 26, Bold: true),
                new($"บรรทุกลงเรือ {d.Vessel(first)} {Data.Voyage(first)}          เที่ยวเรือ {Eta(h.Branch, first)}", 10),
                new("ออกใบเสร็จในนาม   S.C.T          รหัสลูกค้า", 10),
            ],
            PreambleRight: [],
            Columns:
            [
                new(1.28292), new(2.33243), new(1.17486), new(1.23882), new(2.5), new(1.80674), new(1.64167), new(2.5), new(1.60042),
                new(1.33583), new(1.09771, "0.00"), new(1.12604, "0.00"), new(2.5),
            ],
            HeaderRows:
            [
                [new("ลำดับ"), new("หมายเลขตู้"), new("ขนาด"), new("ประเภท"), new("สินค้า"), new("Seal No1"), new("เมืองท่า   ปลายทาง"),
                 new("Remarks"), new("IMO"), new("UN No."), new("Temp"), new("Vent"), new("Booking BLNo")],
            ],
            Rows: rows,
            PageLabel: null);
    }

    /// <summary>Shore Pass #2 — the LCMT request form. The 20'/40'/45' quantities are left for the depot to fill, as on Vector.</summary>
    public static async Task<TabularReport> LcmtAsync(
        TosDbContext db, IMasterDataReferences master, ReportHeading h, DateOnly from, DateOnly to, string? agentCode, CancellationToken ct)
    {
        var d = await DataAsync(db, master, h.Branch, from, to, agentCode, ct);
        var first = d.Rows.FirstOrDefault();
        var rows = d.Rows.Select((r, i) =>
        {
            var (size, type) = d.SizeType(r);
            return new TabularRow([i + 1, r.ContainerNo, Data.BookingNo(r), size, type, d.Port(r.FpdPortCode), r.TempC, r.Imdg, null, "CREDIT"]);
        }).ToList();
        return new TabularReport(
            FileName: $"ShorePass2_{from:yyyyMMdd}-{to:yyyyMMdd}",
            Page: ReportPage.A4Landscape,
            MarginCm: 0.5,
            Heading:
            [
                new("บริษัท แอล ซี เอ็ม ที จำกัด", 10, Bold: true), new("LCMT  Company  Limited", 10, Bold: true),
                new("คำร้องขอนำตู้คอนเทนเนอร์ ออกนอก - เข้าใน เขต บริษัทแอล ซี เอ็ม ที จำกัด", 10, Bold: true),
            ],
            HeadingRight: [],
            Preamble:
            [
                new($"วันที่ {Stamp(h.PrintedAt, "dd-MM-yyyy HH:mm")}          บริษัท {d.Party(first?.LineCode)}          (  x ) รถยนต์    (     ) รถไฟ", 10),
                new("IN", 26, Bold: true),
                new($"บรรทุกลงเรือ {d.Vessel(first)}          เที่ยวเรือ {Data.Voyage(first)}          เที่ยวเรือ {Eta(h.Branch, first)}", 10),
                new("(   x  )  20'  จำนวน                    ตู้   ( x  )  40'  จำนวน                    45'  จำนวน", 10),
                new("รายละเอียดตู้สินค้า", 10),
            ],
            PreambleRight: [],
            Columns:
            [
                new(1.28292), new(2.78222), new(2.83294), new(1.17486), new(1.23882), new(3.76876), new(2.5, "0.00"), new(1.60042),
                new(1.99729), new(2.5),
            ],
            HeaderRows:
            [
                [new("No."), new("CONTAINER NO."), new("BOOKING NO."), new("SIZE"), new("TYPE"), new("PORT"), new("TEMP"), new("IMO"),
                 new("STOW CODE"), new("REMARK")],
            ],
            Rows: rows,
            PageLabel: null);
    }

    /// <summary>Shore Pass #3 — the Kerry Siam Seaport request form; its tick boxes print empty ("[ ]"), STATUS "E" as the RDL wrote it.</summary>
    public static async Task<TabularReport> KerryAsync(
        TosDbContext db, IMasterDataReferences master, ReportHeading h, DateOnly from, DateOnly to, string? agentCode, CancellationToken ct)
    {
        var d = await DataAsync(db, master, h.Branch, from, to, agentCode, ct);
        var first = d.Rows.FirstOrDefault();
        var rows = d.Rows.Select((r, i) =>
        {
            var (size, type) = d.SizeType(r);
            return new TabularRow([i + 1, r.ContainerNo, size, type, "E", d.Port(r.PodPortCode), d.Port(r.FpdPortCode), Data.BookingNo(r),
                d.Seal(r), r.Imdg, null, r.TempC, r.VentPct, d.Party(r.CustomerCode), d.Commodity(r), r.Remarks]);
        }).ToList();
        return new TabularReport(
            FileName: $"ShorePass3_{from:yyyyMMdd}-{to:yyyyMMdd}",
            Page: ReportPage.A3Landscape,
            MarginCm: 0.5,
            Heading: [new("KERRY SIAM SEAPORT LIMITED", 10, Bold: true)],
            HeadingRight: [],
            Preamble:
            [
                new($"EXPORT LOAD VESSEL   {d.Vessel(first)}          VOYAGE   {Data.Voyage(first)}"),
                new($"FULL CONTAINER    [ ] FULL IN    [ ] FULL OUT          BOOKING NO   {Data.BookingNo(first)}"),
                new("                  [ ] EMPTY IN   [ ] EMPTY OUT         DELIVERY TO"),
                new("EMPTY CONTAINER   [ ] CREDIT     [ ] BY TRUCK"),
                new("                  [ ] CASH       [ ] BY TRAIN"),
                new("20'   UNITS                40'   UNITS                45'   UNITS"),
            ],
            PreambleRight: [],
            Columns:
            [
                new(1.28292), new(2.14722), new(1.17486), new(1.23882), new(1.44167), new(2.94759), new(3.76876), new(2.5), new(2.5),
                new(1.71507), new(1.52104), new(1.25646, "0.00"), new(1.17708, "0.00"), new(4.11396), new(4.43146), new(3.74354),
            ],
            HeaderRows:
            [
                [new("No."), new("CONTAINER NO."), new("SIZE"), new("TYPE"), new("STATUS"), new("DISCHARGE PORT "), new("DESTINATION PORT"),
                 new("BOOKING NO."), new("SEAL NO."), new("IMO\n(CLASS)"), new("OOG\n(OVER)"), new("TEMP"), new("Vent \n(%)"), new("SHIPPER"),
                 new("COMMODITY"), new("REMARKS")],
            ],
            Rows: rows,
            PageLabel: null);
    }
}
