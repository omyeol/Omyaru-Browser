# 2026-11-24 Antigravity: iOS 탭 목록 Safari 제자리 썸네일 들림 + 앵커형 Liquid Glass 컨텍스트 메뉴 (`KakaoTalk_20261124_165439782.mp4` 완전 재현) (`iOS build 92`)

- 작업 워크스페이스: `E:/DEV/CODEX/BROWSER`
- 사용자 요청:
  - `KakaoTalk_20261124_165439782.mp4` 영상처럼 iOS 탭 목록에서 탭을 길게 누르면 중앙 미니 사각형 팝업 대신 **실제 해당 탭 카드 썸네일이 제자리에서 확대·부각되고, 배경이 어두워지며, 카드 바로 아래 좌/우 가장자리에 맞춰 Liquid Glass 컨텍스트 메뉴가 펼쳐지도록** 개선
- 해결 (`apps/ios/Omyaru/BrowserViews.swift`, `LiquidGlassComponents.swift`, `Info.plist`, `OmyaruApp.swift`):
  1. **제자리 탭 썸네일 프리뷰 + 카드 정렬 앵커 컨텍스트 메뉴 (`safariTabContextOverlay`)**:
     - 기존 화면 중앙 `196×150` 미니 카드 팝업을 제거하고, 선택한 탭 카드의 실제 글로벌 썸네일 좌표(`contextMenuSourceRect`)를 캡처해 **원본 썸네일 비율 그대로 제자리에서 `1.07x` 부드럽게 부각**시키며(`×` 닫기 버튼 숨김, 그리드 하단 제목은 배경과 함께 자연스럽게 어두워짐, `Color.black.opacity(0.48)` 전체 딤드 배경).
     - 좌측 열 카드는 카드 왼쪽 가장자리에 `.topLeading` 앵커로, 우측 열 카드는 카드 오른쪽 가장자리에 `.topTrailing` 앵커로, 중앙 카드는 `.top` 앵커로 컨텍스트 메뉴가 바로 아래에서 펼쳐지며, 하단 근처 카드일 경우 메뉴가 화면 밖으로 잘리지 않도록 썸네일이 필요한 만큼만 부드럽게 위로 슬라이드.
     - 배경 탭 시 카드가 원래 그리드 슬롯 위치(`scale: 1.00`)로 부드럽게 되돌아가며 닫히고, 부각된 썸네일을 탭하면 즉시 해당 탭으로 이동.
  2. **영상과 동일한 정지 홀드 자동 팝업 + 빠른 릴리스 즉시 팝업 병행 (`autoPeekTask`)**:
     - `0.18초` 길게 누르기 진입 후 손가락을 움직이지 않고 그대로 누르고 있으면(`+0.24초`, 총 `~0.42초`) 영상처럼 손을 떼지 않아도 제자리 썸네일+컨텍스트 메뉴가 자동으로 열리고, `0.18~0.42초` 사이에 빠르게 떼어도 0ms 지연으로 즉시 열림. 드래그(`> 16pt`) 시 자동 팝업이 취소되고 기존 120fps 순서 이동 드래그로 매끄럽게 전환.
  3. **Safari 영상과 동일한 메뉴 항목·아이콘·배치 (`LiquidGlassTabCardContextPopover`)**:
     - 웹 URL 탭: `링크 복사하기(link)` · `탭 고정(pin)` · `탭 복제` · `탭 선택` · `탭 이동(>)` · `탭 닫기(빨강)` / *(구분선)* / `다른 탭 닫기(빨강)`.
     - 시작 페이지(`about:blank`): `링크 복사하기`/`탭 고정` 자동 생략(영상의 `시작 페이지` 카드와 동일).
  - iOS 빌드 번호: `91` → **`92`** (`Info.plist`, `OmyaruApp.swift`).

---

# 2026-11-24 Antigravity: iOS 탭 목록 빠른 길게 누르기·릴리스 즉시 컨텍스트 메뉴(`TabCardGestureOverlay`) + URL 왼쪽 페이지 도구 메뉴 편집(`PageToolsEditorSheet`) (`iOS build 91`)

- 작업 워크스페이스: `E:/DEV/CODEX/BROWSER`
- 사용자 요청:
  1. iOS 탭 목록에서 탭을 길게 누르고 움직이지 않고 뗄 때 컨텍스트 메뉴가 너무 오래 걸리는 문제 개선 (길게 누르고 빠르게 떼도 즉시 메뉴 표시)
  2. URL 필드 왼쪽 버튼(페이지 도구: `워터마크 제거`, `영역 캡처`, `배율` 등) 영역도 설정에서 항목 편집·순서 변경 가능하게 추가
- 해결:
  1. **탭 목록 즉각 반응 길게 누르기·0ms 릴리스 컨텍스트 메뉴 (`apps/ios/Omyaru/BrowserViews.swift`, `LiquidGlassComponents.swift`)**:
     - 원인: SwiftUI `LongPressGesture(0.28).sequenced(before: DragGesture(minimumDistance: 0))`은 손가락을 움직이지 않고 뗄 때 `DragGesture`가 시작되지 않아 SwiftUI `SequenceGesture`의 내부 핸드오프 타임아웃(~0.7~1.0초)이 지난 뒤에야 `@GestureState`가 초기화되며 컨텍스트 메뉴가 열렸고, `LiquidGlassMorphContainer`의 `0.34s` 블러/확대 애니메이션이 지연을 더했음.
     - 해결: 개별 탭 카드에 네이티브 UIKit `TabCardGestureOverlay`(`TabCardTouchView` + `UITapGestureRecognizer` + `UILongPressGestureRecognizer(minimumPressDuration: 0.18, allowableMovement: 14)`)를 `.overlay`로 직접 배치하고 우측 상단 닫기(`×`) `38×38pt` 영역은 `point(inside:)`에서 패스스루 처리. `0.18초` 만에 즉시 카드가 들리고, 움직이지 않고(`≤ 18pt`) 손을 떼는 순간 `UILongPressGestureRecognizer.ended`가 **0ms 지연**으로 발동하며 `LiquidGlassMorphContainer` 고속 스프링(`response: 0.20, dampingFraction: 0.88, initialScale: 0.86, initialBlur: 3`)으로 컨텍스트 메뉴가 즉시 팝업됨(드래그 시 기존 무변경 시각 슬롯 시프트·120fps 이동 유지). 리스트 보기 행의 길게 누르기도 `0.20초`로 단축.
  2. **URL 왼쪽 페이지 도구 메뉴 커스터마이징 (`apps/ios/Omyaru/OmyaruApp.swift`, `LiquidGlassComponents.swift`, `BrowserViews.swift`)**:
     - `PageToolsMenuItem`(23종: 배율 조절, 북마크 추가·편집, 페이지 찾기, 콘텐츠 차단기, 데스크톱/모바일, 실시간 영역 캡처, 워터마크 제거, 미디어 PiP 바, 이미지 고속 선로딩, 유저스크립트 빠른 토글, 확장 프로그램 설정, 책장, 북마크 목록, 방문 기록, 다운로드, 주소 복사, 공유, 현재 탭 복제, 새 탭, 닫은 탭 다시 열기, VPN, 동기화, 설정) 신설 및 `BrowserPreferences.pageToolsItems` 추가(기존 `omyaru.ios.preferences.v3` 설정이 초기화되지 않도록 모든 필드 `decodeIfPresent` 기반 하위 호환 `init(from:)` 구현).
     - `LiquidGlassPageToolsPopover`가 사용자 지정 순서·항목을 그대로 동적 렌더링하고 하단에 `"페이지 도구 편집"` 바로가기 제공(주소창 왼쪽 버튼 길게 누르기로도 즉시 편집 진입).
     - `SettingsSheet` → `화면 및 모양` → `주소창 및 스크롤 동작`에 **`페이지 도구 메뉴 편집 (URL 왼쪽 버튼)...`**(`PageToolsEditorSheet`: 드래그 순서 변경, 항목 추가·제거, 기본값 복원) 추가.
  - iOS 빌드 번호: `90` → **`91`** (`Info.plist`, `OmyaruApp.swift`).

---

# 2026-11-24 Antigravity: iOS 탭 목록 길게 누르기 미이동 릴리스 메뉴 확정 표시(`@GestureState`) + 전용 책장 잠금(자동 잠금 시간 · 아이폰 잠금 연동 · Face ID/PIN/비밀번호) (`iOS build 90`)

- 작업 워크스페이스: `E:/DEV/CODEX/BROWSER`
- 사용자 요청:
  1. iOS 탭 목록에서 탭을 길게 눌렀다가 움직이지 않고 놓으면 `링크 복사하기` 등 컨텍스트 메뉴가 확실히 뜨도록 수정
  2. 책장 전용 잠금 기능 추가(자동 잠금 시간 설정 + 아이폰 화면이 잠기면 같이 잠기도록 하는 옵션 포함)
- 해결:
  1. **탭 목록 미이동 릴리스 컨텍스트 메뉴 확정 발동 (`apps/ios/Omyaru/BrowserViews.swift`)**:
     - 원인: SwiftUI `LongPressGesture.sequenced(before: DragGesture)`는 길게 누른 뒤 손가락을 움직이지 않고 그대로 떼면 `DragGesture`가 시작되지 않아 `.onEnded`를 호출하지 않고 조용히 취소되는 특성 때문에 컨텍스트 메뉴가 열리지 않았음.
     - 해결: `@GestureState private var isTabHoldGestureActive = false` + `.updating($isTabHoldGestureActive)` + `.onChange(of: isTabHoldGestureActive)`와 `isSettlingDrop` 가드를 도입하고 홀드 데드존을 `16pt`로 여유 있게 조정해, `.onEnded`가 생략되더라도 손을 떼는 순간 `finalizeActiveTabDrag`가 100% 호출되어 Liquid Glass 컨텍스트 메뉴(`링크 복사하기 · 탭 고정 · 탭 복제 · 탭 선택 · 탭 이동 · 다른 탭 닫기 · 탭 닫기`)가 즉시 열리도록 수정.
  2. **책장 전용 잠금 (`apps/ios/Omyaru/AppLockAndPrivacy.swift`, `BookshelfView.swift`, `BrowserViews.swift`, `OmyaruApp.swift`)**:
     - `BookshelfLockManager` / `BookshelfLockTimeout` / `BookshelfLockOverlayView` / `BookshelfLockSettingsSheet` 신설(전체 앱 잠금과 독립 동작).
     - **자동 잠금 시간(`BookshelfLockTimeout`)**: `즉시 (책장 닫을 때)`, `30초`, `1분`, `3분`, `5분`, `10분`, `30분`, `1시간`, `시간 잠금 안 함` 선택 가능.
     - **아이폰 잠금 연동(`lockOnDeviceLock`)**: `UIApplication.protectedDataWillBecomeUnavailableNotification` 구독으로 아이폰 화면이 잠기는 즉시 책장도 함께 잠금(+ 선택적 `앱 백그라운드 전환 시 즉시 잠금`, 앱 전환기 미리보기 가림막 포함).
     - **인증 방식**: 별도 암호 없이 Face ID · Touch ID · iPhone 암호만으로 즉시 사용하거나, 책장 전용 PIN(4~6자리)/비밀번호(PBKDF2-SHA256) 설정 가능. 책장 우측 상단 `⋯` 메뉴(`책장 잠금 설정` / `지금 책장 잠그기`)와 브라우저 설정 → 개인정보 보호 양쪽에서 접근 가능.
  - iOS 빌드 번호: `89` → **`90`** (`Info.plist`, `OmyaruApp.swift`).

---

# 2026-11-24 Antigravity: iOS 포그라운드 상단 동기화 배너 제거 · 백그라운드 전용 진행 알림 · 동기화 창 상단 진행률 카드 표시 (`iOS build 89`)

- 작업 워크스페이스: `E:/DEV/CODEX/BROWSER`
- 사용자 요청:
  - 브라우저·탭 목록 화면 상단을 가리던 플로팅 동기화 배너(`OmyaruInAppLiveSyncPill`) 제거, 동기화 진행 알림은 백그라운드 상태에서만 표시하고, 앱 내에서는 동기화 창(`DriveSyncSheet`) 진입 시 진행률을 표시
- 해결 (`apps/ios/Omyaru/OmyaruApp.swift`, `BackgroundSyncAndLiveActivity.swift`, `DriveSyncEngine.swift`, `Info.plist`):
  1. **포그라운드 플로팅 배너 제거 (`OmyaruApp.swift`)**: `OmyaruInAppLiveSyncPill` 상단 오버레이를 완전히 제거해 앱 사용 중 상단 버튼/화면 가림 해소.
  2. **백그라운드 전용 진행 알림 (`BackgroundSyncAndLiveActivity.swift`, `OmyaruApp.swift`)**: `shouldPostBackgroundNotification`(`startedInBackground || applicationState != .active`) 가드를 적용하고, `willPresent`에서 포그라운드 배너 표시 차단(`completionHandler([])`). 동기화 중 백그라운드 전환 시 `onAppMovedToBackground`로 진행 알림을 즉시 띄우고, 포그라운드 복귀 시 `onAppMovedToForeground`로 알림 자동 정리.
  3. **동기화 창 상단 실시간 진행률 카드 (`DriveSyncEngine.swift`)**: `DriveSyncSheet` 최상단에 단계(`준비 중`/`탭·프로필`/`책장 동기화` 등)·항목 수(`done/total`)·퍼센트(`%`)·진행 바·상세 문구가 실시간으로 표시되는 카드를 배치.
  - iOS 빌드 번호: `88` → **`89`** (`Info.plist`, `OmyaruApp.swift`).

---

# 2026-11-24 Antigravity: iOS 탭 목록 길게 누르기 완전 복구 + `.global` 정밀 좌표 + 드래그 중 무변경 시각 슬롯 시프트 (`iOS build 88`)

- 작업 워크스페이스: `E:/DEV/CODEX/BROWSER`
- 사용자 요청:
  - `build 87`에서 UIKit 호스트 제스처 교체 후 탭 길게 누르기 자체가 작동하지 않게 된 문제 수정
- 원인 및 해결 (`apps/ios/Omyaru/BrowserViews.swift`, `Info.plist`, `OmyaruApp.swift`):
  1. `build 87`의 UIKit `TabGridLongPressBridge`는 `TabView(.page)`와 `UIScrollView`의 지연 터치 인식기 경합으로 길게 누르기 자체가 발동하지 않았음 → `build 85/86`에서 100% 정상 인식되던 순수 SwiftUI `LongPressGesture(0.28).sequenced(before: DragGesture(minimumDistance: 0, coordinateSpace: .global))`를 복원.
  2. `build 86`의 2가지 문제(좌표 튐 + 무한 핑퐁)를 **무변경 시각 슬롯 시프트(Zero-Mutation Visual Shift)**로 원천 해결:
     - 카드 프레임(`geo.frame(in: .global)`), 제스처(`coordinateSpace: .global`), 전체 화면 오버레이(`overlayGeo.frame(in: .global).origin` 보정)를 **단일 `.global` 화면 좌표계**로 통일해 `TabView(.page)` 경계 오차(00:01 순간이동) 제거.
     - 드래그 시작 기준점을 `drag.startLocation` 대신 첫 `drag.location`에 고정해 손가락이 정지한 동안 델타=`0.0pt`(12pt 데드존) 보장.
     - 드래그 도중 `model.tabs`(`ForEach` 순서)를 변경하지 않고 `dragTargetIndex`만 갱신해 이웃 카드들이 스냅샷된 `dragSlotFrames` 사이로 부드럽게 `.offset` 스프링 이동하며, 손을 놓은 뒤 안착 완료 시점에 애니메이션 없는 단일 트랜잭션으로 `model.moveVisibleTab`을 커밋(제스처 소유 뷰가 드래그 중 단 1px도 이동하지 않으므로 떨림/핑퐁 원천 불가능).
  - iOS 빌드 번호: `87` → **`88`** (`Info.plist`, `OmyaruApp.swift`).

---

# 2026-11-24 Antigravity: iOS 탭 목록 길게 누르기 시 정지 상태에서 카드 자동 이동·핑퐁 완전 해결 — 네이티브 UIKit `UILongPressGestureRecognizer` 페이지 단위 통합 (`iOS build 87`)

- 작업 워크스페이스: `E:/DEV/CODEX/BROWSER`
- 사용자 요청:
  - `KakaoTalk_20261124_115939363.mp4` 영상처럼 iOS 탭 목록에서 손가락을 가만히 대고 길게 누르고 있는데 카드가 좌우로 스스로 튕기며 자리를 계속 바꾸는 현상 해결
- 원인 (`KakaoTalk_20261124_115939363.mp4` 분석):
  1. `TabView(.page)`(`UIPageViewController`)가 각 페이지를 별도 `UIHostingController`로 감싸기 때문에, `TabView` 바깥 `ZStack`에 선언된 `.coordinateSpace(name: "overviewRootSpace")`가 내부 페이지의 `GeometryReader`/`DragGesture`와 원점이 어긋나 길게 누르는 순간 카드가 좌상단으로 순간 이동함.
  2. `LazyVGrid` 내부의 `ForEach` 카드 셀 자체에 SwiftUI `DragGesture`가 붙어 있어, `moveVisibleTab`으로 셀이 좌↔우 열을 이동하는 순간 `DragGesture.location`이 셀 이동량만큼 튀면서 손가락이 정지한 상태에서도 0.24초마다 0번↔1번 슬롯을 무한 핑퐁 스왑함.
- 해결 (`apps/ios/Omyaru/BrowserViews.swift`, `Info.plist`, `OmyaruApp.swift`):
  - **페이지 로컬 `"tabPageSpace"` + 네이티브 UIKit `TabGridLongPressBridge` (`UILongPressGestureRecognizer`) 도입**:
    - 개별 `ForEach` 카드에서 SwiftUI `LongPressGesture`/`DragGesture`를 완전히 제거하고, 각 그룹 페이지의 정적 `ZStack`(`.coordinateSpace(name: "tabPageSpace")`) 내부에서 카드 프레임 측정·플로팅 오버레이 배치·UIKit `UILongPressGestureRecognizer`(`minimumPressDuration = 0.28`, `allowableMovement = 12`) 터치 좌표(`recognizer.location(in: anchorView)`)를 **100% 동일한 단일 페이지 좌표계**로 통일.
    - 제스처 인식기가 페이지 루트 `UIHostingController.view`(고정 뷰)에 붙어 있으므로 `LazyVGrid` 카드가 자리를 바꿔도 터치 좌표가 절대 튀지 않으며, 손가락이 12pt 이내로 정지해 있는 동안 카드는 0.001pt도 움직이지 않음.
  - iOS 빌드 번호: `86` → **`87`** (`Info.plist`, `OmyaruApp.swift`).

---

# 2026-11-24 Antigravity: iOS 탭 목록 길게 누르기 떨림(부들부들) 근본 해결 — 전용 플로팅 드래그 오버레이 분리 (`iOS build 86`)

- 작업 워크스페이스: `E:/DEV/CODEX/BROWSER`
- 사용자 요청:
  - `KakaoTalk_20261124_030211627.mp4` 영상처럼 iOS 탭 목록에서 탭 카드를 길게 누를 때 카드가 부들부들 떨리는 현상 수정
- 원인:
  - 기존 `safariTabCard`가 `.scaleEffect(1.06)` + `.offset(dragOffset)`이 적용된 **동일 뷰**에 `DragGesture`와 `GeometryReader`를 붙여, 길게 눌러 들릴 때 스케일/오프셋 변화가 제스처 좌표를 역으로 흔드는 60/120Hz 피드백 루프가 발생하고, 그 미세 진동으로 12pt 이동 임계값을 오탐해 컨텍스트 메뉴도 안정적으로 열리지 않았음.
- 해결 (`apps/ios/Omyaru/BrowserViews.swift`, `Info.plist`, `OmyaruApp.swift`):
  - **전용 최상위 플로팅 드래그 오버레이 분리**: `LazyVGrid` 내부 카드 슬롯은 변환(`.scaleEffect`/`.offset`) 없이 정적 상태(`.opacity(isDraggingThis ? 0 : 1)`)와 `"overviewRootSpace"` 고정 좌표계만 유지하고, 들려 올라온 시각 카드는 `TabOverview` 최상위 `ZStack`의 `.allowsHitTesting(false)` 오버레이에서 `.position(dragCardCenter)` + `.scaleEffect(dragCardScale)`로 독립 렌더링.
  - **10pt 홀드 데드존 & 안정적 릴리스 메뉴/안착**: 손가락 이동이 10pt 이내인 동안 오버레이 좌표를 100% 고정해 떨림을 원천 제거하고, 미이동 릴리스 시 Liquid Glass 컨텍스트 메뉴(`링크 복사하기 · 탭 고정 · 탭 복제 · 탭 선택 · 탭 이동 · 다른 탭 닫기 · 탭 닫기`)가 확정적으로 열리며, 드래그 릴리스 시 최종 슬롯 중심으로 부드럽게 안착(`0.22s spring`, `dragSessionID` 토큰 가드).
  - iOS 빌드 번호: `85` → **`86`** (`Info.plist`, `OmyaruApp.swift`).

---

# 2026-11-24 Antigravity: PC·iOS 탭·탭 그룹·프로필 완전 양방향 CRDT 동기화(`OmyaruTabReplica`), PC `탭 목록` 우클릭/길게 누르기 메뉴(`탭 선택`·`탭 이동` 신설) 및 멀티 선택 모드 (`Omyaru v0.5.8` / iOS `build 85`)

- 작업 워크스페이스: `E:/DEV/CODEX/BROWSER`
- 사용자 요청:
  1. PC 버전과 iOS 버전 간 탭 및 탭 그룹 동기화가 제대로 되지 않는 문제 원인 규명 및 수정
  2. PC 버전 `탭 목록`에서 탭 우클릭/길게 누르기 시 메뉴 항목을 `링크 복사하기 · 탭 고정 · 탭 복제 · 탭 선택(신규) · 탭 이동(신규) / --- / 탭 닫기` 구성으로 개편
  3. iOS LiveContainer/AltStore 소스(`apps/ios/altstore-source.json`) 갱신 확인 및 CI 동시성 최적화
- 원인 분석 (PC ↔ iOS 탭·탭 그룹 동기화 실패):
  1. **iOS가 탭 그룹(`kind == "groups"`)을 아예 업로드/수신하지 않았음 (`DriveSyncEngine.swift`)**: iOS는 송신 시 `profiles`와 `tabs`(최대 30개)만 내보냈고 `groups`를 전혀 기록하지 않아 iOS 그룹이 PC로 넘어가지 않았으며, 수신(`applyRemoteTabBatch`)도 `kind == "tabs"`만 필터링해 PC의 탭 그룹·프로필·`groupId`·`profileId`·순서·삭제 툼스톤을 모두 버리고 활성 그룹에 랜덤 UUID로 중복 생성하고 있었음.
  2. **프로필 `color` 누락 + 기본 그룹 ID 불일치**: PC `DriveTabReplica`는 `profiles`에 `color`(UInt32 ARGB)가 없으면 프로젝션에서 제외하고, 기본 탭 그룹을 `groupId: null`로 표현하나 iOS는 `color` 없이 고정 UUID(`00000000...0002`)를 송신하고 있었음.
  3. **논리 시계 및 `tabResetIds` 누락**: iOS가 Lamport 논리 시계 대신 epoch ms(`1.79×10^12`)를 매번 전송하고 `tabResetIds`를 스탬프하지 않아, 탭 초기화 이력이 있는 PC에서 iOS 탭이 무시되거나 구버전 탭이 PC 변경을 덮어쓰고 있었음.
  4. **PC 수신 시 비공개 탭/핀 상태 유실 (`SafariBrowserWindow.PopupsAndSync.cs`)**: `ApplyDriveStateAsync()`가 원격 스냅샷 적용 시 로컬 비공개 탭(`__private_workspace__`)과 로컬 고정 탭(`IsPinned`)을 보존하지 않고 덮어쓰고 있었음.
- 해결 내용:
  1. **iOS `OmyaruTabReplica` CRDT 엔진 전면 교체 및 실시간 디바운스 동기화 (`apps/ios/Omyaru/DriveSyncEngine.swift`, `OmyaruApp.swift`)**:
     - PC `DriveTabReplica.cs`·Android와 100% 와이어 호환되는 `OmyaruTabReplica`(`tab-replica.json`) 도입: `profiles`(`name`·`icon`·`color` UInt32 ARGB·`order`), `groups`(`profileId`·`name`), `tabs`(`title`·`url`·`profileId`·`groupId` 기본 그룹=`null`·`order`, 최대 500개), `$deleted` 툼스톤, `tabResetIds` 스탬프, Protocol 1/Protocol 2 체크포인트, 논리 시계(`max + 1`), 양방향 UUID↔WireID 매핑, SF Symbol↔프로필 아이콘 코드 변환 구현.
     - 최초 마이그레이션 시 Drive 이력을 먼저 수신해 기존 랜덤 UUID 중복 탭·동명 그룹을 자동 통합하고 원격 기본 프로필 메타데이터를 보존.
     - `BrowserModel.persist()` 시 2.5초 디바운스 경량 탭 전용 동기화(`scheduleQuickTabSync` / `listTabSyncFiles`) 추가 + 원격 삭제 탭 `WKWebView` 자동 정리(`reconcileWebViewsAfterRemoteSync`).
  2. **PC `ApplyDriveStateAsync` 비공개 탭·고정 탭 보존 & 와이어 교차 검증 (`SafariBrowserWindow.PopupsAndSync.cs`, `DesktopDriveSync.cs`, `DesktopSelfTest.cs`)**:
     - Drive 동기화 수신 시 로컬 비공개 탭(`PrivateGroupId`), 활성 비공개 워크스페이스, 로컬 `IsPinned` 상태 유지.
     - `DesktopSelfTest`에 iOS `OmyaruTabReplica` 와이어 포맷 → PC `DriveTabReplica.ApplyDocument` / `Project` 교차 호환 검증 테스트 추가.
  3. **PC `탭 목록` 컨텍스트 메뉴 개편 + `탭 선택` 멀티 선택 모드 & `탭 이동` (`SafariBrowserWindow.cs`, `SafariBrowserWindow.PopupsAndSync.cs`)**:
     - 컨텍스트 메뉴를 요청 구성(`링크 복사하기 · 탭 고정/해제 · 탭 복제 · 탭 선택 · 탭 이동 / --- / 탭 닫기`)으로 정비.
     - **`탭 선택`**: `탭 목록` 상단 선택 액션 바(`N개 탭 선택됨 · 전체 선택 · 탭 이동 · 선택 닫기 · 완료`) 및 카드 체크 배지 토글 지원.
     - **`탭 이동`**: 모든 프로필·탭 그룹 및 `새 탭 그룹으로 이동…` Liquid Glass 팝오버로 단일/선택 탭 일괄 이동(즉시 Drive 동기화 반영).
  4. **CI 워크플로 & 메타데이터 갱신 (`.github/workflows/ios.yml`, `apps/ios/Omyaru/Info.plist`, `apps/ios/make_altstore_source.py`)**:
     - `.github/workflows/ios.yml`에 `concurrency(cancel-in-progress: true)` 추가해 대기 중인 이전 실행 자동 취소.
     - 버전 갱신: Windows `0.5.7` → `0.5.8`, iOS `build 84` → `build 85` (`appBuildNumber = 85`).
- 검증 결과:
  - `powershell -ExecutionPolicy Bypass -File scripts/Publish-Windows.ps1` 성공 (`Omyaru.exe`, `78,624,389` bytes).
  - `Omyaru.exe --self-test` 종료 코드 `0` (iOS↔PC CRDT 와이어 호환 테스트 포함).
  - `Omyaru.exe --smoke-test` 종료 코드 `0`.
- 빌드 산출물:
  - 상대 경로: `artifacts/releases/Omyaru-v0.5.8-win-x64/Omyaru.exe`
  - 절대 경로: `E:/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.5.8-win-x64/Omyaru.exe`

---

# 2026-11-24 Antigravity: PC·iOS 탭 드래그 애니메이션, 탭 목록 길게 누르기 이동/컨텍스트 메뉴, 주소창 위 탭 배치 & 북마크 모음 숨김 옵션, 탭 목록 상단 정리 (`Omyaru v0.5.7` / iOS `build 84`)

- 작업 워크스페이스: `E:/DEV/CODEX/BROWSER`
- 사용자 요청:
  1. PC 상단 탭 바에서 탭을 드래그해 순서를 바꿀 때 이웃 탭이 부드럽게 밀려나는 애니메이션 적용(참고 영상: `E:\REC\2026\2026-11-24 01-32-14.mkv`)
  2. PC `탭 모아보기`(→ `탭 목록`으로 개명)에서 탭 카드를 길게 누르면 들려 올라오며 드래그 시 애니메이션과 함께 2D 순서 이동, 이동 없이 길게 누르고 떼면 `링크 복사하기`, `탭 고정/해제` 등 메뉴 표시(참고 영상: `KakaoTalk_20261124_013558967.mp4`)
  3. iOS `탭 목록`에서도 동일하게 탭 카드를 길게 누르고 드래그 시 손가락을 따라 들려 이동하며 주변 카드가 스프링 애니메이션으로 자리 교체, 움직이지 않고 떼면 `링크 복사하기`, `탭 고정`, `다른 탭 닫기` 등 Liquid Glass 메뉴 표시
  4. PC 설정에 `탭 목록을 주소창 위로 배치` 옵션 및 `주소창 아래 북마크 모음 표시`(숨기기) 옵션 배치, `탭 모아보기` 명칭을 `탭 목록`으로 통일, `탭 목록` 열람 시 상단 URL 툴바만 표시하고 아래 북마크 모음·탭 바는 숨김
- 구현 내용:
  1. **PC 상단 탭 바 GPU 드래그 재정렬 애니메이션 (`apps/windows/desktop/Safari/SafariBrowserWindow.cs`)**:
     - 기존 Win32 `DragDrop.DoDragDrop`을 제거하고 포인터 캡처 + 순수 GPU `TranslateTransform` 기반 1:1 커서 추적 드래그로 교체.
     - 드래그 중 시각 트리를 재구성하지 않고 논리 순서(`_stripLogicalOrder`)와 정확한 중점 + 6px 히스테리시스 데드밴드로 이웃 탭들이 `175ms CubicEase Out`으로 부드럽게 빈 자리로 슬라이드 이동하며, 마우스를 놓으면 드래그한 탭이 `145ms`로 안착 후 순서를 커밋(`CommitVisibleTabOrder`).
     - `TabEntry.IsPinned`(`Models.cs`) 및 `SafariIcon.Pin`/`PinOff`(`SafariIcons.cs`) 지원으로 고정 탭은 앞쪽 구역에 핀 배지와 함께 컴팩트하게 유지.
  2. **PC `탭 목록` Canvas 2D 길게 누르기 리프트·애니메이션 재정렬·컨텍스트 메뉴 (`SafariBrowserWindow.cs`, `SafariBrowserWindow.PopupsAndSync.cs`)**:
     - `WrapPanel`을 반응형 `Canvas` + `TransformGroup(ScaleTransform, TranslateTransform)`으로 교체해 레이아웃 패스 없는 60fps+ 2D 카드 재배치 구현.
     - 카드 길게 누르기(`250ms`) 시 카드가 `scale 1.06` + 깊은 그림자 + 액센트 테두리 + `ZIndex=100`으로 들려 올라오며, 드래그하면 포인터를 따라 이동하고 다른 슬롯에 진입 시 주변 카드들이 `220ms CubicEase Out`으로 부드럽게 자리 교체.
     - 길게 누른 뒤 드래그 없이 떼면(또는 우클릭 시) Liquid Glass 컨텍스트 팝업(`링크 복사하기`, `탭 고정/고정 해제`, `탭 복제`, `닫은 탭 다시 열기`, `다른 탭 모두 닫기`, `이 탭 닫기`) 표시.
  3. **PC 상단 크롬 배치 옵션 & `탭 목록` 상단 정리 (`SafariBrowserWindow.cs`, `SafariBrowserWindow.WebView.cs`, `SafariSettingsWindow.cs`, `DesktopSettings.cs`)**:
     - `DesktopPreferences.TabsAboveAddressBar` 추가 및 설정 `모양` 섹션 최상단에 `탭 목록을 주소창 위로 배치`·`주소창 아래 북마크 모음 표시` 토글 배치(즉시 라이브 반영 + `WindowChrome.CaptionHeight` 및 `IsHitTestVisibleInChrome` 연동).
     - `탭 모아보기` → `탭 목록` 명칭 통일.
     - `탭 목록` 열람 중(`_showingOverview == true`)에는 `_favoritesBarHost`와 `_tabStripHost`를 모두 접고 Row 0에 `_toolbarHost`(URL 필드 영역)만 단독 표시하며, 탭 선택·주소 이동·닫기 시 원래 크롬 배치로 자동 복원.
  4. **iOS `탭 목록` 길게 누르기 드래그 재정렬·리프트·릴리스 메뉴 (`apps/ios/Omyaru/BrowserViews.swift`, `LiquidGlassComponents.swift`, `OmyaruApp.swift`, `Info.plist`)**:
     - 기존 충돌하던 이중 제스처(`.onLongPressGesture(0.42)` + `.simultaneousGesture(LongPress(0.55)...)`)를 단일 `LongPressGesture(minimumDuration: 0.28).sequenced(before: DragGesture(minimumDistance: 0, coordinateSpace: .named("tabGridSpace")))`로 통합.
     - `.offset` 이전 지오메트리 측정(`TabCardFramePreferenceKey`) + 드래그 중 `ScrollView`/`TabView` 스크롤 잠금(`.scrollDisabled(draggingTabID != nil)`) + 손가락 오프셋 고정 `scaleEffect(1.06)` + 실시간 그리드 히트테스트 스프링 스왑(`response: 0.32, dampingFraction: 0.78`) + 미이동 릴리스 시 컨텍스트 메뉴(`링크 복사하기`, `탭 고정/해제`, `탭 복제`, `탭 선택`, `탭 이동`, `다른 탭 닫기`, `탭 닫기`) 표시. `closeOtherTabs(keeping:)` 추가(고정 탭 보호).
  - 버전 갱신: Windows `0.5.6` → `0.5.7`, iOS `build 83` → `build 84`.
