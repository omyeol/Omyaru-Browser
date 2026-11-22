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

// MARK: - KeyRing Supporting Multiple Account Keys & Legacy Passphrase Envelopes

struct OmyaruDriveKeyRing {
    var keys: [String: String] = [:]
    var legacyPasswords: [String] = []

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

// MARK: - Google Drive AppData & Chunked Bookshelf Sync Coordinator

@MainActor
final class DriveSyncManager: NSObject, ObservableObject, ASWebAuthenticationPresentationContextProviding {
    static let shared = DriveSyncManager()
    static let tabResetConfirmationPhrase = "동기화 탭 초기화"
    static let chunkSize = 1024 * 1024
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
    @Published var onDemandCloudPhotos: Bool {
        didSet { UserDefaults.standard.set(onDemandCloudPhotos, forKey: "omyaru.ios.drive.onDemand") }
    }
    @Published private(set) var isConnected: Bool = false
    @Published var busy: Bool = false
    @Published var status: String = "Google 계정을 연결하면 Android · PC · iOS가 자동 동기화됩니다."
    @Published var progress: Double? = nil
    @Published var lastSyncedAt: Date? = nil

    private var accessToken: String = "" {
        didSet {
            Self.saveKeychainString(accessToken, account: "access_token")
            isConnected = !accessToken.isEmpty || !refreshToken.isEmpty
        }
    }
    private var refreshToken: String = "" {
        didSet {
            Self.saveKeychainString(refreshToken, account: "refresh_token")
            isConnected = !accessToken.isEmpty || !refreshToken.isEmpty
        }
    }
    private var cachedKeyRing: OmyaruDriveKeyRing? = nil

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

    private static func saveKeychainString(_ value: String, account: String) {
        let baseQuery: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: keychainService,
            kSecAttrAccount as String: account
        ]
        SecItemDelete(baseQuery as CFDictionary)
        guard !value.isEmpty, let data = value.data(using: .utf8) else { return }
        var item = baseQuery
        item[kSecValueData as String] = data
        item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        SecItemAdd(item as CFDictionary, nil)
    }

    override init() {
        let d = UserDefaults.standard
        connectedEmail = d.string(forKey: "omyaru.ios.drive.email") ?? ""
        autoSyncEnabled = d.bool(forKey: "omyaru.ios.drive.autoSync")
        onDemandCloudPhotos = d.bool(forKey: "omyaru.ios.drive.onDemand")

        let kcAccess = Self.readKeychainString(account: "access_token") ?? d.string(forKey: "omyaru.ios.drive.accessToken") ?? ""
        let kcRefresh = Self.readKeychainString(account: "refresh_token") ?? d.string(forKey: "omyaru.ios.drive.refreshToken") ?? ""
        let kcPass = Self.readKeychainString(account: "sync_passphrase") ?? d.string(forKey: "omyaru.ios.drive.passphrase") ?? ""
        syncPassphrase = kcPass
        super.init()

        accessToken = kcAccess
        refreshToken = kcRefresh
        // Clean up any legacy plaintext token entries in UserDefaults
        d.removeObject(forKey: "omyaru.ios.drive.clientId")
        d.removeObject(forKey: "omyaru.ios.drive.accessToken")
        d.removeObject(forKey: "omyaru.ios.drive.refreshToken")
        d.removeObject(forKey: "omyaru.ios.drive.passphrase")
        if isConnected {
            status = connectedEmail.isEmpty ? "Google Drive 연결됨" : "\(connectedEmail) 연결됨"
        }
    }

    func presentationAnchor(for session: ASWebAuthenticationSession) -> ASPresentationAnchor {
        UIApplication.shared.connectedScenes
            .compactMap { $0 as? UIWindowScene }
            .flatMap(\.windows)
            .first(where: \.isKeyWindow) ?? ASPresentationAnchor()
    }

