using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;

namespace DSVHVN.Tests.Unit;

/// <summary>
/// Quy tắc năm ngôn ngữ của phần công khai: đọc mã (kể cả mã dạng vùng của trình duyệt), tiếng Trung phồn thể không thuộc năm ngôn ngữ, tiêu đề Accept-Language,
/// ngôn ngữ mặc định khi mở lần đầu (tiếng Anh nếu không thuộc năm ngôn ngữ), ngôn ngữ của một yêu cầu, trạng thái ban đầu và điều kiện hiện của bản dịch.
/// </summary>
public sealed class LanguagesTests
{
    [Theory]
    [InlineData("vi", LanguageCode.vi)]
    [InlineData("VI", LanguageCode.vi)]
    [InlineData("vi-VN", LanguageCode.vi)]
    [InlineData("en", LanguageCode.en)]
    [InlineData("en-US", LanguageCode.en)]
    [InlineData("en_GB", LanguageCode.en)]
    [InlineData("ko", LanguageCode.ko)]
    [InlineData("ko-KR", LanguageCode.ko)]
    [InlineData("ja", LanguageCode.ja)]
    [InlineData("ja-JP", LanguageCode.ja)]
    [InlineData("zh", LanguageCode.zh)]
    [InlineData("zh-CN", LanguageCode.zh)]
    [InlineData("zh-SG", LanguageCode.zh)]
    [InlineData("zh-Hans", LanguageCode.zh)]
    [InlineData("zh-Hans-CN", LanguageCode.zh)]
    [InlineData(" zh-hans ", LanguageCode.zh)]
    public void Supported_tags_are_read_including_regional_forms(string tag, LanguageCode expected) =>
        Assert.Equal(expected, Languages.TryParse(tag));

    [Theory]
    [InlineData("zh-TW")]
    [InlineData("zh-HK")]
    [InlineData("zh-Hant")]
    [InlineData("zh-Hant-TW")]
    [InlineData("fr")]
    [InlineData("de-DE")]
    [InlineData("vie")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Traditional_chinese_and_other_languages_are_not_supported(string? tag) => Assert.Null(Languages.TryParse(tag));

    [Fact]
    public void There_are_exactly_five_languages_each_with_its_own_name_and_speech_voice()
    {
        Assert.Equal(["vi", "en", "ko", "zh-Hans", "ja"], Languages.All.Select(l => l.Tag));
        Assert.Equal(["Tiếng Việt", "English", "한국어", "简体中文", "日本語"], Languages.All.Select(l => l.NativeName));
        Assert.Equal(["vi-VN", "en-US", "ko-KR", "zh-CN", "ja-JP"], Languages.All.Select(l => l.SpeechTag));
        Assert.Equal(5, Enum.GetValues<LanguageCode>().Length);
        Assert.Equal("zh", LanguageCode.zh.ToString());
        Assert.Equal([LanguageCode.ko, LanguageCode.zh, LanguageCode.ja], Languages.MachineOnly);
        Assert.Equal([LanguageCode.en, LanguageCode.ko, LanguageCode.zh, LanguageCode.ja], Languages.TranslationTargets);
    }

    [Theory]
    [InlineData("ko-KR,ko;q=0.9,en;q=0.8", LanguageCode.ko)]
    [InlineData("fr-FR,fr;q=0.9,ja;q=0.8,en;q=0.7", LanguageCode.ja)]
    [InlineData("en;q=0.5,vi;q=0.9", LanguageCode.vi)]
    [InlineData("zh-TW,zh;q=0.9", LanguageCode.zh)]
    [InlineData("en;q=0,ko;q=0.2", LanguageCode.ko)]
    [InlineData("vi, en", LanguageCode.vi)]
    public void Accept_language_picks_the_best_supported_language(string header, LanguageCode expected) =>
        Assert.Equal(expected, Languages.FromAcceptLanguage(header));

    [Theory]
    [InlineData("fr-FR,de;q=0.8")]
    [InlineData("zh-TW,zh-HK;q=0.9")]
    [InlineData("")]
    [InlineData(null)]
    public void Accept_language_without_a_supported_language_gives_nothing(string? header) =>
        Assert.Null(Languages.FromAcceptLanguage(header));

    [Fact]
    public void First_open_defaults_to_the_first_supported_preference_and_otherwise_to_english()
    {
        Assert.Equal(LanguageCode.ko, Languages.DefaultFor(["fr", "ko-KR", "en"]));
        Assert.Equal(LanguageCode.zh, Languages.DefaultFor(["zh-CN"]));
        Assert.Equal(LanguageCode.en, Languages.DefaultFor(["zh-TW", "zh-HK"]));    // tiếng Trung phồn thể không thuộc năm ngôn ngữ
        Assert.Equal(LanguageCode.en, Languages.DefaultFor(["fr", "de"]));
        Assert.Equal(LanguageCode.en, Languages.DefaultFor([]));
    }

    [Fact]
    public void A_request_uses_the_lang_parameter_then_the_header_then_vietnamese()
    {
        Assert.Equal(LanguageCode.ja, Languages.Resolve("ja", "ko-KR"));
        Assert.Equal(LanguageCode.ko, Languages.Resolve(null, "ko-KR,en;q=0.5"));
        Assert.Equal(LanguageCode.ko, Languages.Resolve("", "ko-KR"));
        Assert.Equal(LanguageCode.vi, Languages.Resolve(null, null));
        Assert.Equal(LanguageCode.vi, Languages.Resolve(null, "fr-FR"));
        // Mã không thuộc năm ngôn ngữ bị bỏ qua và trả tiếng Việt, không lùi về tiêu đề.
        Assert.Equal(LanguageCode.vi, Languages.Resolve("fr", "ko-KR"));
        Assert.Equal(LanguageCode.vi, Languages.Resolve("zh-TW", "en-US"));
    }

    [Fact]
    public void New_translations_and_summaries_start_in_the_status_the_review_rules_require()
    {
        Assert.Equal(TranslationStatus.PENDING, Languages.NewTranslationStatus(LanguageCode.en));
        Assert.All(Languages.MachineOnly, l => Assert.Equal(TranslationStatus.MACHINE, Languages.NewTranslationStatus(l)));

        Assert.Equal(TranslationStatus.PENDING, Languages.NewSummaryStatus(LanguageCode.vi));
        Assert.Equal(TranslationStatus.PENDING, Languages.NewSummaryStatus(LanguageCode.en));
        Assert.All(Languages.MachineOnly, l => Assert.Equal(TranslationStatus.MACHINE, Languages.NewSummaryStatus(l)));

        Assert.True(Languages.TranslationNeedsReview(LanguageCode.en));
        Assert.False(Languages.TranslationNeedsReview(LanguageCode.vi));
        Assert.All(Languages.MachineOnly, l => Assert.False(Languages.TranslationNeedsReview(l) || Languages.SummaryNeedsReview(l)));
        Assert.True(Languages.SummaryNeedsReview(LanguageCode.vi) && Languages.SummaryNeedsReview(LanguageCode.en));
    }

    [Fact]
    public void Only_approved_and_machine_versions_are_shown_to_visitors()
    {
        Assert.True(Languages.IsVisible(TranslationStatus.APPROVED));
        Assert.True(Languages.IsVisible(TranslationStatus.MACHINE));
        Assert.False(Languages.IsVisible(TranslationStatus.PENDING));
        Assert.False(Languages.IsVisible(TranslationStatus.REJECTED));
    }
}
