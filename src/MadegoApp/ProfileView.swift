import SwiftUI
import AppKit
struct ProfileView:View {
    @EnvironmentObject var store:AppStore
    @State private var nickname=""
    @State private var invite:Invite?
    @State private var backupExport:Bool?
    var body:some View {
        ScrollView {VStack(alignment:.leading,spacing:24) {
            Panel {VStack(alignment:.leading,spacing:16) {
                Text(store.tr("nickname")).font(.headline)
                HStack{TextField(store.tr("name"),text:$nickname).textFieldStyle(.roundedBorder);Button(store.tr("save")){Task{_ = await store.perform("profile.nickname",["name":nickname])}}.buttonStyle(.borderedProminent).disabled(nickname.trimmingCharacters(in:.whitespacesAndNewlines).isEmpty||nickname.count>40)}
                Text(store.tr("peerId")).font(.caption).foregroundStyle(.secondary)
                HStack{Text(store.snapshot?.chat.peerId ?? "").font(.system(.caption,design:.monospaced)).textSelection(.enabled).lineLimit(2);Spacer();Button{store.copy(store.snapshot?.chat.peerId ?? "")}label:{Image(systemName:"doc.on.doc")}.disabled(store.snapshot?.chat.peerId.isEmpty != false)}
            }}
            Panel {VStack(alignment:.leading,spacing:14){Text(store.tr("createInvite")).font(.headline);Text(store.tr("inviteInfo")).font(.callout).foregroundStyle(.secondary);Button(store.tr("createInvite")){Task{if let result=await store.perform("profile.invite"){invite=try? JSONDecoder().decode(Invite.self,from:result)}}}.disabled(store.snapshot?.chat.nickname.isEmpty != false);if let invite{HStack{Text(invite.code).font(.system(.caption2,design:.monospaced)).lineLimit(3);Button(store.tr("copy")){store.copy(invite.uri)}}};ForEach(store.snapshot?.chat.invites ?? []){issued in HStack{Text(String(issued.id.prefix(8))).font(.caption).foregroundStyle(.secondary);Spacer();Button(store.tr("revoke")){Task{_ = await store.perform("profile.revoke",["id":issued.id]);invite=nil}}}}}}
            Panel {VStack(alignment:.leading,spacing:14){Text(store.tr("backup")).font(.headline);Text(store.tr("backupInfo")).font(.callout).foregroundStyle(.secondary);HStack{Button(store.tr("exportBackup")){backupExport=true};Button(store.tr("restoreBackup")){backupExport=false}}}}
            if store.snapshot?.chat.blocked.isEmpty==false {Text(store.tr("blocked")).font(.headline);ForEach(store.snapshot?.chat.blocked ?? []){contact in HStack{Text(contact.name);Spacer();Button(store.tr("unblock")){Task{_ = await store.perform("contact.unblock",["peer":contact.peerId])}}}}}
        }.padding(28).frame(maxWidth:700).frame(maxWidth:.infinity)}
            .onAppear{nickname=store.snapshot?.chat.nickname ?? ""}
            .onChange(of:store.snapshot?.chat.nickname){_,new in if let new{nickname=new}}
            .sheet(isPresented:Binding(get:{backupExport != nil},set:{if !$0 {backupExport=nil}})){BackupSheet(export:backupExport ?? true).environmentObject(store)}
    }
}
struct BackupSheet:View {
    @EnvironmentObject var store:AppStore
    @Environment(\.dismiss) var dismiss
    var export:Bool
    @State private var password=""
    @State private var previewId:String?
    @State private var previewText=""
    @State private var confirm=false
    @State private var busy=false
    var body:some View {
        VStack(alignment:.leading,spacing:18) {
            Text(store.tr(export ? "exportBackup":"restoreBackup")).font(.title2.weight(.semibold))
            Text(store.tr("backupInfo")).font(.callout).foregroundStyle(.secondary)
            SecureField(store.tr("password"),text:$password).textFieldStyle(.roundedBorder)
            HStack{Spacer();if busy{ProgressView().controlSize(.small)};Button(store.tr("cancel")){Task{_ = await store.perform("backup.cancel");password="";dismiss()}};Button(store.tr(export ? "save":"file")){choose()}.buttonStyle(.borderedProminent).disabled(password.count<10||busy)}
        }.padding(28).frame(width:460)
        .alert(store.tr("restoreConfirm"),isPresented:$confirm){Button(store.tr("cancel"),role:.cancel){Task{_ = await store.perform("backup.cancel")}};Button(store.tr("restoreBackup"),role:.destructive){Task{if let previewId{_ = await store.perform("backup.restore",["id":previewId,"confirmed":true]);password="";dismiss()}}}}message:{Text(previewText)}
    }
    private func choose() {
        if export {let panel=NSSavePanel();panel.nameFieldStringValue="Madego-profile.mdybackup";if panel.runModal() == .OK,let url=panel.url {busy=true;Task{defer{busy=false};if await store.perform("backup.export",["path":url.path,"password":password]) != nil {password="";dismiss()}}}}
        else {let panel=NSOpenPanel();panel.allowsMultipleSelection=false;panel.canChooseDirectories=false;if panel.runModal() == .OK,let url=panel.url {busy=true;Task{defer{busy=false};if let result=await store.perform("backup.preview",["path":url.path,"password":password]),let object=try? JSONSerialization.jsonObject(with:result) as? [String:Any],let id=object["id"] as? String {previewId=id;previewText=(object["name"] as? String ?? "")+"\n"+(object["peerId"] as? String ?? "");confirm=true}}}}
    }
}
