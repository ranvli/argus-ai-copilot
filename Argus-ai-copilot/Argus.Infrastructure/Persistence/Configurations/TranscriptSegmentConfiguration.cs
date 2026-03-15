using Argus.Core.Domain.Entities;
using Argus.Core.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Argus.Infrastructure.Persistence.Configurations;

internal sealed class TranscriptSegmentConfiguration : IEntityTypeConfiguration<TranscriptSegment>
{
    public void Configure(EntityTypeBuilder<TranscriptSegment> b)
    {
        b.ToTable("TranscriptSegments");
        b.HasKey(t => t.Id);

        b.Property(t => t.Id).ValueGeneratedNever();
        b.Property(t => t.SessionId).IsRequired();
        b.Property(t => t.Text).IsRequired();
        b.Property(t => t.SpeakerType).HasConversion<string>().HasMaxLength(50);
        b.Property(t => t.SpeakerLabel).HasMaxLength(200);
        b.Property(t => t.Language).HasMaxLength(20);
        b.Property(t => t.CreatedAt).IsRequired();

        // TimeRange value object — owned, flattened to columns
        b.OwnsOne(t => t.Range, r =>
        {
            r.Property(x => x.Start).HasColumnName("RangeStart").IsRequired();
            r.Property(x => x.End).HasColumnName("RangeEnd").IsRequired();
        });

        // ConfidenceScore value object — store the double value only
        b.Property(t => t.Confidence)
            .HasConversion(
                cs => cs.Value,
                v => new ConfidenceScore(v))
            .HasColumnName("Confidence");

        b.HasIndex(t => t.SessionId);
    }
}
