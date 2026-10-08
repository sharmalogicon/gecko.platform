using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Gecko.SharedKernel;
using Gecko.Tos.Application;
using Gecko.Tos.Endpoints.Gate;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// The truck-in form as KORAKIT's TMS_TruckInForm(KPS).rdl prints it: the RDL's expressions on Gecko's
/// values, one sheet per box, and a reefer truck through the real gate printing its form.
/// </summary>
/// <remarks>No host: the RDL's expressions and the layout, on values alone.</remarks>
public sealed class TruckInFormTests
{

    internal static int PageCount(byte[] pdf) => Regex.Matches(Encoding.Latin1.GetString(pdf), @"/Type\s*/Page\b(?!s)").Count;

    [Fact]
    public void The_values_print_as_the_RDL_expressions_print_them()
    {
        // =Fields!Size.Value + " " + Fields!Type.Value
        Assert.Equal("20 GP", TruckInDocument.SizeType("20GP", "20"));
        Assert.Equal("40 HC", TruckInDocument.SizeType("40HC", "40"));
        Assert.Equal("45 RH", TruckInDocument.SizeType("45RH", null));
        // =IIF(Temperature<>0, Temperature.tostring() + " " + mode, ""): numeric(5,2), Celsius
        Assert.Equal("-18.00 CEL", TruckInDocument.SetTemp(-18m));
        Assert.Equal("5.50 CEL", TruckInDocument.SetTemp(5.5m));
        Assert.Equal("", TruckInDocument.SetTemp(0m));
        Assert.Equal("", TruckInDocument.SetTemp(null));
        Assert.Equal("", TruckInDocument.Vent(null));
        Assert.Equal("25.00 ", TruckInDocument.Vent(25m));
        // =format(DateTimeIn,"HH:mm") + " Hrs"
        Assert.Equal("07:05 Hrs", TruckInDocument.TimeOfDay(new DateTimeOffset(2026, 10, 8, 7, 5, 0, TimeSpan.FromHours(7))));
        // =Fields!Remarks.Value + " " + Fields!ContainerRemarks.Value
        Assert.Equal("keep dry handle with care", TruckInDocument.Remarks("keep dry", "handle with care"));
        Assert.Equal("box only", TruckInDocument.Remarks(null, "box only"));
    }

    [Fact]
    public void Each_box_is_its_own_sheet_and_a_voided_one_still_prints()
    {
        var header = new TruckInDocument.Header("KORAKRIT LOGISTICS CO.,LTD.", "Gate Clerk", "08-10-2026 07:05");
        TruckInDocument.Sheet Box(string no, bool voided) => new(
            "MAEU", "บริษัท ทดสอบ จำกัด", "FROZEN FISH", "CHENG-BK-88421", "20 RF", "-18.00 CEL", "", "YES",
            no, "SEAL-001", "Haulier Co.", "70-4321", "07:05 Hrs", "JPTYO", "keep frozen", voided);

        var pdf = TruckInDocument.Render(header, [Box("ZZTU1234565", false), Box("ZZTU7654321", true)]);

        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf[..4]));
        Assert.Equal(2, PageCount(pdf));
        if (Environment.GetEnvironmentVariable("GECKO_SAMPLE_PDF_DIR") is { Length: > 0 } dir)
            File.WriteAllBytes(Path.Combine(dir, "truck-in-sample.pdf"), pdf);
    }
}

/// <summary>A reefer truck through the real gate prints its truck-in form, one sheet for its one box.</summary>
[Collection(TosApiCollection.Name)]
public sealed class TruckInFormApiTests(TosApiFactory api)
{
    private const string Prefix = "ZZK-";
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    [Fact]
    public async Task A_reefer_truck_prints_its_truck_in_form()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = $"{Prefix}{Guid.NewGuid():N}"[..16].ToUpperInvariant();
        var ten = $"ZZTU{Random.Shared.Next(100000, 999999)}";
        var box = ten + ContainerNumber.CheckDigitOf(ten);
        try
        {
            var booked = await client.PostAsJsonAsync("/api/tos/bookings", new
            {
                branchId = SctLcb01, orderTypeCode = "IMP CY/CY", lineCode = "MAEU", customerCode = "CUS-TAE", carrierRef,
                validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
                requirements = new object[] { new { equipmentTypeCode = "20RF", qty = 1, reeferSetTempC = -18.0m } },
                containers = new object[] { new { containerNo = box } },
            }, ct);
            Assert.True(booked.StatusCode == HttpStatusCode.Created, await booked.Content.ReadAsStringAsync(ct));

            var gated = await client.PostAsJsonAsync("/api/tos/gate/transactions", new
            {
                branchId = SctLcb01, containerNo = box, direction = "IN",
                tripType = "DROP_OFF_CONT", tareWeightKg = 2200m, maxGrossWeightKg = 30480m, cargoWeightKg = 18000m, customsPermitNo = "ZZ-PERMIT-1",
                truck = new { plate = "70-4321", driverName = "Somchai P." },
                grossWeightKg = 24100m, weightSource = "WEIGHBRIDGE",
                seals = new object[] { new { sealNo = $"ZZ-{box[^4..]}", sealType = "LINE", isIntact = true } },
            }, ct);
            Assert.True(gated.StatusCode == HttpStatusCode.Created, await gated.Content.ReadAsStringAsync(ct));
            var eir = (await gated.Content.ReadFromJsonAsync<GateTransactionResponse>(ct))!;

            var form = await client.GetAsync($"/api/tos/gate/visits/{eir.TruckVisitId}/truck-in.pdf", ct);
            Assert.True(form.StatusCode == HttpStatusCode.OK, await form.Content.ReadAsStringAsync(ct));
            Assert.Equal("application/pdf", form.Content.Headers.ContentType?.MediaType);
            var pdf = await form.Content.ReadAsByteArrayAsync(ct);
            Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf[..4]));
            Assert.Equal(1, TruckInFormTests.PageCount(pdf));   // one box on the truck, one sheet
            if (Environment.GetEnvironmentVariable("GECKO_SAMPLE_PDF_DIR") is { Length: > 0 } dir)
                File.WriteAllBytes(Path.Combine(dir, "truck-in-reefer.pdf"), pdf);
        }
        finally { await TestDatabase.RemoveGateAsync(carrierRef); await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }
}
