using Argus.Core.Domain.Entities;
using Argus.Core.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Argus.Infrastructure.Persistence.Configurations;

internal sealed class ScreenshotArtifactConfiguration : IEntityTypeConfiguration<ScreenshotArtifact>
{
    public void Configure(EntityTypeBuilder<ScreenshotArtifact> b)
    {
        b.ToTable("Screenshots");
        b.HasKey(s => s.Id);

        b.Property(s => s.Id).ValueGeneratedNever();
        b.Property(s => s.SessionId).IsRequired();
        b.Property(s => s.FilePath).HasMaxLength(1000).IsRequired();
        b.Property(s => s.ActiveWindowTitle).HasMaxLength(500);
        b.Property(s => s.OcrText);
        b.Property(s => s.CapturedAt).IsRequired();
        b.Property(s => s.CreatedAt).IsRequired();

        // MonitorDescriptor — owned, flattened to columns
        b.OwnsOne(s => s.Monitor, m =>
        {
            m.Property(x => x.Index).HasColumnName("MonitorIndex");
            m.Property(x => x.DeviceName).HasColumnName("MonitorDeviceName").HasMaxLength(200);
            m.Property(x => x.Width).HasColumnName("MonitorWidth");
            m.Property(x => x.Height).HasColumnName("MonitorHeight");
        });

        b.HasIndex(s => s.SessionId);
    }
}
