using Pi.Durable.Harness;

namespace Pi.Durable.Tools;

/// <summary>
/// 内建编码工具集：<c>read</c> / <c>write</c> / <c>edit</c> / <c>bash</c>。
/// 对应 TS <c>tools/index.ts</c> 的 <c>CodingTools</c>（没有任何地方会自动装上它）。
/// </summary>
public static class CodingTools
{
    /// <summary>四个工具扩展（<c>read</c> 不接图片处理器）。对应 TS <c>CodingTools</c>。</summary>
    public static IExtension Extension { get; } = Create();

    /// <summary>
    /// 四个工具扩展，<c>read</c> 用 <paramref name="images"/> 备图片。对应 TS
    /// <c>createCodingTools(options)</c> 的 <c>options.images</c>：不给处理器时，受支持且在限制内的
    /// 图片原样透传，BMP 之类需要转换的格式报错。
    /// </summary>
    public static IExtension Create(IImageProcessor? images = null) => new CodingToolsExtension(images);

    private sealed class CodingToolsExtension(IImageProcessor? images) : IExtension
    {
        public string Name => "coding-tools";

        public IReadOnlyList<IToolRegistration>? Tools =>
        [
            ReadTool.Create(images),
            WriteTool.Create(),
            EditTool.Create(),
            BashTool.Create(),
        ];

        public IReadOnlyList<IPromptSection>? Sections => null;

        public IReadOnlyList<HookRegistration>? Hooks => null;

        public IReadOnlyList<Wrap>? Wraps => null;

        public IReadOnlyList<AnyDurableTask>? Tasks => null;
    }
}
