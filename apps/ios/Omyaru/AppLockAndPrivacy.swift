import SwiftUI
import LocalAuthentication
import CryptoKit
import CommonCrypto
import UniformTypeIdentifiers
import UIKit

// MARK: - App Lock Manager (Face ID / Touch ID + PBKDF2 PIN)

@MainActor
final class AppLockManager: ObservableObject {
    static let shared = AppLockManager()

    @Published var isEnabled: Bool {
        didSet { UserDefaults.standard.set(isEnabled, forKey: "omyaru.ios.lock.enabled") }
    }
    @Published var useBiometrics: Bool {
        didSet { UserDefaults.standard.set(useBiometrics, forKey: "omyaru.ios.lock.biometrics") }
    }
    @Published var lockOnBackground: Bool {
        didSet { UserDefaults.standard.set(lockOnBackground, forKey: "omyaru.ios.lock.onBackground") }
    }
    @Published var isLocked: Bool = false
    @Published var errorMessage: String = ""

    private let hashKey = "omyaru.ios.lock.pinHash"
    private let saltKey = "omyaru.ios.lock.pinSalt"

    init() {
        let d = UserDefaults.standard
        let enabled = d.bool(forKey: "omyaru.ios.lock.enabled")
        isEnabled = enabled
        useBiometrics = d.object(forKey: "omyaru.ios.lock.biometrics") as? Bool ?? true
        lockOnBackground = d.object(forKey: "omyaru.ios.lock.onBackground") as? Bool ?? true
        isLocked = enabled
    }

    var hasPIN: Bool {
        !(UserDefaults.standard.string(forKey: hashKey) ?? "").isEmpty
    }

    func setPIN(_ pin: String) -> Bool {
        let trimmed = pin.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.count >= 4, trimmed.count <= 12 else {
            errorMessage = "PIN은 4~12자리여야 합니다."
            return false
        }
        var saltBytes = [UInt8](repeating: 0, count: 16)
        _ = SecRandomCopyBytes(kSecRandomDefault, 16, &saltBytes)
        let salt = Data(saltBytes)
        let hash = Self.hashPIN(trimmed, salt: salt)
        UserDefaults.standard.set(salt.base64EncodedString(), forKey: saltKey)
        UserDefaults.standard.set(hash.base64EncodedString(), forKey: hashKey)
        isEnabled = true
        isLocked = false
        errorMessage = ""
        return true
    }

    func verifyPIN(_ pin: String) -> Bool {
        guard let saltB64 = UserDefaults.standard.string(forKey: saltKey),
              let salt = Data(base64Encoded: saltB64),
              let expectedB64 = UserDefaults.standard.string(forKey: hashKey),
              let expected = Data(base64Encoded: expectedB64) else {
            return false
        }
        let actual = Self.hashPIN(pin.trimmingCharacters(in: .whitespacesAndNewlines), salt: salt)
        if actual == expected {
            isLocked = false
            errorMessage = ""
            return true
        } else {
            errorMessage = "PIN이 일치하지 않습니다."
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
            errorMessage = "생체 인증을 완료하지 못했습니다. PIN을 입력해 주세요."
        }
    }