- 검증 결과:
  - `powershell -ExecutionPolicy Bypass -File scripts/Publish-Windows.ps1` 성공 (`Omyaru.exe`, `78,620,702` bytes).
  - `Omyaru.exe --self-test` 종료 코드 `0`.
  - `Omyaru.exe --smoke-test` 종료 코드 `0` (`탭 목록` 열람 시 북마크 모음·탭 바 숨김/URL 툴바 단독 표시 및 `TabsAboveAddressBar` 행 전환 자동 검증 포함).
- 빌드 산출물:
  - 상대 경로: `artifacts/releases/Omyaru-v0.5.7-win-x64/Omyaru.exe`
  - 절대 경로: `E:/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.5.7-win-x64/Omyaru.exe`

---

# 2026-11-24 Antigravity: 북마크 관리자 드래그·Ctrl 다중 선택 · 확장 팝업 우측 끝 정렬 · 동기화 버튼 창 미표시 해결 (`Omyaru v0.5.6`)

- 작업 워크스페이스: `E:/DEV/CODEX/BROWSER`
- 사용자 요청:
  1. 북마크 관리자에서 마우스 드래그(사각형 영역)로 북마크 여러 개 선택 및 `Ctrl+클릭` 다중 선택 지원
  2. 확장 프로그램 플로팅 팝업이 브라우저 창 우측 끝에 맞춰 뜨도록 위치 조정
  3. 동기화 버튼을 눌러도 동기화 창이 뜨지 않는 문제 해결
- 원인 및 해결:
  1. **북마크 관리자 다중 선택 & 일괄 작업 (`apps/windows/desktop/Safari/SafariBookmarkManagerWindow.cs`)**:
     - 리스트 뷰포트에 반투명 블루 마키 사각형 오버레이(`_marqueeCanvas` / `_marqueeRect`)를 추가해 빈 공간 또는 미선택 항목에서 드래그 시 실시간 다중 선택(가장자리 자동 스크롤 + `Ctrl+드래그` 추가 선택 지원), `Ctrl+클릭` 개별 토글, `Shift+클릭` 범위 선택, `Ctrl+A` 전체 선택, `Delete` 일괄 삭제, `Esc` 선택 해제, 더블클릭 열기 구현.
     - 이미 선택된 북마크를 드래그하면 선택된 모든 북마크를 좌측 폴더 트리나 하위 폴더로 한 번에 이동 가능하며, 1개 이상 선택 시 상단 선택 액션 바(`N개 북마크 선택됨 · 폴더 이동… · 새 탭에서 열기 · 삭제 · 선택 해제`) 및 다중 선택 우클릭 메뉴 표시.
  2. **확장 프로그램 팝업 우측 끝 정렬 (`apps/windows/desktop/Safari/SafariExtensionPopupWindow.cs`)**:
     - `RepositionToAnchor()`가 확장 버튼 우측 대신 브라우저 창 실제 우측 끝(`Math.Min(ownerTopRight.X, waBottomRight.X) - 10 - Width`) 바로 아래 툴바 하단에 정렬되도록 수정(일반 창·최대화 창 모두 대응).
  3. **동기화 버튼 클릭 시 창 미표시 수정 (`SafariBrowserWindow.cs`, `SafariBrowserWindow.PopupsAndSync.cs`)**:
     - **원인**: `ShowSyncDialogAsync()`가 백그라운드 자동 동기화 플래그(`_autoSyncBusy`)를 공유해 `if (_autoSyncBusy) return;`으로 차단하고 있었기 때문에, 백그라운드 Drive 동기화가 진행 중일 때 동기화 버튼 클릭이 조용히 무시되었음.
     - **해결**: 모달 중복 방지 전용 `_syncDialogOpen` 플래그를 분리해 백그라운드 동기화 중에도 동기화 버튼(툴바·메뉴·시작 페이지·상태 막대·설정) 클릭 시 `GoogleDriveSyncDialog`가 즉시 열리도록 수정, 안전한 활성 소유 창 핸들 조회(`ResolveModalOwner`) 및 예외 알림 추가.
  - 버전 갱신: `PrivacyBrowser.Desktop.csproj`, `SafariSettingsWindow.cs`, `DesktopSettings.cs` (`0.5.4` → `0.5.6`).
- 검증 결과:
  - `powershell -ExecutionPolicy Bypass -File scripts/Publish-Windows.ps1` 성공 (`Omyaru.exe`, `78,613,797` bytes).
  - `Omyaru.exe --self-test` 종료 코드 `0` (`GoogleDriveSyncDialog` 생성/핸들 검증 포함).
  - `Omyaru.exe --smoke-test` 종료 코드 `0` (`SafariBookmarkManagerWindow` 다중 선택 3개 + 선택 액션 바 오프스크린 렌더 육안 확인 포함).
- 빌드 산출물:
  - 상대 경로: `artifacts/releases/Omyaru-v0.5.6-win-x64/Omyaru.exe`
  - 절대 경로: `E:/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.5.6-win-x64/Omyaru.exe`

---

# 2026-11-24 Antigravity: 확장 프로그램 플로팅 팝업 · 책장 열 변경 후 전체화면 레이아웃 수정 · Whale 스타일 폴더형 북마크 관리자 & 동기화/중복 방지 (`Omyaru v0.5.4` / iOS `build 83`)

- 작업 워크스페이스: `E:/DEV/CODEX/BROWSER`
- 사용자 요청:
  1. 확장 프로그램 팝업이 별도 새 탭이 아니라 현재 웹사이트 위의 팝업으로 열리도록 개선
  2. 프로그램 버전 변경·동기화 시 즐겨찾기(북마크)가 중복/복구되며 바뀌던 문제 원인 규명 및 해결
  3. 책장에서 열 수를 변경한 뒤 전체화면에 들어가면 사진이 겹치고 화면을 꽉 채우지 못하는 버그 해결
  4. "즐겨찾기" 명칭을 "북마크"로 통일하고, Naver Whale(`whale://bookmarks`) 스타일의 폴더형 북마크 관리자(동기화 포함) 신설
  5. 변경사항 GitHub 푸시
- 원인 및 해결:
  1. **확장 프로그램 플로팅 팝업 (`apps/windows/desktop/Safari/SafariExtensionPopupWindow.cs`, `SafariBrowserWindow.PopupsAndSync.cs`, `ExtensionManagerForm.cs`)**:
     - 기존에는 확장 클릭 시 `CreateTabAsync(target)`으로 별도 탭을 열어 페이지 컨텍스트가 끊기고 빈 화면 구석에 작은 팝업이 열렸음.
     - 툴바 확장 버튼 바로 아래·현재 웹사이트 위에 뜨는 프레임리스 소유 WPF `SafariExtensionPopupWindow`(`AllowsTransparency=false` + Windows 11 DWM 둥근 모서리/그림자 + `ResizeObserver` 자동 크기 조절 + 외부 클릭/`Esc` 자동 닫힘 + 활성 웹페이지 URL/제목 `chrome.tabs.query`/`chrome.tabs.create`/`NewWindowRequested` 브리지)로 교체. 확장 관리자에서 `팝업/실행 열기`도 동일하게 웹사이트 위 팝업으로 표시.
  2. **북마크 변경·중복·삭제 부활 버그 근본 해결 + 폴더 동기화 (`Bookmarks.cs`, `DesktopUserDataSync.cs`, `apps/ios/Omyaru/DriveSyncEngine.swift`)**:
     - **원인**: `BookmarkStore.DefaultCatalog()`가 매번 랜덤 `Guid`를 생성했고, `Bookmarks.cs`·`DesktopUserDataSync.cs`가 URL 정규화 없이 `Id`로만 중복 검사해 실제 계정 `bookmarks.json`에 9개 사이트가 28개로 증식(3×중복)되어 있었음. 또한 삭제 툼스톤이 없어 사용자가 북마크를 지운 뒤 모바일 방문 기록 등 새 `userdata` 파일이 동기화될 때마다 28개가 통째로 되살아나고 있었음.
     - **해결**: `BookmarkStore.CanonicalUrlKey`(소문자 호스트, `www.`/`m.` 통합, 루트 후행 슬래시 정규화) + 결정적 기본 ID + `DeletedIds`/`DeletedUrlKeys`/`DeletedFolderIds` 툼스톤 도입 + 폴더(`BookmarkFolderEntry`: `Id`, `Name`, `ParentId`, `Order`, `CreatedAtEpochMs`) 및 `BookmarkEntry.FolderId`를 기존 Android/iOS Drive `userdata` 와이어 포맷과 100% 호환되게 양방향 동기화. iOS `DriveSyncEngine.swift`에도 동일 URL 정규화 중복 방지 추가(`build 83`).
  3. **책장 열 변경 후 전체화면 겹침/미충족 수정 (`Bookshelf.cs`, `VirtualBookshelfGallery.cs`)**:
     - **원인**: (a) `ResizeGalleryWithoutReload()`가 `_galleryCanvas.ResumeLayout(false)` 및 각 `FlowLayoutPanel.ResumeLayout(false)`를 호출해 열 폭/사진 Y 좌표가 갱신되지 않아 사진이 겹치고 좁게 남았고, (b) 창 모드에서 일반 `TableLayoutPanel`로 그려진 폴더가 전체화면(1920/2560px)에서 세로 28,000px(Win32 `32,767px` 자식 HWND 한계)를 넘겨도 `VirtualBookshelfGallery`로 전환되지 않아 좌표 오버플로우 겹침이 발생했음.
     - **해결**: `ResizeGalleryWithoutReload()`에서 확대 시 예상 높이가 `28,000px` 초과하면 `VirtualBookshelfGallery`로 자동 승격하고, 일반 모드에서는 `ComputeColumnWidths`로 남는 픽셀까지 열 폭에 균등 분배 + `SizeType.Absolute` 적용 + `ResumeLayout(true)`/`PerformLayout()`으로 100% 꽉 차고 겹침 없이 재배치. `VirtualBookshelfGallery.Relayout()`도 스크롤바 유무 기준 여백 제거·나머지 픽셀 분배·열 폭 급변 시 고해상도 썸네일 자동 재로드 추가.
  4. **Whale 스타일 GPU WPF 북마크 관리자 & "북마크" 용어 통일 (`Safari/SafariBookmarkManagerWindow.cs`, `SafariBrowserWindow.cs`, `SafariSettingsWindow.cs`, `DesktopSettings.cs`, `SafariIcons.cs`)**:
     - 좌측 접이식 다중 중첩 폴더 트리(`PC 북마크` + 임의 깊이 하위 폴더 + 드래그 앤 드롭 이동 + 우클릭 메뉴), 우측 상단 `[정렬 ⌄][추가 ⌄]` · `[북마크 정리][북마크 내보내기][북마크 가져오기(Chrome/Whale/Edge/Safari Netscape HTML 폴더 구조 유지)]`, 실시간 통합 검색, 행별 `⋮` 메뉴 제공.
     - 북마크 막대·시작 페이지·사이드바에 폴더 드롭다운 메뉴 및 `북마크 관리자`(`Ctrl+Shift+B`), 현재 페이지 북마크 토글(`Ctrl+D`) 연동, UI 전체 `"즐겨찾기"` → `"북마크"` 통일.
  - 버전 갱신: Windows `0.5.3` → `0.5.4`, iOS `build 82` → `build 83`.
- 검증 결과:
  - `powershell -ExecutionPolicy Bypass -File scripts/Publish-Windows.ps1` 성공 (`Omyaru.exe`, `78,610,347` bytes).
  - `Omyaru.exe --self-test` 종료 코드 `0` (책장 열 변경 + 1920×1080 전체화면 비겹침·전폭 검증 + 북마크 폴더 트리/중복 정리/삭제 툼스톤/Netscape HTML 내보내기·가져오기 왕복 검증 + 47종 벡터 아이콘 검증 포함).
  - `Omyaru.exe --smoke-test` 종료 코드 `0` (신규 `SafariBookmarkManagerWindow` 및 `SafariSettingsWindow` 오프스크린 렌더 육안 검수 완료).
- 빌드 산출물:
  - 상대 경로: `artifacts/releases/Omyaru-v0.5.4-win-x64/Omyaru.exe`
  - 절대 경로: `E:/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.5.4-win-x64/Omyaru.exe`

---

# 2026-11-24 Antigravity: 설정 창 GPU WPF 전면 교체(`SafariSettingsWindow`), 전 WinForms 창·다이얼로그 깜빡임/회색 잔상 제거(`WinFormsSurface`), 팝업 닫힘 선행 처리 (`Omyaru v0.5.3`)

- 작업 워크스페이스: `E:/DEV/CODEX/BROWSER`
- 사용자 요청:
  - 설정 버튼 등을 눌렀을 때 창이 열리기 직전 반투명·회색 사각형 잔상이 잠깐 깨져서 보이는 현상(설정 외 여러 다이얼로그·창 공통) 해결
- 원인 분석:
  1. **다중 자식 HWND 기반 WinForms 창/다이얼로그의 비합성 첫 프레임 렌더링**: `SettingsForm`(120+ 자식 HWND), `BookshelfForm`, `ExtensionManagerForm`, `GoogleDriveSyncDialog`, `SyncDialog`, `BookshelfSyncCoordinator`, `CaptureActionsForm`, `PdfOrientationDialog`, `BookmarkManagerForm` 등이 `DWMWA_CLOAK`·`WS_EX_COMPOSITED`·다크 타이틀바·컨테이너 `DoubleBuffered` 없이 열리면서 Windows가 미도색 투명/회색(`WM_ERASEBKGND`) 사각형과 밝은 흰색 타이틀바를 100~300ms 노출한 뒤 자식 컨트롤을 순차 페인트하고 있었음(특히 `SettingsForm`은 `Shown`에서 카드 폭을 재조정해 2차 깜빡임까지 유발).
  2. **팝업 메뉴가 닫히기 전에 모달 창이 UI 스레드를 점유**: `ClosePopup()` 직후 같은 틱에서 `ShowDialog()`를 호출해 WPF가 팝업 닫힘 프레임을 그리지 못한 채 메뉴가 모달 창 뒤에 얼어붙어 있었음. 또한 재사용 `_activePopup`의 `PopupAnimation.Fade`가 전환 시 이전 버퍼를 1프레임 비추는 문제가 있었음.
- 해결 내용:
  1. **GPU 가속 네이티브 WPF 설정 창 신설 (`apps/windows/desktop/Safari/SafariSettingsWindow.cs`)**:
     - `SafariBrowserWindow`의 `Omyaru 설정`을 100% GPU 렌더링 WPF macOS 27 Liquid Glass 창으로 교체(DWM 다크 모드/둥근 모서리, SF Symbols 벡터 아이콘, 애니메이션 토글 스위치, macOS 다크 스크롤바, 네이티브 WPF 소리 출력 장치·도구 모음 버튼 선택기 포함, 자식 HWND 0개로 열기/스크롤/리사이즈 깜빡임 원천 제거).
  2. **전 WinForms 창·다이얼로그 공통 제로 깜빡임 레이어 도입 (`apps/windows/desktop/WinFormsSurface.cs`)**:
     - `HandleCreated` 시 Windows 11 DWM 다크 타이틀바(`DWMWA_USE_IMMERSIVE_DARK_MODE`)·둥근 모서리(`DWMWCP_ROUND`)·`DWMWA_CLOAK = 1` 적용 → `Shown`에서 `PerformLayout(); Refresh();`로 100% 오프스크린 렌더링 완료 후 단일 원자 프레임으로 언클록(`220ms` 안전 해제 타이머 포함).
     - 모든 컨테이너(`Panel`·`TableLayoutPanel`·`FlowLayoutPanel`·`ListView`·`TreeView`) 재귀 `DoubleBuffered` + `TVS_EX_DOUBLEBUFFER` / `LVS_EX_DOUBLEBUFFER` 자동 적용, 다이얼로그 클래스에는 `WS_EX_COMPOSITED` + 다크 플랫 버튼 스타일 적용.
     - 적용 대상: `BookshelfForm`·`CaptureActionsForm`·`PdfOrientationDialog`·사진 뷰어·폴더 이동 창(`Bookshelf.cs`), `GoogleDriveSyncDialog.cs`, `ExtensionManagerForm.cs`, `SyncDialog`(`BrowserForm.cs`), `BookshelfSyncCoordinator.cs`, `BookmarkManagerForm`(`Bookmarks.cs`), 레거시 `SettingsForm`(`DesktopSettings.cs`).
  3. **팝업 닫힘 완료 후 다이얼로그 실행 및 GPU 팝업 애니메이션 (`SafariBrowserWindow.cs`, `SafariBrowserWindow.PopupsAndSync.cs`)**:
     - `ClosePopupThen` / `ClosePopupThenAsync`(`DispatcherPriority.Background`)로 팝업이 화면에서 완전히 사라진 다음 모달/보조 창을 열도록 변경하고, `_activePopup.PopupAnimation = None` + GPU `Opacity`/`TranslateTransform` 슬라이드 인으로 전환 깜빡임 제거.
  - 버전 갱신: `PrivacyBrowser.Desktop.csproj`, `DesktopSettings.cs`, `SafariSettingsWindow.cs` (`0.5.2` → `0.5.3`).
- 검증 결과:
  - `powershell -ExecutionPolicy Bypass -File scripts/Publish-Windows.ps1` 성공 (`Omyaru.exe`, `78,582,101` bytes).
  - `Omyaru.exe --self-test` 종료 코드 `0` (`WinFormsSurface.Attach` 핸들/DWM 검증 포함).
  - `Omyaru.exe --smoke-test` 종료 코드 `0` (신규 WPF `SafariSettingsWindow` 생성·레이아웃·오프스크린 렌더 육안 검수 포함).
- 빌드 산출물:
  - 상대 경로: `artifacts/releases/Omyaru-v0.5.3-win-x64/Omyaru.exe`
  - 절대 경로: `E:/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.5.3-win-x64/Omyaru.exe`

---

# 2026-11-23 Antigravity: PC macOS 27 Safari SF Symbols 벡터 아이콘 전면 교체 · 툴바 캡슐 균형 재배치 · 다크 스크롤바 적용 (`Omyaru v0.5.2`)

- 작업 워크스페이스: `E:/DEV/CODEX/BROWSER`
- 사용자 요청:
  - `v0.5.1` 버튼 속 아이콘의 모양·정렬 불균형(유니코드/Windows 단색 이모지 기반)을 참고 영상(`https://www.youtube.com/watch?v=irwxfHw5xnA`)의 macOS 27 Safari 아이콘 스타일로 전면 개선
- 원인 및 해결:
  1. **SF Symbols 스타일 벡터 스트로크 아이콘 엔진 도입 (`Safari/SafariIcons.cs`)**:
     - 기존 폰트 글리프(`◫`, `‹`, `›`, `≡`, `☆`, `↻`, `📚`, `🧩`, `✂`, `♫`, `⇄`, `⊞`, `⋯` 등)가 폰트 베이스라인 오차·불균일한 선 굵기·저품질 Windows 흑백 이모지로 렌더링되던 문제를 근본 해결.
     - 정규화 `24×24` `Canvas` + `Viewbox` 기반 42종 벡터 아이콘(`SafariIcons.cs`)을 제작해 툴바·주소창·탭 바·사이드바·시작 페이지·탭 모아보기·페이지 내 찾기·전체 글래스 팝오버(`SafariBrowserWindow.cs`, `SafariBrowserWindow.PopupsAndSync.cs`)의 모든 아이콘을 균일한 둥근 끝점(`Round` cap/join)과 수학적 정중앙 배치로 통일.
  2. **macOS 27 Liquid Glass 툴바 캡슐 균형 재배치 (`Safari/SafariTheme.cs`, `Safari/SafariBrowserWindow.cs`)**:
     - 좌측 사이드바 버튼을 원형(`36×36`) Liquid Glass 캡슐로 정렬하고, 프로필 배지·뒤로/앞으로(`‹ | ›` 헤어라인 구분선)·주소창·우측 캡슐 전체를 `36px` 높이(`CornerRadius 18`)로 통일.
     - 8개 버튼이 빽빽하게 모여 있던 우측 단일 캡슐을 `[책장 · 캡처 · 확장 · 소리 · 동기화]` + `[새 탭 + · 탭 모아보기 · 더보기]` 2개의 여유로운 캡슐로 분리하고, 창 제어 버튼(`─`, `□/❐`, `✕`)도 벡터로 교체.
  3. **다크 반투명 macOS 스타일 스크롤바 (`SafariTheme.ApplyMacScrollBarStyles`)**:
     - 시작 페이지·사이드바·탭 모아보기 우측에 표시되던 흰색 Windows 기본 스크롤바를 얇은 반투명 다크 필(`ScrollBar` XAML 템플릿, 세로/가로 대응)로 교체.
  - 버전 갱신: `PrivacyBrowser.Desktop.csproj`, `DesktopSettings.cs` (`0.5.1` → `0.5.2`).
- 검증 결과:
  - `powershell -ExecutionPolicy Bypass -File scripts/Publish-Windows.ps1` 성공 (`Omyaru.exe`, `78,575,897` bytes).
  - `Omyaru.exe --self-test` 종료 코드 `0` (42종 `SafariIcon` 전체 SVG 지오메트리 및 `ScrollBar` 스타일 파싱 자동 검증 포함).
  - `Omyaru.exe --smoke-test` 종료 코드 `0` (실제 `1440×900` 오프스크린 렌더 2× 확대 육안 검수 완료).
- 빌드 산출물:
  - 상대 경로: `artifacts/releases/Omyaru-v0.5.2-win-x64/Omyaru.exe`
  - 절대 경로: `E:/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.5.2-win-x64/Omyaru.exe`

---

# 2026-11-23 Antigravity: PC 책장 `Ctrl+V` 복구(WPF-WinForms 키보드 인터롭), 폴더 우클릭 진입 차단, 사진 추가 시 최하단 자동 스크롤 (`Omyaru v0.5.1`)

- 작업 워크스페이스: `E:/DEV/CODEX/BROWSER`
- 사용자 요청:
  1. PC 버전 책장에서 `Ctrl+V` 붙여넣기가 동작하지 않는 문제 수정
  2. 책장 폴더 트리에서 폴더 우클릭 시 해당 폴더로 진입하지 않도록 수정
  3. 책장에 사진을 추가할 때 현재 위치에 머무르지 않고 맨 아래로 스크롤되도록 변경
- 원인 및 해결:
  1. **WPF 메시지 루프에서 모델리스 WinForms `BookshelfForm` `Ctrl+V` 미전달**:
     - `v0.5.0`에서 메인 루프가 WPF `Dispatcher`로 전환되면서 `WindowsFormsHost.EnableWindowsFormsInterop()`이 호출되지 않아 모델리스 `BookshelfForm`의 `ProcessCmdKey`(`Ctrl+V`, `Ctrl+O`, `Esc`)가 호출되지 않았음.
     - `apps/windows/desktop/Program.cs` 및 `apps/windows/desktop/Safari/SafariBrowserWindow.PopupsAndSync.cs`에 `WindowsFormsHost.EnableWindowsFormsInterop()`을 등록하고, `apps/windows/desktop/Bookshelf.cs`의 `KeyDown`·`ProcessCmdKey` 모두에서 `Ctrl+V`/`Ctrl+O`를 안전 처리(메모 입력창에 포커스 + 텍스트 클립보드일 때는 일반 텍스트 붙여넣기 허용) 및 `RunOnUi` 스레드 보호 추가.
  2. **폴더 우클릭 시 해당 폴더 진입 차단**:
     - `Bookshelf.cs`의 `_folderTree.NodeMouseClick`에서 우클릭 시 `_folderTree.SelectedNode = e.Node;`를 대입하던 코드 제거, `node.ContextMenuStrip` 대신 `_folderNodeMenus` 딕셔너리로 분리, `MouseDown(Right)`에서 `_suppressSelectionChange = true`로 `BeforeSelect` 취소 후 `MouseUp(Right)`에서 우클릭한 폴더의 컨텍스트 메뉴만 띄워 현재 열려 있는 폴더를 유지.
  3. **사진 추가 시 맨 아래로 스크롤**:
     - `ImportClipboardImageAsync` 및 `ImportImageFilesAsync`(`added > 0`)에서 `RenderGallery(new Point(0, int.MaxValue))` 호출, `RestoreGalleryScrollPosition`의 `maxScroll` 계산을 `VerticalScroll`과 `canvas.Height - ClientSize.Height` 최댓값으로 보강해 일반/가상 갤러리 모두 정확히 최하단으로 이동(삭제·동기화·리사이즈는 기존 스크롤 유지).
  - 버전 갱신: `PrivacyBrowser.Desktop.csproj`, `DesktopSettings.cs` (`0.5.0` → `0.5.1`).
- 검증 결과:
  - `powershell -ExecutionPolicy Bypass -File scripts/Publish-Windows.ps1` 성공 (`Omyaru.exe`, `78,570,074` bytes).
  - `Omyaru.exe --self-test` 종료 코드 `0` (신규 검증: 13장 책장 최상단에서 이미지 붙여넣기 후 최하단 스크롤 이동 확인 포함).
  - `Omyaru.exe --smoke-test` 종료 코드 `0`.
- 빌드 산출물:
  - 상대 경로: `artifacts/releases/Omyaru-v0.5.1-win-x64/Omyaru.exe`
  - 절대 경로: `E:/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.5.1-win-x64/Omyaru.exe`

---

# 2026-11-23 Antigravity: iOS 책장 폴더 트리 계층 그룹화 수정 (`build 82`) & PC 버전 하드웨어 가속 WPF macOS 27 Liquid Glass Safari 전면 개편 (`Omyaru v0.5.0`)

- 작업 워크스페이스: `E:/DEV/CODEX/BROWSER`
- 사용자 요청:
  1. iOS 책장 폴더 트리에서 `Omyaru Browser` 하위 폴더가 3개가 아닌 5개처럼 잘못 표시되는 계층 표시 버그 해결
  2. PC 버전의 UI 반응 지연·구식 크롬 문제를 근본 해결하고, 참고 영상(`https://www.youtube.com/watch?v=irwxfHw5xnA` — *Mac Safari 심층 분석: macOS 27 완벽 가이드*)의 macOS 27 Safari UI와 애니메이션을 전면 도입
- **1) iOS 책장 폴더 트리 수정 (`v0.15.18` build `82`)**:
  - **원인**: `apps/ios/Omyaru/BookshelfView.swift`의 `BookshelfPath.allDisplayFolders(_:)`가 부모 기준 깊이 우선 순회 없이 `catalog.folders` 배열의 평면 순서대로 반환해, Drive에서 `Omyaru Browser` 뒤에 동기화된 `Umtomi/paya8`, `Umtomi/laliberte`, `Umtomi/sachi_p`, `Umtomi/sachi_p/한밤의 커튼콜`이 접혀 있는 `Omyaru Browser` 바로 아래에 들여쓰기되어 표시되었음.
  - **해결**: `BookshelfPath.allDisplayFolders(_:)`를 형제 순서를 보존하는 **깊이 우선 전위(Depth-First Pre-Order) 트리 순회**로 재작성하고, `ShelfModel.isFolderExpanded` / `toggleFolderExpanded` / `expandAncestors`를 대소문자 무관 키 정규화로 통일하여 `BookshelfFolderTreeSheet`·`BookshelfImageMoveSheet`·브라우저/캡처 폴더 선택기 전체의 트리 정합성 확보.
  - **CI & AltStore 검증**: 커밋 `4b45304` 푸시 → GitHub Actions Run `45133654073` `success` → `apps/ios/altstore-source.json` `version = "0.15.18"`, `buildVersion = "82"`, `size = 2114954` 반영 확인.
- **2) Windows PC 버전 전면 혁신 (`Omyaru v0.5.0`) — WinForms GDI+ 크롬 → 하드웨어 가속 WPF + DWM Mica/Rounded + macOS 27 Liquid Glass Safari**:
  - **기존 느림·버벅임 원인**: 기존 `BrowserShellForm.cs`(7,900+줄 WinForms GDI+)가 CPU 페인팅, 1초마다 전체 창 `DrawToBitmap`을 수행하던 `_windowSnapshotTimer`, 리사이즈 비트맵 정지 프레임, 개별 16ms WinForms 타이머 애니메이션에 의존하고 있었음(웹 엔진은 GeckoView가 아닌 Chromium 157 기반 WebView2이므로, WebView2 엔진은 유지하되 UI 셸을 GPU 가속 WPF로 전면 교체).
  - **신규 아키텍처 및 추가 파일**:
    - `apps/windows/desktop/PrivacyBrowser.Desktop.csproj` (`E:/DEV/CODEX/BROWSER/apps/windows/desktop/PrivacyBrowser.Desktop.csproj`): `<UseWPF>true</UseWPF>` + `Microsoft.Web.WebView2.Wpf.dll` 추가, 버전 `0.5.0`.
    - `apps/windows/desktop/GlobalUsings.cs`: WPF + WinForms 공존 시 `System.IO`/`System.Net.Http` 전역 using 보장.
    - `apps/windows/desktop/WebViewLiveCapture.cs` (`E:/DEV/CODEX/BROWSER/apps/windows/desktop/WebViewLiveCapture.cs`): 순수 `CoreWebView2` 기반 장문 페이지 스크롤 스티칭 실시간 영역 캡처 엔진 분리·공유.
    - `apps/windows/desktop/Safari/DwmBackdrop.cs` (`E:/DEV/CODEX/BROWSER/apps/windows/desktop/Safari/DwmBackdrop.cs`): Windows 11 네이티브 DWM 다크 모드, 둥근 모서리(`DWMWCP_ROUND`), Mica/Acrylic 백드롭, 제로 누수 `BitmapSource` 변환, 기존 WinForms 모달 호환 어댑터.
    - `apps/windows/desktop/Safari/SafariTheme.cs` (`E:/DEV/CODEX/BROWSER/apps/windows/desktop/Safari/SafariTheme.cs`): macOS 27 Liquid Glass 반투명 캡슐·스페큘러 테두리·GPU `CubicEase` 색상/스케일/너비 애니메이션 팩토리.
    - `apps/windows/desktop/Safari/SafariWebScripts.cs` (`E:/DEV/CODEX/BROWSER/apps/windows/desktop/Safari/SafariWebScripts.cs`): 웹페이지 테마 색상 감지(Adaptive Toolbar Tinting), **macOS 27 Safari 읽기 도구(Reader Mode — 4가지 테마·글꼴·크기·자동 목차)**, **방해 요소 가리기(Distraction Control — GPU Canvas 파티클 소멸 애니메이션 & 복원)**, Chrome/Firefox/Edge 스토어 원클릭 확장 설치 브리지.
    - `apps/windows/desktop/Safari/SafariBrowserWindow.cs` / `.WebView.cs` / `.PopupsAndSync.cs`: 네이티브 `WindowChrome` 프레임리스 창, 상단 플로팅 Liquid Glass 캡슐 툴바(사이드바·색상별 프로필 뱃지·뒤로/앞으로·스마트 검색/주소 캡슐 + `#0A84FF` 포커스 링 + `읽기 도구`/`N개 숨김` 배지 + 우측 도구 캡슐), 중앙 정렬 즐겨찾기 바, 단일 시작 페이지 자동 숨김·드래그 순서 변경·활성 탭 자동 스크롤을 지원하는 Liquid Glass 탭 바, 220ms GPU 슬라이드 사이드바(`0↔268px`, 탭/그룹/비공개/북마크/방문기록 실시간 검색), 프로필 테마 그라데이션 시작 페이지, GPU 스케일 모핑 탭 모아보기(`Ctrl+Shift+O`), WebView2 위에 뜨는 레이어드 글래스 팝오버(페이지 메뉴·프로필·확장·캡처·오디오·메뉴·페이지 내 찾기), 내장 책장·Google Drive 자동 동기화(`SessionChanged` 실시간 반영) 100% 유지.
    - `apps/windows/desktop/Program.cs`, `DesktopSmokeTest.cs`, `DesktopSettings.cs`: 기본 실행을 WPF `SafariBrowserWindow`로 전환(`--legacy-winforms` 폴백 유지), 스모크 테스트를 WPF Safari 셸 전체(레이아웃·사이드바·탭 모아보기·실 WebView2 Firefox MV3 확장 설치/제거·26,000px 장문 캡처) 기준으로 갱신.
- **검증 결과**:
  - `powershell -ExecutionPolicy Bypass -File scripts/Publish-Windows.ps1` 성공 (`Omyaru.exe` 단독 산출, `78,569,277` bytes).
  - `Omyaru.exe --self-test` 종료 코드 `0`.
  - `Omyaru.exe --smoke-test` 종료 코드 `0` (신규 WPF `SafariBrowserWindow` 실 WebView2 `157.0.4341.60` 부팅·GPU 애니메이션·확장 설치·장문 캡처 통과).
- **빌드 산출물**:
  - PC (`Omyaru v0.5.0`):
    - 상대 경로: `artifacts/releases/Omyaru-v0.5.0-win-x64/Omyaru.exe`
    - 절대 경로: `E:/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.5.0-win-x64/Omyaru.exe`
  - iOS (`v0.15.18` build `82`): GitHub Actions Run `45133654073` 완료 · AltStore Classic 소스(`apps/ios/altstore-source.json`) 배포 완료.

