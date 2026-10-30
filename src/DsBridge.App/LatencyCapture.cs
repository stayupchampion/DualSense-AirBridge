using System.Diagnostics;
using System.Runtime.InteropServices;
using DsBridge.Core;
using Microsoft.Win32.SafeHandles;
namespace DsBridge.App;

internal enum LatencyKind { Buttons, Sticks, Triggers, Touch, Haptics, Resistance }
internal sealed record LatencyVisual(int Event,long SourceQpc,DualSenseInputSnapshot Input,string Caption,bool Flash,bool Output);
internal sealed record LatencyRead(long Qpc,DualSenseInputSnapshot Input,DualSenseInputChanges Changes);
internal sealed record LatencyPaint(int Event,long SourceQpc,long PaintQpc,bool Output);
internal sealed record LatencyCommand(int Trial,string Action,long BeforeQpc,long AfterQpc,int? FramesAhead=null,int? SampleRate=null,uint? BufferFrames=null);

internal sealed class LatencyCapture {
    readonly object sync=new();
    readonly List<LatencyRead> reads=[];
    readonly List<LatencyPaint> paints=[];
    readonly List<LatencyCommand> commands=[];
    int eventNumber;
    public Controller Target {get;}
    public LatencyKind Kind {get;}
    public string Mode {get;}
    public int CameraFps {get;}
    public DateTimeOffset Started {get;private set;}
    public string Status {get;private set;}="ready";
    public string Error {get;private set;}="";
    public string AudioDetails {get;private set;}="";
    public long Frequency=>Stopwatch.Frequency;
    public DualSenseInputOptions Options {get;}=new();
    public int Count {get{lock(sync)return reads.Count;}}
    public LatencyCapture(Controller target,LatencyKind kind,int cameraFps) {
        Target=target;Kind=kind;CameraFps=cameraFps;Mode=FeedbackTests.Label(target);
    }
    public void Painted(LatencyVisual visual) {
        if(visual.SourceQpc<=0)return;
        lock(sync)paints.Add(new(visual.Event,visual.SourceQpc,Stopwatch.GetTimestamp(),visual.Output));
    }
    static void RequireTarget(Controller target) {
        var games=Process.GetProcessesByName("PBZ");
        try{if(games.Length>0)throw new InvalidOperationException("请先退出《影之刃：零》，再做延迟对比。");}
        finally{foreach(var p in games)p.Dispose();}
        if(!FeedbackTests.Targets().Any(x=>x.InstanceId==target.InstanceId&&x.DevicePath==target.DevicePath))
            throw new InvalidOperationException("所选 USB/虚拟 USB 手柄已变化，请刷新目标。");
    }
    bool Relevant(DualSenseInputChanges c)=>Kind switch {
        LatencyKind.Buttons=>(c.Kinds&(DualSenseChangeKinds.ButtonPressed|DualSenseChangeKinds.ButtonReleased))!=0,
        LatencyKind.Sticks=>(c.Kinds&(DualSenseChangeKinds.LeftStick|DualSenseChangeKinds.RightStick))!=0,
        LatencyKind.Triggers=>(c.Kinds&(DualSenseChangeKinds.LeftTrigger|DualSenseChangeKinds.RightTrigger))!=0,
        LatencyKind.Touch=>(c.Kinds&(DualSenseChangeKinds.TouchDown|DualSenseChangeKinds.TouchUp|DualSenseChangeKinds.TouchMove|DualSenseChangeKinds.TouchpadPressed|DualSenseChangeKinds.TouchpadReleased))!=0,
        _=>false
    };
    public static string Name(LatencyKind k)=>k switch {
        LatencyKind.Buttons=>"按键",LatencyKind.Sticks=>"左右摇杆",LatencyKind.Triggers=>"L2 / R2 输入",
        LatencyKind.Touch=>"触控 / 触控板点击",LatencyKind.Haptics=>"精细震动输出",_=>"R2 阻力开 / 关"
    };
    public static string Instruction(LatencyKind k)=>k switch {
        LatencyKind.Buttons=>"每秒按下并松开一次 × 键，约 20～30 次；同时拍清手指和白色响应区。",
        LatencyKind.Sticks=>"先回中；重复轻推左摇杆、回中，再测试右摇杆。画面显示相同死区与原始位置。",
        LatencyKind.Triggers=>"先松开；分别轻扣 L2、R2 并松开。测输入时不会施加扳机阻力。",
        LatencyKind.Touch=>"分别重复触碰/离开、滑动、按下触控板；手指接触与机械点击分开记录。",
        LatencyKind.Haptics=>"固定手柄，双手离开；拍摄屏幕提交标记和可见振动标记。看不清振动起点时不能据视频给数值。",
        _=>"轻微预压 R2，拍摄开/关标记和扳机位移；不要硬掰。关闭阻力后的下沉可作相对比较，视频不直接测力。"
    };
    public async Task RunAsync(Action<LatencyVisual> visual,CancellationToken cancel,int inputSeconds=30) {
        if(!await FeedbackTests.Gate.WaitAsync(0,cancel))throw new InvalidOperationException("已有手柄测试运行，请先停止。");
        FeedbackTests.HidOutput? output=null;
        DeviceTestLease? lease=null;
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancel);
        Task reading=Task.CompletedTask;
        var inputReady=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? readFailure=null;
        Started=DateTimeOffset.Now;Status="running";
        try {
            lease=DeviceTestLease.Acquire();
            RequireTarget(Target);
            reading=Task.Run(async()=>{
                try{await ReadLoop(visual,inputReady,linked.Token);}
                catch(OperationCanceledException)when(linked.IsCancellationRequested){}
                catch(Exception ex){readFailure=ex;inputReady.TrySetException(ex);linked.Cancel();}
            });
            await inputReady.Task.WaitAsync(TimeSpan.FromSeconds(3),linked.Token);
            if(Kind is LatencyKind.Haptics or LatencyKind.Resistance) {
                output=new FeedbackTests.HidOutput(Target.DevicePath);
                if(Kind==LatencyKind.Haptics) {
                    await output.Write(FeedbackSignal.Audio(false),linked.Token);
                    AudioDetails=await Task.Run(()=>Audio.PlayLatencyPulse(Target,linked.Token,m=>{
                        lock(sync)commands.Add(new(m.Trial,"haptic-sample-queued",m.Qpc,m.Qpc,m.FramesAhead,m.SampleRate,m.BufferFrames));
                        visual(new(Interlocked.Increment(ref eventNumber),m.Qpc,default,$"震动提交 #{m.Trial} / 12",true,true));
                    }),linked.Token);
                    await Task.Delay(400,linked.Token);
                } else {
                    for(int n=1;n<=8;n++) {
                        foreach(bool release in new[]{false,true}) {
                            linked.Token.ThrowIfCancellationRequested();
                            var before=Stopwatch.GetTimestamp();
                            // Flash is requested at the software send boundary. Its actual
                            // display time is unknown until video analysis, never called hardware latency.
                            visual(new(Interlocked.Increment(ref eventNumber),before,default,
                                $"R2 {(release?"关闭阻力":"建立阻力")} #{n} / 8",true,true));
                            try{await output.Write(FeedbackSignal.Trigger(release),linked.Token);}
                            finally{lock(sync)commands.Add(new(n,release?"trigger-release":"trigger-resistance",before,Stopwatch.GetTimestamp()));}
                            await Task.Delay(release?1200:800,linked.Token);
                        }
                    }
                }
            } else await Task.Delay(TimeSpan.FromSeconds(inputSeconds),linked.Token);
            if(Count==0)throw new IOException("未读到有效输入报告，不能确认测试完成。");
            Status="completed";
        }catch(OperationCanceledException)when(cancel.IsCancellationRequested){Status="cancelled";}
         catch(OperationCanceledException){Status="failed";Error=readFailure?.Message??"读取中断或测试超时。";}
         catch(Exception ex){Status="failed";Error=ex.Message;}
        finally {
            linked.Cancel();
            try{await reading;}catch(Exception ex){Error+=" 输入关闭："+ex.Message;}
            if(output is not null) {
                try{await output.Write(Kind==LatencyKind.Resistance?FeedbackSignal.Trigger(true):FeedbackSignal.Audio(false,true),CancellationToken.None);}
                catch(Exception ex){Status="cleanup-failed";Error+=" 输出清理："+ex.Message;}
                try{output.Dispose();}catch(Exception ex){Status="cleanup-failed";Error+=" 输出关闭："+ex.Message;}
            }
            try{lease?.Dispose();}finally{FeedbackTests.Gate.Release();}
        }
        if(readFailure is not null) {
            if(Status!="cleanup-failed")Status="failed";
            if(!Error.Contains(readFailure.Message,StringComparison.Ordinal))Error+=" 输入错误："+readFailure.Message;
        }
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern SafeFileHandle CreateFile(string p,uint a,uint s,IntPtr sa,uint c,uint f,IntPtr t);
    async Task ReadLoop(Action<LatencyVisual> visual,TaskCompletionSource<bool> ready,CancellationToken cancel) {
        using var handle=CreateFile(Target.DevicePath,0x80000000,3,IntPtr.Zero,3,0x40000000,IntPtr.Zero);
        if(handle.IsInvalid)throw new IOException("无法打开所选手柄的输入句柄："+Marshal.GetLastWin32Error());
        using var stream=new FileStream(handle,FileAccess.Read,1,true);
        var bytes=new byte[64];var tracker=new DualSenseInputTracker(Options);bool first=true;
        while(!cancel.IsCancellationRequested) {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancel);deadline.CancelAfter(2500);
            int n;
            try{n=await stream.ReadAsync(bytes,deadline.Token).ConfigureAwait(false);}
            catch(OperationCanceledException)when(!cancel.IsCancellationRequested){throw new IOException("2.5 秒没有读到手柄报告；测试已停止。");}
            var qpc=Stopwatch.GetTimestamp();
            cancel.ThrowIfCancellationRequested();
            if(!DualSenseInput.TryParse(bytes.AsSpan(0,n),out var data))
                throw new IOException("目标返回了非64字节完整USB报告；拒绝套用错误的报告布局。");
            var changes=tracker.Observe(data);
            lock(sync)reads.Add(new(qpc,data,changes));
            ready.TrySetResult(true);
            if(Kind is not (LatencyKind.Haptics or LatencyKind.Resistance)&&(first||Relevant(changes))) {
                visual(new(Interlocked.Increment(ref eventNumber),qpc,data,Name(Kind),false,false));
            }
            first=false;
        }
    }
    static object Distribution(IEnumerable<double> values) {
        var a=values.Where(x=>x>=0).Order().ToArray();
        double? P(double p)=>a.Length==0?null:a[(int)Math.Ceiling((a.Length-1)*p)];
        return new {Count=a.Length,MedianMs=P(.5),P95Ms=P(.95),MaxMs=P(1)};
    }
    public string Save(string? outputFile=null) {
        lock(sync) {
            var file=outputFile??Path.Combine(Paths.State,"latency-tests",DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Kind+"-"+Guid.NewGuid().ToString("N")[..6]+".json");
            var full=Path.GetFullPath(file);
            if(!full.StartsWith(Path.GetFullPath(Paths.State)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("测量记录必须位于本工具 .local 内。");
            if(File.Exists(full))throw new IOException("测量记录已存在，未覆盖。");
            var gaps=reads.Zip(reads.Skip(1),(a,b)=>(b.Qpc-a.Qpc)*1000.0/Frequency);
            var software=paints.Where(x=>!x.Output).Select(x=>(x.PaintQpc-x.SourceQpc)*1000.0/Frequency);
            AtomicJson.Write(full,new {
                Schema=2,AppVersion=typeof(LatencyCapture).Assembly.GetName().Version?.ToString(),Started,Finished=DateTimeOffset.Now,Kind=Kind.ToString(),Mode,TargetInstance=Target.InstanceId,
                CameraCaptureFpsUserDeclared=CameraFps,StopwatchFrequency=Frequency,Options,Status,Error,AudioDetails,
                ReportArrivalIntervals=Distribution(gaps),ReadToPaintCallback=Distribution(software),
                PhysicalInputLatencyMs=(double?)null,PhysicalHapticsLatencyMs=(double?)null,PhysicalTriggerForceLatencyMs=(double?)null,
                Limitations=new[]{
                    "报告间隔和Read到Paint回调耗时不是物理输入到屏幕的延迟；Paint回调不等于像素已经显示。",
                    "输出标记是软件提交边界；显示器扫描/渲染存在延迟。视频可做同条件相对A/B，不能直接把画面间隔当成指令到力/振动的绝对延迟。",
                    "扳机位移是预压力下的间接观测，不是力传感器；建立阻力的准确时刻可能无法从视频判定。",
                    "传感器时间戳属于手柄时钟，未与PC时钟同步；不能直接相减求单程延迟。",
                    "相机拍摄帧率需人工核实；慢动作导出播放帧率不能直接当成拍摄帧率。"
                },Reads=reads,PaintCallbacks=paints,Commands=commands
            });
            return full;
        }
    }
}
