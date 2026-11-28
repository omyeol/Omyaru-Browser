import SwiftUI
import PhotosUI
import UniformTypeIdentifiers
import UIKit
import ImageIO
import CryptoKit

// MARK: - Bookshelf Path Helper (Parity with Android/Windows BookshelfPath)

enum BookshelfPath {
    static func normalize(_ raw: String) -> String? {
        let replaced = raw.replacingOccurrences(of: "\\", with: "/")
        let segments = replaced
            .split(separator: "/", omittingEmptySubsequences: true)
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
        guard !segments.isEmpty, segments.count <= 16 else { return nil }
        for seg in segments {
            if seg.isEmpty || seg == "." || seg == ".." || seg.count > 80 { return nil }
            if seg.contains(where: { "<>:\"|?*".contains($0) || $0.isNewline }) { return nil }
        }
        return segments.joined(separator: "/")
    }

    static func parent(_ path: String) -> String {
        guard let norm = normalize(path), let idx = norm.lastIndex(of: "/") else { return "" }
        return String(norm[..<idx])
    }

    static func leaf(_ path: String) -> String {
        guard let norm = normalize(path) else { return path.trimmingCharacters(in: .whitespacesAndNewlines) }
        guard let idx = norm.lastIndex(of: "/") else { return norm }
        return String(norm[norm.index(after: idx)...])
    }

    static func contains(_ root: String, _ candidate: String) -> Bool {
        guard let r = normalize(root), let c = normalize(candidate) else { return false }
        if r.caseInsensitiveCompare(c) == .orderedSame { return true }
        return c.lowercased().hasPrefix(r.lowercased() + "/")
    }

    static func allDisplayFolders(_ folders: [String]) -> [String] {
        var seen = Set<String>()
        var roots: [String] = []
        var childrenByParent: [String: [String]] = [:]
        for folder in folders {
            guard let norm = normalize(folder) else { continue }
            var ancestors: [String] = []
            var cur = norm
            while !cur.isEmpty {
                ancestors.append(cur)
                cur = parent(cur)
            }
            for path in ancestors.reversed() {
                let key = path.lowercased()
                if seen.insert(key).inserted {
                    let parentKey = parent(path).lowercased()
                    if parentKey.isEmpty {
                        roots.append(path)
                    } else {
                        childrenByParent[parentKey, default: []].append(path)
                    }
                }
            }
        }
        var result: [String] = []
        var visited = Set<String>()
        func visit(_ path: String) {
            let key = path.lowercased()
            guard visited.insert(key).inserted else { return }
            result.append(path)
            if let children = childrenByParent[key] {
                for child in children {
                    visit(child)
                }
            }
        }
        for root in roots {
            visit(root)
        }
        return result
    }

    static func relocation(_ folders: [String], from source: String, to destination: String) -> [String: String]? {
        guard let src = normalize(source), let dst = normalize(destination) else { return nil }
        if src.caseInsensitiveCompare(dst) == .orderedSame { return nil }
        if contains(src, dst) { return nil }
        var map: [String: String] = [:]
        let all = allDisplayFolders(folders)
        for folder in all where contains(src, folder) {
            let suffix = folder.count == src.count ? "" : String(folder.dropFirst(src.count))
            guard let target = normalize(dst + suffix) else { return nil }
            map[folder] = target
        }
        if map.isEmpty { map[src] = dst }
        let movingKeys = Set(map.keys.map { $0.lowercased() })
        let existingRemaining = Set(all.map { $0.lowercased() }).subtracting(movingKeys)
        for target in map.values {
            if existingRemaining.contains(target.lowercased()) { return nil }
        }
        return map
    }

    static func moveSibling(_ folders: [String], folder: String, direction: Int) -> [String]? {
        guard let target = normalize(folder), direction != 0 else { return nil }
        let all = allDisplayFolders(folders)
        let parentPath = parent(target)
        let siblings = all.filter { parent($0).caseInsensitiveCompare(parentPath) == .orderedSame }
        guard let idx = siblings.firstIndex(where: { $0.caseInsensitiveCompare(target) == .orderedSame }) else { return nil }
        let nextIdx = idx + (direction < 0 ? -1 : 1)
        guard siblings.indices.contains(nextIdx) else { return nil }
        let other = siblings[nextIdx]

        var blocks: [(root: String, items: [String])] = []
        var consumed = Set<String>()
        for f in folders {
            let key = f.lowercased()
            if consumed.contains(key) { continue }
            if let sib = siblings.first(where: { contains($0, f) }) {
                let group = folders.filter { contains(sib, $0) }
                for g in group { consumed.insert(g.lowercased()) }
                blocks.append((sib, group))
            } else {
                consumed.insert(key)
                blocks.append((f, [f]))
            }
        }
        guard let b1 = blocks.firstIndex(where: { $0.root.caseInsensitiveCompare(target) == .orderedSame }),
              let b2 = blocks.firstIndex(where: { $0.root.caseInsensitiveCompare(other) == .orderedSame }) else {
            return nil
        }
        blocks.swapAt(b1, b2)
        return blocks.flatMap(\.items)
    }
}

// MARK: - Cloud Image Chunk Manifest (Wire-Compatible with Android/Windows/Umtomi)

struct ShelfCloudImage: Codable, Equatable, Hashable, Sendable {
    struct Reference: Codable, Equatable, Hashable, Sendable {
        var id: String
        var name: String
    }
    var version: Int = 1
    var length: Int
    var sha256: String
    var chunks: [String]
    var files: [Reference]
}

// MARK: - Bookshelf Item & Catalog Models

struct BookshelfItem: Codable, Identifiable, Hashable {
    var id: String = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
    var folder: String
    var fileName: String
    var sourceUrl: String = ""
    var width: Int = 720
    var height: Int = 1024
    var createdAtEpochMs: Int64 = Int64(Date().timeIntervalSince1970 * 1000)
    var cloud: ShelfCloudImage? = nil
}

struct BookshelfCatalogData: Codable {
    var activeFolder: String = "기본 책장"
    var columnCount: Int = 2
    var folders: [String] = ["기본 책장"]
    var folderIds: [String: String] = [:]
    var folderNotes: [String: String] = [:]
    var folderColumnCounts: [String: Int] = [:]
    var items: [BookshelfItem] = []
}

// MARK: - Bookshelf Store (Singleton & ObservableObject)

@MainActor
final class ShelfModel: ObservableObject {
    static let shared = ShelfModel()

    @Published var catalog: BookshelfCatalogData
    @Published var expandedFolders: Set<String> = []
    @Published var status: String = ""
    @Published var busy: Bool = false

    let privateRoot: URL = {
        let base = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("Bookshelf", isDirectory: true)
    }()

    /// Active storage directory for bookshelf images (shared `<BaseFolder>/<account>/originals` when configured, otherwise private sandbox).
    var root: URL {
        if let accountRoot = SharedSyncFolderManager.shared.activeAccountRoot(for: DriveSyncManager.shared.connectedEmail) {
            return SharedSyncFolderManager.originalsDirectory(in: accountRoot)
        }
        return privateRoot
    }

    private var catalogURL: URL { privateRoot.appendingPathComponent("catalog.json") }

    init() {
        try? FileManager.default.createDirectory(at: privateRoot, withIntermediateDirectories: true)
        if let data = try? Data(contentsOf: privateRoot.appendingPathComponent("catalog.json")),
           let decoded = try? JSONDecoder().decode(BookshelfCatalogData.self, from: data) {
            catalog = Self.normalizeCatalog(decoded)
        } else {
            catalog = Self.normalizeCatalog(BookshelfCatalogData())
        }
        expandAncestors(of: catalog.activeFolder)
    }

    private static func compactUUID() -> String {
        UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
    }

    /// Normalize legacy seconds timestamps (< 10^11) to epoch milliseconds.
    nonisolated static func normalizedEpochMs(_ raw: Int64) -> Int64 {
        (raw > 0 && raw < 100_000_000_000) ? raw * 1000 : raw
    }

    /// Canonical bookshelf photo order: oldest photo FIRST (top) -> most recent photo LAST (bottom).
    nonisolated static func sortItemsChronologically(_ items: [BookshelfItem]) -> [BookshelfItem] {
        items.sorted { a, b in
            let ta = normalizedEpochMs(a.createdAtEpochMs)
            let tb = normalizedEpochMs(b.createdAtEpochMs)
            if ta != tb { return ta < tb }
            return a.id.localizedCaseInsensitiveCompare(b.id) == .orderedAscending
        }
    }

