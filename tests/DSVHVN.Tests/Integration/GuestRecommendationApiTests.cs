using System.Text.Json;
using DSVHVN.Application.Guests;
using DSVHVN.Application.Heritages;
using DSVHVN.Application.Recommendations;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using static DSVHVN.Tests.Integration.GuestTestKit;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// "Gợi ý cho bạn" của khách: di sản giống những gì khách đã xem, đọc tóm tắt, nghe, xem 3D (cùng loại, cùng tỉnh, cùng vùng), tối đa 6, bỏ di sản đã xem từ 3 lần và chính di sản đang làm sở thích,
/// chỉ xét 20 lượt gần nhất trong 90 ngày, chưa có lịch sử thì hiện di sản nổi bật, Dịch vụ AI lỗi thì máy chủ tự chọn, không gọi mô hình ngôn ngữ và không gửi dữ liệu của thiết bị sang Dịch vụ AI.
/// Mỗi test dùng một CSDL riêng để bảng xếp hạng không lẫn di sản của test khác.
/// </summary>
public sealed class GuestRecommendationApiTests
{
    /// <summary>Di sản dựng sẵn: hai di sản Hà Nội (Bắc, vật thể), một ở Huế (Trung, vật thể), một phi vật thể ở Huế, một thiên nhiên ở TP. Hồ Chí Minh (Nam) và một di sản UNESCO ở Đà Nẵng (Trung, vật thể).</summary>
    private sealed record Scene(
        GuestTestKit Kit, ApiFactory Factory, string Admin, HeritageDetailDto Hn1, HeritageDetailDto Hn2, HeritageDetailDto Hue1, HeritageDetailDto Hue2,
        HeritageDetailDto Sg1, HeritageDetailDto Unesco);

    private static async Task<Scene> SceneAsync(ApiFactory factory)
    {
        var kit = new GuestTestKit(factory);
        var admin = await kit.AdminAsync();
        return new Scene(
            kit, factory, admin,
            await kit.HeritageAsync(admin, "Hoàng thành", "TANGIBLE", "01", "NATIONAL"),
            await kit.HeritageAsync(admin, "Văn Miếu", "TANGIBLE", "01", "NATIONAL"),
            await kit.HeritageAsync(admin, "Lăng Tự Đức", "TANGIBLE", "46", "PROVINCIAL"),
            await kit.HeritageAsync(admin, "Nhã nhạc", "INTANGIBLE", "46", "PROVINCIAL"),
            await kit.HeritageAsync(admin, "Rừng ngập mặn", "NATURAL", "79", "PROVINCIAL"),
            await kit.HeritageAsync(admin, "Phố cổ Hội An", "TANGIBLE", "48", "UNESCO"));
    }

    private static List<string> Slugs(GuestRecommendationsDto result) => result.Items.Select(i => i.Heritage.Slug).ToList();

    // ---- Chưa có lịch sử ----

    [Fact]
    public async Task A_guest_without_history_sees_the_featured_heritage_with_an_invitation_and_no_ai_call()
    {
        using var factory = new ApiFactory();
        var s = await SceneAsync(factory);
        var withSearchOnly = NewKey();
        await s.Kit.SeedAsync(withSearchOnly, new Seed(GuestActivityType.SEARCH, null, Minutes(5), "hoàng thành"));   // từ khóa tìm không thành sở thích

        foreach (var key in new string?[] { null, NewKey(), withSearchOnly })
        {
            var result = await s.Kit.RecommendationsAsync(key);
            Assert.Equal((false, false, GuestMessages.NoHistoryYet), (result.IsPersonalized, result.UsedFallback, result.Message));
            Assert.Equal([s.Unesco.Slug], Slugs(result));      // chỉ di sản UNESCO hoặc quốc gia đặc biệt, như trang chủ
            Assert.All(result.Items, i => Assert.Empty(i.Reasons));
            Assert.All(result.Items, i => Assert.Null(i.BecauseOfHeritageId));
        }
        Assert.Equal(0, factory.RecommendationAi.Calls);
        Assert.Equal(1, await s.Kit.DeviceCountAsync());        // chỉ thiết bị seed; đọc gợi ý không tạo thiết bị
    }

