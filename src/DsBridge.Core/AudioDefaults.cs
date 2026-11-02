namespace DsBridge.Core;

public sealed record AudioGuardResult(AudioState Preferred,AudioObservation? Before,
    AudioObservation? After,int Restored,string Error);

public static class AudioDefaults {
    public static bool Equivalent(AudioState left,AudioState right)=>
        left.Defaults.SequenceEqual(right.Defaults)&&left.EndpointIds.SequenceEqual(right.EndpointIds,Safety.Names);
    public static bool ControllerName(string name) =>
        name.Contains("DualSense",StringComparison.OrdinalIgnoreCase)||
        name.Contains("Wireless Controller",StringComparison.OrdinalIgnoreCase);

    // Remember observed non-controller choices, including newly connected headphones.
    // A controller-looking endpoint must never become a restoration target by name.
    // Keep the startup endpoint set: it remains the boundary of the existing guard.
    public static AudioState RememberExternal(AudioState previous,AudioObservation current) {
        if(current.Error.Length!=0)return previous;
        var defaults=previous.Defaults.ToList();
        foreach(var now in current.Defaults) {
            if(ControllerName(now.Name)||!current.Endpoints.Any(x=>Safety.Names.Equals(x.Id,now.Id)&&x.State==1))continue;
            var index=defaults.FindIndex(x=>x.Flow==now.Flow&&x.Role==now.Role);
            if(index>=0)defaults[index]=now;else defaults.Add(now);
        }
        return new(defaults.ToArray(),previous.EndpointIds);
    }

    // Platform calls are injected so refusal, partial failure and concurrent user changes
    // are tested without opening a real audio device or modifying Windows defaults.
    public static AudioGuardResult Guard(AudioState baseline,Func<AudioObservation> observe,
        Func<string,bool> isOwned,Func<string,bool> isActive,
        Func<int,int,string?> currentDefault,Action<AudioDefault> restore) {
        AudioObservation? before=null,after=null;var restored=0;
        try {
            before=observe();
            if(before.Error.Length>0)throw new IOException(before.Error);
            baseline=RememberExternal(baseline,before);
            foreach(var original in baseline.Defaults) {
                var now=before.Defaults.FirstOrDefault(x=>x.Flow==original.Flow&&x.Role==original.Role);
                if(!RecoveryAssessment.ShouldRestoreDefault(baseline,original,now,before.Endpoints))continue;
                if(!isOwned(now!.Id))throw new InvalidOperationException("新增手柄音频端点的来源无法确认，已停止桥接，未擅自更改默认输出。");
                if(!isActive(original.Id))continue;
                // A user/another application may have changed this role during enumeration.
                // Recheck immediately before the write. This reduces, not eliminates, races.
                if(!Safety.Names.Equals(currentDefault(original.Flow,original.Role),now.Id))continue;
                restore(original);restored++;
            }
            if(restored>0)after=observe();
            return new(baseline,before,after,restored,"");
        }catch(Exception ex){return new(baseline,before,after,restored,ex.Message);}
    }
}
