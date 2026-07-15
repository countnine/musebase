# 홈페이지(countnine/lyricsx-home) 버전 표기 즉시 갱신 트리거
#
# 릴리스를 GitHub에 발행한 직후(`vpk upload github ... --publish` 성공 후) 실행하면,
# 홈페이지 저장소의 "Sync latest release version" 워크플로우를 repository_dispatch로
# 즉시 깨워 "Latest release: vX.Y.Z" 문구를 새 버전으로 갱신한다.
#
# - 로컬 gh 인증(countnine, repo 스코프)을 그대로 사용 → 별도 PAT/시크릿 불필요.
# - 실행하지 않아도 홈페이지는 매일 06:00 UTC 크론으로 최신화되므로, 이 스크립트는
#   "즉시 반영"을 위한 선택 단계다.
#
# 사용법:
#   .\scripts\notify-homepage.ps1

$ErrorActionPreference = "Stop"

# gh 로그인 확인
gh auth status 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "gh CLI에 로그인되어 있지 않습니다. 'gh auth login' 후 다시 실행하세요."
}

gh api --method POST repos/countnine/lyricsx-home/dispatches -f event_type=release-published
if ($LASTEXITCODE -ne 0) {
    throw "repository_dispatch 전송 실패 (토큰에 lyricsx-home 쓰기 권한이 있는지 확인)."
}

Write-Host "홈페이지 버전 동기화를 트리거했습니다 (repository_dispatch: release-published)."
Write-Host "진행 상황: https://github.com/countnine/lyricsx-home/actions"
