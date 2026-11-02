namespace DsBridge.App;

internal sealed partial class LatencyForm {
    void BuildLayout() {
        Text="延迟对比 · DualSense AirBridge";UiTheme.Window(this,new(1120,960),new(980,900));
        var layout=UiTheme.Stack(UiTheme.Fixed(92),UiTheme.Fixed(108),UiTheme.Fixed(58),UiTheme.Fixed(34),
            new(SizeType.Percent,65),UiTheme.Auto(),new(SizeType.Percent,35));layout.Padding=new(28,24,28,24);
        layout.Controls.Add(UiTheme.Heading("USB 与蓝牙延迟对比","同流程拍摄 USB 与蓝牙桥接。输入拍手部动作，输出拍提交标记与实际反馈；\n物理延迟需结合视频逐帧核对，软件报告间隔不代表端到端延迟。"),0,0);
        var settings=new UiCard{Dock=DockStyle.Fill,Padding=new(20,14,20,14)};
        var selection=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=4,RowCount=1,Margin=Padding.Empty};
        selection.ColumnStyles.Add(new(SizeType.Percent,52));selection.ColumnStyles.Add(new(SizeType.Absolute,110));
        selection.ColumnStyles.Add(new(SizeType.Percent,28));selection.ColumnStyles.Add(new(SizeType.Percent,20));
        selection.RowStyles.Add(new(SizeType.Percent,100));
        UiTheme.Combo(targets,"延迟对比目标");UiTheme.Combo(kinds,"要记录的输入或反馈");UiTheme.Combo(fps,"手机拍摄帧率");
        selection.Controls.Add(UiTheme.Field("测试目标",targets),0,0);refresh.Anchor=AnchorStyles.Left|AnchorStyles.Top;refresh.Margin=new(0,28,12,0);refresh.Font=UiTheme.Text(9);refresh.Padding=new(10,6,10,6);refresh.MinimumSize=new(0,34);selection.Controls.Add(refresh,1,0);
        selection.Controls.Add(UiTheme.Field("测试项目",kinds),2,0);selection.Controls.Add(UiTheme.Field("拍摄帧率 / fps",fps),3,0);settings.Controls.Add(selection);layout.Controls.Add(settings,0,1);
        var actions=UiTheme.Actions(start,stop);actions.Padding=new(0,14,0,0);layout.Controls.Add(actions,0,2);
        state.ForeColor=UiTheme.Secondary;state.Font=UiTheme.Text(9);state.Margin=new(0,4,0,4);state.Dock=DockStyle.Fill;layout.Controls.Add(state,0,3);
        // Keep the recording canvas and its paint/timestamp callbacks unchanged.
        // Its black/white contrast is intentional for slow-motion frame review.
        var screen=new UiCard{Dock=DockStyle.Fill,Padding=new(12)};screen.Controls.Add(canvas);layout.Controls.Add(screen,0,4);
        instructions.Font=UiTheme.Text(9);instructions.ForeColor=UiTheme.Secondary;instructions.Padding=new(0,12,0,12);instructions.Margin=Padding.Empty;layout.Controls.Add(instructions,0,5);
        var report=new UiCard{Dock=DockStyle.Fill,Padding=new(18,14,18,14)};var reportLayout=UiTheme.Stack(UiTheme.Fixed(29),UiTheme.Fill());
        reportLayout.Controls.Add(UiTheme.Label("本次记录",10,null,true),0,0);UiTheme.ReadOnlyText(notes);notes.AccessibleName="延迟对比记录";reportLayout.Controls.Add(notes,0,1);report.Controls.Add(reportLayout);layout.Controls.Add(report,0,6);
        Controls.Add(layout);ResumeLayout(true);
    }
}
