namespace DsBridge.App;

// Independent from Core Audio, IPC health queries, and diagnostics. Signals the backend
// immediately when the user stops or the UI disappears, even if a health call is stuck.
internal sealed class SessionStopMonitor : IAsyncDisposable {
    readonly CancellationTokenSource shutdown=new(),requested=new();
    readonly Task task;
    public CancellationToken Token=>requested.Token;
    public SessionStopMonitor(Func<bool> shouldStop,Action signal) {
        task=Task.Run(async()=>{
            try {
                while(!shutdown.IsCancellationRequested) {
                    if(shouldStop()) {signal();requested.Cancel();}
                    await Task.Delay(100,shutdown.Token);
                }
            }catch(OperationCanceledException) when(shutdown.IsCancellationRequested){}
        });
    }
    public async ValueTask DisposeAsync() {
        shutdown.Cancel();await task;shutdown.Dispose();requested.Dispose();
    }
}
