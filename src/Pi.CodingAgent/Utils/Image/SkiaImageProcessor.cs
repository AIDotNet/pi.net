using Pi.Durable.Tools;

namespace Pi.CodingAgent.Utils.Image;

/// <summary>
/// durable 的 <c>read</c> 工具用的图片处理器：把 <see cref="ImageResize"/> 的缩放/编码结果
/// 翻成 <see cref="PreparedImage"/>。对应 TS <c>@earendil-works/pi-durable/images</c> 的
/// <c>createPhotonImages()</c>。
/// </summary>
/// <remarks>
/// <para>
/// 上游用 Photon（Rust→WASM）解码与编码，端口用 SkiaSharp，因此底层不是同一套实现；
/// 决策顺序沿用上游 <c>prepare</c>：始终先解码（损坏文件返回 null 而不是原样透传，免得 provider
/// 拒收）、按 EXIF 方向摆正、缩到 maxWidth×maxHeight、再按编码结果反复缩尺寸直到 base64 装得下。
/// </para>
/// <para>
/// 两处已知差异：① 上游对 JPEG 源先试 JPEG 再试 PNG（<c>jpegFirst</c>），端口沿用
/// <see cref="ImageResize"/> 的「PNG 优先、再 JPEG 各档质量」——结果更保守（可能多降一档尺寸），
/// 但不会更差；② 上游的字节判定是 <c>&lt;=</c>，端口既有的缩放核心用 <c>&lt;</c>。
/// </para>
/// </remarks>
internal sealed class SkiaImageProcessor : IImageProcessor
{
    /// <summary>进程内共享一个实例（无状态）。</summary>
    public static SkiaImageProcessor Instance { get; } = new();

    public async Task<PreparedImage?> PrepareAsync(byte[] bytes, string mimeType, ImageLimits limits)
    {
        var resized = await ImageResize.ResizeImageAsync(bytes, mimeType, new ImageResizeOptions
        {
            MaxWidth = (int)Math.Min(int.MaxValue, limits.MaxWidth),
            MaxHeight = (int)Math.Min(int.MaxValue, limits.MaxHeight),
            MaxBytes = limits.MaxBytes,
            // provider 不能内联接收的格式（BMP）必须转成 PNG，不能原样透传。
            ForceReencode = !ImageEncoding.InlineImageTypes.Contains(mimeType),
        }).ConfigureAwait(false);
        if (resized is null) return null;

        // 只有尺寸真的变了才给 resized：字节超限触发的重编码不该让模型去换算坐标。
        var dimensionsChanged = resized.Width != resized.OriginalWidth
            || resized.Height != resized.OriginalHeight;
        return new PreparedImage
        {
            Data = resized.Data,
            MimeType = resized.MimeType,
            ResizedFrom = dimensionsChanged
                ? new ImageSize(resized.OriginalWidth, resized.OriginalHeight)
                : null,
            ResizedTo = dimensionsChanged ? new ImageSize(resized.Width, resized.Height) : null,
            ConvertedFrom = string.Equals(resized.MimeType, mimeType, StringComparison.OrdinalIgnoreCase)
                ? null
                : mimeType,
        };
    }
}
