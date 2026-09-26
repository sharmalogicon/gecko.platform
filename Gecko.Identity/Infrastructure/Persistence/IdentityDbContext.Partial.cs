using Gecko.Data;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Identity.Infrastructure.Persistence;

// IdentityDbContext.cs and Entities/ are SCAFFOLDED from the gecko_identity
// scripts (database-first, no migrations — the SQL owns the schema). Never
// hand-edit them; re-run the scaffold instead. Everything custom lives here.
//
// Re-scaffold (from Gecko.Identity\):
//   dotnet dotnet-ef dbcontext scaffold "Server=DESKTOP-6AQI384\APPIFY;Database=gecko_identity;Integrated Security=true;TrustServerCertificate=true" Microsoft.EntityFrameworkCore.SqlServer --schema lookup --schema tenant --schema iam --schema subscription --schema audit --context IdentityDbContext --context-dir Infrastructure/Persistence --output-dir Infrastructure/Persistence/Entities --namespace Gecko.Identity.Infrastructure.Persistence.Entities --context-namespace Gecko.Identity.Infrastructure.Persistence --no-onconfiguring --force
public partial class IdentityDbContext
{
    /// <summary>For <see cref="IdentitySystemDbContext"/>, which needs its own options type.</summary>
    protected IdentityDbContext(DbContextOptions options) : base(options)
    {
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplySoftDeleteFilters();
    }
}

/// <summary>
/// The same model on the gecko_system connection with IsSystemContext raised:
/// sees every tenant, and can read iam.credential.password_hash.
///
/// Inject it ONLY into login, invitation acceptance, password reset and
/// platform provisioning. Everything else takes <see cref="IdentityDbContext"/>.
/// </summary>
public sealed class IdentitySystemDbContext(DbContextOptions<IdentitySystemDbContext> options)
    : IdentityDbContext(options);
