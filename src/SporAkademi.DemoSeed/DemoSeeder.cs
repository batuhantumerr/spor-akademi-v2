using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Announcements;
using SporAkademi.Application.Attendance;
using SporAkademi.Application.Billing;
using SporAkademi.Application.Enrollments;
using SporAkademi.Application.Groups;
using SporAkademi.Application.Identity;
using SporAkademi.Application.Identity.Coaches;
using SporAkademi.Application.Identity.Passwords;
using SporAkademi.Application.Organization;
using SporAkademi.Application.Students;
using SporAkademi.Application.Tenancy;
using SporAkademi.Domain.Announcements;
using SporAkademi.Domain.Attendance;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.DemoSeed;

/// <param name="Now">The moment the demo history ends ("today"); the seeder replays the days before it.</param>
/// <param name="HistoryDays">How far back the history starts (about three months by default).</param>
/// <param name="Password">The password of every demo account; generated when empty.</param>
/// <param name="Seed">Seed of the random choices: the same seed gives the same data.</param>
internal sealed record DemoSeedOptions(DateTimeOffset Now, int HistoryDays = 92, string? Password = null, int Seed = 20_261_107);

internal sealed record DemoSchoolSummary(string Name, string Slug, string AdminEmail, Guid SchoolId);

internal sealed record DemoSeedResult(bool Created, string? Password, IReadOnlyList<DemoSchoolSummary> Schools)
{
    public static DemoSeedResult Refused => new(false, null, []);
}

internal sealed class DemoSeedException(string message) : Exception(message);

/// <summary>
/// P1-38: two demo schools with about three months of history. Every change is a command through the
/// application layer (validation, domain rules, unit of work), acting as the role that would make it in real
/// life, with the clock set to the moment it would happen. Refuses to run if a demo school already exists.
/// </summary>
internal sealed class DemoSeeder(IServiceScopeFactory scopes, SeedClock clock, SeedActor actor)
{
    private static readonly TimeZoneInfo Istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    private SeedClock Clock => clock;

    private SeedActor Actor => actor;

    public async Task<DemoSeedResult> RunAsync(DemoSeedOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        await using (var scope = scopes.CreateAsyncScope())
        {
            var schools = scope.ServiceProvider.GetRequiredService<ISchoolRepository>();
            foreach (var plan in DemoData.Schools)
            {
                if (await schools.SlugExistsAsync(Slug.Create(plan.Slug).Value, cancellationToken))
                {
                    return DemoSeedResult.Refused;
                }
            }
        }

        var password = string.IsNullOrWhiteSpace(options.Password) ? GeneratePassword() : options.Password;
        if (!PasswordPolicy.IsStrongEnough(password))
        {
            throw new DemoSeedException("The demo password does not meet the password policy (at least 12 characters, not a common password).");
        }

        var summaries = new List<DemoSchoolSummary>();
        for (var i = 0; i < DemoData.Schools.Length; i++)
        {
            var run = new SchoolRun(this, DemoData.Schools[i], i, options, password, cancellationToken);
            summaries.Add(await run.SeedAsync());
        }

        Clock.Set(options.Now);
        return new DemoSeedResult(true, password, summaries);
    }

    /// <summary>Easy to type, policy-compliant and random: "Demo-" plus 12 letters and digits.</summary>
    private static string GeneratePassword() =>
        "Demo-" + RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789", 12);

    private async Task<T> SendAsync<TCommand, T>(TCommand command, CancellationToken cancellationToken)
        where TCommand : ICommand<T>
    {
        await using var scope = scopes.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ICommandHandler<TCommand, T>>().Handle(command, cancellationToken);
        return result.IsSuccess
            ? result.Value
            : throw new DemoSeedException($"{typeof(TCommand).Name} failed with {result.Error.Code}.");
    }

    private async Task<T> AskAsync<TQuery, T>(TQuery query, CancellationToken cancellationToken)
        where TQuery : IQuery<T>
    {
        await using var scope = scopes.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, T>>().Handle(query, cancellationToken);
        return result.IsSuccess
            ? result.Value
            : throw new DemoSeedException($"{typeof(TQuery).Name} failed with {result.Error.Code}.");
    }

