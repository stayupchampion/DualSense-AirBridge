using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DsBridge.Core;
namespace DsBridge.App;

internal sealed record SpeakerPreference(bool? SpeakerMuted);
internal sealed record BackendLiveStatus(bool OK,bool Connected,string Address,int BatteryRaw,long BatteryAgeMs,bool SpeakerMuted);
internal static class BridgeControl {
    static readonly JsonSerializerOptions Json=new(){PropertyNameCaseInsensitive=true};
    public static async Task<BackendLiveStatus> Query(string pipeName,bool? muted=null) {
        using var timeout=new CancellationTokenSource(1500);
        using var pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.InOut,PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        var command=muted is bool value?JsonSerializer.Serialize(new {command="speaker",mode=value?"mute":"unmute"}):"{\"command\":\"bridge-status\"}";
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(command+"\n"),timeout.Token);
        await pipe.FlushAsync(timeout.Token);
        // The private backend emits a single small JSON line and the timeout bounds reads.
        using var reader=new StreamReader(pipe,new UTF8Encoding(false),false,4096,leaveOpen:true);
        var line=await reader.ReadLineAsync(timeout.Token)??throw new IOException("桥接状态未返回。");
        var result=JsonSerializer.Deserialize<BackendLiveStatus>(line,Json);
        if(result is null||!result.OK)throw new IOException("后端未确认扬声器设置或状态。");
        return result;
    }
    // Disposable UI telemetry: atomic replacement without the durable journal's disk flush.
    public static void Save(string directory,BridgeTelemetry value) {
        var file=Path.Combine(directory,"telemetry.json");
        File.WriteAllText(file+".new",JsonSerializer.Serialize(value));
        File.Move(file+".new",file,true);
    }
}
