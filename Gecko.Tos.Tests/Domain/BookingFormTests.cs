using Gecko.Tos.Application.Reports.Forms;

namespace Gecko.Tos.Tests.Domain;

/// <summary>
/// Vector's 13 booking forms (TMS.Booking.CY/CFS/CYD.*) render from a booking's data alone — with a short and a long box
/// list (the list grows and the items below it move down, onto a second page) — and number the boxes as Vector's
/// RunningValue(ContainerNo, CountDistinct) did. Invented values only.
/// </summary>
public sealed class BookingFormTests
{
    private static BookingForm Form(int boxes, string? company = "บริษัท ทดสอบ ดีโป จำกัด") => new(
        "BK-TST-0001", "BL-TEST-1", "SUB-1", "Test Line Co., Ltd.", "Test Buyer Co., Ltd.", "TEST VESSEL", "001W",
        new DateTime(2026, 10, 20), "TERM1", "MARKS 1-10", "10", "CTN", 25.5m, 18000m, "Frozen shrimp",
        Enumerable.Range(1, boxes).Select(i => new BookingFormBox($"TSTU{i:0000000}", i % 2 == 0 ? "40" : "20", "GP")).ToList(), company);

    public static TheoryData<string> Keys => [.. BookingForms.All.Select(f => f.Key)];

    [Theory]
    [MemberData(nameof(Keys))]
    public void Every_form_renders_with_a_short_and_a_long_box_list(string key)
    {
        var form = BookingForms.All.Single(f => f.Key == key);
        foreach (var data in new[] { Form(1), Form(25, company: null), Form(0) })
        {
            var pdf = form.Render(data);
            Assert.Equal("%PDF"u8.ToArray(), pdf[..4]);
        }
    }

    [Fact]
    public void Thirteen_forms_in_the_desktop_menus_order()
    {
        Assert.Equal(13, BookingForms.All.Count);
        Assert.Equal(13, BookingForms.All.Select(f => f.Key).Distinct().Count());
        Assert.StartsWith("CY/CY", BookingForms.All[0].Menu, StringComparison.Ordinal);
    }

    [Fact]
    public void Boxes_are_numbered_as_vector_numbered_them_an_unnominated_place_repeating_the_number()
    {
        var form = Form(0) with
        {
            Boxes = [new("TSTU0000001", "20", "GP"), new("", "20", "GP"), new("", "40", "HC"), new("TSTU0000002", "40", "HC")],
        };

        Assert.Equal([1, 2, 2, 3], form.LineNumbers);
        Assert.Equal("2.  40 HC", form.BoxLines[2]);
        Assert.Equal(4, form.ContainerCount);
    }
}
