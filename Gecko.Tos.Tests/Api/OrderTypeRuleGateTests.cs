using System.Net;
using System.Net.Http.Json;
using Gecko.Data;
using Gecko.SharedKernel;
using Gecko.Tos.Endpoints.Bookings;
using Gecko.Tos.Endpoints.Gate;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// The order-type rules at the gate (WALK_IN_ORDERS_PLAN §2, gecko_master 34): R6 survey required, R12 seal must match,
/// R4 owner match per order type. Each test switches its rule on for an SCT fixture order type and off again; with the
/// rule off the gate behaves as before (the rest of the suite runs that way).
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class OrderTypeRuleGateTests(TosApiFactory api)
{
    private const string Gate = "/api/tos/gate";
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private static string NewTag() => $"ZZRULE-{Guid.NewGuid():N}"[..20];

    private static string NewBox()
    {
        var ten = $"ZZRU{Random.Shared.Next(100000, 999999)}";
        return ten + ContainerNumber.CheckDigitOf(ten);
    }

    private static async Task<HttpResponseMessage> SaveAsync(HttpClient client, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Gate}/trips") { Content = JsonContent.Create(body) };
        request.Headers.Add(Idempotency.Header, Guid.NewGuid().ToString());
        return await client.SendAsync(request, ct);
    }

    /// <summary>R6: the box comes in only with its survey; the Save records a clean one when the clerk found no damage.</summary>
    [Fact]
    public async Task A_step_that_requires_the_survey_refuses_an_unsurveyed_box_and_records_a_clean_survey()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var tag = NewTag();
        var box = NewBox();
        object Body(bool surveyed) => new
        {
            branchId = SctLcb01, draftId = Guid.NewGuid(), truck = new { plate = "70-6161" },
            rows = new[]
            {
                new
                {
                    surveyed,
                    walkIn = new { fullEmpty = "EMPTY", customerCode = "CUS-TAE", lineCode = "MAEU", equipmentTypeCode = "20GP", remarks = tag },
                    move = new { containerNo = box, direction = "IN", tripType = "DROP_OFF_CONT", tareWeightKg = 2200m, maxGrossWeightKg = 30480m },
                },
            },
        };
        await TestDatabase.SetSctStepRuleAsync("WALK MTY IN", "MTY_IN", "require_survey", true);
        try
        {
            var refused = await SaveAsync(client, Body(surveyed: false), ct);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var body = await refused.Content.ReadAsStringAsync(ct);
            Assert.Contains("SURVEY_REQUIRED", body);
            Assert.Contains("\"charged\":false", body);

            var saved = await SaveAsync(client, Body(surveyed: true), ct);
            Assert.True(saved.StatusCode == HttpStatusCode.Created, await saved.Content.ReadAsStringAsync(ct));
            var row = (await saved.Content.ReadFromJsonAsync<TripSaveResponse>(ct))!.Rows.Single();
            Assert.True(row.Status == "GATED", row.Reason);
            Assert.NotNull(row.SurveyId);
        }
        finally
        {
            await TestDatabase.SetSctStepRuleAsync("WALK MTY IN", "MTY_IN", "require_survey", false);
            await TestDatabase.RemoveWalkInAsync(tag);
            await TestDatabase.RemoveBoxReservationsAsync(box);
        }
    }

    /// <summary>R12: a seal the booking does not know refuses the box before anything is recorded; the booked seal passes.</summary>
    [Fact]
    public async Task A_step_that_matches_the_seal_refuses_a_seal_the_booking_does_not_know()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = $"ZZR-{Guid.NewGuid():N}"[..16].ToUpperInvariant();
        var box = NewBox();
        await TestDatabase.SetSctStepRuleAsync("GATE TEST", "FULL_IN", "seal_must_match", true);
        try
        {
            var booked = await client.PostAsJsonAsync("/api/tos/bookings", new
            {
                branchId = SctLcb01, orderTypeCode = "GATE TEST", lineCode = "MAEU", customerCode = "CUS-TAE", carrierRef,
                validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
                requirements = new object[] { new { equipmentTypeCode = "20GP", qty = 1 } },
                containers = new object[] { new { containerNo = box, declaredSealNo = "ZZS-BOOKED" } },
            }, ct);
            Assert.True(booked.StatusCode == HttpStatusCode.Created, await booked.Content.ReadAsStringAsync(ct));
            var place = (await booked.Content.ReadFromJsonAsync<BookingDetailResponse>(ct))!.Containers.Single().BookingContainerId;
            object Body(string seal) => new
            {
                branchId = SctLcb01, draftId = Guid.NewGuid(), truck = new { plate = "70-6262" },
                rows = new[]
                {
                    new
                    {
                        bookingContainerId = place,
                        move = new
                        {
                            containerNo = box, direction = "IN", tripType = "DROP_OFF_CONT", grossWeightKg = 22000m, tareWeightKg = 2200m,
                            maxGrossWeightKg = 30480m, cargoWeightKg = 19800m, seals = new object[] { new { sealNo = seal, sealType = "LINE", isIntact = true } },
                        },
                    },
                },
            };

            var refused = await SaveAsync(client, Body("ZZS-OTHER"), ct);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("SEAL_NOT_AS_BOOKED", await refused.Content.ReadAsStringAsync(ct));

            var saved = await SaveAsync(client, Body("zzs-booked"), ct);
            Assert.True(saved.StatusCode == HttpStatusCode.Created, await saved.Content.ReadAsStringAsync(ct));
            Assert.Equal("GATED", (await saved.Content.ReadFromJsonAsync<TripSaveResponse>(ct))!.Rows.Single().Status);
        }
        finally
        {
            await TestDatabase.SetSctStepRuleAsync("GATE TEST", "FULL_IN", "seal_must_match", false);
            await TestDatabase.RemoveGateAsync(carrierRef);
            await TestDatabase.RemoveBoxReservationsAsync(box);
            await TestDatabase.RemoveBookingsAsync(carrierRef);
        }
    }

    /// <summary>R4: SCT warns about a box of another owner (tenant setting off for the tests); the order type's REFUSE wins.</summary>
    [Fact]
    public async Task The_order_type_refuses_a_box_of_another_owner_over_the_tenant_setting()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = $"ZZR-{Guid.NewGuid():N}"[..16].ToUpperInvariant();
        const string oneyBox = "AKLU6019856";   // the fixture registry: a ONEY box, booked here on a MAEU order
        try
        {
            var booked = await client.PostAsJsonAsync("/api/tos/bookings", new
            {
                branchId = SctLcb01, orderTypeCode = "GATE TEST", lineCode = "MAEU", customerCode = "CUS-TAE", carrierRef,
                validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
                requirements = new object[] { new { equipmentTypeCode = "20GP", qty = 1 } },
                containers = new object[] { new { containerNo = oneyBox } },
            }, ct);
            Assert.True(booked.StatusCode == HttpStatusCode.Created, await booked.Content.ReadAsStringAsync(ct));

            async Task<GateFindingResponse> OwnerFindingAsync() =>
                Assert.Single((await client.GetFromJsonAsync<GatePreflightResponse>(
                    $"{Gate}/preflight?branchId={SctLcb01}&containerNo={oneyBox}&direction=IN", ct))!.Findings, f => f.Code == "OWNER_MISMATCH");

            Assert.Equal("WARN", (await OwnerFindingAsync()).Severity);
            await TestDatabase.SetSctOwnerMatchAsync("GATE TEST", "REFUSE");
            Assert.Equal("BLOCK", (await OwnerFindingAsync()).Severity);
        }
        finally
        {
            await TestDatabase.SetSctOwnerMatchAsync("GATE TEST", null);
            await TestDatabase.RemoveBookingsAsync(carrierRef);
        }
    }
}
