import SwiftUI
import AppKit
@MainActor final class AppStore:ObservableObject {
    let client=BrokerClient()
    @Published var snapshot:Snapshot?
    @Published var route:Route = .home
    @Published var previousRoute:Route = .chat
    @Published var error:String?
    @Published var messages:[Message]=[]
    @Published var selectedPeer:String?
    @Published var draft=""
    @Published var importing=false
    @Published var sending=false
    private var refreshing=false
    var language:String {snapshot?.app.language ?? "ru"}
    func tr(_ key:String)->String {L10n.text(key,language)}
    func start() async {
        do {
            try await client.start()
            await client.setEvents { [weak self] name in
                Task {@MainActor [weak self] in guard let self else{return};if name=="changed"||name=="incoming" {await self.refresh();if self.selectedPeer != nil {await self.loadMessages()}}}
            }
            await refresh()
        }catch {show(error)}
    }
    func refresh() async {
        if refreshing{return};refreshing=true;defer{refreshing=false}
        do {let bytes=try await client.request("snapshot");snapshot=try JSONDecoder().decode(Snapshot.self,from:bytes)}catch{show(error)}
    }
    @discardableResult func perform(_ method:String,_ params:[String:Any]=[:]) async -> Data? {
        do {let result=try await client.requestData(method,params:JSONSerialization.data(withJSONObject:params));await refresh();return result}
        catch {show(error);return nil}
    }
    func show(_ failure:Error) {
        switch failure {case BrokerError.failed(let code):error=tr(code)==code ? tr("operation_failed"):tr(code)
        case BrokerError.cancelled:error=nil
        case BrokerError.unavailable:error=tr("brokerUnavailable")
        default:error=tr("operation_failed")}
    }
    func profile() {previousRoute=route;route = .profile;Task{_ = await perform("profile.get")}}
    func choosePeer(_ peer:String) async {if let old=selectedPeer {_ = await perform("messages.draft",["peer":old,"text":draft])};selectedPeer=peer;draft="";await loadMessages(loadDraft:true)}
    func loadMessages(loadDraft:Bool=false) async {
        guard let peer=selectedPeer else {return}
        do {let bytes=try await client.request("messages.list",params:["peer":peer]);let data=try JSONDecoder().decode(Messages.self,from:bytes);guard selectedPeer==peer else{return};messages=data.messages;if loadDraft{draft=data.draft}}
        catch{show(error)}
    }
    func send() async {
        guard let peer=selectedPeer,!draft.trimmingCharacters(in:.whitespacesAndNewlines).isEmpty,draft.utf16.count<=2000,!sending else{return}
        sending=true;let text=draft;draft="";defer{sending=false}
        _ = await perform("messages.draft",["peer":peer,"text":""])
        let result=await perform("messages.send",["peer":peer,"text":text]);if result==nil&&selectedPeer==peer {draft=text};await loadMessages()
    }
    func importText(_ text:String,_ name:String) async -> Bool {
        importing=true;defer{importing=false};return await perform("import",["input":text,"name":name]) != nil
    }
    func shutdown() async {
        if let peer=selectedPeer {_ = try? await client.request("messages.draft",params:["peer":peer,"text":draft])}
        await client.stop()
    }
    func copy(_ text:String){NSPasteboard.general.clearContents();NSPasteboard.general.setString(text,forType:.string)}
}
