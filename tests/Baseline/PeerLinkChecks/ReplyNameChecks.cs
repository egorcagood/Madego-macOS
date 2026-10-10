using Madyar.PeerChat;
internal static class ReplyNameChecks
{
    // Executes the real controllers, signed invite parser and SQLite request/ACK path.
    // A transport double replaces only the physical socket for deterministic regression.
    internal static async Task Run()
    {
        var root=Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"madyar-reply-name-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var sender=await PeerWorkspace.OpenAsync(Path.Combine(root,"sender"),new Plain());
        await using var issuer=await PeerWorkspace.OpenAsync(Path.Combine(root,"issuer"),new Plain());
        await issuer.SetNicknameAsync("Автор");var target=PeerInviteCodec.Parse((await issuer.CreateInviteAsync()).Uri);
        var own=(await sender.ReadProfileAsync()).PeerId;
        var ts=new IntroductionFixture();var ti=new IntroductionFixture();ts.Other=ti;ti.Other=ts;
        await using var cs=new PeerLinkController(sender,()=>ts);await using var ci=new PeerLinkController(issuer,()=>ti);
        await ci.EnableAsync();await cs.EnableAsync();
        Check((await sender.ReadProfileAsync()).Nickname=="","Fresh sender already had a nickname");
        Check(await SendNamed(cs,target,"Получатель",CancellationToken.None),"Reply was not acknowledged");
        var request=(await issuer.ListContactRequestsAsync()).Single();
        Check(request.Nickname=="Получатель"&&request.PeerId==own&&(await sender.ReadProfileAsync()).Nickname=="Получатель"&&(await sender.ReadProfileAsync()).PeerId==own,"Reply did not persist explicit name under own identity");
        Console.WriteLine("PASS Fresh reply saves entered name before generating signed return invite and durable ACK");
        foreach(var name in new[]{"",new string('x',41),"\ud800"}) {
            try{await SendNamed(cs,target,name,CancellationToken.None);throw new Exception("Invalid nickname sent");}catch(InvalidDataException){}
        }
        Check((await sender.ReadProfileAsync()).Nickname=="Получатель"&&(await issuer.ListContactRequestsAsync()).Single()==request,"Invalid name changed profile or remote request");
        Console.WriteLine("PASS Invalid reply name cannot mutate profile or deliver a request");
        using var stop=new CancellationTokenSource();stop.Cancel();
        try{await SendNamed(cs,target,"Отменено",stop.Token);throw new Exception("Cancellation ignored");}catch(OperationCanceledException){}
        Check((await sender.ReadProfileAsync()).Nickname=="Получатель","Cancelled reply saved new name");
        Console.WriteLine("PASS Cancelled reply preserves prior profile name");
        await cs.DisableAsync();
        try{await SendNamed(cs,target,"Выключено",CancellationToken.None);throw new Exception("Disabled chat accepted reply");}catch(InvalidOperationException){}
        Check((await sender.ReadProfileAsync()).Nickname=="Получатель"&&!await sender.ReadLinkEnabledAsync(),"Disabled reply changed name or opt-in");
        Console.WriteLine("PASS Disabled reply neither changes profile nor revives opt-in");
        Console.WriteLine("Reply-name checks: 4 passed");
    }
    static Task<bool> SendNamed(PeerLinkController link,VerifiedContactInvite target,string name,CancellationToken ct)=>link.SendIntroductionAsync(target,name,ct);
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
}
