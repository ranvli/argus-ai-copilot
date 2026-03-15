namespace Argus.AI.Models;

public sealed class VisionRequest
{
    /// <summary>Path to an image file on disk. Mutually exclusive with ImageBytes.</summary>
    public string? ImagePath { get; init; }

    /// <summary>Raw image bytes. Mutually exclusive with ImagePath.</summary>
    public byte[]? ImageBytes { get; init; }

    /// <summary>MIME type required when ImageBytes is supplied, e.g. "image/png".</summary>
    public string? MimeType { get; init; }

    /// <summary>Optional instruction for the vision model.</summary>
    public string Prompt { get; init; } = "Describe the contents of this image in detail.";
}
