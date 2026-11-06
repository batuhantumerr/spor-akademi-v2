using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using SporAkademi.IntegrationTests.Enrollments;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Attendance;

/// <summary>
/// A group led by coach Ayşe that trains on Tuesdays 17:00–18:00 and Thursdays 10:00–11:00 and
/// 17:00–18:00, with students enrolled since 2027-01-04. The clock says Thursday 2027-01-14 15:00 in
/// Istanbul: this morning's session is over, this evening's is still ahead.
/// </summary>
public abstract class AttendanceTest(DatabaseFixture database) : EnrollmentsTest(database)
{
    protected const string ThisMorning = "2027-01-14T10:00";
    protected const string ThisEvening = "2027-01-14T17:00";
    protected const string LastTuesday = "2027-01-12T17:00";
    protected const string LastThursday = "2027-01-07T10:00";

    protected Guid GroupId { get; private set; }

    protected Guid Ali { get; private set; }

    protected Guid Veli { get; private set; }

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        GroupId = IdOf(await GroupAsync(
            "U10 Futbol",
            20,
            Ayse,
            Slot("Tuesday", "17:00", "18:00"),
            Slot("Thursday", "10:00", "11:00"),
            Slot("Thursday", "17:00", "18:00")));
        Ali = await RegisterStudentAsync("Ali", "Yılmaz");
        Veli = await RegisterStudentAsync("Veli", "Çelik");
        await EnrollOkAsync(Ali, GroupId, "2027-01-04");
        await EnrollOkAsync(Veli, GroupId, "2027-01-04");
    }

    protected string SessionUrl(string key, Guid? groupId = null) => $"/api/v1/groups/{groupId ?? GroupId}/sessions/{key}/attendance";

    protected static object Records(params (Guid Student, string Status)[] marks) =>
        new { records = marks.Select(m => new { studentId = m.Student, status = m.Status }).ToArray() };

    protected Task<HttpResponseMessage> SaveAsync(HttpClient client, string key, params (Guid Student, string Status)[] marks) =>
        client.PutAsJsonAsync(SessionUrl(key), Records(marks), Ct);

    protected async Task<JsonElement> SaveOkAsync(HttpClient client, string key, params (Guid Student, string Status)[] marks)
    {
        var response = await SaveAsync(client, key, marks);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await BodyOf(response);
    }

    /// <summary>Status per student id from an attendance list response.</summary>
    protected static SortedDictionary<Guid, string?> StatusesOf(JsonElement list) =>
        new(list.GetProperty("students").EnumerateArray().ToDictionary(
            s => s.GetProperty("studentId").GetGuid(),
            s => s.GetProperty("status").ValueKind == JsonValueKind.Null ? null : s.GetProperty("status").GetString()));

    /// <summary>Reads the database directly (bypassing the tenant filter) to check what was stored.</summary>
    protected async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Database.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    protected async Task<List<object?[]>> RowsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Database.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(Ct))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = await reader.IsDBNullAsync(i, Ct) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Moves the clock forward; clients minted before then hold expired tokens, so make new ones.</summary>
    protected void AdvanceTo(DateTimeOffset utc) => Api.Clock.SetUtcNow(utc);
}
