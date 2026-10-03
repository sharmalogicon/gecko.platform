using System;
using System.Collections.Generic;
using Gecko.Tos.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Tos.Infrastructure.Persistence;

public partial class TosDbContext : DbContext
{
    public TosDbContext(DbContextOptions<TosDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<Attachment> Attachments { get; set; }

    public virtual DbSet<Booking> Bookings { get; set; }

    public virtual DbSet<BookingContainer> BookingContainers { get; set; }

    public virtual DbSet<ContainerHold> ContainerHolds { get; set; }

    public virtual DbSet<ContainerVisit> ContainerVisits { get; set; }

    public virtual DbSet<CutoffException> CutoffExceptions { get; set; }

    public virtual DbSet<EquipmentRequirement> EquipmentRequirements { get; set; }

    public virtual DbSet<GateAuthorization> GateAuthorizations { get; set; }

    public virtual DbSet<GateTransaction> GateTransactions { get; set; }

    public virtual DbSet<GateTransactionSeal> GateTransactionSeals { get; set; }

    public virtual DbSet<Module> Modules { get; set; }

    public virtual DbSet<MovementPlan> MovementPlans { get; set; }

    public virtual DbSet<ReeferPowerSession> ReeferPowerSessions { get; set; }

    public virtual DbSet<Survey> Surveys { get; set; }

    public virtual DbSet<SurveyDamage> SurveyDamages { get; set; }

    public virtual DbSet<TruckVisit> TruckVisits { get; set; }

    public virtual DbSet<VesselCall> VesselCalls { get; set; }

    public virtual DbSet<VesselCallCutoff> VesselCallCutoffs { get; set; }

    public virtual DbSet<VesselCallLine> VesselCallLines { get; set; }

    public virtual DbSet<VisitEvent> VisitEvents { get; set; }

    public virtual DbSet<VwActiveHold> VwActiveHolds { get; set; }

    public virtual DbSet<VwBookingDefect> VwBookingDefects { get; set; }

    public virtual DbSet<VwBookingProgress> VwBookingProgresses { get; set; }

    public virtual DbSet<VwContainerInYard> VwContainerInYards { get; set; }

    public virtual DbSet<VwVesselCallDefect> VwVesselCallDefects { get; set; }

    public virtual DbSet<VwVesselCallStatus> VwVesselCallStatuses { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.UseCollation("Latin1_General_100_CI_AS_SC_UTF8");

        modelBuilder.Entity<Attachment>(entity =>
        {
            entity.HasKey(e => e.AttachmentId).HasName("pk_attachment");

            entity.ToTable("attachment", "gate");

            entity.HasIndex(e => new { e.TenantId, e.OwnerType, e.OwnerId }, "ix_attachment__owner").HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.AttachmentId)
                .HasDefaultValueSql("(newsequentialid())", "df_attachment__id")
                .HasColumnName("attachment_id");
            entity.Property(e => e.BlobUri)
                .HasMaxLength(500)
                .HasColumnName("blob_uri");
            entity.Property(e => e.Caption)
                .HasMaxLength(200)
                .HasColumnName("caption");
            entity.Property(e => e.ContentType)
                .HasMaxLength(100)
                .IsUnicode(false)
                .HasColumnName("content_type");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_attachment__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.OwnerId).HasColumnName("owner_id");
            entity.Property(e => e.OwnerType)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("owner_type");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.Sha256)
                .HasMaxLength(32)
                .HasColumnName("sha256");
            entity.Property(e => e.SizeBytes).HasColumnName("size_bytes");
            entity.Property(e => e.TakenAt).HasColumnName("taken_at");
            entity.Property(e => e.TakenBy).HasColumnName("taken_by");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_attachment__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<Booking>(entity =>
        {
            entity.HasKey(e => e.BookingId).HasName("pk_booking");

            entity
                .ToTable("booking", "booking")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("booking_booking", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.BranchId, e.Status, e.CreatedAt }, "ix_booking__branch_status").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.LinePartyId, e.CarrierRef }, "ix_booking__carrier_ref").HasFilter("([carrier_ref] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.VesselCallId }, "ix_booking__vessel_call").HasFilter("([vessel_call_id] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.OrderNo }, "uq_booking__order_no")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.BookingId)
                .HasDefaultValueSql("(newsequentialid())", "df_booking__id")
                .HasColumnName("booking_id");
            entity.Property(e => e.AgentPartyCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("agent_party_code");
            entity.Property(e => e.AgentPartyId).HasColumnName("agent_party_id");
            entity.Property(e => e.BookingTypeCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("booking_type_code");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.CancelReason)
                .HasMaxLength(500)
                .HasColumnName("cancel_reason");
            entity.Property(e => e.CancelledAt).HasColumnName("cancelled_at");
            entity.Property(e => e.CancelledBy).HasColumnName("cancelled_by");
            entity.Property(e => e.CargoCategoryCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("cargo_category_code");
            entity.Property(e => e.CargoClassCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("cargo_class_code");
            entity.Property(e => e.CarrierRef)
                .HasMaxLength(40)
                .IsUnicode(false)
                .HasColumnName("carrier_ref");
            entity.Property(e => e.IdempotencyKey)
                .HasMaxLength(100)
                .IsUnicode(false)
                .HasColumnName("idempotency_key");
            entity.Property(e => e.IdempotencyHash)
                .HasMaxLength(32)
                .IsFixedLength()
                .HasColumnName("idempotency_hash");
            entity.Property(e => e.CloseReason)
                .HasMaxLength(500)
                .HasColumnName("close_reason");
            entity.Property(e => e.ClosedAt).HasColumnName("closed_at");
            entity.Property(e => e.ClosedBy).HasColumnName("closed_by");
            entity.Property(e => e.CommodityCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("commodity_code");
            entity.Property(e => e.CommodityId).HasColumnName("commodity_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_booking__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CustomerPartyCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("customer_party_code");
            entity.Property(e => e.CustomerPartyId).HasColumnName("customer_party_id");
            entity.Property(e => e.CustomerRef)
                .HasMaxLength(50)
                .HasColumnName("customer_ref");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.DirectionCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("direction_code");
            entity.Property(e => e.EdiMessageRef)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("edi_message_ref");
            entity.Property(e => e.ForwarderPartyCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("forwarder_party_code");
            entity.Property(e => e.ForwarderPartyId).HasColumnName("forwarder_party_id");
            entity.Property(e => e.FpdPortCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("fpd_port_code");
            entity.Property(e => e.FpdPortId).HasColumnName("fpd_port_id");
            entity.Property(e => e.HaulierPartyCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("haulier_party_code");
            entity.Property(e => e.HaulierPartyId).HasColumnName("haulier_party_id");
            entity.Property(e => e.LegacyOrderNo)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("legacy_order_no");
            entity.Property(e => e.LinePartyCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("line_party_code");
            entity.Property(e => e.LinePartyId).HasColumnName("line_party_id");
            entity.Property(e => e.OrderNo)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("order_no");
            entity.Property(e => e.OrderTypeCode)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("order_type_code");
            entity.Property(e => e.OrderTypeId).HasColumnName("order_type_id");
            entity.Property(e => e.PodPortCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("pod_port_code");
            entity.Property(e => e.PodPortId).HasColumnName("pod_port_id");
            entity.Property(e => e.PolPortCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("pol_port_code");
            entity.Property(e => e.PolPortId).HasColumnName("pol_port_id");
            entity.Property(e => e.Remarks)
                .HasMaxLength(1000)
                .HasColumnName("remarks");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.Source)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("MANUAL", "df_booking__source")
                .HasColumnName("source");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("OPEN", "df_booking__status")
                .HasColumnName("status");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_booking__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.ValidFrom).HasColumnName("valid_from");
            entity.Property(e => e.ValidTo).HasColumnName("valid_to");
            entity.Property(e => e.VesselCallId).HasColumnName("vessel_call_id");
            entity.Property(e => e.VesselCallLineId).HasColumnName("vessel_call_line_id");
        });

        modelBuilder.Entity<BookingContainer>(entity =>
        {
            entity.HasKey(e => e.BookingContainerId).HasName("pk_booking_container");

            entity
                .ToTable("booking_container", "booking")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("booking_booking_container", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.BookingId, e.EquipmentRequirementId }, "ix_booking_container__booking").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.ContainerNo, e.AssignedAt }, "ix_booking_container__history").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.ContainerNo }, "uq_booking_container__active")
                .IsUnique()
                .HasFilter("([ended_at] IS NULL AND [deleted_at] IS NULL)");

            entity.Property(e => e.BookingContainerId)
                .HasDefaultValueSql("(newsequentialid())", "df_booking_container__id")
                .HasColumnName("booking_container_id");
            entity.Property(e => e.AssignedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_booking_container__assigned_at")
                .HasColumnName("assigned_at");
            entity.Property(e => e.AssignedBy).HasColumnName("assigned_by");
            entity.Property(e => e.AssignmentSource)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("assignment_source");
            entity.Property(e => e.BookingId).HasColumnName("booking_id");
            entity.Property(e => e.ContainerId).HasColumnName("container_id");
            entity.Property(e => e.ContainerNo)
                .HasMaxLength(11)
                .IsUnicode(false)
                .HasColumnName("container_no");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_booking_container__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeclaredSealNo)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("declared_seal_no");
            entity.Property(e => e.DeclaredVgmKg)
                .HasColumnType("decimal(10, 2)")
                .HasColumnName("declared_vgm_kg");
            entity.Property(e => e.HandoverModeCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("handover_mode_code");
            entity.HasIndex(e => new { e.TenantId, e.BookingId, e.ClientLineId }, "uq_booking_container__client_line")
                .IsUnique()
                .HasFilter("([client_line_id] IS NOT NULL AND [deleted_at] IS NULL)");
            entity.Property(e => e.ClientLineId).HasColumnName("client_line_id");
            entity.Property(e => e.CustomerSealNo).HasMaxLength(20).IsUnicode(false).HasColumnName("customer_seal_no");
            entity.Property(e => e.DeclaredVolumeCbm).HasColumnType("decimal(10, 3)").HasColumnName("declared_volume_cbm");
            entity.Property(e => e.RequiredDate).HasColumnName("required_date");
            entity.Property(e => e.CargoCategoryCode).HasMaxLength(40).IsUnicode(false).HasColumnName("cargo_category_code");
            entity.Property(e => e.ImdgClass).HasMaxLength(10).IsUnicode(false).HasColumnName("imdg_class");
            entity.Property(e => e.UnNumber).HasMaxLength(4).IsUnicode(false).IsFixedLength().HasColumnName("un_number");
            entity.Property(e => e.ReeferSetTempC).HasColumnType("decimal(5, 2)").HasColumnName("reefer_set_temp_c");
            entity.Property(e => e.ReeferVentPct).HasColumnType("decimal(5, 2)").HasColumnName("reefer_vent_pct");
            entity.Property(e => e.ReeferHumidityPct).HasColumnType("decimal(5, 2)").HasColumnName("reefer_humidity_pct");
            entity.Property(e => e.StowageCode).HasMaxLength(20).IsUnicode(false).HasColumnName("stowage_code");
            entity.Property(e => e.StowageNo).HasMaxLength(20).IsUnicode(false).HasColumnName("stowage_no");
            entity.Property(e => e.IsPreCool).HasColumnName("is_pre_cool");
            entity.Property(e => e.Remarks).HasMaxLength(500).HasColumnName("remarks");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.EndReason)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("end_reason");
            entity.Property(e => e.EndedAt).HasColumnName("ended_at");
            entity.Property(e => e.EndedBy).HasColumnName("ended_by");
            entity.Property(e => e.EquipmentRequirementId).HasColumnName("equipment_requirement_id");
            entity.Property(e => e.IsCheckDigitValid).HasColumnName("is_check_digit_valid");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_booking_container__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<ContainerHold>(entity =>
        {
            entity.HasKey(e => e.ContainerHoldId).HasName("pk_container_hold");

            entity
                .ToTable("container_hold", "yard")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("yard_container_hold", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.ContainerNo }, "ix_container_hold__barrier").HasFilter("([released_at] IS NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.BookingId }, "ix_container_hold__booking").HasFilter("([booking_id] IS NOT NULL AND [released_at] IS NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.ContainerNo, e.AppliedAt }, "ix_container_hold__history").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.BookingId, e.HoldCode }, "uq_container_hold__active_booking")
                .IsUnique()
                .HasFilter("([container_no] IS NULL AND [booking_id] IS NOT NULL AND [released_at] IS NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.ContainerNo, e.HoldCode }, "uq_container_hold__active_box")
                .IsUnique()
                .HasFilter("([container_no] IS NOT NULL AND [released_at] IS NULL AND [deleted_at] IS NULL)");

            entity.Property(e => e.ContainerHoldId)
                .HasDefaultValueSql("(newsequentialid())", "df_container_hold__id")
                .HasColumnName("container_hold_id");
            entity.Property(e => e.AppliedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_container_hold__applied_at")
                .HasColumnName("applied_at");
            entity.Property(e => e.AppliedBy).HasColumnName("applied_by");
            entity.Property(e => e.ApplyReason)
                .HasMaxLength(500)
                .HasColumnName("apply_reason");
            entity.Property(e => e.BookingId).HasColumnName("booking_id");
            entity.Property(e => e.ContainerNo)
                .HasMaxLength(11)
                .IsUnicode(false)
                .HasColumnName("container_no");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_container_hold__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.ExternalRef)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("external_ref");
            entity.Property(e => e.HoldCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("hold_code");
            entity.Property(e => e.HoldId).HasColumnName("hold_id");
            entity.Property(e => e.ReleaseReason)
                .HasMaxLength(500)
                .HasColumnName("release_reason");
            entity.Property(e => e.ReleaseRef)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("release_ref");
            entity.Property(e => e.ReleaseSource)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("release_source");
            entity.Property(e => e.ReleasedAt).HasColumnName("released_at");
            entity.Property(e => e.ReleasedBy).HasColumnName("released_by");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.Source)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("MANUAL", "df_container_hold__source")
                .HasColumnName("source");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_container_hold__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<ContainerVisit>(entity =>
        {
            entity.HasKey(e => e.ContainerVisitId).HasName("pk_container_visit");

            entity
                .ToTable("container_visit", "yard")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("yard_container_visit", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.CurrentBookingContainerId }, "ix_container_visit__booking_container").HasFilter("([current_booking_container_id] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.ContainerNo, e.LastEventAt }, "ix_container_visit__container")
                .IsDescending(false, false, true)
                .HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.GateInTransactionId }, "ix_container_visit__gate_in").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.BranchId, e.LinePartyCode, e.FullEmpty }, "ix_container_visit__stock").HasFilter("([gate_out_transaction_id] IS NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.ContainerNo }, "uq_container_visit__open")
                .IsUnique()
                .HasFilter("([gate_out_transaction_id] IS NULL AND [deleted_at] IS NULL)");

            entity.Property(e => e.ContainerVisitId)
                .HasDefaultValueSql("(newsequentialid())", "df_container_visit__id")
                .HasColumnName("container_visit_id");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.ConditionCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("condition_code");
            entity.Property(e => e.ContainerId).HasColumnName("container_id");
            entity.Property(e => e.ContainerNo)
                .HasMaxLength(11)
                .IsUnicode(false)
                .HasColumnName("container_no");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_container_visit__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CurrentBookingContainerId).HasColumnName("current_booking_container_id");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.EquipmentTypeCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("equipment_type_code");
            entity.Property(e => e.EquipmentTypeId).HasColumnName("equipment_type_id");
            entity.Property(e => e.FullEmpty)
                .HasMaxLength(5)
                .IsUnicode(false)
                .HasColumnName("full_empty");
            entity.Property(e => e.GateInTransactionId).HasColumnName("gate_in_transaction_id");
            entity.Property(e => e.GateOutTransactionId).HasColumnName("gate_out_transaction_id");
            entity.Property(e => e.GradeCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("grade_code");
            entity.Property(e => e.LastEventAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_container_visit__last_event")
                .HasColumnName("last_event_at");
            entity.Property(e => e.LinePartyCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("line_party_code");
            entity.Property(e => e.LinePartyId).HasColumnName("line_party_id");
            entity.Property(e => e.PositionText)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("position_text");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_container_visit__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.YardId).HasColumnName("yard_id");
            entity.Property(e => e.YardSlotId).HasColumnName("yard_slot_id");
        });

        modelBuilder.Entity<CutoffException>(entity =>
        {
            entity.HasKey(e => e.CutoffExceptionId).HasName("pk_cutoff_exception");

            entity
                .ToTable("cutoff_exception", "booking")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("booking_cutoff_exception", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.BookingId, e.CutoffKind }, "ix_cutoff_exception__booking").HasFilter("([revoked_at] IS NULL AND [deleted_at] IS NULL)");

            entity.Property(e => e.CutoffExceptionId)
                .HasDefaultValueSql("(newsequentialid())", "df_cutoff_exception__id")
                .HasColumnName("cutoff_exception_id");
            entity.Property(e => e.AllowedUntil).HasColumnName("allowed_until");
            entity.Property(e => e.ApprovedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_cutoff_exception__approved_at")
                .HasColumnName("approved_at");
            entity.Property(e => e.ApprovedBy).HasColumnName("approved_by");
            entity.Property(e => e.BookingId).HasColumnName("booking_id");
            entity.Property(e => e.CarrierApprovalRef)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("carrier_approval_ref");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_cutoff_exception__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CutoffKind)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("cutoff_kind");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.EquipmentRequirementId).HasColumnName("equipment_requirement_id");
            entity.Property(e => e.Reason)
                .HasMaxLength(500)
                .HasColumnName("reason");
            entity.Property(e => e.RevokeReason)
                .HasMaxLength(300)
                .HasColumnName("revoke_reason");
            entity.Property(e => e.RevokedAt).HasColumnName("revoked_at");
            entity.Property(e => e.RevokedBy).HasColumnName("revoked_by");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_cutoff_exception__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<EquipmentRequirement>(entity =>
        {
            entity.HasKey(e => e.EquipmentRequirementId).HasName("pk_equipment_requirement");

            entity
                .ToTable("equipment_requirement", "booking")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("booking_equipment_requirement", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.BookingId, e.LineNo }, "uq_equipment_requirement__line")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.EquipmentRequirementId)
                .HasDefaultValueSql("(newsequentialid())", "df_equipment_requirement__id")
                .HasColumnName("equipment_requirement_id");
            entity.Property(e => e.BookingId).HasColumnName("booking_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_equipment_requirement__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeclaredGrossWeightKg)
                .HasColumnType("decimal(10, 2)")
                .HasColumnName("declared_gross_weight_kg");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.EquipmentTypeCode)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("equipment_type_code");
            entity.Property(e => e.EquipmentTypeId).HasColumnName("equipment_type_id");
            entity.Property(e => e.ImdgClass)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("imdg_class");
            entity.Property(e => e.LineNo).HasColumnName("line_no");
            entity.Property(e => e.MinGradeCode)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("min_grade_code");
            entity.Property(e => e.OogOverHeightCm).HasColumnName("oog_over_height_cm");
            entity.Property(e => e.OogOverLengthBackCm).HasColumnName("oog_over_length_back_cm");
            entity.Property(e => e.OogOverLengthFrontCm).HasColumnName("oog_over_length_front_cm");
            entity.Property(e => e.OogOverWidthLeftCm).HasColumnName("oog_over_width_left_cm");
            entity.Property(e => e.OogOverWidthRightCm).HasColumnName("oog_over_width_right_cm");
            entity.Property(e => e.Qty).HasColumnName("qty");
            entity.Property(e => e.ReeferHumidityPct)
                .HasColumnType("decimal(5, 2)")
                .HasColumnName("reefer_humidity_pct");
            entity.Property(e => e.ReeferSetTempC)
                .HasColumnType("decimal(5, 2)")
                .HasColumnName("reefer_set_temp_c");
            entity.Property(e => e.ReeferVentPct)
                .HasColumnType("decimal(5, 2)")
                .HasColumnName("reefer_vent_pct");
            entity.Property(e => e.Remarks)
                .HasMaxLength(500)
                .HasColumnName("remarks");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UnNumber)
                .HasMaxLength(4)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("un_number");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_equipment_requirement__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<GateAuthorization>(entity =>
        {
            entity.HasKey(e => e.GateAuthorizationId).HasName("pk_gate_authorization");

            entity.ToTable("gate_authorization", "gate");

            entity.HasIndex(e => new { e.TenantId, e.ContainerNo, e.MovementCode }, "ix_gate_authorization__barrier").HasFilter("([consumed_by_gate_transaction_id] IS NULL AND [revoked_at] IS NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.BookingId }, "ix_gate_authorization__booking").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.ConsumedByGateTransactionId }, "uq_gate_authorization__consumer")
                .IsUnique()
                .HasFilter("([consumed_by_gate_transaction_id] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.BookingId, e.ContainerNo, e.MovementCode }, "uq_gate_authorization__live")
                .IsUnique()
                .HasFilter("([consumed_by_gate_transaction_id] IS NULL AND [revoked_at] IS NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.CouponRef }, "uq_gate_authorization__ref")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.SourceEventId }, "uq_gate_authorization__source_event")
                .IsUnique()
                .HasFilter("([source_event_id] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.Property(e => e.GateAuthorizationId)
                .HasDefaultValueSql("(newsequentialid())", "df_gate_authorization__id")
                .HasColumnName("gate_authorization_id");
            entity.Property(e => e.Amount)
                .HasColumnType("decimal(18, 4)")
                .HasColumnName("amount");
            entity.Property(e => e.BookingId).HasColumnName("booking_id");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.ConsumedAt).HasColumnName("consumed_at");
            entity.Property(e => e.ConsumedByGateTransactionId).HasColumnName("consumed_by_gate_transaction_id");
            entity.Property(e => e.ContainerNo)
                .HasMaxLength(11)
                .IsUnicode(false)
                .HasColumnName("container_no");
            entity.Property(e => e.CouponRef)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("coupon_ref");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_gate_authorization__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CurrencyCode)
                .HasMaxLength(3)
                .IsUnicode(false)
                .IsFixedLength()
                .HasColumnName("currency_code");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.MovementCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("movement_code");
            entity.Property(e => e.PaymentChannel)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("payment_channel");
            entity.Property(e => e.RevokeReason)
                .HasMaxLength(300)
                .HasColumnName("revoke_reason");
            entity.Property(e => e.RevokedAt).HasColumnName("revoked_at");
            entity.Property(e => e.RevokedBy).HasColumnName("revoked_by");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.SourceEventId).HasColumnName("source_event_id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_gate_authorization__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.ValidFrom).HasColumnName("valid_from");
            entity.Property(e => e.ValidUntil).HasColumnName("valid_until");
            entity.Property(e => e.TruckCategoryCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("truck_category_code");
            entity.Property(e => e.HaulierPartyCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("haulier_party_code");
        });

        modelBuilder.Entity<GateTransaction>(entity =>
        {
            entity.HasKey(e => e.GateTransactionId).HasName("pk_gate_transaction");

            entity.ToTable("gate_transaction", "gate");

            entity.HasIndex(e => new { e.TenantId, e.BookingId }, "ix_gate_transaction__booking").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.ContainerNo, e.TransactionAt }, "ix_gate_transaction__container")
                .IsDescending(false, false, true)
                .HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.BranchId, e.TransactionAt }, "ix_gate_transaction__day").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.BranchId, e.TransactionAt }, "ix_gate_transaction__unpriced").HasFilter("([price_snapshot_json] IS NULL AND [status]='COMPLETED' AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.TruckVisitId }, "ix_gate_transaction__visit").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.EirNo }, "uq_gate_transaction__eir_no")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.MovementPlanId }, "uq_gate_transaction__plan")
                .IsUnique()
                .HasFilter("([status]='COMPLETED' AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.TruckVisitId, e.Direction, e.PositionNo }, "uq_gate_transaction__position")
                .IsUnique()
                .HasFilter("([status]='COMPLETED' AND [deleted_at] IS NULL)");

            entity.Property(e => e.GateTransactionId)
                .HasDefaultValueSql("(newsequentialid())", "df_gate_transaction__id")
                .HasColumnName("gate_transaction_id");
            entity.Property(e => e.BookingContainerId).HasColumnName("booking_container_id");
            entity.Property(e => e.BookingId).HasColumnName("booking_id");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.CargoWeightKg)
                .HasColumnType("decimal(10, 2)")
                .HasColumnName("cargo_weight_kg");
            entity.Property(e => e.CheckDigitOverrideBy).HasColumnName("check_digit_override_by");
            entity.Property(e => e.CheckDigitOverrideReason)
                .HasMaxLength(300)
                .HasColumnName("check_digit_override_reason");
            entity.Property(e => e.ClipOnNo)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("clip_on_no");
            entity.Property(e => e.ConditionCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("condition_code");
            entity.Property(e => e.ContainerId).HasColumnName("container_id");
            entity.Property(e => e.ContainerNo)
                .HasMaxLength(11)
                .IsUnicode(false)
                .HasColumnName("container_no");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_gate_transaction__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CustomsPermitNo)
                .HasMaxLength(40)
                .HasColumnName("customs_permit_no");
            entity.Property(e => e.CutoffAtApplied).HasColumnName("cutoff_at_applied");
            entity.Property(e => e.CutoffExceptionId).HasColumnName("cutoff_exception_id");
            entity.Property(e => e.CutoffKindApplied)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("cutoff_kind_applied");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.Direction)
                .HasMaxLength(3)
                .IsUnicode(false)
                .HasColumnName("direction");
            entity.Property(e => e.EirNo)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("eir_no");
            entity.Property(e => e.EquipmentTypeCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("equipment_type_code");
            entity.Property(e => e.EquipmentTypeId).HasColumnName("equipment_type_id");
            entity.Property(e => e.FullEmpty)
                .HasMaxLength(5)
                .IsUnicode(false)
                .HasColumnName("full_empty");
            entity.Property(e => e.GateAuthorizationId).HasColumnName("gate_authorization_id");
            entity.Property(e => e.GensetNo)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("genset_no");
            entity.Property(e => e.GradeCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("grade_code");
            entity.Property(e => e.GrossWeightKg)
                .HasColumnType("decimal(10, 2)")
                .HasColumnName("gross_weight_kg");
            entity.Property(e => e.HumidityPct)
                .HasColumnType("decimal(5, 2)")
                .HasColumnName("humidity_pct");
            entity.Property(e => e.IsCheckDigitValid).HasColumnName("is_check_digit_valid");
            entity.Property(e => e.IsLate).HasColumnName("is_late");
            entity.Property(e => e.IsoCode)
                .HasMaxLength(4)
                .IsUnicode(false)
                .HasColumnName("iso_code");
            entity.Property(e => e.LateOverrideBy).HasColumnName("late_override_by");
            entity.Property(e => e.LateOverrideReason)
                .HasMaxLength(300)
                .HasColumnName("late_override_reason");
            entity.Property(e => e.LinePartyCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("line_party_code");
            entity.Property(e => e.LinePartyId).HasColumnName("line_party_id");
            entity.Property(e => e.MaterialCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("material_code");
            entity.Property(e => e.MaxGrossWeightKg)
                .HasColumnType("decimal(10, 2)")
                .HasColumnName("max_gross_weight_kg");
            entity.Property(e => e.MovementCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("movement_code");
            entity.Property(e => e.MovementId).HasColumnName("movement_id");
            entity.Property(e => e.MovementPlanId).HasColumnName("movement_plan_id");
            entity.Property(e => e.NextLocationCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("next_location_code");
            entity.Property(e => e.PaperlessCode)
                .HasMaxLength(40)
                .IsUnicode(false)
                .HasColumnName("paperless_code");
            entity.Property(e => e.PositionNo)
                .HasDefaultValue((byte)1, "df_gate_transaction__position")
                .HasColumnName("position_no");
            entity.Property(e => e.PositionText)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("position_text");
            entity.Property(e => e.PriceSnapshotJson).HasColumnName("price_snapshot_json");
            entity.Property(e => e.RecordedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_gate_transaction__recorded_at")
                .HasColumnName("recorded_at");
            entity.Property(e => e.Remarks)
                .HasMaxLength(500)
                .HasColumnName("remarks");
            entity.Property(e => e.ReplacesGateTransactionId).HasColumnName("replaces_gate_transaction_id");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.SealMismatch).HasColumnName("seal_mismatch");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("COMPLETED", "df_gate_transaction__status")
                .HasColumnName("status");
            entity.Property(e => e.SurveyId).HasColumnName("survey_id");
            entity.Property(e => e.TareWeightKg)
                .HasColumnType("decimal(10, 2)")
                .HasColumnName("tare_weight_kg");
            entity.Property(e => e.TempObservedC)
                .HasColumnType("decimal(5, 2)")
                .HasColumnName("temp_observed_c");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TransactionAt).HasColumnName("transaction_at");
            entity.Property(e => e.TripTypeCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("trip_type_code");
            entity.Property(e => e.TruckVisitId).HasColumnName("truck_visit_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_gate_transaction__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.VentSetting)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("vent_setting");
            entity.Property(e => e.VesselCallId).HasColumnName("vessel_call_id");
            entity.Property(e => e.VgmKg)
                .HasColumnType("decimal(10, 2)")
                .HasColumnName("vgm_kg");
            entity.Property(e => e.VgmMethod)
                .HasMaxLength(3)
                .IsUnicode(false)
                .HasColumnName("vgm_method");
            entity.Property(e => e.VoidReason)
                .HasMaxLength(300)
                .HasColumnName("void_reason");
            entity.Property(e => e.VoidedAt).HasColumnName("voided_at");
            entity.Property(e => e.VoidedBy).HasColumnName("voided_by");
            entity.Property(e => e.WeightSource)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("weight_source");
            entity.Property(e => e.YardId).HasColumnName("yard_id");
            entity.Property(e => e.YardSlotId).HasColumnName("yard_slot_id");
        });

        modelBuilder.Entity<GateTransactionSeal>(entity =>
        {
            entity.HasKey(e => e.GateTransactionSealId).HasName("pk_gate_transaction_seal");

            entity.ToTable("gate_transaction_seal", "gate");

            entity.HasIndex(e => new { e.TenantId, e.GateTransactionId }, "ix_gate_transaction_seal__transaction").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.GateTransactionId, e.SealType, e.SealNo }, "uq_gate_transaction_seal__one")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.GateTransactionSealId)
                .HasDefaultValueSql("(newsequentialid())", "df_gate_transaction_seal__id")
                .HasColumnName("gate_transaction_seal_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_gate_transaction_seal__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.GateTransactionId).HasColumnName("gate_transaction_id");
            entity.Property(e => e.IsIntact)
                .HasDefaultValue(true, "df_gate_transaction_seal__intact")
                .HasColumnName("is_intact");
            entity.Property(e => e.MatchesDeclared).HasColumnName("matches_declared");
            entity.Property(e => e.Remarks)
                .HasMaxLength(300)
                .HasColumnName("remarks");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.SealNo)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("seal_no");
            entity.Property(e => e.SealType)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("seal_type");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_gate_transaction_seal__updated_at")
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
            entity.Property(e => e.IsActive).HasColumnName("is_active");
            entity.Property(e => e.IsLicensable).HasColumnName("is_licensable");
            entity.Property(e => e.IsOperational).HasColumnName("is_operational");
            entity.Property(e => e.ReplicatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_module__replicated_at")
                .HasColumnName("replicated_at");
            entity.Property(e => e.SortOrder).HasColumnName("sort_order");
        });

        modelBuilder.Entity<MovementPlan>(entity =>
        {
            entity.HasKey(e => e.MovementPlanId).HasName("pk_movement_plan");

            entity
                .ToTable("movement_plan", "booking")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("booking_movement_plan", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.BookingContainerId, e.SequenceNo }, "ix_movement_plan__pending").HasFilter("([status]='PENDING' AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.GateTransactionId }, "uq_movement_plan__gate_transaction")
                .IsUnique()
                .HasFilter("([gate_transaction_id] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.BookingContainerId, e.SequenceNo }, "uq_movement_plan__sequence")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.MovementPlanId)
                .HasDefaultValueSql("(newsequentialid())", "df_movement_plan__id")
                .HasColumnName("movement_plan_id");
            entity.Property(e => e.BookingContainerId).HasColumnName("booking_container_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_movement_plan__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.GateTransactionId).HasColumnName("gate_transaction_id");
            entity.Property(e => e.IsRequired).HasColumnName("is_required");
            entity.Property(e => e.MovementCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("movement_code");
            entity.Property(e => e.MovementId).HasColumnName("movement_id");
            entity.Property(e => e.OrderTypeMovementId).HasColumnName("order_type_movement_id");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.SequenceNo).HasColumnName("sequence_no");
            entity.Property(e => e.SkipReason)
                .HasMaxLength(300)
                .HasColumnName("skip_reason");
            entity.Property(e => e.SkippedAt).HasColumnName("skipped_at");
            entity.Property(e => e.SkippedBy).HasColumnName("skipped_by");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("PENDING", "df_movement_plan__status")
                .HasColumnName("status");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_movement_plan__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<ReeferPowerSession>(entity =>
        {
            entity.HasKey(e => e.ReeferPowerSessionId).HasName("pk_reefer_power_session");

            entity
                .ToTable("reefer_power_session", "yard")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("yard_reefer_power_session", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.BranchId, e.PluggedOutAt }, "ix_reefer_power_session__branch").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.CloseGateTransactionId }, "ix_reefer_power_session__gate_close").HasFilter("([close_gate_transaction_id] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.ContainerVisitId }, "ix_reefer_power_session__visit").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.ContainerVisitId }, "uq_reefer_power_session__open")
                .IsUnique()
                .HasFilter("([plugged_out_at] IS NULL AND [deleted_at] IS NULL)");

            entity.Property(e => e.ReeferPowerSessionId)
                .HasDefaultValueSql("(newsequentialid())", "df_reefer_power_session__id")
                .HasColumnName("reefer_power_session_id");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.CloseGateTransactionId).HasColumnName("close_gate_transaction_id");
            entity.Property(e => e.CloseReason)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("close_reason");
            entity.Property(e => e.ContainerNo)
                .HasMaxLength(11)
                .IsUnicode(false)
                .HasColumnName("container_no");
            entity.Property(e => e.ContainerVisitId).HasColumnName("container_visit_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_reefer_power_session__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.PlugPointCode)
                .HasMaxLength(20)
                .HasColumnName("plug_point_code");
            entity.Property(e => e.PluggedInAt).HasColumnName("plugged_in_at");
            entity.Property(e => e.PluggedInBy).HasColumnName("plugged_in_by");
            entity.Property(e => e.PluggedOutAt).HasColumnName("plugged_out_at");
            entity.Property(e => e.PluggedOutBy).HasColumnName("plugged_out_by");
            entity.Property(e => e.Remarks)
                .HasMaxLength(500)
                .HasColumnName("remarks");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.SetPointC)
                .HasColumnType("decimal(5, 1)")
                .HasColumnName("set_point_c");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_reefer_power_session__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<Survey>(entity =>
        {
            entity.HasKey(e => e.SurveyId).HasName("pk_survey");

            entity.ToTable("survey", "gate");

            entity.HasIndex(e => new { e.TenantId, e.ContainerNo, e.SurveyedAt }, "ix_survey__container")
                .IsDescending(false, false, true)
                .HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.ContainerVisitId, e.SurveyedAt }, "ix_survey__visit")
                .IsDescending(false, false, true)
                .HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.GateTransactionId }, "uq_survey__gate_transaction")
                .IsUnique()
                .HasFilter("([gate_transaction_id] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.Property(e => e.SurveyId)
                .HasDefaultValueSql("(newsequentialid())", "df_survey__id")
                .HasColumnName("survey_id");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.ConditionCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("condition_code");
            entity.Property(e => e.ContainerNo)
                .HasMaxLength(11)
                .IsUnicode(false)
                .HasColumnName("container_no");
            entity.Property(e => e.ContainerVisitId).HasColumnName("container_visit_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_survey__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.GateTransactionId).HasColumnName("gate_transaction_id");
            entity.Property(e => e.GradeCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("grade_code");
            entity.Property(e => e.IsServiceable)
                .HasDefaultValue(true, "df_survey__serviceable")
                .HasColumnName("is_serviceable");
            entity.Property(e => e.Remarks)
                .HasMaxLength(1000)
                .HasColumnName("remarks");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.SurveyType)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("survey_type");
            entity.Property(e => e.SurveyedAt).HasColumnName("surveyed_at");
            entity.Property(e => e.SurveyedBy).HasColumnName("surveyed_by");
            entity.Property(e => e.SurveyorName)
                .HasMaxLength(100)
                .HasColumnName("surveyor_name");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_survey__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<SurveyDamage>(entity =>
        {
            entity.HasKey(e => e.SurveyDamageId).HasName("pk_survey_damage");

            entity.ToTable("survey_damage", "gate");

            entity.HasIndex(e => new { e.TenantId, e.SurveyId }, "ix_survey_damage__survey").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.SurveyId, e.LineNo }, "uq_survey_damage__line")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.SurveyDamageId)
                .HasDefaultValueSql("(newsequentialid())", "df_survey_damage__id")
                .HasColumnName("survey_damage_id");
            entity.Property(e => e.ComponentCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("component_code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_survey_damage__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DamageCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("damage_code");
            entity.Property(e => e.DamageLocationCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("damage_location_code");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.IsPreExisting).HasColumnName("is_pre_existing");
            entity.Property(e => e.LengthCm)
                .HasColumnType("decimal(7, 1)")
                .HasColumnName("length_cm");
            entity.Property(e => e.LineNo).HasColumnName("line_no");
            entity.Property(e => e.Quantity)
                .HasDefaultValue((short)1, "df_survey_damage__quantity")
                .HasColumnName("quantity");
            entity.Property(e => e.Remarks)
                .HasMaxLength(500)
                .HasColumnName("remarks");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.SurveyId).HasColumnName("survey_id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_survey_damage__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.WidthCm)
                .HasColumnType("decimal(7, 1)")
                .HasColumnName("width_cm");
        });

        modelBuilder.Entity<TruckVisit>(entity =>
        {
            entity.HasKey(e => e.TruckVisitId).HasName("pk_truck_visit");

            entity.ToTable("truck_visit", "gate");

            entity.HasIndex(e => new { e.TenantId, e.BranchId, e.ArrivedAt }, "ix_truck_visit__day").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.BranchId, e.TruckPlate }, "ix_truck_visit__open").HasFilter("([gate_out_at] IS NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.SlotBookingId }, "ix_truck_visit__slot").HasFilter("([slot_booking_id] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.VisitNo }, "uq_truck_visit__no")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.TruckVisitId)
                .HasDefaultValueSql("(newsequentialid())", "df_truck_visit__id")
                .HasColumnName("truck_visit_id");
            entity.Property(e => e.ArrivedAt).HasColumnName("arrived_at");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_truck_visit__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.DriverLicenceHash)
                .HasMaxLength(32)
                .HasColumnName("driver_licence_hash");
            entity.Property(e => e.DriverName)
                .HasMaxLength(100)
                .HasColumnName("driver_name");
            entity.Property(e => e.DwellMinutes)
                .HasComputedColumnSql("(datediff(minute,[arrived_at],[gate_out_at]))", true)
                .HasColumnName("dwell_minutes");
            entity.Property(e => e.GateInAt).HasColumnName("gate_in_at");
            entity.Property(e => e.GateOutAt).HasColumnName("gate_out_at");
            entity.Property(e => e.HaulierPartyCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("haulier_party_code");
            entity.Property(e => e.HaulierPartyId).HasColumnName("haulier_party_id");
            entity.Property(e => e.LaneCode)
                .HasMaxLength(10)
                .IsUnicode(false)
                .HasColumnName("lane_code");
            entity.Property(e => e.Remarks)
                .HasMaxLength(500)
                .HasColumnName("remarks");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.SlotBookingId).HasColumnName("slot_booking_id");
            entity.Property(e => e.Source)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("GATE", "df_truck_visit__source")
                .HasColumnName("source");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TrailerPlate)
                .HasMaxLength(20)
                .HasColumnName("trailer_plate");
            entity.Property(e => e.TruckCategoryCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("truck_category_code");
            entity.Property(e => e.TruckPlate)
                .HasMaxLength(20)
                .HasColumnName("truck_plate");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_truck_visit__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.VisitNo)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("visit_no");
        });

        modelBuilder.Entity<VesselCall>(entity =>
        {
            entity.HasKey(e => e.VesselCallId).HasName("pk_vessel_call");

            entity
                .ToTable("vessel_call", "vessel")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("vessel_vessel_call", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.Etd }, "ix_vessel_call__etd").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.VesselId, e.PortId, e.OperatorVoyageOut }, "uq_vessel_call__operator_voyage")
                .IsUnique()
                .HasFilter("([operator_voyage_out] IS NOT NULL AND [deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.CallRef }, "uq_vessel_call__ref")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.VesselCallId)
                .HasDefaultValueSql("(newsequentialid())", "df_vessel_call__id")
                .HasColumnName("vessel_call_id");
            entity.Property(e => e.Ata).HasColumnName("ata");
            entity.Property(e => e.Atb).HasColumnName("atb");
            entity.Property(e => e.Atd).HasColumnName("atd");
            entity.Property(e => e.CallRef)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("call_ref");
            entity.Property(e => e.CancelReason)
                .HasMaxLength(500)
                .HasColumnName("cancel_reason");
            entity.Property(e => e.CancelledAt).HasColumnName("cancelled_at");
            entity.Property(e => e.CancelledBy).HasColumnName("cancelled_by");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_vessel_call__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.Eta).HasColumnName("eta");
            entity.Property(e => e.Etb).HasColumnName("etb");
            entity.Property(e => e.Etd).HasColumnName("etd");
            entity.Property(e => e.IsCancelled).HasColumnName("is_cancelled");
            entity.Property(e => e.LegacyVesselScheduleIds)
                .HasMaxLength(200)
                .IsUnicode(false)
                .HasColumnName("legacy_vessel_schedule_ids");
            entity.Property(e => e.LadenReleaseAt).HasColumnName("laden_release_at");
            entity.Property(e => e.OperatorVoyageIn)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("operator_voyage_in");
            entity.Property(e => e.OperatorVoyageOut)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("operator_voyage_out");
            entity.Property(e => e.PortCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("port_code");
            entity.Property(e => e.PortId).HasColumnName("port_id");
            entity.Property(e => e.Remarks)
                .HasMaxLength(500)
                .HasColumnName("remarks");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.Source)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("MANUAL", "df_vessel_call__source")
                .HasColumnName("source");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TerminalCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("terminal_code");
            entity.Property(e => e.TerminalLocationId).HasColumnName("terminal_location_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_vessel_call__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.VesselCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("vessel_code");
            entity.Property(e => e.VesselId).HasColumnName("vessel_id");
        });

        modelBuilder.Entity<VesselCallCutoff>(entity =>
        {
            entity.HasKey(e => e.VesselCallCutoffId).HasName("pk_vessel_call_cutoff");

            entity
                .ToTable("vessel_call_cutoff", "vessel")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("vessel_vessel_call_cutoff", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.VesselCallId, e.CutoffKind, e.LinePartyId, e.BranchId }, "uq_vessel_call_cutoff__scope")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.VesselCallCutoffId)
                .HasDefaultValueSql("(newsequentialid())", "df_vessel_call_cutoff__id")
                .HasColumnName("vessel_call_cutoff_id");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_vessel_call_cutoff__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CutoffAt).HasColumnName("cutoff_at");
            entity.Property(e => e.CutoffKind)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("cutoff_kind");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.DerivedLeadHours).HasColumnName("derived_lead_hours");
            entity.Property(e => e.LinePartyCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("line_party_code");
            entity.Property(e => e.LinePartyId).HasColumnName("line_party_id");
            entity.Property(e => e.Remarks)
                .HasMaxLength(300)
                .HasColumnName("remarks");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.Source)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("MANUAL", "df_vessel_call_cutoff__source")
                .HasColumnName("source");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_vessel_call_cutoff__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.VesselCallId).HasColumnName("vessel_call_id");
        });

        modelBuilder.Entity<VesselCallLine>(entity =>
        {
            entity.HasKey(e => e.VesselCallLineId).HasName("pk_vessel_call_line");

            entity
                .ToTable("vessel_call_line", "vessel")
                .ToTable(tb => tb.IsTemporal(ttb =>
                    {
                        ttb.UseHistoryTable("vessel_vessel_call_line", "history");
                        ttb
                            .HasPeriodStart("sys_valid_from")
                            .HasColumnName("sys_valid_from");
                        ttb
                            .HasPeriodEnd("sys_valid_to")
                            .HasColumnName("sys_valid_to");
                    }));

            entity.HasIndex(e => new { e.TenantId, e.LinePartyId, e.VoyageOut }, "ix_vessel_call_line__voyage").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.VesselCallId, e.LinePartyId }, "uq_vessel_call_line__line")
                .IsUnique()
                .HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.VesselCallLineId)
                .HasDefaultValueSql("(newsequentialid())", "df_vessel_call_line__id")
                .HasColumnName("vessel_call_line_id");
            entity.Property(e => e.AgentPartyCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("agent_party_code");
            entity.Property(e => e.AgentPartyId).HasColumnName("agent_party_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_vessel_call_line__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.LinePartyCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("line_party_code");
            entity.Property(e => e.LinePartyId).HasColumnName("line_party_id");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.ServiceCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("service_code");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_vessel_call_line__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.VesselCallId).HasColumnName("vessel_call_id");
            entity.Property(e => e.VoyageIn)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("voyage_in");
            entity.Property(e => e.VoyageOut)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("voyage_out");
        });

        modelBuilder.Entity<VisitEvent>(entity =>
        {
            entity.HasKey(e => e.VisitEventId).HasName("pk_visit_event");

            entity.ToTable("visit_event", "yard");

            entity.HasIndex(e => new { e.TenantId, e.EventAt }, "ix_visit_event__day").HasFilter("([deleted_at] IS NULL)");

            entity.HasIndex(e => new { e.TenantId, e.ContainerVisitId, e.VisitEventId }, "ix_visit_event__visit").HasFilter("([deleted_at] IS NULL)");

            entity.Property(e => e.VisitEventId).HasColumnName("visit_event_id");
            entity.Property(e => e.ContainerVisitId).HasColumnName("container_visit_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_visit_event__created_at")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.DeletedBy).HasColumnName("deleted_by");
            entity.Property(e => e.EventAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_visit_event__event_at")
                .HasColumnName("event_at");
            entity.Property(e => e.EventBy).HasColumnName("event_by");
            entity.Property(e => e.EventType)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("event_type");
            entity.Property(e => e.FromValue)
                .HasMaxLength(100)
                .HasColumnName("from_value");
            entity.Property(e => e.ReferenceId).HasColumnName("reference_id");
            entity.Property(e => e.Remarks)
                .HasMaxLength(500)
                .HasColumnName("remarks");
            entity.Property(e => e.RowVersion)
                .IsRowVersion()
                .IsConcurrencyToken()
                .HasColumnName("row_version");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.ToValue)
                .HasMaxLength(100)
                .HasColumnName("to_value");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("((sysutcdatetime() AT TIME ZONE 'UTC'))", "df_visit_event__updated_at")
                .HasColumnName("updated_at");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<VwActiveHold>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("vw_active_hold", "yard");

            entity.Property(e => e.AppliedAt).HasColumnName("applied_at");
            entity.Property(e => e.ApplyReason)
                .HasMaxLength(500)
                .HasColumnName("apply_reason");
            entity.Property(e => e.BookingId).HasColumnName("booking_id");
            entity.Property(e => e.ContainerHoldId).HasColumnName("container_hold_id");
            entity.Property(e => e.ContainerNo)
                .HasMaxLength(11)
                .IsUnicode(false)
                .HasColumnName("container_no");
            entity.Property(e => e.HeldVia)
                .HasMaxLength(9)
                .IsUnicode(false)
                .HasColumnName("held_via");
            entity.Property(e => e.HoldCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("hold_code");
            entity.Property(e => e.HoldId).HasColumnName("hold_id");
            entity.Property(e => e.Source)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("source");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
        });

        modelBuilder.Entity<VwBookingDefect>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("vw_booking_defects", "booking");

            entity.Property(e => e.BookingId).HasColumnName("booking_id");
            entity.Property(e => e.DefectCode)
                .HasMaxLength(49)
                .IsUnicode(false)
                .HasColumnName("defect_code");
            entity.Property(e => e.Detail)
                .HasMaxLength(40)
                .IsUnicode(false)
                .HasColumnName("detail");
            entity.Property(e => e.SubjectId).HasColumnName("subject_id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
        });

        modelBuilder.Entity<VwBookingProgress>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("vw_booking_progress", "booking");

            entity.Property(e => e.BookingId).HasColumnName("booking_id");
            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.LinePartyCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("line_party_code");
            entity.Property(e => e.OrderNo)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("order_no");
            entity.Property(e => e.OrderTypeCode)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("order_type_code");
            entity.Property(e => e.ProgressStatus)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("progress_status");
            entity.Property(e => e.QtyAssigned).HasColumnName("qty_assigned");
            entity.Property(e => e.QtyCompleted).HasColumnName("qty_completed");
            entity.Property(e => e.QtyOpen).HasColumnName("qty_open");
            entity.Property(e => e.QtyRequired).HasColumnName("qty_required");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("status");
            entity.Property(e => e.StepsDone).HasColumnName("steps_done");
            entity.Property(e => e.StepsPendingRequired).HasColumnName("steps_pending_required");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.ValidFrom).HasColumnName("valid_from");
            entity.Property(e => e.ValidTo).HasColumnName("valid_to");
        });

        modelBuilder.Entity<VwContainerInYard>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("vw_container_in_yard", "yard");

            entity.Property(e => e.BranchId).HasColumnName("branch_id");
            entity.Property(e => e.ConditionCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("condition_code");
            entity.Property(e => e.ContainerNo)
                .HasMaxLength(11)
                .IsUnicode(false)
                .HasColumnName("container_no");
            entity.Property(e => e.ContainerVisitId).HasColumnName("container_visit_id");
            entity.Property(e => e.CurrentBookingContainerId).HasColumnName("current_booking_container_id");
            entity.Property(e => e.DaysInYard).HasColumnName("days_in_yard");
            entity.Property(e => e.EquipmentTypeCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("equipment_type_code");
            entity.Property(e => e.FullEmpty)
                .HasMaxLength(5)
                .IsUnicode(false)
                .HasColumnName("full_empty");
            entity.Property(e => e.GateInAt).HasColumnName("gate_in_at");
            entity.Property(e => e.GateInEirNo)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("gate_in_eir_no");
            entity.Property(e => e.GateInMovementCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("gate_in_movement_code");
            entity.Property(e => e.GradeCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("grade_code");
            entity.Property(e => e.IsHeld).HasColumnName("is_held");
            entity.Property(e => e.LastEventAt).HasColumnName("last_event_at");
            entity.Property(e => e.LinePartyCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("line_party_code");
            entity.Property(e => e.LinePartyId).HasColumnName("line_party_id");
            entity.Property(e => e.PositionText)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("position_text");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.YardId).HasColumnName("yard_id");
            entity.Property(e => e.YardSlotId).HasColumnName("yard_slot_id");
        });

        modelBuilder.Entity<VwVesselCallDefect>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("vw_vessel_call_defects", "vessel");

            entity.Property(e => e.DefectCode)
                .HasMaxLength(23)
                .IsUnicode(false)
                .HasColumnName("defect_code");
            entity.Property(e => e.Detail)
                .HasMaxLength(111)
                .IsUnicode(false)
                .HasColumnName("detail");
            entity.Property(e => e.SubjectId).HasColumnName("subject_id");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.VesselCallId).HasColumnName("vessel_call_id");
        });

        modelBuilder.Entity<VwVesselCallStatus>(entity =>
        {
            entity
                .HasNoKey()
                .ToView("vw_vessel_call_status", "vessel");

            entity.Property(e => e.Ata).HasColumnName("ata");
            entity.Property(e => e.Atb).HasColumnName("atb");
            entity.Property(e => e.Atd).HasColumnName("atd");
            entity.Property(e => e.CallRef)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("call_ref");
            entity.Property(e => e.CallStatus)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("call_status");
            entity.Property(e => e.Eta).HasColumnName("eta");
            entity.Property(e => e.Etb).HasColumnName("etb");
            entity.Property(e => e.Etd).HasColumnName("etd");
            entity.Property(e => e.LastYardCutoffAt).HasColumnName("last_yard_cutoff_at");
            entity.Property(e => e.PortCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("port_code");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.TerminalCode)
                .HasMaxLength(30)
                .IsUnicode(false)
                .HasColumnName("terminal_code");
            entity.Property(e => e.VesselCallId).HasColumnName("vessel_call_id");
            entity.Property(e => e.VesselCode)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasColumnName("vessel_code");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
