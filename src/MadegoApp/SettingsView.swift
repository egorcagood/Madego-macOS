import SwiftUI
struct SettingsView:View {
    @EnvironmentObject var store:AppStore
    @State private var search=""
    @State private var domain=""
    @State private var match="Suffix"
    @State private var route="Direct"
    var body:some View {
        ScrollView {VStack(alignment:.leading,spacing:22) {
            Panel {VStack(alignment:.leading,spacing:16) {
                HStack {Text(store.tr("language"));Spacer();Picker("",selection:Binding(get:{store.language},set:{value in Task{_ = await store.perform("settings",["language":value])}})){Text("Русский").tag("ru");Text("English").tag("en")}.labelsHidden().frame(width:170)}
                Divider()
                HStack{Text(store.tr("interval"));Spacer();Picker("",selection:Binding(get:{store.snapshot?.app.refreshMinutes ?? 60},set:{value in Task{_ = await store.perform("settings",["refreshMinutes":value])}})){Text(store.tr("manual")).tag(0);ForEach([15,30,60,120],id:\.self){value in Text("\(value) "+store.tr("minutes")).tag(value)}}.labelsHidden().frame(width:170)}
                Text(store.tr("storageInfo")).font(.caption).foregroundStyle(.secondary)
            }}
            Text(store.tr("ruServices")).font(.headline)
            Text(store.tr("ruInfo")).font(.callout).foregroundStyle(.secondary)
            TextField(store.tr("search"),text:$search).textFieldStyle(.roundedBorder)
            LazyVGrid(columns:[GridItem(.flexible()),GridItem(.flexible())],alignment:.leading,spacing:12) {
                ForEach((store.snapshot?.app.ruServices ?? []).filter{search.isEmpty||$0.name.localizedCaseInsensitiveContains(search)}){service in
                    Toggle(service.name,isOn:Binding(get:{store.snapshot?.app.ruServices.first{$0.id==service.id}?.enabled ?? service.enabled},set:{value in Task{_ = await store.perform("ru.service",["id":service.id,"enabled":value])}})).toggleStyle(.switch).controlSize(.small).padding(12).background(Theme.panel,in:RoundedRectangle(cornerRadius:10))
                }
            }
            Text(store.tr("rules")).font(.headline)
            HStack{TextField(store.tr("domain"),text:$domain).textFieldStyle(.roundedBorder);Picker("",selection:$match){Text(store.tr("exact")).tag("Exact");Text(store.tr("suffix")).tag("Suffix")}.labelsHidden();Picker("",selection:$route){Text(store.tr("direct")).tag("Direct");Text(store.tr("proxy")).tag("Proxy")}.labelsHidden();Button(store.tr("add")){Task{if await store.perform("ru.rule",["value":domain,"match":match,"route":route]) != nil {domain=""}}}.disabled(domain.isEmpty)}
            ForEach(store.snapshot?.app.ruRules ?? []){rule in HStack{Text(rule.value).font(.system(.body,design:.monospaced));Text(store.tr(rule.route=="Proxy" ? "proxy":"direct")).foregroundStyle(.secondary);Spacer();Button{Task{_ = await store.perform("ru.remove",["value":rule.value])}}label:{Image(systemName:"minus.circle")}}.padding(12).background(Theme.panel,in:RoundedRectangle(cornerRadius:10))}
        }.padding(28)}
    }
}
