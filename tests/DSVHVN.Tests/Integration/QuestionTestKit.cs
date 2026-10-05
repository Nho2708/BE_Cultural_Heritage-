using System.Net;
using DSVHVN.Application.Heritages;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Questions;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Quizzes;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Tests.Integration;

/// <summary>
/// Dựng dữ liệu cho test ngân hàng câu hỏi qua chính API: trường có giáo viên và lớp theo cấp, di sản đã xuất bản có đủ ảnh trong thư viện và dòng thời gian,
/// bài học, câu hỏi mẫu hợp lệ cho từng dạng dựa trên ảnh thật của di sản, và các lời gọi API của khu Giáo viên, Quản trị hệ thống.
/// </summary>
public sealed class QuestionTestKit(ApiFactory factory)
{
    public const string Teacher = "/api/v1/teacher/questions";
    public const string Admin = "/api/v1/admin/questions";
    public const string Lessons = "/api/v1/teacher/lessons";

    public LessonTestKit Lesson { get; } = new(factory);
    public ApiFactory Factory { get; } = factory;
    public HttpClient Http => Lesson.Http;

    /// <summary>Di sản đã xuất bản có <paramref name="extraImages"/> ảnh thêm trong thư viện (ngoài ảnh đại diện) và <paramref name="events"/> sự kiện.</summary>
    public async Task<(HeritageDetailDto Heritage, List<string> Images)> HeritageAsync(string name = "Hoàng thành", int extraImages = 3, int events = 6)
    {
        var heritage = await Lesson.HeritageAsync(name, events);
        var admin = await Lesson.Discovery.Api.AdminTokenAsync();
        for (var i = 0; i < extraImages; i++)
        {
            await DiscoveryTestKit.Expect(Http.PostAsync<MediaDto>($"{DiscoveryTestKit.Admin}/heritages/{heritage.Id}/media", new
            {
                mediaType = "IMAGE",
                url = $"https://upload.wikimedia.org/{DiscoveryTestKit.Unique()}.jpg",
                caption = $"Ảnh {i + 2} của {name}",
                author = "Nguyễn Văn An",
                license = "CC BY-SA 4.0",
                source = "Wikimedia Commons",
            }, admin), HttpStatusCode.Created);
        }
        var images = await Factory.WithDbAsync(db => db.Media.AsNoTracking()
            .Where(m => m.HeritageId == heritage.Id && m.MediaType == "IMAGE").OrderBy(m => m.Id).Select(m => m.Url!).ToListAsync());
        return (heritage, images);
    }

    /// <summary>Một câu hợp lệ cho mỗi dạng, dùng ảnh trong thư viện của di sản (cần ít nhất 4 ảnh).</summary>
    public static Dictionary<QuestionType, SaveQuestionRequest> Samples(long heritageId, IReadOnlyList<string> images)
    {
        var (a, b, c) = (images[0], images[1], images[2]);
        return new()
        {
            [QuestionType.SINGLE_CHOICE] = new(QuestionType.SINGLE_CHOICE, heritageId, "Hoàng thành Thăng Long được UNESCO ghi danh vào năm nào?",
                "UNESCO ghi danh năm 2010.", DifficultyLevel.EASY,
                Options: [new("Năm 1999", null, false), new("Năm 2010", null, true), new("Năm 2003", null, false), new("Năm 2015", null, false)]),
            [QuestionType.TRUE_FALSE] = new(QuestionType.TRUE_FALSE, heritageId, "Em hãy xem hình: Hoàng thành Thăng Long nằm giữa lòng Hà Nội.",
                "Khu di tích nằm ở trung tâm Hà Nội.", DifficultyLevel.EASY, ImageUrl: a, AnswerIsTrue: true),
            [QuestionType.IMAGE_CHOICE] = new(QuestionType.IMAGE_CHOICE, heritageId, "Em hãy chọn hình có tên: Cổng Đoan Môn.", "Hình thứ hai là cổng Đoan Môn.",
                DifficultyLevel.EASY, Options: [new("Điện Kính Thiên", a, false), new("Cổng Đoan Môn", b, true), new("Khu khảo cổ", c, false)]),
            [QuestionType.MATCHING] = new(QuestionType.MATCHING, heritageId, "Em hãy ghép mỗi hình với tên của hình đó.", "Mỗi tên là chú thích của hình.",
                DifficultyLevel.MEDIUM, Pairs: [new(null, a, "Điện Kính Thiên"), new(null, b, "Cổng Đoan Môn"), new(null, c, "Khu khảo cổ")]),
            [QuestionType.ORDERING] = new(QuestionType.ORDERING, heritageId, "Hãy sắp xếp các sự kiện của Hoàng thành theo thứ tự thời gian.", "Xếp theo năm diễn ra.",
                DifficultyLevel.MEDIUM, Steps: ["Năm 1010: Lý Thái Tổ dời đô", "Năm 1070: Lập Văn Miếu", "Năm 1428: Lê Lợi lên ngôi", "Năm 2010: UNESCO ghi danh"]),
            [QuestionType.FILL_BLANK] = new(QuestionType.FILL_BLANK, heritageId, "Vua ___ dời đô từ Hoa Lư về Thăng Long vào năm 1010.", "Lý Thái Tổ là người dời đô.",
                DifficultyLevel.MEDIUM, Blanks: [new(["Lý Thái Tổ", "Lý Công Uẩn"])], Distractors: ["Lê Lợi", "Trần Hưng Đạo"]),
            [QuestionType.MULTI_TRUE_FALSE] = new(QuestionType.MULTI_TRUE_FALSE, heritageId,
                "Hoàng thành Thăng Long là trung tâm quyền lực của nhiều triều đại phong kiến Việt Nam.", "Nhận định thứ hai sai vì khu di tích nằm ở Hà Nội.",
                DifficultyLevel.HARD, Statements:
                [
                    new("Khu di tích được UNESCO ghi danh năm 2010.", true), new("Khu di tích nằm ở thành phố Huế.", false),
                    new("Khu di tích có nhiều lớp văn hóa chồng lên nhau.", true),
                ]),
            [QuestionType.SOURCE_READING] = new(QuestionType.SOURCE_READING, heritageId, "Theo đoạn tư liệu, vua nào dời đô về Thăng Long?", "Đoạn tư liệu nêu rõ Lý Thái Tổ.",
                DifficultyLevel.MEDIUM, Passage: "Mùa thu năm Canh Tuất, Lý Thái Tổ dời đô từ Hoa Lư về thành Đại La và đổi tên là Thăng Long.",
                PassageSource: "Đại Việt sử ký toàn thư, Ngô Sĩ Liên, thế kỷ 15",
                Options: [new("Lý Thái Tổ", null, true), new("Trần Thái Tông", null, false), new("Lê Thái Tổ", null, false), new("Đinh Tiên Hoàng", null, false)]),
        };
    }

