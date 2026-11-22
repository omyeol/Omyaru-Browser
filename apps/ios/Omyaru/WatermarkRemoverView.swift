import SwiftUI
import PhotosUI
import WebKit
import UIKit

// MARK: - Watermark Remover & Inpainting Tool

struct NormalizedBox: Identifiable, Equatable {
    var id = UUID()
    var left: Double
    var top: Double
    var right: Double
    var bottom: Double
}

struct WatermarkRemoverView: View {
    @Environment(\.dismiss) private var dismiss
    let webView: WKWebView?
    let sourceURL: String
    @ObservedObject var shelf: ShelfModel = .shared

    @State private var originalImage: UIImage? = nil
    @State private var processedImage: UIImage? = nil
    @State private var boxes: [NormalizedBox] = [
        NormalizedBox(left: 0.72, top: 0.90, right: 0.98, bottom: 0.98)
    ]
    @State private var selectedBoxID: UUID? = nil
    @State private var showOriginal: Bool = false
    @State private var processing: Bool = false
    @State private var selectedPhoto: PhotosPickerItem? = nil
    @State private var showFileImporter = false
    @State private var targetFolder: String = ""
    @State private var showFolderPicker = false
    @State private var statusMessage: String = "워터마크 영역을 드래그해 지정한 뒤 [워터마크 제거 실행]을 누르세요."

