namespace DsBridge.App;

internal sealed partial class FeedbackForm {
    void BuildLayout() {
        Text="反馈测试 · DualSense AirBridge";UiTheme.Window(this,new(980,800),new(860,740));StartPosition=FormStartPosition.CenterParent;
        var layout=UiTheme.Stack(UiTheme.Fixed(86),UiTheme.Fixed(122),UiTheme.Fixed(174),UiTheme.Fill(),UiTheme.Fixed(56));layout.Padding=new(28,24,28,20);
        layout.Controls.Add(UiTheme.Heading("手柄反馈测试","无需进入游戏，独立检查手柄的触觉、扳机与声音。测试前请先退出游戏。"),0,0);
        var selection=new UiCard{Dock=DockStyle.Fill,Padding=new(20,14,20,14)};
        var select=UiTheme.Stack(UiTheme.Fixed(28),UiTheme.Fixed(40),UiTheme.Fill());
        select.Controls.Add(UiTheme.Label("测试目标",10,null,true),0,0);
        var row=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,Margin=Padding.Empty};row.ColumnStyles.Add(new(SizeType.Percent,100));row.ColumnStyles.Add(new(SizeType.AutoSize));
        UiTheme.Combo(targets,"反馈测试目标");targets.Anchor=AnchorStyles.Left|AnchorStyles.Right;targets.Margin=new(0,4,12,0);refresh.Padding=new(12,6,12,6);refresh.MinimumSize=new(0,34);refresh.Margin=Padding.Empty;
        row.Controls.Add(targets,0,0);row.Controls.Add(refresh,1,0);select.Controls.Add(row,0,1);
        mode.Font=UiTheme.Text(8);mode.ForeColor=UiTheme.Secondary;mode.Margin=Padding.Empty;select.Controls.Add(mode,0,2);selection.Controls.Add(select);layout.Controls.Add(selection,0,1);
        var choices=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Padding=new(0,16,0,16),Margin=Padding.Empty};
        for(int i=0;i<3;i++)choices.ColumnStyles.Add(new(SizeType.Percent,100f/3));
        choices.Controls.Add(ChoiceCard("精细触觉","左侧 → 右侧 → 双侧","约 4 秒",haptics,new(0,0,8,0)),0,0);
        choices.Controls.Add(ChoiceCard("自适应扳机","轻按 R2，感受阻力","最多 5 秒",trigger,new(4,0,4,0)),1,0);
        choices.Controls.Add(ChoiceCard("手柄扬声器","播放两声低音量提示","约 2 秒 · 静音开关仍生效",speaker,new(8,0,0,0)),2,0);layout.Controls.Add(choices,0,2);
        var result=new UiCard{Dock=DockStyle.Fill,Padding=new(20)};var resultLayout=UiTheme.Stack(UiTheme.Fixed(48),UiTheme.Fill());
        var bar=new TableLayoutPanel{ColumnCount=2,Dock=DockStyle.Fill,Margin=Padding.Empty};bar.ColumnStyles.Add(new(SizeType.Percent,100));bar.ColumnStyles.Add(new(SizeType.AutoSize));
        state.Font=UiTheme.Text(11,FontStyle.Bold);state.ForeColor=UiTheme.Ink;state.AutoSize=false;state.Dock=DockStyle.Fill;state.Margin=new(0,6,12,0);bar.Controls.Add(state,0,0);stop.Margin=Padding.Empty;bar.Controls.Add(stop,1,0);
        resultLayout.Controls.Add(bar,0,0);UiTheme.ReadOnlyText(output);output.AccessibleName="反馈测试记录";resultLayout.Controls.Add(output,0,1);result.Controls.Add(resultLayout);layout.Controls.Add(result,0,3);
        var note=UiTheme.Label("请轻按扳机，不要用力掰动。停止或关闭窗口会取消测试，并等待清理完成。\n普通蓝牙直连不作为完整反馈目标；游戏效果仍需在对应场景中验证。",9,UiTheme.Secondary);note.Dock=DockStyle.Fill;note.Padding=new(0,14,0,0);layout.Controls.Add(note,0,4);
        Controls.Add(layout);ResumeLayout(true);
    }
    static Control ChoiceCard(string title,string description,string duration,Button button,Padding margin) {
        var card=new UiCard{Dock=DockStyle.Fill,Padding=new(16,14,16,12),Margin=margin};
        var stack=UiTheme.Stack(UiTheme.Fixed(29),UiTheme.Fixed(25),UiTheme.Fixed(24),UiTheme.Fill());
        stack.Controls.Add(UiTheme.Label(title,12,null,true),0,0);stack.Controls.Add(UiTheme.Label(description,9,UiTheme.Secondary),0,1);
        stack.Controls.Add(UiTheme.Label(duration,8,UiTheme.Secondary),0,2);button.Font=UiTheme.Text(9);button.Padding=new(12,4,12,4);button.MinimumSize=new(0,32);button.Margin=Padding.Empty;button.Anchor=AnchorStyles.Left;
        stack.Controls.Add(button,0,3);card.Controls.Add(stack);return card;
    }
}
