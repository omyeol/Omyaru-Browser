import SwiftUI
import PhotosUI
import UniformTypeIdentifiers
import UIKit

struct ShelfFolder: Codable, Identifiable, Hashable {
    var id: UUID = UUID()
    var name: String
    var note: String = ""
    var columns: Int = 2
    var images: [String] = []
}

@MainActor
final class ShelfModel: ObservableObject {
    @Published var folders: [ShelfFolder] = []
    @Published var activeID: UUID?
    @Published var status = ""
    private let root = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0].appendingPathComponent("Bookshelf")
    private var catalogURL: URL { root.appendingPathComponent("catalog.json") }
    var active: ShelfFolder? { folders.first { $0.id == activeID } }

    init() {
        try? FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        if let data = try? Data(contentsOf: catalogURL), let saved = try? JSONDecoder().decode([ShelfFolder].self, from: data) {
            folders = saved
        }
        if folders.isEmpty { folders = [ShelfFolder(name: "기본 책장")] }
        activeID = folders[0].id
    }

    func imageURL(_ name: String, in folder: ShelfFolder) -> URL {
        root.appendingPathComponent(folder.id.uuidString).appendingPathComponent(name)
    }

    func save() {
        do {
            let data = try JSONEncoder().encode(folders)
            try data.write(to: catalogURL, options: .atomic)
        } catch { status = error.localizedDescription }
    }

    func addFolder(_ name: String) {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        let folder = ShelfFolder(name: trimmed)
        folders.append(folder)
        activeID = folder.id
        save()
    }

    func rename(_ id: UUID, to name: String) {
        guard let index = folders.firstIndex(where: { $0.id == id }), !name.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        folders[index].name = name.trimmingCharacters(in: .whitespaces)
        save()
    }

    func removeFolder(_ id: UUID) {
        guard folders.count > 1 else { status = "마지막 책장은 삭제할 수 없습니다."; return }
        guard let index = folders.firstIndex(where: { $0.id == id }) else { return }
        let directory = root.appendingPathComponent(id.uuidString)
        do {
            if FileManager.default.fileExists(atPath: directory.path) { try FileManager.default.removeItem(at: directory) }
            folders.remove(at: index)
            if activeID == id { activeID = folders[0].id }
            save()
        } catch { status = error.localizedDescription }
    }

    func updateNote(_ note: String) {
        guard let index = folders.firstIndex(where: { $0.id == activeID }) else { return }
        folders[index].note = String(note.prefix(4000))
        save()
    }

    func updateColumns(_ columns: Int) {
        guard let index = folders.firstIndex(where: { $0.id == activeID }) else { return }
        folders[index].columns = min(6, max(2, columns))
        save()
    }

    func add(_ data: Data, fileExtension ext: String = "jpg") {
        guard let index = folders.firstIndex(where: { $0.id == activeID }), UIImage(data: data) != nil else { return }
        let directory = root.appendingPathComponent(folders[index].id.uuidString)
        let name = UUID().uuidString + "." + ext
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            try data.write(to: directory.appendingPathComponent(name), options: .atomic)
            folders[index].images.append(name)
            save()
        } catch { status = error.localizedDescription }
    }

    func removeImage(_ name: String) {
        guard let index = folders.firstIndex(where: { $0.id == activeID }) else { return }
        let url = imageURL(name, in: folders[index])
        do {
            if FileManager.default.fileExists(atPath: url.path) { try FileManager.default.removeItem(at: url) }
            folders[index].images.removeAll { $0 == name }
            save()
        } catch { status = error.localizedDescription }
    }

    func exportPDF(landscape: Bool) -> URL? {
        guard let folder = active else { return nil }
        let size = landscape ? CGSize(width: 960, height: 540) : CGSize(width: 540, height: 960)
        let output = FileManager.default.temporaryDirectory.appendingPathComponent("\(folder.name)-\(UUID().uuidString).pdf")
        let renderer = UIGraphicsPDFRenderer(bounds: CGRect(origin: .zero, size: size))
        do {
            try renderer.writePDF(to: output) { context in
                var y = [CGFloat](repeating: 0, count: folder.columns)
                let cellWidth = size.width / CGFloat(folder.columns)
                context.beginPage()
                for name in folder.images {
                    guard let image = UIImage(contentsOfFile: imageURL(name, in: folder).path), image.size.width > 0 else { continue }
                    let column = y.enumerated().min(by: { $0.element < $1.element })!.offset
                    var height = image.size.height * cellWidth / image.size.width
                    if height > size.height { height = size.height }
                    if y[column] + height > size.height {
                        context.beginPage()
                        y = [CGFloat](repeating: 0, count: folder.columns)
                    }
                    image.draw(in: CGRect(x: CGFloat(column) * cellWidth, y: y[column], width: cellWidth, height: height))
                    y[column] += height
                }
            }
            return output
        } catch { status = error.localizedDescription; return nil }
    }
}

struct BookshelfView: View {
    @Environment(\.dismiss) private var dismiss
    @StateObject private var model = ShelfModel()
    @State private var selectedPhotos: [PhotosPickerItem] = []
    @State private var showFilePicker = false
    @State private var showFolders = false
    @State private var showFullscreen = false
    @State private var newFolderName = ""
    @State private var showNewFolder = false
    @State private var pdfURL: URL?
    @State private var showPDFOptions = false
    @State private var note = ""

