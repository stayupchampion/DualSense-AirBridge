using System.Text.Json;
using System.Text.RegularExpressions;
namespace DsBridge.Core;

public sealed record HideState(bool Active, bool Inverse, string[] Devices, string[] Applications);
public sealed record Controller(string InstanceId, string DevicePath, string Address, string Transport, string Name) {
    public override string ToString() => $"{Name} · {Transport} · {(Address.Length > 8 ? Address[..8] + ":…" : Address)}";
}
public sealed record AudioDefault(int Flow, int Role, string Id, string Name);
public sealed record AudioState(AudioDefault[] Defaults, string[] EndpointIds);
public sealed class Journal {
    public int Schema { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Stage { get; set; } = "prepared";
    public string BackendPath { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public string Address { get; set; } = "";
    public HideState Original { get; set; } = new(false, false, [], []);
    public AudioState Audio { get; set; } = new([], []);
    public bool RegisterIntent { get; set; }
    public bool HideIntent { get; set; }
    public bool CloakIntent { get; set; }
    public int BackendPid { get; set; }
    public long BackendStarted { get; set; }
    public string? UsbPort { get; set; }
    public string LastError { get; set; } = "";
    public List<string> Notes { get; set; } = [];
    public string RecoveryEvidencePath { get; set; } = "";
    public string[] RecoveryWarnings { get; set; } = [];
}
public interface IHideAccess {
    Task<HideState> Read();
    Task Register(string path);
    Task Unregister(string path);
    Task Hide(string instance);
    Task Unhide(string instance);
    Task SetActive(bool active);
}
public static class Safety {
    public static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;
    public static bool Contains(IEnumerable<string> values, string item) => values.Contains(item, Names);
    public static bool IsUsbipHost(string instance, string? service, IEnumerable<string> hardwareIds) =>
        instance.StartsWith(@"ROOT\",StringComparison.OrdinalIgnoreCase) &&
        Names.Equals(service,"usbip2_ude") && Contains(hardwareIds,@"ROOT\USBIP_WIN2\UDE");
    public static void Validate(HideState state, string instance) {
        if (state.Inverse) throw new InvalidOperationException("HidHide 正在使用反向应用列表，无法安全接管。请先在原软件中处理。");
        if (Contains(state.Devices, instance)) throw new InvalidOperationException("该手柄已被其他配置隐藏；工具不会接管已有隐藏规则。");
        if (!state.Active && state.Devices.Length != 0)
            throw new InvalidOperationException("HidHide 已有未启用的隐藏列表。启用全局开关会影响其他设备，因此已阻止。");
    }
    public static bool IsAddress(string value) => Regex.IsMatch(value, @"\A[0-9A-Fa-f]{2}(:[0-9A-Fa-f]{2}){5}\z");
    public static async Task Apply(IHideAccess hide, Journal j, Action save) {
        var current = await hide.Read();
        Validate(current, j.InstanceId);
        if (!Equivalent(current, j.Original)) throw new InvalidOperationException("HidHide 配置在准备期间发生变化，请重新检查。");
        if (!Contains(j.Original.Applications, j.BackendPath)) {
            j.RegisterIntent = true; save(); await hide.Register(j.BackendPath);
        }
        j.HideIntent = true; save(); await hide.Hide(j.InstanceId);
        if (!j.Original.Active) { j.CloakIntent = true; save(); await hide.SetActive(true); }
        var after = await hide.Read();
        if (!after.Active || after.Inverse || !Contains(after.Devices, j.InstanceId) || !Contains(after.Applications, j.BackendPath))
            throw new InvalidOperationException("设备隐藏设置写入后校验失败。");
        j.Stage = "hidden"; save();
    }
    public static bool Equivalent(HideState a, HideState b) =>
        a.Active == b.Active && a.Inverse == b.Inverse &&
        new HashSet<string>(a.Devices, Names).SetEquals(b.Devices) &&
        new HashSet<string>(a.Applications, Names).SetEquals(b.Applications);
    // Idempotent, narrowly scoped recovery. Never replace a shared list wholesale.
    public static async Task<List<string>> Restore(IHideAccess hide, Journal j, Action save) {
        var issues = new List<string>();
        async Task Attempt(Func<Task> action, string label) {
            try { await action(); } catch (Exception ex) { issues.Add(label + ": " + ex.Message); }
        }
        if (j.HideIntent && !Contains(j.Original.Devices, j.InstanceId))
            await Attempt(() => hide.Unhide(j.InstanceId), "解除本次设备隐藏失败");
        if (j.RegisterIntent && !Contains(j.Original.Applications, j.BackendPath))
            await Attempt(() => hide.Unregister(j.BackendPath), "撤销本次应用登记失败");
        try {
            var current = await hide.Read();
            if (j.CloakIntent && !j.Original.Active && current.Active) {
                // Changing this global switch can affect other software; require unchanged shared state.
                if (!current.Inverse && new HashSet<string>(current.Devices, Names).SetEquals(j.Original.Devices) &&
                    new HashSet<string>(current.Applications, Names).SetEquals(j.Original.Applications)) {
                    await hide.SetActive(false);
                } else issues.Add("检测到其他软件改变 HidHide 配置，已保留其全局开关。需人工确认恢复状态。");
            }
            current = await hide.Read();
            if (j.HideIntent && Contains(current.Devices, j.InstanceId) && !Contains(j.Original.Devices, j.InstanceId))
                issues.Add("实体手柄仍被隐藏。");
            if (j.RegisterIntent && Contains(current.Applications, j.BackendPath) && !Contains(j.Original.Applications, j.BackendPath))
                issues.Add("本次应用登记仍存在。");
            if (j.CloakIntent && current.Active != j.Original.Active) issues.Add("全局隐藏开关尚未恢复。");
        } catch (Exception ex) { issues.Add("无法回读恢复状态: " + ex.Message); }
        j.LastError = string.Join("\n", issues);
        try { save(); } catch (Exception ex) { issues.Add("恢复日志写入失败，但已继续执行设备恢复: " + ex.Message); }
        return issues;
    }
}
public static class AtomicJson {
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public static void Write<T>(string path, T value) {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".new";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) {
            file.Write(bytes); file.Flush(true);
        }
        File.Move(tmp, path, true);
    }
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Options)
        ?? throw new InvalidDataException("状态文件为空");
}
public static class HideParser {
    public static string[] Entries(string text, string command) {
        var values = new List<string>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            if (!line.StartsWith(command + " \"", StringComparison.Ordinal) || !line.EndsWith('"'))
                throw new InvalidDataException("无法识别 HidHide 列表输出，已停止。");
            values.Add(line[(command.Length + 2)..^1]);
        }
        return values.ToArray();
    }
    public static bool Flag(string text, string name) => text.Trim() switch {
        var s when s == "--" + name + "-on" => true,
        var s when s == "--" + name + "-off" => false,
        _ => throw new InvalidDataException("无法识别 HidHide 开关状态，已停止。")
    };
}