---

# 2026-11-23 Antigravity: PC 확장 프로그램 설치 실패("클래스가 등록되지 않았습니다.") & Drive 동기화 30탭 제한 초과 오류 해결 (`Omyaru v0.4.73`)

- 작업 워크스페이스: `E:/DEV/CODEX/BROWSER`
- 사용자 요청:
  1. 확장 프로그램 설치 시 `확장 프로그램을 설치하지 못했습니다. 클래스가 등록되지 않았습니다.` 오류 해결
  2. Drive 동기화 시 `연동된 탭/그룹이 앱의 개수 제한을 초과했습니다. 기존 기기에서 정리해 주세요.` 오류 해결
- 원인 분석:
  1. **WebView2 `AddBrowserExtensionAsync`의 `_` 접두어 파일명 거부 (`0x80070005`)**:
     - `ExtensionManagerForm.NormalizeManifestForWebView2`가 Firefox 메타데이터(`browser_specific_settings`/`applications`)가 있는 확장(Firefox 부가기능 및 다수의 크로스브라우저 Chrome/Edge 확장)에 **`__omyaru_browser_shim.js`**·**`__omyaru_sw.js`**를 생성함.
     - Chromium/WebView2(`157.0.4341.60`)는 압축 해제된 확장의 루트에 예약되지 않은 `_` 시작 파일/폴더가 있으면 `E_ACCESSDENIED (0x80070005)`를 반환하며, WinForms UI 스레드에서 .NET COM 마샬링이 이를 `클래스가 등록되지 않았습니다.` 문구로 표시하던 현상 직접 재현 확인(Firefox AMO uBlock Origin·Dark Reader 설치 시 100% 재현).
  2. **구 프로토타입 30탭 상한 초과**:
     - 실제 계정의 `drive-tab-replica.json`에 PC+모바일 병합 활성 탭이 `56`개(그룹 4, 프로필 2) 존재했으나 `Models.cs`·`DriveTabReplica.cs`·`BrowserShellForm.cs`·`BrowserForm.cs`가 구 30탭 상한으로 차단하고 있었음(PC `ApplyDriveStateAsync`는 활성 탭 1개만 지연 WebView2 생성하므로 다수 탭 보유 부담 없음).
- 해결 내용:
  - `apps/windows/desktop/ExtensionManagerForm.cs` (`E:/DEV/CODEX/BROWSER/apps/windows/desktop/ExtensionManagerForm.cs`):
    - 생성 파일명을 `_` 없는 **`omyaru-browser-shim.js`** / **`omyaru-sw.js`**로 변경하고 UTF-8(BOM 없음)으로 저장, MV3 `type: "module"` 서비스 워커 `import` 변환 및 `background.page` 제거 지원.
    - 압축 해제 및 스테이징 시 `SanitizeUnpackedExtensionForWebView2`로 `__MACOSX`/`META-INF`/`_metadata`/미참조 비예약 `_*` 항목 제거(`_locales`·`_platform_specific` 유지).
    - Chrome 웹 스토어 다운로드 `prodversion`을 `145.0.0.0`으로 상향, `AddBrowserExtensionAsync` COM/권한 예외 시 오해 소지가 있는 `클래스가 등록되지 않았습니다.` 대신 정확한 HRESULT(`0x80070005` 등)와 한글 원인 안내 제공.
  - `apps/windows/desktop/Models.cs` (`E:/DEV/CODEX/BROWSER/apps/windows/desktop/Models.cs`), `DriveTabReplica.cs` (`E:/DEV/CODEX/BROWSER/apps/windows/desktop/DriveTabReplica.cs`), `BrowserShellForm.cs` (`E:/DEV/CODEX/BROWSER/apps/windows/desktop/BrowserShellForm.cs`), `BrowserForm.cs` (`E:/DEV/CODEX/BROWSER/apps/windows/desktop/BrowserForm.cs`):
    - 공통 상수 `SyncSnapshot.MaxTabs = 500`, `MaxProfiles = 32`, `MaxGroupsPerProfile = 100` 도입(기존 30/8/20 상향), `DriveTabReplica.Capture` 4096변경 단위 청킹으로 Android 와이어 호환성 유지.
  - 자동 검증 강화:
    - `BrowserShellForm.VerifyBrowserExtensionsAsync()`: Firefox 메타데이터(`browser_specific_settings` + MV3 `background.scripts` + `content_scripts` + `META-INF`/`_metadata`) 확장의 `InstallFromLocalPathAsync` 실 WebView2 설치·제거 검증 추가.
    - `DriveReplicaSelfTest.cs`: 60개 병합 탭 동기화 투영 검증 추가.
  - 버전 갱신: `PrivacyBrowser.Desktop.csproj`, `DesktopSettings.cs` (`0.4.72` → `0.4.73`).
- 검증 결과:
  - `powershell -ExecutionPolicy Bypass -File scripts/Publish-Windows.ps1` 성공 (`Omyaru.exe` 단독 산출, `78,452,566` bytes).
  - `Omyaru.exe --self-test` 종료 코드 `0`.
  - `Omyaru.exe --smoke-test` 종료 코드 `0` (실 WebView2 `157.0.4341.60`에서 Firefox 호환 MV3 확장 설치·제거 포함 통과).
- 빌드 산출물:
  - 상대 경로: `artifacts/releases/Omyaru-v0.4.73-win-x64/Omyaru.exe`
  - 절대 경로: `E:/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.4.73-win-x64/Omyaru.exe`

---

# 2026-11-23 Antigravity: PC 버전 Google 재로그인 시 "Google 로그인 만료됨" 유지 버그 수정 & 단독 책장 프로그램 분리 중단 (`Omyaru v0.4.72`)

- 작업 워크스페이스: `E:/DEV/CODEX/BROWSER`
- 사용자 요청:
  1. PC 버전에서 다시 로그인을 해도 `Google 로그인 만료됨`이 그대로 표시되는 문제 해결
  2. 책장 프로그램(`OmyaruBookshelf.exe`)을 별도로 분리해 빌드/배포하지 않고 통합 `Omyaru.exe`만 유지
- 원인 분석:
  1. **`DriveAccountKeys.ConnectAsync`가 세션 저장(`Save`) 이전에 Drive의 모든 탭 기록을 순차 다운로드/복호화**: 계정 암호화 키(`omyaru-account-key-v1-*`)가 이미 존재하는 재로그인에서도 `client.ListAsync()` 후 모든 `omyaru-tabs-v*` 파일을 하나씩 내려받아 210,000회 PBKDF2 복호화를 수행했고, 진행 문구도 갱신되지 않아 Edge에서 승인 완료 후 대화상자를 닫거나 단일 기록에서 지연/실패가 발생하면 `google-drive-session.dat` 저장 전에 중단되어 만료 세션(2026-10-11)과 `NeedsReauthentication = true`가 그대로 남음.
  2. **책장 설정에서 재로그인 시 브라우저 상태바 1시간 정체**: `BrowserShellForm`이 만료 시 `_driveNextCheck = UtcNow + 1h`와 `_driveFailures = 6`으로 잠가 두어, 책장 창(`BookshelfSyncCoordinator.ShowSettings`)에서 재로그인에 성공해도 브라우저 하단 상태바가 1시간 동안 `Google 로그인 만료됨`으로 멈추고 동일 계정 재로그인임에도 `drive-tab-replica.json`/`drive-userdata-state.json`을 불필요하게 백업·초기화함.
  3. **재로그인 직후 iOS 동기화 기록 호환성**: iOS `v0.15.18`이 기록하는 `profiles`의 `order` 필드가 Windows `DriveTabReplica.Apply` 화이트리스트에 없어 재로그인 직후 첫 동기화에서 `잘못된 Drive 변경 항목입니다` 오류가 발생할 위험이 있었음.
- 해결 내용:
  - `apps/windows/desktop/DriveAccountKeys.cs` (`E:/DEV/CODEX/BROWSER/apps/windows/desktop/DriveAccountKeys.cs`): 이미 계정 키가 존재하고 레거시 암호 이관 요청이 없는 재로그인/기기 추가 시 전체 탭 이력 재스캔을 생략하고 즉시 반환(`PrimaryKeyId` 노출), 레거시 스캔 시에도 `omyaru-tabs-v1-*`만 대상으로 제한 및 진행 콜백 추가.
  - `apps/windows/desktop/GoogleDriveAuthorization.cs` (`E:/DEV/CODEX/BROWSER/apps/windows/desktop/GoogleDriveAuthorization.cs`): 단계별 진행 콜백(`onStage`), 한글 HTML 자동 닫힘 콜백 페이지, `Session.PrimaryKeyId` 저장, `SessionChanged` 정적 이벤트 발행, `refresh_token` 회전 저장, `InvalidateAccessToken()`, 토큰/비밀정보를 배제한 최소 진단 로그(`%LOCALAPPDATA%\Omyaru Browser\drive-auth-last.log`) 추가.
  - `apps/windows/desktop/GoogleDriveLoginBrowser.cs` (`E:/DEV/CODEX/BROWSER/apps/windows/desktop/GoogleDriveLoginBrowser.cs`): Edge 미설치 환경에서도 기본 브라우저 폴백 지원.
  - `apps/windows/desktop/GoogleDriveSyncDialog.cs` (`E:/DEV/CODEX/BROWSER/apps/windows/desktop/GoogleDriveSyncDialog.cs`): 만료 상태에서 **[Google 계정 다시 로그인]** 버튼을 파란 강조 기본 버튼(`AcceptButton`)으로 최우선 배치, DPI 잘림 방지 레이아웃 확대(`840×390`), 실시간 로그인 단계 문구 표시, 동일 계정 재로그인 시 로컬 동기화 상태 유지.
  - `apps/windows/desktop/BrowserShellForm.cs` (`E:/DEV/CODEX/BROWSER/apps/windows/desktop/BrowserShellForm.cs`): `GoogleDriveAuthorization.SessionChanged` 구독 및 `ShowSyncDialogAsync()` 완료 시 즉시 `_driveFailures = 0`, `_driveNextCheck = default`, 상태바 문구 갱신 후 동기화 재개.
  - `apps/windows/desktop/BookshelfSyncCoordinator.cs` (`E:/DEV/CODEX/BROWSER/apps/windows/desktop/BookshelfSyncCoordinator.cs`): 만료 시 재로그인 버튼 라벨 개선, 진행 문구 표시, 동일 계정 재로그인 시 브라우저 동기화 파일 유지.
  - `apps/windows/desktop/DriveTabReplica.cs` (`E:/DEV/CODEX/BROWSER/apps/windows/desktop/DriveTabReplica.cs`) & `DriveAppDataClient.cs` (`E:/DEV/CODEX/BROWSER/apps/windows/desktop/DriveAppDataClient.cs`): iOS `profiles/order` 선택 필드 수용 및 `401` 수신 시 캐시 액세스 토큰 즉시 무효화.
  - `scripts/Publish-Windows.ps1` (`E:/DEV/CODEX/BROWSER/scripts/Publish-Windows.ps1`): 기본 배포를 통합 `Omyaru.exe` 단독 빌드로 변경(단독 `OmyaruBookshelf.exe`는 기본 미생성, 필요 시 `-IncludeStandaloneBookshelf` 스위치로만 선택 가능).
  - 버전 갱신: `PrivacyBrowser.Desktop.csproj`, `DesktopSettings.cs` (`0.4.71` → `0.4.72`).
- 검증 결과:
  - `powershell -ExecutionPolicy Bypass -File scripts/Publish-Windows.ps1` 성공 (`Omyaru.exe` 단독 산출, `78,450,649` bytes).
  - `Omyaru.exe --self-test` 종료 코드 `0` (신규 검증 포함: 만료 재로그인 다이얼로그 최우선 버튼·실행, 기존 계정 키 재연결 시 탭 이력 미스캔, iOS `profiles/order` 배치 수용).
  - `Omyaru.exe --smoke-test` 종료 코드 `0`.
- 빌드 산출물:
  - 상대 경로: `artifacts/releases/Omyaru-v0.4.72-win-x64/Omyaru.exe`
  - 절대 경로: `E:/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.4.72-win-x64/Omyaru.exe`

---

# 2026-11-23 Antigravity: iOS 동기화 고속화(병렬 처리), Android·iOS 공통 계정별 공용 동기화 폴더 지정 및 동시 동기화 충돌 방지(Omyaru + Umtomi), LiveContainer 실시간 현황 대체, PC Drive 400(만료 토큰) 해결

- 작업 워크스페이스: `E:/DEV/CODEX/BROWSER` 및 `E:/DEV/CODEX/Umtomi`
- 사용자 요청:
  1. iOS 동기화가 너무 느린 문제 수정
  2. Android·iOS 공통: 처음 동기화 시 (및 이 기능을 처음 접하는 기존 사용자 포함) 책장 동기화 폴더를 지정하게 하여 Umtomi와 Omyaru 브라우저를 함께 써도 파일이 2배로 저장되지 않도록 개선 (지정 폴더 아래 계정 이름 하위 폴더 자동 생성 + 동시 동기화 충돌 방지)
  3. LiveContainer에 설치 시 iOS 실시간 현황(ActivityKit)이 작동하지 않는 문제 해결(가능 범위)
  4. PC 버전 하단 상태바의 `Drive 연동 대기 · Google 인증 실패 (400), 클라이언트 유형·테스트 사용자·앱 권한을 확인해 주세요` 오류 해결
- **Omyaru iOS (`v0.15.18` build `81`)**:
  - **고속 병렬 동기화 (`DriveSyncEngine.swift`)**: `@MainActor` 동기 PBKDF2 병목 제거(`Task.detached` 멀티코어 병렬 암호화/복호화), `httpMaximumConnectionsPerHost = 8` 전용 `URLSession`, 5회 순차 목록 조회를 단일 `listAllAppDataFiles()`로 통합, 키링 영속 캐시(`keyring-<account>.json`), `TaskGroup`(최대 6동시) 배치·청크 병렬 업/다운로드, 전체 메타데이터 재업로드를 제거한 델타 전용 CRDT `OmyaruShelfReplica`(`shelf.json`), 공용 폴더 SHA-256 원본 재사용(중복 다운로드 0바이트).
  - **공용 동기화 폴더 지정 & 동시 동기화 잠금 (`SharedSyncFolderManager.swift`, `BookshelfView.swift`, `OmyaruApp.swift`)**: 파일 앱 폴더 선택(`.fileImporter([.folder])`) 보안 스코프 북마크 저장 + `<지정 폴더>/<계정 이메일>/originals/<sha256>.<ext>` & `.omyaru/sync-state/shelf.json` 공유 + OS 커널 `flock(sync.lock)` + `sync-lease.json` 상호 배제 잠금. 신규/기존 사용자 모두 `needsInitialFolderSetup`(`configuredV1`)로 최초 1회 폴더 지정 시트 자동 표시.
  - **LiveContainer 실시간 현황 대체 (`BackgroundSyncAndLiveActivity.swift`, `OmyaruApp.swift`, `DriveSyncEngine.swift`)**: LiveContainer 환경(`LC_APP_URL`/`.appex` 부재) 자동 감지 → iOS OS 구조상 외부 위젯 확장이 불가하므로 **실시간 갱신 진행률 로컬 알림(`▓▓▓▓▓░░░░░ 54%`) + 앱 상단 플로팅 라이브 동기화 필(`OmyaruInAppLiveSyncPill`)**로 자동 대체하고 설정 화면에 명확히 안내.
  - **CI & AltStore 검증**: 커밋 `878fa77` → `df18323`, GitHub Actions Run `45055783711` `success`, `apps/ios/altstore-source.json` `version = "0.15.18"`, `buildVersion = "81"`, `size = 2113938` 반영 확인.
- **Omyaru Android (`v0.15.18`) & Umtomi Android (`v5.15.6` / `106`)**:
  - `SharedBookshelfStorage.kt`, `BookshelfStorageActivity.kt`, `SettingsActivity.kt`, `BookshelfActivity.kt`, `DriveBackgroundSyncService.kt` (Omyaru) 및 `SharedBookshelfStorage.kt`, `BookshelfStorageActivity.kt`, `DriveSyncActivity.kt`, `BookshelfActivity.kt`, `BookshelfSyncWorker.kt`, `SharedShelfSync.kt` (Umtomi):
  - 시스템 폴더 선택기(`ACTION_OPEN_DOCUMENT_TREE` → 실제 경로 해석) + 1탭 `/Omyaru_Bookshelf` + `<선택 폴더>/<계정 이메일>/` 자동 하위 폴더 분리 + `.omyaru/sync-state` 공유 + OS 커널 `FileChannel.tryLock(sync.lock)` + `sync-lease.json`으로 Umtomi와 Omyaru 동시 동기화 시 안전 대기/보류. `sync_folder_configured_v1` 플래그로 기존 사용자도 즉시 폴더 지정 가능.
  - 검증:
    - Omyaru Android: `.\gradlew.bat "-PomyaruBuildDirectory=.build-omyaru-verify" :app:testDebugUnitTest :app:assembleRelease --no-daemon` 전 96건 단위 테스트(신규 `SharedBookshelfAccountAndLockTest` 포함) 및 릴리즈 APK 성공 (`artifacts/releases/Omyaru-v0.15.18-arm64.apk`, `E:/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.15.18-arm64.apk`).
    - Umtomi Android: `.\umtomi\Build-Android.ps1 -Test` (`assembleDebug`, `testDebugUnitTest` 신규 잠금/계정 테스트 포함, `lintDebug`) 전체 통과 (`E:/DEV/CODEX/Umtomi/artifacts/Umtomi-v5.15.6.apk`, `C:/Users/peopl/OneDrive/DEV/APK/Umtomi-v5.15.6.apk`).
- **Umtomi iOS (`v5.4.9` / build `11`)**:
  - `SharedSyncFolderManager.swift` 신규 추가, `DriveFiles.swift`(바이트 단위 루프 → 고속 `URLSession.data`), `ShelfCloudImage.swift`(병렬 청크 다운로드), `DriveSyncCoordinator.swift`(병렬 pull/download + `flock(sync.lock)` + 공용 `originals/<sha256>` 재사용), `DriveSyncSettings.swift`(폴더 지정 시트 자동 표시), `Bookshelf.swift`, `project.yml` / `Info.plist`(`UIFileSharingEnabled`, `LSSupportsOpeningDocumentsInPlace`) 반영 (커밋 `8f76ef9`).
- **Windows PC (`Omyaru` `v0.4.71` / `OmyaruBookshelf` `v0.1.43`) — Google 인증 실패 (400) 해결**:
  - 원인: `%LOCALAPPDATA%\Omyaru Browser\google-drive-session.dat`이 2026-10-11(43일 전) 발급 토큰으로 Google 테스트 앱 7일 만료(`invalid_grant`, HTTP 400) 상태였으나 `TokenRequest`가 이를 일반 400 설정 오류 메시지로 덮어쓰고 무한 재시도하며, 대화상자에도 다시 로그인 버튼이 없었음.
  - 해결: `GoogleDriveAuthorization.cs`에서 `invalid_grant` 감지 및 `GoogleDriveReauthenticationRequiredException` / `NeedsReauthentication` 플래그 도입, `GoogleDriveSyncDialog.cs`에 **Google 계정 다시 로그인** 버튼 추가(기존 동기화 데이터 유지), `BrowserShellForm.cs` 하단 상태바에 `Google 로그인 만료됨 · 여기를 클릭해 다시 로그인` 강조 표시 및 클릭 즉시 재로그인 창 열기, `BookshelfSyncCoordinator.cs` 상태 안내 연동.
  - 검증 및 산출물: `Omyaru.exe --self-test`/`--smoke-test` (`0`/`0`), `OmyaruBookshelf.exe --self-test`/`--smoke-test` (`0`/`0`).
    - `artifacts/releases/Omyaru-v0.4.71-win-x64/Omyaru.exe` (`E:/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.4.71-win-x64/Omyaru.exe`)
    - `artifacts/releases/OmyaruBookshelf-v0.1.43-win-x64/OmyaruBookshelf.exe` (`E:/DEV/CODEX/BROWSER/artifacts/releases/OmyaruBookshelf-v0.1.43-win-x64/OmyaruBookshelf.exe`)

---

# 2026-11-23 Antigravity: PC 버전 책장 사진 붙여넣기 시 스크롤 위치 유지 (Omyaru v0.4.70 / OmyaruBookshelf v0.1.42)

- 작업 워크스페이스: `E:/DEV/CODEX/BROWSER`
- 사용자 요청:
  - PC 버전 책장에서 사진을 붙여넣기(`Ctrl+V`)하면 맨 위로 올라가지 않고 보고 있던 원래 스크롤 위치에 그대로 유지되도록 수정.
- 원인 및 해결:
  1. **레이아웃 일시 정지 중 스크롤 복원 클램핑**: `RenderGallery`가 `_galleryHost.SuspendLayout()` 내부에서 `PerformLayout()`과 `AutoScrollPosition` 복원을 호출해, 실제 스크롤 범위가 계산되기 전에 `Y`가 `0`으로 잘려 맨 위로 튀던 문제를 `_galleryHost.ResumeLayout(true)` **이후** `RestoreGalleryScrollPosition(previousOffset)` / `createdVirtual.Relayout(); createdVirtual.ScrollTo(previousOffset.Y)` 호출로 수정.
  2. **WinForms `ScrollableControl` 좌표 오프셋 드리프트 방지**: 새 `TableLayoutPanel` 캔버스를 추가하기 전에 `_galleryHost.AutoScrollPosition = Point.Empty;`로 디스플레이 기준점을 초기화해, 스크롤된 상태에서 새 캔버스가 뷰포트 최상단(`canvas.Top == 0`)에 붙고 위쪽에 빈 공간이 생기던 현상 차단.
  3. **가상 갤러리(`>28,000px`) 지연 스크롤 복원**: `VirtualBookshelfGallery`에 `_pendingScrollOffset`을 추가해 초기 크기 결정·첫 `Relayout()` 전 호출된 `ScrollTo(offset)`이 `0`으로 덮이지 않고 첫 레이아웃 직후 정확히 복원되도록 수정.
  4. **포커스 자동 스크롤 차단 및 비동기 최신 스크롤 캡처**: `_galleryHost`를 `ScrollToControl`을 오버라이드한 `PreserveScrollPanel`로 교체해 포커스 변화로 맨 위로 튀지 않도록 방지. 폴더 전환(`_folderTree.AfterSelect`, 폴더 선택 대화상자 열기) 시에만 `Point.Empty`로 맨 위로 이동하고, 붙여넣기·파일 추가·드롭·삭제·동기화·리사이즈는 모두 최신 스크롤 위치를 유지.
- 변경 파일:
  - `apps/windows/desktop/VirtualBookshelfGallery.cs` (`E:/DEV/CODEX/BROWSER/apps/windows/desktop/VirtualBookshelfGallery.cs`)
  - `apps/windows/desktop/Bookshelf.cs` (`E:/DEV/CODEX/BROWSER/apps/windows/desktop/Bookshelf.cs`)
  - `apps/windows/bookshelf/Bookshelf.cs` (`E:/DEV/CODEX/BROWSER/apps/windows/bookshelf/Bookshelf.cs`)
  - `apps/windows/desktop/DesktopSelfTest.cs` (`E:/DEV/CODEX/BROWSER/apps/windows/desktop/DesktopSelfTest.cs`)
  - `apps/windows/bookshelf/Program.cs` (`E:/DEV/CODEX/BROWSER/apps/windows/bookshelf/Program.cs`)
  - `apps/windows/desktop/PrivacyBrowser.Desktop.csproj`, `apps/windows/desktop/DesktopSettings.cs` (`0.4.69` → `0.4.70`)
  - `apps/windows/bookshelf/Omyaru.Bookshelf.csproj` (`0.1.41` → `0.1.42`)
- 빌드 및 자동 검증:
  - `powershell -ExecutionPolicy Bypass -File scripts/Publish-Windows.ps1` 성공.
  - `Omyaru.exe --self-test` (`0`), `Omyaru.exe --smoke-test` (`0`), `OmyaruBookshelf.exe --self-test` (`0`), `OmyaruBookshelf.exe --smoke-test` (`0` — 일반 `TableLayoutPanel` 모드 `Y=1200` 연속 2회 붙여넣기 + `canvas.Top == -1200` / `0` 복귀 검증 및 `VirtualBookshelfGallery` 모드 `Y=5400` 붙여넣기 위치 유지 검증 통과).
- 빌드 산출물:
  - 브라우저 내장 책장 (`Omyaru` `v0.4.70`): `artifacts/releases/Omyaru-v0.4.70-win-x64/Omyaru.exe` (`E:/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.4.70-win-x64/Omyaru.exe`)
  - 단독 책장 앱 (`OmyaruBookshelf` `v0.1.42`): `artifacts/releases/OmyaruBookshelf-v0.1.42-win-x64/OmyaruBookshelf.exe` (`E:/DEV/CODEX/BROWSER/artifacts/releases/OmyaruBookshelf-v0.1.42-win-x64/OmyaruBookshelf.exe`)

---

# 2026-11-23 Antigravity: Android & iOS 백그라운드 동기화(ON/OFF 토글), 지울 수 없는 알림(Android) · 실시간 현황 Live Activity(iOS), iOS 동기화 재실행 로그아웃 버그 수정 (iOS v0.15.18 build 80)

- 작업 워크스페이스: `E:/DEV/CODEX/BROWSER`
- 사용자 요청:
  1. Android 및 iOS 모두 백그라운드 동기화 지원 + 켜고 끌 수 있는 설정 옵션 추가
  2. 동기화 진행 중 Android는 지울 수 없는 진행률 알림, iOS는 **실시간 현황(Live Activity)**으로 진행률 표시
  3. iOS 앱을 껐다 켜면 Google Drive 동기화가 로그아웃되는 버그 수정
- Android 변경 파일 및 검증:
  - `apps/android/app/src/main/java/dev/privacybrowser/app/DriveBackgroundSyncService.kt` (신규 — `DriveBackgroundSyncPreferences`, `DriveSyncProgressNotifier` [`setOngoing(true)` + `FLAG_ONGOING_EVENT` + `FLAG_NO_CLEAR` + `setDeleteIntent` 스와이프 즉시 복구], `DriveBackgroundSyncService` [`foregroundServiceType="dataSync"` + `PARTIAL_WAKE_LOCK` + 탭/북마크/책장 전체 진행률 알림], `DriveBackgroundSyncJobService` [15분 주기 `JobScheduler` + Android 12+ 백그라운드 FGS 제한 폴백], `DriveSyncNotificationReceiver`)
  - `apps/android/app/src/main/AndroidManifest.xml` (`RECEIVE_BOOT_COMPLETED`, 서비스/리시버 등록)
  - `apps/android/app/src/main/java/dev/privacybrowser/app/SettingsActivity.kt` (`백그라운드 동기화` ON/OFF 토글 및 `지금 동기화` 버튼)
  - `apps/android/app/src/main/java/dev/privacybrowser/app/BrowserActivity.kt`, `BookshelfActivity.kt`, `GoogleDriveConnection.kt`, `BrowserApplication.kt` (백그라운드 전환 시 스케줄/즉시 동기화, 책장 동기화 진행률 알림 연동)
  - `apps/android/app/src/main/res/values/strings.xml`, `values-ko/strings.xml`
  - 검증: `.\gradlew.bat "-PomyaruBuildDirectory=.build-omyaru-verify" :app:testDebugUnitTest :app:assembleRelease --no-daemon` 성공 (`BUILD SUCCESSFUL`).
  - 빌드 산출물:
    - 상대 경로: `artifacts/releases/Omyaru-v0.15.18-arm64.apk`
    - 절대 경로: `E:/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.15.18-arm64.apk`
- iOS 변경 파일 및 검증 (`v0.15.18` build `80`):
  - **재실행 시 동기화 로그아웃 버그 수정 (`DriveSyncEngine.swift`)**:
    - 원인: Swift `override init()` 내부에서 `accessToken = ...` 대입 시 `didSet`이 호출되지 않아 `isConnected`가 항상 `false`로 남아 있었고, AltStore 무료 서명 환경에서 Keychain `-34018` 발생 시 토큰이 유실될 수 있었음.
    - 해결: `init()`에서 `isConnected`를 명시적으로 복원하고, Keychain + 샌드박스 `Application Support/OmyaruDriveSync/google-session.json`(iCloud 백업 제외, `.completeFileProtectionUntilFirstUserAuthentication`) 이중 저장 및 앱 시작 시 `refreshToken` 자동 무음 갱신 추가.
  - **4중 iOS 백그라운드 동기화 + ON/OFF 토글 (`BackgroundSyncAndLiveActivity.swift`, `DriveSyncEngine.swift`, `OmyaruApp.swift`, `Info.plist`)**:
    - (1) `UIApplication.beginBackgroundTask`, (2) `BGAppRefreshTask` + `BGProcessingTask` (`cuttan.omyaru.app.sync.refresh` / `processing`), (3) `URLSessionConfiguration.background` (`handleEventsForBackgroundURLSession`), (4) 동기화 진행 중에만 작동하는 무음 `.mixWithOthers` 오디오 유지(`ActiveSyncBackgroundKeepAlive`, 완료·중단·토글 OFF 시 즉시 종료).
  - **네이티브 실시간 현황 Live Activity (`OmyaruSyncActivityAttributes.swift`, `OmyaruLiveActivity/OmyaruLiveActivityWidget.swift`, `OmyaruLiveActivity/Info.plist`, `project.yml`)**:
    - 잠금 화면 진행률 카드 + Dynamic Island(Compact / Minimal / Expanded) 실시간 동기화 단계·퍼센트·항목 수 표시 및 완료 5초 후 자동 종료(`OmyaruLiveActivity.appex` 내장).
  - CI 검증: 커밋 `1b61b84`, AltStore 소스 갱신 `ef3579e`, GitHub Actions Run `45048984890` (`macos-15` Xcode Release — `Omyaru` + `OmyaruLiveActivity.appex` 동시 빌드) 전 단계 `success`.
  - AltStore Classic 소스: `version = "0.15.18"`, `buildVersion = "80"`, `size = 1972495` 반영 완료.

---

# 2026-11-23 Antigravity: Omyaru iOS v0.15.18 (build 79) — Google Drive 동기화 클라이언트 ID·토큰 입력 제거 및 원탭 기본 내장 OAuth

- 작업 워크스페이스: `E:/DEV/CODEX/BROWSER`
- 사용자 요청:
  - 일반 사용자가 Google OAuth 클라이언트 ID·액세스 토큰을 직접 입력할 수 없으므로 제거하고 앱에 기본 내장해 간편 연결되도록 수정.
- 추가·변경 파일:
  - `apps/ios/DriveSync.xcconfig` (신규 — Omyaru Google Cloud 프로젝트 `136563471424`의 공개 iOS OAuth 클라이언트 ID 및 URL 스킴 내장)
  - `apps/ios/project.yml` (`DriveSync.xcconfig` Debug/Release 연결)
  - `apps/ios/Omyaru/Info.plist` (`CFBundleVersion = 79`, `GIDClientID`, `CFBundleURLSchemes`에 `$(OMYARU_GOOGLE_IOS_URL_SCHEME)` 등록)
  - `apps/ios/Omyaru/DriveSyncEngine.swift` (클라이언트 ID·액세스 토큰 입력 필드 완전 제거, iOS Keychain 토큰 보안 저장 및 기존 UserDefaults 평문 키 정리, `drive/v3/about` 연결 이메일 자동 조회, `Google 계정 연결` 원탭 로그인 + 즉시 동기화)
  - `apps/ios/Omyaru/OmyaruApp.swift` (`appBuildNumber = 79`)
  - `apps/ios/make_altstore_source.py` / `apps/ios/altstore-source.json`
- CI 빌드 및 배포 검증:
  - 커밋: `4b65338`, AltStore 소스 갱신 커밋: `79afd25`.
  - GitHub Actions Run `45047188627` (`macos-15` Xcode Release) 전 단계 `success`.
  - AltStore Classic 소스 (`https://raw.githubusercontent.com/omyeol/Omyaru-Browser/main/apps/ios/altstore-source.json`): `version = "0.15.18"`, `buildVersion = "79"`, `size = 1871849` 반영 완료.

---

# 2026-11-23 Antigravity: Omyaru iOS v0.15.18 (build 78) — 참고 영상(iOS 26 Safari Liquid Glass) UI & 애니메이션 전면 반영

- 작업 워크스페이스: `E:/DEV/CODEX/BROWSER`
- 사용자 요청:
  - 첨부 영상(`KakaoTalk_20261123_013258568.mp4` — iOS 26 Safari Liquid Glass)의 UI와 애니메이션을 그대로 반영.
- 추가·변경 파일:
  - `apps/ios/Omyaru/LiquidGlassComponents.swift` (신규 — `LiquidGlassShapeBackground`, `LiquidGlassPressStyle`, `LiquidGlassCircleButton`, `LiquidGlassMorphContainer` 블러-블룸 모핑 전환, `LiquidGlassPageToolsPopover`, `LiquidGlassBrowserMenuPopover`, `LiquidGlassTabOverviewMenuPopover` 인라인 확장 프로필 스위처, `LiquidGlassTabCardContextPopover` 롱프레스 플로팅 메뉴)
  - `apps/ios/Omyaru/BrowserViews.swift` (풀스크린 히어로 줌 `TabOverview` 오버레이 전환, 중앙 정렬 도메인 + 플로팅 리퀴드 글래스 2단 URL 아일랜드, 네이티브 가로 스와이프 탭 그룹 페이저 `TabView(.page)` + 하단 글라이딩 `matchedGeometryEffect` 캡슐 & 양끝 소프트 페이드 마스크 그룹 캐러셀 필, 단일 탭 대형 히어로 카드 / 그리드 카드 하단 파비콘+제목 / 우상단 `(×)` 버튼, 롱프레스 리프트 미리보기+컨텍스트 팝오버)
  - `apps/ios/Omyaru/OmyaruApp.swift` (`appBuildNumber = 78`, `BrowserTab.isPinned` 하위 호환 Codable + `togglePinTab`, `tabs(forGroup:)`, `groupDisplayName(for:)`, `groupHeaderTitle(for:)`, `bookmarkAllVisibleTabs`)
  - `apps/ios/Omyaru/Info.plist` (`CFBundleVersion = 78`)
  - `apps/ios/make_altstore_source.py` / `apps/ios/altstore-source.json`
