namespace DsBridge.Core;

public readonly record struct LatencyPulseStart(int Trial, int OffsetFrames);

/// <summary>Identical deterministic PCM for physical USB and the virtual USB bridge.
/// Frame positions describe generated samples, never the time a controller physically vibrates.</summary>
public static class LatencyPulseSignal
{
    public const int TrialCount = 12;
    public const int DurationSeconds = 19;
    public const double IntervalSeconds = 1.5;
    public const double PulseSeconds = 0.15;
    public const double Amplitude = 0.24;
    public const double FrequencyHz = 125;
    public const double EnvelopeSeconds = 0.01;

    public static uint TotalFrames(int sampleRate)
    {
        ValidateRate(sampleRate);
        return checked((uint)(DurationSeconds * sampleRate));
    }

    public static long StartFrame(int trial, int sampleRate)
    {
        ValidateRate(sampleRate);
        if (trial < 1 || trial > TrialCount) throw new ArgumentOutOfRangeException(nameof(trial));
        return sampleRate + ((trial - 1) * 3L * sampleRate + 1) / 2;
    }

    // At offset 0 the envelope is zero. At +1, the cosine carrier is nonzero and the
    // envelope is large enough to survive PCM16 quantization for every supported rate.
    public static long FirstNonZeroFrame(int trial, int sampleRate) => StartFrame(trial, sampleRate) + 1;

    public static int PulseFrames(int sampleRate)
    {
        ValidateRate(sampleRate);
        return (sampleRate * 3 + 10) / 20;
    }

    public static double Sample(int channel, long absoluteFrame, int sampleRate)
    {
        ValidateRate(sampleRate);
        if (channel is not (2 or 3) || absoluteFrame < sampleRate || absoluteFrame >= (long)DurationSeconds * sampleRate) return 0;
        var length = PulseFrames(sampleRate);
        for (int trial = 1; trial <= TrialCount; ++trial)
        {
            var offset = absoluteFrame - StartFrame(trial, sampleRate);
            if (offset < 0) return 0;
            if (offset >= length) continue;
            var envelope = Math.Clamp(Math.Min(offset, length - offset) / (sampleRate * EnvelopeSeconds), 0, 1);
            return Amplitude * envelope * Math.Cos(2 * Math.PI * FrequencyHz * offset / sampleRate);
        }
        return 0;
    }

    /// <summary>Events contained in [firstFrame, firstFrame + frameCount). Emit only after
    /// that block's WASAPI ReleaseBuffer has succeeded; OffsetFrames is within that block.</summary>
    public static IReadOnlyList<LatencyPulseStart> StartsInBlock(uint firstFrame, uint frameCount, int sampleRate)
    {
        ValidateRate(sampleRate);
        List<LatencyPulseStart>? starts = null;
        var end = (long)firstFrame + frameCount;
        for (int trial = 1; trial <= TrialCount; ++trial)
        {
            var first = FirstNonZeroFrame(trial, sampleRate);
            if (first >= firstFrame && first < end)
                (starts ??= []).Add(new(trial, checked((int)(first - firstFrame))));
        }
        return starts is null ? Array.Empty<LatencyPulseStart>() : starts;
    }

    private static void ValidateRate(int rate)
    {
        if (rate < 32000 || rate > 192000) throw new ArgumentOutOfRangeException(nameof(rate), "Expected 32000..192000 Hz.");
    }
}
