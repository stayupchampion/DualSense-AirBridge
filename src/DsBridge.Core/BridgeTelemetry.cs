namespace DsBridge.Core;

public sealed record BridgeTelemetry(DateTimeOffset Updated,string Address,bool Connected,int BatteryRaw,
    long BatteryAgeMs,bool SpeakerMuted,string PreferenceError) {
    public bool IsCurrent(string address,DateTimeOffset now)=>Connected&&Safety.Names.Equals(Address,address)&&
        now-Updated>=TimeSpan.FromSeconds(-2)&&now-Updated<=TimeSpan.FromSeconds(12);
    public ControllerBattery Battery(string address,DateTimeOffset now)=>
        IsCurrent(address,now)&&BatteryAgeMs>=0&&BatteryAgeMs+(now-Updated).TotalMilliseconds<=15000
        ?ControllerBattery.Decode(BatteryRaw):new(null,"等待新报告");
}
