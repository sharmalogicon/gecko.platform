using Gecko.Data;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Infrastructure.Persistence;

// RevenueDbContext.cs and Entities/ are SCAFFOLDED from the gecko_revenue
// scripts (database-first, no migrations). Never hand-edit them; re-scaffold.
// `history` is not scaffolded: tariff.* is system-versioned and EF maps that
// with IsTemporal().
//
// Re-scaffold (from Gecko.Revenue\):
//   dotnet dotnet-ef dbcontext scaffold "Server=DESKTOP-6AQI384\APPIFY;Database=gecko_revenue;Integrated Security=true;TrustServerCertificate=true" Microsoft.EntityFrameworkCore.SqlServer --schema lookup --schema tariff --schema import --schema config --schema projection --schema billing --schema cashier --context RevenueDbContext --context-dir Infrastructure/Persistence --output-dir Infrastructure/Persistence/Entities --namespace Gecko.Revenue.Infrastructure.Persistence.Entities --context-namespace Gecko.Revenue.Infrastructure.Persistence --no-onconfiguring --force
public partial class RevenueDbContext
{
    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplySoftDeleteFilters();
    }
}
