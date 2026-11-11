using FluentValidation;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Data;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Abstractions.Outbox;
using SporAkademi.Application.Students.Contracts;
using SporAkademi.Application.Students.Documents;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Application.Students;

/// <summary>What one school's anonymization run applies (DEC-362), fixed when the run starts.</summary>
/// <param name="Today">Today in the school's time zone.</param>
/// <param name="Cutoff">Students who left on or before this date are due.</param>
/// <param name="Now">The <c>AnonymizedAt</c> stamp of the run.</param>
public sealed record AnonymizationPlan(DateOnly Today, DateOnly Cutoff, int PersonalDataMonths, DateTimeOffset Now);

/// <param name="Anonymized">The students anonymized by this batch.</param>
/// <param name="Skipped">Due students that could not be anonymized now (a concurrent change); the next run retries them.</param>
/// <remarks>Ids of the batch in neither list were no longer due when loaded (e.g. reactivated meanwhile).</remarks>
public sealed record AnonymizationBatchResult(IReadOnlyList<StudentId> Anonymized, IReadOnlyList<StudentId> Skipped);

/// <summary>A run of the anonymization job as the School Admin sees it: counts and dates only (DEC-364).</summary>
public sealed record AnonymizationRunDto(
    Guid Id,
    DateTimeOffset StartedAt,
    long DurationMs,
    DateOnly Cutoff,
    int PersonalDataMonths,
    int AnonymizedCount,
    int SkippedCount,
    AnonymizationRunStatus Status);

/// <summary>The latest runs of the caller's school, newest first.</summary>
public sealed record ListAnonymizationRunsQuery : IQuery<IReadOnlyList<AnonymizationRunDto>>
{
    public const int Limit = 20;
}

/// <summary>Runs of the current school (tenant filter applies).</summary>
public interface IAnonymizationRunRepository
{
    void Add(AnonymizationRun run);

    Task<IReadOnlyList<AnonymizationRunDto>> ListRecentAsync(int limit, CancellationToken cancellationToken);
}

/// <summary>
/// P2-F5: anonymizes the current school's departed students whose retention period is over (DEC-008, DEC-362). The
/// job calls it per school inside that school's background scope; it never reads another school's rows.
/// </summary>
public interface IStudentAnonymizer
{
    /// <summary>The school's cutoff for today; the same plan is used for every batch of the run.</summary>
    Task<AnonymizationPlan> PlanAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Ids of due students in id order, except <paramref name="seen"/>: the ids earlier batches of the run returned
    /// but did not anonymize, so a student that keeps failing cannot make the run loop.
    /// </summary>
    Task<IReadOnlyList<StudentId>> NextDueAsync(AnonymizationPlan plan, IReadOnlyCollection<StudentId> seen, int batchSize, CancellationToken cancellationToken);

    /// <summary>
    /// Anonymizes the batch in one transaction. When it conflicts with a concurrent change (xmin) or a lock, each
    /// student is retried in its own transaction, so one busy student does not hold back the others; a student that
    /// is no longer due (e.g. reactivated meanwhile) is left alone.
    /// </summary>
    Task<AnonymizationBatchResult> AnonymizeAsync(AnonymizationPlan plan, IReadOnlyList<StudentId> students, CancellationToken cancellationToken);

    /// <summary>Stores the run record (DEC-364) in its own transaction.</summary>
    Task RecordRunAsync(AnonymizationRun run, CancellationToken cancellationToken);

    /// <summary>
    /// DEC-368: deletes guardians no student is linked to (e.g. left over from before guardians were deleted on
    /// unlinking), at most <paramref name="limit"/>, in one transaction.
    /// </summary>
    /// <returns>The number of deleted guardians.</returns>
    Task<int> DeleteUnlinkedGuardiansAsync(int limit, CancellationToken cancellationToken);
}

