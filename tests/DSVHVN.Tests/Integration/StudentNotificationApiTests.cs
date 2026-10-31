using System.Net;
using DSVHVN.Application.Notifications;
using DSVHVN.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Thông báo cho học sinh: thầy cô gửi cho cả lớp về bài học đã xuất bản hoặc bài kiểm tra đang phát hành, em xem ở nút Thông báo, mở được bài và đánh dấu đã đọc;
/// em chỉ thấy thông báo của mình.
/// </summary>
public sealed class StudentNotificationApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Notifications = "/api/v1/student/notifications";

    private readonly AttemptTestKit _k = new(factory);
    private LessonTestKit Lessons => _k.Quizzes.Questions.Lesson;
    private HttpClient Http => _k.Http;

    private static string LessonUrl(long id) => $"/api/v1/teacher/lessons/{id}/notify-class";
    private static string QuizUrl(long id) => $"{QuizTestKit.Quizzes}/{id}/notify-class";

    private async Task<StudentNotificationPageDto> ListAsync(string token) =>
        await DiscoveryTestKit.Expect(Http.GetAsync<StudentNotificationPageDto>(Notifications, token));

    [Fact]
    public async Task A_teacher_notifies_the_class_about_a_published_lesson_and_each_student_gets_the_note_and_the_code()
    {
        var s = await _k.Quizzes.SetupAsync(2);
        var first = await Lessons.StudentTokenAsync(s.Token, s.ClassId, "Em Một");
        var second = await Lessons.StudentTokenAsync(s.Token, s.ClassId, "Em Hai");

        var (response, body) = await Http.PostAsync<NotifyClassResultDto>(LessonUrl(s.LessonId), new { message = "  Về nhà đọc trước thứ Sáu.  " }, s.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((2, "Đã gửi thông báo cho 2 học sinh."), (body.Data!.SentTo, body.Data.Message));
        foreach (var token in new[] { first, second })
        {
            var page = await ListAsync(token);
            var item = Assert.Single(page.Items);
            Assert.Equal(1, page.UnreadCount);
            Assert.Equal((StudentNotificationKind.LESSON, "Về nhà đọc trước thứ Sáu.", s.LessonId, s.AccessCode, (long?)null, false),
                (item.Kind, item.Body, item.LessonId, item.LessonCode, item.QuizId, item.IsRead));
        }
    }

    [Fact]
    public async Task Leaving_the_note_empty_uses_a_default_sentence()
    {
        var s = await _k.Quizzes.SetupAsync(2);
        var student = await Lessons.StudentTokenAsync(s.Token, s.ClassId);

        await DiscoveryTestKit.Expect(Http.PostAsync<NotifyClassResultDto>(LessonUrl(s.LessonId), new { }, s.Token));

        var item = Assert.Single((await ListAsync(student)).Items);
        Assert.StartsWith("Bài học mới:", item.Title);
        Assert.StartsWith("Thầy cô giao bài học", item.Body);
    }

    [Fact]
    public async Task A_quiz_notification_links_to_the_quiz()
    {
        var quiz = await _k.OpenQuizAsync(2);

        await DiscoveryTestKit.Expect(Http.PostAsync<NotifyClassResultDto>(QuizUrl(quiz.Id), new { }, quiz.TeacherToken));

        var item = Assert.Single((await ListAsync(quiz.StudentToken)).Items);
        Assert.Equal((StudentNotificationKind.QUIZ, quiz.Id, quiz.Setup.AccessCode), (item.Kind, item.QuizId, item.LessonCode));
        Assert.StartsWith("Bài kiểm tra mới:", item.Title);
        Assert.Contains("hạn nộp", item.Body);
    }

    [Fact]
    public async Task A_closed_quiz_or_a_draft_lesson_cannot_be_notified()
    {
        var quiz = await _k.OpenQuizAsync(2);
        await _k.Quizzes.CloseAsync(quiz.TeacherToken, quiz.Id);
        var (closed, _) = await Http.PostAsync<NotifyClassResultDto>(QuizUrl(quiz.Id), new { }, quiz.TeacherToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, closed.StatusCode);

        var draft = await _k.Quizzes.SetupAsync(2, publishLesson: false);
        await Lessons.StudentTokenAsync(draft.Token, draft.ClassId);
        var (lesson, _) = await Http.PostAsync<NotifyClassResultDto>(LessonUrl(draft.LessonId), new { }, draft.Token);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, lesson.StatusCode);
    }

    [Fact]
    public async Task A_class_without_students_cannot_be_notified_and_a_too_long_note_is_refused()
    {
        var s = await _k.Quizzes.SetupAsync(2);

        var (empty, _) = await Http.PostAsync<NotifyClassResultDto>(LessonUrl(s.LessonId), new { }, s.Token);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);

        var (tooLong, _) = await Http.PostAsync<NotifyClassResultDto>(LessonUrl(s.LessonId), new { message = new string('a', 501) }, s.Token);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task Another_teacher_cannot_notify_a_class_that_is_not_theirs()
    {
        var mine = await _k.Quizzes.SetupAsync(2);
        var other = await _k.Quizzes.SetupAsync(2);

        var (response, _) = await Http.PostAsync<NotifyClassResultDto>(LessonUrl(mine.LessonId), new { }, other.Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Reading_lowers_the_unread_count_and_read_all_clears_it()
    {
        var s = await _k.Quizzes.SetupAsync(2);
        var student = await Lessons.StudentTokenAsync(s.Token, s.ClassId);
        await DiscoveryTestKit.Expect(Http.PostAsync<NotifyClassResultDto>(LessonUrl(s.LessonId), new { }, s.Token));
        await DiscoveryTestKit.Expect(Http.PostAsync<NotifyClassResultDto>(LessonUrl(s.LessonId), new { message = "Nhắc lại" }, s.Token));
        var page = await ListAsync(student);
        Assert.Equal(2, page.UnreadCount);

        await Http.PostAsync<object>($"{Notifications}/{page.Items[0].Id}/read", null, student);
        await Http.PostAsync<object>($"{Notifications}/{page.Items[0].Id}/read", null, student);
        Assert.Equal(1, (await ListAsync(student)).UnreadCount);

        await Http.PostAsync<object>($"{Notifications}/read-all", null, student);
        var after = await ListAsync(student);
        Assert.Equal(0, after.UnreadCount);
        Assert.All(after.Items, item => Assert.True(item.IsRead));
    }

    [Fact]
    public async Task A_student_only_sees_their_own_notifications()
    {
        var s = await _k.Quizzes.SetupAsync(2);
        var student = await Lessons.StudentTokenAsync(s.Token, s.ClassId);
        var other = await _k.Quizzes.SetupAsync(2);
        var stranger = await Lessons.StudentTokenAsync(other.Token, other.ClassId);
        await DiscoveryTestKit.Expect(Http.PostAsync<NotifyClassResultDto>(LessonUrl(s.LessonId), new { }, s.Token));
        var id = Assert.Single((await ListAsync(student)).Items).Id;

        Assert.Empty((await ListAsync(stranger)).Items);
        var (response, _) = await Http.PostAsync<object>($"{Notifications}/{id}/read", null, stranger);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null((await _k.Factory.WithDbAsync(db => db.StudentNotifications.AsNoTracking().SingleAsync(n => n.Id == id))).ReadAt);
    }

    [Fact]
    public async Task A_notification_about_a_lesson_that_is_no_longer_published_is_hidden()
    {
        var s = await _k.Quizzes.SetupAsync(2);
        var student = await Lessons.StudentTokenAsync(s.Token, s.ClassId);
        await DiscoveryTestKit.Expect(Http.PostAsync<NotifyClassResultDto>(LessonUrl(s.LessonId), new { }, s.Token));

        await _k.Factory.WithDbAsync(async db =>
        {
            var lesson = await db.Lessons.SingleAsync(l => l.Id == s.LessonId);
            lesson.Status = ContentStatus.ARCHIVED;
            await db.SaveChangesAsync();
            return 0;
        });

        var page = await ListAsync(student);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.UnreadCount);
    }
}
