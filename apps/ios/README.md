# Omyaru iOS preview

This is a new SwiftUI/WKWebView app for iPhone and iPad. It currently provides tabs,
private tabs, tab groups, bookmarks, history, basic downloads, file upload through
WebKit, a local bookshelf with multi-image import and PDF export, and a manual
GitHub release update check. It does **not** yet share the Android Drive sync
protocol, VPN engine, or long-page region capture. It has not been tested on a
physical iOS device. Do not describe it as Android feature parity.

GitHub Actions builds an unsigned device IPA. AltStore Classic re-signs sideloaded
apps for the user's device. The stable source URL is:

`https://raw.githubusercontent.com/omyeol/Omyaru-Browser/main/apps/ios/altstore-source.json`

Xcode project generation on a Mac: `xcodegen generate --spec project.yml` from
this directory. The generated `.xcodeproj` is not committed.
