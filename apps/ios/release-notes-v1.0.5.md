## Omyaru Browser v1.0.5 (iOS)

iOS 웹 뷰포트(상단 고정 헤더 · 하단 사이트 고정 UI 겹침), 앞으로가기 스와이프 제스처, 그리고 내장 WireGuard VPN 연결 기능을 전면 개선한 업데이트입니다.

### 주요 변경 사항

1. **상단 고정 헤더 & 하단 사이트 UI 겹침 해결 (Safari 동일 뷰포트)**
   - **상단 상태바 영역**: 고정/스티키 헤더가 있는 사이트를 스크롤할 때 헤더 위로 본문이 비쳐 보이던 문제를 수정해, Safari와 동일하게 상태바 영역을 고정 헤더 색상으로 깔끔하게 채웁니다 (고정 헤더가 없는 일반 피드에서는 기존 가우시안 블러 유지).
   - **하단 사이트 고정 UI 보호**: `WKWebView`의 `obscuredContentInsets`(iOS 26+) 및 `setMinimumViewportInset`을 적용해 사이트의 하단 고정 툴바·플로팅 버튼이 Omyaru URL 필드 섬 위로 자연스럽게 올라와 가려지지 않도록 수정했습니다.

2. **오른쪽→왼쪽 스와이프 앞으로가기 제스처 지원**
   - 화면 우측 가장자리 전체를 덮고 있던 `FastScrollBarOverlay`의 투명 제스처 영역을 제거하고 스크롤바 썸이 보일 때만 수직 드래그에 반응하도록 정밀화해, 왼쪽→오른쪽 뒤로가기 제스처의 정확한 반대 방향인 **오른쪽→왼쪽 밀기 앞으로가기 제스처**가 네이티브 Safari 애니메이션으로 동작합니다.

3. **내장 WireGuard VPN 직접 연결/해제 지원 (AltStore 무료 서명 완전 호환)**
   - `wireguard-go` + `netstack` 기반 내장 사용자 공간 WireGuard 엔진(`libomyaru_wg.a`)을 탑재해, 별도 `NetworkExtension` 권한 없이도 `VPN · 보안 DNS` 화면에서 저장된 `.conf` 프로필의 **`연결` / `해제`** 버튼으로 즉시 Omyaru 브라우저 트래픽(웹 · DNS)을 암호화 터널로 우회할 수 있습니다.
   - 실시간 연결 상태·송수신 바이트 표시, WireGuard 터널 경유 외부 IP/지연 시간 검사, 수동 SOCKS5/HTTP 프록시 실제 라우팅, 공식 WireGuard 앱용 `.conf` 파일 공유도 함께 지원합니다.