    static func normalizeCatalog(_ input: BookshelfCatalogData) -> BookshelfCatalogData {
        var c = input
        var seen = Set<String>()
        var normalizedFolders: [String] = []
        for raw in c.folders {
            if let norm = BookshelfPath.normalize(raw) {
                let key = norm.lowercased()
                if seen.insert(key).inserted {
                    normalizedFolders.append(norm)
                }
            }
        }
        for item in c.items {
            if let norm = BookshelfPath.normalize(item.folder) {
                let key = norm.lowercased()
                if seen.insert(key).inserted {
                    normalizedFolders.append(norm)
                }
            }
        }
        if normalizedFolders.isEmpty {
            normalizedFolders.append("기본 책장")
        }
        c.folders = normalizedFolders

        // Preserve notes, IDs, and columnCounts for both explicit and virtual parent folders!
        let allPaths = Set(BookshelfPath.allDisplayFolders(normalizedFolders).map { $0 })
        for path in allPaths {
            if c.folderIds[path] == nil {
                c.folderIds[path] = compactUUID()
            }
            if c.folderColumnCounts[path] == nil {
                c.folderColumnCounts[path] = 2
            } else {
                c.folderColumnCounts[path] = min(6, max(1, c.folderColumnCounts[path] ?? 2))
            }
        }
        for (k, v) in c.folderColumnCounts {
            c.folderColumnCounts[k] = min(6, max(1, v))
        }

        if let activeNorm = BookshelfPath.normalize(c.activeFolder),
           allPaths.contains(where: { $0.caseInsensitiveCompare(activeNorm) == .orderedSame }) {
            c.activeFolder = allPaths.first(where: { $0.caseInsensitiveCompare(activeNorm) == .orderedSame }) ?? normalizedFolders[0]
        } else {
            c.activeFolder = normalizedFolders[0]
        }
        c.columnCount = min(6, max(1, c.folderColumnCounts[c.activeFolder] ?? c.columnCount))
        c.folderColumnCounts[c.activeFolder] = c.columnCount
        c.items = sortItemsChronologically(c.items)
        return c
    }

    var activeFolder: String { catalog.activeFolder }
    var activeNote: String { catalog.folderNotes[catalog.activeFolder] ?? "" }
    var activeColumns: Int { min(6, max(1, catalog.folderColumnCounts[catalog.activeFolder] ?? 2)) }
    var allDisplayFolders: [String] { BookshelfPath.allDisplayFolders(catalog.folders) }

    func visibleItems(in folder: String? = nil) -> [BookshelfItem] {
        let target = folder ?? catalog.activeFolder
        let filtered = catalog.items.filter { BookshelfPath.contains(target, $0.folder) }
        return Self.sortItemsChronologically(filtered)
    }

    func directItems(in folder: String) -> [BookshelfItem] {
        let filtered = catalog.items.filter { $0.folder.caseInsensitiveCompare(folder) == .orderedSame }
        return Self.sortItemsChronologically(filtered)
    }

    func imageURL(for item: BookshelfItem) -> URL {
        let fm = FileManager.default
        if let accountRoot = SharedSyncFolderManager.shared.activeAccountRoot(for: DriveSyncManager.shared.connectedEmail) {
            if let sha = item.cloud?.sha256,
               let byHash = SharedSyncFolderManager.existingOriginalURL(sha256: sha, in: accountRoot) {
                return byHash
            }
            let sharedCandidate = SharedSyncFolderManager.originalsDirectory(in: accountRoot).appendingPathComponent(item.fileName)
            if fm.fileExists(atPath: sharedCandidate.path) {
                return sharedCandidate
            }
            let privateCandidate = privateRoot.appendingPathComponent(item.fileName)
            if fm.fileExists(atPath: privateCandidate.path) {
                return privateCandidate
            }
            if let sha = item.cloud?.sha256, sha.count == 64 {
                return SharedSyncFolderManager.originalsDirectory(in: accountRoot).appendingPathComponent("\(sha.lowercased()).png")
            }
            return sharedCandidate
        }
        return privateRoot.appendingPathComponent(item.fileName)
    }

    func reloadFromSharedStorageIfAvailable(accountEmail: String?) {
        guard let accountRoot = SharedSyncFolderManager.shared.activeAccountRoot(for: accountEmail) else { return }
        let sharedCatalogFile = SharedSyncFolderManager.sharedCatalogURL(in: accountRoot)
        guard FileManager.default.fileExists(atPath: sharedCatalogFile.path) else {
            persistToSharedCatalogIfAvailable(accountEmail: accountEmail)
            return
        }
        let merged: BookshelfCatalogData? = (try? SharedSyncFolderManager.withCatalogWriteLock(in: accountRoot) { () -> BookshelfCatalogData? in
            guard let data = try? Data(contentsOf: sharedCatalogFile),
                  let remote = try? JSONDecoder().decode(BookshelfCatalogData.self, from: data) else { return nil }
            return Self.mergeCatalogs(local: catalog, shared: remote)
        }) ?? nil
        if let merged {
            catalog = Self.normalizeCatalog(merged)
            save(syncToShared: false)
        }
    }

    private static func mergeCatalogs(local: BookshelfCatalogData, shared: BookshelfCatalogData) -> BookshelfCatalogData {
        var result = shared
        var seenFolders = Set(result.folders.map { $0.lowercased() })
        for folder in local.folders {
            if seenFolders.insert(folder.lowercased()).inserted {
                result.folders.append(folder)
            }
        }
        for (k, v) in local.folderIds where result.folderIds[k] == nil {
            result.folderIds[k] = v
        }
        for (k, v) in local.folderNotes where (result.folderNotes[k] ?? "").isEmpty && !v.isEmpty {
            result.folderNotes[k] = v
        }
        for (k, v) in local.folderColumnCounts where result.folderColumnCounts[k] == nil {
            result.folderColumnCounts[k] = v
        }
        var existingItemIDs = Set(result.items.map { $0.id.lowercased() })
        for item in local.items {
            if existingItemIDs.insert(item.id.lowercased()).inserted {
                result.items.append(item)
            }
        }
        result.items = sortItemsChronologically(result.items)
        return result
    }

    private func persistToSharedCatalogIfAvailable(accountEmail: String?) {
        guard let accountRoot = SharedSyncFolderManager.shared.activeAccountRoot(for: accountEmail) else { return }
        let snapshot = catalog
        let sharedCatalogFile = SharedSyncFolderManager.sharedCatalogURL(in: accountRoot)
        try? SharedSyncFolderManager.withCatalogWriteLock(in: accountRoot) {
            let encoded = try JSONEncoder().encode(snapshot)
            try encoded.write(to: sharedCatalogFile, options: .atomic)
        }
    }

    func save(syncToShared: Bool = true) {
        catalog = Self.normalizeCatalog(catalog)
        do {
            try FileManager.default.createDirectory(at: privateRoot, withIntermediateDirectories: true)
            let data = try JSONEncoder().encode(catalog)
            try data.write(to: catalogURL, options: .atomic)
            if syncToShared {
                persistToSharedCatalogIfAvailable(accountEmail: DriveSyncManager.shared.connectedEmail)
            }
        } catch {
            status = "저장 실패: \(error.localizedDescription)"
        }
    }

    func isFolderExpanded(_ path: String) -> Bool {
        let key = (BookshelfPath.normalize(path) ?? path).lowercased()
        return expandedFolders.contains(key) || expandedFolders.contains(path)
    }

    func toggleFolderExpanded(_ path: String) {
        let key = (BookshelfPath.normalize(path) ?? path).lowercased()
        if isFolderExpanded(path) {
            expandedFolders.remove(key)
            expandedFolders.remove(path)
        } else {
            expandedFolders.insert(key)
        }
    }

    func expandAncestors(of path: String) {
        var cur = BookshelfPath.normalize(path) ?? path
        while !cur.isEmpty {
            expandedFolders.insert(cur.lowercased())
            cur = BookshelfPath.parent(cur)
        }
    }

    func selectFolder(_ path: String) {
        guard let norm = BookshelfPath.normalize(path) else { return }
        catalog.activeFolder = norm
        if catalog.folderColumnCounts[norm] == nil {
            catalog.folderColumnCounts[norm] = 2
        }
        catalog.columnCount = min(6, max(1, catalog.folderColumnCounts[norm] ?? 2))
        expandAncestors(of: norm)
        save()
    }

    @discardableResult
    func addFolder(_ requestedPath: String, select: Bool = true) -> String? {
        guard let norm = BookshelfPath.normalize(requestedPath) else {
            status = "사용할 수 없는 폴더 이름입니다."
            return nil
        }
        if !catalog.folders.contains(where: { $0.caseInsensitiveCompare(norm) == .orderedSame }) {
            catalog.folders.append(norm)
        }
        if catalog.folderIds[norm] == nil { catalog.folderIds[norm] = Self.compactUUID() }
        if catalog.folderColumnCounts[norm] == nil { catalog.folderColumnCounts[norm] = 2 }
        if select {
            catalog.activeFolder = norm
            catalog.columnCount = catalog.folderColumnCounts[norm] ?? 2
            expandAncestors(of: norm)
        }
        save()
        return norm
    }

