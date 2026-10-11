using System.Net;
using System.Net.Http.Json;
using Gecko.SharedKernel;
using Gecko.Tos.Endpoints.Bookings;

namespace Gecko.Tos.Tests.Api;

/// <summary>
/// Vector's booking forms (Booking Entry → Print, owner 2026-10-11) through the real host: the 13 forms are listed in the
/// desktop menu's order, each prints a booking as a PDF, an unknown form or another tenant's booking is not found, and a
/// cancelled booking has no forms. The layouts themselves are covered by BookingFormTests.
/// </summary>
[Collection(TosApiCollection.Name)]
public sealed class BookingFormApiTests(TosApiFactory api)
{
    private static readonly Guid SctLcb01 = Guid.Parse("C558E785-33A5-F111-9B0D-00919E4766D5");

    private static string NewRef() => $"ZZF-{Guid.NewGuid():N}"[..16].ToUpperInvariant();

    private static string NewBox()
    {
        var ten = $"ZZFU{Random.Shared.Next(100000, 999999)}";
        return ten + ContainerNumber.CheckDigitOf(ten);
    }

    [Fact]
    public async Task Every_form_prints_the_booking_and_a_cancelled_booking_has_none()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await api.ClientForAsync(TosApiFactory.SctOwner);
        var carrierRef = NewRef();
        try
        {
            var created = await client.PostAsJsonAsync("/api/tos/bookings", new
            {
                branchId = SctLcb01, orderTypeCode = "IMP CY/CY", lineCode = "MAEU", customerCode = "CUS-TAE", carrierRef,
                validTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
                requirements = new object[] { new { equipmentTypeCode = "20GP", qty = 2 } },
                containers = new object[] { new { containerNo = NewBox() } },
            }, ct);
            Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(ct));
            var booking = (await created.Content.ReadFromJsonAsync<BookingDetailResponse>(ct))!.Booking;

            var forms = (await client.GetFromJsonAsync<List<BookingFormResponse>>("/api/tos/bookings/forms", ct))!;
            Assert.Equal(13, forms.Count);
            Assert.Equal(("cy-cargo-release-permit", "CY/CY › Cargo Release Permit Form"), (forms[0].Key, forms[0].Menu));

            foreach (var form in forms)
            {
                var pdf = await client.GetAsync($"/api/tos/bookings/{booking.BookingId}/forms/{form.Key}.pdf", ct);
                Assert.True(pdf.StatusCode == HttpStatusCode.OK, $"{form.Key}: {(int)pdf.StatusCode} {await pdf.Content.ReadAsStringAsync(ct)}");
                Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
                Assert.Equal("%PDF"u8.ToArray(), (await pdf.Content.ReadAsByteArrayAsync(ct))[..4]);
            }

            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/tos/bookings/{booking.BookingId}/forms/receipt.pdf", ct)).StatusCode);
            var other = await api.ClientForAsync(TosApiFactory.SssOwner);
            Assert.Equal(HttpStatusCode.NotFound,
                (await other.GetAsync($"/api/tos/bookings/{booking.BookingId}/forms/cy-delivery-order.pdf", ct)).StatusCode);

            var cancelled = await client.PostAsJsonAsync($"/api/tos/bookings/{booking.BookingId}/cancel",
                new { reason = "Customer cancelled the delivery order", rowVersion = booking.RowVersion }, ct);
            Assert.True(cancelled.StatusCode == HttpStatusCode.OK, await cancelled.Content.ReadAsStringAsync(ct));
            Assert.Equal(HttpStatusCode.Conflict,
                (await client.GetAsync($"/api/tos/bookings/{booking.BookingId}/forms/cy-delivery-order.pdf", ct)).StatusCode);
        }
        finally { await TestDatabase.RemoveBookingsAsync(carrierRef); }
    }
}
