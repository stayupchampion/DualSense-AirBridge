using System.Text.Json;
using DsBridge.Core;
internal static class RecoveryFaultTests {
    public static async Task Run(Action<bool,string> check) {
        for(int fault=1;fault<=10;fault++) {
            var original=new HideState(false,false,[],[@"C:\other.exe"]);
            var journal=new Journal{Original=original,InstanceId=@"HID\fixture",BackendPath=@"C:\bridge.exe",
                HideIntent=true,RegisterIntent=true,CloakIntent=true};
            var hide=new RestoreHide(new(true,false,[journal.InstanceId],[..original.Applications,journal.BackendPath]),fault);
            var issues=await Safety.Restore(hide,journal,()=>{});
            check(hide.Failed&&issues.Count>0,"recovery reports injected before/after operation failure "+fault);
            hide.FailAt=0;
            check((await Safety.Restore(hide,journal,()=>{})).Count==0&&Safety.Equivalent(hide.State,original),"recovery retry restores only owned state "+fault);
        }
        {
            var original=new HideState(false,false,[],[]);
            var journal=new Journal{Original=original,InstanceId=@"HID\fixture",BackendPath=@"C:\bridge.exe",
                HideIntent=true,RegisterIntent=true,CloakIntent=true};
            var hide=new RestoreHide(new(true,false,[journal.InstanceId],[journal.BackendPath]),0);
            var issues=await Safety.Restore(hide,journal,()=>throw new IOException("disk unavailable"));
            check(issues.Count>0&&Safety.Equivalent(hide.State,original),"journal write failure cannot skip physical hide-rule restoration");
            check((await Safety.Restore(hide,journal,()=>{})).Count==0,"journal failure recovery is retryable");
        }
        var startup=new AudioState([new(0,0,"a","Output A")],["a"]);
        var preferred=new AudioState([new(0,0,"b","Output B")],startup.EndpointIds);
        var recovered=JsonSerializer.Deserialize<Journal>(JsonSerializer.Serialize(new Journal{Audio=startup,PreferredAudio=preferred}))!;
        check(AudioDefaults.Equivalent(recovered.PreferredAudio!,preferred)&&AudioDefaults.Equivalent(recovered.Audio,startup),"latest audio choice survives manager restart without changing startup ownership");
        check(JsonSerializer.Deserialize<Journal>("{\"Schema\":1}")!.PreferredAudio is null,"legacy recovery journals remain readable");
        var now=new RecoverySample(DateTimeOffset.Now,new(DateTimeOffset.Now,preferred.Defaults,[new("b","Output B",1)],""),
            new(DateTimeOffset.Now,"fixture",true,true,64,"read"));
        check(RecoveryAssessment.Warnings(recovered.PreferredAudio!,now).Length==0,"intentional output switch is not reported as failed restoration");
        foreach(var reported in new[]{"checking","starting","active","restoring"})
            check(RecoveryAssessment.DisplayState(reported,false)=="worker-exited","dead manager cannot leave live status visible: "+reported);
        check(RecoveryAssessment.DisplayState("active",true)=="active","running session retains active UI status");
        check(RecoveryAssessment.DisplayState("restored-with-warning",false)=="restored-with-warning","completed recovery keeps health warnings visible");
    }
    sealed class RestoreHide(HideState state,int failAt):IHideAccess {
        public HideState State=state;
        public int FailAt=failAt;
        public bool Failed;
        int calls;
        void Step(){if(++calls==FailAt){Failed=true;throw new IOException("injected cleanup failure");}}
        public Task<HideState> Read(){Step();var result=State;Step();return Task.FromResult(result);}
        public Task Unhide(string id){Step();State=State with{Devices=State.Devices.Where(x=>!Safety.Names.Equals(x,id)).ToArray()};Step();return Task.CompletedTask;}
        public Task Unregister(string path){Step();State=State with{Applications=State.Applications.Where(x=>!Safety.Names.Equals(x,path)).ToArray()};Step();return Task.CompletedTask;}
        public Task SetActive(bool active){Step();State=State with{Active=active};Step();return Task.CompletedTask;}
        public Task Hide(string id)=>throw new InvalidOperationException("recovery must not hide a device");
        public Task Register(string path)=>throw new InvalidOperationException("recovery must not register an app");
    }
}
