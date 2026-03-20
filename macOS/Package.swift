// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "IndolentMacWorkspace",
    platforms: [
        .macOS(.v14)
    ],
    products: [
        .library(name: "IndolentKit", targets: ["IndolentKit"]),
        .executable(name: "IndolentMac", targets: ["IndolentMac"])
    ],
    targets: [
        .target(
            name: "IndolentKit",
            linkerSettings: [
                .linkedFramework("AppKit"),
                .linkedFramework("ApplicationServices"),
                .linkedFramework("CoreGraphics"),
                .linkedFramework("ScreenCaptureKit"),
                .linkedFramework("Vision")
            ]
        ),
        .executableTarget(
            name: "IndolentMac",
            dependencies: ["IndolentKit"],
            linkerSettings: [
                .linkedFramework("AppKit")
            ]
        ),
        .testTarget(
            name: "IndolentKitTests",
            dependencies: ["IndolentKit"]
        )
    ]
)
