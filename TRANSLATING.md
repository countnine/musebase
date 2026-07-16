# Translating Musebase / Musebase 번역하기

[English](TRANSLATING.en.md) · **한국어**

Musebase의 UI는 로케일별 JSON 카탈로그로 번역됩니다. 별도 번역 플랫폼 없이 **GitHub에서 직접**
기여할 수 있습니다 — 파일을 고쳐 Pull Request를 열거나, 이슈로 수정을 제안하세요.

- 카탈로그 위치: **[`src/Musebase.Windows/i18n/`](https://github.com/countnine/musebase/tree/master/src/Musebase.Windows/i18n)** (예: `ko.json`, `ja.json`, `de.json`)
- 참조(원문) 언어: **영어(`en.json`)** — 다른 모든 언어의 기준
- 형식: 평면 `키→문자열` JSON. 인자/복수형은 **ICU MessageFormat** (`{value}`, `{count, plural, ...}`)

## 방법 1) GitHub에서 바로 편집 → PR (권장)

1. 위 `i18n/` 폴더에서 번역할 언어 파일(예: `ko.json`)을 엽니다. 없으면 `en.json`을 복사해 새로 만드세요.
2. 오른쪽 위 **연필(✏️ Edit) 아이콘**을 클릭합니다. GitHub 계정만 있으면 됩니다.
3. 값을 번역/수정합니다. **키(왼쪽)와 중괄호 자리표시자는 그대로** 두세요.
4. 아래 **"Propose changes"** → **"Create pull request"** 를 누르면 자동으로 포크·PR이 생성됩니다.
5. 유지관리자가 검토·병합하면, 다음 릴리스의 자동 업데이트로 사용자에게 반영됩니다.

## 방법 2) 이슈로 제안 (Git이 익숙하지 않다면)

[**번역 제안 이슈 열기**](https://github.com/countnine/musebase/issues/new?template=translation.yml) →
언어·키·제안 문구만 적어 제출하면 됩니다. 유지관리자가 반영합니다.

## 번역 시 주의

- `{value}`, `{version}`, `{track}` 같은 **중괄호 자리표시자는 그대로**(번역 금지).
- `{count, plural, one {...} other {...}}` 복수형 구문은 구조를 유지하고 안쪽 텍스트만 번역.
- `[mm:ss.xx]`, `[tr]`, `[tt]`, `【 】` 같은 표기는 그대로 둡니다.
- en/ko 외 언어는 **기계번역(DeepL) 초벌**이라 어색할 수 있습니다 — 다듬어 주시면 좋습니다.

## 새 언어 추가

지원 언어 목록은 [`Services/Localization.cs`](https://github.com/countnine/musebase/blob/master/src/Musebase.Windows/Services/Localization.cs)의
`SupportedLanguages`에 있습니다. 새 언어는 그 목록에 `LanguageOption(코드, 자국어표기)` 를 넣고
`src/Musebase.Windows/i18n/<코드>.json` 을 만들면 됩니다(없으면 영어로 폴백).

## 유지관리자 메모

- MT 시드 재생성: `tools/mt-bootstrap.ps1` (DeepL). ICU 자리표시자를 XML 태그로 보호·검증.
- 규모가 커지면 오픈소스 TMS 자가호스팅(**Tolgee**/**Weblate**) 또는 Hosted Weblate(libre, 승인 필요)로
  이관할 수 있습니다. 현 구조(JSON + git)는 그대로 재사용됩니다.
