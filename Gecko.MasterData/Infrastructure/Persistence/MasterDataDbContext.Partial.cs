using Gecko.Data;
using Microsoft.EntityFrameworkCore;

namespace Gecko.MasterData.Infrastructure.Persistence;

// MasterDataDbContext.cs and Entities/ are SCAFFOLDED from the gecko_master
// scripts (database-first, no migrations — the SQL owns the schema). Never
// hand-edit them; re-run the scaffold instead. Everything custom lives here.
//
// The `history` schema is deliberately NOT scaffolded: 43 of these tables are
// system-versioned and EF maps that with IsTemporal(), which is how history is
// queried (TemporalAsOf and friends). Scaffolding history.* as well would give
// a second, writable copy of every entity.
//
// Re-scaffold (from Gecko.MasterData\):
//   dotnet dotnet-ef dbcontext scaffold "Server=DESKTOP-6AQI384\APPIFY;Database=gecko_master;Integrated Security=true;TrustServerCertificate=true" Microsoft.EntityFrameworkCore.SqlServer --schema lookup --schema config --schema org --schema party --schema logistics --schema equipment --schema commercial --context MasterDataDbContext --context-dir Infrastructure/Persistence --output-dir Infrastructure/Persistence/Entities --namespace Gecko.MasterData.Infrastructure.Persistence.Entities --context-namespace Gecko.MasterData.Infrastructure.Persistence --no-onconfiguring --force
public partial class MasterDataDbContext
{
    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplySoftDeleteFilters();
    }
}
