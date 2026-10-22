using System.Text;
using System.Text.RegularExpressions;

namespace Pi.Tui;

/// <summary>What the program is doing, as reported through OSC 7501 (port of the TS union).</summary>
public enum ProgramStatusState
{
    Idle,
    Working,
    Blocked,
    Done,
    Error,

    /// <summary>Remove the status instead of reporting one.</summary>
    Clear,
}

/// <summary>What a blocked program waits for (port of the TS union).</summary>
public enum ProgramStatusKind
{
    Permission,
    Question,
    Auth,
}

/// <summary>
/// One program-status report (port of <c>program-status.ts</c>'s <c>ProgramStatus</c>). Only the root
/// record is supported.
/// </summary>
public sealed record ProgramStatus(ProgramStatusState State)
{
    /// <summary>Stable program name, <c>[A-Za-z0-9_.+-]{1,32}</c>. Other values are omitted.</summary>
    public string? App { get; init; }

    /// <summary>What a blocked program waits for. Omitted for other states.</summary>
    public ProgramStatusKind? Kind { get; init; }

    /// <summary>One human-readable line. Control characters become spaces; longer text is cut to the spec limit.</summary>
    public string? Message { get; init; }
}

/// <summary>
/// The Program Status Protocol (OSC 7501): a program tells the terminal whether it is idle, working,
/// blocked on the user, done, or failed. Port of <c>program-status.ts</c>.
/// </summary>
/// <remarks>
/// The wire format is <c>ESC ] 7501 ; key=value:key=value ESC \</c>. The optional <c>msg</c> is
/// base64-encoded UTF-8, capped at 2048 decoded bytes so its encoding stays under the 2732-byte
/// encoded limit the spec sets. Terminals discard reports whose text carries control characters, so
/// those are replaced rather than escaped.
/// </remarks>
public static partial class ProgramStatusProtocol
{
    /// <summary>Feature detection query. A supporting terminal replies with the same body.</summary>
    public const string Query = "\x1b]7501;?\x1b\\";

    /// <summary>Upper bound on the decoded <c>msg</c> payload, in UTF-8 bytes.</summary>
    public const int MaxMessageBytes = 2048;

    [GeneratedRegex(@"\A\x1b\]7501;\?[^\x07\x1b]*(?:\x07|\x1b\\)\z")]
    private static partial Regex ReplyRegex();

    [GeneratedRegex(@"\A[A-Za-z0-9_.+-]{1,32}\z")]
    private static partial Regex AppRegex();

    [GeneratedRegex(@"[\u0000-\u001f\u007f-\u009f]+")]
    private static partial Regex ControlCharactersRegex();

    /// <summary>
    /// Whether <paramref name="sequence"/> is the reply to <see cref="Query"/>. Later spec revisions may
    /// add pairs after the <c>?</c>.
    /// </summary>
    public static bool IsReply(string sequence) => ReplyRegex().IsMatch(sequence);

    /// <summary>Encodes a status report.</summary>
    public static string Format(ProgramStatus status)
    {
        var pairs = new List<string> { $"state={StateName(status.State)}" };
        if (status.App is { } app && AppRegex().IsMatch(app))
        {
            pairs.Add($"app={app}");
        }

        if (status.State == ProgramStatusState.Blocked && status.Kind is { } kind)
        {
            pairs.Add($"kind={KindName(kind)}");
        }

        var message = TruncateUtf8(JsString.Trim(ControlCharactersRegex().Replace(status.Message ?? "", " ")), MaxMessageBytes);
        if (message.Length > 0)
        {
            pairs.Add($"msg={Convert.ToBase64String(Encoding.UTF8.GetBytes(message))}");
        }

        return $"\x1b]7501;{string.Join(':', pairs)}\x1b\\";
    }

    /// <summary>Wire spelling of a state.</summary>
    public static string StateName(ProgramStatusState state) => state switch
    {
        ProgramStatusState.Idle => "idle",
        ProgramStatusState.Working => "working",
        ProgramStatusState.Blocked => "blocked",
        ProgramStatusState.Done => "done",
        ProgramStatusState.Error => "error",
        ProgramStatusState.Clear => "clear",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown program status state"),
    };

    /// <summary>Wire spelling of a blocked kind.</summary>
    public static string KindName(ProgramStatusKind kind) => kind switch
    {
        ProgramStatusKind.Permission => "permission",
        ProgramStatusKind.Question => "question",
        ProgramStatusKind.Auth => "auth",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown program status kind"),
    };

    /// <summary>
    /// Cuts <paramref name="text"/> to at most <paramref name="maxBytes"/> UTF-8 bytes, never splitting a
    /// code point (the TS version iterates <c>for…of</c>, i.e. by code point).
    /// </summary>
    private static string TruncateUtf8(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return text;
        }

        var bytes = 0;
        var end = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (bytes + size > maxBytes)
            {
                break;
            }

            bytes += size;
            end += rune.Utf16SequenceLength;
        }

        return text[..end];
    }
}
