using Microsoft.EntityFrameworkCore;
using SporAkademi.Application.Groups;
using SporAkademi.Domain.Identity;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Groups;

/// <summary>
/// Transaction-scoped PostgreSQL advisory locks (DEC-148). The two-key form keeps the coach-schedule
/// keys apart from other advisory locks; a hash collision between coaches only serializes a little more.
/// Waits are bounded by the unit of work's <c>lock_timeout</c> and then fail with <c>Lock.Timeout</c>.
/// </summary>
internal sealed class GroupLocks(AppDbContext db) : IGroupLocks
{
    /// <summary>Namespace of the coach schedule locks ("CSCH" as bytes).</summary>
    private const int CoachScheduleSpace = 0x43534348;

    public async Task LockCoachScheduleAsync(UserId coachId, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Group locks are only taken inside the unit of work's transaction.");
        }

        var key = KeyOf(coachId.Value);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({CoachScheduleSpace}, {key})", cancellationToken);
    }

    internal static int KeyOf(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        var key = 0;
        for (var i = 0; i < bytes.Length; i += 4)
        {
            key ^= BitConverter.ToInt32(bytes[i..(i + 4)]);
        }

        return key;
    }
}