    [Fact]
    public async Task Deleting_the_whole_history_brings_the_recommendations_back_to_the_default()
    {
        using var factory = new ApiFactory();
        var s = await SceneAsync(factory);
        var key = NewKey();
        await s.Kit.ExpectAsync(s.Kit.GetAsync<object>($"{PublicRoot}/heritages/{s.Hn1.Slug}", key));
        Assert.True((await s.Kit.RecommendationsAsync(key)).IsPersonalized);

        await s.Kit.ExpectAsync(s.Kit.SendAsync<GuestDeletedDto>(HttpMethod.Delete, $"{Root}/history", key));
        var after = await s.Kit.RecommendationsAsync(key);
        Assert.Equal((false, GuestMessages.NoHistoryYet), (after.IsPersonalized, after.Message));
        Assert.Equal([s.Unesco.Slug], Slugs(after));
    }

    [Fact]
    public async Task History_older_than_ninety_days_no_longer_shapes_the_recommendations()
    {
        using var factory = new ApiFactory();
        var s = await SceneAsync(factory);
        var key = NewKey();
        await s.Kit.SeedAsync(key, new Seed(GuestActivityType.VIEW, s.Hn1.Id, Days(91)));

        var result = await s.Kit.RecommendationsAsync(key);
        Assert.False(result.IsPersonalized);
        Assert.Equal(0, factory.RecommendationAi.Calls);
    }

    // ---- Theo lịch sử ----

    [Fact]
    public async Task Recommendations_follow_what_the_guest_viewed_and_never_repeat_the_viewed_heritage_nor_exceed_six()
    {
        using var factory = new ApiFactory();
        var s = await SceneAsync(factory);
        // Thêm năm di sản giống hệt Hoàng thành (vật thể, Hà Nội): với Văn Miếu là sáu ứng viên cùng hồ sơ, cộng các di sản khác là hơn sáu ứng viên.
        var twins = new List<HeritageDetailDto>();
        for (var i = 0; i < 5; i++) twins.Add(await s.Kit.HeritageAsync(s.Admin, $"Di tích song sinh {i}", "TANGIBLE", "01", "NATIONAL"));
        var key = NewKey();

        await s.Kit.ExpectAsync(s.Kit.GetAsync<object>($"{PublicRoot}/heritages/{s.Hn1.Slug}", key));
        var result = await s.Kit.RecommendationsAsync(key);

        Assert.True(result.IsPersonalized);
        Assert.False(result.UsedFallback);
        Assert.Null(result.Message);
        Assert.Equal(GuestRules.ResultLimit, result.Items.Count);
        Assert.DoesNotContain(s.Hn1.Slug, Slugs(result));                                   // không gợi ý lại chính di sản đang làm sở thích
        var expectedSameProfile = twins.Append(s.Hn2).Select(h => h.Slug).ToHashSet();
        Assert.True(Slugs(result).All(expectedSameProfile.Contains));                       // cùng loại, cùng tỉnh, cùng cấp đứng trước mọi di sản khác
        Assert.All(result.Items, i =>
        {
            Assert.Contains(SimilarityFactor.SAME_TYPE, i.Reasons);
            Assert.Contains(SimilarityFactor.SAME_PROVINCE, i.Reasons);
            Assert.Equal(s.Hn1.Id, i.BecauseOfHeritageId);
            Assert.Equal(s.Hn1.Name, i.BecauseOfName);
        });
        // Mỗi thẻ là thẻ di sản công khai đầy đủ.
        Assert.All(result.Items, i => Assert.False(string.IsNullOrEmpty(i.Heritage.Name)));
        Assert.Equal(1, factory.RecommendationAi.Calls);
    }

