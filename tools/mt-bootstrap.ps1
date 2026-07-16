<#
.SYNOPSIS
  en.json을 기준으로 지원 언어들의 UI 카탈로그를 DeepL 기계번역으로 생성한다(P2 시드).

.DESCRIPTION
  - ICU 자리표시자({value}, {count, plural, ...} 등)는 사설영역 마커로 보호 후 복원하고,
    복원 검증에 실패한 문자열은 안전하게 영어 원문으로 폴백한다(깨진 자리표시자 배포 방지).
  - DeepL이 지원하지 않는 대상 언어(vi 등)는 건너뛴다 → 앱에서 영어로 폴백.
  - 결과는 사람이 검토·수정하도록 Weblate에 올리는 '시드'다(최종본 아님).

.PARAMETER Key
  DeepL Auth Key. 생략 시 %LOCALAPPDATA%\Musebase\settings.json의 deeplApiKey 사용.
#>
param([string]$Key)

$ErrorActionPreference = 'Stop'
$i18n = Join-Path $PSScriptRoot '..\src\Musebase.Windows\i18n'
$enPath = Join-Path $i18n 'en.json'

if (-not $Key) {
    $sp = Join-Path $env:LOCALAPPDATA 'Musebase\settings.json'
    $Key = (Get-Content $sp -Raw | ConvertFrom-Json).deeplApiKey
}
if (-not $Key) { throw 'DeepL 키가 없습니다. -Key 로 전달하거나 settings.json에 설정하세요.' }

$base = if ($Key.TrimEnd().EndsWith(':fx')) { 'https://api-free.deepl.com' } else { 'https://api.deepl.com' }
$headers = @{ Authorization = "DeepL-Auth-Key $Key" }

# 우리 코드 → DeepL target_lang
$map = [ordered]@{
    ja = 'JA'; 'zh-Hans' = 'ZH-HANS'; 'zh-Hant' = 'ZH-HANT'; es = 'ES'; 'pt-BR' = 'PT-BR';
    fr = 'FR'; de = 'DE'; ru = 'RU'; it = 'IT'; pl = 'PL'; tr = 'TR'; nl = 'NL';
    uk = 'UK'; cs = 'CS'; id = 'ID'; ar = 'AR'; vi = 'VI'
}
# 구조적 문자열은 번역 제외(영어 폴백)
$skipKeys = @('export.filter')
# ICU 자리표시자(1단계 중첩까지: plural 포함)
$phPattern = '\{[^{}]*(?:\{[^{}]*\}[^{}]*)*\}'

$supported = (Invoke-RestMethod "$base/v2/languages?type=target" -Headers $headers).language
Write-Host "DeepL endpoint: $base"
Write-Host "지원 대상 언어: $($supported -join ', ')"

$enObj = Get-Content $enPath -Raw | ConvertFrom-Json
$keys = $enObj.PSObject.Properties.Name

# 자리표시자를 <ph>...</ph>로 감싸 DeepL이 번역하지 않도록 보호(ignore_tags=ph)
function Protect([string]$s, [ref]$phs) {
    $phs.Value = @([regex]::Matches($s, $phPattern) | ForEach-Object { $_.Value })
    return [regex]::Replace($s, $phPattern, '<ph>$0</ph>')
}
function Restore([string]$s) {
    return ($s -replace '</?ph>', '')
}

foreach ($code in $map.Keys) {
    $dl = $map[$code]
    if ($supported -notcontains $dl) { Write-Host "skip $code ($dl 미지원)"; continue }

    # 번역 대상 텍스트(보호본) 준비
    $texts = @(); $phList = @(); $sendKeys = @()
    foreach ($k in $keys) {
        if ($skipKeys -contains $k) { continue }
        $ph = $null
        $texts += (Protect $enObj.$k ([ref]$ph))
        $phList += , $ph
        $sendKeys += $k
    }

    # DeepL 배치 요청(text 다중, XML 태그 보호)
    $pairs = @("target_lang=$dl", 'source_lang=EN', 'preserve_formatting=1', 'tag_handling=xml', 'ignore_tags=ph')
    foreach ($t in $texts) { $pairs += 'text=' + [uri]::EscapeDataString($t) }
    $resp = Invoke-RestMethod -Uri "$base/v2/translate" -Method Post -Headers $headers `
        -Body ($pairs -join '&') -ContentType 'application/x-www-form-urlencoded'
    $tr = @($resp.translations.text)

    # 복원 + 검증 + 조립(원래 키 순서 유지, 실패/제외는 영어 폴백)
    $out = [ordered]@{}; $fallback = 0
    $ti = 0
    foreach ($k in $keys) {
        if ($skipKeys -contains $k) { $out[$k] = $enObj.$k; continue }
        $restored = Restore $tr[$ti]
        $ok = ($restored -notmatch '</?ph>')
        foreach ($p in $phList[$ti]) { if ($restored -notlike "*$p*") { $ok = $false } }
        if ($ok) { $out[$k] = $restored } else { $out[$k] = $enObj.$k; $fallback++ }
        $ti++
    }

    $path = Join-Path $i18n "$code.json"
    ($out | ConvertTo-Json -Depth 5) | Set-Content -Path $path -Encoding UTF8
    Write-Host ("wrote {0}.json  ({1} keys, {2} english-fallback)" -f $code, $out.Count, $fallback)
}
Write-Host 'done.'
