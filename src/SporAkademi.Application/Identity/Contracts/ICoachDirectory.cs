namespace SporAkademi.Application.Identity.Contracts;

/// <summary>A coach of the current school as other modules see it.</summary>
public sealed record CoachInfo(Guid Id, string FirstName, string LastName, bool IsActive);

/// <summary>Read contract of the Identity module for coaches of the current school (used by Groups).</summary>
public interface ICoachDirectory
{
    /// <summary>The coach with this id in the current school, or null (also for non-coach users and other schools).</summary>
    Task<CoachInfo?> FindAsync(Guid coachId, CancellationToken cancellationToken);

    /// <summary>Coaches of the current school among the given ids; unknown ids are omitted.</summary>
    Task<IReadOnlyDictionary<Guid, CoachInfo>> GetManyAsync(IReadOnlyCollection<Guid> coachIds, CancellationToken cancellationToken);
}