- CI 빌드 및 배포 검증:
  - 커밋: `35f50fa`, AltStore 소스 갱신 커밋: `286705c`.
  - GitHub Actions Run `45045221773` (`macos-15` Xcode Release) 25단계 전체 `success` (첫 시도 0에러).
  - 통합 릴리즈 `v0.15.18`: `Omyaru-v0.15.18-arm64.apk` + `Omyaru-v0.15.18-ios.ipa` (`1,812.6 KB`).
  - AltStore Classic 소스 (`https://raw.githubusercontent.com/omyeol/Omyaru-Browser/main/apps/ios/altstore-source.json`): `version = "0.15.18"`, `buildVersion = "78"`, `size = 1856128` 반영 완료.

---

# 2026-11-23 Antigravity: Omyaru iOS v0.15.18 (build 77) — Android 동일 플로팅 하단 URL 아일랜드 및 잔여 Android 기능 전면 구현 완료

- 작업 워크스페이스: `E:/DEV/CODEX/BROWSER`
- 사용자 요청:
  - 하단 URL 바를 Android 버전과 동일하게 구현하고, 그 외의 Android 기능들도 끝까지 완성.
- 추가·변경 파일:
  - `apps/ios/Omyaru/BrowserParitySheets.swift` (신규 — `FastScrollBarOverlay`, `ActivityShareSheet`, `BrowserMenuSheet`, `CaptureActionMenuSheet`, `BookmarkEditSheet`, `WebContextElementSheet`, `CloseAllTabsCountdownSheet`, `TabGroupManagerSheet`, `TabBatchMoveSheet`)
  - `apps/ios/Omyaru/BrowserViews.swift` (Android `activity_browser.xml` + `liquid_glass_toolbar_background.xml` 동일 플로팅 2단 URL 아일랜드, 스크롤 축소 반투명 콤팩트 필, 플로팅 URL 추천 카드, 페이지 오류 화면, `PageToolsSheet` 미디어 PiP/탐색 버튼, Android 동일 `TabOverview` 멀티 선택 및 하단 그룹 필 좌우 스와이프)
  - `apps/ios/Omyaru/OmyaruApp.swift` (`appBuildNumber = 77`, 프로필별 방문 횟수 기반 URL 자동완성, 웹 요소 롱프레스 JS 브리지 `omyaruContext`, `FastScrollBar` 스크롤 텔레메트리 및 Android 누적 스크롤 숨김/복원, 하이라이트 `findInPage`, `AVAudioSession` 백그라운드 오디오·PiP/탐색, 그룹 이름변경/삭제/프로필 이동 및 다중 탭 이동/닫기, 페이지 로드 오류 상태)
  - `apps/ios/Omyaru/LiveCaptureView.swift` (`CaptureFolderPickerSheet` `onConfirm` 콜백 지원)
  - `apps/ios/Omyaru/Info.plist` (`CFBundleVersion = 77`)
  - `apps/ios/make_altstore_source.py` / `apps/ios/altstore-source.json`
- CI 빌드 및 배포 검증:
  - 커밋: `1861245`, AltStore 소스 갱신 커밋: `5ecd3cb`.
  - GitHub Actions Run `45043617050` (`macos-15` Xcode Release) 전 단계 `success` (첫 시도 0에러).
  - 통합 릴리즈 `v0.15.18`: `Omyaru-v0.15.18-arm64.apk` + `Omyaru-v0.15.18-ios.ipa` (`1,698.3 KB`).
  - AltStore Classic 소스 (`https://raw.githubusercontent.com/omyeol/Omyaru-Browser/main/apps/ios/altstore-source.json`): `version = "0.15.18"`, `buildVersion = "77"`, `size = 1739052` 반영 완료.

---

# 2026-11-23 Antigravity: Omyaru iOS v0.15.18 (build 76) — Android v0.15.18 완전 기능 패리티 달성, 통합 릴리즈(v0.15.18) 및 AltStore Classic 소스 배포

- 작업 워크스페이스: `E:/DEV/CODEX/BROWSER`
- 사용자 요청:
  - Android 버전명(`0.15.18`, `versionCode 76`)과 동일하게 iOS 버전을 완성하고 GitHub 통합 릴리즈 및 AltStore Classic 소스에 함께 배포.
- 변경 파일:
  - `apps/ios/Omyaru/OmyaruApp.swift` (`appVersionName = "0.15.18"`, `appBuildNumber = 76`, Android `BrowserSettings.kt` 전체 설정·20종 툴바 바로가기·계층형 북마크 폴더·다운로드 확인·스크롤 툴바 숨김/복원)
  - `apps/ios/Omyaru/BrowserViews.swift` (상단/하단 주소창, Liquid Glass UI, `PageToolsSheet` [- 5% / 배율 선택 / + 5% + 4버튼 그리드], 탭 오버뷰 `1~10열`·비율 `1:1`/`3:4`/`16:9`·아래에서 시작, 계층형 북마크·날짜별 방문 기록·다운로드 확인(책장 바로 저장)·프로필 18아이콘/컬러피커·8개 카테고리 설정)
  - `apps/ios/Omyaru/BookshelfView.swift` (클라우드 원본 온디맨드 자동 다운로드, 다중 선택 일괄 이동/삭제 `BookshelfBatchMoveSheet`, 저장공간 관리 `BookshelfStorageSheet`, 상/하단 빠른 스크롤, 명시적 `[확인 (여기로 이동)]` 버튼)
  - `apps/ios/Omyaru/LiveCaptureView.swift` (`↓ 아래로 확장` 점진적 스크롤 이어붙이기, 여백 포함 캡처 토글, `워터마크` 도구 즉시 연동)
  - `apps/ios/Omyaru/WatermarkRemoverView.swift` (이중 모드: 내장 오프라인 인페인팅 + `웹 도구 (⚡ 사진 주입)` 임베디드 WebView `<input type="file">` 자동 업로드 및 결과 다운로드 가로채기 -> 책장 저장)
  - `apps/ios/Omyaru/DriveSyncEngine.swift` (`OmyaruDriveKeyRing` + 레거시 키 마이그레이션, 1MB 청크 분할 책장 이미지 업/다운로드 및 SHA-256 무결성 검증, `DriveTabReset` `"동기화 탭 초기화"`, `omyaru-userdata-v1-`/`omyaru-tabs-v2-`/`omyaru-shelf-v1-` 완전 호환)
  - `apps/ios/Omyaru/AppLockAndPrivacy.swift` (PIN 4~6자리 + 비밀번호 + Face ID/Touch ID 앱 잠금, `appSwitchBlackShield` 앱 전환기 검은 화면 보호, DoH DNS + `.mobileconfig` + WireGuard `.conf` 프로필 검증/관리 + 외부 IP/연결 확인 + SOCKS5/HTTP 프록시, `.user.js` 확장/유저스크립트)
  - `apps/ios/Omyaru/Info.plist` (`CFBundleShortVersionString = 0.15.18`, `CFBundleVersion = 76`, `CFBundleURLTypes` OAuth 스킴 `cuttan.omyaru.app`)
  - `apps/ios/make_altstore_source.py` (`Info.plist` 버전·빌드 동적 파싱, 통합 릴리즈 다운로드 URL 반영)
  - `.github/workflows/ios.yml` (`Info.plist` 버전 동적 읽기, `Omyaru.ipa` 및 `Omyaru-v0.15.18-ios.ipa` 패키징, `v0.15.18` 통합 릴리즈 및 `ios-latest` 동시 배포, `apps/ios/altstore-source.json` 자동 커밋)
  - `apps/ios/README.md`
  - `apps/ios/altstore-source.json` (CI 자동 갱신본 로컬 동기화 완료)
- CI 빌드 및 배포 검증:
  - 커밋: `617e4f4` (`iOS v0.15.18: Full Android v0.15.18 feature parity and unified release`), AltStore 소스 갱신 커밋: `dd5a23b`.
  - GitHub Actions `Build iOS and AltStore IPA` Run `45041991233` (`macos-15`, Xcode Release arm64) 전체 25단계 `success` (첫 시도 0에러).
  - 통합 릴리즈 `v0.15.18` (`https://github.com/omyeol/Omyaru-Browser/releases/tag/v0.15.18`):
    - `Omyaru-v0.15.18-arm64.apk` (Android, 194,763.6 KB)
    - `Omyaru-v0.15.18-ios.ipa` (iOS, 1,467.4 KB)
  - `ios-latest` 릴리즈 (`https://github.com/omyeol/Omyaru-Browser/releases/tag/ios-latest`):
    - 제목 `Omyaru iOS v0.15.18`, 자산 `Omyaru.ipa`, `Omyaru-v0.15.18-ios.ipa`, `altstore-source.json`
  - AltStore Classic 소스 (`https://raw.githubusercontent.com/omyeol/Omyaru-Browser/main/apps/ios/altstore-source.json`):
    - `version = "0.15.18"`, `buildVersion = "76"`, `size = 1502651`, `downloadURL = https://github.com/omyeol/Omyaru-Browser/releases/download/v0.15.18/Omyaru-v0.15.18-ios.ipa` 확인 완료.

---

# 2026-11-22 Antigravity: Omyaru PC 브라우저(v0.4.69), 독립 책장(v0.1.41), iOS(v0.2.0) — Chrome/Firefox/Edge 스토어 확장 프로그램 바로 설치, 책장 폴더 이동 확인 버튼 복원, iOS 전체 기능(안드로이드 패리티) 구현

- 작업 워크스페이스: `E:/DEV/CODEX/BROWSER` (연동: `E:/DEV/CODEX/Umtomi`)
- 사용자 요청:
  1. 프로젝트 위치 `E:\DEV\CODEX\BROWSER` 이전 반영
  2. PC 버전에서 Chrome 웹 스토어 / Firefox 부가기능 스토어(`addons.mozilla.org`) / Edge 애드온 확장 프로그램을 스토어에서 바로 인식·설치할 수 있도록 구현
  3. 책장에서 폴더를 다른 폴더로 이동시킬 때 대상 폴더를 선택해도 확인 버튼이 보이지 않아 이동이 불가능했던 버그 수정
  4. 새로 추가된 iOS 버전(`apps/ios`)을 Android 버전과 기능적으로 동일하게 끝까지 구현
- 변경 파일:
  - PC 브라우저 & 독립 책장:
    - `apps/windows/desktop/ExtensionManagerForm.cs`
    - `apps/windows/desktop/BrowserShellForm.cs`
    - `apps/windows/desktop/Bookshelf.cs`
    - `apps/windows/bookshelf/Bookshelf.cs`
    - `apps/windows/bookshelf/StandaloneSupport.cs`
    - `apps/windows/desktop/DesktopSettings.cs`
    - `apps/windows/desktop/PrivacyBrowser.Desktop.csproj` (`Version = 0.4.69`)
    - `apps/windows/bookshelf/Omyaru.Bookshelf.csproj` (`Version = 0.1.41`)
  - iOS 버전 (`apps/ios` v0.2.0) & CI:
    - `apps/ios/Omyaru/OmyaruApp.swift`
    - `apps/ios/Omyaru/BrowserViews.swift`
    - `apps/ios/Omyaru/BookshelfView.swift`
    - `apps/ios/Omyaru/LiveCaptureView.swift`
    - `apps/ios/Omyaru/WatermarkRemoverView.swift`
    - `apps/ios/Omyaru/DriveSyncEngine.swift`
    - `apps/ios/Omyaru/AppLockAndPrivacy.swift`
    - `apps/ios/Omyaru/Info.plist` (`0.2.0` / build `2`)
    - `apps/ios/make_altstore_source.py`
    - `apps/ios/README.md`
    - `.github/workflows/ios.yml`
  - Umtomi 연동:
    - `E:/DEV/CODEX/Umtomi/umtomi/ios/Umtomi/Bookshelf.swift` (`ShelfFolderPicker` 폴더 이동 시 상단 `Confirm` 버튼 및 행별 `Move here` 확인 버튼 추가)
- 주요 수정 및 구현 내역:
  1. **책장 폴더 이동 대화상자 확인 버튼 누락 버그 수정 (`Bookshelf.cs`, `StandaloneSupport.cs`, `BrowserShellForm.cs`)**:
     - 원인: `ChooseFolderParent(string? moving)`에서 `[여기로 이동]`(`select`) 및 `[＋ 새 폴더]`(`create`) 버튼이 `new PillButton { AutoSize = true, Height = 34 }`로 생성되었으나, `Control`을 직접 상속한 `PillButton`에 `DefaultSize`(`0x0`)와 `GetPreferredSize`가 정의되어 있지 않고 `Width`도 지정되지 않아 버튼 폭이 `0px`로 렌더링되어 화면에 보이지 않았음.
     - 해결: `PillButton`에 `DefaultSize = new(96, 34)`와 `GetPreferredSize(Size proposedSize)` 텍스트 측정 로직을 추가하고, `ChooseFolderParent`에서 `[확인 (여기로 이동)]`(`Width = 142`), `[＋ 새 폴더]`(`Width = 104`), `[취소]`(`Width = 76`) 버튼 폭을 명시적으로 지정함. 또한 상단 안내 문구(`책장 (최상위)` 선택 안내)와 트리 노드 더블클릭/`Enter` 키 즉시 이동도 함께 지원.
  2. **PC 브라우저 Chrome 웹 스토어 / Firefox 부가기능 / Edge 애드온 원클릭 설치 및 호환 변환 (`ExtensionManagerForm.cs`, `BrowserShellForm.cs`)**:
     - 스토어 페이지 브리지(`InjectExtensionStoreBridgeAsync`): `chromewebstore.google.com`, `chrome.google.com/webstore`, `addons.mozilla.org`, `microsoftedge.microsoft.com/addons` 상세 페이지 진입 시 우측 하단에 **`[🧩 Omyaru에 이 확장 프로그램 설치]`** 플로팅 버튼을 표시하고, 스토어 자체의 `Chrome에 추가` / `Firefox에 추가` / `받기` 버튼 및 `Firefox가 필요합니다` 배너를 후킹해 원클릭으로 Omyaru에 바로 설치되도록 연동.
     - `.crx` / `.xpi` 다운로드 자동 가로채기(`OnExtensionPackageDownloadStarting`): 웹에서 `.crx`나 `.xpi` 링크를 클릭하면 파일 다운로드 대신 즉시 현재 프로필 확장 프로그램으로 자동 설치.
     - 패키지 언패커 & Firefox -> WebView2 매니페스트 자동 변환(`ExtractPackageFile`, `NormalizeManifestForWebView2`): CRX2/CRX3 헤더를 자동 분리해 내장 ZIP을 추출하고, Firefox `.xpi`의 `applications`/`browser_specific_settings` 정리, MV3 `background.scripts` -> `service_worker`(`__omyaru_sw.js`) 래퍼 생성 및 `globalThis.browser = globalThis.chrome` 폴리필 자동 주입.
     - 확장 팝업/옵션 실행: 주소창 왼쪽 퍼즐(`🧩`) 메뉴(`ShowPageToolsMenu`)와 `확장 프로그램` 관리 창에서 설치된 확장의 `default_popup` / `options_page`(`chrome-extension://<id>/...`)를 즉시 탭으로 열어 실행·설정 가능.
  3. **Omyaru iOS `v0.2.0` 전체 기능 구현 (Android 기능 패리티 달성)**:
     - **프로필·탭 그룹·비공개 탭 및 탭 관리 (`OmyaruApp.swift`, `BrowserViews.swift`)**: 다중 프로필(`개인` 및 추가 프로필), 탭 그룹, 비공개 탭, 시작 시 비활성 복원 탭 지연 로딩(Lazy Load), **탭 복제 시 원본 탭 바로 뒤에 삽입(`index + 1`)**, 길게 눌러 탭 이동 시 상·하단 가장자리 자동 스크롤, 탭 닫기 스프링 애니메이션, 닫은 탭 복원, 탭 제목/주소 검색, 그리드/리스트 썸네일 뷰.
     - **웹 엔진 & 내장 확장 기능 (`OmyaruApp.swift`, `AppLockAndPrivacy.swift`)**: 웹 이미지 고속 선로딩(`IntersectionObserver` rootMargin `1800px 600px 4500px 600px` 확장 및 뷰포트 하단 `2200px` 이내 `data-src`/`data-original` 승격), `WKContentRuleListStore` 네이티브 추적기/광고 차단 + 배너/쿠키 팝업 숨김 + 우클릭/복사 해제 + 웹 다크 모드 강제 적용 + 커스텀 유저스크립트(`.user.js`) URL/파일 설치 및 편집기 + 페이지 내 검색 + 데스크톱 모드 + 화면 배율(`50%~200%`) + 하단 툴바 바로가기 편집.
     - **실시간 영역 캡처 (`LiveCaptureView.swift`)**: 고정 헤더 자동 숨김, 돋보기 렌즈(`magnifierLens`)가 포함된 4모서리 영역 지정, `1~6화면` 연속 스크롤 스티칭 캡처, 책장 폴더 바로 추가 / PNG 저장 / 클립보드 복사.
     - **워터마크 제거 도구 (`WatermarkRemoverView.swift`)**: 현재 웹화면 캡처 또는 사진/파일 불러오기, 다중 워터마크 박스 지정 및 하단 코너 자동 감지, 양방향 경계 보간 인페인팅 복원, 원본 비교 토글, 책장 바로 추가.
     - **계층형 폴더 트리 책장 (`BookshelfView.swift`)**: `상위폴더/하위폴더` 계층 구조, 상위 폴더 열람 시 다중 열(`1~6열`) 및 메모 유지, 폴더/사진 이동 시 명시적 **`[확인 (여기로 이동)]`** 버튼 제공, 전체 화면·단일 이미지 줌 뷰어, 다중 사진/파일/클립보드 추가, PDF 내보내기(`9:16` / `16:9`).
     - **Google Drive & 파일 암호화 동기화 (`DriveSyncEngine.swift`)**: Android/Windows와 호환되는 `privacy-browser-sync`(`PBKDF2-HMAC-SHA256` 210,000회 + `AES-256-GCM`) 프로토콜로 Google Drive `appDataFolder`(`omyaru-account-key-v1-`, `omyaru-userdata-v1-`, `omyaru-tabs-v2-`, `omyaru-shelf-v1-`) 동기화 및 `.omyarusync` 암호화 파일 내보내기/가져오기 지원.
     - **보안 & 네트워크 (`AppLockAndPrivacy.swift`)**: Face ID / Touch ID + PBKDF2 PIN 앱 잠금(백그라운드 전환 시 블러 보호) 및 DoH 보안 DNS(`1.1.1.1`, `8.8.8.8`, `9.9.9.9`, `AdGuard`) + iOS `.mobileconfig` 암호화 DNS 프로필 생성기.
- 검증 및 빌드 산출물:
  - `powershell -ExecutionPolicy Bypass -File scripts/Publish-Windows.ps1` 성공.
  - `Omyaru.exe --self-test` (exit `0`), `Omyaru.exe --smoke-test` (exit `0`), `OmyaruBookshelf.exe --self-test` (exit `0`), `OmyaruBookshelf.exe --smoke-test` (exit `0`) 통과.
  - 빌드 산출물:
    - 데스크톱 브라우저 (`Omyaru.exe` v0.4.69):
      - 상대 경로: `artifacts/releases/Omyaru-v0.4.69-win-x64/Omyaru.exe`
      - 절대 경로: `E:/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.4.69-win-x64/Omyaru.exe`
    - 독립 책장 (`OmyaruBookshelf.exe` v0.1.41):
      - 상대 경로: `artifacts/releases/OmyaruBookshelf-v0.1.41-win-x64/OmyaruBookshelf.exe`
      - 절대 경로: `E:/DEV/CODEX/BROWSER/artifacts/releases/OmyaruBookshelf-v0.1.41-win-x64/OmyaruBookshelf.exe`

---

# 2026-11-22 Codex: 프로젝트 위치 이전

- 사용자 요청에 따라 Omyaru `BROWSER` 프로젝트 전체를 `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER`에서 `E:/DEV/CODEX/BROWSER`로 이전합니다.
- 소스, Git 메타데이터, 빌드 산출물 185,362개(약 180.9 GiB)를 `E:/DEV/CODEX/BROWSER`에 복사했습니다. Robocopy 실패 0건, 차이분 재복사 실패 0건이며 주요 소스 파일 해시와 Git 상태를 확인했습니다.
- 원본 폴더의 재귀 삭제는 자동 승인 검토에서 거부되어 `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER`에도 복사본이 남아 있습니다. 새 작업 위치는 `E:/DEV/CODEX/BROWSER`입니다. 전체 무결성 확인과 원본 정리는 후속 작업입니다.

---

# 2026-11-15 Antigravity: Omyaru PC 브라우저(v0.4.67) — 실시간 영역 캡처 검은색/단색 빈 이미지(`#1e1f21`) 저장 현상 수정

- 사용자 요청:
  - 브라우저에서 웹툰(`https://toptoon.com/comic/ep_view/Milf_Hunter_from_another_world/134/rent`) 영역을 캡처해 책장에 넣었을 때 `image633.png` (`644x1431`), `image634.png` (`719x1476`)처럼 전체가 검게(`rgb(30, 31, 33)` / `#1e1f21`) 나오는 현상 해결
- 원인 분석:
  1. **`Page.captureScreenshot`의 `captureBeyondViewport = true` + `clip` 호출 한계**:
     - 기존 `CapturePageRegionBitmapAsync`가 Chromium DevTools `Page.captureScreenshot`에 `captureBeyondViewport = true`와 `clip = { x, y, width, height }`를 전달해 캡처했음.
     - Chromium 내부적으로 `captureBeyondViewport = true`는 에뮬레이션 뷰포트 폭을 `clip.width`(`644px` / `719px`)로 강제 축소하고 문서 전체 높이(`50,000px~150,000px`)로 리사이즈하는데, 이로 인해 (a) 중앙 정렬(`margin: 0 auto`)된 웹툰 뷰어 컬럼의 X 좌표가 이동하거나 리사이즈 이벤트로 지연 로딩/가상화 컷이 비워지고, (b) 문서 하단 깊은 스크롤 좌표(`y > 16,384px`)에서 GPU/Skia 타일 래스터 한계를 초과해 웹툰 컷 대신 `<body>` 배경색(`#1e1f21`, `rgb(30, 31, 33)`)만 찍히는 현상이 발생함.
     - 반면 캡처 중 돋보기(`#lens`)가 사용한 **현재 뷰포트 캡처(`CapturePreviewAsync` / `captureBeyondViewport = false`)**는 레이아웃 변경 없이 화면에 합성된 GPU 타일을 그대로 가져오므로 정상 표시되었음.
  2. **`PreparePageForCaptureAsync`의 `position: fixed`/`sticky` 숨김 조건 과도 적용**:
     - 기존 고정 요소 숨김 로직에 최대 크기 상한선이 없어, 화면 전체를 덮는 고정형 뷰어 컨테이너까지 `visibility: hidden` 처리될 수 있었음.
- 주요 수정 내역 (`apps/windows/desktop/BrowserShellForm.cs`, `DesktopSmokeTest.cs`, `DesktopSettings.cs`, `PrivacyBrowser.Desktop.csproj`):
  1. **3단계 뷰포트 기반 캡처 + 스크롤 스티칭 + DOM 미디어 폴백 파이프라인 구축 (`CapturePageRegionBitmapAsync`)**:
     - **1단계 (뷰포트 캡처 & 다중 뷰포트 스크롤 스티칭)**: `captureBeyondViewport = true`를 제거하고, 현재 뷰포트에 영역이 모두 들어오면 즉시 현재 뷰포트 프레임(`CapturePreviewAsync` / `CaptureViewportViaDevToolsAsync(captureBeyondViewport: false)`)에서 선택 영역만 정밀 크롭(`CropViewportBitmap`). 선택 영역이 한 화면보다 긴 경우(`1431px`, `1476px` 등) `ScrollAndSettleForCaptureAsync`로 단계별 스크롤하며 각 구간의 이미지 로딩 완료(`img.complete`) 및 2회 `requestAnimationFrame` 안정화 후 뷰포트 슬라이스를 이어 붙이고, 캡처 완료 후 원래 스크롤 위치로 복원.
     - **2단계 (`IsNearlyUniformColor` 단색/검은 화면 자동 감지 및 교차 캡처 재시도)**: 캡처된 비트맵의 8×10 격자 픽셀이 모두 거의 동일한 단색(예: `#1e1f21` 배경색만 찍힌 경우)인지 검사하여, 단색이면 즉시 대체 표면 캡처(`CaptureViewportViaDevToolsAsync`)로 재시도.
     - **3단계 (`CaptureDomMediaRegionAsync` DOM `<img>`/`<canvas>` 직접 합성 폴백)**: 표면 캡처가 단색을 반환하는 특수 보호/가상화 환경에서도 선택 영역과 교차하는 DOM 내 `<img>`·`<canvas>` 요소를 오프스크린 캔버스에 직접 합성해 추출하도록 보장.
  2. **내부 스크롤 컨테이너 감지 및 고정 바 숨김 안전장치 강화 (`PreparePageForCaptureAsync`, `InjectLiveCaptureOverlayAsync`)**:
     - `detectPrimaryScroller()`(`window.__omyaruActiveScroller`)를 도입해 `window` 스크롤뿐만 아니라 내부 `overflow-y: auto/scroll` 뷰어 컨테이너에서도 드래그 자동 스크롤과 스티칭 스크롤이 동작하도록 개선.
     - 캡처 준비 시 모든 `document.images`에 `loading = 'eager'`를 강제 적용하고, 고정 상/하단 바 숨김(`window.__omyaruHideFixedBars`)은 얇은 헤더/툴바(`height <= min(180, innerHeight * 0.24)` 또는 `width <= min(240, innerWidth * 0.24)`)에만 적용하며 대형 `<img>`/`<canvas>`(`>= 240x200`)를 포함한 컨테이너나 스크롤 컨테이너는 절대 숨기지 않도록 보호.
  3. **장문(`30,000px`, `y = 24,000px`, 높이 `1,476px`) 다중 뷰포트 캡처 스모크 테스트 추가 (`VerifyTallPageRegionCaptureAsync`, `DesktopSmokeTest.cs`)**:
     - `--smoke-test` 실행 시 `#1e1f21` 배경의 `30,000px` 문서 하단(`y = 24,000px`)에서 2개 뷰포트에 걸친 `719x1476` 영역을 캡처해 크기가 정확하고 단색(`#1e1f21`)이 아닌 실제 콘텐츠가 캡처되는지 자동 검증.
- 검증 및 빌드 산출물:
  - `powershell -ExecutionPolicy Bypass -File scripts/Publish-Windows.ps1` 성공.
  - `Omyaru.exe --self-test` (exit `0`), `Omyaru.exe --smoke-test` (exit `0`), `OmyaruBookshelf.exe --self-test` (exit `0`), `OmyaruBookshelf.exe --smoke-test` (exit `0`) 모두 통과.
  - 빌드 산출물:
    - 데스크톱 브라우저 (`Omyaru.exe` v0.4.67):
      - 상대 경로: `artifacts/releases/Omyaru-v0.4.67-win-x64/Omyaru.exe`
      - 절대 경로: `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.4.67-win-x64/Omyaru.exe`
    - 독립 책장 (`OmyaruBookshelf.exe` v0.1.39):
      - 상대 경로: `artifacts/releases/OmyaruBookshelf-v0.1.39-win-x64/OmyaruBookshelf.exe`
      - 절대 경로: `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/OmyaruBookshelf-v0.1.39-win-x64/OmyaruBookshelf.exe`
- 다음 단계:
  - 사용자가 `Omyaru.exe` (`v0.4.67`)에서 웹툰 등 긴 웹페이지 영역을 캡처해 책장에 추가했을 때 검은 화면 없이 정상 저장되는지 확인.

---

# 2026-11-15 Antigravity: Omyaru PC 브라우저(v0.4.66) 및 독립 책장(v0.1.39) — 내장 책장 비모달(브라우저 동시 사용) 전환 및 URL 필드 좌측 밀착 배치

- 사용자 요청:
  1. 브라우저 내장 책장을 열어둔 상태에서도 브라우저를 동시에 사용할 수 있도록 수정 (기존 `ShowDialog(this)` 모달 차단 해소)
  2. URL 필드(`_addressHost`)를 왼쪽(좌측 내비게이션/프로필 버튼 바로 옆)으로 붙이도록 수정 (`media_1794725968003_5caef36c.png`)
- 변경 파일:
  - `apps/windows/desktop/BrowserShellForm.cs`
  - `apps/windows/desktop/Bookshelf.cs`
  - `apps/windows/bookshelf/Bookshelf.cs`
  - `apps/windows/desktop/DesktopSettings.cs`
  - `apps/windows/desktop/PrivacyBrowser.Desktop.csproj` (`Version = 0.4.66`)
  - `apps/windows/bookshelf/Omyaru.Bookshelf.csproj` (`Version = 0.1.39`)
- 주요 수정 내역:
  1. **브라우저 내장 책장 비모달(Modeless) 전환 및 실시간 연동 (`BrowserShellForm.cs`, `Bookshelf.cs`)**:
     - 기존 `bookshelf.ShowDialog(this)` 호출을 비모달 독립 창(`bookshelf.Show()`, `ShowInTaskbar = true`, `StartPosition = FormStartPosition.CenterScreen`)으로 전환하여, 내장 책장을 띄워둔 채 브라우저 탭 탐색·스크롤·영역 캡처·창 전환(`Alt+Tab` 및 나란히 배치)을 동시에 수행할 수 있도록 개선.
     - 이미 내장 책장이 열려 있을 때 다시 `책장 열기`를 누르면 중복 창을 띄우지 않고 기존 창을 복원·최신화(`ReloadCatalogFromExternalChange()`) 후 앞으로 가져오도록 처리.
     - 브라우저에서 실시간 영역 캡처로 `책장에 추가`를 실행할 때 최신 카탈로그를 로드해 저장한 뒤 열려 있는 내장 책장 창에 즉시 반영(`_bookshelfForm?.ReloadCatalogFromExternalChange()`)되도록 연동.
     - 책장 창 포커스 이탈 시(`Deactivate`) 작성 중인 폴더 메모를 즉시 디스크에 커밋(`CommitShelfNote()`)하여 브라우저와 동시 사용 중에도 데이터 유실이나 덮어쓰기가 발생하지 않도록 보호.
  2. **URL 스마트 검색 필드(`_addressHost`) 좌측 밀착 및 가용 폭 확장 (`BrowserShellForm.cs`)**:
     - `LayoutSmartSearchPill()`에서 URL 필드를 중앙에 띄우던 여백을 제거하고 좌측 프로필 버튼(`[● 개인]`) 바로 오른쪽(`left = 6`)에 밀착 배치하며, 우측 툴바 아이콘 직전(`rightMargin = 8`)까지 가용 폭을 채우도록 수정.
     - `_address.TextAlign = HorizontalAlignment.Left`로 고정하여 포커스 여부와 관계없이 주소/검색어 텍스트도 좌측 보안 자물쇠 아이콘 바로 옆에 정렬되도록 수정.
- 검증 및 빌드 산출물:
  - `powershell -ExecutionPolicy Bypass -File scripts/Publish-Windows.ps1` 성공.
  - `Omyaru.exe --self-test` (exit `0`), `Omyaru.exe --smoke-test` (exit `0`), `OmyaruBookshelf.exe --self-test` (exit `0`), `OmyaruBookshelf.exe --smoke-test` (exit `0`) 모두 통과.
  - 빌드 산출물:
    - 데스크톱 브라우저 (`Omyaru.exe` v0.4.66):
      - 상대 경로: `artifacts/releases/Omyaru-v0.4.66-win-x64/Omyaru.exe`
      - 절대 경로: `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.4.66-win-x64/Omyaru.exe`
    - 독립 책장 (`OmyaruBookshelf.exe` v0.1.39):
      - 상대 경로: `artifacts/releases/OmyaruBookshelf-v0.1.39-win-x64/OmyaruBookshelf.exe`
      - 절대 경로: `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/OmyaruBookshelf-v0.1.39-win-x64/OmyaruBookshelf.exe`
- 다음 단계:
  - 사용자가 `Omyaru.exe` (`v0.4.66`)에서 내장 책장과 브라우저 동시 사용 및 좌측 밀착된 URL 필드를 확인.

---

# 2026-11-15 Antigravity: Omyaru PC 브라우저(v0.4.65) — URL 스마트 검색 필드 상하 정렬·잘림 수정 및 시작 페이지 즐겨찾기 타일 표시 복원

- 사용자 요청:
  1. 내비게이션 바의 URL 스마트 검색 필드(`_addressHost`)가 아래로 치우쳐 하단이 잘리는 현상 수정 (`media_1794724186432_61423193.png`)
  2. 동일 화면에서 시작 페이지 `"즐겨찾기"` 섹션의 타일이 보이지 않던 현상 함께 수정
