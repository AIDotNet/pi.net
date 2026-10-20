using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// <see cref="Js.ToPrecision"/> 的差分向量。
/// <para>期望值全部由 Node（V8）对同一组字面量实测采出，不是从 C# 实现回抄——
/// 否则断言没有判别力。覆盖四类边界：固定/科学记数法的切换点（<c>1e-7</c>、<c>1e-6</c>、
/// <c>1e-6</c> 下方进位、<c>10^p</c> 进位）、double 精确值低于字面值导致的「看起来该进位却没进」
/// （<c>1.45</c>、<c>0.35</c>）、补齐有效位（<c>0.5</c> → <c>0.50</c>）、以及次正规数。</para>
/// </summary>
public class JsNumberFormatTests
{
    [Theory]
    // 零与整数：toPrecision 总是补齐到 p 位有效数字，零也一样。
    [InlineData(0.0, 1, "0")]
    [InlineData(0.0, 2, "0.0")]
    [InlineData(0.0, 3, "0.00")]
    [InlineData(7.0, 1, "7")]
    [InlineData(7.0, 2, "7.0")]
    [InlineData(7.0, 3, "7.00")]
    // 恰好 10^p：p 位有效数字放不下，改科学记数法。
    [InlineData(100.0, 1, "1e+2")]
    [InlineData(100.0, 2, "1.0e+2")]
    [InlineData(100.0, 3, "100")]
    [InlineData(1234.0, 1, "1e+3")]
    [InlineData(1234.0, 2, "1.2e+3")]
    [InlineData(1234.0, 3, "1.23e+3")]
    [InlineData(123.456, 1, "1e+2")]
    [InlineData(123.456, 2, "1.2e+2")]
    [InlineData(123.456, 3, "123")]
    [InlineData(1e21, 1, "1e+21")]
    [InlineData(1e21, 2, "1.0e+21")]
    [InlineData(1e22, 3, "1.00e+22")]
    // 进位改变指数：9.99 → 10 仍在固定区，99.5 → 100 越过 p。
    [InlineData(9.99, 1, "1e+1")]
    [InlineData(9.99, 2, "10")]
    [InlineData(9.99, 3, "9.99")]
    [InlineData(99.5, 1, "1e+2")]
    [InlineData(99.5, 2, "1.0e+2")]
    [InlineData(99.5, 3, "99.5")]
    // double 的精确值略低于字面值：1.45 不进位、0.35 也不进位；1.5 / 2.5 是精确的，按「向大取」进位。
    [InlineData(1.45, 1, "1")]
    [InlineData(1.45, 2, "1.4")]
    [InlineData(1.45, 3, "1.45")]
    [InlineData(0.35, 1, "0.3")]
    [InlineData(0.35, 2, "0.35")]
    [InlineData(0.35, 3, "0.350")]
    [InlineData(1.5, 1, "2")]
    [InlineData(1.5, 2, "1.5")]
    [InlineData(1.5, 3, "1.50")]
    [InlineData(2.5, 1, "3")]
    [InlineData(2.5, 2, "2.5")]
    [InlineData(0.25, 1, "0.3")]
    [InlineData(0.25, 2, "0.25")]
    // 补齐有效位。
    [InlineData(0.5, 1, "0.5")]
    [InlineData(0.5, 2, "0.50")]
    [InlineData(0.5, 3, "0.500")]
    [InlineData(0.1, 2, "0.10")]
    [InlineData(0.01, 2, "0.010")]
    [InlineData(0.01, 3, "0.0100")]
    // 固定记数法的下界：-6 仍固定，-7 改科学记数法。
    [InlineData(0.0001, 1, "0.0001")]
    [InlineData(0.0001, 2, "0.00010")]
    [InlineData(0.00012345, 2, "0.00012")]
    [InlineData(0.00012345, 3, "0.000123")]
    [InlineData(0.000012345, 2, "0.000012")]
    [InlineData(0.00003, 1, "0.00003")]
    [InlineData(0.00003, 2, "0.000030")]
    [InlineData(0.00003, 3, "0.0000300")]
    [InlineData(1e-6, 1, "0.000001")]
    [InlineData(1e-6, 2, "0.0000010")]
    [InlineData(1e-6, 3, "0.00000100")]
    // 9.999999e-7 的指数是 -7，但两位有效数字进位到 1.0e-6，于是回到固定记数法。
    [InlineData(9.999999e-7, 1, "0.000001")]
    [InlineData(9.999999e-7, 2, "0.0000010")]
    [InlineData(9.999999e-7, 3, "0.00000100")]
    [InlineData(1e-7, 1, "1e-7")]
    [InlineData(1e-7, 2, "1.0e-7")]
    [InlineData(1e-7, 3, "1.00e-7")]
    [InlineData(1.234e-7, 1, "1e-7")]
    [InlineData(1.234e-7, 2, "1.2e-7")]
    [InlineData(1.234e-7, 3, "1.23e-7")]
    // 次正规数：精确值 4.9406…e-324。
    [InlineData(5e-324, 1, "5e-324")]
    [InlineData(5e-324, 2, "4.9e-324")]
    [InlineData(5e-324, 3, "4.94e-324")]
    public void ToPrecisionMatchesV8(double value, int precision, string expected)
        => Assert.Equal(expected, Js.ToPrecision(value, precision));

    [Theory]
    [InlineData(-0.00003, 2, "-0.000030")]
    [InlineData(-1.45, 2, "-1.4")]
    [InlineData(-1234.0, 2, "-1.2e+3")]
    [InlineData(-0.5, 1, "-0.5")]
    public void ToPrecisionKeepsTheSign(double value, int precision, string expected)
        => Assert.Equal(expected, Js.ToPrecision(value, precision));

    [Fact]
    public void ToPrecisionRejectsAPrecisionOutsideTheSpecifiedRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Js.ToPrecision(1.5, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Js.ToPrecision(1.5, 101));
    }

    [Theory]
    [InlineData(1, "1")]
    [InlineData(2, "1.0")]
    [InlineData(21, "1.00000000000000000000")]
    public void ToPrecisionHandlesPrecisionOneAndLongPrecisions(int precision, string expected)
        => Assert.Equal(expected, Js.ToPrecision(1.0, precision));
}
