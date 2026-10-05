using System.Net;
using System.Text.RegularExpressions;
using DSVHVN.Application.Lessons;
using DSVHVN.Domain.Billing;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Quizzes;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Giáo viên tạo, soạn, xuất bản, lưu trữ, xóa và nhân bản bài học cho lớp của mình qua Api thật: bài dành cho đúng một lớp, cấp của bài là
/// cấp của lớp, khung nội dung đổi theo cấp, điều kiện xuất bản, mã bài học sinh một lần, phạm vi theo giáo viên.
/// </summary>
public sealed class LessonAuthoringApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly LessonTestKit _k = new(factory);
    private HttpClient Http => _k.Http;
    private const string T = LessonTestKit.Teacher;

    private static void AssertFail(HttpResponseMessage res, Envelope<object> body, HttpStatusCode status, string code)
    {
        Assert.Equal((status, code), (res.StatusCode, body.ErrorCode));
        Assert.False(body.Success);
    }

    // ---- Tạo ----

    [Theory]
    [InlineData(1, 3, "Cấp 1", "STEP_BY_STEP")]
    [InlineData(2, 8, "Cấp 2", "FREE_STEPS")]
    [InlineData(3, 11, "Cấp 3", "DOCUMENT")]
    public async Task Creating_a_lesson_makes_a_draft_for_the_class_and_the_level_follows_the_class(int level, int grade, string levelName, string layout)
    {
        var (school, schoolClass) = await _k.SchoolWithClassAsync(level);
        var h1 = await _k.HeritageAsync("Hoàng thành");
        var h2 = await _k.HeritageAsync("Văn Miếu");

        var lesson = await _k.CreateLessonAsync(school.TeacherToken, schoolClass.Id, [h1.Id, h2.Id], "  Bài học về Thăng Long  ");

        Assert.Equal(("Bài học về Thăng Long", ContentStatus.DRAFT, null, null), (lesson.Title, lesson.Status, lesson.AccessCode, lesson.AccessPath));
        Assert.Equal((schoolClass.Id, grade, level, levelName), (lesson.ClassId, lesson.Grade, lesson.SchoolLevel, lesson.SchoolLevelName));
        Assert.Equal([h1.Id, h2.Id], lesson.Heritages.Select(h => h.Id).Order());
        Assert.Equal((layout, level), (lesson.Frame.Layout, lesson.Frame.Level));
        Assert.Empty(lesson.Sections);
        Assert.False(lesson.Readiness.IsReady);
        Assert.Contains("tóm tắt", lesson.Readiness.Missing);
        Assert.Equal((true, 20, 20, 0), (lesson.AiQuota.HasEffectivePlan, lesson.AiQuota.Max, lesson.AiQuota.Remaining, lesson.AiQuota.Used));
    }

    [Fact]
    public async Task Creating_a_lesson_validates_class_heritages_title_and_plan()
    {
        var (school, schoolClass) = await _k.SchoolWithClassAsync(2);
        var published = await _k.HeritageAsync("Hoàng thành");
        var draft = await _k.Discovery.DraftAsync(await _k.Discovery.Api.AdminTokenAsync(), new DiscoveryTestKit.Spec("Di sản nháp " + DiscoveryTestKit.Unique()));
        var token = school.TeacherToken;

        var (res, body) = await Http.PostAsync<object>(T, new { classId = schoolClass.Id, title = "", heritageIds = new[] { published.Id } }, token);
        AssertFail(res, body, HttpStatusCode.BadRequest, "REQUIRED");
        Assert.Equal("Vui lòng nhập tiêu đề bài học.", body.Errors!["title"]);

        (res, body) = await Http.PostAsync<object>(T, new { classId = schoolClass.Id, title = new string('x', 256), heritageIds = new[] { published.Id } }, token);
        AssertFail(res, body, HttpStatusCode.BadRequest, "INVALID");

        (res, body) = await Http.PostAsync<object>(T, new { classId = schoolClass.Id, title = "A", heritageIds = Array.Empty<long>() }, token);
        AssertFail(res, body, HttpStatusCode.BadRequest, "REQUIRED");
        Assert.True(body.Errors!.ContainsKey("heritageIds"));

        var eleven = Enumerable.Range(1, 11).Select(i => (long)i).ToArray();
        (res, body) = await Http.PostAsync<object>(T, new { classId = schoolClass.Id, title = "A", heritageIds = eleven }, token);
        AssertFail(res, body, HttpStatusCode.BadRequest, "INVALID");

        // Di sản chưa xuất bản hoặc không tồn tại: không chọn được.
        (res, body) = await Http.PostAsync<object>(T, new { classId = schoolClass.Id, title = "A", heritageIds = new[] { published.Id, draft.Id } }, token);
        AssertFail(res, body, HttpStatusCode.BadRequest, "INVALID");
        Assert.Contains("chỉ chọn được di sản đã xuất bản", body.Errors!["heritageIds"]);

        (res, body) = await Http.PostAsync<object>(T, new { classId = (long?)null, title = "A", heritageIds = new[] { published.Id } }, token);
        AssertFail(res, body, HttpStatusCode.BadRequest, "REQUIRED");

        // Lớp không phải của mình (kể cả cùng trường) hoặc không tồn tại: 404.
        var (other, otherToken) = await _k.Classroom.AnotherTeacherAsync(school);
        var otherClass = await _k.Classroom.CreateClassAsync(otherToken, "Lớp bạn", 8);
        (res, body) = await Http.PostAsync<object>(T, new { classId = otherClass.Id, title = "A", heritageIds = new[] { published.Id } }, token);
        AssertFail(res, body, HttpStatusCode.NotFound, "BLOCKED");
        (res, body) = await Http.PostAsync<object>(T, new { classId = 999_999, title = "A", heritageIds = new[] { published.Id } }, token);
        AssertFail(res, body, HttpStatusCode.NotFound, "BLOCKED");
        Assert.NotNull(other);
    }

    [Fact]
    public async Task Creating_and_duplicating_need_an_effective_plan_but_editing_publishing_and_archiving_do_not()
    {
        var (school, schoolClass) = await _k.SchoolWithClassAsync(2);
        var secondClass = await _k.Classroom.CreateClassAsync(school.TeacherToken, "Lớp hai", 9);
        var h = await _k.HeritageAsync("Hoàng thành", events: 3);
        var lesson = await _k.CreateLessonAsync(school.TeacherToken, schoolClass.Id, [h.Id]);
        await _k.SaveDraftAsync(school.TeacherToken, lesson.Id, await _k.ComposeAsync(school.TeacherToken, lesson.Id));

        await ExpirePlanAsync(school.OrgId);

        var (res, body) = await Http.PostAsync<object>(T, new { classId = schoolClass.Id, title = "Bài mới", heritageIds = new[] { h.Id } }, school.TeacherToken);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "PLAN_REQUIRED");
        Assert.StartsWith("Không thể tạo bài học.", body.Message);

        (res, body) = await Http.PostAsync<object>($"{T}/{lesson.Id}/duplicate", new { targetClassId = secondClass.Id }, school.TeacherToken);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "PLAN_REQUIRED");

        // Xem, sửa, xuất bản, lưu trữ bài đã có vẫn được.
        Assert.False((await _k.GetAsync(school.TeacherToken, lesson.Id)).AiQuota.HasEffectivePlan);
        var (edit, editBody) = await Http.PutAsync<object>($"{T}/{lesson.Id}", new { title = "Đổi tên khi hết gói" }, school.TeacherToken);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var published = await _k.PublishAsync(school.TeacherToken, lesson.Id);
        Assert.Equal(ContentStatus.PUBLISHED, published.Lesson.Status);
        var (arch, _) = await Http.PostAsync<LessonDetailDto>($"{T}/{lesson.Id}/archive", null, school.TeacherToken);
        Assert.Equal(HttpStatusCode.OK, arch.StatusCode);
        Assert.NotNull(editBody);
    }

    // ---- Phạm vi theo giáo viên ----

    [Fact]
    public async Task Another_teacher_the_org_admin_and_students_cannot_touch_the_lesson()
    {
        var (school, schoolClass) = await _k.SchoolWithClassAsync(2);
        var h = await _k.HeritageAsync("Hoàng thành");
        var lesson = await _k.CreateLessonAsync(school.TeacherToken, schoolClass.Id, [h.Id]);
        var (_, otherToken) = await _k.Classroom.AnotherTeacherAsync(school);

        foreach (var (method, url) in new[]
                 {
                     (HttpMethod.Get, $"{T}/{lesson.Id}"), (HttpMethod.Put, $"{T}/{lesson.Id}"), (HttpMethod.Delete, $"{T}/{lesson.Id}"),
                     (HttpMethod.Post, $"{T}/{lesson.Id}/publish"), (HttpMethod.Post, $"{T}/{lesson.Id}/archive"),
                     (HttpMethod.Post, $"{T}/{lesson.Id}/ai/compose"), (HttpMethod.Get, $"{T}/{lesson.Id}/materials"),
                 })
        {
            var (res, body) = await Http.SendJsonAsync<object>(method, url, method == HttpMethod.Get || method == HttpMethod.Delete ? null : new { }, otherToken);
            Assert.True(res.StatusCode == HttpStatusCode.NotFound, $"{method} {url}: {(int)res.StatusCode}");
            Assert.Equal("BLOCKED", body.ErrorCode);
        }

        var (dup, _) = await Http.PostAsync<object>($"{T}/{lesson.Id}/duplicate", new { targetClassId = schoolClass.Id }, otherToken);
        Assert.Equal(HttpStatusCode.NotFound, dup.StatusCode);

        // Danh sách của giáo viên khác không có bài này.
        var list = await DiscoveryTestKit.Expect(Http.GetAsync<LessonPageDto>(T, otherToken));
        Assert.Empty(list.Items);

        // Quản trị trường và học sinh nhận 403; chưa đăng nhập nhận 401.
        var (forbidden, _) = await Http.GetAsync<object>($"{T}/{lesson.Id}", school.OrgAdminToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var studentToken = await _k.StudentTokenAsync(school.TeacherToken, schoolClass.Id);
        var (studentRes, _) = await Http.GetAsync<object>(T, studentToken);
        Assert.Equal(HttpStatusCode.Forbidden, studentRes.StatusCode);
        var (anonymous, _) = await Http.GetAsync<object>(T);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    // ---- Khung nội dung theo cấp ----

    [Fact]
    public async Task Saving_checks_section_types_against_the_level_images_against_the_library_and_required_fields_per_type()
    {
        var (school, schoolClass) = await _k.SchoolWithClassAsync(1);
        var h = await _k.HeritageAsync("Hoàng thành", events: 3);
        var lesson = await _k.CreateLessonAsync(school.TeacherToken, schoolClass.Id, [h.Id]);
        var materials = await DiscoveryTestKit.Expect(Http.GetAsync<LessonMaterialsDto>($"{T}/{lesson.Id}/materials", school.TeacherToken));
        var imageUrl = materials.Images.First().Url;
        Assert.Equal(3, materials.Events.Count);
        Assert.Contains(materials.Images, i => i.Credit is not null && i.Author is not null && i.License is not null);

        async Task<(HttpResponseMessage, Envelope<object>)> Save(params object[] sections) =>
            await Http.PutAsync<object>($"{T}/{lesson.Id}", new { sections }, school.TeacherToken);

        // Cấp 1 không có bài đọc, mốc thời gian, trích tư liệu, câu hỏi gợi suy nghĩ.
        foreach (var type in new[] { "READING", "MILESTONE", "SOURCE_EXCERPT", "REFLECTION" })
        {
            var (res, body) = await Save(new { type, body = "Nội dung", milestoneYear = 1010, sourceCitation = "Nguồn" });
            AssertFail(res, body, HttpStatusCode.BadRequest, "INVALID");
            Assert.Contains("không dùng được ở bài của lớp cấp 1", body.Errors!["sections[0].type"]);
        }

        // Hình phải nằm trong thư viện ảnh của các di sản của bài.
        var (bad, badBody) = await Save(new { type = "STORY", body = "Câu chuyện", imageUrl = "https://example.org/ngoai-thu-vien.jpg" });
        AssertFail(bad, badBody, HttpStatusCode.BadRequest, "INVALID");
        Assert.Contains("thư viện ảnh của các di sản của bài", badBody.Errors!["sections[0].imageUrl"]);

        // Trường bắt buộc theo loại phần.
        var (noBody, noBodyBody) = await Save(new { type = "STORY", body = " ", imageUrl });
        Assert.Equal(("REQUIRED", HttpStatusCode.BadRequest), (noBodyBody.ErrorCode, noBody.StatusCode));
        Assert.True(noBodyBody.Errors!.ContainsKey("sections[0].body"));
        var (noImage, noImageBody) = await Save(new { type = "IMAGE", body = "Chú thích" });
        Assert.True(noImageBody.Errors!.ContainsKey("sections[0].imageUrl"));
        Assert.Equal(HttpStatusCode.BadRequest, noImage.StatusCode);

        // Lưu đúng khung: các phần giữ thứ tự gửi, hình có ghi công, khối "Cấp 1" nhận cả bước thiếu hình (chỉ chặn lúc xuất bản).
        var (ok, okBody) = await Http.PutAsync<LessonDetailDto>($"{T}/{lesson.Id}", new
        {
            summary = "Em cùng khám phá Hoàng thành.",
            keyPoints = new[] { "Hoàng thành rất cổ.", "  ", },
            sections = new object[]
            {
                new { type = "STORY", title = "Bước 1", body = "Ngày xưa có một tòa thành.", imageUrl },
                new { type = "STORY", body = "Tòa thành rất đẹp." },
                new { type = "IMAGE", body = "Cổng thành", imageUrl },
            },
        }, school.TeacherToken);
        Assert.Equal(HttpStatusCode.BadRequest, ok.StatusCode); // ý chính rỗng bị từ chối
        Assert.True(okBody.Errors!.ContainsKey("keyPoints[1]"));

        var saved = await DiscoveryTestKit.Expect(Http.PutAsync<LessonDetailDto>($"{T}/{lesson.Id}", new
        {
            summary = "Em cùng khám phá Hoàng thành.",
            keyPoints = new[] { "Hoàng thành rất cổ." },
            sections = new object[]
            {
                new { type = "STORY", title = "Bước 1", body = "Ngày xưa có một tòa thành.", imageUrl },
                new { type = "STORY", body = "Tòa thành rất đẹp." },
                new { type = "IMAGE", body = "Cổng thành", imageUrl },
            },
        }, school.TeacherToken));
        Assert.Equal([LessonSectionType.STORY, LessonSectionType.STORY, LessonSectionType.IMAGE], saved.Sections.Select(s => s.Type));
        Assert.Equal([1, 2, 3], saved.Sections.Select(s => s.SortOrder));
        Assert.NotNull(saved.Sections[0].Image?.Credit);
        Assert.Null(saved.Sections[1].ImageUrl);
        Assert.False(saved.Readiness.IsReady);
        Assert.Contains("hình cho mỗi bước câu chuyện", saved.Readiness.Missing);
        Assert.Contains(saved.Readiness.Warnings, w => w.Contains("từ; bài của lớp cấp 1 thường dài 150-250 từ"));
        Assert.Contains(saved.Readiness.Warnings, w => w.Contains("Câu chuyện có 2 bước"));
    }

    [Fact]
    public async Task Level_two_and_three_frames_check_milestones_events_excerpts_and_sources()
    {
        var h = await _k.HeritageAsync("Hoàng thành", events: 3);

        // Cấp 2: mốc thời gian nối được với sự kiện của di sản của bài, năm bắt buộc.
        var (school2, class2) = await _k.SchoolWithClassAsync(2);
        var lesson2 = await _k.CreateLessonAsync(school2.TeacherToken, class2.Id, [h.Id]);
        var materials = await DiscoveryTestKit.Expect(Http.GetAsync<LessonMaterialsDto>($"{T}/{lesson2.Id}/materials", school2.TeacherToken));
        var evt = materials.Events.First();

        var (noYear, noYearBody) = await Http.PutAsync<object>($"{T}/{lesson2.Id}",
            new { sections = new[] { new { type = "MILESTONE", body = "Mốc", eventId = (long?)evt.EventId } } }, school2.TeacherToken);
        Assert.Equal((HttpStatusCode.BadRequest, true), (noYear.StatusCode, noYearBody.Errors!.ContainsKey("sections[0].milestoneYear")));
        var (foreign, foreignBody) = await Http.PutAsync<object>($"{T}/{lesson2.Id}",
            new { sections = new[] { new { type = "MILESTONE", body = "Mốc", milestoneYear = 1010, eventId = (long?)987_654 } } }, school2.TeacherToken);
        Assert.Equal((HttpStatusCode.BadRequest, true), (foreign.StatusCode, foreignBody.Errors!.ContainsKey("sections[0].eventId")));
        var (future, _) = await Http.PutAsync<object>($"{T}/{lesson2.Id}",
            new { sections = new[] { new { type = "MILESTONE", body = "Mốc", milestoneYear = 3000 } } }, school2.TeacherToken);
        Assert.Equal(HttpStatusCode.BadRequest, future.StatusCode);

        var saved2 = await DiscoveryTestKit.Expect(Http.PutAsync<LessonDetailDto>($"{T}/{lesson2.Id}", new
        {
            sections = new object[]
            {
                new { type = "MILESTONE", title = "Mốc đầu", body = "Dời đô ra Thăng Long.", milestoneYear = 1010, eventId = (long?)evt.EventId },
                new { type = "MILESTONE", body = "Trước Công nguyên.", milestoneYear = -179 },
            },
        }, school2.TeacherToken));
        Assert.Equal(("1010", evt.EventId, "179 TCN"), (saved2.Sections[0].MilestoneLabel, saved2.Sections[0].EventId, saved2.Sections[1].MilestoneLabel));
        Assert.Contains(saved2.Readiness.Warnings, w => w.Contains("Bài có 2 mốc thời gian; cấp 2 thường có 3-8 mốc."));
        Assert.DoesNotContain("ít nhất một mốc thời gian", saved2.Readiness.Missing);

        // Cấp 3: đoạn trích bắt buộc có nguồn; câu hỏi gợi suy nghĩ; không có hình hay mốc... mốc thì có.
        var (school3, class3) = await _k.SchoolWithClassAsync(3);
        var lesson3 = await _k.CreateLessonAsync(school3.TeacherToken, class3.Id, [h.Id]);
        var (noSource, noSourceBody) = await Http.PutAsync<object>($"{T}/{lesson3.Id}",
            new { sections = new[] { new { type = "SOURCE_EXCERPT", body = "Trích nguyên văn." } } }, school3.TeacherToken);
        Assert.Equal((HttpStatusCode.BadRequest, true), (noSource.StatusCode, noSourceBody.Errors!.ContainsKey("sections[0].sourceCitation")));
        var (image3, image3Body) = await Http.PutAsync<object>($"{T}/{lesson3.Id}",
            new { sections = new[] { new { type = "IMAGE", body = "Hình", imageUrl = "https://example.org/x.jpg" } } }, school3.TeacherToken);
        Assert.Equal(HttpStatusCode.BadRequest, image3.StatusCode);
        Assert.Contains("không dùng được ở bài của lớp cấp 3", image3Body.Errors!["sections[0].type"]);

        var saved3 = await DiscoveryTestKit.Expect(Http.PutAsync<LessonDetailDto>($"{T}/{lesson3.Id}", new
        {
            sections = new object[]
            {
                new { type = "SOURCE_EXCERPT", body = "Trích nguyên văn.", sourceCitation = "Quyết định xếp hạng, truy cập 26/09/2026" },
                new { type = "REFLECTION", body = "Theo em vì sao cần gìn giữ?" },
            },
        }, school3.TeacherToken));
        Assert.Equal(["Quyết định xếp hạng, truy cập 26/09/2026", null], saved3.Sections.Select(s => s.SourceCitation));
        Assert.DoesNotContain("ít nhất một đoạn trích tư liệu gốc", saved3.Readiness.Missing);
    }

    // ---- Xuất bản và mã bài học ----

    [Fact]
    public async Task Publishing_needs_the_full_frame_then_creates_one_lesson_code_that_never_changes()
    {
        var (school, schoolClass) = await _k.SchoolWithClassAsync(2);
        var h = await _k.HeritageAsync("Hoàng thành", events: 4);
        var token = school.TeacherToken;
        var lesson = await _k.CreateLessonAsync(token, schoolClass.Id, [h.Id], "Thăng Long");

        // Chưa đủ điều kiện: liệt kê phần còn thiếu, chưa có mã.
        var (res, body) = await Http.PostAsync<object>($"{T}/{lesson.Id}/publish", null, token);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "NOT_READY");
        Assert.StartsWith("Chưa đủ điều kiện xuất bản bài học. Còn thiếu: tóm tắt, ", body.Message);
        Assert.Contains("ít nhất một mốc thời gian", body.Message);
        Assert.Null((await _k.GetAsync(token, lesson.Id)).AccessCode);

        var draft = await _k.ComposeAsync(token, lesson.Id);
        await _k.SaveDraftAsync(token, lesson.Id, draft);

        var published = await _k.PublishAsync(token, lesson.Id);
        var code = published.AccessCode;
        Assert.Matches(new Regex("^BH[2-9A-HJ-NP-Z]{6}$"), code);
        Assert.True(published.CodeCreated);
        Assert.Equal(ContentStatus.PUBLISHED, published.Lesson.Status);
        Assert.Equal($"/bai-hoc/{code}", published.Lesson.AccessPath);
        Assert.Equal($"Đã xuất bản bài học. Mã bài học: {code}. Hãy đưa mã hoặc đường dẫn cho học sinh lớp {schoolClass.Name}.", published.Message);

        // Xuất bản lại bài đã xuất bản không đổi mã; sửa bài giữ mã; lưu trữ rồi xuất bản lại vẫn mã cũ.
        var again = await _k.PublishAsync(token, lesson.Id);
        Assert.Equal((code, false), (again.AccessCode, again.CodeCreated));
        var edited = await DiscoveryTestKit.Expect(Http.PutAsync<LessonDetailDto>($"{T}/{lesson.Id}", new { title = "Thăng Long (sửa)" }, token));
        Assert.Equal((code, ContentStatus.PUBLISHED, "Thăng Long (sửa)"), (edited.AccessCode, edited.Status, edited.Title));
        var archived = await DiscoveryTestKit.Expect(Http.PostAsync<LessonDetailDto>($"{T}/{lesson.Id}/archive", null, token));
        Assert.Equal((ContentStatus.ARCHIVED, code), (archived.Status, archived.AccessCode));
        var republished = await _k.PublishAsync(token, lesson.Id);
        Assert.Equal((code, false, ContentStatus.PUBLISHED), (republished.AccessCode, republished.CodeCreated, republished.Lesson.Status));

        // Hai bài khác nhau có hai mã khác nhau.
        var second = await _k.PublishedLessonAsync(token, schoolClass.Id, [h.Id], "Bài thứ hai");
        Assert.NotEqual(code, second.AccessCode);
    }

    [Fact]
    public async Task A_published_lesson_must_stay_publishable_and_an_archived_lesson_cannot_be_edited()
    {
        var (school, schoolClass) = await _k.SchoolWithClassAsync(3);
        var h = await _k.HeritageAsync("Hoàng thành");
        var token = school.TeacherToken;
        var lesson = await _k.PublishedLessonAsync(token, schoolClass.Id, [h.Id]);
        Assert.NotEmpty(lesson.Sections);

        // Xóa hết đoạn trích khỏi bài đã xuất bản: bị từ chối, không lưu gì.
        var onlyReflections = lesson.Sections.Where(s => s.Type == LessonSectionType.REFLECTION).Select(LessonTestKit.ToSection).ToList();
        var (res, body) = await Http.PutAsync<object>($"{T}/{lesson.Id}", new { title = "Đổi tên không lưu", sections = onlyReflections }, token);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "NOT_READY");
        Assert.Contains("ít nhất một đoạn trích tư liệu gốc", body.Message);
        var unchanged = await _k.GetAsync(token, lesson.Id);
        Assert.Equal((lesson.Title, lesson.Sections.Count), (unchanged.Title, unchanged.Sections.Count));

        // Xóa tóm tắt: cũng bị từ chối.
        var (res2, body2) = await Http.PutAsync<object>($"{T}/{lesson.Id}", new { summary = "   " }, token);
        AssertFail(res2, body2, HttpStatusCode.UnprocessableEntity, "NOT_READY");

        // Bài lưu trữ không sửa được cho tới khi xuất bản lại.
        await DiscoveryTestKit.Expect(Http.PostAsync<LessonDetailDto>($"{T}/{lesson.Id}/archive", null, token));
        var (res3, body3) = await Http.PutAsync<object>($"{T}/{lesson.Id}", new { title = "Sửa khi lưu trữ" }, token);
        AssertFail(res3, body3, HttpStatusCode.UnprocessableEntity, "BLOCKED");
        Assert.Contains("Bài học đã lưu trữ", body3.Message);
    }

    [Fact]
    public async Task Archiving_needs_a_published_lesson_and_no_open_quiz_and_deleting_needs_no_quiz()
    {
        var (school, schoolClass) = await _k.SchoolWithClassAsync(2);
        var h = await _k.HeritageAsync("Hoàng thành", events: 3);
        var token = school.TeacherToken;

        // Bài nháp: xóa được, không lưu trữ.
        var draft = await _k.CreateLessonAsync(token, schoolClass.Id, [h.Id]);
        var (archDraft, archDraftBody) = await Http.PostAsync<object>($"{T}/{draft.Id}/archive", null, token);
        AssertFail(archDraft, archDraftBody, HttpStatusCode.UnprocessableEntity, "BLOCKED");
        var (del, delBody) = await Http.DeleteAsync<object>($"{T}/{draft.Id}", token);
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Equal("Xóa bài học thành công.", delBody.Message);
        var (gone, _) = await Http.GetAsync<object>($"{T}/{draft.Id}", token);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

        // Bài có bài kiểm tra: không xóa được, chỉ lưu trữ; bài kiểm tra đang mở thì chưa lưu trữ được.
        var lesson = await _k.PublishedLessonAsync(token, schoolClass.Id, [h.Id]);
        var quiz = await _k.AddQuizAsync(lesson.Id, QuizStatus.PUBLISHED, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddDays(1));
        var (delQuiz, delQuizBody) = await Http.DeleteAsync<object>($"{T}/{lesson.Id}", token);
        AssertFail(delQuiz, delQuizBody, HttpStatusCode.UnprocessableEntity, "BLOCKED");
        Assert.Contains("lưu trữ bài học thay vì xóa", delQuizBody.Message);
        var (archOpen, archOpenBody) = await Http.PostAsync<object>($"{T}/{lesson.Id}/archive", null, token);
        AssertFail(archOpen, archOpenBody, HttpStatusCode.UnprocessableEntity, "BLOCKED");
        Assert.Contains("bài kiểm tra đang mở", archOpenBody.Message);

        // Đóng bài kiểm tra rồi lưu trữ được.
        await factory.WithDbAsync(async db =>
        {
            var q = await db.Quizzes.SingleAsync(x => x.Id == quiz);
            q.Status = QuizStatus.CLOSED;
            return await db.SaveChangesAsync();
        });
        var archived = await DiscoveryTestKit.Expect(Http.PostAsync<LessonDetailDto>($"{T}/{lesson.Id}/archive", null, token));
        Assert.Equal((ContentStatus.ARCHIVED, 1), (archived.Status, archived.QuizCount));
    }

    // ---- Nhân bản ----

    [Fact]
    public async Task Duplicating_copies_content_into_a_draft_for_another_class_without_a_code()
    {
        var (school, class8) = await _k.SchoolWithClassAsync(2, "8A1");
        var token = school.TeacherToken;
        var class9 = await _k.Classroom.CreateClassAsync(token, "9A1", 9);
        var class11 = await _k.Classroom.CreateClassAsync(token, "11A1", 11);
        var h1 = await _k.HeritageAsync("Hoàng thành", events: 3);
        var h2 = await _k.HeritageAsync("Văn Miếu");
        var source = await _k.PublishedLessonAsync(token, class8.Id, [h1.Id, h2.Id], "Bài gốc");
        Assert.NotNull(source.AccessCode);

        // Cùng lớp thì không; lớp chưa chọn thì không.
        var (same, sameBody) = await Http.PostAsync<object>($"{T}/{source.Id}/duplicate", new { targetClassId = class8.Id }, token);
        AssertFail(same, sameBody, HttpStatusCode.BadRequest, "INVALID");
        var (none, noneBody) = await Http.PostAsync<object>($"{T}/{source.Id}/duplicate", new { }, token);
        AssertFail(none, noneBody, HttpStatusCode.BadRequest, "REQUIRED");

        // Cùng cấp: chép nguyên, không lời nhắc.
        var quotaBefore = (await _k.GetAsync(token, source.Id)).AiQuota.Used;
        var res = await DiscoveryTestKit.Expect(Http.PostAsync<DuplicateLessonResultDto>($"{T}/{source.Id}/duplicate", new { targetClassId = class9.Id }, token),
            HttpStatusCode.Created);
        var copy = res.Lesson;
        Assert.Equal(($"Đã tạo bài học nháp ở lớp 9A1.", null, 0), (res.Message, res.LevelNotice, res.SkippedHeritages));
        Assert.Equal((ContentStatus.DRAFT, null, class9.Id, source.Title, source.Summary), (copy.Status, copy.AccessCode, copy.ClassId, copy.Title, copy.Summary));
        Assert.Equal(source.KeyPoints, copy.KeyPoints);
        Assert.Equal(source.Sections.Select(s => (s.Type, s.Body, s.ImageUrl, s.MilestoneYear, s.EventId)),
            copy.Sections.Select(s => (s.Type, s.Body, s.ImageUrl, s.MilestoneYear, s.EventId)));
        Assert.Equal(source.Heritages.Select(h => h.Id).Order(), copy.Heritages.Select(h => h.Id).Order());
        Assert.Equal(quotaBefore, (await _k.GetAsync(token, source.Id)).AiQuota.Used); // nhân bản không tốn lượt AI

        // Bài gốc không đổi; bản chép xuất bản có mã riêng.
        var original = await _k.GetAsync(token, source.Id);
        Assert.Equal((source.AccessCode, ContentStatus.PUBLISHED), (original.AccessCode, original.Status));
        var published = await _k.PublishAsync(token, copy.Id);
        Assert.NotEqual(source.AccessCode, published.AccessCode);

        // Lớp đích khác cấp: vẫn chép nguyên, kèm lời nhắc.
        var cross = await DiscoveryTestKit.Expect(Http.PostAsync<DuplicateLessonResultDto>($"{T}/{source.Id}/duplicate", new { targetClassId = class11.Id }, token),
            HttpStatusCode.Created);
        Assert.Equal("Lớp 11A1 thuộc cấp 3, khác cấp 2 của bài gốc. Nội dung đang theo khung của cấp 2; bạn nên xem lại hoặc nhờ AI soạn lại.", cross.LevelNotice);
        Assert.Equal(source.Sections.Count, cross.Lesson.Sections.Count);
        Assert.Equal(3, cross.Lesson.SchoolLevel);
        // Các phần cấp 2 không thuộc khung cấp 3 nên bài nháp ở lớp cấp 3 cảnh báo; chưa đủ điều kiện xuất bản.
        Assert.Contains(cross.Lesson.Readiness.Warnings, w => w.Contains("thuộc khung của cấp khác"));
        Assert.False(cross.Lesson.Readiness.IsReady);
    }

    [Fact]
    public async Task Duplicating_drops_hidden_heritages_and_the_images_and_events_that_came_from_them()
    {
        var (school, class8) = await _k.SchoolWithClassAsync(2, "8A1");
        var token = school.TeacherToken;
        var class9 = await _k.Classroom.CreateClassAsync(token, "9A1", 9);
        var kept = await _k.HeritageAsync("Hoàng thành", events: 3);
        var hidden = await _k.HeritageAsync("Văn Miếu", events: 3);
        var lesson = await _k.CreateLessonAsync(token, class8.Id, [kept.Id, hidden.Id]);
        var materials = await DiscoveryTestKit.Expect(Http.GetAsync<LessonMaterialsDto>($"{T}/{lesson.Id}/materials", token));
        var keptImage = materials.Images.First(i => i.HeritageId == kept.Id).Url;
        var hiddenImage = materials.Images.First(i => i.HeritageId == hidden.Id).Url;
        var hiddenEvent = materials.Events.First(e => e.HeritageId == hidden.Id).EventId;
        await DiscoveryTestKit.Expect(Http.PutAsync<LessonDetailDto>($"{T}/{lesson.Id}", new
        {
            summary = "Tóm tắt.",
            keyPoints = new[] { "Ý chính." },
            sections = new object[]
            {
                new { type = "IMAGE", body = "Hình của di sản giữ lại", imageUrl = keptImage },
                new { type = "IMAGE", body = "Hình của di sản sẽ ẩn", imageUrl = hiddenImage },
                new { type = "MILESTONE", body = "Mốc của di sản sẽ ẩn", milestoneYear = 1010, eventId = (long?)hiddenEvent },
            },
        }, token));

        await _k.ArchiveHeritageAsync(hidden.Id);

        var res = await DiscoveryTestKit.Expect(Http.PostAsync<DuplicateLessonResultDto>($"{T}/{lesson.Id}/duplicate", new { targetClassId = class9.Id }, token),
            HttpStatusCode.Created);
        Assert.Equal(1, res.SkippedHeritages);
        Assert.Equal("Đã tạo bài học nháp ở lớp 9A1. Bỏ qua 1 di sản đã ẩn.", res.Message);
        Assert.Equal([kept.Id], res.Lesson.Heritages.Select(h => h.Id));
        // Hình của di sản đã ẩn bị bỏ; mốc giữ năm và nội dung nhưng gỡ liên kết với sự kiện của di sản đã ẩn.
        Assert.Equal([LessonSectionType.IMAGE, LessonSectionType.MILESTONE], res.Lesson.Sections.Select(s => s.Type));
        Assert.Equal(keptImage, res.Lesson.Sections[0].ImageUrl);
        Assert.Equal((1010, (long?)null), (res.Lesson.Sections[1].MilestoneYear, res.Lesson.Sections[1].EventId));
    }

    // ---- Danh sách ----

    [Fact]
    public async Task The_list_filters_by_class_status_heritage_type_and_title_and_shows_the_code_only_when_published()
    {
        var (school, class8) = await _k.SchoolWithClassAsync(2, "8A1");
        var token = school.TeacherToken;
        var class10 = await _k.Classroom.CreateClassAsync(token, "10A1", 10);
        var h = await _k.HeritageAsync("Hoàng thành", events: 3);
        var draft = await _k.CreateLessonAsync(token, class8.Id, [h.Id], "Bài nháp về sông");
        var published = await _k.PublishedLessonAsync(token, class10.Id, [h.Id], "Bài đã xuất bản về thành");

        var all = await DiscoveryTestKit.Expect(Http.GetAsync<LessonPageDto>(T, token));
        Assert.Equal(2, all.TotalItems);
        Assert.Equal(new[] { draft.Id, published.Id }.Order(), all.Items.Select(i => i.Id).Order());
        var rowDraft = all.Items.Single(i => i.Id == draft.Id);
        var rowPublished = all.Items.Single(i => i.Id == published.Id);
        Assert.Null(rowDraft.AccessCode);
        Assert.Equal(published.AccessCode, rowPublished.AccessCode);
        Assert.Equal(("8A1", 2, 0), (rowDraft.ClassName, rowDraft.SchoolLevel, rowDraft.QuizCount));
        Assert.Equal(3, rowPublished.SchoolLevel);
        Assert.Equal([h.Id], rowDraft.Heritages.Select(x => x.Id));

        Assert.Equal([draft.Id], (await DiscoveryTestKit.Expect(Http.GetAsync<LessonPageDto>($"{T}?classId={class8.Id}", token))).Items.Select(i => i.Id));
        Assert.Equal([published.Id], (await DiscoveryTestKit.Expect(Http.GetAsync<LessonPageDto>($"{T}?status=PUBLISHED", token))).Items.Select(i => i.Id));
        Assert.Equal([draft.Id], (await DiscoveryTestKit.Expect(Http.GetAsync<LessonPageDto>($"{T}?keyword=sông", token))).Items.Select(i => i.Id));
        Assert.Equal(2, (await DiscoveryTestKit.Expect(Http.GetAsync<LessonPageDto>($"{T}?heritageType=TANGIBLE", token))).TotalItems);
        Assert.Equal(0, (await DiscoveryTestKit.Expect(Http.GetAsync<LessonPageDto>($"{T}?heritageType=NATURAL", token))).TotalItems);
        var (bad, _) = await Http.GetAsync<object>($"{T}?pageSize=1000", token);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    // ---- Di sản của bài ----

    [Fact]
    public async Task Heritages_change_only_while_the_lesson_is_a_draft_and_a_hidden_heritage_no_longer_counts()
    {
        var (school, schoolClass) = await _k.SchoolWithClassAsync(2);
        var token = school.TeacherToken;
        var h1 = await _k.HeritageAsync("Hoàng thành", events: 3);
        var h2 = await _k.HeritageAsync("Văn Miếu");
        var lesson = await _k.CreateLessonAsync(token, schoolClass.Id, [h1.Id]);

        var swapped = await DiscoveryTestKit.Expect(Http.PutAsync<LessonDetailDto>($"{T}/{lesson.Id}", new { heritageIds = new[] { h1.Id, h2.Id } }, token));
        Assert.Equal(new[] { h1.Id, h2.Id }.Order(), swapped.Heritages.Select(h => h.Id).Order());
        var only2 = await DiscoveryTestKit.Expect(Http.PutAsync<LessonDetailDto>($"{T}/{lesson.Id}", new { heritageIds = new[] { h2.Id } }, token));
        Assert.Equal([h2.Id], only2.Heritages.Select(h => h.Id));

        await DiscoveryTestKit.Expect(Http.PutAsync<LessonDetailDto>($"{T}/{lesson.Id}", new { heritageIds = new[] { h1.Id } }, token));
        await _k.SaveDraftAsync(token, lesson.Id, await _k.ComposeAsync(token, lesson.Id));
        await _k.PublishAsync(token, lesson.Id);

        // Bài đã xuất bản: không đổi di sản.
        var (res, body) = await Http.PutAsync<object>($"{T}/{lesson.Id}", new { heritageIds = new[] { h2.Id } }, token);
        AssertFail(res, body, HttpStatusCode.UnprocessableEntity, "BLOCKED");

        // Di sản bị ẩn: bài giữ nguyên, nhưng không còn tính là di sản đã xuất bản (thẻ trống, AI từ chối), và lưu bài đã xuất bản bị chặn.
        await _k.ArchiveHeritageAsync(h1.Id);
        var detail = await _k.GetAsync(token, lesson.Id);
        Assert.Equal(ContentStatus.PUBLISHED, detail.Status);
        Assert.Equal(ContentStatus.ARCHIVED, detail.Heritages.Single().Status);
        Assert.Contains("ít nhất một di sản đã xuất bản", detail.Readiness.Missing);
        var (ai, aiBody) = await Http.PostAsync<object>($"{T}/{lesson.Id}/ai/compose", new { }, token);
        AssertFail(ai, aiBody, HttpStatusCode.UnprocessableEntity, "BLOCKED");
        Assert.Contains("ít nhất một di sản đã xuất bản", aiBody.Message);
    }

    [Fact]
    public async Task Removing_a_heritage_from_a_draft_clears_the_pictures_and_events_that_came_from_it()
    {
        var (school, schoolClass) = await _k.SchoolWithClassAsync(2);
        var token = school.TeacherToken;
        var keep = await _k.HeritageAsync("Hoàng thành", events: 3);
        var drop = await _k.HeritageAsync("Văn Miếu", events: 3);
        var lesson = await _k.CreateLessonAsync(token, schoolClass.Id, [keep.Id, drop.Id]);
        var materials = await DiscoveryTestKit.Expect(Http.GetAsync<LessonMaterialsDto>($"{T}/{lesson.Id}/materials", token));
        var keepImage = materials.Images.First(i => i.HeritageId == keep.Id).Url;
        var dropImage = materials.Images.First(i => i.HeritageId == drop.Id).Url;
        var dropEvent = materials.Events.First(e => e.HeritageId == drop.Id).EventId;
        await DiscoveryTestKit.Expect(Http.PutAsync<LessonDetailDto>($"{T}/{lesson.Id}", new
        {
            sections = new object[]
            {
                new { type = "IMAGE", body = "Hình giữ", imageUrl = keepImage },
                new { type = "MILESTONE", body = "Mốc của di sản bị bớt", milestoneYear = 1010, eventId = (long?)dropEvent },
            },
        }, token));

        // Gửi đồng thời các phần tham chiếu hình của di sản vừa bớt: kiểm theo tập di sản mới nên bị từ chối.
        var (bad, badBody) = await Http.PutAsync<object>($"{T}/{lesson.Id}", new
        {
            heritageIds = new[] { keep.Id },
            sections = new[] { new { type = "IMAGE", body = "Hình bị bớt", imageUrl = dropImage } },
        }, token);
        AssertFail(bad, badBody, HttpStatusCode.BadRequest, "INVALID");
        Assert.Equal(2, (await _k.GetAsync(token, lesson.Id)).Heritages.Count);             // không lưu gì

        // Chỉ bớt di sản: các phần giữ lại, hình thuộc di sản bị bớt bị gỡ, mốc gỡ liên kết sự kiện.
        var after = await DiscoveryTestKit.Expect(Http.PutAsync<LessonDetailDto>($"{T}/{lesson.Id}", new { heritageIds = new[] { keep.Id } }, token));
        Assert.Equal([keep.Id], after.Heritages.Select(h => h.Id));
        Assert.Equal(2, after.Sections.Count);
        Assert.Equal(keepImage, after.Sections[0].ImageUrl);
        Assert.Equal((1010, (long?)null), (after.Sections[1].MilestoneYear, after.Sections[1].EventId));
    }

    [Fact]
    public async Task Saving_with_only_some_fields_keeps_the_others_and_blank_summaries_are_cleared()
    {
        var (school, schoolClass) = await _k.SchoolWithClassAsync(2);
        var token = school.TeacherToken;
        var h = await _k.HeritageAsync("Hoàng thành", events: 3);
        var lesson = await _k.CreateLessonAsync(token, schoolClass.Id, [h.Id], "Tên đầu");
        await DiscoveryTestKit.Expect(Http.PutAsync<LessonDetailDto>($"{T}/{lesson.Id}", new
        {
            summary = "  Tóm tắt có khoảng trắng.  ",
            keyPoints = new[] { "  Ý một  ", "Ý hai" },
            sections = new[] { new { type = "MILESTONE", body = "Mốc", milestoneYear = 1010 } },
        }, token));

        var renamed = await DiscoveryTestKit.Expect(Http.PutAsync<LessonDetailDto>($"{T}/{lesson.Id}", new { title = "  Tên mới  " }, token));
        Assert.Equal(("Tên mới", "Tóm tắt có khoảng trắng.", 1), (renamed.Title, renamed.Summary, renamed.Sections.Count));
        Assert.Equal(["Ý một", "Ý hai"], renamed.KeyPoints);

        var cleared = await DiscoveryTestKit.Expect(Http.PutAsync<LessonDetailDto>($"{T}/{lesson.Id}", new { summary = " ", keyPoints = Array.Empty<string>(), sections = Array.Empty<object>() }, token));
        Assert.Equal((null, 0, 0), (cleared.Summary, cleared.KeyPoints.Count, cleared.Sections.Count));

        var (empty, emptyBody) = await Http.PutAsync<object>($"{T}/{lesson.Id}", new { title = " " }, token);
        AssertFail(empty, emptyBody, HttpStatusCode.BadRequest, "REQUIRED");
        var (many, _) = await Http.PutAsync<object>($"{T}/{lesson.Id}", new { keyPoints = Enumerable.Repeat("ý", 11).ToArray() }, token);
        Assert.Equal(HttpStatusCode.BadRequest, many.StatusCode);
    }

    // ---- Dây chuyền với admin sửa dòng thời gian ----

    [Fact]
    public async Task Deleting_a_timeline_event_keeps_the_milestone_but_drops_its_link()
    {
        var (school, schoolClass) = await _k.SchoolWithClassAsync(2);
        var token = school.TeacherToken;
        var h = await _k.HeritageAsync("Hoàng thành", events: 3);
        var lesson = await _k.CreateLessonAsync(token, schoolClass.Id, [h.Id]);
        var materials = await DiscoveryTestKit.Expect(Http.GetAsync<LessonMaterialsDto>($"{T}/{lesson.Id}/materials", token));
        var evt = materials.Events.First();
        await DiscoveryTestKit.Expect(Http.PutAsync<LessonDetailDto>($"{T}/{lesson.Id}", new
        {
            sections = new[] { new { type = "MILESTONE", body = "Dời đô.", milestoneYear = 1010, eventId = (long?)evt.EventId } },
        }, token));

        var admin = await _k.Discovery.Api.AdminTokenAsync();
        var (del, _) = await Http.DeleteAsync<object>($"{DiscoveryTestKit.Admin}/events/{evt.EventId}", admin);
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var after = await _k.GetAsync(token, lesson.Id);
        Assert.Equal((1010, "Dời đô.", (long?)null), (after.Sections.Single().MilestoneYear, after.Sections.Single().Body, after.Sections.Single().EventId));
    }

    private async Task ExpirePlanAsync(long orgId) =>
        await factory.WithDbAsync(async db =>
        {
            foreach (var s in await db.Subscriptions.Where(x => x.OrganizationId == orgId).ToListAsync())
                s.Status = SubscriptionStatus.EXPIRED;
            return await db.SaveChangesAsync();
        });
}
