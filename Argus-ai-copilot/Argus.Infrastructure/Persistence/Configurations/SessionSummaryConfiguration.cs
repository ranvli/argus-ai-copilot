using Argus.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Argus.Infrastructure.Persistence.Configurations;

internal sealed class SessionSummaryConfiguration : IEntityTypeConfiguration<SessionSummary>
{
    public void Configure(EntityTypeBuilder<SessionSummary> b)
    {
        b.ToTable("SessionSummaries");
        b.HasKey(s => s.Id);

        b.Property(s => s.Id).ValueGeneratedNever();
        b.Property(s => s.SessionId).IsRequired();
        b.Property(s => s.ShortSummary).HasMaxLength(1000).IsRequired();
        b.Property(s => s.FullSummary);
        b.Property(s => s.Sentiment).HasMaxLength(100);
        b.Property(s => s.ModelUsed).HasMaxLength(200);
        b.Property(s => s.GeneratedAt).IsRequired();

        // Store List<string> as pipe-delimited text — no extra table needed
        b.Property(s => s.ActionItems)
            .HasConversion(
                list => string.Join('|', list),
                raw => raw.Length == 0
                    ? new List<string>()
                    : raw.Split('|', StringSplitOptions.None).ToList())
            .HasColumnName("ActionItems");

        b.Property(s => s.KeyTopics)
            .HasConversion(
                list => string.Join('|', list),
                raw => raw.Length == 0
                    ? new List<string>()
                    : raw.Split('|', StringSplitOptions.None).ToList())
            .HasColumnName("KeyTopics");

        b.HasIndex(s => s.SessionId).IsUnique();
    }
}
