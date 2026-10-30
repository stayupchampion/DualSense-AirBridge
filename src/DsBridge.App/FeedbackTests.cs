using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using DsBridge.Core;
namespace DsBridge.App;
internal enum FeedbackKind { Haptics, Trigger, Speaker }
internal static class FeedbackTests {
    internal static readonly SemaphoreSlim Gate=new(1,1);
    public static Controller[] Targets()=>Devices.Enumerate().Where(x=>x.Transport=="USB"&&Devices.ControllerUsbRoot(x.InstanceId)!=null).ToArray();
    public static string Label(Controller target)=>(Devices.IsUsbipDescendant(target.InstanceId)?"虚拟 USB（USB/IP）":"物理 USB 原生")+" · 普通 DualSense";
    public static async Task<string> RunAsync(Controller target,FeedbackKind kind,CancellationToken cancel,IProgress<string>? progress=null) {
        if(!await Gate.WaitAsync(0,cancel))throw new InvalidOperationException("已有反馈测试正在运行。");
        HidOutput? output=null; DeviceTestLease? lease=null; bool sent=false; string status="failed"; string error="";string audioEvidence="";string mode="尚未确认";
        var started=DateTimeOffset.Now;var timer=Stopwatch.StartNew();var log=Path.Combine(Paths.State,"feedback-tests",Guid.NewGuid().ToString("N")+".json");
        try {
            lease=DeviceTestLease.Acquire();
            cancel.ThrowIfCancellationRequested();
            var games=Process.GetProcessesByName("PBZ");
            var gameRunning=games.Length>0;foreach(var game in games)game.Dispose();
            if(gameRunning)throw new InvalidOperationException("《影之刃：零》仍在运行。请先从游戏菜单正常退出，再做独立反馈测试。");
            if(!Targets().Any(x=>x.InstanceId==target.InstanceId&&x.DevicePath==target.DevicePath))
                throw new InvalidOperationException("目标手柄已变化，请刷新后重试。");
            mode=Label(target);
            output=new HidOutput(target.DevicePath);
            // Mark cleanup intent before a write: a failed or cancelled write may have reached hardware.
            sent=true;
            await output.Write(kind==FeedbackKind.Trigger?FeedbackSignal.Trigger(false):FeedbackSignal.Audio(kind==FeedbackKind.Speaker),cancel);
            if(kind==FeedbackKind.Trigger) {
                progress?.Report("轻按右侧 R2，感受中段阻力；5 秒后释放。不要用力硬掰。");
                await Task.Delay(5000,cancel);
            } else {
                progress?.Report(kind==FeedbackKind.Haptics?"正在通过四声道音频发送：左侧 → 右侧 → 双侧；约 4 秒。":"仅向选定手柄播放两声低音量提示；约 2 秒。");
                audioEvidence=await Task.Run(()=>Audio.PlayFeedback(target,kind==FeedbackKind.Speaker,cancel),cancel);
            }
            status="sent";
        } catch(OperationCanceledException) when(cancel.IsCancellationRequested) {status="cancelled";}
          catch(OperationCanceledException) {error="手柄输出写入超时。";}
          catch(Exception ex) {error=ex.Message;}
        finally {
            if(output is not null) {
                if(sent) try {
                    await output.Write(kind==FeedbackKind.Trigger?FeedbackSignal.Trigger(true):FeedbackSignal.Audio(kind==FeedbackKind.Speaker,true),CancellationToken.None);
                } catch(Exception ex) {status="cleanup-failed";error+=" 停止信号写入失败："+ex.Message+"；请停止桥接或断开这只手柄。";}
                try {output.Dispose();} catch(Exception ex) {status="cleanup-failed";error+=" 输出句柄关闭失败："+ex.Message;}
            }
            try {
                AtomicJson.Write(log,new {Started=started,Finished=DateTimeOffset.Now,Kind=kind.ToString(),
                    TargetInstance=target.InstanceId,Mode=mode,Status=status,Error=error,
                    ElapsedMilliseconds=timer.ElapsedMilliseconds,AudioEvidence=audioEvidence,
                    HumanPerception="尚需使用者确认；发送成功不代表反馈感觉已验收"});
            } finally {try{lease?.Dispose();}finally{Gate.Release();}}
        }
        if(error.Length>0)throw new InvalidOperationException(error+"\n记录："+log);
        return (status=="cancelled"?"已停止并发送复位信号。":"信号已发送并结束；请确认实际感觉，不能仅凭此记录判定通过。")+"\n"+audioEvidence+"\n记录："+log;
    }
    internal sealed class HidOutput:IDisposable {
        readonly FileStream stream;readonly int length;
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr sa,uint creation,uint flags,IntPtr template);
        [DllImport("hid.dll",SetLastError=true)] static extern bool HidD_GetPreparsedData(SafeFileHandle handle,out IntPtr data);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data,[Out]byte[] caps);
        public HidOutput(string path) {
            var handle=CreateFile(path,0x40000000,3,IntPtr.Zero,3,0x40000000,IntPtr.Zero);
            if(handle.IsInvalid){handle.Dispose();throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"无法打开目标 USB 手柄输出");}
            try {
                if(!HidD_GetPreparsedData(handle,out var data))throw new IOException("无法读取 HID 报告长度。");
                try {var caps=new byte[64];if(HidP_GetCaps(data,caps)!=0x110000)throw new IOException("HID 能力无效。");length=BitConverter.ToUInt16(caps,6);}
                finally {HidD_FreePreparsedData(data);}
                if(length<48||length>64)throw new IOException("非预期的 DualSense USB 输出报告长度。");
                stream=new FileStream(handle,FileAccess.Write,4096,true);
            } catch {handle.Dispose();throw;}
        }
        public async Task Write(byte[] report,CancellationToken cancel) {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancel);deadline.CancelAfter(2000);
            var padded=new byte[length];report.CopyTo(padded,0);
            await stream.WriteAsync(padded,deadline.Token);
            await stream.FlushAsync(deadline.Token);
        }
        public void Dispose()=>stream.Dispose();
    }
}
