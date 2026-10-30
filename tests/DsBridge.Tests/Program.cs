using DsBridge.Core;
using System.Text.Json;
var count=0;
void Check(bool value,string label) { if(!value)throw new Exception("FAIL: "+label); count++;Console.WriteLine("PASS "+label); }
async Task Reject(Func<Task> action,string label) {try{await action();}catch(InvalidOperationException){Check(true,label);return;}throw new Exception("DID NOT REJECT "+label);}
HideState Empty()=>new(false,false,[],[]);
Journal Make(HideState? state=null)=>new(){Original=state??Empty(),BackendPath=@"E:\bridge\backend\vdsd.exe",InstanceId=@"HID\TEST_DS5",Address="01:23:45:67:89:ab"};
Check(Safety.IsUsbipHost(@"ROOT\USB\0000","usbip2_ude",[@"ROOT\USBIP_WIN2\UDE"]),"real USBIP host uses generic PnP instance ID");
Check(!Safety.IsUsbipHost(@"ROOT\USBIP_WIN2\0000","other_driver",[@"ROOT\USBIP_WIN2\UDE"]),"similar instance name cannot authorize audio changes");
Check(!Safety.IsUsbipHost(@"ROOT\USB\0000","usbip2_ude",[@"OTHER"]),"USBIP service alone cannot authorize audio changes");
Check(!Safety.IsUsbipHost(@"PCI\VEN_1234","USBXHCI",[@"PCI\VEN_1234"]),"physical USB host is not USBIP");
Check(!Safety.IsUsbipHost(@"ROOT\USB\0000",null,[]),"missing ownership data fails closed");
Check(Safety.IsAddress("01:23:45:67:89:ab"),"valid address");
Check(!Safety.IsAddress("01:23:45:67:89:ab\n"),"reject trailing newline");
Check(!Safety.IsAddress("01:23;whoami"),"reject command-like address");
Check(HideParser.Flag("--cloak-off\r\n","cloak")==false,"parse inactive");
Check(HideParser.Entries("--dev-hide \"HID\\ONE\"\r\n--dev-hide \"HID\\TWO\"\n","--dev-hide").Length==2,"parse device list");
Check(HideParser.Entries("","--dev-hide").Length==0,"parse empty list");
try{HideParser.Flag("unknown","cloak");throw new Exception("accepted invalid output");}catch(InvalidDataException){Check(true,"unknown output fails closed");}
try{HideParser.Entries("unrecognized output","--dev-hide");throw new Exception("accepted malformed list");}catch(InvalidDataException){Check(true,"malformed list fails closed");}
await Reject(()=>{Safety.Validate(new(false,false,["OTHER"],[]),"SELF");return Task.CompletedTask;},"inactive foreign hidden list blocks start");
await Reject(()=>{Safety.Validate(new(true,true,[],[]),"SELF");return Task.CompletedTask;},"inverse mode blocks start");
await Reject(()=>{Safety.Validate(new(true,false,["SELF"],[]),"SELF");return Task.CompletedTask;},"pre-hidden target blocks takeover");

