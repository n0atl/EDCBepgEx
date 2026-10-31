#if NETFRAMEWORK
using System.Diagnostics;
using System.Globalization;

namespace System.Runtime.CompilerServices { internal static class IsExternalInit {} }

namespace EpgCollector
{
    public readonly record struct TimeOnly(int Hour,int Minute)
    {
        public static bool TryParseExact(string text,string format,out TimeOnly result)
        {
            bool ok=DateTime.TryParseExact(text,format,CultureInfo.InvariantCulture,DateTimeStyles.NoCurrentDateDefault,out var time);
            result=new(time.Hour,time.Minute);return ok;
        }
    }
    internal static class FrameworkExtensions
    {
        public static IEnumerable<T> DistinctBy<T,TKey>(this IEnumerable<T> source,Func<T,TKey> key){var seen=new HashSet<TKey>();foreach(var item in source)if(seen.Add(key(item)))yield return item;}
        public static HashSet<T> ToHashSet<T>(this IEnumerable<T> source)=>new(source);
        public static TValue? GetValueOrDefault<TKey,TValue>(this Dictionary<TKey,TValue> source,TKey key)=>source.TryGetValue(key,out var value)?value:default;
        public static bool Remove<TKey,TValue>(this Dictionary<TKey,TValue> source,TKey key,out TValue value){if(!source.TryGetValue(key,out value))return false;return source.Remove(key);}
        public static bool Contains(this string text,char value)=>text.IndexOf(value)>=0;
        public static bool StartsWith(this string text,char value)=>text.Length>0&&text[0]==value;
        public static IAsyncResult BeginInvoke(this Control control,Action action)=>control.BeginInvoke((Delegate)action);
        public static void Kill(this Process process,bool entireProcessTree)=>process.Kill(); // Workers do not spawn children.
        public static async Task WaitForExitAsync(this Process process,CancellationToken token=default)
        {
            var done=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler handler=(_,_)=>done.TrySetResult(true);
            process.Exited+=handler;
            try
            {
                process.EnableRaisingEvents=true;
                if(process.HasExited)done.TrySetResult(true);
                using var registration=token.Register(()=>done.TrySetCanceled());
                await done.Task.ConfigureAwait(false);
            }
            finally{process.Exited-=handler;}
        }
        public static async Task WaitAsync(this Task task,TimeSpan timeout)
        {
            using var cancel=new CancellationTokenSource();
            var delay=Task.Delay(timeout,cancel.Token);
            if(await Task.WhenAny(task,delay).ConfigureAwait(false)!=task)throw new TimeoutException();
            cancel.Cancel();await task.ConfigureAwait(false);
        }
        public static Task WriteAsync(this Stream stream,byte[] bytes,CancellationToken token)=>stream.WriteAsync(bytes,0,bytes.Length,token);
        public static async Task ReadExactlyAsync(this Stream stream,byte[] bytes,CancellationToken token)
        {
            int offset=0;while(offset<bytes.Length){int n=await stream.ReadAsync(bytes,offset,bytes.Length-offset,token).ConfigureAwait(false);if(n==0)throw new EndOfStreamException();offset+=n;}
        }
    }
}
#endif
