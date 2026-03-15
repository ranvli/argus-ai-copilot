using Argus.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Argus.Infrastructure.Persistence.Configurations;

internal sealed class ProviderSettingsConfiguration : IEntityTypeConfiguration<ProviderSettings>
{
    public void Configure(EntityTypeBuilder<ProviderSettings> b)
    {
        b.ToTable("ProviderSettings");
        b.HasKey(p => p.Id);

        b.Property(p => p.Id).ValueGeneratedNever();
        b.Property(p => p.ProviderKey).HasMaxLength(200).IsRequired();
        b.Property(p => p.ApiKey).HasMaxLength(500);
        b.Property(p => p.Endpoint).HasMaxLength(500);
        b.Property(p => p.ModelId).HasMaxLength(200);
        b.Property(p => p.IsEnabled);
        b.Property(p => p.UpdatedAt).IsRequired();

        // Store Dictionary<string,string> as JSON text — no extra join table needed
        b.Property(p => p.ExtraProperties)
            .HasConversion(
                dict => System.Text.Json.JsonSerializer.Serialize(dict, (System.Text.Json.JsonSerializerOptions?)null),
                json => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json, (System.Text.Json.JsonSerializerOptions?)null)
                        ?? new Dictionary<string, string>())
            .HasColumnName("ExtraProperties");

        b.HasIndex(p => p.ProviderKey).IsUnique();
    }
}
