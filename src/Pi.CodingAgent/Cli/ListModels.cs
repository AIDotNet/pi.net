using System.Globalization;
using Pi.Ai.Models;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Utils;
using Pi.Tui;

namespace Pi.CodingAgent.Cli;

// ============================================================================
// `--list-models` — TS `cli/list-models.ts`（115 行）。
//
// 行为要点：
//   1. 先报 `models.json` 的加载错误（黄字警告，不中断）。
//   2. 目录为空时打 `formatNoModelsAvailableMessage()` 并返回（不是空表）。
//   3. 有搜索词才走 fuzzy 过滤；过滤后为空另打一句 `No models matching "…"`。
//   4. 排序是 provider 再 model id，两级都是 `localeCompare` → CurrentCulture（C79）。
//   5. 列宽 = max(表头长度, 各单元格长度)，列间用**两个空格**分隔，右侧补齐（`padEnd`）。
//
// 拆出一个纯渲染函数 `RenderTable`：TS 里读屏输出和排版混在一个函数里，测试只能
// 抓 stdout；把「模型列表 → 文本行」这段抽成纯函数后可以逐行对拍，IO 只剩
// `Console.WriteLine` 一层。
//
// 平台/类型差异：
//   - TS 的 `count: number` 是 double；这里入参是 `long`（`contextWindow`/`maxTokens`
//     在 C# 侧是 `long`）。除法显式写 `/ 1_000_000.0` 以免落成整数除法。
//   - `toFixed(1)` 与 .NET `"0.0"` 在「恰好一位小数」与「需要舍入」两种情形下一致
//     （1_250_000 → 1.3M 两侧相同，已用测试钉住）；数字格式一律 InvariantCulture，
//     否则德语等区域会打出 `1,3M`。
//   - `padEnd` 按 UTF-16 码元计宽，`PadRight` 同；provider/model id 均为 ASCII，等价。
// ============================================================================

/// <summary>Port of <c>cli/list-models.ts</c>.</summary>
public static class ListModels
{
    private static readonly string[] Headers = ["provider", "model", "context", "max-out", "thinking", "images"];

    /// <summary>
    /// 把 token 数格式化成人类可读形式（200000 → "200K"，1000000 → "1M"）。
    /// 注意 999_999 → "1000.0K"：TS 不会在舍入后回头升级成 M。
    /// </summary>
    internal static string FormatTokenCount(long count)
    {
        if (count >= 1_000_000)
        {
            var millions = count / 1_000_000.0;
            return millions % 1 == 0
                ? $"{millions.ToString("0", CultureInfo.InvariantCulture)}M"
                : $"{millions.ToString("0.0", CultureInfo.InvariantCulture)}M";
        }

        if (count >= 1_000)
        {
            var thousands = count / 1_000.0;
            return thousands % 1 == 0
                ? $"{thousands.ToString("0", CultureInfo.InvariantCulture)}K"
                : $"{thousands.ToString("0.0", CultureInfo.InvariantCulture)}K";
        }

        return count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 排序 + 排版，返回要逐行打印的文本。对应 TS 里从 <c>filteredModels.sort</c> 到函数末尾。
    /// 入参须已过滤且非空（TS 的两个提前返回在上层）。
    /// </summary>
    internal static List<string> RenderTable(IReadOnlyList<ModelSpec> models)
    {
        // provider 再 id，两级 localeCompare → CurrentCulture；LINQ OrderBy 稳定，
        // 与 V8 稳定 sort 一致（同 provider 同 id 不会出现，但保持稳定语义无成本）。
        var sorted = models
            .OrderBy(model => model.Provider, StringComparer.CurrentCulture)
            .ThenBy(model => model.Id, StringComparer.CurrentCulture)
            .ToList();

        var rows = sorted
            .Select(model => new[]
            {
                model.Provider,
                model.Id,
                FormatTokenCount(model.ContextWindow),
                FormatTokenCount(model.MaxTokens),
                model.Reasoning ? "yes" : "no",
                model.Input.Contains(ModelInput.Image) ? "yes" : "no",
            })
            .ToList();

        // TS: Math.max(headers.x.length, ...rows.map(r => r.x.length))——逐列求最大宽度。
        var widths = new int[Headers.Length];
        for (var column = 0; column < Headers.Length; column++)
        {
            var width = Headers[column].Length;
            foreach (var row in rows)
            {
                if (row[column].Length > width)
                {
                    width = row[column].Length;
                }
            }

            widths[column] = width;
        }

        var lines = new List<string>(rows.Count + 1)
        {
            string.Join("  ", Headers.Select((header, column) => header.PadRight(widths[column]))),
        };
        lines.AddRange(rows.Select(row => string.Join("  ", row.Select((cell, column) => cell.PadRight(widths[column])))));
        return lines;
    }

    /// <summary>Filter by provider, ID, or display name using the CLI's existing fuzzy matcher.</summary>
    internal static IReadOnlyList<ModelSpec> FilterModels(IReadOnlyList<ModelSpec> models, string? searchPattern)
        => string.IsNullOrEmpty(searchPattern)
            ? models
            : Fuzzy.Filter(models, searchPattern, model =>
                string.IsNullOrEmpty(model.Name)
                    ? $"{model.Provider} {model.Id}"
                    : $"{model.Provider} {model.Id} {model.Name}");

    /// <summary>对应 TS <c>listModels</c>。</summary>
    public static async Task ListModelsAsync(
        ModelRuntime modelRuntime,
        string? searchPattern = null,
        CancellationToken signal = default)
    {
        var loadError = modelRuntime.GetError();
        if (loadError is not null)
        {
            Console.Error.WriteLine(Chalk.Yellow($"Warning: errors loading models.json:\n{loadError}"));
        }

        var models = await modelRuntime.GetAvailableAsync(null, signal).ConfigureAwait(false);

        if (models.Count == 0)
        {
            Console.WriteLine(AuthGuidance.FormatNoModelsAvailableMessage());
            return;
        }

        var filtered = FilterModels(models, searchPattern);

        if (filtered.Count == 0)
        {
            Console.WriteLine($"No models matching \"{searchPattern}\"");
            return;
        }

        foreach (var line in RenderTable(filtered))
        {
            Console.WriteLine(line);
        }
    }
}
