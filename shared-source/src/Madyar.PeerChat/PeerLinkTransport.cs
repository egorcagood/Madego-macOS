namespace Madyar.PeerChat;
public sealed record PeerLinkEvent(string Op,string Peer="",string Id="",string Kind="",string Text="",string Receipt="",bool Online=false,bool Delivered=false,string Detail="",string Invite="");
public interface IPeerIntroductionTransport {
 Task UpdateInvitesAsync(IReadOnlyList<PeerIssuedInvite> invites,CancellationToken ct);
 Task SendIntroductionAsync(string id,string recipient,string targetInviteId,string signedReturnUri,CancellationToken ct);
}
public interface IPeerLinkTransport : IAsyncDisposable {
 Func<PeerLinkEvent,Task>? HandleEvent {get;set;}
 Task StartAsync(PeerLinkCredentials identity,IReadOnlyList<PeerContact> contacts,CancellationToken ct);
 Task UpdateContactsAsync(IReadOnlyList<PeerContact> contacts,CancellationToken ct);
 Task SendAsync(PeerMessage message,CancellationToken ct);
 Task AcceptAsync(string receipt,bool accepted,CancellationToken ct);
 Task StopAsync();
}
