using Microsoft.EntityFrameworkCore;

namespace vizo_backend.Models;

/// <summary>
/// HAND-WRITTEN PARTIAL -- not produced by scaffolding.
///
/// AppDbContext.cs is scaffolded straight from the Neon database, so anything
/// added by hand inside it is wiped the next time somebody runs
/// dotnet ef dbcontext scaffold. Everything this project adds therefore lives
/// here instead, attached through the OnModelCreatingPartial hook that the
/// scaffolder leaves behind for exactly this purpose.
///
/// Result: not one scaffolded file is edited, so re-scaffolding is safe.
///
/// NOTE FOR A FUTURE RE-SCAFFOLD: the "PasswordResetCode" table now exists on
/// Neon, so a fresh scaffold WILL generate its own PasswordResetCode.cs and its
/// own DbSet. If you re-scaffold, delete this file and Models/PasswordResetCode.cs
/// first, or you will get duplicate-definition errors.
/// </summary>
public partial class AppDbContext
{
    /// <summary>
    /// The one table this project added to the database.
    /// Created on Neon by backend/database/06_neon_auth.sql.
    /// </summary>
    public virtual DbSet<PasswordResetCode> PasswordResetCodes { get; set; } = null!;

    /// <summary>
    /// Every PDF the API has generated and where it was pushed to.
    /// Created on Neon by backend/database/10_document_files.sql.
    /// </summary>
    public virtual DbSet<DocumentFile> DocumentFiles { get; set; } = null!;

    /// <summary>
    /// One row per BROWSER that has allowed notifications -- not per user.
    /// Created on Neon by backend/database/13_push_subscriptions.sql.
    /// </summary>
    public virtual DbSet<PushSubscription> PushSubscriptions { get; set; } = null!;

    /// <summary>
    /// Only the notification kinds somebody has deliberately switched off.
    /// A missing row means on.
    /// Created on Neon by backend/database/13_push_subscriptions.sql.
    /// </summary>
    public virtual DbSet<NotificationPreference> NotificationPreferences { get; set; } = null!;

    /// <summary>
    /// A salesperson asking permission to edit or delete an order.
    /// Created on Neon by backend/database/15_order_workflow.sql.
    /// </summary>
    public virtual DbSet<OrderChangeRequest> OrderChangeRequests { get; set; } = null!;

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PasswordResetCode>(entity =>
        {
            entity.HasKey(e => e.ResetId).HasName("PasswordResetCode_pkey");

            entity.ToTable("PasswordResetCode");

            entity.HasIndex(e => e.UserId, "ix_prc_user");

            entity.Property(e => e.CodeHash).HasMaxLength(200);
            entity.Property(e => e.Attempts).HasDefaultValue((short)0);

            /* .WithMany() with no navigation argument: the relationship is
               declared to EF without either class needing a navigation
               property, which is what keeps the scaffolded User.cs untouched. */
            entity.HasOne<User>().WithMany()
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("fk_prc_user");
        });

        /* ── columns added by backend/database/08_sales_documents.sql ──

           These live on scaffolded entities, so their lengths and defaults are
           declared here rather than in AppDbContext.cs -- same reason as
           everything else in this file: a re-scaffold must not lose them. */

        modelBuilder.Entity<Product>(entity =>
        {
            /* Landed cost is derived, never stored. */
            entity.Ignore(e => e.LandedCost);

            entity.Property(e => e.DutyPrice).HasPrecision(14, 2).HasDefaultValue(0m);
            entity.Property(e => e.MarginPrice).HasPrecision(14, 2).HasDefaultValue(0m);
        });

        modelBuilder.Entity<SalesInvoice>(entity =>
        {
            entity.Property(e => e.PdfUrl).HasMaxLength(500);
            entity.Property(e => e.PdfPublicId).HasMaxLength(255);
            entity.Property(e => e.IsWalkIn).HasDefaultValue(false);
            entity.Property(e => e.WalkInName).HasMaxLength(150);
            entity.Property(e => e.WalkInPhone).HasMaxLength(30);
        });

