using System.Drawing.Drawing2D;
using System.ComponentModel;
namespace DsBridge.App;

// Presentation only. These controls keep the native Button/CheckBox semantics,
// keyboard navigation and accessibility; no device state or timing lives here.
internal static class UiTheme {
    internal static readonly Color Background=Color.FromArgb(245,245,247);
    internal static readonly Color Ink=Color.FromArgb(29,29,31);
    internal static readonly Color Secondary=Color.FromArgb(108,108,114);
    internal static readonly Color Line=Color.FromArgb(226,227,232);
    internal static readonly Color Blue=Color.FromArgb(0,113,227);
    internal static readonly Color SoftBlue=Color.FromArgb(233,242,255);
    internal static readonly Color Green=Color.FromArgb(36,128,77);
    internal static readonly Color Amber=Color.FromArgb(153,91,18);
    internal static Color Backdrop(Control control) {
        for(var parent=control.Parent;parent is not null;parent=parent.Parent)
            if(parent.BackColor.A==255)return parent.BackColor;
        return Background;
    }
    internal static Font Text(float size=10,FontStyle style=FontStyle.Regular)=>new("Microsoft YaHei UI",size,style);
    internal static void Window(Form form,Size size,Size minimum) {
        form.SuspendLayout();
        form.AutoScaleDimensions=new SizeF(96,96);form.AutoScaleMode=AutoScaleMode.Dpi;
        form.Font=Text();form.BackColor=Background;form.ForeColor=Ink;
        form.Size=size;form.MinimumSize=minimum;form.StartPosition=FormStartPosition.CenterScreen;
        var iconPath=Path.Combine(AppContext.BaseDirectory,"assets","branding","dualsense-airbridge.ico");
        if(File.Exists(iconPath))form.Icon=new Icon(iconPath);
    }
    internal static Label Label(string text,float size=10,Color? color=null,bool bold=false)=>new(){
        Text=text,AutoSize=true,Font=Text(size,bold?FontStyle.Bold:FontStyle.Regular),
        ForeColor=color??Ink,BackColor=Color.Transparent,Margin=Padding.Empty};
    internal static TableLayoutPanel Stack(params RowStyle[] rows) {
        var grid=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=rows.Length,Margin=Padding.Empty,BackColor=Color.Transparent};
        grid.ColumnStyles.Add(new(SizeType.Percent,100));foreach(var row in rows)grid.RowStyles.Add(row);return grid;
    }
    internal static RowStyle Fixed(int height)=>new(SizeType.Absolute,height);
    internal static RowStyle Auto()=>new(SizeType.AutoSize);
    internal static RowStyle Fill()=>new(SizeType.Percent,100);
    internal static Control Field(string title,Control input) {
        var grid=Stack(Fixed(28),Fill());grid.Margin=new(0,0,12,0);
        grid.Controls.Add(Label(title,9,Secondary),0,0);input.Dock=DockStyle.Fill;input.Margin=Padding.Empty;grid.Controls.Add(input,0,1);return grid;
    }
    internal static void Combo(ComboBox combo,string accessibleName) {
        combo.FlatStyle=FlatStyle.Standard;combo.Font=Text(10);combo.ForeColor=Ink;
        combo.BackColor=Color.White;combo.IntegralHeight=false;combo.DropDownHeight=240;
        combo.AccessibleName=accessibleName;
    }
    internal static void ReadOnlyText(RichTextBox text) {
        text.Font=Text(10);text.ForeColor=Secondary;text.BackColor=Color.White;
        text.BorderStyle=BorderStyle.None;text.ReadOnly=true;text.DetectUrls=false;
        text.WordWrap=true;text.ScrollBars=RichTextBoxScrollBars.Vertical;
    }
    internal static GraphicsPath Round(RectangleF bounds,float radius) {
        var path=new GraphicsPath();float d=Math.Min(radius*2,Math.Min(bounds.Width,bounds.Height));
        if(d<=0){path.AddRectangle(bounds);return path;}
        path.AddArc(bounds.X,bounds.Y,d,d,180,90);path.AddArc(bounds.Right-d,bounds.Y,d,d,270,90);
        path.AddArc(bounds.Right-d,bounds.Bottom-d,d,d,0,90);path.AddArc(bounds.X,bounds.Bottom-d,d,d,90,90);
        path.CloseFigure();return path;
    }
    internal static FlowLayoutPanel Actions(params Control[] controls) {
        var row=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,WrapContents=true,Margin=Padding.Empty,BackColor=Color.Transparent};
        foreach(var control in controls){control.Margin=new(0,0,10,0);row.Controls.Add(control);}return row;
    }
    internal static Control Heading(string title,string subtitle) {
        var grid=Stack(Fixed(39),Fill());grid.Margin=new(0,0,0,16);
        grid.Controls.Add(Label(title,20,Ink,true),0,0);
        var caption=Label(subtitle,9,Secondary);caption.Dock=DockStyle.Fill;grid.Controls.Add(caption,0,1);return grid;
    }
}

