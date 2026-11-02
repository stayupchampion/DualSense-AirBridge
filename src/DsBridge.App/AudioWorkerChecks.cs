using System.Diagnostics;
using DsBridge.Core;
namespace DsBridge.App;

// Uses only synthetic child replies. No Core Audio calls, controller, driver, or registry access.
internal static class AudioWorkerChecks {
    internal sealed record OwnerReceipt(int Pid,long Started);
    internal static async Task<int> OwnerFixture(string path) {
        var file=Path.GetFullPath(path);
        if(!file.StartsWith(Path.GetFullPath(Paths.State)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("测试回执必须位于本程序 .local 内。");
        var worker=new AudioWorker("normal",readOnly:true);
        await worker.Snapshot();
        using var child=Process.GetProcessById(worker.LastPid);
        Directory.CreateDirectory(Paths.State);
        AtomicJson.Write(file,new OwnerReceipt(child.Id,child.StartTime.ToUniversalTime().Ticks));
        // Deliberately bypass DisposeAsync, like owner process termination. The OS job
        // must remove this synthetic helper when this separate fixture process exits.
        GC.KeepAlive(worker);
        return 0;
    }
    public static async Task<int> Run() {
        var results=new Dictionary<string,object>();
        static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        static bool Exited(int pid) {
            try {using var p=Process.GetProcessById(pid);return p.HasExited;}
            catch(ArgumentException){return true;}
        }
        var expected=new AudioState([new(0,0,"fixture-output","Fixture")],["fixture-output"]);
        await using(var normal=new AudioWorker("normal")) {
            var snapshot=await normal.Snapshot();
            Check(snapshot.Defaults[0].Id=="fixture-output","worker handshake");
            var clock=Stopwatch.StartNew();
            for(int i=0;i<40;i++)Check((await normal.Guard(expected)).Error.Length==0,"worker response");
            results["syntheticRoundTripMeanMs"]=clock.Elapsed.TotalMilliseconds/40;
            var pid=normal.LastPid;
            await normal.DisposeAsync();await normal.DisposeAsync();
            Check(Exited(pid),"normal disposal left an audio worker");
            results["normalDispose"]=true;
        }
        await using(var hung=new AudioWorker("hang")) {
            await hung.Snapshot();
            bool request=false;using var signal=new ManualResetEventSlim();
            await using var monitor=new SessionStopMonitor(()=>Volatile.Read(ref request),()=>signal.Set());
            var task=hung.Guard(expected,monitor.Token);
            await Task.Delay(100);
            var clock=Stopwatch.StartNew();Volatile.Write(ref request,true);
            bool cancelled=false;
            try {await task;}catch(OperationCanceledException){cancelled=true;}
            Check(cancelled&&signal.IsSet&&clock.Elapsed<TimeSpan.FromSeconds(4),"stop did not bypass hung audio");
            Check(Exited(hung.LastPid),"cancelled worker can still perform a late write");
            var pid=hung.LastPid;
            Check((await hung.Observe()).Error.Length>0&&hung.LastPid==pid,"poisoned worker restarted");
            results["cancelAndStopMs"]=clock.Elapsed.TotalMilliseconds;
            results["noLateWorkerOrRestart"]=true;
        }
        await using(var timeout=new AudioWorker("hang")) {
            await timeout.Snapshot();var clock=Stopwatch.StartNew();bool bounded=false;
            try {await timeout.Guard(expected);}catch(TimeoutException){bounded=true;}
            Check(bounded&&Exited(timeout.LastPid)&&clock.Elapsed<TimeSpan.FromSeconds(9),"audio timeout not bounded");
            results["timeoutAndReapMs"]=clock.Elapsed.TotalMilliseconds;
        }
        await using(var failed=new AudioWorker("exit")) {
            await failed.Snapshot();bool rejected=false;
            try {await failed.Guard(expected);}catch(IOException){rejected=true;}
            Check(rejected&&Exited(failed.LastPid),"abrupt worker exit not handled");
            results["unexpectedExit"]=true;
        }
        await using(var readOnly=new AudioWorker("normal",readOnly:true)) {
            Check((await readOnly.ReadVolumes()).Outputs.Single().Scalar==.4f,"read-only volume response");
            bool rejected=false;
            try {await readOnly.Guard(expected);}catch(IOException){rejected=true;}
            Check(rejected&&Exited(readOnly.LastPid),"read-only worker accepted a policy write");
            Check((await readOnly.ReadVolumes()).Display.Contains("读取暂不可用"),"failed observation claimed a disconnected output");
            results["readOnlyRejectsPolicyWrites"]=true;
        }
        Directory.CreateDirectory(Paths.State);
        var proof=Path.Combine(Paths.State,"audio-owner-fixture.json");
        // Do not put this owner fixture inside Run.Text's job: its own AudioWorker job
        // must prove cleanup without an outer process runner doing the cleanup for it.
        using(var owner=Process.Start(global::DsBridge.App.Run.Info(Environment.ProcessPath!,"--audio-owner-fixture",proof))!) {
            var output=owner.StandardOutput.ReadToEndAsync();var error=owner.StandardError.ReadToEndAsync();
            try {await Task.WhenAll(output,error,owner.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(10));}
            catch {if(!owner.HasExited)owner.Kill(entireProcessTree:true);throw;}
            Check(owner.ExitCode==0,"owner fixture failed: "+await error);
        }
        var receipt=AtomicJson.Read<OwnerReceipt>(proof);
        for(int i=0;i<20&&global::DsBridge.App.Run.IsAlive(receipt.Pid,receipt.Started);i++)await Task.Delay(100);
        Check(!global::DsBridge.App.Run.IsAlive(receipt.Pid,receipt.Started),"audio helper outlived its owner process");
        results["ownerExitReapsHelper"]=true;
        await using(var policy=new AudioWorker("normal"))
        await using(var observer=new AudioWorker("observe-hang",readOnly:true)) {
            await policy.Snapshot();
            using var budget=new CancellationTokenSource(150);
            Check((await observer.Observe(budget.Token)).Error.Length>0,"stuck recovery observation was not bounded");
            Check((await policy.Guard(expected)).Error.Length==0&&policy.IsUsable,"diagnostic timeout poisoned policy recovery");
            Check(Exited(observer.LastPid),"diagnostic worker survived timeout");
            results["recoveryObserverFailureIsolated"]=true;
        }
        results["deviceApisCalled"]=false;
        AtomicJson.Write(Path.Combine(Paths.State,"audio-isolation-check.json"),results);
        return 0;
    }
}