internal sealed class StudentAnonymizer(
    IStudentRepository students,
    IGuardianRepository guardians,
    GuardianAftercare aftercare,
    IAnonymizationRunRepository runs,
    ITenancyModule tenancy,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider) : IStudentAnonymizer
{
    public async Task<AnonymizationPlan> PlanAsync(CancellationToken cancellationToken)
    {
        var tenant = currentUser.RequireTenant();
        var policy = await tenancy.RetentionPolicyAsync(tenant, cancellationToken)
            ?? throw new InvalidOperationException("The school of a background run must exist.");
        var today = await tenancy.TodayAsync(tenant, cancellationToken);
        return new AnonymizationPlan(today, policy.AnonymizationCutoff(today), policy.PersonalDataMonths, timeProvider.GetUtcNow());
    }

    public Task<IReadOnlyList<StudentId>> NextDueAsync(AnonymizationPlan plan, IReadOnlyCollection<StudentId> seen, int batchSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return students.ListDueForAnonymizationAsync(plan.Cutoff, seen, batchSize, cancellationToken);
    }

    public async Task<AnonymizationBatchResult> AnonymizeAsync(AnonymizationPlan plan, IReadOnlyList<StudentId> due, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(due);

        var batch = await AnonymizeInTransactionAsync(plan, due, cancellationToken);
        if (batch.IsSuccess)
        {
            return new AnonymizationBatchResult(batch.Value, []);
        }

        var anonymized = new List<StudentId>();
        var skipped = new List<StudentId>();
        foreach (var id in due)
        {
            var single = await AnonymizeInTransactionAsync(plan, [id], cancellationToken);
            if (single.IsSuccess)
            {
                anonymized.AddRange(single.Value);
            }
            else
            {
                skipped.Add(id);
            }
        }

        return new AnonymizationBatchResult(anonymized, skipped);
    }

    public async Task RecordRunAsync(AnonymizationRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var recorded = await unitOfWork.ExecuteInTransactionAsync(
            _ =>
            {
                runs.Add(run);
                return Task.FromResult(Result.Success(Unit.Value));
            },
            cancellationToken);
        if (recorded.IsFailure)
        {
            throw new InvalidOperationException($"The anonymization run could not be recorded: {recorded.Error.Code}.");
        }
    }

    public async Task<int> DeleteUnlinkedGuardiansAsync(int limit, CancellationToken cancellationToken)
    {
        var deleted = await unitOfWork.ExecuteInTransactionAsync(
            async ct => Result.Success(await aftercare.AfterUnlinkAsync(await guardians.ListUnlinkedAsync(limit, ct), ct)),
            cancellationToken);
        // A conflict (a guardian linked at the same moment) leaves the rest for the next run.
        return deleted.IsSuccess ? deleted.Value : 0;
    }

    private Task<Result<IReadOnlyList<StudentId>>> AnonymizeInTransactionAsync(AnonymizationPlan plan, IReadOnlyList<StudentId> ids, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                var loaded = await students.GetManyAsync(ids, ct);
                // Re-checked on fresh rows: a student reactivated since the ids were listed is not due any more.
                IReadOnlyList<StudentId> anonymized = [.. loaded.Where(s => s.Anonymize(plan.Cutoff, plan.Now).IsSuccess).Select(s => s.Id)];
                return Result.Success(anonymized);
            },
            cancellationToken);
}

/// <summary>
/// DEC-365: a guardian exists only for the students linked to it. When the last of them is anonymized the guardian
/// (name, phone, e-mail, address) is deleted; a guardian a sibling still needs stays as it is. Its parent account is
/// closed with it (DEC-379).
/// </summary>
internal sealed class DeleteGuardiansOfAnonymizedStudent(GuardianAftercare aftercare) : IDomainEventConsumer<StudentAnonymized>
{
    public async Task<Result> Handle(StudentAnonymized domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        await aftercare.AfterUnlinkAsync(domainEvent.FormerGuardians, cancellationToken);
        return Result.Success();
    }
}

/// <summary>
/// DEC-368: a guardian removed from a student and linked to no other student is deleted with it; DEC-379: its parent
/// account is closed, and so is the account of a guardian that keeps only students who left.
/// </summary>
internal sealed class DeleteGuardiansUnlinkedFromStudent(GuardianAftercare aftercare) : IDomainEventConsumer<StudentGuardiansUnlinked>
{
    public async Task<Result> Handle(StudentGuardiansUnlinked domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        await aftercare.AfterUnlinkAsync(domainEvent.Removed, cancellationToken);
        return Result.Success();
    }
}