    private static DateTimeOffset At(DateOnly date, TimeOnly time) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time), Istanbul), TimeSpan.Zero);

    private static DateOnly LocalDate(DateTimeOffset utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, Istanbul).DateTime);

    private sealed class DemoGroup(Guid id, string name, Guid coachId, int capacity, int minYear, int maxYear, IReadOnlyList<(DayOfWeek Day, TimeOnly Start, TimeOnly End)> slots)
    {
        public Guid Id { get; } = id;

        public string Name { get; } = name;

        public Guid CoachId { get; } = coachId;

        public int Capacity { get; } = capacity;

        public int MinYear { get; } = minYear;

        public int MaxYear { get; } = maxYear;

        public IReadOnlyList<(DayOfWeek Day, TimeOnly Start, TimeOnly End)> Slots { get; } = slots;

        public decimal Fee { get; set; }
    }

    private sealed class DemoEnrollment(Guid studentId, DemoGroup group, DateOnly start)
    {
        public Guid Id { get; set; }

        public Guid StudentId { get; } = studentId;

        public DemoGroup Group { get; } = group;

        public DateOnly Start { get; } = start;

        public DateOnly? End { get; set; }

        /// <summary>How reliably the student attends (0.75–0.97).</summary>
        public double Attendance { get; init; }

        public bool IsActiveOn(DateOnly date) => Start <= date && (End is not { } end || date <= end);
    }

    private sealed record Family(string LastName, Guid GuardianId);

    /// <summary>One school's seeding, with its own deterministic random sequence.</summary>
    private sealed class SchoolRun(DemoSeeder seeder, SchoolPlan plan, int index, DemoSeedOptions options, string password, CancellationToken ct)
    {
        private readonly Random _random = new(options.Seed + index);
        private readonly DateOnly _today = LocalDate(options.Now);
        private readonly List<DemoGroup> _groups = [];
        private readonly List<DemoEnrollment> _enrollments = [];
        private readonly List<(DateOnly Date, Guid StudentId)> _leaves = [];
        private readonly List<(DateOnly Date, DemoEnrollment Enrollment)> _lateJoiners = [];
        private readonly List<(DateOnly Date, string Period, DemoEnrollment Enrollment, decimal Amount, bool Mistake)> _payments = [];
        private readonly List<Guid> _coaches = [];
        private readonly List<Family> _families = [];
        private int _phones;
        private int _emails;
        private Guid _schoolId;
        private Guid _adminId;

        private DateOnly Start => _today.AddDays(-options.HistoryDays);

        public async Task<DemoSchoolSummary> SeedAsync()
        {
            seeder.Clock.Set(At(Start, new TimeOnly(9, 0)));
            await CreateSchoolAsync();
            var sports = await CreateSportsAsync();
            var facilities = await CreateFacilitiesAsync();
            await CreateCoachesAsync();
            await CreateGroupsAsync(sports, facilities);
            await RegisterAndEnrollStudentsAsync();
            PlanPaymentsAndLeaves();
            await ReplayDaysAsync();
            return new DemoSchoolSummary(plan.Name, plan.Slug, plan.AdminEmail, _schoolId);
        }

        private string NextPhone() => DemoData.Phone(index, ++_phones);

        private string NextEmail(string prefix) => $"{prefix}{++_emails}@{plan.Domain}";

        private T Pick<T>(IReadOnlyList<T> items) => items[_random.Next(items.Count)];

        private Task<T> Send<TCommand, T>(TCommand command)
            where TCommand : ICommand<T> => seeder.SendAsync<TCommand, T>(command, ct);

        private void AsAdmin() => seeder.Actor.Act(Role.SchoolAdmin, _adminId, _schoolId);

        private async Task CreateSchoolAsync()
        {
            seeder.Actor.Act(Role.PlatformAdmin, Guid.CreateVersion7(), null);
            var created = await Send<CreateSchoolCommand, SchoolCreated>(new CreateSchoolCommand(
                plan.Name,
                plan.Slug,
                "Europe/Istanbul",
                new NewSchoolAdmin(Pick(DemoData.FemaleNames), Pick(DemoData.LastNames), plan.AdminEmail)));
            _schoolId = created.SchoolId;
            _adminId = created.AdminId;
            await Send<ChangePasswordCommand, AuthSession>(new ChangePasswordCommand(_adminId, created.TemporaryPassword, password));
            AsAdmin();
        }

        private async Task<List<(SportPlan Plan, Guid Id)>> CreateSportsAsync()
        {
            var sports = new List<(SportPlan, Guid)>();
            foreach (var sport in plan.Sports)
            {
                sports.Add((sport, (await Send<CreateSportCommand, SportId>(new CreateSportCommand(sport.Name))).Value));
            }

            return sports;
        }

        private async Task<List<(FacilityPlan Plan, Guid Id)>> CreateFacilitiesAsync()
        {
            var facilities = new List<(FacilityPlan, Guid)>();
            var street = 1;
            foreach (var facility in plan.Facilities)
            {
                var address = new AddressDto($"Spor Cad. No:{street++ * 7}", plan.District, plan.Slug.StartsWith("kadikoy", StringComparison.Ordinal) ? "İstanbul" : "İzmir");
                var id = await Send<CreateFacilityCommand, FacilityId>(new CreateFacilityCommand(facility.Name, facility.Type, address, 40 + (street * 10)));
                facilities.Add((facility, id.Value));
            }

            return facilities;
        }

        private async Task CreateCoachesAsync()
        {
            for (var i = 0; i < plan.CoachCount; i++)
            {
                var female = i % 2 == 0;
                var created = await Send<CreateCoachCommand, CoachCreated>(new CreateCoachCommand(
                    Pick(female ? DemoData.FemaleNames : DemoData.MaleNames),
                    Pick(DemoData.LastNames),
                    $"antrenor{i + 1}@{plan.Domain}",
                    NextPhone()));
                await Send<ChangePasswordCommand, AuthSession>(new ChangePasswordCommand(created.CoachId, created.TemporaryPassword, password));
                _coaches.Add(created.CoachId);
            }
        }

        /// <summary>Groups of each sport cycle through the age bands; slots never overlap for a coach or a facility.</summary>
        private async Task CreateGroupsAsync(List<(SportPlan Plan, Guid Id)> sports, List<(FacilityPlan Plan, Guid Id)> facilities)
        {
            var coachBusy = new HashSet<(Guid, DayOfWeek, TimeOnly)>();
            var facilityBusy = new HashSet<(Guid, DayOfWeek, TimeOnly)>();
            var year = Start.Year;

            for (var g = 0; g < plan.GroupCount; g++)
            {
                var (sport, sportId) = sports[g % sports.Count];
                var matching = facilities.Where(f => f.Plan.Type == sport.Facility).ToList();
                // The n-th sport needing a court uses the n-th court (basketball the hall, tennis the courts).
                var rank = sports.Take(g % sports.Count).Count(s => s.Plan.Facility == sport.Facility);
                var facilityId = matching[rank % matching.Count].Id;
                var band = DemoData.AgeBands[(g / sports.Count + g) % DemoData.AgeBands.Length];
                var adult = band.MinAge >= 18;
                var coachId = _coaches[g % _coaches.Count];
                var capacity = adult ? 20 : 12 + _random.Next(0, 3) * 2;
                var name = $"{band.Label} {sport.Name}{(g >= sports.Count * 2 ? " B" : string.Empty)}";

                // Adults train late; children in the afternoon.
                var times = adult ? DemoData.SlotStarts[^1..] : DemoData.SlotStarts[..^1];
                var slots = PickSlots(coachId, facilityId, times, coachBusy, facilityBusy);

                AsAdmin();
                var id = (await Send<CreateGroupCommand, GroupId>(new CreateGroupCommand(
                    sportId, name, new BirthYearRangeDto(year - band.MaxAge, year - band.MinAge), capacity, facilityId, coachId))).Value;
                var version = (await seeder.AskAsync<GetGroupQuery, GroupDto>(new GetGroupQuery(GroupId.From(id)), ct)).Version;
                await Send<ReplaceScheduleCommand, IReadOnlyList<Warning>>(new ReplaceScheduleCommand(
                    GroupId.From(id),
                    [.. slots.Select(s => new SlotInput(s.Day, s.Start.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), s.End.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)))],
                    version));

                var group = new DemoGroup(id, name, coachId, capacity, year - band.MaxAge, year - band.MinAge, slots)
                {
                    Fee = adult ? 1500m : 900m + (_random.Next(0, 7) * 100m),
                };
                await Send<DefineGroupFeeCommand, Unit>(new DefineGroupFeeCommand(GroupId.From(id), YearMonth.From(Start).ToString(), Money(group.Fee)));
                _groups.Add(group);
            }
        }

        private List<(DayOfWeek Day, TimeOnly Start, TimeOnly End)> PickSlots(
            Guid coachId,
            Guid facilityId,
            TimeOnly[] times,
            HashSet<(Guid, DayOfWeek, TimeOnly)> coachBusy,
            HashSet<(Guid, DayOfWeek, TimeOnly)> facilityBusy)
        {
            foreach (var pair in DemoData.DayPairs.OrderBy(_ => _random.Next()))
            {
                foreach (var start in times.OrderBy(_ => _random.Next()))
                {
                    var free = new[] { pair.First, pair.Second }.All(day =>
                        !coachBusy.Contains((coachId, day, start)) && !facilityBusy.Contains((facilityId, day, start)));
                    if (!free)
                    {
                        continue;
                    }

                    foreach (var day in new[] { pair.First, pair.Second })
                    {
                        coachBusy.Add((coachId, day, start));
                        facilityBusy.Add((facilityId, day, start));
                    }

                    return [(pair.First, start, start.AddMinutes(75)), (pair.Second, start, start.AddMinutes(75))];
                }
            }

            throw new DemoSeedException("No free weekly slot left for a demo group.");
        }

        /// <summary>Fills each group to 65–100 %; a fifth of the children are siblings sharing a guardian.</summary>
        private async Task RegisterAndEnrollStudentsAsync()
        {
            AsAdmin();
            foreach (var group in _groups)
            {
                var count = (int)Math.Ceiling(group.Capacity * (0.65 + (_random.NextDouble() * 0.35)));
                for (var i = 0; i < count; i++)
                {
                    var studentId = await RegisterStudentAsync(group);
                    var joinsLater = _random.NextDouble() < 0.1;
                    var start = joinsLater ? Start.AddDays(_random.Next(10, options.HistoryDays - 5)) : Start;
                    var enrollment = new DemoEnrollment(studentId, group, start) { Attendance = 0.75 + (_random.NextDouble() * 0.22) };
                    if (joinsLater)
                    {
                        _lateJoiners.Add((start, enrollment));
                    }
                    else
                    {
                        _enrollments.Add(await EnrollAsync(enrollment));
                    }
                }
            }
        }

        private async Task<DemoEnrollment> EnrollAsync(DemoEnrollment enrollment)
        {
            enrollment.Id = (await Send<EnrollStudentCommand, EnrollmentId>(new EnrollStudentCommand(enrollment.StudentId, enrollment.Group.Id, enrollment.Start))).Value;
            return enrollment;
        }

        private async Task<Guid> RegisterStudentAsync(DemoGroup group)
        {
            var female = _random.Next(2) == 0;
            var firstName = Pick(female ? DemoData.FemaleNames : DemoData.MaleNames);
            var birthDate = new DateOnly(_random.Next(group.MinYear, group.MaxYear + 1), _random.Next(1, 13), _random.Next(1, 29));
            var adult = Start.Year - birthDate.Year > 18;
            var gender = female ? Gender.Female : Gender.Male;

            if (adult)
            {
                var lastName = Pick(DemoData.LastNames);
                var id = await Send<RegisterStudentCommand, StudentId>(new RegisterStudentCommand(
                    firstName, lastName, birthDate, gender, NextPhone(), NextEmail("ogrenci"),
                    new EmergencyContactInput($"{Pick(DemoData.FemaleNames)} {lastName}", NextPhone(), "Eş"),
                    []));
                return id.Value;
            }

            // A sibling joins an existing family and shares its guardian.
            if (_families.Count > 0 && _random.NextDouble() < 0.2)
            {
                var family = Pick(_families);
                var sibling = await Send<RegisterStudentCommand, StudentId>(new RegisterStudentCommand(
                    firstName, family.LastName, birthDate, gender, null, null,
                    new EmergencyContactInput($"{Pick(DemoData.FemaleNames)} {Pick(DemoData.LastNames)}", NextPhone(), "Teyze"),
                    [new GuardianLinkInput(family.GuardianId, null, GuardianRelationship.Mother)]));
                return sibling.Value;
            }

            var familyName = Pick(DemoData.LastNames);
            var motherName = Pick(DemoData.FemaleNames);
            var studentId = await Send<RegisterStudentCommand, StudentId>(new RegisterStudentCommand(
                firstName, familyName, birthDate, gender, null, null,
                new EmergencyContactInput($"{Pick(DemoData.MaleNames)} {familyName}", NextPhone(), "Baba"),
                [new GuardianLinkInput(null, new NewGuardianInput(motherName, familyName, NextPhone(), NextEmail("veli"), null), GuardianRelationship.Mother)]));
            var student = await seeder.AskAsync<GetStudentQuery, StudentDto>(new GetStudentQuery(studentId), ct);
            _families.Add(new Family(familyName, student.Guardians[0].GuardianId));
            return studentId.Value;
        }

        /// <summary>
        /// Most families pay each month in full between the 2nd and 20th; some pay half (a few the rest later),
        /// some not at all, so the dues list has entries. Two students leave, and one entry is a mistake that
        /// gets cancelled and recorded again.
        /// </summary>
        private void PlanPaymentsAndLeaves()
        {
            var leavers = _enrollments.Where(e => e.Group.Capacity > 12).OrderBy(_ => _random.Next()).Take(2).ToList();
            foreach (var leaver in leavers)
            {
                var date = Start.AddDays(_random.Next(35, options.HistoryDays - 10));
                _leaves.Add((date, leaver.StudentId));
                leaver.End = date;
            }

            var mistakeDone = false;
            foreach (var enrollment in _enrollments.Concat(_lateJoiners.Select(j => j.Enrollment)))
            {
                var last = enrollment.End ?? _today;
                for (var period = YearMonth.From(enrollment.Start); period <= YearMonth.From(last); period = period.Next())
                {
                    var roll = _random.NextDouble();
                    var firstDay = period.FirstDay < enrollment.Start ? enrollment.Start : period.FirstDay;
                    var payDay = Max(firstDay, period.FirstDay.AddDays(_random.Next(1, 20)));
                    if (payDay > _today || roll < 0.12)
                    {
                        continue;
                    }

                    var fee = enrollment.Group.Fee;
                    if (roll < 0.22)
                    {
                        _payments.Add((payDay, period.ToString(), enrollment, fee / 2, false));
                        var rest = payDay.AddDays(10);
                        if (_random.NextDouble() < 0.5 && rest <= _today)
                        {
                            _payments.Add((rest, period.ToString(), enrollment, fee / 2, false));
                        }

                        continue;
                    }

                    if (!mistakeDone)
                    {
                        _payments.Add((payDay, period.ToString(), enrollment, fee * 10, true));
                        mistakeDone = true;
                    }

                    _payments.Add((payDay, period.ToString(), enrollment, fee, false));
                }
            }
        }

        private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

        /// <summary>Replays each day in order: joins, payments, a raised fee, leaves, announcements, then the day's attendance.</summary>
        private async Task ReplayDaysAsync()
        {
            var raisedGroups = _groups.OrderBy(_ => _random.Next()).Take(2).ToList();
            var announcementDays = Enumerable.Range(0, 3).Select(i => Start.AddDays(14 + (i * 28))).ToHashSet();

            for (var day = Start; day <= _today; day = day.AddDays(1))
            {
                seeder.Clock.Set(Min(At(day, new TimeOnly(12, 0)), options.Now));
                AsAdmin();

                foreach (var (_, joiner) in _lateJoiners.Where(j => j.Date == day))
                {
                    _enrollments.Add(await EnrollAsync(joiner));
                }

                if (day.Day == 1 && day != Start)
                {
                    foreach (var group in raisedGroups.Where(_ => YearMonth.From(day) == YearMonth.From(_today)))
                    {
                        group.Fee += 100m;
                        await Send<DefineGroupFeeCommand, Unit>(new DefineGroupFeeCommand(GroupId.From(group.Id), YearMonth.From(day).ToString(), Money(group.Fee)));
                    }
                }

                foreach (var payment in _payments.Where(p => p.Date == day))
                {
                    await RecordPaymentAsync(payment.Enrollment, payment.Period, payment.Amount, payment.Mistake, day);
                }

                foreach (var (_, studentId) in _leaves.Where(l => l.Date == day))
                {
                    await Send<LeaveStudentCommand, Unit>(new LeaveStudentCommand(StudentId.From(studentId), day));
                }

                if (announcementDays.Contains(day))
                {
                    var group = Pick(_groups);
                    await Send<PublishAnnouncementCommand, AnnouncementId>(new PublishAnnouncementCommand(
                        GroupId.From(group.Id),
                        Pick(DemoData.AnnouncementTitles),
                        $"Sevgili veliler, {group.Name} grubu için bilgilendirme: ayrıntılar antrenman öncesinde paylaşılacaktır."));
                }

                await TakeAttendanceAsync(day);
            }
        }

        private async Task RecordPaymentAsync(DemoEnrollment enrollment, string period, decimal amount, bool mistake, DateOnly day)
        {
            var method = _random.NextDouble() < 0.6 ? PaymentMethod.BankTransfer : PaymentMethod.Cash;
            var payment = await Send<RecordPaymentCommand, PaymentId>(new RecordPaymentCommand(enrollment.Id, period, Money(amount), method, day));
            if (mistake)
            {
                await Send<CancelPaymentCommand, Unit>(new CancelPaymentCommand(payment, "Tutar yanlış girildi; doğrusu ayrıca kaydedildi."));
            }
        }

        /// <summary>The coach of each group takes attendance shortly after the session ends.</summary>
        private async Task TakeAttendanceAsync(DateOnly day)
        {
            foreach (var group in _groups)
            {
                foreach (var slot in group.Slots.Where(s => s.Day == day.DayOfWeek))
                {
                    var takenAt = At(day, slot.End.AddMinutes(20));
                    var present = _enrollments.Where(e => e.Group == group && e.IsActiveOn(day)).ToList();
                    if (takenAt > options.Now || present.Count == 0)
                    {
                        continue;
                    }

                    seeder.Clock.Set(takenAt);
                    seeder.Actor.Act(Role.Coach, group.CoachId, _schoolId);
                    var marks = present.Select(e => new AttendanceMarkInput(e.StudentId, StatusFor(e))).ToList();
                    var key = new SessionKey(day, slot.Start).ToString();
                    await Send<SaveAttendanceCommand, Unit>(new SaveAttendanceCommand(GroupId.From(group.Id), key, marks));
                }
            }

            AsAdmin();
        }

        private AttendanceStatus StatusFor(DemoEnrollment enrollment)
        {
            var roll = _random.NextDouble();
            if (roll < enrollment.Attendance)
            {
                return AttendanceStatus.Present;
            }

            roll = _random.NextDouble();
            return roll < 0.35 ? AttendanceStatus.Late : roll < 0.65 ? AttendanceStatus.Excused : AttendanceStatus.Absent;
        }

        private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

        private static MoneyDto Money(decimal amount) => MoneyDto.From(Domain.SharedKernel.Money.Try(amount));
    }
}
