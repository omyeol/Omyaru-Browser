import Foundation
import BackgroundTasks
import AVFoundation
import UserNotifications
import UIKit
#if canImport(ActivityKit)
import ActivityKit
#endif

// MARK: - Live Activity ("실시간 현황") + Fallback Progress Notification Controller

@MainActor
final class OmyaruSyncLiveActivityController {
    static let shared = OmyaruSyncLiveActivityController()
    private static let fallbackNotificationID = "omyaru.sync.progress.notice"

    #if canImport(ActivityKit)
    private var activeActivityBox: Any? = nil
    #endif
    private var lastUpdateAt: Date = .distantPast

    var areLiveActivitiesAvailable: Bool {
        #if canImport(ActivityKit)
        if #available(iOS 16.1, *) {
            return ActivityAuthorizationInfo().areActivitiesEnabled
        }
        #endif
        return false
    }

    func cleanupStaleActivitiesOnLaunch() {
        #if canImport(ActivityKit)
        if #available(iOS 16.1, *) {
            Task {
                for existing in Activity<OmyaruSyncActivityAttributes>.activities {
                    await existing.end(dismissalPolicy: .immediate)
                }
            }
        }
        #endif
    }

    func start(
        accountLabel: String,
        isBackground: Bool,
        phase: String,
        detail: String,
        progress: Double,
        enabled: Bool
    ) {
        guard enabled else { return }
        lastUpdateAt = Date()
        #if canImport(ActivityKit)
        if #available(iOS 16.1, *), ActivityAuthorizationInfo().areActivitiesEnabled {
            let attributes = OmyaruSyncActivityAttributes(
                accountLabel: accountLabel.isEmpty ? "Google Drive" : accountLabel,
                isBackground: isBackground
            )
            let state = OmyaruSyncActivityAttributes.ContentState(
                phase: phase,
                detail: detail,
                progress: min(1.0, max(0.0, progress)),
                completedItems: 0,
                totalItems: 0,
                isFinished: false,
                isError: false
            )
            Task {
                for stale in Activity<OmyaruSyncActivityAttributes>.activities {
                    await stale.end(dismissalPolicy: .immediate)
                }
                if let created = try? Activity<OmyaruSyncActivityAttributes>.request(
                    attributes: attributes,
                    contentState: state,
                    pushType: nil
                ) {
                    await MainActor.run {
                        self.activeActivityBox = created
                    }
                } else {
                    self.postFallbackNotification(title: "Omyaru 동기화 중…", body: detail)
                }
            }
            return
        }
        #endif
        if isBackground {
            postFallbackNotification(title: "Omyaru 동기화 중…", body: detail)
        }
    }

    func update(
        phase: String,
        detail: String,
        progress: Double,
        completedItems: Int = 0,
        totalItems: Int = 0,
        force: Bool = false
    ) {
        let now = Date()
        if !force && now.timeIntervalSince(lastUpdateAt) < 0.35 { return }
        lastUpdateAt = now

        #if canImport(ActivityKit)
        if #available(iOS 16.1, *),
           let activity = activeActivityBox as? Activity<OmyaruSyncActivityAttributes> {
            let state = OmyaruSyncActivityAttributes.ContentState(
                phase: phase,
                detail: detail,
                progress: min(1.0, max(0.0, progress)),
                completedItems: completedItems,
                totalItems: totalItems,
                isFinished: false,
                isError: false
            )
            Task {
                await activity.update(using: state)
            }
        }
        #endif
    }

    func finish(phase: String, detail: String, isError: Bool) {
        #if canImport(ActivityKit)
        if #available(iOS 16.1, *),
           let activity = activeActivityBox as? Activity<OmyaruSyncActivityAttributes> {
            let finalState = OmyaruSyncActivityAttributes.ContentState(
                phase: phase,
                detail: detail,
                progress: isError ? 0.0 : 1.0,
                completedItems: 0,
                totalItems: 0,
                isFinished: !isError,
                isError: isError
            )
            activeActivityBox = nil
            Task {
                await activity.end(using: finalState, dismissalPolicy: .after(Date().addingTimeInterval(5)))
            }
            return
        }
        #endif
        UNUserNotificationCenter.current().removeDeliveredNotifications(withIdentifiers: [Self.fallbackNotificationID])
    }

    func requestFallbackNotificationPermissionIfNeeded() {
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .badge]) { _, _ in }
    }

    private func postFallbackNotification(title: String, body: String) {
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        let request = UNNotificationRequest(identifier: Self.fallbackNotificationID, content: content, trigger: nil)
        UNUserNotificationCenter.current().add(request)
    }
}

