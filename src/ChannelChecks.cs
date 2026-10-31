using System.Windows.Forms.VisualStyles;

namespace EpgCollector;

internal sealed class ChannelChecks : CheckedListBox
{
    bool mouseAction,allowToggle;
    protected override void WndProc(ref Message m)
    {
        if(m.Msg is not (0x201 or 0x202 or 0x203)){base.WndProc(ref m);return;}
        int x=(short)(m.LParam.ToInt64()&0xFFFF),y=(short)((m.LParam.ToInt64()>>16)&0xFFFF);
        int index=IndexFromPoint(new Point(x,y));mouseAction=true;allowToggle=false;
        if(index>=0)
        {
            using var g=CreateGraphics();int width=CheckBoxRenderer.GetGlyphSize(g,CheckBoxState.UncheckedNormal).Width+6;
            var bounds=GetItemRectangle(index);allowToggle=x>=bounds.Left&&x<bounds.Left+width;
        }
        try{base.WndProc(ref m);}finally{mouseAction=false;}
    }
    protected override void OnItemCheck(ItemCheckEventArgs e){if(mouseAction&&!allowToggle)e.NewValue=e.CurrentValue;base.OnItemCheck(e);}
}
