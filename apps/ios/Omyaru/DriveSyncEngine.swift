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

// MARK: - Unified Portable Sync Snapshot (Tabs, Profiles, Groups, Bookmarks, History, Bookshelf)

struct OmyaruFullSyncBundle: Codable {
    var version: Int = 1
    var exportedAtEpochMs: Int64 = Int64(Date().timeIntervalSince1970 * 1000)
    var profiles: [BrowserProfile]
    var groups: [BrowserGroup]
    var tabs: [BrowserTab]
    var bookmarks: [BrowserBookmark]
    var history: [HistoryEntry]
    var bookshelf: BookshelfCatalogData
    var bookshelfImagesBase64: [String: String] = [:]
}

// MARK: - Google Drive AppData & Encrypted File Sync Coordinator

@MainActor
final class DriveSyncManager: NSObject, ObservableObject, ASWebAuthenticationPresentationContextProviding {
    static let shared = DriveSyncManager()

    @Published var clientID: String {
        didSet { UserDefaults.standard.set(clientID, forKey: "omyaru.ios.drive.clientId") }
    }
    @Published var accessToken: String {
        didSet { UserDefaults.standard.set(accessToken, forKey: "omyaru.ios.drive.accessToken") }
    }
    @Published var refreshToken: String {
        didSet { UserDefaults.standard.set(refreshToken, forKey: "omyaru.ios.drive.refreshToken") }
    }
    @Published var syncPassphrase: String {
        didSet { UserDefaults.standard.set(syncPassphrase, forKey: "omyaru.ios.drive.passphrase") }
    }
    @Published var autoSyncEnabled: Bool {
        didSet { UserDefaults.standard.set(autoSyncEnabled, forKey: "omyaru.ios.drive.autoSync") }
    }
    @Published var onDemandCloudPhotos: Bool {
        didSet { UserDefaults.standard.set(onDemandCloudPhotos, forKey: "omyaru.ios.drive.onDemand") }
    }
    @Published var busy: Bool = false
    @Published var status: String = "로컬 암호화 대기 중"
    @Published var progress: Double? = nil
    @Published var lastSyncedAt: Date? = nil

    override init() {
        let d = UserDefaults.standard
        let plistClient = (Bundle.main.object(forInfoDictionaryKey: "GIDClientID") as? String) ?? ""
        clientID = d.string(forKey: "omyaru.ios.drive.clientId") ?? plistClient
        accessToken = d.string(forKey: "omyaru.ios.drive.accessToken") ?? ""
        refreshToken = d.string(forKey: "omyaru.ios.drive.refreshToken") ?? ""
        syncPassphrase = d.string(forKey: "omyaru.ios.drive.passphrase") ?? ""
        autoSyncEnabled = d.bool(forKey: "omyaru.ios.drive.autoSync")
        onDemandCloudPhotos = d.bool(forKey: "omyaru.ios.drive.onDemand")
        super.init()
    }

    var isConnected: Bool { !accessToken.isEmpty }

    func presentationAnchor(for session: ASWebAuthenticationSession) -> ASPresentationAnchor {
        UIApplication.shared.connectedScenes
            .compactMap { $0 as? UIWindowScene }
            .flatMap(\.windows)
            .first(where: \.isKeyWindow) ?? ASPresentationAnchor()
    }

    // MARK: - Google OAuth 2.0 PKCE Sign-In via ASWebAuthenticationSession

