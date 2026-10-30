using DsBridge.Core;

internal static class LatencyPulseSignalTests
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (var rate in new[] { 32000, 44100, 44101, 48000, 96000, 192000 })
        {
            check(LatencyPulseSignal.TotalFrames(rate) == 19u * rate, $"latency pulse total duration at {rate} Hz");
            for (int trial = 1; trial <= LatencyPulseSignal.TrialCount; ++trial)
            {
                var start = LatencyPulseSignal.StartFrame(trial, rate);
                var first = LatencyPulseSignal.FirstNonZeroFrame(trial, rate);
                if (first != start + 1 || LatencyPulseSignal.Sample(2, start - 1, rate) != 0
                    || LatencyPulseSignal.Sample(2, start, rate) != 0) throw new Exception("Pulse onset offset mismatch");
                var nonzero = LatencyPulseSignal.Sample(2, first, rate);
                if (nonzero == 0 || (short)Math.Round(nonzero * 32767) == 0 || (float)nonzero == 0)
                    throw new Exception("First nonzero sample vanished during supported format conversion");
                if (LatencyPulseSignal.Sample(2, start + LatencyPulseSignal.PulseFrames(rate), rate) != 0)
                    throw new Exception("Pulse continues beyond duration");
            }
            check(true, $"12 exact first nonzero samples survive PCM16 and float32 conversion at {rate} Hz");
        }
        const int sampleRate = 48000;
        double maximum = 0;
        int activeFrames = 0;
        for (uint frame = 0; frame < LatencyPulseSignal.TotalFrames(sampleRate); ++frame)
        {
            if (LatencyPulseSignal.Sample(0, frame, sampleRate) != 0 || LatencyPulseSignal.Sample(1, frame, sampleRate) != 0)
                throw new Exception("Latency pulses leaked into speaker channels");
            var left = LatencyPulseSignal.Sample(2, frame, sampleRate);
            var right = LatencyPulseSignal.Sample(3, frame, sampleRate);
            if (left != right) throw new Exception("Comparison pulse is not simultaneous on both haptic channels");
            if (frame <= sampleRate && left != 0) throw new Exception("Missing one-second silent lead-in");
            maximum = Math.Max(maximum, Math.Abs(left));
            if (left != 0) ++activeFrames;
        }
        check(maximum > 0.23 && maximum <= 0.24 && maximum <= 0.3 && activeFrames > 0,
            "all pulse samples bounded below existing feedback amplitude");
        check(true, "all 19 seconds keep speaker channels silent and both haptic channels identical");
        check(LatencyPulseSignal.Sample(2, -1, sampleRate) == 0
            && LatencyPulseSignal.Sample(2, 18 * sampleRate, sampleRate) == 0
            && LatencyPulseSignal.Sample(2, 19 * sampleRate, sampleRate) == 0
            && LatencyPulseSignal.Sample(3, 20 * sampleRate, sampleRate) == 0, "lead-in and final tail remain silent including outside duration");
        check(LatencyPulseSignal.StartFrame(12, sampleRate) == 840000
            && LatencyPulseSignal.PulseFrames(sampleRate) == 7200, "last trial starts at 17.5 seconds and each pulse lasts 150 ms");

        var onset = checked((uint)LatencyPulseSignal.FirstNonZeroFrame(1, sampleRate));
        check(LatencyPulseSignal.StartsInBlock(0, onset, sampleRate).Count == 0,
            "block ending before first nonzero frame has no premature submission marker");
        var marker = LatencyPulseSignal.StartsInBlock(onset, 1, sampleRate).Single();
        check(marker.Trial == 1 && marker.OffsetFrames == 0
            && LatencyPulseSignal.StartsInBlock(onset + 1, 1, sampleRate).Count == 0, "half-open block bounds emit onset exactly once");
        check(LatencyPulseSignal.StartsInBlock(onset - 17, 18, sampleRate).Single().OffsetFrames == 17,
            "marker offset identifies the actual sample inside its submitted block");
        check(LatencyPulseSignal.StartsInBlock(onset, 0, sampleRate).Count == 0, "empty block emits no marker");
        var wholeStream = LatencyPulseSignal.StartsInBlock(0, LatencyPulseSignal.TotalFrames(sampleRate), sampleRate);
        check(wholeStream.Count == 12 && wholeStream.Select(x => x.Trial).SequenceEqual(Enumerable.Range(1, 12)),
            "a block spanning multiple trials emits all markers in order");
        var firstChunking = MarkersForChunks(sampleRate, [4800, 240, 509, 31]);
        var secondChunking = MarkersForChunks(sampleRate, [2048, 17, 777]);
        check(firstChunking.SequenceEqual(secondChunking) && firstChunking.Length == 12,
            "physical USB and bridge schedules are identical regardless of render block boundaries");
        for (int i = 0; i < firstChunking.Length; ++i)
            if (firstChunking[i] != LatencyPulseSignal.FirstNonZeroFrame(i + 1, sampleRate)) throw new Exception("Rendered block marker drift");
        check(true, "all chunked markers point at the twelve absolute first nonzero frames");
        foreach (var badRate in new[] { 0, 31999, 192001 })
        {
            try { LatencyPulseSignal.TotalFrames(badRate); throw new Exception("Invalid rate accepted"); }
            catch (ArgumentOutOfRangeException) { }
        }
        foreach (var badTrial in new[] { 0, 13 })
        {
            try { LatencyPulseSignal.StartFrame(badTrial, sampleRate); throw new Exception("Invalid trial accepted"); }
            catch (ArgumentOutOfRangeException) { }
        }
        check(true, "invalid pulse trial and sample-rate inputs reject");
    }

    private static long[] MarkersForChunks(int sampleRate, uint[] sizes)
    {
        var frames = LatencyPulseSignal.TotalFrames(sampleRate);
        List<long> markers = [];
        uint written = 0;
        int block = 0;
        while (written < frames)
        {
            uint count = Math.Min(sizes[block++ % sizes.Length], frames - written);
            foreach (var start in LatencyPulseSignal.StartsInBlock(written, count, sampleRate))
                markers.Add((long)written + start.OffsetFrames);
            written += count;
        }
        return markers.ToArray();
    }
}
