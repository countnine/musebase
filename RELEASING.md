# 릴리스 & 자동 업데이트 (Velopack + GitHub Releases)

Musebase는 [Velopack](https://velopack.io)으로 자동 업데이트를 처리한다.
앱은 시작 시(및 트레이 메뉴 "업데이트 확인…"으로 수동) GitHub Releases를 확인해
새 버전이 있으면 델타만 내려받아 재시작 시 적용한다.

- 업데이트 소스: `https://github.com/countnine/musebase` (공개 리포)
- 앱 내 구현: `src/Musebase.Windows/Services/UpdateService.cs`, 배선은 `Program.cs`
- **개발/디버그 실행에서는 동작하지 않는다** (Velopack 설치본이 아니면 `IsInstalled=false`).
  실제 업데이트는 아래로 만든 설치본에서만 확인·적용된다.

> **⚠️ 개명(LyricsX→Musebase) 클린 브레이크**: packId가 `LyricsX`→`Musebase`로 바뀌어
> 구 LyricsX 설치본(≤0.9.x)은 자동 업데이트로 넘어오지 못한다(수동 재설치 필요, 릴리스 노트에 명시).
> `%LOCALAPPDATA%\LyricsX`의 설정·캐시는 새 앱 첫 실행 시 `Musebase`로 자동 이전된다.
> 첫 Musebase 릴리스는 이전 델타 없이 full 패키지만 생성된다(`vpk download github` 결과에 구 packId만 있으면 무시).

## 사전 준비 (최초 1회)

```bash
# Velopack CLI 설치
dotnet tool install -g vpk
```

## 릴리스 절차

버전은 `src/Musebase.Windows/Musebase.Windows.csproj`의 `<Version>`과 아래 `VERSION`을 일치시킨다.

```bash
VERSION=0.6.0            # 릴리스할 버전 (SemVer). csproj <Version>과 동일하게
REPO=https://github.com/countnine/musebase

# 1) self-contained 게시 (단일 런타임 포함)
dotnet publish src/Musebase.Windows/Musebase.Windows.csproj \
  -c Release -r win-x64 --self-contained true \
  -o publish

# 2) Velopack 패키지 생성 → ./Releases 에 Setup.exe + 델타 + RELEASES 생성
vpk pack \
  --packId Musebase \
  --packVersion $VERSION \
  --packDir publish \
  --mainExe Musebase.exe \
  --packTitle "Musebase for Windows" \
  --icon src/Musebase.Windows/assets/app.ico

# 3) GitHub Releases 업로드 (릴리스 생성 + 자산 첨부까지 vpk가 수행)
#    토큰: countnine 계정, repo 스코프 (gh auth token 로 확인 가능)
vpk upload github \
  --repoUrl $REPO \
  --publish \
  --releaseName "Musebase $VERSION" \
  --tag $VERSION \
  --token <GITHUB_TOKEN>
```

`vpk upload github`은 태그 `$VERSION`으로 릴리스를 만들고 `RELEASES`, 델타 `.nupkg`,
`Musebase-win-Setup.exe`를 첨부한다. 앱의 `GithubSource(prerelease: false)`가 이 릴리스를 읽는다.

**홈페이지 버전 표기는 자동 갱신된다.** 릴리스를 발행(위 3단계)하면 이 리포의
`.github/workflows/notify-homepage.yml`가 `release: published`로 발동해
홈페이지(countnine/lyricsx-home)의 "Sync latest release version" 워크플로우를
`repository_dispatch`로 즉시 깨워 "Latest release: vX.Y.Z" 문구를 갱신한다.
(추가 안전망: 홈페이지는 매일 06:00 UTC 크론으로도 최신화된다.)

> **전제:** 이 리포에 시크릿 `HOMEPAGE_DISPATCH_TOKEN`(lyricsx-home 쓰기 권한 PAT)이
> 설정돼 있어야 한다. 없으면 워크플로우가 실패하며, 아래 수동 스크립트로 대체할 수 있다.

```powershell
# (수동 대체/폴백) 홈페이지 버전 표기 즉시 갱신 — 위 자동 워크플로우가 실패했거나
# 로컬에서 바로 반영하고 싶을 때. 로컬 gh 인증(countnine)을 사용한다.
.\scripts\notify-homepage.ps1
```

> 홈페이지는 다운로드 버튼을 `releases/latest/download/...` 직링크로 걸어 두므로
> 버전과 무관하게 항상 최신 설치파일을 받는다. 이 단계는 표시 문구 갱신만 담당한다.

## 동작 규칙

- **버전 비교는 SemVer** — 태그가 `0.6.0` 형식이어야 한다. 현재 설치 버전보다 높을 때만 업데이트로 인식.
- **Public 리포 필요** — 앱은 토큰 없이 릴리스를 읽는다(`accessToken: null`). Private이면 사용자에게 토큰이 필요해 배포용으로 부적합.
- **첫 배포** — 사용자는 GitHub Releases의 `Musebase-win-Setup.exe`로 최초 설치. 이후 릴리스부터 자동 업데이트가 적용된다.
- **prerelease** — GitHub의 "pre-release" 표시 릴리스는 무시된다(정식 릴리스만). 프리릴리스를 쓰려면 `UpdateService`의 `prerelease: true`로 변경.

## 체크리스트

- [ ] `csproj <Version>` 갱신 + `PROGRESS.md`/`README` 버전 반영
- [ ] `dotnet test` 통과
- [ ] 위 1~3 단계 실행
- [ ] GitHub Releases에 태그·자산 확인
- [ ] 홈페이지 버전 자동 갱신 확인(`notify-homepage.yml` 실행 → lyricsx-home Actions). 실패 시 `.\scripts\notify-homepage.ps1`로 대체
- [ ] (권장) 이전 버전 설치본에서 "업데이트 확인"으로 실제 업데이트 검증
