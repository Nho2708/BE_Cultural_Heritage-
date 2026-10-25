using DSVHVN.Domain.Rules;
using DSVHVN.Domain.Speech;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DSVHVN.Infrastructure.Persistence.Configurations;

/// <summary>
/// Bảng <c>speech_clips</c> (CSDL v4, nhóm Giọng đọc): bộ nhớ đệm bền của giọng đọc tự host. Băm là char(64) không Unicode; mỗi (băm, giọng) đúng một dòng.
/// Không có khóa ngoại: bảng chỉ nối với kho tệp qua khóa tệp.
/// </summary>
internal sealed class SpeechClipConfiguration : IEntityTypeConfiguration<SpeechClip>
{
    public void Configure(EntityTypeBuilder<SpeechClip> b)
    {
        b.ToTable("speech_clips", t =>
        {
            t.HasCheckConstraint("CK_speech_clips_duration", "[duration_ms] > 0");
            t.HasCheckConstraint("CK_speech_clips_byte_size", "[byte_size] > 0");
        });
        b.Property(x => x.TextHash).HasMaxLength(NarrationRules.HashLength).IsFixedLength().IsUnicode(false).IsRequired();
        b.Property(x => x.Voice).HasMaxLength(SpeechRules.VoiceMaxLength).IsUnicode(false).IsRequired();
        b.Property(x => x.StorageKey).HasMaxLength(500).IsUnicode(false).IsRequired();
        b.Property(x => x.CreatedAt).IsRequired();
        b.HasIndex(x => new { x.TextHash, x.Voice }).IsPlainUnique("UX_speech_clips_hash_voice");
    }
}