internal sealed class UiCard:Panel {
    internal UiCard(){DoubleBuffered=true;ResizeRedraw=true;BackColor=Color.White;Padding=new(22);Margin=Padding.Empty;}
    protected override void OnPaintBackground(PaintEventArgs e) {
        e.Graphics.Clear(UiTheme.Backdrop(this));e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        var scale=DeviceDpi/96f;
        using var path=UiTheme.Round(new RectangleF(.5f,.5f,Width-1,Height-1),16*scale);
        using var fill=new SolidBrush(BackColor);using var pen=new Pen(UiTheme.Line);
        e.Graphics.FillPath(fill,path);e.Graphics.DrawPath(pen,path);
    }
}

internal sealed class UiButton:Button {
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal bool Primary {get;set;}
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal bool Destructive {get;set;}
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal bool Navigation {get;set;}
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal bool Selected {get;set;}
    bool hovered,pressed;
    internal UiButton(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.Opaque|ControlStyles.ResizeRedraw,true);
        FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;BackColor=UiTheme.Background;ForeColor=UiTheme.Ink;
        AutoSize=true;AutoSizeMode=AutoSizeMode.GrowAndShrink;Padding=new(17,10,17,10);MinimumSize=new(0,42);Cursor=Cursors.Hand;}
    public override Size GetPreferredSize(Size proposedSize) {
        var size=TextRenderer.MeasureText(Text,Font);return new(Math.Max(MinimumSize.Width,size.Width+Padding.Horizontal),Math.Max(MinimumSize.Height,size.Height+Padding.Vertical));
    }
    protected override void OnMouseEnter(EventArgs e){hovered=true;Invalidate();base.OnMouseEnter(e);}
    protected override void OnMouseLeave(EventArgs e){hovered=pressed=false;Invalidate();base.OnMouseLeave(e);}
    protected override void OnMouseDown(MouseEventArgs e){pressed=true;Invalidate();base.OnMouseDown(e);}
    protected override void OnMouseUp(MouseEventArgs e){pressed=false;Invalidate();base.OnMouseUp(e);}
    protected override void OnEnabledChanged(EventArgs e){Invalidate();base.OnEnabledChanged(e);}
    protected override void OnPaintBackground(PaintEventArgs e)=>e.Graphics.Clear(UiTheme.Backdrop(this));
    protected override void OnPaint(PaintEventArgs e) {
        using(var background=new SolidBrush(UiTheme.Backdrop(this)))e.Graphics.FillRectangle(background,ClientRectangle);
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        var fill=Primary?UiTheme.Blue:Selected?UiTheme.SoftBlue:Navigation?Color.Transparent:Color.FromArgb(239,240,244);
        var ink=Primary?Color.White:Selected?UiTheme.Blue:Destructive?Color.FromArgb(188,47,48):UiTheme.Ink;
        if(!Enabled){fill=Navigation?Color.Transparent:Color.FromArgb(237,238,241);ink=Color.FromArgb(156,157,164);}
        else if(pressed)fill=Primary?Color.FromArgb(0,91,192):Color.FromArgb(218,228,242);
        else if(hovered)fill=Primary?Color.FromArgb(0,102,213):Selected?Color.FromArgb(220,234,253):Color.FromArgb(231,233,239);
        using var path=UiTheme.Round(new RectangleF(.5f,.5f,Width-1,Height-1),10*DeviceDpi/96f);
        using var brush=new SolidBrush(fill);e.Graphics.FillPath(brush,path);
        var rect=ClientRectangle;rect.Inflate(-Padding.Left,0);
        var flags=TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|(Navigation?TextFormatFlags.Left:TextFormatFlags.HorizontalCenter);
        if(!ShowKeyboardCues)flags|=TextFormatFlags.HidePrefix;
        TextRenderer.DrawText(e.Graphics,Text,Font,rect,ink,flags);
        if(Focused&&ShowFocusCues){using var focus=new Pen(UiTheme.Blue,2);var r=ClientRectangle;r.Inflate(-3,-3);using var fp=UiTheme.Round(r,8);e.Graphics.DrawPath(focus,fp);}
    }
}

