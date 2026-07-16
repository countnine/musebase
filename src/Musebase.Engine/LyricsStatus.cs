namespace Musebase.Engine;

/// <summary>가사 검색·표시 상태 종류(현지화는 소비자 UI가 담당).</summary>
public enum LyricsStatusKind
{
    NoTrack,
    HiddenByUser,
    Cache,
    Searching,
    Found,
    NotFound,
    Wrong,
    Manual,
    Edited,
}

/// <summary>
/// 엔진이 발행하는 구조화된 상태. 문자열/현지화 대신 종류+데이터를 전달해
/// 엔진을 UI(로컬라이제이션)에서 분리한다. 소비자가 Loc 등으로 문구를 만든다.
/// </summary>
public sealed record LyricsStatus(
    LyricsStatusKind Kind,
    string? Track = null,
    string? Service = null,
    double? Quality = null);
