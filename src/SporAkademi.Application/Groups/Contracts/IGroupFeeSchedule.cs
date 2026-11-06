using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Groups.Contracts;

/// <summary>A group's monthly fee from <see cref="EffectiveFrom"/> until the next one.</summary>
public sealed record GroupFeeInfo(YearMonth EffectiveFrom, Money Amount);

/// <summary>Read contract of the Groups module for fee histories (used by Billing, US-23, US-24).</summary>
public interface IGroupFeeSchedule
{
    /// <summary>Fee histories of the current school's groups among <paramref name="groupIds"/>; groups without fees or unknown ids are omitted.</summary>
    Task<IReadOnlyDictionary<GroupId, IReadOnlyList<GroupFeeInfo>>> GetFeesAsync(IReadOnlyCollection<GroupId> groupIds, CancellationToken cancellationToken);
}
