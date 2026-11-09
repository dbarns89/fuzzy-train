import SwiftUI

@main
struct VCXDiagApp: App {
    @StateObject private var session = DiagSession()

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environmentObject(session)
        }
    }
}
