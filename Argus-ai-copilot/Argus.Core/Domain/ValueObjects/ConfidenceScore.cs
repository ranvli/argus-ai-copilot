namespace Argus.Core.Domain.ValueObjects;

public sealed record ConfidenceScore
{
    public double Value { get; }

    public ConfidenceScore(double value)
    {
        if (value < 0.0 || value > 1.0)
            throw new ArgumentOutOfRangeException(nameof(value), "Confidence must be between 0.0 and 1.0.");
        Value = value;
    }

    public static ConfidenceScore None => new(0.0);
    public static ConfidenceScore Full => new(1.0);

    public bool IsAbove(double threshold) => Value > threshold;

    public override string ToString() => $"{Value:P0}";
}
