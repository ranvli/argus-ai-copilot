using Argus.Core.Domain.Entities;
using Argus.Core.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Argus.Infrastructure.Persistence.Configurations;

internal sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> b)
    {
        b.ToTable("Sessions");
        b.HasKey(s => s.Id);

        b.Property(s => s.Id).ValueGeneratedNever();
        b.Property(s => s.Title).HasMaxLength(500).IsRequired();
        b.Property(s => s.Type).HasConversion<string>().HasMaxLength(50);
        b.Property(s => s.ListeningMode).HasConversion<string>().HasMaxLength(50);
        b.Property(s => s.LifecycleState).HasConversion<string>().HasMaxLength(50).IsRequired();
        b.Property(s => s.ApplicationContext).HasMaxLength(500);

        // DateTimeOffset stored as UTC text — SQLite-friendly
        b.Property(s => s.StartedAt).IsRequired();
        b.Property(s => s.EndedAt);
        b.Property(s => s.CreatedAt).IsRequired();
        b.Property(s => s.UpdatedAt).IsRequired();

        // Computed / transient — not mapped
        b.Ignore(s => s.IsActive);
        b.Ignore(s => s.Duration);

        // Navigation
        b.HasMany(s => s.Transcript)
            .WithOne()
            .HasForeignKey(t => t.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasMany(s => s.Screenshots)
            .WithOne()
            .HasForeignKey(sc => sc.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasMany(s => s.Recordings)
            .WithOne()
            .HasForeignKey(r => r.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(s => s.Summary)
            .WithOne()
            .HasForeignKey<SessionSummary>(ss => ss.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
