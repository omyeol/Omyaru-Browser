import SwiftUI
import LocalAuthentication
import CryptoKit
import CommonCrypto
import UniformTypeIdentifiers
import UIKit

// MARK: - App Lock Manager (PIN 4~6 digits OR Password + Face ID / Touch ID + App Switcher Black Shield)

enum AppLockKind: String, CaseIterable, Codable, Identifiable {
    case pin = "PIN"
    case password = "PASSWORD"
    var id: String { rawValue }
    var title: String { self == .pin ? "숫자 PIN (4~6자리)" : "문자 비밀번호" }
}

@MainActor
final class AppLockManager: ObservableObject {
    static let shared = AppLockManager()

    @Published var isEnabled: Bool {
        didSet { UserDefaults.standard.set(isEnabled, forKey: "omyaru.ios.lock.enabled") }
    }
    @Published var lockKind: AppLockKind {
        didSet { UserDefaults.standard.set(lockKind.rawValue, forKey: "omyaru.ios.lock.kind") }
    }
    @Published var pinLength: Int {
        didSet { UserDefaults.standard.set(pinLength, forKey: "omyaru.ios.lock.pinLength") }
    }
    @Published var useBiometrics: Bool {
        didSet { UserDefaults.standard.set(useBiometrics, forKey: "omyaru.ios.lock.biometrics") }
    }
    @Published var lockOnBackground: Bool {
        didSet { UserDefaults.standard.set(lockOnBackground, forKey: "omyaru.ios.lock.onBackground") }
    }
    @Published var appSwitchBlackShield: Bool {
        didSet { UserDefaults.standard.set(appSwitchBlackShield, forKey: "omyaru.ios.lock.blackShield") }
    }
    @Published var isLocked: Bool = false
    @Published var errorMessage: String = ""

    private let hashKey = "omyaru.ios.lock.pinHash"
    private let saltKey = "omyaru.ios.lock.pinSalt"

    init() {
        let d = UserDefaults.standard
        let enabled = d.bool(forKey: "omyaru.ios.lock.enabled")
        isEnabled = enabled
        lockKind = AppLockKind(rawValue: d.string(forKey: "omyaru.ios.lock.kind") ?? "PIN") ?? .pin
        pinLength = min(6, max(4, d.object(forKey: "omyaru.ios.lock.pinLength") as? Int ?? 4))
        useBiometrics = d.object(forKey: "omyaru.ios.lock.biometrics") as? Bool ?? true
        lockOnBackground = d.object(forKey: "omyaru.ios.lock.onBackground") as? Bool ?? true
        appSwitchBlackShield = d.bool(forKey: "omyaru.ios.lock.blackShield")
        isLocked = enabled
    }

    var hasSecret: Bool {
        !(UserDefaults.standard.string(forKey: hashKey) ?? "").isEmpty
    }

    func setSecret(_ secret: String, kind: AppLockKind) -> Bool {
        let trimmed = secret.trimmingCharacters(in: .whitespacesAndNewlines)
        if kind == .pin {
            guard (4...6).contains(trimmed.count), trimmed.allSatisfy(\.isNumber) else {
                errorMessage = "PIN은 4~6자리 숫자여야 합니다."
                return false
            }
            pinLength = trimmed.count
        } else {
            guard trimmed.count >= 4, trimmed.count <= 64 else {
                errorMessage = "비밀번호는 4~64자여야 합니다."
                return false
            }
        }
        var saltBytes = [UInt8](repeating: 0, count: 16)
        _ = SecRandomCopyBytes(kSecRandomDefault, 16, &saltBytes)
        let salt = Data(saltBytes)
        let hash = Self.hashSecret(trimmed, salt: salt)
        UserDefaults.standard.set(salt.base64EncodedString(), forKey: saltKey)
        UserDefaults.standard.set(hash.base64EncodedString(), forKey: hashKey)
        lockKind = kind
        isEnabled = true
        isLocked = false
        errorMessage = ""
        return true
    }