    // MARK: - Built-In One-Tap Google OAuth 2.0 PKCE Sign-In & Refresh (Zero User Setup!)

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
            status = connectedEmail.isEmpty ? "Google Drive 연결 완료" : "\(connectedEmail) 연결 완료"
            return true
        } catch {
            status = "Google 로그인 취소 또는 실패: \(error.localizedDescription)"
            return false
        }
    }

    private func fetchConnectedAccountEmail() async {
        guard let url = URL(string: "https://www.googleapis.com/drive/v3/about?fields=user(emailAddress,displayName)") else { return }
        if let data = try? await driveDataRequest(URLRequest(url: url, timeoutInterval: 15)),
           let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           let user = json["user"] as? [String: Any],
           let email = user["emailAddress"] as? String, !email.isEmpty {
            connectedEmail = email
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

    func disconnectGoogle() {
        accessToken = ""
        refreshToken = ""
        connectedEmail = ""
        autoSyncEnabled = false
        cachedKeyRing = nil
        status = "Google Drive 연결이 해제되었습니다."
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

    // MARK: - Full Google Drive Sync (Tabs, Profiles, Groups, Resets, Bookmarks, History, Bookshelf + Chunks!)

    func syncWithGoogleDrive(browser: BrowserModel?, shelf: ShelfModel) async {
        guard !busy else { return }
        if accessToken.isEmpty && !refreshToken.isEmpty {
            _ = await refreshAccessTokenIfPossible()
        }
        guard !accessToken.isEmpty else {
            status = "먼저 Google 계정을 연결해 주세요."
            return
        }
        busy = true
        progress = 0.05
        status = "Google Drive 계정 키 확인 중..."
        defer {
            busy = false
            progress = nil
        }

        do {
            let keyRing = try await loadOrCreateKeyRing()
            cachedKeyRing = keyRing
            progress = 0.15

            if let browser {
                status = "북마크·방문 기록 동기화 중..."
                try await syncUserData(browser: browser, keys: keyRing)
                progress = 0.35

                status = "프로필·탭 그룹·열린 탭 및 초기화 기록 확인 중..."
                try await syncTabsAndResets(browser: browser, keys: keyRing)
                progress = 0.55
            }

            status = "책장 폴더·메모·이미지 조각 동기화 중..."
            try await syncBookshelfWithChunks(shelf: shelf, keys: keyRing)

            progress = 1.0
            lastSyncedAt = Date()
            status = "Google Drive 동기화 완료 (프로필·탭·북마크·책장 이미지)"
        } catch {
            status = "동기화 실패: \(error.localizedDescription)"
        }
    }

    /// Publishes an `omyaru-tab-reset-v1-<id>.json` record (Android `DriveTabReset` exact wire parity!)
    func publishTabReset(confirmation: String, browser: BrowserModel?) async throws {
        guard confirmation == Self.tabResetConfirmationPhrase else {
            throw NSError(domain: "OmyaruDrive", code: -20, userInfo: [NSLocalizedDescriptionKey: "‘\(Self.tabResetConfirmationPhrase)’를 정확히 입력해야 합니다."])
        }
        let keys = try await loadOrCreateKeyRing()
        let resetID = OmyaruSyncCrypto.compactUUID()
        let payloadObj: [String: Any] = [
            "format": "omyaru-tab-reset",
            "version": 1,
            "id": resetID
        ]
        let plain = try JSONSerialization.data(withJSONObject: payloadObj, options: [.sortedKeys])
        let encrypted = try keys.encrypt(plain)
        _ = try await uploadDriveFile(name: "omyaru-tab-reset-v1-\(resetID).json", payload: encrypted)
        var seenResets = loadSeenSet(name: "seen-tab-resets.json")
        seenResets.insert(resetID)
        saveSeenSet(seenResets, name: "seen-tab-resets.json")
        browser?.closeAllVisibleTabs()
        status = "모든 기기의 동기화 탭 초기화 요청을 기록했습니다."
    }

    /// On-demand download of a cloud bookshelf photo's encrypted 1MB chunks (`omyaru-shelf-chunk-v1-*.json`)!
    func downloadCloudImageIfNeeded(item: BookshelfItem, shelf: ShelfModel) async -> URL? {
        let localURL = shelf.imageURL(for: item)
        if FileManager.default.fileExists(atPath: localURL.path) {
            return localURL
        }
        guard let manifest = item.cloud, isConnected else { return nil }
        if accessToken.isEmpty { _ = await refreshAccessTokenIfPossible() }
        guard !accessToken.isEmpty else { return nil }
        do {
            let keys: OmyaruDriveKeyRing
            if let cached = cachedKeyRing {
                keys = cached
            } else {
                keys = try await loadOrCreateKeyRing()
                cachedKeyRing = keys
            }
            let assembled = try await downloadChunkedImage(manifest: manifest, keys: keys)
            try FileManager.default.createDirectory(at: shelf.root, withIntermediateDirectories: true)
            try assembled.write(to: localURL, options: .atomic)
            return localURL
        } catch {
            return nil
        }
    }

    // MARK: - Low-Level Drive REST Helpers

    private struct DriveFileEntry: Decodable {
        let id: String
        let name: String
    }

    private func driveDataRequest(_ req: URLRequest, retried: Bool = false) async throws -> Data {
        var signed = req
        signed.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        let (data, resp) = try await URLSession.shared.data(for: signed)
        guard let http = resp as? HTTPURLResponse else {
            throw NSError(domain: "OmyaruDrive", code: -1, userInfo: [NSLocalizedDescriptionKey: "잘못된 응답"])
        }
        if http.statusCode == 401 && !retried {
            if await refreshAccessTokenIfPossible() {
                return try await driveDataRequest(req, retried: true)
            }
        }
        guard (200..<300).contains(http.statusCode) else {
            throw NSError(domain: "OmyaruDrive", code: http.statusCode, userInfo: [NSLocalizedDescriptionKey: "Google Drive 요청 실패 (HTTP \(http.statusCode))"])
        }
        return data
    }

    private func listDriveFiles(prefix: String) async throws -> [DriveFileEntry] {
        struct FileListResponse: Decodable {
            let files: [DriveFileEntry]
            let nextPageToken: String?
        }
        var results: [DriveFileEntry] = []
        var pageToken: String? = nil
        repeat {
            var comps = URLComponents(string: "https://www.googleapis.com/drive/v3/files")!
            var query = [
                URLQueryItem(name: "spaces", value: "appDataFolder"),
                URLQueryItem(name: "pageSize", value: "1000"),
                URLQueryItem(name: "fields", value: "nextPageToken,files(id,name)"),
                URLQueryItem(name: "q", value: "trashed = false and name contains '\(prefix)'")
            ]
            if let pageToken { query.append(URLQueryItem(name: "pageToken", value: pageToken)) }
            comps.queryItems = query
            let data = try await driveDataRequest(URLRequest(url: comps.url!, timeoutInterval: 25))
            let page = try JSONDecoder().decode(FileListResponse.self, from: data)
            results.append(contentsOf: page.files)
            pageToken = page.nextPageToken
        } while pageToken != nil && results.count < 10_000
        return results
    }

    private func readDriveFile(_ fileID: String) async throws -> Data {
        var comps = URLComponents(string: "https://www.googleapis.com/drive/v3/files/\(fileID)")!
        comps.queryItems = [URLQueryItem(name: "alt", value: "media")]
        return try await driveDataRequest(URLRequest(url: comps.url!, timeoutInterval: 30))
    }

    @discardableResult
    private func uploadDriveFile(name: String, payload: Data) async throws -> String {
        let boundary = "omyaru-\(UUID().uuidString)"
        var body = Data()
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

        let data = try await driveDataRequest(req)
        guard let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let id = json["id"] as? String else {
            throw NSError(domain: "OmyaruDrive", code: -3, userInfo: [NSLocalizedDescriptionKey: "업로드 파일 ID를 확인하지 못했습니다."])
        }
        return id
    }

    private func loadOrCreateKeyRing() async throws -> OmyaruDriveKeyRing {
        var ring = OmyaruDriveKeyRing()
        if syncPassphrase.count >= 6 {
            ring.legacyPasswords.append(syncPassphrase)
        }
        let keyFiles = try await listDriveFiles(prefix: "omyaru-account-key-v1-")
        for file in keyFiles.sorted(by: { $0.name < $1.name }) {
            let expectedID = file.name
                .replacingOccurrences(of: "omyaru-account-key-v1-", with: "")
                .replacingOccurrences(of: ".json", with: "")
            if let data = try? await readDriveFile(file.id) {
                try? ring.addRecord(data, expectedID: expectedID)
            }
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
            _ = try await uploadDriveFile(name: "omyaru-account-key-v1-\(newID).json", payload: payload)
            try ring.addRecord(payload, expectedID: newID)
        }
        return ring
    }

    // MARK: - Sync Sub-Protocols

    private func syncUserData(browser: BrowserModel, keys: OmyaruDriveKeyRing) async throws {
        var seen = loadSeenSet(name: "seen-userdata.json")
        let files = try await listDriveFiles(prefix: "omyaru-userdata-v1-")
        for file in files where !seen.contains(file.id) {
            guard let encrypted = try? await readDriveFile(file.id),
                  let plain = try? keys.decrypt(encrypted),
                  let doc = try? JSONSerialization.jsonObject(with: plain) as? [String: Any] else { continue }
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
                    for row in items {
                        guard let title = row["title"] as? String,
                              let url = row["url"] as? String else { continue }
                        let folderID = row["folderId"] as? String
                        if !browser.bookmarks.contains(where: { $0.url == url }) {
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
            seen.insert(file.id)
        }
        saveSeenSet(seen, name: "seen-userdata.json")
        browser.persist()

        let opID = OmyaruSyncCrypto.compactUUID()
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
        let payloadObj: [String: Any] = [
            "id": opID,
            "deviceId": UIDevice.current.identifierForVendor?.uuidString ?? "ios",
            "timestamp": Int64(Date().timeIntervalSince1970 * 1000),
            "bookmarks": ["folders": folderPayload, "items": bookmarkItems],
            "history": historyItems
        ]
        let plain = try JSONSerialization.data(withJSONObject: payloadObj, options: [.sortedKeys])
        let hash = OmyaruSyncCrypto.sha256Hex(plain)
        let lastHash = UserDefaults.standard.string(forKey: "omyaru.ios.drive.lastUserDataHash") ?? ""
        if hash != lastHash {
            let encrypted = try keys.encrypt(plain)
            let uploadedID = try await uploadDriveFile(name: "omyaru-userdata-v1-\(opID).json", payload: encrypted)
            seen.insert(uploadedID)
            saveSeenSet(seen, name: "seen-userdata.json")
            UserDefaults.standard.set(hash, forKey: "omyaru.ios.drive.lastUserDataHash")
        }
    }

    private func syncTabsAndResets(browser: BrowserModel, keys: OmyaruDriveKeyRing) async throws {
        // 1. Check remote DriveTabReset records first
        var seenResets = loadSeenSet(name: "seen-tab-resets.json")
        let resetFiles = try await listDriveFiles(prefix: "omyaru-tab-reset-v1-")
        var triggeredReset = false
        for file in resetFiles {
            let resetID = file.name
                .replacingOccurrences(of: "omyaru-tab-reset-v1-", with: "")
                .replacingOccurrences(of: ".json", with: "")
            if !seenResets.contains(resetID) {
                if let encrypted = try? await readDriveFile(file.id),
                   let plain = try? keys.decrypt(encrypted),
                   let doc = try? JSONSerialization.jsonObject(with: plain) as? [String: Any],
                   doc["format"] as? String == "omyaru-tab-reset" {
                    seenResets.insert(resetID)
                    triggeredReset = true
                }
            }
        }
        saveSeenSet(seenResets, name: "seen-tab-resets.json")
        if triggeredReset {
            browser.tabs.removeAll { !$0.isPrivate }
            if browser.tabs.isEmpty { _ = browser.newTab() }
            browser.persist()
        }

        // 2. Pull unseen tab batches
        var seenTabs = loadSeenSet(name: "seen-tabs.json")
        let files = try await listDriveFiles(prefix: "omyaru-tabs-v")
        for file in files where !seenTabs.contains(file.id) {
            if let encrypted = try? await readDriveFile(file.id),
               let plain = try? keys.decrypt(encrypted),
               let batch = try? JSONSerialization.jsonObject(with: plain) as? [String: Any] {
                applyRemoteTabBatch(batch, to: browser)
            }
            seenTabs.insert(file.id)
        }
        saveSeenSet(seenTabs, name: "seen-tabs.json")
        browser.persist()

        // 3. Push local tabs/profiles/groups snapshot if changed
        let batchID = OmyaruSyncCrypto.compactUUID()
        let actorID = (UIDevice.current.identifierForVendor?.uuidString ?? UUID().uuidString)
            .replacingOccurrences(of: "-", with: "").lowercased().padding(toLength: 32, withPad: "a", startingAt: 0)
        let clock = Int64(Date().timeIntervalSince1970 * 1000)
        var changes: [[String: Any]] = []

        for (idx, profile) in browser.profiles.enumerated() {
            let pid = profile.id == BrowserModel.defaultProfileID ? "default" : OmyaruSyncCrypto.compactUUID(profile.id)
            changes.append(["kind": "profiles", "id": pid, "field": "name", "value": profile.name])
            changes.append(["kind": "profiles", "id": pid, "field": "icon", "value": profile.icon])
            changes.append(["kind": "profiles", "id": pid, "field": "order", "value": idx])
        }
        for (idx, tab) in browser.tabs.filter({ !$0.isPrivate }).prefix(30).enumerated() {
            let tid = OmyaruSyncCrypto.compactUUID(tab.id)
            let pid = tab.profileID == BrowserModel.defaultProfileID ? "default" : OmyaruSyncCrypto.compactUUID(tab.profileID)
            let gid = OmyaruSyncCrypto.compactUUID(tab.groupID)
            changes.append(["kind": "tabs", "id": tid, "field": "title", "value": tab.title])
            changes.append(["kind": "tabs", "id": tid, "field": "url", "value": tab.url])
            changes.append(["kind": "tabs", "id": tid, "field": "profileId", "value": pid])
            changes.append(["kind": "tabs", "id": tid, "field": "groupId", "value": gid])
            changes.append(["kind": "tabs", "id": tid, "field": "order", "value": idx])
        }
        guard !changes.isEmpty else { return }
        let fingerprintData = try JSONSerialization.data(withJSONObject: changes, options: [.sortedKeys])
        let hash = OmyaruSyncCrypto.sha256Hex(fingerprintData)
        let lastHash = UserDefaults.standard.string(forKey: "omyaru.ios.drive.lastTabsHash") ?? ""
        if hash != lastHash {
            let batchObj: [String: Any] = [
                "protocol": 1,
                "id": batchID,
                "actor": String(actorID.prefix(32)),
                "clock": clock,
                "changes": changes
            ]
            let plain = try JSONSerialization.data(withJSONObject: batchObj, options: [.sortedKeys])
            let encrypted = try keys.encrypt(plain)
            let uploadedID = try await uploadDriveFile(name: "omyaru-tabs-v2-\(batchID).json", payload: encrypted)
            seenTabs.insert(uploadedID)
            saveSeenSet(seenTabs, name: "seen-tabs.json")
            UserDefaults.standard.set(hash, forKey: "omyaru.ios.drive.lastTabsHash")
        }
    }

    private func applyRemoteTabBatch(_ batch: [String: Any], to browser: BrowserModel) {
        let rows: [[String: Any]]
        if let direct = batch["changes"] as? [[String: Any]] {
            rows = direct
        } else if let batches = batch["batches"] as? [[String: Any]] {
            rows = batches.flatMap { ($0["changes"] as? [[String: Any]]) ?? [] }
        } else {
            return
        }
        var tabTitles: [String: String] = [:]
        var tabURLs: [String: String] = [:]
        var deletedTabs = Set<String>()
        for row in rows {
            guard let kind = row["kind"] as? String, kind == "tabs",
                  let id = row["id"] as? String,
                  let field = row["field"] as? String else { continue }
            if field == "$deleted" { deletedTabs.insert(id) }
            else if field == "title", let val = row["value"] as? String { tabTitles[id] = val }
            else if field == "url", let val = row["value"] as? String { tabURLs[id] = val }
        }
        for (id, url) in tabURLs where !deletedTabs.contains(id) && url != "about:blank" {
            if !browser.tabs.contains(where: { $0.url == url }) && browser.tabs.count < 30 {
                browser.tabs.append(BrowserTab(
                    title: tabTitles[id] ?? url,
                    url: url,
                    profileID: browser.activeProfileID,
                    groupID: browser.activeGroupID,
                    isPrivate: false
                ))
            }
        }
    }

    // MARK: - Full Bookshelf Sync With 1MB Encrypted Image Chunks (omyaru-shelf-v1- & omyaru-shelf-chunk-v1-)

    private func syncBookshelfWithChunks(shelf: ShelfModel, keys: OmyaruDriveKeyRing) async throws {
        // 1. Upload local images that don't yet have a cloud manifest
        for i in shelf.catalog.items.indices {
            if shelf.catalog.items[i].cloud != nil { continue }
            let item = shelf.catalog.items[i]
            let localURL = shelf.imageURL(for: item)
            guard let imageBytes = try? Data(contentsOf: localURL), !imageBytes.isEmpty else { continue }
            status = "책장 사진 업로드 중 (\(i + 1)/\(shelf.catalog.items.count))..."
            let manifest = try await uploadChunkedImage(data: imageBytes, keys: keys)
            shelf.catalog.items[i].cloud = manifest
        }
        shelf.save()

        // 2. Pull unseen remote shelf metadata batches
        var seenShelf = loadSeenSet(name: "seen-shelf.json")
        let shelfFiles = try await listDriveFiles(prefix: "omyaru-shelf-v1-")
        var folderNames: [String: String] = [:]
        var folderNotes: [String: String] = [:]
        var folderCols: [String: Int] = [:]
        var deletedFolders = Set<String>()
        var remoteImages: [String: [String: Any]] = [:]
        var deletedImages = Set<String>()

        for (idx, file) in shelfFiles.enumerated() where !seenShelf.contains(file.id) {
            progress = 0.55 + 0.20 * Double(idx + 1) / Double(max(1, shelfFiles.count))
            guard let encrypted = try? await readDriveFile(file.id),
                  let plain = try? keys.decrypt(encrypted),
                  let batch = try? JSONSerialization.jsonObject(with: plain) as? [String: Any],
                  let changes = batch["changes"] as? [[String: Any]] else {
                seenShelf.insert(file.id)
                continue
            }
            for row in changes {
                guard let kind = row["kind"] as? String,
                      let entityID = row["id"] as? String,
                      let field = row["field"] as? String else { continue }
                if kind == "folders" {
                    if field == "$deleted" { deletedFolders.insert(entityID) }
                    else if field == "name", let val = row["value"] as? String { folderNames[entityID] = val }
                    else if field == "note", let val = row["value"] as? String { folderNotes[entityID] = val }
                    else if field == "columns", let val = (row["value"] as? NSNumber)?.intValue { folderCols[entityID] = val }
                } else if kind == "images" {
                    if field == "$deleted" { deletedImages.insert(entityID) }
                    else if let val = row["value"] {
                        remoteImages[entityID, default: [:]][field] = val
                    }
                }
            }
            seenShelf.insert(file.id)
        }
        saveSeenSet(seenShelf, name: "seen-shelf.json")

        // Apply remote folders
        var folderIDToPath: [String: String] = [:]
        for (path, fid) in shelf.catalog.folderIds {
            folderIDToPath[fid.lowercased()] = path
        }
        for (fid, rawPath) in folderNames where !deletedFolders.contains(fid) {
            if let norm = BookshelfPath.normalize(rawPath) {
                _ = shelf.addFolder(norm, select: false)
                shelf.catalog.folderIds[norm] = fid.lowercased()
                folderIDToPath[fid.lowercased()] = norm
                if let note = folderNotes[fid] { shelf.catalog.folderNotes[norm] = note }
                if let col = folderCols[fid] { shelf.catalog.folderColumnCounts[norm] = min(6, max(1, col)) }
            }
        }

        // Apply remote images (download chunks now if !onDemandCloudPhotos, or keep cloud manifest for instant on-demand loading!)
        let imageIDs = Array(remoteImages.keys)
        for (idx, imageID) in imageIDs.enumerated() where !deletedImages.contains(imageID) {
            guard let row = remoteImages[imageID],
                  let folderID = (row["folderId"] as? String)?.lowercased(),
                  let folderPath = folderIDToPath[folderID],
                  let manifestObj = row["manifest"] as? [String: Any],
                  let manifestData = try? JSONSerialization.data(withJSONObject: manifestObj),
                  let manifest = try? JSONDecoder().decode(ShelfCloudImage.self, from: manifestData) else { continue }

            let width = (row["width"] as? NSNumber)?.intValue ?? 720
            let height = (row["height"] as? NSNumber)?.intValue ?? 1024
            let sourceUrl = (row["sourceUrl"] as? String) ?? ""
            let created = (row["createdAt"] as? NSNumber)?.int64Value ?? Int64(Date().timeIntervalSince1970 * 1000)

            if let existingIdx = shelf.catalog.items.firstIndex(where: { $0.id.caseInsensitiveCompare(imageID) == .orderedSame }) {
                shelf.catalog.items[existingIdx].folder = folderPath
                shelf.catalog.items[existingIdx].cloud = manifest
            } else {
                let fileName = "image-sync-\(imageID)-\(manifest.sha256.prefix(12)).png"
                let newItem = BookshelfItem(
                    id: imageID,
                    folder: folderPath,
                    fileName: fileName,
                    sourceUrl: sourceUrl,
                    width: max(1, width),
                    height: max(1, height),
                    createdAtEpochMs: created,
                    cloud: manifest
                )
                shelf.catalog.items.append(newItem)
                if !onDemandCloudPhotos {
                    status = "책장 사진 내려받는 중 (\(idx + 1)/\(imageIDs.count))..."
                    let localURL = shelf.imageURL(for: newItem)
                    if !FileManager.default.fileExists(atPath: localURL.path),
                       let bytes = try? await downloadChunkedImage(manifest: manifest, keys: keys) {
                        try? bytes.write(to: localURL, options: .atomic)
                    }
                }
            }
        }
        shelf.save()

        // 3. Push local folders & images metadata batch
        let batchID = OmyaruSyncCrypto.compactUUID()
        let actorID = (UIDevice.current.identifierForVendor?.uuidString ?? UUID().uuidString)
            .replacingOccurrences(of: "-", with: "").lowercased().padding(toLength: 32, withPad: "b", startingAt: 0)
        let clock = Int64(Date().timeIntervalSince1970 * 1000)
        var changes: [[String: Any]] = []

        for (idx, folder) in shelf.allDisplayFolders.enumerated() {
            let fid = (shelf.catalog.folderIds[folder] ?? OmyaruSyncCrypto.compactUUID()).lowercased()
            changes.append(["kind": "folders", "id": fid, "field": "name", "value": folder])
            changes.append(["kind": "folders", "id": fid, "field": "note", "value": shelf.catalog.folderNotes[folder] ?? ""])
            changes.append(["kind": "folders", "id": fid, "field": "columns", "value": shelf.catalog.folderColumnCounts[folder] ?? 2])
            changes.append(["kind": "folders", "id": fid, "field": "order", "value": idx])
        }
        for (idx, item) in shelf.catalog.items.enumerated() {
            guard let manifest = item.cloud,
                  let manifestData = try? JSONEncoder().encode(manifest),
                  let manifestObj = try? JSONSerialization.jsonObject(with: manifestData) else { continue }
            let fid = (shelf.catalog.folderIds[item.folder] ?? OmyaruSyncCrypto.compactUUID()).lowercased()
            changes.append(["kind": "images", "id": item.id.lowercased(), "field": "folderId", "value": fid])
            changes.append(["kind": "images", "id": item.id.lowercased(), "field": "manifest", "value": manifestObj])
            changes.append(["kind": "images", "id": item.id.lowercased(), "field": "sourceUrl", "value": item.sourceUrl])
            changes.append(["kind": "images", "id": item.id.lowercased(), "field": "width", "value": item.width])
            changes.append(["kind": "images", "id": item.id.lowercased(), "field": "height", "value": item.height])
            changes.append(["kind": "images", "id": item.id.lowercased(), "field": "createdAt", "value": item.createdAtEpochMs])
            changes.append(["kind": "images", "id": item.id.lowercased(), "field": "order", "value": idx])
        }
        guard !changes.isEmpty else { return }

        let fingerprintData = try JSONSerialization.data(withJSONObject: changes, options: [.sortedKeys])
        let hash = OmyaruSyncCrypto.sha256Hex(fingerprintData)
        let lastHash = UserDefaults.standard.string(forKey: "omyaru.ios.drive.lastShelfHash") ?? ""
        if hash != lastHash {
            // Split into batches of 128 changes to stay well under 4MB limit
            for offset in stride(from: 0, to: changes.count, by: 128) {
                let slice = Array(changes[offset..<min(changes.count, offset + 128)])
                let subBatchID = OmyaruSyncCrypto.compactUUID()
                let batchObj: [String: Any] = [
                    "protocol": 1,
                    "id": subBatchID,
                    "actor": String(actorID.prefix(32)),
                    "clock": clock + Int64(offset),
                    "changes": slice
                ]
                let plain = try JSONSerialization.data(withJSONObject: batchObj, options: [.sortedKeys])
                let encrypted = try keys.encrypt(plain)
                let uploadedID = try await uploadDriveFile(name: "omyaru-shelf-v1-\(subBatchID).json", payload: encrypted)
                seenShelf.insert(uploadedID)
            }
            saveSeenSet(seenShelf, name: "seen-shelf.json")
            UserDefaults.standard.set(hash, forKey: "omyaru.ios.drive.lastShelfHash")
        }
    }

    private func uploadChunkedImage(data: Data, keys: OmyaruDriveKeyRing) async throws -> ShelfCloudImage {
        var hashes: [String] = []
        var refs: [ShelfCloudImage.Reference] = []
        let totalLength = data.count
        var offset = 0
        var chunkIndex = 0
        while offset < totalLength {
            let end = min(totalLength, offset + Self.chunkSize)
            let chunk = data.subdata(in: offset..<end)
            let chunkHash = OmyaruSyncCrypto.sha256Hex(chunk)
            let row: [String: Any] = [
                "version": 1,
                "index": chunkIndex,
                "sha256": chunkHash,
                "data": chunk.base64EncodedString()
            ]
            let plain = try JSONSerialization.data(withJSONObject: row, options: [.sortedKeys])
            let encrypted = try keys.encrypt(plain)
            let opID = OmyaruSyncCrypto.compactUUID()
            let fileName = "omyaru-shelf-chunk-v1-\(opID).json"
            let cloudID = try await uploadDriveFile(name: fileName, payload: encrypted)
            hashes.append(chunkHash)
            refs.append(ShelfCloudImage.Reference(id: cloudID, name: fileName))
            offset = end
            chunkIndex += 1
        }
        return ShelfCloudImage(
            version: 1,
            length: totalLength,
            sha256: OmyaruSyncCrypto.sha256Hex(data),
            chunks: hashes,
            files: refs
        )
    }

    private func downloadChunkedImage(manifest: ShelfCloudImage, keys: OmyaruDriveKeyRing) async throws -> Data {
        var output = Data(capacity: manifest.length)
        for (idx, ref) in manifest.files.enumerated() {
            let encrypted = try await readDriveFile(ref.id)
            let plain = try keys.decrypt(encrypted)
            guard let row = try JSONSerialization.jsonObject(with: plain) as? [String: Any],
                  let b64 = row["data"] as? String,
                  let chunk = Data(base64Encoded: b64) else {
                throw NSError(domain: "OmyaruDrive", code: -30, userInfo: [NSLocalizedDescriptionKey: "이미지 조각 \(idx) 복호화 실패"])
            }
            if idx < manifest.chunks.count && OmyaruSyncCrypto.sha256Hex(chunk) != manifest.chunks[idx] {
                throw NSError(domain: "OmyaruDrive", code: -31, userInfo: [NSLocalizedDescriptionKey: "이미지 조각 무결성 검증 실패"])
            }
            output.append(chunk)
        }
        guard OmyaruSyncCrypto.sha256Hex(output) == manifest.sha256 else {
            throw NSError(domain: "OmyaruDrive", code: -32, userInfo: [NSLocalizedDescriptionKey: "전체 이미지 SHA-256 무결성 검증 실패"])
        }
        return output
    }

    private func loadSeenSet(name: String) -> Set<String> {
        let url = syncStateDirectory.appendingPathComponent(name)
        guard let data = try? Data(contentsOf: url),
              let arr = try? JSONDecoder().decode([String].self, from: data) else { return [] }
        return Set(arr)
    }

    private func saveSeenSet(_ set: Set<String>, name: String) {
        let url = syncStateDirectory.appendingPathComponent(name)
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

        try FileManager.default.createDirectory(at: shelf.root, withIntermediateDirectories: true)
        for (fileName, b64) in bundle.bookshelfImagesBase64 {
            if let imgData = Data(base64Encoded: b64) {
                let target = shelf.root.appendingPathComponent(fileName)
                if !FileManager.default.fileExists(atPath: target.path) {
                    try? imgData.write(to: target, options: .atomic)
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

// MARK: - Drive Sync Sheet UI (Includes Tab Reset Confirmation & Cloud Photo Mode)

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

    @State private var exportingBundle = false
    @State private var importingBundle = false
    @State private var exportDoc = EncryptedSyncDocument(data: Data())
    @State private var showTabResetPrompt = false
    @State private var tabResetInput = ""
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
                                Text("Android · PC · iOS 종단간 암호화 동기화 활성")
                                    .font(.caption2)
                                    .foregroundStyle(.secondary)
                            }
                        }
                        .padding(.vertical, 4)

                        Button {
                            Task { await sync.syncWithGoogleDrive(browser: browser, shelf: shelf) }
                        } label: {
                            HStack {
                                Label("지금 동기화", systemImage: "arrow.triangle.2.circlepath.icloud.fill")
                                    .font(.subheadline.weight(.semibold))
                                Spacer()
                                if sync.busy { ProgressView().controlSize(.small) }
                            }
                        }
                        .disabled(sync.busy)

                        Toggle("앱 활성화 시 자동 동기화", isOn: $sync.autoSyncEnabled)

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
                                        await sync.syncWithGoogleDrive(browser: browser, shelf: shelf)
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
                                    await sync.syncWithGoogleDrive(browser: browser, shelf: shelf)
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
