using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

// 测试用假守护进程：模拟 pi-env 的启动形状（`serve --token <hex>`）与几种传输边界。
// 用法：Pi.Env.FakeDaemon serve --token <hex> --script <noise|corrupt|garbage-json|hello>
var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
if (!arguments.Contains("serve", StringComparer.Ordinal))
{
    Console.Error.WriteLine("usage: Pi.Env.FakeDaemon serve --token <hex> --script <name>");
    return 2;
}

string? Value(string name)
{
    var index = Array.IndexOf(arguments, name);
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}

var token = Value("--token") ?? "";
var script = Value("--script") ?? "hello";
var stdout = Console.OpenStandardOutput();

void Write(byte[] bytes)
{
    stdout.Write(bytes, 0, bytes.Length);
    stdout.Flush();
}

// 登录 shell 的横幅：客户端必须丢弃同步行之前的一切。
if (script == "noise") Write(Encoding.UTF8.GetBytes("banner from the login shell\n"));
Write(Encoding.UTF8.GetBytes($"PI-ENV {token}\n"));

switch (script)
{
    case "corrupt":
        // 帧长度 5 < 9：长度字段本身非法。
        Write([0, 0, 0, 5, 1, 0, 0, 0, 1, 0, 0, 0, 0]);
        break;
    case "garbage-json":
        Write(Frame(2, 1, Encoding.UTF8.GetBytes("not json")));
        break;
    default:
        Write(Frame(2, 1, Encoding.UTF8.GetBytes(Hello())));
        break;
}

// 守护进程活到 stdin 结束（客户端关闭时）。
try
{
    Console.OpenStandardInput().CopyTo(Stream.Null);
}
catch (IOException)
{
}

return 0;

static byte[] Frame(byte type, uint id, byte[] json, byte[]? payload = null)
{
    payload ??= [];
    var frame = new byte[13 + json.Length + payload.Length];
    BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)(9 + json.Length + payload.Length));
    frame[4] = type;
    BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(5), (uint)id);
    BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(9), (uint)json.Length);
    json.CopyTo(frame, 13);
    payload.CopyTo(frame, 13 + json.Length);
    return frame;
}

static string Hello() => new JsonObject
{
    ["protocol"] = 1,
    ["version"] = "fake",
    ["os"] = "windows",
    ["arch"] = "x64",
    ["home"] = "/home/fake",
    ["tmpdir"] = "/tmp",
    ["separator"] = "/",
    ["cwd"] = "/",
    ["driveCwds"] = new JsonObject(),
    ["pid"] = 4242,
}.ToJsonString();