for(int fault=0;fault<=6;fault++) {
    var fake=new FakeHide(Empty()){FailAt=fault};var j=Make();int saves=0;
    try{await Safety.Apply(fake,j,()=>saves++);}catch(InvalidOperationException){}
    fake.FailAt=0;
    var issues=await Safety.Restore(fake,j,()=>saves++);
    Check(issues.Count==0&&Safety.Equivalent(fake.State,Empty()),"rollback every before/after mutation fault "+fault);
    var again=await Safety.Restore(fake,j,()=>{});
    Check(again.Count==0&&Safety.Equivalent(fake.State,Empty()),"idempotent recovery "+fault);
    Check(saves>0,"write-ahead snapshot "+fault);
}
{
    var initial=new HideState(true,false,["XBOX"],[@"C:\other.exe"]);
    var fake=new FakeHide(initial);var j=Make(initial);
    await Safety.Apply(fake,j,()=>{});
    Check(Safety.Contains(fake.State.Devices,"XBOX"),"preserve other controller while active");
    var issues=await Safety.Restore(fake,j,()=>{});
    Check(issues.Count==0&&Safety.Equivalent(fake.State,initial),"preserve shared config after stop");
}
{
    var initial=new HideState(false,false,[],[@"E:\bridge\backend\vdsd.exe"]);
    var fake=new FakeHide(initial);var j=Make(initial);
    await Safety.Apply(fake,j,()=>{});await Safety.Restore(fake,j,()=>{});
    Check(Safety.Equivalent(fake.State,initial),"preexisting app whitelist survives");
}
{
    var fake=new FakeHide(Empty());var j=Make();
    await Safety.Apply(fake,j,()=>{});
    fake.State=fake.State with {Devices=[..fake.State.Devices,"FOREIGN"],Applications=[..fake.State.Applications,"other.exe"]};
    var issues=await Safety.Restore(fake,j,()=>{});
    Check(issues.Count>0,"concurrent shared change reports incomplete restore");
    Check(fake.State.Active && Safety.Contains(fake.State.Devices,"FOREIGN"),"do not switch off others cloak");
    Check(!Safety.Contains(fake.State.Devices,j.InstanceId),"still remove own hidden target");
}
{
    var fake=new FakeHide(Empty());var j=Make();
    fake.State=new(false,false,[],["changed.exe"]);
    await Reject(()=>Safety.Apply(fake,j,()=>{}),"snapshot race prevents mutation");
    Check(fake.Calls==0,"no writes on stale snapshot");
}
{
    var fake=new FakeHide(Empty());var j=Make();await Safety.Apply(fake,j,()=>{});
    fake.FailRemove=true;
    var issues=await Safety.Restore(fake,j,()=>{});
    Check(issues.Count>0,"failed unhide never reports success");
    fake.FailRemove=false;issues=await Safety.Restore(fake,j,()=>{});
    Check(issues.Count==0,"retry recovery completes");
}

{
    var fake=new FakeHide(Empty());var j=Make();
    await Safety.Apply(fake,j,()=>{});
    var issues=await Safety.Restore(fake,j,()=>throw new IOException("disk full"));
    Check(Safety.Equivalent(fake.State,Empty()),"disk failure never prevents physical config recovery");
    Check(issues.Any(x=>x.Contains("日志")),"disk failure is reported after cleanup");
}
{
    var fake=new FakeHide(Empty());var j=Make();
    try {await Safety.Apply(fake,j,()=>throw new IOException("disk full"));}
    catch(IOException) {}
    Check(fake.Calls==0,"failed write-ahead journal prevents first mutation");
}

var root=Path.Combine(AppContext.BaseDirectory,"test-state");Directory.CreateDirectory(root);
var path=Path.Combine(root,"journal.json");var sample=Make();AtomicJson.Write(path,sample);
sample.HideIntent=true;AtomicJson.Write(path,sample);
Check(AtomicJson.Read<Journal>(path).HideIntent,"durable journal roundtrip");
Check(!File.Exists(path+".new"),"atomic journal leaves no temp after success");

{
    var on=FeedbackSignal.Trigger(false);var off=FeedbackSignal.Trigger(true);
    Check(on.Length==48&&on[0]==2&&on[1]==4&&on[11]==1&&on[12]==40&&on[13]==80,"bounded right-trigger output");
    Check(off[11]==0&&off.Where((_,i)=>i>11).All(x=>x==0),"right-trigger explicit release");
    double left=0,right=0,sound=0;
    for(int n=0;n<48000*5;n++){
        var t=n/48000.0;
        if(FeedbackSignal.Sample(false,0,t)!=0||FeedbackSignal.Sample(false,1,t)!=0)throw new Exception("Haptics leaked into speaker channels");
        if(FeedbackSignal.Sample(true,2,t)!=0||FeedbackSignal.Sample(true,3,t)!=0)throw new Exception("Speaker leaked into haptic channels");
        left=Math.Max(left,Math.Abs(FeedbackSignal.Sample(false,2,t)));
        right=Math.Max(right,Math.Abs(FeedbackSignal.Sample(false,3,t)));
        sound=Math.Max(sound,Math.Abs(FeedbackSignal.Sample(true,0,t)));
    }
    Check(left>0.29&&left<=0.301&&right>0.29&&right<=0.301,"haptic PCM separate channels and bounded levels");
    Check(sound>0.039&&sound<=0.041,"speaker PCM low level and no haptic channels");
    Check(Enumerable.Range(0,4).All(c=>FeedbackSignal.Sample(false,c,4)==0&&FeedbackSignal.Sample(true,c,2)==0),"test audio ends in silence");
}

