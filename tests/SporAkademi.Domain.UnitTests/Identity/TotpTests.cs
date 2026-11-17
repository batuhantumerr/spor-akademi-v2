using System.Text;
using SporAkademi.Domain.Identity;

namespace SporAkademi.Domain.UnitTests.Identity;

/// <summary>P5-01: RFC 6238 (SHA-1, 6 digits, 30 second step), checked against the RFC's own test vectors.</summary>
[Trait("Story", "P5-01")]
public sealed class TotpTests
{
    /// <summary>The ASCII secret of RFC 6238 appendix B for SHA-1.</summary>
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    // RFC 6238 appendix B lists 8 digits; the six-digit code is the last six of them (RFC 4226 truncation, mod 10^6).
    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Matches_the_rfc_6238_test_vectors(long unixSeconds, string expected)
    {
        var step = Totp.StepOf(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

        Totp.Compute(RfcSecret, step).ShouldBe(expected);
    }

    [Fact]
    public void A_step_is_thirty_seconds()
    {
        Totp.StepOf(DateTimeOffset.FromUnixTimeSeconds(29)).ShouldBe(0);
        Totp.StepOf(DateTimeOffset.FromUnixTimeSeconds(30)).ShouldBe(1);
        Totp.StepOf(DateTimeOffset.FromUnixTimeSeconds(59)).ShouldBe(1);
    }

    [Fact]
    public void Accepts_the_previous_current_and_next_step_but_nothing_further()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111111);
        var step = Totp.StepOf(now);

        Totp.TryMatch(RfcSecret, Totp.Compute(RfcSecret, step), now, out var current).ShouldBeTrue();
        current.ShouldBe(step);
        Totp.TryMatch(RfcSecret, Totp.Compute(RfcSecret, step - 1), now, out var previous).ShouldBeTrue();
        previous.ShouldBe(step - 1);
        Totp.TryMatch(RfcSecret, Totp.Compute(RfcSecret, step + 1), now, out var next).ShouldBeTrue();
        next.ShouldBe(step + 1);
        Totp.TryMatch(RfcSecret, Totp.Compute(RfcSecret, step - 2), now, out _).ShouldBeFalse();
        Totp.TryMatch(RfcSecret, Totp.Compute(RfcSecret, step + 2), now, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12 456")]
    [InlineData("abcdef")]
    public void Malformed_codes_never_match(string code)
    {
        Totp.TryMatch(RfcSecret, code, DateTimeOffset.FromUnixTimeSeconds(59), out _).ShouldBeFalse();
    }

    [Fact]
    public void Base32_round_trips_and_matches_the_rfc_4648_vectors()
    {
        Base32.Encode(Encoding.ASCII.GetBytes("foobar")).ShouldBe("MZXW6YTBOI");
        Base32.Encode(Encoding.ASCII.GetBytes("fooba")).ShouldBe("MZXW6YTB");
        Base32.Encode(RfcSecret).ShouldBe("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ");
        Base32.Decode("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ").ShouldBe(RfcSecret);
        Base32.Decode("gezd gnbv").ShouldBe(Encoding.ASCII.GetBytes("12345"));
    }

    [Fact]
    public void A_new_secret_is_160_bits_and_two_secrets_differ()
    {
        var first = Totp.NewSecret();

        first.Length.ShouldBe(20);
        Totp.NewSecret().ShouldNotBe(first);
    }

    [Fact]
    public void The_provisioning_uri_names_issuer_account_and_the_standard_parameters()
    {
        var uri = Totp.ProvisioningUri("Spor Akademi", "admin@okul.test", RfcSecret);

        uri.ShouldBe("otpauth://totp/Spor%20Akademi:admin%40okul.test?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=Spor%20Akademi&algorithm=SHA1&digits=6&period=30");
    }
}
