using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DsBridge.Core;
namespace DsBridge.App;

internal sealed record AudioRequest(string Command,AudioState? Baseline=null);
internal sealed record AudioReply(AudioObservation? Observation=null,AudioGuardResult? Guard=null,string Error="",VolumeObservation? Volumes=null);

// Persistent private stdio child: Core Audio calls never run on the session coordinator.
// No named service, device stream, timer on the HID/audio data path, or new dependency.
// On timeout/cancellation the process is killed and reaped; a stalled client cannot
// resume to issue more policy calls. This does not roll back an in-flight OS operation.
// A poisoned client never restarts itself.
internal sealed class AudioWorker : IAsyncDisposable {
    readonly SemaphoreSlim gate=new(1,1);
    readonly string? testScenario;
    readonly bool readOnly;
    Process? child;
    ChildJob? job;
    bool unusable;
    public int LastPid { get; private set; }
    public AudioState? Preferred { get; private set; }
    public bool IsUsable=>!unusable;
    internal AudioWorker(string? testScenario=null,bool readOnly=false) { this.testScenario=testScenario;this.readOnly=readOnly; }

    void Start() {
        if(child is not null)return;
        var args=testScenario is null?new[]{readOnly?"--audio-read-worker":"--audio-worker"}:new[]{readOnly?"--audio-read-worker-test":"--audio-worker-test",testScenario};
        var info=Run.Info(Environment.ProcessPath!,args);
        info.RedirectStandardInput=true;
        info.StandardInputEncoding=new UTF8Encoding(false);
        info.StandardOutputEncoding=new UTF8Encoding(false);
        job=new ChildJob();
        child=Process.Start(info)??throw new IOException("音频观察子进程未启动。");
        LastPid=child.Id;
        // The server does no work before its first request, sent only after job assignment.
        job.Add(child);
    }

    async Task Abort() {
        unusable=true;
        job?.Dispose();job=null;
        if(child is not null) {
            try {if(!child.HasExited)child.Kill();}
            catch(InvalidOperationException) when(child.HasExited){}
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            child.Dispose();child=null;
        }
    }

    async Task<AudioReply> Send(AudioRequest request,CancellationToken cancel,TimeSpan? budget=null) {
        await gate.WaitAsync(cancel);
        try {
            if(unusable)throw new IOException("音频观察进程已结束；本次不再重试系统调用。");
            cancel.ThrowIfCancellationRequested();
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(budget??TimeSpan.FromSeconds(5));
            try {
                Start();
                var token=timeout.Token;
                await child!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(),token);
                await child.StandardInput.FlushAsync(token);
                var line=await child.StandardOutput.ReadLineAsync(token)??throw new IOException("音频观察进程提前退出。");
                if(line.Length>1024*1024)throw new InvalidDataException("音频观察回复过大。");
                var result=JsonSerializer.Deserialize<AudioReply>(line)??throw new IOException("音频观察回复无效。");
                if(result.Guard is {} guard)Preferred=guard.Preferred;
                if(result.Error.Length>0)throw new IOException(result.Error);
                return result;
            }catch(OperationCanceledException) {
                await Abort();
                if(cancel.IsCancellationRequested)throw;
                throw new TimeoutException("系统音频调用超时，已结束本次观察进程。");
            }catch {await Abort();throw;}
        }finally{gate.Release();}
    }

    public async Task<AudioState> Snapshot(CancellationToken cancel=default) {
        var result=await Send(new("snapshot"),cancel);
        var observation=result.Observation??throw new IOException("缺少音频快照。");
        return new(observation.Defaults,observation.Endpoints.Where(x=>x.State==1).Select(x=>x.Id).ToArray());
    }
    public async Task<AudioGuardResult> Guard(AudioState baseline,CancellationToken cancel=default) =>
        (await Send(new("guard",Preferred??baseline),cancel)).Guard??throw new IOException("缺少音频保护结果。");
    public async Task<AudioObservation> Observe(CancellationToken cancel=default) {
        try {return (await Send(new("observe"),cancel)).Observation??throw new IOException("缺少音频观察。");}
        catch(Exception ex){return new(DateTimeOffset.Now,[],[],ex.Message);}
    }
    public async Task<VolumeObservation> ReadVolumes(CancellationToken cancel=default) {
        try {return (await Send(new("volumes"),cancel)).Volumes??throw new IOException("缺少音量读数。");}
        catch(Exception ex){return new(DateTimeOffset.Now,[],ex.Message);}
    }
    public async ValueTask DisposeAsync() {
        await gate.WaitAsync();
        try {await Abort();}finally{gate.Release();}
    }

    internal static int Serve(string? fake=null,bool readOnly=false) {
        using var input=new StreamReader(Console.OpenStandardInput(),new UTF8Encoding(false));
        using var output=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false)){AutoFlush=true};
        string? line;
        while((line=input.ReadLine()) is not null) {
            if(line.Length>1024*1024)return 2;
            AudioReply reply;
            try {
                var request=JsonSerializer.Deserialize<AudioRequest>(line)??throw new InvalidDataException("无效音频命令。");
                if(readOnly&&request.Command=="guard")throw new InvalidOperationException("只读音频进程不接受默认设备写入。");
                if(fake is not null) {
                    // Software-only fault injection: deliberately never accesses Core Audio.
                    if(fake=="hang"&&request.Command=="guard")Thread.Sleep(Timeout.Infinite);
                    if(fake=="observe-hang"&&request.Command=="observe")Thread.Sleep(Timeout.Infinite);
                    if(fake=="exit"&&request.Command=="guard")return 3;
                    var observation=new AudioObservation(DateTimeOffset.Now,[new(0,0,"fixture-output","Fixture")],[new("fixture-output","Fixture",1)],"");
                    reply=request.Command switch {
                        "guard"=>new(Guard:new(request.Baseline!,observation,observation,0,"")),
                        "volumes"=>new(Volumes:new(DateTimeOffset.Now,[new("fixture-output","Fixture",[0,1,2],.4f,false,3,"")],"")),
                        _=>new(observation)
                    };
                }else reply=request.Command switch {
                    "snapshot"=>new(Audio.Observe(false)),
                    "observe"=>new(Audio.Observe()),
                    "volumes"=>new(Volumes:Audio.ReadVolumes()),
                    "guard" when request.Baseline is not null=>new(Guard:Audio.GuardDefaults(request.Baseline)),
                    _=>throw new InvalidDataException("不支持的音频命令。")
                };
            }catch(Exception ex){reply=new(Error:ex.Message);}
            output.WriteLine(JsonSerializer.Serialize(reply));
        }
        return 0;
    }
}
