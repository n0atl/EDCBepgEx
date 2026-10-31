using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace EpgCollector;

internal static class SelfTests
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
    public static int Fault()
    {
        string root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));
        string tmp=Path.Combine(root,"test-work","fault-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(tmp);
        var lines=new List<string>();
        try
        {
            string samples=Path.Combine(root,"..","EDCB");
            var targets=Channels.Read(Path.Combine(samples,"BonDriver_Proxy_PX-MLT(BonDriverProxy).ChSet4.txt")).Where(c=>File.Exists(Path.Combine(samples,"EpgData",c.FileName))).Take(2).ToList();
            var first=targets[0];string chset=Path.Combine(tmp,"test.ChSet4.txt");
            Compat.RegisterCodePages();
            File.WriteAllLines(chset,first.Services.Select(s=>$"{first.Name}\t{s.Name}\tReplay\t0\t0\t{first.Onid}\t{first.Tsid}\t{s.Sid}\t{s.Type}\t{(s.Partial?1:0)}\t1\t0"),System.Text.Encoding.GetEncoding(932));
            string driver=Path.Combine(tmp,"MockBonDriver.dll");File.Copy(Path.Combine(AppContext.BaseDirectory,"native","x86","MockBonDriver.dll"),driver);
            File.WriteAllLines(Path.ChangeExtension(driver,".ini"),["[Replay]",$"0={Path.Combine(samples,"EpgData",first.FileName)}","SwitchAfter=20",$"SwitchFile={Path.Combine(samples,"EpgData",targets[1].FileName)}"]);
            string dest=Path.Combine(tmp,first.FileName);File.Copy(Path.Combine(samples,"EpgData",first.FileName),dest);string? hash=Publisher.Fingerprint(dest);
            var p=new Profile{Name="Stream switch test",Driver=driver,ChSet=chset,Parallel=1,MinimumSeconds=60,Selected=Channels.Read(chset).Select(c=>c.Key).ToList()};
            var result=new Collector(AppContext.BaseDirectory,lines.Add,_=>{}).Run(new(){Output=tmp,Reload=false},[p],CancellationToken.None).GetAwaiter().GetResult();
            if(result.Saved!=0||result.Failed!=1||Publisher.Fingerprint(dest)!=hash||!lines.Any(s=>s.Contains("Stream changed")))throw new Exception("Stream switch protection failed");
            lines.Add("PASS Unexpected ONID/TSID change stops collection and preserves existing dat");
            File.WriteAllLines(Path.Combine(root,"fault-results.txt"),lines);return 0;
        }
        catch(Exception ex){lines.Add("FAIL "+ex);File.WriteAllLines(Path.Combine(root,"fault-results.txt"),lines);return 1;}
    }
    public static int Integration(bool quick=false)
    {
        string root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));
        string tmp=Path.Combine(root,"test-work","integration-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(tmp);
        var lines=new System.Collections.Concurrent.ConcurrentQueue<string>();
        string resultPath=Path.Combine(root,"integration-results.txt");
        try
        {
            string samples=Path.Combine(root,"..","EDCB");
            var channels=Channels.Read(Path.Combine(samples,"BonDriver_Proxy_PX-MLT(BonDriverProxy).ChSet4.txt"));
            var selected=channels.Where(c=>File.Exists(Path.Combine(samples,"EpgData",c.FileName))).Take(2).ToList();
            // Map real service identities to fixture channel indices, without changing sample ChSet4.
            string chset=Path.Combine(tmp,"replay.ChSet4.txt");
            Compat.RegisterCodePages();
            var rows=selected.SelectMany((c,index)=>c.Services.Select(s=>$"{c.Name}\t{s.Name}\tReplay\t0\t{index}\t{c.Onid}\t{c.Tsid}\t{s.Sid}\t{s.Type}\t{(s.Partial?1:0)}\t1\t0"));
            File.WriteAllLines(chset,rows,System.Text.Encoding.GetEncoding(932));
            var mapped=Channels.Read(chset);
            string fixture=Path.Combine(tmp,"受信 A.dll");File.Copy(Path.Combine(AppContext.BaseDirectory,"native","x86","MockBonDriver.dll"),fixture);
            File.WriteAllLines(Path.ChangeExtension(fixture,".ini"),new[]{"[Replay]"}.Concat(selected.Select((c,i)=>$"{i}={Path.Combine(samples,"EpgData",c.FileName)}")));
            var p=new Profile{Name="Offline replay",Enabled=false,Driver=fixture,ChSet=chset,Parallel=2,MinimumSeconds=quick?0:60,TimeoutSeconds=90,Selected=mapped.Select(c=>c.Key).ToList()};
            var settings=new Settings{Output=tmp,Reload=false,Profiles=[p]};
            var active=new System.Collections.Concurrent.ConcurrentDictionary<string,bool>();int max=0;
            var collect=new Collector(AppContext.BaseDirectory,s=>lines.Enqueue(s),v=>{if(v.State=="取得中")active[v.Channel]=true;else active.TryRemove(v.Channel,out _);int count=active.Count;int old;do{old=max;}while(count>old&&Interlocked.CompareExchange(ref max,count,old)!=old);});
            var watch=Stopwatch.StartNew();var result=collect.Run(settings,[p],CancellationToken.None).GetAwaiter().GetResult();
            if(result.Saved!=2||result.Failed!=0||max!=2||(!quick&&watch.Elapsed.TotalSeconds<60))throw new Exception($"Parallel capture failure: {result}, peak={max}, seconds={watch.Elapsed.TotalSeconds}");
            lines.Enqueue($"PASS Schedule-disabled profile can capture manually with two workers and minimum {p.MinimumSeconds} seconds");
            var before=Directory.GetFiles(tmp,"*_epg.dat").ToDictionary(x=>x,Publisher.Fingerprint);
            using var cancel=new CancellationTokenSource(1000);
            p.MinimumSeconds=60;
            bool cancelled=false;try{collect.Run(settings,[p],cancel.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}
            if(!cancelled || before.Any(pair=>Publisher.Fingerprint(pair.Key)!=pair.Value))throw new Exception("Cancellation changed saved EPG");
            lines.Enqueue("PASS Cancellation stopped worker processes and preserved published dat");
            // A and B each have two workers. B deliberately uses x64 to test mixed architecture.
            var more=channels.Where(c=>File.Exists(Path.Combine(samples,"EpgData",c.FileName))).Skip(2).Take(2).ToList();
            string chsetB=Path.Combine(tmp,"replayB.ChSet4.txt");
            File.WriteAllLines(chsetB,more.SelectMany((c,index)=>c.Services.Select(s=>$"{c.Name}\t{s.Name}\tReplay\t0\t{index}\t{c.Onid}\t{c.Tsid}\t{s.Sid}\t{s.Type}\t{(s.Partial?1:0)}\t1\t0")),System.Text.Encoding.GetEncoding(932));
            string fixtureB=Path.Combine(tmp,"受信 B.dll");File.Copy(Path.Combine(AppContext.BaseDirectory,"native","x64","MockBonDriver.dll"),fixtureB);
            File.WriteAllLines(Path.ChangeExtension(fixtureB,".ini"),new[]{"[Replay]"}.Concat(more.Select((c,i)=>$"{i}={Path.Combine(samples,"EpgData",c.FileName)}")));
            var b=new Profile{Name="Offline replay B",Driver=fixtureB,ChSet=chsetB,Parallel=2,MinimumSeconds=60,TimeoutSeconds=90,Selected=Channels.Read(chsetB).Select(c=>c.Key).ToList()};
            if(quick){p.MinimumSeconds=0;b.MinimumSeconds=0;}
            active.Clear();max=0;
            var firstRun=collect.Run(settings,[p],CancellationToken.None);
            Thread.Sleep(300);
            var secondRun=collect.Run(settings,[b],CancellationToken.None);
            var mixed=Task.WhenAll(firstRun,secondRun).GetAwaiter().GetResult();
            if(mixed.Sum(r=>r.Saved)!=4||mixed.Sum(r=>r.Failed)!=0||max!=4)throw new Exception($"Four-worker capture failure: saved={mixed.Sum(r=>r.Saved)}, peak={max}");
            lines.Enqueue("PASS Starting B while A is already capturing runs both profiles, each with two workers (x86 + x64)");
            p.MinimumSeconds=0;b.MinimumSeconds=0;watch.Restart();
            var immediate=collect.Run(settings,[p,b],CancellationToken.None).GetAwaiter().GetResult();
            if(immediate.Saved!=4||immediate.Failed!=0||watch.Elapsed.TotalSeconds>=30)throw new Exception("Zero-minimum capture did not finish promptly after EPG completion");
            lines.Enqueue("PASS Zero minimum completes as soon as EPG is ready, with no fixed 60-second wait");
            using(var runs=new BonRuns())
            {
                p.MinimumSeconds=60;b.MinimumSeconds=0;
                var unchanged=before.Keys.ToDictionary(x=>x,Publisher.Fingerprint);
                var stopA=collect.Run(settings,[p],runs.Start(p.Id));
                var continueB=collect.Run(settings,[b],runs.Start(b.Id));
                Thread.Sleep(1000);runs.Stop(p.Id);
                bool stopped=false;try{stopA.GetAwaiter().GetResult();}catch(OperationCanceledException){stopped=true;}
                runs.Finish(p.Id);var kept=continueB.GetAwaiter().GetResult();runs.Finish(b.Id);
                if(!stopped||kept.Saved!=2||kept.Failed!=0||unchanged.Any(x=>Publisher.Fingerprint(x.Key)!=x.Value))throw new Exception("Per-Bon cancellation interrupted another Bon or changed cancelled output");
                lines.Enqueue("PASS Stopping A cancels both A workers and preserves its dat while both B workers complete");
            }
            lines.Enqueue("All integration tests passed. Mock driver only; no real tuner opened.");File.WriteAllLines(resultPath,lines);return 0;
        }
        catch(Exception ex){lines.Enqueue("FAIL "+ex);File.WriteAllLines(resultPath,lines);return 1;}
    }
    public static int Run()
    {
        string root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));
        string results=Path.Combine(root,"test-results.txt");
        var lines=new List<string>();
        void Check(bool value,string description){if(!value)throw new Exception(description);lines.Add("PASS "+description);}
        string tmp=Path.Combine(root,"test-work",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(tmp);
        try
        {
            string samples=Path.Combine(root,"..","EDCB");
            string chset=Path.Combine(samples,"BonDriver_Proxy_PX-MLT(BonDriverProxy).ChSet4.txt");
#if NETFRAMEWORK
            Check(Environment.Version.Major==4,"Runs on Windows .NET Framework CLR 4: "+typeof(object).Assembly.Location);
#endif
            var channels=Channels.Read(chset);
            Check(channels.Count==13,"Provided ChSet4 groups into 13 tuning targets");
            Check(channels.Sum(c=>c.Services.Count)==47,"All 47 services retained");
            Check(channels.Single(c=>c.Name=="19ch").NetworkName=="ＮＣＣ長崎文化放送","Channel network name comes from third ChSet4 column");
            Check(new Profile().Parallel==1,"Default concurrency is one tuner");
            var bs1=new Channel(0,0,4,100,"BS1",[new("A",1,1,false)]);var bs2=new Channel(0,1,4,200,"BS2",[new("B",1,1,false)]);
            var basic=Collector.Plan(new Profile(),[bs1,bs2]);
            Check(basic.Count==1&&basic[0].FileName=="0004FFFF_epg.dat"&&basic[0].CaptureTargets.Count==2,"BS basic acquisition groups network and preserves TSID/SID identity");
            Check(Collector.Plan(new Profile{BSBasicOnly=false},[bs1,bs2]).Count==2,"Detailed BS acquisition visits each selected TS");
            Check(bs1.Category=="BS"&&new Channel(0,0,10,1,"",[]).Category=="スカパー！"&&channels[0].Category=="地デジ","Network categories match EDCB ONID rules");
            using(var checkForm=new Form())
            {
                var checks=new ChannelChecks{Dock=DockStyle.Fill,CheckOnClick=true};checks.Items.Add("Channel name");checkForm.Controls.Add(checks);checkForm.Show();Application.DoEvents();
                int y=checks.GetItemRectangle(0).Height/2;
                void Click(int x){var point=(IntPtr)((y<<16)|x);SendMessage(checks.Handle,0x201,(IntPtr)1,point);SendMessage(checks.Handle,0x202,IntPtr.Zero,point);Application.DoEvents();}
                Click(80);Click(80);Check(!checks.GetItemChecked(0),"Clicking channel text never toggles the check");
                Click(5);Check(checks.GetItemChecked(0),"Clicking the checkbox toggles channel selection");checkForm.Close();
            }
            Check(MainForm.CompareProfiles(new(){Parallel=2},new(){Parallel=10},"parallel")<0,"Concurrency column sorts numerically, not as text");
            Check(MainForm.CompareProfiles(new(){Enabled=false},new(){Enabled=true},"enabled")<0,"Schedule state column supports sorting");
            Check(MainForm.CompareProfiles(new(){Times="04:00"},new(){Times="12:00"},"time")<0,"Schedule column sorts by weekday and time");
            using(var columns=new DataGridView())
            {
                columns.Columns.Add("a","A");columns.Columns.Add("b","B");columns.Columns.Add("c","C");
                columns.Columns["a"]!.Width=210;columns.Columns["b"]!.Visible=false;columns.Columns["c"]!.DisplayIndex=0;
                var saved=ListColumns.Capture(columns);columns.Columns["a"]!.Width=90;columns.Columns["b"]!.Visible=true;columns.Columns["c"]!.DisplayIndex=2;ListColumns.Restore(columns,saved);
                Check(columns.Columns["a"]!.Width==210&&!columns.Columns["b"]!.Visible&&columns.Columns["c"]!.DisplayIndex==0,"Column widths, visibility and order restore correctly");
            }
            Check(channels.Any(c=>c.Services.Any(s=>s.Name.Contains("長崎"))),"CP932 station names decoded");
            Check(channels.Any(c=>c.Services.Count>1&&c.EpgServices.Count<c.Services.Count),"Partial/data services excluded from completion checks");
            Check(Channels.Bitness(Path.Combine(samples,"BonDriver_Proxy_PX-MLT.dll"))==32,"Provided BonDriver is x86");
            var p=new Profile{Times="04:00,12:30"};var clock=new ScheduleClock();var now=new DateTime(2026,10,29,4,0,0);
            Check(clock.Due(p,now)&&!clock.Due(p,now.AddSeconds(1)),"Schedule fires once per profile per minute");
            Check(clock.Due(p,now.AddDays(1)),"Schedule fires on following day");
            p.Enabled=false;Check(!clock.Due(p,now.AddDays(2)),"Disabled profile never scheduled");
            var weekly=new Profile{Schedule=[new(){Days=(1<<(int)DayOfWeek.Monday)|(1<<(int)DayOfWeek.Friday),Hour=4,Minute=30}]};
            var weeklyClock=new ScheduleClock();
            Check(!weeklyClock.Due(weekly,new DateTime(2026,10,29,4,30,0))&&weeklyClock.Due(weekly,new DateTime(2026,10,30,4,30,0)),"Weekday masks exclude Thursday and include Friday");
            weekly.Schedule.Add(new(){Days=127,Hour=12,Minute=15});
            Check(weeklyClock.Due(weekly,new DateTime(2026,10,29,12,15,0)),"Multiple weekday/time entries supported");
            Check(new Profile{Times="04:00,12:30"}.ScheduleEntries().Count==2,"Legacy daily times migrate without losing entries");
            Check(new Profile{Driver=@"C:\Drivers\custom.dll"}.DisplayName=="custom.dll","Full DLL filename displayed without requiring BonDriver prefix");
            Check(MainForm.FormatElapsed(90)=="01:30"&&MainForm.FormatElapsed(3600)=="60:00","Elapsed display uses minutes and seconds");
            Check(MainForm.FormatChannelName("ＮＣＣ長崎文化放送","19ch")=="ＮＣＣ長崎文化放送(19ch)","Channel display uses name(physical)");
            {
                string edcb=Path.Combine(tmp,"EDCB");Directory.CreateDirectory(Path.Combine(edcb,"BonDriver"));Directory.CreateDirectory(Path.Combine(edcb,"Setting"));
                Check(!Edcb.IsEdcbFolder(edcb),"Folder without EDCB files is not treated as EDCB");
                File.WriteAllText(Path.Combine(edcb,"EpgTimerSrv.exe"),"");
                Check(Edcb.IsEdcbFolder(edcb)&&Edcb.EpgDataPath(edcb)==Path.Combine(edcb,"Setting","EpgData"),"EDCB folder resolves to Setting\\EpgData by default");
                string driver=Path.Combine(edcb,"BonDriver","BonDriver_Proxy_PX-MLT.dll");File.Copy(Path.Combine(samples,"BonDriver_Proxy_PX-MLT.dll"),driver);
                File.Copy(chset,Path.Combine(edcb,"Setting","BonDriver_Proxy_PX-MLTX(Other).ChSet4.txt"));
                Check(Edcb.FindChSet(driver)==null,"ChSet4 of a DLL with a longer name is not picked");
                string own=Path.Combine(edcb,"Setting","BonDriver_Proxy_PX-MLT(BonDriverProxy).ChSet4.txt");File.Copy(chset,own);
                Check(Edcb.FolderOfDriver(driver)==edcb&&Edcb.FindChSet(driver)==own,"EDCB BonDriver finds its ChSet4 in the Setting folder");
                var imported=Edcb.Import(edcb,[]);
                Check(imported.Count==1&&imported[0].ChSet==own&&!imported[0].Enabled&&imported[0].Selected.Count==channels.Count(c=>c.EpgServices.Count>0),"EDCB import adds BonDriver with ChSet4, all EPG channels and schedule off");
                Check(Edcb.Import(edcb,imported).Count==0,"EDCB import skips registered BonDriver");
                string moved=Path.Combine(tmp,"MovedSetting");File.WriteAllText(Path.Combine(edcb,"Common.ini"),$"[SET]\r\nDataSavePath={moved}\r\n",System.Text.Encoding.Unicode);
                Check(Edcb.EpgDataPath(edcb)==Path.Combine(moved,"EpgData"),"Common.ini DataSavePath relocates the Setting folder");
            }
            using(var runs=new BonRuns())
            {
                var a=runs.Start("a");var b=runs.Start("b");runs.Stop("a");
                Check(a.IsCancellationRequested&&!b.IsCancellationRequested&&runs.ButtonText("a")=="停止中…"&&runs.ButtonText("b")=="停止","Per-Bon stop leaves other Bon running");
                runs.Finish("a");Check(runs.ButtonText("a")=="取得開始"&&runs.Count==1,"Completed Bon can restart while another runs");
                runs.StopAll();Check(b.IsCancellationRequested&&runs.StoppingAll,"Global stop cancels remaining Bon");runs.Finish("b");Check(!runs.StoppingAll&&runs.Count==0,"Stop state resets after completion");
            }
            using(var form=new Form())
            {
                var editor=new ScheduleEditor([]);form.Controls.Add(editor);form.Show();Application.DoEvents();
                Check(((ComboBox)editor.Controls.Find("scheduleHour",true).Single()).SelectedIndex==4&&((ComboBox)editor.Controls.Find("scheduleMinute",true).Single()).SelectedIndex==0,"Weekday editor initializes valid hour/minute selections");form.Close();
            }
            string dest=Path.Combine(tmp,"sample_epg.dat"),stage=Path.Combine(tmp,"staging.ts");
            File.WriteAllText(dest,"old");File.WriteAllText(stage,"new");string baseline=Publisher.Fingerprint(dest)!;
            File.WriteAllText(dest,"EDCB changed");bool rejected=false;try{Publisher.Commit(stage,dest,baseline,preserveHistory:false);}catch(IOException){rejected=true;}
            Check(rejected&&File.ReadAllText(dest)=="EDCB changed","Concurrent EDCB change preserved");
            baseline=Publisher.Fingerprint(dest)!;File.WriteAllText(dest+".tmp","");rejected=false;
            try{Publisher.Commit(stage,dest,baseline,preserveHistory:false);}catch(IOException){rejected=true;}File.Delete(dest+".tmp");
            Check(rejected&&File.ReadAllText(dest)=="EDCB changed","EDCB active capture prevents publishing");
            Publisher.Commit(stage,dest,baseline,preserveHistory:false);
            Check(File.ReadAllText(dest)=="new"&&File.ReadAllText(Path.Combine(tmp,"CollectorBackup","sample_epg.dat"))=="EDCB changed","Atomic publish retains previous data backup");
            string fresh=Path.Combine(tmp,"new_epg.dat");Publisher.Commit(stage,fresh,null,preserveHistory:false);Check(File.ReadAllText(fresh)=="new","New TS file can be published");
            File.WriteAllText(fresh,"another writer");rejected=false;try{Publisher.Commit(stage,fresh,null,preserveHistory:false);}catch(IOException){rejected=true;}
            Check(rejected&&File.ReadAllText(fresh)=="another writer","New target race does not overwrite other writer");
            EpgHistoryTests.Run(samples,tmp,Check);
            var settings=new Settings{Output=tmp,Profiles=[new(){Name="日本語",Selected=[channels[0].Key]}]};string config=Path.Combine(tmp,"config.json");settings.Save(config);Check(Settings.Load(config).Profiles[0].Name=="日本語","Configuration round trip");
            settings.Profiles[0].Schedule=[new(){Days=5,Hour=23,Minute=45}];settings.Profiles[0].Enabled=false;settings.Profiles[0].BSBasicOnly=false;
            settings.Columns["workers"]=[new("space",90,3,false)];settings.Save(config);var loaded=Settings.Load(config);
            Check(!loaded.Profiles[0].Enabled&&!loaded.Profiles[0].BSBasicOnly&&loaded.Profiles[0].Schedule![0].Days==5&&loaded.Profiles[0].Schedule![0].Hour==23&&loaded.Columns["workers"][0]==settings.Columns["workers"][0],"Atomic settings replacement preserves schedule and record-based column layout");
            string existing=Path.Combine(root,"dist","config.json");
            if(File.Exists(existing))
            {
                var imported=Settings.Load(existing);string importedPath=Path.Combine(tmp,"imported-config.json");imported.Save(importedPath);
                Check(JsonSerializer.Serialize(imported,Settings.Json)==JsonSerializer.Serialize(Settings.Load(importedPath),Settings.Json),"Existing application configuration imports and round-trips without changes");
            }
            PipeTest().GetAwaiter().GetResult();lines.Add("PASS EDCB reload command 2 uses correct header and validates response");
            foreach(var arch in new[]{"x86","x64"})
            foreach(var dat in Directory.GetFiles(Path.Combine(samples,"EpgData"),"*_epg.dat"))
            {
                var pi=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"native",arch,"EpgWorker.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};Compat.AddArgument(pi,"--inspect");Compat.AddArgument(pi,dat);
                using var proc=Process.Start(pi)!;string stdout=proc.StandardOutput.ReadToEnd();string err=proc.StandardError.ReadToEnd();proc.WaitForExit();
                Check(proc.ExitCode==0,$"{arch} parser reads {Path.GetFileName(dat)} ({err})");
                using var doc=JsonDocument.Parse(stdout);Check(doc.RootElement.GetProperty("events").GetInt64()>0,$"{arch} {Path.GetFileName(dat)} contains EPG events");
            }
            lines.Add("All tests passed. No tuner opened; provided files unchanged.");File.WriteAllLines(results,lines);return 0;
        }
        catch(Exception ex){lines.Add("FAIL "+ex);File.WriteAllLines(results,lines);return 1;}
    }
    static async Task PipeTest()
    {
        using var timeout=new CancellationTokenSource(5000);string name="EpgCollectorTest-"+Guid.NewGuid().ToString("N");
        using var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
        var peer=Task.Run(async()=>{await server.WaitForConnectionAsync(timeout.Token);var bytes=new byte[8];await server.ReadExactlyAsync(bytes,timeout.Token);if(BitConverter.ToUInt32(bytes,0)!=2||BitConverter.ToUInt32(bytes,4)!=0)throw new Exception("Invalid EDCB protocol");var response=new byte[8];BitConverter.GetBytes((uint)1).CopyTo(response,0);await server.WriteAsync(response,timeout.Token);});
        await Collector.Reload(name,timeout.Token);await peer;
    }
}
