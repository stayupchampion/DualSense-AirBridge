namespace DsBridge.Core;

public sealed record AudioEndpointState(string Id,string Name,uint State);
public sealed record AudioObservation(DateTimeOffset CapturedAt,AudioDefault[] Defaults,AudioEndpointState[] Endpoints,string Error);
public sealed record ControllerObservation(DateTimeOffset CapturedAt,string InstanceId,bool Present,bool InputReadable,int ReportBytes,string Detail);
public sealed record RecoverySample(DateTimeOffset CapturedAt,AudioObservation Audio,ControllerObservation Controller);

/// <summary>Configuration rollback and observed device health are separate outcomes.</summary>
public static class RecoveryAssessment {
    public static bool ShouldRestoreDefault(AudioState baseline,AudioDefault original,AudioDefault? current,
        IEnumerable<AudioEndpointState> endpoints) {
        if(current is null || Safety.Names.Equals(current.Id,original.Id) ||
            Safety.Contains(baseline.EndpointIds,current.Id))return false;
        if(!endpoints.Any(x=>Safety.Names.Equals(x.Id,original.Id)&&x.State==1))return false;
        return current.Name.Contains("DualSense",StringComparison.OrdinalIgnoreCase) ||
            current.Name.Contains("Wireless Controller",StringComparison.OrdinalIgnoreCase);
    }
    public static string[] Warnings(AudioState baseline,RecoverySample sample) {
        var warnings=new List<string>();
        if(sample.Audio.Error.Length>0)warnings.Add("音频状态未确认："+sample.Audio.Error);
        else {
            foreach(var original in baseline.Defaults.DistinctBy(x=>x.Id,Safety.Names)) {
                var endpoint=sample.Audio.Endpoints.FirstOrDefault(x=>Safety.Names.Equals(x.Id,original.Id));
                if(endpoint?.State!=1)
                    warnings.Add($"待保留的音频设备当前不可用：{original.Name}（状态 {(endpoint is null?"未枚举":endpoint.State.ToString())}）。未强制切换或重连设备。");
            }
            foreach(var original in baseline.Defaults) {
                var current=sample.Audio.Defaults.FirstOrDefault(x=>x.Flow==original.Flow&&x.Role==original.Role);
                if(current is null)warnings.Add($"停止后没有默认{(original.Flow==0?"播放":"录音")}端点（角色 {original.Role}）。");
                else if(!Safety.Names.Equals(current.Id,original.Id))
                    warnings.Add($"默认{(original.Flow==0?"播放":"录音")}设备与最近记录的选择不同（角色 {original.Role}）：{current.Name}。未覆盖其他设备选择。");
            }
        }
        if(!sample.Controller.InputReadable)
            warnings.Add("未确认选定实体手柄的蓝牙输入："+sample.Controller.Detail+
                " 手柄关机或切换 USB 也会出现此结果；这不会阻止退出或下一次连接。");
        return warnings.Distinct().ToArray();
    }
    public static string Status(bool configurationRestored,bool startupFailed,IEnumerable<string> warnings)=>
        !configurationRestored?"recovery-required":warnings.Any()?"restored-with-warning":startupFailed?"stopped":"restored";
    public static bool CleanupComplete(string status)=>status is "restored" or "restored-with-warning" or "stopped" or "blocked";
    public static string DisplayState(string reported,bool workerAlive)=>
        !workerAlive&&reported is "checking" or "starting" or "active" or "restoring"?"worker-exited":reported;
}
