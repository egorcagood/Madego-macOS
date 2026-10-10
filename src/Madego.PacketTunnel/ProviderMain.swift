import Foundation
import NetworkExtension
import Dispatch
@main enum ProviderMain {
    static func main() {autoreleasepool {NEProvider.startSystemExtensionMode();dispatchMain()}}
}