    var body: some View {
        NavigationStack {
            VStack(spacing: 0) {
                HStack {
                    Button { showFolders = true } label: {
                        Label(model.active?.name ?? "책장", systemImage: "folder").lineLimit(1)
                    }
                    Spacer()
                    Button { model.updateColumns((model.active?.columns ?? 2) - 1) } label: { Image(systemName: "minus") }
                    Text("\(model.active?.columns ?? 2)열").font(.caption)
                    Button { model.updateColumns((model.active?.columns ?? 2) + 1) } label: { Image(systemName: "plus") }
                }.padding(.horizontal)
                TextEditor(text: $note).frame(height: 65)
                    .scrollContentBackground(.hidden)
                    .background(.thinMaterial)
                    .onChange(of: note) { value in model.updateNote(value) }
                ScrollView {
                    if let folder = model.active {
                        LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 0), count: folder.columns), spacing: 0) {
                            ForEach(folder.images, id: \.self) { name in
                                if let image = UIImage(contentsOfFile: model.imageURL(name, in: folder).path) {
                                    Image(uiImage: image).resizable().scaledToFit()
                                        .contextMenu { Button("삭제", role: .destructive) { model.removeImage(name) } }
                                }
                            }
                        }
                    }
                }
            }
            .navigationTitle(model.active?.name ?? "책장")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) { Button("완료") { dismiss() } }
                ToolbarItemGroup(placement: .topBarTrailing) {
                    PhotosPicker(selection: $selectedPhotos, maxSelectionCount: 0, matching: .images) {
                        Image(systemName: "photo.badge.plus")
                    }
                    Menu {
                        Button("파일에서 여러 장 추가") { showFilePicker = true }
                        Button("전체 화면") { showFullscreen = true }
                        Button("PDF 내보내기") { showPDFOptions = true }
                    } label: { Image(systemName: "ellipsis.circle") }
                }
            }
            .fileImporter(isPresented: $showFilePicker, allowedContentTypes: [.image], allowsMultipleSelection: true) { result in
                if case let .success(urls) = result {
                    for url in urls {
                        let access = url.startAccessingSecurityScopedResource()
                        defer { if access { url.stopAccessingSecurityScopedResource() } }
                        if let data = try? Data(contentsOf: url) { model.add(data, fileExtension: url.pathExtension.isEmpty ? "jpg" : url.pathExtension) }
                    }
                }
            }
            .onChange(of: selectedPhotos) { photos in
                Task {
                    for photo in photos {
                        if let data = try? await photo.loadTransferable(type: Data.self) { model.add(data) }
                    }
                    selectedPhotos = []
                }
            }
            .onChange(of: model.activeID) { _ in note = model.active?.note ?? "" }
            .onAppear { note = model.active?.note ?? "" }
            .sheet(isPresented: $showFolders) { foldersSheet }
            .fullScreenCover(isPresented: $showFullscreen) { fullscreenGallery }
            .confirmationDialog("PDF 방향", isPresented: $showPDFOptions) {
                Button("세로 9:16") { pdfURL = model.exportPDF(landscape: false) }
                Button("가로 16:9") { pdfURL = model.exportPDF(landscape: true) }
            }
            .sheet(isPresented: Binding(get: { pdfURL != nil }, set: { if !$0 { pdfURL = nil } })) {
                if let url = pdfURL {
                    NavigationStack { ShareLink(item: url) { Label("PDF 공유 또는 저장", systemImage: "square.and.arrow.up") }.padding().navigationTitle("PDF 내보내기") }
                }
            }
        }
    }

    private var foldersSheet: some View {
        NavigationStack {
            List(model.folders) { folder in
                Button { model.activeID = folder.id; showFolders = false } label: {
                    Label(folder.name, systemImage: folder.id == model.activeID ? "folder.fill" : "folder")
                }
                .contextMenu { Button("삭제", role: .destructive) { model.removeFolder(folder.id) } }
            }
            .navigationTitle("책장 폴더")
            .toolbar {
                ToolbarItem(placement: .topBarLeading) { Button("완료") { showFolders = false } }
                ToolbarItem(placement: .topBarTrailing) { Button { showNewFolder = true } label: { Image(systemName: "folder.badge.plus") } }
            }
            .alert("새 책장 폴더", isPresented: $showNewFolder) {
                TextField("이름", text: $newFolderName)
                Button("만들기") { model.addFolder(newFolderName); newFolderName = "" }
                Button("취소", role: .cancel) { newFolderName = "" }
            }
        }
    }

    private var fullscreenGallery: some View {
        ZStack(alignment: .topTrailing) {
            Color.black.ignoresSafeArea()
            ScrollView {
                if let folder = model.active {
                    LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 0), count: folder.columns), spacing: 0) {
                        ForEach(folder.images, id: \.self) { name in
                            if let image = UIImage(contentsOfFile: model.imageURL(name, in: folder).path) {
                                Image(uiImage: image).resizable().scaledToFit()
                            }
                        }
                    }
                }
            }.ignoresSafeArea()
            Button { showFullscreen = false } label: { Image(systemName: "xmark.circle.fill").font(.title).padding() }
        }
    }
}
