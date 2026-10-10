import SwiftUI
import AppKit
struct SubscriptionsView:View {
    @EnvironmentObject var store:AppStore
    @Binding var showImport:Bool
    var body:some View {
        VStack(alignment:.leading,spacing:20) {
            HStack{Text(store.tr("emptyServersBody")).font(.callout).foregroundStyle(.secondary);Spacer();Button(store.tr("add")){showImport=true}.buttonStyle(.borderedProminent)}
            if store.snapshot?.app.groups.isEmpty != false {EmptyState(icon:"rectangle.stack",title:store.tr("emptyServers"),subtitle:store.tr("emptyServersBody"))}
            else {ScrollView{VStack(spacing:12){ForEach(store.snapshot?.app.groups ?? []){group in
                Panel {HStack{VStack(alignment:.leading,spacing:8){Text(group.name).font(.headline);Text("\(group.count) "+store.tr("nodes")+" · "+(group.local ? store.tr("local"):store.tr("subscriptions"))).font(.caption).foregroundStyle(.secondary);if let used=group.usedBytes,let total=group.totalBytes {Text(ByteCountFormatter.string(fromByteCount:used,countStyle:.binary)+" / "+ByteCountFormatter.string(fromByteCount:total,countStyle:.binary)).font(.caption2).foregroundStyle(.secondary)}};Spacer();if !group.local {Button(store.tr("refresh")){Task{_ = await store.perform("subscription.refresh",["id":group.id])}}};Menu{Button(store.tr("remove"),role:.destructive){Task{_ = await store.perform("subscription.remove",["id":group.id])}}}label:{Image(systemName:"ellipsis")}}
                }
            }}}}
            Spacer()
        }.padding(28)
    }
}
struct ImportSheet:View {
    @EnvironmentObject var store:AppStore
    @Environment(\.dismiss) var dismiss
    @State private var name=""
    @State private var input=""
    var body:some View {
        VStack(alignment:.leading,spacing:18) {
            Text(store.tr("addSubscription")).font(.title2.weight(.semibold))
            TextField(store.tr("optionalName"),text:$name).textFieldStyle(.roundedBorder)
            Text(store.tr("importHint")).foregroundStyle(.secondary).font(.callout)
            TextEditor(text:$input).font(.system(.body,design:.monospaced)).frame(height:180).scrollContentBackground(.hidden).padding(8).background(Theme.background,in:RoundedRectangle(cornerRadius:10))
            HStack {Button(store.tr("file")){openFile()};Spacer();if store.importing{ProgressView().controlSize(.small)};Button(store.tr("cancel")){dismiss()};Button(store.tr("import")){Task{if await store.importText(input,name){dismiss()}}}.buttonStyle(.borderedProminent).disabled(input.isEmpty||store.importing)}
        }.padding(28).frame(width:540).background(Theme.panel)
    }
    private func openFile(){let panel=NSOpenPanel();panel.canChooseDirectories=false;panel.allowsMultipleSelection=false;if panel.runModal() == .OK,let url=panel.url {do{let info=try url.resourceValues(forKeys:[.fileSizeKey]);guard (info.fileSize ?? 0)<=128*1024 else {store.error=store.tr("input_too_large");return};input=try String(contentsOf:url,encoding:.utf8)}catch{store.show(error)}}}
}
