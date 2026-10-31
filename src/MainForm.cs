using System.Diagnostics;
using System.ComponentModel;

namespace EpgCollector;

public sealed class MainForm : Form
{
    readonly string app=AppContext.BaseDirectory;
    readonly Settings settings;
    readonly TextBox output=new(){Dock=DockStyle.Fill};
    readonly CheckBox reload=new(){Text="取得後にEDCBを再読み込み",AutoSize=true};
    readonly CheckBox sequential=new(){Text="複数のBonを順番に取得",AutoSize=true};
    readonly CheckBox armed=new(){Text="時刻指定を有効にする（起動中のみ）",AutoSize=true};
    readonly DataGridView profiles=Grid();
    readonly DataGridView workers=Grid();
    readonly TextBox log=new(){Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,BackColor=Color.FromArgb(246,248,251),BorderStyle=BorderStyle.None};
    readonly Label status=new(){Text="待機中",AutoSize=true,ForeColor=Color.FromArgb(30,94,160),Padding=new(0,9,0,0)};
    readonly List<Button> editing=[];
    readonly Button stop=Ui.Button("停止",false);
    readonly System.Windows.Forms.Timer timer=new(){Interval=1000};
    readonly ScheduleClock schedule=new();
    readonly BonRuns runs=new();
    bool running;
    // Tail of the sequential queue; each Bon waits for the previous one to finish (FIFO).
    Task sequentialTail=Task.CompletedTask;
    string configPath=>Path.Combine(app,"config.json");
    public MainForm()
    {
        Text="EDCBepgEx — EPG取得";Icon=Ui.AppIcon;Font=new("Yu Gothic UI",9);ClientSize=new(1040,700);MinimumSize=new(900,600);StartPosition=FormStartPosition.CenterScreen;BackColor=Color.White;
        try{settings=Settings.Load(configPath);}catch(Exception ex){settings=new();MessageBox.Show("設定を読めませんでした。既存ファイルは自動上書きしません。\n"+ex.Message);}
        if(settings.Profiles.Count==0)
        {
            string root=Path.GetFullPath(Path.Combine(app,"..","..","EDCB"));
            string driver=Path.Combine(root,"BonDriver_Proxy_PX-MLT.dll");
            string chset=Path.Combine(root,"BonDriver_Proxy_PX-MLT(BonDriverProxy).ChSet4.txt");
            if(File.Exists(driver)&&File.Exists(chset))
            {
                settings.Profiles.Add(new(){Name="PX-MLT",Driver=driver,ChSet=chset,Selected=Channels.Read(chset).Where(c=>c.EpgServices.Count>0).Select(c=>c.Key).ToList()});
                if(string.IsNullOrEmpty(settings.Output))
                {
                    settings.Output=Path.GetFullPath(Path.Combine(app,"..","TestEpgData"));
                    Directory.CreateDirectory(settings.Output);
                    settings.Reload=false;
                }
            }
        }
        output.Text=settings.Output;reload.Checked=settings.Reload;sequential.Checked=settings.Sequential;
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new(12),ColumnCount=1,RowCount=10};
        layout.RowStyles.Add(new(SizeType.Absolute,50));layout.RowStyles.Add(new(SizeType.Absolute,32));layout.RowStyles.Add(new(SizeType.Absolute,20));layout.RowStyles.Add(new(SizeType.Absolute,32));layout.RowStyles.Add(new(SizeType.Absolute,38));layout.RowStyles.Add(new(SizeType.Percent,42));layout.RowStyles.Add(new(SizeType.Absolute,38));layout.RowStyles.Add(new(SizeType.Percent,30));layout.RowStyles.Add(new(SizeType.Absolute,26));layout.RowStyles.Add(new(SizeType.Percent,28));
        var header=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false};
        header.Controls.Add(new Label{Text="EDCBepgEx",Font=new(Font.FontFamily,16,FontStyle.Bold),AutoSize=true,ForeColor=Color.FromArgb(28,53,83)});
        header.Controls.Add(new Label{Text="BonDriverごとに、必要なチャンネルを必要な時刻に収集",AutoSize=true,ForeColor=Color.DimGray});layout.Controls.Add(header);
        var dest=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3};dest.ColumnStyles.Add(new(SizeType.Absolute,100));dest.ColumnStyles.Add(new(SizeType.Percent,100));dest.ColumnStyles.Add(new(SizeType.Absolute,90));dest.Controls.Add(new Label{Text="EPG保存先",AutoSize=true,Padding=new(0,6,0,0)});dest.Controls.Add(output);var browse=Ui.Button("参照…");browse.Click+=(_,_)=>BrowseOutput();dest.Controls.Add(browse);editing.Add(browse);layout.Controls.Add(dest);
        layout.Controls.Add(new Label{Text="EDCBのフォルダー（EpgTimer.exeがある場所）を選ぶと、EDCBのEpgDataフォルダーを自動で設定します。",AutoSize=true,ForeColor=Color.DimGray,Padding=new(100,0,0,0)});
        var options=Flow();options.Controls.Add(reload);options.Controls.Add(sequential);layout.Controls.Add(options);
        var controls=Flow();AddButton(controls,"Bon追加",AddProfile,true);AddButton(controls,"EDCBから追加",ImportFromEdcb,true).Width=110;AddButton(controls,"Bon設定",EditProfile,true);AddButton(controls,"削除",RemoveProfile);AddButton(controls,"設定保存",Save);layout.Controls.Add(controls);
        foreach(var col in new[]{("name","BonDriverファイル名",320),("count","取得ch",65),("parallel","同時数",65),("time","曜日・開始時刻",230)})profiles.Columns.Add(new DataGridViewTextBoxColumn{Name=col.Item1,HeaderText=col.Item2,Width=col.Item3});
        profiles.Columns.Add(new DataGridViewButtonColumn{Name="enabled",HeaderText="定期実行",Width=110});
        profiles.Columns.Add(new DataGridViewButtonColumn{Name="manual",HeaderText="手動取得",Width=100,UseColumnTextForButtonValue=false});
        foreach(DataGridViewColumn column in profiles.Columns)
        {
            column.SortMode=column.Name=="manual"?DataGridViewColumnSortMode.NotSortable:DataGridViewColumnSortMode.Programmatic;
            if(column.Name!="manual")column.HeaderCell.ToolTipText="クリックで昇順・降順を切り替え";
        }
        profiles.ColumnHeaderMouseClick+=(_,e)=>{var column=profiles.Columns[e.ColumnIndex];if(column.SortMode==DataGridViewColumnSortMode.NotSortable)return;var order=profiles.SortedColumn==column&&profiles.SortOrder==SortOrder.Ascending?ListSortDirection.Descending:ListSortDirection.Ascending;profiles.Sort(column,order);};
        profiles.SortCompare+=(_,e)=>{if(profiles.Rows[e.RowIndex1].Tag is Profile a&&profiles.Rows[e.RowIndex2].Tag is Profile b){e.SortResult=CompareProfiles(a,b,e.Column.Name);e.Handled=true;}};
        profiles.DefaultCellStyle.WrapMode=DataGridViewTriState.False;
        profiles.CellContentClick+=(_,e)=>{if(e.RowIndex<0)return;var p=(Profile)profiles.Rows[e.RowIndex].Tag!;if(profiles.Columns[e.ColumnIndex].Name=="manual"){if(runs.Contains(p.Id)){runs.Stop(p.Id);UpdateManualButtons();}else Start([p],false);return;}if(profiles.Columns[e.ColumnIndex].Name=="enabled"){p.Enabled=!p.Enabled;profiles.Rows[e.RowIndex].Cells["enabled"].Value=p.Enabled?"無効にする":"有効にする";Save();}};
        profiles.CellDoubleClick+=(_,e)=>{if(e.RowIndex>=0&&profiles.Columns[e.ColumnIndex].Name is not ("enabled" or "manual"))EditProfile();};layout.Controls.Add(profiles);
        profiles.MultiSelect=true;
        var runbar=Flow();AddButton(runbar,"選択Bon取得",()=>Start(InDisplayOrder(SelectedProfiles())),true);AddButton(runbar,"全Bon取得",()=>Start(InDisplayOrder(settings.Profiles)),true);stop.Click+=(_,_)=>{runs.StopAll();UpdateManualButtons();stop.Enabled=false;status.Text="停止処理中…";};runbar.Controls.Add(stop);runbar.Controls.Add(armed);runbar.Controls.Add(status);layout.Controls.Add(runbar);
        foreach(var col in new[]{("bon","BonDriver",260),("channel","チャンネル名",245),("state","状況",100),("seconds","経過時間",80),("done","蓄積サービス",115),("signal","Signal",80),("eit","EITパケット",110),("drop","Drop",80)})workers.Columns.Add(new DataGridViewTextBoxColumn{Name=col.Item1,HeaderText=col.Item2,Width=col.Item3});workers.DefaultCellStyle.WrapMode=DataGridViewTriState.False;layout.Controls.Add(workers);
        layout.Controls.Add(new Label{Text="ログ",AutoSize=true,ForeColor=Color.DimGray,Padding=new(0,5,0,0)});layout.Controls.Add(log);Controls.Add(layout);
        foreach(var col in new[]{("space","Space",80),("chIndex","ch",80),("onid","ONID",80),("tsid","TSID",80),("sids","対象SID",180),("bits","受信ワーカー",100),("packets","EPG対象パケット",120),("started","開始時刻",90),("updated","更新時刻",90),("driverPath","BonDriverパス",280),("chsetPath","ChSet4パス",280),("file","EPG保存ファイル",280),("error","エラー内容",320)})workers.Columns.Add(new DataGridViewTextBoxColumn{Name=col.Item1,HeaderText=col.Item2,Width=col.Item3,Visible=false});
        ListColumns.Configure(profiles,settings.Columns.GetValueOrDefault("profiles"));
        ListColumns.Configure(workers,settings.Columns.GetValueOrDefault("workers"));
        RefreshProfiles();
        armed.CheckedChanged+=(_,_)=>{if(armed.Checked){try{ReadSettings();foreach(var p in settings.Profiles.Where(p=>p.Enabled)){Collector.Validate(p);foreach(var entry in p.ScheduleEntries())entry.Validate();}Save();Log("時刻指定を有効にしました。PCが起動し、このアプリが開いている間に実行します。");}catch(Exception ex){armed.Checked=false;MessageBox.Show(ex.Message);}}else Log("時刻指定を無効にしました。");};
        timer.Tick+=(_,_)=>TickSchedule();timer.Start();
        FormClosing+=(_,e)=>{if(running){e.Cancel=true;MessageBox.Show("停止ボタンで取得を止めてから閉じてください。");}else if(armed.Checked&&MessageBox.Show("終了すると時刻指定の取得も停止します。終了しますか？","終了",MessageBoxButtons.YesNo)==DialogResult.No)e.Cancel=true;};
        FormClosed+=(_,_)=>{settings.Columns["profiles"]=ListColumns.Capture(profiles);settings.Columns["workers"]=ListColumns.Capture(workers);try{settings.Save(configPath);}catch{}timer.Dispose();runs.Dispose();};
        Log("待機中");
    }
    static DataGridView Grid()=>new(){Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,AllowUserToResizeRows=false,AllowUserToResizeColumns=true,AllowUserToOrderColumns=true,AutoGenerateColumns=false,RowHeadersVisible=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,BackgroundColor=Color.FromArgb(249,250,252),BorderStyle=BorderStyle.FixedSingle,AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.None,RowTemplate={Height=24},CellBorderStyle=DataGridViewCellBorderStyle.None,ColumnHeadersHeightSizeMode=DataGridViewColumnHeadersHeightSizeMode.DisableResizing,ColumnHeadersHeight=26,EnableHeadersVisualStyles=false,ColumnHeadersDefaultCellStyle=new(){BackColor=SystemColors.Control,ForeColor=SystemColors.ControlText},DefaultCellStyle=new(){SelectionBackColor=Color.FromArgb(215,231,248),SelectionForeColor=Color.Black,Padding=new(3,0,3,0)}};
    static FlowLayoutPanel Flow()=>new(){Dock=DockStyle.Fill,WrapContents=false,Padding=new(0,3,0,0)};
    Button AddButton(FlowLayoutPanel panel,string text,Action action,bool allowDuringRun=false){var b=Ui.Button(text);b.Click+=(_,_)=>{try{action();}catch(Exception ex){MessageBox.Show(ex.Message,"確認");}};panel.Controls.Add(b);if(!allowDuringRun)editing.Add(b);return b;}
    // Edit/remove act on the focused row; runs use every selected row.
    Profile? Selected()=>profiles.CurrentRow is {Selected:true} row?row.Tag as Profile:profiles.SelectedRows.Count==0?null:profiles.SelectedRows[0].Tag as Profile;
    List<Profile> SelectedProfiles()=>profiles.SelectedRows.Cast<DataGridViewRow>().Select(r=>r.Tag).OfType<Profile>().ToList();
    // Sequential runs follow the list as currently shown (including any column sort).
    List<Profile> InDisplayOrder(IEnumerable<Profile> list){var order=profiles.Rows.Cast<DataGridViewRow>().Where(r=>r.Tag is Profile).ToDictionary(r=>((Profile)r.Tag!).Id,r=>r.Index);return list.OrderBy(p=>order.TryGetValue(p.Id,out var i)?i:int.MaxValue).ToList();}
    void RefreshProfiles(){var sorted=profiles.SortedColumn;var order=profiles.SortOrder;profiles.Rows.Clear();foreach(var p in settings.Profiles){int i=profiles.Rows.Add(p.DisplayName,p.Selected.Count,p.Parallel,p.ScheduleText(),p.Enabled?"無効にする":"有効にする",runs.ButtonText(p.Id));profiles.Rows[i].Tag=p;profiles.Rows[i].Cells[0].ToolTipText=p.Driver;}if(sorted!=null)profiles.Sort(sorted,order==SortOrder.Descending?ListSortDirection.Descending:ListSortDirection.Ascending);}
    internal static int CompareProfiles(Profile a,Profile b,string column)
    {
        static int ScheduleKey(Profile p)=>p.ScheduleEntries().SelectMany(s=>Enumerable.Range(0,7).Where(d=>(s.Days&(1<<d))!=0).Select(d=>d*1440+s.Hour*60+s.Minute)).DefaultIfEmpty(int.MaxValue).Min();
        return column switch
        {
            "count"=>a.Selected.Count.CompareTo(b.Selected.Count),
            "parallel"=>a.Parallel.CompareTo(b.Parallel),
            "time"=>ScheduleKey(a).CompareTo(ScheduleKey(b)),
            "enabled"=>a.Enabled.CompareTo(b.Enabled),
            _=>StringComparer.CurrentCultureIgnoreCase.Compare(a.DisplayName,b.DisplayName)
        };
    }
    void BrowseOutput()
    {
        using var d=new FolderBrowserDialog{Description="EDCBのフォルダー、またはEPGの保存先フォルダーを選んでください。",SelectedPath=Directory.Exists(output.Text)?output.Text:Edcb.IsEdcbFolder(settings.EdcbFolder)?settings.EdcbFolder:app};
        if(d.ShowDialog()!=DialogResult.OK)return;
        if(!Edcb.IsEdcbFolder(d.SelectedPath)){output.Text=d.SelectedPath;return;}
        settings.EdcbFolder=d.SelectedPath;output.Text=Edcb.EpgDataPath(d.SelectedPath);
        Log($"EDCBのフォルダーからEPG保存先を設定しました: {output.Text}");
        if(!Directory.Exists(output.Text))MessageBox.Show($"EpgDataフォルダーがまだありません。\n{output.Text}\nEDCBで一度EPGを取得するか、フォルダーを作成してください。","EPG保存先");
    }
    void ImportFromEdcb()
    {
        string edcb=settings.EdcbFolder;
        if(!Edcb.IsEdcbFolder(edcb))
        {
            using var d=new FolderBrowserDialog{Description="EDCBのフォルダー（EpgTimer.exeがある場所）を選んでください。"};
            if(d.ShowDialog()!=DialogResult.OK)return;
            if(!Edcb.IsEdcbFolder(d.SelectedPath)){MessageBox.Show("EDCBのフォルダーではありません。EpgTimer.exeやEpgTimerSrv.exeがあるフォルダーを選んでください。","EDCBから追加");return;}
            edcb=settings.EdcbFolder=d.SelectedPath;
        }
        var found=Edcb.Import(edcb,settings.Profiles);
        if(found.Count==0){MessageBox.Show($"追加できるBonDriverがありませんでした。\n{Edcb.BonDriverPath(edcb)} のBonDriverのうち、ChSet4があり未登録のものを追加します。","EDCBから追加");return;}
        string list=string.Join("\n",found.Take(20).Select(p=>p.DisplayName))+(found.Count>20?$"\n…ほか{found.Count-20}件":"");
        if(MessageBox.Show($"次の{found.Count}件を追加します。全チャンネルを選択し、定期実行は無効の状態で追加します。\n\n{list}","EDCBから追加",MessageBoxButtons.OKCancel)!=DialogResult.OK)return;
        settings.Profiles.AddRange(found);RefreshProfiles();Save();Log($"EDCBから{found.Count}件のBonDriverを追加しました。");
    }
    // Adding is safe during a run: running jobs hold their own Profile and channel plan.
    void AddProfile(){using var d=new ProfileDialog(new Profile());if(d.ShowDialog()==DialogResult.OK){settings.Profiles.Add(d.Value);RefreshProfiles();Save();}}
    void EditProfile(){if(Selected() is not {} p)return;using var d=new ProfileDialog(p,runs.Contains(p.Id));if(d.ShowDialog()==DialogResult.OK){settings.Profiles[settings.Profiles.IndexOf(p)]=d.Value;RefreshProfiles();Save();}}
    void RemoveProfile(){if(Selected() is not {} p)return;if(MessageBox.Show($"「{p.DisplayName}」を取得設定から削除しますか？","設定削除",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;settings.Profiles.Remove(p);RefreshProfiles();Save();}
    void ReadSettings(){settings.Columns["profiles"]=ListColumns.Capture(profiles);settings.Columns["workers"]=ListColumns.Capture(workers);settings.Output=output.Text.Trim();settings.Reload=reload.Checked;settings.Sequential=sequential.Checked;}
    void Save(){ReadSettings();settings.Save(configPath);Log("本ソフトの設定を保存しました。");}
    void Log(string text)
    {
        if(IsDisposed)return;if(InvokeRequired){BeginInvoke(()=>Log(text));return;}
        string line=$"[{DateTime.Now:HH:mm:ss}] {text}";log.AppendText(line+Environment.NewLine);
        try{Directory.CreateDirectory(Path.Combine(app,"logs"));File.AppendAllText(Path.Combine(app,"logs",$"{DateTime.Now:yyyyMMdd}.log"),line+Environment.NewLine);}catch{}
    }
    void OnProgress(ProgressInfo p)
    {
        if(IsDisposed)return;if(InvokeRequired){BeginInvoke(()=>OnProgress(p));return;}
        string key=$"{p.Profile}/{p.Space}:{p.ChannelIndex}:{p.Onid}:{p.Tsid}";var row=workers.Rows.Cast<DataGridViewRow>().FirstOrDefault(r=>Equals(r.Tag,key));
        if(row==null){row=workers.Rows[workers.Rows.Add(string.IsNullOrEmpty(p.DriverPath)?settings.Profiles.FirstOrDefault(x=>x.Id==p.Profile)?.DisplayName??p.Profile:Path.GetFileName(p.DriverPath),FormatChannelName(p.NetworkName,p.Channel),p.State)];row.Tag=key;row.Cells["started"].Value=DateTime.Now.ToString("HH:mm:ss");}
        row.Cells[2].Value=p.State;
        if(p.State=="取得中"&&p.Total==0){row.Cells["started"].Value=DateTime.Now.ToString("HH:mm:ss");row.Cells["error"].Value="";row.Cells["seconds"].Value="00:00";}
        row.Cells["space"].Value=p.Space;row.Cells["chIndex"].Value=p.ChannelIndex;row.Cells["onid"].Value=p.Onid;row.Cells["tsid"].Value=p.Tsid;row.Cells["sids"].Value=p.Sids;
        if(p.Bits>0)row.Cells["bits"].Value=$"{p.Bits}bit";
        row.Cells["driverPath"].Value=p.DriverPath;row.Cells["chsetPath"].Value=p.ChSetPath;row.Cells["updated"].Value=DateTime.Now.ToString("HH:mm:ss");
        if(p.Onid!=0||p.Tsid!=0)row.Cells["file"].Value=Path.Combine(settings.Output,$"{p.Onid:X4}{(p.BasicOnly?0xFFFF:p.Tsid):X4}_epg.dat");
        if(!string.IsNullOrEmpty(p.Message))row.Cells["error"].Value=p.Message;
        if(p.Total>0){row.Cells[3].Value=FormatElapsed(p.Seconds);row.Cells[4].Value=$"{p.Done} / {p.Total}";row.Cells[5].Value=p.Signal.ToString("F2");row.Cells[6].Value=p.Eit;row.Cells["drop"].Value=p.Drop;}
        if(p.Packets>0)row.Cells["packets"].Value=p.Packets;
    }
    internal static string FormatElapsed(int seconds)=>$"{Math.Max(0,seconds)/60:00}:{Math.Max(0,seconds)%60:00}";
    internal static string FormatChannelName(string name,string physical)=>string.IsNullOrWhiteSpace(name)?physical:string.IsNullOrWhiteSpace(physical)?name:$"{name}({physical})";
    void UpdateManualButtons(){foreach(DataGridViewRow row in profiles.Rows)if(row.Tag is Profile p)row.Cells["manual"].Value=runs.ButtonText(p.Id);}
    // The row's own start button runs at once even in sequential mode; it neither waits for nor delays the queue.
    void Start(List<Profile> list,bool queue=true)
    {
        list=list.Where(p=>!runs.Contains(p.Id)).DistinctBy(p=>p.Id).ToList();
        if(list.Count==0){Log("選択したBonDriverは取得中、または未選択です。");return;}
        if(runs.StoppingAll){Log("停止処理が終わってから取得してください。");return;}
        try
        {
            ReadSettings();var prepared=Collector.Prepare(list);
            if(!Directory.Exists(settings.Output))throw new InvalidDataException("存在するEPG保存先フォルダーを指定してください。");
            settings.Save(configPath);
            if(!running)workers.Rows.Clear();
            var jobs=list.Select(p=>(Profile:p,Token:runs.Start(p.Id))).ToList();
            running=true;SetBusy(true);UpdateManualButtons();status.Text=$"取得中 {runs.Count} Bon";
            foreach(var job in jobs)RunOne(job.Profile,job.Token,prepared,queue&&settings.Sequential);
        }
        catch(Exception ex){Log(ex.Message);if(!armed.Checked)MessageBox.Show(ex.Message,"取得エラー");}
    }
    async void RunOne(Profile p,CancellationToken token,IReadOnlyDictionary<string,List<Channel>> prepared,bool queue)
    {
        Task previous=Task.CompletedTask;var turn=new TaskCompletionSource<bool>();
        if(queue){previous=sequentialTail;sequentialTail=turn.Task;}
        try
        {
            if(!previous.IsCompleted)
            {
                Log($"{p.DisplayName}: 前のBonの取得終了を待っています。");
                await Task.WhenAny(previous,Task.Delay(Timeout.Infinite,token));
                token.ThrowIfCancellationRequested();
            }
            var result=await new Collector(app,Log,OnProgress).Run(settings,[p],token,prepared);
            status.Text=$"保存 {result.Saved} / 失敗 {result.Failed}";Log($"{p.DisplayName}: 取得終了: 保存 {result.Saved}件 / 失敗 {result.Failed}件");
        }
        catch(OperationCanceledException){status.Text="中止";Log($"{p.DisplayName}: 取得を中止しました。完了前のデータは破棄しました。");}
        catch(Exception ex){status.Text="エラー";Log(ex.Message);if(!armed.Checked)MessageBox.Show(ex.Message,"取得エラー");}
        finally
        {
            // A Bon stopped while waiting must not let later Bons overtake the one still running.
            _=previous.ContinueWith(_=>turn.TrySetResult(true),TaskScheduler.Default);
            runs.Finish(p.Id);running=runs.Count>0;
            if(running)status.Text=$"取得中 {runs.Count} Bon";
            SetBusy(running);UpdateManualButtons();
        }
    }
    void SetBusy(bool busy){foreach(var b in editing)b.Enabled=!busy;output.Enabled=!busy;reload.Enabled=!busy;sequential.Enabled=!busy;stop.Enabled=busy&&!runs.StoppingAll;}
    void TickSchedule()
    {
        if(!armed.Checked)return;
        try
        {
            var due=InDisplayOrder(settings.Profiles.Where(p=>schedule.Due(p,DateTime.Now)));
            if(due.Count==0)return;
            Start(due);
        }
        catch(Exception ex){armed.Checked=false;Log("時刻指定エラー: "+ex.Message);}
    }
}