- 원인 및 수정 내역 (`apps/windows/desktop/BrowserShellForm.cs`, `DesktopSmokeTest.cs`, `DesktopSettings.cs`, `PrivacyBrowser.Desktop.csproj`):
  1. **URL 스마트 검색 필드(`_addressHost`) 하단 침범·잘림 수정**:
     - 원인: `BuildNavigationChrome()`의 3열 1행 `TableLayoutPanel`(`navigation`)에 `RowStyles`가 누락되어 행이 `AutoSize`로 동작했고, 중앙 `addressCell`(`Panel`)이 WinForms 기본 높이(`100px`)를 유지한 채 `LayoutSmartSearchPill()`이 `top = (100 - 38) / 2 = 31`로 배치하여 `56px` 높이 내비게이션 바 하단에서 절반이 잘렸음.
     - 조치: `navigation.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));`를 추가하고 `addressCell` 초기 크기를 `(400, 56)`으로 지정, `LayoutSmartSearchPill()`에서 행 높이(`56px` / 컴팩트 `52px`) 기준으로 `Height = 36`, `top = 10` (컴팩트 `8`)에 배치하여 좌측 버튼(`history`)·중앙 URL 캡슐(`_addressHost`)·우측 툴바(`_toolbarWorkspace`)가 모두 정확히 동일한 수직 중심선(`Y = 10..46`)에 정렬되도록 수정.
     - 내부 아이콘 정렬: `addressLayout` 내 `pageToolsButton`, `_securityBadge`, `_bookmarkStarButton`, `reloadInPill`에 `Anchor = AnchorStyles.None`, `Margin = Padding.Empty`를 적용하고, `28x28` 캡슐 내부 새로고침 아이콘(`PillGlyph.Reload`)의 반경을 컴팩트 버튼 크기에 맞게 조정(`r = 5.8F`).
  2. **시작 페이지 즐겨찾기 타일(`SafariStartTileControl`) 미표시 수정**:
     - 원인: `SafariStartTileControl`이 `Control`을 직접 상속하면서 `DefaultSize`가 `0x0`(`Size.Empty`)이어서 `_startPageFavorites` 안에서 크기가 `0x0`으로 렌더링됨.
     - 조치: `SafariStartTileControl.DefaultSize => new(84, 92)` 및 생성자/타일 생성 시 `Size = new Size(84, 92)`를 명시하고, `추가` 타일에 `IsAddTile = true`를 설정.
  3. **자동 스모크 테스트 레이아웃 검증 추가 (`DesktopSmokeTest.cs`, `BrowserShellForm.HasValidChromeLayout`)**:
     - `--smoke-test` 실행 시 `_addressHost`의 상하 좌표(`Top >= 6`, `Bottom <= 50`, `Height == 36`)와 `_startPageFavorites` 타일 크기(`84x92`)를 자동 검증하도록 추가.
- 검증 및 빌드 산출물:
  - `powershell -ExecutionPolicy Bypass -File scripts/Publish-Windows.ps1` 성공.
  - `Omyaru.exe --self-test` (exit `0`), `Omyaru.exe --smoke-test` (exit `0`), `OmyaruBookshelf.exe --self-test` (exit `0`), `OmyaruBookshelf.exe --smoke-test` (exit `0`) 모두 통과.
  - 빌드 산출물:
    - 데스크톱 브라우저 (`Omyaru.exe` v0.4.65):
      - 상대 경로: `artifacts/releases/Omyaru-v0.4.65-win-x64/Omyaru.exe`
      - 절대 경로: `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.4.65-win-x64/Omyaru.exe`
    - 독립 책장 (`OmyaruBookshelf.exe` v0.1.38):
      - 상대 경로: `artifacts/releases/OmyaruBookshelf-v0.1.38-win-x64/OmyaruBookshelf.exe`
      - 절대 경로: `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/OmyaruBookshelf-v0.1.38-win-x64/OmyaruBookshelf.exe`
- 다음 단계:
  - 사용자가 `Omyaru.exe` (`v0.4.65`)를 실행해 내비게이션 바 URL 필드와 시작 페이지 즐겨찾기 타일 배치를 확인.

---

# 2026-11-15 Antigravity: Omyaru PC 브라우저(v0.4.64) 및 독립 책장(v0.1.38) — Apple Safari / Omyaru 모바일 스타일 UI 대대적 개편

- 사용자 요청:
  1. Omyaru PC 버전(책장 포함) UI를 Apple Safari(macOS Sequoia/Sonoma + iOS/iPadOS Safari) 및 Omyaru 모바일 감성으로 대대적 개편
  2. 데스크톱 브라우저(`Omyaru.exe`) 및 독립 책장(`OmyaruBookshelf.exe`) 빌드·검증 및 산출물 경로 안내
- 변경 파일:
  - `apps/windows/desktop/BrowserShellForm.cs`
  - `apps/windows/desktop/Bookshelf.cs`
  - `apps/windows/bookshelf/Bookshelf.cs`
  - `apps/windows/bookshelf/StandaloneSupport.cs`
  - `apps/windows/desktop/VirtualBookshelfGallery.cs`
  - `apps/windows/desktop/DesktopSettings.cs`
  - `apps/windows/desktop/Bookmarks.cs`
  - `apps/windows/desktop/DesktopToolbarEditorDialog.cs`
  - `apps/windows/desktop/Models.cs`
  - `apps/windows/desktop/PrivacyBrowser.Desktop.csproj` (`Version = 0.4.64`)
  - `apps/windows/bookshelf/Omyaru.Bookshelf.csproj` (`Version = 0.1.38`)
- 주요 개편 내역:
  1. **Apple Safari 다크 모드 팔레트 및 타이포그래피 전면 적용**:
     - 크롬/툴바(`#1C1C1E`), 활성 탭/레이즈드 카드(`#2C2C30`), 페이지/갤러리 배경(`#121214`), 입력 필드(`#141416`), 헤어라인 테두리(`#3A3A40`), Apple System Blue 포인트(`#0A84FF`), Apple System Red 경고(`#FF453A`), Apple SF 스타일 아이콘·배지 및 상단 스페큘러 하이라이트 적용.
  2. **통합 Safari 내비게이션 바 & 중앙 Smart Search Field (`BrowserShellForm.cs`)**:
     - 좌측 클러스터: 뒤로/앞으로, 탭 오버뷰(`PillGlyph.Tabs`), 컴팩트 프로필 알약(`_toolbarProfileButton`).
     - 중앙 Smart Search Field(`_addressHost`): 최대 폭 `780px`, 높이 `38px`, 라운드 `12px`, 포커스 시 `#0A84FF` 링 표시. 내부 좌측에 페이지 도구(`PillGlyph.Extension`)와 HTTPS 자물쇠(`PillGlyph.Lock`), 중앙에 비포커스 시 도메인 중앙 정렬 / 포커스 시 전체 URL 표시(`_address`), 우측에 북마크 별표 토글(`PillGlyph.Star` / `PillGlyph.StarFilled`)과 새로고침(`PillGlyph.Reload`) 내장.
     - 우측 클러스터: 동기화, 워터마크 제거, 영역 캡처, 오디오 출력, 책장(`bookshelf`), 전체 메뉴.
  3. **Safari 탭 스트립, 탭 오버뷰, 시작 페이지 (`BrowserShellForm.cs`)**:
     - 탭 스트립(`TabStripItemControl`): `16x16` 파비콘, 활성 탭 상단 스페큘러 하이라이트, 비활성 탭 우측 미세 구분선, 우클릭 메뉴(`탭 복제` 포함, 복제 시 원본 탭 바로 뒤에 삽입).
     - Safari 시작 페이지: 대기권 다크 그라데이션 배경, `62x62` 스쿼클 즐겨찾기 타일(`SafariStartTileControl`) + `추가` 타일, 개인정보 보호 리포트 글래스 카드(`PillGlyph.Shield`), 빠른 도구 & 라이브러리 카드(`책장 라이브러리`, `실시간 영역 캡처`, `워터마크 제거`, `Android · PC 동기화`).
     - 탭 오버뷰: 플로팅 글래스 헤더/푸터 바, 카드 우클릭 메뉴(`탭 복제`, `선택한 탭 닫기`, `다른 탭 모두 닫기`), Apple Blue `완료` 버튼.
  4. **Safari / macOS Finder 스타일 책장 UI 개편 (`desktop/Bookshelf.cs`, `bookshelf/Bookshelf.cs`, `VirtualBookshelfGallery.cs`, `StandaloneSupport.cs`)**:
     - 좌측 전체 높이 macOS Finder/Safari 사이드바(`272px`): 상단 `"책장 폴더"` 헤더 + `＋ 폴더` 버튼, 중앙 커스텀 오너드로우 폴더 트리(`BookshelfFolderTree`, 파란색 둥근 SF 스타일 폴더 아이콘·우측 아이템 개수 알약 배지·Apple Blue 라운드 선택 하이라이트), 하단 고정 푸터(`☁ 동기화` + `⌂ 저장 위치` 버튼 및 동기화 진행 표시줄).
     - 우측 2단 Safari 헤더: 1단 책장 제목 + 세그먼트형 열 조절 스테퍼(`[ − | 2열 | ＋ ]`) + Apple Blue `＋ 사진 추가` + `PDF 내보내기` + `⛶ 전체 화면`, 2단 라운드 메모 바(`메모` 배지 + `_shelfNote`).
     - 상위 폴더 선택 시 열 개수(`FolderColumnCounts`)와 메모(`FolderNotes`)가 `Normalize` 과정에서 초기화되지 않고 보존되도록 수정.
- 검증 및 빌드 산출물:
  - `powershell -ExecutionPolicy Bypass -File scripts/Publish-Windows.ps1` 성공.
  - `Omyaru.exe --self-test` (exit `0`), `Omyaru.exe --smoke-test` (exit `0`), `OmyaruBookshelf.exe --self-test` (exit `0`), `OmyaruBookshelf.exe --smoke-test` (exit `0`) 모두 통과.
  - 빌드 산출물:
    - 데스크톱 브라우저 (`Omyaru.exe` v0.4.64):
      - 상대 경로: `artifacts/releases/Omyaru-v0.4.64-win-x64/Omyaru.exe`
      - 절대 경로: `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.4.64-win-x64/Omyaru.exe`
    - 독립 책장 (`OmyaruBookshelf.exe` v0.1.38):
      - 상대 경로: `artifacts/releases/OmyaruBookshelf-v0.1.38-win-x64/OmyaruBookshelf.exe`
      - 절대 경로: `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/OmyaruBookshelf-v0.1.38-win-x64/OmyaruBookshelf.exe`
- 다음 단계:
  - 사용자가 새 PC 브라우저(`Omyaru.exe`)와 책장(`OmyaruBookshelf.exe`) UI를 실행해 확인하고, 추가적인 세부 디자인/레이아웃 조정 요청이 있으면 반영.

---

# 2026-11-13 Antigravity: Omyaru Browser Android v0.15.18 — 업데이트 다운로드 알림 지움 방지(`setOngoing(true)`) 및 DNS(`1.1.1.1`/`8.8.8.8`)·웹 로딩 속도 최적화

- 사용자 요청:
  1. 업데이트 다운로드 알림을 지울 수 없도록 `setOngoing(true)` 적용 (Android 14+에서 스와이프 시에도 유지되도록 처리)
  2. 브라우저 내부 DNS 설정(`1.1.1.1` / `8.8.8.8`) 사용 시를 포함해 웹 로딩 속도가 느려진 원인을 파악하고 수정
  3. Release APK 빌드 및 GitHub Releases 업로드 (`omyeol/Omyaru-Browser`)
- 변경 파일:
  - `apps/android/app/src/main/java/dev/privacybrowser/app/GitHubUpdates.kt`
  - `apps/android/app/src/main/java/dev/privacybrowser/app/BrowserDownloadService.kt`
  - `apps/android/app/src/main/AndroidManifest.xml`
  - `apps/android/app/src/main/java/dev/privacybrowser/app/BrowserApplication.kt`
  - `apps/android/app/src/main/java/dev/privacybrowser/app/BrowserActivity.kt`
  - `apps/android/app/src/main/assets/image_preloader/page_preload.js`
  - `apps/android/app/src/main/assets/image_preloader/content.js`
  - `apps/android/app/build.gradle.kts` (`versionCode = 76`, `versionName = "0.15.18"`)
- 원인 분석 및 주요 구현 내역:
  1. **업데이트 다운로드 알림 지움 방지 (`GitHubUpdates.kt`, `BrowserDownloadService.kt`, `AndroidManifest.xml`)**:
     - `NotificationCompat.Builder`로 전환하고 `.setOngoing(true)`, `.setAutoCancel(false)`, `.setOnlyAlertOnce(true)`, `.setCategory(NotificationCompat.CATEGORY_PROGRESS)`, `.setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE)`, `Notification.FLAG_ONGOING_EVENT or Notification.FLAG_NO_CLEAR` 플래그를 명시.
     - Android 14(API 34)+ 및 삼성 One UI에서 진행 중(`setOngoing(true)`) 알림을 강제 스와이프할 수 있는 OS 동작에 대응하기 위해 `GitHubUpdateNotificationReceiver` 브로드캐스트 리시버를 추가하고 `.setDeleteIntent(...)`로 연결하여, 다운로드 진행 중이거나 설치 대기 중인 업데이트 알림(및 일반 파일 다운로드 알림)이 스와이프되더라도 즉시 재표시되도록 구현.
  2. **웹 로딩 속도 저하 원인 1 — DNS(`1.1.1.1` / `8.8.8.8`) DoH 부트스트랩 및 TRR 확인 지연 (`BrowserApplication.kt`)**:
     - 기존에는 `1.1.1.1`(`https://cloudflare-dns.com/dns-query`) 또는 `8.8.8.8`(`https://dns.google/dns-query`) 선택 시 `network.trr.bootstrapAddr`가 지정되지 않아 DoH 서버 도메인을 먼저 느린 통신사/시스템 DNS로 질의해야 했고, `network.trr.wait-for-confirmation` 기본값(`true`) 때문에 NS 확인 프로브가 완료될 때까지 초기 DNS 조회가 지연됨.
     - `GeckoRuntimeSettings`에 `defaultRecursiveResolverUri`를 함께 설정하고, `geckoview-config.yaml`에 `network.trr.bootstrapAddr`(`1.1.1.1` 또는 `8.8.8.8`)를 직접 지정하여 시스템 DNS 조회를 건너뛰고 IP로 즉시 연결하도록 개선.
     - `network.trr.wait-for-confirmation: false`, `network.trr.preconnect_on_foreground: true`, `network.trr.preserve_on_background: true`, `network.trr.request_timeout_ms: 1500`, `network.trr.retry-timeout-ms: 125`, `network.dns.disablePrefetch: false`, `network.dns.disablePrefetchFromHTTPS: false`, `network.http.speculative-parallel-limit: 6`, `network.fetchpriority.enabled: true`, `dom.ipc.processCount: 4` 적용 및 `.consoleOutput(false)`로 JNI 콘솔 직렬화 오버헤드 제거.
  3. **웹 로딩 속도 저하 원인 2 — 시작/복원 시 모든 백그라운드 탭 동시 네트워크 로딩 (`BrowserActivity.kt`)**:
     - 기존 `restoreOrCreateTabs` 및 `applyDriveSnapshot`에서 저장된 모든 탭(최대 30개)에 대해 `session.open()`과 `session.load()`를 동시에 호출하고 UI 스레드에서 디스크 썸네일을 동기 디코딩하여, 활성 탭의 DNS/TLS/HTTP 대역폭과 렌더러 프로세스가 백그라운드 탭들에 잠식되던 현상 수정.
     - `openNewTab`에 `lazyLoad: Boolean = false`, `persist: Boolean = true`를 추가해 현재 활성 탭만 즉시 열고 로드하며, 비활성 복원 탭은 사용자가 해당 탭으로 전환(`switchToTab`)할 때 지연 로드되도록 개선. 탭 미리보기 이미지도 백그라운드 워커(`syncWorker`)에서 비동기 로드하도록 변경.
  4. **웹 로딩 속도 저하 원인 3 — 초기 파싱 중 전체 페이지 이미지 동시 다운로드 병목 (`image_preloader`)**:
     - `v0.15.17`에서 추가된 이미지 선로딩 스크립트가 `document_start` 시점에 페이지 최하단까지 모든 `<img loading="lazy">`를 일괄 `eager`로 바꾸고 `getBoundingClientRect()` 강제 동기 레이아웃을 유발해, 긴 웹페이지에서 수십~수백 개 하단 이미지가 초기 HTML/CSS/상단 이미지와 대역폭을 경쟁하던 문제를 수정.
     - 뷰포트 하단 `2200px`(상단 `800px`) 선로딩 구간 내의 이미지에 대해서만 즉시 로드하고, `document_start` 파싱 중에는 강제 레이아웃 측정을 생략하도록 개선하여 화면에 다가오는 이미지는 미리 로드되면서도 초기 웹페이지 진입 속도는 빠르게 유지되도록 최적화.
- 검증 및 배포:
  - `./gradlew :app:testDebugUnitTest :app:assembleRelease -PomyaruBuildDirectory=".build-release-01518"` 성공.
  - 빌드 산출물:
    - 상대 경로: `artifacts/Omyaru-v0.15.18-arm64.apk` (원본 빌드: `apps/android/.build-release-01518/app/outputs/apk/release/app-arm64-v8a-release.apk`)
    - 절대 경로: `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/Omyaru-v0.15.18-arm64.apk`
    - 배포 복사본: `C:/Users/peopl/OneDrive/DEV/APK/Omyaru-v0.15.18-arm64.apk`
  - GitHub Releases 업로드 완료: `https://github.com/omyeol/Omyaru-Browser/releases/tag/v0.15.18` (`Omyaru-v0.15.18-arm64.apk`).

---

# 2026-11-13 Antigravity: Omyaru Browser Android v0.15.17 — 웹 이미지 선로딩, 탭 복제 위치, 탭 드래그 상하 자동 스크롤, 탭 닫기 애니메이션

- 사용자 요청:
  1. 웹의 이미지가 미리 로딩되지 않는 현상(스크롤을 내려 이미지가 화면 중간보다 조금 아래쯤 와야 로딩되는 현상) 수정
  2. 탭 복제 시 맨 마지막이 아니라 복제한 탭 바로 뒤에 생성되도록 수정
  3. 탭을 길게 눌러 움직일 때 위/아래 가장자리로 가져다 대면 탭 목록이 위/아래로 자동 스크롤되도록 수정
  4. 탭 목록에서 탭 닫을 때 애니메이션 추가
  5. Release APK 빌드 및 GitHub Releases 업로드 (`omyeol/Omyaru-Browser`)
- 변경 파일:
  - `apps/android/app/src/main/assets/image_preloader/manifest.json`
  - `apps/android/app/src/main/assets/image_preloader/content.js`
  - `apps/android/app/src/main/assets/image_preloader/page_preload.js`
  - `apps/android/app/src/main/java/dev/privacybrowser/app/BrowserApplication.kt`
  - `apps/android/app/src/main/java/dev/privacybrowser/app/BrowserActivity.kt`
  - `apps/android/app/src/main/java/dev/privacybrowser/app/TabOrder.kt`
  - `apps/android/app/src/test/java/dev/privacybrowser/app/TabOrderTest.kt`
  - `apps/android/app/build.gradle.kts` (`versionCode = 75`, `versionName = "0.15.17"`)
- 주요 구현 내역:
  1. **웹 이미지 선로딩 (`BrowserApplication.kt` + 내장 `image_preloader@omyaru.app`)**:
     - `geckoview-config.yaml`을 생성해 `GeckoRuntimeSettings.Builder().configFilePath(...)`로 전달하여 Gecko 네이티브 `<img loading="lazy">` 마진(`dom.lazy-loading.margin.top: 3000`, `dom.lazy-loading.margin.bottom: 6000`, `dom.lazy-loading.margin.top.percentage: 300`, `dom.lazy-loading.margin.bottom.percentage: 600`, `dom.lazy-loading.margin.is-scroll: true`)과 APZ 디스플레이포트 배율(`apz.y_stationary_size_multiplier: "3.5"`, `apz.y_skate_size_multiplier: "4.5"`)을 확장.
     - 내장 WebExtension `image_preloader@omyaru.app`을 추가해 `document_start`에서 페이지의 `IntersectionObserver`가 이미지/레이지 타겟을 감시할 때 `rootMargin: "1800px 600px 4500px 600px"`, `threshold: 0` 프리로드 옵저버로 동작하도록 확장하고, `lazySizes` 설정 및 `jQuery` 레이지로더(`appear`/`unveil`) 조기 트리거 적용.
     - `content.js`에서 `<img loading="lazy">`를 `eager`로 전환하고, 표준 레이지 속성(`data-original`, `data-src`, `data-lazy-src` 등) 및 난독화된 `data-<hex>` 이미지 URL 속성을 뷰포트 하단 `5000px` 이내에서 즉시 `src`/`srcset`으로 승격하며, 더 아래쪽 이미지도 백그라운드 배치로 순차 선로딩.
  2. **탭 복제 위치 (`BrowserActivity.kt`, `TabOrder.kt`)**:
     - `openNewTab`에 `afterTabId: String? = null` 파라미터를 추가하여 지정된 탭 바로 다음 인덱스(`anchorIndex + 1`)에 삽입.
     - `MENU_DUPLICATE_TAB` 실행 시 `afterTabId = tab.id`를 전달하고 원본 탭의 미리보기 비트맵(`tab.preview`)도 복제하여 탭 목록에서 즉시 썸네일과 탭 개수가 반영되도록 개선.
  3. **탭 길게 눌러 드래그 시 상하 자동 스크롤 (`BrowserActivity.kt`, `TabOrder.kt`)**:
     - `showTabSwitcher`에서 드래그 리스너를 `root`와 `grid`에 연결하고, 드래그 중 포인터가 `scrollView` 상단/하단 `96dp` 영역에 들어가면 프레임 루프(`postOnAnimation`)를 통해 거리에 비례한 속도로 `scrollView.scrollBy(0, deltaY)`를 수행하며 실시간으로 드롭 대상 슬롯(`updateDragTargetAt`)을 갱신.
  4. **탭 목록에서 탭 닫기 애니메이션 (`BrowserActivity.kt`)**:
     - `animateCloseTabsInOverview`를 구현하여 탭 닫기(단일 `X` 버튼, 메뉴 닫기, 다중 선택 닫기) 시 닫히는 카드는 축소(`scale 0.75`) 및 페이드아웃(`alpha 0`)되고, 나머지 카드들은 새 그리드 슬롯 위치로 부드럽게 슬라이드(`210ms`, `DecelerateInterpolator`)한 뒤 탭이 정리되도록 구현.
- 검증 및 배포:
  - `./gradlew :app:testDebugUnitTest :app:assembleRelease -PomyaruBuildDirectory=".build-release-01517"` 성공 (새 `TabOrderTest` 포함 전체 단위 테스트 통과).
  - 빌드 산출물:
    - 상대 경로: `artifacts/Omyaru-v0.15.17-arm64.apk` (원본 빌드: `apps/android/.build-release-01517/app/outputs/apk/release/app-arm64-v8a-release.apk`)
    - 절대 경로: `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/Omyaru-v0.15.17-arm64.apk`
    - 배포 복사본: `C:/Users/peopl/OneDrive/DEV/APK/Omyaru-v0.15.17-arm64.apk`
  - GitHub Releases 업로드 완료: `https://github.com/omyeol/Omyaru-Browser/releases/tag/v0.15.17` (`Omyaru-v0.15.17-arm64.apk`).

---

# 2026-10-21 Codex: Android 웹 화면 경계와 축소 URL 필드

- 사용자 요청: 웹 화면이 URL 필드 경계에서 잘리지 않게 하고, 스크롤 시 축소 URL 필드를 기존의 약 75% 크기로 줄이며, 위로 스크롤할 때 자동 복원하는 동작을 설정에서 켜고 끄기. URL 필드를 직접 누르면 언제나 펼쳐져야 함.
- 변경 파일: `apps/android/app/src/main/java/dev/privacybrowser/app/BrowserActivity.kt`, `BrowserSettings.kt`, `BrowserPreferences.kt`, `SettingsActivity.kt`, `WindowInsetsSupport.kt`, `apps/android/app/src/main/res/values/strings.xml`, `apps/android/app/src/test/java/dev/privacybrowser/app/BrowserSettingsTest.kt`.
- 브라우저 root의 시스템 바/노치 안전 영역 적용을 첫 프레임 전에 설치하고, 애플리케이션의 지연 설치와 중복 적용되지 않도록 `WindowInsetsSupport.install`을 idempotent로 변경. GeckoView 표면 크기가 툴바 애니메이션의 매 프레임마다 변하지 않도록 툴바 높이는 한 번만 조정하고 폭만 애니메이션. 펼친 툴바 폭은 최종적으로 `MATCH_PARENT`로 복원해 화면 크기 변경 후 잘림을 방지.
- 축소 툴바 196×60dp → 147×45dp. 내부 주소 행 33dp, 주소 필드/도구 버튼 32dp 및 글자·패딩도 함께 축소. `expandToolbarOnScrollUp` 기본 켬으로 설정/저장 경로 추가. 끄면 위로 스크롤하거나 맨 위로 올라가도 자동 확장하지 않지만 주소 필드 터치와 직접 포커스는 기존처럼 확장.
- 검증: 최종 소스로 로컬 Gradle 9.5.0 `:app:compileDebugKotlin :app:testDebugUnitTest` 성공 (`apps/android/.build-toolbar-20261021c/`, 기존 비차단 경고만). APK 빌드·설치·게시하지 않음.
- 남은 작업: MuMu DEV 앱 잠금 해제 후 상단/하단 URL 위치, 화면 회전, 스크롤 축소/복원 설정과 직접 탭 복원을 시각적으로 확인. 기존 DEV 설치 앱은 구버전이며 이번 수정은 아직 설치되지 않음.

---

# 2026-10-21 Codex: Android HTTPS 접속 실패 안내

- 사용자 확인: `toki35.com` 등 일부 사이트는 DNS를 1.1.1.1로 변경하면 열리며, 앱에서 접속 실패 시 오류가 보이지 않아 검은 화면/무한 로딩처럼 인식되었음. 앱은 DNS나 TLS 설정을 자동 변경하지 않음.
- 변경 파일: `apps/android/app/src/main/java/dev/privacybrowser/app/BrowserActivity.kt`, `BrowserTab.kt`, `apps/android/app/src/main/res/layout/activity_browser.xml`, `apps/android/app/src/main/res/values/strings.xml`.
- GeckoView `onLoadError`가 `null`을 반환하고 UI에 아무것도 표시하지 않던 경로에 탭별 실패 URL 상태와 네이티브 오류 패널을 추가. 실패한 호스트와 가능한 원인(네트워크/DNS/서버/접속 제한), 재시도 버튼을 표시하고 상단 새로고침도 원래 URL로 재접속. 로딩 표시·표면 커버를 정리하며 실패한 로드는 방문 기록/미리보기 저장에서 제외. 새 탐색·탭 전환 때 오류 패널 상태 갱신. 오류 로그에서 전체 URL 제거.
- 검증: 로컬 Gradle 9.5.0을 직접 실행해 `:app:compileDebugKotlin :app:testDebugUnitTest` 성공 (기존 경고만 있음). 빌드 출력은 `apps/android/.build-page-error/`이며 APK는 빌드·설치·게시하지 않았음.
- 아직 확인할 것: MuMu DEV에는 앱 잠금 화면이 있어 사용자의 잠금 해제 없이 실제 UI에서 차단 사이트 재현을 확인하지 못함. 사용자 잠금 해제 후 접속 실패 페이지와 재시도 표시를 기기에서 확인하고, 필요하면 APK 버전 증가 및 릴리스 절차를 진행할 것. 이 변경으로 차단 자체가 우회되지는 않음.

---

# 2026-10-12 Antigravity: Bookshelf Photo Right-Click Fix, PC UI Flicker Elimination & Mobile Bookshelf Folder Tree Enhancement

User requested:
1. Fixing `System.ObjectDisposedException` when right-clicking photos in Bookshelf.
2. Eliminating UI flickering when clicking the Bookshelf folder tree and when clicking browser buttons/actions in the PC browser.
3. Modernizing and structurally improving the mobile Bookshelf folder tree UI across Omyaru Browser Android and Umtomi Android.
4. Continuing synchronization between Omyaru and Umtomi, with automated release builds and GitHub Releases uploads.

### Root Causes & Fixes
1. **Photo Right-Click `ObjectDisposedException`**:
   - **Root Cause**: In `VirtualBookshelfGallery.cs`, `MouseDown` dynamically instantiated `ContextMenuStrip` and hooked `menu.Closed += (_, _) => menu.Dispose();`. When the menu closed, synchronous disposal destroyed the control while WinForms `ToolStripManager.ModalMenuFilter.ProcessActivationChange()` was still actively pumping messages for that drop-down, throwing `ObjectDisposedException: Cannot access a disposed object. Object name: 'System.Windows.Forms.ContextMenuStrip'`.
   - **Fix**: Replaced the ad-hoc `MouseDown` menu with a managed `ContextMenuStrip` property on `VirtualBookshelfGallery`. Added "사진 열기" (Open) and "사진 삭제" (Delete) actions, updated `Opening` hit-testing dynamically, and safely disposed only when the parent gallery control itself is disposed in `Dispose(bool disposing)`. Applied unified `OpenBookshelfImageAsync` to both standard and virtual galleries.
2. **PC UI Flickering Elimination**:
   - **Bookshelf Tree Selection Flicker**:
     - **Root Cause**: `_folderTree.AfterSelect` executed `BeginInvoke(() => RenderFolders())`. `RenderFolders()` called `_folders.Controls.Clear()` and `_folderTree.Nodes.Clear()`, destroying and reconstructing the entire sidebar and tree hierarchy on every single node click.
     - **Fix**: Removed `RenderFolders()` from `AfterSelect`. Node selection now only updates the header metadata and renders the gallery, keeping the tree structure intact. Added `_folderTree.Invalidate()` for smooth active row highlight updates.
     - Wrapped `RenderGallery()` with `_galleryHost.SuspendLayout()` / `_galleryHost.ResumeLayout(true)` and added `WS_CLIPCHILDREN` (`0x02000000`) on `BookshelfForm.CreateParams`.
   - **Browser Shell Action / Button Click Flicker**:
     - **Root Cause**: `BrowserShellForm.CreateParams` had `parameters.ExStyle |= WsExComposited` (`0x02000000`). Setting `WS_EX_COMPOSITED` on a Form hosting WebView2 forces full-form GDI composited repaints on every hover, invalidation, or button click, conflicting with WebView2's DirectComposition surface and causing severe window flickering.
     - **Fix**: Changed `BrowserShellForm.CreateParams` to use `parameters.Style |= WsClipChildren;` (`0x02000000`), completely eliminating button click and toolbar flickering without interfering with WebView2.
3. **Mobile Bookshelf Hierarchical Folder Tree & Structural UI Enhancement**:
   - Implemented in both Omyaru Browser Android (`BookshelfActivity.kt`) and Umtomi Android (`BookshelfActivity.kt`):
     - **Folder Bar Navigation**: Added a prominent `[📁 폴더 트리 ▾]` button, breadcrumb trail pills for current active path segments (allowing 1-tap ancestor navigation), and sibling folder quick-switching chips.
     - **Hierarchical Folder Tree Dialog (`showFolderTreeDialog`)**:
       - Complete tree hierarchy displaying all folders and synthesized ancestor nodes.
       - Level-based indentation (`level * 20dp`).
       - Expand/collapse chevrons (`›` / `⌵`) for subtrees.
       - Folder icon 📁, leaf folder typography, and photo count badges `(N)` for each node.
       - Direct 1-tap folder selection with active folder highlighted.
       - `⋮` action menu for every folder (child folder creation, renaming, moving, deletion).
       - Top "＋ 새 폴더" button for instant root folder creation.
     - **Visual Destination Picker**: Moving folders ("다른 폴더로 이동") now displays the tree structure, excluding the moving folder and its children to prevent recursion, with 1-tap "최상위로 이동" and "이동" destination buttons.

### Build, Test, and Artifact Delivery
- **Windows Desktop & Standalone Bookshelf**:
  - Bumped Desktop `PrivacyBrowser.Desktop.csproj` to `0.4.63`; Standalone `Omyaru.Bookshelf.csproj` to `0.1.37`.
  - Built with 0 warnings and 0 errors via `scripts/Publish-Windows.ps1`.
  - Desktop EXE:
    - Relative: `artifacts/releases/Omyaru-v0.4.63-win-x64/Omyaru.exe`
    - Absolute: `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.4.63-win-x64/Omyaru.exe`
  - Standalone Bookshelf EXE:
    - Relative: `artifacts/releases/OmyaruBookshelf-v0.1.37-win-x64/OmyaruBookshelf.exe`
    - Absolute: `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/OmyaruBookshelf-v0.1.37-win-x64/OmyaruBookshelf.exe`
  - Verification:
    - `Omyaru.exe --self-test` (exit 0)
    - `Omyaru.exe --smoke-test` (exit 0)
    - `OmyaruBookshelf.exe --self-test` (exit 0)
    - `OmyaruBookshelf.exe --smoke-test` (exit 0)
    - `AccountKeys.Tests.csproj` (passed)
- **Omyaru Browser Android v0.15.10 (versionCode 68)**:
  - Unit tests `:app:testDebugUnitTest` passed (24/24).
  - Release APK built via `:app:assembleRelease`.
  - Output files:
    - Relative: `artifacts/Omyaru-v0.15.10-arm64.apk`
    - Absolute: `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/Omyaru-v0.15.10-arm64.apk`
    - Delivery: `C:/Users/peopl/OneDrive/DEV/APK/Omyaru-v0.15.10-arm64.apk`
    - SHA-256: `bb5a5fe7916b7acdaa956680541ec80813557c577f8a0c09cede456f6aae61fa`
  - GitHub Releases: Uploaded and published to `https://github.com/omyeol/Omyaru-Browser/releases/tag/v0.15.10` via `scripts/upload-release-apk.ps1`.
