namespace Argus.AI.Providers;

public interface IVisionModel
{
    string ModelId { get; }

    Task<string> DescribeImageAsync(string imagePath, string? prompt = null, CancellationToken ct = default);

    Task<string> DescribeImageBytesAsync(byte[] imageBytes, string mimeType, string? prompt = null, CancellationToken ct = default);
}
