using Argus.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Argus.Infrastructure.Persistence.Configurations;

internal sealed class RecordingArtifactConfiguration : IEntityTypeConfiguration<RecordingArtifact>
{
    public void Configure(EntityTypeBuilder<RecordingArtifact> b)
    {
        b.ToTable("Recordings");
        b.HasKey(r => r.Id);

        b.Property(r => r.Id).ValueGeneratedNever();
        b.Property(r => r.SessionId).IsRequired();
        b.Property(r => r.Type).HasConversion<string>().HasMaxLength(50);
        b.Property(r => r.FilePath).HasMaxLength(1000).IsRequired();
        b.Property(r => r.FileSizeBytes);
        b.Property(r => r.MimeType).HasMaxLength(100);
        b.Property(r => r.CreatedAt).IsRequired();

        // TimeRange value object — owned, flattened
        b.OwnsOne(r => r.Range, rng =>
        {
            rng.Property(x => x.Start).HasColumnName("RangeStart").IsRequired();
            rng.Property(x => x.End).HasColumnName("RangeEnd").IsRequired();
        });

        b.HasIndex(r => r.SessionId);
    }
}