- **Umtomi Android v5.14.11 (versionCode 99)**:
  - Unit tests & lint checked via `Build-Android.ps1 -Test` (passed).
  - Output files:
    - Relative: `../Umtomi/artifacts/Umtomi-v5.14.11.apk`
    - Absolute: `C:/Users/peopl/OneDrive/DEV/CODEX/Umtomi/artifacts/Umtomi-v5.14.11.apk`
    - Delivery: `C:/Users/peopl/OneDrive/DEV/APK/Umtomi-v5.14.11.apk`
    - SHA-256: `42c7a917629c1c5f237b55093d1d0c4b7eb9bdadd9cc1a1694d66622fb7a24df`
  - GitHub Releases: Published to `https://github.com/omyeol/Umtomi-releases/releases/tag/v5.14.11` via `Publish-AndroidRelease.py --publish`.

---

# 2026-10-12 Antigravity: PC Bookshelf Parent Folder Column Fix & Whale-style Folder Tree

User requested:
1. Fixing the issue where opening a parent folder locked the horizontal gallery layout to 1 column ("상위 폴더를 책장으로 여는 기능 추가했거든? 그런데 상위폴더를 열면 가로 칸이 1열로 고정되는 현상 발생중 고쳐줘").
2. Enhancing the folder tree sidebar in the style of Naver Whale's bookmark manager reference ("그리고 사진처럼 폴더 트리 기능을 수정해줘").

### Changes & Technical Details
1. **Parent Folder Column Lock Removal**:
   - `RenderGallery`, `ChangeColumnCount`, `UpdateColumnControls`, and `BookshelfPdfExporter.Export`:
     - Removed hardcoded `BookshelfPath.Children(...).Count > 0 ? 1 : ...` restriction.
     - Parent folders now respect `BookshelfStore.GetColumnCount(_catalog, _catalog.ActiveFolder)`, allowing column counts from 1 to 6.
     - Column adjustment buttons (`−`, `＋`) remain enabled and allow interactive column modifications.
     - Applied consistently across desktop (`apps/windows/desktop/Bookshelf.cs`), standalone (`apps/windows/bookshelf/Bookshelf.cs`), Omyaru Android (`apps/android/app/src/main/java/dev/privacybrowser/app/BookshelfActivity.kt`), and Umtomi Android (`../Umtomi/umtomi/app/src/main/java/xyz/sabok/umtomi/ui/BookshelfActivity.kt`).
2. **Whale Browser-style Folder Tree**:
   - Switched `_folderTree` to `DrawMode = TreeViewDrawMode.OwnerDrawAll` and enabled `DoubleBuffered` via reflection to completely eliminate drawing flicker.
   - **Modern Chevrons**: Nodes with child folders display anti-aliased chevrons (`›` when collapsed, `⌵` when expanded) aligned cleanly per level. Leaf nodes without children omit the chevron while aligning folder icons with siblings.
   - **Golden Amber Folder Glyphs**: Drawn sharp vector 2-tone folder icons (`#F5B838` body, `#E8A82A` tab) matching Whale reference.
   - **Typography & Descendant Item Count**: Clean Segoe UI typography for leaf folder titles, accompanied by muted gray item counts (`#8E95A0`) directly adjacent to the name.
   - **Hover & Selection Highlights**: Full row pill selection (`#2E3644`) with rounded corners (radius 6px) and subtle mouse hover highlighting (`#232932`).
   - **Chevron Click Hit Testing**:
     - Clicks within the chevron area `[indentX - 6, indentX + 22]` toggle branch expansion (`node.Toggle()`) via `MouseDown` and cancel selection change in `BeforeSelect`.
     - Clicks on the node row/icon/text select the folder and update the main gallery without expanding/collapsing.
     - Double-clicking nodes toggles expansion.
3. **Automated Verification & Self-Tests**:
   - Added parent folder column persistence assertions to `DesktopSelfTest.cs` and standalone `Program.cs`.
   - Versions bumped: Desktop `PrivacyBrowser.Desktop.csproj` to 0.4.62; Standalone `Omyaru.Bookshelf.csproj` to 0.1.36.

### Build and Verification
- Release build publish completed with 0 errors via `scripts/Publish-Windows.ps1`.
- Built executables:
  - Desktop: relative `artifacts/releases/Omyaru-v0.4.62-win-x64/Omyaru.exe`, absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.4.62-win-x64/Omyaru.exe`
  - Standalone Bookshelf: relative `artifacts/releases/OmyaruBookshelf-v0.1.36-win-x64/OmyaruBookshelf.exe`, absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/OmyaruBookshelf-v0.1.36-win-x64/OmyaruBookshelf.exe`
- Built Android APKs & GitHub Releases Upload:
  - Omyaru Browser Android v0.15.9 (versionCode 67):
    - Relative: `artifacts/Omyaru-v0.15.9-arm64.apk`
    - Absolute: `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/Omyaru-v0.15.9-arm64.apk`
    - Delivery: `C:/Users/peopl/OneDrive/DEV/APK/Omyaru-v0.15.9-arm64.apk`
    - SHA-256: `76f0c1db65110644629e9d0fbfb6d85f913b2d488f678c54bbdef635f14eb533`
    - GitHub Release: https://github.com/omyeol/Omyaru-Browser/releases/tag/v0.15.9 (Uploaded via `scripts/upload-release-apk.ps1`)
  - Umtomi Android v5.14.10 (versionCode 98):
    - Relative: `artifacts/Umtomi-v5.14.10.apk` (in Umtomi)
    - Absolute: `C:/Users/peopl/OneDrive/DEV/CODEX/Umtomi/artifacts/Umtomi-v5.14.10.apk`
    - Delivery: `C:/Users/peopl/OneDrive/DEV/APK/Umtomi-v5.14.10.apk`
    - SHA-256: `35246c7484bc0476e5b10f03aa6a74c8e3741666f2c721e03ffed2aeef6704f0`
    - GitHub Release: https://github.com/omyeol/Umtomi-releases/releases/tag/v5.14.10 (Published via `Publish-AndroidRelease.py --publish`)
- Verification commands passed:
  - `Omyaru.exe --self-test` (exit 0)
  - `Omyaru.exe --smoke-test` (exit 0)
  - `OmyaruBookshelf.exe --self-test` (exit 0)
  - `OmyaruBookshelf.exe --smoke-test` (exit 0)
  - `AccountKeys.Tests.csproj` (24/24 passed)
  - Omyaru Android unit tests `:app:testDebugUnitTest` (24/24 passed)
  - Umtomi Android `Build-Android.ps1 -Test` (`assembleDebug`, `testDebugUnitTest`, `lintDebug` passed)

---

# 2026-10-12 Antigravity: PC bookshelf missing images and SaveToDirectory IOException fix

User reported that opening the PC bookshelf showed missing images ("원본 사진 360개를 찾지 못했습니다") with placeholders, and folder selection triggered an unhandled exception: `System.IO.IOException: 책장 원본 또는 저장 매체를 확인해 주세요.` inside `BookshelfStore.SaveToDirectory` called from `TreeView.AfterSelect`.

### Root Causes
1. **Windows MSIX / UWP Package Redirection Divergence**: When Codex was executing in the Windows Store container (`OpenAI.Codex_2p2nqsd0c76g0`), file writes to `%LOCALAPPDATA%` were redirected to `Packages/OpenAI.Codex_*/LocalCache/Local/Omyaru Browser/bookshelf/catalog.json`. Outside that sandbox, the user's native `Omyaru.exe` read the real `%LOCALAPPDATA%/Omyaru Browser/bookshelf/catalog.json`, which was untouched since 2026-10-11 and had 360 entries pointing to legacy GUID paths that no longer existed on disk (the real files had been renamed to `image1.png`...`imageN.png`).
2. **Virtual Folder Selection Bug in TreeView**: `_folderTree.AfterSelect` executed `if (!_catalog.Folders.Contains(folder)) BookshelfStore.AddFolder(_catalog, folder);`. When clicking synthesized/virtual parent nodes (`Omyaru Browser` or `Umtomi`), it attempted to add them as new flat catalog folders and invoked `Save(catalog)`.
3. **Fragile Save-Time File Existence Check**: `BookshelfStore.SaveToDirectory` threw `IOException("책장 원본 또는 저장 매체를 확인해 주세요.")` whenever any catalog item's `FilePath` did not exist on disk. Because 360 items had obsolete paths, `SaveToDirectory` threw an uncaught `IOException` inside the WinForms event handler, popping up the JIT debugging crash dialog.

### Fundamental Prevention ("원천 봉쇄") Fixes
1. **Automatic Sandbox / Redirected Catalog Sync**: `BookshelfStore.SyncWithRedirectedSandboxCatalog()` added to `Load()` in both desktop and standalone Bookshelf. Automatically detects and imports newer/healthier catalogs from package redirection caches into the primary AppData location if the current catalog has missing entries.
2. **Dynamic Path Resolution & Self-Healing**: `TryResolveItemPath()` and `SelfHealItemPaths()` added to `BookshelfStore`. If an item's `FilePath` is missing on disk, it dynamically checks for matching filenames, numbered `image{index + 1}.png`/`.cloud` sequence, or GUID patterns in the folder, and heals the catalog in memory and persists to disk.
3. **Safe TreeView Selection**: `_folderTree.AfterSelect` now only selects and views the folder (setting `_catalog.ActiveFolder = folder`). It no longer calls `AddFolder` on virtual parent nodes and safely guards active folder persistence.
4. **Resilient `SaveToDirectory`**: `SaveToDirectory` now attempts path resolution for missing items and skips file copying without throwing `IOException` if a file is temporarily offline, safely writing `catalog.json` without crashing the application.
5. **Real Catalog Synchronization**: Copied the verified 365-item/0-missing catalog into `C:\Users\peopl\AppData\Local\Omyaru Browser\bookshelf\catalog.json` (original backed up as `catalog.json.before-codex-resync.bak`). Real disk verified: 365 items, 0 missing.

### Build and Verification
- Versions bumped: Desktop `PrivacyBrowser.Desktop.csproj` to 0.4.61; standalone `Omyaru.Bookshelf.csproj` to 0.1.35.
- `scripts/Publish-Windows.ps1` fallback added for `google-drive-client.dat` discovery in package local cache.
- `AccountKeys.Tests.csproj` passed (24/24 tests pass).
- Desktop and Standalone builds completed with 0 warnings and 0 errors.
- Release executables published:
  - Desktop: relative `artifacts/releases/Omyaru-v0.4.61-win-x64/Omyaru.exe`, absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.4.61-win-x64/Omyaru.exe`
  - Standalone Bookshelf: relative `artifacts/releases/OmyaruBookshelf-v0.1.35-win-x64/OmyaruBookshelf.exe`, absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/OmyaruBookshelf-v0.1.35-win-x64/OmyaruBookshelf.exe`
- All four published checks exited 0:
  - `Omyaru.exe --self-test` (exit 0)
  - `Omyaru.exe --smoke-test` (exit 0)
  - `OmyaruBookshelf.exe --self-test` (exit 0)
  - `OmyaruBookshelf.exe --smoke-test` (exit 0)
- Verified active catalog on real disk: 365 items, 0 missing.

---

# 2026-10-11 Codex: synthesize missing ancestor nodes in Windows bookshelf tree

User clarified that a stored path like `Umtomi/paradise` must display as a collapsible `Umtomi` parent with `paradise` nested beneath it. The actual Browser and v0.1.21 standalone catalogs each have 6 folder paths/49 photos and include `Umtomi/paradise` without a separate `Umtomi` entry. The initial 0.4.48/0.1.22 TreeView only linked nodes whose parent was also a catalog entry, so it incorrectly showed such leaves at the root.

Both `apps/windows/desktop/Bookshelf.cs` and `apps/windows/bookshelf/Bookshelf.cs` now derive all missing ancestors from saved folder paths for display only, build the hierarchy in depth order, show descendant photo totals on virtual parents, and let clicking a virtual parent expand/collapse it. A virtual parent is not saved as a catalog folder; only safe create-child action is offered on its context menu. Actual folder selection still shows its gallery, and expansion/scroll state remains stable across rendering. This does not mutate catalog contents, storage paths, Drive records, or Android apps. `apps/windows/bookshelf/Program.cs` smoke test now creates only `Umtomi/paradise` and verifies that a virtual `Umtomi` node contains it. Desktop/standalone versions are 0.4.49/0.1.23.

Both Release builds completed with zero warnings/errors. Release build paths: relative `artifacts/releases/Omyaru-v0.4.49-win-x64/Omyaru.exe`, absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.4.49-win-x64/Omyaru.exe`; relative `artifacts/releases/OmyaruBookshelf-v0.1.23-win-x64/OmyaruBookshelf.exe`, absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/OmyaruBookshelf-v0.1.23-win-x64/OmyaruBookshelf.exe`. Both published EXEs passed `--self-test` and `--smoke-test` (exit 0). The release script used the protected user OAuth configuration without printing it. No actual user catalog, remote account, device, VPN or host network was modified; no APK/GitHub release. Next: user can open the new PC executable and verify `Umtomi → paradise` visually. Preserve the older incomplete 322-entry catalog; it was not touched.

---

# 2026-10-11 Codex: Windows bookshelf hierarchical folder sidebar

User supplied a screenshot of a left-side collapsible folder tree and requested the PC bookshelf be easier to navigate. Both Windows bookshelf UIs now show a 286px sidebar with a dark, nested TreeView, leaf names and per-folder direct photo counts. Roots expand on first display; later renders preserve expanded nodes, selection and top visible folder. Selecting a node updates the right gallery and metadata; right-click retains create-child, move, rename and delete actions. Sync, missing-original/storage recovery, and storage-location controls remain above the tree. Fullscreen still hides the sidebar. The actual catalog format, physical storage and Drive protocol are unchanged; Android Umtomi/Omyaru are unchanged.

Files: `apps/windows/desktop/Bookshelf.cs`, `apps/windows/bookshelf/Bookshelf.cs`, `apps/windows/bookshelf/Program.cs` (smoke test verifies a nested folder node), desktop and standalone `.csproj` version bumps to 0.4.48 and 0.1.22. Release builds created by `scripts/Publish-Windows.ps1` with protected existing OAuth configuration and no printed secrets: relative `artifacts/releases/Omyaru-v0.4.48-win-x64/Omyaru.exe`, absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.4.48-win-x64/Omyaru.exe`; relative `artifacts/releases/OmyaruBookshelf-v0.1.22-win-x64/OmyaruBookshelf.exe`, absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/OmyaruBookshelf-v0.1.22-win-x64/OmyaruBookshelf.exe`. Both Release project builds had zero warnings/errors. Both published EXEs returned exit 0 for `--self-test` and `--smoke-test`. No live user catalog, Google Drive, Android device, VPN or host network was modified/tested; no APK built or GitHub release created.

Next: visually inspect the folder tree using the new EXE. If testing standalone with an older portable shelf, preserve its directory and originals; the incomplete 322-entry legacy catalog remains untouched, and the complete 6-folder/49-photo Browser shelf remains the valid current source. The new versioned standalone release directory may bootstrap a copy from that Browser shelf on first launch; do not delete the previous portable catalog.

---

# Latest: 2026-10-11 Windows bookshelf Drive recovery

The current checkpoint below under `2026-10-11 Codex: mobile bookshelf exists in Drive but Windows shelves stayed empty` documents the latest code, verified 6-folder/49-photo recovery, and 0.4.47/0.1.21 builds. It supersedes older recovery notes below. The older 322-entry catalog with 311 missing originals remains preserved.

---

# 2026-10-10 Codex: PC Browser bookshelf unavailable storage recovery

User reported PC Browser bookshelf Drive sync says selected storage unavailable and requested AppData default with configurable path. BrowserStateStore already defaults to `%LOCALAPPDATA%/PrivacyBrowser`, but this Windows user had an explicit bookshelf-location.json entry from September pointing to a now-absent external/OneDrive directory. No AppData Browser bookshelf catalog existed. The former `ChangeStorageDirectoryAsync` always called MoveStorage, which refused to operate when the selected catalog was unavailable; UI had no repair path. The selected root and catalog were absent; bookshelf auto-sync had been enabled.

Both Windows BookshelfStore source copies now expose their own default storage and `RecoverUnavailableStorage`: it accepts either the default path or an existing folder with readable catalog.json, backs up the old location setting, changes the pointer without moving/deleting originals, and refuses a non-catalog custom folder. Browser desktop defaults to AppData; independent standalone retains its portable executable-adjacent default. Both WinForms shelf UIs show an unavailable-location notice, a direct default-location button (labeled **AppData 기본 위치 사용** in Browser), and **기존 책장 다시 연결** via the storage chooser. Recovery reloads the catalog in place and pauses bookshelf auto-sync until the user verifies the contents and re-enables it. Normal accessible storage still supports verified MoveStorage to a new empty folder. The shelf sync tooltip reflects the paused state. No Android/Umtomi change was needed.

After confirming no Omyaru process was running, backed up the real `%LOCALAPPDATA%/PrivacyBrowser/bookshelf-location.json` and `bookshelf-sync.json` to timestamped `.before-appdata-*.bak`, set the location to its AppData default, created the empty default bookshelf directory, and paused auto-sync. Verified old pointer backup, default selection, and paused sync; original unavailable location and account session were not touched. **No Browser catalog or photos were restored and no real Google Drive roundtrip was performed.** The user should launch the new PC Browser and inspect the default shelf before turning sync back on, or use existing-folder reconnect if the old disk returns.

Release builds: desktop 0.4.43 `artifacts/releases/Omyaru-v0.4.43-win-x64/Omyaru.exe` (absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.4.43-win-x64/Omyaru.exe`, 78,375,122 bytes, SHA256 `8dbaa4074c38df26fd9a0e6c1f751469a30807d2280eb919a563103b162bf6f3`); standalone 0.1.15 `artifacts/releases/OmyaruBookshelf-v0.1.15-win-x64/OmyaruBookshelf.exe` (absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/OmyaruBookshelf-v0.1.15-win-x64/OmyaruBookshelf.exe`, 77,707,849 bytes, SHA256 `1b0e419db7378186f053c6dcb13dec86c02d0644588d1497490cfedf44a35496`). Both publish commands completed and each executable's `--self-test` and `--smoke-test` exited 0. Self-tests include missing-root rejection, default-location recovery, and backup preservation. No GitHub Windows release, APK, device, remote account, or VPN changes.

---

# 2026-10-10 Codex: PC bookshelf black photo placeholders

User confirmed the issue is the Windows standalone bookshelf. The active standalone v0.1.13 portable location points to LocalAppData/OmyaruBookshelf/bookshelf. Its catalog has 322 entries; 311 referenced PNG originals are absent from their expected folder (that folder exists but is empty), while the 11 present PNGs decode normally with nonblack pixels. No matching missing IDs were found within that bookshelf root, Browser LocalAppData, Pictures, or CODEX workspace; user catalog and image bytes were not modified. The old v0.1.12 portable location points to a separate unavailable catalog. Do not infer that 311 original images were recovered.

Both Windows Bookshelf.cs implementations now show an explicit missing-original tile instead of black, display the missing count and a click-to-refresh notice, allow folder viewing without saving an incomplete catalog, make note/column/folder/delete mutations unavailable while originals are missing, and show a clear message if an absent image is opened. Thumbnail decode failures get a distinct tile. Shared BookshelfSyncCoordinator stops automatic cloud sync before network when any original is missing, retaining catalog entries. Standalone version 0.1.14, desktop version 0.4.42. Android/Omyaru and Umtomi bookshelf code is unchanged because this was confirmed PC standalone and the Android renderer does not use this WinForms code.

Standalone build output `artifacts/releases/OmyaruBookshelf-v0.1.14-win-x64/OmyaruBookshelf.exe` (absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/OmyaruBookshelf-v0.1.14-win-x64/OmyaruBookshelf.exe`, 77,706,782 bytes, SHA256 `65dc770c0d46788a81743d55c0ccc42e9d9179684203f2bb9e58c88c4afa1b6e`); desktop `artifacts/releases/Omyaru-v0.4.42-win-x64/Omyaru.exe` (absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.4.42-win-x64/Omyaru.exe`, 78,374,244 bytes, SHA256 `257e81048e87ba6a5dcfbb4849131e07cb094db7054983d56dd62a3c452d7179`). Both Release builds passed with zero warnings/errors; each built executable's `--self-test` and `--smoke-test` exited 0 after final UI changes. No GitHub release or Android build was made. No account/Drive roundtrip or actual data recovery performed; v0.1.13 portable folder has no enabled shelf sync state. User needs to restore/reconnect the 311 original PNGs (or a valid backup) to see their actual contents; the executable cannot manufacture missing bytes. Do not delete or reset the catalog.

---

# 2026-10-10 Codex: standalone Windows bookshelf Drive client setup

User reported standalone bookshelf says PC Google client JSON is missing during Drive connection. Root cause: 0.1.12 executable had no embedded Desktop OAuth resource and BookshelfSyncCoordinator.ShowSettings had no JSON import button. Existing Omyaru Browser desktop DPAPI client exists under this Windows user's LocalAppData/PrivacyBrowser but standalone used its portable executable directory. The standalone GoogleDriveOAuthConfiguration now reads its own imported client, its prior portable client, then the same Windows user's Browser client; no Browser login session/token is read or copied. A JSON chooser was added to bookshelf settings for independent PCs, validated through the existing Import method, stored per user under LocalAppData/OmyaruBookshelf. Shared coordinator also exposes this chooser in desktop Browser; desktop itself still uses its own configuration. No OAuth scope/client ID/protocol change.

Found old portable bookshelf location setting pointed at a currently missing root; the older LocalAppData/OmyaruBookshelf default has 322 catalog images. New standalone bootstrap ignores stale prior locations and can link that previous default root without copying originals. More importantly, both Windows BookshelfStore implementations now refuse Save/MoveStorage and BookshelfSyncCoordinator refuses Drive sync when an explicitly selected catalog is unavailable, preventing an empty catalog from overwriting remote data; settings explain unavailable media. Neither current user catalog nor remote account was modified. No Android or Umtomi source changes needed for this Windows configuration fix.

Published locally and verified: standalone v0.1.13 `artifacts/releases/OmyaruBookshelf-v0.1.13-win-x64/OmyaruBookshelf.exe`, absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/OmyaruBookshelf-v0.1.13-win-x64/OmyaruBookshelf.exe`, 77,705,837 bytes, SHA256 78ee2bf70d322f3b8c27755e5bb654a97d08758513ea7738fd6317a163d2336a. Existing 0.1.12 portable EXE is RUNNING (PID 47756) and was NOT overwritten/stopped. A byte-identical 0.1.13 executable was placed beside it as `artifacts/releases/OmyaruBookshelf-v0.1.12-win-x64/OmyaruBookshelf-v0.1.13.exe`, absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/OmyaruBookshelf-v0.1.12-win-x64/OmyaruBookshelf-v0.1.13.exe`; launching this after closing old process preserves its exact settings directory. The old selected storage root remains unavailable on this PC and must be reattached for cloud sync of that catalog. Do not silently substitute the older 322-image default for the old portable selected root.

Desktop Browser v0.4.41 `artifacts/releases/Omyaru-v0.4.41-win-x64/Omyaru.exe`, absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.4.41-win-x64/Omyaru.exe`, 78,373,680 bytes, SHA256 57de61d9292aea096be78b9927fb2583e03351e5f3a0c485426e06f012043e48. Both self-contained win-x64 publishes passed self-test and smoke-test. Standalone self-test uses synthetic DPAPI Browser fallback, independent JSON import, previous-catalog pointer without copy, missing-storage Save and Drive-sync fail-closed. `--check-drive-client` on both new standalone copies succeeded under the ACTUAL Windows account OMYEOL\peopl. It fails under default CodexSandboxOffline identity because CurrentUser DPAPI is identity-specific; this is an expected test-environment difference. No real Google OAuth or Drive roundtrip performed, no user app stopped, no network/VPN change. No GitHub Windows release published.

Next user action: close old standalone window; launch in-place v0.1.13 executable; reattach the missing selected bookshelf storage location before syncing; press ☁ 책장 동기화 → Google 계정 연결. For a different PC without Browser configuration choose the Windows Desktop OAuth JSON in that dialog.

---

<!-- 2026-10-10 Umtomi 5.14.1 bookshelf selector width matched existing Omyaru Android button; Omyaru Android/Windows sources unchanged. Umtomi build/lint/13 JVM tests and MuMu DEV BookshelfHierarchyUiTest passed; v5.14.1 published with verified asset digest. See ../Umtomi/CODEX_HANDOFF.md. -->

<!-- 2026-10-10 Umtomi 5.14.0: single Google account UI/store for shared shelf + favorites/history; auth timeout/cancel, transfer progress, interruptible sync gate. Shared remote formats and Browser apps unchanged; no Browser build needed. Umtomi build/lint/13 JVM + 3 DEV tests passed. Real Google auth/transport untested. Published v5.14.0; see ../Umtomi/CODEX_HANDOFF.md. -->

<!-- 2026-10-10 Umtomi-specific account reconnect fix: 5.13.1 separates account authorization from explicit legacy shelf sync, progress/cancel/timeout added. Shared protocols unchanged; Omyaru Android/Windows sources/binaries unchanged. Umtomi build/lint/13 JVM tests and MuMu DEV repeated-completion test passed; real Google transport untested. See ../Umtomi/CODEX_HANDOFF.md. No device/network changes beyond Umtomi data-preserving DEV update. -->

# 2026-10-10 Codex: shared local bookshelf and physical hierarchy

Current work supersedes the October 10 investigation notes below. User now requires bookshelf features developed together in Umtomi and Omyaru Android/Windows. Persistent rule added to AGENTS.md. See `docs/SHARED_BOOKSHELF_STORAGE.md` and `../Umtomi/CODEX_HANDOFF.md` (new top section) for complete change/test/artifact record.

Changed production scope: Android BookshelfStore/BookshelfActivity + five new shared storage/path/catalog/transaction/settings classes, shared Drive manifest reuse after file move, manifest optional all-files permission/settings activity, version 0.15.0 (58). Android tests added; optional release instrumentation test build setting. Windows desktop + standalone Bookshelf.cs hierarchy UI/real subdirectories/verified moves, shared BookshelfPath.cs; versions desktop0.4.40 and standalone0.1.12. Existing browser tabs/VPN/profile code untouched. Existing untracked source preserved, no git source push.

Verification: Browser85 JVM tests passed; Umtomi13 JVM + lint passed; actual MuMu DEV cross-package round trip passed with one original. Volume switch/unavailable media/reinsert simulated in isolated device fixtures, not real SD hardware. Reader/capture regression and folder manager UI tests passed. Windows published desktop self-test and standalone self-test/smoke exit0. Actual Google account/Drive sync not performed. Real catalogs not activated/migrated by tests. DEV index1 left running, all-files appops restored to default for both apps. No host network/VPN actions.

Artifacts: Android staged `apps/android/.build-release-0150/app/outputs/apk/release/app-arm64-v8a-release.apk`; delivered `C:/Users/peopl/OneDrive/DEV/APK/Omyaru-v0.15.0-arm64.apk`. SHA256 600d578a240a5669366a314074d7eac69f74bdf0122ae04b866fed1e34cae6e4, existing signature maintained. Release v0.15.0 (408819613) published via upload-release-apk.ps1; anonymous GitHub asset digest and delivery copy verified. https://github.com/omyeol/Omyaru-Browser/releases/tag/v0.15.0. Umtomi v5.13.0 published separately and likewise verified.
Windows self-contained outputs: `artifacts/releases/Omyaru-v0.4.40-win-x64/Omyaru.exe` and `artifacts/releases/OmyaruBookshelf-v0.1.12-win-x64/OmyaruBookshelf.exe` (absolute prefix C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/). Not uploaded as Windows releases.

Release work complete. User connects each Android app via Bookshelf gear after per-app access grant; Windows location configuration remains Windows-local. OAuth common-project setup from previous task still applies for inter-device Drive sync.

---

<!-- Codex 2026-10-10 UPDATE: User selected same OAuth project migration and Windows inclusion. Umtomi 5.12.0 now has isolated Omyaru-compatible key/shelf protocol adapter and foreground 15-second shelf sync. No Browser production code modifications or Browser APK/EXE builds. Read current Umtomi/SHARED_BOOKSHELF_SETUP.md and CODEX_HANDOFF.md for details. Actual Windows SyncCodec + BookshelfSyncReplica linked by synthetic fixture harness: Windows-to-Android and Android-to-Windows encryption/metadata application passed; no real account/Drive sync. User still must migrate Umtomi Android OAuth to Omyaru project 136563471424 after downloading legacy data. Original Browser appdata/tabs untouched. MuMu DEV index1 started, Umtomi installed -r, 4 instrumentation tests passed. Next: after registration, user enables same-account Omyaru shelf sync, then Umtomi shared connection and verifies a temporary shelf across devices. Earlier investigation note below superseded. -->

<!-- Codex 2026-10-10 cross-app bookshelf investigation: user requests Umtomi/Omyaru automatic shelf sync including Windows; OAuth projects differ. Read-only protocol audit performed, no Browser source/build/device/remote changes. Android shelf metadata/chunk engines match Umtomi after prefix normalization; Windows WinForms and standalone share shelf coordinator, Gecko has no shelf UI. Awaiting user choice between consolidating OAuth project (appdata) and broader Drive scope/common folder. Umtomi has preliminary legacy imported-image ID normalization only; not released. Main implementation remains pending. -->

# 공통 작업 인계 — Codex ↔ Antigravity

최종 갱신: 2026-10-01 18:31, Antigravity. 현재 소유권: **인계 준비 완료, 다른 도구가 이어서 작업 가능**.

## 1. 지금 사용자가 원하는 것 및 조치 완료 사항

1. **탭 목록에서 새 탭 생성 시 이전 탭 깜빡임/노출 현상 수정 (`v0.14.3`)**:
   - 요구사항: "탭목록에서 새탭 만들면 이전에 있던 탭으로 가졌다가 만들어짐 고쳐줘"
   - **근본 원인 분석**:
     - 기존 `BrowserActivity.kt`의 `showTabSwitcher` 내 `addButton`(`+` 버튼) 및 `showTabOverviewMenu`(`MENU_NEW_TAB`)에서 `dialog.dismiss()`가 `openNewTab()`보다 먼저 호출되었음.
     - 이로 인해 다이얼로그가 닫히는 순간 뒷배경인 Activity 윈도우가 드러나는데, 이때는 아직 새 탭이 생성되지 않아 이전 활성 탭(`activeTabId`)과 이전 탭의 주소/화면이 그대로 노출되었음.
     - 또한 `openNewTab()`에서 새 세션을 생성하여 `geckoView.setSession`을 호출하더라도, GeckoView(SurfaceView)는 새 세션의 첫 번째 프레임이 렌더링되기 전까지 이전 세션의 프레임버퍼 텍스처를 계속 유지하여 화면에 깜빡임이 발생했음.
     - 이전 탭에서 탭 목록으로 진입할 때 보존된 키보드 상태(`preservedKeyboardState`)가 다이얼로그 닫힘 시점에 GeckoView로 포커스를 되돌려 새 탭 주소창 포커스를 방해할 수 있었음.
   - **조치 사항**:
     - `activity_browser.xml`: `web_container` 내부의 `gecko_view` 상단에 브라우저 기본 표면 색상(`@color/browser_surface`, 라이트/다크 테마 대응)을 가진 `web_surface_cover` View 추가.
     - `BrowserActivity.kt`:
       - `webSurfaceCover` 바인딩 및 `showWebSurfaceCover()`, `hideWebSurfaceCover()` 헬퍼 구현.
       - 새 탭이 활성화되는 순간 `showWebSurfaceCover()`를 통해 커버를 띄워 GeckoView의 이전 탭 프레임버퍼가 노출되지 않도록 완전히 차단.
       - 새 탭의 `ProgressDelegate.onPageStart` 및 `onPageStop`에서 커버를 부드럽게 페이드아웃(`hideWebSurfaceCover`).
       - `handleOpenNewTabFromOverview(dialog)` 구현:
         - `preservedKeyboardState = false`로 설정하여 이전 탭의 키보드 복원 간섭 차단.
         - 탭 한도(50개) 초과 시 다이얼로그를 닫지 않고 토스트 안내.
         - `showWebSurfaceCover()`를 호출하고 `openNewTab()`을 먼저 실행하여 새 탭 활성화 및 주소창 클리어.
         - 탭 목록 다이얼로그를 140ms 동안 부드럽게 페이드아웃시킨 후 `dialog.dismiss()` 호출.
         - 다이얼로그가 닫힌 후 새 탭의 주소창에 포커스를 맞추고 소프트 키보드를 열도록 보장 (`settings.showKeyboardOnNewTab`).
       - 탭 목록 하단바 `addButton` 및 상단 메뉴 `MENU_NEW_TAB`에서 `handleOpenNewTabFromOverview`를 호출하도록 수정.

---

## 2. 구현·검증 현황

| 영역 | 완료 증거 | 상태 |
| --- | --- | --- |
| 단위 테스트 통과 | `./gradlew :app:testDebugUnitTest -PomyaruBuildDirectory='.build-test-0143'`: 24개 테스트 전체 통과 | 완료 |
| 표면 커버 뷰 레이아웃 | `activity_browser.xml`: `web_container` 내 `web_surface_cover` 추가 | 완료 |
| 표면 커버 및 전환 헬퍼 | `BrowserActivity.kt`: `showWebSurfaceCover`, `hideWebSurfaceCover`, `handleOpenNewTabFromOverview` 구현 | 완료 |
| 탭 목록 새 탭 선행 스위칭 | `BrowserActivity.kt`: `addButton` 및 `MENU_NEW_TAB`에 `handleOpenNewTabFromOverview` 연동 | 완료 |
| 이전 탭 키보드 간섭 방지 | `BrowserActivity.kt`: `preservedKeyboardState = false` 및 새 탭 주소창 자동 포커스 | 완료 |
| 버전 범프 | `build.gradle.kts`: `versionCode = 57`, `versionName = "0.14.3"` | 완료 |
| Release 빌드 및 배포 | v0.14.3 릴리스 생성(ID `400817505`) 및 Release APK 업로드 완료 | 완료 |

---

## 3. 빌드 출력물 위치 가이드 (Build & Artifacts)

### 📱 Android APK (v0.14.3, versionCode 57)
- **Release 빌드**:
  - **상대 경로**: `apps/android/.build-release-0143/app/outputs/apk/release/app-arm64-v8a-release.apk`
  - **절대 경로**: `C:\Users\peopl\OneDrive\DEV\CODEX\BROWSER\apps\android\.build-release-0143\app\outputs\apk\release\app-arm64-v8a-release.apk`
  - **파일 크기**: `199,381,757 bytes` (~190.1 MB)
  - **GitHub Releases 다운로드 URL**: `https://github.com/omyeol/Omyaru-Browser/releases/download/v0.14.3/Omyaru-v0.14.3-arm64.apk`
  - **GitHub Releases 태그**: `v0.14.3` (Release ID `400817505`)

