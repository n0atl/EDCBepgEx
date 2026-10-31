namespace EpgCollector;

internal static class EpgDatHistory
{
    // Keep each reception's PAT/TOT and EIT in order. EpgDataCap3 uses the
    // stream timestamps to apply newer event data without dropping old events
    // merely because they are absent from the latest reception.
    public static void Write(Stream? previous,Stream received,Stream destination,CancellationToken token=default)
    {
        if(previous!=null)Copy(previous,destination,token);
        Copy(received,destination,token);
    }
    static void Copy(Stream input,Stream output,CancellationToken token)
    {
        if(input.Length==0||input.Length%188!=0)throw new IOException("EPG datのTSサイズが不正なため、既存データを維持して保存を見送りました。");
        input.Position=0;
        byte[] buffer=new byte[188*256];
        while(true)
        {
            token.ThrowIfCancellationRequested();
            int count=0;
            while(count<buffer.Length){int read=input.Read(buffer,count,buffer.Length-count);if(read==0)break;count+=read;}
            if(count==0)break;
            for(int i=0;i<count;i+=188)if(buffer[i]!=0x47)throw new IOException("EPG datのTS同期が不正なため、既存データを維持して保存を見送りました。");
            output.Write(buffer,0,count);
        }
    }
}
