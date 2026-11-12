using Microsoft.Net.Http.Headers;

namespace SporAkademi.Api.Errors;

/// <summary>
/// P3-10 (DEC-430): the language of problem texts from <c>Accept-Language</c>. The supported language with the highest
/// quality wins (<c>en-US</c> counts as English); Turkish when the header is missing, unparsable or lists neither.
/// Only problem texts use it: the request culture, formatting and e-mails do not change (DEC-431).
/// </summary>
internal static class ProblemLanguage
{
    public static string From(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!StringWithQualityHeaderValue.TryParseList(context.Request.Headers.AcceptLanguage, out var ranges))
        {
            return ProblemTexts.Turkish;
        }

        foreach (var range in ranges.OrderByDescending(r => r.Quality ?? 1.0))
        {
            if (range.Quality is 0)
            {
                continue;
            }
            var tag = range.Value.Value ?? string.Empty;
            if (IsLanguage(tag, ProblemTexts.English))
            {
                return ProblemTexts.English;
            }
            if (IsLanguage(tag, ProblemTexts.Turkish) || tag == "*")
            {
                return ProblemTexts.Turkish;
            }
        }
        return ProblemTexts.Turkish;
    }

    private static bool IsLanguage(string tag, string language) =>
        tag.Equals(language, StringComparison.OrdinalIgnoreCase)
        || tag.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase);
}