// MARK: - Active Transfer Background Keep-Alive (Silent Mixable Audio Session ONLY While Syncing!)

@MainActor
final class ActiveSyncBackgroundKeepAlive {
    static let shared = ActiveSyncBackgroundKeepAlive()
    private var player: AVAudioPlayer? = nil
    private var bgTaskID: UIBackgroundTaskIdentifier = .invalid

    func begin(allowExtendedKeepAlive: Bool) {
        if bgTaskID == .invalid {
            bgTaskID = UIApplication.shared.beginBackgroundTask(withName: "OmyaruDriveBackgroundSync") { [weak self] in
                Task { @MainActor in
                    self?.endBackgroundTaskOnly()
                }
            }
        }
        guard allowExtendedKeepAlive else { return }
        startSilentAudioLoopIfNeeded()
    }

    func end() {
        stopSilentAudioLoop()
        endBackgroundTaskOnly()
    }

    private func endBackgroundTaskOnly() {
        if bgTaskID != .invalid {
            UIApplication.shared.endBackgroundTask(bgTaskID)
            bgTaskID = .invalid
        }
    }

    private func startSilentAudioLoopIfNeeded() {
        guard player == nil else { return }
        do {
            let session = AVAudioSession.sharedInstance()
            try session.setCategory(.playback, mode: .default, options: [.mixWithOthers])
            try session.setActive(true)
            let wavData = Self.makeOneSecondSilentWAV()
            let audioPlayer = try AVAudioPlayer(data: wavData)
            audioPlayer.numberOfLoops = -1
            audioPlayer.volume = 0.0
            audioPlayer.prepareToPlay()
            audioPlayer.play()
            player = audioPlayer
        } catch {
            player = nil
        }
    }

    private func stopSilentAudioLoop() {
        player?.stop()
        player = nil
    }

    /// Generates an in-memory 1-second 8kHz 16-bit mono silent PCM WAV buffer (~16KB, zero disk I/O).
    private static func makeOneSecondSilentWAV() -> Data {
        let sampleRate: UInt32 = 8000
        let numChannels: UInt16 = 1
        let bitsPerSample: UInt16 = 16
        let dataSize: UInt32 = sampleRate * UInt32(numChannels) * UInt32(bitsPerSample / 8)
        let chunkSize: UInt32 = 36 + dataSize
        let byteRate: UInt32 = sampleRate * UInt32(numChannels) * UInt32(bitsPerSample / 8)
        let blockAlign: UInt16 = numChannels * (bitsPerSample / 8)

        var wav = Data()
        wav.append(Data("RIFF".utf8))
        wav.append(withUnsafeBytes(of: chunkSize.littleEndian) { Data($0) })
        wav.append(Data("WAVE".utf8))
        wav.append(Data("fmt ".utf8))
        wav.append(withUnsafeBytes(of: UInt32(16).littleEndian) { Data($0) })
        wav.append(withUnsafeBytes(of: UInt16(1).littleEndian) { Data($0) })
        wav.append(withUnsafeBytes(of: numChannels.littleEndian) { Data($0) })
        wav.append(withUnsafeBytes(of: sampleRate.littleEndian) { Data($0) })
        wav.append(withUnsafeBytes(of: byteRate.littleEndian) { Data($0) })
        wav.append(withUnsafeBytes(of: blockAlign.littleEndian) { Data($0) })
        wav.append(withUnsafeBytes(of: bitsPerSample.littleEndian) { Data($0) })
        wav.append(Data("data".utf8))
        wav.append(withUnsafeBytes(of: dataSize.littleEndian) { Data($0) })
        wav.append(Data(count: Int(dataSize)))
        return wav
    }
}

// MARK: - 4-Layer iOS Background Task Scheduler (BGAppRefreshTask + BGProcessingTask + Background URLSession)

final class OmyaruBackgroundSyncScheduler: NSObject, URLSessionDelegate {
    static let shared = OmyaruBackgroundSyncScheduler()
    static let refreshTaskID = "cuttan.omyaru.app.sync.refresh"
    static let processingTaskID = "cuttan.omyaru.app.sync.processing"
    static let backgroundSessionID = "cuttan.omyaru.app.drive.bgtransfer"

