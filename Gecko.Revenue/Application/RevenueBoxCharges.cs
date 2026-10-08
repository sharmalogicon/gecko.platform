using Gecko.Revenue.Contracts;
using Gecko.Revenue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Application;

/// <summary><see cref="IRevenueBoxCharges"/>, under the caller's tenant (RLS).</summary>
internal sealed class RevenueBoxCharges(RevenueDbContext db) : IRevenueBoxCharges
{
    public Task<string?> PaidOnAsync(Guid bookingContainerId, CancellationToken ct) =>
        (from c in db.Charges.AsNoTracking()
         where c.BookingContainerId == bookingContainerId && c.Status == ChargeStatus.Paid && c.ReceiptId != null
         join r in db.Receipts on c.ReceiptId equals r.ReceiptId
         where r.Status == "ISSUED"
         select r.ReceiptNo).FirstOrDefaultAsync(ct);
}
