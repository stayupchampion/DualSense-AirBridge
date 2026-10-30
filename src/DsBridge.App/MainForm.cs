using System.Diagnostics;
using System.Drawing.Drawing2D;
using DsBridge.Core;
namespace DsBridge.App;

internal sealed class MainForm : Form {
    readonly Label state=new(){AutoSize=true,Text="原生模式 · 桥接未启动",Font=new("Microsoft YaHei UI",15,FontStyle.Bold)};
    readonly Label description=new(){AutoSize=true,Text="正在读取环境；此步骤不会修改系统或安装驱动。"};
    readonly ComboBox controllers=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
    readonly RichTextBox detail=new(){ReadOnly=true,Dock=DockStyle.Fill,BorderStyle=BorderStyle.None,BackColor=Color.White,
        Font=new("Microsoft YaHei UI",10),DetectUrls=false};
    readonly Button refresh=new(){Text="检查连接",AutoSize=true};
    readonly Button start=new(){Text="启用蓝牙桥接",AutoSize=true,Enabled=false};
    readonly Button stop=new(){Text="停止并恢复原生",AutoSize=true,Enabled=false};
    readonly Button recover=new(){Text="恢复遗留状态",AutoSize=true,Enabled=false};
    readonly Button export=new(){Text="导出检查结果",AutoSize=true};
    readonly Button guide=new(){Text="游戏验收步骤",AutoSize=true};
    readonly Button feedback=new(){Text="反馈测试",AutoSize=true};
    readonly Button latency=new(){Text="延迟对比",AutoSize=true};
    LatencyForm? latencyWindow;
    FeedbackForm? feedbackWindow;
    enum StopOutcome {Complete,Cancelled,Incomplete}
    Task<StopOutcome>? stoppingTask;
    readonly System.Windows.Forms.Timer timer=new(){Interval=500};
    ProbeReport? probe; Process? worker; string? session; bool busy,closing,allowClose; readonly string[] args;
    public MainForm(string[] args) {
        this.args=args;
        Text="DualSense AirBridge · 普通蓝牙原生反馈";
        MinimumSize=new(920,720); Size=args.Length>=3 && args[0]=="--ui-check"?new(920,720):new(1040,800); StartPosition=FormStartPosition.CenterScreen;
        Font=new("Microsoft YaHei UI",10); BackColor=Color.FromArgb(242,245,249);
        var outer=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=6,Padding=new(24)};
        outer.RowStyles.Add(new(SizeType.Absolute,86));
        outer.RowStyles.Add(new(SizeType.Absolute,94));
        outer.RowStyles.Add(new(SizeType.Absolute,48));
        outer.RowStyles.Add(new(SizeType.AutoSize));
        outer.RowStyles.Add(new(SizeType.Percent,100));
        outer.RowStyles.Add(new(SizeType.AutoSize));
        var head=new Panel{Dock=DockStyle.Fill,BackColor=Color.FromArgb(22,36,61),Padding=new(18)};
        head.Controls.Add(new Label{Text="DualSense AirBridge",AutoSize=true,ForeColor=Color.White,Font=new("Segoe UI",22,FontStyle.Bold),Location=new(18,8)});
        head.Controls.Add(new Label{Text="按需连接，让游戏的原生反馈走向蓝牙。",AutoSize=true,ForeColor=Color.FromArgb(180,203,235),Location=new(20,52)});
        outer.Controls.Add(head,0,0);
        var statusPanel=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new(4,14,0,0)};
        statusPanel.Controls.Add(state); statusPanel.Controls.Add(description); outer.Controls.Add(statusPanel,0,1);
        var selectRow=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2};
        selectRow.ColumnStyles.Add(new(SizeType.Absolute,104)); selectRow.ColumnStyles.Add(new(SizeType.Percent,100));
        selectRow.Controls.Add(new Label{Text="目标手柄",AutoSize=true,Anchor=AnchorStyles.Left},0,0);selectRow.Controls.Add(controllers,1,0);
        outer.Controls.Add(selectRow,0,2);
        var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,Padding=new(0,10,0,8),WrapContents=true,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink};
        foreach(var b in new[]{refresh,start,stop,recover,feedback,latency,export,guide}) { b.Padding=new(8,5,8,5); actions.Controls.Add(b); }
        start.BackColor=Color.FromArgb(32,104,221);start.ForeColor=Color.White; start.FlatStyle=FlatStyle.Flat;
        outer.Controls.Add(actions,0,3);
        var report=new Panel{Dock=DockStyle.Fill,Padding=new(16),BackColor=Color.White};report.Controls.Add(detail);outer.Controls.Add(report,0,4);
        outer.Controls.Add(new Label{Text="默认不开机自启 · 不自动安装驱动 · 不修改固件\n首个验收目标：《影之刃：零》PC 版 / 普通 DualSense",Dock=DockStyle.Fill,AutoSize=true,Padding=new(0,8,0,0),ForeColor=Color.FromArgb(75,91,116)},0,5);
        Controls.Add(outer);
        refresh.Click+=async(_,_)=>await RefreshProbe();
        controllers.SelectedIndexChanged+=(_,_)=>UpdateButtons();
        controllers.DrawMode=DrawMode.OwnerDrawFixed;
        controllers.ItemHeight=26;
        controllers.DrawItem+=(_,e)=>{
            e.DrawBackground();
            var item=e.Index>=0 ? controllers.Items[e.Index] : controllers.SelectedItem;
            if(item is not null) TextRenderer.DrawText(e.Graphics,item.ToString(),Font,e.Bounds,
                e.ForeColor,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
            e.DrawFocusRectangle();
        };
        start.Click+=async(_,_)=>await StartBridge();
        stop.Click+=async(_,_)=>await StopBridge();
        recover.Click+=async(_,_)=>await RecoverPending();
        export.Click+=(_,_)=>Export();
        guide.Click+=(_,_)=>detail.Text=GuideText;
        feedback.Click+=async(_,_)=>await OpenFeedback();
        latency.Click+=async(_,_)=>await OpenLatency();
        timer.Tick+=(_,_)=>Poll();
        FormClosing+=OnClosing;
        Shown+=async(_,_)=>{
            await RefreshProbe(); timer.Start();
            if(args.Length==1&&args[0]=="--open-latency")await OpenLatency();
            if(args.Length>=2 && args[0]=="--ui-check") {
                await Task.Delay(500);
                using var bitmap=new Bitmap(Width,Height);
                DrawToBitmap(bitmap,new Rectangle(Point.Empty,Size));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
                bitmap.Save(args[1],System.Drawing.Imaging.ImageFormat.Png);
                guide.PerformClick(); var guideWorks=detail.Text.Contains("USB 原生基线");
                export.PerformClick();var exportWorks=detail.Text.Contains("已保存检查结果");
                AtomicJson.Write(Path.ChangeExtension(args[1],".json"),new {guideWorks,exportWorks,controllerIndex=controllers.SelectedIndex,controllerText=controllers.SelectedItem?.ToString(),width=Width,height=Height,startEnabled=start.Enabled,
                    stopEnabled=stop.Enabled,pending=SessionWorker.Pending().Count(),status=state.Text,hasProbe=probe is not null});
                allowClose=true;Close();
            }
        };
    }
    bool Running=>worker is not null && !worker.HasExited;
    void UpdateButtons() {
        if(IsDisposed||Disposing)return;
        start.Enabled=!busy && !closing && !Running && probe?.Blockers.Length==0 &&
            controllers.SelectedItem is Controller{Transport:"蓝牙"} && !SessionWorker.Pending().Any();
        start.BackColor=start.Enabled?Color.FromArgb(32,104,221):Color.FromArgb(220,226,236);
        stop.Enabled=Running&&!busy&&!closing; refresh.Enabled=!Running&&!busy&&!closing; controllers.Enabled=!Running&&!busy&&!closing;
        recover.Enabled=!Running&&!busy&&!closing&&SessionWorker.Pending().Any(); export.Enabled=probe is not null&&!closing;
        feedback.Enabled=latency.Enabled=!busy&&!closing;
    }
    async Task RefreshProbe() {
        if(busy||closing)return;
        busy=true;UpdateButtons();
        try {
            probe=await Task.Run(Probe.Read);
            if(closing||IsDisposed)return;
            controllers.Items.Clear();
            foreach(var device in probe.Controllers) controllers.Items.Add(device);
            controllers.SelectedIndex=controllers.Items.Count>0?0:-1;
            for(int i=0;i<controllers.Items.Count;i++) if(controllers.Items[i] is Controller{Transport:"蓝牙"}) {controllers.SelectedIndex=i;break;}
            detail.Text="环境检查\n\n"+string.Join("\n",new[]{
                $"Windows：{probe.Windows}",
                $"受控桥接后端：{(probe.BackendReady?"已就绪":"未就绪")}",
                $"HidHide：{(probe.HidHideInstalled?"已安装":"未安装")}",
                $"usbip-win2：{(probe.UsbipInstalled?"已安装":"未安装")}",
                $"当前默认音频输出：{string.Join(" / ",probe.AudioOutputs)}",
                $"可读取的普通 DualSense：{probe.Controllers.Length} 个",
                $"待恢复会话：{SessionWorker.Pending().Count()} 个"
            })+"\n\n"+(probe.Blockers.Length>0?"启用前需要处理\n"+string.Join("\n",probe.Blockers.Select(x=>"• "+x)):"可以手动启用桥接。")+
                "\n\n"+string.Join("\n",probe.Notes.Select(x=>"• "+x));
            state.Text="原生模式 · 桥接未启动";
            description.Text=SessionWorker.Pending().Any()?"检测到未完成的恢复记录，请先恢复。":
                probe.Blockers.Length>0?"检查完成。尚未修改手柄、音频或驱动配置。":"检查通过，等待你手动启用。";
        } catch(Exception ex) {if(!IsDisposed){detail.Text=ex.Message;description.Text="环境检查失败，已禁止启用。";}probe=null;}
        finally{busy=false;UpdateButtons();}
    }
    Process Launch(string mode,string dir) {
        var info=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=true,WorkingDirectory=Paths.Root,WindowStyle=ProcessWindowStyle.Hidden};
        info.ArgumentList.Add(mode);info.ArgumentList.Add(dir);
        if(!Paths.Admin) info.Verb="runas";
        return Process.Start(info)??throw new InvalidOperationException("无法启动会话管理进程");
    }
    async Task StartBridge() {
        if(busy||closing||controllers.SelectedItem is not Controller selected) return;
        busy=true;UpdateButtons();
        try {
            await CloseFeedbackAsync();
            if(closing)return;
            var dir=Path.Combine(Paths.Sessions,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
            using var parent=Process.GetCurrentProcess();
            AtomicJson.Write(Path.Combine(dir,"request.json"),new SessionRequest(selected,parent.Id,parent.StartTime.ToUniversalTime().Ticks));
            worker=Launch("--worker",dir);session=dir;state.Text="准备蓝牙桥接";description.Text="会话管理器正在检查并启用。";
        } catch(Exception ex){MessageBox.Show(this,ex.Message,"未启用",MessageBoxButtons.OK,MessageBoxIcon.Information);}
        finally{busy=false;UpdateButtons();}
        await Task.CompletedTask;
    }
    void Poll() {
        if(session is null) return;
        var file=Path.Combine(session,"status.json");
        if(File.Exists(file)) try {
            var status=AtomicJson.Read<SessionStatus>(file);
            state.Text=status.State switch {
                "active"=>"蓝牙桥接已连接","checking" or "starting"=>"正在启用蓝牙桥接",
                "restoring"=>"正在停止并检查恢复","restored"=>"桥接已停止 · 配置已清理",
                "restored-with-warning"=>"桥接已停止 · 设备状态待确认",
                "recovery-required"=>"恢复尚未完成","stopped" or "blocked"=>"桥接未运行",_=>status.State
            };
            description.Text=status.Message.Split('\n')[0];
            if(status.State is "restored" or "restored-with-warning" or "stopped" or "blocked" or "recovery-required") detail.Text=status.Message+"\n\n会话记录："+session;
        }catch(IOException){}catch(System.Text.Json.JsonException){}
        if(worker is not null&&worker.HasExited) {
            worker.Dispose();worker=null;
            if(!File.Exists(file)) {state.Text="桥接未运行";description.Text="会话进程已退出，请检查恢复记录。";}
        }
        UpdateButtons();
    }
    async Task<StopOutcome> StopBridge() {
        if(stoppingTask is not null)return await stoppingTask;
        var pending=StopBridgeCore();stoppingTask=pending;
        try{return await pending;}
        finally{if(ReferenceEquals(stoppingTask,pending))stoppingTask=null;}
    }
    bool ConfirmStopForGame() {
        var processes=Process.GetProcessesByName("PBZ");
        try {
            if(processes.Length==0)return true;
            return MessageBox.Show(this,
                "检测到《影之刃：零》仍在运行。\n\n停止桥接会拔除游戏正在使用的虚拟 USB 手柄，游戏可能丢失输入。恢复普通蓝牙连接不能继承 USB 原生触觉等能力，也不能保证游戏自动重新识别。\n\n建议先正常退出游戏，再停止桥接。本工具不会关闭游戏或 Steam。\n\n仍要立即停止桥接吗？",
                "建议先退出游戏",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)==DialogResult.Yes;
        }finally{foreach(var process in processes)process.Dispose();}
    }
    async Task<StopOutcome> StopBridgeCore() {
        busy=true;UpdateButtons();
        try {
            if(Running&&!ConfirmStopForGame())return StopOutcome.Cancelled;
            await CloseFeedbackAsync();
            if(!Running)return SessionWorker.Pending().Any()?StopOutcome.Incomplete:StopOutcome.Complete;
            File.WriteAllText(Path.Combine(session!,"stop.request"),"stop");
            await worker!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            Poll();
            var status=AtomicJson.Read<SessionStatus>(Path.Combine(session!,"status.json"));
            return RecoveryAssessment.CleanupComplete(status.State)&&!SessionWorker.Pending().Any()?StopOutcome.Complete:StopOutcome.Incomplete;
        } catch(Exception ex) {detail.Text="尚未确认配置清理完成："+ex.Message;return StopOutcome.Incomplete;}
        finally{busy=false;UpdateButtons();}
    }
    async Task RecoverPending() {
        if(busy||closing)return;
        var dir=SessionWorker.Pending().FirstOrDefault();if(dir is null)return;
        busy=true;UpdateButtons();
        try {await CloseFeedbackAsync();if(closing)return;worker=Launch("--recover",dir);session=dir;}
        catch(Exception ex){MessageBox.Show(this,ex.Message,"恢复未启动");}
        finally{busy=false;UpdateButtons();}
        await Task.CompletedTask;
    }
    void Export() {
        if(probe is null)return;
        var path=Path.Combine(Paths.State,"diagnostics","check-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".json");
        AtomicJson.Write(path,probe);
        detail.Text+="\n\n已保存检查结果："+path+"\n报告包含本机设备标识，仅保存在本地。";
    }
    async Task OpenFeedback() {
        if(busy||closing)return;
        if(feedbackWindow is {IsDisposed:false} existing){existing.Show();existing.Activate();return;}
        busy=true;UpdateButtons();
        try {
            if(latencyWindow is {IsDisposed:false} bench)await bench.CloseAfterCleanupAsync();
            if(closing)return;
        }finally{busy=false;UpdateButtons();}
        var panel=new FeedbackForm();feedbackWindow=panel;
        panel.FormClosed+=(_,_)=>{if(ReferenceEquals(feedbackWindow,panel))feedbackWindow=null;};
        panel.Show(this);
    }
    async Task OpenLatency() {
        if(busy||closing)return;
        if(latencyWindow is {IsDisposed:false} existing){existing.Show();existing.Activate();return;}
        busy=true;UpdateButtons();
        try {
            if(feedbackWindow is {IsDisposed:false} tests)await tests.CloseAfterCleanupAsync();
            if(closing)return;
            var panel=new LatencyForm();latencyWindow=panel;
            panel.FormClosed+=(_,_)=>{if(ReferenceEquals(latencyWindow,panel))latencyWindow=null;};
            panel.Show(this);
        }finally{busy=false;UpdateButtons();}
    }
    async Task CloseFeedbackAsync() {
        if(latencyWindow is {IsDisposed:false} bench)await bench.CloseAfterCleanupAsync();
        var panel=feedbackWindow;
        if(panel is {IsDisposed:false})await panel.CloseAfterCleanupAsync();
    }
    async void OnClosing(object? sender,FormClosingEventArgs e) {
        if(allowClose)return;
        e.Cancel=true;if(closing)return;closing=true;UpdateButtons();
        try {
            await CloseFeedbackAsync();
            if(Running||stoppingTask is not null) {
                var outcome=await StopBridge();
                if(outcome==StopOutcome.Cancelled)return;
                if(outcome==StopOutcome.Incomplete) {
                    MessageBox.Show(this,"尚未确认配置清理完成，窗口将保持打开。请查看状态并使用“恢复遗留状态”。","请先完成配置清理",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                    return;
                }
            }else if(SessionWorker.Pending().Any()&&
                MessageBox.Show(this,"存在尚未确认恢复的会话。关闭窗口不会清除残留规则；下次需使用“恢复遗留状态”。仍要退出吗？","恢复尚未完成",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
            timer.Stop();
            await Task.Yield(); // Let the original FormClosing event unwind before closing again.
            allowClose=true;Close();
        }catch(Exception ex) {
            detail.Text="尚未确认测试或桥接清理完成："+ex.Message;
            MessageBox.Show(this,"窗口将保持打开，请检查状态后重试停止。","清理尚未完成",MessageBoxButtons.OK,MessageBoxIcon.Warning);
        }finally{closing=false;if(!IsDisposed)UpdateButtons();}
    }
    internal const string GuideText="""
《影之刃：零》验收步骤

1. 先记录 USB 原生基线
   使用同一存档与场景，检查触觉细节、自适应扳机和按键。
   Bluetooth 耳机保持为游戏主音频输出。
   Steam Input 的设置按游戏原生支持情况对比，不全局关闭或改写。

2. 切换到普通蓝牙
   退出游戏，拔掉 USB 数据线，在 Windows 中正常配对并唤醒手柄。
   点击“检查连接”，确认目标显示“蓝牙”。

3. 启用桥接后重新进入游戏
   对比同一场景的触觉与扳机；普通震动不等于原生高清触觉。
   检查耳机有无爆音、断续，其他手柄有无双输入或失效。

4. 停止并恢复
   先正常退出游戏，再点击“停止并恢复原生”。工具会分别记录配置清理与设备状态。
   5 秒观察窗口不能排除稍后的断连；请继续观察耳机和手柄至少 90 秒。
   分别验证蓝牙原生和 USB 原生；重启电脑且不打开工具，再验证一次。

当前边界
软件检查不等于游戏实测通过。系统驱动尚未安装时，不能验证真实桥接效果。
主窗口异常退出时，会话管理器会尝试恢复；断电或两个进程均被强杀时，
持久化隐藏规则可能残留，下次打开需手动恢复。没有开机常驻恢复服务。
""";
}
