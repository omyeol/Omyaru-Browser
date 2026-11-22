"""Build the AltStore Classic source from the IPA produced by GitHub Actions."""
import json
import pathlib
import sys
from datetime import date


def main() -> None:
    ipa = pathlib.Path(sys.argv[1])
    output = pathlib.Path(sys.argv[2])
    release = "https://github.com/omyeol/Omyaru-Browser/releases/download/ios-latest"
    icon = "https://raw.githubusercontent.com/omyeol/Omyaru-Browser/main/apps/ios/Omyaru/Assets.xcassets/AppIcon.appiconset/AppIcon.png"
    source = {
        "name": "Omyaru",
        "subtitle": "Omyaru Browser for iPhone and iPad",
        "description": "Omyaru iOS preview builds for AltStore Classic.",
        "iconURL": icon,
        "website": "https://github.com/omyeol/Omyaru-Browser",
        "tintColor": "#5D97FF",
        "apps": [{
            "name": "Omyaru Browser",
            "bundleIdentifier": "cuttan.omyaru.app",
            "developerName": "Omyeol",
            "subtitle": "Privacy browser and bookshelf",
            "localizedDescription": "Omyaru iOS Browser with full Android feature parity: profiles, tab groups, private tabs, tab duplication after source, drag auto-scroll, animated tab close, web image preloader, tracker/ad blocker, userscripts (.user.js), live multi-viewport region capture, watermark remover, hierarchical folder tree bookshelf with PDF export, Face ID/PIN app lock, DoH encrypted DNS, and Google Drive / AES-256-GCM encrypted sync.",
            "iconURL": icon,
            "tintColor": "#5D97FF",
            "category": "utilities",
            "versions": [{
                "version": "0.2.0",
                "buildVersion": "2",
                "date": date.today().isoformat(),
                "localizedDescription": "v0.2.0: Full Android feature parity (hierarchical bookshelf tree, live scroll-stitch capture, watermark remover, Google Drive & AES-256-GCM sync, image preloader, userscripts, DoH DNS, Face ID app lock).",
                "downloadURL": release + "/Omyaru.ipa",
                "size": ipa.stat().st_size,
                "minOSVersion": "16.0",
            }],
            "appPermissions": {
                "entitlements": [],
                "privacy": {
                    "NSPhotoLibraryUsageDescription": "선택한 사진을 책장에 추가합니다.",
                    "NSPhotoLibraryAddUsageDescription": "책장 이미지를 사진 앱에 저장합니다.",
                },
            },
        }],
    }
    output.write_text(json.dumps(source, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
