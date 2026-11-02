namespace DsBridge.Core;

/// <summary>Physical transport alone cannot identify whether a virtual bridge is active.</summary>
public sealed record ConnectionEvidence(bool Checked,bool BackendRunning,bool VirtualController,
    bool ImportedPort,bool PendingRecovery,bool BluetoothPresent,bool UsbPresent,bool PlaybackAvailable);

public static class ConnectionAssessment {
    public static string IdleStatus(ConnectionEvidence e) {
        if(e.BackendRunning||e.VirtualController||e.ImportedPort)
            return "检测到桥接或虚拟设备 · 请确认会话";
        if(e.PendingRecovery)return "恢复尚未完成";
        if(!e.Checked)return "连接状态待确认";
        if(!e.PlaybackAvailable)return "桥接未运行 · 默认播放设备不可用";
        if(e.BluetoothPresent)return "原生蓝牙可用 · 桥接未运行";
        if(e.UsbPresent)return "原生 USB 可用 · 桥接未运行";
        return "未连接手柄 · 桥接未运行";
    }
}
