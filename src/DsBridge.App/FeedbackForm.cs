using DsBridge.Core;
namespace DsBridge.App;

/// <summary>Manual feedback checks; closing waits until the test has returned from its cleanup.</summary>
internal sealed class FeedbackForm : Form {
    sealed record TargetItem(Controller Device,string Mode) {
        public override string ToString()=>$"{Mode} · {Device.InstanceId[^8..]}";
    }
    readonly ComboBox targets=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
    readonly Label mode=new(){AutoSize=true,Text="尚未选择目标"};
    readonly Button refresh=new(){Text="刷新目标",AutoSize=true};
    readonly Button haptics=new(){Text="左右精细触觉",AutoSize=true};
    readonly Button trigger=new(){Text="右扳机阻力",AutoSize=true};
    readonly Button speaker=new(){Text="手柄扬声器",AutoSize=true};
    readonly Button stop=new(){Text="立即停止",AutoSize=true,Enabled=false};
    readonly Label state=new(){Text="等待手动选择测试",AutoSize=true,Font=new("Microsoft YaHei UI",11,FontStyle.Bold)};
    readonly RichTextBox output=new(){ReadOnly=true,Dock=DockStyle.Fill,BackColor=Color.White,
        BorderStyle=BorderStyle.None,DetectUrls=false,Font=new("Microsoft YaHei UI",10)};
    CancellationTokenSource? cancellation;
    Task activeTest=Task.CompletedTask;
    Task? closeTask;
    bool refreshing,running,closing,allowClose;
    int testSerial;

    public FeedbackForm() {
        Text="DualSense Bridge · 独立反馈测试";
        MinimumSize=new(760,590);Size=new(900,680);StartPosition=FormStartPosition.CenterParent;
        Font=new("Microsoft YaHei UI",10);BackColor=Color.FromArgb(242,245,249);
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=6,Padding=new(22)};
        layout.ColumnStyles.Add(new(SizeType.Percent,100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Absolute,86));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent,100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        var instructions=new Label{AutoSize=true,Dock=DockStyle.Fill,Padding=new(0,0,0,14),
            Text="无需进入游戏关卡；每次只测试所选普通 DualSense。请先退出正在使用手柄的游戏。\n"+
                 "目标仅限 USB 实体手柄或蓝牙桥接生成的虚拟 USB；普通蓝牙直连不作为完整反馈目标。"};
        layout.Controls.Add(instructions,0,0);
        var selection=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=2};
        selection.ColumnStyles.Add(new(SizeType.Percent,100));selection.ColumnStyles.Add(new(SizeType.AutoSize));
        selection.RowStyles.Add(new(SizeType.Absolute,38));selection.RowStyles.Add(new(SizeType.Percent,100));
        selection.Controls.Add(targets,0,0);selection.Controls.Add(refresh,1,0);
        selection.Controls.Add(mode,0,1);selection.SetColumnSpan(mode,2);mode.Margin=new(0,5,0,0);
        layout.Controls.Add(selection,0,1);
        var actions=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,WrapContents=true,Padding=new(0,0,0,8)};
        foreach(var button in new[]{haptics,trigger,speaker,stop}) {
            button.Padding=new(7,5,7,5);actions.Controls.Add(button);
        }
        stop.ForeColor=Color.FromArgb(166,38,38);layout.Controls.Add(actions,0,2);
        state.Padding=new(0,6,0,12);layout.Controls.Add(state,0,3);
        var report=new Panel{Dock=DockStyle.Fill,BackColor=Color.White,Padding=new(12)};
        report.Controls.Add(output);layout.Controls.Add(report,0,4);
        layout.Controls.Add(new Label{AutoSize=true,Dock=DockStyle.Fill,Padding=new(0,12,0,0),
            Text="左右触觉约 4 秒 · 右扳机阻力最多 5 秒 · 低音量扬声器约 2 秒\n"+
                 "扳机测试期间轻按即可，不要用力掰动；“立即停止”或关闭窗口会取消测试并等待清理。\n"+
                 "测试结果仅说明独立反馈链路；《影之刃：零》的实际效果仍需游戏内对比。"},0,5);
        Controls.Add(layout);
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
