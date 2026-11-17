namespace SporAkademi.Domain.Identity;

/// <summary>
/// How long a session of one role lasts (P5-02, DEC-610): <paramref name="Sliding"/> is the longest gap between two
/// refreshes (a refresh token's life, which is also the idle timeout of the panel roles), <paramref name="Absolute"/>
/// the end of the session counted from the login, whatever the activity.
/// </summary>
public sealed record SessionPolicy(TimeSpan Sliding, TimeSpan Absolute)
{
    /// <summary>NFR-SEC-05 / DEC-032: 7 days sliding, 30 days absolute (the Parent role).</summary>
    public static readonly SessionPolicy Standard = new(TimeSpan.FromDays(7), TimeSpan.FromDays(30));

    /// <summary>ASVS L2 3.3.2: 30 minutes without a refresh, 12 hours at most (Platform Admin, School Admin, Coach).</summary>
    public static readonly SessionPolicy Panel = new(TimeSpan.FromMinutes(30), TimeSpan.FromHours(12));
}