/// <summary>
/// DEC-366: an anonymized student's documents (health reports, photo) are deleted: the metadata rows in this
/// transaction, the stored objects after commit through the existing <see cref="DocumentObjectDeletedMessage"/>
/// outbox message (DEC-325), which the outbox retries until the storage confirms. The per-student document lock keeps
/// a concurrent upload from slipping in; the upload re-checks the student under the same lock.
/// </summary>
internal sealed class DeleteDocumentsOfAnonymizedStudent(IStudentDocumentRepository documents, IOutbox outbox)
    : IDomainEventConsumer<StudentAnonymized>
{
    public async Task<Result> Handle(StudentAnonymized domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        await documents.LockStudentAsync(domainEvent.StudentId, cancellationToken);
        foreach (var document in await documents.ListOfStudentForDeletionAsync(domainEvent.StudentId, cancellationToken))
        {
            documents.Remove(document);
            outbox.Enqueue(new DocumentObjectDeletedMessage(document.StorageKey), domainEvent.TenantId.Value);
        }

        return Result.Success();
    }
}

/// <summary>What a retention period would mean today: the cutoff and how many departed students it makes due.</summary>
public sealed record RetentionImpactDto(int PersonalDataMonths, DateOnly Cutoff, int DueCount);

/// <param name="PersonalDataMonths">The period to try; the school's current period when null.</param>
public sealed record GetRetentionImpactQuery(int? PersonalDataMonths) : IQuery<RetentionImpactDto>;

internal sealed class GetRetentionImpactValidator : AbstractValidator<GetRetentionImpactQuery>
{
    public GetRetentionImpactValidator() =>
        RuleFor(q => q.PersonalDataMonths)
            .InclusiveBetween(RetentionPolicy.MinPersonalDataMonths, RetentionPolicy.MaxPersonalDataMonths)
            .When(q => q.PersonalDataMonths is not null)
            .WithErrorCode(CommonErrors.FieldInvalid.Code);
}

/// <summary>
/// P2-F5: lets the settings page warn before a shorter period is saved (DEC-361): the students it would make due are
/// anonymized irreversibly in the next nightly run.
/// </summary>
internal sealed class GetRetentionImpactHandler(IStudentRepository students, ITenancyModule tenancy, ICurrentUser currentUser)
    : IQueryHandler<GetRetentionImpactQuery, RetentionImpactDto>
{
    public async Task<Result<RetentionImpactDto>> Handle(GetRetentionImpactQuery query, CancellationToken cancellationToken)
    {
        var tenant = currentUser.RequireTenant();
        var current = await tenancy.RetentionPolicyAsync(tenant, cancellationToken);
        if (current is null)
        {
            return Result.Failure<RetentionImpactDto>(SchoolErrors.NotFound);
        }

        var policy = query.PersonalDataMonths is { } months ? RetentionPolicy.Create(months, current.PaymentYears) : Result.Success(current);
        if (policy.IsFailure)
        {
            return Result.Failure<RetentionImpactDto>(policy.Error);
        }

        var cutoff = policy.Value.AnonymizationCutoff(await tenancy.TodayAsync(tenant, cancellationToken));
        var due = await students.CountDueForAnonymizationAsync(cutoff, cancellationToken);
        return Result.Success(new RetentionImpactDto(policy.Value.PersonalDataMonths, cutoff, due));
    }
}

internal sealed class ListAnonymizationRunsHandler(IAnonymizationRunRepository runs)
    : IQueryHandler<ListAnonymizationRunsQuery, IReadOnlyList<AnonymizationRunDto>>
{
    public async Task<Result<IReadOnlyList<AnonymizationRunDto>>> Handle(ListAnonymizationRunsQuery query, CancellationToken cancellationToken) =>
        Result.Success(await runs.ListRecentAsync(ListAnonymizationRunsQuery.Limit, cancellationToken));
}
