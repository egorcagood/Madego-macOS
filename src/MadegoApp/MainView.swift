import SwiftUI
struct MainView:View {
    @EnvironmentObject var store:AppStore
    @State private var showImport=false
    var body:some View {
        HStack(spacing:0) {
            VStack(alignment:.leading,spacing:6) {
                HStack(spacing:10){Text("M").font(.system(size:24,weight:.bold)).foregroundStyle(Theme.accent);Text("madego").font(.system(size:25,weight:.semibold,design:.rounded))}.padding(.bottom,28).padding(.top,10)
                ForEach([Route.home,.subscriptions,.chat,.settings],id:\.self) {route in
                    Button {store.route=route} label:{HStack(spacing:12){Image(systemName:route.icon).frame(width:20);Text(store.tr(route.key)).lineLimit(1);Spacer();if route == .chat,let count=store.snapshot?.chat.requests.count,count>0 {Text("\(count)").font(.caption).foregroundStyle(Theme.accent)}}.padding(.horizontal,12).padding(.vertical,13).contentShape(Rectangle())}.buttonStyle(.plain).foregroundStyle(store.route==route ? Theme.accent:Color.secondary).background(store.route==route ? Theme.accent.opacity(0.08):Color.clear,in:RoundedRectangle(cornerRadius:10))
                }
                Spacer()
                Button {store.profile()} label:{Label(store.snapshot?.chat.nickname.isEmpty==false ? store.snapshot!.chat.nickname:store.tr("profile"),systemImage:"person.crop.circle").lineLimit(1).frame(maxWidth:.infinity,alignment:.leading).padding(12)}.buttonStyle(.plain)
                Text(store.snapshot?.app.version ?? "0.1.0-dev.1").font(.caption2).foregroundStyle(.tertiary).padding(.horizontal,12)
            }.padding(20).frame(width:210).background(Theme.sidebar)
            Rectangle().fill(Theme.line).frame(width:1)
            VStack(spacing:0) {
                HStack {
                    if store.route == .profile {Button{store.route=store.previousRoute}label:{Label(store.tr("back"),systemImage:"chevron.left")}.buttonStyle(.plain)}
                    Text(store.tr(store.route.key)).font(.title2.weight(.semibold));Spacer()
                    Text(store.tr("development")).font(.caption).foregroundStyle(.secondary).padding(.horizontal,10).padding(.vertical,5).background(Theme.panel,in:Capsule())
                    if store.route == .home || store.route == .subscriptions {Button{showImport=true}label:{Image(systemName:"plus")}.help(store.tr("addSubscription"))}
                }.padding(.horizontal,28).padding(.vertical,24)
                Rectangle().fill(Theme.line).frame(height:1)
                if let attention=store.snapshot?.app.attention {Label(store.tr(attention),systemImage:"lock.trianglebadge.exclamationmark").font(.caption).foregroundStyle(.secondary).padding(16).frame(maxWidth:.infinity,alignment:.leading).background(Theme.panel)}
                Group {
                    switch store.route {case .home:ConnectionView(showImport:$showImport)
                    case .subscriptions:SubscriptionsView(showImport:$showImport)
                    case .chat:ChatView()
                    case .settings:SettingsView()
                    case .profile:ProfileView()}
                }.frame(maxWidth:.infinity,maxHeight:.infinity)
            }
        }.background(Theme.background).frame(minWidth:880,minHeight:620)
        .sheet(isPresented:$showImport){ImportSheet().environmentObject(store)}
        .alert(store.tr("error"),isPresented:Binding(get:{store.error != nil},set:{if !$0 {store.error=nil}})){Button("OK"){store.error=nil}}message:{Text(store.error ?? "")}
    }
}
