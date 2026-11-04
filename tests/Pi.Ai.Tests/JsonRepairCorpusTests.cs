using System.Text.Json;
using Pi.Ai.Utils;
using Xunit;

namespace Pi.Ai.Tests;

/// <summary>
/// <c>repairJson()</c> 的差分语料：输入与期望值全部由上游 TypeScript 实跑取得
/// （<c>node --experimental-strip-types tools/gen-ai-json-repair-corpus.mjs</c>）。
///
/// <para>为什么单独做这一份：这条 151 行的手写宽容解析器在每个畸形工具参数上都会跑，而上游没有专门的用例，
/// 端口此前也没有——它属于「没有被验证覆盖的生产路径」。</para>
/// </summary>
public class JsonRepairCorpusTests
{
    [Fact]
    public void RepairsEveryCorpusInputLikeTheTypeScriptOriginal()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "json-repair-corpus.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var cases = document.RootElement;
        Assert.True(cases.GetArrayLength() > 100, $"corpus too small: {cases.GetArrayLength()}");

        var failures = new List<string>();
        foreach (var entry in cases.EnumerateArray())
        {
            var input = entry[0].GetString()!;
            var expected = entry[1].GetString()!;
            var actual = JsonParse.RepairJson(input);
            if (actual != expected)
            {
                failures.Add($"input={JsonSerializer.Serialize(input)}\n  expected={JsonSerializer.Serialize(expected)}\n  actual  ={JsonSerializer.Serialize(actual)}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} mismatch(es):\n{string.Join("\n", failures)}");
    }
}
