using System.Diagnostics;
using System.Drawing.Drawing2D;
using DsBridge.Core;
namespace DsBridge.App;

internal sealed class LatencyForm:Form {
    sealed record TargetChoice(Controller Target) {public override string ToString()=>FeedbackTests.Label(Target);}
    sealed record KindChoice(LatencyKind Kind){public override string ToString()=>LatencyCapture.Name(Kind);}
    readonly ComboBox targets=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=320};
    readonly ComboBox kinds=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=180};
    readonly ComboBox fps=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=80};
    readonly Button refresh=new(){Text="刷新目标",AutoSize=true};
    readonly Button start=new(){Text="开始本项（3 秒准备）",AutoSize=true};
    readonly Button stop=new(){Text="停止并保存",AutoSize=true};
    readonly Label instructions=new(){AutoSize=true,Dock=DockStyle.Fill};
    readonly Label state=new(){AutoSize=true,Text="先连接 USB，选择测试项；尚未采集或发送反馈。"};
    readonly RichTextBox notes=new(){ReadOnly=true,Dock=DockStyle.Fill,BackColor=Color.White,BorderStyle=BorderStyle.None,DetectUrls=false};
    readonly LatencyCanvas canvas=new(){Dock=DockStyle.Fill};
    readonly System.Windows.Forms.Timer uiTimer=new(){Interval=100};
    CancellationTokenSource? cancellation;
    LatencyCapture? capture;
    Task active=Task.CompletedTask;
    Task? closeTask;
    bool running,closing,allowClose,refreshing;
    int serial,paintQueued;
    volatile LatencyVisual? pending;
    long flashUntil;
    Stopwatch? clock;
    public LatencyForm() {
        Text="DualSense Bridge · USB / 蓝牙桥接延迟对比";
        Size=new(1140,900);MinimumSize=new(920,760);StartPosition=FormStartPosition.CenterScreen;
        Font=new("Microsoft YaHei UI",10);BackColor=Color.FromArgb(242,245,249);
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=7,Padding=new(20)};
        layout.ColumnStyles.Add(new(SizeType.Percent,100));
        layout.RowStyles.Add(new(SizeType.AutoSize));layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent,68));layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent,32));
        layout.Controls.Add(new Label{Text="USB / 蓝牙桥接 · 同流程拍摄对比",AutoSize=true,Font=new(Font.FontFamily,17,FontStyle.Bold),Padding=new(0,0,0,8)},0,0);
        layout.Controls.Add(new Label{Text="输入：拍摄手部动作 → 本窗口响应。输出：拍摄提交标记 → 可见振动 / 扳机位移。\n软件报告间隔不是硬件延迟；屏幕标记有显示延迟；结果必须结合视频逐帧核对。",AutoSize=true,Dock=DockStyle.Fill,Padding=new(0,0,0,10)},0,1);
        var selection=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,WrapContents=true};
        selection.Controls.Add(targets);selection.Controls.Add(refresh);selection.Controls.Add(kinds);
        selection.Controls.Add(new Label{Text="拍摄 fps",AutoSize=true,Padding=new(4,6,0,0)});selection.Controls.Add(fps);
        layout.Controls.Add(selection,0,2);
        var actions=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,WrapContents=true,Padding=new(0,8,0,8)};
        actions.Controls.Add(start);actions.Controls.Add(stop);actions.Controls.Add(state);layout.Controls.Add(actions,0,3);
        layout.Controls.Add(canvas,0,4);
        instructions.Padding=new(0,10,0,10);layout.Controls.Add(instructions,0,5);
        layout.Controls.Add(notes,0,6);Controls.Add(layout);
        foreach(var k in Enum.GetValues<LatencyKind>())kinds.Items.Add(new KindChoice(k));
        kinds.SelectedIndex=0;fps.Items.AddRange([120,240]);fps.SelectedItem=240;
        kinds.SelectedIndexChanged+=(_,_)=>UpdateControls();
        refresh.Click+=async(_,_)=>await RefreshTargets();
        start.Click+=(_,_)=>StartRecording();
        stop.Click+=async(_,_)=>await StopAsync();
        canvas.Painted+=v=>capture?.Painted(v);
        Shown+=async(_,_)=>await RefreshTargets();
        uiTimer.Tick+=(_,_)=>{
            if(running&&clock is not null)state.Text=$"记录 {clock.Elapsed.TotalSeconds:F1}s · {capture?.Count??0} 份报告";
            if(canvas.Visual is {Output:true,Flash:true} v&&Stopwatch.GetTimestamp()>flashUntil) {
                canvas.Visual=v with{Flash=false};canvas.Invalidate();
            }
        };
        uiTimer.Start();
        FormClosing+=(_,e)=>{if(allowClose)return;e.Cancel=true;_ = CloseAfterCleanupAsync();};
        Append("建议顺序：USB完整一轮 → 结束采集 → 拔USB并连接蓝牙 → 主窗口启用桥接 → 相同项目再测。切换连接前先停止本窗口采集。");
        Append("输入每项30秒；震动约19秒；R2阻力8次开关约16秒。保持显示器刷新率、窗口位置、游戏/Steam设置、相机帧率相同。");
        UpdateControls();
    }
    void Append(string s){if(!IsDisposed){notes.AppendText(s+Environment.NewLine);notes.SelectionStart=notes.TextLength;notes.ScrollToCaret();}}
    void UpdateControls() {
        if(IsDisposed)return;
        targets.Enabled=kinds.Enabled=fps.Enabled=refresh.Enabled=!running&&!closing&&!refreshing;
        start.Enabled=!running&&!closing&&!refreshing&&targets.SelectedItem is TargetChoice;
        stop.Enabled=running&&!closing;
        if(kinds.SelectedItem is KindChoice k)instructions.Text=LatencyCapture.Instruction(k.Kind)+
            "\n拍摄前先让按键松开、摇杆回中。120fps 每帧8.33ms；240fps 每帧4.17ms。保留原视频，不套用慢放导出的播放帧率。";
    }
    async Task RefreshTargets() {
        if(running||closing)return;refreshing=true;UpdateControls();
        try {
            var items=await Task.Run(FeedbackTests.Targets);
            if(closing||IsDisposed)return;
            targets.Items.Clear();foreach(var item in items)targets.Items.Add(new TargetChoice(item));
            if(items.Length>0)targets.SelectedIndex=0;
            state.Text=items.Length==0?"请连接实体 USB 或先启用蓝牙桥接。":"等待开始；当前没有发送反馈。";
        }catch(Exception ex){Append(ex.Message);}
        finally{refreshing=false;UpdateControls();}
    }
    void StartRecording() {
        if(running||closing||targets.SelectedItem is not TargetChoice t||kinds.SelectedItem is not KindChoice k)return;
        running=true;cancellation=new();var id=++serial;canvas.Reset();pending=null;
        capture=new(t.Target,k.Kind,(int)fps.SelectedItem!);canvas.Kind=k.Kind;
        canvas.Mode=capture.Mode;clock=null;UpdateControls();
        active=Record(t.Target,k.Kind,id,cancellation);
    }
    void ShowVisual(LatencyVisual visual,int id) {
        if(id!=serial||closing||IsDisposed)return;
        pending=visual;
        if(Interlocked.Exchange(ref paintQueued,1)!=0)return;
        try{BeginInvoke((Action)(()=>{
            Interlocked.Exchange(ref paintQueued,0);
            if(id!=serial||closing||IsDisposed||pending is not {} v)return;
            canvas.Visual=v;
            if(v.Output&&v.Flash)flashUntil=Stopwatch.GetTimestamp()+Stopwatch.Frequency/2;
            canvas.Invalidate();canvas.Update();
        }));}catch(InvalidOperationException){Interlocked.Exchange(ref paintQueued,0);}
    }
    async Task Record(Controller target,LatencyKind kind,int id,CancellationTokenSource cancel) {
        var current=capture!;
        try {
            Append($"{current.Mode} · {LatencyCapture.Name(kind)} · 声明拍摄帧率 {current.CameraFps}fps");
            for(int n=3;n>0;n--) {state.Text=$"{n} 秒后开始；请开始拍摄并松开按键";await Task.Delay(1000,cancel.Token);}
            clock=Stopwatch.StartNew();
            await current.RunAsync(v=>ShowVisual(v,id),cancel.Token);
            await Task.Yield();canvas.Update();
            state.Text=current.Status=="completed"?"本项记录完成；没有自动计算物理延迟。":"本项已停止或异常，请看记录。";
        }catch(OperationCanceledException){state.Text="准备已取消。";}
         catch(Exception ex){state.Text="测试未完成。";Append(ex.Message);}
        finally {
            clock=null;
            if(current.Started!=default) {
                try{var file=current.Save();Append($"状态：{current.Status}；{current.Error}\n记录：{file}\n请将视频命名为同一JSON的文件名，便于配对；物理延迟目前留空。");}
                catch(Exception ex){Append("保存失败："+ex.Message);}
            }
            if(ReferenceEquals(capture,current))capture=null;
            running=false;if(ReferenceEquals(cancellation,cancel))cancellation=null;
            cancel.Dispose();UpdateControls();
        }
    }
    internal async Task StopAsync() {
        cancellation?.Cancel();
        await active;
    }
    internal Task CloseAfterCleanupAsync()=>closeTask??=CloseCore();
    async Task CloseCore() {
        if(IsDisposed)return;closing=true;UpdateControls();
        await StopAsync();uiTimer.Stop();await Task.Yield();allowClose=true;Close();
    }
    internal async Task RenderCheck(string path) {
        await RefreshTargets();
        var demo=new DualSenseInputSnapshot{LX=190,LY=128,RX=128,RY=128,L2=0,R2=128};
        canvas.Kind=LatencyKind.Sticks;canvas.Mode="渲染检查 · 未连接测试信号";
        canvas.Visual=new(1,0,demo,"渲染示例",false,false);canvas.Update();
        using var bmp=new Bitmap(Width,Height);DrawToBitmap(bmp,new Rectangle(Point.Empty,Size));
        bmp.Save(Path.GetFullPath(path));
    }
}

