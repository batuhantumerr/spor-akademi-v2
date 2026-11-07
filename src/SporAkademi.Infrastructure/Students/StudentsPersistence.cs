using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Students;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Students;

internal sealed class StudentConfiguration : IEntityTypeConfiguration<Student>
{
    public void Configure(EntityTypeBuilder<Student> builder)
    {
        builder.ToTable("students", Schemas.Students, table =>
        {
            table.HasCheckConstraint("ck_students_left_at", "(status = 'Left') = (left_at IS NOT NULL)");
            // DEC-363: birth date and emergency contact are cleared only by anonymization, which needs Left.
            table.HasCheckConstraint(
                "ck_students_personal_data",
                "anonymized_at IS NOT NULL OR (birth_date IS NOT NULL AND emergency_contact_name IS NOT NULL)");
            table.HasCheckConstraint("ck_students_anonymized_left", "anonymized_at IS NULL OR status = 'Left'");
        });
        builder.HasKey(s => s.Id);
        // ADR-0003 layer 3: (tenant_id, id) is the target of composite foreign keys (guardian links, enrollments).
        builder.HasAlternateKey(s => new { s.TenantId, s.Id }).HasName("ak_students_tenant_id_id");
        builder.HasOne<School>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(s => s.FirstName).HasMaxLength(PersonNames.MaxLength);
        builder.Property(s => s.LastName).HasMaxLength(PersonNames.MaxLength);
        builder.Property(s => s.SearchKey).HasMaxLength(Student.SearchKeyMaxLength);
        // DEC-150: substring search scans one school's rows; the tenant prefix keeps that small.
        builder.HasIndex(s => new { s.TenantId, s.SearchKey });
        // P2-F5: the daily job looks for departed students not yet anonymized; the partial index stays small.
        builder.HasIndex(s => new { s.TenantId, s.LeftAt })
            .HasDatabaseName("ix_students_tenant_id_left_at_not_anonymized")
            .HasFilter("status = 'Left' AND anonymized_at IS NULL");
        builder.ComplexProperty(s => s.EmergencyContact, contact =>
        {
            contact.Property(c => c.Name).HasColumnName("emergency_contact_name").HasMaxLength(EmergencyContact.NameMaxLength);
            contact.Property(c => c.Phone).HasColumnName("emergency_contact_phone");
            contact.Property(c => c.Relationship).HasColumnName("emergency_contact_relationship").HasMaxLength(EmergencyContact.RelationshipMaxLength);
        });

        builder.HasMany(s => s.Guardians).WithOne()
            .HasForeignKey(g => new { g.TenantId, g.StudentId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(s => s.Guardians).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Ignore(s => s.DomainEvents);
    }
}

internal sealed class StudentGuardianConfiguration : IEntityTypeConfiguration<StudentGuardian>
{
    public void Configure(EntityTypeBuilder<StudentGuardian> builder)
    {
        builder.ToTable("student_guardians", Schemas.Students);
        builder.HasKey(g => new { g.StudentId, g.GuardianId });
        // Composite FK: a student can only be linked to a guardian of the same school.
        builder.HasOne<Guardian>().WithMany()
            .HasForeignKey(g => new { g.TenantId, g.GuardianId })
            .HasPrincipalKey(g => new { g.TenantId, g.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class GuardianConfiguration : IEntityTypeConfiguration<Guardian>
{
    public void Configure(EntityTypeBuilder<Guardian> builder)
    {
        builder.ToTable("guardians", Schemas.Students);
        builder.HasKey(g => g.Id);
        builder.HasAlternateKey(g => new { g.TenantId, g.Id });
        builder.HasOne<School>().WithMany().HasForeignKey(g => g.TenantId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(g => g.FirstName).HasMaxLength(PersonNames.MaxLength);
        builder.Property(g => g.LastName).HasMaxLength(PersonNames.MaxLength);
        // Appendix B §4.7: one guardian per phone in a school, so siblings share it (US-13).
        builder.HasIndex(g => new { g.TenantId, g.Phone }).IsUnique();
        builder.ComplexProperty(g => g.Address, address =>
        {
            address.Property(a => a.Line).HasColumnName("address_line").HasMaxLength(Address.LineMaxLength);
            address.Property(a => a.District).HasColumnName("address_district").HasMaxLength(Address.PartMaxLength);
            address.Property(a => a.City).HasColumnName("address_city").HasMaxLength(Address.PartMaxLength);
        });
        builder.Ignore(g => g.DomainEvents);
    }
}

internal sealed class StudentRepository(AppDbContext db) : IStudentRepository
{
    public Task<Student?> GetAsync(StudentId id, CancellationToken cancellationToken) =>
        db.Set<Student>().Include(s => s.Guardians).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);

    public void Add(Student student) => db.Add(student);

    public Task<bool> ExistsAsync(StudentId id, CancellationToken cancellationToken) =>
        db.Set<Student>().AnyAsync(s => s.Id == id, cancellationToken);

    public Task<bool> IsAnonymizedAsync(StudentId id, CancellationToken cancellationToken) =>
        db.Set<Student>().AnyAsync(s => s.Id == id && s.AnonymizedAt != null, cancellationToken);

    public void ExpectVersion(Student student, uint version)
    {
        var entry = db.Entry(student);
        entry.Property<uint>(ConcurrencyToken.PropertyName).OriginalValue = version;
        // Always issue the version-checked UPDATE, so a no-op edit (or a guardian-only change) with a stale version still conflicts.
        entry.State = EntityState.Modified;
    }

    public async Task<StudentDto?> FindDtoAsync(StudentId id, CancellationToken cancellationToken)
    {
        var student = await db.Set<Student>().AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new
            {
                s.Id,
                s.FirstName,
                s.LastName,
                s.BirthDate,
                s.Gender,
                s.Status,
                s.LeftAt,
                s.Phone,
                s.Email,
                // The columns are null once the student is anonymized (DEC-363).
                ContactName = (string?)s.EmergencyContact!.Name,
                ContactPhone = (PhoneNumber?)s.EmergencyContact!.Phone,
                ContactRelationship = (string?)s.EmergencyContact!.Relationship,
                s.AnonymizedAt,
                Version = EF.Property<uint>(s, ConcurrencyToken.PropertyName),
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (student is null)
        {
            return null;
        }

        var guardians = await (
                from link in db.Set<StudentGuardian>().AsNoTracking()
                join guardian in db.Set<Guardian>().AsNoTracking() on link.GuardianId equals guardian.Id
                where link.StudentId == id
                orderby guardian.LastName, guardian.FirstName, guardian.Id
                select new { guardian.Id, link.Relationship, guardian.FirstName, guardian.LastName, guardian.Phone, guardian.Email })
            .ToListAsync(cancellationToken);

        return new StudentDto(
            student.Id.Value,
            student.FirstName,
            student.LastName,
            student.BirthDate,
            student.Gender,
            student.Status,
            student.LeftAt,
            student.Phone?.Value,
            student.Email?.Value,
            student is { ContactName: { } name, ContactPhone: { } phone, ContactRelationship: { } relationship }
                ? new EmergencyContactDto(name, phone.Value, relationship)
                : null,
            [.. guardians.Select(g => new StudentGuardianDto(g.Id.Value, g.Relationship, g.FirstName, g.LastName, g.Phone.Value, g.Email?.Value))],
            student.AnonymizedAt,
            student.Version);
    }

    public async Task<PagedResult<StudentListItemDto>> SearchAsync(StudentSearch search, PageRequest page, CancellationToken cancellationToken)
    {
        var query = db.Set<Student>().AsNoTracking();
        if (search.SearchKey is { Length: > 0 } key)
        {
            query = query.Where(s => s.SearchKey.Contains(key));
        }

        if (search.Status is { } status)
        {
            query = query.Where(s => s.Status == status);
        }

        if (search.OnlyStudents is { } only)
        {
            var ids = only.ToList();
            query = query.Where(s => ids.Contains(s.Id));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await Sort(query, search.Sort)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .Select(s => new StudentListItemDto(s.Id.Value, s.FirstName, s.LastName, s.BirthDate, s.Gender, s.Status, s.LeftAt, s.AnonymizedAt))
            .ToListAsync(cancellationToken);
        return new PagedResult<StudentListItemDto>(items, page.Page, page.PageSize, total);
    }

    public async Task<IReadOnlyList<StudentId>> ListDueForAnonymizationAsync(
        DateOnly cutoff,
        IReadOnlyCollection<StudentId> exclude,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exclude);
        var query = db.Set<Student>().AsNoTracking()
            .Where(s => s.Status == StudentStatus.Left && s.LeftAt <= cutoff && s.AnonymizedAt == null);
        if (exclude.Count > 0)
        {
            var excluded = exclude.ToList();
            query = query.Where(s => !excluded.Contains(s.Id));
        }

        return await query.OrderBy(s => s.Id).Take(limit).Select(s => s.Id).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Student>> GetManyAsync(IReadOnlyCollection<StudentId> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var list = ids.ToList();
        return await db.Set<Student>().Include(s => s.Guardians).Where(s => list.Contains(s.Id)).ToListAsync(cancellationToken);
    }

    private static IQueryable<Student> Sort(IQueryable<Student> query, StudentSort sort) => sort switch
    {
        StudentSort.LastNameDescending => query.OrderByDescending(s => s.LastName).ThenByDescending(s => s.FirstName).ThenByDescending(s => s.Id),
        StudentSort.CreatedAt => query.OrderBy(s => EF.Property<DateTimeOffset>(s, AuditColumns.CreatedAt)).ThenBy(s => s.Id),
        StudentSort.CreatedAtDescending => query.OrderByDescending(s => EF.Property<DateTimeOffset>(s, AuditColumns.CreatedAt)).ThenByDescending(s => s.Id),
        _ => query.OrderBy(s => s.LastName).ThenBy(s => s.FirstName).ThenBy(s => s.Id),
    };
}

internal sealed class GuardianRepository(AppDbContext db) : IGuardianRepository
{
    public Task<Guardian?> GetAsync(GuardianId id, CancellationToken cancellationToken) =>
        db.Set<Guardian>().SingleOrDefaultAsync(g => g.Id == id, cancellationToken);

    public Task<bool> ExistsAsync(GuardianId id, CancellationToken cancellationToken) =>
        db.Set<Guardian>().AnyAsync(g => g.Id == id, cancellationToken);

    public async Task<GuardianId?> FindIdByPhoneAsync(PhoneNumber phone, CancellationToken cancellationToken)
    {
        // The phone is unique per school, so there is at most one.
        var match = await db.Set<Guardian>().Where(g => g.Phone == phone).Select(g => new { g.Id }).SingleOrDefaultAsync(cancellationToken);
        return match?.Id;
    }

    public Task<bool> PhoneExistsAsync(PhoneNumber phone, GuardianId? except, CancellationToken cancellationToken) =>
        db.Set<Guardian>().AnyAsync(g => g.Phone == phone && (except == null || g.Id != except), cancellationToken);

    public void Add(Guardian guardian) => db.Add(guardian);

    public void Remove(Guardian guardian) => db.Remove(guardian);

    public Task<bool> IsLinkedToAnyStudentAsync(GuardianId id, CancellationToken cancellationToken) =>
        db.Set<StudentGuardian>().AnyAsync(l => l.GuardianId == id, cancellationToken);

    public void ExpectVersion(Guardian guardian, uint version)
    {
        var entry = db.Entry(guardian);
        entry.Property<uint>(ConcurrencyToken.PropertyName).OriginalValue = version;
        entry.State = EntityState.Modified;
    }

    public Task<GuardianDto?> FindDtoAsync(GuardianId id, CancellationToken cancellationToken) =>
        FirstDtoAsync(db.Set<Guardian>().Where(g => g.Id == id), cancellationToken);

    public Task<GuardianDto?> FindDtoByPhoneAsync(PhoneNumber phone, CancellationToken cancellationToken) =>
        FirstDtoAsync(db.Set<Guardian>().Where(g => g.Phone == phone), cancellationToken);

    private static async Task<GuardianDto?> FirstDtoAsync(IQueryable<Guardian> query, CancellationToken cancellationToken)
    {
        var row = await query.AsNoTracking()
            .Select(g => new { Guardian = g, Version = EF.Property<uint>(g, ConcurrencyToken.PropertyName) })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var g = row.Guardian;
        return new GuardianDto(
            g.Id.Value,
            g.FirstName,
            g.LastName,
            g.Phone.Value,
            g.Email?.Value,
            g.Address is { } a ? new GuardianAddressDto(a.Line, a.District, a.City) : null,
            row.Version);
    }
}
