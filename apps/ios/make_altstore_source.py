"""Build the AltStore Classic source from the IPA produced by GitHub Actions."""
import json
import pathlib
import plistlib
import sys
from datetime import date


def read_bundle_version(plist_path: pathlib.Path) -> tuple[str, str]:
    with plist_path.open("rb") as fp:
        info = plistlib.load(fp)
    version = str(info.get("CFBundleShortVersionString") or "0.15.18")
    build = str(info.get("CFBundleVersion") or "76")
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
                "Omyaru Browser for iOS with full Android v0.15.18 feature parity: "
                "profiles (18 icons & custom colors), tab groups, private tabs, top/bottom address bar, "
                "Liquid Glass UI, scroll auto-hide toolbar, Page Tools (zoom 50%~200%, find in page, per-site shield exception, desktop/mobile UA), "
                "tab overview (1~10 columns, 1:1/3:4/16:9 aspect ratio, bottom-first order, duplicate after source, drag auto-scroll, undo close), "
                "hierarchical bookmarks & date-grouped history, download confirmation to Bookshelf, "
                "built-in image preloader, tracker/ad blocker, userscripts (.user.js), "
                "incremental scroll-stitch region capture, dual-mode watermark remover (offline inpainting + web auto-inject), "
                "hierarchical folder tree bookshelf (multi-column, batch move/delete, storage manager, PDF export), "
                "Face ID/PIN/Password app lock with app-switcher black shield, DoH encrypted DNS & WireGuard profile manager, "
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
                    f"v{version} (build {build_version}): Safari 스타일 120fps Hero Zoom 전환, "
                    "상황별 탭 열기/닫기 애니메이션(새 탭 슬롯 블룸, 중간 탭 2D 리플로우, 끝 탭 행 축소 스크롤), "
                    "탭 목록 복귀 시 상·하단 버튼 겹침 자동 회피 스크롤, Safari 동일 높이 축소 URL 필드 및 좌우 빈 공간 터치 확장, "
                    "상단 가우시안 블러 적용(하단 블러 제거), 프로필 색상 활성 탭 테두리, 상단 정렬 썸네일, 당겨서 새로고침, "
                    "설정 다크 모드/내부 서브메뉴 수정 및 PC·Android·iOS 양방향 CRDT 탭·그룹·프로필·책장 동기화."
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
