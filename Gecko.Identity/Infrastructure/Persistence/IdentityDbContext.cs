using System;
using System.Collections.Generic;
using Gecko.Identity.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Identity.Infrastructure.Persistence;

public partial class IdentityDbContext : DbContext
{
    public IdentityDbContext(DbContextOptions<IdentityDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<ApiToken> ApiTokens { get; set; }

    public virtual DbSet<AuthEvent> AuthEvents { get; set; }

    public virtual DbSet<Branch> Branches { get; set; }

    public virtual DbSet<ChangeLog> ChangeLogs { get; set; }

    public virtual DbSet<Country> Countries { get; set; }

    public virtual DbSet<Credential> Credentials { get; set; }

    public virtual DbSet<Entitlement> Entitlements { get; set; }

    public virtual DbSet<Invitation> Invitations { get; set; }

    public virtual DbSet<Invoice> Invoices { get; set; }

    public virtual DbSet<InvoiceLine> InvoiceLines { get; set; }

    public virtual DbSet<Module> Modules { get; set; }

    public virtual DbSet<OnboardingState> OnboardingStates { get; set; }

    public virtual DbSet<Permission> Permissions { get; set; }

    public virtual DbSet<Plan> Plans { get; set; }

    public virtual DbSet<PlanLimit> PlanLimits { get; set; }

    public virtual DbSet<RefreshToken> RefreshTokens { get; set; }

    public virtual DbSet<Role> Roles { get; set; }

    public virtual DbSet<RolePermission> RolePermissions { get; set; }

    public virtual DbSet<RoleTemplate> RoleTemplates { get; set; }

    public virtual DbSet<RoleTemplatePermission> RoleTemplatePermissions { get; set; }

    public virtual DbSet<Tenant> Tenants { get; set; }

    public virtual DbSet<UsageCounter> UsageCounters { get; set; }

    public virtual DbSet<User> Users { get; set; }

    public virtual DbSet<UserBranch> UserBranches { get; set; }

    public virtual DbSet<UserRole> UserRoles { get; set; }

    public virtual DbSet<VwApiTokenUsable> VwApiTokenUsables { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApiToken>(entity =>
        {
            entity.HasKey(e => e.ApiTokenId).HasName("pk_api_token");

            entity.ToTable("api_token", "iam");

            entity.HasIndex(e => e.TenantId, "ix_api_token__tenant").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => e.TokenHash, "uq_api_token__hash")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.ApiTokenId)
                .HasDefaultValueSql("(newsequentialid())")
                .HasColumnName("api_token_id");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.LastUsedAt).HasColumnName("last_used_at");
            entity.Property(e => e.ModuleCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("module_code");
            entity.Property(e => e.Name)
                .HasMaxLength(100)
                .HasColumnName("name");
            entity.Property(e => e.RevokedAt).HasColumnName("revoked_at");
            entity.Property(e => e.ScopesJson).HasColumnName("scopes_json");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TokenHash)
                .HasMaxLength(32)
                .IsFixedLength()
                .HasColumnName("token_hash");
            entity.Property(e => e.TokenPrefix)
                .HasMaxLength(12)
                .IsUnicode(false)
                .HasColumnName("token_prefix");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<AuthEvent>(entity =>
        {
            entity.HasKey(e => e.AuthEventId).HasName("pk_auth_event");

            entity.ToTable("auth_event", "audit");

            entity.HasIndex(e => new { e.IpAddress, e.OccurredAt }, "ix_auth_event__failures")
                .IsDescending(false, true)
                .HasFilter("([outcome]='FAILURE')");

            entity.HasIndex(e => new { e.TenantId, e.OccurredAt }, "ix_auth_event__tenant_time").IsDescending(false, true);

            entity.Property(e => e.AuthEventId).HasColumnName("auth_event_id");
            entity.Property(e => e.CorrelationId).HasColumnName("correlation_id");
            entity.Property(e => e.EmailAttempted)
                .HasMaxLength(256)
                .HasColumnName("email_attempted");
            entity.Property(e => e.EventType)
                .HasMaxLength(40)
                .IsUnicode(false)
                .HasColumnName("event_type");
            entity.Property(e => e.FailureReason)
                .HasMaxLength(60)
                .IsUnicode(false)
                .HasColumnName("failure_reason");
            entity.Property(e => e.IpAddress)
                .HasMaxLength(45)
                .IsUnicode(false)
                .HasColumnName("ip_address");
            entity.Property(e => e.OccurredAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("occurred_at");
            entity.Property(e => e.Outcome)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("outcome");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UserAgent)
                .HasMaxLength(400)
                .HasColumnName("user_agent");
            entity.Property(e => e.UserId).HasColumnName("user_id");
        });

        modelBuilder.Entity<Branch>(entity =>
        {
            entity.HasKey(e => e.BranchId).HasName("pk_branch");

            entity.ToTable("branch", "tenant");

            entity.HasIndex(e => e.TenantId, "ix_branch__tenant").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.BranchCode }, "uq_branch__tenant_code")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.BranchId)
                .HasDefaultValueSql("(newsequentialid())")
                .HasColumnName("branch_id");
            entity.Property(e => e.AddressLine1)
                .HasMaxLength(200)
                .HasColumnName("address_line1");
            entity.Property(e => e.AddressLine2)
                .HasMaxLength(200)
                .HasColumnName("address_line2");
            entity.Property(e => e.BranchCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("branch_code");
            entity.Property(e => e.BranchType)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("branch_type");
            entity.Property(e => e.City)
                .HasMaxLength(100)
                .HasColumnName("city");
            entity.Property(e => e.CountryCode)
                .HasMaxLength(2)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("country_code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DefaultLocale)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("default_locale");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.DisplayName)
                .HasMaxLength(200)
                .HasColumnName("display_name");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.Timezone)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("timezone");
            entity.Property(e => e.Unlocode)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("unlocode");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<ChangeLog>(entity =>
        {
            entity.HasKey(e => e.ChangeLogId).HasName("pk_change_log");

            entity.ToTable("change_log", "audit");

            entity.HasIndex(e => new { e.EntityType, e.EntityId, e.OccurredAt }, "ix_change_log__entity").IsDescending(false, false, true);

            entity.HasIndex(e => new { e.TenantId, e.OccurredAt }, "ix_change_log__tenant_time").IsDescending(false, true);

            entity.Property(e => e.ChangeLogId).HasColumnName("change_log_id");
            entity.Property(e => e.Action)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("action");
            entity.Property(e => e.ActorDescription)
                .HasMaxLength(200)
                .HasColumnName("actor_description");
            entity.Property(e => e.ActorUserId).HasColumnName("actor_user_id");
            entity.Property(e => e.AfterJson).HasColumnName("after_json");
            entity.Property(e => e.BeforeJson).HasColumnName("before_json");
            entity.Property(e => e.CorrelationId).HasColumnName("correlation_id");
            entity.Property(e => e.EntityId).HasColumnName("entity_id");
            entity.Property(e => e.EntityType)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("entity_type");
            entity.Property(e => e.IpAddress)
                .HasMaxLength(45)
                .IsUnicode(false)
                .HasColumnName("ip_address");
            entity.Property(e => e.OccurredAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("occurred_at");
            entity.Property(e => e.Reason)
                .HasMaxLength(400)
                .HasColumnName("reason");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
        });

        modelBuilder.Entity<Country>(entity =>
        {
            entity.HasKey(e => e.CountryCode).HasName("pk_country");

            entity.ToTable("country", "lookup");

            entity.Property(e => e.CountryCode)
                .HasMaxLength(2)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("country_code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("created_at");
            entity.Property(e => e.DefaultCurrency)
                .HasMaxLength(3)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("default_currency");
            entity.Property(e => e.DefaultLocale)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("default_locale");
            entity.Property(e => e.DefaultTimezone)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("default_timezone");
            entity.Property(e => e.DisplayName)
                .HasMaxLength(100)
                .HasColumnName("display_name");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
        });

        modelBuilder.Entity<Credential>(entity =>
        {
            entity.HasKey(e => e.CredentialId).HasName("pk_credential");

            entity.ToTable("credential", "iam");

            entity.HasIndex(e => e.UserId, "uq_credential__current")
                .IsUnique()
                .HasFilter("([is_current]=(1) AND [deleted_at] IS NULL)");

            entity.Property(e => e.CredentialId)
                .HasDefaultValueSql("(newsequentialid())")
                .HasColumnName("credential_id");
            entity.Property(e => e.Algorithm)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasDefaultValue("ARGON2ID")
                .HasColumnName("algorithm");
            entity.Property(e => e.AlgorithmParams)
                .HasMaxLength(200)
                .HasColumnName("algorithm_params");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
            entity.Property(e => e.IsCurrent)
                .HasDefaultValue(true)
                .HasColumnName("is_current");
            entity.Property(e => e.MustChange).HasColumnName("must_change");
            entity.Property(e => e.PasswordHash)
                .HasMaxLength(512)
                .HasColumnName("password_hash");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UserId).HasColumnName("user_id");
        });

        modelBuilder.Entity<Entitlement>(entity =>
        {
            entity.HasKey(e => e.EntitlementId).HasName("pk_entitlement");

            entity.ToTable("entitlement", "subscription");

            entity.HasIndex(e => e.CurrentPeriodEnd, "ix_entitlement__period_end").HasFilter("(([status] IN ('TRIAL', 'ACTIVE', 'PAST_DUE')) AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.Status }, "ix_entitlement__tenant_status").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.BranchId, e.ModuleCode }, "uq_entitlement__branch_scoped")
                .IsUnique()
                .HasFilter("([branch_id] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.ModuleCode }, "uq_entitlement__tenant_scoped")
                .IsUnique()
                .HasFilter("([branch_id] IS NULL AND [deleted_at] IS NULL)");

            entity.Property(e => e.EntitlementId)
                .HasDefaultValueSql("(newsequentialid())")
                .HasColumnName("entitlement_id");
            entity.Property(e => e.ActivatedAt).HasColumnName("activated_at");
            entity.Property(e => e.BillingCycle)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasDefaultValue("MONTHLY")
                .HasColumnName("billing_cycle");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.CancelAtPeriodEnd).HasColumnName("cancel_at_period_end");
            entity.Property(e => e.CancelledAt).HasColumnName("cancelled_at");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CurrencyOverride)
                .HasMaxLength(3)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("currency_override");
            entity.Property(e => e.CurrentPeriodEnd).HasColumnName("current_period_end");
            entity.Property(e => e.CurrentPeriodStart).HasColumnName("current_period_start");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.ModuleCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("module_code");
            entity.Property(e => e.Notes)
                .HasMaxLength(500)
                .HasColumnName("notes");
            entity.Property(e => e.PlanId).HasColumnName("plan_id");
            entity.Property(e => e.PriceOverride)
                .HasColumnType("decimal(12, 2)")
                .HasColumnName("price_override");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("TRIAL")
                .HasColumnName("status");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TrialEndsAt).HasColumnName("trial_ends_at");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<Invitation>(entity =>
        {
            entity.HasKey(e => e.InvitationId).HasName("pk_invitation");

            entity.ToTable("invitation", "iam");

            entity.HasIndex(e => new { e.TenantId, e.EmailNormalised }, "uq_invitation__pending")
                .IsUnique()
                .HasFilter("([accepted_at] IS NULL AND [revoked_at] IS NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => e.TokenHash, "uq_invitation__token")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.InvitationId)
                .HasDefaultValueSql("(newsequentialid())")
                .HasColumnName("invitation_id");
            entity.Property(e => e.AcceptedAt).HasColumnName("accepted_at");
            entity.Property(e => e.AcceptedUserId).HasColumnName("accepted_user_id");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.Email)
                .HasMaxLength(256)
                .HasColumnName("email");
            entity.Property(e => e.EmailNormalised)
                .HasMaxLength(256)
                .HasColumnName("email_normalised");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
            entity.Property(e => e.FullName)
                .HasMaxLength(200)
                .HasColumnName("full_name");
            entity.Property(e => e.ResentCount).HasColumnName("resent_count");
            entity.Property(e => e.RevokedAt).HasColumnName("revoked_at");
            entity.Property(e => e.RoleId).HasColumnName("role_id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TokenHash)
                .HasMaxLength(32)
                .IsFixedLength()
                .HasColumnName("token_hash");
        });

        modelBuilder.Entity<Invoice>(entity =>
        {
            entity.HasKey(e => e.InvoiceId).HasName("pk_invoice");

            entity.ToTable("invoice", "subscription");

            entity.HasIndex(e => new { e.TenantId, e.Status }, "ix_invoice__tenant_status").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => e.InvoiceNumber, "uq_invoice__number")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.InvoiceId)
                .HasDefaultValueSql("(newsequentialid())")
                .HasColumnName("invoice_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.Currency)
                .HasMaxLength(3)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("currency");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.DueAt).HasColumnName("due_at");
            entity.Property(e => e.InvoiceNumber)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("invoice_number");
            entity.Property(e => e.IssuedAt).HasColumnName("issued_at");
            entity.Property(e => e.PaidAt).HasColumnName("paid_at");
            entity.Property(e => e.PaymentReference)
                .HasMaxLength(100)
                .HasColumnName("payment_reference");
            entity.Property(e => e.PeriodEnd).HasColumnName("period_end");
            entity.Property(e => e.PeriodStart).HasColumnName("period_start");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("DRAFT")
                .HasColumnName("status");
            entity.Property(e => e.Subtotal)
                .HasColumnType("decimal(12, 2)")
                .HasColumnName("subtotal");
            entity.Property(e => e.TaxAmount)
                .HasColumnType("decimal(12, 2)")
                .HasColumnName("tax_amount");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TotalAmount)
                .HasColumnType("decimal(12, 2)")
                .HasColumnName("total_amount");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<InvoiceLine>(entity =>
        {
            entity.HasKey(e => e.InvoiceLineId).HasName("pk_invoice_line");

            entity.ToTable("invoice_line", "subscription");

            entity.HasIndex(e => e.InvoiceId, "ix_invoice_line__invoice").HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.InvoiceLineId)
                .HasDefaultValueSql("(newsequentialid())")
                .HasColumnName("invoice_line_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.Description)
                .HasMaxLength(300)
                .HasColumnName("description");
            entity.Property(e => e.EntitlementId).HasColumnName("entitlement_id");
            entity.Property(e => e.InvoiceId).HasColumnName("invoice_id");
            entity.Property(e => e.LineTotal)
                .HasColumnType("decimal(12, 2)")
                .HasColumnName("line_total");
            entity.Property(e => e.Quantity)
                .HasDefaultValue(1m)
                .HasColumnType("decimal(12, 2)")
                .HasColumnName("quantity");
            entity.Property(e => e.SortOrder).HasColumnName("sort_order");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UnitPrice)
                .HasColumnType("decimal(12, 2)")
                .HasColumnName("unit_price");
        });

        modelBuilder.Entity<Module>(entity =>
        {
            entity.HasKey(e => e.ModuleCode).HasName("pk_module");

            entity.ToTable("module", "lookup");

            entity.Property(e => e.ModuleCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("module_code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("created_at");
            entity.Property(e => e.Description)
                .HasMaxLength(400)
                .HasColumnName("description");
            entity.Property(e => e.DisplayName)
                .HasMaxLength(100)
                .HasColumnName("display_name");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.IsBranchScoped)
                .HasDefaultValue(true)
                .HasColumnName("is_branch_scoped");
            entity.Property(e => e.RequiresOnboarding)
                .HasDefaultValue(true)
                .HasColumnName("requires_onboarding");
            entity.Property(e => e.SortOrder).HasColumnName("sort_order");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("updated_at");
        });

        modelBuilder.Entity<OnboardingState>(entity =>
        {
            entity.HasKey(e => e.OnboardingId).HasName("pk_onboarding_state");

            entity.ToTable("onboarding_state", "tenant");

            entity.HasIndex(e => new { e.TenantId, e.BranchId, e.ModuleCode }, "uq_onboarding__branch_scoped")
                .IsUnique()
                .HasFilter("([branch_id] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.ModuleCode }, "uq_onboarding__tenant_scoped")
                .IsUnique()
                .HasFilter("([branch_id] IS NULL AND [deleted_at] IS NULL)");

            entity.Property(e => e.OnboardingId)
                .HasDefaultValueSql("(newsequentialid())")
                .HasColumnName("onboarding_id");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.CompletedAt).HasColumnName("completed_at");
            entity.Property(e => e.CompletedBy).HasColumnName("completed_by");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CurrentStep)
                .HasDefaultValue(1)
                .HasColumnName("current_step");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.ModuleCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("module_code");
            entity.Property(e => e.StartedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("started_at");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TotalSteps)
                .HasDefaultValue(5)
                .HasColumnName("total_steps");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.WizardDataJson).HasColumnName("wizard_data_json");
        });

        modelBuilder.Entity<Permission>(entity =>
        {
            entity.HasKey(e => e.PermissionCode).HasName("pk_permission");

            entity.ToTable("permission", "iam");

            entity.HasIndex(e => e.ModuleCode, "ix_permission__module");

            entity.Property(e => e.PermissionCode)
                .HasMaxLength(80)
                .IsUnicode(false)
                .HasColumnName("permission_code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("created_at");
            entity.Property(e => e.Description)
                .HasMaxLength(400)
                .HasColumnName("description");
            entity.Property(e => e.DisplayName)
                .HasMaxLength(150)
                .HasColumnName("display_name");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.ModuleCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("module_code");
        });

        modelBuilder.Entity<Plan>(entity =>
        {
            entity.HasKey(e => e.PlanId).HasName("pk_plan");

            entity.ToTable("plan", "subscription");

            entity.HasIndex(e => new { e.ModuleCode, e.PlanCode }, "uq_plan__module_code")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.PlanId)
                .HasDefaultValueSql("(newsequentialid())")
                .HasColumnName("plan_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.Currency)
                .HasMaxLength(3)
                .IsUnicode(false)
                .HasDefaultValue("USD")
                .IsFixedLength()
                .HasColumnName("currency");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.Description)
                .HasMaxLength(400)
                .HasColumnName("description");
            entity.Property(e => e.DisplayName)
                .HasMaxLength(100)
                .HasColumnName("display_name");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.IsPublic)
                .HasDefaultValue(true)
                .HasColumnName("is_public");
            entity.Property(e => e.ModuleCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("module_code");
            entity.Property(e => e.PlanCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("plan_code");
            entity.Property(e => e.PriceMonthly)
                .HasColumnType("decimal(12, 2)")
                .HasColumnName("price_monthly");
            entity.Property(e => e.PriceYearly)
                .HasColumnType("decimal(12, 2)")
                .HasColumnName("price_yearly");
            entity.Property(e => e.TierLevel).HasColumnName("tier_level");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<PlanLimit>(entity =>
        {
            entity.HasKey(e => e.PlanLimitId).HasName("pk_plan_limit");

            entity.ToTable("plan_limit", "subscription");

            entity.HasIndex(e => new { e.PlanId, e.MetricCode }, "uq_plan_limit")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.PlanLimitId)
                .HasDefaultValueSql("(newsequentialid())")
                .HasColumnName("plan_limit_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.DisplayName)
                .HasMaxLength(100)
                .HasColumnName("display_name");
            entity.Property(e => e.IsHardLimit).HasColumnName("is_hard_limit");
            entity.Property(e => e.LimitValue).HasColumnName("limit_value");
            entity.Property(e => e.MetricCode)
                .HasMaxLength(40)
                .IsUnicode(false)
                .HasColumnName("metric_code");
            entity.Property(e => e.Period)
                .HasMaxLength(15)
                .IsUnicode(false)
                .HasDefaultValue("MONTH")
                .HasColumnName("period");
            entity.Property(e => e.PlanId).HasColumnName("plan_id");
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasKey(e => e.TokenId).HasName("pk_refresh_token");

            entity.ToTable("refresh_token", "iam");

            entity.HasIndex(e => new { e.UserId, e.ExpiresAt }, "ix_refresh_token__active").HasFilter("([revoked_at] IS NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => e.TokenHash, "uq_refresh_token__hash")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.TokenId)
                .HasDefaultValueSql("(newsequentialid())")
                .HasColumnName("token_id");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
            entity.Property(e => e.IpAddress)
                .HasMaxLength(45)
                .IsUnicode(false)
                .HasColumnName("ip_address");
            entity.Property(e => e.IssuedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("issued_at");
            entity.Property(e => e.ReplacedById).HasColumnName("replaced_by_id");
            entity.Property(e => e.RevokedAt).HasColumnName("revoked_at");
            entity.Property(e => e.RevokedReason)
                .HasMaxLength(40)
                .IsUnicode(false)
                .HasColumnName("revoked_reason");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TokenHash)
                .HasMaxLength(32)
                .IsFixedLength()
                .HasColumnName("token_hash");
            entity.Property(e => e.UserAgent)
                .HasMaxLength(400)
                .HasColumnName("user_agent");
            entity.Property(e => e.UserId).HasColumnName("user_id");
        });

        modelBuilder.Entity<Role>(entity =>
        {
            entity.HasKey(e => e.RoleId).HasName("pk_role");

            entity.ToTable("role", "iam");

            entity.HasIndex(e => new { e.TenantId, e.RoleCode }, "uq_role__tenant_code")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.RoleId)
                .HasDefaultValueSql("(newsequentialid())")
                .HasColumnName("role_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.Description)
                .HasMaxLength(400)
                .HasColumnName("description");
            entity.Property(e => e.DisplayName)
                .HasMaxLength(100)
                .HasColumnName("display_name");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.IsSystem)
                .HasDefaultValue(true)
                .HasColumnName("is_system");
            entity.Property(e => e.IsTenantAdmin).HasColumnName("is_tenant_admin");
            entity.Property(e => e.RoleCode)
                .HasMaxLength(40)
                .IsUnicode(false)
                .HasColumnName("role_code");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<RolePermission>(entity =>
        {
            entity.HasKey(e => e.RolePermissionId).HasName("pk_role_permission");

            entity.ToTable("role_permission", "iam");

            entity.HasIndex(e => new { e.RoleId, e.PermissionCode }, "uq_role_permission")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.RolePermissionId)
                .HasDefaultValueSql("(newsequentialid())")
                .HasColumnName("role_permission_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.PermissionCode)
                .HasMaxLength(80)
                .IsUnicode(false)
                .HasColumnName("permission_code");
            entity.Property(e => e.RoleId).HasColumnName("role_id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
        });

        modelBuilder.Entity<RoleTemplate>(entity =>
        {
            entity.HasKey(e => e.RoleCode).HasName("pk_role_template");

            entity.ToTable("role_template", "iam");

            entity.Property(e => e.RoleCode)
                .HasMaxLength(40)
                .IsUnicode(false)
                .HasColumnName("role_code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("created_at");
            entity.Property(e => e.Description)
                .HasMaxLength(400)
                .HasColumnName("description");
            entity.Property(e => e.DisplayName)
                .HasMaxLength(100)
                .HasColumnName("display_name");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.IsAssignable)
                .HasDefaultValue(true)
                .HasColumnName("is_assignable");
            entity.Property(e => e.IsTenantAdmin).HasColumnName("is_tenant_admin");
            entity.Property(e => e.SortOrder).HasColumnName("sort_order");
        });

        modelBuilder.Entity<RoleTemplatePermission>(entity =>
        {
            entity.HasKey(e => new { e.RoleCode, e.PermissionCode }).HasName("pk_role_template_permission");

            entity.ToTable("role_template_permission", "iam");

            entity.Property(e => e.RoleCode)
                .HasMaxLength(40)
                .IsUnicode(false)
                .HasColumnName("role_code");
            entity.Property(e => e.PermissionCode)
                .HasMaxLength(80)
                .IsUnicode(false)
                .HasColumnName("permission_code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("created_at");
        });

        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.HasKey(e => e.TenantId).HasName("pk_tenant");

            entity.ToTable("tenant", "tenant");

            entity.HasIndex(e => e.Status, "ix_tenant__status").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => e.TenantCode, "uq_tenant__code")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.TenantId)
                .HasDefaultValueSql("(newsequentialid())")
                .HasColumnName("tenant_id");
            entity.Property(e => e.ActivatedAt).HasColumnName("activated_at");
            entity.Property(e => e.ClosedAt).HasColumnName("closed_at");
            entity.Property(e => e.CountryCode)
                .HasMaxLength(2)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("country_code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DefaultCurrency)
                .HasMaxLength(3)
                .IsUnicode(false)
                .HasDefaultValue("THB")
                .IsFixedLength()
                .HasColumnName("default_currency");
            entity.Property(e => e.DefaultLocale)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasDefaultValue("en")
                .HasColumnName("default_locale");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.DisplayName)
                .HasMaxLength(200)
                .HasColumnName("display_name");
            entity.Property(e => e.LegalName)
                .HasMaxLength(200)
                .HasColumnName("legal_name");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("ACTIVE")
                .HasColumnName("status");
            entity.Property(e => e.SuspendedAt).HasColumnName("suspended_at");
            entity.Property(e => e.TaxId)
                .HasMaxLength(50)
                .HasColumnName("tax_id");
            entity.Property(e => e.TenantCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("tenant_code");
            entity.Property(e => e.Timezone)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasDefaultValue("Asia/Bangkok")
                .HasColumnName("timezone");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<UsageCounter>(entity =>
        {
            entity.HasKey(e => e.UsageId).HasName("pk_usage_counter");

            entity.ToTable("usage_counter", "subscription");

            entity.HasIndex(e => new { e.EntitlementId, e.MetricCode, e.PeriodStart }, "uq_usage_counter").IsUnique();

            entity.Property(e => e.UsageId)
                .HasDefaultValueSql("(newsequentialid())")
                .HasColumnName("usage_id");
            entity.Property(e => e.EntitlementId).HasColumnName("entitlement_id");
            entity.Property(e => e.LastUpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("last_updated_at");
            entity.Property(e => e.LimitSnapshot).HasColumnName("limit_snapshot");
            entity.Property(e => e.MetricCode)
                .HasMaxLength(40)
                .IsUnicode(false)
                .HasColumnName("metric_code");
            entity.Property(e => e.PeriodStart).HasColumnName("period_start");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UsedValue).HasColumnName("used_value");
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.UserId).HasName("pk_user");

            entity.ToTable("user", "iam");

            entity.HasIndex(e => e.TenantId, "ix_user__tenant").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => e.EmailNormalised, "uq_user__email")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => e.UserName, "uq_user__user_name")
                .IsUnique()
                .HasFilter("([user_name] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.Property(e => e.UserId)
                .HasDefaultValueSql("(newsequentialid())")
                .HasColumnName("user_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DefaultBranchId).HasColumnName("default_branch_id");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.Email)
                .HasMaxLength(256)
                .HasColumnName("email");
            entity.Property(e => e.EmailNormalised)
                .HasMaxLength(256)
                .HasColumnName("email_normalised");
            entity.Property(e => e.EmailVerifiedAt).HasColumnName("email_verified_at");
            entity.Property(e => e.UserName)
                .HasMaxLength(64)
                .HasColumnName("user_name");
            entity.Property(e => e.FailedLoginCount).HasColumnName("failed_login_count");
            entity.Property(e => e.FullName)
                .HasMaxLength(200)
                .HasColumnName("full_name");
            entity.Property(e => e.JobTitle)
                .HasMaxLength(100)
                .HasColumnName("job_title");
            entity.Property(e => e.LastLoginAt).HasColumnName("last_login_at");
            entity.Property(e => e.Locale)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("locale");
            entity.Property(e => e.LockedUntil).HasColumnName("locked_until");
            entity.Property(e => e.Phone)
                .HasMaxLength(30)
                .HasColumnName("phone");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("INVITED")
                .HasColumnName("status");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.Timezone)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("timezone");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.UserType)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("OPERATOR")
                .HasColumnName("user_type");
        });

        modelBuilder.Entity<UserBranch>(entity =>
        {
            entity.HasKey(e => e.UserBranchId).HasName("pk_user_branch");

            entity.ToTable("user_branch", "iam");

            entity.HasIndex(e => new { e.UserId, e.BranchId }, "uq_user_branch")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.UserBranchId)
                .HasDefaultValueSql("(newsequentialid())")
                .HasColumnName("user_branch_id");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UserId).HasColumnName("user_id");
        });

        modelBuilder.Entity<UserRole>(entity =>
        {
            entity.HasKey(e => e.UserRoleId).HasName("pk_user_role");

            entity.ToTable("user_role", "iam");

            entity.HasIndex(e => e.UserId, "ix_user_role__user").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.UserId, e.RoleId }, "uq_user_role__all_branches")
                .IsUnique()
                .HasFilter("([branch_id] IS NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.UserId, e.RoleId, e.BranchId }, "uq_user_role__branch_scoped")
                .IsUnique()
                .HasFilter("([branch_id] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.Property(e => e.UserRoleId)
                .HasDefaultValueSql("(newsequentialid())")
                .HasColumnName("user_role_id");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.RoleId).HasColumnName("role_id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UserId).HasColumnName("user_id");
        });

        modelBuilder.Entity<VwApiTokenUsable>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("vw_api_token_usable", "iam");

            entity.Property(e => e.ApiTokenId).HasColumnName("api_token_id");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
            entity.Property(e => e.LastUsedAt).HasColumnName("last_used_at");
            entity.Property(e => e.ModuleCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("module_code");
            entity.Property(e => e.Name)
                .HasMaxLength(100)
                .HasColumnName("name");
            entity.Property(e => e.ScopesJson).HasColumnName("scopes_json");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TokenHash)
                .HasMaxLength(32)
                .IsFixedLength()
                .HasColumnName("token_hash");
            entity.Property(e => e.TokenPrefix)
                .HasMaxLength(12)
                .IsUnicode(false)
                .HasColumnName("token_prefix");
        });
        modelBuilder.HasSequence("seq_invoice_number", "subscription");

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
