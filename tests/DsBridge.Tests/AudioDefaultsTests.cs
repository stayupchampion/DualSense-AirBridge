using DsBridge.Core;
internal static class AudioDefaultsTests {
    public static void Run(Action<bool,string> check) {
        var a=new AudioDefault(0,0,"a","Headphones A");
        var b=new AudioDefault(0,0,"b","Headphones B");
        var mic=new AudioDefault(1,0,"mic","Microphone");
        var baseline=new AudioState([a,mic],["a","mic"]);
        var observation=new AudioObservation(DateTimeOffset.Now,[b,mic],
            [new("a",a.Name,1),new("b",b.Name,1),new("mic",mic.Name,1)],"");
        var preferred=AudioDefaults.RememberExternal(baseline,observation);
        check(preferred.Defaults[0]==b&&preferred.Defaults[1]==mic,"new playback preference does not overwrite capture role");
        check(preferred.EndpointIds.SequenceEqual(baseline.EndpointIds),"startup endpoint ownership boundary retained");
        var virtualDefault=new AudioDefault(0,0,"ds","DualSense Wireless Controller");
        var stolen=observation with{Defaults=[virtualDefault,mic],Endpoints=[..observation.Endpoints,new("ds",virtualDefault.Name,1)]};
        var retained=AudioDefaults.RememberExternal(preferred,stolen);
        check(retained.Defaults[0]==b,"controller takeover cannot become the saved external preference");
        check(RecoveryAssessment.ShouldRestoreDefault(retained,b,virtualDefault,stolen.Endpoints),"guard restores later user selection B after takeover");
        check(!RecoveryAssessment.ShouldRestoreDefault(retained,b,virtualDefault,[new("ds",virtualDefault.Name,1)]),"disconnected latest preference is not forced active");
        check(AudioDefaults.RememberExternal(baseline,observation with{Error="timeout"})==baseline,"failed observation cannot change preferences");
        check(AudioDefaults.RememberExternal(baseline,observation with{Endpoints=[]}).Defaults[0]==a,"missing active endpoint cannot be remembered");
        check(AudioDefaults.RememberExternal(baseline,observation with{Defaults=[]}).Defaults[0]==a,"missing default cannot invent a preference");
        var communications=new AudioDefault(0,2,"call","Call Headset");
        var added=AudioDefaults.RememberExternal(baseline,new(DateTimeOffset.Now,[communications],[new("call",communications.Name,1)],""));
        check(added.Defaults.Contains(communications)&&added.Defaults.Contains(a),"new communications role remains independent");
        check(AudioDefaults.ControllerName("2- Wireless Controller")&&AudioDefaults.ControllerName("DUALSENSE"),"controller aliases excluded from preference learning");
        check(!AudioDefaults.ControllerName("Bose Mini II SE SoundLink"),"external headphones remain eligible");
        var writes=new List<AudioDefault>();var reads=0;
        var restored=AudioDefaults.Guard(retained,()=>++reads==1?stolen:observation,_=>true,_=>true,(_,_)=>"ds",writes.Add);
        check(restored.Restored==1&&writes.Single()==b,"production guard restores latest external output only");
        check(restored.Before==stolen&&restored.After==observation,"guard returns both pre-write and post-write evidence");
        writes.Clear();
        var refused=AudioDefaults.Guard(retained,()=>stolen,_=>false,_=>true,(_,_)=>"ds",writes.Add);
        check(refused.Error.Length>0&&writes.Count==0&&refused.Before==stolen,"unowned endpoint refuses writes while retaining evidence");
        var race=AudioDefaults.Guard(retained,()=>stolen,_=>true,_=>true,(_,_)=>"user-chosen",writes.Add);
        check(race.Restored==0&&writes.Count==0,"role changed by user during guard is not overwritten");
        var absent=AudioDefaults.Guard(retained,()=>stolen,_=>true,_=>false,(_,_)=>"ds",writes.Add);
        check(absent.Restored==0&&writes.Count==0,"endpoint disappearing before write is not restored");
        var failed=AudioDefaults.Guard(retained,()=>stolen,_=>true,_=>true,(_,_)=>"ds",_=>throw new IOException("denied"));
        check(failed.Error=="denied"&&failed.Before==stolen&&failed.Restored==0,"write failure keeps pre-change observation");
        var readFailed=AudioDefaults.Guard(retained,()=>throw new IOException("unavailable"),_=>true,_=>true,(_,_)=>"ds",writes.Add);
        check(readFailed.Error=="unavailable"&&writes.Count==0,"audio service failure produces no default write");
        var quiet=AudioDefaults.Guard(baseline,()=>observation,_=>throw new Exception("unexpected topology query"),_=>true,(_,_)=>"b",writes.Add);
        check(quiet.Error.Length==0&&writes.Count==0&&quiet.Preferred.Defaults[0]==b,"ordinary external device changes cause no policy writes");
        var both=new AudioState([b,b with{Role=1}],baseline.EndpointIds);
        var two=stolen with{Defaults=[virtualDefault,virtualDefault with{Role=1}]};
        var attempts=0;
        var partial=AudioDefaults.Guard(both,()=>two,_=>true,_=>true,(_,_)=>"ds",_=>{if(++attempts==2)throw new IOException("second role failed");});
        check(partial.Restored==1&&partial.Before==two&&partial.Error.Length>0,"partial correction is reported rather than silently discarded");
    }
}
