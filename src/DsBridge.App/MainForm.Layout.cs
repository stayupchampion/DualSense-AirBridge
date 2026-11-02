namespace DsBridge.App;

internal sealed partial class MainForm {
    readonly UiButton overviewNav=new(){Text="连接概览",Navigation=true};
    readonly UiButton diagnosticsNav=new(){Text="检查与恢复",Navigation=true};
    readonly Label pageTitle=UiTheme.Label("连接概览",22,null,true);
    readonly Label pageSubtitle=UiTheme.Label("让 DualSense 的原生反馈，无线抵达。",10,UiTheme.Secondary);
    readonly Label connectionKicker=UiTheme.Label("●  连接状态",9,UiTheme.Secondary);
    readonly RichTextBox guideContent=new(){Dock=DockStyle.Fill};
    readonly Panel pageHost=new(){Dock=DockStyle.Fill,Margin=Padding.Empty};
    Control? overviewPage,diagnosticsPage,guidePage;
    string currentPage="overview";

    void BuildShell() {
        Text="DualSense AirBridge";
        UiTheme.Window(this,args.Length>=3&&args[0]=="--ui-check"?new(980,740):new(1120,840),new(980,740));
        var shell=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};
        shell.ColumnStyles.Add(new(SizeType.Absolute,210));shell.ColumnStyles.Add(new(SizeType.Percent,100));
        shell.RowStyles.Add(new(SizeType.Percent,100));
        var side=new Panel{Dock=DockStyle.Fill,BackColor=Color.FromArgb(249,249,251),Padding=new(20,28,20,20),Margin=Padding.Empty};
        side.Paint+=(_,e)=>{using var pen=new Pen(UiTheme.Line);e.Graphics.DrawLine(pen,side.Width-1,0,side.Width-1,side.Height);};
        var menu=UiTheme.Stack(UiTheme.Fixed(120),UiTheme.Fixed(50),UiTheme.Fixed(50),UiTheme.Fixed(50),UiTheme.Fill(),UiTheme.Fixed(64));
        var brand=UiTheme.Stack(UiTheme.Fixed(40),UiTheme.Fixed(26),UiTheme.Fixed(24));
        var mark=new Label{Text="AirBridge",Font=new("Segoe UI",21,FontStyle.Bold),AutoSize=true,ForeColor=UiTheme.Ink,Margin=Padding.Empty};
        brand.Controls.Add(mark,0,0);brand.Controls.Add(UiTheme.Label("为 DualSense 而设计",9,UiTheme.Secondary),0,1);
        brand.Controls.Add(UiTheme.Label("界面预览",8,UiTheme.Blue),0,2);menu.Controls.Add(brand,0,0);
        var navs=new[]{overviewNav,diagnosticsNav,guide};
        for(int i=0;i<navs.Length;i++) {var button=navs[i];button.AutoSize=false;button.Dock=DockStyle.Fill;button.Margin=new(0,0,0,6);button.AccessibleName=button.Text;menu.Controls.Add(button,0,i+1);}
        var foot=UiTheme.Label("按需启用 · 默认不自启\n普通 DualSense / Windows",8,UiTheme.Secondary);foot.Dock=DockStyle.Bottom;menu.Controls.Add(foot,0,5);
        side.Controls.Add(menu);shell.Controls.Add(side,0,0);
        var main=UiTheme.Stack(UiTheme.Fixed(86),UiTheme.Fill(),UiTheme.Fixed(30));main.Padding=new(28,24,28,16);
        var header=new TableLayoutPanel{ColumnCount=2,Dock=DockStyle.Fill,Margin=Padding.Empty};
        header.ColumnStyles.Add(new(SizeType.Percent,100));header.ColumnStyles.Add(new(SizeType.AutoSize));
        var title=UiTheme.Stack(UiTheme.Fixed(44),UiTheme.Fill());title.Controls.Add(pageTitle,0,0);title.Controls.Add(pageSubtitle,0,1);
        header.Controls.Add(title,0,0);refresh.Anchor=AnchorStyles.Top|AnchorStyles.Right;refresh.Margin=new(12,4,0,0);header.Controls.Add(refresh,1,0);main.Controls.Add(header,0,0);
        overviewPage=BuildOverview();diagnosticsPage=BuildDiagnostics();guidePage=BuildGuide();
        pageHost.Controls.AddRange([guidePage,diagnosticsPage,overviewPage]);main.Controls.Add(pageHost,0,1);
        var footer=UiTheme.Label("原生反馈，按需连接。",8,UiTheme.Secondary);footer.Dock=DockStyle.Fill;footer.TextAlign=ContentAlignment.BottomLeft;main.Controls.Add(footer,0,2);
        shell.Controls.Add(main,1,0);Controls.Add(shell);
        overviewNav.Click+=(_,_)=>ShowPage("overview");diagnosticsNav.Click+=(_,_)=>ShowPage("diagnostics");
        state.TextChanged+=(_,_)=>UpdatePresentation();ShowPage("overview");
        ResumeLayout(true);
    }
    Control BuildOverview() {
        var scroll=new Panel{Dock=DockStyle.Fill,AutoScroll=true,Margin=Padding.Empty};
        var layout=UiTheme.Stack(UiTheme.Fixed(260),UiTheme.Fixed(16),UiTheme.Fixed(178),UiTheme.Fixed(16),UiTheme.Fixed(122));
        layout.Dock=DockStyle.Top;layout.Height=592;
        var hero=new UiCard{Dock=DockStyle.Fill};
        var heroGrid=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=3,Margin=Padding.Empty,BackColor=Color.White};
        heroGrid.ColumnStyles.Add(new(SizeType.Percent,100));heroGrid.ColumnStyles.Add(new(SizeType.Absolute,144));
        heroGrid.RowStyles.Add(new(SizeType.Absolute,116));heroGrid.RowStyles.Add(new(SizeType.Absolute,48));heroGrid.RowStyles.Add(new(SizeType.Percent,100));
        var status=UiTheme.Stack(UiTheme.Fixed(23),UiTheme.Fixed(58),UiTheme.Fill());
        state.AutoSize=false;state.Dock=DockStyle.Fill;state.Font=UiTheme.Text(17,FontStyle.Bold);state.ForeColor=UiTheme.Ink;state.Margin=Padding.Empty;
        description.AutoSize=false;description.Dock=DockStyle.Fill;description.Font=UiTheme.Text(9);description.ForeColor=UiTheme.Secondary;description.Margin=Padding.Empty;
        status.Controls.Add(connectionKicker,0,0);status.Controls.Add(state,0,1);status.Controls.Add(description,0,2);
        heroGrid.Controls.Add(status,0,0);heroGrid.Controls.Add(new ControllerIllustration{Dock=DockStyle.Fill,BackColor=Color.White},1,0);
        var inspect=new UiButton{Text="查看检查结果"};inspect.Click+=(_,_)=>ShowPage("diagnostics");
        var actionRow=UiTheme.Actions(start,stop,inspect);heroGrid.Controls.Add(actionRow,0,1);heroGrid.SetColumnSpan(actionRow,2);
        var select=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,Margin=new(0,10,0,0)};
        select.ColumnStyles.Add(new(SizeType.Absolute,80));select.ColumnStyles.Add(new(SizeType.Percent,100));
        var label=UiTheme.Label("实体连接",9,UiTheme.Secondary);label.Anchor=AnchorStyles.Left;select.Controls.Add(label,0,0);
        UiTheme.Combo(controllers,"选择实体手柄");controllers.Anchor=AnchorStyles.Left|AnchorStyles.Right;controllers.Margin=Padding.Empty;select.Controls.Add(controllers,1,0);
        heroGrid.Controls.Add(select,0,2);heroGrid.SetColumnSpan(select,2);hero.Controls.Add(heroGrid);layout.Controls.Add(hero,0,0);
        layout.Controls.Add(CreateLiveControls(),0,2);
        var tools=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};
        tools.ColumnStyles.Add(new(SizeType.Percent,50));tools.ColumnStyles.Add(new(SizeType.Percent,50));
        tools.Controls.Add(ToolCard("反馈测试","快速检查精细触觉、扳机和扬声器。",feedback,new(0,0,8,0)),0,0);
        tools.Controls.Add(ToolCard("延迟对比","按相同流程比较 USB 与蓝牙桥接。",latency,new(8,0,0,0)),1,0);
        layout.Controls.Add(tools,0,4);scroll.Controls.Add(layout);return scroll;
    }
    static Control ToolCard(string title,string subtitle,Button button,Padding margin) {
        var card=new UiCard{Dock=DockStyle.Fill,Padding=new(18,14,18,12),Margin=margin};
        var layout=UiTheme.Stack(UiTheme.Fixed(25),UiTheme.Fixed(26),UiTheme.Fill());
        layout.Controls.Add(UiTheme.Label(title,11,null,true),0,0);layout.Controls.Add(UiTheme.Label(subtitle,9,UiTheme.Secondary),0,1);
        button.Font=UiTheme.Text(9);button.Padding=new(12,4,12,4);button.MinimumSize=new(0,32);button.Anchor=AnchorStyles.Left;button.Margin=Padding.Empty;layout.Controls.Add(button,0,2);card.Controls.Add(layout);return card;
    }
    Control BuildDiagnostics() {
        var card=new UiCard{Dock=DockStyle.Fill};var layout=UiTheme.Stack(UiTheme.Fixed(28),UiTheme.Fixed(44),UiTheme.Fill(),UiTheme.Fixed(60));
        layout.Controls.Add(UiTheme.Label("环境与会话",13,null,true),0,0);
        var caption=UiTheme.Label("查看依赖和恢复记录。导出的设备标识仅保存在本机。",9,UiTheme.Secondary);caption.Dock=DockStyle.Fill;layout.Controls.Add(caption,0,1);
        UiTheme.ReadOnlyText(detail);detail.AccessibleName="环境检查与会话记录";layout.Controls.Add(detail,0,2);
        var actions=UiTheme.Actions(export,recover);actions.Padding=new(0,14,0,0);layout.Controls.Add(actions,0,3);card.Controls.Add(layout);return card;
    }
    Control BuildGuide() {
        var card=new UiCard{Dock=DockStyle.Fill};var layout=UiTheme.Stack(UiTheme.Fixed(32),UiTheme.Fixed(36),UiTheme.Fill());
        layout.Controls.Add(UiTheme.Label("从连接到原生反馈",13,null,true),0,0);
        layout.Controls.Add(UiTheme.Label("先启用桥接，再进入游戏；切换连接前先退出游戏。",9,UiTheme.Secondary),0,1);
        UiTheme.ReadOnlyText(guideContent);guideContent.Text=GuideText;guideContent.AccessibleName="游戏使用指南";layout.Controls.Add(guideContent,0,2);card.Controls.Add(layout);return card;
    }
    void ShowPage(string page) {
        currentPage=page;
        if(overviewPage is null||diagnosticsPage is null||guidePage is null)return;
        overviewPage.Visible=page=="overview";diagnosticsPage.Visible=page=="diagnostics";guidePage.Visible=page=="guide";
        overviewNav.Selected=page=="overview";diagnosticsNav.Selected=page=="diagnostics";guide.Selected=page=="guide";
        foreach(var nav in new[]{overviewNav,diagnosticsNav,guide})nav.Invalidate();
        pageTitle.Text=page=="overview"?"连接概览":page=="diagnostics"?"检查与恢复":"使用指南";
        pageSubtitle.Text=page=="overview"?"让 DualSense 的原生反馈，无线抵达。":page=="diagnostics"?"连接环境、恢复状态与本地记录。":"把第一次连接，变成简单的几步。";
    }
    void UpdatePresentation() {
        start.Visible=!Running;stop.Visible=Running;
        var value=state.Text;
        var active=value=="蓝牙桥接已连接";
        var warning=value.Contains("待确认")||value.Contains("尚未")||value.Contains("失败")||value.Contains("迹象")||value.Contains("不可用");
        connectionKicker.Text=active?"●  桥接已启用":warning?"●  需要检查":"●  连接状态";
        connectionKicker.ForeColor=active?UiTheme.Green:warning?UiTheme.Amber:UiTheme.Secondary;
        recover.Destructive=recover.Enabled;recover.Invalidate();
    }
}