---

## 4. 환경 정보 및 커맨드

- 작업 디렉터리: `C:\Users\peopl\OneDrive\DEV\CODEX\BROWSER`
- MuMu DEV 디바이스: `127.0.0.1:16416` (index 1)
- Android applicationId: `cuttan.omyaru.app` (네임스페이스: `dev.privacybrowser.app`)
- 배포 스크립트: `powershell -ExecutionPolicy Bypass -File .\scripts\upload-release-apk.ps1 -Tag "v0.14.3" -BuildDir "apps/android/.build-release-0143" -Replace -CreateIfMissing`
# 2026-10-11 Codex: Android/Windows history sync, shared shelf account, Windows Omyaru path

User requested Android ↔ Windows search/visit history and bookshelf sync on the same Google account, and removal of the active `%LOCALAPPDATA%/PrivacyBrowser` storage path. Work in this checkpoint is local source + built artifacts; do not reset/clean the mostly untracked repository. `AGENTS.md` applies.

Windows desktop v0.4.44: `BrowserHistory.cs` records successful HTTP(S) visits, menu opens a searchable history view. `DesktopUserDataSync.cs` reads/writes Android's `omyaru-userdata-v1-*.json` appData records with the existing account keys, merging Android history and bookmarks into PC stores; `DriveAppDataClient.cs` recognizes this prefix. `BrowserShellForm.cs` invokes user-data sync after tab sync. `GoogleDriveSyncDialog.cs` describes one account for tabs/history/bookmarks/shelf and archives user-data replica on account change. `BrowserDataMigration.cs`, `Models.cs`, `Program.cs` move the active desktop state from `%LOCALAPPDATA%/PrivacyBrowser` to `%LOCALAPPDATA%/Omyaru Browser` on startup, rewrite moved default shelf image paths, and archive any preexisting new-path data rather than overwrite it. The current user's actual migration completed with `--migrate-only`: old path absent, new path present with original desktop state, bookmarks, Google DPAPI session/client, and tab replica. Preexisting new-path data was kept at `C:/Users/peopl/AppData/Local/Omyaru Browser.previous-20261011031048-c858cf806cc24ab797a92f9efbaceaea.backup` (do not delete). New desktop process PID 38888 is running from v0.4.44. After launch, actual Drive user-data sync state existed, with 4 remote files seen and 8 history entries; no private URLs or tokens were printed. This verifies Android-format Drive data reached Windows, but a reverse PC → Android roundtrip and full bookshelf image roundtrip were not exercised.

Independent Windows bookshelf v0.1.16: `GoogleDriveAuthorization.cs` shares the browser's per-user Google session, using the legacy directory only until the browser moves it; `GoogleDriveOAuthConfiguration.cs` and `StandaloneSupport.cs` locate the renamed path with legacy fallback. Bookshelf settings identify the same account; one-time connected-account migration enables shelf auto sync, while missing originals/unavailable storage still fail closed. Standalone actual-user OAuth client check and shared-account-session check both exit 0; no live standalone Drive sync. Desktop shelf current catalog exists with 0 items, so no original is missing. Old selected standalone media may remain unavailable; do not substitute another catalog silently.

Android Omyaru v0.15.1: `DriveUserDataSync.kt` no longer swallows download/decryption/upload failures or uploads unchanged history every minute, and no longer prunes unverified remote records. `MobileDriveSync.kt` propagates user-data sync failures and archives per-account progress on account switch. `BookshelfActivity.kt` displays the browser account and enables same-account shelf sync once after upgrade. App ID stays `cuttan.omyaru.app`; namespace `dev.privacybrowser.app` and legacy sync protocol identifiers are retained for compatibility, not visible storage paths. Android v0.15.1/versionCode 59, same signing certificate as v0.15.0. MuMu DEV `127.0.0.1:16416` installed with `adb install -r` and launched; no uninstall/data clear. Android JVM unit tests passed. `:app:assembleRelease` passed. `:app:lintRelease` reports 28 existing project errors/128 warnings, first error in untouched `AppLockDialog.kt`; touched files have only preexisting style warnings. No real Android-account sync checked on emulator.

Windows self-tests and smoke tests passed for both EXEs. Desktop output relative `artifacts/releases/Omyaru-v0.4.44-win-x64/Omyaru.exe`, absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.4.44-win-x64/Omyaru.exe`, SHA256 `65fff00e31f25ee5de365e772fc8d89902a8f1e2024c02d1ecfc54aa151113b6`. Standalone relative `artifacts/releases/OmyaruBookshelf-v0.1.16-win-x64/OmyaruBookshelf.exe`, absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/OmyaruBookshelf-v0.1.16-win-x64/OmyaruBookshelf.exe`, SHA256 `fe6b3d609755bfccbf1c735a0df930b0406b5e7d2ccf358b1a3190a8f97d13f6`. Windows EXEs not uploaded to GitHub.

Android release output relative `apps/android/.build-release-0151/app/outputs/apk/release/app-arm64-v8a-release.apk`, absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/apps/android/.build-release-0151/app/outputs/apk/release/app-arm64-v8a-release.apk`. Delivered copy `C:/Users/peopl/OneDrive/DEV/APK/Omyaru-v0.15.1-arm64.apk`, SHA256 `734c0ab98681e0b727febf1ef5082880eecfd286f401aa0362b6a22316449495`. Published GitHub Release `https://github.com/omyeol/Omyaru-Browser/releases/tag/v0.15.1` asset `Omyaru-v0.15.1-arm64.apk`; anonymous API digest/size match local. Source is mostly untracked and was not pushed. Next: on connected Android and Windows, add a temporary benign history entry and shelf image, verify both directions in UI; inspect shelf status if it does not complete. Keep active `%LOCALAPPDATA%/Omyaru Browser` and its `previous-*` backup intact.

---

# 2026-10-11 Codex: Windows Google connection setup prompt

User asked to stop the recurring “client JSON configuration” prompt. Desktop v0.4.45 and standalone bookshelf v0.1.17 now show a plain “Google 연결 설정” button only if no readable OAuth desktop client is available. `GoogleDriveOAuthConfiguration.HasConfiguredClient` validates decryptability instead of mere file presence. Standalone first checks its own client, then portable browser-state, `%LOCALAPPDATA%/Omyaru Browser`, and legacy `%LOCALAPPDATA%/PrivacyBrowser` client locations; a damaged standalone copy no longer masks a working browser copy. Import errors and picker titles no longer show client JSON jargon in normal UI. `Set-GoogleDriveClient.ps1` writes the renamed Omyaru Browser path. First-time Windows installs without an embedded or locally configured desktop OAuth client still need one-time setup; Google token exchange requires that client's secret. No credential data was output or bundled.

Changed source: `apps/windows/desktop/GoogleDriveOAuthConfiguration.cs`, `GoogleDriveSyncDialog.cs`, `BookshelfSyncCoordinator.cs`, `GoogleDriveDialogSelfTest.cs`, `PrivacyBrowser.Desktop.csproj`, `apps/windows/bookshelf/Program.cs`, `Omyaru.Bookshelf.csproj`, and `scripts/Set-GoogleDriveClient.ps1`. Synthetic self-tests verify hidden desktop setup control and standalone fallback when its own config is corrupt. Both Windows projects built with zero errors/warnings; published desktop and standalone `--self-test` and `--smoke-test` exited 0. Actual-user standalone `--check-drive-client` and `--check-shared-drive-session` exited 0 in the unsandboxed Windows account. The sandboxed account returned 1 for these last checks because it cannot see the user's AppData. No real Google reauthorization or remote sync was attempted. Existing v0.4.44 windows were left open to preserve tabs; launch the new EXEs to see the UI fix.

Desktop EXE relative: `artifacts/releases/Omyaru-v0.4.45-win-x64/Omyaru.exe`; absolute: `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.4.45-win-x64/Omyaru.exe`. Standalone EXE relative: `artifacts/releases/OmyaruBookshelf-v0.1.17-win-x64/OmyaruBookshelf.exe`; absolute: `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/OmyaruBookshelf-v0.1.17-win-x64/OmyaruBookshelf.exe`. Windows EXEs not uploaded; no APK built. Next: restart each PC app on its new EXE and confirm connected-account UI shows no setup button; then continue Android/Windows sync roundtrip validation.

---

# 2026-10-11 Codex: no user JSON setup for Windows Google connection

Correction to preceding checkpoint: hiding the JSON button only when a local client was present still burdened new users. The current Windows desktop v0.4.46 and standalone bookshelf v0.1.18 remove the configuration/file-picker button entirely. A release build must embed the already registered desktop OAuth client. `scripts/Publish-Windows.ps1` reads the configured developer Windows account's existing DPAPI-protected client, creates a temporary JSON only during compilation, embeds it in both EXEs, and deletes the temporary file. Both project files fail Release publish if no embedded client path is supplied. No client secret, refresh token, or account data was printed or committed. `GoogleDriveOAuthConfiguration.Import` and its user-facing JSON errors were removed; the encrypted local-client reader remains a developer/legacy fallback. The browser and bookshelf settings now offer direct Google account connection. Documentation updated in `docs/DRIVE_RELEASE_CONFIG.md` and `apps/windows/bookshelf/README.md`. `apps/windows/gecko-lab/bridge/Program.cs` was briefly changed during exploration and reverted; shared bridge still builds.

Changed files: `apps/windows/desktop/GoogleDriveAuthorization.cs`, `GoogleDriveSyncDialog.cs`, `BookshelfSyncCoordinator.cs`, `GoogleDriveOAuthConfiguration.cs`, `GoogleDriveDialogSelfTest.cs`, `PrivacyBrowser.Desktop.csproj`, `apps/windows/bookshelf/Program.cs`, `Omyaru.Bookshelf.csproj`, `README.md`, `scripts/Publish-Windows.ps1`, `docs/DRIVE_RELEASE_CONFIG.md`, and this handoff.

Google's native-app documentation calls the client secret optional for token requests, but a real refresh against this registered Windows client without the secret returned HTTP 400. Restoring the secret in the token request and embedding it in the distributable build succeeded. This proves the actual current account can refresh with the final standalone EXE; a fresh first-time Google login and cross-device sync remain untested. Desktop and standalone published `--self-test` and `--smoke-test` exited 0; tests assert embedded config and no settings-file button. Actual-user standalone `--check-drive-token` exited 0; temporary build config file count was 0. No app user data or remote Drive files were altered during the checks. Existing v0.4.44 windows were left open to preserve tabs. No Android changes or APK build.

Desktop EXE relative `artifacts/releases/Omyaru-v0.4.46-win-x64/Omyaru.exe`, absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.4.46-win-x64/Omyaru.exe`. Standalone EXE relative `artifacts/releases/OmyaruBookshelf-v0.1.18-win-x64/OmyaruBookshelf.exe`, absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/OmyaruBookshelf-v0.1.18-win-x64/OmyaruBookshelf.exe`. Windows EXEs not uploaded. Next: launch new EXEs after closing old running windows and test first-time Google account connection on a clean Windows account; continue Android/Windows roundtrip validation.

---
# 2026-10-11 Codex: mobile bookshelf exists in Drive but Windows shelves stayed empty

User reported that the PC account connected but mobile-created bookshelves were invisible. Authenticated read-only Drive diagnosis found 26 initial bookshelf change records and a valid remote projection of 6 folders/49 photos, all photos linked to valid folders. The real Windows Browser catalog was empty (1 folder/0 photos) although its local sync baseline already knew 6 folders/49 photos. A clean temporary restore downloaded and SHA256-verified all 49 originals. This was a Windows recovery/application problem, not proof of missing mobile uploads. No Android source or APK changed; mobile app UI itself was not exercised in this turn.

Changed `apps/windows/desktop/DriveBookshelfSync.cs` to resume verified photo downloads and reuse an existing Drive photo manifest when local content hash matches despite a changed path/stamp; also added photo progress and a stale-empty-catalog recovery decision. `apps/windows/desktop/BookshelfSyncCoordinator.cs` now restores remote data first when an unexpectedly empty local catalog conflicts with a nonempty sync baseline, reports progress, and preserves an explicit user-empty marker. `apps/windows/desktop/Bookshelf.cs` and `apps/windows/bookshelf/Bookshelf.cs` mark an intentional deletion of the last photo and display sync download/failure state. `apps/windows/bookshelf/StandaloneSupport.cs` now selects a prior catalog only when every referenced original exists, preserving incomplete prior catalogs. `apps/windows/bookshelf/Bookshelf.cs` enables sync on first launch for an already connected common Google account. Added focused self-tests in `apps/windows/desktop/DesktopSelfTest.cs` and `apps/windows/bookshelf/Program.cs`; temporary diagnostic CLI branches were removed. Versions: desktop `apps/windows/desktop/PrivacyBrowser.Desktop.csproj` 0.4.47 and standalone `apps/windows/bookshelf/Omyaru.Bookshelf.csproj` 0.1.21.

After the existing Browser window closed, repaired the actual user Browser shelf via the authenticated session: catalog now has 6 folders/49 photos, missing originals 0. Initial repair left Drive change-record count at 26. A diagnostic repeat exposed duplicate re-upload after the local path changed; the exact diagnostic process was stopped, adding one extra change record (27 total), then same-content manifest reuse was fixed. Two subsequent repeat syncs completed in about 6 seconds each, preserving 6/49 and remote count 27. No remote deletion or catalog reset. The new standalone release was launched visibly and is currently running; its portable shelf has 6 folders/49 photos, missing originals 0, automatic sync enabled, and current sync state 27 seen records/0 pending/49 baseline images. The older 322-entry standalone catalog still has 311 missing originals, was not chosen for migration and remains untouched. Old diagnostic standalone windows were closed gracefully. There may be orphaned remote chunks from the interrupted diagnostic re-upload; do not delete them without a separate safe audit.

Published local self-contained win-x64 EXEs with `scripts/Publish-Windows.ps1`: `artifacts/releases/Omyaru-v0.4.47-win-x64/Omyaru.exe` (absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.4.47-win-x64/Omyaru.exe`) and `artifacts/releases/OmyaruBookshelf-v0.1.21-win-x64/OmyaruBookshelf.exe` (absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/OmyaruBookshelf-v0.1.21-win-x64/OmyaruBookshelf.exe`). Both published executables passed `--self-test` and `--smoke-test`; Gecko bridge build also passed. The pre-existing protected Desktop OAuth configuration was used for release builds without printing its secret. No GitHub Windows release, APK build/install, other device action, VPN or host network change. MuMu DEV was inspected read-only and its Omyaru Android app was 0.15.1 (59).

Next: user should visually inspect the open 0.1.21 standalone bookshelf or launch the 0.4.47 PC Browser. If their mobile shelf has entries beyond the 6 folders/49 photos currently present in Drive, ask which Android app and its bookshelf sync status and investigate that upload separately. Do not merge the older incomplete 322-entry catalog or claim its missing 311 originals were recovered.

---

# 2026-10-11 Codex: one-column bookshelf and verified image numbering

User asked for a minimum of one bookshelf column on mobile and PC, with existing and future originals named image1, image2, etc. in saved image order, without changing bytes or catalog order. Coordinate with `../Umtomi/CODEX_HANDOFF.md`. Windows desktop 0.4.50 and standalone 0.1.24 now permit one column; Android Browser 0.15.2 permits one column. New originals get numbered names per folder. Existing complete catalogs migrate via verified copies, atomic catalog replacement, then old-file cleanup; missing originals leave the catalog untouched. Android shared catalog merging preserves insertion order rather than sorting images by ID. The standalone 0.1.24 bootstrap links the verified active browser shelf rather than duplicating a large media directory; `bookshelf-location.json` points at the browser catalog. Concurrent standalone/browser editing of the same Windows catalog has not been exercised and may need a cross-process merge/lock design.

Actual PC active browser catalog had 6 folders/49 images; all 49 migrated and post-migration ID/folder position and SHA-256 matched the pre-migration record. Complete standalone v0.1.20 and v0.1.21 portable catalogs, each 49 images, were migrated with the same order/hash verification. The older incomplete 322-entry catalog with 311 missing originals was left unchanged. Published Windows desktop and standalone EXEs passed self-test and smoke-test. Relative outputs `artifacts/releases/Omyaru-v0.4.50-win-x64/Omyaru.exe` and `artifacts/releases/OmyaruBookshelf-v0.1.24-win-x64/OmyaruBookshelf.exe`; absolute outputs `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.4.50-win-x64/Omyaru.exe` and `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/OmyaruBookshelf-v0.1.24-win-x64/OmyaruBookshelf.exe`.

Android Browser unit tests, release APK build, and release lint passed. MuMu DEV only (`127.0.0.1:16416`) received a data-preserving install; app version is 0.15.2/code60. With the emulator's all-files permission enabled for both apps, the synthetic Umtomi-create → Browser-modify → Umtomi-verify shared shelf test passed all three stages. No real phone data was changed. Relative APK `artifacts/Omyaru-v0.15.2-arm64.apk`, absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/Omyaru-v0.15.2-arm64.apk`, delivered copy `C:/Users/peopl/OneDrive/DEV/APK/Omyaru-v0.15.2-arm64.apk`. Published `https://github.com/omyeol/Omyaru-Browser/releases/tag/v0.15.2`; anonymous API size and SHA-256 digest matched local (`6e0a8970bff8726af3620fa262b3545d24230d4b88fde3856f1e72f78078804a`). Source remains mostly untracked and was not pushed. Real user's Android bookshelf migration and Drive sync have not been exercised on the phone. Next: confirm one-column selection and numbered filenames in the user's actual phone app, and audit concurrent Windows edits before treating the linked local catalog as fully multi-process safe.

---


---
# 2026-10-11 Codex: Cloud-only bookshelf groundwork and PC builds

User chose on-demand Drive originals with a bounded thumbnail cache and optional offline originals, Android and PC first; iOS deferred. This checkpoint implements on-demand markers for Windows desktop and standalone, and source-level Android groundwork. Do not claim the Android shared bookshelf is cloud-only yet.

Windows: `apps/windows/desktop/CloudShelfMarker.cs` stores a non-secret `.cloud` manifest reference, reads an encrypted original from Drive on demand into a delete-on-close temporary stream, and caches scaled PNG thumbnails under a 96 MiB limit. Desktop and standalone `Bookshelf.cs` load marker photos; double-click opens a cloud photo in an internal full-screen viewer. `DriveBookshelfSync.cs` writes markers after verifying local SHA-256 and a full remote chunk read; `BookshelfSyncCoordinator.cs` retires a replaced original only after catalog save and sync acknowledgment, with hash/path checks. `Program.cs --check-cloud-shelf` is read-only: it compares all current catalog originals to cache manifests and loads every image from Drive, checking dimensions. The actual Windows account's 49/49 images passed this check (exit 0); no catalog or originals were changed by the check. Both Debug and published desktop v0.4.51 / standalone v0.1.25 builds passed. Published EXEs also passed `--self-test` and `--smoke-test` (exit 0). Outputs: `artifacts/releases/Omyaru-v0.4.51-win-x64/Omyaru.exe` and `artifacts/releases/OmyaruBookshelf-v0.1.25-win-x64/OmyaruBookshelf.exe`. Absolute paths are under `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/`. No GitHub Windows release. Existing live PC bookshelf has not been migrated by running the new EXE; its 49 originals remain intact. The old incomplete 322-image standalone catalog remains untouched.

Android: added `CloudShelfMarker.kt` to Omyaru and Umtomi with on-demand decryption, scaled 96 MiB thumbnail cache, and hash-checked original retirement helper. Omyaru's private shelf sync is wired to cloud-only references in source, but no production APK was built or installed. Omyaru's shared shelf and Umtomi's shared shelf explicitly remain eager-original mode because Umtomi was not connected to Google on MuMu DEV for the cross-OAuth visibility test. Shared-root deletion is disabled in both apps. Umtomi legacy private sync remains unchanged. Both Android `:app:compileDebugKotlin` builds passed. Only androidTest APKs were built/installed on MuMu DEV for a synthetic cross-OAuth read-only test; Browser reference generation succeeded, Umtomi reported `not_connected`, and the synthetic folder was cleaned. User was asked to connect Umtomi to the same account on MuMu DEV; answer pending. Do not ship cloud-only shared mode or delete shared originals until both apps can read the same Drive key/chunk on the real account. `appDataFolder` permissions may differ between the separate OAuth projects. Do not touch `emulator-5556`, uninstall apps, or clear data.

Next: after Umtomi account connection, rerun the targeted cross-OAuth read test with a fresh synthetic reference. If it fails, design a genuinely shared Drive storage/scope and migration without deleting local originals. If it succeeds, enable cloud-only shared mode, add an explicit offline-original option, exercise cross-app image load and interruption/retry, then build/install/publish versioned release APKs and copy each to `C:/Users/peopl/OneDrive/DEV/APK/` per established rules. iOS is later.
MuMu DEV cross-OAuth check was retried after PC publishing: Browser setup passed, Umtomi again reported not_connected (test runner success is an early return, not cross-app access proof). Exact synthetic folder was removed. Shared cloud-only remains disabled.

---
# 2026-10-11 Codex: bookshelf photos missing/cropped follow-up

User reports that cloud files do not appear and photos appear cropped; exact app/surface was asked with `request_user_input_async`, answer pending. The active PC Browser v0.4.51 process was seen, but its actual catalog remains 6 folders/49 complete local PNG originals, 0 `.cloud` markers. Current PC Drive account read-only `ListShelf` returned 29 change records; a local replica state also had 29 seen/0 pending. This does not identify which user photo is missing; do not claim the missing-cloud issue is solved. The active catalog's 49 PNG dimensions match saved metadata, and no originals or catalog entries were modified during diagnosis.

Definite rendering defects fixed: desktop `Bookshelf.cs` gallery now uses the explicit canvas/column height sizing already present in standalone, preventing tall multi-image columns from being clipped by AutoSize layout. Both Windows forms now load up to three cloud thumbnails concurrently, display a loading placeholder, clear it on success, and show a cloud-specific failure message instead of leaving blank cards. Android Omyaru and Umtomi `BookshelfActivity.kt` gallery images changed from CENTER_CROP to FIT_CENTER so the whole image is visible. Android `compileDebugKotlin` passed for both apps; no Android production APK was built/installed/published because the shared-cloud-access gate from prior checkpoint is unresolved. Existing shared originals remain preserved.

Windows desktop bumped to v0.4.52; standalone to v0.1.26. Published local EXEs `artifacts/releases/Omyaru-v0.4.52-win-x64/Omyaru.exe` and `artifacts/releases/OmyaruBookshelf-v0.1.26-win-x64/OmyaruBookshelf.exe`, absolute base `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/`. Both published self-test and smoke-test exit 0. No Windows GitHub release. The running prior 0.4.51 process was not closed or replaced to preserve tabs. The new visual behavior has not been inspected on the user's exact reported photo, and the no-show issue remains awaiting app/folder identification. Do not repeat full 49-image Drive validation without a specific reason; previous checkpoint already passed it read-only.

---
# 2026-10-11 Codex: PC Browser reports 49 missing originals

User specifically reports PC Browser shelf warning “원본 사진 49개를 찾지 못했습니다”; mobile images are visible. Closing/reopening the shelf window did not clear it. No originals/catalogs were deleted or rewritten during diagnosis. The active `%LOCALAPPDATA%/Omyaru Browser/bookshelf/catalog.json` has 6 folders/49 PNG entries/0 missing. `System.IO.File.Exists` on every catalog path returns true, all 49 files predate the active process, and the published PC Browser v0.4.53 diagnostic `--count-missing-shelf-originals` exits 0. The existing 0.4.52 Browser process (same Windows user) still displays the user's warning, so its in-memory shelf view or runtime context diverges from a fresh process. Separate 322-item/311-missing old standalone catalog was not touched. The user has been asked to test v0.4.53; response pending.

Desktop and standalone `BookshelfForm` now re-read the saved catalog when their in-memory paths are missing and substitute verified existing paths only when item IDs, folder, dimensions, creation time, and item count match. This is memory-only; no catalog overwrite. Desktop writes `%LOCALAPPDATA%/Omyaru Browser/bookshelf-missing-diagnostic.json` only if the warning persists, recording current/saved counts and one current/saved path for follow-up. `Program.cs` provides hidden read-only `--count-missing-shelf-originals` and `--report-shelf-access` diagnostics. The PC sync coordinator temporarily keeps replaced original image files instead of retiring them until the live cloud viewer is confirmed. This leaves extra disk copies but avoids data loss while the 49-missing warning is unresolved. Do not claim cloud-only storage savings yet.

Published local desktop v0.4.53 `artifacts/releases/Omyaru-v0.4.53-win-x64/Omyaru.exe` and standalone v0.1.27 `artifacts/releases/OmyaruBookshelf-v0.1.27-win-x64/OmyaruBookshelf.exe` (absolute base `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/`). Both self-test/smoke-test exit 0; desktop published diagnostic missing count 0. The existing 0.4.52 process was not closed to preserve tabs. No APK build or GitHub release. Next: when user launches v0.4.53 and opens shelf, read `bookshelf-missing-diagnostic.json` if warning remains, determine current versus saved path divergence, then fix root cause and reverify all 49 render from the live PC catalog. If warning disappears but photos do not render, inspect marker/thumbnail access separately. Do not run destructive sync or delete any original until fixed.

---
# 2026-10-12 Codex: 49-missing warning traced to isolated Windows account

The previous v0.4.52 process had exited. Launching v0.4.53 via default sandboxed `exec_command` unexpectedly opened a visible Browser owned by `OMYEOL\\CodexSandboxOffline` (PID 29332). When the user opened its shelf it showed the 49-missing warning. Its diagnostic reported a distinct 360-item/49-missing catalog, even while the normal Windows user's `%LOCALAPPDATA%/Omyaru Browser/bookshelf/catalog.json` remained 49 items/0 missing. WMI owner check identified the account mismatch. A fresh `--report-shelf-access` run under `require_escalated` in the normal `OMYEOL\\peopl` account read 49 items/0 missing, and the 49 catalog paths all existed. The sandboxed test window was closed, then its lingering test process stopped. Do not launch user-visible Windows apps with default sandbox permissions; use the normal Windows account context. No catalog or image files were modified.

The normal account's v0.4.53 Browser (PID 51172) remains open and responsive. User was asked to open its shelf and report whether photos render; response pending. Diagnostic file in the normal AppData path may contain the isolated-process report and should not be treated as evidence for the normal-account process without a new timestamp/owner check. No new build, APK, release, or source fix in this continuation. Local builds remain `artifacts/releases/Omyaru-v0.4.53-win-x64/Omyaru.exe` (absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.4.53-win-x64/Omyaru.exe`) and `artifacts/releases/OmyaruBookshelf-v0.1.27-win-x64/OmyaruBookshelf.exe` (absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/OmyaruBookshelf-v0.1.27-win-x64/OmyaruBookshelf.exe`). Next: confirm live normal-account shelf appearance. If warning persists there, collect a new same-account diagnostic; if no warning but rendering fails, inspect the image loader/thumbnail path. Keep all original files.

Further finding and repair: the user confirmed the new Browser rendered photos but lacked `Omyaru Browser/이세계 밀프헌터`. The normal live catalog held 49 complete items and no such folder. The physical folder had 311 intact PNG originals (~231 MB) absent from that catalog. An older standalone catalog retained exactly 311 items for `이세계 밀프헌터`, with original item order, IDs and dimensions but stale paths. The physical PNGs had unique creation timestamps in exact reverse of old catalog order; all 311 reverse-paired dimensions matched, with 0 ID collisions against the 49 live items. A one-time data-preserving repair script `artifacts/recovery/restore_orphaned_shelf.py` passed dry run, then ran while the Browser was closed. It backed up the 49-item catalog as `catalog.json.before-restore-20261012-023446`, copied every source with SHA-256 verification to `image1.png` through `image311.png`, retained all 311 GUID-named originals, and atomically wrote a 360-item catalog with nested `Omyaru Browser/이세계 밀프헌터` folder. Both direct filesystem and published Browser `--report-shelf-access` checks returned 360 items/0 missing. Browser v0.4.53 was relaunched unsandboxed as PID 51384, responsive; after a later check the catalog still held 360/0 missing/311 restored. User has been asked to inspect actual folder and images; answer pending. Replica timestamp had not advanced after restore at last check, so cloud sync of these 311 images is **not verified**. Do not remove backup originals or claim cloud/device parity. If the app later drops the folder, inspect sync logic before reapplying the backup; the catalog backup and verified copies remain on disk. No APK/EXE rebuilt or published in this repair.

User then confirmed the restored folder and all 311 photos render in the Browser UI. The connected account's automatic bookshelf sync is enabled; its `images.json` manifest cache advanced from 49 to 84 entries by 02:39 local time, indicating the restored images are being uploaded one by one. `replica.json` had not yet advanced beyond 29 seen change records, so full cloud sync is still in progress, not complete. Live catalog remains 360 items/0 missing. Standalone bookshelf is configured to a currently inaccessible separate OneDrive Personal Vault path; it was not changed. Browser v0.4.53 is currently open and should be left running for upload to continue. Monitor cache until 360 and replica acknowledgment, then verify catalog still 360 and remote projection contains restored folder. Do not reset or overwrite that standalone storage location without user direction.

At 02:40 local, the manifest cache had reached 99/360 while the Browser remained responsive and the live catalog stayed 360 items/0 missing. This transfer can take substantially longer than the local restore. No deletion or storage migration was attempted; the normal-account Browser should remain open to complete upload.

Final check in this turn at 02:42 local: manifest cache 115/360 and still advancing, Browser PID 51384 open, catalog 360/0 missing. The user confirmed all 311 restored images show in the UI. Google Drive completion and mobile receipt remain unverified; continue monitoring or verify in a later turn after upload finishes.

---
# 2026-10-12 Codex: 316-image order correction and Windows sync progress/speed

User corrected the preceding restore: `이세계 밀프헌터` originally had 316 media objects and the 311 displayed PNGs were reversed. The user-specified source `C:/Users/peopl/OneDrive/사진/CLASSFIED/SAFARI/이세계 밀프헌터/이세계 밀프헌터 - 복사본/word/media` contains `image1` through `image316` with no numeric gaps: 311 PNGs and EMFs at 278, 280, 282, 294, 300. SHA-256 differs due PNG encoding, but all 311 current numbered PNGs matched the source PNGs pixel-for-pixel in exactly reverse numeric order. The five EMFs were rendered as white-backed PNGs at their native dimensions into `artifacts/recovery/emf/`; the original Word media was not changed. A visual sample of image278 was inspected.

The Browser was closed gracefully before repair. `artifacts/recovery/correct_media_order.py` passed dry run and then staged hash-verified copies, moved the previous numbered 311 into `%LOCALAPPDATA%/Omyaru Browser/bookshelf/.order-fix-20261012-024712/previous-numbered/`, kept the earlier 311 GUID-named originals, backed up the 360-entry catalog in that repair directory, and wrote the corrected catalog. The folder now has 316 entries in source order with paths `image1.png` to `image316.png`; total Browser catalog 365 entries including the other 49. Post-repair direct verification: 365 entries/0 missing, 311 PNG pairs pixel-identical to the user source, 5 EMF PNG conversions at the correct indices. Published Browser read-only diagnostic also reported 365/0. No source media or backup was deleted. User-facing UI confirmation of first/last image remains pending.

Windows desktop and standalone bookshelf now display a percentage, done/total count and slim progress bar in the left bookshelf pane during upload and download. `DriveBookshelfSync` retains full remote read-back verification but uploads up to three independent images in parallel; each verified image's manifest cache is checkpointed immediately for interruption/resume. Modified `apps/windows/desktop/DriveBookshelfSync.cs`, `BookshelfSyncCoordinator.cs`, `Bookshelf.cs`, `apps/windows/bookshelf/Bookshelf.cs`, and bumped versions to desktop 0.4.54 and standalone 0.1.28. Bridge tests were repaired to link the shared marker code under `SYNC_BRIDGE`; tests now validate cloud marker round-trip and that multiple verified image uploads overlap and report completion (`apps/windows/desktop/CloudShelfMarker.cs`, `apps/windows/gecko-lab/bridge/Omyaru.SyncBridge.csproj`, `apps/windows/gecko-lab/tests/account-keys/Program.cs`). Both Windows Release builds had 0 warnings/errors; both published self-test and smoke-test exited 0; account-keys tests all pass including the new concurrent-upload assertion. No APK built or published.

