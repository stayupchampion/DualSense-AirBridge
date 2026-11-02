using DsBridge.Core;
namespace DsBridge.App;

/// <summary>Manual feedback checks; closing waits until the test has returned from its cleanup.</summary>
internal sealed partial class FeedbackForm : Form {
    sealed record TargetItem(Controller Device,string Mode) {
        public override string ToString()=>$"{Mode} · {Device.InstanceId[^8..]}";
    }
    readonly ComboBox targets=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
    readonly Label mode=new(){AutoSize=true,Text="尚未选择目标"};
    readonly UiButton refresh=new(){Text="刷新目标"};
    readonly UiButton haptics=new(){Text="测试触觉",Primary=true};
    readonly UiButton trigger=new(){Text="测试扳机"};
    readonly UiButton speaker=new(){Text="测试扬声器"};
    readonly UiButton stop=new(){Text="立即停止",Destructive=true,Enabled=false};
    readonly Label state=new(){Text="等待手动选择测试",AutoSize=true,Font=new("Microsoft YaHei UI",11,FontStyle.Bold)};
    readonly RichTextBox output=new(){ReadOnly=true,Dock=DockStyle.Fill,BackColor=Color.White,
        BorderStyle=BorderStyle.None,DetectUrls=false,Font=new("Microsoft YaHei UI",10)};
    CancellationTokenSource? cancellation;
    Task activeTest=Task.CompletedTask;
    Task? closeTask;
    bool refreshing,running,closing,allowClose;
    int testSerial;

    public FeedbackForm() {
        BuildLayout();
        targets.SelectedIndexChanged+=(_,_)=>UpdateControls();
        refresh.Click+=async(_,_)=>await RefreshTargets();
        haptics.Click+=(_,_)=>StartTest(FeedbackKind.Haptics);
        trigger.Click+=(_,_)=>StartTest(FeedbackKind.Trigger);
        speaker.Click+=(_,_)=>StartTest(FeedbackKind.Speaker);
        stop.Click+=async(_,_)=>await StopAndWaitAsync();
        Shown+=async(_,_)=>await RefreshTargets();
        FormClosing+=OnClosing;
        UpdateControls();
    }

    void Append(string message) {
        if(IsDisposed||Disposing)return;
        output.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        output.SelectionStart=output.TextLength;output.ScrollToCaret();
    }
    void UpdateControls() {
        if(IsDisposed||Disposing)return;
        var selected=targets.SelectedItem as TargetItem;
        var idle=!running&&!refreshing&&!closing;
        targets.Enabled=idle;refresh.Enabled=idle;
        haptics.Enabled=trigger.Enabled=speaker.Enabled=idle&&selected is not null;
        stop.Enabled=running&&!closing&&cancellation?.IsCancellationRequested==false;
        mode.Text=selected is null?"没有可测试的 USB 目标":$"当前模式：{selected.Mode}";
    }
    async Task RefreshTargets() {
        if(running||refreshing||closing)return;
        refreshing=true;state.Text="正在读取可测试目标";UpdateControls();
        var previous=(targets.SelectedItem as TargetItem)?.Device.InstanceId;
        try {
            var devices=await Task.Run(FeedbackTests.Targets);
            if(closing||IsDisposed)return;
            targets.Items.Clear();
            foreach(var device in devices)targets.Items.Add(new TargetItem(device,FeedbackTests.Label(device)));
            targets.SelectedIndex=targets.Items.Count>0?0:-1;
            for(int i=0;i<targets.Items.Count;i++)
                if(targets.Items[i] is TargetItem item&&item.Device.InstanceId==previous){targets.SelectedIndex=i;break;}
            state.Text=devices.Length==0?"没有可测试的目标":"等待手动选择测试";
            Append(devices.Length==0
                ?"请连接 USB 数据线，或先在主窗口启用蓝牙桥接，再刷新。普通蓝牙直连不会显示在此列表。"
                :$"找到 {devices.Length} 个目标；尚未发送任何测试反馈。");
        }catch(Exception ex) {
            if(!closing&&!IsDisposed){targets.Items.Clear();state.Text="读取目标失败";Append(ex.Message);}
        }finally{refreshing=false;UpdateControls();}
    }
    void StartTest(FeedbackKind kind) {
        if(running||refreshing||closing||targets.SelectedItem is not TargetItem selected)return;
        running=true;
        var cancel=new CancellationTokenSource();cancellation=cancel;
        var serial=++testSerial;
        state.Text=kind switch {
            FeedbackKind.Haptics=>"正在测试左右精细触觉",
            FeedbackKind.Trigger=>"正在测试右扳机阻力",
            _=>"正在测试手柄扬声器"
        };
        Append($"{state.Text}；{selected.Mode}。");UpdateControls();
        activeTest=RunTestAsync(selected.Device,kind,cancel,serial);
    }
    async Task RunTestAsync(Controller target,FeedbackKind kind,CancellationTokenSource cancel,int serial) {
        try {
            var progress=new Progress<string>(message=>{
                if(!closing&&!IsDisposed&&running&&serial==testSerial)Append(message);
            });
            var result=await FeedbackTests.RunAsync(target,kind,cancel.Token,progress);
            state.Text=cancel.IsCancellationRequested?"测试已停止":"测试结束，请记录实际感受";
            Append(result);
        }catch(OperationCanceledException) when(cancel.IsCancellationRequested) {
            state.Text="测试已停止";Append("取消已完成，测试任务已返回。");
        }catch(Exception ex) {
            state.Text="测试未完成";Append(ex.Message);
        }finally {
            running=false;
            if(ReferenceEquals(cancellation,cancel))cancellation=null;
            cancel.Dispose();UpdateControls();
        }
    }

    /// <summary>Returns only after RunAsync has finished all cleanup; no hardware work is detached.</summary>
    internal async Task StopAndWaitAsync() {
        if(running&&cancellation is {IsCancellationRequested:false} current) {
            state.Text="正在停止并清理测试";
            try{current.Cancel();}
            catch(AggregateException ex){Append("取消通知异常，继续等待测试清理："+ex.GetBaseException().Message);}
            UpdateControls();
        }
        await activeTest;
    }
    internal Task CloseAfterCleanupAsync()=>closeTask??=CloseCoreAsync();
    async Task CloseCoreAsync() {
        closing=true;UpdateControls();
        await StopAndWaitAsync();
        await Task.Yield(); // Avoid recursive Close inside a synchronous FormClosing callback.
        allowClose=true;
        if(!IsDisposed)Close();
    }
    async void OnClosing(object? sender,FormClosingEventArgs e) {
        if(allowClose)return;
        e.Cancel=true;
        try{await CloseAfterCleanupAsync();}
        catch(Exception ex) {
            closing=false;closeTask=null;UpdateControls();
            Append("尚未确认测试清理完成："+ex.Message);
            state.Text="请先停止测试";
        }
    }
}
