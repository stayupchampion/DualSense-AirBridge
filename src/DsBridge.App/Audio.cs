using System.Runtime.InteropServices;
using DsBridge.Core;
namespace DsBridge.App;

// Windows Core Audio read APIs. PolicyConfig is isolated and used only to restore a previously
// selected endpoint if a newly created DualSense endpoint unexpectedly became the default.
internal static partial class Audio {
    [ComImport,Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface Enumerator {
        [PreserveSig] int EnumAudioEndpoints(int flow,uint mask,out Collection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow,int role,out Device device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id,out Device device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }
    [ComImport,Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface Collection { [PreserveSig] int GetCount(out uint count); [PreserveSig] int Item(uint index,out Device device); }
    [ComImport,Guid("D666063F-1587-4E43-81F1-B948E807363F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface Device {
        [PreserveSig] int Activate(ref Guid iid,uint clsctx,IntPtr props,[MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int mode,out PropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }
    [StructLayout(LayoutKind.Sequential)] struct Key { public Guid Fmt; public uint Id; }
    [StructLayout(LayoutKind.Explicit,Size=24)] struct Value { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }
    [ComImport,Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface PropertyStore {
        [PreserveSig] int GetCount(out uint count); [PreserveSig] int GetAt(uint n,out Key key);
        [PreserveSig] int GetValue(ref Key key,out Value value);
        [PreserveSig] int SetValue(ref Key key,ref Value value); [PreserveSig] int Commit();
    }
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref Value value);
    [ComImport,Guid("F8679F50-850A-41CF-9C72-430F290290C8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface Policy {
        [PreserveSig] int GetMixFormat(string id,IntPtr format);
        [PreserveSig] int GetDeviceFormat(string id,int defaultValue,IntPtr format);
        [PreserveSig] int ResetDeviceFormat(string id);
        [PreserveSig] int SetDeviceFormat(string id,IntPtr endpoint,IntPtr mix);
        [PreserveSig] int GetProcessingPeriod(string id,int defaultValue,IntPtr defaultPeriod,IntPtr minPeriod);
        [PreserveSig] int SetProcessingPeriod(string id,IntPtr period);
        [PreserveSig] int GetShareMode(string id,IntPtr mode);
        [PreserveSig] int SetShareMode(string id,IntPtr mode);
        [PreserveSig] int GetPropertyValue(string id,ref Key key,out Value value);
        [PreserveSig] int SetPropertyValue(string id,ref Key key,ref Value value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id,int role);
        [PreserveSig] int SetEndpointVisibility(string id,int visible);
    }
    static Enumerator Open() => (Enumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
    static string Name(Device device) {
        Marshal.ThrowExceptionForHR(device.OpenPropertyStore(0,out var store));
        try {
            var key=new Key {Fmt=new("A45C254E-DF1C-4EFD-8020-67D146A850E0"),Id=14};
            Marshal.ThrowExceptionForHR(store.GetValue(ref key,out var value));
            try { return value.Type==31 ? Marshal.PtrToStringUni(value.Pointer)??"" : ""; }
            finally { PropVariantClear(ref value); }
        } finally { Marshal.ReleaseComObject(store); }
    }

    [ComImport,Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface Topology {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index,out Connector connector);
    }
    [ComImport,Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface Connector {
        [PreserveSig] int GetType(out int type);
        [PreserveSig] int GetDataFlow(out int flow);
        [PreserveSig] int ConnectTo(Connector connector);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
        [PreserveSig] int GetConnectedTo(out Connector connector);
        [PreserveSig] int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }
    static bool IsVirtualDualSense(string endpointId) { var id=HardwareInstance(endpointId); return id is not null && id.Contains("VID_054C&PID_0CE6",StringComparison.OrdinalIgnoreCase) && Devices.IsUsbipDescendant(id); }
    static string? HardwareInstance(string endpointId) {
        var e=Open();
        try {
            Marshal.ThrowExceptionForHR(e.GetDevice(endpointId,out var endpoint));
            try {
                var iid=new Guid("2A07407E-6497-4A18-9787-32F79BD0D98F");
                Marshal.ThrowExceptionForHR(endpoint.Activate(ref iid,23,IntPtr.Zero,out var obj));
                var topology=(Topology)obj;
                try {
                    Marshal.ThrowExceptionForHR(topology.GetConnector(0,out var connector));
                    try {
                        Marshal.ThrowExceptionForHR(connector.GetDeviceIdConnectedTo(out var connected));
                        Marshal.ThrowExceptionForHR(e.GetDevice(connected,out var hardware));
                        try {
                            Marshal.ThrowExceptionForHR(hardware.OpenPropertyStore(0,out var store));
                            try {
                                var key=new Key {Fmt=new("78C34FC8-104A-4ACA-9EA4-524D52996E57"),Id=256};
                                Marshal.ThrowExceptionForHR(store.GetValue(ref key,out var value));
                                try {
                                    var instance=value.Type==31?Marshal.PtrToStringUni(value.Pointer):null;
                                    return instance;
                                } finally {PropVariantClear(ref value);}
                            } finally {Marshal.ReleaseComObject(store);}
                        } finally {Marshal.ReleaseComObject(hardware);}
                    } finally {Marshal.ReleaseComObject(connector);}
                } finally {Marshal.ReleaseComObject(topology);}
            } finally {Marshal.ReleaseComObject(endpoint);}
        } catch(COMException) {return null;}
        finally {Marshal.ReleaseComObject(e);}
    }

    public static AudioObservation Observe(bool includeInactive=true) {
        var e=Open();
        try {
            var defaults=new List<AudioDefault>();
            for(int flow=0;flow<2;flow++) for(int role=0;role<3;role++) {
                var hr=e.GetDefaultAudioEndpoint(flow,role,out var device);
                if(hr==unchecked((int)0x80070490))continue;
                Marshal.ThrowExceptionForHR(hr);
                try {Marshal.ThrowExceptionForHR(device.GetId(out var id));defaults.Add(new(flow,role,id,Name(device)));}
                finally {Marshal.ReleaseComObject(device);}
            }
            // Include unplugged, disabled and not-present endpoints in diagnostic evidence.
            Marshal.ThrowExceptionForHR(e.EnumAudioEndpoints(2,includeInactive?0xFu:1u,out var collection));
            try {
                Marshal.ThrowExceptionForHR(collection.GetCount(out var count));
                var endpoints=new List<AudioEndpointState>();
                for(uint i=0;i<count;i++) {
                    Marshal.ThrowExceptionForHR(collection.Item(i,out var device));
                    try {
                        Marshal.ThrowExceptionForHR(device.GetId(out var id));
                        Marshal.ThrowExceptionForHR(device.GetState(out var state));
                        string name;
                        try{name=Name(device);}catch(COMException){name="端点名称暂不可读";}
                        endpoints.Add(new(id,name,state));
                    }finally {Marshal.ReleaseComObject(device);}
                }
                return new(DateTimeOffset.Now,defaults.ToArray(),endpoints.ToArray(),"");
            }finally {Marshal.ReleaseComObject(collection);}
        }finally {Marshal.ReleaseComObject(e);}
    }
    public static AudioState Snapshot() {
        var observation=Observe(false);
        return new(observation.Defaults,observation.Endpoints.Where(x=>x.State==1).Select(x=>x.Id).ToArray());
    }
    static bool IsEndpointActive(string id) {
        var e=Open();
        try {
            if(e.GetDevice(id,out var device)<0)return false;
            try {return device.GetState(out var state)>=0&&state==1;}
            finally {Marshal.ReleaseComObject(device);}
        }finally {Marshal.ReleaseComObject(e);}
    }

    public static int GuardDefaults(AudioState baseline) {
        var current=Observe(false);int restored=0;
        foreach(var original in baseline.Defaults) {
            var now=current.Defaults.FirstOrDefault(x=>x.Flow==original.Flow&&x.Role==original.Role);
            // A disconnected baseline is evidence to report, never a target to force back on.
            if(!RecoveryAssessment.ShouldRestoreDefault(baseline,original,now,current.Endpoints))continue;
            if(!IsVirtualDualSense(now!.Id))throw new InvalidOperationException("新增手柄音频端点的来源无法确认，已停止桥接，未擅自更改默认输出。");
            if(!IsEndpointActive(original.Id))continue;
            var policy=(Policy)Activator.CreateInstance(Type.GetTypeFromCLSID(new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9"))!)!;
            try {Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(original.Id,original.Role));restored++;}
            finally {Marshal.ReleaseComObject(policy);}
        }
        return restored;
    }
}