using DSVHVN.Application.Discovery;
using DSVHVN.Infrastructure.Security;
using DSVHVN.Tests.Support;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DSVHVN.Tests.Unit;

/// <summary>Mã xem trước bản nháp di sản: ký, gắn một di sản, hết hạn đúng hạn, không đoán được, không sửa được.</summary>
public sealed class HeritagePreviewCodecTests
{
    private sealed class TestingEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static JwtSigningKeyProvider Keys(string key) =>
        new(Options.Create(new JwtOptions { SigningKey = key }), new TestingEnvironment(), NullLogger<JwtSigningKeyProvider>.Instance);

    private static HeritagePreviewCodec Create(MutableTimeProvider clock, string key = "unit-test-signing-key-0123456789-abcdef", int minutes = 30) =>
        new(Keys(key), Options.Create(new PreviewOptions { CodeMinutes = minutes }), clock);

    [Fact]
    public void A_fresh_code_reads_back_to_its_heritage_and_expires_after_thirty_minutes_by_default()
    {
        var clock = new MutableTimeProvider();
        var codec = Create(clock);

        var ticket = codec.Issue(4711);

        // Hạn được lưu theo giây Unix nên so tới giây.
        Assert.Equal(clock.GetUtcNow().UtcDateTime.AddMinutes(30), ticket.ExpiresAt);
        var read = codec.Read(ticket.Code);
        Assert.NotNull(read);
        Assert.Equal((4711L, ticket.ExpiresAt), (read.HeritageId, read.ExpiresAt));
    }

    [Fact]
    public void The_code_is_valid_until_the_last_second_and_dead_from_the_expiry_instant()
    {
        var clock = new MutableTimeProvider();
        var codec = Create(clock);
        var code = codec.Issue(1).Code;

        clock.Advance(TimeSpan.FromMinutes(29) + TimeSpan.FromSeconds(59));
        Assert.NotNull(codec.Read(code));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(codec.Read(code));

        clock.Advance(TimeSpan.FromDays(1));
        Assert.Null(codec.Read(code));
    }

    [Fact]
    public void The_lifetime_follows_the_configuration()
    {
        var clock = new MutableTimeProvider();
        var code = Create(clock, minutes: 5).Issue(1).Code;
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Null(Create(clock, minutes: 5).Read(code));
    }

    [Fact]
    public void Every_issued_code_is_different_and_long_enough_not_to_be_guessed()
    {
        var codec = Create(new MutableTimeProvider());
        var codes = Enumerable.Range(0, 50).Select(_ => codec.Issue(7).Code).ToList();
        Assert.Equal(50, codes.Distinct().Count());
        Assert.All(codes, c => Assert.True(c.Length > 80 && c.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')));
    }

    [Fact]
    public void Changing_any_part_of_the_code_makes_it_invalid()
    {
        var codec = Create(new MutableTimeProvider());
        var code = codec.Issue(5).Code;
        var dot = code.IndexOf('.');

        // Sửa một ký tự ở phần dữ liệu hoặc ở chữ ký.
        foreach (var index in new[] { 0, 10, dot - 1, dot + 1, code.Length - 1 })
        {
            var chars = code.ToCharArray();
            chars[index] = chars[index] == 'A' ? 'B' : 'A';
            Assert.Null(codec.Read(new string(chars)));
        }
        Assert.Null(codec.Read(code[..dot] + "." + new string('A', code.Length - dot - 1)));
        Assert.Null(codec.Read(code + "A"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("a.b")]
    [InlineData("a.b.c")]
    [InlineData("!!!.???")]
    [InlineData("..")]
    public void Garbage_is_rejected_without_throwing(string? code) => Assert.Null(Create(new MutableTimeProvider()).Read(code));

    [Fact]
    public void A_code_is_only_valid_with_the_signing_key_that_issued_it()
    {
        var clock = new MutableTimeProvider();
        var code = Create(clock, "first-signing-key-0123456789-abcdefgh").Issue(9).Code;
        Assert.Null(Create(clock, "other-signing-key-0123456789-abcdefgh").Read(code));
    }

    [Fact]
    public void The_lifetime_must_be_between_one_and_240_minutes()
    {
        var validator = new PreviewOptionsValidator();
        Assert.True(validator.Validate(null, new PreviewOptions()).Succeeded);
        Assert.True(validator.Validate(null, new PreviewOptions { CodeMinutes = 0 }).Failed);
        Assert.True(validator.Validate(null, new PreviewOptions { CodeMinutes = 241 }).Failed);
        Assert.True(validator.Validate(null, new PreviewOptions { CodeMinutes = 240 }).Succeeded);
    }
}
