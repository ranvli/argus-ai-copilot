namespace Argus.Core.Domain.ValueObjects;

public sealed record TimeRange(DateTimeOffset Start, DateTimeOffset End)
{
    public TimeSpan Duration => End - Start;

    public bool Contains(DateTimeOffset point) => point >= Start && point <= End;

    public bool Overlaps(TimeRange other) => Start < other.End && End > other.Start;
}
