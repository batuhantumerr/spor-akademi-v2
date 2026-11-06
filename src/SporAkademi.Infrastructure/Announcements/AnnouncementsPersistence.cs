using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Announcements;
using SporAkademi.Domain.Announcements;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Announcements;

internal sealed class AnnouncementConfiguration : IEntityTypeConfiguration<Announcement>
{
    public void Configure(EntityTypeBuilder<Announcement> builder)
    {
        builder.ToTable("announcements", Schemas.Announcements);
        builder.HasKey(a => a.Id);
        builder.HasOne<School>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Restrict);
        // ADR-0003 layer 3: an announcement can only belong to a group of its own school.
        builder.HasOne<Group>().WithMany()
            .HasForeignKey(a => new { a.TenantId, a.GroupId })
            .HasPrincipalKey(g => new { g.TenantId, g.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => new { a.TenantId, a.GroupId, a.PublishedAt });

        builder.Property(a => a.Title).HasMaxLength(Announcement.TitleMaxLength);
        builder.Property(a => a.Body).HasMaxLength(Announcement.BodyMaxLength);
        builder.Ignore(a => a.DomainEvents);
    }
}

internal sealed class AnnouncementRepository(AppDbContext db) : IAnnouncementRepository
{
    public void Add(Announcement announcement) => db.Add(announcement);

    public Task<Announcement?> FindAsync(AnnouncementId id, CancellationToken cancellationToken) =>
        db.Set<Announcement>().AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken);

    public async Task<PagedResult<Announcement>> ListOfGroupsAsync(IReadOnlyCollection<GroupId> groupIds, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(groupIds);
        ArgumentNullException.ThrowIfNull(page);
        var ids = groupIds.ToList();
        var query = db.Set<Announcement>().AsNoTracking().Where(a => ids.Contains(a.GroupId));
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(a => a.PublishedAt).ThenByDescending(a => a.Id)
            .Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<Announcement>(items, page.Page, page.PageSize, total);
    }
}
