using Madego.Broker;
using System.Text;
using System.Threading.Channels;
static class IpcChecks
{
    public static async Task RunAsync()
    {
        var input=new QueueInput();using var output=new MemoryStream();using var life=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server=new IpcServer();var sendStarted=new TaskCompletionSource();var disabled=new TaskCompletionSource();var hold=new TaskCompletionSource();
        async Task<object> Dispatch(IpcRequest request,CancellationToken ct) {
            if(request.Method=="send"){sendStarted.TrySetResult();await hold.Task.WaitAsync(ct);return new{sent=false};}
            if(request.Method=="chat.disable"){hold.TrySetResult();disabled.TrySetResult();return new{disabled=true};}
            throw new InvalidDataException("PRIVATE-URL-and-SEED-must-not-leak");
        }
        var run=server.RunAsync(input,output,Dispatch,life.Token);
        input.Send("{\"id\":\"send1\",\"method\":\"send\"}\n");await sendStarted.Task.WaitAsync(life.Token);
        input.Send("{\"id\":\"off\",\"method\":\"chat.disable\"}\n{\"id\":\"bad\",\"method\":\"fault\"}\n");
        await disabled.Task.WaitAsync(TimeSpan.FromSeconds(1));await Task.Delay(100,life.Token);input.Complete();await run;
        var text=Encoding.UTF8.GetString(output.ToArray());if(!text.Contains("disabled")||text.Contains("PRIVATE-URL"))throw new Exception("IPC opt-out concurrent/no raw fault");
        var large=new QueueInput();using var largeOutput=new MemoryStream();int called=0;
        var largeRun=new IpcServer().RunAsync(large,largeOutput,(_,_)=>{called++;return Task.FromResult<object>(new{});},life.Token);
        large.Send("{\"id\":\"big\",\"method\":\"fault\",\"params\":{\"text\":\""+new string('\u2603',50000)+"\"}}\n");large.Complete();
        bool rejected=false;try{await largeRun;}catch(InvalidDataException){rejected=true;}
        if(!rejected||called!=0)throw new Exception("UTF8 IPC frame byte bound");
        Console.WriteLine("IPC_CHECKS_PASS=3");
    }
    sealed class QueueInput:Stream {
        readonly Channel<byte[]> queue=Channel.CreateUnbounded<byte[]>();byte[] pending=[];int offset;
        public void Send(string text)=>queue.Writer.TryWrite(Encoding.UTF8.GetBytes(text));public void Complete()=>queue.Writer.Complete();
        public override async ValueTask<int> ReadAsync(Memory<byte> destination,CancellationToken ct=default) {
            if(offset==pending.Length){if(!await queue.Reader.WaitToReadAsync(ct))return 0;pending=await queue.Reader.ReadAsync(ct);offset=0;}
            int n=Math.Min(destination.Length,pending.Length-offset);pending.AsMemory(offset,n).CopyTo(destination);offset+=n;return n;
        }
        public override int Read(byte[] b,int o,int n)=>ReadAsync(b.AsMemory(o,n)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override void Flush(){}public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long n)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int n)=>throw new NotSupportedException();
    }
}
