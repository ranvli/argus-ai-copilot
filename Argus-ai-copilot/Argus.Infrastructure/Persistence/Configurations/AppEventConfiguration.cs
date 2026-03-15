using Argus.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Argus.Infrastructure.Persistence.Configurations;

internal sealed class AppEventConfiguration : IEntityTypeConfiguration<AppEvent>
{
    public void Configure(EntityTypeBuilder<AppEvent> b)
    {
        b.ToTable("AppEvents");
        b.HasKey(e => e.Id);

        b.Property(e => e.Id).ValueGeneratedNever();
        b.Property(e => e.Type).HasConversion<string>().HasMaxLength(100).IsRequired();
        b.Property(e => e.SessionId);
        b.Property(e => e.Details).HasMaxLength(2000);
        b.Property(e => e.OccurredAt).IsRequired();

        b.HasIndex(e => e.OccurredAt);
        b.HasIndex(e => e.SessionId);
    }
}
