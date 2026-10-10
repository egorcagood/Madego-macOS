using Madyar.PeerChat;
namespace PeerChatChecks;
internal static partial class Program
{
 static void SetupProfileTests()
 {
  Tests.Add(("setup","FreshInstallerExportUsesExplicitOwnNameWithoutOptIn",async()=>{
   var path=Temp();await using var w=await PeerWorkspace.OpenAsync(path,new TestProtector());var before=await w.ReadProfileAsync();
   var output=Path.Combine(Path.GetDirectoryName(path)!,"public.json");
   await PeerSetupProfile.ExportPublicContactAsync(w,output,"  Егор Мадяр  ");
   var contact=(await PeerClientPackage.ReadPublicContactAsync(output))!;var after=await w.ReadProfileAsync();
   Check(contact.Invite.Nickname=="Егор Мадяр"&&after.Nickname=="Егор Мадяр"&&after.PeerId==before.PeerId&&contact.Invite.PeerId==before.PeerId,"Installer export lost explicit name or own identity");
   Check(!await w.ReadLinkEnabledAsync()&&(await w.ListContactsAsync()).Count==0&&(await w.ListIssuedInvitesAsync()).Any(i=>i.Id==contact.Invite.InviteId.ToString("D")),"Naming/export silently opted in or omitted durable invite");
  }));
  Tests.Add(("setup","RecipientSetupNameAppliesOnceWithoutIdentityOrNetworkChanges",async()=>{
   var path=Temp();var install=Path.Combine(Path.GetDirectoryName(path)!,"install");Directory.CreateDirectory(install);
   await using var w=await PeerWorkspace.OpenAsync(path,new TestProtector());var id=(await w.ReadProfileAsync()).PeerId;
   PeerSetupProfile.WritePendingName(install,"Эдик");await PeerSetupProfile.ApplyPendingNameAsync(install,w);
   Check((await w.ReadProfileAsync()).Nickname=="Эдик"&&!File.Exists(Path.Combine(install,PeerSetupProfile.PendingNameFile)),"Explicit installation name not consumed");
   await w.SetNicknameAsync("Новое имя");await PeerSetupProfile.ApplyPendingNameAsync(install,w);
   Check((await w.ReadProfileAsync()).Nickname=="Новое имя"&&(await w.ReadProfileAsync()).PeerId==id&&!await w.ReadLinkEnabledAsync(),"Old installer request overwrote later name, replaced identity or enabled chat");
  }));
  Tests.Add(("setup","RepeatedInstallNameOverridesLocalNicknameAndPreservesConsentIdentity",async()=>{
   var path=Temp();var install=Path.Combine(Path.GetDirectoryName(path)!,"install");Directory.CreateDirectory(install);
   await using var w=await PeerWorkspace.OpenAsync(path,new TestProtector());await w.SetNicknameAsync("Первое имя");await w.SetLinkEnabledAsync(true);var before=await w.ReadProfileAsync();
   PeerSetupProfile.WritePendingName(install,"Имя ноутбука");await PeerSetupProfile.ApplyPendingNameAsync(install,w);
   Check((await w.ReadProfileAsync()).Nickname=="Имя ноутбука"&&(await w.ReadProfileAsync()).PeerId==before.PeerId&&await w.ReadLinkEnabledAsync(),"Reinstall reused old local name or reset profile consent");
   PeerSetupProfile.WritePendingName(install,"Третье имя");await PeerSetupProfile.ApplyPendingNameAsync(install,w);
   Check((await w.ReadProfileAsync()).Nickname=="Третье имя"&&!File.Exists(Path.Combine(install,PeerSetupProfile.PendingNameFile)),"Second explicit install request was not applied");
  }));
  Tests.Add(("setup","SetupNameRejectsUnsafeInputBeforeProfileMutation",async()=>{
   var path=Temp();await using var w=await PeerWorkspace.OpenAsync(path,new TestProtector());await w.SetNicknameAsync("Сохранённое");
   var output=Path.Combine(Path.GetDirectoryName(path)!,"public.json");
   foreach(var name in new[]{"",new string('x',41),"bad\nname","bad\u0000name","\ud800"}) {
    try {await PeerSetupProfile.ExportPublicContactAsync(w,output,name);throw new TestFailure("Invalid nickname exported");}catch(InvalidDataException){}
   }
   Check((await w.ReadProfileAsync()).Nickname=="Сохранённое"&&!File.Exists(output)&&(await w.ListIssuedInvitesAsync()).Count==0,"Rejected nickname mutated local state");
   var encoded=PeerSetupProfile.EncodeName("Эдик ' \" $() 🕊");Check(PeerSetupProfile.DecodeName(encoded)=="Эдик ' \" $() 🕊","Encoded command nickname changed Unicode or shell punctuation");
   try {PeerSetupProfile.DecodeName("not base64");throw new TestFailure("Invalid encoded nickname accepted");}catch(InvalidDataException){}
  }));
  Tests.Add(("setup","CancelledSetupNameKeepsPendingFileAndPriorProfile",async()=>{
   var path=Temp();var install=Path.Combine(Path.GetDirectoryName(path)!,"install");Directory.CreateDirectory(install);
   await using var w=await PeerWorkspace.OpenAsync(path,new TestProtector());await w.SetNicknameAsync("Старое");PeerSetupProfile.WritePendingName(install,"Новое");
   using var stop=new CancellationTokenSource();stop.Cancel();try{await PeerSetupProfile.ApplyPendingNameAsync(install,w,stop.Token);throw new TestFailure("Cancellation ignored");}catch(OperationCanceledException){}
   Check((await w.ReadProfileAsync()).Nickname=="Старое"&&File.Exists(Path.Combine(install,PeerSetupProfile.PendingNameFile)),"Cancelled apply lost pending request or modified name");
   await PeerSetupProfile.ApplyPendingNameAsync(install,w);Check((await w.ReadProfileAsync()).Nickname=="Новое","Retry after cancellation failed");
  }));
  Tests.Add(("setup","InvalidPendingSetupDocumentCannotChangeProfile",async()=>{
   var path=Temp();var install=Path.Combine(Path.GetDirectoryName(path)!,"install");Directory.CreateDirectory(install);
   await using var w=await PeerWorkspace.OpenAsync(path,new TestProtector());await w.SetNicknameAsync("Прежнее");
   foreach(var json in new[]{"{\"schemaVersion\":2,\"nickname\":\"Неверное\"}","{\"schemaVersion\":1,\"nickname\":\"Неверное\",\"nickname\":\"Дубликат\"}","{\"schemaVersion\":1,\"nickname\":\"Неверное\",\"seed\":\"secret\"}"}) {
    File.WriteAllText(Path.Combine(install,PeerSetupProfile.PendingNameFile),json);
    try{await PeerSetupProfile.ApplyPendingNameAsync(install,w);throw new TestFailure("Malformed setup document accepted");}catch(InvalidDataException){}
    Check((await w.ReadProfileAsync()).Nickname=="Прежнее"&&!await w.ReadLinkEnabledAsync(),"Malformed setup document mutated name or consent");
   }
  }));
 }
}
