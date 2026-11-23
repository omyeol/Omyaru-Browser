import SwiftUI
import Foundation
import CryptoKit
import CommonCrypto
import Security
import AuthenticationServices
import UniformTypeIdentifiers
import UIKit

// MARK: - Omyaru Cross-Platform Encrypted Sync Crypto (Android/Windows/iOS Wire Compatible)

enum OmyaruSyncCrypto {
    static let format = "privacy-browser-sync"
    static let aad = Data("privacy-browser-sync-v1".utf8)
    static let defaultIterations = 210_000

    static func sha256Hex(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }

    static func sha256Hex(fileAt url: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hasher = SHA256()
        while let chunk = try handle.read(upToCount: 512 * 1024), !chunk.isEmpty {
            hasher.update(data: chunk)
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    static func compactUUID(_ id: UUID = UUID()) -> String {
        id.uuidString.replacingOccurrences(of: "-", with: "").lowercased()
    }

    static func encrypt(_ plain: Data, password: String, accountKeyID: String? = nil) throws -> Data {
        var salt = [UInt8](repeating: 0, count: 16)
        guard SecRandomCopyBytes(kSecRandomDefault, 16, &salt) == errSecSuccess else {
            throw NSError(domain: "OmyaruSync", code: -1, userInfo: [NSLocalizedDescriptionKey: "보안 난수 생성 실패"])
        }
        let saltData = Data(salt)
        let key = try deriveKey(password: password, salt: saltData, iterations: defaultIterations)
        let sealed = try AES.GCM.seal(plain, using: key, authenticating: aad)
        var envelope: [String: Any] = [
            "format": format,
            "version": 1,
            "kdf": [
                "name": "pbkdf2-sha256",
                "iterations": defaultIterations,
                "salt": saltData.base64EncodedString()
            ],
            "cipher": [
                "name": "aes-256-gcm",
                "nonce": Data(sealed.nonce).base64EncodedString(),
                "ciphertext": (sealed.ciphertext + sealed.tag).base64EncodedString()
            ]
        ]
        if let accountKeyID, !accountKeyID.isEmpty {
            envelope["accountKeyId"] = accountKeyID
        }
        return try JSONSerialization.data(withJSONObject: envelope, options: [.sortedKeys])
    }

    static func decrypt(_ envelopeData: Data, password: String) throws -> Data {
        guard let envelope = try JSONSerialization.jsonObject(with: envelopeData) as? [String: Any],
              envelope["format"] as? String == format,
              envelope["version"] as? Int == 1,
              let kdf = envelope["kdf"] as? [String: Any],
              kdf["name"] as? String == "pbkdf2-sha256",
              let iterations = kdf["iterations"] as? Int,
              (100_000...1_000_000).contains(iterations),
              let saltB64 = kdf["salt"] as? String,
              let salt = Data(base64Encoded: saltB64), salt.count == 16,
              let cipher = envelope["cipher"] as? [String: Any],
              cipher["name"] as? String == "aes-256-gcm",
              let nonceB64 = cipher["nonce"] as? String,
              let nonce = Data(base64Encoded: nonceB64), nonce.count == 12,
              let bodyB64 = cipher["ciphertext"] as? String,
              let body = Data(base64Encoded: bodyB64), body.count >= 16 else {
            throw NSError(domain: "OmyaruSync", code: -2, userInfo: [NSLocalizedDescriptionKey: "유효하지 않은 Omyaru 암호화 봉투 형식입니다."])
        }
        let key = try deriveKey(password: password, salt: salt, iterations: iterations)
        let box = try AES.GCM.SealedBox(
            nonce: AES.GCM.Nonce(data: nonce),
            ciphertext: body.dropLast(16),
            tag: body.suffix(16)
        )
        return try AES.GCM.open(box, using: key, authenticating: aad)
    }

    private static func deriveKey(password: String, salt: Data, iterations: Int) throws -> SymmetricKey {
        var derived = [UInt8](repeating: 0, count: 32)
        let status = salt.withUnsafeBytes { saltBuf in
            password.withCString { passPtr in
                CCKeyDerivationPBKDF(
                    CCPBKDFAlgorithm(kCCPBKDF2),
                    passPtr,
                    password.utf8.count,
                    saltBuf.bindMemory(to: UInt8.self).baseAddress,
                    salt.count,
                    CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256),
                    UInt32(iterations),
                    &derived,
                    32
                )
            }
        }
        guard status == kCCSuccess else {
            throw NSError(domain: "OmyaruSync", code: -3, userInfo: [NSLocalizedDescriptionKey: "PBKDF2 키 파생 실패"])
        }
        return SymmetricKey(data: derived)
    }
}

// MARK: - Sendable KeyRing Supporting Multi-Core Off-MainActor PBKDF2 Encryption/Decryption

struct OmyaruDriveKeyRing: Codable, Sendable {
    var keys: [String: String] = [:]
    var legacyPasswords: [String] = []
    var keyFileIDs: [String] = []

    var primaryKey: (id: String, secret: String)? {
        guard let firstID = keys.keys.sorted().first, let secret = keys[firstID] else { return nil }
        return (firstID, secret)
    }

    mutating func addRecord(_ data: Data, expectedID: String) throws {
        guard let obj = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              obj["format"] as? String == "omyaru-account-key",
              obj["version"] as? Int == 1,
              obj["id"] as? String == expectedID,
              let secret = obj["secret"] as? String,
              Data(base64Encoded: secret)?.count == 32 else {
            return
        }
        keys[expectedID] = secret
        if let legacyText = obj["legacyEnvelope"] as? String,
           let legacyData = legacyText.data(using: .utf8),
           let plain = try? OmyaruSyncCrypto.decrypt(legacyData, password: secret),
           let legacyObj = try? JSONSerialization.jsonObject(with: plain) as? [String: Any],
           let pass = legacyObj["passphrase"] as? String, pass.count >= 6 {
            if !legacyPasswords.contains(pass) {
                legacyPasswords.append(pass)
            }
        }
    }

    func encrypt(_ plain: Data) throws -> Data {
        guard let primary = primaryKey else {
            throw NSError(domain: "OmyaruSync", code: -10, userInfo: [NSLocalizedDescriptionKey: "동기화 계정 키가 없습니다."])
        }
        return try OmyaruSyncCrypto.encrypt(plain, password: primary.secret, accountKeyID: primary.id)
    }

    func decrypt(_ envelopeData: Data) throws -> Data {
        if let obj = try? JSONSerialization.jsonObject(with: envelopeData) as? [String: Any],
           let keyID = obj["accountKeyId"] as? String,
           let secret = keys[keyID] {
            return try OmyaruSyncCrypto.decrypt(envelopeData, password: secret)
        }
        for pass in legacyPasswords {
            if let plain = try? OmyaruSyncCrypto.decrypt(envelopeData, password: pass) {
                return plain
            }
        }
        if let primary = primaryKey, let plain = try? OmyaruSyncCrypto.decrypt(envelopeData, password: primary.secret) {
            return plain
        }
        throw NSError(domain: "OmyaruSync", code: -11, userInfo: [NSLocalizedDescriptionKey: "암호화된 동기화 레코드를 복호화할 수 없습니다."])
    }

    func encryptAsync(_ plain: Data) async throws -> Data {
        let ring = self
        return try await Task.detached(priority: .userInitiated) {
            try ring.encrypt(plain)
        }.value
    }

    func decryptAsync(_ envelopeData: Data) async throws -> Data {
        let ring = self
        return try await Task.detached(priority: .userInitiated) {
            try ring.decrypt(envelopeData)
        }.value
    }
}

// MARK: - High-Throughput Non-Isolated Google Drive REST Client (HTTP/2 + Concurrent Pool)

private struct DriveFileEntry: Decodable, Sendable {
    let id: String
    let name: String
}

private struct DriveRESTClient: Sendable {
    let accessToken: String

    private static let fastSession: URLSession = {
        let cfg = URLSessionConfiguration.ephemeral
        cfg.httpMaximumConnectionsPerHost = 8
        cfg.timeoutIntervalForRequest = 25
        cfg.timeoutIntervalForResource = 90
        cfg.urlCache = nil
        cfg.requestCachePolicy = .reloadIgnoringLocalCacheData
        return URLSession(configuration: cfg)
    }()

    struct UnauthorizedError: Error {}

    func request(_ req: URLRequest) async throws -> Data {
        try Task.checkCancellation()
        var signed = req
        signed.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        let (data, resp) = try await Self.fastSession.data(for: signed)
        guard let http = resp as? HTTPURLResponse else {
            throw NSError(domain: "OmyaruDrive", code: -1, userInfo: [NSLocalizedDescriptionKey: "잘못된 응답"])
        }
        if http.statusCode == 401 {
            throw UnauthorizedError()
        }
        guard (200..<300).contains(http.statusCode) else {
            throw NSError(domain: "OmyaruDrive", code: http.statusCode, userInfo: [NSLocalizedDescriptionKey: "Google Drive 요청 실패 (HTTP \(http.statusCode))"])
        }
        return data
    }

    /// Lists all non-trashed files in `appDataFolder` in one sweep (replacing 5 separate sequential list calls!).
    func listAllAppDataFiles() async throws -> [DriveFileEntry] {
        struct FileListResponse: Decodable {
            let files: [DriveFileEntry]
            let nextPageToken: String?
        }
        var results: [DriveFileEntry] = []
        var pageToken: String? = nil
        var seenTokens = Set<String>()
        repeat {
            var comps = URLComponents(string: "https://www.googleapis.com/drive/v3/files")!
            var query = [
                URLQueryItem(name: "spaces", value: "appDataFolder"),
                URLQueryItem(name: "pageSize", value: "1000"),
                URLQueryItem(name: "fields", value: "nextPageToken,files(id,name)"),
                URLQueryItem(name: "q", value: "trashed = false")
            ]
            if let pageToken { query.append(URLQueryItem(name: "pageToken", value: pageToken)) }
            comps.queryItems = query
            let data = try await request(URLRequest(url: comps.url!, timeoutInterval: 25))
            let page = try JSONDecoder().decode(FileListResponse.self, from: data)
            results.append(contentsOf: page.files)
            pageToken = page.nextPageToken
            if let pageToken, !seenTokens.insert(pageToken).inserted { break }
        } while pageToken != nil && results.count < 20_000
        return results
    }

    func listTabSyncFiles(includeKeys: Bool) async throws -> [DriveFileEntry] {
        struct FileListResponse: Decodable {
            let files: [DriveFileEntry]
            let nextPageToken: String?
        }
        let q = includeKeys
            ? "trashed = false and (name contains 'omyaru-tabs-v' or name contains 'omyaru-tab-reset-v1-' or name contains 'omyaru-account-key-v1-')"
            : "trashed = false and (name contains 'omyaru-tabs-v' or name contains 'omyaru-tab-reset-v1-')"
        var results: [DriveFileEntry] = []
        var pageToken: String? = nil
        var seenTokens = Set<String>()
        repeat {
            var comps = URLComponents(string: "https://www.googleapis.com/drive/v3/files")!
            var query = [
                URLQueryItem(name: "spaces", value: "appDataFolder"),
                URLQueryItem(name: "pageSize", value: "1000"),
                URLQueryItem(name: "fields", value: "nextPageToken,files(id,name)"),
                URLQueryItem(name: "q", value: q)
            ]
            if let pageToken { query.append(URLQueryItem(name: "pageToken", value: pageToken)) }
            comps.queryItems = query
            let data = try await request(URLRequest(url: comps.url!, timeoutInterval: 20))
            let page = try JSONDecoder().decode(FileListResponse.self, from: data)
            results.append(contentsOf: page.files)
            pageToken = page.nextPageToken
            if let pageToken, !seenTokens.insert(pageToken).inserted { break }
        } while pageToken != nil && results.count < 10_000
        return results
    }

    func readDriveFile(_ fileID: String) async throws -> Data {
        var comps = URLComponents(string: "https://www.googleapis.com/drive/v3/files/\(fileID)")!
        comps.queryItems = [URLQueryItem(name: "alt", value: "media")]
        return try await request(URLRequest(url: comps.url!, timeoutInterval: 30))
    }

    @discardableResult
    func uploadDriveFile(name: String, payload: Data) async throws -> String {
        let boundary = "omyaru-\(UUID().uuidString)"
        var body = Data(capacity: payload.count + 512)
        let meta: [String: Any] = [
            "name": name,
            "parents": ["appDataFolder"],
            "mimeType": "application/json"
        ]
        let metaData = try JSONSerialization.data(withJSONObject: meta)
        body.append(Data("--\(boundary)\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n".utf8))
        body.append(metaData)
        body.append(Data("\r\n--\(boundary)\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n".utf8))
        body.append(payload)
        body.append(Data("\r\n--\(boundary)--\r\n".utf8))

        var req = URLRequest(url: URL(string: "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id")!, timeoutInterval: 45)
        req.httpMethod = "POST"
        req.setValue("multipart/related; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        req.httpBody = body

        let data = try await request(req)
        guard let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let id = json["id"] as? String else {
            throw NSError(domain: "OmyaruDrive", code: -3, userInfo: [NSLocalizedDescriptionKey: "업로드 파일 ID를 확인하지 못했습니다."])
        }
        return id
    }
}

// MARK: - Unified Portable Sync Bundle (Offline / AirDrop / Files)

struct OmyaruFullSyncBundle: Codable {
    var version: Int = 2
    var exportedAtEpochMs: Int64 = Int64(Date().timeIntervalSince1970 * 1000)
    var profiles: [BrowserProfile]
    var groups: [BrowserGroup]
    var tabs: [BrowserTab]
    var bookmarkFolders: [BookmarkFolder] = []
    var bookmarks: [BrowserBookmark]
    var history: [HistoryEntry]
    var bookshelf: BookshelfCatalogData
    var bookshelfImagesBase64: [String: String] = [:]
}

// MARK: - Google Drive AppData & High-Speed Parallel Chunked Bookshelf Sync Coordinator

@MainActor
final class DriveSyncManager: NSObject, ObservableObject, ASWebAuthenticationPresentationContextProviding {
    static let shared = DriveSyncManager()
    static let tabResetConfirmationPhrase = "동기화 탭 초기화"
    nonisolated static let chunkSize = 1024 * 1024
    private static let maxConcurrentTransfers = 6
    /// Built-in public iOS OAuth client identifier for Omyaru Google Cloud project 136563471424.
    private static let defaultBuiltInClientID = "136563471424-n3bgne716cvcm8kdk767niqpommo04k2.apps.googleusercontent.com"
    private static let keychainService = "cuttan.omyaru.app.drive-sync"

