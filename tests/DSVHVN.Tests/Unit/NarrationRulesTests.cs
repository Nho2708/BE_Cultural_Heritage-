using System.Text;
using DSVHVN.Domain.Rules;
using DSVHVN.Tests.Support;

namespace DSVHVN.Tests.Unit;

/// <summary>Quy tắc thuần của thuyết minh tạo sẵn: băm chữ, dạng băm, nhận dạng MP3 bằng nội dung, kiểm danh sách câu, khóa tệp trong kho.</summary>
public sealed class NarrationRulesTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void The_hash_is_sha256_of_the_utf8_bytes_as_stored_in_lowercase_hex()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", NarrationRules.TextHash("abc"));
        Assert.Equal(NarrationRules.HashLength, NarrationRules.TextHash("Hội Gióng ở đền Phù Đổng.").Length);

        // Không cắt khoảng trắng, không chuẩn hóa: khác một ký tự là khác băm.
        Assert.NotEqual(NarrationRules.TextHash("Hội Gióng."), NarrationRules.TextHash("Hội Gióng. "));
        Assert.NotEqual(NarrationRules.TextHash("Hội Gióng."), NarrationRules.TextHash("Hội Gióng.".Normalize(NormalizationForm.FormD)));
        Assert.Equal(NarrationRules.TextHash("경복궁"), NarrationRules.TextHash("경복궁"));
    }

    [Theory]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", true)]
    [InlineData("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", false)]   // phải hex thường
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015a", false)]    // 63 ký tự
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad0", false)]  // 65 ký tự
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_64_lowercase_hex_characters_are_a_valid_hash(string? hash, bool expected) =>
        Assert.Equal(expected, NarrationRules.IsValidHash(hash));

    [Fact]
    public void An_mp3_is_recognised_by_its_id3_tag_or_its_first_mpeg_frame_not_by_its_name()
    {
        Assert.True(NarrationRules.IsMp3(FileSamples.Mp3()));
        Assert.True(NarrationRules.IsMp3(FileSamples.Mp3(withTag: false)));
        Assert.True(NarrationRules.IsMp3([0xFF, 0xF3, 0x40, 0xC0]));   // MPEG-2 lớp III
        Assert.True(NarrationRules.IsMp3([0xFF, 0xE3, 0x18, 0xC4]));   // MPEG-2.5

        Assert.False(NarrationRules.IsMp3([]));
        Assert.False(NarrationRules.IsMp3([0xFF, 0xFB]));              // quá ngắn
        Assert.False(NarrationRules.IsMp3([0xFF, 0xD8, 0xFF, 0xE0]));  // JPEG
        Assert.False(NarrationRules.IsMp3(FileSamples.Png()));
        Assert.False(NarrationRules.IsMp3(FileSamples.Glb()));
        Assert.False(NarrationRules.IsMp3(Encoding.ASCII.GetBytes("RIFF....WAVEfmt ")));
        Assert.False(NarrationRules.IsMp3(Encoding.ASCII.GetBytes("ID2 not a tag")));
    }

    private static NarrationSentence S(string text, int start, int end) => new(text, start, end);

    [Fact]
    public void A_well_formed_sentence_list_passes()
    {
        var sentences = new[] { S("Hội Gióng là lễ hội.", 150, 4200), S("Lễ hội ở Phù Đổng.", 4550, 8900), S("Câu cuối.", 9250, 12400) };
        Assert.Equal(SentenceProblem.None, NarrationRules.Check(sentences, 12000));   // câu cuối vượt thời lượng 400 ms: trong biên 1000 ms
        Assert.Equal(SentenceProblem.None, NarrationRules.Check([S("Một câu.", 0, 1)], 1));
        Assert.Equal(SentenceProblem.None, NarrationRules.Check([S("A.", 0, 10), S("B.", 0, 20)], 20));   // startMs bằng nhau là không giảm
    }

    [Fact]
    public void Sentence_lists_are_refused_for_count_text_range_duration_and_order()
    {
        Assert.Equal(SentenceProblem.Count, NarrationRules.Check([], 1000));
        Assert.Equal(SentenceProblem.Count, NarrationRules.Check(Enumerable.Range(0, NarrationRules.MaxSentences + 1).Select(i => S("x", i, i + 1)).ToList(), 100_000));
        Assert.Equal(SentenceProblem.None, NarrationRules.Check(Enumerable.Range(0, NarrationRules.MaxSentences).Select(i => S("x", i, i + 1)).ToList(), 100_000));

        Assert.Equal(SentenceProblem.Text, NarrationRules.Check([S("   ", 0, 100)], 1000));
        Assert.Equal(SentenceProblem.Text, NarrationRules.Check([S(new string('a', NarrationRules.SentenceMaxLength + 1), 0, 100)], 1000));
        Assert.Equal(SentenceProblem.None, NarrationRules.Check([S(new string('a', NarrationRules.SentenceMaxLength), 0, 100)], 1000));

        Assert.Equal(SentenceProblem.Range, NarrationRules.Check([S("x", -1, 100)], 1000));
        Assert.Equal(SentenceProblem.Range, NarrationRules.Check([S("x", 100, 100)], 1000));
        Assert.Equal(SentenceProblem.Range, NarrationRules.Check([S("x", 200, 100)], 1000));

        Assert.Equal(SentenceProblem.BeyondDuration, NarrationRules.Check([S("x", 0, 2001)], 1000));
        Assert.Equal(SentenceProblem.None, NarrationRules.Check([S("x", 0, 2000)], 1000));

        Assert.Equal(SentenceProblem.Order, NarrationRules.Check([S("a", 500, 900), S("b", 400, 1000)], 1000));
    }

    [Theory]
    [InlineData("/files/{key}", true)]
    [InlineData("https://api.example.vn/files/{key}", true)]
    [InlineData("https://res.cloudinary.com/demo/raw/upload/dsvhvn/{key}", true)]
    [InlineData("https://res.cloudinary.com/demo/raw/upload/v1700000000/dsvhvn/{key}", true)]
    [InlineData("https://res.cloudinary.com/demo/image/upload/dsvhvn/{key}", false)]
    [InlineData("https://evil.example/{key}", false)]
    [InlineData("/files/models/" + Id + ".glb", false)]
    [InlineData("/files/thuyet-minh/" + Id + ".wav", false)]
    [InlineData("/files/thuyet-minh/" + Id + ".MP3", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_addresses_of_narration_files_in_this_store_yield_a_key(string? template, bool recognised)
    {
        var key = StoredFileRules.NarrationKey(Guid.ParseExact(Id, "N"));
        var url = template?.Replace("{key}", key);
        Assert.Equal(recognised ? key : null, NarrationRules.StoredNarrationKey(url));
    }

    [Fact]
    public void Narration_keys_live_in_their_own_folder_and_are_raw_files()
    {
        var key = StoredFileRules.NarrationKey(Guid.ParseExact(Id, "N"));
        Assert.Equal($"thuyet-minh/{Id}.mp3", key);
        Assert.True(StoredFileRules.IsValidKey(key));
        Assert.True(StoredFileRules.IsStoredPath("/files/" + key));
        Assert.True(StoredFileRules.IsRawKey(key));
        Assert.True(StoredFileRules.IsRawKey(StoredFileRules.ModelKey(Guid.Empty)));
        Assert.False(StoredFileRules.IsRawKey(StoredFileRules.ImageKey(Guid.Empty, StoredFileRules.Jpeg)));
        Assert.Null(StoredFileRules.ThumbnailUrlOf("/files/" + key));
    }
}