    func verifySecret(_ input: String) -> Bool {
        guard let saltB64 = UserDefaults.standard.string(forKey: saltKey),
              let salt = Data(base64Encoded: saltB64),
              let expectedB64 = UserDefaults.standard.string(forKey: hashKey),
              let expected = Data(base64Encoded: expectedB64) else {
            return false
        }
        let actual = Self.hashSecret(input.trimmingCharacters(in: .whitespacesAndNewlines), salt: salt)
        if actual == expected {
            isLocked = false
            errorMessage = ""
            return true
        } else {
            errorMessage = lockKind == .pin ? "PIN이 일치하지 않습니다." : "비밀번호가 일치하지 않습니다."
            return false
        }
    }

    func disableLock() {
        isEnabled = false
        isLocked = false
        UserDefaults.standard.removeObject(forKey: hashKey)
        UserDefaults.standard.removeObject(forKey: saltKey)
    }

    func lockIfNeeded() {
        if isEnabled && lockOnBackground {
            isLocked = true
        }
    }

    func unlockWithBiometrics() async {
        guard isEnabled, useBiometrics else { return }
        let context = LAContext()
        var error: NSError?
        guard context.canEvaluatePolicy(.deviceOwnerAuthenticationWithBiometrics, error: &error) else {
            return
        }
        do {
            let success = try await context.evaluatePolicy(
                .deviceOwnerAuthenticationWithBiometrics,
                localizedReason: "Omyaru Browser 잠금 해제"
            )
            if success {
                isLocked = false
                errorMessage = ""
            }
        } catch {
            errorMessage = "생체 인증을 완료하지 못했습니다. 암호를 입력해 주세요."
        }
    }

    private static func hashSecret(_ secret: String, salt: Data) -> Data {
        var derived = [UInt8](repeating: 0, count: 32)
        _ = salt.withUnsafeBytes { saltBuf in
            secret.withCString { ptr in
                CCKeyDerivationPBKDF(
                    CCPBKDFAlgorithm(kCCPBKDF2),
                    ptr,
                    secret.utf8.count,
                    saltBuf.bindMemory(to: UInt8.self).baseAddress,
                    salt.count,
                    CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256),
                    120_000,
                    &derived,
                    32
                )
            }
        }
        return Data(derived)
    }
}

struct AppLockOverlayView: View {
    @ObservedObject var lock: AppLockManager = .shared
    @State private var secretInput: String = ""

    var body: some View {
        ZStack {
            Color(red: 0.05, green: 0.05, blue: 0.06).ignoresSafeArea()
            VStack(spacing: 20) {
                Image(systemName: "lock.shield.fill")
                    .font(.system(size: 56))
                    .foregroundStyle(.blue)
                Text("Omyaru 잠금")
                    .font(.title2.weight(.bold))
                Text("브라우저와 책장을 보호하고 있습니다.")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)

                SecureField(lock.lockKind == .pin ? "\(lock.pinLength)자리 PIN 입력" : "비밀번호 입력", text: $secretInput)
                    .keyboardType(lock.lockKind == .pin ? .numberPad : .default)
                    .multilineTextAlignment(.center)
                    .font(.title3.monospacedDigit())
                    .padding(12)
                    .frame(width: 240)
                    .background(Color(red: 0.14, green: 0.14, blue: 0.16), in: RoundedRectangle(cornerRadius: 12))
                    .onSubmit {
                        if lock.verifySecret(secretInput) { secretInput = "" }
                    }

                if !lock.errorMessage.isEmpty {
                    Text(lock.errorMessage)
                        .font(.caption)
                        .foregroundStyle(.red)
                }

                HStack(spacing: 12) {
                    Button {
                        if lock.verifySecret(secretInput) { secretInput = "" }
                    } label: {
                        Label("잠금 해제", systemImage: "key.fill")
                            .padding(.horizontal, 16)
                            .padding(.vertical, 10)
                    }
                    .buttonStyle(.borderedProminent)

                    if lock.useBiometrics {
                        Button {
                            Task { await lock.unlockWithBiometrics() }
                        } label: {
                            Label("Face ID / Touch ID", systemImage: "faceid")
                                .padding(.horizontal, 16)
                                .padding(.vertical, 10)
                        }
                        .buttonStyle(.bordered)
                    }
                }
            }
            .padding(28)
        }
        .task {
            await lock.unlockWithBiometrics()
        }
    }
}

