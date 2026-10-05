using System.Net;
using DSVHVN.Application.Questions;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Ngân hàng câu hỏi 8 dạng qua Api thật: tạo, đọc, sửa, xóa từng dạng; kiểm hợp lệ trả lỗi theo trường; quyền xem và sửa theo người tạo, theo quiz và theo trường;
/// chỉ đọc khi câu nằm trong quiz, nhân bản; tìm và lọc; xem trước, chấm thử, kiểm không lưu.
/// </summary>
public sealed class QuestionBankApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly QuestionTestKit _k = new(factory);
    private HttpClient Http => _k.Http;
    private const string T = QuestionTestKit.Teacher;

    private async Task<(QuestionTestKit.LessonSetup Setup, Dictionary<QuestionType, SaveQuestionRequest> Samples)> SetupAsync(int level = 2)
    {
        var setup = await _k.LessonAsync(level);
        return (setup, QuestionTestKit.Samples(setup.Heritage.Id, setup.Images));
    }

    // ---- Tạo, đọc, sửa, xóa từng dạng ----

    [Fact]
    public async Task Every_type_can_be_created_read_updated_and_deleted_and_is_approved_at_once()
    {
        var (setup, samples) = await SetupAsync();
        var token = setup.Token;

        foreach (var (type, request) in samples)
        {
            var created = await _k.CreateAsync(token, request);
            var info = QuestionTypes.Of(type);

            Assert.Equal((type, QuestionSource.MANUAL, ReviewStatus.APPROVED), (created.QuestionType, created.Source, created.ReviewStatus));
            Assert.Equal((setup.School.Teacher.Id, setup.School.Teacher.Id), (created.CreatedBy, created.ReviewedBy));
            Assert.NotNull(created.ReviewedAt);
            Assert.Null(created.AiGenerationId);
            Assert.Equal((info.MinLevel, info.MaxLevel), (created.LevelMin, created.LevelMax));
            Assert.Equal(Enumerable.Range(info.MinLevel, info.MaxLevel - info.MinLevel + 1), created.Levels);
            Assert.Equal((setup.Heritage.Id, request.Content, request.DifficultyLevel), (created.HeritageId, created.Content, created.DifficultyLevel));
            Assert.Equal(QuestionTypes.NameOf(type), created.TypeName);
            Assert.True(created.PartsTotal >= 1);
            Assert.False(created.Access.IsReadOnly);
            Assert.True(created.Access is { CanEdit: true, CanDelete: true, CanDuplicate: true, CanApprove: false, CanReject: false });

            var read = await _k.GetAsync(token, created.Id);
            Assert.Equal(created.Id, read.Id);
            Assert.Equal(request.Explanation, read.Explanation);

            var edited = await DiscoveryTestKit.Expect(Http.PutAsync<QuestionDetailDto>($"{T}/{created.Id}", request with { Explanation = "Giải thích đã sửa." }, token));
            Assert.Equal(("Giải thích đã sửa.", ReviewStatus.APPROVED, created.ReviewedAt), (edited.Explanation, edited.ReviewStatus, edited.ReviewedAt));

            var (del, delBody) = await Http.DeleteAsync<object>($"{T}/{created.Id}", token);
            Assert.Equal(HttpStatusCode.OK, del.StatusCode);
            Assert.True(delBody.Success);
            var (gone, _) = await Http.GetAsync<QuestionDetailDto>($"{T}/{created.Id}", token);
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }
    }

    [Fact]
    public async Task The_answer_structure_is_stored_and_read_back_as_it_was_sent_for_every_type()
    {
        var (setup, samples) = await SetupAsync();
        var token = setup.Token;

        var single = await _k.CreateAsync(token, samples[QuestionType.SINGLE_CHOICE]);
        Assert.Equal(["Năm 1999", "Năm 2010", "Năm 2003", "Năm 2015"], single.Options.Select(o => o.Content));
        Assert.Equal([false, true, false, false], single.Options.Select(o => o.IsCorrect));

        var trueFalse = await _k.CreateAsync(token, samples[QuestionType.TRUE_FALSE]);
        Assert.Equal((true, setup.Images[0]), (trueFalse.AnswerIsTrue, trueFalse.ImageUrl));
        Assert.Equal(["Đúng", "Sai"], trueFalse.Options.Select(o => o.Content));
        Assert.Contains(trueFalse.Images, i => i.Url == setup.Images[0] && !string.IsNullOrEmpty(i.Credit));      // ảnh luôn kèm ghi công

        var image = await _k.CreateAsync(token, samples[QuestionType.IMAGE_CHOICE]);
        Assert.Equal([setup.Images[0], setup.Images[1], setup.Images[2]], image.Options.Select(o => o.ImageUrl));
        Assert.Equal([false, true, false], image.Options.Select(o => o.IsCorrect));
        Assert.Equal(3, image.Images.Count);

        var matching = await _k.CreateAsync(token, samples[QuestionType.MATCHING]);
        Assert.Equal(["Điện Kính Thiên", "Cổng Đoan Môn", "Khu khảo cổ"], matching.Pairs.Select(p => p.Right));
        Assert.Equal(3, matching.PartsTotal);

        var ordering = await _k.CreateAsync(token, samples[QuestionType.ORDERING]);
        Assert.Equal(samples[QuestionType.ORDERING].Steps, ordering.Steps);
        Assert.Equal(4, ordering.PartsTotal);

        var fill = await _k.CreateAsync(token, samples[QuestionType.FILL_BLANK]);
        Assert.Equal(["Lý Thái Tổ", "Lý Công Uẩn"], fill.Blanks.Single().Accepted);
        Assert.Equal(["Lê Lợi", "Trần Hưng Đạo"], fill.Distractors);
        Assert.Equal(["Lý Thái Tổ", "Lê Lợi", "Trần Hưng Đạo"], fill.WordBank);

        var statements = await _k.CreateAsync(token, samples[QuestionType.MULTI_TRUE_FALSE]);
        Assert.Equal([true, false, true], statements.Statements.Select(s => s.IsTrue));
        Assert.Equal(3, statements.PartsTotal);

        var reading = await _k.CreateAsync(token, samples[QuestionType.SOURCE_READING]);
        Assert.Equal((samples[QuestionType.SOURCE_READING].Passage, samples[QuestionType.SOURCE_READING].PassageSource), (reading.Passage, reading.PassageSource));
        Assert.Equal(4, reading.Options.Count);
    }

    [Fact]
    public async Task The_cap_phu_hop_defaults_to_the_levels_of_the_type_and_can_be_narrowed()
    {
        var (setup, samples) = await SetupAsync();
        var token = setup.Token;

        var ordering = await _k.CreateAsync(token, samples[QuestionType.ORDERING]);
        Assert.Equal((1, 2), (ordering.LevelMin, ordering.LevelMax));
        var onlyLevelTwo = await _k.CreateAsync(token, samples[QuestionType.ORDERING] with { LevelMin = 2, LevelMax = 2 });
        Assert.Equal([2], onlyLevelTwo.Levels);

        // Sửa để thu hẹp cấp.
        var narrowed = await DiscoveryTestKit.Expect(Http.PutAsync<QuestionDetailDto>($"{T}/{ordering.Id}", samples[QuestionType.ORDERING] with { LevelMin = 1, LevelMax = 1 }, token));
        Assert.Equal([1], narrowed.Levels);
    }

    [Fact]
    public async Task The_type_table_endpoint_lists_the_eight_types_and_the_types_of_each_level()
    {
        var (setup, _) = await SetupAsync();
        var table = await DiscoveryTestKit.Expect(Http.GetAsync<QuestionTypesDto>($"{T}/types", setup.Token));

        Assert.Equal(8, table.Types.Count);
        Assert.Equal(4, table.ByLevel[1].Count);
        Assert.Equal(4, table.ByLevel[2].Count);
        Assert.Equal(3, table.ByLevel[3].Count);
        Assert.Equal((2, 3), table.Types.Where(t => t.Type == QuestionType.SINGLE_CHOICE).Select(t => (t.MinLevel, t.MaxLevel)).Single());
        Assert.True(table.Types.Single(t => t.Type == QuestionType.MATCHING).MultiPart);
    }

    // ---- Kiểm hợp lệ: 400 theo trường ----

    [Fact]
    public async Task An_invalid_question_gets_a_400_with_the_errors_by_field_and_nothing_is_saved()
    {
        var (setup, samples) = await SetupAsync();
        var token = setup.Token;
        var before = (await _k.ListAsync(token, "scope=MINE")).TotalItems;

        var (res, body) = await Http.PostAsync<object>(T, samples[QuestionType.SINGLE_CHOICE] with
        {
            Content = "Ngắn",
            Options = [new("A", null, true), new("A", null, true), new("C", null, false), new("D", null, false)],
        }, token);
        Assert.Equal((HttpStatusCode.BadRequest, false), (res.StatusCode, body.Success));
        Assert.Contains("content", body.Errors!.Keys);
        Assert.Contains("options[1].content", body.Errors.Keys);

        var (r2, b2) = await Http.PostAsync<object>(T, samples[QuestionType.FILL_BLANK] with { Content = "Câu không có chỗ trống." }, token);
        Assert.Equal(HttpStatusCode.BadRequest, r2.StatusCode);
        Assert.Contains("content", b2.Errors!.Keys);

        var (r3, b3) = await Http.PostAsync<object>(T, samples[QuestionType.IMAGE_CHOICE] with
        {
            Options = [new("Một", "https://nguoi-la.example/anh.jpg", true), new("Hai", setup.Images[1], false), new("Ba", setup.Images[2], false)],
        }, token);
        Assert.Equal(HttpStatusCode.BadRequest, r3.StatusCode);
        Assert.Contains("options[0].imageUrl", b3.Errors!.Keys);                                  // ảnh phải thuộc thư viện của di sản

        Assert.Equal(before, (await _k.ListAsync(token, "scope=MINE")).TotalItems);
    }

    [Fact]
    public async Task The_heritage_must_exist_and_be_published_and_images_must_belong_to_it()
    {
        var (setup, samples) = await SetupAsync();
        var other = await _k.HeritageAsync("Văn Miếu", extraImages: 3);
        var token = setup.Token;

        // Ảnh của di sản khác không dùng được.
        var (r1, b1) = await Http.PostAsync<object>(T, samples[QuestionType.TRUE_FALSE] with { ImageUrl = other.Images[0] }, token);
        Assert.Equal(HttpStatusCode.BadRequest, r1.StatusCode);
        Assert.Contains("imageUrl", b1.Errors!.Keys);

        var (r2, b2) = await Http.PostAsync<object>(T, samples[QuestionType.SINGLE_CHOICE] with { HeritageId = 99_999_999 }, token);
        Assert.Equal(HttpStatusCode.BadRequest, r2.StatusCode);
        Assert.Contains("heritageId", b2.Errors!.Keys);

        var (r3, b3) = await Http.PostAsync<object>(T, samples[QuestionType.SINGLE_CHOICE] with { HeritageId = null }, token);
        Assert.Equal(HttpStatusCode.BadRequest, r3.StatusCode);
        Assert.Contains("heritageId", b3.Errors!.Keys);

        // Di sản đã ẩn không gắn được cho câu mới.
        await _k.Lesson.ArchiveHeritageAsync(other.Heritage.Id);
        var (r4, b4) = await Http.PostAsync<object>(T, samples[QuestionType.SINGLE_CHOICE] with { HeritageId = other.Heritage.Id }, token);
        Assert.Equal(HttpStatusCode.BadRequest, r4.StatusCode);
        Assert.Contains("heritageId", b4.Errors!.Keys);
    }

    [Fact]
    public async Task Level_one_true_false_needs_a_picture_but_a_level_two_only_true_false_does_not()
    {
        var (setup, samples) = await SetupAsync();
        var noPicture = samples[QuestionType.TRUE_FALSE] with { ImageUrl = null };

        var (res, body) = await Http.PostAsync<object>(T, noPicture, setup.Token);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("imageUrl", body.Errors!.Keys);

        var levelTwo = await _k.CreateAsync(setup.Token, noPicture with { LevelMin = 2, LevelMax = 2 });
        Assert.Null(levelTwo.ImageUrl);
    }

    // ---- Quyền ----

    [Fact]
    public async Task Only_teachers_use_the_bank_and_everyone_must_be_signed_in()
    {
        var (setup, samples) = await SetupAsync();
        var created = await _k.CreateAsync(setup.Token, samples[QuestionType.SINGLE_CHOICE]);
        var student = await _k.Lesson.StudentTokenAsync(setup.Token, setup.ClassId);

        foreach (var (token, expected) in new (string?, HttpStatusCode)[]
                 {
                     (null, HttpStatusCode.Unauthorized), (setup.School.OrgAdminToken, HttpStatusCode.Forbidden),
                     (student, HttpStatusCode.Forbidden), (await _k.Lesson.Discovery.Api.AdminTokenAsync(), HttpStatusCode.Forbidden),
                 })
        {
            Assert.Equal(expected, (await Http.GetAsync<object>(T, token)).Response.StatusCode);
            Assert.Equal(expected, (await Http.GetAsync<object>($"{T}/{created.Id}", token)).Response.StatusCode);
            Assert.Equal(expected, (await Http.PostAsync<object>(T, samples[QuestionType.SINGLE_CHOICE], token)).Response.StatusCode);
            Assert.Equal(expected, (await Http.PostAsync<object>($"{T}/{created.Id}/duplicate", null, token)).Response.StatusCode);
            Assert.Equal(expected, (await Http.GetAsync<object>($"{T}/counts", token)).Response.StatusCode);
        }
    }

    [Fact]
    public async Task Approved_questions_are_shared_across_schools_with_the_creator_and_school_name_but_only_the_creator_can_change_them()
    {
        var (setup, samples) = await SetupAsync();
        var created = await _k.CreateAsync(setup.Token, samples[QuestionType.SINGLE_CHOICE]);
        var otherSchool = await _k.Lesson.Classroom.SchoolAsync();
        var other = otherSchool.TeacherToken;

        var seen = await _k.GetAsync(other, created.Id);
        Assert.Equal((setup.School.Teacher.FullName, true), (seen.CreatorName, seen.OrganizationName is not null));
        Assert.True(seen.Access is { IsReadOnly: true, Reason: QuestionReadOnlyReason.NOT_OWNER, CanEdit: false, CanDelete: false, CanDuplicate: true });
        Assert.Equal("Câu hỏi này chỉ xem được vì do người khác tạo. Bấm \"Nhân bản\" để tạo bản của bạn rồi sửa.", seen.Access.Message);

        var row = (await _k.ListAsync(other, $"keyword={Uri.EscapeDataString(created.Content!)}")).Items.Single(i => i.Id == created.Id);
        Assert.Equal((setup.School.Teacher.FullName, false, false), (row.CreatorName, row.IsMine, row.IsHidden));
        Assert.NotNull(row.OrganizationName);

        // Người khác không sửa, xóa, duyệt, loại được câu: 403.
        foreach (var call in new Func<Task<(HttpResponseMessage Response, Envelope<object> Body)>>[]
                 {
                     () => Http.PutAsync<object>($"{T}/{created.Id}", samples[QuestionType.SINGLE_CHOICE], other),
                     () => Http.DeleteAsync<object>($"{T}/{created.Id}", other),
                     () => Http.PostAsync<object>($"{T}/{created.Id}/approve", null, other),
                     () => Http.PostAsync<object>($"{T}/{created.Id}/reject", null, other),
                 })
        {
            var (res, body) = await call();
            Assert.Equal((HttpStatusCode.Forbidden, "FORBIDDEN"), (res.StatusCode, body.ErrorCode));
        }
        Assert.Equal(created.Content, (await _k.GetAsync(setup.Token, created.Id)).Content);
    }

    [Fact]
    public async Task Pending_and_rejected_questions_are_invisible_to_other_teachers_even_in_the_same_school()
    {
        var setup = await _k.LessonAsync(2);
        var (_, colleague) = await _k.Lesson.Classroom.AnotherTeacherAsync(setup.School);
        var batch = await _k.GenerateAsync(setup.Token, setup.LessonId, new { count = 2, types = new[] { "SINGLE_CHOICE" } });
        var pending = batch.Questions[0];
        var rejected = batch.Questions[1];
        await DiscoveryTestKit.Expect(Http.PostAsync<QuestionDetailDto>($"{T}/{rejected.Id}/reject", null, setup.Token));

        foreach (var id in new[] { pending.Id, rejected.Id })
        {
            Assert.Equal(HttpStatusCode.OK, (await Http.GetAsync<QuestionDetailDto>($"{T}/{id}", setup.Token)).Response.StatusCode);
            var (res, body) = await Http.GetAsync<QuestionDetailDto>($"{T}/{id}", colleague);
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
            Assert.Equal("BLOCKED", body.ErrorCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Http.PostAsync<object>($"{T}/{id}/approve", null, colleague)).Response.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Http.PostAsync<object>($"{T}/{id}/duplicate", null, colleague)).Response.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync<object>($"{T}/{id}/preview", colleague)).Response.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Http.PostAsync<object>($"{T}/{id}/try-grade", new { }, colleague)).Response.StatusCode);
        }
        var shared = await _k.ListAsync(colleague);
        Assert.DoesNotContain(shared.Items, i => i.Id == pending.Id || i.Id == rejected.Id);
        Assert.Equal((0, 0, 0), (shared.Items.Count(i => i.CreatedBy == setup.School.Teacher.Id), (await _k.ListAsync(colleague, "scope=MINE")).TotalItems, (await _k.ListAsync(colleague, "scope=PENDING")).TotalItems));
    }

    // ---- Chỉ đọc khi câu nằm trong quiz ----

    [Fact]
    public async Task A_question_inside_a_quiz_is_read_only_and_can_only_be_duplicated()
    {
        var (setup, samples) = await SetupAsync();
        var token = setup.Token;
        var created = await _k.CreateAsync(token, samples[QuestionType.SINGLE_CHOICE]);
        await _k.AddToQuizAsync(setup.LessonId, created.Id);

        var detail = await _k.GetAsync(token, created.Id);
        Assert.Equal(1, detail.QuizCount);
        Assert.True(detail.Access is { IsReadOnly: true, Reason: QuestionReadOnlyReason.USED_IN_QUIZ, CanEdit: false, CanDelete: false, CanDuplicate: true });
        Assert.Equal("Câu hỏi này chỉ xem được vì đã được dùng trong bài kiểm tra. Bấm \"Nhân bản\" để tạo bản của bạn rồi sửa.", detail.Access.Message);
        Assert.Equal(1, (await _k.ListAsync(token, "scope=MINE")).Items.Single(i => i.Id == created.Id).QuizCount);

        var (put, putBody) = await Http.PutAsync<object>($"{T}/{created.Id}", samples[QuestionType.SINGLE_CHOICE] with { Explanation = "Sửa lén" }, token);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "BLOCKED"), (put.StatusCode, putBody.ErrorCode));
        Assert.StartsWith("Không thể sửa câu hỏi. Câu hỏi đã nằm trong bài kiểm tra.", putBody.Message);
        var (del, delBody) = await Http.DeleteAsync<object>($"{T}/{created.Id}", token);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "BLOCKED"), (del.StatusCode, delBody.ErrorCode));
        Assert.Equal(samples[QuestionType.SINGLE_CHOICE].Explanation, (await _k.GetAsync(token, created.Id)).Explanation);

        var copy = await DiscoveryTestKit.Expect(Http.PostAsync<QuestionDetailDto>($"{T}/{created.Id}/duplicate", null, token), HttpStatusCode.Created);
        Assert.NotEqual(created.Id, copy.Id);
        Assert.Equal((QuestionSource.MANUAL, ReviewStatus.APPROVED, 0), (copy.Source, copy.ReviewStatus, copy.QuizCount));
        Assert.Equal(created.Options.Select(o => (o.Content, o.IsCorrect)), copy.Options.Select(o => (o.Content, o.IsCorrect)));
        Assert.True(copy.Access.CanEdit);
        var edited = await DiscoveryTestKit.Expect(Http.PutAsync<QuestionDetailDto>($"{T}/{copy.Id}", samples[QuestionType.SINGLE_CHOICE] with { Explanation = "Bản sao đã sửa" }, token));
        Assert.Equal("Bản sao đã sửa", edited.Explanation);
        Assert.Equal(samples[QuestionType.SINGLE_CHOICE].Explanation, (await _k.GetAsync(token, created.Id)).Explanation);        // câu gốc không đổi
    }

    [Fact]
    public async Task Another_teacher_can_duplicate_a_shared_question_into_a_question_of_their_own()
    {
        var (setup, samples) = await SetupAsync();
        var created = await _k.CreateAsync(setup.Token, samples[QuestionType.FILL_BLANK]);
        var other = (await _k.Lesson.Classroom.SchoolAsync()).TeacherToken;

        var copy = await DiscoveryTestKit.Expect(Http.PostAsync<QuestionDetailDto>($"{T}/{created.Id}/duplicate", null, other), HttpStatusCode.Created);

        Assert.NotEqual(setup.School.Teacher.Id, copy.CreatedBy);
        Assert.Equal((QuestionType.FILL_BLANK, created.LevelMin, created.LevelMax, created.HeritageId), (copy.QuestionType, copy.LevelMin, copy.LevelMax, copy.HeritageId));
        Assert.Equal(created.Blanks.Select(b => b.Accepted), copy.Blanks.Select(b => b.Accepted));
        Assert.Equal(created.Distractors, copy.Distractors);
        Assert.Equal((created.Content, created.Explanation, created.DifficultyLevel), (copy.Content, copy.Explanation, copy.DifficultyLevel));
        Assert.Equal(copy.CreatedBy, copy.ReviewedBy);
        Assert.True(copy.Access.CanEdit);
    }

    // ---- Tìm và lọc ----

    [Fact]
    public async Task The_shared_list_filters_by_type_level_difficulty_heritage_creator_and_keyword()
    {
        var (setup, samples) = await SetupAsync();
        var second = await _k.HeritageAsync("Văn Miếu Quốc Tử Giám", extraImages: 3);
        var secondSamples = QuestionTestKit.Samples(second.Heritage.Id, second.Images);
        var colleague = (await _k.Lesson.Classroom.SchoolAsync()).TeacherToken;

        var single = await _k.CreateAsync(setup.Token, samples[QuestionType.SINGLE_CHOICE]);                                  // cấp 2-3, dễ
        var fill = await _k.CreateAsync(setup.Token, samples[QuestionType.FILL_BLANK]);                                       // cấp 2, vừa
        var multi = await _k.CreateAsync(setup.Token, samples[QuestionType.MULTI_TRUE_FALSE]);                                // cấp 3, khó
        var image = await _k.CreateAsync(colleague, secondSamples[QuestionType.IMAGE_CHOICE]);                                // cấp 1, dễ, di sản khác, người khác
        var ordering = await _k.CreateAsync(colleague, secondSamples[QuestionType.ORDERING]);                                 // cấp 1-2, vừa
        var ids = new[] { single.Id, fill.Id, multi.Id, image.Id, ordering.Id };

        async Task<long[]> Ids(string query) =>
            (await _k.ListAsync(colleague, query + "&pageSize=100")).Items.Select(i => i.Id).Where(ids.Contains).Order().ToArray();

        Assert.Equal(ids.Order().ToArray(), await Ids("scope=SHARED"));
        Assert.Equal(new[] { single.Id }, await Ids("questionType=SINGLE_CHOICE"));
        Assert.Equal(new[] { image.Id, ordering.Id }.Order().ToArray(), await Ids("level=1"));
        Assert.Equal(new[] { single.Id, fill.Id, ordering.Id }.Order().ToArray(), await Ids("level=2"));
        Assert.Equal(new[] { single.Id, multi.Id }.Order().ToArray(), await Ids("level=3"));
        Assert.Equal(new[] { single.Id, image.Id }.Order().ToArray(), await Ids("difficultyLevel=EASY"));
        Assert.Equal(new[] { multi.Id }, await Ids("difficultyLevel=HARD"));
        Assert.Equal(new[] { image.Id, ordering.Id }.Order().ToArray(), await Ids($"heritageId={second.Heritage.Id}"));
        Assert.Equal(new[] { single.Id, fill.Id, multi.Id }.Order().ToArray(), await Ids($"createdBy={setup.School.Teacher.Id}"));
        Assert.Equal(new[] { fill.Id }, await Ids("keyword=Hoa%20L%C6%B0"));                                                  // "Hoa Lư" trong lời câu
        Assert.Equal(new[] { single.Id }, await Ids("questionType=SINGLE_CHOICE&level=2&difficultyLevel=EASY&source=MANUAL"));
        Assert.Empty(await Ids("questionType=MATCHING&level=3"));
        Assert.Empty(await Ids("keyword=khong-co-cau-nao-nhu-vay"));
    }

    [Fact]
    public async Task The_my_questions_tab_shows_every_status_and_the_status_filter_narrows_it()
    {
        var setup = await _k.LessonAsync(2);
        var samples = QuestionTestKit.Samples(setup.Heritage.Id, setup.Images);
        var manual = await _k.CreateAsync(setup.Token, samples[QuestionType.SINGLE_CHOICE]);
        var batch = await _k.GenerateAsync(setup.Token, setup.LessonId, new { count = 3, types = new[] { "TRUE_FALSE", "FILL_BLANK" } });
        var toReject = batch.Questions[0];
        await DiscoveryTestKit.Expect(Http.PostAsync<QuestionDetailDto>($"{T}/{toReject.Id}/reject", null, setup.Token));

        var mine = await _k.ListAsync(setup.Token, "scope=MINE&pageSize=100");
        Assert.Equal(1 + batch.Questions.Count, mine.TotalItems);
        Assert.Equal(new[] { ReviewStatus.APPROVED }, (await _k.ListAsync(setup.Token, "scope=MINE&status=APPROVED")).Items.Select(i => i.ReviewStatus!.Value).Distinct());
        Assert.Equal(new[] { toReject.Id }, (await _k.ListAsync(setup.Token, "scope=MINE&status=REJECTED")).Items.Select(i => i.Id));
        Assert.Equal(batch.Questions.Count - 1, (await _k.ListAsync(setup.Token, "scope=MINE&status=PENDING")).TotalItems);
        Assert.Equal(batch.Questions.Count - 1, (await _k.ListAsync(setup.Token, "scope=PENDING")).TotalItems);
        Assert.All((await _k.ListAsync(setup.Token, "scope=PENDING")).Items, i => Assert.Equal((ReviewStatus.PENDING, QuestionSource.AI, true), (i.ReviewStatus, i.Source, i.IsMine)));
        Assert.Equal(new[] { manual.Id }, (await _k.ListAsync(setup.Token, "scope=MINE&source=MANUAL")).Items.Select(i => i.Id));

        var counts = await DiscoveryTestKit.Expect(Http.GetAsync<QuestionCountsDto>($"{T}/counts", setup.Token));
        Assert.Equal((1 + batch.Questions.Count, batch.Questions.Count - 1, 1), (counts.Mine, counts.Pending, counts.Rejected));
        Assert.True(counts.Shared >= 1);
    }

    [Fact]
    public async Task Deleted_questions_leave_every_list_and_paging_is_validated_and_stable()
    {
        var (setup, samples) = await SetupAsync();
        var token = setup.Token;
        var made = new List<long>();
        for (var i = 0; i < 5; i++)
            made.Add((await _k.CreateAsync(token, samples[QuestionType.SINGLE_CHOICE] with { Content = $"Câu hỏi số {i + 1} về Hoàng thành Thăng Long là gì?" })).Id);

        var first = await _k.ListAsync(token, "scope=MINE&pageSize=2&page=1");
        var third = await _k.ListAsync(token, "scope=MINE&pageSize=2&page=3");
        Assert.Equal((5, 3, 2, 1), (first.TotalItems, first.TotalPages, first.Items.Count, third.Items.Count));
        Assert.Equal(made.OrderByDescending(x => x), (await _k.ListAsync(token, "scope=MINE&pageSize=100")).Items.Select(i => i.Id));   // mới tạo trước

        await Http.DeleteAsync<object>($"{T}/{made[0]}", token);
        Assert.Equal(4, (await _k.ListAsync(token, "scope=MINE")).TotalItems);
        Assert.Equal(4, (await _k.ListAsync(token, "scope=SHARED&heritageId=" + setup.Heritage.Id)).TotalItems);

        foreach (var bad in new[] { "page=0", "pageSize=0", "pageSize=101", "level=0", "level=4" })
            Assert.Equal(HttpStatusCode.BadRequest, (await Http.GetAsync<object>($"{T}?{bad}", token)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Http.GetAsync<object>($"{T}?questionType=ESSAY", token)).Response.StatusCode);
    }

    // ---- Xem trước, chấm thử, kiểm không lưu ----

    [Fact]
    public async Task The_preview_shows_the_question_without_answers_as_a_student_of_the_level_sees_it()
    {
        var (setup, samples) = await SetupAsync();
        var token = setup.Token;

        var single = await _k.CreateAsync(token, samples[QuestionType.SINGLE_CHOICE]);
        var asLevel2 = await DiscoveryTestKit.Expect(Http.GetAsync<QuestionPreviewDto>($"{T}/{single.Id}/preview", token));
        Assert.Equal((2, "STANDARD", false, 4), (asLevel2.Level, asLevel2.Layout, asLevel2.ReadAloud, asLevel2.Options.Count));
        var asLevel3 = await DiscoveryTestKit.Expect(Http.GetAsync<QuestionPreviewDto>($"{T}/{single.Id}/preview?level=3", token));
        Assert.Equal((3, "COMPACT"), (asLevel3.Level, asLevel3.Layout));
        Assert.Equal(HttpStatusCode.BadRequest, (await Http.GetAsync<object>($"{T}/{single.Id}/preview?level=1", token)).Response.StatusCode);    // câu không dùng ở cấp 1

        var trueFalse = await _k.CreateAsync(token, samples[QuestionType.TRUE_FALSE]);
        var level1 = await DiscoveryTestKit.Expect(Http.GetAsync<QuestionPreviewDto>($"{T}/{trueFalse.Id}/preview?level=1", token));
        Assert.Equal((1, "LARGE_CARDS", true, setup.Images[0]), (level1.Level, level1.Layout, level1.ReadAloud, level1.ImageUrl));

        var matching = await _k.CreateAsync(token, samples[QuestionType.MATCHING]);
        var pairs = await DiscoveryTestKit.Expect(Http.GetAsync<QuestionPreviewDto>($"{T}/{matching.Id}/preview", token));
        Assert.Equal(3, pairs.LeftItems.Count);
        Assert.Equal(matching.Pairs.Select(p => p.Right).Order(), pairs.RightItems.Order());

        var ordering = await _k.CreateAsync(token, samples[QuestionType.ORDERING]);
        var items = await DiscoveryTestKit.Expect(Http.GetAsync<QuestionPreviewDto>($"{T}/{ordering.Id}/preview", token));
        Assert.Equal(ordering.Steps.Order(), items.Items.Order());
        Assert.NotEqual(ordering.Steps, items.Items);                                                                  // đã xáo, không lộ thứ tự đúng

        var fill = await _k.CreateAsync(token, samples[QuestionType.FILL_BLANK]);
        var blanks = await DiscoveryTestKit.Expect(Http.GetAsync<QuestionPreviewDto>($"{T}/{fill.Id}/preview", token));
        Assert.Equal((1, 3), (blanks.BlankCount, blanks.WordBank.Count));

        // Không có chỗ nào của bản xem trước nói đáp án nào đúng.
        var raw = await (await Http.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"{T}/{single.Id}/preview")
        {
            Headers = { Authorization = new("Bearer", token) },
        })).Content.ReadAsStringAsync();
        Assert.DoesNotContain("isCorrect", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("explanation", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Trial_grading_scores_each_part_like_the_real_grader()
    {
        var (setup, samples) = await SetupAsync();
        var token = setup.Token;

        var single = await _k.CreateAsync(token, samples[QuestionType.SINGLE_CHOICE]);
        var right = await DiscoveryTestKit.Expect(Http.PostAsync<GradeResultDto>($"{T}/{single.Id}/try-grade", new { selectedIndex = 1 }, token));
        Assert.Equal((1, 1, true), (right.PartsCorrect, right.PartsTotal, right.IsCorrect));
        var wrong = await DiscoveryTestKit.Expect(Http.PostAsync<GradeResultDto>($"{T}/{single.Id}/try-grade", new { selectedIndex = 0 }, token));
        Assert.Equal((0, 1, false), (wrong.PartsCorrect, wrong.PartsTotal, wrong.IsCorrect));
        var blank = await DiscoveryTestKit.Expect(Http.PostAsync<GradeResultDto>($"{T}/{single.Id}/try-grade", new { }, token));
        Assert.Equal(0, blank.PartsCorrect);

        var statements = await _k.CreateAsync(token, samples[QuestionType.MULTI_TRUE_FALSE]);
        var partly = await DiscoveryTestKit.Expect(Http.PostAsync<GradeResultDto>($"{T}/{statements.Id}/try-grade", new { statements = new bool?[] { true, true, true } }, token));
        Assert.Equal((2, 3, false), (partly.PartsCorrect, partly.PartsTotal, partly.IsCorrect));
        Assert.Equal(0.67m, Math.Round(partly.Fraction, 2));

        var fill = await _k.CreateAsync(token, samples[QuestionType.FILL_BLANK]);
        Assert.True((await DiscoveryTestKit.Expect(Http.PostAsync<GradeResultDto>($"{T}/{fill.Id}/try-grade", new { blanks = new[] { "  LÝ  công uẩn " } }, token))).IsCorrect);
        Assert.False((await DiscoveryTestKit.Expect(Http.PostAsync<GradeResultDto>($"{T}/{fill.Id}/try-grade", new { blanks = new[] { "Ly Cong Uan" } }, token))).IsCorrect);

        var ordering = await _k.CreateAsync(token, samples[QuestionType.ORDERING]);
        var swapped = await DiscoveryTestKit.Expect(Http.PostAsync<GradeResultDto>($"{T}/{ordering.Id}/try-grade", new { order = new[] { 1, 0, 2, 3 } }, token));
        Assert.Equal((2, 4), (swapped.PartsCorrect, swapped.PartsTotal));
    }

    [Fact]
    public async Task Validate_reports_errors_and_parts_without_saving_anything()
    {
        var (setup, samples) = await SetupAsync();
        var token = setup.Token;
        var before = (await _k.ListAsync(token, "scope=MINE")).TotalItems;

        var good = await DiscoveryTestKit.Expect(Http.PostAsync<ValidateQuestionResultDto>($"{T}/validate", new
        {
            question = samples[QuestionType.MATCHING],
            previewLevel = 1,
            submission = new { matches = new int?[] { 0, 2, 1 } },
        }, token));
        Assert.True(good.IsValid);
        Assert.Empty(good.Errors);
        Assert.Equal(3, good.PartsTotal);
        Assert.Equal((1, "LARGE_CARDS"), (good.Preview!.Level, good.Preview.Layout));
        Assert.Equal((1, 3), (good.Trial!.PartsCorrect, good.Trial.PartsTotal));

        var bad = await DiscoveryTestKit.Expect(Http.PostAsync<ValidateQuestionResultDto>($"{T}/validate", new
        {
            question = samples[QuestionType.SINGLE_CHOICE] with { Options = [new("Chỉ một", null, true)] },
        }, token));                                                                           // 200 dù câu sai: trình soạn hiện lỗi ngay khi gõ
        Assert.False(bad.IsValid);
        Assert.Contains("options", bad.Errors.Keys);
        Assert.Null(bad.Preview);

        var missing = await Http.PostAsync<object>($"{T}/validate", new { }, token);
        Assert.Equal(HttpStatusCode.BadRequest, missing.Response.StatusCode);
        Assert.Equal(before, (await _k.ListAsync(token, "scope=MINE")).TotalItems);
    }

    // ---- Sửa ----

    [Fact]
    public async Task Editing_replaces_the_answers_and_can_change_the_type_of_a_manual_question()
    {
        var (setup, samples) = await SetupAsync();
        var token = setup.Token;
        var created = await _k.CreateAsync(token, samples[QuestionType.SINGLE_CHOICE]);

        var asFill = await DiscoveryTestKit.Expect(Http.PutAsync<QuestionDetailDto>($"{T}/{created.Id}", samples[QuestionType.FILL_BLANK], token));

        Assert.Equal((created.Id, QuestionType.FILL_BLANK, 2, 2), (asFill.Id, asFill.QuestionType, asFill.LevelMin, asFill.LevelMax));
        Assert.Empty(asFill.Options);
        Assert.Equal(["Lý Thái Tổ", "Lý Công Uẩn"], asFill.Blanks.Single().Accepted);
        var answerCount = await factory.WithDbAsync(db => db.Answers.CountAsync(a => a.QuestionId == created.Id));
        Assert.Equal(4, answerCount);                                                          // 2 cách viết + 2 từ gây nhiễu, không còn 4 phương án cũ
    }

    [Fact]
    public async Task An_invalid_edit_changes_nothing()
    {
        var (setup, samples) = await SetupAsync();
        var created = await _k.CreateAsync(setup.Token, samples[QuestionType.SINGLE_CHOICE]);

        var (res, body) = await Http.PutAsync<object>($"{T}/{created.Id}", samples[QuestionType.SINGLE_CHOICE] with { Explanation = null }, setup.Token);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("explanation", body.Errors!.Keys);
        var after = await _k.GetAsync(setup.Token, created.Id);
        Assert.Equal((created.Explanation, created.Options.Count), (after.Explanation, after.Options.Count));
    }
}
