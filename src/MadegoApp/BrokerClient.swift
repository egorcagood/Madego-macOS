import Foundation

enum BrokerError: Error {case unavailable, protocolError, failed(String), cancelled}
actor BrokerClient {
    private var process: Process?
    private var input: FileHandle?
    private var buffer=Data()
    private var pending: [String: CheckedContinuation<Data,Error>] = [:]
    private var events: (@Sendable (String) async -> Void)?
    func setEvents(_ callback: @escaping @Sendable (String) async -> Void) {events=callback}
    func start() throws {
        if process != nil {return}
        let p=Process();let stdin=Pipe();let stdout=Pipe();let stderr=Pipe()
        let environment=ProcessInfo.processInfo.environment
        guard let resource=Bundle.main.resourceURL else {throw BrokerError.unavailable}
        p.executableURL=URL(fileURLWithPath:environment["MADEGO_BROKER_PATH"] ?? resource.appendingPathComponent("Core/Madego.Broker").path)
        p.standardInput=stdin;p.standardOutput=stdout;p.standardError=stderr
        p.environment=environment.merging(["DOTNET_CLI_TELEMETRY_OPTOUT":"1","DOTNET_NOLOGO":"1"]){_,new in new}
        if let root=environment["MADEGO_TEST_ROOT"] {p.arguments=["--data-root",root,"--keychain-service","dev.madego.test.ui"]}
        try p.run();process=p;input=stdin.fileHandleForWriting
        let out=stdout.fileHandleForReading;let err=stderr.fileHandleForReading
        Task.detached { [weak self] in
            do {while let bytes=try out.read(upToCount:8192),!bytes.isEmpty {await self?.receive(bytes)}}catch{}
            await self?.ended()
        }
        Task.detached {do{while let bytes=try err.read(upToCount:4096),!bytes.isEmpty { /* Sensitive child errors are never logged. */ }}catch{}}
    }
    func request(_ method:String,params:[String:String]=[:]) async throws -> Data {
        try await requestData(method,params:try JSONSerialization.data(withJSONObject:params))
    }
    func requestData(_ method:String,params:Data) async throws -> Data {
        guard input != nil else {throw BrokerError.unavailable}
        let id=UUID().uuidString
        return try await withTaskCancellationHandler(operation:{
            if Task.isCancelled {throw BrokerError.cancelled}
            return try await withCheckedThrowingContinuation { continuation in
                pending[id]=continuation
                do {try write(id:id,method:method,params:params)}catch{pending.removeValue(forKey:id)?.resume(throwing:BrokerError.unavailable)}
                Task {try? await Task.sleep(for:.seconds(110));self.cancel(id)}
            }
        },onCancel:{Task{await self.cancel(id)}})
    }
    private func write(id:String,method:String,params:Data) throws {
        let object=try JSONSerialization.jsonObject(with:params)
        var bytes=try JSONSerialization.data(withJSONObject:["id":id,"method":method,"params":object])
        guard bytes.count<=128*1024 else {throw BrokerError.failed("input_too_large")}
        bytes.append(10);try input?.write(contentsOf:bytes)
    }
    private func cancel(_ id:String) {
        guard let continuation=pending.removeValue(forKey:id) else {return}
        continuation.resume(throwing:BrokerError.cancelled)
        try? write(id:UUID().uuidString,method:"cancel",params:JSONSerialization.data(withJSONObject:["id":id]))
    }
    private func receive(_ data:Data) async {
        buffer.append(data)
        while let end=buffer.firstIndex(of:10) {
            let line=buffer[..<end];buffer.removeSubrange(...end)
            guard line.count<=2*1024*1024 else {await stop();return}
            guard let json=try? JSONSerialization.jsonObject(with:Data(line)) as? [String:Any] else {await stop();return}
            if let event=json["event"] as? String {if let events {await events(event)};continue}
            guard let id=json["id"] as? String,let continuation=pending.removeValue(forKey:id) else {continue}
            if let error=json["error"] as? [String:Any] {continuation.resume(throwing:BrokerError.failed(error["code"] as? String ?? "operation_failed"))}
            else if let result=json["result"],let bytes=try? JSONSerialization.data(withJSONObject:result,options:.fragmentsAllowed) {continuation.resume(returning:bytes)}
            else {continuation.resume(throwing:BrokerError.protocolError)}
        }
        if buffer.count>2*1024*1024 {await stop()}
    }
    private func ended() {
        let waits=pending;pending.removeAll();for continuation in waits.values {continuation.resume(throwing:BrokerError.unavailable)}
        process=nil;input=nil
    }
    func stop() async {
        let old=process;try? input?.close();input=nil
        if let old {for _ in 0..<30 {if !old.isRunning {break};try? await Task.sleep(for:.milliseconds(100))};if old.isRunning {old.terminate()}}
        ended()
    }
}