    @discardableResult
    func renameOrMoveFolder(from source: String, to destination: String) -> Bool {
        guard let moves = BookshelfPath.relocation(catalog.folders, from: source, to: destination) else {
            status = "같은 이름의 폴더가 있거나 이동할 수 없는 위치입니다."
            return false
        }
        var savedIds: [String: String] = [:]
        var savedNotes: [String: String] = [:]
        var savedCols: [String: Int] = [:]
        for (oldPath, _) in moves {
            savedIds[oldPath] = catalog.folderIds[oldPath] ?? Self.compactUUID()
            savedNotes[oldPath] = catalog.folderNotes[oldPath] ?? ""
            savedCols[oldPath] = catalog.folderColumnCounts[oldPath] ?? 2
        }
        for i in catalog.folders.indices {
            if let renamed = moves[catalog.folders[i]] {
                catalog.folders[i] = renamed
            }
        }
        // If moving a virtual parent folder whose children were in catalog.folders, ensure destination exists
        if let topTarget = moves[source], !catalog.folders.contains(where: { BookshelfPath.contains(topTarget, $0) }) {
            catalog.folders.append(topTarget)
        }
        for (oldPath, newPath) in moves {
            catalog.folderIds.removeValue(forKey: oldPath)
            catalog.folderNotes.removeValue(forKey: oldPath)
            catalog.folderColumnCounts.removeValue(forKey: oldPath)
            catalog.folderIds[newPath] = savedIds[oldPath]
            catalog.folderNotes[newPath] = savedNotes[oldPath]
            catalog.folderColumnCounts[newPath] = savedCols[oldPath]
        }
        for i in catalog.items.indices {
            if let targetFolder = moves[catalog.items[i].folder] {
                catalog.items[i].folder = targetFolder
            }
        }
        if let activeMoved = moves[catalog.activeFolder] {
            catalog.activeFolder = activeMoved
            expandAncestors(of: activeMoved)
        }
        save()
        status = "‘\(BookshelfPath.leaf(destination))’ 폴더 이동 완료"
        return true
    }

    @discardableResult
    func moveFolderOrder(_ folder: String, direction: Int) -> Bool {
        guard let reordered = BookshelfPath.moveSibling(catalog.folders, folder: folder, direction: direction) else {
            return false
        }
        catalog.folders = reordered
        save()
        return true
    }

    @discardableResult
    func deleteFolder(_ folder: String, includeSubfolders: Bool = true) -> Bool {
        let all = allDisplayFolders
        guard all.count > 1 else {
            status = "마지막 책장 폴더는 삭제할 수 없습니다."
            return false
        }
        let removedItems = catalog.items.filter {
            includeSubfolders ? BookshelfPath.contains(folder, $0.folder) : ($0.folder.caseInsensitiveCompare(folder) == .orderedSame)
        }
        let removedURLs = removedItems.map { imageURL(for: $0) }
        catalog.items.removeAll {
            includeSubfolders ? BookshelfPath.contains(folder, $0.folder) : ($0.folder.caseInsensitiveCompare(folder) == .orderedSame)
        }
        let liveURLs = Set(catalog.items.map { imageURL(for: $0).standardizedFileURL })
        for url in removedURLs where !liveURLs.contains(url.standardizedFileURL) {
            try? FileManager.default.removeItem(at: url)
        }
        catalog.folders.removeAll {
            includeSubfolders ? BookshelfPath.contains(folder, $0) : ($0.caseInsensitiveCompare(folder) == .orderedSame)
        }
        for key in Array(catalog.folderNotes.keys) where BookshelfPath.contains(folder, key) {
            catalog.folderNotes.removeValue(forKey: key)
            catalog.folderColumnCounts.removeValue(forKey: key)
            catalog.folderIds.removeValue(forKey: key)
        }
        if catalog.folders.isEmpty {
            catalog.folders = ["기본 책장"]
        }
        if BookshelfPath.contains(folder, catalog.activeFolder) {
            let parent = BookshelfPath.parent(folder)
            catalog.activeFolder = (!parent.isEmpty && allDisplayFolders.contains(parent)) ? parent : catalog.folders[0]
        }
        save()
        return true
    }

    func updateNote(_ note: String) {
        let clipped = String(note.prefix(4000))
        if catalog.folderNotes[catalog.activeFolder] == clipped { return }
        catalog.folderNotes[catalog.activeFolder] = clipped
        save()
    }

    func updateColumns(_ columns: Int) {
        let clamped = min(6, max(1, columns))
        catalog.folderColumnCounts[catalog.activeFolder] = clamped
        catalog.columnCount = clamped
        save()
    }

    private func nextNumberedFileName(ext: String) -> String {
        let cleanExt = ext.trimmingCharacters(in: CharacterSet(charactersIn: ".")).lowercased()
        let resolvedExt = cleanExt.isEmpty ? "png" : cleanExt
        let existingFiles = (try? FileManager.default.contentsOfDirectory(atPath: root.path)) ?? []
        var maxNumber = 0
        for name in existingFiles {
            let base = (name as NSString).deletingPathExtension
            if base.lowercased().hasPrefix("image"),
               let num = Int(base.dropFirst(5)), num > maxNumber {
                maxNumber = num
            }
        }
        var next = maxNumber + 1
        while FileManager.default.fileExists(atPath: root.appendingPathComponent("image\(next).\(resolvedExt)").path) {
            next += 1
        }
        return "image\(next).\(resolvedExt)"
    }

    @discardableResult
    func addImageData(_ data: Data, fileExtension ext: String = "png", toFolder folder: String? = nil, sourceUrl: String = "") -> BookshelfItem? {
        guard let uiImage = UIImage(data: data), uiImage.size.width > 0, uiImage.size.height > 0 else {
            status = "유효한 이미지 데이터가 아닙니다."
            return nil
        }
        let targetFolder = BookshelfPath.normalize(folder ?? catalog.activeFolder) ?? catalog.activeFolder
        if !catalog.folders.contains(where: { $0.caseInsensitiveCompare(targetFolder) == .orderedSame }) {
            catalog.folders.append(targetFolder)
        }
        do {
            let fileName: String
            if let accountRoot = SharedSyncFolderManager.shared.activeAccountRoot(for: DriveSyncManager.shared.connectedEmail) {
                let storedURL = try SharedSyncFolderManager.storeOriginal(data, fallbackExt: ext, in: accountRoot)
                fileName = storedURL.lastPathComponent
            } else {
                fileName = nextNumberedFileName(ext: ext)
                let targetURL = privateRoot.appendingPathComponent(fileName)
                try FileManager.default.createDirectory(at: privateRoot, withIntermediateDirectories: true)
                try data.write(to: targetURL, options: .atomic)
            }
            let pixelWidth = Int(uiImage.size.width * uiImage.scale)
            let pixelHeight = Int(uiImage.size.height * uiImage.scale)
            let nowMs = Int64(Date().timeIntervalSince1970 * 1000)
            let maxExistingMs = catalog.items.map { Self.normalizedEpochMs($0.createdAtEpochMs) }.max() ?? 0
            let item = BookshelfItem(
                folder: targetFolder,
                fileName: fileName,
                sourceUrl: sourceUrl,
                width: max(1, pixelWidth),
                height: max(1, pixelHeight),
                createdAtEpochMs: max(nowMs, maxExistingMs + 1)
            )
            catalog.items.append(item)
            save()
            status = "‘\(BookshelfPath.leaf(targetFolder))’ 책장에 이미지 추가됨"
            return item
        } catch {
            status = "이미지 저장 실패: \(error.localizedDescription)"
            return nil
        }
    }

    func moveImage(_ itemID: String, toFolder targetFolder: String) {
        guard let norm = BookshelfPath.normalize(targetFolder),
              let idx = catalog.items.firstIndex(where: { $0.id == itemID }) else { return }
        if !catalog.folders.contains(where: { $0.caseInsensitiveCompare(norm) == .orderedSame }) {
            catalog.folders.append(norm)
        }
        catalog.items[idx].folder = norm
        save()
        status = "사진을 ‘\(BookshelfPath.leaf(norm))’ 폴더로 이동했습니다."
    }

    func moveImageOrder(_ itemID: String, direction: Int) {
        let visible = visibleItems()
        guard let visIdx = visible.firstIndex(where: { $0.id == itemID }) else { return }
        let targetVisIdx = visIdx + direction
        guard visible.indices.contains(targetVisIdx) else { return }
        let otherID = visible[targetVisIdx].id
        guard let i1 = catalog.items.firstIndex(where: { $0.id == itemID }),
              let i2 = catalog.items.firstIndex(where: { $0.id == otherID }) else { return }
        let t1 = Self.normalizedEpochMs(catalog.items[i1].createdAtEpochMs)
        let t2 = Self.normalizedEpochMs(catalog.items[i2].createdAtEpochMs)
        if t1 == t2 {
            catalog.items[i1].createdAtEpochMs = direction < 0 ? max(0, t1 - 1) : (t1 + 1)
        } else {
            catalog.items[i1].createdAtEpochMs = t2
            catalog.items[i2].createdAtEpochMs = t1
        }
        catalog.items.swapAt(i1, i2)
        save()
    }

    func removeImage(_ itemID: String) {
        guard let idx = catalog.items.firstIndex(where: { $0.id == itemID }) else { return }
        let item = catalog.items[idx]
        let url = imageURL(for: item)
        catalog.items.remove(at: idx)
        let stillReferenced = catalog.items.contains { imageURL(for: $0).standardizedFileURL == url.standardizedFileURL }
        if !stillReferenced {
            try? FileManager.default.removeItem(at: url)
        }
        save()
    }

