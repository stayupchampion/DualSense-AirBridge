namespace DsBridge.Core;
// Deterministic signals shared by physical USB and the virtual USB bridge.
public static class FeedbackSignal {
    public static byte[] Trigger(bool release) {
        var report=new byte[48]; report[0]=2; report[1]=4;
        report[11]=release?(byte)0:(byte)1;
        if(!release) {report[12]=40; report[13]=80;}
        return report;
    }
    public static byte[] Audio(bool speaker, bool release=false) {
        var report=new byte[48];report[0]=2;
        if(speaker) {report[1]=release?(byte)0x20:(byte)0xA0;report[6]=release?(byte)0:(byte)60;report[8]=0x30;}
        return report;
    }
    public static double Sample(bool speaker,int channel,double time) {
        static double Tone(double t,double start,double end,double hz,double gain) {
            if(t<start||t>=end)return 0;
            var envelope=Math.Clamp(Math.Min(t-start,end-t)/0.025,0,1);
            return gain*envelope*Math.Sin(2*Math.PI*hz*t);
        }
        if(speaker) return channel<2 ? Tone(time,0.2,0.6,440,0.04)+Tone(time,0.95,1.35,660,0.04):0;
        return channel switch {
            2=>Tone(time,0.25,1.25,95,0.3)+Tone(time,2.85,3.65,125,0.22),
            3=>Tone(time,1.55,2.55,170,0.3)+Tone(time,2.85,3.65,125,0.22),
            _=>0
        };
    }
}
