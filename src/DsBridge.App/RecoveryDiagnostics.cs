using System.Diagnostics;
using DsBridge.Core;
namespace DsBridge.App;

internal sealed class RecoveryEvidence {
    public int Schema { get; set; }=1;
    public DateTimeOffset Started { get; set; }=DateTimeOffset.Now;
    public DateTimeOffset? Completed { get; set; }
    public string File { get; set; }="";
    public string ControllerInstance { get; set; }="";
    public string Operation { get; set; }="normal-stop";
    public AudioState StartupAudio { get; set; }=new([],[]);
    public AudioState ExpectedAudio { get; set; }=new([],[]);
    public RecoverySample? BeforeStop { get; set; }
    public bool StopSignaledBeforeObservation { get; set; }
    public List<RecoverySample> AfterStop { get; set; }=[];
    public bool ConfigurationRestored { get; set; }
    public double ObservationSeconds { get; set; }
    public string Scope { get; set; }="必要配置恢复步骤执行后约 5 秒的状态采样；诊断不先于配置清理。不是持续监控，不能排除窗口之后的断连。音频 State: 1活动/2禁用/4不存在/8未插接。ExpectedAudio 包含最近观察到的用户音频选择。";
    public List<string> EvidenceErrors { get; set; }=[];
    public string[] Warnings { get; set; }=[];
}
internal static class RecoveryDiagnostics {
    internal static string? BackendOutputWarning(string directory,Journal journal) {
        if(journal.BackendPid==0)return null;
        try {
            var path=Path.Combine(directory,"backend.log");
            if(!System.IO.File.Exists(path))return "缺少本次后端日志，无法确认最终震动归零、扳机释放、麦克风关闭及灯光恢复指令。";
            using var log=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            var evidence=BackendLogEvidence.Read(log);
            if(evidence.NeedsWarning)return "后端未确认最终震动归零、扳机释放及麦克风关闭报告；请检查本次日志。";
            return evidence.LightingNeedsWarning?
                "后端未确认原生灯光恢复指令，灯光可能仍保留桥接状态；请检查本次日志。":null;
        }catch(Exception ex){return "无法核对最终手柄输出："+ex.Message;}
    }
    static void Save(RecoveryEvidence evidence) {
        try{AtomicJson.Write(evidence.File,evidence);}
        catch(Exception ex) {
            var message="恢复诊断文件未完整保存："+ex.Message;
            if(!evidence.EvidenceErrors.Contains(message))evidence.EvidenceErrors.Add(message);
        }
    }
    static async Task<RecoverySample> Capture(Journal journal,TimeSpan limit,AudioWorker worker) {
        using var cancel=new CancellationTokenSource(limit);
        var audio=worker.Observe(cancel.Token);
        var token=cancel.Token;
        var controller=Task.Run(()=>Devices.ObserveNativeInput(journal.InstanceId,journal.Address,token));
        try{await Task.WhenAll(audio,controller).WaitAsync(limit);}
        catch(TimeoutException){}
        finally{cancel.Cancel();}
        var observedAudio=audio.IsCompletedSuccessfully?audio.Result:
            new AudioObservation(DateTimeOffset.Now,[],[],"读取音频端点超时；未更改设备。");
        var observedController=controller.IsCompletedSuccessfully?controller.Result:
            new ControllerObservation(DateTimeOffset.Now,journal.InstanceId,false,false,0,"读取选定手柄状态超时；未重启设备。");
        return new(DateTimeOffset.Now,observedAudio,observedController);
    }
    internal static RecoveryEvidence Begin(string dir,Journal journal,string operation="normal-stop",bool stopSignaled=false) {
        var evidence=new RecoveryEvidence {
            File=Path.Combine(dir,"recovery-check-"+DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff")+"-"+Guid.NewGuid().ToString("N")[..8]+".json"),
            ControllerInstance=journal.InstanceId,StartupAudio=journal.Audio,ExpectedAudio=journal.PreferredAudio??journal.Audio,Operation=operation,
            StopSignaledBeforeObservation=stopSignaled
        };
        journal.RecoveryEvidencePath=evidence.File;
        // Legacy BeforeStop remains null. No audio or HID call precedes essential cleanup.
        Save(evidence);
        return evidence;
    }
    internal static async Task<string[]> Finish(RecoveryEvidence evidence,Journal journal,bool configurationRestored,AudioWorker worker) {
        evidence.ConfigurationRestored=configurationRestored;
        var clock=Stopwatch.StartNew();
        var window=TimeSpan.FromSeconds(5);
        do {
            var remaining=window-clock.Elapsed;
            if(remaining<=TimeSpan.Zero)break;
            evidence.AfterStop.Add(await Capture(journal,
                remaining<TimeSpan.FromSeconds(1)?remaining:TimeSpan.FromSeconds(1),worker));
            Save(evidence);
            remaining=window-clock.Elapsed;
            if(remaining<=TimeSpan.Zero)break;
            await Task.Delay(remaining<TimeSpan.FromMilliseconds(450)?remaining:TimeSpan.FromMilliseconds(450));
        }while(clock.Elapsed<window);
        evidence.ObservationSeconds=Math.Round(clock.Elapsed.TotalSeconds,3);
        var warnings=evidence.AfterStop.Count==0?new List<string>{"未完成停止后的设备状态检查。"}:
            RecoveryAssessment.Warnings(evidence.ExpectedAudio,evidence.AfterStop[^1]).ToList();
        var ids=evidence.ExpectedAudio.Defaults.Select(x=>x.Id).Distinct(Safety.Names).ToArray();
        if(evidence.AfterStop.Any(s=>s.Audio.Error.Length==0&&ids.Any(id=>
            !s.Audio.Endpoints.Any(e=>Safety.Names.Equals(e.Id,id)&&e.State==1))) &&
            evidence.AfterStop.Count>0&&evidence.AfterStop[^1].Audio.Error.Length==0&&
            ids.All(id=>evidence.AfterStop[^1].Audio.Endpoints.Any(e=>Safety.Names.Equals(e.Id,id)&&e.State==1)))
            warnings.Add("观察窗口内曾有待保留的音频端点不可用，最后一次采样已恢复；此记录不能确定断连原因。");
        evidence.Completed=DateTimeOffset.Now;
        evidence.Warnings=warnings.Concat(evidence.EvidenceErrors).Distinct().ToArray();
        Save(evidence);
        evidence.Warnings=warnings.Concat(evidence.EvidenceErrors).Distinct().ToArray();
        return evidence.Warnings;
    }
    internal static async Task ObserveOnce(string directory,string outputFile) {
        var dir=Paths.CheckSession(directory);
        var journalFile=Path.Combine(dir,"journal.json");
        if(!System.IO.File.Exists(journalFile))throw new InvalidDataException("会话 journal.json 不存在，拒绝观察未知目标。");
        var journal=AtomicJson.Read<Journal>(journalFile);
        if(journal.Schema!=1||!Guid.TryParseExact(journal.Id,"N",out _)||!Safety.IsAddress(journal.Address)||
            !journal.InstanceId.StartsWith("HID\\",StringComparison.OrdinalIgnoreCase)||
            !System.Text.RegularExpressions.Regex.IsMatch(journal.InstanceId,@"(?i)VID(?:_|&0002)054C[&_].*PID(?:_|&)0CE6"))
            throw new InvalidDataException("会话记录中的目标不是有效的普通 DualSense。");
        var output=Path.GetFullPath(outputFile);
        var state=Path.GetFullPath(Paths.State)+Path.DirectorySeparatorChar;
        var sessions=Path.GetFullPath(Paths.Sessions)+Path.DirectorySeparatorChar;
        if(!output.StartsWith(state,StringComparison.OrdinalIgnoreCase)||output.StartsWith(sessions,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只读观察的输出必须在本工具 .local 内，且不能写入 sessions 恢复记录目录。");
        if(System.IO.File.Exists(output))throw new IOException("观察输出文件已存在；请为每次采样使用新文件名。");
        var clock=Stopwatch.StartNew();
        await using var worker=new AudioWorker(readOnly:true);
        var sample=await Capture(journal,TimeSpan.FromSeconds(2),worker);
        AtomicJson.Write(output,new {
            Schema=1,ReadOnlyDeviceCheck=true,SessionId=journal.Id,RecordedConfigurationStage=journal.Stage,
            ControllerInstance=journal.InstanceId,StartupAudio=journal.Audio,Sample=sample,
            ObservationSeconds=Math.Round(clock.Elapsed.TotalSeconds,3),
            Warnings=RecoveryAssessment.Warnings(journal.PreferredAudio??journal.Audio,sample),
            Scope="单次只读采样；不切换默认音频、不解除隐藏、不重连设备或重启驱动/服务。"
        });
    }

    internal static string Summary(RecoveryEvidence? evidence,IEnumerable<string> warnings) {
        var detail=warnings.Any()?"设备状态存在未确认项："+Environment.NewLine+string.Join(Environment.NewLine,warnings):
            evidence is null?"未完成原生输入与音频状态确认。":
            "观察窗口内读到实体蓝牙输入，待保留的默认音频端点处于活动状态。";
        return detail+Environment.NewLine+
            "检查仅覆盖停止后约 5 秒，不能保证之后不掉线，也不代表游戏已重新识别原生蓝牙手柄。"+
            (evidence is null?"":Environment.NewLine+"诊断记录："+evidence.File);
    }
}
