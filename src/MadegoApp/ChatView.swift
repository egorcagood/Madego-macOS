import SwiftUI
struct ChatView:View {
    @EnvironmentObject var store:AppStore
    @State private var showAdd=false
    @State private var invite=""
    @State private var showShare=false
    var body:some View {
        VStack(spacing:0) {
            HStack {Circle().fill(store.snapshot?.chat.enabled==true ? Theme.accent:Color.secondary).frame(width:6,height:6);Text(chatState).font(.caption).foregroundStyle(.secondary);Spacer();Button{store.profile()}label:{Label(store.tr("profile"),systemImage:"person.crop.circle")};Button(store.tr(store.snapshot?.chat.enabled==true ? "disableChat":"enableChat")){Task{_ = await store.perform(store.snapshot?.chat.enabled==true ? "chat.disable":"chat.enable")}}.buttonStyle(.bordered)}.padding(20)
            Divider()
            if store.snapshot?.chat.enabled != true && (store.snapshot?.chat.contacts.isEmpty != false) {EmptyState(icon:"bubble.left.and.bubble.right",title:store.tr("chatOff"),subtitle:store.tr("chatOffBody"))}
            else {
                HStack(spacing:0) {
                    VStack(alignment:.leading,spacing:14) {
                        HStack{Text(store.tr("contacts")).font(.headline);Spacer();Button{showAdd=true}label:{Image(systemName:"plus")}.help(store.tr("addContact"))}
                        ScrollView {LazyVStack(alignment:.leading,spacing:6) {
                            ForEach(store.snapshot?.chat.requests ?? []){request in VStack(alignment:.leading,spacing:8){Text(request.name).font(.callout.weight(.semibold));HStack{Button(store.tr("accept")){Task{_ = await store.perform("contact.accept",["peer":request.peerId])}};Button(store.tr("reject")){Task{_ = await store.perform("contact.reject",["peer":request.peerId])}}}.controlSize(.small)}.padding(10).background(Theme.panel,in:RoundedRectangle(cornerRadius:10))}
                            ForEach(store.snapshot?.chat.contacts ?? []){contact in
                                Button {Task{await store.choosePeer(contact.peerId)}}label:{HStack(spacing:10){Text(String(contact.name.prefix(1)).uppercased()).font(.headline).frame(width:30,height:30).background(Theme.panel,in:Circle());VStack(alignment:.leading,spacing:3){Text(contact.name).lineLimit(1);Text(store.tr(contact.online ? "online":"offline")).font(.caption2).foregroundStyle(.secondary)};Spacer()}.padding(10).contentShape(Rectangle())}.buttonStyle(.plain).background(store.selectedPeer==contact.peerId ? Theme.accent.opacity(0.1):Color.clear,in:RoundedRectangle(cornerRadius:10))
                                    .contextMenu{Button(store.tr("block"),role:.destructive){Task{_ = await store.perform("contact.block",["peer":contact.peerId]);if store.selectedPeer==contact.peerId{store.selectedPeer=nil;store.messages=[]}}};Button(store.tr("remove"),role:.destructive){Task{_ = await store.perform("contact.remove",["peer":contact.peerId]);if store.selectedPeer==contact.peerId{store.selectedPeer=nil;store.messages=[]}}}}
                            }
                        }}
                        if store.snapshot?.chat.contacts.isEmpty != false {Text(store.tr("emptyContacts")).font(.caption).foregroundStyle(.secondary)}
                    }.padding(16).frame(width:230)
                    Divider()
                    VStack(spacing:0) {
                        if let peer=store.selectedPeer,let contact=store.snapshot?.chat.contacts.first(where:{$0.peerId==peer}) {
                            HStack{Text(contact.name).font(.headline);Spacer();Button{showShare=true}label:{Label(store.tr("share"),systemImage:"paperplane")}.disabled(store.snapshot?.chat.enabled != true);Menu{Button(store.tr("clearHistory"),role:.destructive){Task{_ = await store.perform("messages.clear",["peer":peer]);await store.loadMessages()}}}label:{Image(systemName:"ellipsis")}}.padding(18)
                            Divider()
                            ScrollViewReader {scroll in
                                ScrollView {LazyVStack(alignment:.leading,spacing:12){ForEach(store.messages){message in MessageBubble(message:message)}}.padding(20)}
                                    .onChange(of:store.messages.count){_,_ in if let last=store.messages.last{scroll.scrollTo(last.id,anchor:.bottom)}}
                            }
                            Divider()
                            HStack(alignment:.bottom,spacing:10) {
                                TextField(store.tr("message"),text:$store.draft,axis:.vertical).textFieldStyle(.plain).lineLimit(1...4).padding(12).background(Theme.panel,in:RoundedRectangle(cornerRadius:12)).onSubmit{Task{await store.send()}}
                                Button{Task{await store.send()}}label:{Image(systemName:"arrow.up").font(.headline).frame(width:34,height:34)}.buttonStyle(.borderedProminent).disabled(store.snapshot?.chat.enabled != true||store.draft.isEmpty||store.draft.utf16.count>2000||store.sending)
                            }.padding(16)
                        } else {EmptyState(icon:"bubble.left",title:store.tr("chooseContact"),subtitle:store.tr("chatOffBody"))}
                    }.frame(maxWidth:.infinity)
                }
            }
        }
        .sheet(isPresented:$showAdd) {VStack(alignment:.leading,spacing:16){Text(store.tr("addContact")).font(.title2.weight(.semibold));Text(store.tr("contactHint")).font(.callout).foregroundStyle(.secondary);TextEditor(text:$invite).frame(height:100).font(.system(.caption,design:.monospaced));HStack{Spacer();Button(store.tr("cancel")){showAdd=false};Button(store.tr("add")){Task{showAdd=false;_ = await store.perform("contact.add",["invite":invite]);invite=""}}.buttonStyle(.borderedProminent).disabled(invite.isEmpty)}}.padding(28).frame(width:480)}
        .sheet(isPresented:$showShare){VStack(alignment:.leading,spacing:16){Text(store.tr("share")).font(.title2);ForEach(store.snapshot?.app.nodes ?? []){node in Button(node.flag+" "+node.name){Task{showShare=false;if let peer=store.selectedPeer{_ = await store.perform("server.share",["id":node.id,"peer":peer]);await store.loadMessages()}}}};Button(store.tr("cancel")){showShare=false}}.padding(28).frame(width:400)}
    }
    private var chatState:String {switch store.snapshot?.chat.state {case "Ready":store.tr("ready");case "Connecting":store.tr("starting");case "Unavailable":store.tr("unavailable");default:store.tr("chatOff")}}
}
struct MessageBubble:View {
    @EnvironmentObject var store:AppStore
    let message:Message
    var body:some View {
        HStack {
            if message.outgoing {Spacer(minLength:70)}
            VStack(alignment:.leading,spacing:8) {
                if message.kind=="server" {
                    Label(store.tr("serverReceived"),systemImage:"network").font(.callout.weight(.medium))
                    if !message.outgoing {Button(store.tr("importServer")){Task{_ = await store.perform("server.import",["uri":message.text])}}.buttonStyle(.bordered)}
                } else {Text(message.text).font(.system(size:14)).textSelection(.enabled)}
                if message.outgoing {HStack(spacing:8){Text(store.tr(message.status=="Delivered" ? "delivered":message.status=="Pending" ? "pending":"failed")).font(.caption2).foregroundStyle(.secondary);if message.status=="Failed" {Button(store.tr("retry")){Task{_ = await store.perform("messages.retry",["peer":message.peerId,"id":message.id]);await store.loadMessages()}}.font(.caption2).buttonStyle(.plain).foregroundStyle(Theme.accent)}}}
            }.padding(14).background(message.outgoing ? Theme.accent.opacity(0.12):Theme.panel,in:RoundedRectangle(cornerRadius:14)).frame(maxWidth:440,alignment:message.outgoing ? .trailing:.leading)
            if !message.outgoing {Spacer(minLength:70)}
        }.id(message.id)
    }
}
