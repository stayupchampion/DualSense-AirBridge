using System.Text;
namespace DsBridge.Core;

public sealed record BackendLogEvidence(bool Started,bool NeutralConfirmed,bool Windowed,bool LightingConfirmed=false) {
    public bool NeedsWarning=>!NeutralConfirmed&&(Started||Windowed);
    public bool LightingNeedsWarning=>!LightingConfirmed&&(Started||Windowed);
    public const int WindowBytes=64*1024;
    public static BackendLogEvidence Read(Stream stream) {
        var length=stream.Length;
        string Window(long offset,int count) {
            stream.Position=offset;var bytes=new byte[count];int used=0,n;
            while(used<count&&(n=stream.Read(bytes,used,count-used))>0)used+=n;
            return Encoding.UTF8.GetString(bytes,0,used);
        }
        var head=Window(0,(int)Math.Min(length,WindowBytes));
        var tail=length>WindowBytes?Window(Math.Max(0,length-WindowBytes),WindowBytes):"";
        bool Contains(string marker)=>head.Contains(marker,StringComparison.Ordinal)||tail.Contains(marker,StringComparison.Ordinal);
        return new(Contains("Windows bridge started virtual="),
            Contains("shutdown neutral reports written; microphone close written"),length>2L*WindowBytes,
            Contains("shutdown lighting released; native light requested"));
    }
}
