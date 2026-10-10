import Foundation
struct Snapshot:Decodable {var app:AppSnapshot;var chat:ChatSnapshot}
struct AppSnapshot:Decodable {
    var version:String;var language:String;var mode:String;var refreshMinutes:Int;var selectedId:String?
    var attention:String?;var connection:Connection;var groups:[SubscriptionGroupDTO];var nodes:[Node];var ruServices:[RuService];var ruRules:[RuRule]
}
struct Connection:Decodable {var status:String;var reason:String}
struct SubscriptionGroupDTO:Decodable,Identifiable {var id:String;var name:String;var local:Bool;var count:Int;var updated:String?;var expires:String?;var usedBytes:Int64?;var totalBytes:Int64?}
struct Node:Decodable,Identifiable {var id:String;var groupId:String;var name:String;var country:String;var flag:String;var `protocol`:String;var transport:String;var security:String}
struct RuService:Decodable,Identifiable {var id:String;var name:String;var category:String;var enabled:Bool}
struct RuRule:Decodable,Identifiable {var value:String;var match:String;var route:String;var id:String {match+value}}
struct ChatSnapshot:Decodable {var enabled:Bool;var state:String;var nickname:String;var peerId:String;var contacts:[Contact];var requests:[Request];var blocked:[Request];var invites:[IssuedInvite]}
struct Contact:Decodable,Identifiable {var peerId:String;var name:String;var online:Bool;var id:String {peerId}}
struct Request:Decodable,Identifiable {var peerId:String;var name:String;var id:String {peerId}}
struct IssuedInvite:Decodable,Identifiable {var id:String;var expires:String}
struct Messages:Decodable {var messages:[Message];var draft:String}
struct Message:Decodable,Identifiable {var id:String;var peerId:String;var outgoing:Bool;var kind:String;var text:String;var createdUtc:String;var status:String}
struct Invite:Decodable {var uri:String;var code:String}
enum Route:CaseIterable {case home,subscriptions,chat,settings,profile
    var key:String {switch self {case .home:"home";case .subscriptions:"subscriptions";case .chat:"chat";case .settings:"settings";case .profile:"profile"}}
    var icon:String {switch self {case .home:"power";case .subscriptions:"rectangle.stack";case .chat:"bubble.left.and.bubble.right";case .settings:"slider.horizontal.3";case .profile:"person.crop.circle"}}
}
