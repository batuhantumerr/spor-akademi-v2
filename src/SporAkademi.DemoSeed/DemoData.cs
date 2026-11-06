using System.Globalization;
using SporAkademi.Domain.Organization;

namespace SporAkademi.DemoSeed;

/// <summary>
/// Synthetic demo data (faz 4'e kadar sadece sentetik veri): common Turkish first and last names combined at
/// random, phone numbers with a fixed <c>000</c> block, and addresses and e-mails under the reserved <c>.test</c>
/// domain. Nothing here describes a real person.
/// </summary>
internal static class DemoData
{
    public static readonly string[] FemaleNames =
    [
        "Zeynep", "Elif", "Defne", "Ecrin", "Azra", "Nehir", "Asel", "Eylül", "Ela", "Duru", "Meryem", "Ada",
        "Miray", "Yağmur", "İpek", "Selin", "Beren", "Nisa", "Ceren", "Derin", "Gizem", "Buse", "Damla", "Aylin",
    ];

    public static readonly string[] MaleNames =
    [
        "Yusuf", "Eymen", "Ömer", "Mustafa", "Alparslan", "Mehmet", "Ali", "Kerem", "Emir", "Aras", "Çınar",
        "Burak", "Efe", "Arda", "Deniz", "Kaan", "Barış", "Mert", "Onur", "Tuna", "Batuhan", "Doruk", "Umut", "Can",
    ];

    public static readonly string[] LastNames =
    [
        "Yılmaz", "Kaya", "Demir", "Şahin", "Çelik", "Yıldız", "Yıldırım", "Öztürk", "Aydın", "Özdemir", "Arslan",
        "Doğan", "Kılıç", "Aslan", "Çetin", "Kara", "Koç", "Kurt", "Özkan", "Şimşek", "Polat", "Korkmaz", "Erdoğan",
        "Güneş", "Aksoy", "Tekin", "Bulut", "Keskin", "Ünal", "Uçar", "Karaca", "Bozkurt", "Turan", "Güler", "Sarı",
    ];

    public static readonly string[] AnnouncementTitles =
    [
        "Bayram tatili programı",
        "Veli toplantısı",
        "Kıyafet ve ekipman hatırlatması",
        "Antrenman saati değişikliği",
    ];

    public static readonly SchoolPlan[] Schools =
    [
        new(
            "Kadıköy Spor Kulübü",
            "kadikoy-spor",
            "Kadıköy",
            [new("Futbol", FacilityType.Field), new("Yüzme", FacilityType.Pool), new("Basketbol", FacilityType.Court), new("Voleybol", FacilityType.Court)],
            [new("Moda Sahası", FacilityType.Field), new("Fenerbahçe Kapalı Havuz", FacilityType.Pool), new("Caferağa Spor Salonu", FacilityType.Court)],
            CoachCount: 6,
            GroupCount: 12),
        new(
            "Bornova Gençlik Spor",
            "bornova-genclik",
            "Bornova",
            [new("Futbol", FacilityType.Field), new("Basketbol", FacilityType.Court), new("Tenis", FacilityType.Court)],
            [new("Kazımdirik Sahası", FacilityType.Field), new("Evka Spor Salonu", FacilityType.Court), new("Ege Tenis Kortları", FacilityType.Court)],
            CoachCount: 5,
            GroupCount: 10),
    ];

    /// <summary>Age bands of the groups (ages in the school's current year); the last one is the adult group.</summary>
    public static readonly (string Label, int MinAge, int MaxAge)[] AgeBands =
    [
        ("U8", 6, 7), ("U10", 8, 9), ("U12", 10, 11), ("U14", 12, 13), ("U16", 14, 15), ("Yetişkin", 20, 50),
    ];

    public static readonly TimeOnly[] SlotStarts = [new(16, 0), new(17, 15), new(18, 30), new(19, 45)];

    public static readonly (DayOfWeek First, DayOfWeek Second)[] DayPairs =
    [
        (DayOfWeek.Monday, DayOfWeek.Wednesday),
        (DayOfWeek.Tuesday, DayOfWeek.Thursday),
        (DayOfWeek.Wednesday, DayOfWeek.Friday),
        (DayOfWeek.Monday, DayOfWeek.Thursday),
        (DayOfWeek.Tuesday, DayOfWeek.Friday),
        (DayOfWeek.Saturday, DayOfWeek.Sunday),
    ];

    /// <summary>
    /// A valid Turkish mobile number that does not look like a real line: <c>+90 5xx 000 nnnn</c>, unique per
    /// <paramref name="sequence"/> within a school.
    /// </summary>
    public static string Phone(int schoolIndex, int sequence) =>
        string.Create(CultureInfo.InvariantCulture, $"+90 5{schoolIndex + 3}{sequence / 10_000 % 10} 000 {sequence % 10_000:0000}");
}

internal sealed record SportPlan(string Name, FacilityType Facility);

internal sealed record FacilityPlan(string Name, FacilityType Type);

internal sealed record SchoolPlan(
    string Name,
    string Slug,
    string District,
    IReadOnlyList<SportPlan> Sports,
    IReadOnlyList<FacilityPlan> Facilities,
    int CoachCount,
    int GroupCount)
{
    public string Domain => $"{Slug}.test";

    public string AdminEmail => $"admin@{Domain}";
}
