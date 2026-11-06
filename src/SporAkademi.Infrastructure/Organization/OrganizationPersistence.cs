using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Groups.Contracts;
using SporAkademi.Application.Organization;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Organization;

internal sealed class SportConfiguration : IEntityTypeConfiguration<Sport>
{
    public void Configure(EntityTypeBuilder<Sport> builder)
    {
        builder.ToTable("sports", Schemas.Organization);
        builder.HasKey(s => s.Id);
        // ADR-0003 layer 3: (tenant_id, id) is the target of composite foreign keys from other modules.
        builder.HasAlternateKey(s => new { s.TenantId, s.Id });
        builder.HasOne<School>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(s => s.Name).HasMaxLength(Sport.NameMaxLength);
        builder.Property(s => s.NameKey).HasMaxLength(Sport.NameMaxLength);
        // ADR-0003 layer 4: names are unique per school.
        builder.HasIndex(s => new { s.TenantId, s.NameKey }).IsUnique();
        builder.Ignore(s => s.DomainEvents);
    }
}

internal sealed class FacilityConfiguration : IEntityTypeConfiguration<Facility>
{
    public void Configure(EntityTypeBuilder<Facility> builder)
    {
        builder.ToTable("facilities", Schemas.Organization);
        builder.HasKey(f => f.Id);
        builder.HasAlternateKey(f => new { f.TenantId, f.Id });
        builder.HasOne<School>().WithMany().HasForeignKey(f => f.TenantId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(f => f.Name).HasMaxLength(Facility.NameMaxLength);
        builder.Property(f => f.NameKey).HasMaxLength(Facility.NameMaxLength);
        builder.HasIndex(f => new { f.TenantId, f.NameKey }).IsUnique();
        builder.ComplexProperty(f => f.Address, address =>
        {
            address.Property(a => a.Line).HasColumnName("address_line").HasMaxLength(Domain.SharedKernel.Address.LineMaxLength);
            address.Property(a => a.District).HasColumnName("address_district").HasMaxLength(Domain.SharedKernel.Address.PartMaxLength);
            address.Property(a => a.City).HasColumnName("address_city").HasMaxLength(Domain.SharedKernel.Address.PartMaxLength);
        });
        builder.ToTable(t => t.HasCheckConstraint("ck_facilities_capacity", "capacity IS NULL OR capacity BETWEEN 1 AND 10000"));
        builder.Ignore(f => f.DomainEvents);
    }
}

internal sealed class SportRepository(AppDbContext db) : ISportRepository
{
    public Task<Sport?> GetAsync(SportId id, CancellationToken cancellationToken) =>
        db.Set<Sport>().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);

    public Task<bool> NameKeyExistsAsync(string nameKey, SportId? except, CancellationToken cancellationToken) =>
        db.Set<Sport>().AnyAsync(s => s.NameKey == nameKey && (except == null || s.Id != except), cancellationToken);

    public void Add(Sport sport) => db.Add(sport);

    public void Remove(Sport sport) => db.Remove(sport);

    public void ExpectVersion(Sport sport, uint version) =>
        db.Entry(sport).Property<uint>(ConcurrencyToken.PropertyName).OriginalValue = version;

    public Task<SportDto?> FindDtoAsync(SportId id, CancellationToken cancellationToken) =>
        Project(db.Set<Sport>().Where(s => s.Id == id)).SingleOrDefaultAsync(cancellationToken);

    public async Task<PagedResult<SportDto>> ListAsync(bool includeArchived, PageRequest page, CancellationToken cancellationToken)
    {
        var query = db.Set<Sport>().Where(s => includeArchived || !s.IsArchived);
        var total = await query.CountAsync(cancellationToken);
        var items = await Project(query.OrderBy(s => s.NameKey).Skip(page.Skip).Take(page.PageSize)).ToListAsync(cancellationToken);
        return new PagedResult<SportDto>(items, page.Page, page.PageSize, total);
    }

    private static IQueryable<SportDto> Project(IQueryable<Sport> query) =>
        query.AsNoTracking().Select(s => new SportDto(
            s.Id.Value, s.Name, s.IsArchived, EF.Property<uint>(s, ConcurrencyToken.PropertyName)));
}

internal sealed class FacilityRepository(AppDbContext db) : IFacilityRepository
{
    public Task<Facility?> GetAsync(FacilityId id, CancellationToken cancellationToken) =>
        db.Set<Facility>().SingleOrDefaultAsync(f => f.Id == id, cancellationToken);

    public Task<bool> NameKeyExistsAsync(string nameKey, FacilityId? except, CancellationToken cancellationToken) =>
        db.Set<Facility>().AnyAsync(f => f.NameKey == nameKey && (except == null || f.Id != except), cancellationToken);

    public void Add(Facility facility) => db.Add(facility);

    public void Remove(Facility facility) => db.Remove(facility);

    public void ExpectVersion(Facility facility, uint version) =>
        db.Entry(facility).Property<uint>(ConcurrencyToken.PropertyName).OriginalValue = version;

    public Task<FacilityDto?> FindDtoAsync(FacilityId id, CancellationToken cancellationToken) =>
        Project(db.Set<Facility>().Where(f => f.Id == id)).SingleOrDefaultAsync(cancellationToken);

    public async Task<PagedResult<FacilityDto>> ListAsync(bool includeArchived, PageRequest page, CancellationToken cancellationToken)
    {
        var query = db.Set<Facility>().Where(f => includeArchived || !f.IsArchived);
        var total = await query.CountAsync(cancellationToken);
        var items = await Project(query.OrderBy(f => f.NameKey).Skip(page.Skip).Take(page.PageSize)).ToListAsync(cancellationToken);
        return new PagedResult<FacilityDto>(items, page.Page, page.PageSize, total);
    }

    private static IQueryable<FacilityDto> Project(IQueryable<Facility> query) =>
        query.AsNoTracking().Select(f => new FacilityDto(
            f.Id.Value,
            f.Name,
            f.Type,
            new AddressDto(f.Address.Line, f.Address.District, f.Address.City),
            f.Capacity,
            f.IsArchived,
            EF.Property<uint>(f, ConcurrencyToken.PropertyName)));
}

/// <summary>Placeholder until the Groups module exists (P1-18): no group uses anything yet.</summary>
internal sealed class NoGroupUsage : IGroupUsage
{
    public Task<bool> AnyGroupUsesSportAsync(SportId sportId, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<bool> AnyActiveGroupUsesFacilityAsync(FacilityId facilityId, CancellationToken cancellationToken) => Task.FromResult(false);
}
