using System.Globalization;
using Gecko.Data.Documents;
using Gecko.Identity.Contracts;
using Gecko.MasterData.Contracts;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Gecko.Tos.Application;

/// <summary>
/// The printed EIR, laid out as KORAKIT's Vector report TMS_EIRForm.rdl (owner 2026-10-08: exactly the RDL's).
/// The RDL is an overlay for pre-printed EIR stationery: it prints no labels, lines or logo — only the move's
/// values, each at its own position on a 102.3mm block, on a Letter page with 0.2in margins and an empty
/// 0.52in header. Data is Report.usp_EIR's, read from Gecko's move (gecko_tos), its booking, truck visit,
/// seals and survey; names are MDM's and Identity's at print time; times the branch's clock.
///
/// Gecko addition the RDL has no rule against: a VOIDED EIR keeps its number and still prints, under the
/// VOID watermark — in Thailand a numbered document never just disappears (Q4).
/// </summary>
internal sealed class EirDocument(TosDbContext db, IMasterDataReferences master, IUserDirectory users, BranchClock clock)
{
    public sealed record Rendered(string FileName, byte[] Pdf);

    /// <summary>One EIR block, every value as Report.usp_EIR returns it and the RDL prints it.</summary>
    internal sealed record Sheet(
        string ContainerNo, string SizeType, string MovementCode,
        string AgentName, string BookingBlNo, string ShipperName, string Wharf,
        string VesselName, string VoyageNo, string TruckName, string TruckNo,
        string SealNo1, string NextPrevLoc, string Temperature, string CustomPermitNo, string SetTemp,
        string ContainerStatus, string Remarks, IReadOnlyList<string> Damages,
        string CreatedOnDate, string CreatedOnTime, string CreatedBy, bool IsVoided);

    public async Task<Rendered?> RenderAsync(Guid gateTransactionId, CancellationToken ct)
    {
        var row = await (
            from t in db.GateTransactions.AsNoTracking().Where(x => x.GateTransactionId == gateTransactionId)
            join b in db.Bookings on t.BookingId equals b.BookingId
            join v in db.TruckVisits on t.TruckVisitId equals v.TruckVisitId
            join bc in db.BookingContainers on t.BookingContainerId equals bc.BookingContainerId
            join r in db.EquipmentRequirements on bc.EquipmentRequirementId equals r.EquipmentRequirementId
            join vc in db.VesselCalls on b.VesselCallId equals vc.VesselCallId into calls
            from vc in calls.DefaultIfEmpty()
            select new { g = t, b, v.TruckPlate, Haulier = v.HaulierPartyCode ?? b.HaulierPartyCode, SetTemp = bc.ReeferSetTempC ?? r.ReeferSetTempC, Call = vc })
            .SingleOrDefaultAsync(ct);
        if (row is null) return null;
        var g = row.g;

        var seals = await db.GateTransactionSeals.AsNoTracking()
            .Where(s => s.GateTransactionId == g.GateTransactionId && s.DeletedAt == null)
            .OrderBy(s => s.CreatedAt).Select(s => new { s.SealType, s.SealNo }).ToListAsync(ct);
        var survey = await db.Surveys.AsNoTracking().Where(s => s.GateTransactionId == g.GateTransactionId && s.DeletedAt == null)
            .OrderByDescending(s => s.SurveyedAt).FirstOrDefaultAsync(ct);
        var damages = survey is null ? [] : await db.SurveyDamages.AsNoTracking()
            .Where(d => d.SurveyId == survey.SurveyId && d.DeletedAt == null).OrderBy(d => d.LineNo).ToListAsync(ct);
        var codes = damages.Count == 0 ? null : await master.SurveyCodesAsync(ct);

        var parties = await master.PartiesAsync(new[] { g.LinePartyCode, row.b.CustomerPartyCode, row.Haulier }.OfType<string>(), ct);
        string Name(string? code) => code is not null && parties.TryGetValue(code, out var p) ? p.Name : code ?? "";
        var vessel = row.Call?.VesselCode is { } vesselCode ? (await master.VesselsAsync([vesselCode], ct)).GetValueOrDefault(vesselCode) : null;
        var createdBy = g.CreatedBy is { } by ? (await users.DisplayNamesAsync([by], ct)).GetValueOrDefault(by) : null;
        var branch = (await clock.BranchesAsync([g.BranchId], ct)).GetValueOrDefault(g.BranchId);
        var at = branch is null ? g.TransactionAt : TimeZoneInfo.ConvertTime(g.TransactionAt, branch.Zone);

        var bookingType = row.b.BookingTypeCode;
        var movement = Movement(g.MovementCode);
        var sheet = new Sheet(
            ContainerNo: g.ContainerNo,
            SizeType: g.EquipmentTypeCode ?? "",
            MovementCode: $"{bookingType} {movement}",
            AgentName: Name(g.LinePartyCode),
            BookingBlNo: row.b.CarrierRef ?? row.b.CustomerRef ?? "",
            ShipperName: Name(row.b.CustomerPartyCode),
            Wharf: Wharf(bookingType, movement, row.Call?.TerminalCode),
            VesselName: vessel?.VesselName ?? "",
            VoyageNo: (row.b.DirectionCode == "EXPORT" ? row.Call?.OperatorVoyageOut ?? row.Call?.OperatorVoyageIn : row.Call?.OperatorVoyageIn ?? row.Call?.OperatorVoyageOut) ?? "",
            TruckName: Name(row.Haulier),
            TruckNo: row.TruckPlate,
            SealNo1: seals.FirstOrDefault(s => s.SealType is "LINE" or "AGENT")?.SealNo ?? "",
            NextPrevLoc: g.NextLocationCode ?? row.b.NextPrevLocation ?? "",
            Temperature: Number(g.TempObservedC),
            CustomPermitNo: bookingType == "IMPORT" ? g.CustomsPermitNo ?? "" : "",
            SetTemp: Number(row.SetTemp),
            ContainerStatus: g.ConditionCode ?? "",
            Remarks: g.Remarks ?? "",
            Damages: damages.Select(d => Damage(d.ComponentCode, d.DamageLocationCode, d.DamageCode,
                codes?.DamageCodes.GetValueOrDefault(d.DamageCode)?.DescriptionEn)).ToList(),
            CreatedOnDate: at.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            CreatedOnTime: at.ToString("H:mm", CultureInfo.InvariantCulture),
            CreatedBy: createdBy ?? "",
            IsVoided: g.Status == "VOIDED");

        return new Rendered($"{g.EirNo}.pdf", Render([sheet]));
    }

