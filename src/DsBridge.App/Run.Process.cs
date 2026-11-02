using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
namespace DsBridge.App;

internal static partial class Run {
    const int MaxCommandCharacters=1024*1024;
    public static async Task<string> Text(string exe,string[] args,int timeout=8000,CancellationToken cancel=default) {
        cancel.ThrowIfCancellationRequested();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(timeout);
        using var job=new ChildJob();
        using var proc=Process.Start(Info(exe,args))??throw new InvalidOperationException("无法启动 "+Path.GetFileName(exe));
        Exception? readFailure=null;
        async Task<string> Capture(StreamReader reader) {
            try {
                var text=new StringBuilder();var buffer=new char[4096];int count;
                while((count=await reader.ReadAsync(buffer.AsMemory(),deadline.Token).ConfigureAwait(false))!=0) {
                    if(text.Length>MaxCommandCharacters-count)throw new InvalidDataException("外部命令输出超出检查范围。");
                    text.Append(buffer,0,count);
                }
                return text.ToString();
            }catch(Exception ex) {
                if(ex is not OperationCanceledException)Interlocked.CompareExchange(ref readFailure,ex,null);
                deadline.Cancel();throw;
            }
        }
        Task all=Task.CompletedTask;Task<string>? stdout=null,stderr=null;
        try {
            job.Add(proc);
            stdout=Capture(proc.StandardOutput);stderr=Capture(proc.StandardError);
            all=Task.WhenAll(stdout,stderr,proc.WaitForExitAsync());
            // The budget includes both process exit AND EOF. A descendant inheriting
            // stdout must not keep recovery waiting after the command itself exits.
            await all.WaitAsync(deadline.Token).ConfigureAwait(false);
        }catch(Exception ex) {
            deadline.Cancel();job.Dispose();
            try {if(!proc.HasExited)proc.Kill(entireProcessTree:true);}catch(InvalidOperationException) when(proc.HasExited){}
            try {await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);}
            catch(Exception cleanup){throw new IOException("外部命令退出未确认："+Path.GetFileName(exe),new AggregateException(ex,cleanup));}
            // Observe drain failures as well; never leave a faulted output task behind.
            try {await all.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);}catch{}
            if(readFailure is not null)ExceptionDispatchInfo.Capture(readFailure).Throw();
            cancel.ThrowIfCancellationRequested();
            if(ex is OperationCanceledException)throw new TimeoutException(Path.GetFileName(exe)+" 响应超时",ex);
            throw;
        }
        var output=await stdout!;var error=await stderr!;
        if(proc.ExitCode!=0)throw new InvalidOperationException(Path.GetFileName(exe)+" 失败: "+error+output);
        return output;
    }
}
