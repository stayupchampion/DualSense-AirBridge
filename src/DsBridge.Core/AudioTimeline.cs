namespace DsBridge.Core;

public sealed record AudioAvailability(string Id,string Name,bool Active);
public sealed record AudioTimelineEntry(DateTimeOffset At,string Phase,AudioDefault[] Defaults,
    AudioAvailability[] StartupEndpoints,string Error);

/// <summary>Bounded state-change evidence, not an audio recorder or a connectivity guarantee.</summary>
public sealed class AudioTimeline(AudioState baseline) {
    public const int Capacity=256;
    readonly List<AudioTimelineEntry> entries=[];
    public IReadOnlyList<AudioTimelineEntry> Entries=>entries;
    public bool Record(string phase,AudioObservation observation) {
        var available=baseline.Defaults.DistinctBy(x=>x.Id,Safety.Names)
            .Select(x=>new AudioAvailability(x.Id,x.Name,
                observation.Endpoints.Any(e=>Safety.Names.Equals(e.Id,x.Id)&&e.State==1))).ToArray();
        var entry=new AudioTimelineEntry(observation.CapturedAt,phase,observation.Defaults,available,observation.Error);
        var previous=entries.LastOrDefault();
        if(previous is not null && previous.Phase==phase && previous.Error==entry.Error &&
            previous.Defaults.SequenceEqual(entry.Defaults) && previous.StartupEndpoints.SequenceEqual(available) &&
            entry.At-previous.At<TimeSpan.FromSeconds(30))return false;
        entries.Add(entry);
        if(entries.Count>Capacity)entries.RemoveAt(0);
        return true;
    }
}
