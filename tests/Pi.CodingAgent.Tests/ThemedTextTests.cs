// ============================================================================
// Tests for modes/interactive/components/themed-text.ts
// ============================================================================

using Pi.CodingAgent.Modes.Interactive.Components;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class ThemedTextTests
{
    [Fact]
    public void Render_BuildsTheTextOnFirstRender()
    {
        var calls = 0;
        var text = new ThemedText(() =>
        {
            calls++;
            return "hello";
        }, 0, 0);

        var lines = text.Render(20);
        Assert.Equal(1, calls);
        Assert.StartsWith("hello", lines[0]);
    }

    [Fact]
    public void Render_DoesNotRebuildWhileFresh()
    {
        var calls = 0;
        var text = new ThemedText(() =>
        {
            calls++;
            return "hello";
        }, 0, 0);

        text.Render(20);
        text.Render(20);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Invalidate_ForcesARebuild()
    {
        var value = "first";
        var text = new ThemedText(() => value, 0, 0);

        Assert.StartsWith("first", text.Render(20)[0]);

        value = "second";
        text.Invalidate();
        Assert.StartsWith("second", text.Render(20)[0]);
    }

    [Fact]
    public void Constructor_AppliesPadding()
    {
        var text = new ThemedText(() => "hi", 2, 0);
        var lines = text.Render(10);
        Assert.StartsWith("  hi", lines[0]);
    }
}
