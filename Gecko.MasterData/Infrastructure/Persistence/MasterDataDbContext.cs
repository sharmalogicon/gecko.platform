using System;
using System.Collections.Generic;
using Gecko.MasterData.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Gecko.MasterData.Infrastructure.Persistence;

public partial class MasterDataDbContext : DbContext
{
    public MasterDataDbContext(DbContextOptions<MasterDataDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<BillToRole> BillToRoles { get; set; }

    public virtual DbSet<BillingUnit> BillingUnits { get; set; }

    public virtual DbSet<BranchProfile> BranchProfiles { get; set; }

    public virtual DbSet<CargoClass> CargoClasses { get; set; }

    public virtual DbSet<ChargeCode> ChargeCodes { get; set; }

    public virtual DbSet<ChargeCodeVariant> ChargeCodeVariants { get; set; }

    public virtual DbSet<CodeListCategory> CodeListCategories { get; set; }

    public virtual DbSet<CodeListValue> CodeListValues { get; set; }

    public virtual DbSet<CodeListValue1> CodeListValues1 { get; set; }

    public virtual DbSet<CodeMapping> CodeMappings { get; set; }

    public virtual DbSet<Commodity> Commodities { get; set; }

    public virtual DbSet<Company> Companies { get; set; }

    public virtual DbSet<Component> Components { get; set; }

    public virtual DbSet<Contact> Contacts { get; set; }

    public virtual DbSet<Container> Containers { get; set; }

    public virtual DbSet<ContainerCondition> ContainerConditions { get; set; }

    public virtual DbSet<ContainerGrade> ContainerGrades { get; set; }

    public virtual DbSet<ContainerPrefix> ContainerPrefixes { get; set; }

    public virtual DbSet<Country> Countries { get; set; }

    public virtual DbSet<Currency> Currencies { get; set; }

    public virtual DbSet<CustomerExtension> CustomerExtensions { get; set; }

    public virtual DbSet<CustomerTier> CustomerTiers { get; set; }

    public virtual DbSet<DamageCode> DamageCodes { get; set; }

    public virtual DbSet<DamageLocation> DamageLocations { get; set; }

    public virtual DbSet<DirectionType> DirectionTypes { get; set; }

    public virtual DbSet<DiscountType> DiscountTypes { get; set; }

    public virtual DbSet<DocumentType> DocumentTypes { get; set; }

    public virtual DbSet<EquipmentType> EquipmentTypes { get; set; }

    public virtual DbSet<EquipmentTypeIsoCode> EquipmentTypeIsoCodes { get; set; }

    public virtual DbSet<ForwarderExtension> ForwarderExtensions { get; set; }

    public virtual DbSet<GateHoursException> GateHoursExceptions { get; set; }

    public virtual DbSet<GateHoursWindow> GateHoursWindows { get; set; }

    public virtual DbSet<HaulierExtension> HaulierExtensions { get; set; }

    public virtual DbSet<Hold> Holds { get; set; }

    public virtual DbSet<ImdgClass> ImdgClasses { get; set; }

    public virtual DbSet<Incoterm> Incoterms { get; set; }

    public virtual DbSet<IsoContainerCode> IsoContainerCodes { get; set; }

    public virtual DbSet<IsoHeightCode> IsoHeightCodes { get; set; }

    public virtual DbSet<IsoLengthCode> IsoLengthCodes { get; set; }

    public virtual DbSet<IsoTypeCode> IsoTypeCodes { get; set; }

    public virtual DbSet<IsoTypeGroup> IsoTypeGroups { get; set; }

    public virtual DbSet<Location> Locations { get; set; }

    public virtual DbSet<Module> Modules { get; set; }

    public virtual DbSet<Movement> Movements { get; set; }

    public virtual DbSet<MovementCharge> MovementCharges { get; set; }

    public virtual DbSet<NumberSeries> NumberSeries { get; set; }

    public virtual DbSet<NumberSeriesCounter> NumberSeriesCounters { get; set; }

    public virtual DbSet<OrderType> OrderTypes { get; set; }

    public virtual DbSet<OrderTypeCharge> OrderTypeCharges { get; set; }

    public virtual DbSet<OrderTypeMovement> OrderTypeMovements { get; set; }

    public virtual DbSet<Party> Parties { get; set; }

    public virtual DbSet<PartyAlias> PartyAliases { get; set; }

    public virtual DbSet<PaymentTerm> PaymentTerms { get; set; }

    public virtual DbSet<Port> Ports { get; set; }

    public virtual DbSet<PublicHoliday> PublicHolidays { get; set; }

    public virtual DbSet<RepairCode> RepairCodes { get; set; }

    public virtual DbSet<SealRange> SealRanges { get; set; }

    public virtual DbSet<ServiceType> ServiceTypes { get; set; }

    public virtual DbSet<SettingDefinition> SettingDefinitions { get; set; }

    public virtual DbSet<ShippingLineExtension> ShippingLineExtensions { get; set; }

    public virtual DbSet<TaxCode> TaxCodes { get; set; }

    public virtual DbSet<TenantSetting> TenantSettings { get; set; }

    public virtual DbSet<Uom> Uoms { get; set; }

    public virtual DbSet<Vessel> Vessels { get; set; }

    public virtual DbSet<VwCodeList> VwCodeLists { get; set; }

    public virtual DbSet<VwIsoCodeResolution> VwIsoCodeResolutions { get; set; }

    public virtual DbSet<Yard> Yards { get; set; }

    public virtual DbSet<YardBlock> YardBlocks { get; set; }

    public virtual DbSet<YardRow> YardRows { get; set; }

    public virtual DbSet<YardSlot> YardSlots { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.UseCollation("Latin1_General_100_CI_AS_SC_UTF8");

        modelBuilder.Entity<BillToRole>(entity =>
        {
            entity.HasKey(e => e.Code).HasName("pk_bill_to_role");

            entity.ToTable("bill_to_role", "lookup");

            entity.HasIndex(e => e.LegacyVectorCode, "uq_bill_to_role__legacy")
                .IsUnique()
                .HasFilter("([legacy_vector_code] IS NOT NULL)");

            entity.Property(e => e.Code)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("code");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(200)
                .HasColumnName("description_en");
            entity.Property(e => e.DescriptionLocal)
                .HasMaxLength(200)
                .HasColumnName("description_local");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_bill_to_role__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.LegacyVectorCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("legacy_vector_code");
            entity.Property(e => e.SortOrder)
                .HasDefaultValue((short)100, "df_bill_to_role__sort")
                .HasColumnName("sort_order");
        });

        modelBuilder.Entity<BillingUnit>(entity =>
        {
            entity.HasKey(e => e.Code).HasName("pk_billing_unit");

            entity.ToTable("billing_unit", "lookup");

            entity.Property(e => e.Code)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("code");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(200)
                .HasColumnName("description_en");
            entity.Property(e => e.DescriptionLocal)
                .HasMaxLength(200)
                .HasColumnName("description_local");
            entity.Property(e => e.DisplayOrder).HasColumnName("display_order");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_billing_unit__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.IsTimeBased).HasColumnName("is_time_based");
            entity.Property(e => e.QuantitySource)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("quantity_source");
        });

        modelBuilder.Entity<BranchProfile>(entity =>
        {
            entity.HasKey(e => e.BranchId).HasName("pk_branch_profile");

            entity
                .ToTable("branch_profile", "org")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("org_branch_profile", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.BranchCode }, "uq_branch_profile__code")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.BranchId)
                .ValueGeneratedNever()
                .HasColumnName("branch_id");
            entity.Property(e => e.Address1)
                .HasMaxLength(255)
                .HasColumnName("address1");
            entity.Property(e => e.Address2)
                .HasMaxLength(255)
                .HasColumnName("address2");
            entity.Property(e => e.BranchCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("branch_code");
            entity.Property(e => e.City)
                .HasMaxLength(100)
                .HasColumnName("city");
            entity.Property(e => e.CompanyId).HasColumnName("company_id");
            entity.Property(e => e.CountryCode)
                .HasMaxLength(2)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("country_code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_branch_profile__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CustomsOfficeCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("customs_office_code");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.EdiLocationCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("edi_location_code");
            entity.Property(e => e.Email)
                .HasMaxLength(255)
                .IsUnicode(false)
                .HasColumnName("email");
            entity.Property(e => e.Latitude)
                .HasColumnType("decimal(9, 6)")
                .HasColumnName("latitude");
            entity.Property(e => e.Longitude)
                .HasColumnType("decimal(9, 6)")
                .HasColumnName("longitude");
            entity.Property(e => e.NameLocal)
                .HasMaxLength(200)
                .HasColumnName("name_local");
            entity.Property(e => e.Phone)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("phone");
            entity.Property(e => e.Postcode)
                .HasMaxLength(25)
                .IsUnicode(false)
                .HasColumnName("postcode");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.State)
                .HasMaxLength(100)
                .HasColumnName("state");
            entity.Property(e => e.SyncedFromIdentityAt).HasColumnName("synced_from_identity_at");
            entity.Property(e => e.TaxBranchCode)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("tax_branch_code");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.Timezone)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("timezone");
            entity.Property(e => e.UnLocode)
                .HasMaxLength(5)
                .IsUnicode(false)
                .HasColumnName("un_locode");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_branch_profile__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<CargoClass>(entity =>
        {
            entity.HasKey(e => e.Code).HasName("pk_cargo_class");

            entity.ToTable("cargo_class", "lookup");

            entity.Property(e => e.Code)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("code");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(200)
                .HasColumnName("description_en");
            entity.Property(e => e.DescriptionLocal)
                .HasMaxLength(200)
                .HasColumnName("description_local");
            entity.Property(e => e.DisplayOrder).HasColumnName("display_order");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_cargo_class__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.IsEmptyRepositioning).HasColumnName("is_empty_repositioning");
            entity.Property(e => e.RequiresImdgHandling).HasColumnName("requires_imdg_handling");
            entity.Property(e => e.RequiresOogHandling).HasColumnName("requires_oog_handling");
            entity.Property(e => e.RequiresTempControl).HasColumnName("requires_temp_control");
        });