    func moveImages(_ ids: Set<String>, toFolder targetFolder: String) {
        guard let norm = BookshelfPath.normalize(targetFolder), !ids.isEmpty else { return }
        if !catalog.folders.contains(where: { $0.caseInsensitiveCompare(norm) == .orderedSame }) {
            catalog.folders.append(norm)
        }
        var count = 0
        for i in catalog.items.indices where ids.contains(catalog.items[i].id) {
            catalog.items[i].folder = norm
            count += 1
        }
        save()
        status = "\(count)개 사진을 ‘\(BookshelfPath.leaf(norm))’ 폴더로 이동했습니다."
    }

    func removeImages(_ ids: Set<String>) {
        guard !ids.isEmpty else { return }
        let targets = catalog.items.filter { ids.contains($0.id) }
        let urls = targets.map { imageURL(for: $0) }
        catalog.items.removeAll { ids.contains($0.id) }
        let liveURLs = Set(catalog.items.map { imageURL(for: $0).standardizedFileURL })
        for url in urls where !liveURLs.contains(url.standardizedFileURL) {
            try? FileManager.default.removeItem(at: url)
        }
        save()
        status = "\(targets.count)개 사진을 삭제했습니다."
    }

    func diskUsageBytes() -> Int64 {
        guard let files = try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: [.fileSizeKey]) else { return 0 }
        return files.reduce(Int64(0)) { acc, url in
            let size = (try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
            return acc + Int64(size)
        }
    }

    func localOriginalCount() -> Int {
        catalog.items.filter { FileManager.default.fileExists(atPath: imageURL(for: $0).path) }.count
    }

    func cleanupOrphanFiles() -> Int {
        let referenced = Set(catalog.items.map(\.fileName) + ["catalog.json"])
        guard let files = try? FileManager.default.contentsOfDirectory(atPath: root.path) else { return 0 }
        var removed = 0
        for name in files where !referenced.contains(name) {
            try? FileManager.default.removeItem(at: root.appendingPathComponent(name))
            removed += 1
        }
        status = removed > 0 ? "미사용 잔여 파일 \(removed)개를 정리했습니다." : "정리할 잔여 파일이 없습니다."
        return removed
    }

    func exportPDF(landscape: Bool) -> URL? {
        let items = visibleItems()
        guard !items.isEmpty else {
            status = "내보낼 이미지가 없습니다."
            return nil
        }
        let cols = max(1, activeColumns)
        let pageSize = landscape ? CGSize(width: 842, height: 595) : CGSize(width: 595, height: 842)
        let colWidth = pageSize.width / CGFloat(cols)
        var colHeights = Array(repeating: CGFloat.zero, count: cols)
        var placements: [(BookshelfItem, CGRect)] = []

        for item in items {
            let col = colHeights.enumerated().min(by: { $0.element < $1.element })?.offset ?? 0
            let aspect = CGFloat(max(1, item.height)) / CGFloat(max(1, item.width))
            let drawHeight = colWidth * aspect
            let rect = CGRect(x: CGFloat(col) * colWidth, y: colHeights[col], width: colWidth, height: drawHeight)
            colHeights[col] += drawHeight
            placements.append((item, rect))
        }

        let totalHeight = max(pageSize.height, colHeights.max() ?? pageSize.height)
        let pageCount = max(1, Int(ceil(totalHeight / pageSize.height)))
        let safeLeaf = BookshelfPath.leaf(activeFolder).replacingOccurrences(of: "/", with: "_")
        let output = FileManager.default.temporaryDirectory.appendingPathComponent("Omyaru-\(safeLeaf)-\(UUID().uuidString.prefix(6)).pdf")
        let renderer = UIGraphicsPDFRenderer(bounds: CGRect(origin: .zero, size: pageSize))
        do {
            try renderer.writePDF(to: output) { context in
                for page in 0..<pageCount {
                    context.beginPage()
                    let slice = CGRect(x: 0, y: CGFloat(page) * pageSize.height, width: pageSize.width, height: pageSize.height)
                    for (item, rect) in placements where rect.intersects(slice) {
                        autoreleasepool {
                            if let img = UIImage(contentsOfFile: imageURL(for: item).path) {
                                img.draw(in: rect.offsetBy(dx: 0, dy: -slice.minY))
                            }
                        }
                    }
                }
            }
            return output
        } catch {
            status = "PDF 생성 실패: \(error.localizedDescription)"
            return nil
        }
    }
}

// MARK: - Main Bookshelf View

struct BookshelfView: View {
    @Environment(\.dismiss) private var dismiss
    @Environment(\.scenePhase) private var scenePhase
    @ObservedObject var model: ShelfModel = .shared
    @ObservedObject var bookshelfLock: BookshelfLockManager = .shared
    @State private var selectedPhotos: [PhotosPickerItem] = []
    @State private var showFilePicker = false
    @State private var showFolderTree = false
    @State private var movingFolderPath: String? = nil
    @State private var movingImageItem: BookshelfItem? = nil
    @State private var showFullscreen = false
    @State private var inspectedItem: BookshelfItem? = nil
    @State private var showNewFolderAlert = false
    @State private var newFolderParent: String = ""
    @State private var newFolderName = ""
    @State private var renamingFolderPath: String? = nil
    @State private var renameInput = ""
    @State private var deletingFolderPath: String? = nil
    @State private var pdfURL: URL? = nil
    @State private var showPDFOptions = false
    @State private var showSyncSheet = false
    @State private var showStorageSheet = false
    @State private var showLockSettings = false
    @State private var selectionMode = false
    @State private var selectedItemIDs: Set<String> = []
    @State private var showBatchMoveSheet = false
    @State private var noteText = ""

