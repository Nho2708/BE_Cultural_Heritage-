using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using DSVHVN.Application.Audit;
using DSVHVN.Application.Common;
using DSVHVN.Application.Files;
using DSVHVN.Application.Heritages;
using DSVHVN.Application.Translations;
using DSVHVN.Domain.Audit;
using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DSVHVN.Application.Narrations;

/// <summary>
/// Thuyết minh tạo sẵn của tóm tắt di sản (quản trị hệ thống; công cụ tạo giọng chạy trên máy phát triển gọi hai API này bằng tài khoản quản trị).
/// <list type="bullet">
/// <item>Danh sách còn thiếu: các bản tóm tắt ngắn <b>đang hiện công khai</b> ở một ngôn ngữ (đúng quy tắc chọn tóm tắt của <see cref="PublicSummaryService"/>, không có quy tắc thứ hai)
/// mà chưa có thuyết minh, hoặc thuyết minh có băm khác chữ hiện tại (tóm tắt đã sửa hoặc dịch lại). Kèm băm để công cụ gửi lại khi tải lên.</item>
/// <item>Tải lên: kiểm các trường chữ trước, đối chiếu băm với chữ hiện tại (lệch thì 409, không nhận tệp), kiểm tệp MP3 bằng nội dung rồi mới lưu vào kho;
/// ghi dòng không được thì tệp vừa lưu bị gỡ lại. Tải lên lần nữa thay dòng cũ; tệp cũ vẫn nằm trong kho như cách các tệp bị thay khác (kho chỉ xóa để hoàn tác).</item>
/// <item>Phần <c>content</c>: thuyết minh đọc toàn bộ nội dung di sản đã xuất bản, hiện chỉ tiếng Việt; chữ đọc lấy từ <c>NarrationRules.ContentSpeechText</c> và băm tính trên chữ đó.</item>
/// <item>Mỗi lần lưu ghi nhật ký thao tác <c>HERITAGE_NARRATION_CHANGED</c> (di sản, ngôn ngữ, giọng, thời lượng, số câu, dung lượng; không ghi đường dẫn tệp).</item>
/// </list>
/// </summary>
public sealed class NarrationService(
    IAppDbContext db, PublicSummaryService summaries, IFileStorage storage, IAuditLogger audit, TimeProvider clock, ILogger<NarrationService> logger)
{
    public const int DefaultLimit = 500;
    public const int MaxLimit = 1000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // Giữ chữ của năm ngôn ngữ đọc được trong CSDL.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ---- Danh sách còn thiếu ----

    /// <summary>Tóm tắt đang hiện công khai ở <paramref name="language"/> mà chưa có thuyết minh khớp băm, theo mã di sản tăng dần, tối đa <paramref name="limit"/> mục (1..1000, mặc định 500).</summary>
    public async Task<MissingNarrationsDto> ListMissingAsync(string? language, int? limit, CancellationToken ct, string? part = null)
    {
        var lang = ParseLanguage(language, "xem thuyết minh còn thiếu");
        var narrationPart = ParsePart(part, lang);
        var size = limit ?? DefaultLimit;
        if (size is < 1 or > MaxLimit)
            throw AppException.Validation("limit", Messages.Invalid("Số mục", $"từ 1 đến {MaxLimit}"));

        var shown = narrationPart == NarrationPart.CONTENT ? await ListContentsAsync(ct) : await summaries.ListShownAsync(lang, ct);
        var existing = await db.HeritageNarrations.AsNoTracking()
            .Where(n => n.LanguageCode == lang && n.Part == narrationPart)
            .Select(n => new { n.HeritageId, n.TextHash })
            .ToDictionaryAsync(n => n.HeritageId, n => n.TextHash, ct);

        var missing = shown
            .Select(s => new MissingNarrationItemDto(s.HeritageId, s.Slug, s.Name, Short(lang), s.ShortText, NarrationRules.TextHash(s.ShortText)))
            .Where(s => !existing.TryGetValue(s.HeritageId, out var hash) || hash != s.TextHash)
            .OrderBy(s => s.HeritageId)
            .ToList();
        return new MissingNarrationsDto(missing.Take(size).ToList(), missing.Count);
    }

    /// <summary>Chữ đọc toàn bộ nội dung của mọi di sản đã xuất bản có nội dung (tiếng Việt), theo mã di sản tăng dần.</summary>
    private async Task<IReadOnlyList<PublicSummaryService.ShownSummary>> ListContentsAsync(CancellationToken ct)
    {
        var rows = await db.Heritages.AsNoTracking()
            .Where(h => h.DeletedAt == null && h.Status == ContentStatus.PUBLISHED && h.Content != null)
            .OrderBy(h => h.Id)
            .Select(h => new { h.Id, h.Slug, h.Name, h.Content })
            .ToListAsync(ct);
        return rows
            .Select(h => new PublicSummaryService.ShownSummary(h.Id, h.Slug ?? string.Empty, h.Name ?? string.Empty, NarrationRules.ContentSpeechText(h.Content)))
            .Where(h => h.ShortText.Length > 0)
            .ToList();
    }

    // ---- Tải lên ----

    /// <summary>
    /// Lưu thuyết minh của một (di sản, ngôn ngữ): thay dòng cũ nếu có. 404 khi không có di sản hoặc không có tóm tắt ngôn ngữ đó; 409 khi băm gửi lên
    /// khác băm của bản tóm tắt ngắn hiện tại (tóm tắt đã đổi, tạo lại); 400 khi trường chữ hoặc tệp không hợp lệ.
    /// </summary>
    public async Task<HeritageNarrationDto> UploadAsync(Actor actor, long heritageId, string? language, UploadNarrationRequest request, UploadedFile? audio, CancellationToken ct,
        string? part = null)
    {
        if (actor.Role != RoleCode.ADMIN) throw AppException.Forbidden(Messages.Forbidden);
        const string action = "lưu thuyết minh";
        var lang = ParseLanguage(language, action);
        var narrationPart = ParsePart(part, lang);
        var (voice, durationMs, sentences) = Validate(request);

        var heritage = await db.FindHeritageAsync(heritageId, action, ct);
        string spoken;
        if (narrationPart == NarrationPart.CONTENT)
        {
            spoken = NarrationRules.ContentSpeechText(heritage.Content);
            if (spoken.Length == 0) throw AppException.NotFound(Messages.NotFound(action, "nội dung di sản"));
        }
        else
        {
            spoken = await db.HeritageSummaries.AsNoTracking()
                         .Where(s => s.HeritageId == heritageId && s.LanguageCode == lang)
                         .Select(s => s.ShortText).SingleOrDefaultAsync(ct)
                     ?? throw AppException.NotFound(Messages.NotFound(action, $"bản tóm tắt {Languages.InlineLabel(lang)}"));
        }
        var currentHash = NarrationRules.TextHash(spoken);
        if (request.TextHash!.Trim() != currentHash)
            throw AppException.Conflict("textHash", narrationPart == NarrationPart.CONTENT ? NarrationMessages.ContentChanged : NarrationMessages.SummaryChanged);

        CheckAudio(audio);
        // Tìm dòng cũ trước khi nhận tệp: hai lần tải lên cùng lúc thì lần thua chạm ràng buộc không trùng và tự gỡ tệp của mình.
        var row = await db.HeritageNarrations.SingleOrDefaultAsync(n => n.HeritageId == heritageId && n.LanguageCode == lang && n.Part == narrationPart, ct);
        var key = StoredFileRules.NarrationKey(Guid.NewGuid());
        await storage.SaveAsync(key, audio!.Content, ct);

        try
        {
            if (row is null)
            {
                row = new HeritageNarration { HeritageId = heritageId, LanguageCode = lang, Part = narrationPart };
                db.HeritageNarrations.Add(row);
            }
            row.TextHash = currentHash;
            row.AudioUrl = storage.PublicUrl(key);
            row.DurationMs = durationMs;
            row.Sentences = JsonSerializer.Serialize(sentences, Json);
            row.Voice = voice;
            row.UpdatedAt = Now;

            // Lưu dòng rồi mới ghi nhật ký để nhật ký có mã dòng; cả hai trong một giao dịch.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(ct);
            audit.Record(AuditAction.HERITAGE_NARRATION_CHANGED, AuditTargets.HeritageNarrations, row.Id, actor,
                newValue: new
                {
                    heritageId,
                    heritage = heritage.Name,
                    language = Short(lang),
                    part = narrationPart.ToString(),
                    voice,
                    durationMs,
                    sentenceCount = sentences.Count,
                    sizeBytes = audio.Content.Length,
                });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new HeritageNarrationDto(heritageId, Short(lang), row.AudioUrl, row.DurationMs, row.Voice, row.TextHash, sentences.Count);
        }
        catch (DbUpdateException ex)
        {
            // Hai lần tải lên cùng một (di sản, ngôn ngữ) chạm nhau ở ràng buộc không trùng: lần thua gỡ tệp của mình và báo thử lại.
            logger.LogWarning(ex, "Hai lần tải thuyết minh cùng lúc cho di sản {HeritageId} ngôn ngữ {Language}", heritageId, lang);
            await DiscardAsync(key);
            throw new AppException(409, NarrationMessages.Concurrent);
        }
        catch (Exception)
        {
            await DiscardAsync(key);
            throw;
        }
    }

    // ---- Kiểm ----

    private static (string Voice, int DurationMs, List<NarrationSentence> Sentences) Validate(UploadNarrationRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.TextHash)) throw AppException.Validation("textHash", Messages.Required("băm của bản tóm tắt"));
        if (!NarrationRules.IsValidHash(r.TextHash.Trim()))
            throw AppException.Validation("textHash", Messages.Invalid("Băm của bản tóm tắt", "phải là 64 ký tự hex thường của SHA-256"));

        if (string.IsNullOrWhiteSpace(r.Voice)) throw AppException.Validation("voice", Messages.Required("mã giọng"));
        var voice = r.Voice.Trim();
        if (voice.Length > NarrationRules.VoiceMaxLength)
            throw AppException.Validation("voice", Messages.Invalid("Mã giọng", $"tối đa {NarrationRules.VoiceMaxLength} ký tự"));

        if (r.DurationMs is null) throw AppException.Validation("durationMs", Messages.Required("thời lượng"));
        var durationMs = r.DurationMs.Value;
        if (durationMs is < 1 or > NarrationRules.MaxDurationMs)
            throw AppException.Validation("durationMs", Messages.Invalid("Thời lượng", $"từ 1 đến {NarrationRules.MaxDurationMs} mili giây"));

        if (string.IsNullOrWhiteSpace(r.Sentences)) throw AppException.Validation("sentences", Messages.Required("danh sách câu"));
        List<NarrationSentence>? sentences;
        try
        {
            sentences = JsonSerializer.Deserialize<List<NarrationSentence>>(r.Sentences, Json);
        }
        catch (JsonException)
        {
            sentences = null;
        }
        if (sentences is null)
            throw AppException.Validation("sentences", Messages.Invalid("Danh sách câu", "phải là chuỗi JSON dạng [{ \"text\", \"startMs\", \"endMs\" }]"));
        // Phần tử null trong mảng JSON thành null của C#; coi như câu trống.
        var cleaned = sentences.Select(s => s ?? new NarrationSentence(string.Empty, 0, 0)).ToList();

        var problem = NarrationRules.Check(cleaned, durationMs);
        if (problem != SentenceProblem.None)
            throw AppException.Validation("sentences", Messages.Invalid("Danh sách câu", problem switch
            {
                SentenceProblem.Count => $"từ 1 đến {NarrationRules.MaxSentences} câu",
                SentenceProblem.Text => $"mỗi câu phải có chữ và không quá {NarrationRules.SentenceMaxLength} ký tự",
                SentenceProblem.Range => "mỗi câu phải có 0 ≤ startMs < endMs",
                SentenceProblem.BeyondDuration => $"endMs không được vượt thời lượng quá {NarrationRules.EndSlackMs} mili giây",
                _ => "startMs của các câu phải không giảm",
            }));
        return (voice, durationMs, cleaned);
    }

    private static void CheckAudio(UploadedFile? audio)
    {
        if (audio is null || audio.Content.Length == 0)
            throw AppException.Validation("audio", Messages.Required("tệp âm thanh"));
        if (audio.Content.Length > StoredFileRules.MaxNarrationBytes)
            throw AppException.Validation("audio", Messages.Invalid("Tệp âm thanh", "tối đa 10 MB"));
        if (!NarrationRules.IsMp3(audio.Content))
            throw AppException.Validation("audio", Messages.Invalid("Tệp âm thanh", "chỉ nhận tệp MP3 (mở đầu bằng thẻ ID3 hoặc khung MPEG)"));
    }

    private static LanguageCode ParseLanguage(string? language, string action) =>
        Languages.TryParse(language)
        ?? throw AppException.Validation("language", Messages.Invalid("Ngôn ngữ", $"không thể {action} cho \"{language}\"; chỉ nhận vi, en, ko, zh-Hans hoặc ja"));

    private static NarrationPart ParsePart(string? part, LanguageCode language)
    {
        if (string.IsNullOrWhiteSpace(part) || part.Equals("summary", StringComparison.OrdinalIgnoreCase)) return NarrationPart.SUMMARY;
        if (!part.Equals("content", StringComparison.OrdinalIgnoreCase))
            throw AppException.Validation("part", Messages.Invalid("Phần được đọc", "chỉ nhận summary hoặc content"));
        if (language != LanguageCode.vi)
            throw AppException.Validation("language", Messages.Invalid("Ngôn ngữ", "thuyết minh toàn bộ nội dung hiện chỉ có tiếng Việt"));
        return NarrationPart.CONTENT;
    }

    /// <summary>Mã ngắn lưu trong CSDL (zh, không phải zh-Hans), để công cụ tạo giọng gửi lại nguyên văn trên đường dẫn.</summary>
    private static string Short(LanguageCode language) => language.ToString();

    private async Task DiscardAsync(string key)
    {
        try
        {
            await storage.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception)
        {
            // Tệp thừa không được làm mất lỗi gốc.
        }
    }
}