    // ---- Lời gọi API ----

    public async Task<QuestionDetailDto> CreateAsync(string token, SaveQuestionRequest request) =>
        await DiscoveryTestKit.Expect(Http.PostAsync<QuestionDetailDto>(Teacher, request, token), HttpStatusCode.Created);

    public async Task<QuestionDetailDto> GetAsync(string token, long id) =>
        await DiscoveryTestKit.Expect(Http.GetAsync<QuestionDetailDto>($"{Teacher}/{id}", token));

    public async Task<QuestionPageDto> ListAsync(string token, string query = "") =>
        await DiscoveryTestKit.Expect(Http.GetAsync<QuestionPageDto>($"{Teacher}{(query.Length == 0 ? "" : "?" + query)}", token));

    public async Task<QuestionBatchDto> GenerateAsync(string token, long lessonId, object? request = null) =>
        await DiscoveryTestKit.Expect(Http.PostAsync<QuestionBatchDto>($"{Lessons}/{lessonId}/ai/questions", request ?? new { }, token));

    /// <summary>Trường có giáo viên, lớp theo cấp, di sản (đủ ảnh, sự kiện) và bài học Nháp.</summary>
    public async Task<LessonSetup> LessonAsync(int level, string heritageName = "Hoàng thành", int extraImages = 3, int events = 6)
    {
        var (school, schoolClass) = await Lesson.SchoolWithClassAsync(level);
        var (heritage, images) = await HeritageAsync(heritageName, extraImages, events);
        var lesson = await Lesson.CreateLessonAsync(school.TeacherToken, schoolClass.Id, [heritage.Id]);
        return new LessonSetup(school, schoolClass.Id, heritage, images, lesson.Id);
    }

    public sealed record LessonSetup(ClassroomClient.School School, long ClassId, HeritageDetailDto Heritage, List<string> Images, long LessonId)
    {
        public string Token => School.TeacherToken;
    }

    /// <summary>Đưa câu vào một bài kiểm tra mới thẳng trong CSDL (luồng bài kiểm tra chưa có API ở nhánh này).</summary>
    public async Task AddToQuizAsync(long lessonId, long questionId)
    {
        var quizId = await Lesson.AddQuizAsync(lessonId, QuizStatus.DRAFT, null, null);
        await Factory.WithDbAsync(async db =>
        {
            db.QuizQuestions.Add(new QuizQuestion { QuizId = quizId, QuestionId = questionId, Point = 1m, SortOrder = 1 });
            return await db.SaveChangesAsync();
        });
    }

    public Task<List<DSVHVN.Domain.Questions.Question>> RowsAsync(long generationId) =>
        Factory.WithDbAsync(db => db.Questions.AsNoTracking().Include(q => q.Answers).Where(q => q.AiGenerationId == generationId).OrderBy(q => q.Id).ToListAsync());
}
