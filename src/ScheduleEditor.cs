namespace EpgCollector;

// Mirrors EpgTimer SearchKeyView's weekday selection, time selectors and add/delete list.
// Collection is a start trigger, so there is no programme-search end-time filter.
public sealed class ScheduleEditor : UserControl
{
    readonly ListBox list=new(){Dock=DockStyle.Fill,SelectionMode=SelectionMode.MultiExtended,IntegralHeight=false};
    readonly CheckBox[] days=Enumerable.Range(0,7).Select(i=>new CheckBox{Text="日月火水木金土"[i].ToString(),Checked=true,AutoSize=true}).ToArray();
    readonly ComboBox hour=new(){Name="scheduleHour",DropDownStyle=ComboBoxStyle.DropDownList,Width=65};
    readonly ComboBox minute=new(){Name="scheduleMinute",DropDownStyle=ComboBoxStyle.DropDownList,Width=65};
    public List<ScheduleEntry> Entries=>list.Items.Cast<ScheduleEntry>().Select(s=>new ScheduleEntry{Days=s.Days,Hour=s.Hour,Minute=s.Minute}).ToList();
    public ScheduleEditor(IEnumerable<ScheduleEntry> entries)
    {
        Dock=DockStyle.Fill;Padding=new(12);
        hour.Items.AddRange(Enumerable.Range(0,24).Select(i=>(object)i.ToString("00")).ToArray());hour.SelectedIndex=4;
        minute.Items.AddRange(Enumerable.Range(0,60).Select(i=>(object)i.ToString("00")).ToArray());minute.SelectedIndex=0;
        foreach(var e in entries){e.Validate();list.Items.Add(e);}
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=5};layout.ColumnStyles.Add(new(SizeType.Percent,100));layout.ColumnStyles.Add(new(SizeType.Absolute,90));
        layout.RowStyles.Add(new(SizeType.Absolute,32));layout.RowStyles.Add(new(SizeType.Percent,100));layout.RowStyles.Add(new(SizeType.Absolute,42));layout.RowStyles.Add(new(SizeType.Absolute,48));layout.RowStyles.Add(new(SizeType.Absolute,50));
        layout.Controls.Add(new Label{Text="実行曜日・時刻",AutoSize=true},0,0);layout.Controls.Add(list,0,1);
        var delete=Ui.Button("削除");delete.Click+=(_,_)=>{foreach(var e in list.SelectedItems.Cast<ScheduleEntry>().ToList())list.Items.Remove(e);};layout.Controls.Add(delete,1,1);
        var weekday=new FlowLayoutPanel{Dock=DockStyle.Fill};foreach(var d in days)weekday.Controls.Add(d);var all=Ui.Button("毎日");all.Click+=(_,_)=>{foreach(var d in days)d.Checked=true;};weekday.Controls.Add(all);layout.Controls.Add(weekday,0,2);layout.SetColumnSpan(weekday,2);
        var time=new FlowLayoutPanel{Dock=DockStyle.Fill};time.Controls.Add(new Label{Text="開始時刻",AutoSize=true,Padding=new(0,5,0,0)});time.Controls.Add(hour);time.Controls.Add(new Label{Text="時",AutoSize=true,Padding=new(0,5,0,0)});time.Controls.Add(minute);time.Controls.Add(new Label{Text="分",AutoSize=true,Padding=new(0,5,0,0)});
        var add=Ui.Button("追加");add.Click+=(_,_)=>{int mask=Enumerable.Range(0,7).Where(i=>days[i].Checked).Sum(i=>1<<i);if(mask==0){MessageBox.Show("曜日を選んでください。");return;}var entry=new ScheduleEntry{Days=mask,Hour=hour.SelectedIndex,Minute=minute.SelectedIndex};if(!Entries.Any(s=>s.Days==entry.Days&&s.Hour==entry.Hour&&s.Minute==entry.Minute))list.Items.Add(entry);};time.Controls.Add(add);layout.Controls.Add(time,0,3);layout.SetColumnSpan(time,2);
        layout.Controls.Add(new Label{Text="実行する曜日と時刻を追加してください。",AutoSize=true,ForeColor=Color.DimGray},0,4);layout.SetColumnSpan(layout.GetControlFromPosition(0,4)!,2);Controls.Add(layout);
        Load+=(_,_)=>{if(hour.SelectedIndex<0)hour.SelectedIndex=4;if(minute.SelectedIndex<0)minute.SelectedIndex=0;};
    }
}
