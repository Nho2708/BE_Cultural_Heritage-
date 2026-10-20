using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using DSVHVN.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DSVHVN.Infrastructure.Persistence.Configurations;

/// <summary>
/// Bảng <c>heritage_narrations</c> (CSDL v4, nhóm Đa ngôn ngữ): thuyết minh tạo sẵn của bản tóm tắt ngắn theo ngôn ngữ. Mỗi (di sản, ngôn ngữ) một dòng
/// (cùng cách ánh xạ và CHECK ngôn ngữ như <c>heritage_summaries</c>); băm chữ là char(64) không Unicode; mốc câu lưu JSON trong nvarchar(max).
/// </summary>
internal sealed class HeritageNarrationConfiguration : IEntityTypeConfiguration<HeritageNarration>
{
    public void Configure(EntityTypeBuilder<HeritageNarration> b)
    {
        b.ToTable("heritage_narrations", t =>
        {
            t.HasEnumCheck<HeritageNarration, LanguageCode>("heritage_narrations", "language_code");
            t.HasCheckConstraint("CK_heritage_narrations_duration", "[duration_ms] > 0");
        });
        b.Property(x => x.LanguageCode).AsEnumText();
        b.Property(x => x.TextHash).HasMaxLength(NarrationRules.HashLength).IsFixedLength().IsUnicode(false).IsRequired();
        b.Property(x => x.AudioUrl).HasMaxLength(500).IsRequired();
        b.Property(x => x.Sentences).IsRequired();
        b.Property(x => x.Voice).HasMaxLength(NarrationRules.VoiceMaxLength).IsRequired();
        b.Property(x => x.CreatedAt).IsRequired();
        b.HasIndex(x => new { x.HeritageId, x.LanguageCode }).IsPlainUnique("UX_heritage_narrations_heritage_language");

        b.HasOne(x => x.Heritage).WithMany().HasForeignKey(x => x.HeritageId).IsRequired().OnDelete(DeleteBehavior.Restrict);
    }
}