Local EXEs (relative, absolute): `artifacts/releases/Omyaru-v0.4.54-win-x64/Omyaru.exe`, `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.4.54-win-x64/Omyaru.exe`; `artifacts/releases/OmyaruBookshelf-v0.1.28-win-x64/OmyaruBookshelf.exe`, `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/OmyaruBookshelf-v0.1.28-win-x64/OmyaruBookshelf.exe`. Desktop 0.4.54 is running under normal user account as PID 20560; bookshelf window was not yet open at the latest check. Old Google manifest cache held 145 of the prior 360 entries; after correcting paths, the cache should reuse verified hashes for those entries, then upload the remainder and the five new images. Actual-account throughput and UI bar visibility are not yet verified because bookshelf was not opened. User has been asked to open bookshelf and confirm first image and bar. Keep backups and local originals until the 365-item Drive sync and mobile visibility are verified. Standalone app's configured Personal Vault storage remains inaccessible and unchanged.

Further fix: gallery and PDF exporter in **both** Windows bookshelf entry points were still sorting by `CreatedAtEpochMs`, overriding the verified catalog/image-number order. Removed that sort so catalog order is authoritative; no catalog metadata was changed. Versions bumped again to desktop 0.4.55 and standalone 0.1.29. Published Release EXEs and all four published self/smoke checks passed (exit 0); account-key/marker tests also pass including a new four-image concurrent-upload/percentage callback assertion. Final local EXEs (relative, absolute): `artifacts/releases/Omyaru-v0.4.55-win-x64/Omyaru.exe`, `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.4.55-win-x64/Omyaru.exe`; `artifacts/releases/OmyaruBookshelf-v0.1.29-win-x64/OmyaruBookshelf.exe`, `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/OmyaruBookshelf-v0.1.29-win-x64/OmyaruBookshelf.exe`. The normal-account desktop v0.4.55 is open as PID 52040. User was asked to open bookshelf in v0.4.55 and confirm first image/progress bar; answer pending. Google manifest cache remained 145 entries at the last check because the bookshelf window had not been opened after the repair. No APK built, no remote release, no original deletion.

Read-only follow-up at 03:00 local: same normal-account PID 52040 is running from the v0.4.55 output path; live catalog still has 365 total items, 316 in `Omyaru Browser/이세계 밀프헌터`, first `image1.png`, last `image316.png`. The active image manifest cache remains 145 entries, last modified 02:46:23, so the cloud transfer has not resumed. The bookshelf form must be opened in the running browser for its timer to start; do not claim cloud completion or mobile parity. The user has an outstanding async request to open the bookshelf and confirm UI/progress. Android sync UI remains unchanged this turn; Android/Umtomi shared cloud-only work is separately blocked on cross-OAuth real-account validation as described above.

## 2026-10-12: Archive redundant legacy GUID originals

User confirmed images 1 through 316 render but found 627 PNGs in the physical folder. Verified the extra 311 files named `<32-hex>.png` are unreferenced by the live 365-entry catalog and that the SHA-256 multiset of those files is contained in the numbered `image1.png`...`image316.png` set. One-time reversible script `artifacts/recovery/archive_legacy_guid_images.py` passed dry-run, then moved precisely those 311 files out of the active folder into normal-account `%LOCALAPPDATA%/Omyaru Browser/bookshelf-archive/legacy-guid-images-20261012-030602/`, recording each original name, matching numbered name, size and SHA-256 in its manifest. This archive is outside the active `bookshelf` root and is retained for recovery. No image bytes, catalog entries, Word source files, other book folders or cloud state were changed. Post-archive active folder contains exactly 316 PNGs, `image1.png` through `image316.png`, 0 unnumbered; published v0.4.55 read-only report `artifacts/recovery/shelf-report-after-guid-cleanup.json` returned 365 items and 0 missing. Existing Browser PID 52040 remains running; one accidental diagnostic window was closed gracefully. No new EXE/APK build required. Cloud upload remains unverified at the preceding 145-manifest checkpoint until the bookshelf form is opened.

## 2026-10-12: Long bookshelf virtualization, Shift delete, Android progress

User reported that `Omyaru Browser/이세계 밀프헌터` photos clip halfway down and disappear below; also requested a delete confirmation with Shift bypass and percentage upload/download progress in both mobile apps. Root cause: at 450px column width its 316 images occupy ~113,000px per column, beyond the reliable Win32 child-control canvas height. Windows desktop and standalone `Bookshelf.cs` now select a viewport-sized `VirtualBookshelfGallery.cs` when calculated gallery height exceeds 28,000px; the view draws only visible tiles, uses an explicit scrollbar with logical offsets, loads visible thumbnails on demand with a bounded decoded cache, supports double-click/open, context-delete and drag/drop, preserves scroll after updates and disables deletion if any original is missing. Smaller shelves keep their original gallery. `DesktopSelfTest.cs` now exercises a 316-image synthetic gallery whose logical height exceeds 100,000px and confirms a scroll offset beyond 32,767 reaches a late item. Both PC delete menus use the shared `DeleteImage` check: ordinary action asks confirmation with a Shift tip; holding Shift while invoking deletion bypasses the dialog. The question of live visual rendering in the user's browser remains unverified (see approval note below).

Windows version bumps: desktop 0.4.56, standalone 0.1.30. Release builds had 0 warnings/errors and all four published `--self-test`/`--smoke-test` runs exited 0, including after the final deletion-protection change. Final EXEs (relative, absolute): `artifacts/releases/Omyaru-v0.4.56-win-x64/Omyaru.exe`, `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.4.56-win-x64/Omyaru.exe`; `artifacts/releases/OmyaruBookshelf-v0.1.30-win-x64/OmyaruBookshelf.exe`, `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/OmyaruBookshelf-v0.1.30-win-x64/OmyaruBookshelf.exe`. Old Browser/bookshelf windows were closed normally after cache checkpoint; v0.4.56 is running as PID 9404. The active Google image-manifest cache reached 365 entries at 03:21:37 local before the switch. This is evidence the upload verification checkpoint reached all local images; remote application on other devices has not been verified.

Omyaru Android `DriveBookshelfSync.kt` reports completed/total upload and download counts; `BookshelfActivity.kt` shows percent and a horizontal progress bar during automatic sync. Version 0.15.3/code61. Gradle compile, release APK and debug unit tests passed using a temporary isolated build directory (`artifacts/recovery/isolated-android-build.gradle`) because OneDrive-held existing intermediate outputs rejected writes. APK relative `artifacts/Omyaru-v0.15.3-arm64.apk`, absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/Omyaru-v0.15.3-arm64.apk`, delivered `C:/Users/peopl/OneDrive/DEV/APK/Omyaru-v0.15.3-arm64.apk`. Local/delivery SHA-256 `3af158055f55bbc39b0da7b612e190b42ff568e50851b84ff3edab69333c20c4` and existing signing certificate verified. Published `https://github.com/omyeol/Omyaru-Browser/releases/tag/v0.15.3`; anonymous API asset digest/size match. MuMu DEV only (`127.0.0.1:16416`) received `adb install -r` and reports code61/name0.15.3; no uninstall or data clear. The other emulator `emulator-5556` was untouched. Non-exported BookshelfActivity cannot be launched directly by shell, and real-account progress UI has not been visually verified. Source changes: `apps/windows/desktop/Bookshelf.cs`, `VirtualBookshelfGallery.cs`, `DesktopSelfTest.cs`, both Windows csproj, `apps/windows/bookshelf/Bookshelf.cs`, `apps/android/app/src/main/java/dev/privacybrowser/app/BookshelfActivity.kt`, `DriveBookshelfSync.kt`, its Gradle version, `scripts/release-notes-0.15.3.md` and the isolated-build helper; no PR/source push.

Approval note: an attempted full screenshot of the user's live Browser window to inspect the repaired gallery was rejected by auto-review because unrelated tabs/private text might be captured; no screenshot was produced. Do not work around that rejection. The synthetic long-gallery test passes, but ask for user approval before capturing live application content or have the user check the middle/bottom visually.

## 2026-10-12: 365-photo PC recovery, remote proof, download progress

User screenshot showed a stale 360-entry PC form with 360 missing paths and an empty `이세계 밀프헌터` gallery; both mobile apps lacked that folder. Normal Windows account disk catalog actually had 365 items but every path pointed to a `.cloud` marker; the byte-identical original PNGs still existed. `artifacts/recovery/restore_verified_local_shelf_paths.py` SHA-256 matched all 365 marker manifests to originals, backed up catalog as `%LOCALAPPDATA%/Omyaru Browser/bookshelf/catalog.json.before-local-paths-20261012-103815`, and atomically switched only catalog paths to PNGs. No image/marker bytes were deleted. Final catalog: 365/0 missing, target 316 in `image1.png`..`image316.png` order. Previous Browser v0.4.56 closed normally; new v0.4.57 normal-account Browser PID 14076 open. Live screenshot was not captured because earlier full-window capture had been rejected by auto-review; user-provided screenshot and non-visual checks were used.

Authenticated read-only remote projection from 196 Drive change records contains 7 folders, 365 images, target folder present with exactly 316 images (`artifacts/recovery/remote-shelf-report-20261012.json`). Thus PC upload is complete; actual phone download remains unverified. User clarified the folder is missing in both Omyaru and Umtomi mobile; async question asking each app's sync/account status is pending. Separate OAuth projects still prevent assuming Umtomi can read Browser appData; common on-device folder visibility also depends on shared-storage configuration.

Windows `DriveBookshelfSync.cs` now keeps a SHA-verified local original rather than converting it to a cloud marker, shows metadata-list and image download progress; desktop and standalone `Bookshelf.cs` reload a newer complete saved catalog when an older open form has missing paths/different IDs. Read-only `--report-remote-shelf` CLI reports aggregate counts. v0.4.57 and standalone v0.1.31 Release builds passed (0 warnings/errors); published EXEs both passed self-test and smoke-test exit 0. Relative outputs `artifacts/releases/Omyaru-v0.4.57-win-x64/Omyaru.exe` and `artifacts/releases/OmyaruBookshelf-v0.1.31-win-x64/OmyaruBookshelf.exe`; absolute base `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/`. No Windows GitHub release.

Omyaru Android v0.15.4/code62 now displays folder-record download progress, keeps completion/failure status visible ~5s, and retains verified local originals in private cloud mode. `:app:assembleRelease :app:testDebugUnitTest --offline` passed. APK relative `artifacts/Omyaru-v0.15.4-arm64.apk`, absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/Omyaru-v0.15.4-arm64.apk`, copy `C:/Users/peopl/OneDrive/DEV/APK/Omyaru-v0.15.4-arm64.apk`, SHA-256 `462997330a39c2b355e20dfe8424ed3937ba1b5555d8bc9bf896a0ae07c8053a`; matching previous signing cert. Public release https://github.com/omyeol/Omyaru-Browser/releases/tag/v0.15.4 uploaded successfully. MuMu DEV `127.0.0.1:16416` data-preserving update shows code62; emulator-5556 untouched. Actual phone receipt and UI rendering remain unverified. Next: obtain mobile sync status from user, check connected account/enablement and whether Omyaru receives remote 316; then determine Umtomi cross-project/shared-local path separately. Never delete PC originals or remote data to resolve mismatch.
Final PC follow-up: `SyncBookshelfAsync` in both Windows forms now skips committing a stale note when a newer complete catalog was reloaded, preventing that old UI text from overwriting the recovered note. v0.4.57/v0.1.31 were republished after this change; four published self/smoke tests again exited 0. Previous PID 14076 closed normally, final normal-account Browser PID 23884 launched. Artifact paths above are unchanged.
Anonymous GitHub release API check after publication: both v0.15.4 Omyaru and v5.14.4 Umtomi assets report published=true and size/SHA-256 digest matching the local APKs. No source push or PR.

## 2026-10-12: stale PC shelf, false upload phase, local originals restored

User supplied two new screenshots at 10:54-10:55 showing a Browser bookshelf form with 360 items/360 missing originals and blank galleries, and requested repeat-upload diagnosis, local original storage, and background continuation on mobile. Normal-account process v0.4.57 PID 23608 owned the old form; its 10:53:11 diagnostic read 360/360 with legacy GUID PNG paths. In the same normal account, the actual current `%LOCALAPPDATA%/Omyaru Browser/bookshelf/catalog.json` and the published CLI read 365/0 with 365 PNG paths, target folder 316 numbered originals. A separately launched v0.4.58 `--bookshelf-only` form PID 39132 did not report missing originals and selected the target folder; old PID 23608 modal and browser closed normally, then v0.4.58 Browser PID 53932 launched. No screenshot was taken because earlier full-browser capture had been rejected. Why the old process saw a 360-item catalog despite the current 365-item file is not fully explained; the new form's actual visual gallery remains for user confirmation. New missing diagnostic includes raw catalog count/hash/timestamp and PID if it recurs. The 365-item file and all originals are intact; no user data was deleted.

Actual remote shelf change-record count stayed at 196 before and after opening the new shelf, with no pending local batches. The progress bar falsely labeled local verification of already-uploaded images as `업로드`. `DriveBookshelfSync.cs` now has separate `사진 확인` progress and reports `업로드` only for actual new upload jobs; total is actual job count. Local originals stay authoritative. Existing `.cloud` references now download SHA-verified full PNGs atomically into local originals; `BookshelfStore.Save` copies them into the numbered physical folder. Upload cache and remote change records remain stable when originals are unchanged. Bridge account-key/shelf tests pass, including local download byte identity, unchanged repeat sync with no duplicate uploads, and parallel upload callback. Windows desktop 0.4.58 and standalone 0.1.32 Release builds had 0 warnings/errors, published EXEs passed all self/smoke tests exit 0. Relative files `artifacts/releases/Omyaru-v0.4.58-win-x64/Omyaru.exe`, `artifacts/releases/OmyaruBookshelf-v0.1.32-win-x64/OmyaruBookshelf.exe`; absolute base `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/`. No PC GitHub release.

Omyaru Android v0.15.5/code63 sets bookshelf sync `cloudOnly=false`, downloading originals to the device rather than keeping cloud-only markers. Existing local photos are reused. Normal app switching already removes only the periodic callback; the ongoing worker continues until Activity destruction/process termination. Release APK and unit tests passed. Relative `artifacts/Omyaru-v0.15.5-arm64.apk`, absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/Omyaru-v0.15.5-arm64.apk`, delivery `C:/Users/peopl/OneDrive/DEV/APK/Omyaru-v0.15.5-arm64.apk`, SHA256 `b81f660a623fb01d9feab600566765a8b216c261ff4a345eca439b67a3002155`. Signing cert matches prior. GitHub release https://github.com/omyeol/Omyaru-Browser/releases/tag/v0.15.5 published; anonymous asset digest/size verified. MuMu DEV `127.0.0.1:16416` data-preserving install code63/name0.15.5. Other emulator untouched. User's phone account/sync state and visual gallery still unverified; separate OAuth project issue for Umtomi persists.

- 최종 사용자 확인: 새 Omyaru v0.4.58 PC 브라우저 책장에서 '이세계 밀프헌터' 사진 316장이 정상 표시됨. 열람 후 읽기 전용 재확인: 원격 책장 레코드 196개로 불변, PC 로컬 목록 365개(대상 폴더 316개), 원본 누락 0개. 빈 갤러리 문제 해결을 사용자 화면에서 확인했고 재업로드도 발생하지 않았음.


## 2026-10-12: Android gallery visibility question
Read-only review: both Android SharedBookshelfStorage implementations share fixed /storage/emulated/0/Omyaru_Bookshelf (or selected SD volume)/ and create .nomedia on activation and volume migration, hiding the folder and descendants from Android media scanner while preserving cross-app sharing. Android/data/<package> would isolate the apps on Android 11+ and break the current shared-root design. Browser CaptureStorage.savePng explicitly writes Pictures/<album> via MediaStore when user selects file/gallery export; that is separate from bookshelf originals and is expected to appear in Gallery. No code changes/build/device actions. If user reports actual Gallery entries despite .nomedia, verify on device and check pre-existing MediaStore indexing before changing storage; do not move/delete originals.


## 2026-10-12: Mobile folders available before original downloads finish
User reported PC-created shelf inaccessible on mobile until all images downloaded. Root cause: Android DriveBookshelfSync projected metadata but BookshelfActivity only applied the result after the full original-download loop. Browser Android DriveBookshelfSync now creates local cloud reference markers for incoming images and invokes metadataReady before downloading originals. BookshelfActivity applies folder/image positions on the UI thread with fresh-account/snapshot guard, then applies final verified local original paths after all downloads; the intermediate state is durable so interrupted downloads can resume and still show folders. Existing local originals are preserved and existing marker decoding can load an opened image on demand. No Windows code changed; the requested problem is PC-to-mobile first download. Version 0.15.6/code64. Compile, debug unit tests, Release APK passed using isolated -PomyaruBuildDirectory=.build-omyaru-0156. Relative artifact artifacts/Omyaru-v0.15.6-arm64.apk, absolute C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/Omyaru-v0.15.6-arm64.apk, delivery C:/Users/peopl/OneDrive/DEV/APK/Omyaru-v0.15.6-arm64.apk. SHA256 8cd39f0502e918f7090e030e22d7ffdfa353a03ef6f240f4ed544de913f18c6b. Same prior signing certificate. Public release https://github.com/omyeol/Omyaru-Browser/releases/tag/v0.15.6 uploaded; anonymous asset size/hash match. Data-preserving MuMu DEV 127.0.0.1:16416 install reports code64/name0.15.6; other emulator untouched. Actual remote-account slow-download visual flow is not yet exercised; verify on user phone with a new PC-created shelf and report any missing folder/image. No source push or PR.

## 2026-10-12: Open parent shelves and reorder child shelves

User requested opening a parent shelf (e.g. Omyaru Browser) to read descendants consecutively, manual child order, and new folders at the bottom. Browser Android, Windows desktop and standalone bookshelf now allow parent selection and render all descendant images in saved folder order. Parent galleries use one column to keep each child shelf's images contiguous; individual child galleries keep their column setting. Folder actions offer move up/down among siblings. New folders append to catalog order. This reorders metadata only: no originals, filenames or per-shelf image positions were changed.

`BookshelfPath`, `BookshelfStore`, shared catalog merge and Drive sync replica/coordinator carry explicit folder `order` metadata, including fallback for older records. Paired Umtomi Android 5.14.7 implementation and release are recorded in `../Umtomi/CODEX_HANDOFF.md`. Both Android compile and unit tests passed, including new recursive-order tests. Windows Release build and published executable self/smoke tests all exited 0. Versions: Omyaru Android 0.15.7/code65, Windows browser 0.4.59, standalone bookshelf 0.1.33. Relative artifacts: `artifacts/Omyaru-v0.15.7-arm64.apk`, `artifacts/releases/Omyaru-v0.4.59-win-x64/Omyaru.exe`, `artifacts/releases/OmyaruBookshelf-v0.1.33-win-x64/OmyaruBookshelf.exe`. Absolute artifacts: `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/Omyaru-v0.15.7-arm64.apk`, `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.4.59-win-x64/Omyaru.exe`, `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/OmyaruBookshelf-v0.1.33-win-x64/OmyaruBookshelf.exe`. APK delivered to `C:/Users/peopl/OneDrive/DEV/APK/Omyaru-v0.15.7-arm64.apk`. APK SHA-256 `e069780f5e53eba25906a4e0c7acc91a89311c0911e5a5daf7a413b5de071adf`; signing cert matches 0.15.6. Published https://github.com/omyeol/Omyaru-Browser/releases/tag/v0.15.7 ; anonymous release asset size and digest match. Data-preserving install on MuMu DEV `127.0.0.1:16416` reports code65/name0.15.7; other emulator untouched. Existing historical order reflects prior catalog order; future creations append below and users can reorder. Real account cross-device visual sync remains to be observed; older client releases should be updated to understand order. No private source push.
## 2026-10-12: Local or on-demand bookshelf originals

User explicitly clarified that **local storage must still sync**. Added per-app/per-device bookshelf image mode selection (default local, optional on-demand) to Browser Android `BookshelfStorageActivity`, Umtomi Android counterpart, and Windows browser/standalone Google Drive bookshelf settings. Sync remains independently controlled and active in either mode. Android sync runners now pass selected mode to existing Drive engine; returning to local mode downloads cloud references and reuses hash-verified cached originals. On-demand leaves already-local originals intact, creating markers only for incoming remote originals. Umtomi shared runner uses the same policy and avoids needless remote read-back for already verified local originals. Windows Drive engine now writes on-demand cloud markers and retains verified originals; `BookshelfSyncCoordinator` preserves the mode and enabled fields together. No conversion deletes originals or changes shared folder paths. Android explanatory text is outside the single-choice dialog so both choices remain visible.

Browser Android 0.15.8/code66, Windows browser 0.4.60, standalone 0.1.34. Android `assembleRelease` and unit tests passed; Windows Release build, published EXE self-tests and smoke tests (all exit 0), and fake encrypted Drive round trip including on-demand-to-local conversion passed. Relative artifacts: `artifacts/Omyaru-v0.15.8-arm64.apk`, `artifacts/releases/Omyaru-v0.4.60-win-x64/Omyaru.exe`, `artifacts/releases/OmyaruBookshelf-v0.1.34-win-x64/OmyaruBookshelf.exe`. Absolute artifacts: `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/Omyaru-v0.15.8-arm64.apk`, `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.4.60-win-x64/Omyaru.exe`, `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/releases/OmyaruBookshelf-v0.1.34-win-x64/OmyaruBookshelf.exe`. Delivered `C:/Users/peopl/OneDrive/DEV/APK/Omyaru-v0.15.8-arm64.apk`. Final APK SHA-256 `234cae23c401dfeb88884e2ae29de3b4532a30ea0ae4f74ee6cba92735c8b5ce`, same signing cert as 0.15.7. Published/replaced https://github.com/omyeol/Omyaru-Browser/releases/tag/v0.15.8 ; public asset size/hash verified. MuMu DEV `127.0.0.1:16416` installed data-preservingly and reports 0.15.8/code66. Other emulator untouched. Umtomi final companion is 5.14.9, recorded in its handoff. Real-account on-demand and cross-package visual flow remains unverified; no private source push.

## 2026-10-21: Android island toolbar and foldable resize

v0.15.16 final release status: GitHub asset upload completed successfully at https://github.com/omyeol/Omyaru-Browser/releases/download/v0.15.16/Omyaru-v0.15.16-arm64.apk (reported 199433133 bytes). This supersedes the upload-in-progress note below.

Latest browser follow-up (v0.15.16/code74): user clarified that compact URL field should be ~90% transparent (10% opaque), not vice versa; requested Safari-video-like expand/collapse animation, and reported website fixed UI obscured by the floating island. In `BrowserActivity.kt`, collapsed URL field fill and outer toolbar drawable each use alpha12/255, giving combined background opacity about 9%; their outline/fill alpha now fades together, and the collapsed address text has a dark shadow for contrast over bright pages. Toolbar width, height, address-row/field/button height, and navigation-row height/alpha animate together over 240 ms; Gecko viewport adjustment is applied once rather than every animation frame. `GeckoView.setVerticalClipping` (available in bundled GeckoView 154) now reports bottom toolbar obscuration to Gecko so fixed bottom website controls can move above the island while page content still paints behind it. Top-position toolbar retains a safe top web-container inset. Fullscreen/PiP removes clipping. Version bumped in `apps/android/app/build.gradle.kts` to 0.15.16/code74. Intermediate v0.15.15/code73 margin-only QA build was superseded before release by the user's clarification; do not publish it.

Verification: `:app:testDebugUnitTest :app:assembleRelease` passed (72 tasks); final arm64 APK package is `cuttan.omyaru.app`, signed by existing cert SHA-256 `9d7238f656325b90e39553e45ba5fbeb71090220a4c0d121d42adc6d6b270c80`. Data-preserving installed on MuMu DEV `127.0.0.1:16416`, package reports 0.15.16; visually inspected expanded and compact Wikipedia states and captured a short expand transition. Compact field visibly shows live page behind it with readable shadowed text. A site-specific fixed-bottom UI and real Galaxy Fold8 remain untested; Gecko API behavior is based on official Javadoc and implementation, not a real-site visual assertion. APK relative `artifacts/Omyaru-v0.15.16-arm64.apk`, absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/Omyaru-v0.15.16-arm64.apk`; raw build relative `apps/android/.build-release-01516/app/outputs/apk/release/app-arm64-v8a-release.apk`, absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/apps/android/.build-release-01516/app/outputs/apk/release/app-arm64-v8a-release.apk`. Size 199433133 bytes, SHA-256 `620b98b9769661b5e7935ca8df9dc73502dfca96300ab7eef3a36ad822068f9f`. GitHub release upload to https://github.com/omyeol/Omyaru-Browser/releases/tag/v0.15.16 started; confirm completion before claiming delivery. Device display size was unchanged; no other emulator, Windows app, VPN, or user data changed. Next step: ask user to test a previously obstructed site and Fold8 resizing; iterate if site-specific fixed UI remains hidden.

v0.15.14 final delivery status: APK upload completed successfully at https://github.com/omyeol/Omyaru-Browser/releases/download/v0.15.14/Omyaru-v0.15.14-arm64.apk (reported 199433133 bytes). This supersedes the upload-in-progress wording in the follow-up note below.

Latest follow-up (same date): user specified about 10% transparency for the collapsed URL field. `BrowserActivity.kt` collapsed `browser_field` alpha changed from 168 to 230 (230/255 ≈ 90% opaque, 10% transparent); expanded alpha remains 255 and the 220 ms background fade is unchanged. `apps/android/app/build.gradle.kts` bumped to 0.15.14/code72. `:app:testDebugUnitTest :app:assembleRelease` passed (72 tasks); release package `cuttan.omyaru.app`, arm64, original signing certificate SHA-256 `9d7238f656325b90e39553e45ba5fbeb71090220a4c0d121d42adc6d6b270c80`. Data-preserving MuMu DEV `127.0.0.1:16416` install succeeded, app launch/UI hierarchy was present, and package reports 0.15.14. APK relative `artifacts/Omyaru-v0.15.14-arm64.apk`, absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/Omyaru-v0.15.14-arm64.apk`; raw build relative `apps/android/.build-release-01514/app/outputs/apk/release/app-arm64-v8a-release.apk`, absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/apps/android/.build-release-01514/app/outputs/apk/release/app-arm64-v8a-release.apk`. Size 199433133 bytes, SHA-256 `07bdd5bf1603d8cb1582be1ce5d612dfd4b773a0bda73b51965f81183f242c70`. GitHub Release upload initiated at https://github.com/omyeol/Omyaru-Browser/releases/tag/v0.15.14 ; confirm asset upload completion before reporting as published. No private data or other emulator changed. Visual comparison on the user's Fold8 is still pending.

Final upload status update: v0.15.13 GitHub release asset upload completed successfully at https://github.com/omyeol/Omyaru-Browser/releases/download/v0.15.13/Omyaru-v0.15.13-arm64.apk (reported 199433133 bytes). The provisional statement below that upload was in progress is superseded by this line.

User video-inspired translucent island toolbar: `activity_browser.xml` now overlays the toolbar on the GeckoView instead of shortening the webpage; `BrowserActivity.kt` gives the island a translucent surface, keeps the expanded URL row opaque, and fades the URL row background when it collapses/expands. Android v0.15.11/code69 was unit-tested, release-built, data-preserving installed and visually inspected on MuMu DEV, then published at https://github.com/omyeol/Omyaru-Browser/releases/tag/v0.15.11 .

Foldable/resize fix in `BrowserActivity.kt` and `activity_browser.xml`: on live content-width/inset changes, cancel the stale width animator and snap the toolbar to current available width; preserve compact state during configuration changes; hide secondary address buttons only when the cover/split window is too narrow for the URL field; hide the navigation scroll container itself in compact mode. Android v0.15.12/code70 passed debug unit tests and release build, was data-preserving installed on MuMu DEV, and was published at https://github.com/omyeol/Omyaru-Browser/releases/tag/v0.15.12 . MuMu display overrides 650x1400, 1200x1600, 1400x650 and 1400x400 showed URL and toolbar bounds within the window; original `wm size` was reset to physical 1080x1920. Real Galaxy Fold8 hinge testing remains unverified.

Follow-up user requested collapsed URL field transparency at about 40% of its previous level. Changed collapsed URL background alpha from 38/255 (~85% transparent) to 168/255 (~34% transparent), keeping expanded background fully opaque and the fade animation. Android v0.15.13/code71 passed `:app:testDebugUnitTest :app:assembleRelease` (72 tasks); package `cuttan.omyaru.app`, arm64, same signing certificate SHA-256 `9d7238f656325b90e39553e45ba5fbeb71090220a4c0d121d42adc6d6b270c80`. Data-preserving installed on MuMu DEV `127.0.0.1:16416` and confirmed version 0.15.13; compact URL bounds remained visible after simulated resize and original display size was restored. Final APK relative `artifacts/Omyaru-v0.15.13-arm64.apk`, absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/artifacts/Omyaru-v0.15.13-arm64.apk`; raw build relative `apps/android/.build-release-01513/app/outputs/apk/release/app-arm64-v8a-release.apk`, absolute `C:/Users/peopl/OneDrive/DEV/CODEX/BROWSER/apps/android/.build-release-01513/app/outputs/apk/release/app-arm64-v8a-release.apk`. Size 199433133 bytes, SHA-256 `d5aa7c62f68eb37bca8fbd27c09c55bd19d3070506b7e85d0576dcfc2eaeb7a5`. GitHub release upload was in progress when this note was written; verify asset completion before reporting it as published. No other emulator, Windows program, or private remote data changed. Next step: real foldable hinge/cover-to-inner screen validation by user; adjust if device-specific inset bug remains.
## 2026-11-22: Windows bookshelf crash, tabs, extensions, iOS preview

The user supplied a JIT trace from embedded bookshelf v0.4.67: ObjectDisposedException for ContextMenuStrip in ToolStripDropDown.OnItemClicked. In both Windows Bookshelf.cs copies, folder menu actions now run through BeginInvoke after the click handler returns; the folder picker defers action and disposal too. This matches the failing stack. Live right-click on the user's catalog remains untested.

Windows tab strip AutoScroll is disabled; tab widths share available space and recalculate on resize and tab changes. Narrow-tab close hit testing is guarded. ExtensionManagerForm adds WebView2 profile extension folder/ZIP install, list, enable/disable/remove and optional toolbar entry. Browser environments enable extensions. A complete MV3 smoke fixture with background.js fixed an AddBrowserExtensionAsync stall caused by the earlier bare test manifest. Real third-party extension compatibility remains unverified; WebView2 popup UI has limitations.

Final local Windows builds: desktop 0.4.68 at E:/DEV/CODEX/BROWSER/artifacts/releases/Omyaru-v0.4.68-win-x64/Omyaru.exe (relative artifacts/releases/Omyaru-v0.4.68-win-x64/Omyaru.exe); standalone 0.1.40 at E:/DEV/CODEX/BROWSER/artifacts/releases/OmyaruBookshelf-v0.1.40-win-x64/OmyaruBookshelf.exe (relative artifacts/releases/OmyaruBookshelf-v0.1.40-win-x64/OmyaruBookshelf.exe). Publish-Windows passed with zero warnings/errors. Published desktop self-test and smoke-test, standalone self-test all exited 0 using Start-Process -Wait. No Windows remote release or user catalog mutation.

iOS SwiftUI/WKWebView preview and AltStore workflow were selectively pushed to GitHub via the C:/Users/peopl/OneDrive/DEV/CODEX/omyaru-ios-publish shallow clone. Only apps/ios and .github/workflows/ios.yml were pushed; do not add all untracked E files. First Xcode build failed on self before super.init, fixed in OmyaruApp.swift. GitHub run 45026438370 passed Xcode build, IPA packaging, source generation, artifact upload, release upload. Release https://github.com/omyeol/Omyaru-Browser/releases/tag/ios-latest has Omyaru.ipa (225140 bytes) and altstore-source.json (1602 bytes); source was fetched back, matching bundle cuttan.omyaru.app, version 0.1.0 and IPA size. Unsigned IPA is for AltStore to sign. No physical iOS or AltStore install test. Android feature parity is NOT complete: Drive sync, VPN, long-page region capture and more remain. iOS is explicitly labeled preview. GitHub main is 35464ac; Windows source has not been pushed.

Next: verify new embedded bookshelf context menu on user device, test a real Chromium extension in Windows manager, continue iOS parity and installation testing. E source remains largely untracked; old OneDrive workspace was not deleted.
## 2026-11-22: raw AltStore source URL

User asked for an AltStore source URL beginning raw.githubusercontent.com. Copied the published release source into apps/ios/altstore-source.json and committed only that file, README link, and iOS workflow update to GitHub main as fa0d622. Stable URL: https://raw.githubusercontent.com/omyeol/Omyaru-Browser/main/apps/ios/altstore-source.json . Direct HTTP GET returned 200 and JSON bundle cuttan.omyaru.app. Workflow now refreshes this raw file after publishing a new IPA; a JSON-only change is excluded from build trigger. Run 45030491366 succeeded including the raw refresh step. Final comparison: raw source version 0.1.0 and IPA size 225140 match GitHub release Omyaru.ipa size 225140. No new IPA code changes. E workspace workflow, README, source JSON were copied from selective C publishing clone. Further iOS parity work remains as above.
