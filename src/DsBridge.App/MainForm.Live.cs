using DsBridge.Core;
namespace DsBridge.App;

internal sealed partial class MainForm {
    readonly Label batteryLabel=new(){Text="电量：等待报告",AutoSize=true,Margin=new(0,6,28,0)};
    readonly UiSwitch speakerMute=new(){Text="手柄扬声器静音（仅桥接）",Checked=true,AccessibleName="手柄扬声器静音，仅在桥接时生效"};
    readonly Label speakerStatus=new(){Text="下次启用桥接时应用",AutoSize=true,Margin=new(0,6,0,0)};
    readonly Label volumeLabel=new(){Text="Windows 音量：正在读取",Dock=DockStyle.Fill,AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft};
    readonly ToolTip liveTips=new();
    VolumeObservation? lastVolume;
    AudioWorker liveAudio=new(readOnly:true);
    DateTime nextBattery=DateTime.MinValue,nextVolume=DateTime.MinValue;
    CancellationTokenSource? batteryCancel;
    Task? batteryRead;
    bool refreshingLive;
    Control CreateLiveControls() {
        var panel=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};
        panel.ColumnStyles.Add(new(SizeType.Percent,38));panel.ColumnStyles.Add(new(SizeType.Percent,62));
        var batteryCard=new UiCard{Dock=DockStyle.Fill,Padding=new(20,16,20,16),Margin=new(0,0,8,0)};
        var battery=UiTheme.Stack(UiTheme.Fixed(28),UiTheme.Fill(),UiTheme.Fixed(38));
        battery.Controls.Add(UiTheme.Label("手柄电量",11,null,true),0,0);
        batteryLabel.AutoSize=false;batteryLabel.Dock=DockStyle.Fill;batteryLabel.Font=UiTheme.Text(13,FontStyle.Bold);
        batteryLabel.ForeColor=UiTheme.Ink;batteryLabel.Margin=Padding.Empty;batteryLabel.AccessibleDescription="手柄电量与充电状态";
        battery.Controls.Add(batteryLabel,0,1);
        var batteryNote=UiTheme.Label("约每 5 秒更新\n电量为手柄报告的近似值。",8,UiTheme.Secondary);batteryNote.Dock=DockStyle.Fill;battery.Controls.Add(batteryNote,0,2);
        batteryCard.Controls.Add(battery);panel.Controls.Add(batteryCard,0,0);
        var audioCard=new UiCard{Dock=DockStyle.Fill,Padding=new(20,16,20,16),Margin=new(8,0,0,0)};
        var audio=UiTheme.Stack(UiTheme.Fixed(28),UiTheme.Fill(),UiTheme.Fixed(36),UiTheme.Fixed(22));
        audio.Controls.Add(UiTheme.Label("声音",11,null,true),0,0);
        volumeLabel.Font=UiTheme.Text(9);volumeLabel.TextAlign=ContentAlignment.TopLeft;volumeLabel.ForeColor=UiTheme.Secondary;volumeLabel.Margin=Padding.Empty;
        volumeLabel.AccessibleDescription="Windows 默认播放设备与音量";audio.Controls.Add(volumeLabel,0,1);
        speakerMute.Dock=DockStyle.Fill;speakerMute.Font=UiTheme.Text(9);speakerMute.Margin=Padding.Empty;audio.Controls.Add(speakerMute,0,2);
        speakerStatus.Font=UiTheme.Text(8);speakerStatus.ForeColor=UiTheme.Secondary;speakerStatus.Margin=new(54,2,0,0);speakerStatus.AutoSize=false;speakerStatus.Dock=DockStyle.Fill;audio.Controls.Add(speakerStatus,0,3);
        audioCard.Controls.Add(audio);panel.Controls.Add(audioCard,1,0);
        liveTips.SetToolTip(batteryLabel,"约每 5 秒刷新。电量是手柄报告的粗略档位；原生蓝牙的简短报告可能不提供电量。不会为查询电量切换报告模式。");
        liveTips.SetToolTip(speakerMute,"默认静音，只控制桥接手柄的扬声器，保留精细触觉与扳机。取消勾选后由游戏决定扬声器音量。退出工具不修改系统音量。");
        liveTips.SetToolTip(volumeLabel,"每秒只读 Windows 默认播放设备的音量。此数值不证明音箱实际响度；异常时请导出检查结果。");
        speakerMute.CheckedChanged+=(_,_)=>{
            if(!Running||session is null){speakerStatus.Text="下次启用桥接时应用";return;}
            try {
                AtomicJson.Write(Path.Combine(session,"preferences.json"),new SpeakerPreference(speakerMute.Checked));
                speakerStatus.Text="等待后端确认…";
            }catch(Exception ex){speakerStatus.Text="设置未写入";detail.Text=ex.Message;}
        };
        return panel;
    }
    void ResetBattery() {
        batteryCancel?.Cancel();nextBattery=DateTime.MinValue;batteryLabel.Text="电量：等待报告";
    }
    async Task StopBatteryRead() {
        batteryCancel?.Cancel();
        if(batteryRead is not null)await batteryRead;
    }
    async Task ReadNativeBattery(Controller selected,CancellationToken cancel) {
        var value=await Task.Run(()=>Devices.ReadBattery(selected,cancel));
        if(!cancel.IsCancellationRequested&&!closing&&!IsDisposed&&!Running&&ReferenceEquals(controllers.SelectedItem,selected))
            batteryLabel.Text=value.Display;
    }
    async Task RefreshLive() {
        if(refreshingLive||closing||IsDisposed)return;
        refreshingLive=true;
        try {
            var now=DateTime.UtcNow;
            if(Running&&session is not null&&controllers.SelectedItem is Controller selected) {
                try {
                    var data=AtomicJson.Read<BridgeTelemetry>(Path.Combine(session,"telemetry.json"));
                    batteryLabel.Text=data.Battery(selected.Address,DateTimeOffset.UtcNow).Display;
                    speakerStatus.Text=!data.IsCurrent(selected.Address,DateTimeOffset.UtcNow)?"等待后端确认…":
                        data.PreferenceError.Length>0?"设置未应用":data.SpeakerMuted!=speakerMute.Checked?"等待后端确认…":
                        data.SpeakerMuted?"已应用静音":"由游戏控制声音";
                }catch(Exception ex) when(ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) {
                    batteryLabel.Text="电量：等待桥接报告";speakerStatus.Text="等待后端确认…";
                }
            }else if(!busy) {
                speakerStatus.Text="下次启用桥接时应用";
                if(controllers.SelectedItem is not Controller selectedNative)batteryLabel.Text="电量：未连接手柄";
                else if(now>=nextBattery&&(batteryRead is null||batteryRead.IsCompleted)) {
                    nextBattery=now.AddSeconds(5);
                    batteryCancel?.Dispose();batteryCancel=new();
                    batteryRead=ReadNativeBattery(selectedNative,batteryCancel.Token);
                    await batteryRead;
                }
            }
            if(now>=nextVolume) {
                nextVolume=now.AddSeconds(1);
                var value=await liveAudio.ReadVolumes();
                if(closing||IsDisposed)return;
                lastVolume=value;volumeLabel.Text=value.Display;
            }
        }catch(Exception ex){if(!closing&&!IsDisposed)batteryLabel.Text="电量：读取暂不可用";System.Diagnostics.Debug.WriteLine(ex);}
        finally {refreshingLive=false;}
    }
}
