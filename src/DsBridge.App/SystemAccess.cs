using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using DsBridge.Core;

namespace DsBridge.App;

internal static class Paths {
    public static string Root => AppContext.BaseDirectory;
    public static string State => Path.Combine(Root, ".local");
    public static string Sessions => Path.Combine(State, "sessions");
    public static string Backend => Path.Combine(Root, "backend", "vdsd.exe");
    public static string HideCli => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Nefarius Software Solutions", "HidHide", "x64", "HidHideCLI.exe");
    public static string Usbip => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "USBip", "usbip.exe");
    public static bool Driver(string name) {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
        return key?.GetValue("Type") is not null;
    }
    public static bool Admin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static string CheckSession(string path) {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.GetFullPath(Sessions) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("会话目录必须位于工具自己的 .local/sessions 内。");
        return full;
    }
}
internal static partial class Run {
    public static ProcessStartInfo Info(string exe, params string[] args) {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = Paths.Root };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }
    public static bool IsAlive(int pid, long ticks) {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited && p.StartTime.ToUniversalTime().Ticks == ticks; }
        catch { return false; }
    }
}
internal sealed class HidHide : IHideAccess {
    readonly CancellationToken cancel;
    readonly Func<string[],CancellationToken,Task<string>> execute;
    public HidHide(CancellationToken cancel=default,Func<string[],CancellationToken,Task<string>>? execute=null) {
        this.cancel=cancel;this.execute=execute??((args,token)=>Run.Text(Paths.HideCli,args,cancel:token));
    }
    private async Task<string> Cmd(params string[] args) {
        for(int attempt=0;;attempt++) {
            cancel.ThrowIfCancellationRequested();
            try { return await execute(args,cancel); }
            // HidHide's control handle is exclusive. Its own configuration utility or a
            // concurrent read-only diagnostic can briefly hold it; retry only open-denied.
            catch(InvalidOperationException ex) when(attempt<4 &&
                ex.Message.Contains("0x0005",StringComparison.OrdinalIgnoreCase)) {
                await Task.Delay(125*(attempt+1),cancel);
            }
        }
    }
    public async Task<HideState> Read() {
        // v1.5.230.0 supports multiple commands sharing one loaded configuration.
        // --cancel avoids the CLI's final ApplyConfigurationChanges on observation.
        return HideParser.Snapshot(await Cmd("--cloak-state","--inv-state","--dev-list","--app-list",
            "--cloak-state","--inv-state","--cancel"));
    }
    public async Task Register(string path) => _ = await Cmd("--app-reg", path);
    public async Task Unregister(string path) => _ = await Cmd("--app-unreg", path);
    public async Task Hide(string instance) => _ = await Cmd("--dev-hide", instance);
    public async Task Unhide(string instance) => _ = await Cmd("--dev-unhide", instance);
    public async Task SetActive(bool active) => _ = await Cmd(active ? "--cloak-on" : "--cloak-off");
}
internal sealed record ProbeReport(DateTimeOffset CheckedAt, string Windows, bool Admin, bool BackendReady,
    bool HidHideInstalled, bool UsbipInstalled, Controller[] Controllers, string[] AudioOutputs,
    string[] Blockers, string[] Notes) {
    public ConnectionEvidence Connection { get; init; }=new(false,false,false,false,false,false,false,false);
}
internal static class Probe {
    public static async Task<ProbeReport> Read() {
        await using var worker=new AudioWorker(readOnly:true);
        return await Read(()=>worker.Snapshot());
    }
    public static async Task<ProbeReport> Read(Func<Task<AudioState>> readAudio,CancellationToken cancel=default) {
        var blockers = new List<string>(); var notes = new List<string>();
        bool backend = false;
        if (File.Exists(Paths.Backend)) {
            try {
                using var j = JsonDocument.Parse(await Run.Text(Paths.Backend, ["--bridge-info"],cancel:cancel));
                var r = j.RootElement;
                backend = r.GetProperty("bridgeProtocol").GetInt32() == 1 &&
                    r.GetProperty("managedHidHide").GetBoolean() && r.GetProperty("stopEvent").GetBoolean() &&
                    r.GetProperty("exclusiveUsbip").GetBoolean() &&
                    r.TryGetProperty("singleConnectionSession",out var singleSession) && singleSession.GetBoolean() &&
                    r.TryGetProperty("controllerPreferences",out var preferences) && preferences.GetBoolean() &&
                    r.TryGetProperty("independentAudioControls",out var audioControls) && audioControls.GetBoolean();
                if (!backend) blockers.Add("桥接后端缺少安全会话补丁。");
            } catch (Exception ex) { blockers.Add("后端校验失败：" + ex.Message); }
        } else blockers.Add("缺少已编译的 backend/vdsd.exe。");
        bool hide = Paths.Driver("HidHide") && File.Exists(Paths.HideCli);
        bool usbip = Paths.Driver("usbip2_ude") && Paths.Driver("usbip2_filter") && File.Exists(Paths.Usbip);
        if (!hide) blockers.Add("未安装 HidHide；需单独确认后安装。");
        if (!usbip) blockers.Add("未安装完整的 usbip-win2；需单独确认后安装。");
        if (!Paths.Admin) notes.Add("查看和检查无需提权；实际启用桥接时会请求管理员权限。");
        try {
            if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == 3240))
                blockers.Add("本机 3240 端口已被占用，不能与其他 USB/IP 桥接并用。");
        } catch (Exception ex) { blockers.Add("无法检查 USB/IP 端口：" + ex.Message); }
        bool backendRunning=false,connectionChecked=true,virtualController=false,importedPort=false;
        foreach (var name in new[] { "vdsd", "DS4Windows", "DSX", "DualSenseX", "DualSenseY", "DualSenseClient", "reWASD" }) {
            var processes = Process.GetProcessesByName(name);
            if (processes.Length != 0) blockers.Add("检测到 " + name + " 正在运行；请先在该软件内正常退出。");
            if(name=="vdsd")backendRunning=processes.Length!=0;
            foreach (var p in processes) p.Dispose();
        }
        using (var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\vdsd"))
            if (service?.GetValue("Start") is int start && start == 2)
                blockers.Add("已有 vDS 自动启动服务，可能接管手柄。请先处理原安装；本工具不会擅自修改它。");
        Controller[] devices = [];
        try {
            devices = Devices.Enumerate();
            virtualController=devices.Any(x=>Devices.IsUsbipDescendant(x.InstanceId));
            if(virtualController)blockers.Add("检测到虚拟 DualSense，不能将当前连接视为原生模式。");
        } catch (Exception ex) { connectionChecked=false;blockers.Add("手柄枚举失败：" + ex.Message); }
        if(usbip)try {
            var ports=await Run.Text(Paths.Usbip,["port"],cancel:cancel);
            importedPort=ports.Contains("usbip://127.0.0.1:3240/1-1",StringComparison.OrdinalIgnoreCase);
            if(importedPort)blockers.Add("本机桥接虚拟 USB 端口仍存在，请先确认并恢复所属会话。");
        }catch(Exception ex){connectionChecked=false;blockers.Add("无法确认虚拟 USB 端口状态："+ex.Message);}
        if (!devices.Any(x => x.Transport == "蓝牙"))
            blockers.Add("未检测到可读取的蓝牙 DualSense。完成普通蓝牙配对、拔掉数据线并唤醒手柄后再检查。");
        string[] audio = [];
        try { audio = (await readAudio()).Defaults.Where(x => x.Flow == 0).Select(x => x.Name).Distinct().ToArray(); }
        catch (Exception ex) { blockers.Add("无法读取默认音频设备：" + ex.Message); }
        if(audio.Length==0)blockers.Add("没有可用的默认播放设备。请先连接所需音箱或耳机并确认正常播放，再启用桥接。");
        notes.Add("《影之刃：零》尚未实机验收。优先比较同一场景的 USB 原生与蓝牙桥接效果。");
        notes.Add("不安装驱动、不注册服务、不修改开机项；本次检查为只读。");
        return new(DateTimeOffset.Now, Environment.OSVersion.VersionString, Paths.Admin, backend, hide, usbip, devices, audio, blockers.ToArray(), notes.ToArray()) {
            Connection=new(connectionChecked,backendRunning,virtualController,importedPort,SessionWorker.Pending().Any(),
                devices.Any(x=>x.Transport=="蓝牙"),devices.Any(x=>x.Transport=="USB"&&!Devices.IsUsbipDescendant(x.InstanceId)),audio.Length>0)
        };
    }
}
