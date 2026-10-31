using System.Text;
using System.Text.Json;

namespace EpgCollector;

public sealed class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    public string Driver { get; set; } = "";
    public string ChSet { get; set; } = "";
    public int Parallel { get; set; } = 1;
    public bool BSBasicOnly { get; set; } = true;
    public bool CS1BasicOnly { get; set; } = true;
    public bool CS2BasicOnly { get; set; } = true;
    public bool CS3BasicOnly { get; set; }
    public bool BasicOnly(ushort onid)=>onid switch {4=>BSBasicOnly,6=>CS1BasicOnly,7=>CS2BasicOnly,10=>CS3BasicOnly,_=>false};
    [System.Text.Json.Serialization.JsonIgnore]
    public int TimeoutSeconds { get; set; } = 600;
    [System.Text.Json.Serialization.JsonIgnore]
    public int MinimumSeconds { get; set; } = 60;
    [System.Text.Json.Serialization.JsonIgnore]
    public int StartupTimeoutSeconds { get; set; } = 30;
    public string Times { get; set; } = "04:00";
    public List<ScheduleEntry>? Schedule { get; set; }
    public List<string> Selected { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Driver) ? Name : Path.GetFileName(Driver);
    public List<ScheduleEntry> ScheduleEntries() => Schedule ?? Channels.Times(Times).Select(t=>new ScheduleEntry{Hour=t.Hour,Minute=t.Minute}).ToList();
    public string ScheduleText() => string.Join(" / ",ScheduleEntries().Select(s=>s.ToString()));
}

public sealed class ScheduleEntry
{
    public int Days { get; set; } = 127;
    public int Hour { get; set; } = 4;
    public int Minute { get; set; }
    public void Validate(){if(Days<1||Days>127||Hour<0||Hour>23||Minute<0||Minute>59)throw new InvalidDataException("曜日・実行時刻が不正です。");}
    public bool Matches(DateTime now){Validate();return (Days&(1<<(int)now.DayOfWeek))!=0&&Hour==now.Hour&&Minute==now.Minute;}
    public override string ToString()=> $"{(Days==127?"毎日":string.Join("・",Enumerable.Range(0,7).Where(i=>(Days&(1<<i))!=0).Select(i=>"日月火水木金土"[i])))} {Hour:00}:{Minute:00}";
}

public sealed class Settings
{
    public string Output { get; set; } = "";
    public bool Reload { get; set; } = true;
    public bool Sequential { get; set; } = true;
    public string EdcbFolder { get; set; } = "";
    public string Pipe { get; set; } = "EpgTimerSrvPipe";
    public List<Profile> Profiles { get; set; } = [];
    public Dictionary<string,List<ColumnLayout>> Columns { get; set; } = [];
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static Settings Load(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new() : new();
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".new";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        Compat.MoveReplace(temp, path);
    }
}

public sealed record ColumnLayout(string Name,int Width,int Order,bool Visible);

public sealed record Service(string Name, ushort Sid, int Type, bool Partial);
public sealed record EpgTarget(ushort Tsid,ushort Sid);
public sealed record Channel(uint Space, uint Ch, ushort Onid, ushort Tsid, string Name, List<Service> Services)
{
    public string NetworkName { get; init; } = "";
    public bool BasicOnly { get; init; }
    public List<EpgTarget>? Targets { get; init; }
    public List<EpgTarget> CaptureTargets=>Targets??EpgServices.Select(s=>new EpgTarget(Tsid,s.Sid)).ToList();
    public string Category=>Onid switch {>=0x7880 and <=0x7FE8=>"地デジ",4=>"BS",6 or 7=>"CS",10=>"スカパー！",_=>"その他"};
    public string Key => $"{Space}:{Ch}:{Onid}:{Tsid}";
    public string FileName => $"{Onid:X4}{(BasicOnly?0xFFFF:Tsid):X4}_epg.dat";
    public List<Service> EpgServices => Services.Where(s => !s.Partial && s.Type is 1 or 2 or 0xA5 or 0xAD).DistinctBy(s=>s.Sid).ToList();
    public override string ToString() => $"{Name}  |  {string.Join(" / ", EpgServices.Select(s => s.Name))}  [space {Space}, ch {Ch}]";
}

public static class Channels
{
    public static List<Channel> Read(string path)
    {
        Compat.RegisterCodePages();
        var text = File.ReadAllText(path, Encoding.GetEncoding(932));
        var rows = new List<Channel>();
        int line = 0;
        foreach (var raw in text.Split('\n'))
        {
            line++;
            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith(';')) continue;
            var c = raw.TrimEnd('\r').Split('\t');
            if(c.Length < 12) throw new InvalidDataException($"ChSet4 {line}行目: 12列必要です。");
            try
            {
                var item = new Channel(uint.Parse(c[3]), uint.Parse(c[4]), ushort.Parse(c[5]), ushort.Parse(c[6]), c[0], []) {NetworkName=c[2]};
                var existing = rows.FirstOrDefault(x => x.Key == item.Key);
                if (existing == null) { rows.Add(item); existing = item; }
                existing.Services.Add(new(c[1], ushort.Parse(c[7]), int.Parse(c[8]), c[9] != "0"));
            }
            catch(FormatException) { throw new InvalidDataException($"ChSet4 {line}行目: 数値が不正です。"); }
        }
        return rows;
    }
    public static int Bitness(string path)
    {
        using var f = File.OpenRead(path); using var r = new BinaryReader(f);
        if(r.ReadUInt16()!=0x5A4D) throw new InvalidDataException("BonDriverがPEファイルではありません。");
        f.Position=60; int offset=r.ReadInt32();
        if(offset<64 || offset>f.Length-6) throw new InvalidDataException("PEヘッダーが不正です。");
        f.Position=offset;
        if(r.ReadUInt32()!=0x4550)throw new InvalidDataException("PE署名が不正です。");
        return r.ReadUInt16() switch {0x14C=>32,0x8664=>64,_=>throw new InvalidDataException("未対応のCPU形式です。")};
    }
    public static List<TimeOnly> Times(string text)
    {
        var result = new List<TimeOnly>();
        foreach(var s in text.Split([',',';',' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if(!TimeOnly.TryParseExact(s,"HH:mm",out var time)) throw new InvalidDataException("実行時刻は04:00,12:30の形式で指定してください。");
            result.Add(time);
        }
        return result.Distinct().ToList();
    }
}

// The scheduler runs only while the GUI is open and armed. No catch-up burst after sleep.
public sealed class ScheduleClock
{
    private readonly HashSet<string> fired=[];
    public bool Due(Profile p,DateTime now)
    {
        if(!p.Enabled)return false;
        bool due=p.ScheduleEntries().Any(t=>t.Matches(now));
        if(!due)return false;
        string key=$"{p.Id}:{now:yyyyMMddHHmm}";
        if(fired.Count>10000)fired.Clear();
        return fired.Add(key);
    }
}