    var body: some View {
        ZStack {
        NavigationStack {
            VStack(spacing: 0) {
                folderBreadcrumbBar
                noteAndColumnBar
                galleryBody
            }
            .background(Color(red: 0.07, green: 0.07, blue: 0.08))
            .navigationTitle(BookshelfPath.leaf(model.activeFolder))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button("완료") { dismiss() }
                }
                ToolbarItemGroup(placement: .topBarTrailing) {
                    Button {
                        showSyncSheet = true
                    } label: {
                        Image(systemName: "arrow.triangle.2.circlepath.icloud")
                    }
                    PhotosPicker(selection: $selectedPhotos, maxSelectionCount: 0, matching: .images) {
                        Image(systemName: "photo.badge.plus")
                    }
                    Menu {
                        Button {
                            showFilePicker = true
                        } label: {
                            Label("파일에서 사진 여러 장 추가", systemImage: "folder.badge.plus")
                        }
                        Button {
                            if let img = UIPasteboard.general.image, let data = img.pngData() {
                                _ = model.addImageData(data, fileExtension: "png")
                            } else {
                                model.status = "클립보드에 이미지가 없습니다."
                            }
                        } label: {
                            Label("클립보드 이미지 붙여넣기", systemImage: "doc.on.clipboard")
                        }
                        Divider()
                        Button {
                            newFolderParent = ""
                            newFolderName = ""
                            showNewFolderAlert = true
                        } label: {
                            Label("새 최상위 폴더 만들기", systemImage: "folder.badge.plus")
                        }
                        Button {
                            newFolderParent = model.activeFolder
                            newFolderName = ""
                            showNewFolderAlert = true
                        } label: {
                            Label("현재 폴더에 하위 폴더 만들기", systemImage: "plus.rectangle.on.folder")
                        }
                        Button {
                            renamingFolderPath = model.activeFolder
                            renameInput = BookshelfPath.leaf(model.activeFolder)
                        } label: {
                            Label("현재 폴더 이름 변경", systemImage: "pencil")
                        }
                        Button {
                            movingFolderPath = model.activeFolder
                            showFolderTree = true
                        } label: {
                            Label("현재 폴더 다른 위치로 이동", systemImage: "folder.badge.gearshape")
                        }
                        Divider()
                        Button {
                            showFullscreen = true
                        } label: {
                            Label("전체 화면 보기", systemImage: "arrow.up.left.and.arrow.down.right")
                        }
                        Button {
                            selectionMode.toggle()
                            if !selectionMode { selectedItemIDs.removeAll() }
                        } label: {
                            Label(selectionMode ? "다중 선택 종료" : "사진 다중 선택 (이동/삭제)", systemImage: "checkmark.circle")
                        }
                        Button {
                            showPDFOptions = true
                        } label: {
                            Label("PDF로 내보내기", systemImage: "doc.richtext")
                        }
                        Button {
                            showStorageSheet = true
                        } label: {
                            Label("책장 저장 공간 관리", systemImage: "externaldrive")
                        }
                        Button {
                            showLockSettings = true
                        } label: {
                            Label("책장 잠금 설정", systemImage: "lock.shield")
                        }
                        if bookshelfLock.isEnabled {
                            Button {
                                bookshelfLock.lockNow()
                            } label: {
                                Label("지금 책장 잠그기", systemImage: "lock.fill")
                            }
                        }
                        Divider()
                        Button(role: .destructive) {
                            deletingFolderPath = model.activeFolder
                        } label: {
                            Label("현재 폴더 삭제", systemImage: "trash")
                        }
                    } label: {
                        Image(systemName: "ellipsis.circle")
                    }
                }
            }
            .fileImporter(isPresented: $showFilePicker, allowedContentTypes: [.image], allowsMultipleSelection: true) { result in
                if case let .success(urls) = result {
                    for url in urls {
                        let access = url.startAccessingSecurityScopedResource()
                        defer { if access { url.stopAccessingSecurityScopedResource() } }
                        if let data = try? Data(contentsOf: url) {
                            _ = model.addImageData(data, fileExtension: url.pathExtension.isEmpty ? "png" : url.pathExtension)
                        }
                    }
                }
            }
            .onChange(of: selectedPhotos) { photos in
                guard !photos.isEmpty else { return }
                Task {
                    for photo in photos {
                        if let data = try? await photo.loadTransferable(type: Data.self) {
                            _ = model.addImageData(data, fileExtension: "jpg")
                        }
                    }
                    selectedPhotos = []
                }
            }
            .onChange(of: model.activeFolder) { _ in
                noteText = model.activeNote
            }
            .onAppear {
                noteText = model.activeNote
            }
            .sheet(isPresented: $showFolderTree, onDismiss: {
                movingFolderPath = nil
            }) {
                BookshelfFolderTreeSheet(
                    model: model,
                    movingFolder: movingFolderPath,
                    onRequestCreateChild: { parent in
                        showFolderTree = false
                        DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) {
                            newFolderParent = parent
                            newFolderName = ""
                            showNewFolderAlert = true
                        }
                    },
                    onRequestRename: { target in
                        showFolderTree = false
                        DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) {
                            renamingFolderPath = target
                            renameInput = BookshelfPath.leaf(target)
                        }
                    },
                    onRequestMove: { target in
                        movingFolderPath = target
                    },
                    onRequestDelete: { target in
                        showFolderTree = false
                        DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) {
                            deletingFolderPath = target
                        }
                    }
                )
            }
            .sheet(item: $movingImageItem) { item in
                BookshelfImageMoveSheet(model: model, item: item)
            }
            .sheet(item: $inspectedItem) { item in
                BookshelfSingleImageViewer(model: model, item: item)
            }
            .sheet(isPresented: $showSyncSheet) {
                DriveSyncSheet(browser: nil, shelf: model)
            }
            .sheet(isPresented: $showStorageSheet) {
                BookshelfStorageSheet(model: model)
            }
            .sheet(isPresented: $showLockSettings) {
                BookshelfLockSettingsSheet()
            }
            .sheet(isPresented: $showBatchMoveSheet) {
                BookshelfBatchMoveSheet(model: model, itemIDs: selectedItemIDs) {
                    selectedItemIDs.removeAll()
                    selectionMode = false
                }
            }
            .fullScreenCover(isPresented: $showFullscreen) {
                fullscreenGallery
            }
            .confirmationDialog("PDF 내보내기 방향", isPresented: $showPDFOptions, titleVisibility: .visible) {
                Button("세로 (9:16)") { pdfURL = model.exportPDF(landscape: false) }
                Button("가로 (16:9)") { pdfURL = model.exportPDF(landscape: true) }
                Button("취소", role: .cancel) {}
            }
            .sheet(isPresented: Binding(get: { pdfURL != nil }, set: { if !$0 { pdfURL = nil } })) {
                if let url = pdfURL {
                    NavigationStack {
                        VStack(spacing: 20) {
                            Image(systemName: "doc.richtext.fill")
                                .font(.system(size: 52))
                                .foregroundStyle(.blue)
                            Text(url.lastPathComponent)
                                .font(.headline)
                            ShareLink(item: url) {
                                Label("PDF 공유 또는 파일에 저장", systemImage: "square.and.arrow.up")
                                    .frame(maxWidth: .infinity)
                                    .padding(.vertical, 12)
                            }
                            .buttonStyle(.borderedProminent)
                        }
                        .padding(24)
                        .navigationTitle("PDF 내보내기 완료")
                        .toolbar {
                            ToolbarItem(placement: .cancellationAction) {
                                Button("닫기") { pdfURL = nil }
                            }
                        }
                    }
                    .presentationDetents([.medium])
                }
            }
            .alert(newFolderParent.isEmpty ? "새 책장 폴더" : "‘\(BookshelfPath.leaf(newFolderParent))’의 하위 폴더 만들기",
                   isPresented: $showNewFolderAlert) {
                TextField("폴더 이름", text: $newFolderName)
                Button("만들기") {
                    let trimmed = newFolderName.trimmingCharacters(in: .whitespacesAndNewlines)
                    guard !trimmed.isEmpty else { return }
                    let path = newFolderParent.isEmpty ? trimmed : "\(newFolderParent)/\(trimmed)"
                    _ = model.addFolder(path, select: true)
                    newFolderName = ""
                }
                Button("취소", role: .cancel) { newFolderName = "" }
            }
            .alert("폴더 이름 변경", isPresented: Binding(get: { renamingFolderPath != nil }, set: { if !$0 { renamingFolderPath = nil } })) {
                TextField("새 폴더 이름", text: $renameInput)
                Button("변경") {
                    guard let target = renamingFolderPath else { return }
                    let leaf = renameInput.trimmingCharacters(in: .whitespacesAndNewlines)
                    guard !leaf.isEmpty, !leaf.contains("/") else { return }
                    let parent = BookshelfPath.parent(target)
                    let dest = parent.isEmpty ? leaf : "\(parent)/\(leaf)"
                    _ = model.renameOrMoveFolder(from: target, to: dest)
                    renamingFolderPath = nil
                }
                Button("취소", role: .cancel) { renamingFolderPath = nil }
            }
            .alert("폴더 삭제 확인", isPresented: Binding(get: { deletingFolderPath != nil }, set: { if !$0 { deletingFolderPath = nil } })) {
                Button("삭제", role: .destructive) {
                    if let target = deletingFolderPath {
                        _ = model.deleteFolder(target, includeSubfolders: true)
                    }
                    deletingFolderPath = nil
                }
                Button("취소", role: .cancel) { deletingFolderPath = nil }
            } message: {
                if let target = deletingFolderPath {
                    let count = model.visibleItems(in: target).count
                    Text("‘\(target)’ 폴더와 하위 폴더, 포함된 이미지 \(count)개를 모두 삭제할까요?")
                }
            }
        }

        if bookshelfLock.isCurrentlyLocked {
            BookshelfLockOverlayView {
                dismiss()
            }
            .zIndex(50)
        } else if scenePhase != .active && bookshelfLock.isEnabled && !(scenePhase == .inactive && bookshelfLock.suppressInactiveShield) {
            Color.black
                .ignoresSafeArea()
                .overlay {
                    Image(systemName: "lock.rectangle.stack.fill")
                        .font(.system(size: 52))
                        .foregroundStyle(.blue)
                }
                .zIndex(60)
        }
        }
        .onChange(of: scenePhase) { phase in
            if phase == .active || phase == .background {
                bookshelfLock.clearBiometricGrace()
            }
        }
        .onAppear {
            bookshelfLock.onBookshelfOpened()
        }
        .onDisappear {
            bookshelfLock.onBookshelfClosed()
        }
        .preferredColorScheme(.dark)
    }

    // MARK: - Subviews

    private var folderBreadcrumbBar: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 6) {
                Button {
                    movingFolderPath = nil
                    showFolderTree = true
                } label: {
                    HStack(spacing: 5) {
                        Image(systemName: "sidebar.left")
                        Text("폴더 트리")
                        Image(systemName: "chevron.down").font(.caption2)
                    }
                    .font(.subheadline.weight(.semibold))
                    .padding(.horizontal, 12)
                    .padding(.vertical, 7)
                    .background(Color(red: 0.17, green: 0.17, blue: 0.19), in: Capsule())
                }
                .buttonStyle(.plain)

                let segments = breadcrumbPaths(for: model.activeFolder)
                ForEach(Array(segments.enumerated()), id: \.element) { idx, path in
                    if idx > 0 {
                        Image(systemName: "chevron.right")
                            .font(.caption2)
                            .foregroundStyle(.secondary)
                    }
                    let isCurrent = idx == segments.count - 1
                    Button {
                        if isCurrent {
                            movingFolderPath = nil
                            showFolderTree = true
                        } else {
                            model.selectFolder(path)
                        }
                    } label: {
                        HStack(spacing: 4) {
                            Image(systemName: isCurrent ? "folder.fill" : "folder")
                                .foregroundStyle(isCurrent ? .white : .blue)
                            Text(BookshelfPath.leaf(path))
                                .lineLimit(1)
                            Text("(\(model.visibleItems(in: path).count))")
                                .font(.caption2)
                                .foregroundStyle(isCurrent ? .white.opacity(0.8) : .secondary)
                        }
                        .font(.subheadline.weight(isCurrent ? .semibold : .regular))
                        .padding(.horizontal, 11)
                        .padding(.vertical, 7)
                        .background(isCurrent ? Color.blue : Color(red: 0.15, green: 0.15, blue: 0.17), in: Capsule())
                    }
                    .buttonStyle(.plain)
                }
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
        }
        .background(Color(red: 0.11, green: 0.11, blue: 0.12))
    }

    private var noteAndColumnBar: some View {
        VStack(spacing: 6) {
            HStack(spacing: 8) {
                HStack(spacing: 6) {
                    Text("메모")
                        .font(.caption.weight(.semibold))
                        .foregroundStyle(.secondary)
                        .padding(.horizontal, 8)
                        .padding(.vertical, 4)
                        .background(Color(red: 0.18, green: 0.18, blue: 0.20), in: Capsule())
                    TextField("폴더 메모 입력...", text: $noteText)
                        .font(.subheadline)
                        .onChange(of: noteText) { val in model.updateNote(val) }
                }
                .padding(.horizontal, 10)
                .padding(.vertical, 7)
                .background(Color(red: 0.13, green: 0.13, blue: 0.15), in: RoundedRectangle(cornerRadius: 10))

                HStack(spacing: 0) {
                    Button {
                        model.updateColumns(model.activeColumns - 1)
                    } label: {
                        Image(systemName: "minus")
                            .frame(width: 30, height: 32)
                    }
                    .disabled(model.activeColumns <= 1)

                    Text("\(model.activeColumns)열")
                        .font(.caption.weight(.semibold))
                        .frame(minWidth: 32)

                    Button {
                        model.updateColumns(model.activeColumns + 1)
                    } label: {
                        Image(systemName: "plus")
                            .frame(width: 30, height: 32)
                    }
                    .disabled(model.activeColumns >= 6)
                }
                .background(Color(red: 0.16, green: 0.16, blue: 0.18), in: RoundedRectangle(cornerRadius: 10))
            }
            if !model.status.isEmpty {
                Text(model.status)
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
        .padding(.horizontal, 12)
        .padding(.bottom, 8)
        .background(Color(red: 0.11, green: 0.11, blue: 0.12))
    }

    private var galleryBody: some View {
        let items = model.visibleItems()
        let cols = model.activeColumns
        return VStack(spacing: 0) {
            if selectionMode {
                HStack(spacing: 10) {
                    Text("\(selectedItemIDs.count)개 선택됨")
                        .font(.subheadline.weight(.semibold))
                    Spacer()
                    Button("모두 선택") {
                        selectedItemIDs = Set(items.map(\.id))
                    }
                    .font(.caption.weight(.semibold))
                    Button("폴더 이동") {
                        showBatchMoveSheet = true
                    }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.small)
                    .disabled(selectedItemIDs.isEmpty)
                    Button("삭제", role: .destructive) {
                        model.removeImages(selectedItemIDs)
                        selectedItemIDs.removeAll()
                        selectionMode = false
                    }
                    .buttonStyle(.bordered)
                    .controlSize(.small)
                    .disabled(selectedItemIDs.isEmpty)
                }
                .padding(.horizontal, 12)
                .padding(.vertical, 8)
                .background(Color.blue.opacity(0.16))
            }

            if items.isEmpty {
                VStack(spacing: 14) {
                    Spacer()
                    Image(systemName: "photo.on.rectangle.angled")
                        .font(.system(size: 46))
                        .foregroundStyle(.secondary)
                    Text("‘\(BookshelfPath.leaf(model.activeFolder))’ 폴더가 비어 있습니다")
                        .font(.headline)
                    Text("우측 상단 사진 추가 버튼이나 브라우저 실시간 영역 캡처로 이미지를 넣어보세요.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                        .padding(.horizontal, 32)
                    Spacer()
                }
            } else {
                GeometryReader { geo in
                    let colWidth = max(40, geo.size.width / CGFloat(cols))
                    let distributed = distributeIntoColumns(items: items, columnCount: cols)
                    ScrollViewReader { proxy in
                        ZStack(alignment: .bottomTrailing) {
                            ScrollView {
                                Color.clear.frame(height: 1).id("__bookshelf_top__")
                                HStack(alignment: .top, spacing: 0) {
                                    ForEach(0..<cols, id: \.self) { colIndex in
                                        LazyVStack(spacing: 0) {
                                            ForEach(distributed[colIndex]) { item in
                                                BookshelfPhotoTile(
                                                    model: model,
                                                    item: item,
                                                    width: colWidth,
                                                    selectionMode: selectionMode,
                                                    isSelected: selectedItemIDs.contains(item.id),
                                                    onTap: {
                                                        if selectionMode {
                                                            if selectedItemIDs.contains(item.id) { selectedItemIDs.remove(item.id) }
                                                            else { selectedItemIDs.insert(item.id) }
                                                        } else {
                                                            inspectedItem = item
                                                        }
                                                    },
                                                    onMoveFolder: { movingImageItem = item }
                                                )
                                            }
                                        }
                                        .frame(width: colWidth)
                                    }
                                }
                                Color.clear.frame(height: 1).id("__bookshelf_bottom__")
                            }

                            if items.count >= 8 {
                                VStack(spacing: 6) {
                                    Button {
                                        withAnimation(.easeOut(duration: 0.25)) {
                                            proxy.scrollTo("__bookshelf_top__", anchor: .top)
                                        }
                                    } label: {
                                        Image(systemName: "chevron.up")
                                            .font(.caption.weight(.bold))
                                            .frame(width: 34, height: 34)
                                            .background(.ultraThinMaterial, in: Circle())
                                    }
                                    Button {
                                        withAnimation(.easeOut(duration: 0.25)) {
                                            proxy.scrollTo("__bookshelf_bottom__", anchor: .bottom)
                                        }
                                    } label: {
                                        Image(systemName: "chevron.down")
                                            .font(.caption.weight(.bold))
                                            .frame(width: 34, height: 34)
                                            .background(.ultraThinMaterial, in: Circle())
                                    }
                                }
                                .buttonStyle(.plain)
                                .padding(12)
                            }
                        }
                    }
                }
            }
        }
    }

    private var fullscreenGallery: some View {
        let items = model.visibleItems()
        let cols = model.activeColumns
        return ZStack(alignment: .topTrailing) {
            Color.black.ignoresSafeArea()
            GeometryReader { geo in
                let colWidth = max(40, geo.size.width / CGFloat(cols))
                let distributed = distributeIntoColumns(items: items, columnCount: cols)
                ScrollView {
                    HStack(alignment: .top, spacing: 0) {
                        ForEach(0..<cols, id: \.self) { colIndex in
                            LazyVStack(spacing: 0) {
                                ForEach(distributed[colIndex]) { item in
                                    BookshelfPhotoTile(
                                        model: model,
                                        item: item,
                                        width: colWidth,
                                        selectionMode: false,
                                        isSelected: false,
                                        onTap: { inspectedItem = item },
                                        onMoveFolder: { movingImageItem = item }
                                    )
                                }
                            }
                            .frame(width: colWidth)
                        }
                    }
                }
                .ignoresSafeArea()
            }
            Button {
                showFullscreen = false
            } label: {
                Image(systemName: "xmark.circle.fill")
                    .font(.system(size: 30))
                    .foregroundStyle(.white.opacity(0.85))
                    .padding(18)
            }
        }
    }

    private func breadcrumbPaths(for active: String) -> [String] {
        var list: [String] = []
        var cur = active
        while !cur.isEmpty {
            list.append(cur)
            cur = BookshelfPath.parent(cur)
        }
        return list.reversed()
    }

    private func distributeIntoColumns(items: [BookshelfItem], columnCount: Int) -> [[BookshelfItem]] {
        let count = max(1, columnCount)
        var columns = Array(repeating: [BookshelfItem](), count: count)
        var heights = Array(repeating: Double.zero, count: count)
        for item in items {
            let shortest = heights.enumerated().min(by: { $0.element < $1.element })?.offset ?? 0
            columns[shortest].append(item)
            heights[shortest] += Double(max(1, item.height)) / Double(max(1, item.width))
        }
        return columns
    }
}

// MARK: - Photo Tile with Context Menu

private struct BookshelfPhotoTile: View {
    @ObservedObject var model: ShelfModel
    let item: BookshelfItem
    let width: CGFloat
    var selectionMode: Bool = false
    var isSelected: Bool = false
    let onTap: () -> Void
    let onMoveFolder: () -> Void

    @State private var loadedImage: UIImage? = nil
    @State private var downloading = false

    var body: some View {
        let url = model.imageURL(for: item)
        let aspect = CGFloat(max(1, item.height)) / CGFloat(max(1, item.width))
        ZStack(alignment: .topTrailing) {
            Group {
                if let image = loadedImage ?? UIImage(contentsOfFile: url.path) {
                    Image(uiImage: image)
                        .resizable()
                        .scaledToFit()
                        .frame(width: width)
                } else {
                    Rectangle()
                        .fill(Color.white.opacity(0.06))
                        .frame(width: width, height: max(80, width * aspect))
                        .overlay {
                            VStack(spacing: 6) {
                                if downloading {
                                    ProgressView()
                                } else {
                                    Image(systemName: "icloud.and.arrow.down")
                                        .font(.title3)
                                        .foregroundStyle(.blue)
                                    Text(item.fileName).font(.caption2).foregroundStyle(.secondary)
                                }
                            }
                        }
                }
            }

            if selectionMode {
                Image(systemName: isSelected ? "checkmark.circle.fill" : "circle")
                    .font(.title3)
                    .foregroundStyle(isSelected ? .blue : .white)
                    .shadow(radius: 3)
                    .padding(6)
            }
        }
        .overlay(
            Rectangle().strokeBorder(selectionMode && isSelected ? Color.blue : Color.clear, lineWidth: 3)
        )
        .contentShape(Rectangle())
        .onTapGesture { onTap() }
        .task(id: item.id) {
            if let existing = UIImage(contentsOfFile: url.path) {
                loadedImage = existing
                return
            }
            if item.cloud != nil {
                downloading = true
                defer { downloading = false }
                if let downloadedURL = await DriveSyncManager.shared.downloadCloudImageIfNeeded(item: item, shelf: model),
                   let img = UIImage(contentsOfFile: downloadedURL.path) {
                    loadedImage = img
                }
            }
        }
        .contextMenu {
            Button {
                onTap()
            } label: {
                Label("크게 보기", systemImage: "magnifyingglass")
            }
            Button {
                onMoveFolder()
            } label: {
                Label("다른 폴더로 이동...", systemImage: "folder")
            }
            Button {
                model.moveImageOrder(item.id, direction: -1)
            } label: {
                Label("앞으로 이동", systemImage: "arrow.up")
            }
            Button {
                model.moveImageOrder(item.id, direction: 1)
            } label: {
                Label("뒤로 이동", systemImage: "arrow.down")
            }
            if let image = UIImage(contentsOfFile: url.path) {
                Button {
                    UIPasteboard.general.image = image
                    model.status = "이미지를 클립보드에 복사했습니다."
                } label: {
                    Label("이미지 복사", systemImage: "doc.on.doc")
                }
                Button {
                    UIImageWriteToSavedPhotosAlbum(image, nil, nil, nil)
                    model.status = "사진 앱에 저장했습니다."
                } label: {
                    Label("사진 앱에 저장", systemImage: "square.and.arrow.down")
                }
            }
            Divider()
            Button(role: .destructive) {
                model.removeImage(item.id)
            } label: {
                Label("삭제", systemImage: "trash")
            }
        }
    }
}

// MARK: - Hierarchical Folder Tree Sheet (With Explicit Confirm Button for Moving!)

struct BookshelfFolderTreeSheet: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var model: ShelfModel
    var movingFolder: String?
    var onRequestCreateChild: (String) -> Void
    var onRequestRename: (String) -> Void
    var onRequestMove: (String) -> Void
    var onRequestDelete: (String) -> Void

    @State private var selectedTargetParent: String = ""

    private var isMoveMode: Bool {
        movingFolder != nil && !(movingFolder?.isEmpty ?? true)
    }

    private var availableFolders: [String] {
        let all = model.allDisplayFolders
        guard let moving = movingFolder, !moving.isEmpty else { return all }
        return all.filter { !BookshelfPath.contains(moving, $0) }
    }

    private func isVisible(_ path: String) -> Bool {
        var p = BookshelfPath.parent(path)
        while !p.isEmpty {
            if !model.isFolderExpanded(p) { return false }
            p = BookshelfPath.parent(p)
        }
        return true
    }

    private func confirmMove(toParent parentPath: String) {
        guard let moving = movingFolder, !moving.isEmpty else { return }
        let leaf = BookshelfPath.leaf(moving)
        let target = parentPath.isEmpty ? leaf : "\(parentPath)/\(leaf)"
        if target.caseInsensitiveCompare(moving) == .orderedSame {
            dismiss()
            return
        }
        if model.renameOrMoveFolder(from: moving, to: target) {
            dismiss()
        }
    }

    var body: some View {
        NavigationStack {
            VStack(spacing: 0) {
                if let moving = movingFolder, !moving.isEmpty {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("‘\(BookshelfPath.leaf(moving))’ 폴더를 이동할 위치를 선택하세요")
                            .font(.subheadline.weight(.semibold))
                        Text("폴더를 선택한 뒤 하단 또는 우측 상단의 [확인 (여기로 이동)] 버튼을 누르면 이동됩니다.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(12)
                    .background(Color.blue.opacity(0.15))
                }

                List {
                    if isMoveMode {
                        Button {
                            selectedTargetParent = ""
                        } label: {
                            HStack {
                                Image(systemName: selectedTargetParent.isEmpty ? "checkmark.circle.fill" : "folder.fill")
                                    .foregroundStyle(selectedTargetParent.isEmpty ? .blue : .secondary)
                                Text("책장 (최상위로 이동)")
                                    .font(.body.weight(selectedTargetParent.isEmpty ? .semibold : .regular))
                                Spacer()
                                Button("여기로 이동") {
                                    confirmMove(toParent: "")
                                }
                                .buttonStyle(.borderedProminent)
                                .controlSize(.small)
                            }
                        }
                        .buttonStyle(.plain)
                    }

                    ForEach(availableFolders.filter { isVisible($0) }, id: \.self) { path in
                        folderRow(for: path)
                    }
                }
                .listStyle(.plain)

                // Bottom explicit confirmation bar (always visible!)
                HStack(spacing: 10) {
                    Button {
                        onRequestCreateChild(isMoveMode ? selectedTargetParent : model.activeFolder)
                    } label: {
                        Label("새 폴더", systemImage: "folder.badge.plus")
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 10)
                    }
                    .buttonStyle(.bordered)

                    if isMoveMode {
                        Button {
                            confirmMove(toParent: selectedTargetParent)
                        } label: {
                            Label(
                                selectedTargetParent.isEmpty
                                    ? "확인 (최상위로 이동)"
                                    : "확인 (‘\(BookshelfPath.leaf(selectedTargetParent))’(으)로 이동)",
                                systemImage: "checkmark.circle.fill"
                            )
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 10)
                        }
                        .buttonStyle(.borderedProminent)
                    }
                }
                .padding(12)
                .background(Color(red: 0.11, green: 0.11, blue: 0.13))
            }
            .navigationTitle(isMoveMode ? "이동할 폴더 선택" : "책장 폴더 트리")
            .navigationBarTitleDisplayMode(.inline)
            .onAppear {
                if isMoveMode, let moving = movingFolder {
                    selectedTargetParent = BookshelfPath.parent(moving)
                } else {
                    selectedTargetParent = model.activeFolder
                }
            }
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("닫기") { dismiss() }
                }
                if isMoveMode {
                    ToolbarItem(placement: .confirmationAction) {
                        Button("확인 (여기로 이동)") {
                            confirmMove(toParent: selectedTargetParent)
                        }
                        .font(.body.weight(.semibold))
                    }
                }
            }
        }
        .preferredColorScheme(.dark)
    }

    @ViewBuilder
    private func folderRow(for path: String) -> some View {
        let level = path.filter { $0 == "/" }.count
        let leaf = BookshelfPath.leaf(path)
        let children = availableFolders.filter { BookshelfPath.parent($0).caseInsensitiveCompare(path) == .orderedSame }
        let hasChildren = !children.isEmpty
        let isExpanded = model.isFolderExpanded(path)
        let isSelected = isMoveMode
            ? (selectedTargetParent.caseInsensitiveCompare(path) == .orderedSame)
            : (model.activeFolder.caseInsensitiveCompare(path) == .orderedSame)
        let count = model.visibleItems(in: path).count

        HStack(spacing: 8) {
            if level > 0 {
                Spacer().frame(width: CGFloat(level) * 18)
            }
            if hasChildren {
                Button {
                    model.toggleFolderExpanded(path)
                } label: {
                    Image(systemName: isExpanded ? "chevron.down" : "chevron.right")
                        .font(.caption.weight(.bold))
                        .foregroundStyle(.secondary)
                        .frame(width: 24, height: 24)
                }
                .buttonStyle(.plain)
            } else {
                Spacer().frame(width: 24)
            }

            Button {
                if isMoveMode {
                    selectedTargetParent = path
                } else {
                    model.selectFolder(path)
                    dismiss()
                }
            } label: {
                HStack(spacing: 8) {
                    Image(systemName: isSelected ? "folder.fill" : "folder")
                        .foregroundStyle(isSelected ? .blue : .secondary)
                    Text(leaf)
                        .font(.body.weight(isSelected ? .semibold : .regular))
                        .lineLimit(1)
                    Text("(\(count))")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Spacer()
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)

            if isMoveMode {
                Button("여기로 이동") {
                    confirmMove(toParent: path)
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.small)
            } else {
                Menu {
                    Button {
                        onRequestCreateChild(path)
                    } label: {
                        Label("하위 폴더 만들기", systemImage: "folder.badge.plus")
                    }
                    Button {
                        onRequestRename(path)
                    } label: {
                        Label("이름 변경", systemImage: "pencil")
                    }
                    Button {
                        onRequestMove(path)
                    } label: {
                        Label("다른 폴더로 이동", systemImage: "folder.badge.gearshape")
                    }
                    Button {
                        _ = model.moveFolderOrder(path, direction: -1)
                    } label: {
                        Label("위로 이동", systemImage: "arrow.up")
                    }
                    Button {
                        _ = model.moveFolderOrder(path, direction: 1)
                    } label: {
                        Label("아래로 이동", systemImage: "arrow.down")
                    }
                    Divider()
                    Button(role: .destructive) {
                        onRequestDelete(path)
                    } label: {
                        Label("폴더 삭제", systemImage: "trash")
                    }
                } label: {
                    Image(systemName: "ellipsis.circle")
                        .foregroundStyle(.secondary)
                        .padding(.horizontal, 4)
                }
            }
        }
        .padding(.vertical, 4)
        .listRowBackground(isSelected ? Color.blue.opacity(0.16) : Color.clear)
    }
}

