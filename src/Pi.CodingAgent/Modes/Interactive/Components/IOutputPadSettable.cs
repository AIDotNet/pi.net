// ============================================================================
// The output-pad capability the settings selector probes for.
//
// TS's showSettingsSelector walks the chat and pending-message containers with
// `"setOutputPad" in child && typeof child.setOutputPad === "function"` — a
// duck-type probe, because only some transcript components carry the member.
// The port gives those components a shared marker interface so the same walk is
// compile-time checked instead of reflective.
// ============================================================================

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>A transcript component whose output padding can be updated in place.</summary>
public interface IOutputPadSettable
{
    /// <summary>TS <c>setOutputPad(padding)</c>.</summary>
    void SetOutputPad(int padding);
}