        modelBuilder.Entity<DocumentFile>(entity =>
        {
            entity.HasKey(e => e.FileId).HasName("DocumentFile_pkey");

            entity.ToTable("DocumentFile");

            /* One current file per document. Re-generating replaces the row
               rather than adding another, so the table stays the size of the
               document set instead of growing with every click. */
            entity.HasIndex(e => new { e.DocKind, e.DocKey }, "UX_DocumentFile_Doc").IsUnique();

            entity.Property(e => e.DocKind).HasMaxLength(40);
            entity.Property(e => e.DocKey).HasMaxLength(120);
            entity.Property(e => e.DocNo).HasMaxLength(60);
            entity.Property(e => e.FileName).HasMaxLength(160);
            entity.Property(e => e.PdfUrl).HasMaxLength(500);
            entity.Property(e => e.PdfPublicId).HasMaxLength(255);
            entity.Property(e => e.IsDeliverable).HasDefaultValue(false);
            entity.Property(e => e.GeneratedAt).HasColumnType("timestamp without time zone");

            entity.HasOne<User>().WithMany()
                .HasForeignKey(d => d.GeneratedByUserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_DocumentFile_User");
        });

        modelBuilder.Entity<SalesReturn>(entity =>
        {
            entity.Property(e => e.DecisionReason).HasMaxLength(300);

            /* Npgsql maps a bare DateTime to "timestamp WITH time zone" and
               then refuses to write one whose Kind is Unspecified -- which is
               exactly what ApiControllerBase.Now() produces, on purpose, for
               every other timestamp in this schema. Declaring the column type
               is what the scaffolder does for LoggedAt, VisitedAt and the
               rest; this column needs the same. */
            entity.Property(e => e.DecidedAt).HasColumnType("timestamp without time zone");

            entity.HasOne<User>().WithMany()
                .HasForeignKey(d => d.DecidedByUserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_SalesReturn_DecidedBy");
        });

        modelBuilder.Entity<SalesOrder>(entity =>
        {
            /* Added by 15_order_workflow.sql. See Models/SalesOrder.Custom.cs. */
            entity.Property(e => e.ConfirmRemindedAt).HasColumnType("timestamp without time zone");
        });

        /* "Notification"."Url" -- added in 16_warehouse_and_notification_links.sql
           so a row in the bell can be clicked through to the thing it is about.
           Declared here rather than in the scaffolded context because that file
           is regenerated from the database and would lose it. */
        modelBuilder.Entity<Notification>(entity =>
        {
            entity.Property(e => e.Url).HasColumnName("Url").HasMaxLength(300);
        });

        /* "Province"."Country" -- added in 17_party_country.sql, so that a
           supplier in Guangdong is known to be Chinese without anybody having
           to say so on the party row. Declared here rather than in the
           scaffolded context, which is regenerated and would lose it. */
        modelBuilder.Entity<Province>(entity =>
        {
            entity.Property(e => e.Country)
                  .HasColumnName("Country")
                  .HasColumnType("character(2)")
                  .HasDefaultValue("PK");
        });

        modelBuilder.Entity<OrderChangeRequest>(entity =>
        {
            entity.HasKey(e => e.RequestId).HasName("OrderChangeRequest_pkey");
            entity.ToTable("OrderChangeRequest");

            entity.Property(e => e.Kind).HasMaxLength(10);
            entity.Property(e => e.Reason).HasMaxLength(500);
            entity.Property(e => e.Status).HasMaxLength(10).HasDefaultValue("PENDING");
            entity.Property(e => e.DecisionNote).HasMaxLength(500);

            /* Same trap as everywhere else in this schema: the columns are
               "timestamp without time zone" and Npgsql will not write a
               DateTime whose Kind is Utc into one. */
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp without time zone");
            entity.Property(e => e.DecidedAt).HasColumnType("timestamp without time zone");

            entity.HasOne(e => e.Order).WithMany()
                .HasForeignKey(e => e.OrderId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("OrderChangeRequest_OrderId_fkey");

            entity.HasOne(e => e.RequestedByUser).WithMany()
                .HasForeignKey(e => e.RequestedByUserId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("OrderChangeRequest_RequestedBy_fkey");

            entity.HasOne(e => e.DecidedByUser).WithMany()
                .HasForeignKey(e => e.DecidedByUserId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("OrderChangeRequest_DecidedBy_fkey");
        });

        modelBuilder.Entity<PushSubscription>(entity =>
        {
            entity.HasKey(e => e.PushSubscriptionId).HasName("PushSubscription_pkey");
            entity.ToTable("PushSubscription");

            entity.Property(e => e.Endpoint).HasMaxLength(500);
            entity.Property(e => e.P256dh).HasMaxLength(255);
            entity.Property(e => e.Auth).HasMaxLength(255);
            entity.Property(e => e.UserAgent).HasMaxLength(300);

            /* Npgsql maps a bare DateTime to "timestamp WITH time zone" and
               then refuses one whose Kind is Unspecified. Same declaration the
               rest of this schema uses -- see SalesReturn.DecidedAt. */
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp without time zone");
            entity.Property(e => e.LastUsedAt).HasColumnType("timestamp without time zone");

            /* A browser re-subscribes on its own after a service-worker update.
               Without this the same person collects a row per update and gets
               every notification three or four times. */
            entity.HasIndex(e => e.Endpoint).IsUnique().HasDatabaseName("PushSubscription_Endpoint_key");

            entity.HasOne(e => e.User).WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("PushSubscription_UserId_fkey");
        });

        modelBuilder.Entity<NotificationPreference>(entity =>
        {
            entity.HasKey(e => e.PreferenceId).HasName("NotificationPreference_pkey");
            entity.ToTable("NotificationPreference");

            entity.Property(e => e.Kind).HasMaxLength(60);
            entity.Property(e => e.PushEnabled).HasDefaultValue(true);
            entity.Property(e => e.BellEnabled).HasDefaultValue(true);

            entity.HasIndex(e => new { e.UserId, e.Kind })
                .IsUnique()
                .HasDatabaseName("NotificationPreference_User_Kind_key");

            entity.HasOne(e => e.User).WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("NotificationPreference_UserId_fkey");
        });

        modelBuilder.Entity<SalesInvoice>(entity =>
        {
            /* Whether Cloudinary will actually SERVE the stored bill. Defaults
               to false because that is the truth for every row uploaded before
               the flag existed -- PDF delivery is switched off on the account
               they went to. See Models/SalesInvoice.Custom.cs and
               database/18_sales_scope_returns_and_places.sql. */
            entity.Property(e => e.PdfDeliverable).HasDefaultValue(false);
        });

        modelBuilder.Entity<PaymentMethod>(entity =>
        {
            /* Whether money can come IN this way. False for every row that
               predates the column, and set true for the owner's four by
               database/21_order_chain_and_receiving.sql -- see
               Models/PaymentMethod.Custom.cs. */
            entity.Property(e => e.IsForReceiving).HasDefaultValue(false);
        });

        modelBuilder.Entity<Party>(entity =>
        {
            /* The customer's documents -- six photographs and the PDF they are
               bound into. See Models/Party.Custom.cs and
               database/22_customer_documents.sql. */
            entity.Property(e => e.CnicFrontUrl).HasMaxLength(500);
            entity.Property(e => e.CnicBackUrl).HasMaxLength(500);
            entity.Property(e => e.CardFrontUrl).HasMaxLength(500);
            entity.Property(e => e.CardBackUrl).HasMaxLength(500);
            entity.Property(e => e.AffidavitFrontUrl).HasMaxLength(500);
            entity.Property(e => e.AffidavitBackUrl).HasMaxLength(500);
            entity.Property(e => e.LegalDocsPdfUrl).HasMaxLength(500);
            entity.Property(e => e.LegalDocsPdfId).HasMaxLength(255);

            /* Who opened the account, as opposed to which rep it is assigned
               to. SetNull rather than Cascade: deactivating the person who
               typed a customer in must not take the customer with them. */
            entity.HasOne(e => e.CreatedByUser).WithMany()
                .HasForeignKey(e => e.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_party_created_by");
        });

        modelBuilder.Entity<SalesOrderItem>(entity =>
        {
            /* What actually left the shelf for this line, once the order is
               dispatched. See Models/SalesOrderItem.Custom.cs and
               database/25_packing_and_claims_removed.sql. */
            entity.Property(e => e.DispatchedQty);
        });

        modelBuilder.Entity<JournalEntry>(entity =>
        {
            /* A reversed entry stays POSTED and points at the entry that undid
               it. See Models/JournalEntry.Custom.cs for why it is not simply
               un-posted, and database/12_journal_reversal_link.sql for the
               column. Self-referencing, so no cascade -- deleting a mirror must
               not take the original with it. */
            entity.HasOne(e => e.ReversedByEntry)
                .WithMany(e => e.Reverses)
                .HasForeignKey(e => e.ReversedByEntryId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("JournalEntry_ReversedByEntryId_fkey");
        });
    }
}
