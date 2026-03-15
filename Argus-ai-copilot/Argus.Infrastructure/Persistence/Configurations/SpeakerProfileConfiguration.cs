using Argus.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Argus.Infrastructure.Persistence.Configurations;

internal sealed class SpeakerProfileConfiguration : IEntityTypeConfiguration<SpeakerProfile>
{
    public void Configure(EntityTypeBuilder<SpeakerProfile> b)
    {
        b.ToTable("SpeakerProfiles");
        b.HasKey(p => p.Id);

        b.Property(p => p.Id).ValueGeneratedNever();
        b.Property(p => p.DisplayName).HasMaxLength(300).IsRequired();
        b.Property(p => p.Type).HasConversion<string>().HasMaxLength(50);
        b.Property(p => p.VoiceEmbeddingRef).HasMaxLength(500);
        b.Property(p => p.AvatarPath).HasMaxLength(1000);
        b.Property(p => p.CreatedAt).IsRequired();
        b.Property(p => p.UpdatedAt).IsRequired();

        b.HasIndex(p => p.DisplayName);
    }
}
