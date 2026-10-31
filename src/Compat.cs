using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace EpgCollector;

internal static class Compat
{
    public static string Hash(Stream stream){using var sha=SHA256.Create();return Hex(sha.ComputeHash(stream));}
    public static string Hash(byte[] bytes){using var sha=SHA256.Create();return Hex(sha.ComputeHash(bytes));}
    static string Hex(byte[] bytes)=>BitConverter.ToString(bytes).Replace("-","");
    public static int Clamp(int value,int min,int max)=>Math.Max(min,Math.Min(max,value));
    public static void AddArgument(ProcessStartInfo info,string value)=>info.Arguments+=(info.Arguments.Length==0?"":" ")+Quote(value);
    // Windows argv quoting: preserve quotes and trailing backslashes in paths.
    internal static string Quote(string value)
    {
        var text=new StringBuilder("\"");int slashes=0;
        foreach(char c in value)
        {
            if(c=='\\'){slashes++;continue;}
            if(c=='"'){text.Append('\\',slashes*2+1).Append(c);slashes=0;continue;}
            text.Append('\\',slashes).Append(c);slashes=0;
        }
        return text.Append('\\',slashes*2).Append('"').ToString();
    }
    public static void MoveReplace(string source,string destination){if(File.Exists(destination))File.Replace(source,destination,null);else File.Move(source,destination);}
    public static void RegisterCodePages()
    {
#if !NETFRAMEWORK
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#endif
    }
    public static async Task ForEachAsync<T>(IEnumerable<T> source,int concurrency,CancellationToken token,Func<T,CancellationToken,Task> action)
    {
        // A fixed set of consumers keeps the per-profile concurrency bound and
        // stops taking queued channels when cancellation is requested.
        using var enumerator=source.GetEnumerator();object sync=new();
        await Task.WhenAll(Enumerable.Range(0,concurrency).Select(_=>Task.Run(async()=>
        {
            while(true)
            {
                T item;lock(sync){token.ThrowIfCancellationRequested();if(!enumerator.MoveNext())return;item=enumerator.Current;}
                await action(item,token);
            }
        },token)));
    }
}