    var body: some View {
        NavigationStack {
            VStack(spacing: 0) {
                sourceToolbar
                imageWorkspace
                controlsBar
            }
            .background(Color(red: 0.06, green: 0.06, blue: 0.07))
            .navigationTitle("워터마크 제거")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("닫기") { dismiss() }
                }
                ToolbarItemGroup(placement: .topBarTrailing) {
                    PhotosPicker(selection: $selectedPhoto, matching: .images) {
                        Image(systemName: "photo")
                    }
                    Button {
                        showFileImporter = true
                    } label: {
                        Image(systemName: "folder")
                    }
                }
            }
            .task {
                if targetFolder.isEmpty { targetFolder = shelf.activeFolder }
                if originalImage == nil, let webView {
                    if let snap = await WebpageCaptureEngine.captureStitchedViewportPages(webView: webView, viewportCount: 1) {
                        originalImage = snap
                        processedImage = nil
                        selectedBoxID = boxes.first?.id
                    }
                }
            }
            .onChange(of: selectedPhoto) { item in
                guard let item else { return }
                Task {
                    if let data = try? await item.loadTransferable(type: Data.self),
                       let img = UIImage(data: data) {
                        originalImage = img
                        processedImage = nil
                        statusMessage = "사진을 불러왔습니다. 워터마크 영역을 조정하세요."
                    }
                    selectedPhoto = nil
                }
            }
            .fileImporter(isPresented: $showFileImporter, allowedContentTypes: [.image]) { result in
                if case let .success(url) = result {
                    let access = url.startAccessingSecurityScopedResource()
                    defer { if access { url.stopAccessingSecurityScopedResource() } }
                    if let data = try? Data(contentsOf: url), let img = UIImage(data: data) {
                        originalImage = img
                        processedImage = nil
                        statusMessage = "파일에서 이미지를 불러왔습니다."
                    }
                }
            }
            .sheet(isPresented: $showFolderPicker) {
                CaptureFolderPickerSheet(shelf: shelf, selectedFolder: $targetFolder)
            }
        }
        .preferredColorScheme(.dark)
    }

    private var sourceToolbar: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 8) {
                Button {
                    let box = NormalizedBox(left: 0.30, top: 0.40, right: 0.70, bottom: 0.55)
                    boxes.append(box)
                    selectedBoxID = box.id
                    processedImage = nil
                } label: {
                    Label("영역 추가", systemImage: "plus.rectangle.on.rectangle")
                        .font(.caption.weight(.semibold))
                        .padding(.horizontal, 10)
                        .padding(.vertical, 6)
                        .background(Color(red: 0.17, green: 0.17, blue: 0.19), in: Capsule())
                }
                .buttonStyle(.plain)

                Button {
                    boxes = [
                        NormalizedBox(left: 0.70, top: 0.91, right: 0.99, bottom: 0.99),
                        NormalizedBox(left: 0.01, top: 0.91, right: 0.30, bottom: 0.99)
                    ]
                    selectedBoxID = boxes.first?.id
                    processedImage = nil
                    statusMessage = "하단 좌/우 코너 워터마크 영역을 자동 선택했습니다."
                } label: {
                    Label("코너 자동 감지", systemImage: "sparkles")
                        .font(.caption.weight(.semibold))
                        .padding(.horizontal, 10)
                        .padding(.vertical, 6)
                        .background(Color(red: 0.17, green: 0.17, blue: 0.19), in: Capsule())
                }
                .buttonStyle(.plain)

                if boxes.count > 1, let selected = selectedBoxID {
                    Button {
                        boxes.removeAll { $0.id == selected }
                        selectedBoxID = boxes.first?.id
                        processedImage = nil
                    } label: {
                        Label("선택 영역 삭제", systemImage: "trash")
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(.red)
                            .padding(.horizontal, 10)
                            .padding(.vertical, 6)
                            .background(Color(red: 0.17, green: 0.17, blue: 0.19), in: Capsule())
                    }
                    .buttonStyle(.plain)
                }

                if processedImage != nil {
                    Toggle("원본 비교", isOn: $showOriginal)
                        .toggleStyle(.button)
                        .font(.caption.weight(.semibold))
                }
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
        }
        .background(Color(red: 0.11, green: 0.11, blue: 0.12))
    }

    private var imageWorkspace: some View {
        ZStack {
            if let displayImage = (showOriginal || processedImage == nil) ? originalImage : processedImage {
                GeometryReader { geo in
                    let fitted = fitSize(imageSize: displayImage.size, in: geo.size)
                    let originX = (geo.size.width - fitted.width) / 2
                    let originY = (geo.size.height - fitted.height) / 2

                    ZStack(alignment: .topLeading) {
                        Image(uiImage: displayImage)
                            .resizable()
                            .frame(width: fitted.width, height: fitted.height)
                            .position(x: geo.size.width / 2, y: geo.size.height / 2)

                        if processedImage == nil || showOriginal {
                            ForEach($boxes) { $box in
                                let isSelected = box.id == selectedBoxID
                                let rect = CGRect(
                                    x: originX + fitted.width * box.left,
                                    y: originY + fitted.height * box.top,
                                    width: fitted.width * (box.right - box.left),
                                    height: fitted.height * (box.bottom - box.top)
                                )
                                ZStack {
                                    Rectangle()
                                        .fill(Color.cyan.opacity(0.20))
                                        .overlay(
                                            Rectangle().strokeBorder(isSelected ? Color.cyan : Color.white.opacity(0.75), lineWidth: isSelected ? 2.5 : 1.5)
                                        )
                                        .frame(width: max(8, rect.width), height: max(8, rect.height))
                                        .position(x: rect.midX, y: rect.midY)
                                        .gesture(
                                            DragGesture()
                                                .onChanged { value in
                                                    selectedBoxID = box.id
                                                    let w = box.right - box.left
                                                    let h = box.bottom - box.top
                                                    let cx = Double((value.location.x - originX) / max(1, fitted.width))
                                                    let cy = Double((value.location.y - originY) / max(1, fitted.height))
                                                    let newLeft = min(1 - w, max(0, cx - w / 2))
                                                    let newTop = min(1 - h, max(0, cy - h / 2))
                                                    box.left = newLeft
                                                    box.right = newLeft + w
                                                    box.top = newTop
                                                    box.bottom = newTop + h
                                                }
                                        )

                                    // Bottom-right resize handle
                                    Circle()
                                        .fill(Color.cyan)
                                        .frame(width: 18, height: 18)
                                        .position(x: rect.maxX, y: rect.maxY)
                                        .gesture(
                                            DragGesture()
                                                .onChanged { value in
                                                    selectedBoxID = box.id
                                                    let nx = Double((value.location.x - originX) / max(1, fitted.width))
                                                    let ny = Double((value.location.y - originY) / max(1, fitted.height))
                                                    box.right = min(1, max(box.left + 0.03, nx))
                                                    box.bottom = min(1, max(box.top + 0.03, ny))
                                                }
                                        )
                                }
                            }
                        }
                    }
                }
                .padding(12)
            } else {
                VStack(spacing: 12) {
                    Image(systemName: "wand.and.stars")
                        .font(.system(size: 44))
                        .foregroundStyle(.cyan)
                    Text("우측 상단에서 사진을 선택하거나 웹페이지에서 실행해 주세요.")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            }

            if processing {
                ProgressView("워터마크 영역 복원 중...")
                    .padding(16)
                    .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 14))
            }
        }
    }

    private var controlsBar: some View {
        VStack(spacing: 8) {
            Text(statusMessage)
                .font(.caption)
                .foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, alignment: .leading)

            HStack(spacing: 8) {
                Button {
                    runInpainting()
                } label: {
                    Label(processedImage == nil ? "워터마크 제거 실행" : "다시 복원", systemImage: "wand.and.stars")
                        .font(.subheadline.weight(.semibold))
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 10)
                }
                .buttonStyle(.borderedProminent)
                .tint(.cyan)
                .disabled(originalImage == nil || processing)

                Button {
                    guard let out = processedImage ?? originalImage else { return }
                    UIImageWriteToSavedPhotosAlbum(out, nil, nil, nil)
                    UIPasteboard.general.image = out
                    statusMessage = "사진 앱 저장 및 클립보드 복사를 완료했습니다."
                } label: {
                    Label("저장/복사", systemImage: "square.and.arrow.down")
                        .font(.subheadline.weight(.semibold))
                        .padding(.vertical, 10)
                        .padding(.horizontal, 12)
                }
                .buttonStyle(.bordered)
                .disabled(originalImage == nil)

                Button {
                    guard let out = processedImage ?? originalImage,
                          let png = out.pngData() else { return }
                    let folder = targetFolder.isEmpty ? shelf.activeFolder : targetFolder
                    if shelf.addImageData(png, fileExtension: "png", toFolder: folder, sourceUrl: sourceURL) != nil {
                        dismiss()
                    }
                } label: {
                    Label("책장 추가", systemImage: "books.vertical.fill")
                        .font(.subheadline.weight(.semibold))
                        .padding(.vertical, 10)
                        .padding(.horizontal, 12)
                }
                .buttonStyle(.borderedProminent)
                .disabled(originalImage == nil)
            }
        }
        .padding(12)
        .background(Color(red: 0.11, green: 0.11, blue: 0.13))
    }

    private func fitSize(imageSize: CGSize, in container: CGSize) -> CGSize {
        guard imageSize.width > 0, imageSize.height > 0, container.width > 0, container.height > 0 else {
            return container
        }
        let scale = min(container.width / imageSize.width, container.height / imageSize.height)
        return CGSize(width: imageSize.width * scale, height: imageSize.height * scale)
    }

    private func runInpainting() {
        guard let source = originalImage else { return }
        let targetBoxes = boxes
        processing = true
        Task {
            let output = await Task.detached(priority: .userInitiated) {
                Self.inpaintBoxes(in: source, boxes: targetBoxes)
            }.value
            processedImage = output
            showOriginal = false
            processing = false
            statusMessage = "워터마크 제거 완료! [원본 비교]로 확인하거나 [책장 추가]를 누르세요."
        }
    }

    nonisolated private static func inpaintBoxes(in image: UIImage, boxes: [NormalizedBox]) -> UIImage {
        guard let cg = image.cgImage else { return image }
        let width = cg.width
        let height = cg.height
        guard width > 4, height > 4 else { return image }

        let bytesPerPixel = 4
        let bytesPerRow = width * bytesPerPixel
        var pixels = [UInt8](repeating: 0, count: height * bytesPerRow)
        let colorSpace = CGColorSpaceCreateDeviceRGB()
        let bitmapInfo = CGImageAlphaInfo.premultipliedLast.rawValue | CGBitmapInfo.byteOrder32Big.rawValue

        guard let ctx = CGContext(
            data: &pixels,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: bytesPerRow,
            space: colorSpace,
            bitmapInfo: bitmapInfo
        ) else { return image }

        ctx.draw(cg, in: CGRect(x: 0, y: 0, width: width, height: height))

        for box in boxes {
            let x0 = max(1, min(width - 2, Int(Double(width) * box.left)))
            let x1 = max(x0 + 1, min(width - 2, Int(Double(width) * box.right)))
            let y0 = max(1, min(height - 2, Int(Double(height) * box.top)))
            let y1 = max(y0 + 1, min(height - 2, Int(Double(height) * box.bottom)))
            let boxW = Double(max(1, x1 - x0))
            let boxH = Double(max(1, y1 - y0))

            for y in y0...y1 {
                let ty = Double(y - y0) / boxH
                let leftIdx = (y * width + (x0 - 1)) * 4
                let rightIdx = (y * width + (x1 + 1)) * 4
                for x in x0...x1 {
                    let tx = Double(x - x0) / boxW
                    let topIdx = ((y0 - 1) * width + x) * 4
                    let botIdx = ((y1 + 1) * width + x) * 4

                    let wx = 1.0 / max(0.001, min(tx, 1.0 - tx))
                    let wy = 1.0 / max(0.001, min(ty, 1.0 - ty))
                    let totalW = wx + wy

                    let dstIdx = (y * width + x) * 4
                    for c in 0..<3 {
                        let horiz = (1.0 - tx) * Double(pixels[leftIdx + c]) + tx * Double(pixels[rightIdx + c])
                        let vert = (1.0 - ty) * Double(pixels[topIdx + c]) + ty * Double(pixels[botIdx + c])
                        let blended = (horiz * wx + vert * wy) / totalW
                        pixels[dstIdx + c] = UInt8(max(0, min(255, Int(blended.rounded()))))
                    }
                    pixels[dstIdx + 3] = 255
                }
            }
        }

        guard let outCG = ctx.makeImage() else { return image }
        return UIImage(cgImage: outCG, scale: image.scale, orientation: image.imageOrientation)
    }
}
