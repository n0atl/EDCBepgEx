using System.Diagnostics;
using System.Text.Json;

namespace EpgCollector;

internal static class EpgHistoryTests
{
    public static void Run(string samples,string tmp,Action<bool,string> check)
    {
        var c=Channels.Read(Path.Combine(samples,"BonDriver_Proxy_PX-MLT(BonDriverProxy).ChSet4.txt")).First(c=>File.Exists(Path.Combine(samples,"EpgData",c.FileName)));
        var source=File.ReadAllBytes(Path.Combine(samples,"EpgData",c.FileName));
        using var control=new MemoryStream();
        for(int i=0;i<source.Length;i+=188){int pid=((source[i+1]&31)<<8)|source[i+2];if(pid!=0x12&&pid!=0x26&&pid!=0x27)control.Write(source,i,188);}
        byte[] Reception(params (ushort Id,int Hour)[] events)=>control.ToArray().Concat(Eit(c,c.EpgServices.First().Sid,events)).ToArray();
        byte[] old=Reception((1001,1),(1002,2));byte[] next=Reception((1001,5),(1003,3));
        string destination=Path.Combine(tmp,"history_epg.dat"),stage=Path.Combine(tmp,"history-new.ts");
        File.WriteAllBytes(destination,old);File.WriteAllBytes(stage,next);
        Publisher.Commit(stage,destination,Publisher.Fingerprint(destination));
        check(File.ReadAllBytes(destination).SequenceEqual(old.Concat(next)),"History retains old TS and appends new reception in order");
        check(File.ReadAllBytes(Path.Combine(tmp,"CollectorBackup","history_epg.dat")).SequenceEqual(old),"History update backs up original dat");
        foreach(string arch in new[]{"x86","x64"})
        {
            var pi=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"native",arch,"EpgWorker.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            Compat.AddArgument(pi,"--inspect-events");Compat.AddArgument(pi,destination);
            using var p=Process.Start(pi)!;string stdout=p.StandardOutput.ReadToEnd();string error=p.StandardError.ReadToEnd();p.WaitForExit();
            check(p.ExitCode==0,$"{arch} history loads with EpgTimerSrv PAT/TOT order ({error})");
            using var doc=JsonDocument.Parse(stdout);var events=doc.RootElement.GetProperty("items").EnumerateArray().ToDictionary(e=>e.GetProperty("id").GetInt32(),e=>e.GetProperty("hour").GetInt32());
            check(events.Count==3&&events.GetValueOrDefault(1001)==5&&events.GetValueOrDefault(1002)==2&&events.GetValueOrDefault(1003)==3,$"{arch} old-only event retained, shared event updated, new event added");
        }
        string? before=Publisher.Fingerprint(destination);File.WriteAllBytes(stage,[1,2,3]);bool rejected=false;
        try{Publisher.Commit(stage,destination,before);}catch(IOException){rejected=true;}
        check(rejected&&Publisher.Fingerprint(destination)==before,"Malformed new TS cannot replace history");
        File.WriteAllBytes(stage,next);using var cancel=new CancellationTokenSource();cancel.Cancel();rejected=false;
        try{Publisher.Commit(stage,destination,before,token:cancel.Token);}catch(OperationCanceledException){rejected=true;}
        check(rejected&&Publisher.Fingerprint(destination)==before,"Cancellation during merge preserves existing history");
        File.WriteAllBytes(destination,[1,2,3]);before=Publisher.Fingerprint(destination);rejected=false;
        try{Publisher.Commit(stage,destination,before);}catch(IOException){rejected=true;}
        check(rejected&&Publisher.Fingerprint(destination)==before,"Malformed existing dat is preserved without silent replacement");
        check(!Directory.GetFiles(tmp,"*.partial").Any(),"Failed merges clean temporary output");
    }
    static byte[] Eit(Channel c,ushort sid,(ushort Id,int Hour)[] events)
    {
        var s=new List<byte>{0x50,0xB0,0,(byte)(sid>>8),(byte)sid,0xC3,0,0,(byte)(c.Tsid>>8),(byte)c.Tsid,(byte)(c.Onid>>8),(byte)c.Onid,0,0x50};
        foreach(var e in events)s.AddRange([(byte)(e.Id>>8),(byte)e.Id,0xEA,0x60,(byte)((e.Hour/10<<4)|e.Hour%10),0,0,1,0,0,0x80,0]);
        int length=s.Count+4-3;s[1]|=(byte)(length>>8);s[2]=(byte)length;
        uint crc=0xFFFFFFFF;foreach(byte b in s){crc^=(uint)b<<24;for(int i=0;i<8;i++)crc=(crc<<1)^((crc&0x80000000)!=0?0x04C11DB7u:0);}
        s.AddRange([(byte)(crc>>24),(byte)(crc>>16),(byte)(crc>>8),(byte)crc]);
        byte[] packet=Enumerable.Repeat((byte)255,188).ToArray();packet[0]=0x47;packet[1]=0x40;packet[2]=0x12;packet[3]=0x10;packet[4]=0;s.CopyTo(packet,5);return packet;
    }
}
