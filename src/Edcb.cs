using System.Runtime.InteropServices;
using System.Text;

namespace EpgCollector;

// Locates EDCB's folders with EDCB's own rules (Common/PathUtil.cpp GetSettingPath, BonCtrl/ChSetUtil.cpp).
public static class Edcb
{
    static readonly string[] Markers=["EpgTimerSrv.exe","EpgTimer.exe","EpgDataCap_Bon.exe","Common.ini"];
    public static bool IsEdcbFolder(string? dir)=>!string.IsNullOrWhiteSpace(dir)&&Directory.Exists(dir)&&Markers.Any(m=>File.Exists(Path.Combine(dir,m)));
    // Common.ini [SET] DataSavePath, or <EDCB>\Setting when it is not set.
    public static string SettingPath(string edcb)
    {
        string ini=Path.Combine(edcb,"Common.ini");
        string saved=File.Exists(ini)?ReadIni(ini,"SET","DataSavePath"):"";
        return string.IsNullOrWhiteSpace(saved)?Path.Combine(edcb,"Setting"):Path.GetFullPath(Path.Combine(edcb,saved));
    }
    public static string EpgDataPath(string edcb)=>Path.Combine(SettingPath(edcb),"EpgData");
    public static string BonDriverPath(string edcb)=>Path.Combine(edcb,"BonDriver");
    // The EDCB folder whose BonDriver subfolder holds this DLL, if any.
    public static string? FolderOfDriver(string driver)
    {
        string? dir=Path.GetDirectoryName(Path.GetFullPath(driver));
        if(dir==null||!string.Equals(Path.GetFileName(dir),"BonDriver",StringComparison.OrdinalIgnoreCase))return null;
        string? edcb=Path.GetDirectoryName(dir);
        return IsEdcbFolder(edcb)?edcb:null;
    }
    // EDCB names channel files "<DLL name>(<tuner name>).ChSet4.txt"; search EDCB's Setting folder first, then the DLL's folder.
    public static string? FindChSet(string driver)
    {
        string pattern=Path.GetFileNameWithoutExtension(driver)+"(*).ChSet4.txt";
        var folders=new List<string>();
        if(FolderOfDriver(driver) is {} edcb)folders.Add(SettingPath(edcb));
        if(Path.GetDirectoryName(Path.GetFullPath(driver)) is {} own)folders.Add(own);
        return folders.Where(Directory.Exists).SelectMany(f=>Directory.EnumerateFiles(f,pattern).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase)).FirstOrDefault();
    }
    // One profile per EDCB BonDriver that has a matching ChSet4 and is not registered yet.
    // Schedules start disabled so importing never starts unattended captures on every tuner.
    public static List<Profile> Import(string edcb,IEnumerable<Profile> existing)
    {
        var registered=new HashSet<string>(existing.Where(p=>!string.IsNullOrWhiteSpace(p.Driver)).Select(p=>Path.GetFullPath(p.Driver)),StringComparer.OrdinalIgnoreCase);
        var result=new List<Profile>();string folder=BonDriverPath(edcb);
        if(!Directory.Exists(folder))return result;
        foreach(var dll in Directory.EnumerateFiles(folder,"*.dll").OrderBy(x=>x,StringComparer.OrdinalIgnoreCase))
        {
            if(registered.Contains(Path.GetFullPath(dll))||FindChSet(dll) is not {} chset)continue;
            try
            {
                Channels.Bitness(dll);
                var selected=Channels.Read(chset).Where(c=>c.EpgServices.Count>0).Select(c=>c.Key).ToList();
                if(selected.Count>0)result.Add(new(){Name=Path.GetFileName(dll),Driver=Path.GetFullPath(dll),ChSet=chset,Selected=selected,Enabled=false});
            }
            catch(InvalidDataException){}
        }
        return result;
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]
    static extern uint GetPrivateProfileString(string section,string key,string defaultValue,StringBuilder result,uint size,string file);
    static string ReadIni(string file,string section,string key){var result=new StringBuilder(32768);GetPrivateProfileString(section,key,"",result,(uint)result.Capacity,file);return result.ToString();}
}
