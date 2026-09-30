using System.Linq.Expressions;
using System.Reflection;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Portal.Domain;
using Portal.Infrastructure.Identity;

namespace Portal.Infrastructure.Persistence;

public sealed class PortalDbContext(DbContextOptions<PortalDbContext> options, ITenantContext tenant)
    : IdentityUserContext<AppUser, Guid>(options)
{
    private static readonly MethodInfo ApplyTenantFilterMethod =
        typeof(PortalDbContext).GetMethod(nameof(ApplyTenantFilter), BindingFlags.NonPublic | BindingFlags.Static)!;

    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<Invitation> Invitations => Set<Invitation>();

    public DbSet<Project> Projects => Set<Project>();

    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();

    public DbSet<Milestone> Milestones => Set<Milestone>();

    public DbSet<Note> Notes => Set<Note>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    /// <summary>Read by the global query filters; EF re-evaluates it per context instance.</summary>
    public Guid? CurrentOrganizationId => tenant.OrganizationId;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Organization>(e =>
        {
            e.Property(o => o.Name).HasMaxLength(200);
            e.Property(o => o.Slug).HasMaxLength(100);
            e.HasIndex(o => o.Slug).IsUnique();
        });

        builder.Entity<AppUser>(e =>
        {
            e.Property(u => u.DisplayName).HasMaxLength(200);
            e.HasIndex(u => u.OrganizationId);
            e.HasOne<Organization>().WithMany().HasForeignKey(u => u.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Invitation>(e =>
        {
            e.Property(i => i.Email).HasMaxLength(256);
            e.Property(i => i.NormalizedEmail).HasMaxLength(256);
            e.Property(i => i.TokenHash).HasMaxLength(32);
            e.HasIndex(i => i.TokenHash).IsUnique();
            e.HasIndex(i => new { i.OrganizationId, i.NormalizedEmail });
            e.HasOne<Organization>().WithMany().HasForeignKey(i => i.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Project>(e =>
        {
            e.Property(p => p.Name).HasMaxLength(200);
            e.Property(p => p.Description).HasMaxLength(4000);
            e.HasIndex(p => new { p.OrganizationId, p.Status });
            e.HasOne<Organization>().WithMany().HasForeignKey(p => p.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ProjectMember>(e =>
        {
            e.HasIndex(m => new { m.ProjectId, m.UserId }).IsUnique();
            e.HasIndex(m => m.UserId);
            e.HasOne<Project>().WithMany().HasForeignKey(m => m.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Milestone>(e =>
        {
            e.Property(m => m.Title).HasMaxLength(200);
            e.Property(m => m.Description).HasMaxLength(4000);
            e.HasIndex(m => new { m.ProjectId, m.DueDate });
            e.HasOne<Project>().WithMany().HasForeignKey(m => m.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Note>(e =>
        {
            e.Property(n => n.Body).HasMaxLength(10000);
            e.HasIndex(n => new { n.ProjectId, n.CreatedAt });
            e.HasOne<Project>().WithMany().HasForeignKey(n => n.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(n => n.AuthorUserId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<IdempotencyRecord>(e =>
        {
            e.Property(r => r.Key).HasMaxLength(128);
            e.Property(r => r.RequestHash).HasMaxLength(32);
            e.Property(r => r.ContentType).HasMaxLength(200);
            e.Property(r => r.Location).HasMaxLength(500);
            e.Property(r => r.ETag).HasMaxLength(100);
            e.HasIndex(r => new { r.OrganizationId, r.UserId, r.Key }).IsUnique();
            e.HasIndex(r => r.CreatedAt);
        });

        // Organizations are only visible to their own members.
        builder.Entity<Organization>().HasQueryFilter(o => o.Id == CurrentOrganizationId);

        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;

            if (typeof(Entity).IsAssignableFrom(clrType))
            {
                builder.Entity(clrType).Property(nameof(Entity.RowVersion)).IsRowVersion();
            }

            if (typeof(ITenantScoped).IsAssignableFrom(clrType))
            {
                ApplyTenantFilterMethod.MakeGenericMethod(clrType).Invoke(null, [builder, this]);
            }
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceTenantOnWrite();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceTenantOnWrite();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private static void ApplyTenantFilter<TEntity>(ModelBuilder builder, PortalDbContext context)
        where TEntity : class, ITenantScoped
    {
        Expression<Func<TEntity, bool>> filter = e => e.OrganizationId == context.CurrentOrganizationId;
        builder.Entity<TEntity>().HasQueryFilter(filter);
    }

    /// <summary>
    /// Stamps new tenant-scoped rows with the current tenant and rejects any write that would create,
    /// change, or delete a row belonging to a different tenant (or to no tenant).
    /// </summary>
    private void EnforceTenantOnWrite()
    {
        var current = CurrentOrganizationId;

        foreach (var entry in ChangeTracker.Entries<ITenantScoped>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity.OrganizationId == Guid.Empty && current is not null)
                    {
                        entry.Entity.OrganizationId = current.Value;
                    }

                    if (current is null || entry.Entity.OrganizationId != current)
                    {
                        throw new TenantViolationException(
                            $"Cannot insert {entry.Metadata.ClrType.Name} outside the current tenant.");
                    }

                    break;

                case EntityState.Modified or EntityState.Deleted:
                    if (current is null || entry.Entity.OrganizationId != current
                        || entry.Property(nameof(ITenantScoped.OrganizationId)).IsModified)
                    {
                        throw new TenantViolationException(
                            $"Cannot modify {entry.Metadata.ClrType.Name} outside the current tenant.");
                    }

                    break;
            }
        }
    }
}

public sealed class TenantViolationException(string message) : InvalidOperationException(message);
