using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;

namespace EpgCollector;

public sealed record ProgressInfo(string Profile, string Channel, string State, int Seconds=0, int Done=0, int Total=0, double Signal=0, long Eit=0, string NetworkName="",string DriverPath="",string ChSetPath="",uint Space=0,uint ChannelIndex=0,ushort Onid=0,ushort Tsid=0,string Sids="",int Bits=0,long Packets=0,string Message="",bool BasicOnly=false,long Drop=0)
{
    public static ProgressInfo For(Profile p,Channel c,string state,int seconds=0,int done=0,int total=0,double signal=0,long eit=0,int bits=0,long packets=0,long drop=0)=>
        new(p.Id,c.Name,state,seconds,done,total,signal,eit,c.NetworkName,p.Driver,p.ChSet,c.Space,c.Ch,c.Onid,c.Tsid,string.Join(",",c.CaptureTargets.Select(s=>$"{s.Tsid}:{s.Sid}")),bits,packets,BasicOnly:c.BasicOnly,Drop:drop);
}
public sealed record RunResult(int Saved, int Failed);

public static class Publisher
{
    public static string? Fingerprint(string path)
    {
        if(!File.Exists(path))return null;
        using var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
        return Compat.Hash(f);
    }
    public static void Commit(string staging,string destination,string? expected,bool preserveHistory=true,CancellationToken token=default)
    {
        if(File.Exists(destination+".tmp"))throw new IOException("EDCBが同じEPGを取得中のため保存を見送りました。");
        string tmp=destination+".collector-"+Guid.NewGuid().ToString("N")+".partial";
        try
        {
            // Do not block EDCB writers throughout a potentially large merge.
            // Recheck and briefly lock the current file only at publication.
            using var previous=expected==null?null:new FileStream(destination,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            if(previous!=null&&Compat.Hash(previous)!=expected)throw new IOException("取得中に既存datが更新されたため保存を見送りました。");
            if(preserveHistory)
            {
                using var received=new FileStream(staging,FileMode.Open,FileAccess.Read,FileShare.Read);
                using var merged=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None);
                EpgDatHistory.Write(previous,received,merged,token);merged.Flush(true);
            }
            else
            {
                File.Copy(staging,tmp,false);
                using var flush=new FileStream(tmp,FileMode.Open,FileAccess.ReadWrite,FileShare.None);flush.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            if(File.Exists(destination+".tmp"))throw new IOException("EDCBが同じEPGを取得中のため保存を見送りました。");
            if(expected!=null)
            {
                // Deny in-place writers until replacement, allow atomic replacement of the open file.
                using var held=new FileStream(destination,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
                if(Compat.Hash(held)!=expected)throw new IOException("datの結合中に既存ファイルが更新されたため保存を見送りました。");
                string backup=Path.Combine(Path.GetDirectoryName(destination)!,"CollectorBackup",Path.GetFileName(destination));
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Replace(tmp,destination,backup,true);
            }
            else File.Move(tmp,destination);
        }
        finally{if(File.Exists(tmp))File.Delete(tmp);}
    }
}

public sealed class Collector
{
    private static readonly ConcurrentDictionary<string,SemaphoreSlim> DriverGates=new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string,SemaphoreSlim> TempGates=new(StringComparer.OrdinalIgnoreCase);
    private readonly string app;
    private readonly Action<string> log;
    private readonly Action<ProgressInfo> progress;
    public Collector(string app,Action<string> log,Action<ProgressInfo> progress){this.app=app;this.log=log;this.progress=progress;}
    public static List<Channel> Validate(Profile p)
    {
        if(!File.Exists(p.Driver))throw new InvalidDataException($"{p.DisplayName}: BonDriverが見つかりません。");
        Channels.Bitness(p.Driver);
        if(p.Parallel<1 || p.Parallel>16)throw new InvalidDataException("同時取得数は1～16で指定してください。");
        if(p.MinimumSeconds<0 || p.TimeoutSeconds<=p.MinimumSeconds || p.TimeoutSeconds>3600)throw new InvalidDataException("最短蓄積は0秒以上、タイムアウトは最短蓄積より長く3600秒以下にしてください。");
        var available=Channels.Read(p.ChSet);
        var selected=available.Where(c=>p.Selected.Contains(c.Key)).ToList();
        if(selected.Count!=p.Selected.Distinct().Count())throw new InvalidDataException($"{p.DisplayName}: ChSet4に存在しない選択があります。チャンネルを再選択してください。");
        if(selected.Count==0 || selected.Any(c=>c.EpgServices.Count==0))throw new InvalidDataException($"{p.DisplayName}: EPG取得対象の通常サービスを含むチャンネルを選んでください。");
        return selected;
    }
    internal static List<Channel> Plan(Profile p,IEnumerable<Channel> selected)
    {
        var all=selected.ToList();var result=new List<Channel>();var done=new HashSet<ushort>();
        foreach(var c in all)
        {
            if(!p.BasicOnly(c.Onid)){result.Add(c);continue;}
            if(done.Add(c.Onid))result.Add(c with {BasicOnly=true,Targets=all.Where(a=>a.Onid==c.Onid).SelectMany(a=>a.CaptureTargets).Distinct().ToList()});
        }
        return result;
    }
    internal static Dictionary<string,List<Channel>> Prepare(IEnumerable<Profile> profiles)
    {
        var jobs=profiles.Select(p=>(Profile:p,Channels:Plan(p,Validate(p)))).ToList();
        Deduplicate(jobs);return jobs.ToDictionary(j=>j.Profile.Id,j=>j.Channels);
    }
    static void Deduplicate(List<(Profile Profile,List<Channel> Channels)> jobs)
    {
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var basicTargets=jobs.SelectMany(j=>j.Channels).Where(c=>c.BasicOnly).GroupBy(c=>c.FileName).ToDictionary(g=>g.Key,g=>g.SelectMany(c=>c.CaptureTargets).Distinct().ToList());
        foreach(var job in jobs){for(int i=0;i<job.Channels.Count;i++)if(job.Channels[i].BasicOnly)job.Channels[i]=job.Channels[i] with {Targets=basicTargets[job.Channels[i].FileName]};job.Channels.RemoveAll(c=>!seen.Add(c.FileName));}
    }
    public async Task<RunResult> Run(Settings settings,IEnumerable<Profile> selected,CancellationToken ct,IReadOnlyDictionary<string,List<Channel>>? prepared=null)
    {
        var jobs=selected.Select(p=>(Profile:p,Channels:prepared!=null?prepared[p.Id]:Plan(p,Validate(p)))).ToList();
        if(jobs.Count==0)throw new InvalidDataException("取得設定を選んでください。");
        string output=Path.GetFullPath(settings.Output);
        if(!Directory.Exists(output))throw new DirectoryNotFoundException("保存先フォルダーがありません。参照ボタンから選択してください。");
        if(prepared==null)Deduplicate(jobs);
        int saved=0,failed=0;
        try
        {
            await Task.WhenAll(jobs.Select(async job=>
            {
                var gate=DriverGates.GetOrAdd(Path.GetFullPath(job.Profile.Driver),_=>new(1,1));
                await gate.WaitAsync(ct);
                try
                {
                    await Compat.ForEachAsync(job.Channels,job.Profile.Parallel,ct,async(c,token)=>
                    {
                        string stage=Path.GetFullPath(Path.Combine(app,"temp",c.FileName+".tmp"));
                        var tempGate=TempGates.GetOrAdd(stage,_=>new(1,1));
                        await tempGate.WaitAsync(token);
                        string cancel=stage+".cancel";
                        string destination=Path.Combine(output,c.FileName);
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(stage)!);
                            string? baseline=Publisher.Fingerprint(destination);
                            await Capture(job.Profile,c,stage,cancel,token);
                            token.ThrowIfCancellationRequested();
                            Publisher.Commit(stage,destination,baseline,token:token);
                            Interlocked.Increment(ref saved);log($"{job.Profile.DisplayName} / {c.Name}: 保存完了 {c.FileName}"+(baseline!=null?"（既存データを保持）":""));
                            progress(ProgressInfo.For(job.Profile,c,"保存完了"));
                        }
                        catch(OperationCanceledException){progress(ProgressInfo.For(job.Profile,c,"中止"));throw;}
                        catch(Exception ex){Interlocked.Increment(ref failed);log($"{job.Profile.DisplayName} / {c.Name}: {ex.Message}");progress(ProgressInfo.For(job.Profile,c,"失敗") with {Message=ex.Message});}
                        finally{TryDelete(stage);TryDelete(cancel);tempGate.Release();}
                    });
                }
                finally{gate.Release();}
            }));
        }
        finally
        {
            // Previously completed channels remain useful even when a later channel is cancelled.
            if(saved>0 && settings.Reload){try{await Reload(settings.Pipe,CancellationToken.None);log("EDCBにEPG再読み込みを依頼しました（表示への反映は非同期です）。");}catch(Exception ex){log("dat保存済み / 再読み込み失敗: "+ex.Message);}}
        }
        return new(saved,failed);
    }
    private async Task Capture(Profile p,Channel c,string stage,string cancel,CancellationToken ct)
    {
        int bits=Channels.Bitness(p.Driver);
        string worker=Path.Combine(app,"native",bits==32?"x86":"x64","EpgWorker.exe");
        if(!File.Exists(worker))throw new FileNotFoundException($"{bits}bit受信ワーカーがありません。",worker);
        var info=new ProcessStartInfo(worker){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(p.Driver)!};
        foreach(var a in new[]{"--capture",Path.GetFullPath(p.Driver),c.Space.ToString(),c.Ch.ToString(),c.Onid.ToString(),c.Tsid.ToString(),p.TimeoutSeconds.ToString(),p.MinimumSeconds.ToString(),string.Join(",",c.CaptureTargets.Select(s=>$"{s.Tsid}:{s.Sid}")),stage,cancel})Compat.AddArgument(info,a);
        if(c.BasicOnly)Compat.AddArgument(info,"--basic");
        using var process=Process.Start(info)??throw new IOException("受信ワーカーを起動できません。");
        progress(ProgressInfo.For(p,c,"取得中",bits:bits));log($"{p.DisplayName} / {c.Name}: 取得開始 ({bits}bit)");
        var stderr=process.StandardError.ReadToEndAsync();
        using var watchdog=CancellationTokenSource.CreateLinkedTokenSource(ct);
        watchdog.CancelAfter(TimeSpan.FromSeconds(p.StartupTimeoutSeconds));
        var read=Task.Run(async()=>
        {
            while(await process.StandardOutput.ReadLineAsync() is {} line)
            {
                using var doc=JsonDocument.Parse(line);var root=doc.RootElement;
                if(root.GetProperty("type").GetString()=="ready")watchdog.CancelAfter(TimeSpan.FromSeconds(p.TimeoutSeconds+30));
                if(root.GetProperty("type").GetString()=="progress")progress(ProgressInfo.For(p,c,"取得中",root.GetProperty("seconds").GetInt32(),root.GetProperty("done").GetInt32(),root.GetProperty("total").GetInt32(),root.GetProperty("signal").GetDouble(),root.GetProperty("eit").GetInt64(),bits,root.GetProperty("packets").GetInt64(),root.TryGetProperty("drop",out var drop)?drop.GetInt64():0));
            }
        });
        try
        {
            await process.WaitForExitAsync(watchdog.Token);
            await read;
            if(process.ExitCode!=0)throw new IOException((await stderr).Trim());
        }
        catch(OperationCanceledException)
        {
            File.WriteAllText(cancel,"");
            try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));}catch(TimeoutException){await EndWorker(process,p,c);}
            try{await read.WaitAsync(TimeSpan.FromSeconds(3));}catch{}
            if(ct.IsCancellationRequested)throw new OperationCanceledException(ct);
            throw new TimeoutException("受信ワーカーが応答しないため終了しました。");
        }
        finally{if(!process.HasExited)await EndWorker(process,p,c);}
    }
    // A worker blocked inside a tuner driver may outlive TerminateProcess; stop waiting so the GUI can finish stopping.
    async Task EndWorker(Process process,Profile p,Channel c)
    {
        log($"{p.DisplayName} / {c.Name}: 受信ワーカー（PID {process.Id}）が応答しないため強制終了します。");
        try{process.Kill(true);}catch(Exception ex) when(ex is InvalidOperationException or System.ComponentModel.Win32Exception){log($"{p.DisplayName} / {c.Name}: 強制終了に失敗しました: {ex.Message}");}
        try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));}
        catch(TimeoutException)
        {
            log($"{p.DisplayName} / {c.Name}: 受信ワーカー（PID {process.Id}）が強制終了後も終わりません。終了するまで裏で強制終了を繰り返します。");
            _=RetryKill(process.Id,process.StartTime,$"{p.DisplayName} / {c.Name}");
        }
    }
    // Keeps terminating a worker that survived Kill; the start time guards against a reused PID.
    async Task RetryKill(int id,DateTime started,string label)
    {
        for(int i=0;i<120;i++)
        {
            await Task.Delay(5000);
            try
            {
                using var worker=Process.GetProcessById(id);
                if(worker.HasExited||worker.StartTime!=started)break;
                worker.Kill();
            }
            catch(ArgumentException){break;}
            catch(Exception ex) when(ex is InvalidOperationException or System.ComponentModel.Win32Exception){}
        }
        try{using var worker=Process.GetProcessById(id);if(!worker.HasExited&&worker.StartTime==started){log($"{label}: 受信ワーカー（PID {id}）を10分間終了できませんでした。タスクマネージャーで終了してください。");return;}}catch(ArgumentException){}catch(InvalidOperationException){}
        log($"{label}: 受信ワーカー（PID {id}）が終了しました。");
    }
    public static async Task Reload(string pipe,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(pipe)||pipe.Contains('\\')||pipe.Contains('/'))throw new InvalidDataException("パイプ名が不正です。");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(5000);
        using var client=new NamedPipeClientStream(".",pipe,PipeDirection.InOut,PipeOptions.Asynchronous);
        await client.ConnectAsync(timeout.Token);
        byte[] command=new byte[8];BitConverter.GetBytes((uint)2).CopyTo(command,0);
        await client.WriteAsync(command,timeout.Token);await client.FlushAsync(timeout.Token);
        byte[] response=new byte[8];await client.ReadExactlyAsync(response,timeout.Token);
        if(BitConverter.ToUInt32(response,0)!=1)throw new IOException($"EDCB応答: {BitConverter.ToUInt32(response,0)}");
    }
    private static void TryDelete(string path){try{File.Delete(path);}catch{}}
}
