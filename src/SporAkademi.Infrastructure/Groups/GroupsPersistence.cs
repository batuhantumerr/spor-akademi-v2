using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Groups;
using SporAkademi.Application.Groups.Contracts;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Groups;

internal sealed class GroupConfiguration : IEntityTypeConfiguration<Group>
{
    public void Configure(EntityTypeBuilder<Group> builder)
    {
        builder.ToTable("groups", Schemas.Groups);
        builder.HasKey(g => g.Id);
        builder.HasAlternateKey(g => new { g.TenantId, g.Id }).HasName("ak_groups_tenant_id_id");
        builder.HasOne<School>().WithMany().HasForeignKey(g => g.TenantId).OnDelete(DeleteBehavior.Restrict);

        // ADR-0003 layer 3: a group can only reference a sport or facility of its own school. These
        // keys also stop deleting a sport or facility a group (even an archived one) still uses.
        builder.HasOne<Sport>().WithMany()
            .HasForeignKey(g => new { g.TenantId, g.SportId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Facility>().WithMany()
            .HasForeignKey(g => new { g.TenantId, g.FacilityId })
            .HasPrincipalKey(f => new { f.TenantId, f.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(g => new { g.TenantId, g.CoachId });

        builder.Property(g => g.Name).HasMaxLength(Group.NameMaxLength);
        builder.Property(g => g.NameKey).HasMaxLength(Group.NameMaxLength);
        // ADR-0003 layer 4: names are unique per school.
        builder.HasIndex(g => new { g.TenantId, g.NameKey }).IsUnique();

        builder.ComplexProperty(g => g.BirthYearRange, range =>
        {
            range.Property(r => r.From).HasColumnName("birth_year_from");
            range.Property(r => r.To).HasColumnName("birth_year_to");
        });

        // The schedule is part of the aggregate, always loaded and replaced as a whole: one jsonb
        // column instead of a child table (DEC-140). EF needs an IList, so the private list is mapped and
        // the read-only view is ignored.
        builder.Ignore(g => g.ScheduleSlots);
        builder.ComplexCollection<List<ScheduleSlot>, ScheduleSlot>(Group.ScheduleSlotsField, slots =>
        {
            slots.ToJson("schedule_slots");
            slots.Property(s => s.Day).HasConversion<string>();
            slots.ComplexProperty(s => s.Time);
        });

        // Fees are a child table, not jsonb like the schedule (DEC-180): money is a numeric(12,2) column with a
        // check, one fee per period is a unique index (the backstop for concurrent definitions), and Billing
        // reads fee histories of many groups with one indexed query.
        builder.Ignore(g => g.Fees);
        builder.HasMany<GroupFee>(Group.FeesField).WithOne().HasForeignKey(f => f.GroupId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(Group.FeesField).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_groups_capacity", $"capacity BETWEEN 1 AND {Group.MaxCapacity}");
            t.HasCheckConstraint("ck_groups_birth_years", "birth_year_from <= birth_year_to");
        });
        builder.Ignore(g => g.DomainEvents);
    }
}

internal sealed class GroupFeeConfiguration : IEntityTypeConfiguration<GroupFee>
{
    public const string UniquePeriodIndex = "ix_group_fees_group_id_effective_from";

    public void Configure(EntityTypeBuilder<GroupFee> builder)
    {
        builder.ToTable("group_fees", Schemas.Groups, t => t.HasCheckConstraint("ck_group_fees_amount", "amount >= 0"));
        builder.HasKey(f => f.Id);
        // Client-generated ids: a fee added to the collection is new, never an update.
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.HasIndex(f => new { f.GroupId, f.EffectiveFrom }).IsUnique().HasDatabaseName(UniquePeriodIndex);
        builder.ComplexProperty(f => f.Amount, MoneyMapping.Configure);
    }
}

internal sealed class GroupRepository(AppDbContext db) : IGroupRepository
{
    public Task<Group?> GetAsync(GroupId id, CancellationToken cancellationToken) =>
        db.Set<Group>().SingleOrDefaultAsync(g => g.Id == id, cancellationToken);

    public Task<bool> NameKeyExistsAsync(string nameKey, GroupId? except, CancellationToken cancellationToken) =>
        db.Set<Group>().AnyAsync(g => g.NameKey == nameKey && (except == null || g.Id != except), cancellationToken);

    public void Add(Group group) => db.Add(group);

    public void ExpectVersion(Group group, uint version)
    {
        var entry = db.Entry(group);
        entry.Property<uint>(ConcurrencyToken.PropertyName).OriginalValue = version;
        // Always issue the version-checked UPDATE, so a no-op edit with a stale version still conflicts.
        entry.State = EntityState.Modified;
    }

    public async Task<IReadOnlyList<GroupWeek>> ListActiveWeeksAsync(
        GroupId except,
        UserId? coachId,
        FacilityId facilityId,
        CancellationToken cancellationToken)
    {
        var groups = await db.Set<Group>().AsNoTracking()
            .Where(g => g.Id != except && g.Status == GroupStatus.Active
                && (g.FacilityId == facilityId || (coachId != null && g.CoachId == coachId)))
            .ToListAsync(cancellationToken);
        return [.. groups.Select(GroupWeek.Of)];
    }

    public async Task<IReadOnlyList<Group>> ListByCoachAsync(UserId coachId, CancellationToken cancellationToken) =>
        await db.Set<Group>().Where(g => g.CoachId == coachId).ToListAsync(cancellationToken);

    public async Task<GroupSnapshot?> FindAsync(GroupId id, CancellationToken cancellationToken) =>
        (await Snapshots(db.Set<Group>().Where(g => g.Id == id)).ToListAsync(cancellationToken)).SingleOrDefault();

    public async Task<PagedResult<GroupSnapshot>> ListAsync(GroupFilter filter, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);

        var query = db.Set<Group>().AsQueryable();
        if (filter.SportId is { } sportId)
        {
            query = query.Where(g => g.SportId == sportId);
        }

        if (filter.CoachId is { } coachId)
        {
            query = query.Where(g => g.CoachId == coachId);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(g => g.Status == status);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await Snapshots(query.OrderBy(g => g.NameKey).ThenBy(g => g.Id).Skip(page.Skip).Take(page.PageSize)).ToListAsync(cancellationToken);
        return new PagedResult<GroupSnapshot>(items, page.Page, page.PageSize, total);
    }

    public async Task<IReadOnlyList<GroupSnapshot>> ListActiveOfCoachAsync(UserId coachId, CancellationToken cancellationToken) =>
        await Snapshots(db.Set<Group>()
                .Where(g => g.CoachId == coachId && g.Status == GroupStatus.Active)
                .OrderBy(g => g.NameKey))
            .ToListAsync(cancellationToken);

    public Task<Group?> GetWithFeesAsync(GroupId id, CancellationToken cancellationToken) =>
        db.Set<Group>().Include(Group.FeesField).SingleOrDefaultAsync(g => g.Id == id, cancellationToken);

    public async Task<IReadOnlyList<GroupFee>?> FindFeesAsync(GroupId id, CancellationToken cancellationToken) =>
        (await db.Set<Group>().AsNoTracking().Include(Group.FeesField).SingleOrDefaultAsync(g => g.Id == id, cancellationToken))?.Fees;

    private static IQueryable<GroupSnapshot> Snapshots(IQueryable<Group> query) =>
        query.AsNoTracking().Select(g => new GroupSnapshot(g, EF.Property<uint>(g, ConcurrencyToken.PropertyName)));
}

/// <summary>Organization asks before deleting a sport or facility (US-06, US-07, DEC-131).</summary>
internal sealed class GroupUsage(AppDbContext db) : IGroupUsage
{
    public Task<bool> AnyGroupUsesSportAsync(SportId sportId, CancellationToken cancellationToken) =>
        db.Set<Group>().AnyAsync(g => g.SportId == sportId, cancellationToken);

    public Task<bool> AnyActiveGroupUsesFacilityAsync(FacilityId facilityId, CancellationToken cancellationToken) =>
        db.Set<Group>().AnyAsync(g => g.FacilityId == facilityId && g.Status == GroupStatus.Active, cancellationToken);
}

/// <summary>Fee histories for Billing. Read through the groups, so the tenant filter applies (group fees have none of their own).</summary>
internal sealed class GroupFeeSchedule(AppDbContext db) : IGroupFeeSchedule
{
    public async Task<IReadOnlyDictionary<GroupId, IReadOnlyList<GroupFeeInfo>>> GetFeesAsync(
        IReadOnlyCollection<GroupId> groupIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(groupIds);
        if (groupIds.Count == 0)
        {
            return new Dictionary<GroupId, IReadOnlyList<GroupFeeInfo>>();
        }

        var ids = groupIds.Distinct().ToList();
        var groups = await db.Set<Group>().AsNoTracking().Include(Group.FeesField).Where(g => ids.Contains(g.Id)).ToListAsync(cancellationToken);
        return groups
            .Where(g => g.Fees.Count > 0)
            .ToDictionary(
                g => g.Id,
                g => (IReadOnlyList<GroupFeeInfo>)[.. g.Fees.Select(f => new GroupFeeInfo(f.EffectiveFrom, f.Amount))]);
    }
}
