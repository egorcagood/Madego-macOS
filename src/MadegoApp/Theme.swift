import SwiftUI
enum Theme {
    static let accent=Color(red:87/255,green:199/255,blue:232/255)
    static let background=Color(red:9/255,green:11/255,blue:15/255)
    static let sidebar=Color(red:13/255,green:16/255,blue:22/255)
    static let panel=Color(red:19/255,green:23/255,blue:30/255)
    static let line=Color.white.opacity(0.08)
}
struct Panel<Content:View>:View {
    @ViewBuilder var content:Content
    var body:some View {content.padding(20).background(Theme.panel,in:RoundedRectangle(cornerRadius:16)).overlay(RoundedRectangle(cornerRadius:16).stroke(Theme.line))}
}
struct EmptyState:View {
    var icon:String;var title:String;var subtitle:String
    var body:some View {VStack(spacing:14){Image(systemName:icon).font(.system(size:30,weight:.light)).foregroundStyle(Theme.accent);Text(title).font(.title3.weight(.semibold));Text(subtitle).font(.callout).foregroundStyle(.secondary).multilineTextAlignment(.center).frame(maxWidth:360)}.padding(28).frame(maxWidth:.infinity,maxHeight:.infinity)}
}
