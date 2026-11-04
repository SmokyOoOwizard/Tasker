// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "TaskerAPI",
    platforms: [.macOS(.v14)],
    products: [
        .library(name: "TaskerAPI", targets: ["TaskerAPI"])
    ],
    dependencies: [
        .package(url: "https://github.com/apple/swift-openapi-runtime", from: "1.13.4"),
        .package(url: "https://github.com/apple/swift-openapi-urlsession", from: "1.4.0"),
        .package(url: "https://github.com/apple/swift-http-types", from: "1.5.1")
    ],
    targets: [
        .target(
            name: "TaskerAPI",
            dependencies: [
                .product(name: "OpenAPIRuntime", package: "swift-openapi-runtime"),
                .product(name: "OpenAPIURLSession", package: "swift-openapi-urlsession"),
                .product(name: "HTTPTypes", package: "swift-http-types")
            ]
        ),
        .testTarget(
            name: "TaskerAPITests",
            dependencies: ["TaskerAPI"],
            resources: [.copy("Fixtures")]
        )
    ]
)
