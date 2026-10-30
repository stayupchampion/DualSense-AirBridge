using DsBridge.Core;

namespace DsBridge.App;

/// <summary>Qpc is sampled after ReleaseBuffer succeeds, not at DAC output or physical vibration.
/// FramesAhead uses the stream padding sampled just before GetBuffer plus the first nonzero
/// sample's offset in that block. It is queue diagnostics, not a measured physical delay.</summary>
internal readonly record struct LatencyAudioMarker(int Trial, long Qpc, int FramesAhead, int SampleRate, uint BufferFrames);

internal static partial class Audio
{
    internal static string PlayLatencyPulse(Controller target, CancellationToken cancel, Action<LatencyAudioMarker> onMarker)
    {
        ArgumentNullException.ThrowIfNull(onMarker);
        return PlayTargetAudio(target, false, cancel, onMarker);
    }
}