    [Fact]
    public async Task Candidates_are_ordered_by_similarity_and_a_different_region_and_type_comes_last()
    {
        using var factory = new ApiFactory();
        var s = await SceneAsync(factory);
        var key = NewKey();
        await s.Kit.SeedAsync(key, new Seed(GuestActivityType.VIEW, s.Hn1.Id, Minutes(5)));

        var slugs = Slugs(await s.Kit.RecommendationsAsync(key));

        Assert.Equal(s.Hn2.Slug, slugs[0]);                                                     // cùng loại, cùng tỉnh, cùng cấp
        Assert.True(slugs.IndexOf(s.Hue1.Slug!) < slugs.IndexOf(s.Hue2.Slug!), "cùng loại đứng trước khác loại");
        if (slugs.Contains(s.Sg1.Slug!)) Assert.True(slugs.IndexOf(s.Hue1.Slug!) < slugs.IndexOf(s.Sg1.Slug!));
    }

    [Fact]
    public async Task Summary_listen_and_3d_weigh_double_and_only_the_latest_twenty_activities_count()
    {
        using var factory = new ApiFactory();
        var s = await SceneAsync(factory);
        var key = NewKey();
        await s.Kit.SeedAsync(key,
            new Seed(GuestActivityType.VIEW, s.Hue1.Id, Days(40)),               // cũ hơn 20 lượt gần nhất: không còn là sở thích
            new Seed(GuestActivityType.VIEW, s.Hn1.Id, Minutes(60)),
            new Seed(GuestActivityType.READ_SUMMARY, s.Sg1.Id, Minutes(50)),
            new Seed(GuestActivityType.LISTEN, s.Sg1.Id, Minutes(40)),
            new Seed(GuestActivityType.VIEW_3D, s.Hue2.Id, Minutes(30)),
            new Seed(GuestActivityType.SEARCH, null, Minutes(20), "không phải sở thích"));
        await s.Kit.SeedAsync(key, Enumerable.Range(1, 16).Select(i => new Seed(GuestActivityType.VIEW, s.Hn2.Id, Minutes(i))).ToArray());   // 16 lượt gần nhất

        await s.Kit.RecommendationsAsync(key);

        var references = factory.RecommendationAi.LastRequest!.References.ToDictionary(r => r.Heritage.Id, r => r.Weight);
        // 20 lượt gần nhất gắn với di sản: 16 lượt Văn Miếu, rồi 4 lượt trước đó (xem 3D Nhã nhạc, nghe và đọc tóm tắt Rừng ngập mặn, xem Hoàng thành); lượt xem Lăng Tự Đức quá cũ.
        Assert.Equal(new Dictionary<long, double> { [s.Hn2.Id] = 16d, [s.Hue2.Id] = 2d, [s.Sg1.Id] = 4d, [s.Hn1.Id] = 1d }, references);
        Assert.DoesNotContain(s.Hue1.Id, references.Keys);
    }

    [Fact]
    public async Task A_heritage_viewed_three_times_is_no_longer_recommended_but_one_viewed_twice_still_is()
    {
        using var factory = new ApiFactory();
        var s = await SceneAsync(factory);
        var twice = await s.Kit.HeritageAsync(s.Admin, "Di tích xem hai lần", "TANGIBLE", "01", "NATIONAL");
        var key = NewKey();
        // 20 lượt gần nhất đều là Hoàng thành, nên hai di sản kia không phải sở thích mà chỉ có trong lịch sử 90 ngày.
        await s.Kit.SeedAsync(key, Enumerable.Range(1, 20).Select(i => new Seed(GuestActivityType.VIEW, s.Hn1.Id, Minutes(i))).ToArray());
        await s.Kit.SeedAsync(key,
            new Seed(GuestActivityType.VIEW, s.Hn2.Id, Days(30)), new Seed(GuestActivityType.VIEW, s.Hn2.Id, Days(31)), new Seed(GuestActivityType.VIEW, s.Hn2.Id, Days(32)),
            new Seed(GuestActivityType.VIEW, twice.Id, Days(33)), new Seed(GuestActivityType.VIEW, twice.Id, Days(34)));

        var slugs = Slugs(await s.Kit.RecommendationsAsync(key));

        Assert.DoesNotContain(s.Hn2.Slug, slugs);        // ba lượt xem chi tiết
        Assert.Equal(twice.Slug, slugs[0]);              // hai lượt: vẫn được gợi ý, giống nhất vì cùng hồ sơ
        Assert.DoesNotContain(s.Hn1.Slug, slugs);
    }

