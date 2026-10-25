using System.Net;
using DSVHVN.Application.Attempts;
using DSVHVN.Application.Lessons;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Trang học cấp 1 (các bài của lớp tự hiện trên con đường, số thẻ album, lời cổ vũ mới), ghi bài đã học xong, album di sản và bước Nhớ lại.
/// </summary>
public sealed class LevelOnePathApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly AttemptTestKit _k = new(factory);
    private LessonTestKit Lessons => _k.Quizzes.Questions.Lesson;
    private HttpClient Http => _k.Http;

    private Task SetHeritageTypeAsync(long heritageId, HeritageType type) =>
        factory.WithDbAsync(async db =>
        {
            (await db.Heritages.SingleAsync(h => h.Id == heritageId)).HeritageType = type;
            return await db.SaveChangesAsync();
        });

    /// <summary>Thêm một bài học đã xuất bản cho cùng lớp, với một di sản mới.</summary>
    private async Task<(LessonDetailDto Lesson, DSVHVN.Application.Heritages.HeritageDetailDto Heritage)> AnotherLessonWithHeritageAsync(
        QuizTestKit.Setup s, string title, string heritageName)
    {
        var (heritage, _) = await _k.Quizzes.Questions.HeritageAsync(heritageName);
        var lesson = await Lessons.PublishedLessonAsync(s.Token, s.ClassId, [heritage.Id], title);
        return (lesson, heritage);
    }

    // ---- Trang học ----

    [Fact]
    public async Task The_home_page_of_a_level_one_class_shows_the_published_lessons_in_teaching_order_with_progress_and_the_new_cheer()
    {
        var first = await _k.OpenQuizAsync(1);
        var (second, secondHeritage) = await AnotherLessonWithHeritageAsync(first.Setup, "Bài thứ hai", "Hội Gióng");
        var draft = await Lessons.CreateLessonAsync(first.Setup.Token, first.Setup.ClassId, [first.Setup.Heritage.Id], "Bài còn nháp");
        var token = first.StudentToken;

        var home = await _k.HomeAsync(token);
        Assert.Equal([first.Setup.LessonId, second.Id], home.ClassLessons.Select(l => l.LessonId));
        Assert.DoesNotContain(home.ClassLessons, l => l.LessonId == draft.Id);
        var one = home.ClassLessons[0];
        Assert.Equal((first.Setup.AccessCode, 1, 0, (int?)null, false), (one.AccessCode, one.QuizzesPublished, one.QuizzesDone, one.Stars, one.Completed));
        Assert.Equal(first.Setup.Heritage.Name, Assert.Single(one.HeritageNames));
        Assert.NotNull(one.CreatedAt);
        Assert.Equal((0, 0), (home.ClassLessons[1].QuizzesPublished, home.ClassLessons[1].QuizzesDone));
        Assert.Equal(secondHeritage.Name, Assert.Single(home.ClassLessons[1].HeritageNames));
        Assert.Equal((0, 2), (home.AlbumCollected, home.AlbumTotal));
        Assert.Equal("Hôm nay mình học bài “Bài học thử” nhé!", home.Cheer);

        // Bài chơi của bài đầu không cần gõ mã nữa ở cấp 1.
        Assert.All(home.OpenQuizzes, x => Assert.False(x.NeedsLessonCode));
        Assert.All(home.OpenQuizzes, x => Assert.Null(x.Label));

        // Đang chơi dở.
        var attempt = await _k.StartAsync(token, first.Id);
        Assert.Equal("Em có bài chơi đang dở. Mình chơi tiếp nhé!", (await _k.HomeAsync(token)).Cheer);

        // Nộp xong bài chơi: bài đầu đã xong, có sao; bài kế tiếp là bài chưa xong.
        await _k.AnswerAllAndSubmitAsync(first, AttemptTestKit.Correct, token);
        var after = await _k.HomeAsync(token);
        var done = after.ClassLessons[0];
        Assert.Equal((1, 1, 3, true), (done.QuizzesPublished, done.QuizzesDone, done.Stars, done.Completed));
        Assert.False(after.ClassLessons[1].Completed);
        Assert.Equal((1, 2), (after.AlbumCollected, after.AlbumTotal));
        Assert.Equal("Hôm nay mình học bài “Bài thứ hai” nhé!", after.Cheer);
        _ = attempt;

        // Học xong bài thứ hai (bước Nhận thẻ) mà bài chưa có bài chơi: xong hết, lời khen theo sao.
        var completed = await DiscoveryTestKit.Expect(Lessons.CompleteRawAsync(token, second.AccessCode!));
        Assert.True(completed.FirstTime);
        var finished = await _k.HomeAsync(token);
        Assert.All(finished.ClassLessons, l => Assert.True(l.Completed));
        Assert.Equal((2, 2), (finished.AlbumCollected, finished.AlbumTotal));
        Assert.Equal($"Em đã có {finished.TotalStars} sao. Giỏi quá!", finished.Cheer);
    }

    [Fact]
    public async Task A_level_one_class_without_lessons_is_told_to_come_back_and_the_other_levels_get_no_path_or_album()
    {
        var setup = await _k.Quizzes.SetupAsync(1, publishLesson: false);
        var token = await Lessons.StudentTokenAsync(setup.Token, setup.ClassId, "Lê Thị Cúc");
        var empty = await _k.HomeAsync(token);
        Assert.Empty(empty.ClassLessons);
        Assert.Equal((0, 0), (empty.AlbumCollected, empty.AlbumTotal));
        Assert.Equal("Thầy cô chưa giao bài nào. Em quay lại sau nhé!", empty.Cheer);

        var two = await _k.OpenQuizAsync(2);
        var home = await _k.HomeAsync(two.StudentToken);
        Assert.Empty(home.ClassLessons);
        Assert.Null(home.AlbumCollected);
        Assert.Null(home.AlbumTotal);
        Assert.Null(home.Cheer);
        Assert.True(Assert.Single(home.OpenQuizzes).NeedsLessonCode);
    }

    [Fact]
    public async Task A_lesson_of_another_class_or_an_archived_or_deleted_one_is_not_on_the_path()
    {
        var q = await _k.OpenQuizAsync(1);
        var other = await _k.OpenQuizAsync(1);   // lớp khác
        var (archived, _) = await AnotherLessonWithHeritageAsync(q.Setup, "Bài đã lưu trữ", "Chùa Một Cột");
        var (deleted, _) = await AnotherLessonWithHeritageAsync(q.Setup, "Bài đã xóa", "Văn Miếu");
        await factory.WithDbAsync(async db =>
        {
            (await db.Lessons.SingleAsync(l => l.Id == archived.Id)).Status = ContentStatus.ARCHIVED;
            (await db.Lessons.SingleAsync(l => l.Id == deleted.Id)).DeletedAt = DateTime.UtcNow;
            return await db.SaveChangesAsync();
        });

        var home = await _k.HomeAsync(q.StudentToken);
        Assert.Equal([q.Setup.LessonId], home.ClassLessons.Select(l => l.LessonId));
        Assert.Equal((0, 1), (home.AlbumCollected, home.AlbumTotal));
        Assert.Equal([other.Setup.LessonId], (await _k.HomeAsync(other.StudentToken)).ClassLessons.Select(l => l.LessonId));
    }

    // ---- Ghi bài đã học xong ----

    [Fact]
    public async Task Completing_a_lesson_records_it_once_and_returns_the_cards_the_album_count_and_the_take_home_question()
    {
        var q = await _k.OpenQuizAsync(1);
        await SetHeritageTypeAsync(q.Setup.Heritage.Id, HeritageType.INTANGIBLE);
        var code = q.Setup.AccessCode!;

        var first = await DiscoveryTestKit.Expect(Lessons.CompleteRawAsync(q.StudentToken, code));
        Assert.Equal((q.Setup.LessonId, true, 1, 1), (first.LessonId, first.FirstTime, first.AlbumCollected, first.AlbumTotal));
        var card = Assert.Single(first.Cards);
        Assert.Equal((q.Setup.Heritage.Id, q.Setup.Heritage.Slug, q.Setup.Heritage.Name, true, "Lễ hội, nghề và làn điệu", HeritageType.INTANGIBLE),
            (card.HeritageId, card.Slug, card.Name, card.Collected, card.GroupName, card.HeritageType));
        Assert.Equal((q.Setup.LessonId, "Bài học thử"), (card.LessonId, card.LessonTitle));
        Assert.Equal($"Ông bà, bố mẹ đã từng xem hay tham gia {q.Setup.Heritage.Name} chưa?", first.TakeHome);

        // Gọi lại: vẫn một dòng, giữ thời điểm đầu, không còn là lần đầu. Gõ mã thường có khoảng trắng vẫn nhận.
        var again = await DiscoveryTestKit.Expect(Lessons.CompleteRawAsync(q.StudentToken, code.ToLowerInvariant()));
        Assert.False(again.FirstTime);
        Assert.Equal(card.CollectedAt, Assert.Single(again.Cards).CollectedAt);
        var rows = await factory.WithDbAsync(db => db.LessonCompletions.Where(c => c.LessonId == q.Setup.LessonId).ToListAsync());
        var row = Assert.Single(rows);
        Assert.Equal(card.CollectedAt, row.CompletedAt);
    }

    [Fact]
    public async Task Completing_with_a_wrong_unpublished_or_foreign_code_looks_the_same_and_counts_toward_the_wrong_code_limit()
    {
        var q = await _k.OpenQuizAsync(1);
        var other = await _k.OpenQuizAsync(1);
        var draft = await Lessons.CreateLessonAsync(q.Setup.Token, q.Setup.ClassId, [q.Setup.Heritage.Id], "Bài nháp");

        var wrong = await DiscoveryTestKit.Fail(Lessons.CompleteRawAsync(q.StudentToken, "ZZZZZZ").AsObjectAsync(), HttpStatusCode.NotFound, "LESSON_CODE_INVALID");
        var foreign = await DiscoveryTestKit.Fail(Lessons.CompleteRawAsync(q.StudentToken, other.Setup.AccessCode!).AsObjectAsync(), HttpStatusCode.NotFound, "LESSON_CODE_INVALID");
        Assert.Equal(wrong.Message, foreign.Message);
        await DiscoveryTestKit.Fail(Lessons.CompleteRawAsync(q.StudentToken, draft.AccessCode ?? "ABCDEF").AsObjectAsync(), HttpStatusCode.NotFound, "LESSON_CODE_INVALID");
        var lessonIds = new[] { q.Setup.LessonId, other.Setup.LessonId, draft.Id };
        Assert.Equal(0, await factory.WithDbAsync(db => db.LessonCompletions.CountAsync(c => lessonIds.Contains(c.LessonId))));

        var (anonymous, _) = await Http.PostAsync<object>($"{LessonTestKit.Student}/{q.Setup.AccessCode}/complete", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var (teacher, _) = await Http.PostAsync<object>($"{LessonTestKit.Student}/{q.Setup.AccessCode}/complete", null, q.TeacherToken);
        Assert.Equal(HttpStatusCode.Forbidden, teacher.StatusCode);

        // Nhập sai mã nhiều lần thì bị chặn nhập mã (429), như khi mở bài bằng mã; ba lần sai ở trên đã tính.
        var blockedAt = 0;
        for (var i = 4; i <= 12 && blockedAt == 0; i++)
        {
            var (res, _) = await Lessons.CompleteRawAsync(q.StudentToken, "ZZZZZZ");
            if (res.StatusCode == HttpStatusCode.TooManyRequests) blockedAt = i;
        }
        Assert.Equal(LessonRules.MaxWrongCodesPerStudent, blockedAt);
        var (blocked, _) = await Lessons.RecallRawAsync(q.StudentToken, q.Setup.AccessCode!);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);   // đang bị chặn thì mã đúng cũng không xét, ở cả bước Nhớ lại
    }

    // ---- Album ----

    [Fact]
    public async Task The_album_shows_collected_cards_newest_first_then_the_missing_ones_in_lesson_order_with_their_group_names()
    {
        var a = await _k.OpenQuizAsync(1);
        var token = a.StudentToken;
        await SetHeritageTypeAsync(a.Setup.Heritage.Id, HeritageType.TANGIBLE);
        var (lessonB, heritageB) = await AnotherLessonWithHeritageAsync(a.Setup, "Bài B", "Hội Gióng");
        await SetHeritageTypeAsync(heritageB.Id, HeritageType.INTANGIBLE);
        var (lessonC, heritageC) = await AnotherLessonWithHeritageAsync(a.Setup, "Bài C", "Vịnh Hạ Long");
        await SetHeritageTypeAsync(heritageC.Id, HeritageType.NATURAL);

        var start = await _k.AlbumAsync(token);
        Assert.Equal((0, 3), (start.Collected, start.Total));
        Assert.Equal([a.Setup.Heritage.Id, heritageB.Id, heritageC.Id], start.Cards.Select(c => c.HeritageId));
        Assert.Equal(["Di tích và đồ vật xưa", "Lễ hội, nghề và làn điệu", "Cảnh đẹp thiên nhiên"], start.Cards.Select(c => c.GroupName));
        Assert.All(start.Cards, c => Assert.Equal((false, (DateTime?)null), (c.Collected, c.CollectedAt)));
        Assert.Equal([a.Setup.LessonId, lessonB.Id, lessonC.Id], start.Cards.Select(c => c.LessonId));
        Assert.Equal(["Bài học thử", "Bài B", "Bài C"], start.Cards.Select(c => c.LessonTitle));

        // Học xong bài C trước, nộp bài chơi của bài A sau: thẻ nhận sau xếp trước; thẻ chưa có xếp cuối.
        await DiscoveryTestKit.Expect(Lessons.CompleteRawAsync(token, lessonC.AccessCode!));
        await Task.Delay(30);
        await _k.AnswerAllAndSubmitAsync(a, AttemptTestKit.Wrong, token);   // nộp bài (dù sai) cũng được thẻ
        var album = await _k.AlbumAsync(token);
        Assert.Equal((2, 3), (album.Collected, album.Total));
        Assert.Equal([a.Setup.Heritage.Id, heritageC.Id, heritageB.Id], album.Cards.Select(c => c.HeritageId));
        Assert.Equal([true, true, false], album.Cards.Select(c => c.Collected));
        Assert.True(album.Cards[0].CollectedAt > album.Cards[1].CollectedAt);
        Assert.Null(album.Cards[2].CollectedAt);
        Assert.Equal(album.Cards.Count, (await _k.HomeAsync(token)).AlbumTotal);
    }

    [Fact]
    public async Task One_heritage_in_two_lessons_is_one_card_of_the_earliest_lesson_and_archived_lessons_keep_only_the_cards_already_collected()
    {
        var a = await _k.OpenQuizAsync(1);
        var token = a.StudentToken;
        // Bài thứ hai dùng lại đúng di sản của bài đầu và thêm một di sản mới.
        var (heritageNew, _) = await _k.Quizzes.Questions.HeritageAsync("Cố đô Huế");
        var second = await Lessons.PublishedLessonAsync(a.Setup.Token, a.Setup.ClassId, [a.Setup.Heritage.Id, heritageNew.Id], "Bài hai di sản");
        var (archivedLesson, archivedHeritage) = await AnotherLessonWithHeritageAsync(a.Setup, "Bài sẽ lưu trữ", "Phố cổ Hội An");
        var (unlearnedLesson, unlearnedHeritage) = await AnotherLessonWithHeritageAsync(a.Setup, "Bài lưu trữ chưa học", "Thánh địa Mỹ Sơn");

        var album = await _k.AlbumAsync(token);
        Assert.Equal((4, 0), (album.Total, album.Collected));
        var shared = album.Cards.Single(c => c.HeritageId == a.Setup.Heritage.Id);
        Assert.Equal(a.Setup.LessonId, shared.LessonId);   // bài sớm nhất

        // Học xong bài hai: thẻ dùng chung được tính cho cả hai bài.
        await DiscoveryTestKit.Expect(Lessons.CompleteRawAsync(token, second.AccessCode!));
        Assert.Equal(2, (await _k.AlbumAsync(token)).Collected);
        Assert.False((await _k.HomeAsync(token)).ClassLessons.Single(l => l.LessonId == a.Setup.LessonId).Completed);

        // Lưu trữ bài chưa học: thẻ của bài đó biến mất; lưu trữ bài đã học: thẻ còn.
        await DiscoveryTestKit.Expect(Lessons.CompleteRawAsync(token, archivedLesson.AccessCode!));
        await factory.WithDbAsync(async db =>
        {
            foreach (var l in await db.Lessons.Where(l => l.Id == archivedLesson.Id || l.Id == second.Id || l.Id == unlearnedLesson.Id).ToListAsync())
                l.Status = ContentStatus.ARCHIVED;
            return await db.SaveChangesAsync();
        });
        var afterArchive = await _k.AlbumAsync(token);
        Assert.Equal((3, 3), (afterArchive.Collected, afterArchive.Total));
        Assert.DoesNotContain(afterArchive.Cards, c => c.HeritageId == unlearnedHeritage.Id);
        Assert.Contains(afterArchive.Cards, c => c.HeritageId == archivedHeritage.Id && c.Collected);
        Assert.Contains(afterArchive.Cards, c => c.HeritageId == heritageNew.Id && c.Collected);
    }

    [Fact]
    public async Task The_album_is_private_to_the_student_and_only_for_a_signed_in_student()
    {
        var a = await _k.OpenQuizAsync(1);
        var friend = await _k.AnotherStudentAsync(a);
        await DiscoveryTestKit.Expect(Lessons.CompleteRawAsync(a.StudentToken, a.Setup.AccessCode!));

        Assert.Equal(1, (await _k.AlbumAsync(a.StudentToken)).Collected);
        Assert.Equal(0, (await _k.AlbumAsync(friend)).Collected);
        var (anonymous, _) = await Http.GetAsync<object>($"{AttemptTestKit.Student}/album");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var (teacher, _) = await Http.GetAsync<object>($"{AttemptTestKit.Student}/album", a.TeacherToken);
        Assert.Equal(HttpStatusCode.Forbidden, teacher.StatusCode);
    }

    // ---- Bước Nhớ lại ----

    [Fact]
    public async Task Recall_is_empty_until_the_student_has_submitted_a_quiz_of_another_lesson()
    {
        var a = await _k.OpenQuizAsync(1);
        var recall = await DiscoveryTestKit.Expect(Lessons.RecallRawAsync(a.StudentToken, a.Setup.AccessCode!));
        Assert.Empty(recall.Questions);

        // Bài chơi của chính bài đang học không dùng để ôn.
        await _k.AnswerAllAndSubmitAsync(a, AttemptTestKit.Wrong);
        Assert.Empty((await DiscoveryTestKit.Expect(Lessons.RecallRawAsync(a.StudentToken, a.Setup.AccessCode!))).Questions);
    }

    [Fact]
    public async Task Recall_gives_true_false_and_image_questions_of_submitted_quizzes_of_other_lessons_wrong_ones_first_with_the_right_answer_in_the_shuffled_order()
    {
        var a = await _k.OpenQuizAsync(1);
        var token = a.StudentToken;
        var (lessonB, _) = await AnotherLessonWithHeritageAsync(a.Setup, "Bài B", "Hội Gióng");

        // Bài A: sai câu Đúng/Sai, đúng câu chọn hình; câu ghép cặp và sắp xếp không dùng để ôn.
        var attempt = await _k.StartAsync(token, a.Id);
        var tf = attempt.Questions.Single(x => x.QuestionType == QuestionType.TRUE_FALSE);
        var image = attempt.Questions.Single(x => x.QuestionType == QuestionType.IMAGE_CHOICE);
        await _k.CheckAsync(token, attempt.Id, tf.QuestionId, AttemptTestKit.Wrong(tf, AttemptTestKit.DetailOf(a, tf.QuestionId)));
        await _k.CheckAsync(token, attempt.Id, image.QuestionId, AttemptTestKit.Correct(image, AttemptTestKit.DetailOf(a, image.QuestionId)));
        // Chưa nộp thì chưa có gì để ôn.
        Assert.Empty((await DiscoveryTestKit.Expect(Lessons.RecallRawAsync(token, lessonB.AccessCode!))).Questions);
        await _k.SubmitAsync(token, attempt.Id);

        var recall = await DiscoveryTestKit.Expect(Lessons.RecallRawAsync(token, lessonB.AccessCode!));
        Assert.Equal([tf.QuestionId, image.QuestionId], recall.Questions.Select(x => x.QuestionId));
        Assert.Equal([QuestionType.TRUE_FALSE, QuestionType.IMAGE_CHOICE], recall.Questions.Select(x => x.QuestionType));

        var tfQuestion = recall.Questions[0];
        Assert.Equal(["Đúng", "Sai"], tfQuestion.Options.Select(o => o.Content));
        Assert.Equal(0, tfQuestion.CorrectIndex);   // câu mẫu có đáp án là Đúng
        Assert.Equal((a.Setup.Heritage.Name, "Bài học thử"), (tfQuestion.HeritageName, tfQuestion.LessonTitle));
        Assert.Equal(AttemptTestKit.DetailOf(a, tf.QuestionId).Explanation, tfQuestion.Explanation);
        Assert.NotNull(tfQuestion.ImageUrl);

        var imageQuestion = recall.Questions[1];
        var correctCard = AttemptTestKit.DetailOf(a, image.QuestionId).Options.Single(o => o.IsCorrect);
        Assert.Equal(correctCard.ImageUrl, imageQuestion.Options[imageQuestion.CorrectIndex].ImageUrl);
        Assert.Equal(AttemptTestKit.DetailOf(a, image.QuestionId).Options.Count, imageQuestion.Options.Count);

        // Cùng em, cùng câu: thứ tự thẻ không đổi giữa các lần gọi; bạn cùng lớp chưa nộp bài nào thì không có gì để ôn.
        var again = await DiscoveryTestKit.Expect(Lessons.RecallRawAsync(token, lessonB.AccessCode!));
        Assert.Equal(imageQuestion.Options.Select(o => o.ImageUrl), again.Questions[1].Options.Select(o => o.ImageUrl));
        var friend = await _k.AnotherStudentAsync(a, "Phạm Văn Dũng");
        Assert.Empty((await DiscoveryTestKit.Expect(Lessons.RecallRawAsync(friend, lessonB.AccessCode!))).Questions);

        // Chỉ đọc: không có lượt, dòng trả lời hay dòng bài đã học xong nào mới.
        Assert.Equal(0, await factory.WithDbAsync(db => db.LessonCompletions.CountAsync()));
        Assert.Equal(1, await factory.WithDbAsync(db => db.Attempts.CountAsync(x => x.QuizId == a.Id)));
    }

    [Fact]
    public async Task Recall_takes_at_most_three_questions_checks_the_code_and_skips_hidden_questions()
    {
        var a = await _k.OpenQuizAsync(1);
        var token = a.StudentToken;
        var second = await _k.OpenQuizAsync(1, setup: a.Setup, studentName: "Hoàng Văn Em");   // bài chơi thứ hai của cùng bài: thêm hai câu ôn
        var (lessonB, _) = await AnotherLessonWithHeritageAsync(a.Setup, "Bài B", "Hội Gióng");
        await _k.AnswerAllAndSubmitAsync(a, AttemptTestKit.Wrong, token);
        var attemptTwo = await _k.StartAsync(token, second.Id);
        foreach (var question in attemptTwo.Questions)
            await _k.SaveAsync(token, attemptTwo.Id, question.QuestionId, AttemptTestKit.Wrong(question, AttemptTestKit.DetailOf(second, question.QuestionId)));
        await _k.SubmitAsync(token, attemptTwo.Id);

        var recall = await DiscoveryTestKit.Expect(Lessons.RecallRawAsync(token, lessonB.AccessCode!));
        Assert.Equal(3, recall.Questions.Count);
        Assert.Equal(3, recall.Questions.Select(x => x.QuestionId).Distinct().Count());

        // Câu đã xóa mềm không còn để ôn.
        var removed = recall.Questions[0].QuestionId;
        await factory.WithDbAsync(async db =>
        {
            (await db.Questions.SingleAsync(x => x.Id == removed)).DeletedAt = DateTime.UtcNow;
            return await db.SaveChangesAsync();
        });
        var next = await DiscoveryTestKit.Expect(Lessons.RecallRawAsync(token, lessonB.AccessCode!));
        Assert.DoesNotContain(next.Questions, x => x.QuestionId == removed);
        Assert.Equal(3, next.Questions.Count);

        await DiscoveryTestKit.Fail(Lessons.RecallRawAsync(token, "ZZZZZZ").AsObjectAsync(), HttpStatusCode.NotFound, "LESSON_CODE_INVALID");
        var other = await _k.OpenQuizAsync(1);
        await DiscoveryTestKit.Fail(Lessons.RecallRawAsync(token, other.Setup.AccessCode!).AsObjectAsync(), HttpStatusCode.NotFound, "LESSON_CODE_INVALID");
    }
}
