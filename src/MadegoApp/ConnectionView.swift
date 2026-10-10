import SwiftUI
struct ConnectionView:View {
    @EnvironmentObject var store:AppStore
    @Binding var showImport:Bool
    @State private var search=""
    @State private var group="*"
    var selected:Node? {store.snapshot?.app.nodes.first{$0.id==store.snapshot?.app.selectedId}}
    var nodes:[Node] {(store.snapshot?.app.nodes ?? []).filter{(group=="*"||$0.groupId==group)&&(search.isEmpty||$0.name.localizedCaseInsensitiveContains(search)||$0.country.localizedCaseInsensitiveContains(search))}}
    var body:some View {
        HStack(alignment:.top,spacing:24) {
            VStack(spacing:18) {
                Panel {
                    VStack(spacing:22) {
                        ZStack {Circle().stroke(Theme.accent.opacity(0.1),lineWidth:1).frame(width:148,height:148);Circle().fill(Theme.accent.opacity(0.035)).frame(width:128,height:128);Image(systemName:"power").font(.system(size:45,weight:.ultraLight)).foregroundStyle(Theme.accent)}.padding(.top,12)
                        Text(store.tr("disconnected")).font(.title2.weight(.medium))
                        if let selected {Text(selected.flag+" "+selected.name).font(.callout).lineLimit(2).multilineTextAlignment(.center)}else {Text(store.tr("noSelection")).font(.callout).foregroundStyle(.secondary)}
                        Button {Task{_ = await store.perform("vpn.connect")}}label:{Text(store.tr("connect")).font(.headline).frame(maxWidth:.infinity).padding(.vertical,7)}.buttonStyle(.borderedProminent).disabled(selected==nil)
                        Picker("Mode",selection:Binding(get:{store.snapshot?.app.mode ?? "FullVpn"},set:{value in Task{_ = await store.perform("settings",["mode":value])}})){Text("Full VPN").tag("FullVpn");Text("RU Mode").tag("RuMode")}.pickerStyle(.segmented).labelsHidden()
                        Text(store.tr("vpnBuildLimit")).font(.caption).foregroundStyle(.secondary).multilineTextAlignment(.center)
                    }.frame(maxWidth:.infinity)
                }
                if store.snapshot?.app.mode=="RuMode" {Text(store.tr("ruInfo")).font(.callout).foregroundStyle(.secondary).padding(.horizontal,12)}
                Spacer()
            }.frame(width:280)
            VStack(alignment:.leading,spacing:16) {
                HStack{Text(store.tr("servers")).font(.headline);Spacer();Text("\(nodes.count)").foregroundStyle(.secondary).font(.caption)}
                TextField(store.tr("search"),text:$search).textFieldStyle(.roundedBorder)
                if (store.snapshot?.app.groups.count ?? 0)>1 {Picker("",selection:$group){Text(store.tr("all")).tag("*");ForEach(store.snapshot?.app.groups ?? []){g in Text(g.name).tag(g.id)}}.labelsHidden()}
                if nodes.isEmpty {
                    EmptyState(icon:"rectangle.stack.badge.plus",title:store.tr("emptyServers"),subtitle:store.tr("emptyServersBody"))
                    Button(store.tr("addSubscription")){showImport=true}.buttonStyle(.bordered).frame(maxWidth:.infinity)
                }else {
                    ScrollView {LazyVStack(spacing:7){ForEach(nodes){node in
                        Button {Task{_ = await store.perform("server.select",["id":node.id])}}label:{
                            HStack(spacing:12){Text(node.flag).font(.title2);VStack(alignment:.leading,spacing:5){Text(node.name).font(.callout.weight(.medium)).lineLimit(1);Text(node.protocol.uppercased()+" · "+node.transport.uppercased()+" · "+node.security.uppercased()).font(.caption2).foregroundStyle(.secondary)};Spacer();Image(systemName:store.snapshot?.app.selectedId==node.id ? "checkmark.circle.fill":"circle").foregroundStyle(store.snapshot?.app.selectedId==node.id ? Theme.accent:Color.secondary)}.padding(14).contentShape(Rectangle())
                        }.buttonStyle(.plain).background(Theme.panel,in:RoundedRectangle(cornerRadius:12)).overlay(RoundedRectangle(cornerRadius:12).stroke(store.snapshot?.app.selectedId==node.id ? Theme.accent.opacity(0.35):Theme.line))
                    }}}
                }
            }.frame(maxWidth:.infinity,maxHeight:.infinity)
        }.padding(28)
    }
}
