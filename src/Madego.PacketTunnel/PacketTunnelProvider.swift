import Foundation
import NetworkExtension

/// Fail-closed system-provider boundary. No routes or DNS are installed without a validated engine adapter.
/// This target is a compile-checked foundation, not an operational VPN engine.
@objc(PacketTunnelProvider)
final class PacketTunnelProvider: NEPacketTunnelProvider {
    override func startTunnel(options: [String:NSObject]?, completionHandler: @escaping (Error?) -> Void) {
        completionHandler(NSError(domain:"Madego.PacketTunnel",code:1,userInfo:[NSLocalizedDescriptionKey:"The dual-engine packet-flow adapter is not implemented in this development build."]))
    }
    override func stopTunnel(with reason: NEProviderStopReason, completionHandler: @escaping () -> Void) {completionHandler()}
    override func handleAppMessage(_ messageData: Data, completionHandler: ((Data?) -> Void)? = nil) {
        guard messageData.count<=64*1024 else {completionHandler?(nil);return}
        completionHandler?(Data("{\"state\":\"unavailable\",\"trafficVerified\":false}".utf8))
    }
}
