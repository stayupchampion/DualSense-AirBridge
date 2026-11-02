using System.Runtime.InteropServices;
namespace DsBridge.App;

internal sealed record PlaybackVolume(string Id,string Name,int[] DefaultRoles,float? Scalar,bool? Muted,uint? HardwareSupport,string Error);
internal sealed record VolumeObservation(DateTimeOffset At,PlaybackVolume[] Outputs,string Error) {
    public string Display {
        get {
            if(Error.Length>0)return "Windows 音量：读取暂不可用";
            var output=Outputs.FirstOrDefault(x=>x.DefaultRoles.Contains(0));
            if(output is null)return "Windows 音量：默认输出暂不可用";
            var value=output.Scalar is float v?$"{Math.Round(v*100)}%":"读数暂不可用";
            return $"Windows 音量：{output.Name} · {value}{(output.Muted==true?" · 已静音":"")}";
        }
    }
}
internal static partial class Audio {
    [ComImport,Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface EndpointVolume {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float db,ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level,ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel,float db,ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel,float level,ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel,out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel,out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)]bool mute,ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)]out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out uint step,out uint total);
        [PreserveSig] int VolumeStepUp(ref Guid context);
        [PreserveSig] int VolumeStepDown(ref Guid context);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
        [PreserveSig] int GetVolumeRange(out float min,out float max,out float increment);
    }
    // Read-only: no key interception, volume setting, recording or audio stream.
    public static VolumeObservation ReadVolumes() {
        var outputs=new List<PlaybackVolume>();
        try {
            var enumerator=Open();
            try {
                var defaults=new Dictionary<int,string>();
                for(int role=0;role<3;role++) {
                    if(enumerator.GetDefaultAudioEndpoint(0,role,out var device)<0)continue;
                    try {Marshal.ThrowExceptionForHR(device.GetId(out var id));defaults[role]=id;}
                    finally {Marshal.ReleaseComObject(device);}
                }
                Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(0,1,out var collection));
                try {
                    Marshal.ThrowExceptionForHR(collection.GetCount(out var count));
                    for(uint i=0;i<count;i++) {
                        Marshal.ThrowExceptionForHR(collection.Item(i,out var device));
                        try {
                            Marshal.ThrowExceptionForHR(device.GetId(out var id));
                            var name=Name(device);float? scalar=null;bool? muted=null;uint? hardware=null;var error="";
                            try {
                                var iid=typeof(EndpointVolume).GUID;
                                Marshal.ThrowExceptionForHR(device.Activate(ref iid,23,IntPtr.Zero,out var obj));
                                try {
                                    var volume=(EndpointVolume)obj;
                                    Marshal.ThrowExceptionForHR(volume.GetMasterVolumeLevelScalar(out var level));scalar=level;
                                    Marshal.ThrowExceptionForHR(volume.GetMute(out var mute));muted=mute;
                                    if(volume.QueryHardwareSupport(out var mask)>=0)hardware=mask;
                                }finally {Marshal.ReleaseComObject(obj);}
                            }catch(COMException ex){error=$"0x{ex.HResult:X8}";}
                            outputs.Add(new(id,name,defaults.Where(x=>x.Value==id).Select(x=>x.Key).ToArray(),scalar,muted,hardware,error));
                        }finally {Marshal.ReleaseComObject(device);}
                    }
                }finally {Marshal.ReleaseComObject(collection);}
            }finally {Marshal.ReleaseComObject(enumerator);}
            return new(DateTimeOffset.Now,outputs.ToArray(),"");
        }catch(Exception ex){return new(DateTimeOffset.Now,outputs.ToArray(),ex.Message);}
    }
}
