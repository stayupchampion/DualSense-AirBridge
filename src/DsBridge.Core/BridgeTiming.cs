namespace DsBridge.Core;

// Only monotonic elapsed time belongs here; wall-clock dates remain in diagnostics.
public sealed class BridgeTiming {
    TimeSpan? connectedSince,audioCorrectedAt;
    TimeSpan lastHealthy;
    public bool StartupExpired(TimeSpan elapsed)=>elapsed>=TimeSpan.FromSeconds(25);
    public void AudioCorrected(TimeSpan elapsed)=>audioCorrectedAt=elapsed;
    public bool StartupReady(TimeSpan elapsed,bool connected) {
        if(connected)connectedSince??=elapsed;else connectedSince=null;
        return connectedSince.HasValue&&elapsed-connectedSince.Value>=TimeSpan.FromSeconds(3)&&
            (!audioCorrectedAt.HasValue||elapsed-audioCorrectedAt.Value>=TimeSpan.FromSeconds(1.5));
    }
    public void Healthy(TimeSpan elapsed)=>lastHealthy=elapsed;
    public bool ContactExpired(TimeSpan elapsed)=>elapsed-lastHealthy>TimeSpan.FromSeconds(5);
}