        modelBuilder.Entity<ChargeCode>(entity =>
        {
            entity.HasKey(e => e.ChargeCodeId).HasName("pk_charge_code");

            entity
                .ToTable("charge_code", "commercial")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("commercial_charge_code", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.ChargeCode1 }, "uq_charge_code__code")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.ChargeCodeId)
                .HasDefaultValueSql("(newsequentialid())", "df_charge_code__id")
                .HasColumnName("charge_code_id");
            entity.Property(e => e.BillingUnitCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("billing_unit_code");
            entity.Property(e => e.ChargeCategory)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasDefaultValue("GENERAL", "df_charge_code__category")
                .HasColumnName("charge_category");
            entity.Property(e => e.ChargeCode1)
                .HasMaxLength(15)
                .IsUnicode(false)
                .HasColumnName("charge_code");
            entity.Property(e => e.ChargeType)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("charge_type");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_charge_code__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(200)
                .HasColumnName("description_en");
            entity.Property(e => e.DescriptionLocal)
                .HasMaxLength(200)
                .HasColumnName("description_local");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_charge_code__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.IsByService).HasColumnName("is_by_service");
            entity.Property(e => e.ModuleCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("module_code");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_charge_code__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<ChargeCodeVariant>(entity =>
        {
            entity.HasKey(e => e.ChargeCodeVariantId).HasName("pk_charge_code_variant");

            entity
                .ToTable("charge_code_variant", "commercial")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("commercial_charge_code_variant", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.LegacyChargeCode }, "uq_charge_code_variant__legacy")
                .IsUnique()
                .HasFilter("([legacy_charge_code] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.ChargeCodeId, e.BillTo, e.PaymentTermCode }, "uq_charge_code_variant__matrix")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.ChargeCodeVariantId)
                .HasDefaultValueSql("(newsequentialid())", "df_charge_code_variant__id")
                .HasColumnName("charge_code_variant_id");
            entity.Property(e => e.BillTo)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("bill_to");
            entity.Property(e => e.ChargeCodeId).HasColumnName("charge_code_id");
            entity.Property(e => e.CostGl)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("cost_gl");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_charge_code_variant__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CreditTermDays).HasColumnName("credit_term_days");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_charge_code_variant__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.LegacyChargeCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("legacy_charge_code");
            entity.Property(e => e.PaymentTermCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("payment_term_code");
            entity.Property(e => e.RevenueGl)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("revenue_gl");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TaxCodeId).HasColumnName("tax_code_id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_charge_code_variant__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.WithholdingTaxCodeId).HasColumnName("withholding_tax_code_id");
        });

        modelBuilder.Entity<CodeListCategory>(entity =>
        {
            entity.HasKey(e => e.CategoryCode).HasName("pk_code_list_category");

            entity.ToTable("code_list_category", "lookup");

            entity.Property(e => e.CategoryCode)
                .HasMaxLength(40)
                .IsUnicode(false)
                .HasColumnName("category_code");
            entity.Property(e => e.AllowsTenantValues).HasColumnName("allows_tenant_values");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(200)
                .HasColumnName("description_en");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_code_list_category__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.LegacyVectorCategory)
                .HasMaxLength(40)
                .IsUnicode(false)
                .HasColumnName("legacy_vector_category");
            entity.Property(e => e.OwningModule)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("owning_module");
        });

        modelBuilder.Entity<CodeListValue>(entity =>
        {
            entity.HasKey(e => e.CodeListValueId).HasName("pk_config_code_list_value");

            entity
                .ToTable("code_list_value", "config")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("config_code_list_value", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.CategoryCode, e.Code }, "uq_config_code_list_value__code")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.CodeListValueId)
                .HasDefaultValueSql("(newsequentialid())", "df_config_code_list_value__id")
                .HasColumnName("code_list_value_id");
            entity.Property(e => e.CategoryCode)
                .HasMaxLength(40)
                .IsUnicode(false)
                .HasColumnName("category_code");
            entity.Property(e => e.Code)
                .HasMaxLength(40)
                .IsUnicode(false)
                .HasColumnName("code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_config_code_list_value__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(200)
                .HasColumnName("description_en");
            entity.Property(e => e.DescriptionLocal)
                .HasMaxLength(200)
                .HasColumnName("description_local");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_config_code_list_value__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.IsoCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("iso_code");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.SortOrder)
                .HasDefaultValue((short)100, "df_config_code_list_value__sort")
                .HasColumnName("sort_order");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_config_code_list_value__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<CodeListValue1>(entity =>
        {
            entity.HasKey(e => new { e.CategoryCode, e.Code }).HasName("pk_code_list_value");

            entity.ToTable("code_list_value", "lookup");

            entity.Property(e => e.CategoryCode)
                .HasMaxLength(40)
                .IsUnicode(false)
                .HasColumnName("category_code");
            entity.Property(e => e.Code)
                .HasMaxLength(40)
                .IsUnicode(false)
                .HasColumnName("code");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(200)
                .HasColumnName("description_en");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_code_list_value__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.IsoCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("iso_code");
            entity.Property(e => e.SortOrder)
                .HasDefaultValue((short)100, "df_code_list_value__sort")
                .HasColumnName("sort_order");
        });

        modelBuilder.Entity<CodeMapping>(entity =>
        {
            entity.HasKey(e => e.CodeMappingId).HasName("pk_code_mapping");

            entity
                .ToTable("code_mapping", "config")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("config_code_mapping", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.MappingType, e.CodeListCategory, e.Channel, e.ExternalCode }, "uq_code_mapping__inbound_default")
                .IsUnique()
                .HasFilter("(([direction] IN ('INBOUND', 'BOTH')) AND [party_id] IS NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.MappingType, e.CodeListCategory, e.PartyId, e.Channel, e.ExternalCode }, "uq_code_mapping__inbound_partner")
                .IsUnique()
                .HasFilter("(([direction] IN ('INBOUND', 'BOTH')) AND [party_id] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.MappingType, e.CodeListCategory, e.Channel, e.InternalCode }, "uq_code_mapping__outbound_default")
                .IsUnique()
                .HasFilter("(([direction] IN ('OUTBOUND', 'BOTH')) AND [party_id] IS NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.MappingType, e.CodeListCategory, e.PartyId, e.Channel, e.InternalCode }, "uq_code_mapping__outbound_partner")
                .IsUnique()
                .HasFilter("(([direction] IN ('OUTBOUND', 'BOTH')) AND [party_id] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.Property(e => e.CodeMappingId)
                .HasDefaultValueSql("(newsequentialid())", "df_code_mapping__id")
                .HasColumnName("code_mapping_id");
            entity.Property(e => e.Channel)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("ANY", "df_code_mapping__channel")
                .HasColumnName("channel");
            entity.Property(e => e.CodeListCategory)
                .HasMaxLength(40)
                .IsUnicode(false)
                .HasColumnName("code_list_category");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_code_mapping__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.Description)
                .HasMaxLength(200)
                .HasColumnName("description");
            entity.Property(e => e.Direction)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("direction");
            entity.Property(e => e.ExternalCode)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("external_code");
            entity.Property(e => e.InternalCode)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("internal_code");
            entity.Property(e => e.InternalId).HasColumnName("internal_id");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_code_mapping__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.MappingType)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("mapping_type");
            entity.Property(e => e.PartyId).HasColumnName("party_id");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_code_mapping__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.ValidFrom).HasColumnName("valid_from");
            entity.Property(e => e.ValidTo).HasColumnName("valid_to");
        });

