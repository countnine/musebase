using Musebase.Engine;

namespace Musebase.Android.Services;

/// <summary>
/// 엔진의 구조화 상태(<see cref="LyricsStatus"/>·<see cref="TranslationDisplayStatus"/>)를
/// 화면용 한국어 문구로 바꾸는 공용 헬퍼. 메인 화면과 알림바가 같은 문구를 쓰도록 한곳에 모았다
/// (Windows판은 i18n 카탈로그가 담당 — Android i18n은 다음 단계).
/// </summary>
internal static class StatusText
{
    /// <summary>가사 검색·표시 상태 문구.</summary>
    public static string Lyrics(LyricsStatus? status) => status?.Kind switch
    {
        LyricsStatusKind.NoTrack => "재생 중인 곡 없음",
        LyricsStatusKind.HiddenByUser => "이 곡은 틀린 가사로 표시되어 숨김",
        LyricsStatusKind.Cache => $"가사: 캐시 · {status.Service}",
        LyricsStatusKind.Searching => "가사 검색 중…",
        LyricsStatusKind.Found => $"가사: {status.Service} (품질 {status.Quality ?? 0:0.00})",
        LyricsStatusKind.NotFound => "가사를 찾지 못했습니다",
        LyricsStatusKind.Wrong => "틀린 가사로 표시됨",
        LyricsStatusKind.Manual => $"가사: 수동 선택 · {status.Service}",
        LyricsStatusKind.Edited => "가사: 사용자 편집",
        _ => "",
    };

    /// <summary>번역 표시 상태 접미사(" · 번역: …"). None이면 빈 문자열.</summary>
    public static string TranslationSuffix(TranslationDisplayStatus status) => status switch
    {
        TranslationDisplayStatus.Translating => " · 번역: 번역 중",
        TranslationDisplayStatus.Live => " · 번역: 정상 번역",
        TranslationDisplayStatus.Cache => " · 번역: 캐시 이용",
        TranslationDisplayStatus.Quota => " · 번역: 한도 초과",
        TranslationDisplayStatus.Failed => " · 번역: 실패",
        TranslationDisplayStatus.Disabled => " · 번역: API 꺼짐",
        TranslationDisplayStatus.DisabledCached => " · 번역: 캐시 이용 (API 꺼짐)",
        _ => "",
    };

    /// <summary>가사 상태 + 번역 상태를 합친 한 줄.</summary>
    public static string Combined(LyricsStatus? lyrics, TranslationDisplayStatus translation) =>
        Lyrics(lyrics) + TranslationSuffix(translation);

    /// <summary>"제목 — 아티스트" (아티스트가 없으면 제목만). 곡이 없으면 null.</summary>
    public static string? Track(TrackInfo? track)
    {
        if (track is null) return null;
        var title = string.IsNullOrWhiteSpace(track.Title) ? null : track.Title;
        var artist = string.IsNullOrWhiteSpace(track.Artist) ? null : track.Artist;
        if (title is null && artist is null) return null;
        return artist is null ? title : $"{title} — {artist}";
    }
}
