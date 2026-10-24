namespace Pi.Durable.Tools;

/// <summary>图片的像素尺寸。对应 TS <c>{ width, height }</c>。</summary>
public sealed record ImageSize(int Width, int Height);

/// <summary>模型看到的图片必须满足的限制。对应 TS <c>ImageLimits</c>。</summary>
public sealed record ImageLimits(int MaxWidth, int MaxHeight, long MaxBytes)
{
    /// <summary>
    /// coding agent 的默认值：2000×2000 像素，base64 后 4.5 MiB（低于 Anthropic 的 5 MB）。
    /// 对应 TS <c>DEFAULT_IMAGE_LIMITS</c>。
    /// </summary>
    public static ImageLimits Default { get; } = new(2000, 2000, (long)(4.5 * 1024 * 1024));
}

/// <summary>可供模型使用的图片：受支持格式、在限制内、已 base64。对应 TS <c>PreparedImage</c>。</summary>
public sealed record PreparedImage
{
    public required string Data { get; init; }

    public required string MimeType { get; init; }

    /// <summary>缩放前的尺寸；未缩放时为 null。</summary>
    public ImageSize? ResizedFrom { get; init; }

    /// <summary>缩放后的尺寸；未缩放时为 null。模型据此把坐标映射回原图。</summary>
    public ImageSize? ResizedTo { get; init; }

    /// <summary>与原格式不同时的原始格式：BMP 变 PNG，过大的 PNG 可能变 JPEG。</summary>
    public string? ConvertedFrom { get; init; }
}

/// <summary>
/// 解码、按方向摆正、缩放并重新编码，使模型能接收。对应 TS <c>ImageProcessor</c>。
/// </summary>
/// <remarks>
/// <c>read</c> 给了处理器才用它；没有处理器时，受支持且在字节上限内的图片原样透传。
/// </remarks>
public interface IImageProcessor
{
    /// <summary>按 <paramref name="limits"/> 备好的图片；无法解码或压不进限制时为 null。</summary>
    Task<PreparedImage?> PrepareAsync(byte[] bytes, string mimeType, ImageLimits limits);
}

/// <summary>图片编解码的纯工具。对应 TS <c>tools/image-processor.ts</c> 的导出。</summary>
public static class ImageEncoding
{
    /// <summary>所有 provider 都能内联接收的格式；BMP 之类需要转换。对应 TS <c>INLINE_IMAGE_TYPES</c>。</summary>
    public static IReadOnlySet<string> InlineImageTypes { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "image/png", "image/jpeg", "image/gif", "image/webp" };

    /// <summary>标准 base64。对应 TS <c>toBase64</c>。</summary>
    public static string ToBase64(byte[] bytes) => Convert.ToBase64String(bytes);

    /// <summary><paramref name="byteLength"/> 字节的 base64 长度。对应 TS <c>base64Length</c>。</summary>
    public static long Base64Length(long byteLength) => (long)Math.Ceiling(byteLength / 3d) * 4;
}
