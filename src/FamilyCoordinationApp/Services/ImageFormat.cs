namespace FamilyCoordinationApp.Services;

/// <summary>
/// What an image's own first bytes say it is. Used for bytes this app did not choose (an agent's image URL), so the
/// stored extension and the content type the uploads gate later serves come from the file, not from a site's
/// <c>Content-Type</c> header or a URL's extension. Only the four formats <see cref="ImageService"/> stores.
/// </summary>
public static class ImageFormat
{
    public sealed record Kind(string Extension, string ContentType);

    public static readonly Kind Jpeg = new(".jpg", "image/jpeg");
    public static readonly Kind Png = new(".png", "image/png");
    public static readonly Kind Gif = new(".gif", "image/gif");
    public static readonly Kind Webp = new(".webp", "image/webp");

    /// <summary>The format whose signature <paramref name="bytes"/> starts with, or null for anything else.</summary>
    public static Kind? Sniff(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return Jpeg;
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A])) return Png;
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return Gif;
        if (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)) return Webp;
        return null;
    }
}