    @Published var connectedEmail: String {
        didSet { UserDefaults.standard.set(connectedEmail, forKey: "omyaru.ios.drive.email") }
    }
    @Published var syncPassphrase: String {
        didSet { Self.saveKeychainString(syncPassphrase, account: "sync_passphrase") }
    }
    @Published var autoSyncEnabled: Bool {
        didSet { UserDefaults.standard.set(autoSyncEnabled, forKey: "omyaru.ios.drive.autoSync") }
    }
    @Published var backgroundSyncEnabled: Bool {
        didSet {
            UserDefaults.standard.set(backgroundSyncEnabled, forKey: "omyaru.ios.drive.backgroundSync")
            OmyaruBackgroundSyncScheduler.shared.scheduleIfNeeded(enabled: backgroundSyncEnabled, isConnected: isConnected)
            if !backgroundSyncEnabled {
                ActiveSyncBackgroundKeepAlive.shared.end()
            }
        }
    }
    @Published var liveActivityEnabled: Bool {
        didSet {
            UserDefaults.standard.set(liveActivityEnabled, forKey: "omyaru.ios.drive.liveActivity")
            if liveActivityEnabled {
                OmyaruSyncLiveActivityController.shared.requestFallbackNotificationPermissionIfNeeded()
            }
        }
    }
    @Published var onDemandCloudPhotos: Bool {
        didSet { UserDefaults.standard.set(onDemandCloudPhotos, forKey: "omyaru.ios.drive.onDemand") }
    }
    @Published private(set) var isConnected: Bool = false
    @Published var busy: Bool = false
    @Published var status: String = "Google 계정을 연결하면 Android · PC · iOS가 자동 동기화됩니다."
    @Published var progress: Double? = nil
    @Published var lastSyncedAt: Date? = nil
    private var cancelRequested: Bool = false
    private var tabSyncBusy: Bool = false
    private var quickTabSyncTask: Task<Void, Never>? = nil

    private var accessToken: String = "" {
        didSet { persistAuthSession() }
    }
    private var refreshToken: String = "" {
        didSet { persistAuthSession() }
    }
    private var cachedKeyRing: OmyaruDriveKeyRing? = nil

    private struct PersistedAuthSession: Codable {
        var accessToken: String
        var refreshToken: String
        var email: String
    }

    private var authSessionFileURL: URL {
        syncStateDirectory.appendingPathComponent("google-session.json")
    }

    private var keyRingCacheURL: URL {
        let acct = SharedSyncFolderManager.sanitizeAccountFolderName(connectedEmail)
        return syncStateDirectory.appendingPathComponent("keyring-\(acct).json")
    }

    private func persistAuthSession() {
        let connected = !accessToken.isEmpty || !refreshToken.isEmpty
        if isConnected != connected { isConnected = connected }
        Self.saveKeychainString(accessToken, account: "access_token")
        Self.saveKeychainString(refreshToken, account: "refresh_token")

        if connected {
            let payload = PersistedAuthSession(
                accessToken: accessToken,
                refreshToken: refreshToken,
                email: connectedEmail
            )
            if let data = try? JSONEncoder().encode(payload) {
                try? data.write(to: authSessionFileURL, options: [.atomic, .completeFileProtectionUntilFirstUserAuthentication])
                var res = URLResourceValues()
                res.isExcludedFromBackup = true
                var mutableURL = authSessionFileURL
                try? mutableURL.setResourceValues(res)
            }
        } else {
            try? FileManager.default.removeItem(at: authSessionFileURL)
        }
    }

    private var resolvedClientID: String {
        if let plist = (Bundle.main.object(forInfoDictionaryKey: "GIDClientID") as? String)?
            .trimmingCharacters(in: .whitespacesAndNewlines),
           plist.hasSuffix(".apps.googleusercontent.com") {
            return plist
        }
        return Self.defaultBuiltInClientID
    }

    private var syncStateDirectory: URL {
        let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("OmyaruDriveSync", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    private func accountBrowserSyncDir() -> URL {
        let acct = SharedSyncFolderManager.sanitizeAccountFolderName(connectedEmail)
        let dir = syncStateDirectory.appendingPathComponent("browser-\(acct)", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    private static func readKeychainString(account: String) -> String? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: keychainService,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne
        ]
        var result: AnyObject?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    @discardableResult
    private static func saveKeychainString(_ value: String, account: String) -> Bool {
        let baseQuery: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: keychainService,
            kSecAttrAccount as String: account
        ]
        SecItemDelete(baseQuery as CFDictionary)
        guard !value.isEmpty, let data = value.data(using: .utf8) else { return true }
        var item = baseQuery
        item[kSecValueData as String] = data
        item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        return SecItemAdd(item as CFDictionary, nil) == errSecSuccess
    }

    override init() {
        let d = UserDefaults.standard
        connectedEmail = d.string(forKey: "omyaru.ios.drive.email") ?? ""
        autoSyncEnabled = d.object(forKey: "omyaru.ios.drive.autoSync") == nil ? true : d.bool(forKey: "omyaru.ios.drive.autoSync")
        backgroundSyncEnabled = d.object(forKey: "omyaru.ios.drive.backgroundSync") == nil ? true : d.bool(forKey: "omyaru.ios.drive.backgroundSync")
        liveActivityEnabled = d.object(forKey: "omyaru.ios.drive.liveActivity") == nil ? true : d.bool(forKey: "omyaru.ios.drive.liveActivity")
        onDemandCloudPhotos = d.bool(forKey: "omyaru.ios.drive.onDemand")

        let kcPass = Self.readKeychainString(account: "sync_passphrase") ?? d.string(forKey: "omyaru.ios.drive.passphrase") ?? ""
        syncPassphrase = kcPass
        super.init()

        let fileSession: PersistedAuthSession? = (try? Data(contentsOf: authSessionFileURL))
            .flatMap { try? JSONDecoder().decode(PersistedAuthSession.self, from: $0) }
        let restoredAccess = Self.readKeychainString(account: "access_token")
            ?? fileSession?.accessToken
            ?? d.string(forKey: "omyaru.ios.drive.accessToken")
            ?? ""
        let restoredRefresh = Self.readKeychainString(account: "refresh_token")
            ?? fileSession?.refreshToken
            ?? d.string(forKey: "omyaru.ios.drive.refreshToken")
            ?? ""
        if connectedEmail.isEmpty, let savedEmail = fileSession?.email, !savedEmail.isEmpty {
            connectedEmail = savedEmail
        }

        accessToken = restoredAccess
        refreshToken = restoredRefresh
        isConnected = !restoredAccess.isEmpty || !restoredRefresh.isEmpty
        if isConnected {
            persistAuthSession()
            d.removeObject(forKey: "omyaru.ios.drive.clientId")
            d.removeObject(forKey: "omyaru.ios.drive.accessToken")
            d.removeObject(forKey: "omyaru.ios.drive.refreshToken")
            if let cachedData = try? Data(contentsOf: keyRingCacheURL),
               let ring = try? JSONDecoder().decode(OmyaruDriveKeyRing.self, from: cachedData) {
                cachedKeyRing = ring
            }
            status = connectedEmail.isEmpty ? "Google Drive 연결됨" : "\(connectedEmail) 연결됨"
            if !refreshToken.isEmpty {
                Task { @MainActor [weak self] in
                    _ = await self?.refreshAccessTokenIfPossible()
                }
            }
        }
    }

    func presentationAnchor(for session: ASWebAuthenticationSession) -> ASPresentationAnchor {
        UIApplication.shared.connectedScenes
            .compactMap { $0 as? UIWindowScene }
            .flatMap(\.windows)
            .first(where: \.isKeyWindow) ?? ASPresentationAnchor()
    }

    // MARK: - Built-In One-Tap Google OAuth 2.0 PKCE Sign-In & Refresh

    @discardableResult
    func signInWithGoogle() async -> Bool {
        let trimmedClient = resolvedClientID
        let prefix = String(trimmedClient.dropLast(".apps.googleusercontent.com".count))
        let callbackScheme = "com.googleusercontent.apps.\(prefix)"
        let redirectURI = "\(callbackScheme):/oauth2redirect/google"
        let verifier = generateCodeVerifier()
        let challenge = codeChallenge(for: verifier)

        var comps = URLComponents(string: "https://accounts.google.com/o/oauth2/v2/auth")!
        comps.queryItems = [
            URLQueryItem(name: "client_id", value: trimmedClient),
            URLQueryItem(name: "redirect_uri", value: redirectURI),
            URLQueryItem(name: "response_type", value: "code"),
            URLQueryItem(name: "scope", value: "https://www.googleapis.com/auth/drive.appdata"),
            URLQueryItem(name: "code_challenge", value: challenge),
            URLQueryItem(name: "code_challenge_method", value: "S256"),
            URLQueryItem(name: "access_type", value: "offline"),
            URLQueryItem(name: "prompt", value: "consent select_account")
        ]
        guard let authURL = comps.url else { return false }

        busy = true
        status = "Google 계정 선택 대기 중..."
        defer { busy = false }

        do {
            let callbackURL: URL = try await withCheckedThrowingContinuation { continuation in
                let session = ASWebAuthenticationSession(url: authURL, callbackURLScheme: callbackScheme) { url, error in
                    if let error {
                        continuation.resume(throwing: error)
                    } else if let url {
                        continuation.resume(returning: url)
                    } else {
                        continuation.resume(throwing: NSError(domain: "OmyaruOAuth", code: -1, userInfo: [NSLocalizedDescriptionKey: "로그인이 취소되었습니다."]))
                    }
                }
                session.presentationContextProvider = self
                session.prefersEphemeralWebBrowserSession = false
                session.start()
            }

            let items = URLComponents(url: callbackURL, resolvingAgainstBaseURL: false)?.queryItems ?? []
            if let errCode = items.first(where: { $0.name == "error" })?.value {
                status = errCode == "access_denied" ? "Google 권한 승인이 취소되었습니다." : "Google 로그인 오류: \(errCode)"
                return false
            }
            guard let code = items.first(where: { $0.name == "code" })?.value, !code.isEmpty else {
                status = "Google 인증 코드를 받지 못했습니다."
                return false
            }

            status = "Google 로그인 완료 중..."
            var tokenReq = URLRequest(url: URL(string: "https://oauth2.googleapis.com/token")!)
            tokenReq.httpMethod = "POST"
            tokenReq.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
            let formAllowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "-._~"))
            func enc(_ s: String) -> String { s.addingPercentEncoding(withAllowedCharacters: formAllowed) ?? s }
            let bodyItems = [
                "client_id=\(enc(trimmedClient))",
                "code=\(enc(code))",
                "code_verifier=\(enc(verifier))",
                "grant_type=authorization_code",
                "redirect_uri=\(enc(redirectURI))"
            ]
            tokenReq.httpBody = bodyItems.joined(separator: "&").data(using: .utf8)

            let (data, response) = try await URLSession.shared.data(for: tokenReq)
            guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode),
                  let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let token = json["access_token"] as? String else {
                let errJson = (try? JSONSerialization.jsonObject(with: data) as? [String: Any])
                let desc = (errJson?["error_description"] as? String) ?? (errJson?["error"] as? String) ?? "다시 시도해 주세요."
                status = "Google 인증 처리 실패: \(desc)"
                return false
            }
            accessToken = token
            if let refresh = json["refresh_token"] as? String, !refresh.isEmpty {
                refreshToken = refresh
            }
            cachedKeyRing = nil
            await fetchConnectedAccountEmail()
            OmyaruBackgroundSyncScheduler.shared.scheduleIfNeeded(enabled: backgroundSyncEnabled, isConnected: isConnected)
            status = connectedEmail.isEmpty ? "Google Drive 연결 완료" : "\(connectedEmail) 연결 완료"
            return true
        } catch {
            status = "Google 로그인 취소 또는 실패: \(error.localizedDescription)"
            return false
        }
    }

    private func fetchConnectedAccountEmail() async {
        guard let url = URL(string: "https://www.googleapis.com/drive/v3/about?fields=user(emailAddress,displayName)") else { return }
        if let data = try? await executeWithTokenRefresh({ try await $0.request(URLRequest(url: url, timeoutInterval: 15)) }),
           let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           let user = json["user"] as? [String: Any],
           let email = user["emailAddress"] as? String, !email.isEmpty {
            connectedEmail = email
            persistAuthSession()
        }
    }

