using System.Net;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Giới hạn nhập sai mã bài học qua Api thật: 10 lần sai trong 10 phút thì bị chặn nhập mã, tính theo từng tài khoản học sinh. Đặt riêng một lớp kiểm
/// thử (và một Api riêng) vì bộ đếm theo IP dùng chung cho mọi học sinh của kiểm thử.
/// </summary>
public sealed class LessonCodeThrottleApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly LessonTestKit _k = new(factory);

    [Fact]
    public async Task Ten_wrong_codes_block_code_entry_for_that_student_only_and_a_right_code_is_refused_while_blocked()
    {
        var (school, schoolClass) = await _k.SchoolWithClassAsync(2);
        var h = await _k.HeritageAsync("Hoàng thành", events: 3);
        var lesson = await _k.PublishedLessonAsync(school.TeacherToken, schoolClass.Id, [h.Id]);
        var guesser = await _k.StudentTokenAsync(school.TeacherToken, schoolClass.Id, "Em hay đoán mã");
        var other = await _k.StudentTokenAsync(school.TeacherToken, schoolClass.Id, "Em bạn cùng lớp");

        // 9 lần sai đầu: cùng một thông báo 404; chưa bị chặn.
        for (var i = 0; i < 9; i++)
        {
            var (res, body) = await _k.OpenAsync(guesser, $"BH22222{(char)('A' + i)}");
            Assert.Equal((HttpStatusCode.NotFound, "LESSON_CODE_INVALID"), (res.StatusCode, body.ErrorCode));
        }

        // Lần sai thứ 10 làm khóa nhập mã bắt đầu: 429 kèm thời gian chờ.
        var (tenth, tenthBody) = await _k.OpenAsync(guesser, "BH22222Z");
        Assert.Equal((HttpStatusCode.TooManyRequests, "LESSON_CODE_BLOCKED"), (tenth.StatusCode, tenthBody.ErrorCode));
        Assert.Equal("Em đã nhập sai mã quá nhiều lần. Em hãy thử lại sau 10 phút.", tenthBody.Message);

        // Đang bị chặn: ngay cả mã đúng cũng chưa mở được; thông báo vẫn là chờ.
        var (right, rightBody) = await _k.OpenAsync(guesser, lesson.AccessCode!);
        Assert.Equal((HttpStatusCode.TooManyRequests, "LESSON_CODE_BLOCKED"), (right.StatusCode, rightBody.ErrorCode));
        Assert.Matches(@"thử lại sau (9|10) phút\.$", rightBody.Message);

        // Bạn cùng lớp (cùng IP của trường) chưa bị chặn.
        var (ok, okBody) = await _k.OpenAsync(other, lesson.AccessCode!);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(lesson.Id, okBody.Data!.Id);

        // Học sinh bị chặn nhập mã vẫn dùng được các chức năng khác của khu học (xem lớp của em).
        var (cls, _) = await _k.Http.GetAsync<object>("/api/v1/student/class", guesser);
        Assert.Equal(HttpStatusCode.OK, cls.StatusCode);
    }

    [Fact]
    public async Task A_right_code_does_not_reset_the_count_and_a_code_of_another_class_counts_as_wrong()
    {
        var (school, class1) = await _k.SchoolWithClassAsync(2, "Lớp A");
        var class2 = await _k.Classroom.CreateClassAsync(school.TeacherToken, "Lớp B", 8);
        var h = await _k.HeritageAsync("Hoàng thành", events: 3);
        var mine = await _k.PublishedLessonAsync(school.TeacherToken, class1.Id, [h.Id], "Bài lớp A");
        var theirs = await _k.PublishedLessonAsync(school.TeacherToken, class2.Id, [h.Id], "Bài lớp B");
        var student = await _k.StudentTokenAsync(school.TeacherToken, class1.Id);

        // Mã đúng của lớp khác tính là nhập sai; xen kẽ với mã đúng của lớp mình vẫn cộng dồn.
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await _k.OpenAsync(student, theirs.AccessCode!)).Response.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await _k.OpenAsync(student, mine.AccessCode!)).Response.StatusCode);
        }
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.NotFound, (await _k.OpenAsync(student, theirs.AccessCode!)).Response.StatusCode);

        // 9 lần sai rồi; lần sai thứ 10 làm khóa bắt đầu.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await _k.OpenAsync(student, theirs.AccessCode!)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await _k.OpenAsync(student, mine.AccessCode!)).Response.StatusCode);
    }
}
