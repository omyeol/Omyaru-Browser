"""Build the AltStore Classic source from the IPA produced by GitHub Actions."""
import json
import pathlib
import plistlib
import sys
from datetime import date


def read_bundle_version(plist_path: pathlib.Path) -> tuple[str, str]:
    with plist_path.open("rb") as fp:
        info = plistlib.load(fp)
    version = str(info.get("CFBundleShortVersionString") or "1.0.3")
    build = str(info.get("CFBundleVersion") or "112")
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
                    "URL 필드 섬 확대·축소 시 간헐적으로 1프레임에 갑자기 커지거나 작아지던 튐 현상을 완전 수정 — "
                    "120Hz CADisplayLink/Choreographer 스프링 물리 엔진과 커밋 애니메이션 잠금을 도입해 "
                    "손가락 드래그 연동·확장·축소·복원의 모든 전환이 100% 연속적으로 부드럽게 동작하도록 개선 (iOS · Android)."
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