    private func refreshAccessTokenIfPossible() async -> Bool {
        let trimmedClient = resolvedClientID
        guard !trimmedClient.isEmpty, !refreshToken.isEmpty else { return false }
        var req = URLRequest(url: URL(string: "https://oauth2.googleapis.com/token")!)
        req.httpMethod = "POST"
        req.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        let formAllowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "-._~"))
        func enc(_ s: String) -> String { s.addingPercentEncoding(withAllowedCharacters: formAllowed) ?? s }
        let body = [
            "client_id=\(enc(trimmedClient))",
            "refresh_token=\(enc(refreshToken))",
            "grant_type=refresh_token"
        ].joined(separator: "&")
        req.httpBody = body.data(using: .utf8)
        guard let (data, resp) = try? await URLSession.shared.data(for: req),
              let http = resp as? HTTPURLResponse, (200..<300).contains(http.statusCode),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let newToken = json["access_token"] as? String else {
            return false
        }
        accessToken = newToken
        return true
    }

    private func executeWithTokenRefresh<T>(_ work: (DriveRESTClient) async throws -> T) async throws -> T {
        let client = DriveRESTClient(accessToken: accessToken)
        do {
            return try await work(client)
        } catch is DriveRESTClient.UnauthorizedError {
            guard await refreshAccessTokenIfPossible() else {
                throw NSError(domain: "OmyaruDrive", code: 401, userInfo: [NSLocalizedDescriptionKey: "Google 인증이 만료되었습니다. 다시 로그인해 주세요."])
            }
            return try await work(DriveRESTClient(accessToken: accessToken))
        }
    }

    func disconnectGoogle() {
        cancelActiveSync()
        accessToken = ""
        refreshToken = ""
        connectedEmail = ""
        autoSyncEnabled = false
        cachedKeyRing = nil
        OmyaruBackgroundSyncScheduler.shared.scheduleIfNeeded(enabled: false, isConnected: false)
        status = "Google Drive 연결이 해제되었습니다."
    }

    func cancelActiveSync() {
        cancelRequested = true
        ActiveSyncBackgroundKeepAlive.shared.end()
        OmyaruSyncLiveActivityController.shared.finish(phase: "중단됨", detail: "동기화가 중단되었습니다.", isError: true)
    }

    private func generateCodeVerifier() -> String {
        var bytes = [UInt8](repeating: 0, count: 32)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        return Data(bytes).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    private func codeChallenge(for verifier: String) -> String {
        let digest = SHA256.hash(data: Data(verifier.utf8))
        return Data(digest).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    // MARK: - Full High-Speed Parallel Google Drive Sync

    private func reportSyncStep(phase: String, detail: String, progress value: Double, done: Int = 0, total: Int = 0, force: Bool = false) {
        let clamped = min(1.0, max(0.0, value))
        progress = clamped
        status = detail
        OmyaruSyncLiveActivityController.shared.update(
            phase: phase,
            detail: detail,
            progress: clamped,
            completedItems: done,
            totalItems: total,
            force: force
        )
    }

    func syncWithGoogleDrive(browser: BrowserModel?, shelf: ShelfModel, isBackground: Bool = false) async {
        guard !busy else { return }
        if accessToken.isEmpty && !refreshToken.isEmpty {
            _ = await refreshAccessTokenIfPossible()
        }
        guard !accessToken.isEmpty else {
            status = "먼저 Google 계정을 연결해 주세요."
            return
        }
        cancelRequested = false
        quickTabSyncTask?.cancel()
        quickTabSyncTask = nil
        busy = true
        progress = 0.05
        status = "Google Drive 목록 확인 중..."
        ActiveSyncBackgroundKeepAlive.shared.begin(allowExtendedKeepAlive: backgroundSyncEnabled)
        OmyaruSyncLiveActivityController.shared.start(
            accountLabel: connectedEmail.isEmpty ? "Google Drive" : connectedEmail,
            isBackground: isBackground,
            phase: "준비 중",
            detail: "Google Drive 목록 확인 중...",
            progress: 0.05,
            enabled: liveActivityEnabled
        )
        defer {
            busy = false
            progress = nil
            ActiveSyncBackgroundKeepAlive.shared.end()
            OmyaruBackgroundSyncScheduler.shared.scheduleIfNeeded(enabled: backgroundSyncEnabled, isConnected: isConnected)
        }

        do {
            if connectedEmail.isEmpty {
                await fetchConnectedAccountEmail()
            }

            // Single sweep listing all Drive files (replaces 5 sequential list queries!)
            let allFiles = try await executeWithTokenRefresh { try await $0.listAllAppDataFiles() }
            if cancelRequested || Task.isCancelled { throw CancellationError() }

            let rest = DriveRESTClient(accessToken: accessToken)
            let keyFiles = allFiles.filter { $0.name.hasPrefix("omyaru-account-key-v1-") }
            let userDataFiles = allFiles.filter { $0.name.hasPrefix("omyaru-userdata-v1-") }
            let tabResetFiles = allFiles.filter { $0.name.hasPrefix("omyaru-tab-reset-v1-") }
            let tabFiles = allFiles.filter { $0.name.hasPrefix("omyaru-tabs-v") }
            let shelfFiles = allFiles.filter { $0.name.hasPrefix("omyaru-shelf-v1-") }

            let keyRing = try await loadOrCreateKeyRing(keyFiles: keyFiles, rest: rest)
            if cancelRequested || Task.isCancelled { throw CancellationError() }
            cachedKeyRing = keyRing
            reportSyncStep(phase: "키 확인", detail: "암호화 키 확인 완료", progress: 0.14, force: true)

            if let browser {
                reportSyncStep(phase: "북마크·기록", detail: "북마크·방문 기록 동기화 중...", progress: 0.22, force: true)
                try await syncUserData(browser: browser, keys: keyRing, files: userDataFiles, rest: rest)
                if cancelRequested || Task.isCancelled { throw CancellationError() }

                reportSyncStep(phase: "탭·프로필", detail: "프로필·탭 그룹·열린 탭 확인 중...", progress: 0.36, force: true)
                try await syncTabsAndResets(browser: browser, keys: keyRing, resetFiles: tabResetFiles, tabFiles: tabFiles, rest: rest)
                if cancelRequested || Task.isCancelled { throw CancellationError() }
            }

            reportSyncStep(phase: "책장 동기화", detail: "책장 폴더·메모·사진 동기화 중...", progress: 0.45, force: true)
            let skippedDueToOtherApp = try await syncBookshelfParallel(
                shelf: shelf,
                keys: keyRing,
                shelfFiles: shelfFiles,
                rest: rest,
                isBackground: isBackground
            )
            if cancelRequested || Task.isCancelled { throw CancellationError() }

            progress = 1.0
            lastSyncedAt = Date()
            if skippedDueToOtherApp {
                status = "다른 앱(Umtomi)이 동기화 중이어서 책장 동기화를 안전하게 보류했습니다."
            } else {
                status = "Google Drive 동기화 완료 (프로필·탭·북마크·책장)"
            }
            OmyaruSyncLiveActivityController.shared.finish(
                phase: "완료",
                detail: status,
                isError: false
            )
        } catch is CancellationError {
            status = "동기화가 중단되었습니다."
            OmyaruSyncLiveActivityController.shared.finish(phase: "중단됨", detail: "동기화가 중단되었습니다.", isError: true)
        } catch {
            status = "동기화 실패: \(error.localizedDescription)"
            OmyaruSyncLiveActivityController.shared.finish(phase: "실패", detail: error.localizedDescription, isError: true)
        }
    }

    func publishTabReset(confirmation: String, browser: BrowserModel?) async throws {
        guard confirmation == Self.tabResetConfirmationPhrase else {
            throw NSError(domain: "OmyaruDrive", code: -20, userInfo: [NSLocalizedDescriptionKey: "‘\(Self.tabResetConfirmationPhrase)’를 정확히 입력해야 합니다."])
        }
        let allFiles = try await executeWithTokenRefresh { try await $0.listAllAppDataFiles() }
        let rest = DriveRESTClient(accessToken: accessToken)
        let keys = try await loadOrCreateKeyRing(
            keyFiles: allFiles.filter { $0.name.hasPrefix("omyaru-account-key-v1-") },
            rest: rest
        )
        let resetID = OmyaruSyncCrypto.compactUUID()
        let payloadObj: [String: Any] = ["format": "omyaru-tab-reset", "version": 1, "id": resetID]
        let plain = try JSONSerialization.data(withJSONObject: payloadObj, options: [.sortedKeys])
        let encrypted = try await keys.encryptAsync(plain)
        _ = try await rest.uploadDriveFile(name: "omyaru-tab-reset-v1-\(resetID).json", payload: encrypted)
        var replica = OmyaruTabReplica(directory: accountBrowserSyncDir())
        replica.observeResets([resetID])
        try? replica.save()
        if let browser {
            browser.isApplyingRemoteSync = true
            browser.tabs.removeAll { !$0.isPrivate }
            if browser.tabs.isEmpty {
                _ = browser.newTab()
            } else if !browser.tabs.contains(where: { $0.id == browser.activeID }), let first = browser.tabs.first {
                browser.activeID = first.id
            }
            browser.reconcileWebViewsAfterRemoteSync(previousURLs: [:])
            browser.persist()
            browser.isApplyingRemoteSync = false
            replica.acknowledge(Self.flattenBrowserForTabSync(browser, replica: &replica))
            try? replica.save()
        }
        status = "모든 기기의 동기화 탭 초기화 요청을 기록했습니다."
    }

    func scheduleQuickTabSync(browser: BrowserModel) {
        guard isConnected, autoSyncEnabled else { return }
        quickTabSyncTask?.cancel()
        quickTabSyncTask = Task { @MainActor [weak self, weak browser] in
            try? await Task.sleep(nanoseconds: 2_500_000_000)
            guard !Task.isCancelled, let self, let browser else { return }
            await self.syncTabsOnly(browser: browser)
        }
    }

    func syncTabsOnly(browser: BrowserModel) async {
        guard isConnected, !busy, !tabSyncBusy else { return }
        if accessToken.isEmpty && !refreshToken.isEmpty {
            _ = await refreshAccessTokenIfPossible()
        }
        guard !accessToken.isEmpty else { return }
        tabSyncBusy = true
        defer { tabSyncBusy = false }
        do {
            let needsKeys = cachedKeyRing?.primaryKey == nil
            let files = try await executeWithTokenRefresh { try await $0.listTabSyncFiles(includeKeys: needsKeys) }
            let rest = DriveRESTClient(accessToken: accessToken)
            let keys: OmyaruDriveKeyRing
            if let cached = cachedKeyRing, cached.primaryKey != nil {
                keys = cached
            } else {
                let loaded = try await loadOrCreateKeyRing(
                    keyFiles: files.filter { $0.name.hasPrefix("omyaru-account-key-v1-") },
                    rest: rest
                )
                cachedKeyRing = loaded
                keys = loaded
            }
            let resetFiles = files.filter { $0.name.hasPrefix("omyaru-tab-reset-v1-") }
            let tabFiles = files.filter { $0.name.hasPrefix("omyaru-tabs-v") }
            try await syncTabsAndResets(browser: browser, keys: keys, resetFiles: resetFiles, tabFiles: tabFiles, rest: rest)
            lastSyncedAt = Date()
        } catch {
            // Silent retry on next edit or foreground full sync
        }
    }

    func downloadCloudImageIfNeeded(item: BookshelfItem, shelf: ShelfModel) async -> URL? {
        let localURL = shelf.imageURL(for: item)
        if FileManager.default.fileExists(atPath: localURL.path) {
            return localURL
        }
        guard let manifest = item.cloud, isConnected else { return nil }
        if accessToken.isEmpty { _ = await refreshAccessTokenIfPossible() }
        guard !accessToken.isEmpty else { return nil }
        do {
            let rest = DriveRESTClient(accessToken: accessToken)
            let keys: OmyaruDriveKeyRing
            if let cached = cachedKeyRing, cached.primaryKey != nil {
                keys = cached
            } else {
                let all = try await executeWithTokenRefresh { try await $0.listAllAppDataFiles() }
                keys = try await loadOrCreateKeyRing(
                    keyFiles: all.filter { $0.name.hasPrefix("omyaru-account-key-v1-") },
                    rest: rest
                )
                cachedKeyRing = keys
            }
            let assembled = try await Self.downloadChunkedImageParallel(manifest: manifest, keys: keys, rest: rest)
            if let accountRoot = SharedSyncFolderManager.shared.activeAccountRoot(for: connectedEmail) {
                return try SharedSyncFolderManager.storeOriginal(assembled, expectedSHA256: manifest.sha256, in: accountRoot)
            }
            try FileManager.default.createDirectory(at: shelf.privateRoot, withIntermediateDirectories: true)
            try assembled.write(to: localURL, options: .atomic)
            return localURL
        } catch {
            return nil
        }
    }

    // MARK: - KeyRing Loader With Persistent Cache

    private func loadOrCreateKeyRing(keyFiles: [DriveFileEntry], rest: DriveRESTClient) async throws -> OmyaruDriveKeyRing {
        let sortedIDs = keyFiles.map(\.id).sorted()
        if let cached = cachedKeyRing, cached.primaryKey != nil, !sortedIDs.isEmpty, cached.keyFileIDs == sortedIDs {
            return cached
        }

        var ring = OmyaruDriveKeyRing()
        if syncPassphrase.count >= 6 {
            ring.legacyPasswords.append(syncPassphrase)
        }
        let downloaded = try await Self.fetchFilesParallel(files: keyFiles.sorted { $0.name < $1.name }, rest: rest, concurrency: 4)
        for (file, data) in downloaded {
            let expectedID = file.name
                .replacingOccurrences(of: "omyaru-account-key-v1-", with: "")
                .replacingOccurrences(of: ".json", with: "")
            try? ring.addRecord(data, expectedID: expectedID)
        }
        if ring.primaryKey == nil {
            let newID = OmyaruSyncCrypto.compactUUID()
            var randomBytes = [UInt8](repeating: 0, count: 32)
            _ = SecRandomCopyBytes(kSecRandomDefault, 32, &randomBytes)
            let secret = Data(randomBytes).base64EncodedString()
            var record: [String: Any] = [
                "format": "omyaru-account-key",
                "version": 1,
                "id": newID,
                "secret": secret,
                "legacyEnvelope": NSNull()
            ]
            if syncPassphrase.count >= 6,
               let legacyPlain = try? JSONSerialization.data(withJSONObject: ["passphrase": syncPassphrase]),
               let wrapped = try? OmyaruSyncCrypto.encrypt(legacyPlain, password: secret),
               let wrappedText = String(data: wrapped, encoding: .utf8) {
                record["legacyEnvelope"] = wrappedText
            }
            let payload = try JSONSerialization.data(withJSONObject: record, options: [.sortedKeys])
            let uploadedID = try await rest.uploadDriveFile(name: "omyaru-account-key-v1-\(newID).json", payload: payload)
            try ring.addRecord(payload, expectedID: newID)
            ring.keyFileIDs = (sortedIDs + [uploadedID]).sorted()
        } else {
            ring.keyFileIDs = sortedIDs
        }

        if let encoded = try? JSONEncoder().encode(ring) {
            try? encoded.write(to: keyRingCacheURL, options: [.atomic, .completeFileProtectionUntilFirstUserAuthentication])
        }
        return ring
    }

    // MARK: - Parallel Network + Multi-Core Crypto Helpers (Non-Isolated!)

    private nonisolated static func fetchFilesParallel(
        files: [DriveFileEntry],
        rest: DriveRESTClient,
        concurrency: Int
    ) async throws -> [(DriveFileEntry, Data)] {
        guard !files.isEmpty else { return [] }
        return try await withThrowingTaskGroup(of: (Int, DriveFileEntry, Data)?.self) { group in
            var results: [(Int, DriveFileEntry, Data)] = []
            results.reserveCapacity(files.count)
            var nextIdx = 0
            let limit = max(1, min(concurrency, files.count))

            for _ in 0..<limit {
                let idx = nextIdx
                let file = files[idx]
                nextIdx += 1
                group.addTask {
                    guard let data = try? await rest.readDriveFile(file.id) else { return nil }
                    return (idx, file, data)
                }
            }
            while let item = try await group.next() {
                if let item { results.append(item) }
                if nextIdx < files.count {
                    let idx = nextIdx
                    let file = files[idx]
                    nextIdx += 1
                    group.addTask {
                        guard let data = try? await rest.readDriveFile(file.id) else { return nil }
                        return (idx, file, data)
                    }
                }
            }
            return results.sorted { $0.0 < $1.0 }.map { ($0.1, $0.2) }
        }
    }

    private nonisolated static func fetchAndDecryptParallel(
        files: [DriveFileEntry],
        keys: OmyaruDriveKeyRing,
        rest: DriveRESTClient,
        concurrency: Int,
        onStep: (@Sendable (Int, Int) -> Void)? = nil
    ) async throws -> [(DriveFileEntry, Data?)] {
        guard !files.isEmpty else { return [] }
        let total = files.count
        return try await withThrowingTaskGroup(of: (Int, DriveFileEntry, Data?).self) { group in
            var results: [(Int, DriveFileEntry, Data?)] = []
            results.reserveCapacity(total)
            var nextIdx = 0
            var completed = 0
            let limit = max(1, min(concurrency, total))

            func enqueue(_ idx: Int) {
                let file = files[idx]
                group.addTask {
                    try Task.checkCancellation()
                    guard let encrypted = try? await rest.readDriveFile(file.id) else {
                        return (idx, file, nil)
                    }
                    let plain = try? keys.decrypt(encrypted)
                    return (idx, file, plain)
                }
            }

            for _ in 0..<limit {
                enqueue(nextIdx)
                nextIdx += 1
            }
            while let item = try await group.next() {
                results.append(item)
                completed += 1
                onStep?(completed, total)
                if nextIdx < total {
                    enqueue(nextIdx)
                    nextIdx += 1
                }
            }
            return results.sorted { $0.0 < $1.0 }.map { ($0.1, $0.2) }
        }
    }

    // MARK: - Sync Sub-Protocols (Bookmarks, History, Tabs, Resets)

    private func syncUserData(browser: BrowserModel, keys: OmyaruDriveKeyRing, files: [DriveFileEntry], rest: DriveRESTClient) async throws {
        var seen = loadSeenSet(name: "seen-userdata.json")
        let unread = files.filter { !seen.contains($0.id) }.sorted { $0.name < $1.name }
        let fetched = try await Self.fetchAndDecryptParallel(files: unread, keys: keys, rest: rest, concurrency: Self.maxConcurrentTransfers)
        for (file, plain) in fetched {
            if let plain, let doc = try? JSONSerialization.jsonObject(with: plain) as? [String: Any] {
                if let bObj = doc["bookmarks"] as? [String: Any] {
                    if let folderRows = bObj["folders"] as? [[String: Any]] {
                        for row in folderRows {
                            guard let name = row["name"] as? String else { continue }
                            let idStr = row["id"] as? String ?? OmyaruSyncCrypto.compactUUID()
                            let parentStr = row["parentId"] as? String
                            if !browser.bookmarkFolders.contains(where: { $0.id == idStr || ($0.name == name && $0.parentID == parentStr) }) {
                                browser.bookmarkFolders.append(BookmarkFolder(id: idStr, name: name, parentID: parentStr))
                            }
                        }
                    }
                    if let items = bObj["items"] as? [[String: Any]] {
                        func canonicalBookmarkKey(_ raw: String) -> String {
                            let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
                            guard let comps = URLComponents(string: trimmed), var host = comps.host?.lowercased() else {
                                return trimmed.lowercased()
                            }
                            if host.hasPrefix("www.") { host = String(host.dropFirst(4)) }
                            else if host.hasPrefix("m.") { host = String(host.dropFirst(2)) }
                            var path = comps.path
                            if path == "/" { path = "" }
                            else { while path.hasSuffix("/") { path.removeLast() } }
                            let query = comps.query.map { "?\($0)" } ?? ""
                            return host + path + query
                        }
                        for row in items {
                            guard let title = row["title"] as? String,
                                  let url = row["url"] as? String else { continue }
                            let folderID = row["folderId"] as? String
                            let key = canonicalBookmarkKey(url)
                            if !browser.bookmarks.contains(where: { canonicalBookmarkKey($0.url) == key && ($0.folderID ?? "") == (folderID ?? "") }) {
                                browser.bookmarks.append(BrowserBookmark(title: title, url: url, folderID: folderID))
                            }
                        }
                    }
                }
                if let hArr = doc["history"] as? [[String: Any]] {
                    for row in hArr.prefix(400) {
                        guard let title = row["title"] as? String,
                              let url = row["url"] as? String else { continue }
                        let visited = (row["visitedAtEpochMs"] as? NSNumber)?.int64Value ?? Int64(Date().timeIntervalSince1970 * 1000)
                        if !browser.history.contains(where: { $0.url == url }) {
                            browser.history.append(HistoryEntry(title: title, url: url, visitedAtEpochMs: visited))
                        }
                    }
                }
            }
            seen.insert(file.id)
        }
        saveSeenSet(seen, name: "seen-userdata.json")
        if !unread.isEmpty { browser.persist() }

        let folderPayload = browser.bookmarkFolders.map { f -> [String: Any] in
            var obj: [String: Any] = ["id": f.id, "name": f.name]
            if let p = f.parentID { obj["parentId"] = p } else { obj["parentId"] = NSNull() }
            return obj
        }
        let bookmarkItems = browser.bookmarks.map { b -> [String: Any] in
            var obj: [String: Any] = [
                "id": OmyaruSyncCrypto.compactUUID(b.id),
                "title": b.title,
                "url": b.url
            ]
            if let f = b.folderID { obj["folderId"] = f } else { obj["folderId"] = NSNull() }
            return obj
        }
        let historyItems = browser.history.prefix(400).map { h -> [String: Any] in
            ["title": h.title, "url": h.url, "visitedAtEpochMs": h.visitedAtEpochMs]
        }
        let fingerprintObj: [String: Any] = [
            "bookmarks": ["folders": folderPayload, "items": bookmarkItems],
            "history": historyItems
        ]
        let fingerprintData = try JSONSerialization.data(withJSONObject: fingerprintObj, options: [.sortedKeys])
        let hash = OmyaruSyncCrypto.sha256Hex(fingerprintData)
        let lastHash = UserDefaults.standard.string(forKey: "omyaru.ios.drive.lastUserDataHash") ?? ""
        if hash != lastHash {
            let opID = OmyaruSyncCrypto.compactUUID()
            let payloadObj: [String: Any] = [
                "id": opID,
                "deviceId": UIDevice.current.identifierForVendor?.uuidString ?? "ios",
                "timestamp": Int64(Date().timeIntervalSince1970 * 1000),
                "bookmarks": ["folders": folderPayload, "items": bookmarkItems],
                "history": historyItems
            ]
            let plain = try JSONSerialization.data(withJSONObject: payloadObj, options: [.sortedKeys])
            let encrypted = try await keys.encryptAsync(plain)
            let uploadedID = try await rest.uploadDriveFile(name: "omyaru-userdata-v1-\(opID).json", payload: encrypted)
            seen.insert(uploadedID)
            saveSeenSet(seen, name: "seen-userdata.json")
            UserDefaults.standard.set(hash, forKey: "omyaru.ios.drive.lastUserDataHash")
        }
    }

    // MARK: - Full Cross-Platform CRDT Tab, Tab Group & Profile Sync (100% Wire Compatible with PC DriveTabReplica & Android)

    private struct OmyaruTabReplica {
        let fileURL: URL
        let hadPersistedState: Bool
        private(set) var state: [String: Any]

        init(directory: URL) {
            try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            fileURL = directory.appendingPathComponent("tab-replica.json")
            if let data = try? Data(contentsOf: fileURL),
               let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
               let actor = obj["actor"] as? String,
               Self.isValidNonZeroHex32(actor),
               obj["registers"] is [String: Any],
               obj["baseline"] is [String: Any],
               obj["seen"] is [String],
               obj["pending"] is [[String: Any]] {
                hadPersistedState = true
                state = obj
            } else {
                hadPersistedState = false
                state = [
                    "actor": OmyaruSyncCrypto.compactUUID(),
                    "clock": Int64(0),
                    "registers": [String: Any](),
                    "baseline": [String: Any](),
                    "seen": [String](),
                    "pending": [[String: Any]](),
                    "tabResetIds": [String](),
                    "needsTabClear": false,
                    "uuidToWireId": [String: String]()
                ]
            }
        }

        var baseline: [String: Any] { state["baseline"] as? [String: Any] ?? [:] }
        var pending: [[String: Any]] { state["pending"] as? [[String: Any]] ?? [] }
        var seen: Set<String> { Set(state["seen"] as? [String] ?? []) }
        var tabResetIDs: Set<String> {
            Set((state["tabResetIds"] as? [String] ?? []).filter { Self.isValidNonZeroHex32($0) })
        }
        var needsTabClear: Bool { (state["needsTabClear"] as? Bool) ?? false }

        func save() throws {
            try FileManager.default.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
            let data = try JSONSerialization.data(withJSONObject: state, options: [.sortedKeys])
            try data.write(to: fileURL, options: .atomic)
        }

        static func isValidNonZeroHex32(_ value: String) -> Bool {
            guard value.count == 32,
                  value.allSatisfy({ ("0"..."9").contains($0) || ("a"..."f").contains($0) || ("A"..."F").contains($0) }) else {
                return false
            }
            return value.contains(where: { $0 != "0" })
        }

        static func deterministicUUID(seed: String) -> UUID {
            let digest = Array(SHA256.hash(data: Data(seed.utf8)))
            let bytes: uuid_t = (
                digest[0], digest[1], digest[2], digest[3],
                digest[4], digest[5],
                (digest[6] & 0x0F) | 0x50, digest[7],
                (digest[8] & 0x3F) | 0x80, digest[9],
                digest[10], digest[11], digest[12], digest[13], digest[14], digest[15]
            )
            return UUID(uuid: bytes)
        }

        static func uuidFromCompactHex(_ hex: String) -> UUID? {
            let s = hex.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
            guard s.count == 32,
                  s.allSatisfy({ ("0"..."9").contains($0) || ("a"..."f").contains($0) }) else {
                return nil
            }
            let i1 = s.index(s.startIndex, offsetBy: 8)
            let i2 = s.index(i1, offsetBy: 4)
            let i3 = s.index(i2, offsetBy: 4)
            let i4 = s.index(i3, offsetBy: 4)
            return UUID(uuidString: "\(s[..<i1])-\(s[i1..<i2])-\(s[i2..<i3])-\(s[i3..<i4])-\(s[i4...])")
        }

        static func defaultGroupUUID(forProfileWireID profileWireID: String) -> UUID {
            if profileWireID == "default" {
                return BrowserModel.defaultGroupID
            }
            return deterministicUUID(seed: "omyaru-default-group:\(profileWireID)")
        }

        mutating func uuid(forWireID wireID: String, isProfile: Bool = false) -> UUID {
            if isProfile && wireID == "default" {
                return BrowserModel.defaultProfileID
            }
            if let parsed = Self.uuidFromCompactHex(wireID) ?? UUID(uuidString: wireID) {
                return parsed
            }
            let derived = Self.deterministicUUID(seed: "omyaru-wire-id:\(wireID)")
            var map = state["uuidToWireId"] as? [String: String] ?? [:]
            map[derived.uuidString.lowercased()] = wireID
            state["uuidToWireId"] = map
            return derived
        }

        func wireID(forUUID uuid: UUID, isProfile: Bool = false) -> String {
            if isProfile && uuid == BrowserModel.defaultProfileID {
                return "default"
            }
            let map = state["uuidToWireId"] as? [String: String] ?? [:]
            if let mapped = map[uuid.uuidString.lowercased()], !mapped.isEmpty {
                return mapped
            }
            return OmyaruSyncCrypto.compactUUID(uuid)
        }

        mutating func recordExistingResets(_ ids: Set<String>) {
            let valid = ids.filter { Self.isValidNonZeroHex32($0) }
            guard !valid.isEmpty else { return }
            var merged = tabResetIDs
            merged.formUnion(valid)
            state["tabResetIds"] = Array(merged).sorted()
        }

        mutating func observeResets(_ ids: Set<String>) {
            let valid = ids.filter { Self.isValidNonZeroHex32($0) }
            let existing = tabResetIDs
            let unseen = valid.subtracting(existing)
            guard !unseen.isEmpty else { return }
            var merged = existing
            merged.formUnion(valid)
            state["tabResetIds"] = Array(merged).sorted()
            state["needsTabClear"] = true

            var registers = state["registers"] as? [String: [String: Any]] ?? [:]
            for k in registers.keys where k.hasPrefix("tabs/") { registers.removeValue(forKey: k) }
            state["registers"] = registers

            var base = baseline
            for k in base.keys where k.hasPrefix("tabs/") { base.removeValue(forKey: k) }
            state["baseline"] = base

            let filteredPending: [[String: Any]] = pending.compactMap { batch in
                guard let rows = batch["changes"] as? [[String: Any]] else { return nil }
                let kept = rows.filter { ($0["kind"] as? String) != "tabs" }
                guard !kept.isEmpty else { return nil }
                var copy = batch
                copy["changes"] = kept
                return copy
            }
            state["pending"] = filteredPending
            state["seen"] = [String]()
        }

        mutating func acknowledge(_ projected: [String: Any]) {
            state["baseline"] = projected
            state["needsTabClear"] = false
        }

        mutating func markSeen(_ id: String) {
            var s = seen
            s.insert(id)
            state["seen"] = Array(s).sorted()
        }

        mutating func popPending(uploadedID: String) {
            var list = pending
            if !list.isEmpty { list.removeFirst() }
            state["pending"] = list
            markSeen(uploadedID)
        }

        private static func jsonEqual(_ a: Any?, _ b: Any?) -> Bool {
            if a == nil && b == nil { return true }
            if a is NSNull && b is NSNull { return true }
            guard let a, let b, !(a is NSNull), !(b is NSNull) else { return false }
            if let sa = a as? String, let sb = b as? String { return sa == sb }
            if let na = a as? NSNumber, let nb = b as? NSNumber {
                let aBool = CFGetTypeID(na) == CFBooleanGetTypeID()
                let bBool = CFGetTypeID(nb) == CFBooleanGetTypeID()
                if aBool || bBool {
                    return aBool && bBool && na.boolValue == nb.boolValue
                }
                return na.uint64Value == nb.uint64Value && na.int64Value == nb.int64Value
            }
            guard let da = try? JSONSerialization.data(withJSONObject: [a], options: [.sortedKeys]),
                  let db = try? JSONSerialization.data(withJSONObject: [b], options: [.sortedKeys]) else {
                return false
            }
            return da == db
        }

        mutating func capture(_ local: [String: Any]) {
            let before = baseline
            var changes: [String: Any] = [:]
            for (key, value) in local where !Self.jsonEqual(before[key], value) {
                changes[key] = value
            }
            let currentEntities = Set(local.keys.compactMap { key -> String? in
                guard let slash = key.lastIndex(of: "/") else { return nil }
                return String(key[..<slash])
            })
            let beforeEntities = Set(before.keys.compactMap { key -> String? in
                guard let slash = key.lastIndex(of: "/") else { return nil }
                return String(key[..<slash])
            })
            for entity in beforeEntities where !currentEntities.contains(entity) {
                changes["\(entity)/$deleted"] = NSNumber(value: true)
            }
            guard !changes.isEmpty else {
                state["baseline"] = local
                return
            }

            let sortedKeys = changes.keys.sorted()
            let actor = (state["actor"] as? String) ?? OmyaruSyncCrypto.compactUUID()
            let resetArray = Array(tabResetIDs).sorted()
            for offset in stride(from: 0, to: sortedKeys.count, by: 512) {
                let slice = sortedKeys[offset..<min(sortedKeys.count, offset + 512)]
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
                    "tabResetIds": resetArray,
                    "changes": rows
                ]
                applyBatch(batch, includeTabs: true)
                state["pending"] = pending + [batch]
            }
            state["baseline"] = local
        }

        mutating func applyDocument(_ doc: [String: Any], expectedID: String) {
            guard let docID = doc["id"] as? String,
                  docID.caseInsensitiveCompare(expectedID) == .orderedSame,
                  let proto = (doc["protocol"] as? NSNumber)?.intValue else { return }
            let remoteResets = Set((doc["tabResetIds"] as? [String] ?? []).filter { Self.isValidNonZeroHex32($0) })
            let includeTabs = tabResetIDs.isSubset(of: remoteResets)
            if proto == 1 {
                applyBatch(doc, includeTabs: includeTabs)
            } else if proto == 2, let batches = doc["batches"] as? [[String: Any]] {
                for batch in batches {
                    applyBatch(batch, includeTabs: includeTabs)
                }
            }
        }

        mutating func applyBatch(_ batch: [String: Any], includeTabs: Bool) {
            guard let actor = batch["actor"] as? String,
                  Self.isValidNonZeroHex32(actor),
                  let clock = (batch["clock"] as? NSNumber)?.int64Value,
                  clock > 0,
                  let rows = batch["changes"] as? [[String: Any]] else { return }
            var registers = state["registers"] as? [String: [String: Any]] ?? [:]
            for row in rows {
                guard let kind = row["kind"] as? String,
                      kind == "profiles" || kind == "groups" || kind == "tabs",
                      let entityID = row["id"] as? String, !entityID.isEmpty,
                      let field = row["field"] as? String, !field.isEmpty,
                      let value = row["value"] else { continue }
                if !includeTabs && kind == "tabs" { continue }
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

        func project() -> (profiles: [String: [String: Any]], groups: [String: [String: Any]], tabs: [String: [String: Any]]) {
            let registers = state["registers"] as? [String: [String: Any]] ?? [:]
            var profiles: [String: [String: Any]] = [:]
            var groups: [String: [String: Any]] = [:]
            var tabs: [String: [String: Any]] = [:]
            for (key, row) in registers {
                let parts = key.split(separator: "/").map(String.init)
                guard parts.count == 3, let value = row["value"] else { continue }
                let kind = parts[0]
                let id = parts[1]
                let field = parts[2]
                if field == "$deleted" || registers["\(kind)/\(id)/$deleted"] != nil { continue }
                switch kind {
                case "profiles": profiles[id, default: [:]][field] = value
                case "groups": groups[id, default: [:]][field] = value
                case "tabs": tabs[id, default: [:]][field] = value
                default: break
                }
            }
            return (profiles, groups, tabs)
        }
    }

    private static func hexToARGBUint(_ hex: String) -> UInt32 {
        let clean = hex.trimmingCharacters(in: .whitespacesAndNewlines).replacingOccurrences(of: "#", with: "")
        if clean.count == 6, let rgb = UInt32(clean, radix: 16) {
            return 0xFF00_0000 | rgb
        }
        if clean.count == 8, let argb = UInt32(clean, radix: 16) {
            return argb
        }
        return 0xFF0A_84FF
    }

    private static func argbUintToHex(_ val: UInt32) -> String {
        String(format: "#%06X", val & 0x00FF_FFFF)
    }

    private static func wireIconToSFSymbol(_ wire: String) -> String {
        switch wire.uppercased() {
        case "PERSON": return "person.fill"
        case "WORK": return "briefcase.fill"
        case "STUDY": return "book.fill"
        case "TRAVEL": return "airplane"
        case "GAMING": return "gamecontroller.fill"
        case "STAR": return "star.fill"
        default:
            let trimmed = wire.trimmingCharacters(in: .whitespacesAndNewlines)
            if trimmed.isEmpty || trimmed == trimmed.uppercased() { return "person.fill" }
            return trimmed
        }
    }

    private static func sfSymbolToWireIcon(_ symbol: String) -> String {
        switch symbol {
        case "person.fill": return "PERSON"
        case "briefcase.fill": return "WORK"
        case "book.fill": return "STUDY"
        case "airplane": return "TRAVEL"
        case "gamecontroller.fill": return "GAMING"
        case "star.fill": return "STAR"
        default:
            let trimmed = symbol.trimmingCharacters(in: .whitespacesAndNewlines)
            return trimmed.isEmpty ? "PERSON" : trimmed
        }
    }

    private static func defaultGroupIDForProfile(
        _ profileID: UUID,
        profileWireID: String,
        groups: [BrowserGroup]
    ) -> UUID {
        let canonical = OmyaruTabReplica.defaultGroupUUID(forProfileWireID: profileWireID)
        if groups.contains(where: { $0.profileID == profileID && $0.id == canonical }) {
            return canonical
        }
        if let firstDefault = groups.first(where: { $0.profileID == profileID && $0.name == "기본" }) {
            return firstDefault.id
        }
        return canonical
    }

    private static func flattenBrowserForTabSync(
        _ browser: BrowserModel,
        replica: inout OmyaruTabReplica
    ) -> [String: Any] {
        var fields: [String: Any] = [:]
        var profileWireByUUID: [UUID: String] = [:]
        var defaultGroupByProfile: [UUID: UUID] = [:]
        var validCustomGroupWires = Set<String>()

        for (idx, profile) in browser.profiles.prefix(32).enumerated() {
            let pid = replica.wireID(forUUID: profile.id, isProfile: true)
            profileWireByUUID[profile.id] = pid
            defaultGroupByProfile[profile.id] = defaultGroupIDForProfile(profile.id, profileWireID: pid, groups: browser.groups)
            fields["profiles/\(pid)/name"] = profile.name.isEmpty ? "개인" : String(profile.name.prefix(40))
            fields["profiles/\(pid)/icon"] = sfSymbolToWireIcon(profile.icon)
            fields["profiles/\(pid)/color"] = NSNumber(value: hexToARGBUint(profile.colorHex))
            fields["profiles/\(pid)/order"] = idx
        }

        var groupCountByProfile: [UUID: Int] = [:]
        for group in browser.groups {
            guard group.id != BrowserModel.privateGroupID,
                  let pid = profileWireByUUID[group.profileID] else { continue }
            if defaultGroupByProfile[group.profileID] == group.id { continue }
            let count = groupCountByProfile[group.profileID] ?? 0
            if count >= 100 { continue }
            groupCountByProfile[group.profileID] = count + 1
            let gid = replica.wireID(forUUID: group.id)
            validCustomGroupWires.insert(gid)
            fields["groups/\(gid)/profileId"] = pid
            fields["groups/\(gid)/name"] = group.name.isEmpty ? "그룹" : String(group.name.prefix(40))
        }

        let regularTabs = browser.tabs.filter { !$0.isPrivate && $0.groupID != BrowserModel.privateGroupID }.prefix(500)
        for (idx, tab) in regularTabs.enumerated() {
            let tid = replica.wireID(forUUID: tab.id)
            let pid = profileWireByUUID[tab.profileID] ?? "default"
            let isDefaultGroup = (defaultGroupByProfile[tab.profileID] == tab.groupID) || (tab.groupID == BrowserModel.defaultGroupID)
            let groupValue: Any
            if isDefaultGroup {
                groupValue = NSNull()
            } else {
                let gid = replica.wireID(forUUID: tab.groupID)
                groupValue = validCustomGroupWires.contains(gid) ? gid : NSNull()
            }
            let safeTitle = tab.title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "새 탭" : String(tab.title.prefix(200))
            let trimmedURL = tab.url.trimmingCharacters(in: .whitespacesAndNewlines)
            let safeURL = (trimmedURL.hasPrefix("http://") || trimmedURL.hasPrefix("https://")) ? String(trimmedURL.prefix(8192)) : "about:blank"
            fields["tabs/\(tid)/title"] = safeTitle
            fields["tabs/\(tid)/url"] = safeURL
            fields["tabs/\(tid)/profileId"] = pid
            fields["tabs/\(tid)/groupId"] = groupValue
            fields["tabs/\(tid)/order"] = idx
        }

        return fields
    }

    private static func extractTabFileID(_ fileName: String) -> String? {
        guard fileName.hasSuffix(".json") else { return nil }
        let raw: String
        if fileName.hasPrefix("omyaru-tabs-v2-") {
            raw = String(fileName.dropFirst("omyaru-tabs-v2-".count).dropLast(5))
        } else if fileName.hasPrefix("omyaru-tabs-v1-") {
            raw = String(fileName.dropFirst("omyaru-tabs-v1-".count).dropLast(5))
        } else {
            return nil
        }
        return OmyaruTabReplica.isValidNonZeroHex32(raw) ? raw : nil
    }

    private func syncTabsAndResets(
        browser: BrowserModel,
        keys: OmyaruDriveKeyRing,
        resetFiles: [DriveFileEntry],
        tabFiles: [DriveFileEntry],
        rest: DriveRESTClient
    ) async throws {
        var replica = OmyaruTabReplica(directory: accountBrowserSyncDir())

        // 1. Check remote tab resets
        let remoteResetIDs: Set<String> = Set(resetFiles.compactMap { file in
            let raw = file.name
                .replacingOccurrences(of: "omyaru-tab-reset-v1-", with: "")
                .replacingOccurrences(of: ".json", with: "")
            return OmyaruTabReplica.isValidNonZeroHex32(raw) ? raw : nil
        })
        if !remoteResetIDs.isEmpty {
            if replica.hadPersistedState {
                replica.observeResets(remoteResetIDs)
            } else {
                replica.recordExistingResets(remoteResetIDs)
            }
            if replica.needsTabClear {
                browser.isApplyingRemoteSync = true
                browser.tabs.removeAll { !$0.isPrivate }
                browser.isApplyingRemoteSync = false
            }
            try? replica.save()
        }

        let validTabFiles = tabFiles.filter { Self.extractTabFileID($0.name) != nil }

        // 2. First-time CRDT migration: pull remote Drive history FIRST, deduplicate legacy random-UUID copies, then capture only truly new local items!
        if !replica.hadPersistedState {
            let unseen = validTabFiles
                .filter { !replica.seen.contains($0.id) }
                .sorted { $0.name < $1.name }
            if !unseen.isEmpty {
                let fetched = try await Self.fetchAndDecryptParallel(
                    files: unseen,
                    keys: keys,
                    rest: rest,
                    concurrency: Self.maxConcurrentTransfers
                )
                for (file, plain) in fetched {
                    if let plain,
                       let expectedID = Self.extractTabFileID(file.name),
                       let doc = try? JSONSerialization.jsonObject(with: plain) as? [String: Any] {
                        replica.applyDocument(doc, expectedID: expectedID)
                    }
                    replica.markSeen(file.id)
                }
                try? replica.save()
            }
            let remote = replica.project()
            if !remote.profiles.isEmpty || !remote.groups.isEmpty || !remote.tabs.isEmpty {
                Self.deduplicateLocalAgainstRemote(browser: browser, remote: remote, replica: &replica)
            }
            let localFlat = Self.flattenBrowserForTabSync(browser, replica: &replica)
            replica.capture(localFlat)
            try? replica.save()
        } else {
            let localFlat = Self.flattenBrowserForTabSync(browser, replica: &replica)
            replica.capture(localFlat)
            try? replica.save()
        }

        // 3. Push local pending delta batches (with logical clock & tabResetIds)
        for batch in replica.pending {
            guard let batchID = batch["id"] as? String else { continue }
            let plain = try JSONSerialization.data(withJSONObject: batch, options: [.sortedKeys])
            let encrypted = try await keys.encryptAsync(plain)
            let uploadedID = try await rest.uploadDriveFile(name: "omyaru-tabs-v2-\(batchID).json", payload: encrypted)
            replica.popPending(uploadedID: uploadedID)
            try? replica.save()
        }

        // 4. Pull any unseen remote batches (for steady-state syncs)
        if replica.hadPersistedState {
            let unseen = validTabFiles
                .filter { !replica.seen.contains($0.id) }
                .sorted { $0.name < $1.name }
            if !unseen.isEmpty {
                let fetched = try await Self.fetchAndDecryptParallel(
                    files: unseen,
                    keys: keys,
                    rest: rest,
                    concurrency: Self.maxConcurrentTransfers
                )
                for (file, plain) in fetched {
                    if let plain,
                       let expectedID = Self.extractTabFileID(file.name),
                       let doc = try? JSONSerialization.jsonObject(with: plain) as? [String: Any] {
                        replica.applyDocument(doc, expectedID: expectedID)
                    }
                    replica.markSeen(file.id)
                }
                try? replica.save()
            }
        }

        // 5. Project unified CRDT state -> apply profiles, tab groups, and tabs to BrowserModel
        let projected = replica.project()
        applyProjectedTabs(projected, to: browser, replica: &replica)
        replica.acknowledge(Self.flattenBrowserForTabSync(browser, replica: &replica))
        try? replica.save()
    }

    private static func deduplicateLocalAgainstRemote(
        browser: BrowserModel,
        remote: (profiles: [String: [String: Any]], groups: [String: [String: Any]], tabs: [String: [String: Any]]),
        replica: inout OmyaruTabReplica
    ) {
        browser.isApplyingRemoteSync = true
        defer { browser.isApplyingRemoteSync = false }

        // Preserve remote default profile's name/icon/color on first sync so iOS defaults don't clobber PC/Android profile branding.
        if let defRow = remote.profiles["default"],
           let defIdx = browser.profiles.firstIndex(where: { $0.id == BrowserModel.defaultProfileID }) {
            if let rName = defRow["name"] as? String, !rName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                browser.profiles[defIdx].name = rName
            }
            if let rIcon = defRow["icon"] as? String {
                browser.profiles[defIdx].icon = wireIconToSFSymbol(rIcon)
            }
            if let rColor = defRow["color"] as? NSNumber {
                browser.profiles[defIdx].colorHex = argbUintToHex(UInt32(truncatingIfNeeded: rColor.uint64Value))
            }
        }

        // Map remote custom groups by (profileUUID, lowercased name) -> remote group UUID
        var remoteGroupByKey: [String: UUID] = [:]
        for (gid, row) in remote.groups {
            guard let pid = row["profileId"] as? String,
                  let name = row["name"] as? String else { continue }
            let pUUID = replica.uuid(forWireID: pid, isProfile: true)
            let gUUID = replica.uuid(forWireID: gid)
            remoteGroupByKey["\(pUUID.uuidString)|\(name.lowercased())"] = gUUID
        }

        var remappedGroups: [UUID: UUID] = [:]

        for i in browser.groups.indices {
            let g = browser.groups[i]
            if g.name == "기본" { continue }
            let key = "\(g.profileID.uuidString)|\(g.name.lowercased())"
            if let remoteUUID = remoteGroupByKey[key], remoteUUID != g.id {
                remappedGroups[g.id] = remoteUUID
                browser.groups[i].id = remoteUUID
            }
        }
        if !remappedGroups.isEmpty {
            for i in browser.tabs.indices {
                if let newGID = remappedGroups[browser.tabs[i].groupID] {
                    browser.tabs[i].groupID = newGID
                }
            }
            if let newActiveG = remappedGroups[browser.activeGroupID] {
                browser.activeGroupID = newActiveG
            }
        }

        // Map remote tabs by (profileUUID, lowercased url) so legacy random-UUID iOS duplicates don't push back to PC
        var remoteURLSet = Set<String>()
        var remoteTabCountByProfile: [UUID: Int] = [:]
        for (_, row) in remote.tabs {
            guard let pid = row["profileId"] as? String,
                  let url = row["url"] as? String else { continue }
            let pUUID = replica.uuid(forWireID: pid, isProfile: true)
            remoteTabCountByProfile[pUUID, default: 0] += 1
            if url != "about:blank" {
                remoteURLSet.insert("\(pUUID.uuidString)|\(url.lowercased())")
            }
        }

        var remoteTabIDs = Set<UUID>()
        for tid in remote.tabs.keys {
            remoteTabIDs.insert(replica.uuid(forWireID: tid))
        }
        browser.tabs.removeAll { tab in
            guard !tab.isPrivate else { return false }
            if remoteTabIDs.contains(tab.id) { return false }
            if tab.url == "about:blank" {
                return (remoteTabCountByProfile[tab.profileID] ?? 0) > 0
            }
            return remoteURLSet.contains("\(tab.profileID.uuidString)|\(tab.url.lowercased())")
        }
    }

    private func applyProjectedTabs(
        _ projected: (profiles: [String: [String: Any]], groups: [String: [String: Any]], tabs: [String: [String: Any]]),
        to browser: BrowserModel,
        replica: inout OmyaruTabReplica
    ) {
        guard !projected.profiles.isEmpty || !projected.groups.isEmpty || !projected.tabs.isEmpty else { return }

        let prevURLs = Dictionary(uniqueKeysWithValues: browser.tabs.map { ($0.id, $0.url) })
        let existingProfilesByID = Dictionary(uniqueKeysWithValues: browser.profiles.map { ($0.id, $0) })
        let existingTabsByID = Dictionary(uniqueKeysWithValues: browser.tabs.map { ($0.id, $0) })
        let privateTabs = browser.tabs.filter { $0.isPrivate }

        // 1. Build synced profiles
        let sortedProfileEntries = projected.profiles.sorted { a, b in
            let oa = (a.value["order"] as? NSNumber)?.intValue ?? (a.key == "default" ? -1 : Int.max)
            let ob = (b.value["order"] as? NSNumber)?.intValue ?? (b.key == "default" ? -1 : Int.max)
            if oa != ob { return oa < ob }
            if a.key == "default" { return true }
            if b.key == "default" { return false }
            return a.key < b.key
        }

        var nextProfiles: [BrowserProfile] = []
        var profileUUIDByWireID: [String: UUID] = [:]
        for (pid, row) in sortedProfileEntries {
            guard let name = row["name"] as? String, !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { continue }
            let pUUID = replica.uuid(forWireID: pid, isProfile: true)
            let existing = existingProfilesByID[pUUID]
            let icon = (row["icon"] as? String).map(Self.wireIconToSFSymbol) ?? existing?.icon ?? "person.fill"
            let colorHex: String
            if let num = row["color"] as? NSNumber {
                colorHex = Self.argbUintToHex(UInt32(truncatingIfNeeded: num.uint64Value))
            } else {
                colorHex = existing?.colorHex ?? "#0A84FF"
            }
            nextProfiles.append(BrowserProfile(id: pUUID, name: name, icon: icon, colorHex: colorHex))
            profileUUIDByWireID[pid] = pUUID
        }
        if !nextProfiles.contains(where: { $0.id == BrowserModel.defaultProfileID }) {
            let fallback = existingProfilesByID[BrowserModel.defaultProfileID]
                ?? BrowserProfile(id: BrowserModel.defaultProfileID, name: "개인", icon: "person.fill", colorHex: "#0A84FF")
            nextProfiles.insert(fallback, at: 0)
            profileUUIDByWireID["default"] = BrowserModel.defaultProfileID
        }

        // 2. Build synced groups (each profile gets its synthetic default "기본" group first, followed by all synced custom groups!)
        var nextGroups: [BrowserGroup] = []
        var defaultGroupByProfile: [UUID: UUID] = [:]
        var validGroupUUIDByWireAndProfile: [String: UUID] = [:]

        for profile in nextProfiles {
            let pWire = replica.wireID(forUUID: profile.id, isProfile: true)
            let defGroupID = Self.defaultGroupIDForProfile(profile.id, profileWireID: pWire, groups: browser.groups)
            defaultGroupByProfile[profile.id] = defGroupID
            nextGroups.append(BrowserGroup(id: defGroupID, profileID: profile.id, name: "기본"))
        }

        let sortedGroupEntries = projected.groups.sorted { $0.key < $1.key }
        for (gid, row) in sortedGroupEntries {
            guard gid != "__private_workspace__",
                  gid != "00000000000000000000000000000002",
                  let pid = row["profileId"] as? String,
                  let pUUID = profileUUIDByWireID[pid],
                  let name = row["name"] as? String,
                  !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { continue }
            let gUUID = replica.uuid(forWireID: gid)
            if nextGroups.contains(where: { $0.id == gUUID }) { continue }
            nextGroups.append(BrowserGroup(id: gUUID, profileID: pUUID, name: name))
            validGroupUUIDByWireAndProfile["\(pUUID.uuidString)|\(gid)"] = gUUID
        }

        // 3. Build synced regular tabs in exact CRDT order, preserving local pin/desktop/zoom metadata & private tabs
        let sortedTabEntries = projected.tabs.sorted { a, b in
            let oa = (a.value["order"] as? NSNumber)?.intValue ?? Int.max
            let ob = (b.value["order"] as? NSNumber)?.intValue ?? Int.max
            return oa == ob ? a.key < b.key : oa < ob
        }

        var nextRegularTabs: [BrowserTab] = []
        for (tid, row) in sortedTabEntries.prefix(500) {
            guard let url = row["url"] as? String,
                  let pid = row["profileId"] as? String else { continue }
            let title = (row["title"] as? String).flatMap { $0.isEmpty ? nil : $0 } ?? "새 탭"
            let pUUID = profileUUIDByWireID[pid] ?? nextProfiles[0].id
            let defGroupID = defaultGroupByProfile[pUUID] ?? BrowserModel.defaultGroupID
            let resolvedGroupID: UUID
            if let rawGID = row["groupId"] as? String,
               !rawGID.isEmpty,
               rawGID != "00000000000000000000000000000002",
               let mapped = validGroupUUIDByWireAndProfile["\(pUUID.uuidString)|\(rawGID)"] {
                resolvedGroupID = mapped
            } else {
                resolvedGroupID = defGroupID
            }
            let tUUID = replica.uuid(forWireID: tid)
            let local = existingTabsByID[tUUID]
            nextRegularTabs.append(BrowserTab(
                id: tUUID,
                title: title,
                url: url,
                profileID: pUUID,
                groupID: resolvedGroupID,
                isPrivate: false,
                isDesktopMode: local?.isDesktopMode ?? false,
                pageZoom: local?.pageZoom ?? 1.0,
                isPinned: local?.isPinned ?? false
            ))
        }

        let nextAllTabs = nextRegularTabs + privateTabs

        // 4. Resolve active profile / group / tab without kicking the user out of their current view unless deleted
        let nextActiveProfileID = nextProfiles.contains(where: { $0.id == browser.activeProfileID })
            ? browser.activeProfileID
            : nextProfiles[0].id
        let nextActiveGroupID: UUID
        if browser.activeGroupID == BrowserModel.privateGroupID {
            nextActiveGroupID = BrowserModel.privateGroupID
        } else if nextGroups.contains(where: { $0.id == browser.activeGroupID && $0.profileID == nextActiveProfileID }) {
            nextActiveGroupID = browser.activeGroupID
        } else {
            nextActiveGroupID = defaultGroupByProfile[nextActiveProfileID] ?? BrowserModel.defaultGroupID
        }

        var finalTabs = nextAllTabs
        let nextActiveTabID: UUID
        if finalTabs.contains(where: { $0.id == browser.activeID }) {
            nextActiveTabID = browser.activeID
        } else if let inGroup = finalTabs.first(where: {
            nextActiveGroupID == BrowserModel.privateGroupID
                ? $0.isPrivate
                : (!$0.isPrivate && $0.profileID == nextActiveProfileID && $0.groupID == nextActiveGroupID)
        }) {
            nextActiveTabID = inGroup.id
        } else if let anyRegular = finalTabs.first(where: { !$0.isPrivate }) {
            nextActiveTabID = anyRegular.id
        } else {
            let fresh = BrowserTab(profileID: nextActiveProfileID, groupID: defaultGroupByProfile[nextActiveProfileID] ?? BrowserModel.defaultGroupID)
            finalTabs.append(fresh)
            nextActiveTabID = fresh.id
        }

        // Skip redundant UI churn if nothing changed
        if browser.profiles == nextProfiles &&
           browser.groups == nextGroups &&
           browser.tabs == finalTabs &&
           browser.activeProfileID == nextActiveProfileID &&
           browser.activeGroupID == nextActiveGroupID &&
           browser.activeID == nextActiveTabID {
            return
        }

        browser.isApplyingRemoteSync = true
        defer { browser.isApplyingRemoteSync = false }
        browser.profiles = nextProfiles
        browser.groups = nextGroups
        browser.tabs = finalTabs
        browser.activeProfileID = nextActiveProfileID
        browser.activeGroupID = nextActiveGroupID
        browser.activeID = nextActiveTabID
        browser.reconcileWebViewsAfterRemoteSync(previousURLs: prevURLs)
        browser.persist()
    }

    // MARK: - High-Speed Parallel Bookshelf Sync With Delta CRDT Replica & Cross-App Kernel Lock

    /// Returns `true` if bookshelf sync was safely deferred because another app (Umtomi) holds the shared folder lock.
    private func syncBookshelfParallel(
        shelf: ShelfModel,
        keys: OmyaruDriveKeyRing,
        shelfFiles: [DriveFileEntry],
        rest: DriveRESTClient,
        isBackground: Bool
    ) async throws -> Bool {
        let accountRoot = SharedSyncFolderManager.shared.activeAccountRoot(for: connectedEmail)
        var lockHandle: SharedSyncFolderManager.CrossAppLockHandle? = nil
        let lockStartedAt = Int64(Date().timeIntervalSince1970 * 1000)

        if let accountRoot {
            lockHandle = await SharedSyncFolderManager.acquireSyncLock(
                in: accountRoot,
                appName: SharedSyncFolderManager.appIdentifier,
                maxWaitSeconds: isBackground ? 2.0 : 30.0
            ) { [weak self] waitingText in
                Task { @MainActor in
                    self?.reportSyncStep(phase: "대기 중", detail: waitingText, progress: 0.46)
                }
            }
            guard lockHandle != nil else {
                // Another app (e.g. Umtomi) is actively syncing this exact shared account folder right now.
                return true
            }
            // Reload any changes committed by the other app right before we acquired the lock!
            shelf.reloadFromSharedStorageIfAvailable(accountEmail: connectedEmail)
        }
        defer { lockHandle?.release() }

        let stateDir: URL = accountRoot.map { SharedSyncFolderManager.syncStateDirectory(in: $0) }
            ?? accountBrowserSyncDir()
        var replica = OmyaruShelfReplica(directory: stateDir)

        // 1. Pull unseen remote shelf metadata batches IN PARALLEL (45% -> 60%)
        let seenBefore = replica.seen
        let unreadShelfFiles = shelfFiles.filter { !seenBefore.contains($0.id) }.sorted { $0.name < $1.name }
        if !unreadShelfFiles.isEmpty {
            let fetchedBatches = try await Self.fetchAndDecryptParallel(
                files: unreadShelfFiles,
                keys: keys,
                rest: rest,
                concurrency: Self.maxConcurrentTransfers
            ) { [weak self] done, total in
                Task { @MainActor in
                    let ratio = Double(done) / Double(max(1, total))
                    self?.reportSyncStep(
                        phase: "목록 받기",
                        detail: "책장 목록 병렬 수신 중 (\(done)/\(total))...",
                        progress: 0.45 + 0.15 * ratio,
                        done: done,
                        total: total
                    )
                }
            }
            for (file, plain) in fetchedBatches {
                if let plain, let batch = try? JSONSerialization.jsonObject(with: plain) as? [String: Any] {
                    replica.apply(batch)
                }
                replica.markSeen(file.id)
            }
            try? replica.save()
        }

        let remoteProjected = replica.project()
        lockHandle?.refreshHeartbeat(appName: SharedSyncFolderManager.appIdentifier, startedAt: lockStartedAt)

        // 2. Upload local images missing a cloud manifest IN PARALLEL with SHA-256 manifest cache (60% -> 74%)
        let manifestCacheURL = stateDir.appendingPathComponent("manifests.json")
        var manifestCache: [String: ShelfCloudImage] = (try? Data(contentsOf: manifestCacheURL))
            .flatMap { try? JSONDecoder().decode([String: ShelfCloudImage].self, from: $0) } ?? [:]

        struct UploadCandidate: Sendable {
            let itemIndex: Int
            let itemID: String
            let fileURL: URL
        }
        var uploadQueue: [UploadCandidate] = []
        for idx in shelf.catalog.items.indices {
            if shelf.catalog.items[idx].cloud != nil { continue }
            let item = shelf.catalog.items[idx]
            let url = shelf.imageURL(for: item)
            if FileManager.default.fileExists(atPath: url.path) {
                uploadQueue.append(UploadCandidate(itemIndex: idx, itemID: item.id, fileURL: url))
            }
        }

        if !uploadQueue.isEmpty {
            // Check remote & cached manifests by SHA-256 first before uploading anything!
            var trulyNeedUpload: [(UploadCandidate, Data, String)] = []
            for candidate in uploadQueue {
                guard let bytes = try? Data(contentsOf: candidate.fileURL), !bytes.isEmpty else { continue }
                let sha = OmyaruSyncCrypto.sha256Hex(bytes)
                let cacheKey = "\(candidate.itemID.lowercased())-\(sha)"
                if let remoteRaw = remoteProjected["images/\(candidate.itemID.lowercased())/manifest"] as? [String: Any],
                   let rawData = try? JSONSerialization.data(withJSONObject: remoteRaw),
                   let remoteManifest = try? JSONDecoder().decode(ShelfCloudImage.self, from: rawData),
                   remoteManifest.sha256 == sha {
                    shelf.catalog.items[candidate.itemIndex].cloud = remoteManifest
                } else if let cached = manifestCache[cacheKey], cached.sha256 == sha {
                    shelf.catalog.items[candidate.itemIndex].cloud = cached
                } else {
                    trulyNeedUpload.append((candidate, bytes, sha))
                }
            }

            if !trulyNeedUpload.isEmpty {
                let totalUpload = trulyNeedUpload.count
                var doneUpload = 0
                try await withThrowingTaskGroup(of: (Int, String, String, ShelfCloudImage).self) { group in
                    var nextIdx = 0
                    let limit = max(1, min(4, totalUpload))
                    func enqueue(_ i: Int) {
                        let (cand, bytes, sha) = trulyNeedUpload[i]
                        group.addTask {
                            try Task.checkCancellation()
                            let manifest = try await Self.uploadChunkedImageParallel(data: bytes, keys: keys, rest: rest)
                            return (cand.itemIndex, cand.itemID.lowercased(), sha, manifest)
                        }
                    }
                    for _ in 0..<limit { enqueue(nextIdx); nextIdx += 1 }
                    while let (itemIdx, itemID, sha, manifest) = try await group.next() {
                        if cancelRequested || Task.isCancelled { throw CancellationError() }
                        shelf.catalog.items[itemIdx].cloud = manifest
                        manifestCache["\(itemID)-\(sha)"] = manifest
                        doneUpload += 1
                        let ratio = Double(doneUpload) / Double(totalUpload)
                        reportSyncStep(
                            phase: "업로드",
                            detail: "책장 사진 병렬 업로드 중 (\(doneUpload)/\(totalUpload))...",
                            progress: 0.60 + 0.14 * ratio,
                            done: doneUpload,
                            total: totalUpload
                        )
                        if nextIdx < totalUpload { enqueue(nextIdx); nextIdx += 1 }
                    }
                }
                if let encoded = try? JSONEncoder().encode(manifestCache) {
                    try? encoded.write(to: manifestCacheURL, options: .atomic)
                }
                shelf.save()
            }
        }

        // 3. Delta-Only CRDT Capture & Push (ONLY uploads actually modified fields — 0 uploads when unchanged!)
        let flattenedLocal = Self.flattenCatalog(shelf.catalog)
        replica.capture(flattenedLocal)
        try? replica.save()

        let pendingBatches = replica.pending
        if !pendingBatches.isEmpty {
            let totalPending = pendingBatches.count
            for (idx, batch) in pendingBatches.enumerated() {
                if cancelRequested || Task.isCancelled { throw CancellationError() }
                guard let batchID = batch["id"] as? String else { continue }
                let plain = try JSONSerialization.data(withJSONObject: batch, options: [.sortedKeys])
                let encrypted = try await keys.encryptAsync(plain)
                let uploadedID = try await rest.uploadDriveFile(name: "omyaru-shelf-v1-\(batchID).json", payload: encrypted)
                replica.popPending(uploadedID: uploadedID)
                try? replica.save()
                reportSyncStep(
                    phase: "기록 전송",
                    detail: "책장 변경 기록 전송 중 (\(idx + 1)/\(totalPending))...",
                    progress: 0.74 + 0.03 * Double(idx + 1) / Double(totalPending),
                    done: idx + 1,
                    total: totalPending
                )
            }
        }

        // 4. Project Final Merged Catalog (Handles Folders, Notes, Columns, Renames, Additions & Deletions)
        let finalProjected = replica.project()
        applyProjectedShelf(finalProjected, to: shelf, accountRoot: accountRoot)
        replica.acknowledge(Self.flattenCatalog(shelf.catalog))
        try? replica.save()
        lockHandle?.refreshHeartbeat(appName: SharedSyncFolderManager.appIdentifier, startedAt: lockStartedAt)

        // 5. Parallel Download of Missing Cloud Photos (77% -> 98%), Skipping Any Existing Shared/Local SHA-256 Files!
        if !onDemandCloudPhotos {
            struct DownloadJob: Sendable {
                let itemID: String
                let manifest: ShelfCloudImage
                let destination: URL
                let accountRoot: URL?
            }
            var jobs: [DownloadJob] = []
            let fm = FileManager.default
            for item in shelf.catalog.items {
                guard let manifest = item.cloud else { continue }
                if let accountRoot, SharedSyncFolderManager.existingOriginalURL(sha256: manifest.sha256, in: accountRoot) != nil {
                    continue // Already exists in shared folder (e.g., downloaded by Umtomi!) -> 0 bytes transferred!
                }
                let dest = shelf.imageURL(for: item)
                if fm.fileExists(atPath: dest.path) { continue }
                jobs.append(DownloadJob(itemID: item.id, manifest: manifest, destination: dest, accountRoot: accountRoot))
            }

            if !jobs.isEmpty {
                let totalDown = jobs.count
                var doneDown = 0
                let privateRoot = shelf.privateRoot
                try await withThrowingTaskGroup(of: (String, URL?).self) { group in
                    var nextIdx = 0
                    let limit = max(1, min(Self.maxConcurrentTransfers, totalDown))
                    func enqueue(_ i: Int) {
                        let job = jobs[i]
                        group.addTask {
                            try Task.checkCancellation()
                            let bytes = try await Self.downloadChunkedImageParallel(manifest: job.manifest, keys: keys, rest: rest)
                            if let accountRoot = job.accountRoot {
                                let stored = try SharedSyncFolderManager.storeOriginal(bytes, expectedSHA256: job.manifest.sha256, in: accountRoot)
                                return (job.itemID, stored)
                            } else {
                                try FileManager.default.createDirectory(at: privateRoot, withIntermediateDirectories: true)
                                try bytes.write(to: job.destination, options: .atomic)
                                return (job.itemID, job.destination)
                            }
                        }
                    }
                    for _ in 0..<limit { enqueue(nextIdx); nextIdx += 1 }
                    while let (itemID, storedURL) = try await group.next() {
                        if cancelRequested || Task.isCancelled { throw CancellationError() }
                        if let storedURL,
                           let idx = shelf.catalog.items.firstIndex(where: { $0.id.caseInsensitiveCompare(itemID) == .orderedSame }) {
                            shelf.catalog.items[idx].fileName = storedURL.lastPathComponent
                        }
                        doneDown += 1
                        if doneDown.isMultiple(of: 8) {
                            lockHandle?.refreshHeartbeat(appName: SharedSyncFolderManager.appIdentifier, startedAt: lockStartedAt)
                        }
                        let ratio = Double(doneDown) / Double(totalDown)
                        reportSyncStep(
                            phase: "다운로드",
                            detail: "책장 사진 병렬 받는 중 (\(doneDown)/\(totalDown))...",
                            progress: 0.77 + 0.21 * ratio,
                            done: doneDown,
                            total: totalDown
                        )
                        if nextIdx < totalDown { enqueue(nextIdx); nextIdx += 1 }
                    }
                }
                shelf.save()
            }
        }
        return false
    }

    private static func flattenCatalog(_ catalog: BookshelfCatalogData) -> [String: Any] {
        var fields: [String: Any] = [:]
        let folders = BookshelfPath.allDisplayFolders(catalog.folders)
        for (idx, folder) in folders.enumerated() {
            let fid = (catalog.folderIds[folder] ?? OmyaruSyncCrypto.compactUUID()).lowercased()
            fields["folders/\(fid)/name"] = folder
            fields["folders/\(fid)/note"] = catalog.folderNotes[folder] ?? ""
            fields["folders/\(fid)/columns"] = min(6, max(1, catalog.folderColumnCounts[folder] ?? 2))
            fields["folders/\(fid)/order"] = idx
        }
        for (idx, item) in catalog.items.enumerated() {
            guard let manifest = item.cloud,
                  let manifestData = try? JSONEncoder().encode(manifest),
                  let manifestObj = try? JSONSerialization.jsonObject(with: manifestData) else { continue }
            let fid = (catalog.folderIds[item.folder] ?? OmyaruSyncCrypto.compactUUID()).lowercased()
            let iid = item.id.lowercased()
            fields["images/\(iid)/folderId"] = fid
            fields["images/\(iid)/manifest"] = manifestObj
            fields["images/\(iid)/sourceUrl"] = item.sourceUrl
            fields["images/\(iid)/width"] = max(1, item.width)
            fields["images/\(iid)/height"] = max(1, item.height)
            fields["images/\(iid)/createdAt"] = item.createdAtEpochMs
            fields["images/\(iid)/order"] = idx
        }
        return fields
    }

    private func applyProjectedShelf(_ projected: [String: Any], to shelf: ShelfModel, accountRoot: URL?) {
        var folderMap: [String: [String: Any]] = [:]
        var imageMap: [String: [String: Any]] = [:]
        for (key, value) in projected {
            let parts = key.split(separator: "/").map(String.init)
            guard parts.count == 3 else { continue }
            if parts[0] == "folders" {
                folderMap[parts[1].lowercased(), default: [:]][parts[2]] = value
            } else if parts[0] == "images" {
                imageMap[parts[1].lowercased(), default: [:]][parts[2]] = value
            }
        }
        guard !folderMap.isEmpty || !imageMap.isEmpty else { return }

        let sortedFolders = folderMap.sorted {
            let o1 = ($0.value["order"] as? NSNumber)?.intValue ?? Int.max
            let o2 = ($1.value["order"] as? NSNumber)?.intValue ?? Int.max
            return o1 == o2 ? $0.key < $1.key : o1 < o2
        }

        var nextFolders: [String] = []
        var nextFolderIDs: [String: String] = [:]
        var nextNotes: [String: String] = [:]
        var nextColumns: [String: Int] = [:]
        var idToPath: [String: String] = [:]

        for (fid, row) in sortedFolders {
            guard let rawName = row["name"] as? String,
                  let norm = BookshelfPath.normalize(rawName) else { continue }
            var finalName = norm
            if nextFolders.contains(where: { $0.caseInsensitiveCompare(finalName) == .orderedSame }) {
                finalName = "\(norm) (\(fid.prefix(8)))"
            }
            nextFolders.append(finalName)
            nextFolderIDs[finalName] = fid
            nextNotes[finalName] = (row["note"] as? String) ?? ""
            nextColumns[finalName] = min(6, max(1, (row["columns"] as? NSNumber)?.intValue ?? 2))
            idToPath[fid] = finalName
        }

        let existingByID = Dictionary(uniqueKeysWithValues: shelf.catalog.items.map { ($0.id.lowercased(), $0) })
        let sortedImages = imageMap.sorted {
            let o1 = ($0.value["order"] as? NSNumber)?.intValue ?? Int.max
            let o2 = ($1.value["order"] as? NSNumber)?.intValue ?? Int.max
            return o1 == o2 ? $0.key < $1.key : o1 < o2
        }

        var nextItems: [BookshelfItem] = []
        for (imageID, row) in sortedImages {
            guard let fid = (row["folderId"] as? String)?.lowercased(),
                  let folderPath = idToPath[fid],
                  let manifestObj = row["manifest"] as? [String: Any],
                  let manifestData = try? JSONSerialization.data(withJSONObject: manifestObj),
                  let manifest = try? JSONDecoder().decode(ShelfCloudImage.self, from: manifestData) else { continue }
            let width = max(1, (row["width"] as? NSNumber)?.intValue ?? 720)
            let height = max(1, (row["height"] as? NSNumber)?.intValue ?? 1024)
            let sourceUrl = (row["sourceUrl"] as? String) ?? ""
            let created = (row["createdAt"] as? NSNumber)?.int64Value ?? Int64(Date().timeIntervalSince1970 * 1000)

            let resolvedFileName: String
            if let accountRoot,
               let existingShared = SharedSyncFolderManager.existingOriginalURL(sha256: manifest.sha256, in: accountRoot) {
                resolvedFileName = existingShared.lastPathComponent
            } else if let prev = existingByID[imageID] {
                resolvedFileName = prev.fileName
            } else if accountRoot != nil {
                resolvedFileName = "\(manifest.sha256.lowercased()).png"
            } else {
                resolvedFileName = "image-sync-\(imageID)-\(manifest.sha256.prefix(12)).png"
            }

            nextItems.append(BookshelfItem(
                id: imageID,
                folder: folderPath,
                fileName: resolvedFileName,
                sourceUrl: sourceUrl,
                width: width,
                height: height,
                createdAtEpochMs: created,
                cloud: manifest
            ))
        }

        // Preserve any unsynced local-only items (cloud == nil) so nothing is ever lost.
        for localOnly in shelf.catalog.items where localOnly.cloud == nil {
            if !nextItems.contains(where: { $0.id.caseInsensitiveCompare(localOnly.id) == .orderedSame }) {
                if !nextFolders.contains(where: { $0.caseInsensitiveCompare(localOnly.folder) == .orderedSame }) {
                    nextFolders.append(localOnly.folder)
                }
                nextItems.append(localOnly)
            }
        }

        if nextFolders.isEmpty { nextFolders = ["기본 책장"] }
        let prevActiveID = shelf.catalog.folderIds[shelf.catalog.activeFolder]?.lowercased()
        let nextActive = prevActiveID.flatMap { idToPath[$0] }
            ?? nextFolders.first(where: { $0.caseInsensitiveCompare(shelf.catalog.activeFolder) == .orderedSame })
            ?? nextFolders[0]

        shelf.catalog.folders = nextFolders
        shelf.catalog.folderIds = nextFolderIDs
        shelf.catalog.folderNotes = nextNotes
        shelf.catalog.folderColumnCounts = nextColumns
        shelf.catalog.items = nextItems
        shelf.catalog.activeFolder = nextActive
        shelf.save()
    }

    // MARK: - Parallel Chunk Upload & Download (Off-MainActor Multi-Core PBKDF2 + HTTP/2!)

    private nonisolated static func uploadChunkedImageParallel(
        data: Data,
        keys: OmyaruDriveKeyRing,
        rest: DriveRESTClient
    ) async throws -> ShelfCloudImage {
        let totalLength = data.count
        var slices: [(Int, Data, String)] = []
        var offset = 0
        var chunkIndex = 0
        while offset < totalLength {
            let end = min(totalLength, offset + chunkSize)
            let chunk = data.subdata(in: offset..<end)
            slices.append((chunkIndex, chunk, OmyaruSyncCrypto.sha256Hex(chunk)))
            offset = end
            chunkIndex += 1
        }
        let wholeHash = OmyaruSyncCrypto.sha256Hex(data)

        let uploaded = try await withThrowingTaskGroup(of: (Int, String, ShelfCloudImage.Reference).self) { group in
            for (idx, chunk, chunkHash) in slices {
                group.addTask {
                    try Task.checkCancellation()
                    let row: [String: Any] = [
                        "version": 1,
                        "index": idx,
                        "sha256": chunkHash,
                        "data": chunk.base64EncodedString()
                    ]
                    let plain = try JSONSerialization.data(withJSONObject: row, options: [.sortedKeys])
                    let encrypted = try keys.encrypt(plain)
                    let opID = OmyaruSyncCrypto.compactUUID()
                    let fileName = "omyaru-shelf-chunk-v1-\(opID).json"
                    let cloudID = try await rest.uploadDriveFile(name: fileName, payload: encrypted)
                    return (idx, chunkHash, ShelfCloudImage.Reference(id: cloudID, name: fileName))
                }
            }
            var out: [(Int, String, ShelfCloudImage.Reference)] = []
            out.reserveCapacity(slices.count)
            for try await item in group { out.append(item) }
            return out.sorted { $0.0 < $1.0 }
        }

        return ShelfCloudImage(
            version: 1,
            length: totalLength,
            sha256: wholeHash,
            chunks: uploaded.map(\.1),
            files: uploaded.map(\.2)
        )
    }

    private nonisolated static func downloadChunkedImageParallel(
        manifest: ShelfCloudImage,
        keys: OmyaruDriveKeyRing,
        rest: DriveRESTClient
    ) async throws -> Data {
        let chunkCount = manifest.files.count
        let parts: [(Int, Data)] = try await withThrowingTaskGroup(of: (Int, Data).self) { group in
            for (idx, ref) in manifest.files.enumerated() {
                let expectedChunkHash = idx < manifest.chunks.count ? manifest.chunks[idx] : nil
                group.addTask {
                    try Task.checkCancellation()
                    let encrypted = try await rest.readDriveFile(ref.id)
                    let plain = try keys.decrypt(encrypted)
                    guard let row = try JSONSerialization.jsonObject(with: plain) as? [String: Any],
                          let b64 = row["data"] as? String,
                          let chunk = Data(base64Encoded: b64) else {
                        throw NSError(domain: "OmyaruDrive", code: -30, userInfo: [NSLocalizedDescriptionKey: "이미지 조각 \(idx) 복호화 실패"])
                    }
                    if let expectedChunkHash, OmyaruSyncCrypto.sha256Hex(chunk) != expectedChunkHash {
                        throw NSError(domain: "OmyaruDrive", code: -31, userInfo: [NSLocalizedDescriptionKey: "이미지 조각 무결성 검증 실패"])
                    }
                    return (idx, chunk)
                }
            }
            var out: [(Int, Data)] = []
            out.reserveCapacity(chunkCount)
            for try await item in group { out.append(item) }
            return out.sorted { $0.0 < $1.0 }
        }

        var output = Data(capacity: manifest.length)
        for (_, chunk) in parts { output.append(chunk) }
        guard OmyaruSyncCrypto.sha256Hex(output) == manifest.sha256 else {
            throw NSError(domain: "OmyaruDrive", code: -32, userInfo: [NSLocalizedDescriptionKey: "전체 이미지 SHA-256 무결성 검증 실패"])
        }
        return output
    }

    private func loadSeenSet(name: String) -> Set<String> {
        let url = accountBrowserSyncDir().appendingPathComponent(name)
        let fallback = syncStateDirectory.appendingPathComponent(name)
        let data = (try? Data(contentsOf: url)) ?? (try? Data(contentsOf: fallback))
        guard let data, let arr = try? JSONDecoder().decode([String].self, from: data) else { return [] }
        return Set(arr)
    }

    private func saveSeenSet(_ set: Set<String>, name: String) {
        let url = accountBrowserSyncDir().appendingPathComponent(name)
        if let data = try? JSONEncoder().encode(Array(set)) {
            try? data.write(to: url, options: .atomic)
        }
    }

    // MARK: - Portable Encrypted .omyarusync File Export & Import

    func exportEncryptedBundle(browser: BrowserModel?, shelf: ShelfModel, password: String) throws -> Data {
        var imagesB64: [String: String] = [:]
        for item in shelf.catalog.items {
            let url = shelf.imageURL(for: item)
            if let data = try? Data(contentsOf: url), data.count <= 15 * 1024 * 1024 {
                imagesB64[item.fileName] = data.base64EncodedString()
            }
        }
        let bundle = OmyaruFullSyncBundle(
            profiles: browser?.profiles ?? [],
            groups: browser?.groups ?? [],
            tabs: browser?.tabs.filter { !$0.isPrivate } ?? [],
            bookmarkFolders: browser?.bookmarkFolders ?? [],
            bookmarks: browser?.bookmarks ?? [],
            history: browser?.history ?? [],
            bookshelf: shelf.catalog,
            bookshelfImagesBase64: imagesB64
        )
        let plain = try JSONEncoder().encode(bundle)
        let key = password.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "omyaru-default-sync-key" : password
        return try OmyaruSyncCrypto.encrypt(plain, password: key)
    }

    func importEncryptedBundle(_ data: Data, browser: BrowserModel?, shelf: ShelfModel, password: String) throws {
        let key = password.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "omyaru-default-sync-key" : password
        let plain = try OmyaruSyncCrypto.decrypt(data, password: key)
        let bundle = try JSONDecoder().decode(OmyaruFullSyncBundle.self, from: plain)

        if let browser {
            for p in bundle.profiles where !browser.profiles.contains(where: { $0.id == p.id }) {
                browser.profiles.append(p)
            }
            for g in bundle.groups where !browser.groups.contains(where: { $0.id == g.id }) {
                browser.groups.append(g)
            }
            for t in bundle.tabs where !t.isPrivate && !browser.tabs.contains(where: { $0.url == t.url && $0.url != "about:blank" }) {
                browser.tabs.append(t)
            }
            for f in bundle.bookmarkFolders where !browser.bookmarkFolders.contains(where: { $0.id == f.id }) {
                browser.bookmarkFolders.append(f)
            }
            for b in bundle.bookmarks where !browser.bookmarks.contains(where: { $0.url == b.url }) {
                browser.bookmarks.append(b)
            }
            for h in bundle.history where !browser.history.contains(where: { $0.url == h.url }) {
                browser.history.append(h)
            }
            browser.persist()
        }

        let accountRoot = SharedSyncFolderManager.shared.activeAccountRoot(for: connectedEmail)
        try FileManager.default.createDirectory(at: shelf.privateRoot, withIntermediateDirectories: true)
        for (fileName, b64) in bundle.bookshelfImagesBase64 {
            if let imgData = Data(base64Encoded: b64) {
                if let accountRoot {
                    _ = try? SharedSyncFolderManager.storeOriginal(imgData, fallbackExt: (fileName as NSString).pathExtension, in: accountRoot)
                } else {
                    let target = shelf.privateRoot.appendingPathComponent(fileName)
                    if !FileManager.default.fileExists(atPath: target.path) {
                        try? imgData.write(to: target, options: .atomic)
                    }
                }
            }
        }
        for folder in bundle.bookshelf.folders {
            _ = shelf.addFolder(folder, select: false)
        }
        for (k, v) in bundle.bookshelf.folderNotes {
            shelf.catalog.folderNotes[k] = v
        }
        for (k, v) in bundle.bookshelf.folderColumnCounts {
            shelf.catalog.folderColumnCounts[k] = v
        }
        for item in bundle.bookshelf.items where !shelf.catalog.items.contains(where: { $0.id == item.id }) {
            shelf.catalog.items.append(item)
        }
        shelf.save()
        lastSyncedAt = Date()
        status = "암호화 동기화 번들 가져오기 완료"
    }
}

// MARK: - Drive Sync Sheet UI (Includes First/Existing-User Folder Setup, LiveContainer Fallback Info & Tab Reset)

struct EncryptedSyncDocument: FileDocument {
    static var readableContentTypes: [UTType] { [.json, .data] }
    var data: Data
    init(data: Data) { self.data = data }
    init(configuration: ReadConfiguration) throws {
        data = configuration.file.regularFileContents ?? Data()
    }
    func fileWrapper(configuration: WriteConfiguration) throws -> FileWrapper {
        FileWrapper(regularFileWithContents: data)
    }
}

struct DriveSyncSheet: View {
    @Environment(\.dismiss) private var dismiss
    let browser: BrowserModel?
    @ObservedObject var shelf: ShelfModel
    @ObservedObject var sync: DriveSyncManager = .shared
    @ObservedObject var sharedFolder: SharedSyncFolderManager = .shared

    @State private var exportingBundle = false
    @State private var importingBundle = false
    @State private var exportDoc = EncryptedSyncDocument(data: Data())
    @State private var showTabResetPrompt = false
    @State private var tabResetInput = ""
    @State private var showFolderSetup = false
    @State private var syncAfterFolderSetup = false
    @State private var alertMessage: String? = nil

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    if sync.isConnected {
                        HStack(spacing: 12) {
                            Image(systemName: "checkmark.icloud.fill")
                                .font(.title2)
                                .foregroundStyle(.green)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(sync.connectedEmail.isEmpty ? "Google Drive 연결됨" : sync.connectedEmail)
                                    .font(.subheadline.weight(.semibold))
                                Text("Android · PC · iOS 병렬 암호화 동기화 활성")
                                    .font(.caption2)
                                    .foregroundStyle(.secondary)
                            }
                        }
                        .padding(.vertical, 4)

                        Button {
                            if sharedFolder.needsInitialFolderSetup {
                                syncAfterFolderSetup = true
                                showFolderSetup = true
                            } else {
                                Task { await sync.syncWithGoogleDrive(browser: browser, shelf: shelf) }
                            }
                        } label: {
                            HStack {
                                Label("지금 동기화", systemImage: "arrow.triangle.2.circlepath.icloud.fill")
                                    .font(.subheadline.weight(.semibold))
                                Spacer()
                                if sync.busy { ProgressView().controlSize(.small) }
                            }
                        }
                        .disabled(sync.busy)

                        if sync.busy {
                            Button(role: .destructive) {
                                sync.cancelActiveSync()
                            } label: {
                                Label("동기화 중단", systemImage: "stop.circle")
                            }
                        }

                        Button {
                            syncAfterFolderSetup = false
                            showFolderSetup = true
                        } label: {
                            HStack {
                                VStack(alignment: .leading, spacing: 3) {
                                    Label("동기화 저장 폴더", systemImage: "folder.badge.gearshape")
                                        .font(.subheadline)
                                    Text(sharedFolder.accountSubpathPreview(for: sync.connectedEmail))
                                        .font(.caption2.monospaced())
                                        .foregroundStyle(.secondary)
                                }
                                Spacer()
                                Text(sharedFolder.isUsingSharedFolder ? "공유 중" : "변경")
                                    .font(.caption.weight(.semibold))
                                    .foregroundStyle(sharedFolder.isUsingSharedFolder ? .green : .blue)
                            }
                        }
                        .disabled(sync.busy)

                        Toggle("앱 활성화 시 자동 동기화", isOn: $sync.autoSyncEnabled)

                        Toggle("백그라운드 동기화", isOn: $sync.backgroundSyncEnabled)

                        Toggle("실시간 현황 / 진행 알림 표시", isOn: $sync.liveActivityEnabled)

                        if OmyaruSyncLiveActivityController.shared.isRunningInLiveContainer {
                            HStack(alignment: .top, spacing: 8) {
                                Image(systemName: "info.circle.fill")
                                    .foregroundStyle(.orange)
                                Text("LiveContainer 실행 감지됨 · iOS 구조상 위젯 확장(ActivityKit)이 제한되어 실시간 진행 알림과 앱 상단 라이브 바로 자동 대체됩니다. 잠금화면 실시간 현황(Dynamic Island)을 사용하려면 AltStore/SideStore에 단독 설치해 주세요.")
                                    .font(.caption2)
                                    .foregroundStyle(.secondary)
                            }
                            .padding(.vertical, 2)
                        } else if sync.liveActivityEnabled && !OmyaruSyncLiveActivityController.shared.areLiveActivitiesAvailable {
                            Button {
                                if let url = URL(string: UIApplication.openSettingsURLString) {
                                    UIApplication.shared.open(url)
                                }
                            } label: {
                                Label("iOS 설정에서 실시간 현황 허용 열기", systemImage: "gearshape")
                                    .font(.caption)
                            }
                        }

                        Picker("책장 사진 저장 방식", selection: $sync.onDemandCloudPhotos) {
                            Text("기기에 원본 모두 저장").tag(false)
                            Text("필요할 때 클라우드에서 불러오기").tag(true)
                        }
                    } else {
                        VStack(alignment: .leading, spacing: 10) {
                            Text("Google 계정으로 한 번 로그인하면 Android, Windows, iOS의 탭·북마크·방문 기록·책장이 Google Drive 앱 전용 공간에 암호화되어 자동 동기화됩니다.")
                                .font(.footnote)
                                .foregroundStyle(.secondary)

                            Button {
                                Task {
                                    if await sync.signInWithGoogle() {
                                        if sharedFolder.needsInitialFolderSetup {
                                            syncAfterFolderSetup = true
                                            showFolderSetup = true
                                        } else {
                                            await sync.syncWithGoogleDrive(browser: browser, shelf: shelf)
                                        }
                                    }
                                }
                            } label: {
                                HStack {
                                    Spacer()
                                    if sync.busy {
                                        ProgressView().controlSize(.small).padding(.trailing, 6)
                                    } else {
                                        Image(systemName: "person.badge.key.fill")
                                    }
                                    Text(sync.busy ? "Google 계정 연결 중..." : "Google 계정 연결")
                                        .font(.subheadline.weight(.semibold))
                                    Spacer()
                                }
                                .padding(.vertical, 10)
                            }
                            .buttonStyle(.borderedProminent)
                            .disabled(sync.busy)
                        }
                        .padding(.vertical, 4)
                    }

                    if let prog = sync.progress {
                        ProgressView(value: prog) {
                            Text("동기화 진행 중")
                                .font(.caption)
                        } currentValueLabel: {
                            Text("\(Int((prog * 100).rounded()))%")
                                .font(.caption.monospacedDigit())
                        }
                    }

                    if !sync.status.isEmpty {
                        Text(sync.status)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }

                    if let last = sync.lastSyncedAt {
                        LabeledContent("마지막 동기화") {
                            Text(last, format: .dateTime.month().day().hour().minute())
                        }
                    }
                } header: {
                    Text("Google Drive 동기화")
                }

                if sync.isConnected {
                    Section("동기화 관리") {
                        Button {
                            Task {
                                if await sync.signInWithGoogle() {
                                    if sharedFolder.needsInitialFolderSetup {
                                        syncAfterFolderSetup = true
                                        showFolderSetup = true
                                    } else {
                                        await sync.syncWithGoogleDrive(browser: browser, shelf: shelf)
                                    }
                                }
                            }
                        } label: {
                            Label("다른 Google 계정으로 변경", systemImage: "person.2")
                        }
                        .disabled(sync.busy)

                        Button("모든 기기 동기화 탭 초기화...", role: .destructive) {
                            tabResetInput = ""
                            showTabResetPrompt = true
                        }
                        .disabled(sync.busy)

                        Button("Google 계정 연결 해제", role: .destructive) {
                            sync.disconnectGoogle()
                        }
                        .disabled(sync.busy)
                    }
                }

                Section {
                    SecureField("백업 파일 암호 (6자 이상)", text: $sync.syncPassphrase)
                    Button {
                        do {
                            let bytes = try sync.exportEncryptedBundle(browser: browser, shelf: shelf, password: sync.syncPassphrase)
                            exportDoc = EncryptedSyncDocument(data: bytes)
                            exportingBundle = true
                        } catch {
                            alertMessage = error.localizedDescription
                        }
                    } label: {
                        Label("암호화 백업 파일(.omyarusync) 내보내기", systemImage: "square.and.arrow.up")
                    }
                    Button {
                        importingBundle = true
                    } label: {
                        Label("암호화 백업 파일(.omyarusync) 가져오기", systemImage: "square.and.arrow.down")
                    }
                } header: {
                    Text("오프라인 파일 백업 (선택)")
                } footer: {
                    Text("Google Drive 없이도 AirDrop이나 파일 앱으로 암호화된 데이터를 직접 옮길 수 있습니다.")
                }
            }
            .navigationTitle("동기화")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("완료") { dismiss() }
                }
            }
            .onAppear {
                // Existing already-connected users who have never seen the folder picker get prompted immediately!
                if sync.isConnected && sharedFolder.needsInitialFolderSetup {
                    syncAfterFolderSetup = false
                    showFolderSetup = true
                }
            }
            .sheet(isPresented: $showFolderSetup) {
                SharedSyncFolderSetupSheet(
                    shelf: shelf,
                    accountEmail: sync.connectedEmail
                ) {
                    if syncAfterFolderSetup {
                        syncAfterFolderSetup = false
                        Task { await sync.syncWithGoogleDrive(browser: browser, shelf: shelf) }
                    }
                }
            }
            .alert("동기화 탭 초기화", isPresented: $showTabResetPrompt) {
                TextField(DriveSyncManager.tabResetConfirmationPhrase, text: $tabResetInput)
                Button("초기화", role: .destructive) {
                    Task {
                        do {
                            try await sync.publishTabReset(confirmation: tabResetInput, browser: browser)
                        } catch {
                            alertMessage = error.localizedDescription
                        }
                    }
                }
                Button("취소", role: .cancel) {}
            } message: {
                Text("모든 연결 기기의 동기화 탭을 비웁니다. 프로필·책장·비공개 탭은 유지됩니다.\n\n‘\(DriveSyncManager.tabResetConfirmationPhrase)’를 정확히 입력하세요.")
            }
            .fileExporter(
                isPresented: $exportingBundle,
                document: exportDoc,
                contentType: .json,
                defaultFilename: "Omyaru-Sync-\(Int(Date().timeIntervalSince1970)).omyarusync.json"
            ) { result in
                if case .success = result {
                    sync.status = "암호화 동기화 파일 내보내기 완료"
                }
            }
            .fileImporter(isPresented: $importingBundle, allowedContentTypes: [.json, .data]) { result in
                if case let .success(url) = result {
                    let access = url.startAccessingSecurityScopedResource()
                    defer { if access { url.stopAccessingSecurityScopedResource() } }
                    do {
                        let data = try Data(contentsOf: url)
                        try sync.importEncryptedBundle(data, browser: browser, shelf: shelf, password: sync.syncPassphrase)
                    } catch {
                        alertMessage = "가져오기 실패: \(error.localizedDescription)"
                    }
                }
            }
            .alert("동기화 알림", isPresented: Binding(get: { alertMessage != nil }, set: { if !$0 { alertMessage = nil } })) {
                Button("확인") { alertMessage = nil }
            } message: {
                Text(alertMessage ?? "")
            }
        }
        .preferredColorScheme(.dark)
    }
}
