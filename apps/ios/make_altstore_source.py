"""Build the AltStore Classic source from the IPA produced by GitHub Actions."""
import json
import pathlib
import plistlib
import sys
from datetime import date


def read_bundle_version(plist_path: pathlib.Path) -> tuple[str, str]:
    with plist_path.open("rb") as fp:
        info = plistlib.load(fp)
    version = str(info.get("CFBundleShortVersionString") or "1.0.5")
    build = str(info.get("CFBundleVersion") or "114")
    return version, build


def main() -> None:
    ipa = pathlib.Path(sys.argv[1])
    output = pathlib.Path(sys.argv[2])
    download_url = (
        sys.argv[3]
        if len(sys.argv) > 3 and sys.argv[3].strip()
        else "https://github.com/omyeol/Omyaru-Browser/releases/download/ios-latest/Omyaru.ipa"
    )
    plist_path = pathlib.Path(__file__).resolve().parent / "Omyaru" / "Info.plist"
    version, build_version = read_bundle_version(plist_path)
    icon = "https://raw.githubusercontent.com/omyeol/Omyaru-Browser/main/apps/ios/Omyaru/Assets.xcassets/AppIcon.appiconset/AppIcon.png"
    source = {
        "name": "Omyaru",
        "subtitle": "Omyaru Browser for iPhone and iPad",
        "description": "Omyaru Browser for iOS (AltStore Classic) — synchronized with Android Omyaru Browser releases.",
        "iconURL": icon,
        "website": "https://github.com/omyeol/Omyaru-Browser",
        "tintColor": "#5D97FF",
        "apps": [{
            "name": "Omyaru Browser",
            "bundleIdentifier": "cuttan.omyaru.app",
            "developerName": "Omyeol",
            "subtitle": "Privacy browser and bookshelf",
            "localizedDescription": (
                "Omyaru Browser v1.0.0 for iOS — unified privacy browser and bookshelf across iOS, Android, and Windows: "
                "profiles (18 icons & custom colors), tab groups, private tabs, top/bottom address bar, "
                "Liquid Glass UI, scroll auto-hide toolbar, Page Tools (zoom 50%~200%, find in page, per-site shield exception, desktop/mobile UA), "
                "Safari-style 120fps Hero Zoom tab overview (1~10 columns, 1:1/3:4/16:9 aspect ratio, bottom-first order, duplicate after source, drag auto-scroll, undo close), "
                "hierarchical bookmarks & date-grouped history, download confirmation to Bookshelf, "
                "built-in image preloader, tracker/ad blocker, userscripts (.user.js), "
                "incremental scroll-stitch region capture, dual-mode watermark remover (offline inpainting + web auto-inject), "
                "hierarchical folder tree bookshelf (multi-column, batch move/delete, storage manager, PDF export), "
                "instant Face ID/PIN/Password app & bookshelf lock with app-switcher privacy shield, DoH encrypted DNS & WireGuard profile manager, "
                "and full Google Drive / AES-256-GCM encrypted sync (tabs, bookmarks, history, chunked bookshelf images, sync tab reset)."
            ),
            "iconURL": icon,
            "tintColor": "#5D97FF",
            "category": "utilities",
            "versions": [{
                "version": version,
                "buildVersion": build_version,
                "date": date.today().isoformat(),
                "localizedDescription": (
                    f"v{version} (build {build_version}): "
                    "iOS 상단 고정 헤더 사이트 상태바 영역 불투명 채움 및 하단 사이트 고정 UI가 URL 필드 섬에 가리지 않도록 obscuredContentInsets/뷰포트 인셋 적용, "
                    "오른쪽→왼쪽 밀기 앞으로가기 제스처 복구(우측 빠른 스크롤바 히트영역 정밀화), "
                    "Omyaru 내장 WireGuard VPN 직접 연결·해제 엔진(AltStore 무료 서명 호환) 및 .conf 파일 공유·실시간 트래픽/IP 검사 지원."
                ),
                "downloadURL": download_url,
                "size": ipa.stat().st_size,
                "minOSVersion": "16.0",
            }],
            "appPermissions": {
                "entitlements": [],
                "privacy": {
                    "NSFaceIDUsageDescription": "Face ID로 Omyaru 브라우저와 책장 잠금을 해제합니다.",
                    "NSPhotoLibraryUsageDescription": "선택한 사진을 책장에 추가합니다.",
                    "NSPhotoLibraryAddUsageDescription": "책장 이미지를 사진 앱에 저장합니다.",
                },
            },
        }],
    }
    output.write_text(json.dumps(source, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
