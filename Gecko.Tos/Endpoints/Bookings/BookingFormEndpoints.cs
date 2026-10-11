using Gecko.Data;
using Gecko.MasterData.Contracts;
using Gecko.SharedKernel;
using Gecko.Tos.Application;
using Gecko.Tos.Application.Reports.Forms;
using Gecko.Tos.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Endpoints.Bookings;

/// <summary>
/// Vector's booking forms (Booking Entry → Print, owner 2026-10-11): the 13 CY/CY, CFS and CYD forms of one booking as PDFs.
///   GET /bookings/forms                     the forms, in the desktop menu's order (key + menu name)
///   GET /bookings/{id}/forms/{form}.pdf     one form of one booking
/// The clerk picks the form, as on Vector (nothing ties a form to a booking type). A cancelled booking has no form
/// (Vector printed only the form's fixed text). tos.booking.view at the booking's branch.
/// </summary>
internal static class BookingFormEndpoints
{
    public static RouteGroupBuilder MapBookingFormEndpoints(this RouteGroupBuilder tos)
    {
        var bookings = tos.MapGroup("/bookings").WithTags("TOS — bookings");
        bookings.MapGet("/forms", () => TypedResults.Ok(BookingForms.All.Select(f => new BookingFormResponse(f.Key, f.Menu)).ToList()))
            .RequireBranchPermission(TosPermissions.BookingView)
            .WithSummary("The booking forms Vector printed from Booking Entry (CY/CY, CFS, CYD), in its menu's order");
        bookings.MapGet("/{id:guid}/forms/{form}.pdf", PdfAsync).RequireBranchPermission(TosPermissions.BookingView)
            .WithSummary("One of the booking's forms as Vector printed it (TMS.Booking.CY/CFS/CYD.*), as a PDF");
        return tos;
    }

    private static async Task<Results<FileContentHttpResult, NotFound<ProblemDetails>, ProblemHttpResult>> PdfAsync(
        Guid id, string form, TosDbContext db, IMasterDataReferences master, BranchClock clock, ICallerPermissions scope, CancellationToken ct)
    {
        var chosen = BookingForms.All.FirstOrDefault(f => string.Equals(f.Key, form, StringComparison.OrdinalIgnoreCase));
        if (chosen.Key is null)
            return TypedResults.NotFound(new ProblemDetails { Title = $"No booking form '{form}'.", Detail = "GET /api/tos/bookings/forms lists them." });
        var booking = await db.Bookings.AsNoTracking().Where(b => b.BookingId == id && b.DeletedAt == null)
            .Select(b => new { b.BranchId, b.Status, b.OrderNo }).SingleOrDefaultAsync(ct);
        // Another branch's booking reads as not found, as GET /bookings/{id} answers.
        if (booking is null || !scope.HasAt(TosPermissions.BookingView, booking.BranchId))
            return TypedResults.NotFound(new ProblemDetails { Title = "No such booking." });
        if (booking.Status == "CANCELLED")
            return TypedResults.Problem(title: $"{booking.OrderNo} is cancelled.", detail: "A cancelled booking has no forms.", statusCode: StatusCodes.Status409Conflict);

        var branch = (await clock.BranchesAsync([booking.BranchId], ct)).GetValueOrDefault(booking.BranchId);
        if (branch is null) return TypedResults.NotFound(new ProblemDetails { Title = "This depot has no master-data profile." });
        var data = await BookingForm.LoadAsync(db, master, branch, id, ct);
        if (data is null) return TypedResults.NotFound(new ProblemDetails { Title = "No such booking." });
        return TypedResults.File(chosen.Render(data), "application/pdf", $"{booking.OrderNo}-{chosen.Key}.pdf");
    }
}

public sealed record BookingFormResponse(string Key, string Menu);