    // ── the RDL's expressions ───────────────────────────────────────────────

    /// <summary>Vector's movement codes read "FULL OUT"; Gecko's are the same words joined ("FULL_OUT").</summary>
    internal static string Movement(string code) => code.Replace('_', ' ');

    /// <summary><c>=IIF(LookupCode="EXPORT" And MovementCode="FULL OUT", "WHARF : " + Terminal, "")</c>.</summary>
    internal static string Wharf(string bookingType, string movement, string? terminal) =>
        bookingType == "EXPORT" && movement == "FULL OUT" ? "WHARF : " + (terminal ?? "") : "";

    /// <summary>Vector's numeric(5,2) as SSRS prints it unformatted: two decimals. Nothing recorded prints nothing.</summary>
    internal static string Number(decimal? value) => value?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";

    /// <summary>
    /// One line of the RDL's damage list (Tablix1, =Fields!LookupDescription.Value, a row per damage of the move):
    /// Vector named the damaged part; Gecko records part, location and damage — printed in that order, the damage
    /// by its MDM description.
    /// </summary>
    internal static string Damage(string? component, string? location, string damageCode, string? description) =>
        string.Join(" ", new[] { component, location, description ?? damageCode }.Where(s => !string.IsNullOrWhiteSpace(s)));

    // ── the layout, in the RDL's own coordinates ────────────────────────────

    private const string Black = "#000000";
    private const float Mm = 72f / 25.4f;
    private const float Inch = 72f;

