using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
namespace DsBridge.App;
internal sealed class ChildJob : IDisposable {
    [StructLayout(LayoutKind.Sequential)] struct BasicLimits {
        public long ProcessTime,JobTime;
        public uint Flags;
        public UIntPtr MinWorkingSet,MaxWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint Priority,Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] struct Counters { public ulong ReadOps,WriteOps,OtherOps,ReadBytes,WriteBytes,OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] struct Limits {
        public BasicLimits Basic;
        public Counters Io;
        public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle CreateJobObject(IntPtr security,string? name);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetInformationJobObject(SafeFileHandle job,int type,ref Limits limits,uint length);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool AssignProcessToJobObject(SafeFileHandle job,IntPtr process);
    readonly SafeFileHandle handle;
    public ChildJob() {
        handle=CreateJobObject(IntPtr.Zero,null);
        if(handle.IsInvalid)throw new System.ComponentModel.Win32Exception();
        var limits=new Limits{Basic=new BasicLimits{Flags=0x2000}};
        if(!SetInformationJobObject(handle,9,ref limits,(uint)Marshal.SizeOf<Limits>())) {
            handle.Dispose();throw new System.ComponentModel.Win32Exception();
        }
    }
    public void Add(Process process) {
        if(!AssignProcessToJobObject(handle,process.Handle))throw new System.ComponentModel.Win32Exception();
    }
    public void Dispose()=>handle.Dispose();
    public static bool SelfTest() {
        using var process=Process.Start(Run.Info(Environment.ProcessPath!,"--test-sleeper"))!;
        using(var job=new ChildJob()) {job.Add(process);}
        var exited=process.WaitForExit(5000);
        if(!exited)process.Kill();
        return exited;
    }
}
