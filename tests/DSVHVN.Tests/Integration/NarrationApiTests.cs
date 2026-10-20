using System.Net;
using DSVHVN.Application.Common;
using DSVHVN.Application.Translations;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using DSVHVN.Tests.Support;
using Microsoft.EntityFrameworkCore;
using static DSVHVN.Tests.Integration.DiscoveryTestKit;
using static DSVHVN.Tests.Integration.NarrationTestKit;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Thuyết minh tạo sẵn qua HTTP: quyền chỉ quản trị hệ thống; danh sách còn thiếu theo đúng quy tắc tóm tắt công khai; tải tệp MP3 kèm mốc câu lên và phục vụ tệp
/// tại /files; tóm tắt công khai kèm thuyết minh chỉ khi ngôn ngữ và băm khớp (kể cả khi lùi về tiếng Việt); tóm tắt đổi chữ thì băm cũ bị 409; kiểm trường và tệp; nhật ký thao tác.
/// </summary>
public sealed class NarrationApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly NarrationTestKit _k = new(factory);
    private HttpClient Http => _k.Http;

    [Fact]
    public async Task Only_the_system_admin_can_list_missing_narrations_or_upload_one()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.ApprovedHeritageAsync(admin);
        var item = await _k.MissingItemAsync(admin, "vi", seed.Heritage.Id);
        Assert.NotNull(item);
        var (_, orgAdmin) = await _k.Translations.Discovery.Api.OrgWithAdminAsync();
        var (_, teacher) = await _k.Translations.Discovery.Api.TeacherAsync(orgAdmin);

        foreach (var token in new string?[] { null, orgAdmin, teacher })
        {
            var expected = token is null ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
            var (list, listBody) = await _k.MissingRawAsync(token, "vi");
            Assert.Equal(expected, list.StatusCode);
            var (upload, uploadBody) = await _k.UploadRawAsync<object>(token, seed.Heritage.Id, "vi", Fields(item!.TextHash), FileSamples.Mp3());
            Assert.Equal(expected, upload.StatusCode);
            if (token is not null) Assert.Equal(("FORBIDDEN", "FORBIDDEN"), (listBody.ErrorCode, uploadBody.ErrorCode));
        }
        Assert.NotNull(await _k.MissingItemAsync(admin, "vi", seed.Heritage.Id));
        Assert.Null((await _k.Translations.SummaryAsync(seed.Heritage.Slug!, "vi")).Narration);
    }

    [Fact]
    public async Task Listing_uploading_serving_and_reading_a_narration_work_end_to_end()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.ApprovedHeritageAsync(admin);
        var id = seed.Heritage.Id;
        var slug = seed.Heritage.Slug!;
        var unapproved = await _k.Translations.SeedAsync(admin);
        await _k.Translations.GenerateAsync(admin, unapproved.Heritage.Id);   // tóm tắt tiếng Việt chờ duyệt: chưa hiện ở ngôn ngữ nào

        // Danh sách còn thiếu: đúng chữ của bản tóm tắt ngắn đang hiện, băm 64 ký tự, mã ngôn ngữ ngắn.
        var vi = await _k.MissingAsync(admin, "vi");
        var item = vi.Items.Single(i => i.HeritageId == id);
        var summary = await _k.Translations.SummaryAsync(slug, "vi");
        Assert.Equal((slug, seed.Heritage.Name, "vi", summary.ShortText, NarrationRules.TextHash(summary.ShortText)),
            (item.Slug, item.Name, item.Language, item.ShortText, item.TextHash));
        Assert.DoesNotContain(vi.Items, i => i.HeritageId == unapproved.Heritage.Id);
        Assert.True(vi.Total >= vi.Items.Count);
        var zh = await _k.MissingItemAsync(admin, "zh-Hans", id);
        Assert.Equal(("zh", (await _k.Translations.SummaryAsync(slug, "zh-Hans")).ShortText), (zh!.Language, zh.ShortText));
        Assert.Single((await _k.MissingAsync(admin, "vi", limit: 1)).Items);

        // Tải lên: dòng mới, tệp trong kho được phục vụ tại /files với đúng loại nội dung, nhật ký thao tác.
        var saved = await _k.UploadAsync(admin, id, "vi", item.TextHash);
        Assert.Equal((id, "vi", 9000, "vieneu-mai-anh", item.TextHash, 2), (saved.HeritageId, saved.Language, saved.DurationMs, saved.Voice, saved.TextHash, saved.SentenceCount));
        Assert.StartsWith("/files/thuyet-minh/", saved.AudioUrl);
        Assert.True(File.Exists(_k.FilePath(saved.AudioUrl)));
        using var audio = await Http.GetAsync(saved.AudioUrl);
        Assert.Equal((HttpStatusCode.OK, "audio/mpeg"), (audio.StatusCode, audio.Content.Headers.ContentType?.MediaType));
        Assert.Equal(FileSamples.Mp3(), await audio.Content.ReadAsByteArrayAsync());
        Assert.Null(await _k.MissingItemAsync(admin, "vi", id));
        var audit = await factory.WithDbAsync(db => db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == AuditAction.HERITAGE_NARRATION_CHANGED && a.TargetType == "heritage_narrations").OrderByDescending(a => a.Id).FirstAsync());
        Assert.Contains($"\"heritageId\":{id}", audit.NewValue);
        Assert.DoesNotContain("thuyet-minh/", audit.NewValue);

        // Tóm tắt công khai: tiếng Việt kèm thuyết minh; tiếng Anh (đang hiện, chưa có thuyết minh tiếng Anh) và bản máy không mượn bản tiếng Việt.
        var withNarration = await _k.Translations.SummaryAsync(slug, "vi");
        Assert.NotNull(withNarration.Narration);
        Assert.Equal((saved.AudioUrl, 9000, "vieneu-mai-anh"), (withNarration.Narration!.AudioUrl, withNarration.Narration.DurationMs, withNarration.Narration.Voice));
        Assert.Equal([("Câu thứ nhất của tóm tắt.", 150, 4200), ("Câu thứ hai của tóm tắt.", 4550, 8900)],
            withNarration.Narration.Sentences.Select(s => (s.Text, s.StartMs, s.EndMs)));
        Assert.Null((await _k.Translations.SummaryAsync(slug, "en")).Narration);
        Assert.Null((await _k.Translations.SummaryAsync(slug, "ja")).Narration);

        // Từ chối tóm tắt tiếng Anh: tiếng Anh lùi về tiếng Việt và mang theo thuyết minh tiếng Việt; tiếng Anh cũng rời danh sách còn thiếu.
        await _k.Translations.RejectAsync(admin, id, "en", "SUMMARY");
        var fallback = await _k.Translations.SummaryAsync(slug, "en");
        Assert.True(fallback.Localization.IsFallback);
        Assert.Equal(saved.AudioUrl, fallback.Narration?.AudioUrl);
        Assert.Null(await _k.MissingItemAsync(admin, "en", id));
    }

    [Fact]
    public async Task A_changed_summary_hides_the_narration_lists_it_again_and_refuses_the_old_hash()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.ApprovedHeritageAsync(admin);
        var id = seed.Heritage.Id;
        var slug = seed.Heritage.Slug!;
        var first = await _k.MissingItemAsync(admin, "en", id);
        var saved = await _k.UploadAsync(admin, id, "en", first!.TextHash, voice: "melo-en-us");
        Assert.Equal("melo-en-us", (await _k.Translations.SummaryAsync(slug, "en")).Narration?.Voice);

        // Sửa chữ tóm tắt tiếng Anh: bản về chờ duyệt (lùi về tiếng Việt, không có thuyết minh tiếng Việt); duyệt lại thì chữ mới hiện mà băm đã lệch.
        await Expect(Http.PutAsync<SummaryDetailDto>($"{TranslationTestKit.AdminRoot}/heritages/{id}/translations/en/summary", new { shortText = "The festival text was edited." }, admin));
        Assert.Null((await _k.Translations.SummaryAsync(slug, "en")).Narration);
        Assert.Null(await _k.MissingItemAsync(admin, "en", id));
        await _k.Translations.ApproveAsync(admin, id, "en", "SUMMARY");
        var edited = await _k.Translations.SummaryAsync(slug, "en");
        Assert.Equal(("The festival text was edited.", null), (edited.ShortText, edited.Narration));
        var again = await _k.MissingItemAsync(admin, "en", id);
        Assert.Equal(NarrationRules.TextHash("The festival text was edited."), again!.TextHash);

        // Băm cũ: 409, tệp không được nhận; băm mới: thay dòng cũ, tệp cũ vẫn còn trong kho.
        var before = Directory.GetFiles(Path.Combine(factory.StorageRoot, StoredFileRules.NarrationFolder)).Length;
        var stale = await Fail(_k.UploadRawAsync<object>(admin, id, "en", Fields(first.TextHash), FileSamples.Mp3()), HttpStatusCode.Conflict, MessageCodes.NarrationStale);
        Assert.Equal("textHash", stale.Errors!.Keys.Single());
        Assert.Equal(before, Directory.GetFiles(Path.Combine(factory.StorageRoot, StoredFileRules.NarrationFolder)).Length);

        var replaced = await _k.UploadAsync(admin, id, "en", again.TextHash, FileSamples.Mp3(withTag: false), voice: "melo-en-us-v2");
        Assert.NotEqual(saved.AudioUrl, replaced.AudioUrl);
        Assert.Equal(before + 1, Directory.GetFiles(Path.Combine(factory.StorageRoot, StoredFileRules.NarrationFolder)).Length);
        Assert.True(File.Exists(_k.FilePath(saved.AudioUrl)));
        Assert.Equal(1, await factory.WithDbAsync(db => db.HeritageNarrations.CountAsync(n => n.HeritageId == id)));
        Assert.Equal(("melo-en-us-v2", replaced.AudioUrl), ((await _k.Translations.SummaryAsync(slug, "en")).Narration?.Voice, (await _k.Translations.SummaryAsync(slug, "en")).Narration?.AudioUrl));
    }

    [Fact]
    public async Task Fields_the_file_and_the_size_are_checked_and_unknown_targets_are_404()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.ApprovedHeritageAsync(admin);
        var id = seed.Heritage.Id;
        var hash = (await _k.MissingItemAsync(admin, "vi", id))!.TextHash;

        var bad = new (IDictionary<string, string> Fields, byte[]? Audio, string Field, string Code)[]
        {
            (Fields("khong-phai-bam"), FileSamples.Mp3(), "textHash", "INVALID"),
            (Fields(hash, voice: ""), FileSamples.Mp3(), "voice", "REQUIRED"),
            (Fields(hash, durationMs: 0), FileSamples.Mp3(), "durationMs", "INVALID"),
            (Fields(hash, sentences: "[]"), FileSamples.Mp3(), "sentences", "INVALID"),
            (Fields(hash, sentences: Sentences(("x", 5000, 4000))), FileSamples.Mp3(), "sentences", "INVALID"),
            (Fields(hash), null, "audio", "REQUIRED"),
            (Fields(hash), FileSamples.Png(), "audio", "INVALID"),
        };
        foreach (var (fields, audio, field, code) in bad)
        {
            var body = await Fail(_k.UploadRawAsync<object>(admin, id, "vi", fields, audio), HttpStatusCode.BadRequest, code);
            Assert.Equal(field, body.Errors!.Keys.Single());
        }
        // Vượt 10 MB một chút: dịch vụ từ chối (400); vượt cả phần chừa cho biểu mẫu: máy chủ từ chối sớm bằng 413, cùng câu và cùng trường.
        var slightly = await Fail(_k.UploadRawAsync<object>(admin, id, "vi", Fields(hash), FileSamples.Mp3(frames: StoredFileRules.MaxNarrationBytes / 417 + 1)),
            HttpStatusCode.BadRequest, "INVALID");
        Assert.Equal("audio", slightly.Errors!.Keys.Single());
        var tooLarge = await Fail(_k.UploadRawAsync<object>(admin, id, "vi", Fields(hash), FileSamples.Mp3(frames: 11 * 1024 * 1024 / 417)),
            HttpStatusCode.RequestEntityTooLarge, "INVALID");
        Assert.Equal(("audio", slightly.Message), (tooLarge.Errors!.Keys.Single(), tooLarge.Message));

        await Fail(_k.UploadRawAsync<object>(admin, id, "xx", Fields(hash), FileSamples.Mp3()), HttpStatusCode.BadRequest, "INVALID");
        await Fail(_k.UploadRawAsync<object>(admin, 999_999, "vi", Fields(hash), FileSamples.Mp3()), HttpStatusCode.NotFound, "BLOCKED");
        var draft = await _k.Translations.Discovery.DraftAsync(admin, new Spec("Chưa tóm tắt " + Unique()));
        await Fail(_k.UploadRawAsync<object>(admin, draft.Id, "vi", Fields(hash), FileSamples.Mp3()), HttpStatusCode.NotFound, "BLOCKED");

        foreach (var query in new[] { "language=vi&limit=0", "language=vi&limit=1001", "language=zh-TW", "language=" })
            await Fail(Http.GetAsync<object>($"{AdminRoot}/narrations/missing?{query}", admin), HttpStatusCode.BadRequest, "INVALID");

        Assert.Null((await _k.Translations.SummaryAsync(seed.Heritage.Slug!, "vi")).Narration);
        Assert.False(await factory.WithDbAsync(db => db.HeritageNarrations.AnyAsync(n => n.HeritageId == id)));
        var folder = Path.Combine(factory.StorageRoot, StoredFileRules.NarrationFolder);
        Assert.True(!Directory.Exists(folder) || Directory.GetFiles(folder).All(f => !File.ReadAllBytes(f).AsSpan().SequenceEqual(FileSamples.Mp3(frames: 11 * 1024 * 1024 / 417))));
    }
}
