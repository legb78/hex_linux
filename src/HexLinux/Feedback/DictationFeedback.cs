using System.Diagnostics.CodeAnalysis;
using HexLinux.Configuration;
using HexLinux.Daemon;
using HexLinux.Diagnostics;

namespace HexLinux.Feedback;

/// <summary>
/// Ties the tone policy to the player that carries it out. Nothing is built
/// for tones the user turned off.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Assembles the audio shell; the decision it follows is FeedbackPolicy, which is tested.")]
public sealed class DictationFeedback
{
    private readonly FeedbackPolicy _policy;
    private readonly SessionLog _log;

    private CueTones? _tones;

    public DictationFeedback(FeedbackMode mode, SessionLog log)
    {
        ArgumentNullException.ThrowIfNull(log);

        _log = log;
        _policy = new FeedbackPolicy(mode);
        _tones = _policy.PlaysTone ? new CueTones(log) : null;
    }

    /// <summary>
    /// True when a tone is actually played, and so when the microphone has a
    /// cue of its own to capture — which the level measurement then skips.
    /// </summary>
    public bool PlaysTone => _policy.PlaysTone;

    /// <summary>Turns the tones on or off while the daemon runs (the tray's "Play tones").</summary>
    public void SetPlaysTone(bool enabled)
    {
        if (_policy.PlaysTone == enabled)
        {
            return;
        }

        _policy.PlaysTone = enabled;
        _tones = enabled ? new CueTones(_log) : null;
    }

    /// <summary>Reflects the state just reached. Called on the daemon's loop.</summary>
    public void Apply(DictationState state)
    {
        CueTone tone = _policy.Next(state);
        _tones?.Play(tone);
    }
}
