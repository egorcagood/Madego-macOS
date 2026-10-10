// swift-tools-version: 6.0
import PackageDescription
let package=Package(name:"Madego",platforms:[.macOS(.v14)],products:[.executable(name:"Madego",targets:["MadegoApp"])],targets:[.executableTarget(name:"MadegoApp",path:"src/MadegoApp")])
