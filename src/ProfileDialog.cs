using System.Text.Json;

namespace EpgCollector;

public sealed class ProfileDialog : Form
{
    public Profile Value { get; private set; }
    readonly TextBox name=new(){Dock=DockStyle.Fill,ReadOnly=true};
    readonly TextBox driver=new(){Dock=DockStyle.Fill};
    readonly TextBox chset=new(){Dock=DockStyle.Fill};
    readonly ScheduleEditor schedules;
    readonly NumericUpDown parallel=new(){Minimum=1,Maximum=16,Width=75};
    readonly CheckBox enabled=new(){Text="このBonDriverの定期実行を有効にする",AutoSize=true};
    readonly ChannelChecks channels=new(){Dock=DockStyle.Fill,CheckOnClick=true,HorizontalScrollbar=true,IntegralHeight=false,BorderStyle=BorderStyle.FixedSingle};
    readonly Label note=new(){AutoSize=true,ForeColor=Color.DimGray};
    readonly Dictionary<string,CheckBox> categories=[];
    readonly Dictionary<ushort,CheckBox> basicOptions=[];
    readonly FlowLayoutPanel basicPanel=new(){Dock=DockStyle.Fill,WrapContents=false};
    List<Channel> loaded=[];
    string loadedPath="";
    public ProfileDialog(Profile value,bool isRunning=false)
    {
        Value=JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(value))!;
        schedules=new(Value.ScheduleEntries());
        Text="BonDriverの取得設定";Icon=Ui.AppIcon;Font=new("Yu Gothic UI",9);ClientSize=new(880,720);MinimumSize=new(760,650);StartPosition=FormStartPosition.CenterParent;BackColor=Color.White;
        name.Text=Value.DisplayName;driver.Text=Value.Driver;chset.Text=Value.ChSet;parallel.Value=Compat.Clamp(Value.Parallel,1,16);enabled.Checked=Value.Enabled;
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new(20),ColumnCount=1,RowCount=5};
        foreach(var h in new[]{42,42,42})layout.RowStyles.Add(new(SizeType.Absolute,h));layout.RowStyles.Add(new(SizeType.Percent,100));layout.RowStyles.Add(new(SizeType.Absolute,48));
        layout.Controls.Add(Row("ファイル名",name));
        layout.Controls.Add(Row("BonDriver",driver,()=>BrowseDriver()));
        layout.Controls.Add(Row("ChSet4",chset,()=>BrowseChset()));
        var tabs=new TabControl{Name="settingsTabs",Dock=DockStyle.Fill};var captureTab=new TabPage("取得チャンネル");var scheduleTab=new TabPage("曜日・時刻");tabs.TabPages.Add(captureTab);tabs.TabPages.Add(scheduleTab);layout.Controls.Add(tabs);
        var capture=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new(10),ColumnCount=1,RowCount=6};capture.RowStyles.Add(new(SizeType.Absolute,36));capture.RowStyles.Add(new(SizeType.Absolute,34));capture.RowStyles.Add(new(SizeType.Absolute,36));capture.RowStyles.Add(new(SizeType.Absolute,36));capture.RowStyles.Add(new(SizeType.Percent,100));capture.RowStyles.Add(new(SizeType.Absolute,24));
        var timing=Flow();timing.Controls.Add(new Label{Text="同時取得数",AutoSize=true,Padding=new(0,5,0,0)});timing.Controls.Add(parallel);capture.Controls.Add(timing);
        var categoryRow=Flow();foreach(string category in new[]{"地デジ","BS","CS","スカパー！","その他"}){var check=new CheckBox{Text=category,AutoSize=true,ThreeState=true,AutoCheck=false};check.Click+=(_,_)=>{bool select=check.CheckState!=CheckState.Checked;for(int i=0;i<loaded.Count;i++)if(loaded[i].Category==category)channels.SetItemChecked(i,select);};categories[category]=check;categoryRow.Controls.Add(check);}capture.Controls.Add(categoryRow);
        basicPanel.Controls.Add(new Label{Text="基本情報のみ取得",AutoSize=true,Padding=new(0,3,0,0)});
        foreach(var option in new[]{((ushort)4,"BS"),((ushort)6,"CS1"),((ushort)7,"CS2"),((ushort)10,"CS3（スカパー！）")}){var check=new CheckBox{Text=option.Item2,AutoSize=true,Checked=Value.BasicOnly(option.Item1)};basicOptions[option.Item1]=check;basicPanel.Controls.Add(check);}capture.Controls.Add(basicPanel);
        var buttons=Flow();Btn(buttons,"すべて選択",()=>CheckAll(true));Btn(buttons,"選択解除",()=>CheckAll(false));capture.Controls.Add(buttons);capture.Controls.Add(channels);capture.Controls.Add(note);captureTab.Controls.Add(capture);
        capture.SetCellPosition(timing,new(0,0));capture.SetCellPosition(categoryRow,new(0,1));capture.SetCellPosition(basicPanel,new(0,2));capture.SetCellPosition(buttons,new(0,3));capture.SetCellPosition(channels,new(0,4));capture.SetCellPosition(note,new(0,5));
        channels.ItemCheck+=(_,e)=>UpdateCategories(e.Index,e.NewValue);
        if(isRunning)note.Text="変更は次回の取得から反映します。";
        var scheduleLayout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Padding=new(10)};scheduleLayout.RowStyles.Add(new(SizeType.Absolute,40));scheduleLayout.RowStyles.Add(new(SizeType.Percent,100));scheduleLayout.Controls.Add(enabled);scheduleLayout.Controls.Add(schedules);scheduleTab.Controls.Add(scheduleLayout);
        var footer=Flow();footer.FlowDirection=FlowDirection.RightToLeft;var cancel=Ui.Button("キャンセル");cancel.DialogResult=DialogResult.Cancel;footer.Controls.Add(cancel);Btn(footer,"OK",Accept);CancelButton=cancel;layout.Controls.Add(footer);Controls.Add(layout);
        if(File.Exists(chset.Text)){try{LoadChannels(false);}catch(Exception ex){note.Text=ex.Message;}}
        chset.Validated+=(_,_)=>{if(File.Exists(chset.Text)&&Path.GetFullPath(chset.Text)!=loadedPath){try{LoadChannels(true);}catch(Exception ex){note.Text=ex.Message;}}};
    }
    static FlowLayoutPanel Flow()=>new(){Dock=DockStyle.Fill,WrapContents=false};
    Control Row(string title,Control input,Action? browse=null)
    {
        var row=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=browse==null?2:3};row.ColumnStyles.Add(new(SizeType.Absolute,90));row.ColumnStyles.Add(new(SizeType.Percent,100));if(browse!=null)row.ColumnStyles.Add(new(SizeType.Absolute,90));row.Controls.Add(new Label{Text=title,AutoSize=true,Padding=new(0,5,0,0)});row.Controls.Add(input);if(browse!=null){var b=Ui.Button("参照…");b.Click+=(_,_)=>browse();row.Controls.Add(b);}return row;
    }
    static void Btn(FlowLayoutPanel panel,string text,Action click){var b=Ui.Button(text);b.Click+=(_,_)=>{try{click();}catch(Exception ex){MessageBox.Show(ex.Message,"設定確認");}};panel.Controls.Add(b);}
    void BrowseDriver()
    {
        using var d=new OpenFileDialog{Filter="BonDriver (*.dll)|*.dll",FileName=driver.Text};
        if(d.ShowDialog()!=DialogResult.OK)return;
        driver.Text=d.FileName;name.Text=Path.GetFileName(d.FileName);
        if(Edcb.FindChSet(d.FileName) is {} candidate){chset.Text=candidate;LoadChannels(true);}
    }
    void BrowseChset(){using var d=new OpenFileDialog{Filter="ChSet4 (*.ChSet4.txt)|*.ChSet4.txt|テキスト (*.txt)|*.txt",FileName=chset.Text};if(d.ShowDialog()==DialogResult.OK){chset.Text=d.FileName;LoadChannels(true);}}
    void LoadChannels(bool selectAll)
    {
        bool sameFile=loadedPath==Path.GetFullPath(chset.Text);
        var selected=sameFile?channels.CheckedItems.Cast<Channel>().Select(c=>c.Key).ToHashSet():Value.Selected.ToHashSet();
        loaded=Channels.Read(chset.Text).Where(c=>c.EpgServices.Count>0).ToList();loadedPath=Path.GetFullPath(chset.Text);channels.Items.Clear();
        foreach(var c in loaded)channels.Items.Add(c,(!sameFile&&selectAll)||selected.Contains(c.Key));
        foreach(var option in basicOptions)option.Value.Visible=loaded.Any(c=>c.Onid==option.Key);
        basicPanel.Visible=loaded.Any(c=>basicOptions.ContainsKey(c.Onid));if(basicPanel.Parent is TableLayoutPanel table)table.RowStyles[2].Height=basicPanel.Visible?36:0;UpdateCategories();
    }
    void UpdateCategories(int changing=-1,CheckState newState=CheckState.Unchecked)
    {
        foreach(var pair in categories){var indexes=Enumerable.Range(0,Math.Min(loaded.Count,channels.Items.Count)).Where(i=>loaded[i].Category==pair.Key).ToList();int count=indexes.Count(i=>i==changing?newState==CheckState.Checked:channels.GetItemChecked(i));pair.Value.Enabled=indexes.Count>0;pair.Value.CheckState=count==0?CheckState.Unchecked:count==indexes.Count?CheckState.Checked:CheckState.Indeterminate;}
    }
    void CheckAll(bool value){for(int i=0;i<channels.Items.Count;i++)channels.SetItemChecked(i,value);}
    void Accept()
    {
        if(loadedPath!=Path.GetFullPath(chset.Text))throw new InvalidDataException("変更したChSet4を読み込んでください。");
        Value.Driver=Path.GetFullPath(driver.Text);Value.Name=Path.GetFileName(Value.Driver);Value.ChSet=loadedPath;Value.Parallel=(int)parallel.Value;Value.Schedule=schedules.Entries;Value.Enabled=enabled.Checked;
        Value.BSBasicOnly=basicOptions[4].Checked;Value.CS1BasicOnly=basicOptions[6].Checked;Value.CS2BasicOnly=basicOptions[7].Checked;Value.CS3BasicOnly=basicOptions[10].Checked;
        foreach(var entry in Value.Schedule)entry.Validate();
        Value.Selected=channels.CheckedItems.Cast<Channel>().Select(c=>c.Key).ToList();Collector.Validate(Value);DialogResult=DialogResult.OK;Close();
    }
}