struct AppLockSettingsSheet: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var lock: AppLockManager = .shared
    @State private var chosenKind: AppLockKind = .pin
    @State private var newSecret: String = ""
    @State private var confirmSecret: String = ""
    @State private var message: String = ""

    var body: some View {
        NavigationStack {
            Form {
                Section("앱 잠금 및 화면 보호") {
                    LabeledContent("현재 상태") {
                        Text(lock.isEnabled ? "사용 중 (\(lock.lockKind == .pin ? "\(lock.pinLength)자리 PIN" : "비밀번호"))" : "꺼짐")
                            .foregroundStyle(lock.isEnabled ? .green : .secondary)
                    }
                    Toggle("Face ID / Touch ID 사용", isOn: $lock.useBiometrics)
                    Toggle("백그라운드 전환 시 즉시 잠금", isOn: $lock.lockOnBackground)
                    Toggle("앱 전환기 검은 화면 보호 (미리보기 가리기)", isOn: $lock.appSwitchBlackShield)
                }

                Section("잠금 방식 및 암호 설정") {
                    Picker("잠금 방식", selection: $chosenKind) {
                        ForEach(AppLockKind.allCases) { kind in
                            Text(kind.title).tag(kind)
                        }
                    }
                    .pickerStyle(.segmented)

                    SecureField(chosenKind == .pin ? "새 PIN (4~6자리 숫자)" : "새 비밀번호 (4자 이상)", text: $newSecret)
                        .keyboardType(chosenKind == .pin ? .numberPad : .default)
                    SecureField("암호 확인", text: $confirmSecret)
                        .keyboardType(chosenKind == .pin ? .numberPad : .default)

                    Button("저장 및 앱 잠금 활성화") {
                        guard newSecret == confirmSecret else {
                            message = "암호 확인이 일치하지 않습니다."
                            return
                        }
                        if lock.setSecret(newSecret, kind: chosenKind) {
                            newSecret = ""
                            confirmSecret = ""
                            message = "앱 잠금이 활성화되었습니다."
                        } else {
                            message = lock.errorMessage
                        }
                    }
                    if lock.isEnabled {
                        Button("앱 잠금 해제 (비활성화)", role: .destructive) {
                            lock.disableLock()
                            message = "앱 잠금이 비활성화되었습니다."
                        }
                    }
                    if !message.isEmpty {
                        Text(message).font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
            .navigationTitle("앱 잠금 (PIN · 비밀번호 · Face ID)")
            .navigationBarTitleDisplayMode(.inline)
            .onAppear { chosenKind = lock.lockKind }
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("완료") { dismiss() }
                }
            }
        }
        .preferredColorScheme(.dark)
    }
}

// MARK: - WireGuard .conf Profile Manager & DNS-over-HTTPS (Android VpnActivity + VpnProfiles Parity!)

struct WireGuardProfile: Codable, Identifiable, Hashable {
    var id: UUID = UUID()
    var name: String
    var endpoint: String
    var address: String
    var dns: String
    var rawConfig: String
}

enum DnsProviderPreset: String, CaseIterable, Codable, Identifiable {
    case system = "system"
    case cloudflare = "1.1.1.1"
    case google = "8.8.8.8"
    case quad9 = "9.9.9.9"
    case adguard = "adguard"
    case custom = "custom"

    var id: String { rawValue }

    var title: String {
        switch self {
        case .system: return "시스템 기본 DNS (우회 안 함)"
        case .cloudflare: return "Cloudflare DNS (1.1.1.1 DoH)"
        case .google: return "Google DNS (8.8.8.8 DoH)"
        case .quad9: return "Quad9 보안 DNS (9.9.9.9 DoH)"
        case .adguard: return "AdGuard DNS (광고·추적기 차단)"
        case .custom: return "사용자 지정 DoH 서버"
        }
    }

    var dohURL: String {
        switch self {
        case .system: return ""
        case .cloudflare: return "https://cloudflare-dns.com/dns-query"
        case .google: return "https://dns.google/dns-query"
        case .quad9: return "https://dns.quad9.net/dns-query"
        case .adguard: return "https://dns.adguard-dns.com/dns-query"
        case .custom: return ""
        }
    }

