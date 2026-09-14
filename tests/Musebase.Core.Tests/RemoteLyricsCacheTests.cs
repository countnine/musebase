using System.Net;
using System.Text;
using Musebase.Core;
using Musebase.Core.Search;
using Xunit;

namespace Musebase.Core.Tests;

/// <summary>
/// <see cref="HttpRemoteLyricsCache"/> — 서버가 없거나 느리거나 이상해도 **앱이 멈추지 않는다**는
/// 계약을 지키는지 본다(조회 실패 = null, 예외 전파 없음, 반복 실패 시 시도 자체를 건너뜀).
/// </summary>
public class RemoteLyricsCacheTests
{
    private const string Lrc = "[ti:T]\n[00:01.00]hello\n[00:01.00][tr:ko]안녕\n";

    [Fact]
    public async Task Get_히트면_LRC를_파싱해_돌려준다()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.OK,
            $$"""{"key":"t|a","title":"T","artist":"A","lrc":{{System.Text.Json.JsonSerializer.Serialize(Lrc)}},"service":"LRCLIB","origin":"provider","langs":["ko"],"match":"exact"}""")));
        var cache = Create(handler);

        var result = await cache.GetAsync("T", "A");

        var lyrics = result.Lyrics;
        Assert.NotNull(lyrics);
        Assert.Single(lyrics!.Lines);
        Assert.Equal("LRCLIB", lyrics.Metadata.ServiceName);
        Assert.Equal("안녕", lyrics.Lines[0].Attachments.Translation("ko"));
        Assert.True(result.HasLanguage("KO")); // 서버가 알려 준 언어 목록도 함께 온다
    }

    [Fact]
    public async Task Get_404면_미스이고_예외가_없다()
    {
        var cache = Create(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        var result = await cache.GetAsync("T", "A");
        Assert.Null(result.Lyrics);
        Assert.False(result.Pending); // 본문 없는 404(구버전 서버)는 평범한 미스다
    }

    [Fact]
    public async Task Get_404_본문의_양보_힌트를_읽는다()
    {
        var cache = Create(new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.NotFound,
            """{"error":"not found","pending":true,"retryAfterMs":3000}"""))));

        var result = await cache.GetAsync("T", "A");

        Assert.Null(result.Lyrics);
        Assert.True(result.Pending);
        Assert.Equal(3000, result.RetryAfterMs);
    }

    [Fact]
    public async Task Get_서버가_죽어도_예외_대신_미스()
    {
        var cache = Create(new StubHandler(_ => Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused"))));
        Assert.Null((await cache.GetAsync("T", "A")).Lyrics);
    }

    [Fact]
    public async Task Get_응답이_깨져도_미스()
    {
        var cache = Create(new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, "{ this is not json"))));
        Assert.Null((await cache.GetAsync("T", "A")).Lyrics);
    }

    [Fact]
    public async Task 연속_실패하면_회로가_열려_더는_요청하지_않는다()
    {
        var handler = new StubHandler(_ => Task.FromException<HttpResponseMessage>(new HttpRequestException("down")));
        var cache = Create(handler);

        await cache.GetAsync("T", "A");
        await cache.GetAsync("T", "A"); // 여기서 임계값(2회) 도달 → 회로 오픈
        var callsAfterOpen = handler.Calls;
        await cache.GetAsync("T", "A");
        await cache.GetAsync("T", "A");

        Assert.Equal(2, callsAfterOpen);
        Assert.Equal(callsAfterOpen, handler.Calls); // 회로가 열린 뒤에는 아예 나가지 않는다
    }

    [Fact]
    public async Task Set_은_사용자_편집본을_origin_user로_올린다()
    {
        string? body = null;
        var handler = new StubHandler(async req =>
        {
            body = req.Content is null ? null : await req.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var cache = Create(handler);

        var lyrics = Lyrics.Parse(Lrc)!;
        lyrics.Metadata.ServiceName = HttpRemoteLyricsCache.EditedServiceName;
        await cache.SetAsync("T", "A", lyrics);

        Assert.NotNull(body);
        Assert.Contains("\"origin\":\"user\"", body);
    }

    [Fact]
    public async Task Set_실패는_조용히_무시된다()
    {
        var cache = Create(new StubHandler(_ => Task.FromException<HttpResponseMessage>(new HttpRequestException("down"))));
        await cache.SetAsync("T", "A", Lyrics.Parse(Lrc)!); // 예외가 새어 나오면 실패
    }

    [Fact]
    public async Task 광고로_표시된_제목은_힌트를_받아_검색하지_않는다()
    {
        var cache = Create(new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.NotFound,
            """{"error":"ad","pending":false,"retryAfterMs":0,"ad":true}"""))));

        var result = await cache.GetAsync("광고 없이 음악을 감상하세요.", "Spotify");

        Assert.True(result.IsAd);
        Assert.Null(result.Lyrics);
        Assert.False(result.Pending); // 양보와 혼동하지 않는다
    }

    [Fact]
    public async Task 구버전_서버의_평범한_404는_광고가_아니다()
    {
        var cache = Create(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        Assert.False((await cache.GetAsync("Kids", "MGMT")).IsAd);
    }

    // ---- 곡의 의미(앱은 읽기만 한다) ----

    [Fact]
    public async Task 의미와_출처를_읽는다()
    {
        var cache = Create(new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, """
            {"summary":"이 곡은 성장의 불안을 다룬다.","lang":"ko",
             "attribution":[{"name":"Wikipedia","url":"https://en.wikipedia.org/wiki/Kids"},
                            {"name":"Genius","url":null}]}
            """))));

        var meaning = await cache.GetMeaningAsync("Kids", "MGMT");

        Assert.NotNull(meaning);
        Assert.Equal("이 곡은 성장의 불안을 다룬다.", meaning!.Summary);
        Assert.Equal(2, meaning.Attribution.Count);
        // 출처 표기는 의무다 — 라이선스까지 붙는다.
        Assert.Contains("Wikipedia", meaning.CreditLine);
        Assert.Contains("CC BY-SA", meaning.CreditLine);
    }

    [Fact]
    public async Task 의미가_없으면_404이고_그것은_정상이다()
    {
        var cache = Create(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        Assert.Null(await cache.GetMeaningAsync("Kids", "MGMT"));
    }

    [Fact]
    public async Task 의미_조회_실패는_가사_조회를_막지_않는다()
    {
        // 부가 기능이라 실패를 서킷 브레이커에 세지 않는다 — 여기서 회로가 열리면 손해가 크다.
        var handler = new StubHandler(req =>
            req.RequestUri!.AbsolutePath.Contains("meaning")
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("down"))
                : Task.FromResult(Json(HttpStatusCode.OK,
                    $$"""{"title":"Kids","artist":"MGMT","lrc":{{System.Text.Json.JsonSerializer.Serialize(Lrc)}},"service":"LRCLIB"}""")));
        var cache = Create(handler);

        for (var i = 0; i < 5; i++) Assert.Null(await cache.GetMeaningAsync("Kids", "MGMT"));

        Assert.NotNull((await cache.GetAsync("Kids", "MGMT")).Lyrics);
    }

    // ---- 의미 만들기(사람이 버튼을 눌렀을 때만) ----

    [Fact]
    public async Task 만들기는_POST로_보낸다()
    {
        HttpMethod? method = null;
        var cache = Create(new StubHandler(req =>
        {
            method = req.Method;
            return Task.FromResult(Json(HttpStatusCode.OK,
                """{"summary":"이 곡은 성장의 불안을 다룬다.","lang":"ko","attribution":[]}"""));
        }));

        var result = await cache.RequestMeaningAsync("Kids", "MGMT");

        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal(MeaningRequestStatus.Created, result.Status);
        Assert.Equal("이 곡은 성장의 불안을 다룬다.", result.Meaning!.Summary);
    }

    /// <summary>
    /// <b>기본은 캐시 우선이다.</b> force 없이 부르면 서버가 만들어 둔 글을 그대로 돌려주므로
    /// 주소에 force가 실리면 안 된다 — 실렸다면 멀쩡한 글을 덮어쓴다.
    /// </summary>
    [Fact]
    public async Task 의미_요청은_기본적으로_다시_만들지_않는다()
    {
        string? url = null;
        var cache = Create(new StubHandler(req =>
        {
            url = req.RequestUri!.ToString();
            return Task.FromResult(Json(HttpStatusCode.OK,
                """{"title":"Kids","artist":"MGMT","summary":"이미 있던 글","lang":"ko"}"""));
        }));

        await cache.RequestMeaningAsync("Kids", "MGMT");

        Assert.DoesNotContain("force", url);
    }

    [Fact]
    public async Task 사람이_확인했을_때만_force를_싣는다()
    {
        string? url = null;
        var cache = Create(new StubHandler(req =>
        {
            url = req.RequestUri!.ToString();
            return Task.FromResult(Json(HttpStatusCode.OK,
                """{"title":"Kids","artist":"MGMT","summary":"새로 쓴 글","lang":"ko"}"""));
        }));

        await cache.RequestMeaningAsync("Kids", "MGMT", force: true);

        Assert.Contains("force=1", url);
    }

    /// <summary>
    /// 만들지 못한 것은 오류가 아니라 흔한 결과다. 다만 이유마다 사람에게 할 말이 다르므로
    /// (다시 눌러 볼지 말지가 갈린다) 202 본문의 status를 그대로 옮겨야 한다.
    /// </summary>
    [Theory]
    [InlineData("no-source", MeaningRequestStatus.NoSource)]
    [InlineData("insufficient", MeaningRequestStatus.Insufficient)]
    [InlineData("retry", MeaningRequestStatus.Retry)]
    [InlineData("뭔가 새로운 값", MeaningRequestStatus.Failed)]
    public async Task 만들지_못한_이유를_구분해_옮긴다(string status, MeaningRequestStatus expected)
    {
        var cache = Create(new StubHandler(_ => Task.FromResult(
            Json(HttpStatusCode.Accepted, $$"""{"status":"{{status}}"}"""))));

        var result = await cache.RequestMeaningAsync("Kids", "MGMT");

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Meaning);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]        // 서버가 앱 생성을 껐다
    [InlineData(HttpStatusCode.ServiceUnavailable)] // 의미 엔진 미구성
    public async Task 서버가_막았으면_안내할_수_있게_구분한다(HttpStatusCode code)
    {
        var cache = Create(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(code))));
        Assert.Equal(MeaningRequestStatus.Unavailable,
            (await cache.RequestMeaningAsync("Kids", "MGMT")).Status);
    }

    [Fact]
    public async Task 만들기_실패는_예외_대신_Failed다()
    {
        var cache = Create(new StubHandler(_ => Task.FromException<HttpResponseMessage>(new HttpRequestException("down"))));
        Assert.Equal(MeaningRequestStatus.Failed, (await cache.RequestMeaningAsync("Kids", "MGMT")).Status);
    }

    /// <summary>
    /// 조회와 달리 <b>회로가 열려 있어도 시도한다</b> — 사람이 방금 누른 동작이라
    /// 조용히 아무 일도 안 일어나면 고장으로 보인다.
    /// </summary>
    [Fact]
    public async Task 회로가_열려_있어도_만들기는_시도한다()
    {
        var handler = new StubHandler(req => req.Method == HttpMethod.Post
            ? Task.FromResult(Json(HttpStatusCode.Accepted, """{"status":"no-source"}"""))
            : Task.FromException<HttpResponseMessage>(new HttpRequestException("down")));
        var cache = Create(handler);

        await cache.GetAsync("T", "A");
        await cache.GetAsync("T", "A");   // 회로 오픈
        var callsAfterOpen = handler.Calls;

        Assert.Equal(MeaningRequestStatus.NoSource, (await cache.RequestMeaningAsync("Kids", "MGMT")).Status);
        Assert.Equal(callsAfterOpen + 1, handler.Calls);
    }

    // ---- 곡에 딸린 것들(커버·좋아요) ----

    [Fact]
    public async Task 커버와_좋아요를_한_번에_받는다()
    {
        var cache = Create(new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, """
            {"coverUrl":"https://is1-ssl.mzstatic.com/a/600x600bb.jpg","coverSource":"itunes",
             "lastFmUrl":"https://www.last.fm/music/MGMT/_/Kids",
             "loveConnected":true,"loveKnown":true,"loved":true}
            """))));

        var result = await cache.GetExtrasAsync("Kids", "MGMT");

        Assert.Equal(ExtrasReach.Ok, result.Reach);
        var extras = result.Extras!;
        Assert.EndsWith("600x600bb.jpg", extras.CoverUrl);
        Assert.True(extras.LoveConnected);
        Assert.True(extras.ShowLoved);
    }

    /// <summary>
    /// 확인하지 못한 좋아요를 켜서 그리면, 사람이 눌러 <b>이미 켜 둔 것을 끈다</b>.
    /// 그래서 Loved가 true여도 Known이 false면 하트를 켜지 않는다.
    /// </summary>
    [Fact]
    public async Task 확인하지_못한_좋아요는_켜서_그리지_않는다()
    {
        var cache = Create(new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.OK,
            """{"loveConnected":true,"loveKnown":false,"loved":true}"""))));

        var extras = (await cache.GetExtrasAsync("Kids", "MGMT")).Extras!;

        Assert.True(extras.Loved);         // 서버가 보낸 값은 그대로 두되
        Assert.False(extras.ShowLoved);    // 화면에는 켜지 않는다
    }

    [Fact]
    public async Task 커버가_없는_곡은_빈_문자열이_아니라_null이다()
    {
        var cache = Create(new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.OK,
            """{"coverUrl":"","loveConnected":false,"loveKnown":false,"loved":false}"""))));

        Assert.Null((await cache.GetExtrasAsync("Kids", "MGMT")).Extras!.CoverUrl);
    }

    [Fact]
    public async Task 좋아요_토글은_POST로_on을_실어_보낸다()
    {
        string? url = null;
        HttpMethod? method = null;
        var cache = Create(new StubHandler(req =>
        {
            url = req.RequestUri!.ToString();
            method = req.Method;
            return Task.FromResult(Json(HttpStatusCode.OK,
                """{"loveConnected":true,"loveKnown":true,"loved":false}"""));
        }));

        var extras = await cache.SetLovedAsync("Kids", "MGMT", loved: false);

        Assert.Equal(HttpMethod.Post, method);
        Assert.Contains("v1/song/love", url);
        Assert.Contains("on=0", url);
        Assert.False(extras!.ShowLoved);
    }

    [Fact]
    public async Task 곡에_딸린_조회가_실패해도_가사_조회는_살아_있다()
    {
        // 부가 정보라 실패를 서킷 브레이커에 세지 않는다(의미 조회와 같은 원칙).
        var handler = new StubHandler(req =>
            req.RequestUri!.AbsolutePath.Contains("song")
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("down"))
                : Task.FromResult(Json(HttpStatusCode.OK,
                    $$"""{"title":"Kids","artist":"MGMT","lrc":{{System.Text.Json.JsonSerializer.Serialize(Lrc)}},"service":"LRCLIB"}""")));
        var cache = Create(handler);

        for (var i = 0; i < 5; i++)
            Assert.Equal(ExtrasReach.Failed, (await cache.GetExtrasAsync("Kids", "MGMT")).Reach);

        Assert.NotNull((await cache.GetAsync("Kids", "MGMT")).Lyrics);
    }

    /// <summary>
    /// <b>처음 트는 곡은 서버에 아직 없다</b> — 그 404를 연결 오류로 올리면 새 곡마다 경고가 뜬다.
    /// 실측에서 실제로 그랬다: 조회 미스 7초 뒤에 이 기기가 올렸는데, 그 사이 화면에는
    /// "서버에 연결하지 못했습니다"가 떴다.
    /// </summary>
    [Fact]
    public async Task 서버가_모르는_곡은_오류가_아니다()
    {
        var cache = Create(new StubHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));

        var result = await cache.GetExtrasAsync("My Sharona", "The Knack");

        Assert.Equal(ExtrasReach.NotFound, result.Reach);
        Assert.Null(result.Extras);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task 서버가_이상하면_실패로_알린다(HttpStatusCode status)
    {
        // 404가 아닌 비정상 응답은 사람이 알아야 한다(주소를 잘못 넣었거나 토큰이 틀렸다).
        var cache = Create(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(status))));

        Assert.Equal(ExtrasReach.Failed, (await cache.GetExtrasAsync("Kids", "MGMT")).Reach);
    }

    private static HttpRemoteLyricsCache Create(StubHandler handler) =>
        new("http://localhost:9/", "token", timeoutMs: 500, log: null, handler: handler);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    /// <summary>요청을 가로채는 스텁. 호출 횟수를 세어 서킷 브레이커를 검증한다.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return await responder(request);
        }
    }
}