// MARK: - Move Image to Another Folder Sheet (With Confirm Button)

struct BookshelfImageMoveSheet: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var model: ShelfModel
    let item: BookshelfItem
    @State private var selectedFolder: String = ""

    var body: some View {
        NavigationStack {
            VStack(spacing: 0) {
                List(model.allDisplayFolders, id: \.self) { folder in
                    let level = folder.filter { $0 == "/" }.count
                    let isSelected = selectedFolder.caseInsensitiveCompare(folder) == .orderedSame
                    HStack {
                        if level > 0 { Spacer().frame(width: CGFloat(level) * 18) }
                        Button {
                            selectedFolder = folder
                        } label: {
                            HStack {
                                Image(systemName: isSelected ? "folder.fill" : "folder")
                                    .foregroundStyle(isSelected ? .blue : .secondary)
                                Text(BookshelfPath.leaf(folder))
                                    .font(.body.weight(isSelected ? .semibold : .regular))
                                Spacer()
                            }
                        }
                        .buttonStyle(.plain)

                        Button("여기로 이동") {
                            model.moveImage(item.id, toFolder: folder)
                            dismiss()
                        }
                        .buttonStyle(.borderedProminent)
                        .controlSize(.small)
                    }
                }
                .listStyle(.plain)

                Button {
                    guard !selectedFolder.isEmpty else { return }
                    model.moveImage(item.id, toFolder: selectedFolder)
                    dismiss()
                } label: {
                    Label("확인 (‘\(BookshelfPath.leaf(selectedFolder))’(으)로 이동)", systemImage: "checkmark.circle.fill")
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 10)
                }
                .buttonStyle(.borderedProminent)
                .padding(12)
            }
            .navigationTitle("사진 이동할 폴더 선택")
            .navigationBarTitleDisplayMode(.inline)
            .onAppear { selectedFolder = item.folder }
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("취소") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("확인") {
                        model.moveImage(item.id, toFolder: selectedFolder)
                        dismiss()
                    }
                }
            }
        }
        .preferredColorScheme(.dark)
    }
}

