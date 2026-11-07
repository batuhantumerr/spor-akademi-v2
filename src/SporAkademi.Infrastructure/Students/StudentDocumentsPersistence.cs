using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SporAkademi.Application.Students.Documents;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Students;

internal sealed class StudentDocumentConfiguration : IEntityTypeConfiguration<StudentDocument>
{
    /// <summary>At most one photo per student; a concurrent second upload fails here (mapped to a conflict).</summary>
    public const string OnePhotoIndex = "ix_student_documents_one_photo";

    public void Configure(EntityTypeBuilder<StudentDocument> builder)
    {
        builder.ToTable("student_documents", Schemas.Students, table =>
            table.HasCheckConstraint("ck_student_documents_size_bytes", "size_bytes > 0"));
        builder.HasKey(d => d.Id);
        builder.HasOne<School>().WithMany().HasForeignKey(d => d.TenantId).OnDelete(DeleteBehavior.Restrict);
        // ADR-0003 layer 3: a document can only belong to a student of its own school.
        builder.HasOne<Student>().WithMany()
            .HasForeignKey(d => new { d.TenantId, d.StudentId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(d => new { d.TenantId, d.StudentId, d.Type });
        builder.HasIndex(d => new { d.TenantId, d.StudentId })
            .IsUnique()
            .HasFilter("type = 'Photo'")
            .HasDatabaseName(OnePhotoIndex);
        builder.HasIndex(d => d.StorageKey).IsUnique();

        builder.Property(d => d.FileName).HasMaxLength(StudentDocument.FileNameMaxLength);
        builder.Property(d => d.StorageKey).HasMaxLength(StudentDocument.StorageKeyMaxLength);
        builder.Ignore(d => d.DomainEvents);
    }
}

internal sealed class StudentDocumentRepository(AppDbContext db) : IStudentDocumentRepository
{
    /// <summary>Namespace of the student document locks ("SDOC" as bytes).</summary>
    private const int DocumentSpace = 0x53444F43;

    public async Task LockStudentAsync(StudentId studentId, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Document locks are only taken inside the unit of work's transaction.");
        }

        var key = Groups.GroupLocks.KeyOf(studentId.Value);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({DocumentSpace}, {key})", cancellationToken);
    }

    public void Add(StudentDocument document) => db.Add(document);

    public void Remove(StudentDocument document) => db.Remove(document);

    public Task<StudentDocument?> FindAsync(StudentDocumentId id, CancellationToken cancellationToken) =>
        db.Set<StudentDocument>().SingleOrDefaultAsync(d => d.Id == id, cancellationToken);

    public Task<StudentDocument?> FindPhotoAsync(StudentId studentId, CancellationToken cancellationToken) =>
        db.Set<StudentDocument>().SingleOrDefaultAsync(d => d.StudentId == studentId && d.Type == DocumentType.Photo, cancellationToken);

    public Task<StudentDocument?> FindForReadingAsync(StudentDocumentId id, CancellationToken cancellationToken) =>
        db.Set<StudentDocument>().AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, cancellationToken);

    public Task<StudentDocument?> FindPhotoForReadingAsync(StudentId studentId, CancellationToken cancellationToken) =>
        db.Set<StudentDocument>().AsNoTracking().SingleOrDefaultAsync(d => d.StudentId == studentId && d.Type == DocumentType.Photo, cancellationToken);

    public Task<int> CountAsync(StudentId studentId, DocumentType type, CancellationToken cancellationToken) =>
        db.Set<StudentDocument>().CountAsync(d => d.StudentId == studentId && d.Type == type, cancellationToken);

    public async Task<IReadOnlyList<StudentDocument>> ListOfStudentAsync(StudentId studentId, CancellationToken cancellationToken) =>
        await db.Set<StudentDocument>().AsNoTracking()
            .Where(d => d.StudentId == studentId)
            .OrderBy(d => d.Type == DocumentType.Photo ? 0 : 1)
            .ThenByDescending(d => d.UploadedAt)
            .ThenByDescending(d => d.Id)
            .ToListAsync(cancellationToken);
}