    /// <summary>Letter portrait, 0.2in margins, an empty 0.52083in page header; Tablix3 a 102.3181mm block per move.</summary>
    internal static byte[] Render(IReadOnlyList<Sheet> sheets)
    {
        GeckoPdf.EnsureInitialised();
        return Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.Letter);
            page.Margin(0.2f, Unit.Inch);
            page.DefaultTextStyle(t => t.FontSize(8).FontFamily(GeckoPdf.LatinFont, GeckoPdf.ThaiFont).FontColor(Black));
            page.Header().Height(0.52083f, Unit.Inch);
            page.Content().Column(column =>
            {
                foreach (var sheet in sheets)
                    column.Item().Width(192.15668f * Mm).Height(102.3181f * Mm).Layers(layers =>
                    {
                        layers.PrimaryLayer();
                        if (sheet.IsVoided) layers.Layer().VoidWatermark();
                        Block(layers, sheet);
                    });
            });
        })).GeneratePdf();
    }

    /// <summary>Rectangle1 (top 0.21448mm, left 2.60485mm) and Rectangle2 inside it (top 6.89958mm): every Textbox at its RDL place.</summary>
    private static void Block(LayersDescriptor layers, Sheet s)
    {
        const float r1Top = 0.21448f, r1Left = 2.60485f;
        void InR1(float top, float left, float width, string text, float size = 8, bool bold = false, bool center = false) =>
            Box(layers, (r1Top + top) * Mm, (r1Left + left) * Mm, width * Mm, text, size, bold, center);
        const float r2Top = r1Top + 6.89958f, r2Left = r1Left;
        void InR2(float top, float left, float width, string text, bool center = false) =>
            Box(layers, (r2Top + top) * Mm, (r2Left + left) * Mm, width * Mm, text, 8, false, center);

        // The top line: container, size/type, booking type + movement.
        InR1(0.3175f, 43.60903f, 30.02708f, s.ContainerNo, size: 9, bold: true);
        InR1(0.3175f, 103.75764f, 14.94583f, s.SizeType);
        InR1(0.3175f, 149.61249f, 37.2935f, s.MovementCode);

        // Rectangle2: the parties, the ship, the truck, the seal and the reefer.
        InR2(0.79375f, 30.34458f, 111.57735f, s.AgentName);
        InR2(6.79375f, 48.28333f, 37.71547f, s.BookingBlNo);
        InR2(13.09639f, 143.52707f, 43.37892f, s.Wharf, center: true);
        InR2(13.46403f, 47.93056f, 73.68333f, s.ShipperName);
        InR2(20.73403f, 28.57854f, 71.82771f, s.VesselName);
        InR2(20.73403f, 115.21096f, 18.45575f, s.VoyageNo);
        InR2(27.05153f, 28.57854f, 74.47354f, s.TruckName);
        InR2(27.40431f, 127.47568f, 22.09584f, s.TruckNo);
        InR2(34.445f, 28.57854f, 25.56445f, s.SealNo1);
        InR2(34.445f, 149.61249f, 22.08958f, s.NextPrevLoc);
        Box(layers, (r2Top + 0) * Mm + 1.3561f * Inch, r2Left * Mm + 2.85545f * Inch, 1.46883f * Inch, s.CustomPermitNo, 8, false, false);
        InR2(41.59153f, 36.72986f, 15.73958f, s.Temperature);
        Box(layers, r2Top * Mm + 1.6458f * Inch, r2Left * Mm + 3.28856f * Inch, 0.53107f * Inch, s.SetTemp, 8, false, false);

        // Condition, remarks, the damage list, then when and by whom.
        InR1(59.26944f, 48.63611f, 37.96242f, s.ContainerStatus);
        InR1(69.62069f, 39.11111f, 116.14582f, s.Remarks);
        layers.Layer().PaddingTop(r1Top * Mm + 2.98406f * Inch).PaddingLeft(r1Left * Mm + 1.54203f * Inch).AlignTop().AlignLeft()
            .Width(4.575f * Inch).Column(c =>
            {
                foreach (var damage in s.Damages) c.Item().MinHeight(0.1875f * Inch).PaddingLeft(2).PaddingTop(2).Text(damage).FontSize(8);
            });
        InR1(90.73584f, 35.10708f, 36.7418f, s.CreatedOnDate);
        InR1(96.73862f, 35.10708f, 36.7418f, s.CreatedOnTime);
        InR1(96.73584f, 156.7625f, 25f, s.CreatedBy);
    }

    /// <summary>An RDL Textbox: no border, 2pt left and top padding, CanGrow (wraps downward inside its width).</summary>
    private static void Box(LayersDescriptor layers, float top, float left, float width, string text, float size, bool bold, bool center)
    {
        if (string.IsNullOrEmpty(text)) return;
        var cell = layers.Layer().PaddingTop(top).PaddingLeft(left).AlignTop().AlignLeft().Width(width).PaddingLeft(2).PaddingTop(2);
        (center ? cell.AlignCenter() : cell.AlignLeft()).Text(t =>
        {
            var span = t.Span(text).FontSize(size);
            if (bold) span.Bold();
        });
    }
}
