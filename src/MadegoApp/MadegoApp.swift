import SwiftUI
import AppKit
@MainActor final class AppDelegate:NSObject,NSApplicationDelegate {
    weak var store:AppStore?
    func applicationShouldTerminate(_ sender:NSApplication)->NSApplication.TerminateReply {
        guard let store else{return .terminateNow}
        Task {await store.shutdown();sender.reply(toApplicationShouldTerminate:true)};return .terminateLater
    }
}
@main struct MadegoApp:App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var delegate
    @StateObject private var store=AppStore()
    var body:some Scene {
        Window("Madego",id:"main") {
            MainView().environmentObject(store).preferredColorScheme(.dark).tint(Theme.accent)
                .task {delegate.store=store;await store.start()}
        }.defaultSize(width:1100,height:730).windowStyle(.hiddenTitleBar)
        .commands {CommandGroup(replacing:.newItem){};CommandGroup(after:.appInfo){Button(store.tr("refresh")){Task{await store.refresh()}}.keyboardShortcut("r")}}
        MenuBarExtra("Madego",systemImage:"paperplane") {
            MenuContents().environmentObject(store)
        }
    }
}
struct MenuContents:View {
    @EnvironmentObject var store:AppStore
    @Environment(\.openWindow) var open
    var body:some View {
        Text("Madego · "+store.tr("development"))
        Button(store.tr("home")){store.route = .home;open(id:"main");NSApp.activate(ignoringOtherApps:true)}
        Button(store.tr("chat")){store.route = .chat;open(id:"main");NSApp.activate(ignoringOtherApps:true)}
        Divider()
        Button(store.tr(store.snapshot?.chat.enabled==true ? "disableChat":"enableChat")){Task{_ = await store.perform(store.snapshot?.chat.enabled==true ? "chat.disable":"chat.enable")}}
        Divider();Button(store.language=="ru" ? "Завершить Madego":"Quit Madego"){NSApp.terminate(nil)}
    }
}
