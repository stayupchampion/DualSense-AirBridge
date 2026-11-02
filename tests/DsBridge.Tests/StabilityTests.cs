using System.Text;
using DsBridge.Core;
internal static class StabilityTests {
    public static void Run(Action<bool,string> check) {
        var flags="--cloak-on\n--inv-off\n";
        var middle="--dev-hide \"HID\\ONE\"\n--app-reg \"C:\\应用 Space\\vdsd.exe\"\n";
        var parsed=HideParser.Snapshot(flags+middle+flags);
        check(parsed.Active&&!parsed.Inverse&&parsed.Devices.Single()==@"HID\ONE"&&parsed.Applications.Single()==@"C:\应用 Space\vdsd.exe","single HidHide snapshot preserves flags, paths and Unicode");
        check(HideParser.Snapshot(flags+flags).Devices.Length==0,"explicitly complete empty hidden list accepted");
        check(HideParser.Snapshot((flags+middle+flags).Replace("\n","\r\n")).Applications.SequenceEqual(parsed.Applications),"CRLF snapshot supported");
        foreach(var malformed in new[]{"",flags,flags+middle,flags+middle+"--cloak-off\n--inv-off\n",
            flags+"unexpected\n"+flags,flags+"--app-reg \"x\"\n--dev-hide \"y\"\n"+flags,
            flags+"--dev-hide missing-quotes\n"+flags,flags+"--cloak-on\n"+flags}) {
            bool rejected=false;try{HideParser.Snapshot(malformed);}catch(InvalidDataException){rejected=true;}
            check(rejected,"incomplete or inconsistent HidHide snapshot rejected");
        }
        var timing=new BridgeTiming();
        check(!timing.StartupReady(TimeSpan.Zero,true),"handshake starts a stability interval");
        check(!timing.StartupReady(TimeSpan.FromSeconds(2.99),true)&&timing.StartupReady(TimeSpan.FromSeconds(3),true),"three-second readiness threshold retained");
        check(!timing.StartupReady(TimeSpan.FromSeconds(4),false)&&!timing.StartupReady(TimeSpan.FromSeconds(5),true),"startup disconnect resets stable interval");
        timing.AudioCorrected(TimeSpan.FromSeconds(7));
        check(!timing.StartupReady(TimeSpan.FromSeconds(8),true)&&timing.StartupReady(TimeSpan.FromSeconds(8.5),true),"audio correction retains 1.5-second settle interval");
        check(!timing.StartupExpired(TimeSpan.FromSeconds(24.99))&&timing.StartupExpired(TimeSpan.FromSeconds(25)),"startup deadline remains 25 monotonic seconds");
        timing.Healthy(TimeSpan.FromSeconds(30));
        check(!timing.ContactExpired(TimeSpan.FromSeconds(35))&&timing.ContactExpired(TimeSpan.FromSeconds(35.001)),"active disconnect threshold retains five-second grace");
        timing.Healthy(TimeSpan.FromSeconds(35.1));
        check(!timing.ContactExpired(TimeSpan.FromSeconds(39)),"new health reply resets elapsed contact age");
        using var small=new MemoryStream(Encoding.UTF8.GetBytes("Windows bridge started virtual=x\n"));
        check(BackendLogEvidence.Read(small).NeedsWarning,"started backend without neutral proof is reported");
        using var empty=new MemoryStream();
        check(!BackendLogEvidence.Read(empty).NeedsWarning,"empty startup-failure log does not invent feedback output");
        var text="Windows bridge started virtual=x\n"+new string('x',300000)+"\nshutdown neutral reports written; microphone close written\n";
        using var large=new CountingStream(Encoding.UTF8.GetBytes(text));
        var evidence=BackendLogEvidence.Read(large);
        check(evidence.Started&&evidence.NeutralConfirmed&&!evidence.NeedsWarning,"large log retains startup and final neutral evidence");
        check(evidence.LightingNeedsWarning,"neutral output alone does not prove lighting handoff");
        check(large.BytesRead<=2*BackendLogEvidence.WindowBytes&&evidence.Windowed,"shutdown log allocation and IO bounded to two windows");
        using var unknown=new MemoryStream(Encoding.UTF8.GetBytes(new string('x',300000)));
        check(BackendLogEvidence.Read(unknown).NeedsWarning,"unobserved middle of large log cannot falsely confirm neutral output");
        using var complete=new MemoryStream(Encoding.UTF8.GetBytes(text+"shutdown lighting released; native light requested\n"));
        var lighting=BackendLogEvidence.Read(complete);
        check(lighting.NeutralConfirmed&&lighting.LightingConfirmed&&!lighting.LightingNeedsWarning,"both shutdown steps retain independent confirmation");
        using var lightOnly=new MemoryStream(Encoding.UTF8.GetBytes("Windows bridge started virtual=x\nshutdown lighting released; native light requested\n"));
        var incomplete=BackendLogEvidence.Read(lightOnly);
        check(incomplete.LightingConfirmed&&incomplete.NeedsWarning,"successful lighting cannot conceal failed force cleanup");
        using var failure=new MemoryStream(Encoding.UTF8.GetBytes("Windows bridge started virtual=x\nshutdown LED release not confirmed: timeout\n"));
        check(BackendLogEvidence.Read(failure).LightingNeedsWarning,"failed lighting write remains unconfirmed");
        check(!BackendLogEvidence.Read(empty).LightingNeedsWarning,"unstarted empty session does not invent a lighting failure");
    }
    sealed class CountingStream(byte[] bytes):MemoryStream(bytes) {
        public int BytesRead;
        public override int Read(byte[] buffer,int offset,int count){var read=base.Read(buffer,offset,count);BytesRead+=read;return read;}
    }
}