    [Fact]
    public async Task Archived_or_draft_heritage_is_never_recommended_but_the_history_still_counts()
    {
        using var factory = new ApiFactory();
        var s = await SceneAsync(factory);
        var key = NewKey();
        var draft = await s.Kit.Discovery.DraftAsync(s.Admin, new DiscoveryTestKit.Spec("Nháp giống " + DiscoveryTestKit.Unique(), "TANGIBLE", "NATIONAL", "01"));
        await s.Kit.SeedAsync(key, new Seed(GuestActivityType.VIEW, s.Hn1.Id, Minutes(5)));
        await s.Kit.ArchiveAsync(s.Admin, s.Hn2.Id);

        var slugs = Slugs(await s.Kit.RecommendationsAsync(key));

        Assert.DoesNotContain(s.Hn2.Slug, slugs);
        Assert.DoesNotContain(draft.Slug, slugs);
        Assert.NotEmpty(slugs);
    }

    [Fact]
    public async Task Names_and_reasons_follow_the_language_with_a_fallback_flag_until_the_translation_is_approved()
    {
        using var factory = new ApiFactory();
        var s = await SceneAsync(factory);
        var key = NewKey();
        await s.Kit.SeedAsync(key, new Seed(GuestActivityType.VIEW, s.Hn1.Id, Minutes(5)));

        var beforeKo = await s.Kit.RecommendationsAsync(key, "lang=ko");
        Assert.All(beforeKo.Items, i => Assert.True(i.Heritage.IsFallback));      // chưa dịch: hiện tiếng Việt kèm cờ lùi
        Assert.Equal(s.Hn1.Name, beforeKo.Items[0].BecauseOfName);

        var translations = new TranslationTestKit(factory);
        await translations.GenerateAsync(s.Admin, s.Hn1.Id);
        await translations.GenerateAsync(s.Admin, s.Hn2.Id);
        await translations.ApproveAllAsync(s.Admin, s.Hn1.Id);
        await translations.ApproveAllAsync(s.Admin, s.Hn2.Id);

        var english = await s.Kit.RecommendationsAsync(key, "lang=en");
        var detail = await translations.DetailAsync(s.Hn1.Slug!, "en");
        var top = english.Items[0];
        Assert.Equal(s.Hn2.Slug, top.Heritage.Slug);
        Assert.False(top.Heritage.IsFallback);
        Assert.Equal(detail.Name, english.Items[0].BecauseOfName);               // tên di sản làm nguyên nhân cũng theo ngôn ngữ
        Assert.NotEqual(s.Hn1.Name, english.Items[0].BecauseOfName);
        Assert.Equal(new[] { SimilarityFactor.SAME_TYPE, SimilarityFactor.SAME_PROVINCE }, top.Reasons.Take(2).ToArray());
    }

    // ---- Dịch vụ AI ----