    func signInWithGoogle() async {
        let trimmedClient = clientID.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmedClient.hasSuffix(".apps.googleusercontent.com") else {
            status = "Google iOS OAuth 클라이언트 ID(.apps.googleusercontent.com)를 입력하거나 암호화 동기화 파일을 사용해 주세요."
            return
        }
        let prefix = String(trimmedClient.dropLast(".apps.googleusercontent.com".count))
        let callbackScheme = "com.googleusercontent.apps.\(prefix)"
        let redirectURI = "\(callbackScheme):/oauthredirect"
        let verifier = generateCodeVerifier()
        let challenge = codeChallenge(for: verifier)

        var comps = URLComponents(string: "https://accounts.google.com/o/oauth2/v2/auth")!
        comps.queryItems = [
            URLQueryItem(name: "client_id", value: trimmedClient),
            URLQueryItem(name: "redirect_uri", value: redirectURI),
            URLQueryItem(name: "response_type", value: "code"),
            URLQueryItem(name: "scope", value: "https://www.googleapis.com/auth/drive.appdata"),
            URLQueryItem(name: "code_challenge", value: challenge),
            URLQueryItem(name: "code_challenge_method", value: "S256")
        ]
        guard let authURL = comps.url else { return }

        busy = true
        status = "Google 계정 인증 진행 중..."
        defer { busy = false }

        do {
            let callbackURL: URL = try await withCheckedThrowingContinuation { continuation in
                let session = ASWebAuthenticationSession(url: authURL, callbackURLScheme: callbackScheme) { url, error in
                    if let error {
                        continuation.resume(throwing: error)
                    } else if let url {
                        continuation.resume(returning: url)
                    } else {
                        continuation.resume(throwing: NSError(domain: "OmyaruOAuth", code: -1, userInfo: [NSLocalizedDescriptionKey: "인증이 취소되었습니다."]))
                    }
                }
                session.presentationContextProvider = self
                session.prefersEphemeralWebBrowserSession = false
                session.start()
            }

            guard let code = URLComponents(url: callbackURL, resolvingAgainstBaseURL: false)?
                .queryItems?.first(where: { $0.name == "code" })?.value else {
                status = "인증 코드를 받지 못했습니다."
                return
            }

            var tokenReq = URLRequest(url: URL(string: "https://oauth2.googleapis.com/token")!)
            tokenReq.httpMethod = "POST"
            tokenReq.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
            let bodyItems = [
                "client_id=\(trimmedClient.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? trimmedClient)",
                "code=\(code.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? code)",
                "code_verifier=\(verifier)",
                "grant_type=authorization_code",
                "redirect_uri=\(redirectURI.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? redirectURI)"
            ]
            tokenReq.httpBody = bodyItems.joined(separator: "&").data(using: .utf8)

            let (data, response) = try await URLSession.shared.data(for: tokenReq)
            guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode),
                  let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let token = json["access_token"] as? String else {
                status = "토큰 교환 실패"
                return
            }
            accessToken = token
            if let refresh = json["refresh_token"] as? String {
                refreshToken = refresh
            }
            status = "Google Drive 연결 완료"
        } catch {
            status = "Google 로그인 취소 또는 실패: \(error.localizedDescription)"
        }
    }

    func disconnectGoogle() {
        accessToken = ""
        refreshToken = ""
        autoSyncEnabled = false
        status = "Google Drive 연결 해제됨"
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

    // MARK: - Google Drive AppData Sync (Wire-Compatible with Android & Windows)

    func syncWithGoogleDrive(browser: BrowserModel?, shelf: ShelfModel) async {
        guard !busy else { return }
        guard !accessToken.isEmpty else {
            status = "먼저 Google 계정을 연결하거나 액세스 토큰을 입력해 주세요."
            return
        }
        busy = true
        progress = 0.05
        status = "Google Drive appDataFolder 확인 중..."
        defer {
            busy = false
            progress = nil
        }

        do {
            // 1. Load or create account key (omyaru-account-key-v1-<id>.json)
            let (accountKeyID, secret) = try await ensureAccountKey()
            progress = 0.20

            // 2. Sync UserData (Bookmarks & History: omyaru-userdata-v1-*.json)
            if let browser {
                status = "북마크·방문 기록 동기화 중..."
                try await syncUserData(browser: browser, keyID: accountKeyID, secret: secret)
            }
            progress = 0.45

            // 3. Sync Tabs/Profiles/Groups (omyaru-tabs-v2-*.json)
            if let browser {
                status = "프로필·탭 그룹·열린 탭 동기화 중..."
                try await syncTabsAndProfiles(browser: browser, keyID: accountKeyID, secret: secret)
            }
            progress = 0.70

            // 4. Sync Bookshelf Catalog & Images (omyaru-shelf-v1-*.json / omyaru-shelf-chunk-v1-*.json)
            status = "책장 폴더 및 이미지 동기화 중..."
            try await syncBookshelf(shelf: shelf, keyID: accountKeyID, secret: secret)

            progress = 1.0
            lastSyncedAt = Date()
            status = "Google Drive 동기화 완료 (탭·북마크·책장)"
        } catch {
            status = "동기화 실패: \(error.localizedDescription)"
        }
    }

    private struct DriveFileEntry: Decodable {
        let id: String
        let name: String
    }

    private func listDriveFiles(prefix: String) async throws -> [DriveFileEntry] {
        struct FileListResponse: Decodable {
            let files: [DriveFileEntry]
        }
        var comps = URLComponents(string: "https://www.googleapis.com/drive/v3/files")!
        comps.queryItems = [
            URLQueryItem(name: "spaces", value: "appDataFolder"),
            URLQueryItem(name: "pageSize", value: "500"),
            URLQueryItem(name: "fields", value: "files(id,name)"),
            URLQueryItem(name: "q", value: "trashed = false and name contains '\(prefix)'")
        ]
        var req = URLRequest(url: comps.url!, timeoutInterval: 25)
        req.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        let (data, resp) = try await URLSession.shared.data(for: req)
        guard let http = resp as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
            throw NSError(domain: "OmyaruDrive", code: code, userInfo: [NSLocalizedDescriptionKey: "Google Drive 목록 요청 실패 (HTTP \(code))"])
        }
        return try JSONDecoder().decode(FileListResponse.self, from: data).files
    }

    private func readDriveFile(_ fileID: String) async throws -> Data {
        var comps = URLComponents(string: "https://www.googleapis.com/drive/v3/files/\(fileID)")!
        comps.queryItems = [URLQueryItem(name: "alt", value: "media")]
        var req = URLRequest(url: comps.url!, timeoutInterval: 30)
        req.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        let (data, resp) = try await URLSession.shared.data(for: req)
        guard let http = resp as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
            throw NSError(domain: "OmyaruDrive", code: code, userInfo: [NSLocalizedDescriptionKey: "Google Drive 파일 읽기 실패 (HTTP \(code))"])
        }
        return data
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
        req.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        req.setValue("multipart/related; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        req.httpBody = body

        let (data, resp) = try await URLSession.shared.data(for: req)
        guard let http = resp as? HTTPURLResponse, (200..<300).contains(http.statusCode),
              let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let id = json["id"] as? String else {
            let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
            throw NSError(domain: "OmyaruDrive", code: code, userInfo: [NSLocalizedDescriptionKey: "Google Drive 업로드 실패 (HTTP \(code))"])
        }
        return id
    }

    private func ensureAccountKey() async throws -> (id: String, secret: String) {
        let keyFiles = try await listDriveFiles(prefix: "omyaru-account-key-v1-")
        for file in keyFiles.sorted(by: { $0.name < $1.name }) {
            if let data = try? await readDriveFile(file.id),
               let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
               json["format"] as? String == "omyaru-account-key",
               let id = json["id"] as? String,
               let secret = json["secret"] as? String {
                return (id, secret)
            }
        }
        let newID = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        var randomBytes = [UInt8](repeating: 0, count: 32)
        _ = SecRandomCopyBytes(kSecRandomDefault, 32, &randomBytes)
        let secret = Data(randomBytes).base64EncodedString()
        let record: [String: Any] = [
            "format": "omyaru-account-key",
            "version": 1,
            "id": newID,
            "secret": secret,
            "legacyEnvelope": NSNull()
        ]
        let payload = try JSONSerialization.data(withJSONObject: record, options: [.sortedKeys])
        _ = try await uploadDriveFile(name: "omyaru-account-key-v1-\(newID).json", payload: payload)
        return (newID, secret)
    }

    private func syncUserData(browser: BrowserModel, keyID: String, secret: String) async throws {
        let files = try await listDriveFiles(prefix: "omyaru-userdata-v1-")
        for file in files {
            guard let encrypted = try? await readDriveFile(file.id),
                  let plain = try? OmyaruSyncCrypto.decrypt(encrypted, password: secret),
                  let doc = try? JSONSerialization.jsonObject(with: plain) as? [String: Any] else { continue }
            if let bObj = doc["bookmarks"] as? [String: Any],
               let items = bObj["items"] as? [[String: Any]] {
                for row in items {
                    guard let title = row["title"] as? String,
                          let url = row["url"] as? String else { continue }
                    let folder = (row["folder"] as? String) ?? ""
                    if !browser.bookmarks.contains(where: { $0.url == url }) {
                        browser.bookmarks.append(BrowserBookmark(title: title, url: url, folder: folder))
                    }
                }
            }
            if let hArr = doc["history"] as? [[String: Any]] {
                for row in hArr.prefix(300) {
                    guard let title = row["title"] as? String,
                          let url = row["url"] as? String else { continue }
                    let visited = (row["visitedAtEpochMs"] as? Int64) ?? Int64(Date().timeIntervalSince1970 * 1000)
                    if !browser.history.contains(where: { $0.url == url }) {
                        browser.history.append(HistoryEntry(title: title, url: url, visitedAtEpochMs: visited))
                    }
                }
            }
        }
        browser.persist()

        let opID = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        let bookmarkItems = browser.bookmarks.map { b -> [String: Any] in
            ["id": b.id.uuidString.replacingOccurrences(of: "-", with: "").lowercased(),
             "title": b.title,
             "url": b.url,
             "folder": b.folder]
        }
        let historyItems = browser.history.prefix(300).map { h -> [String: Any] in
            ["title": h.title, "url": h.url, "visitedAtEpochMs": h.visitedAtEpochMs]
        }
        let payloadObj: [String: Any] = [
            "id": opID,
            "deviceId": UIDevice.current.identifierForVendor?.uuidString ?? "ios",
            "timestamp": Int64(Date().timeIntervalSince1970 * 1000),
            "bookmarks": ["folders": browser.bookmarkFolders, "items": bookmarkItems],
            "history": historyItems
        ]
        let plain = try JSONSerialization.data(withJSONObject: payloadObj)
        let encrypted = try OmyaruSyncCrypto.encrypt(plain, password: secret, accountKeyID: keyID)
        _ = try await uploadDriveFile(name: "omyaru-userdata-v1-\(opID).json", payload: encrypted)
    }

    private func syncTabsAndProfiles(browser: BrowserModel, keyID: String, secret: String) async throws {
        let files = try await listDriveFiles(prefix: "omyaru-tabs-v")
        for file in files {
            guard let encrypted = try? await readDriveFile(file.id),
                  let plain = try? OmyaruSyncCrypto.decrypt(encrypted, password: secret),
                  let batch = try? JSONSerialization.jsonObject(with: plain) as? [String: Any] else { continue }
            applyTabChanges(from: batch, to: browser)
        }
        browser.persist()

        // Push local tabs/groups/profiles batch
        let batchID = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        let actorID = (UIDevice.current.identifierForVendor?.uuidString ?? UUID().uuidString)
            .replacingOccurrences(of: "-", with: "").lowercased().padding(toLength: 32, withPad: "a", startingAt: 0)
        let clock = Int64(Date().timeIntervalSince1970 * 1000)
        var changes: [[String: Any]] = []

        for (idx, tab) in browser.tabs.filter({ !$0.isPrivate }).enumerated() {
            let tid = tab.id.uuidString.replacingOccurrences(of: "-", with: "").lowercased()
            let pid = tab.profileID.uuidString.replacingOccurrences(of: "-", with: "").lowercased()
            let gid = tab.groupID.uuidString.replacingOccurrences(of: "-", with: "").lowercased()
            changes.append(["kind": "tabs", "id": tid, "field": "title", "value": tab.title])
            changes.append(["kind": "tabs", "id": tid, "field": "url", "value": tab.url])
            changes.append(["kind": "tabs", "id": tid, "field": "profileId", "value": pid])
            changes.append(["kind": "tabs", "id": tid, "field": "groupId", "value": gid])
            changes.append(["kind": "tabs", "id": tid, "field": "order", "value": idx])
        }
        guard !changes.isEmpty else { return }
        let batchObj: [String: Any] = [
            "protocol": 1,
            "id": batchID,
            "actor": String(actorID.prefix(32)),
            "clock": clock,
            "changes": changes
        ]
        let plain = try JSONSerialization.data(withJSONObject: batchObj)
        let encrypted = try OmyaruSyncCrypto.encrypt(plain, password: secret, accountKeyID: keyID)
        _ = try await uploadDriveFile(name: "omyaru-tabs-v2-\(batchID).json", payload: encrypted)
    }

    private func applyTabChanges(from batch: [String: Any], to browser: BrowserModel) {
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
                let title = tabTitles[id] ?? url
                browser.tabs.append(BrowserTab(
                    title: title,
                    url: url,
                    profileID: browser.activeProfileID,
                    groupID: browser.activeGroupID,
                    isPrivate: false
                ))
            }
        }
    }

    private func syncBookshelf(shelf: ShelfModel, keyID: String, secret: String) async throws {
        let shelfFiles = try await listDriveFiles(prefix: "omyaru-shelf-v1-")
        var folderNames: [String: String] = [:]
        var folderNotes: [String: String] = [:]
        var folderCols: [String: Int] = [:]
        for file in shelfFiles {
            guard let encrypted = try? await readDriveFile(file.id),
                  let plain = try? OmyaruSyncCrypto.decrypt(encrypted, password: secret),
                  let batch = try? JSONSerialization.jsonObject(with: plain) as? [String: Any],
                  let changes = batch["changes"] as? [[String: Any]] else { continue }
            for row in changes {
                guard let kind = row["kind"] as? String, kind == "folders",
                      let fid = row["id"] as? String,
                      let field = row["field"] as? String else { continue }
                if field == "name", let val = row["value"] as? String { folderNames[fid] = val }
                else if field == "note", let val = row["value"] as? String { folderNotes[fid] = val }
                else if field == "columns", let val = row["value"] as? Int { folderCols[fid] = val }
            }
        }
        for (fid, path) in folderNames {
            if let norm = BookshelfPath.normalize(path) {
                _ = shelf.addFolder(norm, select: false)
                if let note = folderNotes[fid], !note.isEmpty {
                    shelf.catalog.folderNotes[norm] = note
                }
                if let col = folderCols[fid] {
                    shelf.catalog.folderColumnCounts[norm] = min(6, max(1, col))
                }
            }
        }
        shelf.save()

        // Upload local shelf folders batch
        let batchID = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        let actorID = (UIDevice.current.identifierForVendor?.uuidString ?? UUID().uuidString)
            .replacingOccurrences(of: "-", with: "").lowercased().padding(toLength: 32, withPad: "b", startingAt: 0)
        let clock = Int64(Date().timeIntervalSince1970 * 1000)
        var changes: [[String: Any]] = []
        for (idx, folder) in shelf.allDisplayFolders.enumerated() {
            let fid = (shelf.catalog.folderIds[folder] ?? UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased())
                .lowercased().padding(toLength: 32, withPad: "0", startingAt: 0)
            let cleanID = String(fid.prefix(32))
            changes.append(["kind": "folders", "id": cleanID, "field": "name", "value": folder])
            changes.append(["kind": "folders", "id": cleanID, "field": "note", "value": shelf.catalog.folderNotes[folder] ?? ""])
            changes.append(["kind": "folders", "id": cleanID, "field": "columns", "value": shelf.catalog.folderColumnCounts[folder] ?? 2])
            changes.append(["kind": "folders", "id": cleanID, "field": "order", "value": idx])
        }
        guard !changes.isEmpty else { return }
        let batchObj: [String: Any] = [
            "protocol": 1,
            "id": batchID,
            "actor": String(actorID.prefix(32)),
            "clock": clock,
            "changes": changes
        ]
        let plain = try JSONSerialization.data(withJSONObject: batchObj)
        let encrypted = try OmyaruSyncCrypto.encrypt(plain, password: secret, accountKeyID: keyID)
        _ = try await uploadDriveFile(name: "omyaru-shelf-v1-\(batchID).json", payload: encrypted)
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

// MARK: - Drive Sync Sheet UI

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
    @State private var alertMessage: String? = nil

    var body: some View {
        NavigationStack {
            Form {
                Section("Google Drive 클라우드 동기화 (Android · Windows · iOS 호환)") {
                    TextField("Google iOS OAuth Client ID (.apps.googleusercontent.com)", text: $sync.clientID)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .font(.caption)

                    SecureField("Google OAuth 액세스 토큰 (선택 · 직접 입력 시)", text: $sync.accessToken)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .font(.caption)

                    HStack(spacing: 10) {
                        Button {
                            Task { await sync.signInWithGoogle() }
                        } label: {
                            Label(sync.isConnected ? "Google 계정 재연결" : "Google 계정 로그인", systemImage: "person.badge.key.fill")
                        }
                        .buttonStyle(.bordered)
                        .disabled(sync.busy)

                        Button {
                            Task { await sync.syncWithGoogleDrive(browser: browser, shelf: shelf) }
                        } label: {
                            Label("지금 동기화", systemImage: "arrow.triangle.2.circlepath.icloud.fill")
                        }
                        .buttonStyle(.borderedProminent)
                        .disabled(sync.busy || !sync.isConnected)
                    }

                    if sync.isConnected {
                        Toggle("앱 활성화 시 자동 동기화", isOn: $sync.autoSyncEnabled)
                        Picker("책장 사진 저장 방식", selection: $sync.onDemandCloudPhotos) {
                            Text("기기에 원본 모두 저장").tag(false)
                            Text("필요할 때 클라우드에서 로드").tag(true)
                        }
                        Button("Google Drive 연결 해제", role: .destructive) {
                            sync.disconnectGoogle()
                        }
                    }

                    if let prog = sync.progress {
                        ProgressView(value: prog)
                    }
                    Text(sync.status)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    if let last = sync.lastSyncedAt {
                        LabeledContent("마지막 동기화") {
                            Text(last, format: .dateTime.month().day().hour().minute())
                        }
                    }
                }

                Section {
                    SecureField("동기화 암호 (PBKDF2-SHA256 + AES-256-GCM)", text: $sync.syncPassphrase)
                    Button {
                        do {
                            let bytes = try sync.exportEncryptedBundle(browser: browser, shelf: shelf, password: sync.syncPassphrase)
                            exportDoc = EncryptedSyncDocument(data: bytes)
                            exportingBundle = true
                        } catch {
                            alertMessage = error.localizedDescription
                        }
                    } label: {
                        Label("암호화 동기화 파일(.omyarusync) 내보내기", systemImage: "square.and.arrow.up")
                    }
                    Button {
                        importingBundle = true
                    } label: {
                        Label("암호화 동기화 파일(.omyarusync) 가져오기", systemImage: "square.and.arrow.down")
                    }
                } header: {
                    Text("파일 / AirDrop / iCloud Drive 종단간 암호화 동기화")
                } footer: {
                    Text("OAuth 설정 없이도 프로필·탭·북마크·방문 기록·책장 폴더와 사진을 AES-256-GCM으로 암호화해 기기 간 즉시 동기화할 수 있습니다.")
                }
            }
            .navigationTitle("동기화")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("완료") { dismiss() }
                }
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