// MARK: - Single Image Zoomable Viewer

struct BookshelfSingleImageViewer: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var model: ShelfModel
    let item: BookshelfItem
    @State private var scale: CGFloat = 1.0

    var body: some View {
        NavigationStack {
            ZStack {
                Color.black.ignoresSafeArea()
                if let uiImage = UIImage(contentsOfFile: model.imageURL(for: item).path) {
                    ScrollView([.horizontal, .vertical], showsIndicators: false) {
                        Image(uiImage: uiImage)
                            .resizable()
                            .scaledToFit()
                            .frame(width: UIScreen.main.bounds.width * scale)
                    }
                    .gesture(
                        MagnificationGesture()
                            .onChanged { val in scale = min(4.0, max(1.0, val)) }
                    )
                    .onTapGesture(count: 2) {
                        withAnimation(.spring()) { scale = scale > 1.1 ? 1.0 : 2.0 }
                    }
                }
            }
            .navigationTitle(item.fileName)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("닫기") { dismiss() }
                }
                ToolbarItemGroup(placement: .topBarTrailing) {
                    if let uiImage = UIImage(contentsOfFile: model.imageURL(for: item).path) {
                        Button {
                            UIPasteboard.general.image = uiImage
                            model.status = "이미지를 복사했습니다."
                        } label: {
                            Image(systemName: "doc.on.doc")
                        }
                    }
                    Button(role: .destructive) {
                        model.removeImage(item.id)
                        dismiss()
                    } label: {
                        Image(systemName: "trash")
                    }
                }
            }
        }
        .preferredColorScheme(.dark)
    }
}

