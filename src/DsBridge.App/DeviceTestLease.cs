namespace DsBridge.App;

// All test entry points share a short-lived kernel semaphore. Unlike a Mutex,
// it can be released by a different continuation thread after an await.
internal sealed class DeviceTestLease:IDisposable {
    Semaphore? semaphore;
    DeviceTestLease(Semaphore owned)=>semaphore=owned;
    public static DeviceTestLease Acquire() {
        var gate=new Semaphore(1,1,@"Local\DsBridge-HardwareTests");
        try {
            if(!gate.WaitOne(0))throw new InvalidOperationException("另一个窗口或进程正在测试手柄，请先停止该测试。");
            return new(gate);
        }catch{gate.Dispose();throw;}
    }
    public void Dispose() {
        var owned=Interlocked.Exchange(ref semaphore,null);
        if(owned is null)return;
        try{owned.Release();}finally{owned.Dispose();}
    }
}
