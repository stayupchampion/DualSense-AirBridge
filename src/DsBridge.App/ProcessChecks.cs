using System.Diagnostics;
using System.Text;
using DsBridge.Core;
namespace DsBridge.App;

// Synthetic process/CLI checks only. No driver, HID or Core Audio APIs.
internal static class ProcessChecks {
    sealed record ChildReceipt(int Pid,long Started);
    static string ReceiptPath(string path) {
        var full=Path.GetFullPath(path);
        if(!full.StartsWith(Path.GetFullPath(Paths.State)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("测试回执必须在本程序 .local 内。");
        return full;
    }
    public static int Fixture(string[] args) {
        using var stdout=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false)){AutoFlush=true};
        using var stderr=new StreamWriter(Console.OpenStandardError(),new UTF8Encoding(false)){AutoFlush=true};
        switch(args[0]) {
            case "hold-output":
                using(var self=Process.GetCurrentProcess())AtomicJson.Write(ReceiptPath(args[1]),new ChildReceipt(self.Id,self.StartTime.ToUniversalTime().Ticks));
                Thread.Sleep(4000);return 0;
            case "pipe-held":
                var info=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true};
                foreach(var arg in new[]{"--process-fixture","hold-output",ReceiptPath(args[1])})info.ArgumentList.Add(arg);
                using(var child=Process.Start(info)!){child.StandardInput.Close();}
                stdout.WriteLine("parent-exited");return 0;
            case "hang":
                using(var self=Process.GetCurrentProcess())AtomicJson.Write(ReceiptPath(args[1]),new ChildReceipt(self.Id,self.StartTime.ToUniversalTime().Ticks));
                Thread.Sleep(10000);return 0;
            case "mixed":stdout.Write(new string('o',131072));stderr.Write(new string('e',131072));return 0;
            case "oversize":stdout.Write(new string('x',2*1024*1024));return 0;
            case "error":stdout.Write("fixture-output");stderr.Write("fixture-error 0x0005");return 7;
            // JSON escapes non-ASCII so this checks Windows argument round-tripping
            // without changing the existing native CLI console-code-page contract.
            case "echo":stdout.Write(System.Text.Json.JsonSerializer.Serialize(args[1]));return 0;
            default:throw new InvalidOperationException("unknown process fixture");
        }
    }
    static async Task LegacyText(string receipt) {
        using var process=Process.Start(Run.Info(Environment.ProcessPath!,"--process-fixture","pipe-held",receipt))!;
        var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await output;await error;
    }
    static void Check(bool value,string label){if(!value)throw new InvalidOperationException(label);}
    static async Task Reaped(string path) {
        var child=AtomicJson.Read<ChildReceipt>(path);
        for(int i=0;i<30&&Run.IsAlive(child.Pid,child.Started);i++)await Task.Delay(100);
        Check(!Run.IsAlive(child.Pid,child.Started),"owned fixture survived command cleanup");
    }
    public static async Task<int> CheckAll() {
        Directory.CreateDirectory(Paths.State);
        var results=new Dictionary<string,object>();
        var prefix=Path.Combine(Paths.State,"process-"+Guid.NewGuid().ToString("N"));
        var clock=Stopwatch.StartNew();
        await LegacyText(prefix+"-legacy.json");
        results["legacyOutputWaitMs"]=clock.Elapsed.TotalMilliseconds;
        Check(clock.Elapsed>TimeSpan.FromSeconds(3),"legacy pipe inheritance case was not reproduced");
        await Reaped(prefix+"-legacy.json");
        clock.Restart();bool timedOut=false;
        try {await Run.Text(Environment.ProcessPath!,["--process-fixture","pipe-held",prefix+"-bounded.json"],1000);}
        catch(TimeoutException){timedOut=true;}
        Check(timedOut&&clock.Elapsed<TimeSpan.FromSeconds(3),"output EOF is outside the command budget");
        results["boundedOutputWaitMs"]=clock.Elapsed.TotalMilliseconds;
        await Reaped(prefix+"-bounded.json");
        Check((await Run.Text(Environment.ProcessPath!,["--process-fixture","mixed"])).Length==131072,"dual output draining lost data");
        const string escaped="space 中文 \"quote\" tail\\";
        Check(System.Text.Json.JsonSerializer.Deserialize<string>(await Run.Text(Environment.ProcessPath!,["--process-fixture","echo",escaped]))==escaped,"argument quoting or Unicode changed");
        bool rejected=false;
        try{await Run.Text(Environment.ProcessPath!,["--process-fixture","error"]);}catch(InvalidOperationException ex){rejected=ex.Message.Contains("0x0005")&&ex.Message.Contains("fixture-output");}
        Check(rejected,"command failure lost diagnostic output");
        rejected=false;
        try{await Run.Text(Environment.ProcessPath!,["--process-fixture","oversize"]);}catch(InvalidDataException){rejected=true;}
        Check(rejected,"oversized command output accepted");
        using(var cancel=new CancellationTokenSource()) {
            var receipt=prefix+"-cancel.json";
            var task=Run.Text(Environment.ProcessPath!,["--process-fixture","hang",receipt],cancel:cancel.Token);
            for(int i=0;i<40&&!File.Exists(receipt);i++)await Task.Delay(50);
            Check(File.Exists(receipt),"cancellation fixture did not start");
            clock.Restart();cancel.Cancel();bool cancelled=false;
            try{await task;}catch(OperationCanceledException){cancelled=true;}
            Check(cancelled&&clock.Elapsed<TimeSpan.FromSeconds(3),"user cancellation was not propagated");
            results["cancelAndReapMs"]=clock.Elapsed.TotalMilliseconds;await Reaped(receipt);
        }
        var calls=0;
        const string snapshot="--cloak-on\n--inv-off\n--dev-hide \"HID\\fixture\"\n--app-reg \"C:\\fixture.exe\"\n--cloak-on\n--inv-off\n";
        var hide=new HidHide(execute:(args,_)=>{
            calls++;Check(args.SequenceEqual(new[]{"--cloak-state","--inv-state","--dev-list","--app-list","--cloak-state","--inv-state","--cancel"}),"HidHide observation commands changed");
            return Task.FromResult(snapshot);
        });
        var state=await hide.Read();
        Check(calls==1&&state.Active&&state.Devices.Single()==@"HID\fixture","HidHide snapshot did not use one invocation");
        int attempt=0;
        var retry=new HidHide(execute:(_,_)=>++attempt==1?Task.FromException<string>(new InvalidOperationException("0x0005")):Task.FromResult(snapshot));
        await retry.Read();Check(attempt==2,"read-only snapshot lost targeted busy retry");
        using(var cancel=new CancellationTokenSource()) {
            attempt=0;
            var busy=new HidHide(cancel.Token,(_,_)=>{attempt++;cancel.Cancel();return Task.FromException<string>(new InvalidOperationException("0x0005"));});
            bool stopped=false;try{await busy.Read();}catch(OperationCanceledException){stopped=true;}
            Check(stopped&&attempt==1,"stop request continued retrying the driver CLI");
        }
        results["processAndHidHideChecks"]=true;
        var evidenceDirectory=prefix+"-recovery";Directory.CreateDirectory(evidenceDirectory);
        var journal=new Journal{BackendPid=123};
        Check(RecoveryDiagnostics.BackendOutputWarning(evidenceDirectory,journal) is not null,"missing neutral evidence claimed success");
        File.WriteAllText(Path.Combine(evidenceDirectory,"backend.log"),"Windows bridge started virtual=fixture\n");
        Check(RecoveryDiagnostics.BackendOutputWarning(evidenceDirectory,journal) is not null,"started backend without neutral proof claimed success");
        File.AppendAllText(Path.Combine(evidenceDirectory,"backend.log"),"shutdown neutral reports written; microphone close written\n");
        Check(RecoveryDiagnostics.BackendOutputWarning(evidenceDirectory,journal) is not null,"neutral output alone hid a missing light handoff");
        File.AppendAllText(Path.Combine(evidenceDirectory,"backend.log"),"shutdown lighting released; native light requested\n");
        Check(RecoveryDiagnostics.BackendOutputWarning(evidenceDirectory,journal) is null,"neutral output evidence lost");
        var evidence=RecoveryDiagnostics.Begin(evidenceDirectory,journal,stopSignaled:true);
        Check(evidence.BeforeStop is null&&evidence.AfterStop.Count==0,"creating recovery evidence performed an early device observation");
        results["recoveryEvidenceChecks"]=true;
        results["hardwareApisCalled"]=false;
        AtomicJson.Write(Path.Combine(Paths.State,"process-check.json"),results);
        return 0;
    }
}
