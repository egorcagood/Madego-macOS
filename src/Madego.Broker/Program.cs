using Madego.Broker;
PrivateFiles.SetProcessMask();
using var life=new CancellationTokenSource();
Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;life.Cancel();};
try {
    string? testRoot=null;string? service=null;string? helper=null;
    for(int i=0;i<args.Length;i+=2){if(i+1==args.Length)throw new ArgumentException();switch(args[i]){case "--data-root":testRoot=args[i+1];break;case "--keychain-service":service=args[i+1];break;case "--helper":helper=args[i+1];break;default:throw new ArgumentException();}}
    var paths=MacPaths.Resolve(testRoot);helper??=Path.Combine(AppContext.BaseDirectory,"MadyarLink");
    var keys=new KeychainKeyStore(service??"dev.madego.macOS",()=>Directory.Exists(paths.Peer)||File.Exists(Path.Combine(paths.Support,"subscriptions.secure")));
    var ipc=new IpcServer();await using var peers=new PeerService(paths,keys,helper,ipc,life.Token);var app=new AppService(paths,keys,peers);
    // Receive requests immediately, including disable/cancel while saved chat intent is being restored.
    var run=ipc.RunAsync(Console.OpenStandardInput(),Console.OpenStandardOutput(),app.DispatchAsync,life.Token);
    async Task MaintainAsync(){try{try{await peers.RestoreIntentAsync(life.Token);}catch(OperationCanceledException) when(!life.IsCancellationRequested){}
        using var timer=new PeriodicTimer(TimeSpan.FromMinutes(1));while(await timer.WaitForNextTickAsync(life.Token)){await app.RefreshDueAsync(life.Token);await ipc.EventAsync("changed",new{},life.Token);}}catch(OperationCanceledException){}catch(Exception){try{await ipc.EventAsync("attention",new{code="restore_or_refresh_failed"},life.Token);}catch(Exception){}}}
    var maintenance=MaintainAsync();await run;life.Cancel();await maintenance;
}catch(OperationCanceledException){}catch(Exception){Environment.ExitCode=1;Console.Error.WriteLine("Madego broker stopped; local data was preserved.");}
