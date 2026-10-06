using System.Net;
using DSVHVN.Application.Common;
using DSVHVN.Application.Discovery;
using DSVHVN.Application.Heritages;
using DSVHVN.Application.Translations;
using DSVHVN.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using static DSVHVN.Tests.Integration.DiscoveryTestKit;
using static DSVHVN.Tests.Integration.TranslationTestKit;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Nội dung công khai theo ngôn ngữ (web học tập và app, không cần đăng nhập): tiếng Anh chỉ hiện bản đã duyệt, tiếng Hàn, Trung, Nhật hiện bản dịch máy kèm cờ,
/// thiếu bản dịch thì lùi về tiếng Việt kèm cờ, ngôn ngữ chọn bằng tham số hoặc tiêu đề Accept-Language, học sinh đã đăng nhập luôn nhận tiếng Việt,
/// tóm tắt ngắn và chi tiết (chỉ hiện sau khi tóm tắt tiếng Việt được duyệt), tìm kiếm khớp cả bản tiếng Việt lẫn bản dịch.
/// </summary>
public sealed class HeritageLocalizationApiTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly TranslationTestKit _k = new(factory);
    private HttpClient Http => _k.Http;

    public Task InitializeAsync()
    {
        factory.HeritageAi.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        factory.HeritageAi.Reset();
        return Task.CompletedTask;
    }

    // ---- Chọn ngôn ngữ ----

    [Fact]
    public async Task The_five_languages_are_listed_with_their_own_names_and_speech_voices()
    {
        var languages = await _k.PublicAsync<List<LanguageOptionDto>>($"{PublicRoot}/languages");

        Assert.Equal(["vi", "en", "ko", "zh-Hans", "ja"], languages.Select(l => l.Code));
        Assert.Equal(["Tiếng Việt", "English", "한국어", "简体中文", "日本語"], languages.Select(l => l.NativeName));
        Assert.Equal(["vi-VN", "en-US", "ko-KR", "zh-CN", "ja-JP"], languages.Select(l => l.SpeechTag));
        Assert.Equal(["vi"], languages.Where(l => l.IsSource).Select(l => l.Code));
        Assert.Equal(["ko", "zh-Hans", "ja"], languages.Where(l => l.IsMachineOnly).Select(l => l.Code));
        Assert.Equal("3600", (await Http.GetAsync($"{PublicRoot}/languages")).Headers.CacheControl!.MaxAge!.Value.TotalSeconds.ToString());
    }

    [Fact]
    public async Task Language_comes_from_the_lang_parameter_then_the_header_and_anything_else_means_vietnamese()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin);
        await _k.GenerateAsync(admin, seed.Heritage.Id);
        var slug = seed.Heritage.Slug!;

        string Language(PublicHeritageDetailDto d) => d.Localization!.Language;

        Assert.Equal("ko", Language(await _k.DetailAsync(slug, "ko")));
        Assert.Equal("zh-Hans", Language(await _k.DetailAsync(slug, "zh-Hans")));
        Assert.Equal("zh-Hans", Language(await _k.DetailAsync(slug, "zh")));
        Assert.Equal("zh-Hans", Language(await _k.DetailAsync(slug, "zh-CN")));
        Assert.Equal("ja", Language(await _k.DetailAsync(slug, "JA-jp")));
        Assert.Equal("ko", Language(await _k.DetailAsync(slug, acceptLanguage: "ko-KR,ko;q=0.9,en;q=0.8")));
        Assert.Equal("ja", Language(await _k.DetailAsync(slug, acceptLanguage: "fr-FR,fr;q=0.9,ja;q=0.8")));
        Assert.Equal("ko", Language(await _k.DetailAsync(slug, "ko", acceptLanguage: "ja")));               // tham số thắng tiêu đề
        Assert.Equal("vi", Language(await _k.DetailAsync(slug)));                                          // không có gì: tiếng Việt
        Assert.Equal("vi", Language(await _k.DetailAsync(slug, acceptLanguage: "fr-FR")));
        // Mã không thuộc năm ngôn ngữ (kể cả tiếng Trung phồn thể) bị bỏ qua: tiếng Việt, không phải lỗi.
        foreach (var unsupported in new[] { "fr", "zh-TW", "zh-Hant", "xx", "de-DE" })
        {
            var detail = await _k.DetailAsync(slug, unsupported, acceptLanguage: "ko");
            Assert.Equal(("vi", "vi", false, seed.Heritage.Name), (detail.Localization!.RequestedLanguage, detail.Localization.Language, detail.Localization.IsFallback, detail.Name));
        }
        var response = await Http.GetAsync($"{PublicRoot}/heritages/{slug}?lang=ko");
        Assert.Contains("Accept-Language", response.Headers.Vary);
    }

    [Fact]
    public async Task A_signed_in_student_always_gets_vietnamese_on_public_pages()
    {
        var admin = await _k.AdminAsync();
        var lessons = new LessonTestKit(factory);
        var (school, schoolClass) = await lessons.SchoolWithClassAsync(2);
        var studentToken = await lessons.StudentTokenAsync(school.TeacherToken, schoolClass.Id);
        var seed = await _k.SeedAsync(admin);
        await _k.GenerateAsync(admin, seed.Heritage.Id);
        await _k.ApproveAllAsync(admin, seed.Heritage.Id);
        var slug = seed.Heritage.Slug!;

        var asGuest = await _k.DetailAsync(slug, "ko");
        Assert.Equal(($"[ko] [en] {seed.Heritage.Name}", "ko"), (asGuest.Name, asGuest.Localization!.Language));   // bản máy dịch lại từ bản tiếng Anh đã duyệt

        var asStudent = await _k.DetailAsync(slug, "ko", acceptLanguage: "ko", token: studentToken);
        Assert.Equal((seed.Heritage.Name, "vi", "vi", false), (asStudent.Name, asStudent.Localization!.Language, asStudent.Localization.RequestedLanguage, asStudent.Localization.IsFallback));
        Assert.Equal("vi", (await _k.PublicAsync<PublicTimelineDto>($"{PublicRoot}/heritages/{slug}/timeline", "ja", token: studentToken)).Localization!.Language);
        var summary = await _k.SummaryAsync(slug, "en", token: studentToken);
        Assert.Equal(("vi", false), (summary.Localization.Language, summary.Localization.IsFallback));
        Assert.StartsWith("Hoàng thành", summary.ShortText);
        var card = (await _k.PublicAsync<PagedResult<HeritageCardDto>>($"{PublicRoot}/heritages?{K(seed.Tag)}&lang=zh", token: studentToken)).Items.Single();
        Assert.Equal((seed.Heritage.Name, false, false), (card.Name, card.IsFallback, card.IsMachineTranslated));

        // Giáo viên (không phải học sinh) vẫn chọn được ngôn ngữ.
        Assert.Equal("ko", (await _k.DetailAsync(slug, "ko", token: school.TeacherToken)).Localization!.Language);
    }

    // ---- Chi tiết, dòng thời gian ----

    [Fact]
    public async Task Detail_shows_each_language_by_the_review_rules_and_falls_back_to_vietnamese_with_a_flag()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin);
        var id = seed.Heritage.Id;
        var slug = seed.Heritage.Slug!;

        // Chưa dịch: mọi ngôn ngữ lùi về tiếng Việt, báo thiếu bản dịch (không lỗi, không trống).
        foreach (var language in new[] { "en", "ko", "zh-Hans", "ja" })
        {
            var detail = await _k.DetailAsync(slug, language);
            Assert.Equal((language, "vi", true, false), (detail.Localization!.RequestedLanguage, detail.Localization.Language, detail.Localization.IsFallback, detail.Localization.IsMachineTranslated));
            Assert.Equal((seed.Heritage.Name, seed.Heritage.Content), (detail.Name, detail.Content));
        }
        Assert.Equal(("vi", false), ((await _k.DetailAsync(slug, "vi")).Localization!.Language, (await _k.DetailAsync(slug, "vi")).Localization!.IsFallback));

        await _k.GenerateAsync(admin, id);

        // Tiếng Anh đang chờ duyệt: chưa hiện. Hàn, Trung, Nhật hiện ngay kèm nhãn bản dịch máy.
        var pendingEnglish = await _k.DetailAsync(slug, "en");
        Assert.Equal((true, seed.Heritage.Name), (pendingEnglish.Localization!.IsFallback, pendingEnglish.Name));
        foreach (var language in new[] { "ko", "zh-Hans", "ja" })
        {
            var machine = await _k.DetailAsync(slug, language);
            Assert.Equal((language, false, true), (machine.Localization!.Language, machine.Localization.IsFallback, machine.Localization.IsMachineTranslated));
            Assert.Equal($"[{language}] {seed.Heritage.Name}", machine.Name);
            Assert.StartsWith($"[{language}] Trung tâm", machine.ShortDescription);
            Assert.StartsWith($"[{language}] ", machine.Content);
            Assert.Equal($"[{language}] {seed.Phase.Name}", Assert.Single(machine.Timeline).Name);
            Assert.Equal(seed.Heritage.Address, machine.Address);                 // địa chỉ không dịch
            Assert.All(machine.Media, m => Assert.StartsWith($"[{language}] ", m.Caption));
            Assert.Equal(seed.Heritage.Media.Single().Credit, machine.Media.Single().Credit);   // ghi công không dịch
            Assert.Equal(seed.Heritage.References.Single().Title, machine.References.Single().Title);   // nguồn không dịch
            Assert.Equal(seed.Heritage.Province!.Name, machine.Province!.Name);
        }

        // Duyệt tiếng Anh: hiện, không còn nhãn bản dịch máy.
        await _k.ApproveAsync(admin, id, "en", "TRANSLATION");
        var english = await _k.DetailAsync(slug, "en");
        Assert.Equal(("en", false, false, $"[en] {seed.Heritage.Name}"), (english.Localization!.Language, english.Localization.IsFallback, english.Localization.IsMachineTranslated, english.Name));
        Assert.Equal(seed.Heritage.Latitude, english.Latitude);
        Assert.Equal(seed.Heritage.Slug, english.Slug);                           // đường dẫn không đổi theo ngôn ngữ

        // Từ chối tiếng Anh: lại không hiện.
        await _k.RejectAsync(admin, id, "en", "TRANSLATION", "Sai");
        Assert.True((await _k.DetailAsync(slug, "en")).Localization!.IsFallback);
        // Tiếng Việt luôn là bản gốc.
        Assert.Equal(seed.Heritage.Name, (await _k.DetailAsync(slug, "vi")).Name);
        // Nội dung nháp hay đã ẩn không lộ qua bản dịch.
        await Expect(Http.PostAsync<HeritageDetailDto>($"{AdminRoot}/heritages/{id}/archive", null, admin));
        await Fail(Http.GetAsync<object>($"{PublicRoot}/heritages/{slug}?lang=ko"), HttpStatusCode.NotFound, "BLOCKED");
    }

    [Fact]
    public async Task A_part_without_its_own_translation_falls_back_to_vietnamese_only_for_that_part()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin);
        var id = seed.Heritage.Id;
        await _k.GenerateAsync(admin, id);
        await _k.ApproveAsync(admin, id, "en", "TRANSLATION");

        // Quản trị xóa bản dịch mô tả ngắn, chú thích ảnh sự kiện và tên giai đoạn: trang vẫn đủ chữ, phần bị xóa hiện tiếng Việt.
        await Expect(Http.PutAsync<TranslationDetailDto>($"{AdminRoot}/heritages/{id}/translations/en/translation", new
        {
            shortDescription = "",
            timelines = new[] { new { id = seed.Phase.Id, name = " " } },
            media = new[] { new { id = seed.HeritageMediaId, caption = "" } },
        }, admin));
        await _k.ApproveAsync(admin, id, "en", "TRANSLATION");

        var detail = await _k.DetailAsync(seed.Heritage.Slug!, "en");
        Assert.False(detail.Localization!.IsFallback);
        Assert.Equal($"[en] {seed.Heritage.Name}", detail.Name);
        Assert.Equal(seed.Heritage.ShortDescription, detail.ShortDescription);
        Assert.Equal(seed.Phase.Name, Assert.Single(detail.Timeline).Name);
        Assert.Equal(seed.Heritage.Media.Single().Caption, detail.Media.Single().Caption);

        var timeline = await _k.TimelineAsync(seed.Heritage.Slug!, "en");
        var phase = Assert.Single(timeline.Phases);
        Assert.Equal((seed.Phase.Name, seed.Phase.Description), (phase.Name, phase.Description));
        var item = Assert.Single(phase.Events);
        Assert.Equal($"[en] {seed.Event.Name}", item.Name);
        Assert.Equal($"[en] {seed.Event.Description}", item.Description);
        Assert.StartsWith("[en] Chiếu dời đô", Assert.Single(item.Media).Caption);
    }

    [Fact]
    public async Task The_timeline_is_translated_with_its_events_and_event_images()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin);
        await _k.GenerateAsync(admin, seed.Heritage.Id);
        var slug = seed.Heritage.Slug!;

        var ko = await _k.TimelineAsync(slug, "ko");
        Assert.Equal(("ko", false, true), (ko.Localization!.Language, ko.Localization.IsFallback, ko.Localization.IsMachineTranslated));
        Assert.Equal($"[ko] {seed.Heritage.Name}", ko.Name);
        var phase = Assert.Single(ko.Phases);
        Assert.Equal(($"[ko] {seed.Phase.Name}", $"[ko] {seed.Phase.Description}", "1010 – 1225"), (phase.Name, phase.Description, phase.RangeLabel));
        var item = Assert.Single(phase.Events);
        Assert.Equal(($"[ko] {seed.Event.Name}", "21/07/1010"), (item.Name, item.DateLabel));
        Assert.StartsWith("[ko] Chiếu dời đô", Assert.Single(item.Media).Caption);

        // Tiếng Anh chờ duyệt thì dòng thời gian cũng lùi về tiếng Việt, trên cả trang.
        var en = await _k.TimelineAsync(slug, "en");
        Assert.Equal((true, seed.Phase.Name), (en.Localization!.IsFallback, Assert.Single(en.Phases).Name));
        var vi = await _k.TimelineAsync(slug);
        Assert.Equal((false, seed.Heritage.Name), (vi.Localization!.IsFallback, vi.Name));
    }

    // ---- Danh sách, trang chủ, bản đồ ----

    [Fact]
    public async Task Cards_pins_and_featured_heritages_are_localized_per_item_with_flags()
    {
        var admin = await _k.AdminAsync();
        var translated = await _k.SeedAsync(admin);
        var plain = await _k.SeedAsync(admin);
        await _k.GenerateAsync(admin, translated.Heritage.Id);
        await _k.ApproveAsync(admin, translated.Heritage.Id, "en", "TRANSLATION");
        var tag = translated.Tag;

        // Thẻ trong kết quả tìm: mỗi thẻ theo bản dịch của riêng nó; thẻ thiếu bản dịch lùi về tiếng Việt và mang cờ.
        var en = (await _k.SearchAsync(K(translated.Tag), "en")).Items.Single();
        Assert.Equal(($"[en] {translated.Heritage.Name}", false, false), (en.Name, en.IsFallback, en.IsMachineTranslated));
        Assert.StartsWith("[en] Trung tâm", en.ShortDescription);
        var ko = (await _k.SearchAsync(K(tag), "ko")).Items.Single();
        Assert.Equal(($"[ko] [en] {translated.Heritage.Name}", false, true), (ko.Name, ko.IsFallback, ko.IsMachineTranslated));
        var fallback = (await _k.SearchAsync(K(plain.Tag), "en")).Items.Single();
        Assert.Equal((plain.Heritage.Name, true, false), (fallback.Name, fallback.IsFallback, fallback.IsMachineTranslated));
        Assert.Equal(plain.Heritage.ShortDescription, fallback.ShortDescription);
        // Cùng một trang kết quả trộn thẻ có và thiếu bản dịch.
        var both = await _k.SearchAsync(K("Hoàng thành"), "en");
        Assert.False(both.Items.Single(i => i.Id == translated.Heritage.Id).IsFallback);
        Assert.True(both.Items.Single(i => i.Id == plain.Heritage.Id).IsFallback);
        // Tiếng Việt: không cờ nào.
        var vi = (await _k.SearchAsync(K(tag))).Items.Single();
        Assert.Equal((translated.Heritage.Name, false, false), (vi.Name, vi.IsFallback, vi.IsMachineTranslated));

        // Ghim bản đồ theo ngôn ngữ.
        var pins = await _k.PublicAsync<HeritageMapDto>($"{PublicRoot}/heritages/map?{K("Hoàng thành")}", "ja");
        var pin = pins.Pins.Single(p => p.Id == translated.Heritage.Id);
        Assert.Equal(($"[ja] [en] {translated.Heritage.Name}", false, true), (pin.Name, pin.IsFallback, pin.IsMachineTranslated));
        Assert.StartsWith("[ja] [en] Trung tâm", pin.ShortDescription);
        Assert.Equal((plain.Heritage.Name, true), (pins.Pins.Single(p => p.Id == plain.Heritage.Id).Name, pins.Pins.Single(p => p.Id == plain.Heritage.Id).IsFallback));

        // Trang chủ: di sản nổi bật cũng theo ngôn ngữ.
        var unesco = await _k.SeedAsync(admin, name: $"Quần thể nổi bật {Unique()}");
        await Expect(Http.PutAsync<HeritageDetailDto>($"{AdminRoot}/heritages/{unesco.Heritage.Id}", new
        {
            name = unesco.Heritage.Name, slug = unesco.Heritage.Slug, shortDescription = unesco.Heritage.ShortDescription, content = unesco.Heritage.Content,
            heritageType = "TANGIBLE", recognitionLevel = "UNESCO", recognizedYear = 1999, provinceId = unesco.Heritage.Province!.Id,
            address = unesco.Heritage.Address, latitude = unesco.Heritage.Latitude, longitude = unesco.Heritage.Longitude,
        }, admin));
        await _k.GenerateAsync(admin, unesco.Heritage.Id);
        var home = await _k.PublicAsync<HomeDto>($"{PublicRoot}/home", "zh-Hans");
        var featured = home.Featured.Single(f => f.Id == unesco.Heritage.Id);
        Assert.Equal(($"[zh-Hans] {unesco.Heritage.Name}", true), (featured.Name, featured.IsMachineTranslated));
        Assert.Equal(unesco.Heritage.Name, (await _k.PublicAsync<HomeDto>($"{PublicRoot}/home")).Featured.Single(f => f.Id == unesco.Heritage.Id).Name);
    }

    // ---- Tìm kiếm ----

    [Fact]
    public async Task Search_matches_the_vietnamese_text_and_the_translation_of_the_chosen_language()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin);
        var other = await _k.SeedAsync(admin);
        var id = seed.Heritage.Id;
        await _k.GenerateAsync(admin, id);
        await _k.ApproveAsync(admin, id, "en", "TRANSLATION");
        // Dùng chữ thật ở từng ngôn ngữ để chứng minh khớp theo bản dịch chứ không theo tiếng Việt.
        var unique = "Citadel" + Unique();
        var korean = "탕롱" + Unique();
        await Expect(Http.PutAsync<TranslationDetailDto>($"{AdminRoot}/heritages/{id}/translations/en/translation",
            new { name = $"Imperial {unique}", shortDescription = "Seat of power for centuries", content = "The long history of the royal palace." }, admin));
        await _k.ApproveAsync(admin, id, "en", "TRANSLATION");      // duyệt xong, bản Hàn bị dịch lại từ bản này
        await factory.WithDbAsync(async db =>
        {
            await db.HeritageTranslations.Where(t => t.HeritageId == id && t.LanguageCode == LanguageCode.ko)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Name, $"황궁 {korean}").SetProperty(t => t.Content, "수세기 동안 이어진 궁궐의 긴 역사"));
            return 0;
        });

        // Tiếng Anh: khớp tên, mô tả ngắn, nội dung của bản dịch (hoa thường không phân biệt); nhiều từ phải có đủ.
        Assert.Equal([id], (await _k.SearchAsync(K(unique), "en")).Items.Select(i => i.Id));
        Assert.Equal([id], (await _k.SearchAsync(K(unique.ToLowerInvariant()), "en")).Items.Select(i => i.Id));
        Assert.Equal([id], (await _k.SearchAsync(K($"imperial {unique}"), "en")).Items.Select(i => i.Id));
        Assert.Contains(id, (await _k.SearchAsync(K("seat of power"), "en")).Items.Select(i => i.Id));
        Assert.Contains(id, (await _k.SearchAsync(K("royal palace"), "en")).Items.Select(i => i.Id));
        Assert.Empty((await _k.SearchAsync(K($"{unique} khongco"), "en")).Items);
        // Trong khi vẫn khớp bản tiếng Việt.
        Assert.Equal([id], (await _k.SearchAsync(K(seed.Tag), "en")).Items.Select(i => i.Id));
        Assert.Equal([id], (await _k.SearchAsync(K($"hoang thanh {seed.Tag}"), "en")).Items.Select(i => i.Id));
        // Không đổi ngôn ngữ thì không khớp bản dịch.
        Assert.Empty((await _k.SearchAsync(K(unique))).Items);
        Assert.Empty((await _k.SearchAsync(K(unique), "ko")).Items);
        // Tiếng Hàn: chữ Hàn khớp bản dịch máy (hiện ngay).
        Assert.Equal([id], (await _k.SearchAsync(K(korean), "ko")).Items.Select(i => i.Id));
        Assert.Contains(id, (await _k.SearchAsync(K("궁궐의 긴"), "ko")).Items.Select(i => i.Id));
        Assert.Empty((await _k.SearchAsync(K(korean), "ja")).Items);
        // Bản đồ dùng cùng bộ lọc.
        Assert.Equal([id], (await _k.PublicAsync<HeritageMapDto>($"{PublicRoot}/heritages/map?{K(unique)}", "en")).Pins.Select(p => p.Id));
        Assert.Empty((await _k.PublicAsync<HeritageMapDto>($"{PublicRoot}/heritages/map?{K(unique)}")).Pins);

        // Bản dịch chưa hiện (chờ duyệt, bị từ chối) thì không tìm ra bằng chữ của chính nó.
        await Expect(Http.PutAsync<TranslationDetailDto>($"{AdminRoot}/heritages/{id}/translations/en/translation", new { name = $"Hidden {unique}" }, admin));
        Assert.Empty((await _k.SearchAsync(K($"hidden {unique}"), "en")).Items);
        await _k.RejectAsync(admin, id, "en", "TRANSLATION");
        Assert.Empty((await _k.SearchAsync(K($"hidden {unique}"), "en")).Items);
        Assert.Contains(other.Heritage.Id, (await _k.SearchAsync(K(other.Tag), "en")).Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Search_treats_wildcard_characters_in_the_keyword_as_plain_text()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin);
        var id = seed.Heritage.Id;
        await _k.GenerateAsync(admin, id);
        await factory.WithDbAsync(async db =>
        {
            await db.HeritageTranslations.Where(t => t.HeritageId == id && t.LanguageCode == LanguageCode.ja)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Name, $"100%_[金閣] {seed.Tag}"));
            return 0;
        });

        Assert.Equal([id], (await _k.SearchAsync(K($"100%_[金閣] {seed.Tag}"), "ja")).Items.Select(i => i.Id));
        Assert.Equal([id], (await _k.SearchAsync(K("[金閣]"), "ja")).Items.Select(i => i.Id));
        Assert.Contains(id, (await _k.SearchAsync(K("100%"), "ja")).Items.Select(i => i.Id));
        // Dấu % hay _ đứng một mình không khớp mọi thứ.
        Assert.DoesNotContain(id, (await _k.SearchAsync(K("zq%"), "ja")).Items.Select(i => i.Id));
        Assert.DoesNotContain(id, (await _k.SearchAsync(K("z_q"), "ja")).Items.Select(i => i.Id));
        Assert.DoesNotContain(id, (await _k.SearchAsync(K("[a-z]q"), "ja")).Items.Select(i => i.Id));
    }

    // ---- Tóm tắt ----

    [Fact]
    public async Task A_summary_appears_only_after_the_vietnamese_summary_is_approved_and_keeps_the_review_rules_per_language()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin);
        var id = seed.Heritage.Id;
        var slug = seed.Heritage.Slug!;
        var url = $"{PublicRoot}/heritages/{slug}/summary";

        // Chưa tóm tắt: di sản không có tóm tắt ở ngôn ngữ nào, báo rõ để giao diện không hiện cổng quảng cáo.
        Assert.False((await _k.DetailAsync(slug)).HasSummary);
        var none = await Fail(Http.GetAsync<object>(url), HttpStatusCode.NotFound, MessageCodes.SummaryUnavailable);
        Assert.Equal(TranslationMessages.NoSummary.Text, none.Message);

        // Có bản chờ duyệt vẫn chưa hiện, kể cả bản dịch máy (dịch từ bản tiếng Việt chưa duyệt).
        await _k.GenerateAsync(admin, id);
        Assert.False((await _k.DetailAsync(slug)).HasSummary);
        foreach (var language in new[] { "vi", "en", "ko", "zh-Hans", "ja" })
            await Fail(Http.GetAsync<object>($"{url}?lang={language}"), HttpStatusCode.NotFound, MessageCodes.SummaryUnavailable);

        // Duyệt tóm tắt tiếng Việt: tiếng Việt hiện; Hàn, Trung, Nhật hiện kèm nhãn; tiếng Anh chờ duyệt nên lùi về tiếng Việt.
        await _k.ApproveAsync(admin, id, "vi", "SUMMARY");
        Assert.True((await _k.DetailAsync(slug)).HasSummary);
        var vi = await _k.SummaryAsync(slug);
        Assert.Equal(("vi", false, false), (vi.Localization.Language, vi.Localization.IsFallback, vi.Localization.IsMachineTranslated));
        Assert.Equal((seed.Heritage.Name, seed.Heritage.Slug, id), (vi.Name, vi.Slug, vi.HeritageId));
        Assert.True(vi.ShortText.Length < vi.DetailedText.Length);
        Assert.StartsWith($"Hoàng thành {seed.Tag}.", vi.ShortText);
        foreach (var language in new[] { "ko", "zh-Hans", "ja" })
        {
            var machine = await _k.SummaryAsync(slug, language);
            Assert.Equal((language, false, true), (machine.Localization.Language, machine.Localization.IsFallback, machine.Localization.IsMachineTranslated));
            Assert.StartsWith($"[{language}] ", machine.ShortText);
            Assert.StartsWith($"[{language}] ", machine.DetailedText);
            Assert.Equal($"[{language}] {seed.Heritage.Name}", machine.Name);
        }
        var pendingEnglish = await _k.SummaryAsync(slug, "en");
        Assert.Equal(("en", "vi", true, false), (pendingEnglish.Localization.RequestedLanguage, pendingEnglish.Localization.Language, pendingEnglish.Localization.IsFallback, pendingEnglish.Localization.IsMachineTranslated));
        Assert.Equal(vi.ShortText, pendingEnglish.ShortText);               // bản lùi là tóm tắt tiếng Việt, nghe bằng giọng tiếng Việt
        Assert.Equal(seed.Heritage.Name, pendingEnglish.Name);

        // Duyệt tóm tắt tiếng Anh (bản dịch nội dung vẫn chờ): tóm tắt tiếng Anh hiện, tên di sản vẫn tiếng Việt.
        await _k.ApproveAsync(admin, id, "en", "SUMMARY");
        var english = await _k.SummaryAsync(slug, "en");
        Assert.Equal(("en", false, false), (english.Localization.Language, english.Localization.IsFallback, english.Localization.IsMachineTranslated));
        Assert.StartsWith("[en] ", english.ShortText);
        Assert.Equal(seed.Heritage.Name, english.Name);
        await _k.ApproveAsync(admin, id, "en", "TRANSLATION");
        Assert.Equal($"[en] {seed.Heritage.Name}", (await _k.SummaryAsync(slug, "en")).Name);

        // Từ chối tóm tắt tiếng Việt: di sản lại không có tóm tắt ở ngôn ngữ nào.
        await _k.RejectAsync(admin, id, "vi", "SUMMARY");
        Assert.False((await _k.DetailAsync(slug)).HasSummary);
        await Fail(Http.GetAsync<object>($"{url}?lang=en"), HttpStatusCode.NotFound, MessageCodes.SummaryUnavailable);
        await Fail(Http.GetAsync<object>($"{url}?lang=ko"), HttpStatusCode.NotFound, MessageCodes.SummaryUnavailable);
    }

    [Fact]
    public async Task Only_published_heritages_have_a_public_summary_and_the_slug_must_exist()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin);
        var id = seed.Heritage.Id;
        await _k.GenerateAsync(admin, id);
        await _k.ApproveAsync(admin, id, "vi", "SUMMARY");
        var url = $"{PublicRoot}/heritages/{seed.Heritage.Slug}/summary";
        await Expect(Http.GetAsync<PublicSummaryDto>(url));

        await Fail(Http.GetAsync<object>($"{PublicRoot}/heritages/khong-co-di-san-nay/summary"), HttpStatusCode.NotFound, "BLOCKED");
        await Fail(Http.GetAsync<object>($"{PublicRoot}/heritages/%20/summary"), HttpStatusCode.NotFound, "BLOCKED");
        await Expect(Http.PostAsync<HeritageDetailDto>($"{AdminRoot}/heritages/{id}/archive", null, admin));
        await Fail(Http.GetAsync<object>(url), HttpStatusCode.NotFound, "BLOCKED");
        await Expect(Http.PostAsync<HeritageDetailDto>($"{AdminRoot}/heritages/{id}/restore", null, admin));
        await Expect(Http.GetAsync<PublicSummaryDto>(url));

        // Một bản nháp có tóm tắt cũng không lộ.
        var draft = await _k.SeedAsync(admin, publish: false);
        await _k.GenerateAsync(admin, draft.Heritage.Id);
        await _k.ApproveAsync(admin, draft.Heritage.Id, "vi", "SUMMARY");
        await Fail(Http.GetAsync<object>($"{PublicRoot}/heritages/{draft.Heritage.Slug}/summary"), HttpStatusCode.NotFound, "BLOCKED");
    }

    [Fact]
    public async Task Public_pages_never_call_the_ai_service_when_a_visitor_looks()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin);
        await _k.GenerateAsync(admin, seed.Heritage.Id);
        await _k.ApproveAllAsync(admin, seed.Heritage.Id);
        factory.HeritageAi.Reset();
        factory.HeritageAi.SummarizeBehavior = (_, _, _) => throw new InvalidOperationException("Không được gọi AI khi xem.");
        factory.HeritageAi.TranslateBehavior = (_, _, _) => throw new InvalidOperationException("Không được gọi AI khi xem.");
        var slug = seed.Heritage.Slug!;

        foreach (var language in new[] { "vi", "en", "ko", "zh-Hans", "ja" })
        {
            await _k.SummaryAsync(slug, language);
            await _k.DetailAsync(slug, language);
            await _k.TimelineAsync(slug, language);
            await _k.SearchAsync(K(seed.Tag), language);
        }
        Assert.Equal((0, 0), (factory.HeritageAi.SummarizeCalls, factory.HeritageAi.TranslateCalls));
    }

    // ---- Xem trước bản nháp ----

    [Fact]
    public async Task The_draft_preview_shows_the_visible_translations_too()
    {
        var admin = await _k.AdminAsync();
        var seed = await _k.SeedAsync(admin, publish: false);
        await _k.GenerateAsync(admin, seed.Heritage.Id);
        var link = await Expect(Http.PostAsync<HeritagePreviewLinkDto>($"{AdminRoot}/heritages/{seed.Heritage.Id}/preview", null, admin));
        var url = $"/api/v1/preview/heritages/{link.Code}";

        var vi = await _k.PublicAsync<HeritagePreviewDto>(url, token: admin);
        Assert.Equal(seed.Heritage.Name, vi.Heritage.Name);
        var ko = await _k.PublicAsync<HeritagePreviewDto>(url, "ko", token: admin);
        Assert.Equal(($"[ko] {seed.Heritage.Name}", true), (ko.Heritage.Name, ko.Heritage.Localization!.IsMachineTranslated));
        var en = await _k.PublicAsync<HeritagePreviewDto>(url, "en", token: admin);
        Assert.Equal((seed.Heritage.Name, true), (en.Heritage.Name, en.Heritage.Localization!.IsFallback));      // tiếng Anh chờ duyệt
        var timeline = await _k.PublicAsync<PublicTimelineDto>($"{url}/timeline", "ja", token: admin);
        Assert.Equal($"[ja] {seed.Phase.Name}", Assert.Single(timeline.Phases).Name);
    }
}
