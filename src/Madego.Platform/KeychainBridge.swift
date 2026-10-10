import Foundation
import Security

// Standard macOS login Keychain. No secret data is written to stdout, logs or IPC.
@_cdecl("madego_keychain_read")
public func readKey(_ service: UnsafePointer<CChar>, _ account: UnsafePointer<CChar>, _ mayCreate: Int32, _ output: UnsafeMutablePointer<UInt8>) -> Int32 {
    let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
        kSecAttrService as String: String(cString: service), kSecAttrAccount as String: String(cString: account)]
    func read() -> OSStatus {
        var request=query; request[kSecReturnData as String]=true; request[kSecMatchLimit as String]=kSecMatchLimitOne
        var item: CFTypeRef?; let status=SecItemCopyMatching(request as CFDictionary, &item)
        guard status==errSecSuccess else {return status}
        guard let bytes=item as? Data, bytes.count==32 else {return errSecDecode}
        bytes.copyBytes(to: output,count:32);return errSecSuccess
    }
    let status=read()
    if status != errSecItemNotFound || mayCreate==0 {return status}
    var key=[UInt8](repeating:0,count:32)
    defer {_ = key.withUnsafeMutableBytes {$0.initializeMemory(as:UInt8.self,repeating:0)}}
    let random=SecRandomCopyBytes(kSecRandomDefault,32,&key);guard random==errSecSuccess else {return random}
    var addition=query;addition[kSecValueData as String]=Data(key);addition[kSecAttrLabel as String]="Madego local data wrapping key"
    let inserted=SecItemAdd(addition as CFDictionary,nil)
    guard inserted==errSecSuccess || inserted==errSecDuplicateItem else {return inserted}
    return read()
}

// Used only for isolated native test accounts. Production code never deletes its wrapping key.
@_cdecl("madego_keychain_delete_test")
public func deleteTest(_ service: UnsafePointer<CChar>) -> Int32 {
    let name=String(cString:service);guard name.hasPrefix("dev.madego.test.") else {return errSecParam}
    return SecItemDelete([kSecClass as String:kSecClassGenericPassword,kSecAttrService as String:name] as CFDictionary)
}
