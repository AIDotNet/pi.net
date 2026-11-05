using System.Net;
using System.Net.Sockets;

namespace Pi.Ai.Tests;

/// <summary>
/// 进程内唯一的测试端口分配器。
/// <para>
/// 原来的写法是「起一个 0 端口监听、读号码、关掉、返回」——单个类没问题，但四个 HTTP 夹具并行时，
/// 系统很可能把刚刚释放的端口再发给下一个调用方，于是两个 <see cref="HttpListener"/> 抢同一个端口：
/// 后启动的那个抛错，或者请求打到别人的服务器上（断言里表现为「没收到请求体」）。
/// 这里记下发出去的端口，保证一次运行内不重复。
/// </para>
/// </summary>
internal static class TestPorts
{
    private static readonly HashSet<int> Taken = [];
    private static readonly object Gate = new();

    /// <summary>一个本次运行内没有发出去过的环回端口。</summary>
    public static int Next()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            lock (Gate)
            {
                if (Taken.Add(port)) return port;
            }
        }

        throw new InvalidOperationException("找不到未使用的测试端口");
    }
}