{
    var original=new AudioDefault(0,0,"bose","Bose Mini II SE");
    var baseline=new AudioState([original],["bose"]);
    var virtualDefault=new AudioDefault(0,0,"virtual-ds","DualSense Wireless Controller");
    Check(!RecoveryAssessment.ShouldRestoreDefault(baseline,original,virtualDefault,
        [new("bose","Bose Mini II SE",8),new("virtual-ds","DualSense Wireless Controller",1)]),
        "offline Bluetooth audio is never forced as default");
    Check(RecoveryAssessment.ShouldRestoreDefault(baseline,original,virtualDefault,
        [new("bose","Bose Mini II SE",1),new("virtual-ds","DualSense Wireless Controller",1)]),
        "active baseline can replace a newly created controller default");
    Check(!RecoveryAssessment.ShouldRestoreDefault(baseline,original,new(0,0,"other","Other Bluetooth Headset"),
        [new("bose","Bose Mini II SE",1),new("other","Other Bluetooth Headset",1)]),
        "new unrelated headset choice is preserved");
    Check(!RecoveryAssessment.ShouldRestoreDefault(new([original],["bose","virtual-ds"]),original,virtualDefault,
        [new("bose","Bose Mini II SE",1),new("virtual-ds","DualSense Wireless Controller",1)]),
        "preexisting endpoint choice is preserved");
    var controllerOff=new ControllerObservation(DateTimeOffset.Now,"HID\\SELECTED",false,false,0,"手柄未连接");
    var audioOff=new AudioObservation(DateTimeOffset.Now,[],[new("bose","Bose Mini II SE",8)],"");
    var missing=RecoveryAssessment.Warnings(baseline,new(DateTimeOffset.Now,audioOff,controllerOff));
    Check(missing.Any(x=>x.Contains("Bose Mini II SE"))&&missing.Any(x=>x.Contains("没有默认"))&&missing.Any(x=>x.Contains("蓝牙输入")),
        "disconnected audio and controller are recorded even with no current default");
    var warningStatus=RecoveryAssessment.Status(true,false,missing);
    Check(warningStatus=="restored-with-warning"&&RecoveryAssessment.CleanupComplete(warningStatus),
        "powered-off controller warns without blocking completed cleanup");
    Check(RecoveryAssessment.Status(false,false,missing)=="recovery-required"&&
        !RecoveryAssessment.CleanupComplete("recovery-required"),"actual cleanup failure remains pending");
    var healthy=new RecoverySample(DateTimeOffset.Now,
        new(DateTimeOffset.Now,[original],[new("bose","Bose Mini II SE",1)],""),
        new(DateTimeOffset.Now,"HID\\SELECTED",true,true,78,"一个输入报告"));
    Check(RecoveryAssessment.Warnings(baseline,healthy).Length==0,"observed input and active matching defaults confirm this sample");
    var changed=healthy with{Audio=new(DateTimeOffset.Now,[new(0,0,"other","Other Headset")],
        [new("bose","Bose Mini II SE",1),new("other","Other Headset",1)],"")};
    Check(RecoveryAssessment.Warnings(baseline,changed).Any(x=>x.Contains("与启动时不同")),
        "changed default is evidence rather than silently claimed restored");
    Check(RecoveryAssessment.Warnings(baseline,healthy with{Audio=new(DateTimeOffset.Now,[],[],"timeout")})
        .Any(x=>x.Contains("未确认")),"failed observation cannot claim healthy audio");
}

DualSenseInputTests.Run(Check);
LatencyPulseSignalTests.Run(Check);

Console.WriteLine($"ALL {count} CHECKS PASSED");

sealed class FakeHide(HideState initial):IHideAccess {
    public HideState State=initial; public int FailAt;public bool FailRemove;public int Calls;
    void Step(){Calls++;if(FailAt!=0&&Calls==FailAt)throw new InvalidOperationException("injected failure");}
    public Task<HideState> Read()=>Task.FromResult(State);
    public Task Register(string p){Step();State=State with{Applications=[..State.Applications,p]};Step();return Task.CompletedTask;}
    public Task Hide(string p){Step();State=State with{Devices=[..State.Devices,p]};Step();return Task.CompletedTask;}
    public Task SetActive(bool a){Step();State=State with{Active=a};Step();return Task.CompletedTask;}
    public Task Unregister(string p){State=State with{Applications=State.Applications.Where(x=>!Safety.Names.Equals(x,p)).ToArray()};return Task.CompletedTask;}
    public Task Unhide(string p){if(FailRemove)throw new InvalidOperationException("injected unhide failure");State=State with{Devices=State.Devices.Where(x=>!Safety.Names.Equals(x,p)).ToArray()};return Task.CompletedTask;}
}
