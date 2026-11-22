import WidgetKit
import SwiftUI
#if canImport(ActivityKit)
import ActivityKit
#endif

@main
struct OmyaruLiveActivityBundle: WidgetBundle {
    var body: some Widget {
        OmyaruSyncLiveActivityWidget()
    }
}

struct OmyaruSyncLiveActivityWidget: Widget {
    var body: some WidgetConfiguration {
        ActivityConfiguration(for: OmyaruSyncActivityAttributes.self) { context in
            lockScreenView(context: context)
                .activityBackgroundTint(Color(red: 0.08, green: 0.10, blue: 0.14).opacity(0.92))
                .activitySystemActionForegroundColor(.white)
        } dynamicIsland: { context in
            let pct = Int((min(1.0, max(0.0, context.state.progress)) * 100).rounded())
            let accent: Color = context.state.isError
                ? .orange
                : (context.state.isFinished ? .green : Color(red: 0.36, green: 0.57, blue: 1.0))

            return DynamicIsland {
                DynamicIslandExpandedRegion(.leading) {
                    HStack(spacing: 6) {
                        Image(systemName: statusIcon(context.state))
                            .font(.system(size: 16, weight: .semibold))
                            .foregroundStyle(accent)
                        Text("Omyaru")
                            .font(.system(size: 14, weight: .bold))
                            .foregroundStyle(.white)
                    }
                    .padding(.leading, 4)
                }
                DynamicIslandExpandedRegion(.trailing) {
                    Text("\(pct)%")
                        .font(.system(size: 15, weight: .bold).monospacedDigit())
                        .foregroundStyle(accent)
                        .padding(.trailing, 4)
                }
                DynamicIslandExpandedRegion(.center) {
                    Text(context.state.phase)
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(.white.opacity(0.92))
                        .lineLimit(1)
                }
                DynamicIslandExpandedRegion(.bottom) {
                    VStack(alignment: .leading, spacing: 6) {
                        ProgressView(value: min(1.0, max(0.0, context.state.progress)))
                            .tint(accent)
                        HStack {
                            Text(context.state.detail)
                                .font(.caption2)
                                .foregroundStyle(.white.opacity(0.75))
                                .lineLimit(1)
                            Spacer(minLength: 6)
                            if context.state.totalItems > 0 {
                                Text("\(context.state.completedItems)/\(context.state.totalItems)")
                                    .font(.caption2.weight(.semibold).monospacedDigit())
                                    .foregroundStyle(.white.opacity(0.85))
                            }
                        }
                    }
                    .padding(.horizontal, 6)
                    .padding(.bottom, 4)
                }
            } compactLeading: {
                Image(systemName: statusIcon(context.state))
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(accent)
            } compactTrailing: {
                if context.state.isFinished {
                    Image(systemName: "checkmark")
                        .font(.system(size: 12, weight: .bold))
                        .foregroundStyle(.green)
                } else {
                    Text("\(pct)%")
                        .font(.system(size: 12, weight: .bold).monospacedDigit())
                        .foregroundStyle(accent)
                }
            } minimal: {
                Image(systemName: statusIcon(context.state))
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(accent)
            }
        }
    }

    @ViewBuilder
    private func lockScreenView(context: ActivityViewContext<OmyaruSyncActivityAttributes>) -> some View {
        let pct = Int((min(1.0, max(0.0, context.state.progress)) * 100).rounded())
        let accent: Color = context.state.isError
            ? .orange
            : (context.state.isFinished ? .green : Color(red: 0.36, green: 0.57, blue: 1.0))

        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                Image(systemName: statusIcon(context.state))
                    .font(.system(size: 18, weight: .semibold))
                    .foregroundStyle(accent)
                VStack(alignment: .leading, spacing: 1) {
                    Text(context.attributes.isBackground ? "Omyaru 백그라운드 동기화" : "Omyaru 동기화")
                        .font(.system(size: 14, weight: .bold))
                        .foregroundStyle(.white)
                    Text(context.attributes.accountLabel)
                        .font(.system(size: 11))
                        .foregroundStyle(.white.opacity(0.65))
                        .lineLimit(1)
                }
                Spacer()
                Text(context.state.phase)
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(.white.opacity(0.9))
                    .padding(.horizontal, 8)
                    .padding(.vertical, 3)
                    .background(Color.white.opacity(0.12), in: Capsule())
                Text("\(pct)%")
                    .font(.system(size: 16, weight: .bold).monospacedDigit())
                    .foregroundStyle(accent)
            }

            ProgressView(value: min(1.0, max(0.0, context.state.progress)))
                .tint(accent)

            HStack {
                Text(context.state.detail)
                    .font(.caption)
                    .foregroundStyle(.white.opacity(0.8))
                    .lineLimit(1)
                Spacer(minLength: 8)
                if context.state.totalItems > 0 {
                    Text("\(context.state.completedItems) / \(context.state.totalItems)")
                        .font(.caption.weight(.semibold).monospacedDigit())
                        .foregroundStyle(.white.opacity(0.85))
                }
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 14)
    }

    private func statusIcon(_ state: OmyaruSyncActivityAttributes.ContentState) -> String {
        if state.isError { return "exclamationmark.icloud.fill" }
        if state.isFinished { return "checkmark.icloud.fill" }
        return "arrow.triangle.2.circlepath.icloud.fill"
    }
}
