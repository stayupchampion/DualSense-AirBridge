using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DsBridge.Core;

namespace DsBridge.App;

internal sealed record SessionRequest(Controller Controller,int ParentPid,long ParentStarted,bool SpeakerMuted=true);
internal sealed record SessionStatus(string State,string Message,int WorkerPid,long WorkerStarted,DateTimeOffset Updated);
internal static class SessionWorker {
    static string StatusPath(string dir)=>Path.Combine(dir,"status.json");
    static void Status(string dir,string state,string message) {
        using var p=Process.GetCurrentProcess();
        AtomicJson.Write(StatusPath(dir),new SessionStatus(state,message,p.Id,p.StartTime.ToUniversalTime().Ticks,DateTimeOffset.Now));
    }
    public static IEnumerable<string> Pending() {
        if(!Directory.Exists(Paths.Sessions)) yield break;
        foreach(var dir in Directory.EnumerateDirectories(Paths.Sessions)) {
            var file=Path.Combine(dir,"journal.json");
            if(!File.Exists(file)) continue;
            Journal? journal=null;
            try { journal=AtomicJson.Read<Journal>(file); } catch {}
            if(journal is null || journal.Stage!="restored") yield return dir;
        }
    }
    public static async Task<int> Start(string directory) {
        var dir=Paths.CheckSession(directory);
        using var mutex=new Semaphore(1,1,@"Local\DsBridge-Session");
        var locked=false;
        try { locked=mutex.WaitOne(0); } catch(AbandonedMutexException) { locked=true; }
        if(!locked) { Status(dir,"blocked","已有桥接或恢复操作正在运行。"); return 2; }
        Journal? j=null; Process? backend=null; EventWaitHandle? stop=null; ChildJob? job=null;
        var issues=new List<string>(); var initialError="";
        await using var audioWorker=new AudioWorker();
        SessionStopMonitor? stopMonitor=null;
        var recoveryWarnings=new List<string>(); RecoveryEvidence? recoveryEvidence=null;
        AudioTimeline? audioTimeline=null;
        void TraceAudio(string phase,AudioObservation observation) {
            audioTimeline?.Record(phase,observation);
        }
        async Task<int> GuardAudio(string phase,CancellationToken cancel=default) {
            var result=await audioWorker.Guard(j!.PreferredAudio??j.Audio,cancel);
            if(!AudioDefaults.Equivalent(j.PreferredAudio??j.Audio,result.Preferred)) {
                j.PreferredAudio=result.Preferred;Save();
            }
            if(result.Before is not null)TraceAudio(phase,result.Before);
            if(result.After is not null)TraceAudio(phase+"-corrected",result.After);
            if(result.Error.Length>0)throw new IOException(result.Error);
            return result.Restored;
        }
        void SaveAudioTimeline() {
            try {
                if(audioTimeline is not null)
                    AtomicJson.Write(Path.Combine(dir,"audio-timeline.json"),new {
                        Schema=1,StartupAudio=j?.Audio,Entries=audioTimeline.Entries,
                        Scope="默认音频和启动时端点的可用性；变化时及每 30 秒在内存记录，最多 256 条，清理后保存。不录制声音，不表示实际听感或精确断连时刻。"
                    });
            }catch(Exception ex){
                var warning="音频状态时间线未完整保存："+ex.Message;
                if(!recoveryWarnings.Contains(warning))recoveryWarnings.Add(warning);
            }
        }
        void Save() { if(j is not null) AtomicJson.Write(Path.Combine(dir,"journal.json"),j); }
        void Note(string value) {if(j is not null&&!j.Notes.Contains(value))j.Notes.Add(value);Save();}
        void RecoverySave() { try { Save(); } catch(Exception ex) { issues.Add("恢复记录写入失败："+ex.Message); } }
        void RecoveryStatus(string value,string message) { try { Status(dir,value,message); } catch(Exception ex) { issues.Add("恢复状态写入失败："+ex.Message); } }
        try {
            Status(dir,"checking","检查依赖、其他手柄软件与恢复记录…");
            var request=AtomicJson.Read<SessionRequest>(Path.Combine(dir,"request.json"));
            if(!Run.IsAlive(request.ParentPid,request.ParentStarted)) throw new InvalidOperationException("主窗口已经退出，取消启动。");
            if(!Paths.Admin) throw new InvalidOperationException("启用桥接需要管理员权限。");
            if(Pending().Any(x=>!string.Equals(x,dir,StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("检测到未完成的恢复记录，请先恢复。");
            stopMonitor=new SessionStopMonitor(
                ()=>File.Exists(Path.Combine(dir,"stop.request"))||!Run.IsAlive(request.ParentPid,request.ParentStarted),
                ()=>stop?.Set());
            var cancel=stopMonitor.Token;
            var probe=await Probe.Read(()=>audioWorker.Snapshot(cancel),cancel);
            cancel.ThrowIfCancellationRequested();
            if(probe.Blockers.Length!=0) throw new InvalidOperationException(string.Join("\n",probe.Blockers));
            var selected=Devices.Enumerate().FirstOrDefault(x=>x.InstanceId==request.Controller.InstanceId &&
                x.Address==request.Controller.Address && x.Transport=="蓝牙");
            if(selected is null) throw new InvalidOperationException("选定的蓝牙手柄已变化，未修改系统。");
            var hide=new HidHide(cancel); var original=await hide.Read();
            Safety.Validate(original,selected.InstanceId);
            j=new Journal { BackendPath=Paths.Backend,InstanceId=selected.InstanceId,Address=selected.Address,
                Original=original,Audio=await audioWorker.Snapshot(cancel) };
            audioTimeline=new(j.Audio);
            Save();
            var db=Path.Combine(dir,"controller.jsonl");
            File.WriteAllText(db,JsonSerializer.Serialize(new {address=selected.Address,profile="ds5",ports=new[]{0}})+"\n",new UTF8Encoding(false));
            var pipe="DsBridge-"+j.Id;
            var desiredMute=request.SpeakerMuted;
            BackendLiveStatus? lastControl=null;
            var nextTelemetry=DateTime.UtcNow;
            async Task<bool> Ready() {
                var preferenceError="";
                var preferences=Path.Combine(dir,"preferences.json");
                try {
                    if(File.Exists(preferences)) {
                        if(new FileInfo(preferences).Length>4096)throw new InvalidDataException("设置文件过大");
                        desiredMute=AtomicJson.Read<SpeakerPreference>(preferences).SpeakerMuted??
                            throw new InvalidDataException("缺少静音选项");
                    }
                }catch(Exception ex) when(ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException) {
                    preferenceError="未应用设置："+ex.Message;
                }
                var current=await BridgeControl.Query(pipe,lastControl?.SpeakerMuted==desiredMute?null:desiredMute,cancel);
                if(DateTime.UtcNow>=nextTelemetry||lastControl?.SpeakerMuted!=current.SpeakerMuted||
                    lastControl?.Connected!=current.Connected||preferenceError.Length>0) {
                    BridgeControl.Save(dir,new(DateTimeOffset.UtcNow,current.Address,current.Connected,
                        current.BatteryRaw,current.BatteryAgeMs,current.SpeakerMuted,preferenceError));
                    nextTelemetry=DateTime.UtcNow.AddSeconds(5);
                }
                lastControl=current;
                return current.Connected&&Safety.Names.Equals(current.Address,j.Address);
            }
            var eventName=@"Local\DsBridge-"+j.Id;
            stop=new EventWaitHandle(false,EventResetMode.ManualReset,eventName,out var created);
            if(!created) throw new InvalidOperationException("会话停止事件名称冲突。");
            cancel.ThrowIfCancellationRequested();
            Status(dir,"starting","准备目标手柄；将保留其他设备的现有配置…");
            await Safety.Apply(hide,j,Save);
            cancel.ThrowIfCancellationRequested();
            j.Stage="starting"; Save();
            job=new ChildJob();
            backend=Process.Start(Run.Info(Paths.Backend,"--db-path",db,"--log",Path.Combine(dir,"backend.log"),
                "--pipe",@"\\.\pipe\"+pipe,"--stop-event",eventName))
                ?? throw new InvalidOperationException("后端未启动。");
            job.Add(backend);
            j.BackendPid=backend.Id; j.BackendStarted=backend.StartTime.ToUniversalTime().Ticks; Save();
            var stdout=backend.StandardOutput.ReadToEndAsync();
            var stderr=backend.StandardError.ReadToEndAsync();
            var connectionClock=Stopwatch.StartNew();var timing=new BridgeTiming();bool ready=false;
            while(!timing.StartupExpired(connectionClock.Elapsed)) {
                if(backend.HasExited) throw new InvalidOperationException("桥接后端退出：" + await stderr);
                if(File.Exists(Path.Combine(dir,"stop.request")) || !Run.IsAlive(request.ParentPid,request.ParentStarted))
                    throw new OperationCanceledException("已取消启动，正在恢复。");
                if(await GuardAudio("starting",cancel)>0) {
                    timing.AudioCorrected(connectionClock.Elapsed);
                    Note("启动期间已恢复原默认音频。");
                }
                bool connected=false;
                try { connected=await Ready(); } catch(IOException) {} catch(TimeoutException) {} catch(OperationCanceledException) when(!cancel.IsCancellationRequested) {}
                // USB audio endpoints appear after the HID handshake. Allow enumeration and
                // default-device correction to settle before telling the user to launch a game.
                ready=timing.StartupReady(connectionClock.Elapsed,connected);
                if(ready) break;
                await Task.Delay(150,cancel);
            }
            if(!ready) throw new InvalidOperationException("未在 25 秒内确认蓝牙和虚拟 USB 连接。正在恢复；详情见本次日志。");
            j.Stage="active"; Save();
            Status(dir,"active","桥接连接已建立。原生触觉、扳机与耳机并行效果仍需在游戏内验收。");
            timing.Healthy(connectionClock.Elapsed);
            while(!File.Exists(Path.Combine(dir,"stop.request")) && Run.IsAlive(request.ParentPid,request.ParentStarted)) {
                if(backend.HasExited) throw new InvalidOperationException("桥接连接已结束，正在恢复原生连接。重新连接手柄后请手动启用桥接。");
                var restored=await GuardAudio("active",cancel);
                if(restored>0)Note("已将新建 DualSense 占用的默认角色恢复到最近的外部音频设备。");
                var current=await hide.Read();
                if(!current.Active || current.Inverse || !Safety.Contains(current.Devices,j.InstanceId))
                    throw new InvalidOperationException("设备隐藏配置被外部更改，桥接已停止以避免双输入。");
                try { if(await Ready()) timing.Healthy(connectionClock.Elapsed); } catch(IOException) {} catch(OperationCanceledException) when(!cancel.IsCancellationRequested) {}
                if(timing.ContactExpired(connectionClock.Elapsed))
                    throw new InvalidOperationException("手柄连接中断，已结束本次桥接。重新连接后可手动启用。");
                await Task.Delay(500,cancel);
            }
        } catch(OperationCanceledException) when(stopMonitor?.Token.IsCancellationRequested==true) {
            // User stop/UI exit is normal cleanup, including cancellation of a stuck audio call.
        } catch(Exception ex) { initialError=ex.Message; }
        finally {
            // Signal first; no diagnostic/COM call is allowed ahead of the backend stop.
            try {stop?.Set();}catch(Exception ex){issues.Add("停止事件设置失败："+ex.Message);}
            if(stopMonitor is not null)try {await stopMonitor.DisposeAsync();}catch(Exception ex){issues.Add("停止观察未完成："+ex.Message);}
            await using var recoveryAudio=audioWorker.IsUsable?audioWorker:new AudioWorker();
            RecoveryStatus("restoring","停止桥接并核对恢复结果…");
            if(j is not null) {
                j.Stage="restoring"; RecoverySave();
                if(backend is not null && !backend.HasExited) {
                    try { await backend.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
                    catch {
                        issues.Add("后端未正常退出，已尝试结束本次进程；恢复状态需要复核。");
                        try { backend.Kill(entireProcessTree:true); await backend.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch {}
                    }
                }
                // Reap owned command descendants before revealing the physical controller.
                // Never let optional diagnostics prolong their lifetime.
                try {job?.Dispose();job=null;}
                catch(Exception ex){issues.Add("本次后端子进程回收未确认："+ex.Message);}
                if(j.HideIntent || j.RegisterIntent || j.CloakIntent) {
                    try { issues.AddRange(await Safety.Restore(new HidHide(),j,RecoverySave)); }
                    catch(Exception ex) { issues.Add("恢复隐藏配置失败："+ex.Message); }
                }
                try {
                    var result=await recoveryAudio.Guard(audioWorker.Preferred??j.PreferredAudio??j.Audio);
                    j.PreferredAudio=result.Preferred;
                    if(result.Before is not null)TraceAudio("restoring",result.Before);
                    if(result.After is not null)TraceAudio("restoring-corrected",result.After);
                    if(result.Error.Length>0)throw new IOException(result.Error);
                } catch(Exception ex) { recoveryWarnings.Add("无法确认默认音频恢复："+ex.Message); }
                if(j.BackendPid!=0) {
                    if(Run.IsAlive(j.BackendPid,j.BackendStarted)) issues.Add("本次后端仍在运行。");
                    try {
                        var ports=await Run.Text(Paths.Usbip,["port"]);
                        if(ports.Contains("usbip://127.0.0.1:3240/1-1",StringComparison.OrdinalIgnoreCase))
                            issues.Add("本次虚拟 USB 端口仍存在，未执行可能影响其他设备的全局断开。");
                    } catch(Exception ex) { issues.Add("无法回读 USB/IP 端口："+ex.Message); }
                }
                // Optional evidence is read only after essential cleanup. A released
                // port alone cannot prove receipt of neutral output by the controller.
                if(RecoveryDiagnostics.BackendOutputWarning(dir,j) is {} outputWarning)recoveryWarnings.Add(outputWarning);
                if(j.BackendPid!=0) {
                    RecoveryStatus("restoring","配置清理已执行，正在观察原生输入与音频状态（约 5 秒）…");
                    try {
                        // Diagnostics start only after essential cleanup, and never share
                        // the policy worker: a short observation timeout must not poison recovery.
                        recoveryEvidence=RecoveryDiagnostics.Begin(dir,j,stopSignaled:true);
                        await using var observer=new AudioWorker(readOnly:true);
                        recoveryWarnings.AddRange(await RecoveryDiagnostics.Finish(recoveryEvidence,j,issues.Count==0,observer));
                        if(recoveryEvidence.AfterStop.Count>0)TraceAudio("after-stop",recoveryEvidence.AfterStop[^1].Audio);
                    }
                    catch(Exception ex){recoveryWarnings.Add("停止后诊断未完成："+ex.Message);}
                }
                // Persist diagnostic history only after device cleanup and observation,
                // never ahead of default-output correction or the backend stop signal.
                SaveAudioTimeline();
                j.RecoveryWarnings=recoveryWarnings.Distinct().ToArray();
                // Device-health warnings do not leave a cleanup journal pending. A user may
                // intentionally power off the controller or change their audio device.
                j.Stage=issues.Count==0 ? "restored" : "recovery-required";
                j.LastError=string.Join("\n",new[]{initialError}.Concat(issues).Where(x=>x.Length>0)); RecoverySave();
            }
            job?.Dispose(); backend?.Dispose(); stop?.Dispose();
            var finalState=RecoveryAssessment.Status(issues.Count==0,initialError.Length>0,recoveryWarnings);
            var finalMessage=issues.Count==0 ? (initialError.Length==0?"桥接已停止，本次配置已清理。":initialError+"\n本次配置已清理。")
                : "本次配置清理尚未完成。\n"+string.Join("\n",issues);
            if(j?.BackendPid>0)finalMessage+="\n"+RecoveryDiagnostics.Summary(recoveryEvidence,recoveryWarnings);
            RecoveryStatus(finalState,finalMessage);
            if(locked) mutex.Release();
        }
        return issues.Count==0 && initialError.Length==0 ? 0 : 1;
    }
    public static async Task<int> Recover(string directory) {
        var dir=Paths.CheckSession(directory);
        using var mutex=new Semaphore(1,1,@"Local\DsBridge-Session");
        bool locked;
        try { locked=mutex.WaitOne(0); } catch(AbandonedMutexException) { locked=true; }
        if(!locked) { Status(dir,"blocked","桥接仍在运行，无法并发恢复。"); return 2; }
        await using var audioWorker=new AudioWorker();
        try {
            if(!Paths.Admin) throw new InvalidOperationException("恢复设备隐藏配置需要管理员权限。");
            var file=Path.Combine(dir,"journal.json"); var j=AtomicJson.Read<Journal>(file);
            if(j.Schema!=1 || !string.Equals(j.BackendPath,Paths.Backend,StringComparison.OrdinalIgnoreCase) ||
                !j.InstanceId.StartsWith("HID\\",StringComparison.OrdinalIgnoreCase) ||
                !System.Text.RegularExpressions.Regex.IsMatch(j.InstanceId,@"(?i)VID(?:_|&0002)054C[&_].*PID(?:_|&)0CE6") ||
                !Guid.TryParseExact(j.Id,"N",out _) || !Safety.IsAddress(j.Address))
                throw new InvalidDataException("恢复记录与当前工具不匹配，已停止自动操作。");
            var recoveryWarnings=new List<string>();RecoveryEvidence? recoveryEvidence=null;
            var stopSignaled=false;
            try { Status(dir,"restoring","检查上次进程并恢复本工具添加的隐藏规则…"); }
            catch(Exception ex){recoveryWarnings.Add("恢复进度未能写入："+ex.Message);}
            if(Run.IsAlive(j.BackendPid,j.BackendStarted)) {
                using var p=Process.GetProcessById(j.BackendPid);
                if(p.StartTime.ToUniversalTime().Ticks!=j.BackendStarted||
                    !string.Equals(p.MainModule?.FileName,j.BackendPath,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("上次进程路径不匹配，已禁止结束其他进程。");
                try {
                    using var ev=EventWaitHandle.OpenExisting(@"Local\DsBridge-"+j.Id);
                    ev.Set();
                    stopSignaled=true;
                    await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                } catch(WaitHandleCannotBeOpenedException) {p.Kill(entireProcessTree:true);await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));}
                  catch(TimeoutException) {p.Kill(entireProcessTree:true);await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));}
            }
            var issues=await Safety.Restore(new HidHide(),j,()=>AtomicJson.Write(file,j));
            try {
                var result=await audioWorker.Guard(j.PreferredAudio??j.Audio);
                j.PreferredAudio=result.Preferred;
                if(result.Error.Length>0)throw new IOException(result.Error);
            } catch(Exception ex) { recoveryWarnings.Add("无法确认默认音频恢复："+ex.Message); }
            if(File.Exists(Paths.Usbip)) {
                try {
                    var ports=await Run.Text(Paths.Usbip,["port"]);
                    if(ports.Contains("usbip://127.0.0.1:3240/1-1",StringComparison.OrdinalIgnoreCase))
                        issues.Add("虚拟 USB 端口仍存在，需确认归属；未操作其他连接。");
                }catch(Exception ex){issues.Add("无法确认本次虚拟 USB 端口已清理："+ex.Message);}
            }
            if(RecoveryDiagnostics.BackendOutputWarning(dir,j) is {} outputWarning)recoveryWarnings.Add(outputWarning);
            if(j.BackendPid!=0) {
                try {
                    recoveryEvidence=RecoveryDiagnostics.Begin(dir,j,"manual-recovery",stopSignaled:stopSignaled);
                    await using var observer=new AudioWorker(readOnly:true);
                    recoveryWarnings.AddRange(await RecoveryDiagnostics.Finish(recoveryEvidence,j,issues.Count==0,observer));
                }
                catch(Exception ex){recoveryWarnings.Add("恢复后诊断未完成："+ex.Message);}
            }
            j.RecoveryWarnings=recoveryWarnings.Distinct().ToArray();
            j.Stage=issues.Count==0?"restored":"recovery-required"; j.LastError=string.Join("\n",issues);
            AtomicJson.Write(file,j);
            var message=issues.Count==0?"本次配置已清理。":j.LastError;
            if(j.BackendPid!=0)message+="\n"+RecoveryDiagnostics.Summary(recoveryEvidence,recoveryWarnings);
            Status(dir,RecoveryAssessment.Status(issues.Count==0,false,recoveryWarnings),message);
            return issues.Count==0?0:1;
        } catch(Exception ex) { Status(dir,"recovery-required",ex.Message); return 1; }
        finally { mutex.Release(); }
    }
}