    private var registered = false
    private var backgroundSessionCompletionHandler: (() -> Void)? = nil
    weak var browserRef: BrowserModel? = nil

    lazy var backgroundURLSession: URLSession = {
        let config = URLSessionConfiguration.background(withIdentifier: Self.backgroundSessionID)
        config.isDiscretionary = false
        config.sessionSendsLaunchEvents = true
        config.waitsForConnectivity = true
        return URLSession(configuration: config, delegate: self, delegateQueue: nil)
    }()

    func registerTasks() {
        guard !registered else { return }
        registered = true

        BGTaskScheduler.shared.register(forTaskWithIdentifier: Self.refreshTaskID, using: nil) { [weak self] task in
            guard let refreshTask = task as? BGAppRefreshTask else {
                task.setTaskCompleted(success: false)
                return
            }
            self?.handleBackgroundTask(refreshTask)
        }

        BGTaskScheduler.shared.register(forTaskWithIdentifier: Self.processingTaskID, using: nil) { [weak self] task in
            guard let procTask = task as? BGProcessingTask else {
                task.setTaskCompleted(success: false)
                return
            }
            self?.handleBackgroundTask(procTask)
        }
    }

    func scheduleIfNeeded(enabled: Bool, isConnected: Bool) {
        BGTaskScheduler.shared.cancel(taskRequestWithIdentifier: Self.refreshTaskID)
        BGTaskScheduler.shared.cancel(taskRequestWithIdentifier: Self.processingTaskID)
        guard enabled && isConnected else { return }

        let refresh = BGAppRefreshTaskRequest(identifier: Self.refreshTaskID)
        refresh.earliestBeginDate = Date(timeIntervalSinceNow: 15 * 60)
        try? BGTaskScheduler.shared.submit(refresh)

        let processing = BGProcessingTaskRequest(identifier: Self.processingTaskID)
        processing.requiresNetworkConnectivity = true
        processing.requiresExternalPower = false
        processing.earliestBeginDate = Date(timeIntervalSinceNow: 15 * 60)
        try? BGTaskScheduler.shared.submit(processing)
    }

    func handleBackgroundURLSessionEvents(identifier: String, completionHandler: @escaping () -> Void) {
        if identifier == Self.backgroundSessionID {
            backgroundSessionCompletionHandler = completionHandler
            _ = backgroundURLSession
        } else {
            completionHandler()
        }
    }

    func urlSessionDidFinishEvents(forBackgroundURLSession session: URLSession) {
        let handler = backgroundSessionCompletionHandler
        backgroundSessionCompletionHandler = nil
        DispatchQueue.main.async {
            handler?()
        }
    }

    private func handleBackgroundTask(_ task: BGTask) {
        Task { @MainActor [weak self] in
            let sync = DriveSyncManager.shared
            self?.scheduleIfNeeded(enabled: sync.backgroundSyncEnabled, isConnected: sync.isConnected)
            guard sync.backgroundSyncEnabled && sync.isConnected else {
                task.setTaskCompleted(success: true)
                return
            }

            let job = Task { @MainActor in
                await sync.syncWithGoogleDrive(
                    browser: self?.browserRef,
                    shelf: .shared,
                    isBackground: true
                )
            }

            task.expirationHandler = {
                job.cancel()
                Task { @MainActor in
                    sync.cancelActiveSync()
                }
            }

            await job.value
            task.setTaskCompleted(success: !job.isCancelled)
        }
    }
}

final class OmyaruAppDelegate: NSObject, UIApplicationDelegate {
    func application(
        _ application: UIApplication,
        didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]? = nil
    ) -> Bool {
        OmyaruBackgroundSyncScheduler.shared.registerTasks()
        Task { @MainActor in
            OmyaruSyncLiveActivityController.shared.cleanupStaleActivitiesOnLaunch()
            let sync = DriveSyncManager.shared
            OmyaruBackgroundSyncScheduler.shared.scheduleIfNeeded(
                enabled: sync.backgroundSyncEnabled,
                isConnected: sync.isConnected
            )
        }
        return true
    }

    func application(
        _ application: UIApplication,
        handleEventsForBackgroundURLSession identifier: String,
        completionHandler: @escaping () -> Void
    ) {
        OmyaruBackgroundSyncScheduler.shared.handleBackgroundURLSessionEvents(
            identifier: identifier,
            completionHandler: completionHandler
        )
    }
}
