namespace EpgCollector;

internal sealed class BonRuns : IDisposable
{
    readonly Dictionary<string,CancellationTokenSource> active=[];
    public int Count=>active.Count;
    public bool StoppingAll {get;private set;}
    public bool Contains(string id)=>active.ContainsKey(id);
    public string ButtonText(string id)=>active.TryGetValue(id,out var c)?c.IsCancellationRequested?"停止中…":"停止":"取得開始";
    public CancellationToken Start(string id)
    {
        if(StoppingAll)throw new InvalidOperationException("停止処理が終わってから取得してください。");
        var c=new CancellationTokenSource();active.Add(id,c);return c.Token;
    }
    public void Stop(string id){if(active.TryGetValue(id,out var c))c.Cancel();}
    public void StopAll(){StoppingAll=Count>0;foreach(var c in active.Values)c.Cancel();}
    public void Finish(string id){if(active.Remove(id,out var c))c.Dispose();if(Count==0)StoppingAll=false;}
    public void Dispose(){foreach(var c in active.Values)c.Dispose();active.Clear();}
}