        modelBuilder.Entity<Commodity>(entity =>
        {
            entity.HasKey(e => e.CommodityId).HasName("pk_commodity");

            entity
                .ToTable("commodity", "logistics")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("logistics_commodity", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.HsCode }, "ix_commodity__hs").HasFilter("([hs_code] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.CommodityCode }, "uq_commodity__code")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.CommodityId)
                .HasDefaultValueSql("(newsequentialid())", "df_commodity__id")
                .HasColumnName("commodity_id");
            entity.Property(e => e.CommodityCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("commodity_code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_commodity__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DefaultMaxTempC)
                .HasColumnType("decimal(5, 2)")
                .HasColumnName("default_max_temp_c");
            entity.Property(e => e.DefaultMinTempC)
                .HasColumnType("decimal(5, 2)")
                .HasColumnName("default_min_temp_c");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(255)
                .HasColumnName("description_en");
            entity.Property(e => e.DescriptionLocal)
                .HasMaxLength(255)
                .HasColumnName("description_local");
            entity.Property(e => e.HsCode)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("hs_code");
            entity.Property(e => e.ImdgClassCode)
                .HasMaxLength(4)
                .IsUnicode(false)
                .HasColumnName("imdg_class_code");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_commodity__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.IsDangerous).HasColumnName("is_dangerous");
            entity.Property(e => e.IsTemperatureControlled).HasColumnName("is_temperature_controlled");
            entity.Property(e => e.LegacyImoCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("legacy_imo_code");
            entity.Property(e => e.PackingGroup)
                .HasMaxLength(3)
                .IsUnicode(false)
                .HasColumnName("packing_group");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UnNumber)
                .HasMaxLength(4)
                .IsUnicode(false)
                .HasColumnName("un_number");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_commodity__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<Company>(entity =>
        {
            entity.HasKey(e => e.CompanyId).HasName("pk_company");

            entity
                .ToTable("company", "org")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("org_company", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.CompanyCode }, "uq_company__code")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.CompanyId)
                .HasDefaultValueSql("(newsequentialid())", "df_company__id")
                .HasColumnName("company_id");
            entity.Property(e => e.Address1)
                .HasMaxLength(255)
                .HasColumnName("address1");
            entity.Property(e => e.Address2)
                .HasMaxLength(255)
                .HasColumnName("address2");
            entity.Property(e => e.City)
                .HasMaxLength(100)
                .HasColumnName("city");
            entity.Property(e => e.CompanyCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("company_code");
            entity.Property(e => e.CountryCode)
                .HasMaxLength(2)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("country_code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_company__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DefaultCurrency)
                .HasMaxLength(3)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("default_currency");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.Email)
                .HasMaxLength(255)
                .IsUnicode(false)
                .HasColumnName("email");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_company__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.NameEn)
                .HasMaxLength(255)
                .HasColumnName("name_en");
            entity.Property(e => e.NameLocal)
                .HasMaxLength(255)
                .HasColumnName("name_local");
            entity.Property(e => e.Phone)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("phone");
            entity.Property(e => e.Postcode)
                .HasMaxLength(25)
                .IsUnicode(false)
                .HasColumnName("postcode");
            entity.Property(e => e.RegistrationNo)
                .HasMaxLength(100)
                .HasColumnName("registration_no");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.ShortName)
                .HasMaxLength(50)
                .HasColumnName("short_name");
            entity.Property(e => e.State)
                .HasMaxLength(100)
                .HasColumnName("state");
            entity.Property(e => e.TaxBranchCode)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("tax_branch_code");
            entity.Property(e => e.TaxId)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("tax_id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_company__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<Component>(entity =>
        {
            entity.HasKey(e => e.ComponentId).HasName("pk_component");

            entity
                .ToTable("component", "equipment")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("equipment_component", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.CodeStandard, e.ComponentCode }, "uq_component__code")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.ComponentId)
                .HasDefaultValueSql("(newsequentialid())", "df_component__id")
                .HasColumnName("component_id");
            entity.Property(e => e.BaseUomCode)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("base_uom_code");
            entity.Property(e => e.CodeStandard)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasDefaultValue("LOCAL", "df_component__standard")
                .HasColumnName("code_standard");
            entity.Property(e => e.ComponentCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("component_code");
            entity.Property(e => e.ComponentGroup)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("component_group");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_component__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(200)
                .HasColumnName("description_en");
            entity.Property(e => e.DescriptionLocal)
                .HasMaxLength(200)
                .HasColumnName("description_local");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_component__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.IsOwnPart).HasColumnName("is_own_part");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_component__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<Contact>(entity =>
        {
            entity.HasKey(e => e.ContactId).HasName("pk_contact");

            entity
                .ToTable("contact", "party")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("party_contact", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.BranchId }, "ix_contact__branch").HasFilter("([branch_id] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.PartyId }, "ix_contact__party").HasFilter("([party_id] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.PortId }, "ix_contact__port").HasFilter("([port_id] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.VesselId }, "ix_contact__vessel").HasFilter("([vessel_id] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.PartyId, e.ContactType }, "uq_contact__default_party")
                .IsUnique()
                .HasFilter("([is_default]=(1) AND [party_id] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.Property(e => e.ContactId)
                .HasDefaultValueSql("(newsequentialid())", "df_contact__id")
                .HasColumnName("contact_id");
            entity.Property(e => e.Address1)
                .HasMaxLength(255)
                .HasColumnName("address1");
            entity.Property(e => e.Address2)
                .HasMaxLength(255)
                .HasColumnName("address2");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.City)
                .HasMaxLength(100)
                .HasColumnName("city");
            entity.Property(e => e.ContactPerson)
                .HasMaxLength(150)
                .HasColumnName("contact_person");
            entity.Property(e => e.ContactType)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("contact_type");
            entity.Property(e => e.CountryCode)
                .HasMaxLength(2)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("country_code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_contact__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.Email)
                .HasMaxLength(255)
                .IsUnicode(false)
                .HasColumnName("email");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_contact__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.IsDefault).HasColumnName("is_default");
            entity.Property(e => e.JobTitle)
                .HasMaxLength(100)
                .HasColumnName("job_title");
            entity.Property(e => e.LineUserId)
                .HasMaxLength(64)
                .IsUnicode(false)
                .HasColumnName("line_user_id");
            entity.Property(e => e.Mobile)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("mobile");
            entity.Property(e => e.PartyId).HasColumnName("party_id");
            entity.Property(e => e.Phone)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("phone");
            entity.Property(e => e.PortId).HasColumnName("port_id");
            entity.Property(e => e.Postcode)
                .HasMaxLength(25)
                .IsUnicode(false)
                .HasColumnName("postcode");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.State)
                .HasMaxLength(100)
                .HasColumnName("state");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_contact__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.VesselId).HasColumnName("vessel_id");
        });

        modelBuilder.Entity<Container>(entity =>
        {
            entity.HasKey(e => e.ContainerId).HasName("pk_container");

            entity
                .ToTable("container", "equipment")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("equipment_container", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.OwnerPartyId }, "ix_container__owner").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.ContainerNo }, "uq_container__number")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.ContainerId)
                .HasDefaultValueSql("(newsequentialid())", "df_container__id")
                .HasColumnName("container_id");
            entity.Property(e => e.AcepRef)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("acep_ref");
            entity.Property(e => e.ContainerNo)
                .HasMaxLength(11)
                .IsUnicode(false)
                .HasColumnName("container_no");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_container__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CscPlateRef)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("csc_plate_ref");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.EquipmentTypeId).HasColumnName("equipment_type_id");
            entity.Property(e => e.IsCheckDigitValid).HasColumnName("is_check_digit_valid");
            entity.Property(e => e.IsoCode)
                .HasMaxLength(4)
                .IsUnicode(false)
                .HasColumnName("iso_code");
            entity.Property(e => e.LessorPartyId).HasColumnName("lessor_party_id");
            entity.Property(e => e.ManufactureDate).HasColumnName("manufacture_date");
            entity.Property(e => e.Manufacturer)
                .HasMaxLength(100)
                .HasColumnName("manufacturer");
            entity.Property(e => e.Material)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("material");
            entity.Property(e => e.MaxGrossKg)
                .HasColumnType("decimal(10, 2)")
                .HasColumnName("max_gross_kg");
            entity.Property(e => e.NextExaminationDate).HasColumnName("next_examination_date");
            entity.Property(e => e.OwnerPartyId).HasColumnName("owner_party_id");
            entity.Property(e => e.OwnershipType)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("LINE_OWNED", "df_container__ownership")
                .HasColumnName("ownership_type");
            entity.Property(e => e.ReeferUnitMake)
                .HasMaxLength(40)
                .IsUnicode(false)
                .HasColumnName("reefer_unit_make");
            entity.Property(e => e.ReeferUnitModel)
                .HasMaxLength(40)
                .IsUnicode(false)
                .HasColumnName("reefer_unit_model");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("IN_SERVICE", "df_container__status")
                .HasColumnName("status");
            entity.Property(e => e.StatusChangedAt).HasColumnName("status_changed_at");
            entity.Property(e => e.TareWeightKg)
                .HasColumnType("decimal(10, 2)")
                .HasColumnName("tare_weight_kg");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_container__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<ContainerCondition>(entity =>
        {
            entity.HasKey(e => e.ContainerConditionId).HasName("pk_container_condition");

            entity
                .ToTable("container_condition", "equipment")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("equipment_container_condition", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.ConditionCode }, "uq_container_condition__code")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.ContainerConditionId)
                .HasDefaultValueSql("(newsequentialid())", "df_container_condition__id")
                .HasColumnName("container_condition_id");
            entity.Property(e => e.CodecoDamageFlag).HasColumnName("codeco_damage_flag");
            entity.Property(e => e.ConditionCode)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("condition_code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_container_condition__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(100)
                .HasColumnName("description_en");
            entity.Property(e => e.DescriptionLocal)
                .HasMaxLength(100)
                .HasColumnName("description_local");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_container_condition__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.IsServiceable)
                .HasDefaultValue(true, "df_container_condition__serviceable")
                .HasColumnName("is_serviceable");
            entity.Property(e => e.RequiresRepair).HasColumnName("requires_repair");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.Severity)
                .HasDefaultValue((byte)5, "df_container_condition__severity")
                .HasColumnName("severity");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_container_condition__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<ContainerGrade>(entity =>
        {
            entity.HasKey(e => e.ContainerGradeId).HasName("pk_container_grade");

            entity
                .ToTable("container_grade", "equipment")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("equipment_container_grade", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.GradeCode }, "uq_container_grade__code")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.ContainerGradeId)
                .HasDefaultValueSql("(newsequentialid())", "df_container_grade__id")
                .HasColumnName("container_grade_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_container_grade__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(200)
                .HasColumnName("description_en");
            entity.Property(e => e.DescriptionLocal)
                .HasMaxLength(200)
                .HasColumnName("description_local");
            entity.Property(e => e.GradeCode)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("grade_code");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_container_grade__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.IsFoodGrade).HasColumnName("is_food_grade");
            entity.Property(e => e.IsReleasable)
                .HasDefaultValue(true, "df_container_grade__releasable")
                .HasColumnName("is_releasable");
            entity.Property(e => e.RankOrder)
                .HasDefaultValue((short)100, "df_container_grade__rank")
                .HasColumnName("rank_order");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_container_grade__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<ContainerPrefix>(entity =>
        {
            entity.HasKey(e => e.ContainerPrefixId).HasName("pk_container_prefix");

            entity
                .ToTable("container_prefix", "party")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("party_container_prefix", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.PartyId }, "uq_container_prefix__one_primary")
                .IsUnique()
                .HasFilter("([is_primary]=(1) AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.Prefix }, "uq_container_prefix__prefix")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.ContainerPrefixId)
                .HasDefaultValueSql("(newsequentialid())", "df_container_prefix__id")
                .HasColumnName("container_prefix_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_container_prefix__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.IsPrimary).HasColumnName("is_primary");
            entity.Property(e => e.OwnershipType)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("LINE_OWNED", "df_container_prefix__ownership")
                .HasColumnName("ownership_type");
            entity.Property(e => e.PartyId).HasColumnName("party_id");
            entity.Property(e => e.Prefix)
                .HasMaxLength(4)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("prefix");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_container_prefix__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<Country>(entity =>
        {
            entity.HasKey(e => e.CountryCode).HasName("pk_country");

            entity.ToTable("country", "lookup");

            entity.HasIndex(e => e.Iso3Code, "uq_country__iso3").IsUnique();

            entity.Property(e => e.CountryCode)
                .HasMaxLength(2)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("country_code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_country__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.DefaultCurrency)
                .HasMaxLength(3)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("default_currency");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_country__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.Iso3Code)
                .HasMaxLength(3)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("iso3_code");
            entity.Property(e => e.NameEn)
                .HasMaxLength(100)
                .HasColumnName("name_en");
            entity.Property(e => e.NameLocal)
                .HasMaxLength(100)
                .HasColumnName("name_local");
            entity.Property(e => e.NumericCode)
                .HasMaxLength(3)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("numeric_code");
            entity.Property(e => e.OfficialNameEn)
                .HasMaxLength(200)
                .HasColumnName("official_name_en");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_country__updated_at")
                .HasColumnName("updated_at");
        });

        modelBuilder.Entity<Currency>(entity =>
        {
            entity.HasKey(e => e.CurrencyCode).HasName("pk_currency");

            entity.ToTable("currency", "lookup");

            entity.Property(e => e.CurrencyCode)
                .HasMaxLength(3)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("currency_code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_currency__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_currency__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.MinorUnits).HasColumnName("minor_units");
            entity.Property(e => e.NameEn)
                .HasMaxLength(100)
                .HasColumnName("name_en");
            entity.Property(e => e.NumericCode)
                .HasMaxLength(3)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("numeric_code");
            entity.Property(e => e.Symbol)
                .HasMaxLength(10)
                .HasColumnName("symbol");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_currency__updated_at")
                .HasColumnName("updated_at");
        });

        modelBuilder.Entity<CustomerExtension>(entity =>
        {
            entity.HasKey(e => e.PartyId).HasName("pk_customer_extension");

            entity
                .ToTable("customer_extension", "party")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("party_customer_extension", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.DebtorCode }, "ix_customer_ext__debtor").HasFilter("([debtor_code] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.Property(e => e.PartyId)
                .ValueGeneratedNever()
                .HasColumnName("party_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_customer_ext__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CreditLimit)
                .HasColumnType("decimal(19, 4)")
                .HasColumnName("credit_limit");
            entity.Property(e => e.CreditLimitCurrency)
                .HasMaxLength(3)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("credit_limit_currency");
            entity.Property(e => e.CreditTermDays).HasColumnName("credit_term_days");
            entity.Property(e => e.DebtorCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("debtor_code");
            entity.Property(e => e.DefaultPaymentTerm)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("CASH", "df_customer_ext__payment")
                .HasColumnName("default_payment_term");
            entity.Property(e => e.DefaultTariffRef).HasColumnName("default_tariff_ref");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.IsBilling)
                .HasDefaultValue(true, "df_customer_ext__billing")
                .HasColumnName("is_billing");
            entity.Property(e => e.IsConsignee).HasColumnName("is_consignee");
            entity.Property(e => e.IsShipper).HasColumnName("is_shipper");
            entity.Property(e => e.IsVatRegistered)
                .HasDefaultValue(true, "df_customer_ext__vat")
                .HasColumnName("is_vat_registered");
            entity.Property(e => e.LongStandingThresholdDays).HasColumnName("long_standing_threshold_days");
            entity.Property(e => e.RevenueAccount)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("revenue_account");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TierCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("STANDARD", "df_customer_ext__tier")
                .HasColumnName("tier_code");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_customer_ext__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.WithholdingTaxCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("withholding_tax_code");
        });

        modelBuilder.Entity<CustomerTier>(entity =>
        {
            entity.HasKey(e => e.Code).HasName("pk_customer_tier");

            entity.ToTable("customer_tier", "lookup");

            entity.Property(e => e.Code)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("code");
            entity.Property(e => e.DefaultCreditTermDays).HasColumnName("default_credit_term_days");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(200)
                .HasColumnName("description_en");
            entity.Property(e => e.DescriptionLocal)
                .HasMaxLength(200)
                .HasColumnName("description_local");
            entity.Property(e => e.DisplayColorHex)
                .HasMaxLength(7)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("display_color_hex");
            entity.Property(e => e.DisplayOrder).HasColumnName("display_order");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_customer_tier__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.Priority).HasColumnName("priority");
        });

        modelBuilder.Entity<DamageCode>(entity =>
        {
            entity.HasKey(e => e.DamageCodeId).HasName("pk_damage_code");

            entity
                .ToTable("damage_code", "equipment")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("equipment_damage_code", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.CodeStandard, e.DamageCode1 }, "uq_damage_code__code")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.DamageCodeId)
                .HasDefaultValueSql("(newsequentialid())", "df_damage_code__id")
                .HasColumnName("damage_code_id");
            entity.Property(e => e.CodeStandard)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasDefaultValue("LOCAL", "df_damage_code__standard")
                .HasColumnName("code_standard");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_damage_code__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DamageCode1)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("damage_code");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(200)
                .HasColumnName("description_en");
            entity.Property(e => e.DescriptionLocal)
                .HasMaxLength(200)
                .HasColumnName("description_local");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_damage_code__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.MakesUnserviceable).HasColumnName("makes_unserviceable");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.Severity)
                .HasDefaultValue((byte)5, "df_damage_code__severity")
                .HasColumnName("severity");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_damage_code__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<DamageLocation>(entity =>
        {
            entity.HasKey(e => e.DamageLocationId).HasName("pk_damage_location");

            entity
                .ToTable("damage_location", "equipment")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("equipment_damage_location", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.CodeStandard, e.LocationCode }, "uq_damage_location__code")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.DamageLocationId)
                .HasDefaultValueSql("(newsequentialid())", "df_damage_location__id")
                .HasColumnName("damage_location_id");
            entity.Property(e => e.CodeStandard)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasDefaultValue("LOCAL", "df_damage_location__standard")
                .HasColumnName("code_standard");
            entity.Property(e => e.ContainerFace)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("container_face");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_damage_location__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(200)
                .HasColumnName("description_en");
            entity.Property(e => e.DescriptionLocal)
                .HasMaxLength(200)
                .HasColumnName("description_local");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_damage_location__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.LocationCode)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("location_code");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_damage_location__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<DirectionType>(entity =>
        {
            entity.HasKey(e => e.Code).HasName("pk_direction_type");

            entity.ToTable("direction_type", "lookup");

            entity.Property(e => e.Code)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("code");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(200)
                .HasColumnName("description_en");
            entity.Property(e => e.DescriptionLocal)
                .HasMaxLength(200)
                .HasColumnName("description_local");
            entity.Property(e => e.DisplayOrder).HasColumnName("display_order");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_direction_type__is_active")
                .HasColumnName("is_active");
        });

        modelBuilder.Entity<DiscountType>(entity =>
        {
            entity.HasKey(e => e.Code).HasName("pk_discount_type");

            entity.ToTable("discount_type", "lookup");

            entity.Property(e => e.Code)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("code");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(200)
                .HasColumnName("description_en");
            entity.Property(e => e.DisplayOrder).HasColumnName("display_order");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_discount_type__is_active")
                .HasColumnName("is_active");
        });

        modelBuilder.Entity<DocumentType>(entity =>
        {
            entity.HasKey(e => e.Code).HasName("pk_document_type");

            entity.ToTable("document_type", "lookup");

            entity.Property(e => e.Code)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("code");
            entity.Property(e => e.DefaultFormat)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("default_format");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(200)
                .HasColumnName("description_en");
            entity.Property(e => e.DescriptionLocal)
                .HasMaxLength(200)
                .HasColumnName("description_local");
            entity.Property(e => e.DisplayOrder).HasColumnName("display_order");
            entity.Property(e => e.DocumentCategory)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("document_category");
            entity.Property(e => e.EdiMessageType)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("edi_message_type");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_document_type__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.IsLegalDocument).HasColumnName("is_legal_document");
            entity.Property(e => e.RequiresSignature).HasColumnName("requires_signature");
            entity.Property(e => e.RetentionYears).HasColumnName("retention_years");
        });

        modelBuilder.Entity<EquipmentType>(entity =>
        {
            entity.HasKey(e => e.EquipmentTypeId).HasName("pk_equipment_type");

            entity
                .ToTable("equipment_type", "equipment")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("equipment_equipment_type", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.TypeCode }, "uq_equipment_type__code")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.EquipmentTypeId)
                .HasDefaultValueSql("(newsequentialid())", "df_equipment_type__id")
                .HasColumnName("equipment_type_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_equipment_type__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(200)
                .HasColumnName("description_en");
            entity.Property(e => e.DescriptionLocal)
                .HasMaxLength(200)
                .HasColumnName("description_local");
            entity.Property(e => e.DisplayColorHex)
                .HasMaxLength(7)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("display_color_hex");
            entity.Property(e => e.HeightClass)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("height_class");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_equipment_type__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.IsOog).HasColumnName("is_oog");
            entity.Property(e => e.IsReefer).HasColumnName("is_reefer");
            entity.Property(e => e.IsTank).HasColumnName("is_tank");
            entity.Property(e => e.IsoGroupCode)
                .HasMaxLength(2)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("iso_group_code");
            entity.Property(e => e.LengthFt)
                .HasColumnType("decimal(4, 1)")
                .HasColumnName("length_ft");
            entity.Property(e => e.MaxGrossKg)
                .HasColumnType("decimal(10, 2)")
                .HasColumnName("max_gross_kg");
            entity.Property(e => e.MaxPayloadKg)
                .HasColumnType("decimal(10, 2)")
                .HasColumnName("max_payload_kg");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.SortOrder)
                .HasDefaultValue((short)100, "df_equipment_type__sort")
                .HasColumnName("sort_order");
            entity.Property(e => e.TareWeightKg)
                .HasColumnType("decimal(10, 2)")
                .HasColumnName("tare_weight_kg");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.Teu)
                .HasColumnType("decimal(4, 2)")
                .HasColumnName("teu");
            entity.Property(e => e.TypeCode)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("type_code");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_equipment_type__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<EquipmentTypeIsoCode>(entity =>
        {
            entity.HasKey(e => e.EquipmentTypeIsoCodeId).HasName("pk_equipment_type_iso_code");

            entity
                .ToTable("equipment_type_iso_code", "equipment")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("equipment_equipment_type_iso_code", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.EquipmentTypeId }, "ix_equipment_type_iso__type").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.IsoCode }, "uq_equipment_type_iso__iso")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.EquipmentTypeId }, "uq_equipment_type_iso__outbound")
                .IsUnique()
                .HasFilter("([is_default_outbound]=(1) AND [deleted_at] IS NULL)");

            entity.Property(e => e.EquipmentTypeIsoCodeId)
                .HasDefaultValueSql("(newsequentialid())", "df_equipment_type_iso__id")
                .HasColumnName("equipment_type_iso_code_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_equipment_type_iso__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.EquipmentTypeId).HasColumnName("equipment_type_id");
            entity.Property(e => e.IsDefaultOutbound).HasColumnName("is_default_outbound");
            entity.Property(e => e.IsoCode)
                .HasMaxLength(4)
                .IsUnicode(false)
                .HasColumnName("iso_code");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_equipment_type_iso__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<ForwarderExtension>(entity =>
        {
            entity.HasKey(e => e.PartyId).HasName("pk_forwarder_extension");

            entity
                .ToTable("forwarder_extension", "party")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("party_forwarder_extension", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.Property(e => e.PartyId)
                .ValueGeneratedNever()
                .HasColumnName("party_id");
            entity.Property(e => e.AeoCertificateNo)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("aeo_certificate_no");
            entity.Property(e => e.AeoCertified).HasColumnName("aeo_certified");
            entity.Property(e => e.AeoExpiryDate).HasColumnName("aeo_expiry_date");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_forwarder_ext__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CustomsLicenceNo)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("customs_licence_no");
            entity.Property(e => e.DefaultIncoterm)
                .HasMaxLength(3)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("default_incoterm");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.FiataMemberNo)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("fiata_member_no");
            entity.Property(e => e.IataCode)
                .HasMaxLength(3)
                .IsUnicode(false)
                .HasColumnName("iata_code");
            entity.Property(e => e.IsCustomsBroker).HasColumnName("is_customs_broker");
            entity.Property(e => e.PreferredCustomsBrokerPartyId).HasColumnName("preferred_customs_broker_party_id");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_forwarder_ext__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<GateHoursException>(entity =>
        {
            entity.HasKey(e => e.GateHoursExceptionId).HasName("pk_gate_hours_exception");

            entity
                .ToTable("gate_hours_exception", "org")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("org_gate_hours_exception", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.BranchId, e.ExceptionDate }, "uq_gate_hours_exception__date")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.GateHoursExceptionId)
                .HasDefaultValueSql("(newsequentialid())", "df_gate_hours_exception__id")
                .HasColumnName("gate_hours_exception_id");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.ClosesAt)
                .HasPrecision(0)
                .HasColumnName("closes_at");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_gate_hours_exception__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.ExceptionDate).HasColumnName("exception_date");
            entity.Property(e => e.IsClosed).HasColumnName("is_closed");
            entity.Property(e => e.OpensAt)
                .HasPrecision(0)
                .HasColumnName("opens_at");
            entity.Property(e => e.Reason)
                .HasMaxLength(150)
                .HasColumnName("reason");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_gate_hours_exception__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<GateHoursWindow>(entity =>
        {
            entity.HasKey(e => e.GateHoursWindowId).HasName("pk_gate_hours_window");

            entity
                .ToTable("gate_hours_window", "org")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("org_gate_hours_window", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.BranchId, e.IsoWeekday }, "ix_gate_hours_window__branch").HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.GateHoursWindowId)
                .HasDefaultValueSql("(newsequentialid())", "df_gate_hours_window__id")
                .HasColumnName("gate_hours_window_id");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.ClosesAt)
                .HasPrecision(0)
                .HasColumnName("closes_at");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_gate_hours_window__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.IsoWeekday).HasColumnName("iso_weekday");
            entity.Property(e => e.OpensAt)
                .HasPrecision(0)
                .HasColumnName("opens_at");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_gate_hours_window__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<HaulierExtension>(entity =>
        {
            entity.HasKey(e => e.PartyId).HasName("pk_haulier_extension");

            entity
                .ToTable("haulier_extension", "party")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("party_haulier_extension", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.Property(e => e.PartyId)
                .ValueGeneratedNever()
                .HasColumnName("party_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_haulier_ext__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DefaultRateCardRef).HasColumnName("default_rate_card_ref");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.FleetSize).HasColumnName("fleet_size");
            entity.Property(e => e.OwnsTrucks).HasColumnName("owns_trucks");
            entity.Property(e => e.PrimaryChassisSizeFt).HasColumnName("primary_chassis_size_ft");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TransportLicenceNo)
                .HasMaxLength(50)
                .HasColumnName("transport_licence_no");
            entity.Property(e => e.TruckingZoneCodes)
                .HasMaxLength(500)
                .HasColumnName("trucking_zone_codes");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_haulier_ext__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<Hold>(entity =>
        {
            entity.HasKey(e => e.HoldId).HasName("pk_hold");

            entity
                .ToTable("hold", "equipment")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("equipment_hold", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.HoldCode }, "uq_hold__code")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.HoldId)
                .HasDefaultValueSql("(newsequentialid())", "df_hold__id")
                .HasColumnName("hold_id");
            entity.Property(e => e.AutoApplyOnEvent)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("auto_apply_on_event");
            entity.Property(e => e.BlockingScope)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("blocking_scope");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_hold__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(200)
                .HasColumnName("description_en");
            entity.Property(e => e.DescriptionLocal)
                .HasMaxLength(200)
                .HasColumnName("description_local");
            entity.Property(e => e.DisplayColorHex)
                .HasMaxLength(7)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("display_color_hex");
            entity.Property(e => e.HoldCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("hold_code");
            entity.Property(e => e.HoldType)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("hold_type");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_hold__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.NotifyOnApply)
                .HasDefaultValue(true, "df_hold__notify")
                .HasColumnName("notify_on_apply");
            entity.Property(e => e.Priority)
                .HasDefaultValue((byte)5, "df_hold__priority")
                .HasColumnName("priority");
            entity.Property(e => e.ReleaseAuthority)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("release_authority");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_hold__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<ImdgClass>(entity =>
        {
            entity.HasKey(e => e.ClassCode).HasName("pk_imdg_class");

            entity.ToTable("imdg_class", "lookup");

            entity.Property(e => e.ClassCode)
                .HasMaxLength(4)
                .IsUnicode(false)
                .HasColumnName("class_code");
            entity.Property(e => e.ClassNo).HasColumnName("class_no");
            entity.Property(e => e.DisplayOrder).HasColumnName("display_order");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_imdg_class__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.NameEn)
                .HasMaxLength(150)
                .HasColumnName("name_en");
        });

        modelBuilder.Entity<Incoterm>(entity =>
        {
            entity.HasKey(e => e.Code).HasName("pk_incoterm");

            entity.ToTable("incoterm", "lookup");

            entity.Property(e => e.Code)
                .HasMaxLength(3)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("code");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(200)
                .HasColumnName("description_en");
            entity.Property(e => e.DescriptionLocal)
                .HasMaxLength(200)
                .HasColumnName("description_local");
            entity.Property(e => e.DisplayOrder).HasColumnName("display_order");
            entity.Property(e => e.Edition)
                .HasMaxLength(4)
                .IsUnicode(false)
                .IsFixedLength()
                .HasDefaultValue("2020", "df_incoterm__edition")
                .HasColumnName("edition");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_incoterm__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.SellerPaysInsurance).HasColumnName("seller_pays_insurance");
            entity.Property(e => e.SellerPaysMainCarriage).HasColumnName("seller_pays_main_carriage");
            entity.Property(e => e.TransportMode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("transport_mode");
        });

        modelBuilder.Entity<IsoContainerCode>(entity =>
        {
            entity.HasKey(e => e.IsoCode).HasName("pk_iso_container_code");

            entity.ToTable("iso_container_code", "lookup");

            entity.HasIndex(e => new { e.LengthFt, e.GroupCode, e.IsHighCube }, "ix_iso_container_code__shape");

            entity.Property(e => e.IsoCode)
                .HasMaxLength(4)
                .IsUnicode(false)
                .HasColumnName("iso_code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_iso_container_code__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(200)
                .HasColumnName("description_en");
            entity.Property(e => e.GroupCode)
                .HasMaxLength(2)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("group_code");
            entity.Property(e => e.HeightCode)
                .HasMaxLength(1)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("height_code");
            entity.Property(e => e.HeightMm).HasColumnName("height_mm");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_iso_container_code__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.IsHighCube).HasColumnName("is_high_cube");
            entity.Property(e => e.IsOpenTop).HasColumnName("is_open_top");
            entity.Property(e => e.IsPlatform).HasColumnName("is_platform");
            entity.Property(e => e.IsReefer).HasColumnName("is_reefer");
            entity.Property(e => e.IsTank).HasColumnName("is_tank");
            entity.Property(e => e.LengthCode)
                .HasMaxLength(1)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("length_code");
            entity.Property(e => e.LengthFt)
                .HasColumnType("decimal(4, 1)")
                .HasColumnName("length_ft");
            entity.Property(e => e.MappingNote)
                .HasMaxLength(300)
                .HasColumnName("mapping_note");
            entity.Property(e => e.Standard)
                .HasMaxLength(12)
                .IsUnicode(false)
                .HasColumnName("standard");
            entity.Property(e => e.SupersededBy)
                .HasMaxLength(4)
                .IsUnicode(false)
                .HasColumnName("superseded_by");
            entity.Property(e => e.Teu)
                .HasColumnType("decimal(4, 2)")
                .HasColumnName("teu");
            entity.Property(e => e.TypeCode)
                .HasMaxLength(2)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("type_code");
        });

        modelBuilder.Entity<IsoHeightCode>(entity =>
        {
            entity.HasKey(e => e.HeightCode).HasName("pk_iso_height_code");

            entity.ToTable("iso_height_code", "lookup");

            entity.Property(e => e.HeightCode)
                .HasMaxLength(1)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("height_code");
            entity.Property(e => e.HeightMm).HasColumnName("height_mm");
            entity.Property(e => e.IsHalfHeight).HasColumnName("is_half_height");
            entity.Property(e => e.IsHighCube).HasColumnName("is_high_cube");
            entity.Property(e => e.Label)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("label");
        });

        modelBuilder.Entity<IsoLengthCode>(entity =>
        {
            entity.HasKey(e => e.LengthCode).HasName("pk_iso_length_code");

            entity.ToTable("iso_length_code", "lookup");

            entity.Property(e => e.LengthCode)
                .HasMaxLength(1)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("length_code");
            entity.Property(e => e.Label)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("label");
            entity.Property(e => e.LengthFt)
                .HasColumnType("decimal(4, 1)")
                .HasColumnName("length_ft");
            entity.Property(e => e.LengthMm).HasColumnName("length_mm");
        });

        modelBuilder.Entity<IsoTypeCode>(entity =>
        {
            entity.HasKey(e => e.TypeCode).HasName("pk_iso_type_code");

            entity.ToTable("iso_type_code", "lookup");

            entity.Property(e => e.TypeCode)
                .HasMaxLength(2)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("type_code");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(200)
                .HasColumnName("description_en");
            entity.Property(e => e.GroupCode)
                .HasMaxLength(2)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("group_code");
        });

        modelBuilder.Entity<IsoTypeGroup>(entity =>
        {
            entity.HasKey(e => e.GroupCode).HasName("pk_iso_type_group");

            entity.ToTable("iso_type_group", "lookup");

            entity.Property(e => e.GroupCode)
                .HasMaxLength(2)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("group_code");
            entity.Property(e => e.DefaultCargoClass)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("default_cargo_class");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(100)
                .HasColumnName("description_en");
            entity.Property(e => e.IsDangerousCapable).HasColumnName("is_dangerous_capable");
            entity.Property(e => e.IsInsulated).HasColumnName("is_insulated");
            entity.Property(e => e.IsOpenTop).HasColumnName("is_open_top");
            entity.Property(e => e.IsPlatform).HasColumnName("is_platform");
            entity.Property(e => e.IsReefer).HasColumnName("is_reefer");
            entity.Property(e => e.IsTank).HasColumnName("is_tank");
        });

        modelBuilder.Entity<Location>(entity =>
        {
            entity.HasKey(e => e.LocationId).HasName("pk_location");

            entity
                .ToTable("location", "logistics")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("logistics_location", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.AreaCode }, "ix_location__area").HasFilter("([area_code] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.LocationCode }, "uq_location__code")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.LocationId)
                .HasDefaultValueSql("(newsequentialid())", "df_location__id")
                .HasColumnName("location_id");
            entity.Property(e => e.Address1)
                .HasMaxLength(255)
                .HasColumnName("address1");
            entity.Property(e => e.Address2)
                .HasMaxLength(255)
                .HasColumnName("address2");
            entity.Property(e => e.AreaCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("area_code");
            entity.Property(e => e.City)
                .HasMaxLength(100)
                .HasColumnName("city");
            entity.Property(e => e.CountryCode)
                .HasMaxLength(2)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("country_code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_location__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_location__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.Latitude)
                .HasColumnType("decimal(9, 6)")
                .HasColumnName("latitude");
            entity.Property(e => e.LocationCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("location_code");
            entity.Property(e => e.LocationNameEn)
                .HasMaxLength(255)
                .HasColumnName("location_name_en");
            entity.Property(e => e.LocationNameLocal)
                .HasMaxLength(255)
                .HasColumnName("location_name_local");
            entity.Property(e => e.LocationType)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("location_type");
            entity.Property(e => e.Longitude)
                .HasColumnType("decimal(9, 6)")
                .HasColumnName("longitude");
            entity.Property(e => e.PartyId).HasColumnName("party_id");
            entity.Property(e => e.Postcode)
                .HasMaxLength(25)
                .IsUnicode(false)
                .HasColumnName("postcode");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.State)
                .HasMaxLength(100)
                .HasColumnName("state");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_location__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<Module>(entity =>
        {
            entity.HasKey(e => e.ModuleCode).HasName("pk_module");

            entity.ToTable("module", "lookup");

            entity.Property(e => e.ModuleCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("module_code");
            entity.Property(e => e.ContextCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("context_code");
            entity.Property(e => e.Description)
                .HasMaxLength(400)
                .HasColumnName("description");
            entity.Property(e => e.DisplayName)
                .HasMaxLength(100)
                .HasColumnName("display_name");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_module__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.IsLicensable).HasColumnName("is_licensable");
            entity.Property(e => e.IsOperational).HasColumnName("is_operational");
            entity.Property(e => e.SortOrder)
                .HasDefaultValue((short)100, "df_module__sort")
                .HasColumnName("sort_order");
        });

        modelBuilder.Entity<Movement>(entity =>
        {
            entity.HasKey(e => e.MovementId).HasName("pk_movement");

            entity
                .ToTable("movement", "commercial")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("commercial_movement", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.MovementCode }, "uq_movement__code")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.MovementId)
                .HasDefaultValueSql("(newsequentialid())", "df_movement__id")
                .HasColumnName("movement_id");
            entity.Property(e => e.AppliesToModule)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("applies_to_module");
            entity.Property(e => e.ChangesStatus).HasColumnName("changes_status");
            entity.Property(e => e.ChangesYardPosition).HasColumnName("changes_yard_position");
            entity.Property(e => e.CodecoStatusCode)
                .HasMaxLength(3)
                .IsUnicode(false)
                .HasColumnName("codeco_status_code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_movement__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(100)
                .HasColumnName("description_en");
            entity.Property(e => e.DescriptionLocal)
                .HasMaxLength(100)
                .HasColumnName("description_local");
            entity.Property(e => e.Direction)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("direction");
            entity.Property(e => e.FullEmpty)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("full_empty");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_movement__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.MovementCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("movement_code");
            entity.Property(e => e.RequiresSurvey).HasColumnName("requires_survey");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_movement__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<MovementCharge>(entity =>
        {
            entity.HasKey(e => e.MovementChargeId).HasName("pk_movement_charge");

            entity
                .ToTable("movement_charge", "commercial")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("commercial_movement_charge", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.MovementId, e.ChargeCodeId }, "uq_movement_charge__pair")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.MovementChargeId)
                .HasDefaultValueSql("(newsequentialid())", "df_movement_charge__id")
                .HasColumnName("movement_charge_id");
            entity.Property(e => e.ChargeCodeId).HasColumnName("charge_code_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_movement_charge__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.IsDefault)
                .HasDefaultValue(true, "df_movement_charge__default")
                .HasColumnName("is_default");
            entity.Property(e => e.MovementId).HasColumnName("movement_id");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_movement_charge__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<NumberSeries>(entity =>
        {
            entity.HasKey(e => e.NumberSeriesId).HasName("pk_number_series");

            entity
                .ToTable("number_series", "config")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("config_number_series", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.BranchId, e.SeriesKey }, "uq_number_series__branch")
                .IsUnique()
                .HasFilter("([branch_id] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.SeriesKey }, "uq_number_series__tenant")
                .IsUnique()
                .HasFilter("([branch_id] IS NULL AND [deleted_at] IS NULL)");

            entity.Property(e => e.NumberSeriesId)
                .HasDefaultValueSql("(newsequentialid())", "df_number_series__id")
                .HasColumnName("number_series_id");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_number_series__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DatePartFormat)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasDefaultValue("NONE", "df_number_series__date_part")
                .HasColumnName("date_part_format");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.Description)
                .HasMaxLength(200)
                .HasColumnName("description");
            entity.Property(e => e.DocumentTypeCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("document_type_code");
            entity.Property(e => e.IncludeBranchCode).HasColumnName("include_branch_code");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_number_series__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.IsGapFreeRequired).HasColumnName("is_gap_free_required");
            entity.Property(e => e.NumberLength)
                .HasDefaultValue((byte)6, "df_number_series__length")
                .HasColumnName("number_length");
            entity.Property(e => e.Prefix)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("prefix");
            entity.Property(e => e.ResetPeriod)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasDefaultValue("NEVER", "df_number_series__reset")
                .HasColumnName("reset_period");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.Separator)
                .HasMaxLength(2)
                .IsUnicode(false)
                .HasDefaultValue("-", "df_number_series__separator")
                .HasColumnName("separator");
            entity.Property(e => e.SeriesKey)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("series_key");
            entity.Property(e => e.StartNumber)
                .HasDefaultValue(1L, "df_number_series__start")
                .HasColumnName("start_number");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_number_series__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<NumberSeriesCounter>(entity =>
        {
            entity.HasKey(e => new { e.NumberSeriesId, e.PeriodKey }).HasName("pk_number_series_counter");

            entity.ToTable("number_series_counter", "config");

            entity.Property(e => e.NumberSeriesId).HasColumnName("number_series_id");
            entity.Property(e => e.PeriodKey)
                .HasMaxLength(6)
                .IsUnicode(false)
                .HasColumnName("period_key");
            entity.Property(e => e.LastNumber).HasColumnName("last_number");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_number_series_counter__updated_at")
                .HasColumnName("updated_at");
        });

        modelBuilder.Entity<OrderType>(entity =>
        {
            entity.HasKey(e => e.OrderTypeId).HasName("pk_order_type");

            entity
                .ToTable("order_type", "commercial")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("commercial_order_type", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.OrderTypeCode }, "uq_order_type__code")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.OrderTypeId)
                .HasDefaultValueSql("(newsequentialid())", "df_order_type__id")
                .HasColumnName("order_type_id");
            entity.Property(e => e.BookingTypeCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("booking_type_code");
            entity.Property(e => e.CargoClassCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("cargo_class_code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_order_type__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(255)
                .HasColumnName("description_en");
            entity.Property(e => e.DescriptionLocal)
                .HasMaxLength(255)
                .HasColumnName("description_local");
            entity.Property(e => e.DirectionCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("direction_code");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_order_type__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.OrderTypeCode)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("order_type_code");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.ServiceTypeId).HasColumnName("service_type_id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_order_type__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<OrderTypeCharge>(entity =>
        {
            entity.HasKey(e => e.OrderTypeChargeId).HasName("pk_order_type_charge");

            entity
                .ToTable("order_type_charge", "commercial")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("commercial_order_type_charge", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.OrderTypeId, e.ChargeCodeId, e.MovementId, e.PaymentTo }, "uq_otc__movement")
                .IsUnique()
                .HasFilter("([movement_id] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.OrderTypeId, e.ChargeCodeId, e.PaymentTo }, "uq_otc__order_level")
                .IsUnique()
                .HasFilter("([movement_id] IS NULL AND [deleted_at] IS NULL)");

            entity.Property(e => e.OrderTypeChargeId)
                .HasDefaultValueSql("(newsequentialid())", "df_otc__id")
                .HasColumnName("order_type_charge_id");
            entity.Property(e => e.ChargeCodeId).HasColumnName("charge_code_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_otc__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DefaultQty)
                .HasColumnType("decimal(10, 2)")
                .HasColumnName("default_qty");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.IsCargoCharge).HasColumnName("is_cargo_charge");
            entity.Property(e => e.IsDefault)
                .HasDefaultValue(true, "df_otc__default")
                .HasColumnName("is_default");
            entity.Property(e => e.IsOptional).HasColumnName("is_optional");
            entity.Property(e => e.IsValueAddedService).HasColumnName("is_value_added_service");
            entity.Property(e => e.MovementId).HasColumnName("movement_id");
            entity.Property(e => e.OrderTypeId).HasColumnName("order_type_id");
            entity.Property(e => e.PaymentTermCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("payment_term_code");
            entity.Property(e => e.PaymentTo)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("CUSTOMER", "df_otc__payment_to")
                .HasColumnName("payment_to");
            entity.Property(e => e.RaiseAtGateIn).HasColumnName("raise_at_gate_in");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TransportMode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("transport_mode");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_otc__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<OrderTypeMovement>(entity =>
        {
            entity.HasKey(e => e.OrderTypeMovementId).HasName("pk_order_type_movement");

            entity
                .ToTable("order_type_movement", "commercial")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("commercial_order_type_movement", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.OrderTypeId, e.MovementId }, "uq_otm__movement")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.OrderTypeId, e.SequenceNo }, "uq_otm__sequence")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.OrderTypeMovementId)
                .HasDefaultValueSql("(newsequentialid())", "df_order_type_movement__id")
                .HasColumnName("order_type_movement_id");
            entity.Property(e => e.AllowDamagedRelease).HasColumnName("allow_damaged_release");
            entity.Property(e => e.CheckGrossWeight).HasColumnName("check_gross_weight");
            entity.Property(e => e.CheckSealNo).HasColumnName("check_seal_no");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_otm__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.IsBillable)
                .HasDefaultValue(true, "df_otm__billable")
                .HasColumnName("is_billable");
            entity.Property(e => e.IsRequired)
                .HasDefaultValue(true, "df_otm__required")
                .HasColumnName("is_required");
            entity.Property(e => e.MovementId).HasColumnName("movement_id");
            entity.Property(e => e.OrderTypeId).HasColumnName("order_type_id");
            entity.Property(e => e.PudoMode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("pudo_mode");
            entity.Property(e => e.RequireVesselVoyage).HasColumnName("require_vessel_voyage");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.SequenceNo).HasColumnName("sequence_no");
            entity.Property(e => e.SkipEdi).HasColumnName("skip_edi");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_otm__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<Party>(entity =>
        {
            entity.HasKey(e => e.PartyId).HasName("pk_party");

            entity
                .ToTable("party", "party")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("party_party", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.NameEn }, "ix_party__name").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.TaxId }, "ix_party__tax_id").HasFilter("([tax_id] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.PartyCode }, "uq_party__code")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.PartyId)
                .HasDefaultValueSql("(newsequentialid())", "df_party__id")
                .HasColumnName("party_id");
            entity.Property(e => e.CountryCode)
                .HasMaxLength(2)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("country_code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_party__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DefaultCurrency)
                .HasMaxLength(3)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("default_currency");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_party__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.NameEn)
                .HasMaxLength(255)
                .HasColumnName("name_en");
            entity.Property(e => e.NameLocal)
                .HasMaxLength(255)
                .HasColumnName("name_local");
            entity.Property(e => e.PartyCode)
                .HasMaxLength(25)
                .IsUnicode(false)
                .HasColumnName("party_code");
            entity.Property(e => e.PrimaryAddress1)
                .HasMaxLength(255)
                .HasColumnName("primary_address1");
            entity.Property(e => e.PrimaryAddress2)
                .HasMaxLength(255)
                .HasColumnName("primary_address2");
            entity.Property(e => e.PrimaryCity)
                .HasMaxLength(100)
                .HasColumnName("primary_city");
            entity.Property(e => e.PrimaryEmail)
                .HasMaxLength(255)
                .IsUnicode(false)
                .HasColumnName("primary_email");
            entity.Property(e => e.PrimaryPhone)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("primary_phone");
            entity.Property(e => e.PrimaryPostcode)
                .HasMaxLength(25)
                .IsUnicode(false)
                .HasColumnName("primary_postcode");
            entity.Property(e => e.PrimaryState)
                .HasMaxLength(100)
                .HasColumnName("primary_state");
            entity.Property(e => e.PrimaryWebsite)
                .HasMaxLength(500)
                .IsUnicode(false)
                .HasColumnName("primary_website");
            entity.Property(e => e.RegistrationNo)
                .HasMaxLength(100)
                .HasColumnName("registration_no");
            entity.Property(e => e.Remarks)
                .HasMaxLength(1000)
                .HasColumnName("remarks");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.ShortName)
                .HasMaxLength(60)
                .HasColumnName("short_name");
            entity.Property(e => e.TaxBranchCode)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("tax_branch_code");
            entity.Property(e => e.TaxId)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("tax_id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_party__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<PartyAlias>(entity =>
        {
            entity.HasKey(e => e.PartyAliasId).HasName("pk_party_alias");

            entity
                .ToTable("party_alias", "party")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("party_party_alias", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.AliasValue }, "ix_party_alias__lookup").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.PartyId }, "ix_party_alias__party").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.AliasType, e.AliasValue }, "uq_party_alias__value")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.PartyAliasId)
                .HasDefaultValueSql("(newsequentialid())", "df_party_alias__id")
                .HasColumnName("party_alias_id");
            entity.Property(e => e.AliasLabel)
                .HasMaxLength(100)
                .HasColumnName("alias_label");
            entity.Property(e => e.AliasType)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("alias_type");
            entity.Property(e => e.AliasValue)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("alias_value");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_party_alias__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.IsPrimaryForType).HasColumnName("is_primary_for_type");
            entity.Property(e => e.PartyId).HasColumnName("party_id");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_party_alias__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.ValidFrom).HasColumnName("valid_from");
            entity.Property(e => e.ValidTo).HasColumnName("valid_to");
        });

        modelBuilder.Entity<PaymentTerm>(entity =>
        {
            entity.HasKey(e => e.Code).HasName("pk_payment_term");

            entity.ToTable("payment_term", "lookup");

            entity.Property(e => e.Code)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("code");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(200)
                .HasColumnName("description_en");
            entity.Property(e => e.DescriptionLocal)
                .HasMaxLength(200)
                .HasColumnName("description_local");
            entity.Property(e => e.DisplayOrder).HasColumnName("display_order");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_payment_term__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.RequiresCreditAccount).HasColumnName("requires_credit_account");
            entity.Property(e => e.SettlesBeforeRelease).HasColumnName("settles_before_release");
        });

        modelBuilder.Entity<Port>(entity =>
        {
            entity.HasKey(e => e.PortId).HasName("pk_port");

            entity
                .ToTable("port", "logistics")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("logistics_port", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.PortCode }, "uq_port__code")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.UnLocode }, "uq_port__locode")
                .IsUnique()
                .HasFilter("([un_locode] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.Property(e => e.PortId)
                .HasDefaultValueSql("(newsequentialid())", "df_port__id")
                .HasColumnName("port_id");
            entity.Property(e => e.CountryCode)
                .HasMaxLength(2)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("country_code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_port__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.EdiMappingCode)
                .HasMaxLength(25)
                .IsUnicode(false)
                .HasColumnName("edi_mapping_code");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_port__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.Latitude)
                .HasColumnType("decimal(9, 6)")
                .HasColumnName("latitude");
            entity.Property(e => e.Longitude)
                .HasColumnType("decimal(9, 6)")
                .HasColumnName("longitude");
            entity.Property(e => e.PaperlessCode)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("paperless_code");
            entity.Property(e => e.PortCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("port_code");
            entity.Property(e => e.PortNameEn)
                .HasMaxLength(255)
                .HasColumnName("port_name_en");
            entity.Property(e => e.PortNameLocal)
                .HasMaxLength(255)
                .HasColumnName("port_name_local");
            entity.Property(e => e.PortType)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("port_type");
            entity.Property(e => e.Postcode)
                .HasMaxLength(25)
                .IsUnicode(false)
                .HasColumnName("postcode");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.Timezone)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("timezone");
            entity.Property(e => e.TradeMode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("INTERNATIONAL", "df_port__trade_mode")
                .HasColumnName("trade_mode");
            entity.Property(e => e.UnLocode)
                .HasMaxLength(5)
                .IsUnicode(false)
                .HasColumnName("un_locode");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_port__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<PublicHoliday>(entity =>
        {
            entity.HasKey(e => e.PublicHolidayId).HasName("pk_public_holiday");

            entity
                .ToTable("public_holiday", "org")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("org_public_holiday", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.BranchId, e.HolidayDate }, "uq_public_holiday__branch")
                .IsUnique()
                .HasFilter("([branch_id] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.HolidayDate }, "uq_public_holiday__tenant")
                .IsUnique()
                .HasFilter("([branch_id] IS NULL AND [deleted_at] IS NULL)");

            entity.Property(e => e.PublicHolidayId)
                .HasDefaultValueSql("(newsequentialid())", "df_public_holiday__id")
                .HasColumnName("public_holiday_id");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_public_holiday__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.HolidayDate).HasColumnName("holiday_date");
            entity.Property(e => e.IsHalfDay).HasColumnName("is_half_day");
            entity.Property(e => e.NameEn)
                .HasMaxLength(150)
                .HasColumnName("name_en");
            entity.Property(e => e.NameLocal)
                .HasMaxLength(150)
                .HasColumnName("name_local");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_public_holiday__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<RepairCode>(entity =>
        {
            entity.HasKey(e => e.RepairCodeId).HasName("pk_repair_code");

            entity
                .ToTable("repair_code", "equipment")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("equipment_repair_code", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.CodeStandard, e.RepairCode1 }, "uq_repair_code__code")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.RepairCodeId)
                .HasDefaultValueSql("(newsequentialid())", "df_repair_code__id")
                .HasColumnName("repair_code_id");
            entity.Property(e => e.CodeStandard)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasDefaultValue("LOCAL", "df_repair_code__standard")
                .HasColumnName("code_standard");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_repair_code__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DefaultUomCode)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("default_uom_code");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(200)
                .HasColumnName("description_en");
            entity.Property(e => e.DescriptionLocal)
                .HasMaxLength(200)
                .HasColumnName("description_local");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_repair_code__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.RepairCode1)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("repair_code");
            entity.Property(e => e.RepairGroup)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("repair_group");
            entity.Property(e => e.RepairMode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("repair_mode");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_repair_code__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<SealRange>(entity =>
        {
            entity.HasKey(e => e.SealRangeId).HasName("pk_seal_range");

            entity
                .ToTable("seal_range", "party")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("party_seal_range", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.SealPrefix, e.SeriesStart, e.SeriesEnd }, "ix_seal_range__lookup").HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.SealRangeId)
                .HasDefaultValueSql("(newsequentialid())", "df_seal_range__id")
                .HasColumnName("seal_range_id");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_seal_range__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_seal_range__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.LastIssuedNumber).HasColumnName("last_issued_number");
            entity.Property(e => e.NumberLength).HasColumnName("number_length");
            entity.Property(e => e.PartyId).HasColumnName("party_id");
            entity.Property(e => e.ReceivedOn).HasColumnName("received_on");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.SealPrefix)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasDefaultValue("", "df_seal_range__prefix")
                .HasColumnName("seal_prefix");
            entity.Property(e => e.SeriesEnd).HasColumnName("series_end");
            entity.Property(e => e.SeriesStart).HasColumnName("series_start");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_seal_range__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<ServiceType>(entity =>
        {
            entity.HasKey(e => e.ServiceTypeId).HasName("pk_service_type");

            entity
                .ToTable("service_type", "commercial")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("commercial_service_type", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.ServiceCode }, "uq_service_type__code")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.ServiceTypeId)
                .HasDefaultValueSql("(newsequentialid())", "df_service_type__id")
                .HasColumnName("service_type_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_service_type__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(200)
                .HasColumnName("description_en");
            entity.Property(e => e.DescriptionLocal)
                .HasMaxLength(200)
                .HasColumnName("description_local");
            entity.Property(e => e.DestinationForm)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("destination_form");
            entity.Property(e => e.DisplayOrder)
                .HasDefaultValue((short)100, "df_service_type__order")
                .HasColumnName("display_order");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_service_type__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.OriginForm)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("origin_form");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.ServiceCode)
                .HasMaxLength(15)
                .IsUnicode(false)
                .HasColumnName("service_code");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_service_type__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<SettingDefinition>(entity =>
        {
            entity.HasKey(e => e.SettingKey).HasName("pk_setting_definition");

            entity.ToTable("setting_definition", "lookup");

            entity.Property(e => e.SettingKey)
                .HasMaxLength(100)
                .IsUnicode(false)
                .HasColumnName("setting_key");
            entity.Property(e => e.AllowedScope)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("allowed_scope");
            entity.Property(e => e.DefaultValue)
                .HasMaxLength(4000)
                .HasColumnName("default_value");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(400)
                .HasColumnName("description_en");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_setting_definition__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.OwningModule)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("owning_module");
            entity.Property(e => e.ValueType)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("value_type");
        });

        modelBuilder.Entity<ShippingLineExtension>(entity =>
        {
            entity.HasKey(e => e.PartyId).HasName("pk_shipping_line_extension");

            entity
                .ToTable("shipping_line_extension", "party")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("party_shipping_line_extension", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.PrincipalLinePartyId }, "ix_line_ext__principal").HasFilter("([principal_line_party_id] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.ScacCode }, "uq_line_ext__scac")
                .IsUnique()
                .HasFilter("([scac_code] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.Property(e => e.PartyId)
                .ValueGeneratedNever()
                .HasColumnName("party_id");
            entity.Property(e => e.AllianceCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("alliance_code");
            entity.Property(e => e.AllianceName)
                .HasMaxLength(100)
                .HasColumnName("alliance_name");
            entity.Property(e => e.BrandColorHex)
                .HasMaxLength(7)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("brand_color_hex");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_line_ext__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.EdiPartnerCode)
                .HasMaxLength(35)
                .IsUnicode(false)
                .HasColumnName("edi_partner_code");
            entity.Property(e => e.EdiSupportsBaplie).HasColumnName("edi_supports_baplie");
            entity.Property(e => e.EdiSupportsCoarri).HasColumnName("edi_supports_coarri");
            entity.Property(e => e.EdiSupportsCodeco).HasColumnName("edi_supports_codeco");
            entity.Property(e => e.EdiSupportsCoparn).HasColumnName("edi_supports_coparn");
            entity.Property(e => e.ImoCompanyNo)
                .HasMaxLength(15)
                .IsUnicode(false)
                .HasColumnName("imo_company_no");
            entity.Property(e => e.LineRole)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasDefaultValue("LINE", "df_line_ext__role")
                .HasColumnName("line_role");
            entity.Property(e => e.OperatorCode)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("operator_code");
            entity.Property(e => e.PrincipalLinePartyId).HasColumnName("principal_line_party_id");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.ScacCode)
                .HasMaxLength(4)
                .IsUnicode(false)
                .HasColumnName("scac_code");
            entity.Property(e => e.SmdgCode)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("smdg_code");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_line_ext__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<TaxCode>(entity =>
        {
            entity.HasKey(e => e.TaxCodeId).HasName("pk_tax_code");

            entity
                .ToTable("tax_code", "commercial")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("commercial_tax_code", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.TaxCode1 }, "uq_tax_code__code")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.CountryCode, e.TaxType }, "uq_tax_code__default")
                .IsUnique()
                .HasFilter("([is_default_for_type]=(1) AND [deleted_at] IS NULL)");

            entity.Property(e => e.TaxCodeId)
                .HasDefaultValueSql("(newsequentialid())", "df_tax_code__id")
                .HasColumnName("tax_code_id");
            entity.Property(e => e.CountryCode)
                .HasMaxLength(2)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("country_code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_tax_code__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(200)
                .HasColumnName("description_en");
            entity.Property(e => e.DescriptionLocal)
                .HasMaxLength(200)
                .HasColumnName("description_local");
            entity.Property(e => e.EffectiveFrom).HasColumnName("effective_from");
            entity.Property(e => e.EffectiveTo).HasColumnName("effective_to");
            entity.Property(e => e.InputTaxGl)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("input_tax_gl");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_tax_code__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.IsDefaultForType).HasColumnName("is_default_for_type");
            entity.Property(e => e.OutputTaxGl)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("output_tax_gl");
            entity.Property(e => e.RatePct)
                .HasColumnType("decimal(7, 4)")
                .HasColumnName("rate_pct");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TaxCode1)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("tax_code");
            entity.Property(e => e.TaxType)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("tax_type");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_tax_code__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<TenantSetting>(entity =>
        {
            entity.HasKey(e => e.TenantSettingId).HasName("pk_tenant_setting");

            entity
                .ToTable("tenant_setting", "config")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("config_tenant_setting", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.BranchId, e.SettingKey }, "uq_tenant_setting__branch")
                .IsUnique()
                .HasFilter("([branch_id] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.SettingKey }, "uq_tenant_setting__tenant")
                .IsUnique()
                .HasFilter("([branch_id] IS NULL AND [deleted_at] IS NULL)");

            entity.Property(e => e.TenantSettingId)
                .HasDefaultValueSql("(newsequentialid())", "df_tenant_setting__id")
                .HasColumnName("tenant_setting_id");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_tenant_setting__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.SettingKey)
                .HasMaxLength(100)
                .IsUnicode(false)
                .HasColumnName("setting_key");
            entity.Property(e => e.SettingValue)
                .HasMaxLength(4000)
                .HasColumnName("setting_value");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_tenant_setting__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<Uom>(entity =>
        {
            entity.HasKey(e => e.UomCode).HasName("pk_uom");

            entity.ToTable("uom", "lookup");

            entity.Property(e => e.UomCode)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("uom_code");
            entity.Property(e => e.BaseUomCode)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("base_uom_code");
            entity.Property(e => e.Category)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("category");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_uom__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.FactorToBase)
                .HasColumnType("decimal(28, 12)")
                .HasColumnName("factor_to_base");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_uom__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.NameEn)
                .HasMaxLength(100)
                .HasColumnName("name_en");
            entity.Property(e => e.Symbol)
                .HasMaxLength(20)
                .HasColumnName("symbol");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_uom__updated_at")
                .HasColumnName("updated_at");
        });

        modelBuilder.Entity<Vessel>(entity =>
        {
            entity.HasKey(e => e.VesselId).HasName("pk_vessel");

            entity
                .ToTable("vessel", "logistics")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("logistics_vessel", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.VesselName }, "ix_vessel__name").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.VesselCode }, "uq_vessel__code")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.ImoNumber }, "uq_vessel__imo")
                .IsUnique()
                .HasFilter("([imo_number] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.Property(e => e.VesselId)
                .HasDefaultValueSql("(newsequentialid())", "df_vessel__id")
                .HasColumnName("vessel_id");
            entity.Property(e => e.CallSign)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("call_sign");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_vessel__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.FlagCountryCode)
                .HasMaxLength(2)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("flag_country_code");
            entity.Property(e => e.GrossTonnage).HasColumnName("gross_tonnage");
            entity.Property(e => e.ImoNumber)
                .HasMaxLength(7)
                .IsUnicode(false)
                .HasColumnName("imo_number");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_vessel__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.LoaM)
                .HasColumnType("decimal(6, 2)")
                .HasColumnName("loa_m");
            entity.Property(e => e.Mmsi)
                .HasMaxLength(9)
                .IsUnicode(false)
                .HasColumnName("mmsi");
            entity.Property(e => e.OperatorPartyId).HasColumnName("operator_party_id");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TeuCapacity).HasColumnName("teu_capacity");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_vessel__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.VesselCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("vessel_code");
            entity.Property(e => e.VesselName)
                .HasMaxLength(255)
                .HasColumnName("vessel_name");
            entity.Property(e => e.VesselNameLocal)
                .HasMaxLength(255)
                .HasColumnName("vessel_name_local");
            entity.Property(e => e.VesselType)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("vessel_type");
        });

        modelBuilder.Entity<VwCodeList>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("vw_code_list", "config");

            entity.Property(e => e.CategoryCode)
                .HasMaxLength(40)
                .IsUnicode(false)
                .HasColumnName("category_code");
            entity.Property(e => e.Code)
                .HasMaxLength(40)
                .IsUnicode(false)
                .HasColumnName("code");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(200)
                .HasColumnName("description_en");
            entity.Property(e => e.DescriptionLocal)
                .HasMaxLength(200)
                .HasColumnName("description_local");
            entity.Property(e => e.IsActive).HasColumnName("is_active");
            entity.Property(e => e.IsTenantDefined).HasColumnName("is_tenant_defined");
            entity.Property(e => e.IsoCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("iso_code");
            entity.Property(e => e.SortOrder).HasColumnName("sort_order");
        });

        modelBuilder.Entity<VwIsoCodeResolution>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("vw_iso_code_resolution", "equipment");

            entity.Property(e => e.EquipmentTypeId).HasColumnName("equipment_type_id");
            entity.Property(e => e.GroupCode)
                .HasMaxLength(2)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("group_code");
            entity.Property(e => e.HeightMm).HasColumnName("height_mm");
            entity.Property(e => e.IsDefaultOutbound).HasColumnName("is_default_outbound");
            entity.Property(e => e.IsHighCube).HasColumnName("is_high_cube");
            entity.Property(e => e.IsReefer).HasColumnName("is_reefer");
            entity.Property(e => e.IsoCode)
                .HasMaxLength(4)
                .IsUnicode(false)
                .HasColumnName("iso_code");
            entity.Property(e => e.IsoDescription)
                .HasMaxLength(200)
                .HasColumnName("iso_description");
            entity.Property(e => e.LengthFt)
                .HasColumnType("decimal(4, 1)")
                .HasColumnName("length_ft");
            entity.Property(e => e.Standard)
                .HasMaxLength(12)
                .IsUnicode(false)
                .HasColumnName("standard");
            entity.Property(e => e.SupersededBy)
                .HasMaxLength(4)
                .IsUnicode(false)
                .HasColumnName("superseded_by");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TypeCode)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("type_code");
        });

        modelBuilder.Entity<Yard>(entity =>
        {
            entity.HasKey(e => e.YardId).HasName("pk_yard");

            entity
                .ToTable("yard", "org")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("org_yard", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.BranchId, e.YardCode }, "uq_yard__code")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.YardId)
                .HasDefaultValueSql("(newsequentialid())", "df_yard__id")
                .HasColumnName("yard_id");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.CapacityTeu).HasColumnName("capacity_teu");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_yard__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.DirectionCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("direction_code");
            entity.Property(e => e.FullEmpty)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasDefaultValue("BOTH", "df_yard__full_empty")
                .HasColumnName("full_empty");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_yard__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.NameEn)
                .HasMaxLength(200)
                .HasColumnName("name_en");
            entity.Property(e => e.NameLocal)
                .HasMaxLength(200)
                .HasColumnName("name_local");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_yard__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.YardCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("yard_code");
            entity.Property(e => e.YardType)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("yard_type");
        });

        modelBuilder.Entity<YardBlock>(entity =>
        {
            entity.HasKey(e => e.YardBlockId).HasName("pk_yard_block");

            entity
                .ToTable("yard_block", "org")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("org_yard_block", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.AllocatedToPartyId }, "ix_yard_block__party").HasFilter("([allocated_to_party_id] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.YardId, e.BlockCode }, "uq_yard_block__code")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.YardBlockId)
                .HasDefaultValueSql("(newsequentialid())", "df_yard_block__id")
                .HasColumnName("yard_block_id");
            entity.Property(e => e.AllocatedSizeFt).HasColumnName("allocated_size_ft");
            entity.Property(e => e.AllocatedToPartyId).HasColumnName("allocated_to_party_id");
            entity.Property(e => e.BlockCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("block_code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_yard_block__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.DisplayColorHex)
                .HasMaxLength(7)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("display_color_hex");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_yard_block__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.IsDgBlock).HasColumnName("is_dg_block");
            entity.Property(e => e.IsOogBlock).HasColumnName("is_oog_block");
            entity.Property(e => e.IsReeferBlock).HasColumnName("is_reefer_block");
            entity.Property(e => e.LayoutHeight).HasColumnName("layout_height");
            entity.Property(e => e.LayoutRotationDeg).HasColumnName("layout_rotation_deg");
            entity.Property(e => e.LayoutWidth).HasColumnName("layout_width");
            entity.Property(e => e.LayoutX).HasColumnName("layout_x");
            entity.Property(e => e.LayoutY).HasColumnName("layout_y");
            entity.Property(e => e.MaxBays).HasColumnName("max_bays");
            entity.Property(e => e.MaxRows).HasColumnName("max_rows");
            entity.Property(e => e.MaxTiers).HasColumnName("max_tiers");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_yard_block__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.YardId).HasColumnName("yard_id");
        });

        modelBuilder.Entity<YardRow>(entity =>
        {
            entity.HasKey(e => e.YardRowId).HasName("pk_yard_row");

            entity
                .ToTable("yard_row", "org")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("org_yard_row", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.YardBlockId, e.RowLabel }, "uq_yard_row__label")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.YardRowId)
                .HasDefaultValueSql("(newsequentialid())", "df_yard_row__id")
                .HasColumnName("yard_row_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_yard_row__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.DescriptionEn)
                .HasMaxLength(100)
                .HasColumnName("description_en");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_yard_row__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.IsBlocked).HasColumnName("is_blocked");
            entity.Property(e => e.IsOogRow).HasColumnName("is_oog_row");
            entity.Property(e => e.IsReeferRow).HasColumnName("is_reefer_row");
            entity.Property(e => e.ReeferPlugCount).HasColumnName("reefer_plug_count");
            entity.Property(e => e.RowLabel)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("row_label");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_yard_row__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.YardBlockId).HasColumnName("yard_block_id");
        });

        modelBuilder.Entity<YardSlot>(entity =>
        {
            entity.HasKey(e => e.YardSlotId).HasName("pk_yard_slot");

            entity
                .ToTable("yard_slot", "org")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("org_yard_slot", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.YardBlockId, e.RowLabel, e.BayNumber, e.TierNumber }, "uq_yard_slot__position")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.YardSlotId)
                .HasDefaultValueSql("(newsequentialid())", "df_yard_slot__id")
                .HasColumnName("yard_slot_id");
            entity.Property(e => e.BayNumber).HasColumnName("bay_number");
            entity.Property(e => e.BlockReason)
                .HasMaxLength(200)
                .HasColumnName("block_reason");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_yard_slot__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.HasReeferPlug).HasColumnName("has_reefer_plug");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true, "df_yard_slot__is_active")
                .HasColumnName("is_active");
            entity.Property(e => e.IsBlocked).HasColumnName("is_blocked");
            entity.Property(e => e.ReeferPlugRef)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("reefer_plug_ref");
            entity.Property(e => e.ReservedForPartyId).HasColumnName("reserved_for_party_id");
            entity.Property(e => e.RowLabel)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("row_label");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TierNumber).HasColumnName("tier_number");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_yard_slot__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.YardBlockId).HasColumnName("yard_block_id");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
