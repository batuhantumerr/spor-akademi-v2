namespace SporAkademi.Notifications;

/// <summary>
/// P5-04 (DEC-630): <c>bash scripts/run-notifications.sh</c> (or <c>dotnet run --project src/SporAkademi.Notifications</c>).
/// An explicit entry class: a top-level <c>Program</c> would clash with the API's in the integration tests.
/// </summary>
internal static class NotificationsProgram
{
    public static async Task<int> Main(string[] args)
    {
        await using var app = NotificationsHost.Create(args);
        await app.RunAsync();
        return 0;
    }
}
