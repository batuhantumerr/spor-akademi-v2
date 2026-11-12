using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using SporAkademi.Api.Errors;

namespace SporAkademi.IntegrationTests.Platform;

/// <summary>P3-10 (DEC-430): every error code the API can return has a Turkish and an English text.</summary>
public sealed partial class ProblemTextsTests
{
    /// <summary>Codes the pipeline writes itself (ProblemShape, GlobalExceptionHandler, validation).</summary>
    private static readonly string[] FrameworkCodes =
    [
        "Validation.Failed", "Auth.Unauthenticated", "Auth.Forbidden", "Resource.NotFound", "Request.MethodNotAllowed",
        "Request.TooManyRequests", "Request.TooLarge", "Request.Cancelled", "Unexpected.Error",
    ];

    [Fact]
    public void Every_error_code_in_the_source_has_a_text_in_both_languages()
    {
        var codes = Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => ErrorDefinition().Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value))
            .Concat(FrameworkCodes)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        codes.Count.ShouldBeGreaterThan(100);
        codes.Where(code => !ProblemTexts.TurkishMessages.ContainsKey(code)).ShouldBeEmpty();
        codes.Where(code => !ProblemTexts.EnglishMessages.ContainsKey(code)).ShouldBeEmpty();
    }

    [Fact]
    public void Both_languages_have_the_same_codes_and_placeholders()
    {
        ProblemTexts.EnglishMessages.Keys.Order(StringComparer.Ordinal)
            .ShouldBe(ProblemTexts.TurkishMessages.Keys.Order(StringComparer.Ordinal));
        foreach (var (code, turkish) in ProblemTexts.TurkishMessages)
        {
            Placeholders(ProblemTexts.EnglishMessages[code]).ShouldBe(Placeholders(turkish), code);
        }
    }

    [Fact]
    public void Fills_arguments_and_leaves_unknown_ones_visible()
    {
        var args = new Dictionary<string, object?> { ["count"] = 12 };

        ProblemTexts.Detail("Group.CapacityBelowActiveEnrollments", ProblemTexts.English, args)
            .ShouldBe("Capacity can’t be lower than the number of active students in the group (12).");
        ProblemTexts.Detail("Enrollment.ScheduleConflict", ProblemTexts.Turkish, args)
            .ShouldBe("Öğrencinin bu saatte başka bir grubu var: {groupName}.");
        ProblemTexts.Detail("No.Such.Code", ProblemTexts.English, args).ShouldBeNull();
        ProblemTexts.Title(StatusCodes.Status409Conflict, ProblemTexts.English).ShouldBe("Conflict");
        ProblemTexts.Title(418, ProblemTexts.Turkish).ShouldBe("Hata");
    }

    [Theory]
    [InlineData(null, "tr")]
    [InlineData("", "tr")]
    [InlineData("en", "en")]
    [InlineData("en-US,en;q=0.9", "en")]
    [InlineData("tr-TR,tr;q=0.9,en-US;q=0.8", "tr")]
    [InlineData("tr;q=0.4, en;q=0.8", "en")]
    [InlineData("de-DE, fr;q=0.9", "tr")]
    [InlineData("de-DE, en;q=0.5", "en")]
    [InlineData("en;q=0, tr;q=0.1", "tr")]
    [InlineData("*", "tr")]
    [InlineData("not a valid header ;;;", "tr")]
    public void Picks_the_language_from_accept_language(string? header, string expected)
    {
        var context = new DefaultHttpContext();
        if (header is not null)
        {
            context.Request.Headers.AcceptLanguage = header;
        }

        ProblemLanguage.From(context).ShouldBe(expected);
    }

    private static string Placeholders(string text) =>
        string.Join(",", Placeholder().Matches(text).Select(m => m.Value).Order(StringComparer.Ordinal));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SporAkademi.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [GeneratedRegex(@"Error\.(?:Validation|Unauthorized|Forbidden|NotFound|Conflict|BusinessRule)\(\s*""([^""]+)""")]
    private static partial Regex ErrorDefinition();

    [GeneratedRegex(@"\{\w+\}")]
    private static partial Regex Placeholder();
}
