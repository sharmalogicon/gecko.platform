using Gecko.Data;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Infrastructure.Persistence;

// TosDbContext.cs and Entities/ are SCAFFOLDED from the gecko_tos scripts
// (database-first, no migrations). Never hand-edit them; re-scaffold.
// config.* (number series) and outbox.* are not scaffolded: the counter only
// moves through config.usp_next_number, and gecko_app cannot write config.* at all.
//
// Re-scaffold (from Gecko.Tos\):
//   dotnet dotnet-ef dbcontext scaffold "Server=DESKTOP-6AQI384\APPIFY;Database=gecko_tos;Integrated Security=true;TrustServerCertificate=true" Microsoft.EntityFrameworkCore.SqlServer --schema lookup --schema vessel --schema booking --schema yard --schema gate --context TosDbContext --context-dir Infrastructure/Persistence --output-dir Infrastructure/Persistence/Entities --namespace Gecko.Tos.Infrastructure.Persistence.Entities --context-namespace Gecko.Tos.Infrastructure.Persistence --no-onconfiguring --force
public partial class TosDbContext
{
    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplySoftDeleteFilters();
    }
}
