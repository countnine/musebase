# PlaybackViewState — 표시 상태 직렬화 계약 (v1)

"지금 화면에 무엇을 보여줄지"의 **언어 중립 계약**. 모든 플랫폼(.NET WPF/Android는 코드로,
Swift/브라우저 JS는 이 문서로)이 같은 형태를 따른다. 단일 진실은 이 문서이며,
.NET 참조 구현은 `src/Musebase.Engine/PlaybackViewState.cs`, 왕복 테스트는
`tests/Musebase.Core.Tests/PlaybackViewStateTests.cs`.

- 전송 형식: JSON (System.Text.Json 기본 — **PascalCase 필드명**, null 필드 포함)
- 발행 주체: 엔진(`LyricsCoordinator.CurrentState`/`StateChanged`) — 상태가 바뀔 때만 발행
- 소비 주체: 로컬 View(WPF/MAUI 바인딩) 또는 원격 디스플레이(WebSocket 수신 후 렌더)

## 필드

| 필드 | 타입 | 의미 |
|---|---|---|
| `IsPlaying` | bool | 재생 중 여부. false면 오버레이(배경 포함)를 숨긴다. |
| `TrackTitle` | string? | 곡 제목 (없으면 null) |
| `TrackArtist` | string? | 아티스트 (없으면 null) |
| `LineContent` | string? | 현재 가사 줄 원문. null이면 표시할 줄 없음. |
| `LineTranslation` | string? | 현재 줄 번역 (없으면 null → 원문만 표시) |
| `Karaoke` | KaraokeMark[]? | 글자 단위 채움 앵커 배열. null이면 줄 단위 표시. |
| `KaraokeDurationSeconds` | double? | 카라오케 전체 채움 길이(초) |
| `LineStartedAt` | DateTimeOffset(ISO-8601)? | **현재 줄이 시작된 절대 시각**. 진행률 보간의 기준 앵커. |
| `LineSpanSeconds` | double | 현재 줄의 표시 지속 시간(초) |
| `CanPrevious` / `CanPlayPause` / `CanNext` | bool | 미디어 컨트롤 버튼 활성화 여부(SMTC 등 소스 능력) |

### KaraokeMark

| 필드 | 타입 | 의미 |
|---|---|---|
| `Index` | int | 이 앵커가 적용되는 문자 인덱스(0-기준, `LineContent` 기준) |
| `Time` | double | 줄 시작 기준 오프셋(초) — 이 시점에 `Index`번째 문자까지 채워진다 |

## 보간 규칙 (원격 디스플레이 핵심)

카라오케 진행률은 **매 프레임 전송하지 않는다**. 수신 측이 로컬 시계로 보간한다:

1. `elapsed = now − LineStartedAt` (초)
2. `Karaoke`에서 `Time ≤ elapsed`인 마지막 마크와 다음 마크 사이를 선형 보간해 채움 위치를 구한다.
3. `elapsed ≥ KaraokeDurationSeconds`면 전부 채움. `Karaoke`가 null이면 줄 전체를 단색 표시.
4. `IsPlaying=false`가 오면 즉시 숨김(보간 중지).

시계 오차: 같은 LAN 기준 수백 ms 이내 오차는 허용 범위. 필요 시 수신 측이
수신 시각과 `LineStartedAt`의 차로 1회 보정한다.

## 예시

`examples/playback-view-state.example.json` — 재생 중 + 카라오케 줄의 실제 페이로드.
빈 상태는 `PlaybackViewState.Empty`(모든 필드 false/null/0).

## 변경 규칙

- 필드 추가는 **하위 호환**(수신 측은 모르는 필드 무시, 새 필드는 nullable/기본값) — v1 유지.
- 필드 의미 변경·삭제는 breaking → 계약 버전을 올리고 ADR로 기록한다.
- 이 문서와 .NET 구현·테스트는 **같은 커밋**에서 갱신한다(코어 에이전트 소유).
