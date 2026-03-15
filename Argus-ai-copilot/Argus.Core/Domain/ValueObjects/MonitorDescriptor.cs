namespace Argus.Core.Domain.ValueObjects;

public sealed record MonitorDescriptor(int Index, string DeviceName, int Width, int Height)
{
    public override string ToString() => $"[{Index}] {DeviceName} ({Width}x{Height})";
}
