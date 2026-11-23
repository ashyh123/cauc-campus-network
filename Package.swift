// swift-tools-version: 6.0

import PackageDescription

let package = Package(
    name: "CAUCCampusNetwork",
    platforms: [
        .macOS(.v13),
    ],
    products: [
        .library(
            name: "CAUCCampusCore",
            targets: ["CAUCCampusCore"]
        ),
        .executable(
            name: "CAUCCampusNetwork",
            targets: ["CAUCCampusNetwork"]
        ),
    ],
    targets: [
        .target(
            name: "CAUCCampusCore"
        ),
        .executableTarget(
            name: "CAUCCampusNetwork",
            dependencies: ["CAUCCampusCore"]
        ),
        .testTarget(
            name: "CAUCCampusCoreTests",
            dependencies: ["CAUCCampusCore"]
        ),
    ]
)
