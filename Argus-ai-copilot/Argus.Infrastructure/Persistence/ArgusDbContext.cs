using Argus.Core.Domain.Entities;
using Argus.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Argus.Infrastructure.Persistence;

public sealed class ArgusDbContext : DbContext
{
    public ArgusDbContext(DbContextOptions<ArgusDbContext> options) : base(options) { }

    public DbSet<Session>            Sessions           => Set<Session>();
    public DbSet<TranscriptSegment>  TranscriptSegments => Set<TranscriptSegment>();
    public DbSet<ScreenshotArtifact> Screenshots        => Set<ScreenshotArtifact>();
    public DbSet<RecordingArtifact>  Recordings         => Set<RecordingArtifact>();
    public DbSet<AppEvent>           AppEvents          => Set<AppEvent>();
    public DbSet<SessionSummary>     SessionSummaries   => Set<SessionSummary>();
    public DbSet<SpeakerProfile>     SpeakerProfiles    => Set<SpeakerProfile>();
    public DbSet<ProviderSettings>   ProviderSettings   => Set<ProviderSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new SessionConfiguration());
        modelBuilder.ApplyConfiguration(new TranscriptSegmentConfiguration());
        modelBuilder.ApplyConfiguration(new ScreenshotArtifactConfiguration());
        modelBuilder.ApplyConfiguration(new RecordingArtifactConfiguration());
        modelBuilder.ApplyConfiguration(new AppEventConfiguration());
        modelBuilder.ApplyConfiguration(new SessionSummaryConfiguration());
        modelBuilder.ApplyConfiguration(new SpeakerProfileConfiguration());
        modelBuilder.ApplyConfiguration(new ProviderSettingsConfiguration());
    }
}

