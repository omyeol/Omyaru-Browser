import SwiftUI
import Foundation
import CryptoKit
import UniformTypeIdentifiers
import Darwin

// MARK: - Cross-App Shared Sync Folder, Account Partitioning & Exclusive Kernel Sync Lock (iOS)

/// Coordinates a user-selected shared directory (e.g., via Files app on iPhone or iCloud Drive)
/// so Omyaru Browser and Umtomi share a single copy of bookshelf originals, catalog metadata,
/// and Drive CRDT replica state per Google account (`<BaseFolder>/<sanitized-account-email>/`)
/// without duplicating storage or colliding during simultaneous sync.
@MainActor
final class SharedSyncFolderManager: ObservableObject {
    static let shared = SharedSyncFolderManager()
    static let appIdentifier = "Omyaru Browser"

    private static let bookmarkKey = "omyaru.ios.sharedSyncFolder.bookmarkV1"
    private static let displayNameKey = "omyaru.ios.sharedSyncFolder.displayNameV1"
    private static let configuredKey = "omyaru.ios.sharedSyncFolder.configuredV1"
    private static let useSharedKey = "omyaru.ios.sharedSyncFolder.useSharedV1"

    @Published private(set) var isConfigured: Bool
    @Published private(set) var isUsingSharedFolder: Bool
    @Published private(set) var folderDisplayName: String
    @Published private(set) var lastError: String? = nil
    @Published private(set) var migrationStatus: String? = nil

    private var activeScopedURL: URL? = nil
    private var hasActiveSecurityScope: Bool = false

    private init() {
        let d = UserDefaults.standard
        isConfigured = d.bool(forKey: Self.configuredKey)
        isUsingSharedFolder = d.bool(forKey: Self.useSharedKey)
        folderDisplayName = d.string(forKey: Self.displayNameKey) ?? ""
        if isUsingSharedFolder {
            restoreSecurityScopedBookmark()
        }
    }

    /// True if the user has never completed the shared sync folder setup prompt (covers both new and existing users!).
    var needsInitialFolderSetup: Bool {
        !isConfigured
    }

    /// Sanitizes an email/account identifier identically across Android & iOS (Omyaru & Umtomi).
    nonisolated static func sanitizeAccountFolderName(_ raw: String?) -> String {
        let trimmed = (raw ?? "").trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard !trimmed.isEmpty else { return "default" }
        let forbidden = CharacterSet(charactersIn: "\\/:*?\"<>|").union(.controlCharacters)
        var cleaned = trimmed.unicodeScalars.map { forbidden.contains($0) ? "_" : String($0) }.joined()
        cleaned = cleaned.trimmingCharacters(in: CharacterSet(charactersIn: ". "))
        if cleaned.count > 120 { cleaned = String(cleaned.prefix(120)) }
        if cleaned.isEmpty || cleaned == "." || cleaned == ".." || cleaned == ".omyaru" || cleaned == ".omyaru-control" {
            return "default"
        }
        return cleaned
    }

    var currentBaseURL: URL? {
        guard isUsingSharedFolder else { return nil }
        if activeScopedURL == nil {
            restoreSecurityScopedBookmark()
        }
        return activeScopedURL
    }

