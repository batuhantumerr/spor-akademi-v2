using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SporAkademi.Application.Abstractions.Data;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Enrollments;

/// <summary>US-02 and ADR-0003 for enrollments: another school's students, groups and enrollments do not exist.</summary>
[Trait("Story", "US-02")]
public sealed class EnrollmentTenantIsolationTests(DatabaseFixture database) : EnrollmentsTest(database)
{
    private SchoolId _schoolB;
    private HttpClient _adminB = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _schoolB = await TestSchools.CreateAsync(Api, "okul-b");
        _adminB = Api.SchoolAdminClient(_schoolB);
    }

    public override async ValueTask DisposeAsync()
    {
        _adminB.Dispose();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Another_schools_student_or_group_cannot_be_enrolled()
    {
        var student = await RegisterStudentAsync();
        var group = await GroupAsync("U10 Futbol");
        var studentOfB = await RegisterStudentAsync("Bora", client: _adminB);

        (await CodeOf(await EnrollAsync(studentOfB, IdOf(group)))).ShouldBe("Student.NotFound");
        (await CodeOf(await EnrollAsync(student, IdOf(group), client: _adminB))).ShouldBe("Student.NotFound");
        (await CodeOf(await EnrollAsync(studentOfB, IdOf(group), client: _adminB))).ShouldBe("Group.NotFound");
    }

    [Fact]
    public async Task Another_schools_enrollments_roster_and_histories_are_not_found()
    {
        var student = await RegisterStudentAsync();
        var group = await GroupAsync("U10 Futbol", 20, Ayse);
        var enrollment = await EnrollOkAsync(student, IdOf(group));

        var responses = new[]
        {
            await _adminB.GetAsync(new Uri($"/api/v1/enrollments/{IdOf(enrollment)}", UriKind.Relative), Ct),
            await EndAsync(IdOf(enrollment), Today, _adminB),
            await _adminB.GetAsync(new Uri($"/api/v1/groups/{IdOf(group)}/enrollments", UriKind.Relative), Ct),
            await _adminB.GetAsync(new Uri($"/api/v1/groups/{IdOf(group)}/roster", UriKind.Relative), Ct),
            await _adminB.GetAsync(new Uri($"/api/v1/students/{student}/enrollments", UriKind.Relative), Ct),
        };

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.NotFound);
        (await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/enrollments/{IdOf(enrollment)}", Ct)).GetProperty("status").GetString().ShouldBe("Active");
    }

    [Fact]
    public async Task Composite_foreign_keys_reject_a_student_or_group_of_another_school()
    {
        var group = GroupId.From(IdOf(await GroupAsync("U10 Futbol")));
        var student = StudentId.From(await RegisterStudentAsync());
        var studentOfB = StudentId.From(await RegisterStudentAsync("Bora", client: _adminB));

        var crossStudent = await InsertDirectlyAsync(School, studentOfB, group);
        var crossGroup = await InsertDirectlyAsync(_schoolB, studentOfB, group);
        var sameSchool = await InsertDirectlyAsync(School, student, group);

        crossStudent.ShouldNotBeNull().ConstraintName.ShouldBe("fk_enrollments_student_tenant_id_student_id");
        crossStudent.SqlState.ShouldBe("23503");
        crossGroup.ShouldNotBeNull().ConstraintName.ShouldBe("fk_enrollments_group_tenant_id_group_id");
        sameSchool.ShouldBeNull();
    }

    [Fact]
    [Trait("Story", "US-17")]
    public async Task Partial_unique_index_backs_already_enrolled_and_maps_to_its_code()
    {
        var group = GroupId.From(IdOf(await GroupAsync("U10 Futbol")));
        var student = StudentId.From(await RegisterStudentAsync());
        await EnrollOkAsync(student.Value, group.Value);

        // Bypass the policy as a lost race would: the database still refuses a second active enrollment.
        var result = await InScopeAsync(sp =>
        {
            sp.GetRequiredService<BackgroundTenantContext>().Enter(School.Value);
            var db = sp.GetRequiredService<AppDbContext>();
            return sp.GetRequiredService<IUnitOfWork>().ExecuteInTransactionAsync(
                _ =>
                {
                    db.Add(NewEnrollment(School, student, group));
                    return Task.FromResult(Result.Success(true));
                },
                Ct);
        });

        result.Error.ShouldBe(EnrollmentErrors.AlreadyEnrolled);
    }

    private static Enrollment NewEnrollment(SchoolId school, StudentId student, GroupId group) =>
        EnrollmentPolicy.Enroll(
            school,
            new EnrollmentStudent(student, StudentStatus.Active, new DateOnly(2015, 5, 1)),
            new EnrollmentGroup(group, "U10", false, BirthYearRange.Create(2015, 2016).Value, 20, []),
            0,
            [],
            new DateOnly(2027, 1, 14)).Value;

    /// <summary>Saved in <paramref name="school"/>'s scope (row level security, P4-04), so only the foreign keys can refuse it.</summary>
    private Task<PostgresException?> InsertDirectlyAsync(SchoolId school, StudentId student, GroupId group) =>
        InScopeAsync(async sp =>
        {
            sp.GetRequiredService<BackgroundTenantContext>().Enter(school.Value);
            var db = sp.GetRequiredService<AppDbContext>();
            db.Add(NewEnrollment(school, student, group));
            try
            {
                await db.SaveChangesAsync(Ct);
                return null;
            }
            catch (DbUpdateException exception)
            {
                return exception.InnerException as PostgresException;
            }
        });
}