    var bootstrapIPs: [String] {
        switch self {
        case .system: return []
        case .cloudflare: return ["1.1.1.1", "1.0.0.1"]
        case .google: return ["8.8.8.8", "8.8.4.4"]
        case .quad9: return ["9.9.9.9", "149.112.112.112"]
        case .adguard: return ["94.140.14.14", "94.140.15.15"]
        case .custom: return ["1.1.1.1"]
        }
    }
}

struct MobileConfigDocument: FileDocument {
    static var readableContentTypes: [UTType] { [.xml, .data] }
    var data: Data
    init(data: Data) { self.data = data }
    init(configuration: ReadConfiguration) throws {
        data = configuration.file.regularFileContents ?? Data()
    }
    func fileWrapper(configuration: WriteConfiguration) throws -> FileWrapper {
        FileWrapper(regularFileWithContents: data)
    }
}

struct NetworkPrivacySheet: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var model: BrowserModel
    @State private var exportingProfile = false
    @State private var profileDoc = MobileConfigDocument(data: Data())
    @State private var importingWireGuard = false
    @State private var checkingConnection = false
    @State private var connectionResult = ""
    @State private var alertMessage: String? = nil

    var body: some View {
        NavigationStack {
            Form {
                // 1. 보안 DNS (DNS 우회) — Android VpnActivity 섹션 1
                Section {
                    Picker("보안 DNS (DoH)", selection: $model.preferences.dnsPreset) {
                        ForEach(DnsProviderPreset.allCases) { preset in
                            Text(preset.title).tag(preset)
                        }
                    }
                    .onChange(of: model.preferences.dnsPreset) { preset in
                        if preset != .custom {
                            model.preferences.customDoHURL = preset.dohURL
                        }
                        model.savePreferences()
                    }

                    if model.preferences.dnsPreset == .custom {
                        TextField("https://.../dns-query", text: $model.preferences.customDoHURL)
                            .textInputAutocapitalization(.never)
                            .autocorrectionDisabled()
                            .keyboardType(.URL)
                            .onChange(of: model.preferences.customDoHURL) { _ in model.savePreferences() }
                    } else if !model.preferences.dnsPreset.dohURL.isEmpty {
                        LabeledContent("DoH 엔드포인트") {
                            Text(model.preferences.dnsPreset.dohURL).font(.caption).foregroundStyle(.secondary)
                        }
                        LabeledContent("부트스트랩 IP") {
                            Text(model.preferences.dnsPreset.bootstrapIPs.joined(separator: ", "))
                                .font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                        }
                    }

                    if model.preferences.dnsPreset != .system {
                        Button {
                            let xml = generateDoHMobileConfig()
                            profileDoc = MobileConfigDocument(data: Data(xml.utf8))
                            exportingProfile = true
                        } label: {
                            Label("iOS 시스템 암호화 DNS 프로필(.mobileconfig) 내보내기", systemImage: "network.badge.shield.half.filled")
                        }
                    }
                } header: {
                    Text("보안 DNS (DNS 우회)")
                } footer: {
                    Text("기기 IP는 바꾸지 않고 도메인 질의(DNS)만 암호화된 DoH로 우회하여 ISP 차단 및 DNS 감청을 방지합니다.")
                }

                // 2. WireGuard VPN 프로필 (.conf) & 인터넷 연결 검사 — Android VpnActivity 섹션 2
                Section {
                    Button {
                        importingWireGuard = true
                    } label: {
                        Label("WireGuard 설정 추가 (.conf)", systemImage: "plus.circle.fill")
                    }

                    if model.wireGuardProfiles.isEmpty {
                        Text("저장된 WireGuard 프로필이 없습니다.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    } else {
                        ForEach(model.wireGuardProfiles) { prof in
                            VStack(alignment: .leading, spacing: 3) {
                                HStack {
                                    Text(prof.name).font(.subheadline.weight(.semibold))
                                    Spacer()
                                    ShareLink(item: prof.rawConfig) {
                                        Label("내보내기", systemImage: "square.and.arrow.up")
                                            .font(.caption)
                                    }
                                }
                                Text("Endpoint: \(prof.endpoint) · Address: \(prof.address)")
                                    .font(.caption2)
                                    .foregroundStyle(.secondary)
                            }
                            .swipeActions {
                                Button("삭제", role: .destructive) {
                                    model.wireGuardProfiles.removeAll { $0.id == prof.id }
                                    model.persist()
                                }
                            }
                        }
                    }

                    Button {
                        Task { await checkInternetConnectivity() }
                    } label: {
                        HStack {
                            Label("인터넷 연결 검사 (외부 IP · 지연 시간)", systemImage: "arrow.clockwise.circle")
                            Spacer()
                            if checkingConnection { ProgressView() }
                        }
                    }
                    .disabled(checkingConnection)

                    if !connectionResult.isEmpty {
                        Text(connectionResult)
                            .font(.caption.monospaced())
                            .foregroundStyle(.secondary)
                    }
                } header: {
                    Text("VPN (WireGuard .conf) 및 연결 검사")
                } footer: {
                    Text("WireGuard .conf 프로필을 검증·보관하고 iOS WireGuard/시스템 VPN으로 바로 공유할 수 있습니다. (AltStore 무료 서명 환경은 시스템 NetworkExtension 권한 제한이 있어 .conf 내보내기 및 DoH .mobileconfig를 함께 제공합니다.)")
                }

                // 3. SOCKS5 / HTTP 프록시
                Section("프록시 설정 (SOCKS5 / HTTP)") {
                    Toggle("프록시 프로필 활성화", isOn: $model.preferences.proxyEnabled)
                        .onChange(of: model.preferences.proxyEnabled) { _ in model.savePreferences() }
                    if model.preferences.proxyEnabled {
                        Picker("유형", selection: $model.preferences.proxyIsSOCKS5) {
                            Text("SOCKS5").tag(true)
                            Text("HTTP CONNECT").tag(false)
                        }
                        TextField("호스트 (예: 127.0.0.1)", text: $model.preferences.proxyHost)
                            .textInputAutocapitalization(.never)
                            .autocorrectionDisabled()
                        TextField("포트 (예: 1080)", value: $model.preferences.proxyPort, format: .number)
                            .keyboardType(.numberPad)
                    }
                }
            }
            .navigationTitle("VPN · 보안 DNS")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("완료") { dismiss() }
                }
            }
            .fileImporter(isPresented: $importingWireGuard, allowedContentTypes: [.data, .plainText]) { result in
                if case let .success(url) = result {
                    let access = url.startAccessingSecurityScopedResource()
                    defer { if access { url.stopAccessingSecurityScopedResource() } }
                    if let text = try? String(contentsOf: url, encoding: .utf8),
                       let parsed = Self.parseWireGuardConfig(name: url.deletingPathExtension().lastPathComponent, text: text) {
                        model.wireGuardProfiles.append(parsed)
                        model.persist()
                    } else {
                        alertMessage = "올바른 WireGuard .conf 파일인지 확인해 주세요. ([Interface] PrivateKey 및 [Peer] PublicKey/Endpoint 필요)"
                    }
                }
            }
            .fileExporter(
                isPresented: $exportingProfile,
                document: profileDoc,
                contentType: .xml,
                defaultFilename: "Omyaru-Encrypted-DNS.mobileconfig"
            ) { _ in }
            .alert("VPN 설정", isPresented: Binding(get: { alertMessage != nil }, set: { if !$0 { alertMessage = nil } })) {
                Button("확인") { alertMessage = nil }
            } message: {
                Text(alertMessage ?? "")
            }
        }
        .preferredColorScheme(.dark)
    }

    private func checkInternetConnectivity() async {
        checkingConnection = true
        defer { checkingConnection = false }
        let start = Date()
        do {
            let (data, _) = try await URLSession.shared.data(from: URL(string: "https://1.1.1.1/cdn-cgi/trace")!)
            let ms = Int(Date().timeIntervalSince(start) * 1000)
            let body = String(data: data, encoding: .utf8) ?? ""
            var ip = "-"
            var loc = "-"
            var tls = "-"
            for line in body.components(separatedBy: .newlines) {
                if line.hasPrefix("ip=") { ip = String(line.dropFirst(3)) }
                else if line.hasPrefix("loc=") { loc = String(line.dropFirst(4)) }
                else if line.hasPrefix("tls=") { tls = String(line.dropFirst(4)) }
            }
            connectionResult = "연결 정상 (\(ms)ms) · IP: \(ip) · 국가: \(loc) · \(tls)"
        } catch {
            connectionResult = "연결 검사 실패: \(error.localizedDescription)"
        }
    }

    private static func parseWireGuardConfig(name: String, text: String) -> WireGuardProfile? {
        guard text.count <= 64 * 1024 else { return nil }
        var hasPrivate = false
        var hasPublic = false
        var endpoint = ""
        var address = ""
        var dns = ""
        for rawLine in text.components(separatedBy: .newlines) {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            if line.isEmpty || line.hasPrefix("#") || line.hasPrefix("[") { continue }
            let parts = line.split(separator: "=", maxSplits: 1).map { $0.trimmingCharacters(in: .whitespaces) }
            guard parts.count == 2 else { continue }
            switch parts[0].lowercased() {
            case "privatekey": hasPrivate = !parts[1].isEmpty
            case "publickey": hasPublic = !parts[1].isEmpty
            case "endpoint": endpoint = parts[1]
            case "address": address = parts[1]
            case "dns": dns = parts[1]
            case "preup", "postup", "predown", "postdown":
                return nil // Reject external shell scripts just like Android VpnProfiles!
            default: break
            }
        }
        guard hasPrivate, hasPublic, !endpoint.isEmpty else { return nil }
        return WireGuardProfile(
            name: name.isEmpty ? "WireGuard" : name,
            endpoint: endpoint,
            address: address.isEmpty ? "-" : address,
            dns: dns.isEmpty ? "-" : dns,
            rawConfig: text
        )
    }

    private func generateDoHMobileConfig() -> String {
        let preset = model.preferences.dnsPreset
        let serverURL = preset == .custom ? model.preferences.customDoHURL : preset.dohURL
        let ips = preset.bootstrapIPs.map { "<string>\($0)</string>" }.joined(separator: "\n              ")
        return """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>PayloadContent</key>
          <array>
            <dict>
              <key>DNSSettings</key>
              <dict>
                <key>DNSProtocol</key>
                <string>HTTPS</string>
                <key>ServerAddresses</key>
                <array>
                  \(ips)
                </array>
                <key>ServerURL</key>
                <string>\(serverURL)</string>
              </dict>
              <key>PayloadDescription</key>
              <string>Omyaru Browser Encrypted DNS-over-HTTPS</string>
              <key>PayloadDisplayName</key>
              <string>Omyaru DoH (\(preset.title))</string>
              <key>PayloadIdentifier</key>
              <string>cuttan.omyaru.app.dns.payload</string>
              <key>PayloadType</key>
              <string>com.apple.dnsSettings.managed</string>
              <key>PayloadUUID</key>
              <string>9B3E31B2-5E10-4D32-9C7A-111122223333</string>
              <key>PayloadVersion</key>
              <integer>1</integer>
            </dict>
          </array>
          <key>PayloadDisplayName</key>
          <string>Omyaru Encrypted DNS (\(preset.title))</string>
          <key>PayloadIdentifier</key>
          <string>cuttan.omyaru.app.dns</string>
          <key>PayloadRemovalDisallowed</key>
          <false/>
          <key>PayloadType</key>
          <string>Configuration</string>
          <key>PayloadUUID</key>
          <string>9B3E31B2-5E10-4D32-9C7A-444455556666</string>
          <key>PayloadVersion</key>
          <integer>1</integer>
        </dict>
        </plist>
        """
    }
}