    private static func hashPIN(_ pin: String, salt: Data) -> Data {
        var derived = [UInt8](repeating: 0, count: 32)
        _ = salt.withUnsafeBytes { saltBuf in
            pin.withCString { pinPtr in
                CCKeyDerivationPBKDF(
                    CCPBKDFAlgorithm(kCCPBKDF2),
                    pinPtr,
                    pin.utf8.count,
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
    @State private var pinInput: String = ""

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

                SecureField("PIN 입력", text: $pinInput)
                    .keyboardType(.numberPad)
                    .multilineTextAlignment(.center)
                    .font(.title3.monospacedDigit())
                    .padding(12)
                    .frame(width: 220)
                    .background(Color(red: 0.14, green: 0.14, blue: 0.16), in: RoundedRectangle(cornerRadius: 12))
                    .onSubmit {
                        if lock.verifyPIN(pinInput) { pinInput = "" }
                    }

                if !lock.errorMessage.isEmpty {
                    Text(lock.errorMessage)
                        .font(.caption)
                        .foregroundStyle(.red)
                }

                HStack(spacing: 12) {
                    Button {
                        if lock.verifyPIN(pinInput) { pinInput = "" }
                    } label: {
                        Label("PIN 해제", systemImage: "key.fill")
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
    @State private var newPIN: String = ""
    @State private var confirmPIN: String = ""
    @State private var message: String = ""

    var body: some View {
        NavigationStack {
            Form {
                Section("앱 잠금 상태") {
                    LabeledContent("현재 상태") {
                        Text(lock.isEnabled ? "사용 중" : "꺼짐")
                            .foregroundStyle(lock.isEnabled ? .green : .secondary)
                    }
                    Toggle("Face ID / Touch ID 사용", isOn: $lock.useBiometrics)
                    Toggle("백그라운드 전환 시 즉시 잠금", isOn: $lock.lockOnBackground)
                }

                Section("PIN 설정 / 변경") {
                    SecureField("새 PIN (4~12자리)", text: $newPIN)
                        .keyboardType(.numberPad)
                    SecureField("새 PIN 확인", text: $confirmPIN)
                        .keyboardType(.numberPad)
                    Button("PIN 저장 및 잠금 활성화") {
                        guard newPIN == confirmPIN else {
                            message = "PIN 확인이 일치하지 않습니다."
                            return
                        }
                        if lock.setPIN(newPIN) {
                            newPIN = ""
                            confirmPIN = ""
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
            .navigationTitle("앱 잠금 (Face ID · PIN)")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("완료") { dismiss() }
                }
            }
        }
        .preferredColorScheme(.dark)
    }
}

// MARK: - DNS / DoH & Proxy Privacy Configuration (Android DNS/VPN Parity)

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
        case .system: return "시스템 기본 DNS"
        case .cloudflare: return "Cloudflare (1.1.1.1 · DoH 고속)"
        case .google: return "Google Public DNS (8.8.8.8 · DoH)"
        case .quad9: return "Quad9 보안 DNS (9.9.9.9 · 악성 도메인 차단)"
        case .adguard: return "AdGuard DNS (광고·추적기 도메인 차단)"
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

    var body: some View {
        NavigationStack {
            Form {
                Section("DNS-over-HTTPS (DoH) 보안 DNS 설정") {
                    Picker("DNS 공급자", selection: $model.preferences.dnsPreset) {
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
                        LabeledContent("엔드포인트") {
                            Text(model.preferences.dnsPreset.dohURL)
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                        LabeledContent("부트스트랩 IP") {
                            Text(model.preferences.dnsPreset.bootstrapIPs.joined(separator: ", "))
                                .font(.caption.monospacedDigit())
                                .foregroundStyle(.secondary)
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
                }

                Section {
                    Toggle("SOCKS5 / HTTP 프록시 사용 (iOS 17+ WebKit)", isOn: $model.preferences.proxyEnabled)
                        .onChange(of: model.preferences.proxyEnabled) { _ in
                            model.savePreferences()
                            model.rebuildAllWebViewsConfiguration()
                        }
                    if model.preferences.proxyEnabled {
                        Picker("프록시 유형", selection: $model.preferences.proxyIsSOCKS5) {
                            Text("SOCKS5").tag(true)
                            Text("HTTP CONNECT").tag(false)
                        }
                        TextField("호스트 (예: 127.0.0.1)", text: $model.preferences.proxyHost)
                            .textInputAutocapitalization(.never)
                            .autocorrectionDisabled()
                        TextField("포트 (예: 1080)", value: $model.preferences.proxyPort, format: .number)
                            .keyboardType(.numberPad)
                        Button("프록시 설정 적용") {
                            model.savePreferences()
                            model.rebuildAllWebViewsConfiguration()
                        }
                    }
                } header: {
                    Text("프록시 / 우회 라우팅")
                } footer: {
                    Text("iOS WebKit 데이터 스토어 및 DoH 부트스트랩 프리페치를 통해 DNS 조회 지연을 줄이고 프라이버시를 보호합니다.")
                }
            }
            .navigationTitle("DNS · 프록시 · 네트워크 보호")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("완료") { dismiss() }
                }
            }
            .fileExporter(
                isPresented: $exportingProfile,
                document: profileDoc,
                contentType: .xml,
                defaultFilename: "Omyaru-Encrypted-DNS.mobileconfig"
            ) { _ in }
        }
        .preferredColorScheme(.dark)
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
                    Toggle(isOn: $model.preferences.blockTrackers) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text("광고 · 추적기 및 배너 차단")
                            Text("서드파티 광고 네트워크 차단 및 배너 요소를 숨깁니다.")
                                .font(.caption2).foregroundStyle(.secondary)
                        }
                    }
                    Toggle(isOn: $model.preferences.hideCookieNotices) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text("쿠키 동의 팝업 자동 숨김")
                            Text("웹사이트 하단/중앙 쿠키 배너를 자동으로 닫거나 숨깁니다.")
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
