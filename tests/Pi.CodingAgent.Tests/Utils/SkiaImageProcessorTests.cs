using Pi.CodingAgent.Utils.Image;
using Pi.Durable.Tools;
using SkiaSharp;
using Xunit;

namespace Pi.CodingAgent.Tests.Utils;

/// <summary>
/// durable 的 <c>read</c> 用 Skia 实现的图片处理器：解码、按 EXIF 摆正、缩放到限制、
/// 换格式，以及「已经在限制内就原样透传」。对应上游 <c>durable/images</c> 的 prepare。
/// </summary>
public class SkiaImageProcessorTests
{
    private static byte[] Encode(int width, int height, SKEncodedImageFormat format, int quality = 90)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        return data.ToArray();
    }

    /// <summary>SkiaSharp 不能编码 BMP，手拼一张 24 位 BI_RGB（自下而上、行尾 4 字节对齐）。</summary>
    private static byte[] Bmp(int width, int height)
    {
        var rowSize = (width * 3 + 3) / 4 * 4;
        var pixelBytes = rowSize * height;
        var bytes = new byte[54 + pixelBytes];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BitConverter.GetBytes(bytes.Length).CopyTo(bytes, 2);
        BitConverter.GetBytes(54).CopyTo(bytes, 10);
        BitConverter.GetBytes(40).CopyTo(bytes, 14);
        BitConverter.GetBytes(width).CopyTo(bytes, 18);
        BitConverter.GetBytes(height).CopyTo(bytes, 22);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 26);
        BitConverter.GetBytes((short)24).CopyTo(bytes, 28);
        BitConverter.GetBytes(pixelBytes).CopyTo(bytes, 34);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = 54 + y * rowSize + x * 3;
                bytes[offset] = 0xcc;     // B
                bytes[offset + 1] = 0x88; // G
                bytes[offset + 2] = 0x44; // R
            }
        }
        return bytes;
    }

    [Fact]
    public async Task PassesThroughAnUprightImageWithinEveryLimit()
    {
        var bytes = Encode(80, 60, SKEncodedImageFormat.Png);

        var prepared = await SkiaImageProcessor.Instance.PrepareAsync(bytes, "image/png", ImageLimits.Default);

        Assert.NotNull(prepared);
        Assert.Equal(Convert.ToBase64String(bytes), prepared!.Data);
        Assert.Equal("image/png", prepared.MimeType);
        Assert.Null(prepared.ResizedFrom);
        Assert.Null(prepared.ConvertedFrom);
    }

    [Fact]
    public async Task ScalesDownToThePixelLimit()
    {
        var bytes = Encode(2400, 1600, SKEncodedImageFormat.Png);

        var prepared = await SkiaImageProcessor.Instance.PrepareAsync(bytes, "image/png", ImageLimits.Default);

        Assert.NotNull(prepared);
        Assert.Equal(new ImageSize(2400, 1600), prepared!.ResizedFrom);
        // 2400 → 2000，等比：1600 × 2000 / 2400 = 1333。
        Assert.Equal(new ImageSize(2000, 1333), prepared.ResizedTo);
        Assert.Equal("image/png", prepared.MimeType);
        Assert.Null(prepared.ConvertedFrom);
        Assert.NotEqual(Convert.ToBase64String(bytes), prepared.Data);
    }

    [Fact]
    public async Task ConvertsFormatsProvidersDoNotTakeInline()
    {
        var bytes = Bmp(64, 64);

        var prepared = await SkiaImageProcessor.Instance.PrepareAsync(bytes, "image/bmp", ImageLimits.Default);

        Assert.NotNull(prepared);
        Assert.Equal("image/bmp", prepared!.ConvertedFrom);
        Assert.Contains(prepared.MimeType, new[] { "image/png", "image/jpeg" });
        Assert.Null(prepared.ResizedFrom);
    }

    [Fact]
    public async Task HonoursACustomPixelLimit()
    {
        var bytes = Encode(400, 300, SKEncodedImageFormat.Png);

        var prepared = await SkiaImageProcessor.Instance.PrepareAsync(
            bytes, "image/png", new ImageLimits(200, 200, ImageLimits.Default.MaxBytes));

        Assert.NotNull(prepared);
        Assert.Equal(new ImageSize(400, 300), prepared!.ResizedFrom);
        Assert.Equal(new ImageSize(200, 150), prepared.ResizedTo);
    }

    [Fact]
    public async Task ShrinksUntilTheEncodedPayloadFits()
    {
        var bytes = Encode(800, 600, SKEncodedImageFormat.Png);
        const long budget = 4_000;
        var prepared = await SkiaImageProcessor.Instance.PrepareAsync(
            bytes, "image/png", new ImageLimits(2000, 2000, budget));

        Assert.NotNull(prepared);
        var base64Length = ((long)prepared!.Data.Length + 3) / 4 * 3;
        Assert.True(base64Length <= budget, $"payload is {base64Length} bytes");
    }

    /// <summary>接线：durable harness 装的是带处理器的 read（描述里多出 bmp，与上游一致）。</summary>
    [Fact]
    public void CodingToolsInstallsTheProcessorBackedReadTool()
    {
        var withProcessor = CodingTools.Create(SkiaImageProcessor.Instance)
            .Tools!.Single(tool => tool.Name == "read");
        var without = CodingTools.Extension.Tools!.Single(tool => tool.Name == "read");

        Assert.Contains("jpg, png, gif, webp, bmp", withProcessor.Description);
        Assert.Contains("jpg, png, gif, webp)", without.Description);
    }

    [Fact]
    public async Task ReturnsNullForBytesThatCannotBeDecoded()
    {
        var garbage = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        var prepared = await SkiaImageProcessor.Instance.PrepareAsync(garbage, "image/png", ImageLimits.Default);

        Assert.Null(prepared);
    }
}
