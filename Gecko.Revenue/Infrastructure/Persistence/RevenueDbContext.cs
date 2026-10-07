using System;
using System.Collections.Generic;
using Gecko.Revenue.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Revenue.Infrastructure.Persistence;

public partial class RevenueDbContext : DbContext
{
    public RevenueDbContext(DbContextOptions<RevenueDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<BillToRole> BillToRoles { get; set; }

    public virtual DbSet<BillingUnit> BillingUnits { get; set; }

    public virtual DbSet<BookingPlan> BookingPlans { get; set; }

    public virtual DbSet<BookingPlanContainer> BookingPlanContainers { get; set; }

    public virtual DbSet<Charge> Charges { get; set; }

    public virtual DbSet<Invoice> Invoices { get; set; }

    public virtual DbSet<InvoiceLine> InvoiceLines { get; set; }

    public virtual DbSet<ContainerStay> ContainerStays { get; set; }

    public virtual DbSet<Currency> Currencies { get; set; }

    public virtual DbSet<FreeTimeRule> FreeTimeRules { get; set; }

    public virtual DbSet<ImportBatch> ImportBatches { get; set; }

    public virtual DbSet<ImportRow> ImportRows { get; set; }

    public virtual DbSet<ImportRowIssue> ImportRowIssues { get; set; }

    public virtual DbSet<Inbox> Inboxes { get; set; }

    public virtual DbSet<Module> Modules { get; set; }

    public virtual DbSet<MovementPricing> MovementPricings { get; set; }

    public virtual DbSet<NumberSeries> NumberSeries { get; set; }

    public virtual DbSet<NumberSeriesCounter> NumberSeriesCounters { get; set; }

    public virtual DbSet<PaymentTerm> PaymentTerms { get; set; }

    public virtual DbSet<RateCondition> RateConditions { get; set; }

    public virtual DbSet<RateConditionValue> RateConditionValues { get; set; }

    public virtual DbSet<RateTier> RateTiers { get; set; }

    public virtual DbSet<Receipt> Receipts { get; set; }

    public virtual DbSet<ReceiptLine> ReceiptLines { get; set; }

    public virtual DbSet<ReceiptPayment> ReceiptPayments { get; set; }

    public virtual DbSet<ReeferSession> ReeferSessions { get; set; }

    public virtual DbSet<Schedule> Schedules { get; set; }

    public virtual DbSet<Shift> Shifts { get; set; }

    public virtual DbSet<ShiftCount> ShiftCounts { get; set; }

    public virtual DbSet<TemplateExport> TemplateExports { get; set; }

    public virtual DbSet<TosRate> TosRates { get; set; }

    public virtual DbSet<VwRateTierDefect> VwRateTierDefects { get; set; }

    public virtual DbSet<VwScheduleEffective> VwScheduleEffectives { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.UseCollation("Latin1_General_100_CI_AS_SC_UTF8");

        modelBuilder.Entity<BillToRole>(entity =>
        {
            entity.HasKey(e => e.Code).HasName("pk_bill_to_role");

            entity.ToTable("bill_to_role", "lookup");

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
            entity.Property(e => e.IsActive).HasColumnName("is_active");
            entity.Property(e => e.LegacyVectorCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("legacy_vector_code");
            entity.Property(e => e.ReplicatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_bill_to_role__replicated_at")
                .HasColumnName("replicated_at");
            entity.Property(e => e.SortOrder).HasColumnName("sort_order");
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
            entity.Property(e => e.IsActive).HasColumnName("is_active");
            entity.Property(e => e.IsTimeBased).HasColumnName("is_time_based");
            entity.Property(e => e.QuantitySource)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("quantity_source");
            entity.Property(e => e.ReplicatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_billing_unit__replicated_at")
                .HasColumnName("replicated_at");
        });

        modelBuilder.Entity<BookingPlan>(entity =>
        {
            entity.HasKey(e => e.BookingId).HasName("pk_booking_plan");

            entity.ToTable("booking_plan", "projection");

            entity.HasIndex(e => new { e.TenantId, e.BranchId, e.Status }, "ix_booking_plan__branch_status");

            entity.HasIndex(e => new { e.TenantId, e.OrderNo }, "uq_booking_plan__order_no").IsUnique();

            entity.Property(e => e.BookingId)
                .ValueGeneratedNever()
                .HasColumnName("booking_id");
            entity.Property(e => e.AgentPartyCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("agent_party_code");
            entity.Property(e => e.BookingTypeCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("booking_type_code");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.CargoCategoryCode)
                .HasMaxLength(40)
                .IsUnicode(false)
                .HasColumnName("cargo_category_code");
            entity.Property(e => e.CargoClassCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("cargo_class_code");
            entity.Property(e => e.ChangedAt).HasColumnName("changed_at");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_booking_plan__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CustomerPartyCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("customer_party_code");
            entity.Property(e => e.DirectionCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("direction_code");
            entity.Property(e => e.ForwarderPartyCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("forwarder_party_code");
            entity.Property(e => e.HaulierPartyCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("haulier_party_code");
            entity.Property(e => e.LastMessageId).HasColumnName("last_message_id");
            entity.Property(e => e.LastReason)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("last_reason");
            entity.Property(e => e.LineCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("line_code");
            entity.Property(e => e.OrderNo)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("order_no");
            entity.Property(e => e.OrderTypeCode)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("order_type_code");
            entity.Property(e => e.PayloadJson).HasColumnName("payload_json");
            entity.Property(e => e.RequirementsJson).HasColumnName("requirements_json");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("status");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_booking_plan__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.ValidFrom).HasColumnName("valid_from");
            entity.Property(e => e.ValidTo).HasColumnName("valid_to");
        });

        modelBuilder.Entity<BookingPlanContainer>(entity =>
        {
            entity.HasKey(e => e.BookingContainerId).HasName("pk_booking_plan_container");

            entity.ToTable("booking_plan_container", "projection");

            entity.HasIndex(e => new { e.TenantId, e.BookingId }, "ix_booking_plan_container__booking");

            entity.HasIndex(e => new { e.TenantId, e.ContainerNo }, "ix_booking_plan_container__box").HasFilter("([container_no] IS NOT NULL AND [is_current]=(1))");

            entity.Property(e => e.BookingContainerId)
                .ValueGeneratedNever()
                .HasColumnName("booking_container_id");
            entity.Property(e => e.BookingId).HasColumnName("booking_id");
            entity.Property(e => e.ContainerNo)
                .HasMaxLength(11)
                .IsUnicode(false)
                .HasColumnName("container_no");
            entity.Property(e => e.DeclaredGrossWeightKg)
                .HasColumnType("decimal(12, 2)")
                .HasColumnName("declared_gross_weight_kg");
            entity.Property(e => e.EndReason)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("end_reason");
            entity.Property(e => e.EquipmentRequirementId).HasColumnName("equipment_requirement_id");
            entity.Property(e => e.EquipmentTypeCode)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("equipment_type_code");
            entity.Property(e => e.IsCurrent)
                .HasDefaultValue(true, "df_booking_plan_container__current")
                .HasColumnName("is_current");
            entity.Property(e => e.IsDangerousGoods).HasColumnName("is_dangerous_goods");
            entity.Property(e => e.IsReefer).HasColumnName("is_reefer");
            entity.Property(e => e.StepsJson).HasColumnName("steps_json");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_booking_plan_container__updated_at")
                .HasColumnName("updated_at");
        });

        modelBuilder.Entity<Charge>(entity =>
        {
            entity.HasKey(e => e.ChargeId).HasName("pk_charge");

            entity
                .ToTable("charge", "billing")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("billing_charge", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.BookingId, e.Status }, "ix_charge__booking");

            entity.HasIndex(e => new { e.TenantId, e.EarnedGateTransactionId }, "ix_charge__earned_by").HasFilter("([earned_gate_transaction_id] IS NOT NULL)");

            entity.HasIndex(e => new { e.TenantId, e.GateTransactionId }, "ix_charge__gate_transaction").HasFilter("([gate_transaction_id] IS NOT NULL)");

            entity.HasIndex(e => new { e.TenantId, e.ReceiptId }, "ix_charge__receipt").HasFilter("([receipt_id] IS NOT NULL)");

            entity.HasIndex(e => new { e.TenantId, e.TruckVisitId }, "ix_charge__truck_visit").HasFilter("([truck_visit_id] IS NOT NULL)");

            entity.HasIndex(e => new { e.TenantId, e.BranchId, e.PayerPartyCode, e.Status }, "ix_charge__unbilled").HasFilter("([status]='UNBILLED')");

            entity.HasIndex(e => new { e.TenantId, e.GateTransactionId, e.ChargeCode, e.BillTo, e.PaymentTermCode }, "uq_charge__gate")
                .IsUnique()
                .HasFilter("([source]='GATE')");

            entity.HasIndex(e => new { e.TenantId, e.TruckVisitId, e.ChargeCode }, "uq_charge__gate_trip")
                .IsUnique()
                .HasFilter("([source]='GATE' AND [is_trip_charge]=(1) AND [status]<>'CANCELLED')");

            entity.HasIndex(e => new { e.TenantId, e.BookingContainerId, e.MovementCode, e.ChargeCode, e.BillTo, e.PaymentTermCode }, "uq_charge__quote")
                .IsUnique()
                .HasFilter("([source]='QUOTE' AND [status]<>'CANCELLED')");

            entity.HasIndex(e => new { e.TenantId, e.ContainerStayId, e.ChargeCode, e.BillingPeriod }, "uq_charge__storage")
                .IsUnique()
                .HasFilter("([source]='STORAGE' AND [status]<>'CANCELLED')");

            entity.HasIndex(e => new { e.TenantId, e.BookingContainerId, e.MovementCode, e.ChargeCode, e.BillTo, e.PaymentTermCode, e.ServiceFrom }, "uq_charge__window")
                .IsUnique()
                .HasFilter("([source]='WINDOW' AND [status]<>'CANCELLED')");

            entity.Property(e => e.ChargeId)
                .HasDefaultValueSql("(newsequentialid())", "df_charge__id")
                .HasColumnName("charge_id");
            entity.Property(e => e.Amount)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("amount");
            entity.Property(e => e.BaseRate)
                .HasColumnType("decimal(18, 4)")
                .HasColumnName("base_rate");
            entity.Property(e => e.BillTo)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("bill_to");
            entity.Property(e => e.BillingPeriod)
                .HasMaxLength(6)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("billing_period");
            entity.Property(e => e.BillingUnitCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("billing_unit_code");
            entity.Property(e => e.BookingContainerId).HasColumnName("booking_container_id");
            entity.Property(e => e.BookingId).HasColumnName("booking_id");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.CancelReason)
                .HasMaxLength(300)
                .HasColumnName("cancel_reason");
            entity.Property(e => e.CancelledAt).HasColumnName("cancelled_at");
            entity.Property(e => e.CancelledBy).HasColumnName("cancelled_by");
            entity.Property(e => e.ChargeCode)
                .HasMaxLength(15)
                .IsUnicode(false)
                .HasColumnName("charge_code");
            entity.Property(e => e.ChargeCodeId).HasColumnName("charge_code_id");
            entity.Property(e => e.ChargeName)
                .HasMaxLength(200)
                .HasColumnName("charge_name");
            entity.Property(e => e.ChargeableQuantity)
                .HasColumnType("decimal(18, 4)")
                .HasColumnName("chargeable_quantity");
            entity.Property(e => e.ContainerNo)
                .HasMaxLength(11)
                .IsUnicode(false)
                .HasColumnName("container_no");
            entity.Property(e => e.ContainerStayId).HasColumnName("container_stay_id");
            entity.Property(e => e.CouponRef)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("coupon_ref");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_charge__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CreditNoteRequired).HasColumnName("credit_note_required");
            entity.Property(e => e.CurrencyCode)
                .HasMaxLength(3)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("currency_code");
            entity.Property(e => e.DiscountRate)
                .HasColumnType("decimal(18, 4)")
                .HasColumnName("discount_rate");
            entity.Property(e => e.DiscountType)
                .HasMaxLength(4)
                .IsUnicode(false)
                .HasColumnName("discount_type");
            entity.Property(e => e.EarnedAt).HasColumnName("earned_at");
            entity.Property(e => e.EarnedGateTransactionId).HasColumnName("earned_gate_transaction_id");
            entity.Property(e => e.EirNo)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("eir_no");
            entity.Property(e => e.FreeUnits)
                .HasColumnType("decimal(18, 4)")
                .HasColumnName("free_units");
            entity.Property(e => e.GateTransactionId).HasColumnName("gate_transaction_id");
            entity.Property(e => e.InvoiceId).HasColumnName("invoice_id");
            entity.Property(e => e.InvoiceLineId).HasColumnName("invoice_line_id");
            entity.Property(e => e.IsLocked).HasColumnName("is_locked");
            entity.Property(e => e.IsRateOverridden).HasColumnName("is_rate_overridden");
            entity.Property(e => e.IsTripCharge).HasColumnName("is_trip_charge");
            entity.Property(e => e.MovementCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("movement_code");
            entity.Property(e => e.OrderNo)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("order_no");
            entity.Property(e => e.OverriddenAt).HasColumnName("overridden_at");
            entity.Property(e => e.OverriddenBy).HasColumnName("overridden_by");
            entity.Property(e => e.OverrideReason)
                .HasMaxLength(500)
                .HasColumnName("override_reason");
            entity.Property(e => e.PayerPartyCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("payer_party_code");
            entity.Property(e => e.PaymentTermCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("payment_term_code");
            entity.Property(e => e.PriceSnapshotJson).HasColumnName("price_snapshot_json");
            entity.Property(e => e.PricedForDate).HasColumnName("priced_for_date");
            entity.Property(e => e.PricesIncludeTax).HasColumnName("prices_include_tax");
            entity.Property(e => e.PricingMethod)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("pricing_method");
            entity.Property(e => e.Quantity)
                .HasColumnType("decimal(18, 4)")
                .HasColumnName("quantity");
            entity.Property(e => e.RateRowVersion)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("rate_row_version");
            entity.Property(e => e.ReceiptId).HasColumnName("receipt_id");
            entity.Property(e => e.ReceiptLineId).HasColumnName("receipt_line_id");
            entity.Property(e => e.ResolvedAt).HasColumnName("resolved_at");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.ScheduleId).HasColumnName("schedule_id");
            entity.Property(e => e.ScheduleNo)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("schedule_no");
            entity.Property(e => e.ScheduleType)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("schedule_type");
            entity.Property(e => e.ScheduleVersionNo).HasColumnName("schedule_version_no");
            entity.Property(e => e.ScopeRank).HasColumnName("scope_rank");
            entity.Property(e => e.ServiceFrom).HasColumnName("service_from");
            entity.Property(e => e.ServiceTo).HasColumnName("service_to");
            entity.Property(e => e.Source)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("source");
            entity.Property(e => e.SourceMessageId).HasColumnName("source_message_id");
            entity.Property(e => e.Specificity).HasColumnName("specificity");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("status");
            entity.Property(e => e.TaxAmount)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("tax_amount");
            entity.Property(e => e.TaxCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("tax_code");
            entity.Property(e => e.TaxRate)
                .HasColumnType("decimal(7, 4)")
                .HasColumnName("tax_rate");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TosRateId).HasColumnName("tos_rate_id");
            entity.Property(e => e.TotalAmount)
                .HasComputedColumnSql("([amount]+[tax_amount])", true)
                .HasColumnType("decimal(19, 2)")
                .HasColumnName("total_amount");
            entity.Property(e => e.TruckVisitId).HasColumnName("truck_visit_id");
            entity.Property(e => e.UnitRate)
                .HasColumnType("decimal(18, 4)")
                .HasColumnName("unit_rate");
            entity.Property(e => e.UnitRateOriginal)
                .HasColumnType("decimal(18, 4)")
                .HasColumnName("unit_rate_original");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_charge__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.WaiveReason)
                .HasMaxLength(300)
                .HasColumnName("waive_reason");
            entity.Property(e => e.WaiveReasonCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("waive_reason_code");
            entity.Property(e => e.WaivedAt).HasColumnName("waived_at");
            entity.Property(e => e.WaivedBy).HasColumnName("waived_by");
        });

        modelBuilder.Entity<Invoice>(entity =>
        {
            entity.HasKey(e => e.InvoiceId).HasName("pk_invoice");

            entity.ToTable("invoice", "billing");

            entity.HasIndex(e => new { e.TenantId, e.BranchId, e.PayerPartyCode, e.IssuedAt }, "ix_invoice__payer");

            entity.HasIndex(e => new { e.TenantId, e.InvoiceNo }, "uq_invoice__no").IsUnique();

            entity.Property(e => e.InvoiceId)
                .ValueGeneratedNever()
                .HasColumnName("invoice_id");
            entity.Property(e => e.BillTo)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("bill_to");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_invoice__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CurrencyCode)
                .HasMaxLength(3)
                .IsUnicode(false)
                .HasColumnName("currency_code");
            entity.Property(e => e.InvoiceNo)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("invoice_no");
            entity.Property(e => e.InvoiceType)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("invoice_type");
            entity.Property(e => e.IssuedAt).HasColumnName("issued_at");
            entity.Property(e => e.IssuedBy).HasColumnName("issued_by");
            entity.Property(e => e.PayerName)
                .HasMaxLength(300)
                .HasColumnName("payer_name");
            entity.Property(e => e.PayerPartyCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("payer_party_code");
            entity.Property(e => e.PaymentTermCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("payment_term_code");
            entity.Property(e => e.Remarks)
                .HasMaxLength(500)
                .HasColumnName("remarks");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.Status)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("status");
            entity.Property(e => e.SubtotalAmount)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("subtotal_amount");
            entity.Property(e => e.TaxAmount)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("tax_amount");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TotalAmount)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("total_amount");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_invoice__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<InvoiceLine>(entity =>
        {
            entity.HasKey(e => e.InvoiceLineId).HasName("pk_invoice_line");

            entity.ToTable("invoice_line", "billing");

            entity.HasIndex(e => new { e.TenantId, e.ChargeId }, "uq_invoice_line__charge").IsUnique();

            entity.HasIndex(e => new { e.InvoiceId, e.LineNo }, "uq_invoice_line__no").IsUnique();

            entity.Property(e => e.InvoiceLineId)
                .ValueGeneratedNever()
                .HasColumnName("invoice_line_id");
            entity.Property(e => e.Amount)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("amount");
            entity.Property(e => e.BookingId).HasColumnName("booking_id");
            entity.Property(e => e.ChargeCode)
                .HasMaxLength(15)
                .IsUnicode(false)
                .HasColumnName("charge_code");
            entity.Property(e => e.ChargeId).HasColumnName("charge_id");
            entity.Property(e => e.ChargeName)
                .HasMaxLength(400)
                .HasColumnName("charge_name");
            entity.Property(e => e.ContainerNo)
                .HasMaxLength(15)
                .IsUnicode(false)
                .HasColumnName("container_no");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_invoice_line__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.InvoiceId).HasColumnName("invoice_id");
            entity.Property(e => e.LineNo).HasColumnName("line_no");
            entity.Property(e => e.MovementCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("movement_code");
            entity.Property(e => e.OrderNo)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("order_no");
            entity.Property(e => e.Quantity)
                .HasColumnType("decimal(18, 3)")
                .HasColumnName("quantity");
            entity.Property(e => e.TaxAmount)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("tax_amount");
            entity.Property(e => e.TaxCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("tax_code");
            entity.Property(e => e.TaxRate)
                .HasColumnType("decimal(7, 4)")
                .HasColumnName("tax_rate");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UnitRate)
                .HasColumnType("decimal(18, 4)")
                .HasColumnName("unit_rate");

            entity.HasOne(d => d.Invoice).WithMany(p => p.InvoiceLines)
                .HasForeignKey(d => d.InvoiceId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_invoice_line__invoice");
        });

        modelBuilder.Entity<ContainerStay>(entity =>
        {
            entity.HasKey(e => e.ContainerStayId).HasName("pk_container_stay");

            entity.ToTable("container_stay", "projection");

            entity.HasIndex(e => new { e.TenantId, e.BranchId, e.Status, e.OutAt }, "ix_container_stay__storage");

            entity.HasIndex(e => new { e.TenantId, e.InGateTransactionId }, "uq_container_stay__in").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.ContainerNo }, "uq_container_stay__open")
                .IsUnique()
                .HasFilter("([status]='OPEN')");

            entity.HasIndex(e => new { e.TenantId, e.OutGateTransactionId }, "uq_container_stay__out")
                .IsUnique()
                .HasFilter("([out_gate_transaction_id] IS NOT NULL)");

            entity.Property(e => e.ContainerStayId)
                .HasDefaultValueSql("(newsequentialid())", "df_container_stay__id")
                .HasColumnName("container_stay_id");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.ContainerNo)
                .HasMaxLength(11)
                .IsUnicode(false)
                .HasColumnName("container_no");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_container_stay__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.EquipmentTypeCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("equipment_type_code");
            entity.Property(e => e.FullEmptyIn)
                .HasMaxLength(5)
                .IsUnicode(false)
                .HasColumnName("full_empty_in");
            entity.Property(e => e.FullEmptyOut)
                .HasMaxLength(5)
                .IsUnicode(false)
                .HasColumnName("full_empty_out");
            entity.Property(e => e.InAt).HasColumnName("in_at");
            entity.Property(e => e.InBookingId).HasColumnName("in_booking_id");
            entity.Property(e => e.InEirNo)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("in_eir_no");
            entity.Property(e => e.InGateTransactionId).HasColumnName("in_gate_transaction_id");
            entity.Property(e => e.IsoCode)
                .HasMaxLength(4)
                .IsUnicode(false)
                .HasColumnName("iso_code");
            entity.Property(e => e.LineCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("line_code");
            entity.Property(e => e.OutAt).HasColumnName("out_at");
            entity.Property(e => e.OutBookingId).HasColumnName("out_booking_id");
            entity.Property(e => e.OutEirNo)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("out_eir_no");
            entity.Property(e => e.OutGateTransactionId).HasColumnName("out_gate_transaction_id");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.Source)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("GATE_EVENT", "df_container_stay__source")
                .HasColumnName("source");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("OPEN", "df_container_stay__status")
                .HasColumnName("status");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_container_stay__updated_at")
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
            entity.Property(e => e.IsActive).HasColumnName("is_active");
            entity.Property(e => e.MinorUnits).HasColumnName("minor_units");
            entity.Property(e => e.NameEn)
                .HasMaxLength(100)
                .HasColumnName("name_en");
            entity.Property(e => e.NumericCode)
                .HasMaxLength(3)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("numeric_code");
            entity.Property(e => e.ReplicatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_currency__replicated_at")
                .HasColumnName("replicated_at");
            entity.Property(e => e.Symbol)
                .HasMaxLength(10)
                .HasColumnName("symbol");
        });

        modelBuilder.Entity<FreeTimeRule>(entity =>
        {
            entity.HasKey(e => e.FreeTimeRuleId).HasName("pk_free_time_rule");

            entity
                .ToTable("free_time_rule", "tariff")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("tariff_free_time_rule", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.ScheduleId, e.FreeTimeKind, e.FullEmpty, e.Direction, e.CargoGroup, e.EquipmentSize }, "uq_free_time_rule__signature")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.FreeTimeRuleId)
                .HasDefaultValueSql("(newsequentialid())", "df_free_time_rule__id")
                .HasColumnName("free_time_rule_id");
            entity.Property(e => e.CargoGroup)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("cargo_group");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_free_time_rule__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.Direction)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("direction");
            entity.Property(e => e.EquipmentSize)
                .HasMaxLength(3)
                .IsUnicode(false)
                .HasColumnName("equipment_size");
            entity.Property(e => e.FreeTimeKind)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("free_time_kind");
            entity.Property(e => e.FreeUnits).HasColumnName("free_units");
            entity.Property(e => e.FullEmpty)
                .HasMaxLength(5)
                .IsUnicode(false)
                .HasColumnName("full_empty");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.ScheduleId).HasColumnName("schedule_id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.Unit)
                .HasMaxLength(5)
                .IsUnicode(false)
                .HasColumnName("unit");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_free_time_rule__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<ImportBatch>(entity =>
        {
            entity.HasKey(e => e.ImportBatchId).HasName("pk_import_batch");

            entity.ToTable("import_batch", "import");

            entity.HasIndex(e => new { e.TenantId, e.ScheduleId, e.UploadedAt }, "ix_import_batch__schedule").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.ScheduleId, e.FileSha256 }, "uq_import_batch__applied_file")
                .IsUnique()
                .HasFilter("([status]='APPLIED' AND [deleted_at] IS NULL)");

            entity.Property(e => e.ImportBatchId)
                .HasDefaultValueSql("(newsequentialid())", "df_import_batch__id")
                .HasColumnName("import_batch_id");
            entity.Property(e => e.AppliedAt).HasColumnName("applied_at");
            entity.Property(e => e.ConfirmedAt).HasColumnName("confirmed_at");
            entity.Property(e => e.ConfirmedBy).HasColumnName("confirmed_by");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_import_batch__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.FailureMessage)
                .HasMaxLength(1000)
                .HasColumnName("failure_message");
            entity.Property(e => e.FileName)
                .HasMaxLength(260)
                .HasColumnName("file_name");
            entity.Property(e => e.FileSha256)
                .HasMaxLength(32)
                .IsFixedLength()
                .HasColumnName("file_sha256");
            entity.Property(e => e.FileSizeBytes).HasColumnName("file_size_bytes");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.RowsDelete).HasColumnName("rows_delete");
            entity.Property(e => e.RowsError).HasColumnName("rows_error");
            entity.Property(e => e.RowsInsert).HasColumnName("rows_insert");
            entity.Property(e => e.RowsOk).HasColumnName("rows_ok");
            entity.Property(e => e.RowsTotal).HasColumnName("rows_total");
            entity.Property(e => e.RowsUnchanged).HasColumnName("rows_unchanged");
            entity.Property(e => e.RowsUpdate).HasColumnName("rows_update");
            entity.Property(e => e.RowsWarning).HasColumnName("rows_warning");
            entity.Property(e => e.ScheduleChangedSinceExport).HasColumnName("schedule_changed_since_export");
            entity.Property(e => e.ScheduleId).HasColumnName("schedule_id");
            entity.Property(e => e.ScheduleRowVersion)
                .HasMaxLength(8)
                .IsFixedLength()
                .HasColumnName("schedule_row_version");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("UPLOADED", "df_import_batch__status")
                .HasColumnName("status");
            entity.Property(e => e.TemplateExportId).HasColumnName("template_export_id");
            entity.Property(e => e.TemplateKind)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("template_kind");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_import_batch__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.UploadedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_import_batch__uploaded_at")
                .HasColumnName("uploaded_at");
            entity.Property(e => e.UploadedBy).HasColumnName("uploaded_by");
        });

        modelBuilder.Entity<ImportRow>(entity =>
        {
            entity.HasKey(e => e.ImportRowId).HasName("pk_import_row");

            entity.ToTable("import_row", "import");

            entity.HasIndex(e => new { e.TenantId, e.ImportBatchId, e.SheetName, e.RowNo }, "uq_import_row__position")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.ImportRowId)
                .HasDefaultValueSql("(newsequentialid())", "df_import_row__id")
                .HasColumnName("import_row_id");
            entity.Property(e => e.Action)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("action");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_import_row__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.ImportBatchId).HasColumnName("import_batch_id");
            entity.Property(e => e.RawJson).HasColumnName("raw_json");
            entity.Property(e => e.ResolvedJson).HasColumnName("resolved_json");
            entity.Property(e => e.RowNo).HasColumnName("row_no");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.SheetName)
                .HasMaxLength(100)
                .HasColumnName("sheet_name");
            entity.Property(e => e.Status)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasDefaultValue("OK", "df_import_row__status")
                .HasColumnName("status");
            entity.Property(e => e.TargetId).HasColumnName("target_id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_import_row__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<ImportRowIssue>(entity =>
        {
            entity.HasKey(e => e.ImportRowIssueId).HasName("pk_import_row_issue");

            entity.ToTable("import_row_issue", "import");

            entity.HasIndex(e => new { e.TenantId, e.ImportRowId }, "ix_import_row_issue__row").HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.ImportRowIssueId)
                .HasDefaultValueSql("(newsequentialid())", "df_import_row_issue__id")
                .HasColumnName("import_row_issue_id");
            entity.Property(e => e.ColumnName)
                .HasMaxLength(100)
                .HasColumnName("column_name");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_import_row_issue__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.ImportRowId).HasColumnName("import_row_id");
            entity.Property(e => e.IssueCode)
                .HasMaxLength(40)
                .IsUnicode(false)
                .HasColumnName("issue_code");
            entity.Property(e => e.Message)
                .HasMaxLength(500)
                .HasColumnName("message");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.Severity)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("severity");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_import_row_issue__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<Inbox>(entity =>
        {
            entity.HasKey(e => e.InboxId).HasName("pk_inbox");

            entity.ToTable("inbox", "billing");

            entity.HasIndex(e => new { e.SourceContext, e.MessageId }, "uq_inbox__message").IsUnique();

            entity.Property(e => e.InboxId).HasColumnName("inbox_id");
            entity.Property(e => e.AggregateId).HasColumnName("aggregate_id");
            entity.Property(e => e.MessageId).HasColumnName("message_id");
            entity.Property(e => e.MessageType)
                .HasMaxLength(100)
                .IsUnicode(false)
                .HasColumnName("message_type");
            entity.Property(e => e.Note)
                .HasMaxLength(400)
                .HasColumnName("note");
            entity.Property(e => e.Outcome)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("APPLIED", "df_inbox__outcome")
                .HasColumnName("outcome");
            entity.Property(e => e.ProcessedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_inbox__processed_at")
                .HasColumnName("processed_at");
            entity.Property(e => e.SourceContext)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("source_context");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
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
            entity.Property(e => e.IsActive).HasColumnName("is_active");
            entity.Property(e => e.IsLicensable).HasColumnName("is_licensable");
            entity.Property(e => e.IsOperational).HasColumnName("is_operational");
            entity.Property(e => e.ReplicatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_module__replicated_at")
                .HasColumnName("replicated_at");
            entity.Property(e => e.SortOrder).HasColumnName("sort_order");
        });

        modelBuilder.Entity<MovementPricing>(entity =>
        {
            entity.HasKey(e => e.MovementPricingId).HasName("pk_movement_pricing");

            entity.ToTable("movement_pricing", "billing");

            entity.HasIndex(e => new { e.TenantId, e.BranchId, e.PricedAt }, "ix_movement_pricing__review").HasFilter("([variants_priced]=(0) AND [reviewed_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.BookingContainerId, e.MovementCode }, "uq_movement_pricing__cash")
                .IsUnique()
                .HasFilter("([clock]='CASH')");

            entity.HasIndex(e => new { e.TenantId, e.GateTransactionId, e.MovementCode }, "uq_movement_pricing__credit")
                .IsUnique()
                .HasFilter("([clock]='CREDIT')");

            entity.Property(e => e.MovementPricingId)
                .HasDefaultValueSql("(newsequentialid())", "df_movement_pricing__id")
                .HasColumnName("movement_pricing_id");
            entity.Property(e => e.BookingContainerId).HasColumnName("booking_container_id");
            entity.Property(e => e.BookingId).HasColumnName("booking_id");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.Clock)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("clock");
            entity.Property(e => e.ContainerNo)
                .HasMaxLength(11)
                .IsUnicode(false)
                .HasColumnName("container_no");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_movement_pricing__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CurrencyCode)
                .HasMaxLength(3)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("currency_code");
            entity.Property(e => e.GateTransactionId).HasColumnName("gate_transaction_id");
            entity.Property(e => e.IsNoCharge)
                .HasComputedColumnSql("(CONVERT([bit],case when [variants_priced]=(0) then (1) else (0) end))", true)
                .HasColumnName("is_no_charge");
            entity.Property(e => e.MovementCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("movement_code");
            entity.Property(e => e.OrderTypeCode)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("order_type_code");
            entity.Property(e => e.PricedAt).HasColumnName("priced_at");
            entity.Property(e => e.ReviewNote)
                .HasMaxLength(300)
                .HasColumnName("review_note");
            entity.Property(e => e.ReviewedAt).HasColumnName("reviewed_at");
            entity.Property(e => e.ReviewedBy).HasColumnName("reviewed_by");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.SourceMessageId).HasColumnName("source_message_id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TotalAmount)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("total_amount");
            entity.Property(e => e.TrailJson).HasColumnName("trail_json");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_movement_pricing__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.VariantsPriced).HasColumnName("variants_priced");
            entity.Property(e => e.VariantsTried).HasColumnName("variants_tried");
        });

        modelBuilder.Entity<NumberSeries>(entity =>
        {
            entity.HasKey(e => e.NumberSeriesId).HasName("pk_number_series");

            entity.ToTable("number_series", "config");

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
            entity.HasKey(e => new { e.NumberSeriesId, e.BranchCode, e.PeriodKey }).HasName("pk_number_series_counter");

            entity.ToTable("number_series_counter", "config");

            entity.Property(e => e.NumberSeriesId).HasColumnName("number_series_id");
            entity.Property(e => e.BranchCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("branch_code");
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
            entity.Property(e => e.IsActive).HasColumnName("is_active");
            entity.Property(e => e.ReplicatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_payment_term__replicated_at")
                .HasColumnName("replicated_at");
            entity.Property(e => e.RequiresCreditAccount).HasColumnName("requires_credit_account");
            entity.Property(e => e.SettlesBeforeRelease).HasColumnName("settles_before_release");
        });

        modelBuilder.Entity<RateCondition>(entity =>
        {
            entity.HasKey(e => e.RateConditionId).HasName("pk_rate_condition");

            entity
                .ToTable("rate_condition", "tariff")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("tariff_rate_condition", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.OwnerType, e.OwnerId, e.SequenceNo }, "uq_rate_condition__sequence")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.RateConditionId)
                .HasDefaultValueSql("(newsequentialid())", "df_rate_condition__id")
                .HasColumnName("rate_condition_id");
            entity.Property(e => e.Axis)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("axis");
            entity.Property(e => e.BoolValue).HasColumnName("bool_value");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_rate_condition__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.Label)
                .HasMaxLength(100)
                .HasColumnName("label");
            entity.Property(e => e.ModifierOp)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("modifier_op");
            entity.Property(e => e.ModifierValue)
                .HasColumnType("decimal(18, 4)")
                .HasColumnName("modifier_value");
            entity.Property(e => e.NumberValue)
                .HasColumnType("decimal(18, 4)")
                .HasColumnName("number_value");
            entity.Property(e => e.Op)
                .HasMaxLength(5)
                .IsUnicode(false)
                .HasColumnName("op");
            entity.Property(e => e.OwnerId).HasColumnName("owner_id");
            entity.Property(e => e.OwnerType)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("owner_type");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.SequenceNo).HasColumnName("sequence_no");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_rate_condition__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<RateConditionValue>(entity =>
        {
            entity.HasKey(e => e.RateConditionValueId).HasName("pk_rate_condition_value");

            entity
                .ToTable("rate_condition_value", "tariff")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("tariff_rate_condition_value", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.RateConditionId, e.ValueCode }, "uq_rate_condition_value__code")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.RateConditionValueId)
                .HasDefaultValueSql("(newsequentialid())", "df_rate_condition_value__id")
                .HasColumnName("rate_condition_value_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_rate_condition_value__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.RateConditionId).HasColumnName("rate_condition_id");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_rate_condition_value__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.ValueCode)
                .HasMaxLength(40)
                .IsUnicode(false)
                .HasColumnName("value_code");
        });

        modelBuilder.Entity<RateTier>(entity =>
        {
            entity.HasKey(e => e.RateTierId).HasName("pk_rate_tier");

            entity
                .ToTable("rate_tier", "tariff")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("tariff_rate_tier", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.OwnerType, e.OwnerId, e.FromQty }, "uq_rate_tier__from")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.RateTierId)
                .HasDefaultValueSql("(newsequentialid())", "df_rate_tier__id")
                .HasColumnName("rate_tier_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_rate_tier__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.FromQty)
                .HasColumnType("decimal(10, 2)")
                .HasColumnName("from_qty");
            entity.Property(e => e.OwnerId).HasColumnName("owner_id");
            entity.Property(e => e.OwnerType)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("owner_type");
            entity.Property(e => e.Rate)
                .HasColumnType("decimal(18, 4)")
                .HasColumnName("rate");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.ToQty)
                .HasColumnType("decimal(10, 2)")
                .HasColumnName("to_qty");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_rate_tier__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<Receipt>(entity =>
        {
            entity.HasKey(e => e.ReceiptId).HasName("pk_receipt");

            entity.ToTable("receipt", "cashier");

            entity.HasIndex(e => new { e.TenantId, e.BookingId }, "ix_receipt__booking").HasFilter("([booking_id] IS NOT NULL)");

            entity.HasIndex(e => new { e.TenantId, e.BranchId, e.ReceiptAt }, "ix_receipt__branch_at");

            entity.HasIndex(e => new { e.TenantId, e.ShiftId }, "ix_receipt__shift");

            entity.HasIndex(e => new { e.TenantId, e.ReceiptNo }, "uq_receipt__no").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.ReplacesReceiptId }, "uq_receipt__replaces")
                .IsUnique()
                .HasFilter("([replaces_receipt_id] IS NOT NULL)");

            entity.Property(e => e.ReceiptId)
                .HasDefaultValueSql("(newsequentialid())", "df_receipt__id")
                .HasColumnName("receipt_id");
            entity.Property(e => e.BookingId).HasColumnName("booking_id");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.CashierUserId).HasColumnName("cashier_user_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_receipt__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CurrencyCode)
                .HasMaxLength(3)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("currency_code");
            entity.Property(e => e.OrderNo)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("order_no");
            entity.Property(e => e.PayerAddress)
                .HasMaxLength(500)
                .HasColumnName("payer_address");
            entity.Property(e => e.PayerBranchNo)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("payer_branch_no");
            entity.Property(e => e.PayerName)
                .HasMaxLength(200)
                .HasColumnName("payer_name");
            entity.Property(e => e.PayerPartyCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("payer_party_code");
            entity.Property(e => e.PayerTaxId)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("payer_tax_id");
            entity.Property(e => e.ReceiptAt).HasColumnName("receipt_at");
            entity.Property(e => e.ReceiptNo)
                .HasMaxLength(40)
                .IsUnicode(false)
                .HasColumnName("receipt_no");
            entity.Property(e => e.ReplacesReceiptId).HasColumnName("replaces_receipt_id");
            entity.Property(e => e.IdempotencyKey)
                .HasMaxLength(100)
                .IsUnicode(false)
                .HasColumnName("idempotency_key");
            entity.Property(e => e.IdempotencyHash)
                .HasMaxLength(32)
                .IsFixedLength()
                .HasColumnName("idempotency_hash");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.ShiftId).HasColumnName("shift_id");
            entity.Property(e => e.Status)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasDefaultValue("ISSUED", "df_receipt__status")
                .HasColumnName("status");
            entity.Property(e => e.SubtotalAmount)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("subtotal_amount");
            entity.Property(e => e.TaxAmount)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("tax_amount");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TotalAmount)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("total_amount");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_receipt__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.VoidReason)
                .HasMaxLength(300)
                .HasColumnName("void_reason");
            entity.Property(e => e.VoidedAt).HasColumnName("voided_at");
            entity.Property(e => e.VoidedBy).HasColumnName("voided_by");
            entity.Property(e => e.WithholdingTaxRate)
                .HasColumnType("decimal(5, 2)")
                .HasColumnName("withholding_tax_rate");
            entity.Property(e => e.WithholdingTaxAmount)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("withholding_tax_amount");
        });

        modelBuilder.Entity<ReceiptLine>(entity =>
        {
            entity.HasKey(e => e.ReceiptLineId).HasName("pk_receipt_line");

            entity.ToTable("receipt_line", "cashier");

            entity.HasIndex(e => new { e.TenantId, e.ReceiptId, e.ChargeId }, "uq_receipt_line__charge").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.ReceiptId, e.LineNo }, "uq_receipt_line__no").IsUnique();

            entity.Property(e => e.ReceiptLineId)
                .HasDefaultValueSql("(newsequentialid())", "df_receipt_line__id")
                .HasColumnName("receipt_line_id");
            entity.Property(e => e.Amount)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("amount");
            entity.Property(e => e.ChargeCode)
                .HasMaxLength(15)
                .IsUnicode(false)
                .HasColumnName("charge_code");
            entity.Property(e => e.ChargeId).HasColumnName("charge_id");
            entity.Property(e => e.ContainerNo)
                .HasMaxLength(11)
                .IsUnicode(false)
                .HasColumnName("container_no");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_receipt_line__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.Description)
                .HasMaxLength(300)
                .HasColumnName("description");
            entity.Property(e => e.LineNo).HasColumnName("line_no");
            entity.Property(e => e.MovementCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("movement_code");
            entity.Property(e => e.Quantity)
                .HasColumnType("decimal(18, 4)")
                .HasColumnName("quantity");
            entity.Property(e => e.ReceiptId).HasColumnName("receipt_id");
            entity.Property(e => e.TaxAmount)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("tax_amount");
            entity.Property(e => e.TaxCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("tax_code");
            entity.Property(e => e.TaxRate)
                .HasColumnType("decimal(7, 4)")
                .HasColumnName("tax_rate");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UnitRate)
                .HasColumnType("decimal(18, 4)")
                .HasColumnName("unit_rate");
        });

        modelBuilder.Entity<ReceiptPayment>(entity =>
        {
            entity.HasKey(e => e.ReceiptPaymentId).HasName("pk_receipt_payment");

            entity.ToTable("receipt_payment", "cashier");

            entity.HasIndex(e => new { e.TenantId, e.ReceiptId }, "ix_receipt_payment__receipt");

            entity.Property(e => e.ReceiptPaymentId)
                .HasDefaultValueSql("(newsequentialid())", "df_receipt_payment__id")
                .HasColumnName("receipt_payment_id");
            entity.Property(e => e.Amount)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("amount");
            entity.Property(e => e.BankName)
                .HasMaxLength(100)
                .HasColumnName("bank_name");
            entity.Property(e => e.ChangeAmount)
                .HasComputedColumnSql("(CONVERT([decimal](18,2),case when [tendered_amount] IS NULL then NULL else [tendered_amount]-[amount] end))", true)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("change_amount");
            entity.Property(e => e.Channel)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("channel");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_receipt_payment__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.ReceiptId).HasColumnName("receipt_id");
            entity.Property(e => e.ReferenceNo)
                .HasMaxLength(100)
                .HasColumnName("reference_no");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TenderedAmount)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("tendered_amount");
        });

        modelBuilder.Entity<ReeferSession>(entity =>
        {
            entity.HasKey(e => e.SessionId).HasName("pk_reefer_session");

            entity.ToTable("reefer_session", "projection");

            entity.HasIndex(e => new { e.TenantId, e.InGateTransactionId }, "ix_reefer_session__stay").HasFilter("([in_gate_transaction_id] IS NOT NULL)");

            entity.HasIndex(e => new { e.TenantId, e.ContainerVisitId }, "ix_reefer_session__visit");

            entity.Property(e => e.SessionId)
                .ValueGeneratedNever()
                .HasColumnName("session_id");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.CloseGateTransactionId).HasColumnName("close_gate_transaction_id");
            entity.Property(e => e.CloseReason)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("close_reason");
            entity.Property(e => e.ContainerNo)
                .HasMaxLength(11)
                .IsUnicode(false)
                .HasColumnName("container_no");
            entity.Property(e => e.ContainerVisitId).HasColumnName("container_visit_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_reefer_session__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.EquipmentTypeCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("equipment_type_code");
            entity.Property(e => e.InGateTransactionId).HasColumnName("in_gate_transaction_id");
            entity.Property(e => e.IsVoided).HasColumnName("is_voided");
            entity.Property(e => e.LastChangedAt).HasColumnName("last_changed_at");
            entity.Property(e => e.LastMessageId).HasColumnName("last_message_id");
            entity.Property(e => e.LastMessageType)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("last_message_type");
            entity.Property(e => e.PayloadJson).HasColumnName("payload_json");
            entity.Property(e => e.PluggedInAt).HasColumnName("plugged_in_at");
            entity.Property(e => e.PluggedInBy).HasColumnName("plugged_in_by");
            entity.Property(e => e.PluggedOutAt).HasColumnName("plugged_out_at");
            entity.Property(e => e.PluggedOutBy).HasColumnName("plugged_out_by");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_reefer_session__updated_at")
                .HasColumnName("updated_at");
        });

        modelBuilder.Entity<Schedule>(entity =>
        {
            entity.HasKey(e => e.ScheduleId).HasName("pk_schedule");

            entity
                .ToTable("schedule", "tariff")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("tariff_schedule", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.ModuleCode, e.Status, e.ScopeRank, e.EffectiveFrom }, "ix_schedule__resolve").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.LegacyQuotationNo, e.VersionNo }, "uq_schedule__legacy")
                .IsUnique()
                .HasFilter("([legacy_quotation_no] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.LineageId, e.VersionNo }, "uq_schedule__lineage_version")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.ScheduleNo, e.VersionNo }, "uq_schedule__no_version")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.LineageId }, "uq_schedule__open_version")
                .IsUnique()
                .HasFilter("(([status] IN ('DRAFT', 'PENDING')) AND [deleted_at] IS NULL)");

            entity.Property(e => e.ScheduleId)
                .HasDefaultValueSql("(newsequentialid())", "df_schedule__id")
                .HasColumnName("schedule_id");
            entity.Property(e => e.AgentPartyCode)
                .HasMaxLength(25)
                .IsUnicode(false)
                .HasColumnName("agent_party_code");
            entity.Property(e => e.AgentPartyId).HasColumnName("agent_party_id");
            entity.Property(e => e.ApprovedAt).HasColumnName("approved_at");
            entity.Property(e => e.ApprovedBy).HasColumnName("approved_by");
            entity.Property(e => e.BookingRef)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("booking_ref");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_schedule__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CurrencyCode)
                .HasMaxLength(3)
                .IsUnicode(false)
                .IsFixedLength()
                .HasDefaultValue("THB", "df_schedule__currency")
                .HasColumnName("currency_code");
            entity.Property(e => e.CustomerPartyCode)
                .HasMaxLength(25)
                .IsUnicode(false)
                .HasColumnName("customer_party_code");
            entity.Property(e => e.CustomerPartyId).HasColumnName("customer_party_id");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.EffectiveFrom).HasColumnName("effective_from");
            entity.Property(e => e.EffectiveTo).HasColumnName("effective_to");
            entity.Property(e => e.ForwarderPartyCode)
                .HasMaxLength(25)
                .IsUnicode(false)
                .HasColumnName("forwarder_party_code");
            entity.Property(e => e.ForwarderPartyId).HasColumnName("forwarder_party_id");
            entity.Property(e => e.LegacyQuotationNo)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("legacy_quotation_no");
            entity.Property(e => e.LineageId).HasColumnName("lineage_id");
            entity.Property(e => e.ModuleCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("module_code");
            entity.Property(e => e.Name)
                .HasMaxLength(200)
                .HasColumnName("name");
            entity.Property(e => e.PricesIncludeTax).HasColumnName("prices_include_tax");
            entity.Property(e => e.RejectedAt).HasColumnName("rejected_at");
            entity.Property(e => e.RejectedBy).HasColumnName("rejected_by");
            entity.Property(e => e.RejectionReason)
                .HasMaxLength(500)
                .HasColumnName("rejection_reason");
            entity.Property(e => e.Remarks)
                .HasMaxLength(1000)
                .HasColumnName("remarks");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.SalesUserId).HasColumnName("sales_user_id");
            entity.Property(e => e.ScheduleNo)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("schedule_no");
            entity.Property(e => e.ScheduleType)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("schedule_type");
            entity.Property(e => e.ScopeRank)
                .HasComputedColumnSql("(CONVERT([tinyint],case when [schedule_type]='SPOT' AND [booking_ref] IS NOT NULL then (1) when [schedule_type]='PUBLIC' AND [branch_id] IS NOT NULL then (9) when [schedule_type]='PUBLIC' then (10) when [agent_party_id] IS NOT NULL AND [forwarder_party_id] IS NOT NULL AND [customer_party_id] IS NOT NULL then (2) when [agent_party_id] IS NOT NULL AND [customer_party_id] IS NOT NULL then (3) when [forwarder_party_id] IS NOT NULL AND [customer_party_id] IS NOT NULL then (4) when [customer_party_id] IS NOT NULL then (5) when [agent_party_id] IS NOT NULL AND [forwarder_party_id] IS NOT NULL then (6) when [agent_party_id] IS NOT NULL then (7) when [forwarder_party_id] IS NOT NULL then (8) else (99) end))", true)
                .HasColumnName("scope_rank");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("DRAFT", "df_schedule__status")
                .HasColumnName("status");
            entity.Property(e => e.SubmittedAt).HasColumnName("submitted_at");
            entity.Property(e => e.SubmittedBy).HasColumnName("submitted_by");
            entity.Property(e => e.SupersedesScheduleId).HasColumnName("supersedes_schedule_id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_schedule__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.VersionNo)
                .HasDefaultValue((short)1, "df_schedule__version")
                .HasColumnName("version_no");
            entity.Property(e => e.WaiveDamagedEmptyStorage).HasColumnName("waive_damaged_empty_storage");
        });

        modelBuilder.Entity<Shift>(entity =>
        {
            entity.HasKey(e => e.ShiftId).HasName("pk_shift");

            entity.ToTable("shift", "cashier");

            entity.HasIndex(e => new { e.TenantId, e.BranchId, e.OpenedAt }, "ix_shift__branch_opened");

            entity.HasIndex(e => new { e.TenantId, e.BranchId, e.CashierUserId }, "uq_shift__open")
                .IsUnique()
                .HasFilter("([status]='OPEN')");

            entity.Property(e => e.ShiftId)
                .HasDefaultValueSql("(newsequentialid())", "df_shift__id")
                .HasColumnName("shift_id");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.CashierUserId).HasColumnName("cashier_user_id");
            entity.Property(e => e.CloseNote)
                .HasMaxLength(500)
                .HasColumnName("close_note");
            entity.Property(e => e.ClosedAt).HasColumnName("closed_at");
            entity.Property(e => e.ClosedBy).HasColumnName("closed_by");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_shift__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CurrencyCode)
                .HasMaxLength(3)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("currency_code");
            entity.Property(e => e.OpenedAt).HasColumnName("opened_at");
            entity.Property(e => e.OpeningFloat)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("opening_float");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.Status)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasDefaultValue("OPEN", "df_shift__status")
                .HasColumnName("status");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_shift__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<ShiftCount>(entity =>
        {
            entity.HasKey(e => e.ShiftCountId).HasName("pk_shift_count");

            entity.ToTable("shift_count", "cashier");

            entity.HasIndex(e => new { e.TenantId, e.ShiftId, e.Channel }, "uq_shift_count__channel").IsUnique();

            entity.Property(e => e.ShiftCountId)
                .HasDefaultValueSql("(newsequentialid())", "df_shift_count__id")
                .HasColumnName("shift_count_id");
            entity.Property(e => e.Channel)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("channel");
            entity.Property(e => e.CountedAmount)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("counted_amount");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_shift_count__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.ExpectedAmount)
                .HasColumnType("decimal(18, 2)")
                .HasColumnName("expected_amount");
            entity.Property(e => e.ShiftId).HasColumnName("shift_id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.Variance)
                .HasComputedColumnSql("([counted_amount]-[expected_amount])", true)
                .HasColumnType("decimal(19, 2)")
                .HasColumnName("variance");
        });

        modelBuilder.Entity<TemplateExport>(entity =>
        {
            entity.HasKey(e => e.TemplateExportId).HasName("pk_template_export");

            entity.ToTable("template_export", "import");

            entity.HasIndex(e => new { e.TenantId, e.Token }, "uq_template_export__token")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.TemplateExportId)
                .HasDefaultValueSql("(newsequentialid())", "df_template_export__id")
                .HasColumnName("template_export_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_template_export__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.GeneratedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_template_export__generated_at")
                .HasColumnName("generated_at");
            entity.Property(e => e.GeneratedBy).HasColumnName("generated_by");
            entity.Property(e => e.ModuleCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("module_code");
            entity.Property(e => e.RowCount).HasColumnName("row_count");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.ScheduleId).HasColumnName("schedule_id");
            entity.Property(e => e.ScheduleRowVersion)
                .HasMaxLength(8)
                .IsFixedLength()
                .HasColumnName("schedule_row_version");
            entity.Property(e => e.TemplateKind)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("template_kind");
            entity.Property(e => e.TemplateVersion).HasColumnName("template_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.Token)
                .HasDefaultValueSql("(newid())", "df_template_export__token")
                .HasColumnName("token");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_template_export__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<TosRate>(entity =>
        {
            entity.HasKey(e => e.TosRateId).HasName("pk_tos_rate");

            entity
                .ToTable("tos_rate", "tariff")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("tariff_tos_rate", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.ScheduleId, e.ChargeCodeId, e.BillTo, e.PaymentTermCode, e.Specificity }, "ix_tos_rate__resolve").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.ScheduleId, e.ChargeCodeId, e.BillTo, e.PaymentTermCode, e.AxisSignature, e.BillingUnitCode }, "uq_tos_rate__signature")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.TosRateId)
                .HasDefaultValueSql("(newsequentialid())", "df_tos_rate__id")
                .HasColumnName("tos_rate_id");
            entity.Property(e => e.AxisSignature)
                .HasMaxLength(220)
                .IsUnicode(false)
                .HasComputedColumnSql("(CONVERT([varchar](220),(((((((((isnull(CONVERT([char](36),[order_type_id]),'*')+'|')+isnull(CONVERT([char](36),[movement_id]),'*'))+'|')+isnull(CONVERT([char](36),[equipment_type_id]),'*'))+'|')+isnull([equipment_size],'*'))+'|')+isnull([cargo_category_code],'*'))+'|')+isnull([truck_category_code],'*')))", true)
                .HasColumnName("axis_signature");
            entity.Property(e => e.BillTo)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("bill_to");
            entity.Property(e => e.BillingUnitCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("billing_unit_code");
            entity.Property(e => e.CargoCategoryCode)
                .HasMaxLength(40)
                .IsUnicode(false)
                .HasColumnName("cargo_category_code");
            entity.Property(e => e.ChargeCode)
                .HasMaxLength(15)
                .IsUnicode(false)
                .HasColumnName("charge_code");
            entity.Property(e => e.ChargeCodeId).HasColumnName("charge_code_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_tos_rate__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CreditTermDays).HasColumnName("credit_term_days");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.EquipmentSize)
                .HasMaxLength(3)
                .IsUnicode(false)
                .HasColumnName("equipment_size");
            entity.Property(e => e.EquipmentTypeCode)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("equipment_type_code");
            entity.Property(e => e.EquipmentTypeId).HasColumnName("equipment_type_id");
            entity.Property(e => e.ImportRowId).HasColumnName("import_row_id");
            entity.Property(e => e.MovementCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("movement_code");
            entity.Property(e => e.MovementId).HasColumnName("movement_id");
            entity.Property(e => e.OrderTypeCode)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("order_type_code");
            entity.Property(e => e.OrderTypeId).HasColumnName("order_type_id");
            entity.Property(e => e.PaymentTermCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("payment_term_code");
            entity.Property(e => e.PricingMethod)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("FLAT", "df_tos_rate__method")
                .HasColumnName("pricing_method");
            entity.Property(e => e.Rate)
                .HasColumnType("decimal(18, 4)")
                .HasColumnName("rate");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.ScheduleId).HasColumnName("schedule_id");
            entity.Property(e => e.Source)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("MANUAL", "df_tos_rate__source")
                .HasColumnName("source");
            entity.Property(e => e.Specificity)
                .HasComputedColumnSql("(CONVERT([smallint],((((case when [order_type_id] IS NOT NULL then (32) else (0) end+case when [movement_id] IS NOT NULL then (16) else (0) end)+case when [equipment_type_id] IS NOT NULL then (8) else (0) end)+case when [equipment_size] IS NOT NULL then (4) else (0) end)+case when [cargo_category_code] IS NOT NULL then (2) else (0) end)+case when [truck_category_code] IS NOT NULL then (1) else (0) end))", true)
                .HasColumnName("specificity");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TierBasis)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("tier_basis");
            entity.Property(e => e.TruckCategoryCode)
                .HasMaxLength(40)
                .IsUnicode(false)
                .HasColumnName("truck_category_code");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_tos_rate__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<VwRateTierDefect>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("vw_rate_tier_defects", "tariff");

            entity.Property(e => e.ChargeCode)
                .HasMaxLength(15)
                .IsUnicode(false)
                .HasColumnName("charge_code");
            entity.Property(e => e.Defect)
                .HasMaxLength(14)
                .IsUnicode(false)
                .HasColumnName("defect");
            entity.Property(e => e.FromQty)
                .HasColumnType("decimal(10, 2)")
                .HasColumnName("from_qty");
            entity.Property(e => e.ScheduleId).HasColumnName("schedule_id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.ToQty)
                .HasColumnType("decimal(10, 2)")
                .HasColumnName("to_qty");
            entity.Property(e => e.TosRateId).HasColumnName("tos_rate_id");
        });

        modelBuilder.Entity<VwScheduleEffective>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("vw_schedule_effective", "tariff");

            entity.Property(e => e.AgentPartyId).HasColumnName("agent_party_id");
            entity.Property(e => e.ApprovedAt).HasColumnName("approved_at");
            entity.Property(e => e.BookingRef)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("booking_ref");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.CurrencyCode)
                .HasMaxLength(3)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("currency_code");
            entity.Property(e => e.CustomerPartyId).HasColumnName("customer_party_id");
            entity.Property(e => e.EffectiveFrom).HasColumnName("effective_from");
            entity.Property(e => e.EffectiveUntil).HasColumnName("effective_until");
            entity.Property(e => e.ForwarderPartyId).HasColumnName("forwarder_party_id");
            entity.Property(e => e.IsFullySuperseded).HasColumnName("is_fully_superseded");
            entity.Property(e => e.LineageId).HasColumnName("lineage_id");
            entity.Property(e => e.ModuleCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("module_code");
            entity.Property(e => e.Name)
                .HasMaxLength(200)
                .HasColumnName("name");
            entity.Property(e => e.PricesIncludeTax).HasColumnName("prices_include_tax");
            entity.Property(e => e.ScheduleId).HasColumnName("schedule_id");
            entity.Property(e => e.ScheduleNo)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("schedule_no");
            entity.Property(e => e.ScheduleType)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("schedule_type");
            entity.Property(e => e.ScopeRank).HasColumnName("scope_rank");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.VersionNo).HasColumnName("version_no");
            entity.Property(e => e.WaiveDamagedEmptyStorage).HasColumnName("waive_damaged_empty_storage");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
