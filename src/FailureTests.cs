using System.Text;

namespace EpgCollector;

internal static class FailureTests
{
    public static int Run()
    {
        string root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));
        string tmp=Path.Combine(root,"test-work","open-failure-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(tmp);
        var log=new System.Collections.Concurrent.ConcurrentQueue<string>();
        try
        {
            string samples=Path.Combine(root,"..","EDCB");
            var channels=Channels.Read(Path.Combine(samples,"BonDriver_Proxy_PX-MLT(BonDriverProxy).ChSet4.txt")).Where(c=>File.Exists(Path.Combine(samples,"EpgData",c.FileName))).Take(2).ToList();
            Compat.RegisterCodePages();
            Profile Fixture(Channel c,string folder,string mode)
            {
                string dir=Path.Combine(tmp,folder);Directory.CreateDirectory(dir);string dll=Path.Combine(dir,"custom.dll");
                File.Copy(Path.Combine(AppContext.BaseDirectory,"native","x86","MockBonDriver.dll"),dll);
                File.WriteAllLines(Path.ChangeExtension(dll,".ini"),["[Replay]",$"0={Path.Combine(samples,"EpgData",c.FileName)}",mode]);
                string chset=Path.Combine(dir,"fixture.ChSet4.txt");File.WriteAllLines(chset,c.Services.Select(s=>$"{c.Name}\t{s.Name}\tReplay\t0\t0\t{c.Onid}\t{c.Tsid}\t{s.Sid}\t{s.Type}\t{(s.Partial?1:0)}\t1\t0"),Encoding.GetEncoding(932));
                return new(){Driver=dll,ChSet=chset,Parallel=1,StartupTimeoutSeconds=1,Selected=Channels.Read(chset).Select(c=>c.Key).ToList()};
            }
            var bad=Fixture(channels[0],"fail","OpenFail=1");string dest=Path.Combine(tmp,channels[0].FileName);File.Copy(Path.Combine(samples,"EpgData",channels[0].FileName),dest);string? hash=Publisher.Fingerprint(dest);
            var collector=new Collector(AppContext.BaseDirectory,s=>log.Enqueue(s),_=>{});var settings=new Settings{Output=tmp,Reload=false};
            var failure=collector.Run(settings,[bad],CancellationToken.None).GetAwaiter().GetResult();
            if(failure.Saved!=0||failure.Failed!=1||Publisher.Fingerprint(dest)!=hash)throw new Exception("OpenTuner failure changed EPG or did not terminate");
            log.Enqueue("PASS OpenTuner failure is recorded and preserves existing dat");
            File.WriteAllLines(Path.ChangeExtension(bad.Driver,".ini"),["[Replay]","OpenHang=1"]);
            var good=Fixture(channels[1],"good","");good.MinimumSeconds=0;good.StartupTimeoutSeconds=30;
            var watch=System.Diagnostics.Stopwatch.StartNew();var concurrent=collector.Run(settings,[bad,good],CancellationToken.None).GetAwaiter().GetResult();
            if(concurrent.Saved!=1||concurrent.Failed!=1||watch.Elapsed.TotalSeconds>=30||Publisher.Fingerprint(dest)!=hash||!log.Any(s=>s.Contains("応答しない")))throw new Exception("Unresponsive driver blocked the other Bon or modified its dat");
            log.Enqueue("PASS Hung OpenTuner is terminated; another Bon completes; existing failed-Bon dat remains intact");
            File.WriteAllLines(Path.Combine(root,"open-failure-results.txt"),log);return 0;
        }
        catch(Exception ex){log.Enqueue("FAIL "+ex);File.WriteAllLines(Path.Combine(root,"open-failure-results.txt"),log);return 1;}
    }
}
