using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DsBridge.Core;
namespace DsBridge.App;
internal static partial class Audio {
    [ComImport,Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface AudioClient {
        [PreserveSig] int Initialize(int mode,uint flags,long duration,long period,IntPtr format,IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int mode,IntPtr format,out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long normal,out long minimum);
        [PreserveSig] int Start(); [PreserveSig] int Stop(); [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid,[MarshalAs(UnmanagedType.IUnknown)]out object service);
    }
    [ComImport,Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface RenderClient {
        [PreserveSig] int GetBuffer(uint frames,out IntPtr buffer);
        [PreserveSig] int ReleaseBuffer(uint frames,uint flags);
    }
    internal static string PlayFeedback(Controller target,bool speaker,CancellationToken cancel) =>
        PlayTargetAudio(target,speaker,cancel,null);

    // Both comparison modes and the existing feedback test share endpoint selection,
    // format conversion and the exact same WASAPI initialization/buffer request.
    private static string PlayTargetAudio(Controller target,bool speaker,CancellationToken cancel,Action<LatencyAudioMarker>? onMarker) {
        bool latencyPulse=onMarker is not null;
        var root=Devices.ControllerUsbRoot(target.InstanceId)??throw new InvalidOperationException("目标不是可定位的 USB DualSense。");
        var e=Open();Device? selected=null;AudioClient? client=null;RenderClient? render=null;
        IntPtr format=IntPtr.Zero;bool started=false,initialized=false;
        using var bufferReady=new AutoResetEvent(false);
        string endpointId="";
        try {
            Marshal.ThrowExceptionForHR(e.EnumAudioEndpoints(0,1,out var collection));
            try {
                Marshal.ThrowExceptionForHR(collection.GetCount(out var count));
                for(uint i=0;i<count;i++) {
                    Marshal.ThrowExceptionForHR(collection.Item(i,out var device));
                    bool keep=false;
                    try {
                        Marshal.ThrowExceptionForHR(device.GetId(out var id));
                        var hardware=HardwareInstance(id);
                        if(hardware is not null && string.Equals(Devices.ControllerUsbRoot(hardware),root,StringComparison.OrdinalIgnoreCase)) {
                            if(selected is not null)throw new InvalidOperationException("目标存在多个音频输出，不能自动决定测试通道。");
                            selected=device;endpointId=id;keep=true;
                        }
                    } finally {if(!keep)Marshal.ReleaseComObject(device);}
                }
            } finally {Marshal.ReleaseComObject(collection);}
            if(selected is null)throw new InvalidOperationException("未发现这只手柄的活动音频端点；请等待连接完成后重试。不会改用默认扬声器。");
            cancel.ThrowIfCancellationRequested();
            var iid=new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
            Marshal.ThrowExceptionForHR(selected.Activate(ref iid,23,IntPtr.Zero,out var obj));
            client=(AudioClient)obj;
            Marshal.ThrowExceptionForHR(client.GetMixFormat(out format));
            int tag=(ushort)Marshal.ReadInt16(format,0),channels=(ushort)Marshal.ReadInt16(format,2);
            int rate=Marshal.ReadInt32(format,4),align=(ushort)Marshal.ReadInt16(format,12),bits=(ushort)Marshal.ReadInt16(format,14);
            uint mask=tag==0xFFFE?(uint)Marshal.ReadInt32(format,20):0;
            var sub=tag==0xFFFE?Marshal.PtrToStructure<Guid>(format+24):Guid.Empty;
            bool floating=tag==3||(tag==0xFFFE&&sub==new Guid("00000003-0000-0010-8000-00AA00389B71"));
            bool pcm=tag==1||(tag==0xFFFE&&sub==new Guid("00000001-0000-0010-8000-00AA00389B71"));
            if(channels!=4||rate<32000||rate>192000||align!=channels*bits/8||
               (mask!=0&&mask!=0x33)||(!floating&&!pcm)||(floating&&bits!=32)||(pcm&&bits is not (16 or 24 or 32)))
                throw new InvalidOperationException($"目标音频不是受支持的四声道格式（{channels}ch/{rate}Hz/{bits}bit/mask={mask:X}）。没有向其他声道发送信号。");
            // Use the target's exact mix format. No default endpoint switch or system setting writes.
            // Request a modest shared buffer and refill on the audio engine's
            // event. This affects only this test stream, never endpoint defaults.
            Marshal.ThrowExceptionForHR(client.GetDevicePeriod(out var normalPeriod,out _));
            long requestedBuffer=Math.Max(200000,normalPeriod*2);
            Marshal.ThrowExceptionForHR(client.Initialize(0,0x40000,requestedBuffer,0,format,IntPtr.Zero));initialized=true;
            Marshal.ThrowExceptionForHR(client.SetEventHandle(bufferReady.SafeWaitHandle.DangerousGetHandle()));
            Marshal.ThrowExceptionForHR(client.GetBufferSize(out var capacity));
            iid=new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
            Marshal.ThrowExceptionForHR(client.GetService(ref iid,out obj));render=(RenderClient)obj;
            long streamLatency=0;
            int streamLatencyResult=latencyPulse?client.GetStreamLatency(out streamLatency):0;
            uint total=latencyPulse?LatencyPulseSignal.TotalFrames(rate):(uint)(rate*(speaker?2:4)),written=0;
            var clock=Stopwatch.StartNew();bool drained=false;
            while(!drained) {
                cancel.ThrowIfCancellationRequested();
                if(clock.Elapsed.TotalSeconds>(latencyPulse?LatencyPulseSignal.DurationSeconds+5:(speaker?5:7)))throw new TimeoutException("音频测试超时，已停止本次流。");
                Marshal.ThrowExceptionForHR(client.GetCurrentPadding(out var padding));
                var frames=Math.Min(capacity-padding,total-written);
                if(frames>0) {
                    var data=new byte[checked((int)frames*align)];
                    for(int frame=0;frame<frames;frame++) for(int channel=0;channel<4;channel++) {
                        var sample=latencyPulse?LatencyPulseSignal.Sample(channel,written+frame,rate)
                            :FeedbackSignal.Sample(speaker,channel,(written+frame)/(double)rate);
                        int offset=frame*align+channel*(bits/8);
                        if(floating)BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset,4),BitConverter.SingleToInt32Bits((float)sample));
                        else if(bits==16)BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset,2),(short)Math.Round(sample*32767));
                        else if(bits==24) {int n=(int)Math.Round(sample*8388607);data[offset]=(byte)n;data[offset+1]=(byte)(n>>8);data[offset+2]=(byte)(n>>16);}
                        else BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset,4),(int)Math.Round(sample*2147483647));
                    }
                    IReadOnlyList<LatencyPulseStart> starts=latencyPulse?LatencyPulseSignal.StartsInBlock(written,frames,rate):Array.Empty<LatencyPulseStart>();
                    uint submissionPadding=padding;
                    // Sample as close to this block submission as possible; sample generation
                    // above may have taken time while the engine drained existing frames.
                    if(starts.Count>0)Marshal.ThrowExceptionForHR(client.GetCurrentPadding(out submissionPadding));
                    Marshal.ThrowExceptionForHR(render.GetBuffer(frames,out var buffer));
                    try {Marshal.Copy(data,0,buffer,data.Length);}
                    catch {render.ReleaseBuffer(frames,2);throw;}
                    Marshal.ThrowExceptionForHR(render.ReleaseBuffer(frames,0));
                    long submittedQpc=starts.Count>0?Stopwatch.GetTimestamp():0;
                    written+=frames;
                    foreach(var start in starts)
                        onMarker!(new(start.Trial,submittedQpc,checked((int)submissionPadding+start.OffsetFrames),rate,capacity));
                }
                if(!started){Marshal.ThrowExceptionForHR(client.Start());started=true;}
                if(written==total) {Marshal.ThrowExceptionForHR(client.GetCurrentPadding(out padding));drained=padding==0;}
                if(!drained) {
                    if(WaitHandle.WaitAny([bufferReady,cancel.WaitHandle],100)==1)cancel.ThrowIfCancellationRequested();
                }
            }
            if(latencyPulse) {
                var latencyText=streamLatencyResult>=0?$"{streamLatency} × 100 ns（{streamLatency/10000.0:F3} ms）":$"不可用 HRESULT=0x{streamLatencyResult:X8}";
                return $"已写入 {written} 帧 / {rate} Hz / 4 声道 / {(floating?"float":"PCM")}{bits} / mask=0x{mask:X}；仅触觉声道 3/4 有信号。"+
                    $"\nBufferFrames={capacity}；请求缓冲={requestedBuffer/10000.0:F1} ms；共享模式/事件驱动；GetStreamLatency={latencyText}。"+
                    "\nQPC 标记为 ReleaseBuffer 成功后的提交时刻；FramesAhead 为提交前 padding 加块内首个非零样本偏移；均不代表 DAC 或实际振动起点。"+
                    "\n端点："+endpointId;
            }
            return $"已写入 {written} 帧 / {rate} Hz / 4 声道 / {(floating?"float":"PCM")}{bits}；"+
                (speaker?"仅声道 1/2 有信号。":"仅触觉声道 3/4 有信号。")+"\n端点："+endpointId;
        } finally {
            try {
                if(client is not null&&initialized) {
                    var stop=started?client.Stop():0;
                    var reset=client.Reset();
                    Marshal.ThrowExceptionForHR(stop);Marshal.ThrowExceptionForHR(reset);
                }
            } finally {
                if(render is not null)Marshal.ReleaseComObject(render);
                if(client is not null)Marshal.ReleaseComObject(client);
                if(format!=IntPtr.Zero)Marshal.FreeCoTaskMem(format);
                if(selected is not null)Marshal.ReleaseComObject(selected);
                Marshal.ReleaseComObject(e);
            }
        }
    }
}
