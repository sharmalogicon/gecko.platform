using System.Text;
using Gecko.Tos.Application;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// The EIR as KORAKIT's TMS_EIRForm.rdl prints it: the RDL's expressions on Gecko's values, and the overlay
/// block rendering for a damaged reefer move (and still printing, marked, once voided).
/// </summary>
/// <remarks>No host: the RDL's expressions and the layout, on values alone. The real gate's eir.pdf is GateApiTests'.</remarks>
public sealed class EirFormTests
{
    [Fact]
    public void The_values_print_as_the_RDL_expressions_print_them()
    {
        // Vector's movement codes read with a space; =LookupCode + " " + MovementCode on the top line.
        Assert.Equal("FULL OUT", EirDocument.Movement("FULL_OUT"));
        Assert.Equal("MTY IN", EirDocument.Movement("MTY_IN"));
        // =IIF(LookupCode="EXPORT" And MovementCode="FULL OUT", "WHARF : " + Terminal, "")
        Assert.Equal("WHARF : LCB-B5", EirDocument.Wharf("EXPORT", "FULL OUT", "LCB-B5"));
        Assert.Equal("", EirDocument.Wharf("IMPORT", "FULL OUT", "LCB-B5"));
        Assert.Equal("", EirDocument.Wharf("EXPORT", "MTY OUT", "LCB-B5"));
        // numeric(5,2), unformatted: two decimals; nothing recorded prints nothing (never a made-up 0.00).
        Assert.Equal("-18.00", EirDocument.Number(-18m));
        Assert.Equal("4.50", EirDocument.Number(4.5m));
        Assert.Equal("", EirDocument.Number(null));
        // The damage list: part, location, the damage by its MDM description.
        Assert.Equal("DOOR PANEL LDR Dent", EirDocument.Damage("DOOR PANEL", "LDR", "DT", "Dent"));
        Assert.Equal("DT", EirDocument.Damage(null, null, "DT", null));
    }

    [Fact]
    public void A_damaged_reefer_move_prints_its_block_and_still_prints_when_voided()
    {
        EirDocument.Sheet Move(bool voided) => new(
            "ZZTU1234565", "20RF", "EXPORT FULL OUT", "Maersk Line", "CHENG-BK-88421", "บริษัท ทดสอบ จำกัด", "WHARF : LCB-B5",
            "MAERSK ESSEN", "641W", "Haulier Co.", "70-4321", "ML-123456", "THLCH", "-17.80", "", "-18.00",
            "DM", "door hard to close", ["DOOR PANEL LDR Dent", "CORNER POST RFR Hole"], "08/10/2026", "7:05", "Gate Clerk", voided);

        var pdf = EirDocument.Render([Move(false)]);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf[..4]));
        Assert.Equal(1, TruckInFormTests.PageCount(pdf));

        var voided = EirDocument.Render([Move(true)]);
        Assert.Equal(1, TruckInFormTests.PageCount(voided));
        Assert.NotEqual(pdf.Length, voided.Length);

        if (Environment.GetEnvironmentVariable("GECKO_SAMPLE_PDF_DIR") is { Length: > 0 } dir)
            File.WriteAllBytes(Path.Combine(dir, "eir-sample.pdf"), pdf);
    }
}