internal sealed class UiSwitch:CheckBox {
    internal UiSwitch(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.Opaque|ControlStyles.ResizeRedraw,true);
        AutoSize=false;Height=36;BackColor=Color.White;Cursor=Cursors.Hand;AccessibleRole=AccessibleRole.CheckButton;}
    protected override void OnCheckedChanged(EventArgs e){Invalidate();base.OnCheckedChanged(e);}
    protected override void OnEnabledChanged(EventArgs e){Invalidate();base.OnEnabledChanged(e);}
    protected override void OnPaintBackground(PaintEventArgs e)=>e.Graphics.Clear(UiTheme.Backdrop(this));
    protected override void OnPaint(PaintEventArgs e) {
        using(var background=new SolidBrush(UiTheme.Backdrop(this)))e.Graphics.FillRectangle(background,ClientRectangle);
        var scale=DeviceDpi/96f;e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        var track=new RectangleF(0,(Height-24*scale)/2,42*scale,24*scale);
        using var path=UiTheme.Round(track,12*scale);
        using var brush=new SolidBrush(!Enabled?Color.FromArgb(218,221,227):Checked?UiTheme.Blue:Color.FromArgb(198,200,208));
        e.Graphics.FillPath(brush,path);using var knob=new SolidBrush(Color.White);
        e.Graphics.FillEllipse(knob,track.X+(Checked?20:2)*scale,track.Y+2*scale,20*scale,20*scale);
        var text=new Rectangle((int)(54*scale),0,Math.Max(0,Width-(int)(54*scale)),Height);
        TextRenderer.DrawText(e.Graphics,Text,Font,text,Enabled?UiTheme.Ink:UiTheme.Secondary,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
        if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(ClientRectangle,-1,-1));
    }
}

internal sealed class ControllerIllustration:Control {
    internal ControllerIllustration(){DoubleBuffered=true;ResizeRedraw=true;TabStop=false;AccessibleName="DualSense 手柄示意";}
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
        float scale=Math.Min(Width/220f,Height/160f);g.TranslateTransform((Width-220*scale)/2,(Height-160*scale)/2);g.ScaleTransform(scale,scale);
        using var body=new GraphicsPath();body.StartFigure();body.AddBezier(51,44,30,43,22,73,17,110);body.AddBezier(17,110,9,146,32,149,51,123);
        body.AddLine(51,123,72,104);body.AddBezier(72,104,93,110,127,110,148,104);body.AddLine(148,104,169,123);
        body.AddBezier(169,123,188,149,211,146,203,110);body.AddBezier(203,110,198,73,190,43,169,44);body.CloseFigure();
        using var fill=new SolidBrush(Color.FromArgb(244,246,250));using var outline=new Pen(Color.FromArgb(207,214,225),1.8f);
        g.FillPath(fill,body);g.DrawPath(outline,body);
        using var center=UiTheme.Round(new RectangleF(78,46,64,42),10);using var centerFill=new SolidBrush(Color.FromArgb(225,232,242));g.FillPath(centerFill,center);
        using var blue=new Pen(UiTheme.Blue,2.5f){StartCap=LineCap.Round,EndCap=LineCap.Round};g.DrawLine(blue,77,51,70,82);g.DrawLine(blue,143,51,150,82);
        using var neutral=new SolidBrush(Color.FromArgb(135,148,169));using var dark=new SolidBrush(Color.FromArgb(85,103,131));
        g.FillEllipse(dark,65,88,25,25);g.FillEllipse(dark,130,88,25,25);
        g.FillRectangle(neutral,47,61,9,27);g.FillRectangle(neutral,38,70,27,9);
        foreach(var p in new[]{new Point(168,60),new Point(157,71),new Point(179,71),new Point(168,82)})g.FillEllipse(neutral,p.X,p.Y,7,7);
        using var signal=new Pen(Color.FromArgb(139,184,234),2){StartCap=LineCap.Round,EndCap=LineCap.Round};
        g.DrawArc(signal,93,13,34,25,210,120);g.DrawArc(signal,102,23,16,13,210,120);
    }
}