    [Fact]
    public async Task When_the_ai_service_fails_the_server_picks_heritage_of_the_same_type_or_region_as_the_latest_view()
    {
        using var factory = new ApiFactory();
        var s = await SceneAsync(factory);
        factory.RecommendationAi.Behavior = (_, _) => throw new HttpRequestException("Dịch vụ AI ngừng");
        var key = NewKey();
        await s.Kit.SeedAsync(key, new Seed(GuestActivityType.VIEW, s.Hn1.Id, Minutes(30)), new Seed(GuestActivityType.VIEW, s.Hue1.Id, Minutes(5)));

        var result = await s.Kit.RecommendationsAsync(key);

        Assert.True(result.UsedFallback);
        Assert.True(result.IsPersonalized);
        // Xem gần nhất là Lăng Tự Đức (vật thể, Trung): Phố cổ Hội An cùng loại và vùng (3), Văn Miếu (cùng loại, 2), Nhã nhạc (cùng vùng, 1); Rừng ngập mặn không có điểm chung.
        // Hoàng thành cũng là sở thích nên không được gợi ý lại.
        Assert.Equal([s.Unesco.Slug, s.Hn2.Slug, s.Hue2.Slug], Slugs(result));
        Assert.Equal(
            new[]
            {
                new[] { SimilarityFactor.SAME_TYPE, SimilarityFactor.SAME_REGION },
                new[] { SimilarityFactor.SAME_TYPE },
                new[] { SimilarityFactor.SAME_REGION },
            },
            result.Items.Select(i => i.Reasons.ToArray()).ToArray());
        Assert.All(result.Items, i => Assert.Equal(s.Hue1.Id, i.BecauseOfHeritageId));
    }

    [Fact]
    public async Task The_ai_service_gets_only_heritage_data_and_weights_and_no_language_model_runs()
    {
        using var factory = new ApiFactory();
        var s = await SceneAsync(factory);
        var key = NewKey();
        var secretQuery = "tu-khoa-rieng-tu-" + DiscoveryTestKit.Unique();
        await s.Kit.SeedAsync(key, new Seed(GuestActivityType.VIEW, s.Hn1.Id, Minutes(9)), new Seed(GuestActivityType.SEARCH, null, Minutes(8), secretQuery));
        await s.Kit.ExpectAsync(s.Kit.GetAsync<object>($"{Root}/recommendations?lang=ko", key, platform: "ANDROID"));

        var request = factory.RecommendationAi.LastRequest!;
        Assert.Null(request.StudentId);
        Assert.Null(request.Level);
        Assert.Null(request.TargetDifficulty);
        var wire = JsonSerializer.Serialize(request, HttpTestExtensions.Json);
        foreach (var forbidden in new[] { key, secretQuery, "ANDROID", "\"ko\"" })
            Assert.DoesNotContain(forbidden, wire, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([s.Hn1.Id], request.References.Select(r => r.Heritage.Id));
        Assert.DoesNotContain(request.Candidates, c => c.Id == s.Hn1.Id);

        // Không gọi mô hình ngôn ngữ, không ghi lượt AI, không lưu kết quả.
        Assert.Equal(0, factory.Ai.Calls + factory.QuestionAi.Calls + factory.HeritageAi.SummarizeCalls + factory.HeritageAi.TranslateCalls);
        Assert.Equal(0, await factory.WithDbAsync(db => db.AiGenerations.CountAsync()));
    }

    [Fact]
    public async Task An_ai_answer_with_no_overlap_falls_back_to_the_featured_heritage_without_an_invitation()
    {
        using var factory = new ApiFactory();
        var s = await SceneAsync(factory);
        factory.RecommendationAi.Behavior = (_, _) => Task.FromResult<IReadOnlyList<RelatedHeritage>>([]);
        var key = NewKey();
        await s.Kit.SeedAsync(key, new Seed(GuestActivityType.VIEW, s.Hn1.Id, Minutes(5)));

        var result = await s.Kit.RecommendationsAsync(key);

        Assert.False(result.IsPersonalized);
        Assert.False(result.UsedFallback);
        Assert.Null(result.Message);         // khách đã có lịch sử nên không mời "xem thử vài di sản"
        Assert.Equal([s.Unesco.Slug], Slugs(result));
    }
}
