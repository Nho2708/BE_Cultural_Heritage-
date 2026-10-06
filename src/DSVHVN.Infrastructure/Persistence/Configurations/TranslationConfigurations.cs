using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DSVHVN.Infrastructure.Persistence.Configurations;

// Nhóm Đa ngôn ngữ của CSDL v4. language_code lưu chữ thường (vi, en, ko, zh, ja) nên tên thành viên enum cũng viết thường.
// Bốn bảng bản dịch không có dòng cho tiếng Việt (bản gốc nằm ở bảng chính); chỉ heritage_summaries có dòng tiếng Việt.

internal sealed class HeritageTranslationConfiguration : IEntityTypeConfiguration<HeritageTranslation>
{
    public void Configure(EntityTypeBuilder<HeritageTranslation> b)
    {
        b.ToTable("heritage_translations", t =>
        {
            t.HasEnumCheck<HeritageTranslation, LanguageCode>("heritage_translations", "language_code");
            // en: PENDING, APPROVED, REJECTED; ko, zh, ja: luôn MACHINE. Không có dòng tiếng Việt. (CHECK này đã bao cả danh sách giá trị của enum trạng thái.)
            t.HasCheckConstraint("CK_heritage_translations_status",
                "([language_code] = 'en' AND [status] IN ('PENDING', 'APPROVED', 'REJECTED')) "
                + "OR ([language_code] IN ('ko', 'zh', 'ja') AND [status] = 'MACHINE')");
        });
        b.Property(x => x.LanguageCode).AsEnumText();
        b.Property(x => x.Name).HasMaxLength(255).IsRequired();
        b.Property(x => x.ShortDescription).HasMaxLength(1000);
        b.Property(x => x.Content);
        b.Property(x => x.Address).HasMaxLength(500);
        b.Property(x => x.Status).AsEnumText();
        b.Property(x => x.IsOutdated).BitWithDefault(false);
        b.Property(x => x.CreatedAt).IsRequired();
        b.HasIndex(x => new { x.HeritageId, x.LanguageCode }).IsPlainUnique("UX_heritage_translations_heritage_language");

        b.HasOne(x => x.Heritage).WithMany().HasForeignKey(x => x.HeritageId).IsRequired().OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.AiGeneration).WithMany().HasForeignKey(x => x.AiGenerationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Reviewer).WithMany().HasForeignKey(x => x.ReviewedBy).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TimelineTranslationConfiguration : IEntityTypeConfiguration<TimelineTranslation>
{
    public void Configure(EntityTypeBuilder<TimelineTranslation> b)
    {
        b.ToTable("timeline_translations", t =>
        {
            t.HasEnumCheck<TimelineTranslation, LanguageCode>("timeline_translations", "language_code");
            t.HasCheckConstraint("CK_timeline_translations_language", "[language_code] <> 'vi'");
        });
        b.Property(x => x.LanguageCode).AsEnumText();
        b.Property(x => x.Name).HasMaxLength(255).IsRequired();
        b.Property(x => x.Description);
        b.Property(x => x.CreatedAt).IsRequired();
        b.HasIndex(x => new { x.TimelineId, x.LanguageCode }).IsPlainUnique("UX_timeline_translations_timeline_language");

        // Ngoại lệ Cascade: xóa giai đoạn thì xóa luôn bản dịch của nó.
        b.HasOne(x => x.Timeline).WithMany().HasForeignKey(x => x.TimelineId).IsRequired().OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class EventTranslationConfiguration : IEntityTypeConfiguration<EventTranslation>
{
    public void Configure(EntityTypeBuilder<EventTranslation> b)
    {
        b.ToTable("event_translations", t =>
        {
            t.HasEnumCheck<EventTranslation, LanguageCode>("event_translations", "language_code");
            t.HasCheckConstraint("CK_event_translations_language", "[language_code] <> 'vi'");
        });
        b.Property(x => x.LanguageCode).AsEnumText();
        b.Property(x => x.Name).HasMaxLength(255).IsRequired();
        b.Property(x => x.Description);
        b.Property(x => x.CreatedAt).IsRequired();
        b.HasIndex(x => new { x.EventId, x.LanguageCode }).IsPlainUnique("UX_event_translations_event_language");

        b.HasOne(x => x.Event).WithMany().HasForeignKey(x => x.EventId).IsRequired().OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class MediaTranslationConfiguration : IEntityTypeConfiguration<MediaTranslation>
{
    public void Configure(EntityTypeBuilder<MediaTranslation> b)
    {
        b.ToTable("media_translations", t =>
        {
            t.HasEnumCheck<MediaTranslation, LanguageCode>("media_translations", "language_code");
            t.HasCheckConstraint("CK_media_translations_language", "[language_code] <> 'vi'");
        });
        b.Property(x => x.LanguageCode).AsEnumText();
        b.Property(x => x.Caption).HasMaxLength(500).IsRequired();
        b.Property(x => x.CreatedAt).IsRequired();
        b.HasIndex(x => new { x.MediaId, x.LanguageCode }).IsPlainUnique("UX_media_translations_media_language");

        b.HasOne(x => x.Media).WithMany().HasForeignKey(x => x.MediaId).IsRequired().OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class HeritageSummaryConfiguration : IEntityTypeConfiguration<HeritageSummary>
{
    public void Configure(EntityTypeBuilder<HeritageSummary> b)
    {
        b.ToTable("heritage_summaries", t =>
        {
            t.HasEnumCheck<HeritageSummary, LanguageCode>("heritage_summaries", "language_code");
            // vi, en: PENDING, APPROVED, REJECTED; ko, zh, ja: luôn MACHINE. (CHECK này đã bao cả danh sách giá trị của enum trạng thái.)
            t.HasCheckConstraint("CK_heritage_summaries_status",
                "([language_code] IN ('vi', 'en') AND [status] IN ('PENDING', 'APPROVED', 'REJECTED')) "
                + "OR ([language_code] IN ('ko', 'zh', 'ja') AND [status] = 'MACHINE')");
        });
        b.Property(x => x.LanguageCode).AsEnumText();
        b.Property(x => x.ShortText).HasMaxLength(2000).IsRequired();
        b.Property(x => x.DetailedText).IsRequired();
        b.Property(x => x.Status).AsEnumText();
        b.Property(x => x.IsOutdated).BitWithDefault(false);
        b.Property(x => x.CreatedAt).IsRequired();
        b.HasIndex(x => new { x.HeritageId, x.LanguageCode }).IsPlainUnique("UX_heritage_summaries_heritage_language");

        b.HasOne(x => x.Heritage).WithMany().HasForeignKey(x => x.HeritageId).IsRequired().OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.AiGeneration).WithMany().HasForeignKey(x => x.AiGenerationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Reviewer).WithMany().HasForeignKey(x => x.ReviewedBy).OnDelete(DeleteBehavior.Restrict);
    }
}