// MARK: - Batch Move Sheet & Bookshelf Storage Manager (Android BookshelfStorageActivity Parity)

struct BookshelfBatchMoveSheet: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var model: ShelfModel
    let itemIDs: Set<String>
    var onMoved: () -> Void
    @State private var selectedFolder: String = ""

    var body: some View {
        NavigationStack {
            VStack(spacing: 0) {
                List(model.allDisplayFolders, id: \.self) { folder in
                    let level = folder.filter { $0 == "/" }.count
                    let isSelected = selectedFolder.caseInsensitiveCompare(folder) == .orderedSame
                    HStack {
                        if level > 0 { Spacer().frame(width: CGFloat(level) * 18) }
                        Button {
                            selectedFolder = folder
                        } label: {
                            HStack {
                                Image(systemName: isSelected ? "folder.fill" : "folder")
                                    .foregroundStyle(isSelected ? .blue : .secondary)
                                Text(BookshelfPath.leaf(folder))
                                    .font(.body.weight(isSelected ? .semibold : .regular))
                                Spacer()
                            }
                        }
                        .buttonStyle(.plain)

                        Button("여기로 이동") {
                            model.moveImages(itemIDs, toFolder: folder)
                            onMoved()
                            dismiss()
                        }
                        .buttonStyle(.borderedProminent)
                        .controlSize(.small)
                    }
                }
                .listStyle(.plain)

                Button {
                    guard !selectedFolder.isEmpty else { return }
                    model.moveImages(itemIDs, toFolder: selectedFolder)
                    onMoved()
                    dismiss()
                } label: {
                    Label("확인 (\(itemIDs.count)개 → ‘\(BookshelfPath.leaf(selectedFolder))’)", systemImage: "checkmark.circle.fill")
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 10)
                }
                .buttonStyle(.borderedProminent)
                .padding(12)
            }
            .navigationTitle("선택한 사진 \(itemIDs.count)개 이동")
            .navigationBarTitleDisplayMode(.inline)
            .onAppear { selectedFolder = model.activeFolder }
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("취소") { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button("확인") {
                        model.moveImages(itemIDs, toFolder: selectedFolder)
                        onMoved()
                        dismiss()
                    }
                }
            }
        }
        .preferredColorScheme(.dark)
    }
}

struct BookshelfStorageSheet: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var model: ShelfModel
    @ObservedObject var sync: DriveSyncManager = .shared
    @State private var diskMB: String = "0.0 MB"
    @State private var localCount: Int = 0
    @State private var downloadingAll = false
    @State private var statusText = ""

    var body: some View {
        NavigationStack {
            Form {
                Section("책장 저장 공간 요약") {
                    LabeledContent("폴더 개수") { Text("\(model.allDisplayFolders.count)개") }
                    LabeledContent("전체 사진 수") { Text("\(model.catalog.items.count)장") }
                    LabeledContent("기기 저장 원본") { Text("\(localCount)장") }
                    LabeledContent("클라우드 전용(온디맨드)") { Text("\(max(0, model.catalog.items.count - localCount))장") }
                    LabeledContent("디스크 사용량") { Text(diskMB).monospacedDigit() }
                }

                Section("클라우드 저장 방식") {
                    Picker("사진 저장 방식", selection: $sync.onDemandCloudPhotos) {
                        Text("기기에 원본 모두 저장").tag(false)
                        Text("필요할 때 클라우드에서 불러오기").tag(true)
                    }
                    Button {
                        Task {
                            downloadingAll = true
                            defer { downloadingAll = false }
                            var downloaded = 0
                            for item in model.catalog.items {
                                if !FileManager.default.fileExists(atPath: model.imageURL(for: item).path),
                                   item.cloud != nil {
                                    if await sync.downloadCloudImageIfNeeded(item: item, shelf: model) != nil {
                                        downloaded += 1
                                    }
                                }
                            }
                            refreshStats()
                            statusText = downloaded > 0 ? "클라우드 사진 \(downloaded)장을 기기에 내려받았습니다." : "이미 모든 사진이 기기에 저장되어 있습니다."
                        }
                    } label: {
                        Label("클라우드 사진 모두 기기에 내려받기", systemImage: "icloud.and.arrow.down")
                    }
                    .disabled(downloadingAll || !sync.isConnected)
                }

                Section("유지 관리") {
                    Button("미사용 잔여 파일 정리") {
                        let cleaned = model.cleanupOrphanFiles()
                        refreshStats()
                        statusText = cleaned > 0 ? "잔여 파일 \(cleaned)개 정리 완료" : "정리할 파일이 없습니다."
                    }
                    if !statusText.isEmpty {
                        Text(statusText).font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
            .navigationTitle("책장 저장 공간")
            .navigationBarTitleDisplayMode(.inline)
            .onAppear { refreshStats() }
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("완료") { dismiss() }
                }
            }
        }
        .preferredColorScheme(.dark)
    }

    private func refreshStats() {
        let bytes = model.diskUsageBytes()
        diskMB = String(format: "%.2f MB", Double(bytes) / (1024.0 * 1024.0))
        localCount = model.localOriginalCount()
    }
}