// MARK: - Built-in Extensions & UserScript (.user.js) Manager

struct UserScriptExtension: Codable, Identifiable, Hashable {
    var id: UUID = UUID()
    var name: String
    var matchPattern: String = "*://*/*"
    var runAtDocumentStart: Bool = false
    var isEnabled: Bool = true
    var code: String
}

struct ExtensionSettingsSheet: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var model: BrowserModel
    @State private var showAddScript = false
    @State private var showImportScript = false
    @State private var scriptName = ""
    @State private var scriptMatch = "*://*/*"
    @State private var scriptStart = false
    @State private var scriptCode = ""
    @State private var installURL = ""
    @State private var statusText = ""

    var body: some View {
        NavigationStack {
            Form {
                Section("Omyaru 내장 확장 기능 (WebExtensions)") {
                    Toggle(isOn: $model.preferences.enableImagePreloader) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text("웹 이미지 고속 선로딩 (Image Preloader)")
                            Text("스크롤 전 뷰포트 하단 2200px 이내 지연 이미지를 미리 로드합니다.")
                                .font(.caption2).foregroundStyle(.secondary)
                        }
                    }
                    Toggle(isOn: $model.preferences.hideCookieNotices) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text("쿠키 동의 팝업 자동 숨김")
                            Text("웹사이트 하단/중앙 쿠키 배너를 자동으로 숨깁니다.")
                                .font(.caption2).foregroundStyle(.secondary)
                        }
                    }
                    Toggle(isOn: $model.preferences.unlockCopyAndContextMenu) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text("우클릭 · 드래그 · 복사 방지 해제")
                            Text("텍스트 선택 및 길게 누르기 제한을 해제합니다.")
                                .font(.caption2).foregroundStyle(.secondary)
                        }
                    }
                    Toggle(isOn: $model.preferences.enableMediaPiPHelper) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text("동영상 PiP · 백그라운드 재생 보조")
                            Text("HTML5 비디오에서 Picture-in-Picture를 활성화합니다.")
                                .font(.caption2).foregroundStyle(.secondary)
                        }
                    }
                    Toggle(isOn: $model.preferences.forceWebDarkMode) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text("웹 페이지 다크 모드 강제 적용")
                            Text("밝은 배경 웹사이트를 어두운 테마로 변환합니다.")
                                .font(.caption2).foregroundStyle(.secondary)
                        }
                    }
                }
                .onChange(of: model.preferences) { _ in
                    model.savePreferences()
                    model.applyUserScriptsToAllWebViews()
                }

                Section("유저스크립트 (.user.js) URL 또는 파일 설치") {
                    HStack {
                        TextField("https://.../*.user.js 주소 입력", text: $installURL)
                            .textInputAutocapitalization(.never)
                            .autocorrectionDisabled()
                            .keyboardType(.URL)
                        Button("URL 설치") {
                            Task { await installScriptFromURL() }
                        }
                        .buttonStyle(.borderedProminent)
                        .disabled(installURL.trimmingCharacters(in: .whitespaces).isEmpty)
                    }
                    HStack(spacing: 12) {
                        Button {
                            showImportScript = true
                        } label: {
                            Label(".user.js / .js 파일 불러오기", systemImage: "doc.badge.plus")
                        }
                        Spacer()
                        Button {
                            scriptName = ""
                            scriptMatch = "*://*/*"
                            scriptStart = false
                            scriptCode = ""
                            showAddScript = true
                        } label: {
                            Label("직접 작성", systemImage: "plus.circle.fill")
                        }
                    }
                    if !statusText.isEmpty {
                        Text(statusText).font(.caption).foregroundStyle(.secondary)
                    }
                }

                Section("설치된 유저스크립트 (\(model.userScripts.count))") {
                    if model.userScripts.isEmpty {
                        Text("추가된 커스텀 유저스크립트가 없습니다.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    } else {
                        ForEach($model.userScripts) { $script in
                            HStack {
                                VStack(alignment: .leading, spacing: 3) {
                                    Text(script.name).font(.subheadline.weight(.semibold))
                                    Text("\(script.matchPattern) · \(script.runAtDocumentStart ? "document-start" : "document-end")")
                                        .font(.caption2)
                                        .foregroundStyle(.secondary)
                                }
                                Spacer()
                                Toggle("", isOn: $script.isEnabled)
                                    .labelsHidden()
                                    .onChange(of: script.isEnabled) { _ in
                                        model.persist()
                                        model.applyUserScriptsToAllWebViews()
                                    }
                            }
                            .swipeActions {
                                Button("삭제", role: .destructive) {
                                    model.userScripts.removeAll { $0.id == script.id }
                                    model.persist()
                                    model.applyUserScriptsToAllWebViews()
                                }
                            }
                        }
                    }
                }
            }
            .navigationTitle("확장 기능 · 유저스크립트")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("완료") { dismiss() }
                }
            }
            .fileImporter(isPresented: $showImportScript, allowedContentTypes: [.data, .plainText, .sourceCode]) { result in
                if case let .success(url) = result {
                    let access = url.startAccessingSecurityScopedResource()
                    defer { if access { url.stopAccessingSecurityScopedResource() } }
                    if let code = try? String(contentsOf: url, encoding: .utf8) {
                        addParsedUserScript(code: code, fallbackName: url.deletingPathExtension().lastPathComponent)
                    }
                }
            }
            .sheet(isPresented: $showAddScript) {
                NavigationStack {
                    Form {
                        TextField("스크립트 이름", text: $scriptName)
                        TextField("대상 도메인 (예: *://*/* 또는 toptoon.com)", text: $scriptMatch)
                            .textInputAutocapitalization(.never)
                            .autocorrectionDisabled()
                        Toggle("문서 시작 시점(document-start) 실행", isOn: $scriptStart)
                        Section("JavaScript 코드") {
                            TextEditor(text: $scriptCode)
                                .font(.system(.caption, design: .monospaced))
                                .frame(minHeight: 220)
                        }
                    }
                    .navigationTitle("새 유저스크립트")
                    .navigationBarTitleDisplayMode(.inline)
                    .toolbar {
                        ToolbarItem(placement: .cancellationAction) {
                            Button("취소") { showAddScript = false }
                        }
                        ToolbarItem(placement: .confirmationAction) {
                            Button("추가") {
                                let name = scriptName.trimmingCharacters(in: .whitespacesAndNewlines)
                                guard !name.isEmpty, !scriptCode.isEmpty else { return }
                                model.userScripts.append(UserScriptExtension(
                                    name: name,
                                    matchPattern: scriptMatch.isEmpty ? "*://*/*" : scriptMatch,
                                    runAtDocumentStart: scriptStart,
                                    isEnabled: true,
                                    code: scriptCode
                                ))
                                model.persist()
                                model.applyUserScriptsToAllWebViews()
                                showAddScript = false
                            }
                        }
                    }
                }
                .preferredColorScheme(.dark)
            }
        }
        .preferredColorScheme(.dark)
    }

    private func installScriptFromURL() async {
        guard let url = URL(string: installURL.trimmingCharacters(in: .whitespacesAndNewlines)) else {
            statusText = "올바른 URL이 아닙니다."
            return
        }
        do {
            let (data, _) = try await URLSession.shared.data(from: url)
            guard let code = String(data: data, encoding: .utf8), !code.isEmpty else {
                statusText = "스크립트 내용을 읽지 못했습니다."
                return
            }
            addParsedUserScript(code: code, fallbackName: url.deletingPathExtension().lastPathComponent)
            installURL = ""
        } catch {
            statusText = "설치 실패: \(error.localizedDescription)"
        }
    }

    private func addParsedUserScript(code: String, fallbackName: String) {
        var name = fallbackName.isEmpty ? "UserScript" : fallbackName
        var match = "*://*/*"
        var start = false
        for line in code.components(separatedBy: .newlines).prefix(60) {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed.hasPrefix("// @name"), let val = trimmed.dropFirst(8).trimmingCharacters(in: .whitespaces) as String?, !val.isEmpty {
                name = val
            } else if trimmed.hasPrefix("// @match"), let val = trimmed.dropFirst(9).trimmingCharacters(in: .whitespaces) as String?, !val.isEmpty {
                match = val
            } else if trimmed.hasPrefix("// @run-at") && trimmed.contains("document-start") {
                start = true
            }
        }
        model.userScripts.append(UserScriptExtension(
            name: name,
            matchPattern: match,
            runAtDocumentStart: start,
            isEnabled: true,
            code: code
        ))
        model.persist()
        model.applyUserScriptsToAllWebViews()
        statusText = "‘\(name)’ 유저스크립트가 설치되었습니다."
    }
}
