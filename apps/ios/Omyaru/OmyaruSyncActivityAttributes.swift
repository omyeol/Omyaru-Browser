import Foundation
#if canImport(ActivityKit)
import ActivityKit

@available(iOS 16.1, *)
public struct OmyaruSyncActivityAttributes: ActivityAttributes {
    public struct ContentState: Codable, Hashable {
        public var phase: String
        public var detail: String
        public var progress: Double
        public var completedItems: Int
        public var totalItems: Int
        public var isFinished: Bool
        public var isError: Bool

        public init(
            phase: String,
            detail: String,
            progress: Double,
            completedItems: Int = 0,
            totalItems: Int = 0,
            isFinished: Bool = false,
            isError: Bool = false
        ) {
            self.phase = phase
            self.detail = detail
            self.progress = progress
            self.completedItems = completedItems
            self.totalItems = totalItems
            self.isFinished = isFinished
            self.isError = isError
        }
    }

    public var accountLabel: String
    public var isBackground: Bool

    public init(accountLabel: String, isBackground: Bool) {
        self.accountLabel = accountLabel
        self.isBackground = isBackground
    }
}
#endif
