using DSVHVN.Application.Audit;
using DSVHVN.Application.Billing;
using DSVHVN.Application.Common;
using DSVHVN.Application.Lessons;
using DSVHVN.Application.Questions;
using DSVHVN.Application.Speech;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Classes;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Questions;
using DSVHVN.Domain.Quizzes;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace DSVHVN.Application.Quizzes;

/// <summary>
/// Bài kiểm tra của bài học (màn hình Bài kiểm tra của tôi, Soạn và phát hành bài kiểm tra, Chọn câu từ ngân hàng).
/// <list type="bullet">
/// <item>Bài kiểm tra gắn một bài học của chính giáo viên (bài Nháp hoặc Đã xuất bản); lớp làm bài là lớp của bài học, không có mã riêng:
/// học sinh vào bằng mã bài học. Một bài học có nhiều bài kiểm tra.</item>
/// <item>Chỉ nhận câu đã duyệt, chưa xóa, có dạng và cấp phù hợp với cấp của lớp; 1-50 câu, điểm mỗi câu bước 0,25; tổng điểm tự tính.</item>
/// <item>Thời gian làm theo cấp: cấp 1 không giới hạn, cấp 2 tùy chọn, cấp 3 bắt buộc khi phát hành.</item>
/// <item>Nháp sửa được hết; phát hành khóa câu, điểm, thứ tự, thời gian làm, giờ mở, xáo câu, cờ hiện đáp án (chỉ còn sửa tên, mô tả, giờ đóng);
/// đóng không mở lại; muốn đổi câu thì nhân bản. Chỉ xóa mềm bài Nháp.</item>
/// </list>
/// </summary>
public sealed class QuizService(
    IAppDbContext db,
    RequestValidator validator,
    PlanGuard plans,
    QuizDetailBuilder detailBuilder,
    IAuditLogger audit,
    SpeechWarmUp speechWarmUp,
    TimeProvider clock)
{
    // ---- Danh sách, chi tiết, hộp chọn câu ----

    /// <summary>Danh sách bài kiểm tra của giáo viên ở mọi bài học: lọc theo bài học, lớp, trạng thái, từ khóa trong tên; mới sửa trước.</summary>
    public async Task<QuizPageDto> ListAsync(Actor actor, QuizListQuery query, CancellationToken ct)
    {
        var quizzes = db.QuizzesOf(actor).AsNoTracking();
        if (query.LessonId is { } lessonId) quizzes = quizzes.Where(q => q.LessonId == lessonId);
        if (query.ClassId is { } classId) quizzes = quizzes.Where(q => q.Lesson!.ClassId == classId);
        if (query.Status is { } status) quizzes = quizzes.Where(q => q.Status == status);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            quizzes = quizzes.Where(q => q.Name != null && q.Name.Contains(keyword));
        }

        var now = Now;
        var rows = quizzes
            .OrderByDescending(q => q.UpdatedAt ?? q.CreatedAt).ThenByDescending(q => q.Id)
            .Select(q => new
            {
                Quiz = q,
                LessonTitle = q.Lesson!.Title,
                ClassId = q.Lesson.ClassId,
                ClassName = q.Lesson.Class!.Name,
                q.Lesson.Class.Grade,
                Questions = q.Questions.Count,
                Submitted = db.Attempts.Count(a => a.QuizId == q.Id && a.Status != AttemptStatus.IN_PROGRESS),
                Students = db.Students.Count(s => s.ClassId == q.Lesson.ClassId && s.DeletedAt == null && s.IsActive),
            });
        var page = await rows.ToPagedAsync(query.Page, query.PageSize, ct);

        var items = page.Items.Select(r =>
        {
            var state = QuizRules.OpenStateOf(r.Quiz.Status, r.Quiz.OpenAt, r.Quiz.CloseAt, now);
            return new QuizRowDto(
                r.Quiz.Id, r.Quiz.Name, r.Quiz.LessonId, r.LessonTitle, r.ClassId, r.ClassName, r.Grade, SchoolClass.SchoolLevelOf(r.Grade),
                r.Quiz.Status, QuizRules.StatusLabel(r.Quiz.Status), state, QuizRules.OpenStateLabel(state), r.Quiz.OpenAt, r.Quiz.CloseAt,
                r.Quiz.TimeLimitMinutes, r.Quiz.DifficultyLevel, r.Questions, r.Quiz.TotalPoint, r.Submitted, r.Students, r.Quiz.PublishedAt,
                r.Quiz.CreatedAt, r.Quiz.UpdatedAt);
        }).ToList();
        return new QuizPageDto(items, page.Page, page.PageSize, page.TotalItems, page.TotalPages);
    }

    public async Task<QuizDetailDto> GetAsync(Actor actor, long quizId, CancellationToken ct)
    {
        var quiz = await db.FindQuizAsync(actor, quizId, "xem bài kiểm tra", tracking: false, ct);
        return await detailBuilder.BuildAsync(quiz, ct);
    }

    /// <summary>
    /// Hộp "Chọn câu từ ngân hàng": câu đã duyệt, chưa xóa, dùng chung mọi trường, chỉ câu có dạng và cấp dùng được ở cấp của lớp (câu khác không hiện).
    /// Mặc định lọc theo các di sản của bài học; lọc thêm dạng, độ khó, di sản, từ khóa. Câu đã có trong bài đánh dấu <c>InQuiz</c>.
    /// </summary>
    public async Task<QuizCandidatePageDto> CandidatesAsync(Actor actor, long quizId, QuizCandidateQuery query, CancellationToken ct)
    {
        var quiz = await db.FindQuizAsync(actor, quizId, "chọn câu hỏi cho bài kiểm tra", tracking: false, ct);
        var level = SchoolClass.SchoolLevelOf(quiz.Lesson!.Class!.Grade);
        var allowedTypes = QuestionTypes.ForLevel(level);
        var nullableTypes = allowedTypes.Select(t => (QuestionType?)t).ToList();
        var levelByte = (byte)level;

        var questions = db.Questions.AsNoTracking()
            .Where(q => q.DeletedAt == null && q.ReviewStatus == ReviewStatus.APPROVED
                        && q.LevelMin <= levelByte && q.LevelMax >= levelByte && nullableTypes.Contains(q.QuestionType));
        if (query.HeritageId is null && query.LessonHeritagesOnly != false)
        {
            var lessonHeritages = db.LessonHeritages.Where(lh => lh.LessonId == quiz.LessonId).Select(lh => lh.HeritageId);
            questions = questions.Where(q => lessonHeritages.Contains(q.HeritageId));
        }
        questions = QuestionService.ApplyFilters(questions,
            new QuestionListQuery(QuestionType: query.QuestionType, DifficultyLevel: query.DifficultyLevel, HeritageId: query.HeritageId, Keyword: query.Keyword));

        var rows = questions
            .OrderByDescending(q => q.CreatedAt).ThenByDescending(q => q.Id)
            .Select(q => new
            {
                Question = q,
                Creator = q.Creator!.FullName,
                Organization = q.Creator.Organization!.Name,
                Heritage = q.Heritage!.Name,
                Quizzes = db.QuizQuestions.Count(x => x.QuestionId == q.Id && x.Quiz!.DeletedAt == null),
            });
        var page = await rows.ToPagedAsync(query.Page, query.PageSize, ct);

        var inQuiz = quiz.Questions.Select(x => x.QuestionId).ToHashSet();
        var items = page.Items.Select(r => new QuizCandidateDto(
            QuestionPresenter.Row(r.Question, r.Creator, r.Organization, r.Heritage, r.Question.CreatedBy == actor.UserId, hidden: false, r.Quizzes),
            inQuiz.Contains(r.Question.Id))).ToList();
        return new QuizCandidatePageDto(items, page.Page, page.PageSize, page.TotalItems, page.TotalPages, level, allowedTypes);
    }

    // ---- Tạo, sửa, xóa ----

    /// <summary>
    /// Tạo bài kiểm tra nháp cho một bài học của giáo viên (bài Nháp hoặc Đã xuất bản). Cần gói hiệu lực. Thời gian làm đúng quy tắc của cấp lớp
    /// (cấp 1 để trống; cấp 3 có thể điền sau, bắt buộc khi phát hành).
    /// </summary>
    public async Task<QuizDetailDto> CreateAsync(Actor actor, CreateQuizRequest request, CancellationToken ct)
    {
        if (actor.Role != RoleCode.TEACHER) throw AppException.Forbidden(Messages.Forbidden);
        var orgId = actor.RequireOrganizationId();
        await validator.EnsureValidAsync(request, ct);

        var lesson = await db.FindLessonAsync(actor, request.LessonId!.Value, "tạo bài kiểm tra", tracking: false, ct);
        if (lesson.Status == ContentStatus.ARCHIVED)
            throw AppException.BusinessRule(Messages.Blocked("tạo bài kiểm tra", "Bài học đã lưu trữ. Hãy xuất bản lại bài học hoặc chọn bài học khác."));
        await EnsureEffectivePlanAsync(orgId, "tạo bài kiểm tra", ct);

        var level = SchoolClass.SchoolLevelOf(lesson.Class!.Grade);
        EnsureTimeLimit(level, request.TimeLimitMinutes, forPublish: false);

        var quiz = new Quiz
        {
            LessonId = lesson.Id,
            CreatedBy = actor.UserId,
            Name = request.Name!.Trim(),
            Description = ValidationRules.TrimToNull(request.Description),
            DifficultyLevel = request.DifficultyLevel,
            TimeLimitMinutes = request.TimeLimitMinutes,
            OpenAt = request.OpenAt is { } open ? QuizRules.ToUtc(open) : null,
            CloseAt = request.CloseAt is { } close ? QuizRules.ToUtc(close) : null,
            ShuffleQuestions = request.ShuffleQuestions,
            ShowAnswersAfterSubmit = request.ShowAnswersAfterSubmit,
            TotalPoint = 0m,
            Status = QuizStatus.DRAFT,
        };
        db.Quizzes.Add(quiz);
        await db.SaveChangesAsync(ct);
        return await GetAsync(actor, quiz.Id, ct);
    }

    /// <summary>
    /// Lưu thiết lập. Bài Nháp sửa tất cả; bài đã phát hành chỉ sửa tên, mô tả, giờ đóng (không sớm hơn hiện tại) và các trường khóa phải giữ nguyên
    /// (422 nếu khác); bài đã đóng không sửa được.
    /// </summary>
    public async Task<QuizDetailDto> UpdateAsync(Actor actor, long quizId, UpdateQuizRequest request, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var quiz = await db.FindQuizAsync(actor, quizId, "lưu bài kiểm tra", tracking: true, ct);
        var level = SchoolClass.SchoolLevelOf(quiz.Lesson!.Class!.Grade);
        var open = request.OpenAt is { } o ? QuizRules.ToUtc(o) : (DateTime?)null;
        var close = request.CloseAt is { } c ? QuizRules.ToUtc(c) : (DateTime?)null;

        switch (quiz.Status)
        {
            case QuizStatus.CLOSED:
                throw AppException.BusinessRule(Messages.Blocked("lưu bài kiểm tra", "Bài kiểm tra đã đóng. Hãy nhân bản bài kiểm tra để dùng lại."));

            case QuizStatus.PUBLISHED:
                var lockedChanged = request.DifficultyLevel != quiz.DifficultyLevel || request.TimeLimitMinutes != quiz.TimeLimitMinutes
                                    || open != quiz.OpenAt || request.ShuffleQuestions != quiz.ShuffleQuestions
                                    || request.ShowAnswersAfterSubmit != quiz.ShowAnswersAfterSubmit;
                if (lockedChanged)
                    throw AppException.BusinessRule(Messages.Blocked("lưu bài kiểm tra",
                        "Bài kiểm tra đã phát hành, chỉ sửa được tên, mô tả và giờ đóng. Muốn đổi độ khó, thời gian làm, giờ mở, xáo câu hoặc cách hiện đáp án, hãy nhân bản bài kiểm tra."));
                if (close is not { } newClose)
                    throw AppException.Validation("closeAt", Messages.Required("giờ đóng"));
                if (newClose != quiz.CloseAt && newClose <= Now)
                    throw AppException.Validation("closeAt", Messages.Invalid("Giờ đóng", "không được sớm hơn hiện tại"));
                if (quiz.OpenAt is { } published && newClose <= published)
                    throw AppException.Validation("closeAt", Messages.Invalid("Giờ đóng", "phải sau giờ mở"));
                quiz.CloseAt = newClose;
                break;

            default:
                EnsureTimeLimit(level, request.TimeLimitMinutes, forPublish: false);
                quiz.DifficultyLevel = request.DifficultyLevel;
                quiz.TimeLimitMinutes = request.TimeLimitMinutes;
                quiz.OpenAt = open;
                quiz.CloseAt = close;
                quiz.ShuffleQuestions = request.ShuffleQuestions;
                quiz.ShowAnswersAfterSubmit = request.ShowAnswersAfterSubmit;
                break;
        }

        quiz.Name = request.Name!.Trim();
        quiz.Description = ValidationRules.TrimToNull(request.Description);
        await db.SaveChangesAsync(ct);
        return await GetAsync(actor, quizId, ct);
    }

    /// <summary>Xóa mềm bài Nháp. Bài đã phát hành hoặc đã đóng giữ nguyên để giữ kết quả.</summary>
    public async Task DeleteAsync(Actor actor, long quizId, CancellationToken ct)
    {
        var quiz = await db.FindQuizAsync(actor, quizId, "xóa bài kiểm tra", tracking: true, ct);
        if (quiz.Status != QuizStatus.DRAFT)
            throw AppException.BusinessRule(Messages.Blocked("xóa bài kiểm tra", "Chỉ xóa được bài kiểm tra nháp. Bài đã phát hành hoặc đã đóng được giữ lại để giữ kết quả."));
        quiz.DeletedAt = Now;
        await db.SaveChangesAsync(ct);
    }

    // ---- Câu hỏi trong bài ----

    /// <summary>
    /// Thêm câu từ ngân hàng vào cuối bài Nháp. Mỗi câu phải đã duyệt, chưa xóa, có dạng và cấp dùng được ở cấp của lớp, chưa có trong bài; tổng không quá 50 câu.
    /// Một câu sai thì không thêm câu nào.
    /// </summary>
    public async Task<QuizDetailDto> AddQuestionsAsync(Actor actor, long quizId, AddQuizQuestionsRequest request, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        var ids = request.QuestionIds!.Distinct().ToList();
        var point = request.Point ?? QuizRules.DefaultPoint;

        await WithLockedQuizAsync(actor, quizId, "thêm câu hỏi vào bài kiểm tra", async quiz =>
        {
            EnsureDraft(quiz, "thêm câu hỏi vào bài kiểm tra");
            var level = SchoolClass.SchoolLevelOf(quiz.Lesson!.Class!.Grade);

            var already = quiz.Questions.Select(x => x.QuestionId).ToHashSet();
            if (already.Count + ids.Count > QuizRules.MaxQuestions)
                throw AppException.BusinessRule(Messages.Blocked("thêm câu hỏi vào bài kiểm tra",
                    $"Bài kiểm tra có tối đa {QuizRules.MaxQuestions} câu, hiện có {already.Count} câu."));

            var found = await db.Questions.Where(q => ids.Contains(q.Id)).ToDictionaryAsync(q => q.Id, ct);
            var invalid = new Dictionary<string, string>();
            var mismatched = new Dictionary<string, string>();
            for (var i = 0; i < ids.Count; i++)
            {
                var field = $"questionIds[{i}]";
                if (!found.TryGetValue(ids[i], out var question) || question.DeletedAt is not null || question.ReviewStatus != ReviewStatus.APPROVED)
                    invalid[field] = Messages.Invalid("Câu hỏi", "chỉ chọn được câu đã duyệt trong ngân hàng").Text;
                else if (already.Contains(ids[i]))
                    invalid[field] = Messages.Invalid("Câu hỏi", "đã có trong bài kiểm tra").Text;
                else if (!QuizRules.QuestionFitsLevel(question.QuestionType, question.LevelMin, question.LevelMax, level))
                    mismatched[field] = Messages.QuestionLevelMismatch(QuestionTypes.NameOf(question.QuestionType), level).Text;
            }
            if (invalid.Count > 0)
                throw new AppException(400, new AppMessage(MessageCodes.Invalid, invalid.Values.First()), invalid.Concat(mismatched).ToDictionary(x => x.Key, x => x.Value));
            if (mismatched.Count > 0)
                throw new AppException(422, new AppMessage(MessageCodes.QuestionLevelMismatch, mismatched.Values.First()), mismatched);

            var order = quiz.Questions.Count == 0 ? 0 : quiz.Questions.Max(x => x.SortOrder ?? 0);
            foreach (var id in ids)
            {
                quiz.Questions.Add(new QuizQuestion { QuizId = quiz.Id, QuestionId = id, Point = point, SortOrder = ++order });
            }
            RecalculateTotal(quiz.Questions, quiz);
            await db.SaveChangesAsync(ct);
            return 0;
        }, ct);
        return await GetAsync(actor, quizId, ct);
    }

    /// <summary>Đặt lại điểm, thứ tự và bỏ câu của bài Nháp. Chỉ nhận câu đã có trong bài; thứ tự đánh lại liên tục từ 1.</summary>
    public async Task<QuizDetailDto> ReplaceQuestionsAsync(Actor actor, long quizId, ReplaceQuizQuestionsRequest request, CancellationToken ct)
    {
        await validator.EnsureValidAsync(request, ct);
        await WithLockedQuizAsync(actor, quizId, "sửa danh sách câu hỏi", async quiz =>
        {
            EnsureDraft(quiz, "sửa danh sách câu hỏi");
            var current = quiz.Questions.ToDictionary(x => x.QuestionId);
            var errors = new Dictionary<string, string>();
            for (var i = 0; i < request.Items!.Count; i++)
            {
                if (!current.ContainsKey(request.Items[i].QuestionId!.Value))
                    errors[$"items[{i}].questionId"] = Messages.Invalid("Câu hỏi", "chưa có trong bài kiểm tra; hãy thêm câu từ ngân hàng trước").Text;
            }
            if (errors.Count > 0) throw new AppException(400, new AppMessage(MessageCodes.Invalid, errors.Values.First()), errors);

            var keep = request.Items.Select(i => i.QuestionId!.Value).ToHashSet();
            db.QuizQuestions.RemoveRange(quiz.Questions.Where(x => !keep.Contains(x.QuestionId)).ToList());
            var kept = new List<QuizQuestion>();
            var order = 0;
            foreach (var item in request.Items)
            {
                var row = current[item.QuestionId!.Value];
                row.Point = item.Point!.Value;
                row.SortOrder = ++order;
                kept.Add(row);
            }
            RecalculateTotal(kept, quiz);
            await db.SaveChangesAsync(ct);
            return 0;
        }, ct);
        return await GetAsync(actor, quizId, ct);
    }

    /// <summary>Bỏ một câu khỏi bài Nháp; các câu còn lại đánh lại thứ tự liên tục và tổng điểm tính lại.</summary>
    public async Task<QuizDetailDto> RemoveQuestionAsync(Actor actor, long quizId, long questionId, CancellationToken ct)
    {
        await WithLockedQuizAsync(actor, quizId, "bỏ câu hỏi khỏi bài kiểm tra", async quiz =>
        {
            EnsureDraft(quiz, "bỏ câu hỏi khỏi bài kiểm tra");
            var item = quiz.Questions.SingleOrDefault(x => x.QuestionId == questionId)
                       ?? throw AppException.NotFound(Messages.NotFound("bỏ câu hỏi khỏi bài kiểm tra", "câu hỏi trong bài kiểm tra"));
            db.QuizQuestions.Remove(item);
            var kept = quiz.Questions.Where(x => x.QuestionId != questionId).OrderBy(x => x.SortOrder).ThenBy(x => x.QuestionId).ToList();
            var order = 0;
            foreach (var x in kept) x.SortOrder = ++order;
            RecalculateTotal(kept, quiz);
            await db.SaveChangesAsync(ct);
            return 0;
        }, ct);
        return await GetAsync(actor, quizId, ct);
    }

    // ---- Phát hành, đóng, nhân bản ----

    /// <summary>
    /// Phát hành bài Nháp khi đủ điều kiện: 1-50 câu còn dùng được ở cấp hiện tại của lớp; giờ mở và giờ đóng đã đặt, giờ mở trước giờ đóng, giờ đóng sau hiện tại;
    /// thời gian làm đúng quy tắc của cấp (cấp 3 phải đã đặt, cấp 1 để trống); bài học đã xuất bản; trường có gói hiệu lực. Một giao dịch: tính lại tổng điểm,
    /// đặt <c>published_at</c>, ghi nhật ký. Không sinh mã, không chọn lớp. Phát hành bài đã phát hành không đổi gì.
    /// </summary>
    public async Task<PublishQuizResultDto> PublishAsync(Actor actor, long quizId, CancellationToken ct)
    {
        if (actor.Role != RoleCode.TEACHER) throw AppException.Forbidden(Messages.Forbidden);
        var orgId = actor.RequireOrganizationId();

        var published = await WithLockedQuizAsync(actor, quizId, "phát hành bài kiểm tra", async quiz =>
        {
            if (quiz.Status == QuizStatus.CLOSED)
                throw AppException.BusinessRule(Messages.Blocked("phát hành bài kiểm tra", "Bài kiểm tra đã đóng. Hãy nhân bản bài kiểm tra để dùng lại."));
            if (quiz.Status == QuizStatus.PUBLISHED) return false;

            await EnsureEffectivePlanAsync(orgId, "phát hành bài kiểm tra", ct);
            var check = detailBuilder.Evaluate(quiz, Now);
            if (!check.IsReady) throw new AppException(422, Messages.NotReady("phát hành bài kiểm tra", check.Missing));

            RecalculateTotal(quiz.Questions, quiz);
            quiz.AccessCode ??= await NewAccessCodeAsync(ct);
            quiz.Status = QuizStatus.PUBLISHED;
            quiz.PublishedAt = Now;
            audit.Record(AuditAction.QUIZ_PUBLISHED, AuditTargets.Quizzes, quiz.Id, actor,
                oldValue: new { status = QuizStatus.DRAFT },
                newValue: new
                {
                    status = QuizStatus.PUBLISHED, quizName = quiz.Name, lessonId = quiz.LessonId, classId = quiz.Lesson!.ClassId,
                    questionCount = quiz.Questions.Count, totalPoint = quiz.TotalPoint, openAt = quiz.OpenAt, closeAt = quiz.CloseAt,
                    timeLimitMinutes = quiz.TimeLimitMinutes,
                });
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);

        // Bài chơi của bài cấp 1 vừa phát hành thì xếp các câu sẽ đọc to vào hàng đợi tạo giọng nền; không chờ và không bao giờ làm hỏng lần phát hành.
        if (published) await speechWarmUp.WarmQuizAsync(quizId, ct);

        var detail = await GetAsync(actor, quizId, ct);
        var message = published
            ? Messages.QuizPublished(detail.ClassName ?? string.Empty, detail.Name ?? string.Empty, detail.AccessCode, detail.LessonAccessCode, detail.OpenAt, detail.CloseAt).Text
            : Messages.Success("Phát hành bài kiểm tra").Text;
        return new PublishQuizResultDto(detail, message);
    }

    /// <summary>
    /// Đóng bài đã phát hành bất kỳ lúc nào (không mở lại): các lượt làm bài đang dở được tự nộp ở trạng thái hết giờ; ghi nhật ký.
    /// Đóng bài đã đóng không đổi gì; bài Nháp không đóng được (xóa nếu không dùng).
    /// </summary>
    public async Task<CloseQuizResultDto> CloseAsync(Actor actor, long quizId, CancellationToken ct)
    {
        var expired = await WithLockedQuizAsync(actor, quizId, "đóng bài kiểm tra", async quiz =>
        {
            if (quiz.Status == QuizStatus.CLOSED) return -1;
            if (quiz.Status != QuizStatus.PUBLISHED)
                throw AppException.BusinessRule(Messages.Blocked("đóng bài kiểm tra", "Chỉ đóng được bài kiểm tra đã phát hành. Bài nháp thì xóa nếu không dùng nữa."));

            var count = await QuizLifecycle.CloseAsync(db, audit, quiz, Now, actor, ct);
            await db.SaveChangesAsync(ct);
            return count;
        }, ct);

        var detail = await GetAsync(actor, quizId, ct);
        return new CloseQuizResultDto(detail, Math.Max(expired, 0),
            expired < 0 ? Messages.Success("Đóng bài kiểm tra").Text : Messages.QuizClosed(detail.Name ?? string.Empty, expired).Text);
    }

    /// <summary>
    /// Nhân bản bài kiểm tra trong cùng bài học thành bài Nháp mới: cùng câu, điểm, thứ tự và thiết lập; không chép giờ mở, giờ đóng, lượt làm bài.
    /// Câu đã bị ẩn hoặc xóa không được chép và hệ thống báo số câu bỏ qua. Cần gói hiệu lực; bài học không phải bài Lưu trữ. Dùng để đổi câu của bài đã
    /// phát hành hoặc dùng lại bài đã đóng.
    /// </summary>
    public async Task<DuplicateQuizResultDto> DuplicateAsync(Actor actor, long quizId, DuplicateQuizRequest request, CancellationToken ct)
    {
        if (actor.Role != RoleCode.TEACHER) throw AppException.Forbidden(Messages.Forbidden);
        var orgId = actor.RequireOrganizationId();
        await validator.EnsureValidAsync(request, ct);

        var source = await db.FindQuizAsync(actor, quizId, "nhân bản bài kiểm tra", tracking: false, ct);
        if (source.Lesson!.Status == ContentStatus.ARCHIVED)
            throw AppException.BusinessRule(Messages.Blocked("nhân bản bài kiểm tra", "Bài học đã lưu trữ. Hãy xuất bản lại bài học trước."));
        await EnsureEffectivePlanAsync(orgId, "nhân bản bài kiểm tra", ct);

        var name = ValidationRules.TrimToNull(request.Name) ?? CopyName(source.Name);
        var (copy, skipped) = QuizCopier.Copy(source, actor.UserId, name, targetLevel: null);
        copy.LessonId = source.LessonId;
        db.Quizzes.Add(copy);
        await db.SaveChangesAsync(ct);

        var detail = await GetAsync(actor, copy.Id, ct);
        return new DuplicateQuizResultDto(detail, skipped, Messages.QuizDuplicated(detail.Name ?? string.Empty, skipped).Text);
    }

    // ---- Nội bộ ----

    private DateTime Now
    {
        get
        {
            var now = clock.GetUtcNow().UtcDateTime;
            return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
        }
    }

    /// <summary>
    /// Chạy một thao tác ghi trên bài kiểm tra trong giao dịch có khóa lớp của bài học, để hai yêu cầu đồng thời trên cùng một lớp chạy tuần tự
    /// (hai lần bấm phát hành không ghi hai dòng nhật ký, thêm câu không vượt 50).
    /// </summary>
    private async Task<T> WithLockedQuizAsync<T>(Actor actor, long quizId, string action, Func<Quiz, Task<T>> body, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var classId = await db.QuizzesOf(actor).Where(q => q.Id == quizId).Select(q => (long?)q.Lesson!.ClassId).SingleOrDefaultAsync(ct)
                      ?? throw AppException.NotFound(Messages.NotFound(action, "bài kiểm tra của bạn"));
        await db.LockClassAsync(classId, ct);
        var quiz = await db.FindQuizAsync(actor, quizId, action, tracking: true, ct);
        var result = await body(quiz);
        await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>Mã bài kiểm tra chưa dùng (kể cả của bài đã xóa: mã không cấp lại). Ràng buộc không trùng chặn nốt trường hợp hai bài cùng sinh một mã.</summary>
    private async Task<string> NewAccessCodeAsync(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= LessonCodes.MaxGenerateAttempts; attempt++)
        {
            var code = QuizCodes.NewCode();
            if (!await db.Quizzes.AnyAsync(q => q.AccessCode == code, ct)) return code;
        }
        throw new InvalidOperationException("Không sinh được mã bài kiểm tra không trùng.");
    }

    private static void EnsureDraft(Quiz quiz, string action)
    {
        if (quiz.Status != QuizStatus.DRAFT)
            throw AppException.BusinessRule(Messages.Blocked(action,
                "Bài kiểm tra đã phát hành, danh sách câu hỏi và điểm đã khóa. Hãy nhân bản bài kiểm tra để đổi câu."));
    }

    private static void RecalculateTotal(IEnumerable<QuizQuestion> items, Quiz quiz) => quiz.TotalPoint = items.Sum(x => x.Point ?? 0m);

    private static void EnsureTimeLimit(int level, int? minutes, bool forPublish)
    {
        if (QuizRules.CheckTimeLimit(level, minutes, forPublish) is { } reason)
            throw AppException.Validation("timeLimitMinutes", Messages.Invalid("Thời gian làm bài", reason));
    }

    private async Task EnsureEffectivePlanAsync(long organizationId, string action, CancellationToken ct)
    {
        if (await plans.GetEffectiveAsync(organizationId, ct) is null) throw new AppException(422, Messages.PlanRequired(action));
    }

    private static string CopyName(string? name)
    {
        var baseName = name ?? string.Empty;
        var max = QuizRules.NameMaxLength - QuizRules.CopySuffix.Length;
        if (baseName.Length > max) baseName = baseName[..max];
        return baseName + QuizRules.CopySuffix;
    }
}
