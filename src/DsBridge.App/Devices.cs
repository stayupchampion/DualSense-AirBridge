using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using DsBridge.Core;
namespace DsBridge.App;
internal static class Devices {
    [StructLayout(LayoutKind.Sequential)] struct InterfaceData { public int Size; public Guid Class; public int Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] struct DeviceData { public int Size; public Guid Class; public uint DevInst; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] struct Attributes { public int Size; public ushort Vendor, Product, Version; }
    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
    [DllImport("hid.dll")] static extern bool HidD_GetSerialNumberString(SafeFileHandle handle, byte[] buffer, int length);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError=true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr dev, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint required, ref DeviceData device);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode)] static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DeviceData data, StringBuilder id, int size, out int required);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("cfgmgr32.dll")] static extern uint CM_Get_Parent(out uint parent, uint child, uint flags);
    [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode)] static extern uint CM_Get_Device_ID(uint dev, StringBuilder buffer, int length, uint flags);

    [DllImport("cfgmgr32.dll",CharSet=CharSet.Unicode)] static extern uint CM_Locate_DevNode(out uint device,string id,uint flags);
    internal static bool IsUsbipDescendant(string id) {
        if(CM_Locate_DevNode(out var current,id,0)!=0)return false;
        for(int i=0;i<16;i++) {
            var name=new StringBuilder(4096);
            if(CM_Get_Device_ID(current,name,name.Capacity,0)!=0)return false;
            // PnP assigns ROOT\USB\0000 on this machine; verify the actual hardware ID
            // and bound driver instead of confusing an instance ID with a hardware ID.
            using(var key=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\"+name)) {
                if(Safety.IsUsbipHost(name.ToString(),key?.GetValue("Service") as string,
                    key?.GetValue("HardwareID") as string[] ?? []))return true;
            }
            if(CM_Get_Parent(out var parent,current,0)!=0)return false;
            current=parent;
        }
        return false;
    }

    internal static string? ControllerUsbRoot(string id) {
        if(CM_Locate_DevNode(out var current,id,0)!=0)return null;
        for(int i=0;i<16;i++) {
            var name=new StringBuilder(4096);
            if(CM_Get_Device_ID(current,name,name.Capacity,0)!=0)return null;
            if(name.ToString().StartsWith(@"USB\VID_054C&PID_0CE6\",StringComparison.OrdinalIgnoreCase))return name.ToString();
            if(CM_Get_Parent(out var parent,current,0)!=0)return null;
            current=parent;
        }
        return null;
    }
    [DllImport("hid.dll",SetLastError=true)] static extern bool HidD_GetPreparsedData(SafeFileHandle handle,out IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data,[Out]byte[] caps);

    // Reads one input report from this exact physical Bluetooth controller; no output,
    // device restart, adapter reset, pairing change or alternate-controller fallback.
    internal static async Task<ControllerObservation> ObserveNativeInput(string instance,string address,CancellationToken cancel) {
        var present=false;
        try {
            cancel.ThrowIfCancellationRequested();
            var selected=Enumerate().FirstOrDefault(x=>x.Transport=="蓝牙"&&
                Safety.Names.Equals(x.InstanceId,instance)&&Safety.Names.Equals(x.Address,address));
            if(selected is null)return new(DateTimeOffset.Now,instance,false,false,0,"未枚举到本次选定的蓝牙 HID；隐藏期间不可见是预期现象。");
            present=true;
            using var handle=CreateFile(selected.DevicePath,0x80000000,3,IntPtr.Zero,3,0x40000000,IntPtr.Zero);
            if(handle.IsInvalid) {
                var error=Marshal.GetLastWin32Error();
                return new(DateTimeOffset.Now,instance,true,false,0,$"无法打开选定 HID 读取句柄（Win32 {error}）。");
            }
            if(!HidD_GetPreparsedData(handle,out var data))throw new IOException("无法读取目标 HID 输入报告长度。");
            int length;
            try {
                var caps=new byte[64];
                if(HidP_GetCaps(data,caps)!=0x110000)throw new IOException("目标 HID 能力报告无效。");
                length=BitConverter.ToUInt16(caps,4);
            }finally {HidD_FreePreparsedData(data);}
            if(length<1||length>4096)throw new IOException("目标 HID 输入报告长度超出检查范围。");
            using var stream=new FileStream(handle,FileAccess.Read,1,true);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(350);
            var bytes=await stream.ReadAsync(new byte[length],timeout.Token).ConfigureAwait(false);
            return new(DateTimeOffset.Now,instance,true,bytes>0,bytes,
                bytes>0?"已读到该实体蓝牙手柄的一个输入报告；不代表游戏已重新识别。":"目标 HID 未返回输入数据。");
        }catch(OperationCanceledException) {
            return new(DateTimeOffset.Now,instance,present,false,0,"在限定时间内未读到输入报告。");
        }catch(Exception ex) {
            return new(DateTimeOffset.Now,instance,present,false,0,ex.Message);
        }
    }

    internal static async Task<ControllerBattery> ReadBattery(Controller selected,CancellationToken cancel) {
        try {
            cancel.ThrowIfCancellationRequested();
            using var handle=CreateFile(selected.DevicePath,0x80000000,3,IntPtr.Zero,3,0x40000000,IntPtr.Zero);
            if(handle.IsInvalid)return new(null,"设备不可读或已断开");
            var attrs=new Attributes{Size=Marshal.SizeOf<Attributes>()};
            if(!HidD_GetAttributes(handle,ref attrs)||attrs.Vendor!=0x054c||attrs.Product!=0x0ce6)
                return new(null,"设备已变化");
            if(!HidD_GetPreparsedData(handle,out var data))return new(null,"暂不可用");
            int length;
            try {
                var caps=new byte[64];
                if(HidP_GetCaps(data,caps)!=0x110000)return new(null,"暂不可用");
                length=BitConverter.ToUInt16(caps,4);
            }finally {HidD_FreePreparsedData(data);}
            if(length<1||length>4096)return new(null,"报告长度无效");
            using var stream=new FileStream(handle,FileAccess.Read,1,true);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(350);
            var report=new byte[length];
            var count=await stream.ReadAsync(report,timeout.Token).ConfigureAwait(false);
            return ControllerBattery.FromReport(report.AsSpan(0,count),selected.Transport=="蓝牙");
        }catch(OperationCanceledException){return new(null,"等待输入报告");}
        catch(IOException){return new(null,"设备不可读或已断开");}
        catch(UnauthorizedAccessException){return new(null,"设备正在由其他程序管理");}
    }

    public static Controller[] Enumerate() {
        var result = new List<Controller>();
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) throw new InvalidOperationException("无法枚举 HID 接口");
        try {
            for (uint index=0; ; index++) {
                var data = new InterfaceData { Size=Marshal.SizeOf<InterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data)) {
                    if (Marshal.GetLastWin32Error() == 259) break;
                    throw new InvalidOperationException("HID 接口枚举中断");
                }
                var device = new DeviceData { Size=Marshal.SizeOf<DeviceData>() };
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var required, ref device);
                if (required < 8 || required > 65536) continue;
                var ptr=Marshal.AllocHGlobal((int)required);
                try {
                    Marshal.WriteInt32(ptr, IntPtr.Size==8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, ptr, required, out _, ref device)) continue;
                    var path = Marshal.PtrToStringUni(ptr+4)!;
                    using var handle = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (handle.IsInvalid) continue;
                    var attrs = new Attributes { Size=Marshal.SizeOf<Attributes>() };
                    if (!HidD_GetAttributes(handle, ref attrs) || attrs.Vendor!=0x054c || attrs.Product!=0x0ce6) continue;
                    var id=new StringBuilder(4096);
                    if (!SetupDiGetDeviceInstanceId(set, ref device, id, id.Capacity, out _)) continue;
                    var ancestors = new List<string> { id.ToString() };
                    uint current=device.DevInst;
                    for (int n=0; n<12 && CM_Get_Parent(out var parent,current,0)==0; n++) {
                        var text=new StringBuilder(4096);
                        if(CM_Get_Device_ID(parent,text,text.Capacity,0)!=0) break;
                        ancestors.Add(text.ToString()); current=parent;
                    }
                    var bluetooth=ancestors.Any(x=>x.StartsWith("BTHENUM",StringComparison.OrdinalIgnoreCase));
                    var address="";
                    if (bluetooth) {
                        var serial=new byte[256];
                        if(HidD_GetSerialNumberString(handle,serial,serial.Length)) {
                            var raw=Encoding.Unicode.GetString(serial).TrimEnd('\0').Replace(":","").Replace("-","");
                            if(Regex.IsMatch(raw,@"\A[0-9a-fA-F]{12}\z")) address=raw;
                        }
                        foreach (var a in ancestors) {
                            var match=Regex.Match(a, @"(?:DEV_|&)([0-9a-fA-F]{12})(?:_|\\|$)");
                            if(address.Length==0 && match.Success) address=match.Groups[1].Value;
                        }
                        if(address.Length==12) address=string.Join(":",Enumerable.Range(0,6).Select(n=>address.Substring(n*2,2))).ToLowerInvariant();
                        if(!Safety.IsAddress(address)) continue;
                    }
                    result.Add(new(id.ToString(),path,address,bluetooth?"蓝牙":"USB","普通 DualSense"));
                } finally { Marshal.FreeHGlobal(ptr); }
            }
        } finally { SetupDiDestroyDeviceInfoList(set); }
        return result.DistinctBy(x=>x.InstanceId,StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
