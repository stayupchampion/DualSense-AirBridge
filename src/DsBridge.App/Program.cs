using System.Diagnostics;
using DsBridge.Core;
namespace DsBridge.App;
internal static class Program {
    [STAThread] static int Main(string[] args) {
        try {
            if(args.Length>=2&&args[0]=="--process-fixture")return ProcessChecks.Fixture(args[1..]);
            if(args.Length==1&&args[0]=="--process-check")return ProcessChecks.CheckAll().GetAwaiter().GetResult();
            if(args.Length==1&&args[0]=="--audio-worker")return AudioWorker.Serve();
            if(args.Length==1&&args[0]=="--audio-read-worker")return AudioWorker.Serve(readOnly:true);
            if(args.Length==2&&args[0]=="--audio-worker-test"&&args[1] is "normal" or "hang" or "exit")return AudioWorker.Serve(args[1]);
            if(args.Length==2&&args[0]=="--audio-read-worker-test"&&args[1] is "normal" or "observe-hang")return AudioWorker.Serve(args[1],readOnly:true);
            if(args.Length==1&&args[0]=="--audio-isolation-check")return AudioWorkerChecks.Run().GetAwaiter().GetResult();
            if(args.Length==2&&args[0]=="--audio-owner-fixture")return AudioWorkerChecks.OwnerFixture(args[1]).GetAwaiter().GetResult();
            if(args.Length==3&&args[0]=="--volume-watch"&&int.TryParse(args[2],out var seconds)&&seconds is >=1 and <=120) {
                var path=Path.GetFullPath(args[1]);
                if(!path.StartsWith(Path.GetFullPath(Paths.State)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("诊断输出必须放在本工具 .local 目录内。");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var log=new StreamWriter(path,false,new System.Text.UTF8Encoding(false)){AutoFlush=true};
                var until=DateTime.UtcNow.AddSeconds(seconds);
                while(DateTime.UtcNow<until) {
                    log.WriteLine(System.Text.Json.JsonSerializer.Serialize(Audio.ReadVolumes()));
                    Thread.Sleep(250);
                }
                return 0;
            }
            if(args.Length==1&&args[0]=="--test-lease-probe") {
                try{using var lease=DeviceTestLease.Acquire();return 0;}
                catch(InvalidOperationException){return 2;}
            }
            if(args.Length==1 && args[0]=="--test-sleeper") { Thread.Sleep(30000);return 0; }
            if(args.Length==1 && args[0]=="--lifecycle-check") {
                Directory.CreateDirectory(Paths.State);
                var passed=ChildJob.SelfTest();
                AtomicJson.Write(Path.Combine(Paths.State,"lifecycle-check.json"),new {childJobKillsOwnedChild=passed});
                return passed?0:1;
            }
            if(args.Length==3 && args[0]=="--observe-recovery") {
                RecoveryDiagnostics.ObserveOnce(args[1],args[2]).GetAwaiter().GetResult();return 0;
            }
            if(args.Length==2 && args[0]=="--worker") return SessionWorker.Start(args[1]).GetAwaiter().GetResult();
            if(args.Length==2 && args[0]=="--recover") return SessionWorker.Recover(args[1]).GetAwaiter().GetResult();
            if(args.Length==3 && args[0]=="--diagnose" && args[1]=="--output") {
                var path=Path.GetFullPath(args[2]);
                if(!path.StartsWith(Path.GetFullPath(Paths.State)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("诊断输出必须放在本工具 .local 目录内。");
                AtomicJson.Write(path,Probe.Read().GetAwaiter().GetResult()); return 0;
            }
            if(args.Length>=2 && args[0]=="--feedback-test" && Enum.TryParse<FeedbackKind>(args[1],out var kind) && Enum.IsDefined(kind)) {
                var targets=FeedbackTests.Targets();
                if(targets.Length!=1)throw new InvalidOperationException("自动测试要求只有一个 USB DualSense 目标。");
                using var cancel=new CancellationTokenSource();
                if(args.Length==4&&args[2]=="--cancel-after"&&int.TryParse(args[3],out var milliseconds)&&milliseconds>0&&milliseconds<=5000)
                    cancel.CancelAfter(milliseconds);
                var result=FeedbackTests.RunAsync(targets[0],kind,cancel.Token).GetAwaiter().GetResult();
                AtomicJson.Write(Path.Combine(Paths.State,"feedback-cli-last.json"),new {kind=kind.ToString(),result});
                return 0;
            }
            if(args.Length==2&&args[0] is "--latency-read-check" or "--latency-haptics-check") {
                var targets=FeedbackTests.Targets();
                if(targets.Length!=1)throw new InvalidOperationException("读取检查需要唯一的 USB 或虚拟 USB DualSense。");
                var capture=new LatencyCapture(targets[0],args[0]=="--latency-haptics-check"?LatencyKind.Haptics:LatencyKind.Buttons,0);
                capture.RunAsync(_=>{},CancellationToken.None,2).GetAwaiter().GetResult();
                capture.Save(args[1]);return capture.Status=="completed"?0:1;
            }
            ApplicationConfiguration.Initialize();
            if(args.Length==2&&args[0]=="--latency-ui-check") {
                using var form=new LatencyForm();
                form.Shown+=async(_,_)=>{
                    await Task.Delay(700);
                    await form.RenderCheck(args[1]);
                    await form.CloseAfterCleanupAsync();
                };
                Application.Run(form);return 0;
            }
            if(args.Length==2 && args[0]=="--feedback-ui-check") {
                using var form=new FeedbackForm();
                form.Shown+=async(_,_)=>{
                    await Task.Delay(900);
                    using var bitmap=new Bitmap(form.Width,form.Height);
                    form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));
                    bitmap.Save(Path.GetFullPath(args[1]),System.Drawing.Imaging.ImageFormat.Png);
                    await form.CloseAfterCleanupAsync();
                };
                Application.Run(form);return 0;
            }
            using var singleton=new Mutex(true,@"Local\DsBridge-UI",out var first);
            if(!first) { MessageBox.Show("工具已经运行。请使用现有窗口。","DualSense AirBridge"); return 2; }
            Application.Run(new MainForm(args)); return 0;
        } catch(Exception ex) {
            Directory.CreateDirectory(Paths.State);
            File.WriteAllText(Path.Combine(Paths.State,"last-error.txt"),ex.ToString());
            if(args.Length==0) MessageBox.Show(ex.Message,"DualSense AirBridge",MessageBoxButtons.OK,MessageBoxIcon.Error);
            return 1;
        }
    }
}