internal sealed class LatencyCanvas:Control {
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal LatencyVisual? Visual {get;set;}
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal LatencyKind Kind {get;set;}
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string Mode {get;set;}="等待测试";
    public event Action<LatencyVisual>? Painted;
    int lastPainted=-1;
    public LatencyCanvas(){DoubleBuffered=true;ResizeRedraw=true;}
    public void Reset(){Visual=null;lastPainted=-1;Invalidate();}
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);
        var v=Visual;var s=v?.Input??default;
        bool active=v is not null&&(v.Output?v.Flash:Kind switch{
            LatencyKind.Buttons=>s.Buttons!=DualSenseButtons.None,
            LatencyKind.Sticks=>Math.Abs(s.LX-128)>8||Math.Abs(s.LY-128)>8||Math.Abs(s.RX-128)>8||Math.Abs(s.RY-128)>8,
            LatencyKind.Triggers=>s.L2>2||s.R2>2,
            LatencyKind.Touch=>s.Touch1.Active||s.Touch2.Active||(s.Buttons&DualSenseButtons.Touchpad)!=0,
            _=>false});
        e.Graphics.Clear(active?Color.White:Color.FromArgb(15,24,38));
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        using var brush=new SolidBrush(active?Color.Black:Color.White);
        using var accent=new SolidBrush(Color.FromArgb(40,130,235));
        using var pen=new Pen(Color.Gray,2);
        using var title=new Font("Microsoft YaHei UI",15,FontStyle.Bold);
        using var huge=new Font("Consolas",28,FontStyle.Bold);
        using var small=new Font("Microsoft YaHei UI",10);
        e.Graphics.DrawString(Mode,title,brush,14,12);
        e.Graphics.DrawString(v?.Caption??"尚未采集 / 尚未发送反馈",title,brush,14,48);
        e.Graphics.DrawString($"事件 {v?.Event??0} · {LatencyCapture.Name(Kind)}",small,brush,14,Height-28);
        if(v is null)return;
        if(v.Output) {
            e.Graphics.DrawString(v.Flash?"提交标记":"等待下一次",huge,brush,18,104);
            e.Graphics.DrawString("画面标记不是物理反馈起点；以原视频作相对对照。",small,brush,18,Height-62);
        } else if(Kind==LatencyKind.Sticks) {
            int d=Math.Min(155,Height-145);d=Math.Max(d,75);
            for(int i=0;i<2;i++) {
                int x=35+i*(d+85),y=95;
                e.Graphics.DrawEllipse(pen,x,y,d,d);
                var ax=i==0?s.LX:s.RX;var ay=i==0?s.LY:s.RY;
                var dx=Math.Abs(ax-128)<=8?128:ax;var dy=Math.Abs(ay-128)<=8?128:ay;
                e.Graphics.FillEllipse(accent,x+dx*d/255f-8,y+dy*d/255f-8,16,16);
                e.Graphics.DrawString($"{(i==0?"L":"R")}: {ax}, {ay}",small,brush,x,y+d+4);
            }
        } else if(Kind==LatencyKind.Triggers) {
            for(int i=0;i<2;i++) {
                int y=105+i*65;int value=i==0?s.L2:s.R2;int width=Math.Max(100,Width-210);
                e.Graphics.DrawRectangle(pen,125,y,width,30);
                e.Graphics.FillRectangle(accent,125,y,width*value/255f,30);
                e.Graphics.DrawString($"{(i==0?"L2":"R2")} {value}",title,brush,15,y);
            }
        } else if(Kind==LatencyKind.Touch) {
            var rect=new Rectangle(20,95,Math.Max(100,Width-40),Math.Max(60,Height-150));
            e.Graphics.DrawRectangle(pen,rect);
            foreach(var t in new[]{s.Touch1,s.Touch2})if(t.Active)
                e.Graphics.FillEllipse(accent,rect.X+Math.Clamp(t.X/1920f,0,1)*rect.Width-8,
                    rect.Y+Math.Clamp(t.Y/1080f,0,1)*rect.Height-8,16,16);
            e.Graphics.DrawString((s.Buttons&DualSenseButtons.Touchpad)!=0?"机械点击：按下":"机械点击：松开",small,brush,25,Height-50);
        } else {
            e.Graphics.DrawString(active?s.Buttons.ToString():"松开",title,brush,new RectangleF(20,110,Width-40,Height-170));
        }
        if(lastPainted!=v.Event){lastPainted=v.Event;Painted?.Invoke(v);}
    }
}
