using Musebase.Core.Meaning;
using Xunit;

namespace Musebase.Core.Tests;

/// <summary>
/// Musixmatch 연동 — 공식 API로 **확인한** 주소만 쓰고, 검색 결과는 그대로 믿지 않는다.
/// </summary>
public class MusixmatchTests
{
    private const string SearchJson = """
        {"message":{"header":{"status_code":200},"body":{"track_list":[
          {"track":{"track_id":15445219,"track_name":"Even Flow","artist_name":"Pearl Jam",
                    "track_share_url":"https://www.musixmatch.com/lyrics/Pearl-Jam/Even-Flow-2"}}]}}}
        """;

    [Fact]
    public void 곡_페이지_주소를_뽑는다()
    {
        var track = MusixmatchApi.Pick(SearchJson, "Even Flow", "Pearl Jam");

        Assert.NotNull(track);
        Assert.Equal("https://www.musixmatch.com/lyrics/Pearl-Jam/Even-Flow-2", track!.ShareUrl);
        Assert.Equal(15445219, track.TrackId);
    }

    [Fact]
    public void 본문_status_code가_실패면_결과를_쓰지_않는다()
    {
        // HTTP는 200이어도 Musixmatch는 성공/실패를 본문에 싣는다(키 오류 401, 플랜 초과 402 …).
        var denied = """
            {"message":{"header":{"status_code":401},"body":{"track_list":[
              {"track":{"track_id":1,"track_name":"Even Flow","artist_name":"Pearl Jam",
                        "track_share_url":"https://example/x"}}]}}}
            """;

        Assert.Null(MusixmatchApi.Pick(denied, "Even Flow", "Pearl Jam"));
    }

    [Fact]
    public void 결과가_없어_body가_빈_배열로_와도_죽지_않는다()
    {
        // 실제로 이렇게 오는 경우가 있어 레코드 역직렬화 대신 방어적으로 읽는다.
        Assert.Null(MusixmatchApi.Pick("""{"message":{"header":{"status_code":200},"body":[]}}""", "T", "A"));
    }

    [Fact]
    public void 무관한_곡은_거른다()
    {
        // 검색 API는 무엇을 넣든 뭔가를 돌려준다 — Genius에서 겪은 것과 같은 함정이다.
        Assert.Null(MusixmatchApi.Pick(SearchJson, "해외에서 화제라는 한국의 지하철 문화", "여기는한국"));
    }

    [Fact]
    public void 합작곡_표기가_달라도_찾는다()
    {
        var json = """
            {"message":{"header":{"status_code":200},"body":{"track_list":[
              {"track":{"track_id":7,"track_name":"Shallow","artist_name":"Lady Gaga & Bradley Cooper",
                        "track_share_url":"https://example/shallow"}}]}}}
            """;

        Assert.NotNull(MusixmatchApi.Pick(json, "Shallow", "Lady Gaga/Bradley Cooper"));
    }

    // ---- 곡 페이지에서 의미 꺼내기 ----

    [Fact]
    public void lens의_의미_문단을_찾는다()
    {
        var html = """
            <html><body><script id="__NEXT_DATA__" type="application/json">
            {"props":{"pageProps":{"track":{"id":1,"lens":{
              "meaning":{"explanation":"이 곡은 고립과 어울리지 못하는 감정, 그리고 연결을 찾는 이야기를 다룬다.","type":"meaning"},
              "moods":{"main_moods":["reflection"],"type":"moods"}}}}}}
            </script></body></html>
            """;

        Assert.Contains("고립과 어울리지 못하는", MusixmatchMeaningSource.Explanation(html));
    }

    [Fact]
    public void 구조가_바뀌어_lens가_없으면_null이다()
    {
        // 경로를 고정하지 않고 재귀로 찾되, 없으면 조용히 포기한다(지어내지 않는다).
        var html = """
            <html><script id="__NEXT_DATA__" type="application/json">
            {"props":{"pageProps":{"track":{"id":1}}}}
            </script></html>
            """;

        Assert.Null(MusixmatchMeaningSource.Explanation(html));
    }

    [Fact]
    public void 페이지에_데이터_블록이_없으면_null이다()
    {
        Assert.Null(MusixmatchMeaningSource.Explanation("<html><body>로그인이 필요합니다</body></html>"));
        Assert.Null(MusixmatchMeaningSource.Explanation(""));
    }

    [Fact]
    public void 중첩이_깊어도_찾아낸다()
    {
        var html = """
            <html><script id="__NEXT_DATA__" type="application/json">
            {"a":[{"b":{"c":[{"lens":{"meaning":{"explanation":"깊은 곳에 있는 설명 문장이다."}}}]}}]}
            </script></html>
            """;

        Assert.Equal("깊은 곳에 있는 설명 문장이다.", MusixmatchMeaningSource.Explanation(html));
    }
}