    func activeAccountRoot(for accountEmail: String?) -> URL? {
        guard let base = currentBaseURL else { return nil }
        let accountFolder = Self.sanitizeAccountFolderName(accountEmail)
        let dir = base.appendingPathComponent(accountFolder, isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    func accountSubpathPreview(for accountEmail: String?) -> String {
        let account = Self.sanitizeAccountFolderName(accountEmail)
        if isUsingSharedFolder && !folderDisplayName.isEmpty {
            return "\(folderDisplayName)/\(account)"
        }
        return "앱 전용 저장소/\(account)"
    }

    func selectSharedFolder(_ pickedURL: URL, accountEmail: String?, shelf: ShelfModel) async -> Bool {
        lastError = nil
        migrationStatus = "공용 동기화 폴더 연결 및 기존 사진 확인 중..."
        defer { migrationStatus = nil }

        let gained = pickedURL.startAccessingSecurityScopedResource()
        do {
            let bookmark = try pickedURL.bookmarkData(
                options: .minimalBookmark,
                includingResourceValuesForKeys: nil,
                relativeTo: nil
            )
            // Verify we can actually create the account subfolder & write a probe file inside it.
            let accountFolder = Self.sanitizeAccountFolderName(accountEmail)
            let accountRoot = pickedURL.appendingPathComponent(accountFolder, isDirectory: true)
            let metaDir = accountRoot.appendingPathComponent(".omyaru", isDirectory: true)
            let originalsDir = accountRoot.appendingPathComponent("originals", isDirectory: true)
            let syncStateDir = metaDir.appendingPathComponent("sync-state", isDirectory: true)
            let fm = FileManager.default
            try fm.createDirectory(at: metaDir, withIntermediateDirectories: true)
            try fm.createDirectory(at: originalsDir, withIntermediateDirectories: true)
            try fm.createDirectory(at: syncStateDir, withIntermediateDirectories: true)

            let probeURL = metaDir.appendingPathComponent(".write-probe-\(UUID().uuidString)")
            try Data("ok".utf8).write(to: probeURL, options: .atomic)
            try? fm.removeItem(at: probeURL)

            // Replace previously active scoped URL cleanly.
            if hasActiveSecurityScope, let prev = activeScopedURL, prev != pickedURL {
                prev.stopAccessingSecurityScopedResource()
            }
            activeScopedURL = pickedURL
            hasActiveSecurityScope = gained

            let display = pickedURL.lastPathComponent.isEmpty ? pickedURL.path : pickedURL.lastPathComponent
            let d = UserDefaults.standard
            d.set(bookmark, forKey: Self.bookmarkKey)
            d.set(display, forKey: Self.displayNameKey)
            d.set(true, forKey: Self.useSharedKey)
            d.set(true, forKey: Self.configuredKey)

            folderDisplayName = display
            isUsingSharedFolder = true
            isConfigured = true

            // Migrate any existing local sandbox originals into the shared content-addressed originals pool!
            let migratedCount = await migrateLocalOriginalsToShared(accountRoot: accountRoot, shelf: shelf)
            shelf.reloadFromSharedStorageIfAvailable(accountEmail: accountEmail)
            shelf.status = migratedCount > 0
                ? "공용 폴더 연결 완료 (\(display)/\(accountFolder) · 원본 \(migratedCount)개 연동)"
                : "공용 폴더 연결 완료 (\(display)/\(accountFolder))"
            return true
        } catch {
            if gained { pickedURL.stopAccessingSecurityScopedResource() }
            lastError = "선택한 폴더에 쓸 수 없습니다: \(error.localizedDescription)"
            return false
        }
    }

    func chooseAppPrivateStorage() {
        if hasActiveSecurityScope, let prev = activeScopedURL {
            prev.stopAccessingSecurityScopedResource()
        }
        activeScopedURL = nil
        hasActiveSecurityScope = false
        let d = UserDefaults.standard
        d.removeObject(forKey: Self.bookmarkKey)
        d.removeObject(forKey: Self.displayNameKey)
        d.set(false, forKey: Self.useSharedKey)
        d.set(true, forKey: Self.configuredKey)
        folderDisplayName = ""
        isUsingSharedFolder = false
        isConfigured = true
        lastError = nil
    }

    private func restoreSecurityScopedBookmark() {
        guard let data = UserDefaults.standard.data(forKey: Self.bookmarkKey) else {
            isUsingSharedFolder = false
            return
        }
        var stale = false
        guard let url = try? URL(
            resolvingBookmarkData: data,
            options: [],
            relativeTo: nil,
            bookmarkDataIsStale: &stale
        ) else {
            lastError = "이전 동기화 폴더 권한이 만료되었습니다. 동기화 설정에서 폴더를 다시 지정해 주세요."
            return
        }
        let gained = url.startAccessingSecurityScopedResource()
        activeScopedURL = url
        hasActiveSecurityScope = gained
        if stale {
            if let refreshed = try? url.bookmarkData(options: .minimalBookmark, includingResourceValuesForKeys: nil, relativeTo: nil) {
                UserDefaults.standard.set(refreshed, forKey: Self.bookmarkKey)
            }
        }
    }

    // MARK: - Content-Addressed Shared Originals Pool (<AccountRoot>/originals/<sha256>.<ext>)

    nonisolated static func originalsDirectory(in accountRoot: URL) -> URL {
        let dir = accountRoot.appendingPathComponent("originals", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    nonisolated static func syncStateDirectory(in accountRoot: URL) -> URL {
        let dir = accountRoot
            .appendingPathComponent(".omyaru", isDirectory: true)
            .appendingPathComponent("sync-state", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    nonisolated static func sharedCatalogURL(in accountRoot: URL) -> URL {
        let meta = accountRoot.appendingPathComponent(".omyaru", isDirectory: true)
        try? FileManager.default.createDirectory(at: meta, withIntermediateDirectories: true)
        return meta.appendingPathComponent("shared-catalog.json")
    }

    nonisolated static func extensionForImageData(_ data: Data, fallback: String = "png") -> String {
        if data.count >= 4 {
            let b = [UInt8](data.prefix(12))
            if b[0] == 0x89 && b[1] == 0x50 { return "png" }
            if b[0] == 0xFF && b[1] == 0xD8 { return "jpg" }
            if b.count >= 3 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 { return "gif" }
            if b.count >= 12 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50 { return "webp" }
        }
        let clean = fallback.trimmingCharacters(in: CharacterSet(charactersIn: ".")).lowercased()
        return clean.isEmpty ? "png" : clean
    }

    /// Looks up an already-existing verified original inside `<accountRoot>/originals/` by SHA-256.
    nonisolated static func existingOriginalURL(sha256: String, in accountRoot: URL) -> URL? {
        let normalized = sha256.lowercased()
        guard normalized.count == 64 else { return nil }
        let dir = originalsDirectory(in: accountRoot)
        let fm = FileManager.default
        for ext in ["png", "jpg", "jpeg", "webp", "gif", "image"] {
            let candidate = dir.appendingPathComponent("\(normalized).\(ext)")
            if fm.fileExists(atPath: candidate.path) {
                return candidate
            }
        }
        return nil
    }

    /// Atomically stores image bytes in `<accountRoot>/originals/<sha256>.<ext>` with zero duplication.
    @discardableResult
    nonisolated static func storeOriginal(_ data: Data, expectedSHA256: String? = nil, fallbackExt: String = "png", in accountRoot: URL) throws -> URL {
        let hash = expectedSHA256?.lowercased() ?? OmyaruSyncCrypto.sha256Hex(data)
        if let existing = existingOriginalURL(sha256: hash, in: accountRoot) {
            return existing
        }
        let ext = extensionForImageData(data, fallback: fallbackExt)
        let dir = originalsDirectory(in: accountRoot)
        let dest = dir.appendingPathComponent("\(hash).\(ext)")
        let fm = FileManager.default
        let staging = dir.appendingPathComponent(".\(hash.prefix(12))-\(UUID().uuidString).partial")
        defer { try? fm.removeItem(at: staging) }
        try data.write(to: staging, options: .atomic)
        if fm.fileExists(atPath: dest.path) {
            return dest
        }
        do {
            try fm.moveItem(at: staging, to: dest)
        } catch {
            if fm.fileExists(atPath: dest.path) {
                return dest
            }
            throw error
        }
        return dest
    }

    private func migrateLocalOriginalsToShared(accountRoot: URL, shelf: ShelfModel) async -> Int {
        let privateRoot = shelf.privateRoot
        let items = shelf.catalog.items
        return await Task.detached(priority: .utility) {
            let fm = FileManager.default
            var count = 0
            for item in items {
                let privURL = privateRoot.appendingPathComponent(item.fileName)
                guard fm.fileExists(atPath: privURL.path),
                      let bytes = try? Data(contentsOf: privURL), !bytes.isEmpty else { continue }
                let hash = item.cloud?.sha256 ?? OmyaruSyncCrypto.sha256Hex(bytes)
                if let dest = try? Self.storeOriginal(bytes, expectedSHA256: hash, fallbackExt: privURL.pathExtension, in: accountRoot),
                   let verified = try? Data(contentsOf: dest),
                   OmyaruSyncCrypto.sha256Hex(verified) == hash {
                    // Safe cleanup of private duplicate ONLY after SHA-256 verification on shared destination!
                    try? fm.removeItem(at: privURL)
                    count += 1
                }
            }
            return count
        }.value
    }

    // MARK: - Cross-App Exclusive Kernel Sync Lock (flock + Lease Heartbeat)

    struct SyncLease: Codable {
        let app: String
        let startedAtEpochMs: Int64
        var heartbeatAtEpochMs: Int64
    }

    final class CrossAppLockHandle: @unchecked Sendable {
        private var fd: Int32
        private let leaseURL: URL?

        init(fd: Int32, leaseURL: URL?) {
            self.fd = fd
            self.leaseURL = leaseURL
        }

        func refreshHeartbeat(appName: String, startedAt: Int64) {
            guard let leaseURL else { return }
            let now = Int64(Date().timeIntervalSince1970 * 1000)
            let lease = SyncLease(app: appName, startedAtEpochMs: startedAt, heartbeatAtEpochMs: now)
            if let data = try? JSONEncoder().encode(lease) {
                try? data.write(to: leaseURL, options: .atomic)
            }
        }

        func release() {
            guard fd >= 0 else { return }
            if let leaseURL {
                try? FileManager.default.removeItem(at: leaseURL)
            }
            flock(fd, LOCK_UN)
            close(fd)
            fd = -1
        }

        deinit {
            release()
        }
    }

    /// Acquires a kernel-enforced cross-process exclusive flock on `<accountRoot>/.omyaru/sync.lock`.
    /// If another app (e.g., Umtomi) is currently syncing the same folder, waits cleanly or returns nil in background mode.
    nonisolated static func acquireSyncLock(
        in accountRoot: URL,
        appName: String = appIdentifier,
        maxWaitSeconds: TimeInterval,
        onWaiting: (@Sendable (String) -> Void)? = nil
    ) async -> CrossAppLockHandle? {
        let meta = accountRoot.appendingPathComponent(".omyaru", isDirectory: true)
        try? FileManager.default.createDirectory(at: meta, withIntermediateDirectories: true)
        let lockURL = meta.appendingPathComponent("sync.lock")
        let leaseURL = meta.appendingPathComponent("sync-lease.json")
        let deadline = Date().addingTimeInterval(maxWaitSeconds)

        while true {
            if Task.isCancelled { return nil }
            let fd = open(lockURL.path, O_CREAT | O_RDWR, 0o600)
            if fd >= 0 {
                if flock(fd, LOCK_EX | LOCK_NB) == 0 {
                    let now = Int64(Date().timeIntervalSince1970 * 1000)
                    let handle = CrossAppLockHandle(fd: fd, leaseURL: leaseURL)
                    handle.refreshHeartbeat(appName: appName, startedAt: now)
                    return handle
                }
                close(fd)
            }
            if Date() >= deadline { return nil }
            let holderName: String = {
                if let data = try? Data(contentsOf: leaseURL),
                   let lease = try? JSONDecoder().decode(SyncLease.self, from: data),
                   !lease.app.isEmpty {
                    return lease.app
                }
                return "다른 앱"
            }()
            onWaiting?("\(holderName)에서 같은 동기화 폴더를 동기화 중입니다. 완료를 기다리는 중...")
            try? await Task.sleep(nanoseconds: 400_000_000)
        }
    }

    /// Executes a catalog read/write block under both `NSFileCoordinator` and `flock(write.lock)` across processes.
    nonisolated static func withCatalogWriteLock<T>(in accountRoot: URL, _ body: () throws -> T) throws -> T {
        let meta = accountRoot.appendingPathComponent(".omyaru", isDirectory: true)
        try FileManager.default.createDirectory(at: meta, withIntermediateDirectories: true)
        let lockURL = meta.appendingPathComponent("write.lock")
        let fd = open(lockURL.path, O_CREAT | O_RDWR, 0o600)
        if fd >= 0 {
            flock(fd, LOCK_EX)
        }
        defer {
            if fd >= 0 {
                flock(fd, LOCK_UN)
                close(fd)
            }
        }
        var coordinatorError: NSError?
        var blockResult: Result<T, Error>?
        let coordinator = NSFileCoordinator()
        coordinator.coordinate(writingItemAt: sharedCatalogURL(in: accountRoot), options: [], error: &coordinatorError) { _ in
            blockResult = Result { try body() }
        }
        if let coordinatorError { throw coordinatorError }
        guard let blockResult else {
            throw NSError(domain: "SharedSyncFolder", code: -1, userInfo: [NSLocalizedDescriptionKey: "공용 카탈로그 잠금 실패"])
        }
        return try blockResult.get()
    }
}

// MARK: - Unified Cross-App CRDT Shelf Replica (Exact Wire Parity with Umtomi iOS DriveReplica)

struct OmyaruShelfReplica: @unchecked Sendable {
    let fileURL: URL
    private(set) var state: [String: Any]

    init(directory: URL) {
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        fileURL = directory.appendingPathComponent("shelf.json")
        if let data = try? Data(contentsOf: fileURL),
           let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           obj["registers"] is [String: Any],
           obj["baseline"] is [String: Any],
           obj["seen"] is [String],
           obj["pending"] is [[String: Any]],
           obj["actor"] is String {
            state = obj
        } else {
            state = [
                "actor": OmyaruSyncCrypto.compactUUID(),
                "clock": Int64(0),
                "registers": [String: Any](),
                "baseline": [String: Any](),
                "seen": [String](),
                "pending": [[String: Any]]()
            ]
        }
    }

    var baseline: [String: Any] { state["baseline"] as? [String: Any] ?? [:] }
    var pending: [[String: Any]] { state["pending"] as? [[String: Any]] ?? [] }
    var seen: Set<String> { Set(state["seen"] as? [String] ?? []) }

    func save() throws {
        try FileManager.default.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        let data = try JSONSerialization.data(withJSONObject: state, options: [.sortedKeys])
        try data.write(to: fileURL, options: .atomic)
    }

    mutating func acknowledge(_ projected: [String: Any]) {
        state["baseline"] = projected
    }

    mutating func markSeen(_ id: String) {
        var s = seen
        s.insert(id)
        state["seen"] = Array(s).sorted()
    }

    mutating func markSeenBatch(_ ids: [String]) {
        var s = seen
        for id in ids { s.insert(id) }
        state["seen"] = Array(s).sorted()
    }

    mutating func popPending(uploadedID: String) {
        var list = pending
        if !list.isEmpty { list.removeFirst() }
        state["pending"] = list
        markSeen(uploadedID)
    }

    private static func jsonEqual(_ a: Any?, _ b: Any?) -> Bool {
        guard let a, let b else { return a == nil && b == nil }
        guard let da = try? JSONSerialization.data(withJSONObject: a, options: [.sortedKeys, .fragmentsAllowed]),
              let db = try? JSONSerialization.data(withJSONObject: b, options: [.sortedKeys, .fragmentsAllowed]) else {
            return false
        }
        return da == db
    }

    /// Captures ONLY delta changes vs `baseline`, avoiding full bookshelf re-uploads!
    mutating func capture(_ local: [String: Any]) {
        let before = baseline
        var changes: [String: Any] = [:]
        for (key, value) in local where !Self.jsonEqual(before[key], value) {
            changes[key] = value
        }
        let currentEntities = Set(local.keys.map { $0.split(separator: "/").dropLast().joined(separator: "/") })
        for key in before.keys {
            let entity = key.split(separator: "/").dropLast().joined(separator: "/")
            if !currentEntities.contains(entity) {
                changes[entity + "/$deleted"] = true
            }
        }
        guard !changes.isEmpty else { return }
        let sortedKeys = changes.keys.sorted()
        let actor = (state["actor"] as? String) ?? OmyaruSyncCrypto.compactUUID()
        for offset in stride(from: 0, to: sortedKeys.count, by: 64) {
            let slice = sortedKeys[offset..<min(sortedKeys.count, offset + 64)]
            let nextClock = ((state["clock"] as? NSNumber)?.int64Value ?? 0) + 1
            let rows: [[String: Any]] = slice.compactMap { key in
                let parts = key.split(separator: "/").map(String.init)
                guard parts.count == 3, let val = changes[key] else { return nil }
                return ["kind": parts[0], "id": parts[1], "field": parts[2], "value": val]
            }
            guard !rows.isEmpty else { continue }
            let batch: [String: Any] = [
                "protocol": 1,
                "id": OmyaruSyncCrypto.compactUUID(),
                "actor": actor,
                "clock": nextClock,
                "changes": rows
            ]
            apply(batch)
            state["pending"] = pending + [batch]
        }
        acknowledge(local)
    }

    mutating func apply(_ batch: [String: Any]) {
        guard let actor = batch["actor"] as? String,
              let clock = (batch["clock"] as? NSNumber)?.int64Value,
              clock > 0,
              let rows = batch["changes"] as? [[String: Any]] else { return }
        var registers = state["registers"] as? [String: [String: Any]] ?? [:]
        for row in rows {
            guard let kind = row["kind"] as? String,
                  let entityID = row["id"] as? String,
                  let field = row["field"] as? String,
                  let value = row["value"] else { continue }
            let key = "\(kind)/\(entityID)/\(field)"
            let old = registers[key]
            let oldClock = (old?["clock"] as? NSNumber)?.int64Value ?? 0
            let oldActor = old?["actor"] as? String ?? ""
            if clock > oldClock || (clock == oldClock && actor > oldActor) {
                registers[key] = ["actor": actor, "clock": clock, "value": value]
            }
        }
        state["registers"] = registers
        let currentClock = (state["clock"] as? NSNumber)?.int64Value ?? 0
        state["clock"] = max(clock, currentClock)
    }

    func project() -> [String: Any] {
        let registers = state["registers"] as? [String: [String: Any]] ?? [:]
        var snapshot: [String: Any] = [:]
        for (key, row) in registers {
            guard let value = row["value"], !(value is NSNull) else { continue }
            let entity = key.split(separator: "/").dropLast().joined(separator: "/")
            if registers[entity + "/$deleted"] != nil { continue }
            snapshot[key] = value
        }
        return snapshot
    }
}

// MARK: - First-Sync / Existing-User Shared Folder Picker Sheet

struct SharedSyncFolderSetupSheet: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var manager: SharedSyncFolderManager = .shared
    @ObservedObject var shelf: ShelfModel
    let accountEmail: String
    var onCompleted: (() -> Void)? = nil

    @State private var showingFolderPicker = false
    @State private var busy = false

    var body: some View {
        NavigationStack {
            VStack(alignment: .leading, spacing: 18) {
                HStack(spacing: 12) {
                    Image(systemName: "folder.badge.gearshape")
                        .font(.system(size: 32))
                        .foregroundStyle(.blue)
                    VStack(alignment: .leading, spacing: 4) {
                        Text("책장 동기화 폴더 지정")
                            .font(.title3.weight(.bold))
                        Text("Umtomi와 같은 폴더를 지정하면 원본을 1벌만 공유합니다.")
                            .font(.footnote)
                            .foregroundStyle(.secondary)
                    }
                }

                VStack(alignment: .leading, spacing: 10) {
                    Label("중복 저장 방지 (용량 50% 절약)", systemImage: "externaldrive.badge.checkmark")
                        .font(.subheadline.weight(.semibold))
                    Text("Omyaru 브라우저와 Umtomi에서 같은 폴더(예: 나의 iPhone › Omyaru_Bookshelf)를 선택하면 동기화된 책장 사진이 2배로 저장되지 않습니다.")
                        .font(.caption)
                        .foregroundStyle(.secondary)

                    Divider()

                    Label("계정별 자동 폴더 분리 & 동시 동기화 충돌 방지", systemImage: "person.2.badge.key")
                        .font(.subheadline.weight(.semibold))
                    Text("선택한 폴더 안에 현재 Google 계정(\(SharedSyncFolderManager.sanitizeAccountFolderName(accountEmail))) 이름의 하위 폴더가 자동 생성됩니다. 두 앱이 동시에 동기화해도 잠금(Lock)으로 안전하게 순차 처리됩니다.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                .padding(14)
                .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 14))

                if manager.isUsingSharedFolder && !manager.folderDisplayName.isEmpty {
                    HStack {
                        Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
                        Text("현재 경로: \(manager.accountSubpathPreview(for: accountEmail))")
                            .font(.caption.monospaced())
                            .lineLimit(2)
                    }
                    .padding(10)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(Color.green.opacity(0.12), in: RoundedRectangle(cornerRadius: 10))
                }

                if let status = manager.migrationStatus {
                    HStack(spacing: 8) {
                        ProgressView().controlSize(.small)
                        Text(status).font(.caption).foregroundStyle(.secondary)
                    }
                }

                if let err = manager.lastError {
                    Text(err)
                        .font(.caption)
                        .foregroundStyle(.red)
                }

                Spacer()

                Button {
                    showingFolderPicker = true
                } label: {
                    HStack {
                        Spacer()
                        Image(systemName: "folder.fill.badge.plus")
                        Text("동기화 공용 폴더 선택하기")
                            .font(.headline)
                        Spacer()
                    }
                    .padding(.vertical, 12)
                }
                .buttonStyle(.borderedProminent)
                .disabled(busy)

                Button {
                    manager.chooseAppPrivateStorage()
                    dismiss()
                    onCompleted?()
                } label: {
                    HStack {
                        Spacer()
                        Text("공유 안 함 (이 앱 전용 저장소 사용)")
                            .font(.subheadline)
                        Spacer()
                    }
                    .padding(.vertical, 8)
                }
                .buttonStyle(.bordered)
                .disabled(busy)
            }
            .padding(20)
            .navigationTitle("동기화 폴더 설정")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                if manager.isConfigured {
                    ToolbarItem(placement: .cancellationAction) {
                        Button("닫기") { dismiss() }
                    }
                }
            }
            .fileImporter(
                isPresented: $showingFolderPicker,
                allowedContentTypes: [.folder]
            ) { result in
                if case let .success(url) = result {
                    busy = true
                    Task {
                        let ok = await manager.selectSharedFolder(url, accountEmail: accountEmail, shelf: shelf)
                        busy = false
                        if ok {
                            dismiss()
                            onCompleted?()
                        }
                    }
                }
            }
        }
        .preferredColorScheme(.dark)
    }
}
