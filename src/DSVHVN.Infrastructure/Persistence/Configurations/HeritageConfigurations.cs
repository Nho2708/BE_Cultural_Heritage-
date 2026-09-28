using DSVHVN.Domain.Enums;
using DSVHVN.Domain.Heritages;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DSVHVN.Infrastructure.Persistence.Configurations;

// Nhóm Di sản của CSDL v3.

internal sealed class ProvinceConfiguration : IEntityTypeConfiguration<Province>
{
    public void Configure(EntityTypeBuilder<Province> b)
    {
        b.ToTable("provinces");
        b.Property(x => x.Code).HasMaxLength(10);
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.Region).HasMaxLength(50);
        b.HasIndex(x => x.Code).IsPlainUnique("UX_provinces_code");
    }
}

internal sealed class HeritageConfiguration : IEntityTypeConfiguration<Heritage>
{
    public void Configure(EntityTypeBuilder<Heritage> b)
    {
        b.ToTable("heritages", t => t
            .HasEnumCheck<Heritage, HeritageType>("heritages", "heritage_type")
            .HasEnumCheck<Heritage, RecognitionLevel>("heritages", "recognition_level")
            .HasEnumCheck<Heritage, ContentStatus>("heritages", "status"));
        b.Property(x => x.Name).HasMaxLength(255);
        b.Property(x => x.Slug).HasMaxLength(255);
        b.Property(x => x.ShortDescription).HasMaxLength(1000);
        b.Property(x => x.Content);
        b.Property(x => x.HeritageType).AsEnumText();
        b.Property(x => x.RecognitionLevel).AsEnumText();
        b.Property(x => x.Address).HasMaxLength(500);
        b.Property(x => x.Latitude).HasPrecision(9, 6);
        b.Property(x => x.Longitude).HasPrecision(9, 6);
        b.Property(x => x.PlaceId).HasMaxLength(255);
        b.Property(x => x.ThumbnailUrl).HasMaxLength(500);
        b.Property(x => x.Status).AsEnumText();
        b.HasIndex(x => x.Slug).IsPlainUnique("UX_heritages_slug");

        b.HasOne(x => x.Province).WithMany().HasForeignKey(x => x.ProvinceId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TimelineConfiguration : IEntityTypeConfiguration<Timeline>
{
    public void Configure(EntityTypeBuilder<Timeline> b)
    {
        b.ToTable("timelines");
        b.Property(x => x.Name).HasMaxLength(255);
        b.Property(x => x.Description);

        b.HasOne(x => x.Heritage).WithMany(h => h.Timelines).HasForeignKey(x => x.HeritageId).IsRequired()
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TimelineEventConfiguration : IEntityTypeConfiguration<TimelineEvent>
{
    public void Configure(EntityTypeBuilder<TimelineEvent> b)
    {
        b.ToTable("events");
        b.Property(x => x.Name).HasMaxLength(255);
        b.Property(x => x.Description);
        b.Property(x => x.StartYear).IsRequired();

        b.HasOne(x => x.Timeline).WithMany(t => t.Events).HasForeignKey(x => x.TimelineId).IsRequired()
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MediaConfiguration : IEntityTypeConfiguration<Media>
{
    public void Configure(EntityTypeBuilder<Media> b)
    {
        // Mỗi dòng thuộc đúng một trong hai: di sản hoặc sự kiện. Viết dạng AND/OR (T-SQL không so sánh được hai biểu thức IS NULL).
        b.ToTable("media", t => t.HasCheckConstraint("CK_media_owner",
            "(heritage_id IS NOT NULL AND event_id IS NULL) OR (heritage_id IS NULL AND event_id IS NOT NULL)"));
        b.Property(x => x.MediaType).HasMaxLength(20);
        b.Property(x => x.Url).HasMaxLength(500);
        b.Property(x => x.Caption).HasMaxLength(500);
        b.Property(x => x.Credit).HasMaxLength(255);

        b.HasOne(x => x.Heritage).WithMany(h => h.Media).HasForeignKey(x => x.HeritageId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Event).WithMany(e => e.Media).HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class HeritageReferenceConfiguration : IEntityTypeConfiguration<HeritageReference>
{
    public void Configure(EntityTypeBuilder<HeritageReference> b)
    {
        b.ToTable("heritage_references");
        b.Property(x => x.Title).HasMaxLength(500);
        b.Property(x => x.Url).HasMaxLength(500);
        b.Property(x => x.SourceType).HasMaxLength(50);

        b.HasOne(x => x.Heritage).WithMany(h => h.References).HasForeignKey(x => x.HeritageId).IsRequired()
            .OnDelete(DeleteBehavior.Restrict);
    }
}
