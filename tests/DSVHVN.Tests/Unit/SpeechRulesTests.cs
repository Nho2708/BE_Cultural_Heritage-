using DSVHVN.Domain.Rules;

namespace DSVHVN.Tests.Unit;

/// <summary>Quy tắc thuần của giọng đọc tự host: chuẩn hóa chữ, băm khóa bộ nhớ đệm, mã giọng, khóa tệp và tách câu.</summary>
public sealed class SpeechRulesTests
{
    // ---- Chuẩn hóa ----

    [Theory]
    [InlineData("  Chào   các em  ", "Chào các em")]
    [InlineData("Một\r\nhai\t\tba\n\n", "Một hai ba")]
    [InlineData("Đã gọn rồi.", "Đã gọn rồi.")]
    [InlineData("a", "a")]
    public void Normalize_trims_and_collapses_every_run_of_whitespace_into_one_space(string raw, string expected) =>
        Assert.Equal(expected, SpeechRules.Normalize(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t\r\n ")]
    public void Normalize_returns_null_for_empty_text(string? raw) => Assert.Null(SpeechRules.Normalize(raw));

    [Fact]
    public void Normalize_accepts_exactly_the_maximum_length_and_refuses_one_more()
    {
        Assert.Equal(SpeechRules.TextMaxLength, SpeechRules.Normalize(new string('a', SpeechRules.TextMaxLength))!.Length);
        Assert.Null(SpeechRules.Normalize(new string('a', SpeechRules.TextMaxLength + 1)));
    }

    [Fact]
    public void Length_is_counted_after_collapsing_whitespace()
    {
        var spaced = string.Join("   ", Enumerable.Repeat("ab", 300));   // 600 + 299 * 3 = 1497 ký tự thô, 899 sau khi gộp
        Assert.True(spaced.Length > SpeechRules.TextMaxLength);
        Assert.Equal(899, SpeechRules.Normalize(spaced)!.Length);
    }

    // ---- Băm ----

    [Fact]
    public void Hash_is_64_lowercase_hex_and_stable()
    {
        var hash = SpeechRules.Hash("Chào các em", SpeechRules.DefaultVoice);

        Assert.True(NarrationRules.IsValidHash(hash));
        Assert.Equal(hash, SpeechRules.Hash("Chào các em", SpeechRules.DefaultVoice));
    }

    [Fact]
    public void Texts_that_differ_only_in_whitespace_share_one_hash_after_normalizing()
    {
        var a = SpeechRules.Hash(SpeechRules.Normalize("Chào   các em")!, SpeechRules.DefaultVoice);
        var b = SpeechRules.Hash(SpeechRules.Normalize("  Chào các\nem ")!, SpeechRules.DefaultVoice);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Hash_changes_with_the_text_and_with_the_voice()
    {
        var baseHash = SpeechRules.Hash("Chào các em", "vieneu-mai-anh");

        Assert.NotEqual(baseHash, SpeechRules.Hash("Chào các em.", "vieneu-mai-anh"));
        Assert.NotEqual(baseHash, SpeechRules.Hash("Chào các em", "vieneu-khac"));
        Assert.NotEqual(SpeechRules.Hash("a", "bc"), SpeechRules.Hash("bc", "a"));
    }

    // ---- Mã giọng và khóa tệp ----

    [Theory]
    [InlineData("vieneu-mai-anh", true)]
    [InlineData("a", true)]
    [InlineData("0abc", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("-abc", false)]
    [InlineData("Mai", false)]
    [InlineData("mai anh", false)]
    [InlineData("mai/anh", false)]
    [InlineData("..", false)]
    public void Voice_codes_are_safe_folder_names(string? voice, bool valid) => Assert.Equal(valid, SpeechRules.IsValidVoice(voice));

    [Fact]
    public void A_voice_code_is_at_most_64_characters()
    {
        Assert.True(SpeechRules.IsValidVoice(new string('a', SpeechRules.VoiceFolderMaxLength)));
        Assert.False(SpeechRules.IsValidVoice(new string('a', SpeechRules.VoiceFolderMaxLength + 1)));
    }

    [Fact]
    public void The_clip_key_is_a_valid_storage_key_in_the_speech_folder_and_stays_raw_on_cloudinary()
    {
        var hash = SpeechRules.Hash("Chào các em", SpeechRules.DefaultVoice);
        var key = StoredFileRules.SpeechClipKey(SpeechRules.DefaultVoice, hash);

        Assert.Equal($"giong-doc/vieneu-mai-anh/{hash}.mp3", key);
        Assert.True(StoredFileRules.IsValidKey(key));
        Assert.True(StoredFileRules.IsRawKey(key));
        Assert.False(StoredFileRules.IsValidKey($"giong-doc/../{hash}.mp3"));
        Assert.False(StoredFileRules.IsValidKey($"giong-doc/Mai/{hash}.mp3"));
        Assert.False(StoredFileRules.IsValidKey("giong-doc/vieneu-mai-anh/abc.mp3"));
    }

    // ---- Tách câu ----

    [Fact]
    public void Sentences_split_after_sentence_ending_punctuation_followed_by_a_space()
    {
        var sentences = SpeechRules.SplitSentences("Hội Gióng rất vui. Em có thích không? Có chứ! Tuyệt quá… Hết rồi.");

        Assert.Equal(["Hội Gióng rất vui.", "Em có thích không?", "Có chứ!", "Tuyệt quá…", "Hết rồi."], sentences);
    }

    [Fact]
    public void Punctuation_inside_a_word_or_number_does_not_split_and_the_final_mark_stays()
    {
        Assert.Equal(["Năm 1010 vua dời đô về Thăng Long.", "Nước ta khoảng 3.5 triệu km2 vùng biển!"],
            SpeechRules.SplitSentences("Năm 1010 vua dời đô về Thăng Long. Nước ta khoảng 3.5 triệu km2 vùng biển!"));
        Assert.Equal(["Không có dấu chấm cuối"], SpeechRules.SplitSentences("Không có dấu chấm cuối"));
    }

    [Fact]
    public void A_closing_quote_after_the_mark_stays_with_its_sentence_and_newlines_count_as_spaces()
    {
        Assert.Equal(["Bà kể: \"Ngày xưa lắm rồi.\"", "Em nghe chăm chú.", "Rồi em kể lại."],
            SpeechRules.SplitSentences("Bà kể: \"Ngày xưa lắm rồi.\" Em nghe chăm chú.\nRồi em kể lại."));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void Splitting_empty_text_gives_no_sentences(string? text) => Assert.Empty(SpeechRules.SplitSentences(text));
}
