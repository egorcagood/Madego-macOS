using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Madyar.PeerChat;
namespace EgorVpn.Services;
public sealed class PeerActivationException(Exception inner):ArgumentException("Неверная ссылка контакта или параметры её открытия.",inner);
/// <summary>Local current-user IPC only. Acknowledgement means queued for preview, never imported.</summary>
public sealed class PeerContactActivation {
    readonly string _name;
    static readonly UTF8Encoding Utf8=new(false,true);
    const int MaxBytes=2048;
    public PeerContactActivation(string suffix) {
        if(!Regex.IsMatch(suffix,"^[A-Fa-f0-9]{20}$"))throw new ArgumentException("Invalid activation scope.");
        _name="MadyarVPN-peer-import-"+suffix;
    }
    public static string? PrepareStartup(string[] args,Action prepareStorage) {
        string? uri;
        try {uri=ParseArgs(args);}catch(Exception ex)when(ex is ArgumentException or InvalidDataException){throw new PeerActivationException(ex);}
        prepareStorage();return uri;
    }
    public static string? ParseArgs(string[] args) {
        if(!args.Any(a=>a.Equals("--import-contact",StringComparison.OrdinalIgnoreCase)))return null;
        if(args.Length!=2||args[0]!="--import-contact")throw new ArgumentException("Для импорта нужна одна ссылка после --import-contact.");
        var uri=args[1];ValidateUri(uri);return uri;
    }
    static void ValidateUri(string uri) {
        if(uri.Length>MaxBytes||uri.Any(c=>char.IsControl(c)||c=='"')||Utf8.GetByteCount(uri)>MaxBytes||!uri.StartsWith("madyar://contact/",StringComparison.Ordinal))throw new InvalidDataException("Неверная ссылка контакта.");
        try {_=PeerInviteCodec.Parse(uri);}
        catch(Exception ex)when(ex is JsonException or FormatException or InvalidOperationException or ArgumentException) {throw new InvalidDataException("Неверная ссылка контакта.",ex);}
    }
    public async Task<bool> TryForwardToRunningInstanceAsync(string uri,CancellationToken ct=default) {
        ValidateUri(uri);using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try {
            await using var pipe=new NamedPipeClientStream(".",_name,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            var bytes=Utf8.GetBytes(uri);var header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,bytes.Length);
            await pipe.WriteAsync(header,timeout.Token).ConfigureAwait(false);await pipe.WriteAsync(bytes,timeout.Token).ConfigureAwait(false);await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            var ack=new byte[1];await pipe.ReadExactlyAsync(ack,timeout.Token).ConfigureAwait(false);return ack[0]==1;
        }catch(Exception e)when(e is IOException or OperationCanceledException or TimeoutException or UnauthorizedAccessException){return false;}
    }
    public Task<IAsyncDisposable> StartReceiver(Func<string,Task> preview,CancellationToken ct=default)=>Task.FromResult<IAsyncDisposable>(new Receiver(_name,preview,ct));
    sealed class Receiver:IAsyncDisposable {
        readonly CancellationTokenSource _stop;
        readonly Channel<string> _queue=Channel.CreateBounded<string>(new BoundedChannelOptions(3){SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait});
        readonly string _name;
        readonly Func<string,Task> _preview;
        readonly Task _read,_deliver;
        NamedPipeServerStream? _server;
        public Receiver(string name,Func<string,Task> preview,CancellationToken ct) {
            _name=name;_preview=preview;_stop=CancellationTokenSource.CreateLinkedTokenSource(ct);
            _read=Listen();_deliver=Deliver();
        }
        async Task Listen() {
            try {
                while(!_stop.IsCancellationRequested) {
                    using var pipe=new NamedPipeServerStream(_name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                    _server=pipe;
                    try {
                        await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);deadline.CancelAfter(TimeSpan.FromSeconds(2));
                        var header=new byte[4];await pipe.ReadExactlyAsync(header,deadline.Token).ConfigureAwait(false);
                        var length=BinaryPrimitives.ReadInt32LittleEndian(header);if(length is <1 or >MaxBytes)continue;
                        var bytes=new byte[length];await pipe.ReadExactlyAsync(bytes,deadline.Token).ConfigureAwait(false);var uri=Utf8.GetString(bytes);ValidateUri(uri);
                        await pipe.WriteAsync(new byte[]{_queue.Writer.TryWrite(uri)?(byte)1:(byte)0},deadline.Token).ConfigureAwait(false);await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
                    } catch(Exception e)when(e is IOException or OperationCanceledException or InvalidDataException or ArgumentException or UnauthorizedAccessException) { }
                    finally {_server=null;}
                }
            }catch(Exception e)when(e is IOException or OperationCanceledException or ObjectDisposedException or UnauthorizedAccessException) { }
            finally {_queue.Writer.TryComplete();}
        }
        async Task Deliver() {
            try {await foreach(var uri in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false)){if(_stop.IsCancellationRequested)break;try {await _preview(uri).ConfigureAwait(false);}catch(OperationCanceledException){}catch(Exception){/* optional preview failure must not affect VPN */}}}
            catch(OperationCanceledException){}
        }
        public async ValueTask DisposeAsync() {
            _stop.Cancel();_queue.Writer.TryComplete();_server?.Dispose();
            await _read.ConfigureAwait(false);
            // Preview is closed by Chat lifecycle; never block application shutdown on a dialog.
            try {await _deliver.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);}catch(TimeoutException){}
        }
    }
}
