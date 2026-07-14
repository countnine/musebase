using LyricsX.Core;
using LyricsX.Core.Search;
using Xunit;
using Xunit.Abstractions;

namespace LyricsX.Core.Tests;

/// <summary>
/// 실제 제공자 API를 호출하는 통합 프로브. 기본은 무동작(오프라인 CI 안전).
/// 실행: 환경변수 LYRICSX_LIVE=1 로 설정 후 이 클래스만 필터 실행.
///   dotnet test --filter "FullyQualifiedName~LiveSearchProbe" -l "console;verbosity=detailed"
/// </summary>
public class LiveSearchProbe
{
    private readonly ITestOutputHelper _out;
    public LiveSearchProbe(ITestOutputHelper output) => _out = output;

    private static bool Live => Environment.GetEnvironmentVariable("LYRICSX_LIVE") == "1";

    private async Task Probe(string label, LyricsSearchRequest request)
    {
        var service = new LyricsSearchService();
        var results = await service.SearchAllAsync(request);
        _out.WriteLine($"[{label}] term=\"{request.Term}\" → {results.Count}건");
        foreach (var l in results.Take(5))
        {
            var svc = l.Metadata.ServiceName;
            var title = l.IdTags.GetValueOrDefault(Lyrics.TagTitle);
            var artist = l.IdTags.GetValueOrDefault(Lyrics.TagArtist);
            var tt = l.Metadata.AttachmentTags.Contains(LineAttachments.TagTimeTag) ? "글자" : "라인";
            var tr = l.HasTranslation() ? "번역O" : "번역-";
            _out.WriteLine($"    {svc,-8} q={l.Quality():0.00} [{tt}/{tr}] {title} / {artist} ({l.Lines.Count}줄)");
        }
        Assert.NotEmpty(results);
    }

    [Fact]
    public async Task Lrclib_English()
    {
        if (!Live) return;
        await Probe("영문/LRCLIB", LyricsSearchRequest.ByInfo("Bohemian Rhapsody", "Queen", 355));
    }

    [Fact]
    public async Task MetadataCleaning_NoisyTitle()
    {
        if (!Live) return;
        // 스트리밍 표기가 붙은 제목 — 정제 변형 덕에 찾혀야 함
        await Probe("정제확장", LyricsSearchRequest.ByInfo("Bohemian Rhapsody - Remastered 2011", "Queen", 355));
    }

    [Fact]
    public async Task Chinese_KugouNetEaseQQ()
    {
        if (!Live) return;
        // 중국 곡 — Kugou/NetEase/QQ 커버리지 (글자단위 tt 기대)
        await Probe("중국곡", LyricsSearchRequest.ByInfo("晴天", "周杰伦", 269));
    }

    [Fact]
    public async Task QQMusic_Only()
    {
        if (!Live) return;
        // QQ 제공자 단독 — lyric_download.fcg XML/QRC 복호 경로 검증
        var service = new LyricsSearchService(new QQMusicProvider());
        var results = await service.SearchAllAsync(LyricsSearchRequest.ByInfo("晴天", "周杰伦", 269));
        _out.WriteLine($"[QQ단독] → {results.Count}건");
        foreach (var l in results.Take(5))
            _out.WriteLine($"    q={l.Quality():0.00} [{(l.Metadata.AttachmentTags.Contains(LineAttachments.TagTimeTag) ? "글자" : "라인")}] " +
                $"{l.IdTags.GetValueOrDefault(Lyrics.TagTitle)} / {l.IdTags.GetValueOrDefault(Lyrics.TagArtist)} ({l.Lines.Count}줄)");
        Assert.NotEmpty(results);
    }
}
