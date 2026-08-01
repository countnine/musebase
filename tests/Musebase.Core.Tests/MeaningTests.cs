using System.Net;
using System.Text;
using Musebase.Core.Meaning;
using Xunit;

namespace Musebase.Core.Tests;

/// <summary>
/// 곡 의미 수집·생성. 두 가지를 특히 본다 —
/// ① 소스가 죽어도 **예외가 아니라 null**이라 다른 소스가 채운다,
/// ② 자료가 하나도 없으면 **LLM을 아예 부르지 않는다**(곡 해설은 창작이 쉬운 영역이라
///    근거 없이 부르면 모델이 지어낸다).
/// </summary>
public class MeaningTests
{
    // ---- 소스 ----

    [Fact]
    public async Task Genius_응답에서_설명과_곡_주소를_뽑는다()
    {
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.StartsWith("/search"))
                return Json("""
                    {"response":{"hits":[
                      {"type":"song","result":{"id":378195,"url":"https://genius.com/Mgmt-kids-lyrics"}}]}}
                    """);
            return Json("""
                {"response":{"song":{"url":"https://genius.com/Mgmt-kids-lyrics",
                 "description":{"plain":"Kids is about the loss of innocence and the anxieties of growing up, written while the duo were students."}}}}
                """);
        });

        var source = new GeniusSource("token", handler.Client);
        var result = await source.FetchAsync("Kids", "MGMT");

        Assert.NotNull(result);
        Assert.Equal("Genius", result!.Name);
        Assert.Equal("https://genius.com/Mgmt-kids-lyrics", result.Url);
        Assert.Contains("loss of innocence", result.Text);
    }

    [Fact]
    public async Task Genius_설명이_비면_소스가_없는_것으로_본다()
    {
        // 대부분의 곡에는 About이 없다 — 빈 문자열을 근거로 넘기면 모델이 지어낸다.
        var handler = new StubHandler(req =>
            req.RequestUri!.PathAndQuery.StartsWith("/search")
                ? Json("""{"response":{"hits":[{"type":"song","result":{"id":1,"url":"u"}}]}}""")
                : Json("""{"response":{"song":{"url":"u","description":{"plain":"?"}}}}"""));

        Assert.Null(await new GeniusSource("token", handler.Client).FetchAsync("X", "Y"));
    }

    [Fact]
    public async Task 토큰이_없으면_네트워크를_건드리지_않는다()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("불려선 안 된다"));
        Assert.Null(await new GeniusSource("", handler.Client).FetchAsync("X", "Y"));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task 소스가_죽어도_예외_대신_null()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("down"));
        Assert.Null(await new GeniusSource("token", handler.Client).FetchAsync("X", "Y"));
        Assert.Null(await new LastFmSource("key", handler.Client).FetchAsync("X", "Y"));
        Assert.Null(await new WikipediaSource("en", handler.Client).FetchAsync("X", "Y"));
    }

    [Fact]
    public async Task LastFm_본문에서_HTML과_꼬리표를_걷어_낸다()
    {
        var handler = new StubHandler(_ => Json("""
            {"track":{"url":"https://last.fm/x","wiki":{
              "content":"<i>Wonderwall</i> was written by Noel Gallagher about an imaginary friend who saves him from himself. <a href=\"x\">Read more on Last.fm</a>. User-contributed text..."}}}
            """));

        var result = await new LastFmSource("key", handler.Client).FetchAsync("Wonderwall", "Oasis");

        Assert.NotNull(result);
        Assert.DoesNotContain("<i>", result!.Text);
        Assert.DoesNotContain("Read more on Last.fm", result.Text);
        Assert.Contains("imaginary friend", result.Text);
    }

    // ---- Wikipedia 문서 선택 ----
    //
    // 여기서 고른 문서가 그대로 LLM의 근거가 된다. 엉뚱한 문서를 넘기면 그럴듯하고 완전히
    // 틀린 "의미"가 만들어지므로, 확신이 없으면 포기하는 쪽이 옳다.

    [Fact]
    public void 아티스트가_제목에_든_곡_문서를_고른다()
    {
        // 실측 함정: "(song)"이 붙은 제목을 무조건 우선하면 정답인 "Kids (MGMT song)"
        // ("(song)"이 아니라 "(MGMT song)"이다)를 제치고 엉뚱한 문서가 뽑혔다.
        WikipediaSource.SearchHit[] hits =
        [
            new("Kids (MGMT song)", "\"Kids\" is a song by American rock band MGMT."),
            new("Pursuit of Happiness (song)", "a song by Kid Cudi"),
            new("MGMT", "MGMT is an American rock band"),
        ];

        Assert.Equal("Kids (MGMT song)", WikipediaSource.PickPage(hits, "Kids", "MGMT"));
    }

    [Fact]
    public void 아티스트가_스니펫에만_있어도_받아들인다()
    {
        WikipediaSource.SearchHit[] hits =
        [
            new("Wonderwall", "\"Wonderwall\" is a song by the English rock band Oasis."),
        ];

        Assert.Equal("Wonderwall", WikipediaSource.PickPage(hits, "Wonderwall", "Oasis"));
    }

    [Fact]
    public void 제목이_맞아도_아티스트_확인이_안_되면_버린다()
    {
        // 동명이곡 — 근거로 쓰면 다른 곡의 이야기를 이 곡의 의미로 쓰게 된다.
        WikipediaSource.SearchHit[] hits =
        [
            new("Kids (song)", "a 2011 single by Sleigh Bells"),
        ];

        Assert.Null(WikipediaSource.PickPage(hits, "Kids", "MGMT"));
    }

    [Fact]
    public void 제목이_아예_다르면_고르지_않는다()
    {
        WikipediaSource.SearchHit[] hits =
        [
            new("Oracular Spectacular", "the debut album by MGMT"),
        ];

        Assert.Null(WikipediaSource.PickPage(hits, "Kids", "MGMT"));
    }

    [Fact]
    public void 괄호와_구두점은_비교에서_무시한다()
    {
        WikipediaSource.SearchHit[] hits =
        [
            new("Don't Delete the Kisses", "a song by Wolf Alice"),
        ];

        Assert.Equal("Don't Delete the Kisses",
            WikipediaSource.PickPage(hits, "Don’t Delete The Kisses", "Wolf Alice"));
    }

    // ---- 생성 ----

    [Fact]
    public async Task 자료가_하나도_없으면_LLM을_부르지_않는다()
    {
        var writer = new CountingWriter();
        var service = new SongMeaningService([new EmptySource()], writer);

        var result = await service.BuildAsync("X", "Y", "ko");

        Assert.Equal(SongMeaning.NoSource, result.Status);
        Assert.Null(result.Summary);
        Assert.Equal(0, writer.Calls);
    }

    [Fact]
    public async Task 소스_하나만_살아_있어도_의미를_만든다()
    {
        var service = new SongMeaningService(
            [new EmptySource(), new FixedSource("Genius", "이 곡은 성장의 불안에 대한 것이다.")],
            new CountingWriter());

        var result = await service.BuildAsync("Kids", "MGMT", "ko");

        Assert.Equal(SongMeaning.Ok, result.Status);
        Assert.Single(result.Sources);
        Assert.Equal("gemini", result.Engine);
    }

    [Fact]
    public async Task 엔진이_실패하면_자료는_남기고_failed로_기록한다()
    {
        var service = new SongMeaningService(
            [new FixedSource("Genius", "설명")], new NullWriter());

        var result = await service.BuildAsync("Kids", "MGMT", "ko");

        Assert.Equal(SongMeaning.Failed, result.Status);
        Assert.Single(result.Sources); // 다시 시도할 때 재수집하지 않아도 되도록 남긴다
    }

    [Fact]
    public void 프롬프트는_지어내지_말라고_못을_박는다()
    {
        var prompt = MeaningPrompt.Build("Kids", "MGMT",
            [new MeaningSource("Genius", "u", "about growing up")], "ko");

        Assert.Contains("한국어", prompt);
        Assert.Contains("지어내지 않는다", prompt);
        Assert.Contains("about growing up", prompt);
    }

    [Fact]
    public void 원문이_길어도_프롬프트_예산을_넘기지_않는다()
    {
        var huge = new string('가', MeaningPrompt.MaxSourceChars * 3);
        var prompt = MeaningPrompt.Build("T", "A",
            [new MeaningSource("Genius", null, huge), new MeaningSource("Wikipedia", null, huge)], "ko");

        // 예산 + 지시문·머리말 몫의 여유를 봐도 폭주하지 않아야 한다.
        Assert.True(prompt.Length < MeaningPrompt.MaxSourceChars + 2000, $"길이 {prompt.Length}");
    }

    // ---- 레지스트리 ----

    [Fact]
    public void 키가_없으면_엔진이_만들어지지_않는다()
    {
        Assert.Null(MeaningWriterRegistry.Build("gemini", new MeaningWriterOptions()));
        Assert.Null(MeaningWriterRegistry.Build("openrouter", new MeaningWriterOptions()));
        Assert.Null(MeaningWriterRegistry.Build("none", new MeaningWriterOptions { GeminiApiKey = "k" }));
        Assert.Null(MeaningWriterRegistry.Build(null, new MeaningWriterOptions { GeminiApiKey = "k" }));
    }

    [Fact]
    public void 엔진은_설정으로_갈아끼운다()
    {
        var options = new MeaningWriterOptions
        {
            GeminiApiKey = "g",
            OpenRouterApiKey = "o",
            OpenRouterModel = "anthropic/claude-opus-5",
        };

        Assert.Equal("gemini", MeaningWriterRegistry.Build("gemini", options)!.EngineId);
        var openRouter = MeaningWriterRegistry.Build("openrouter", options)!;
        Assert.Equal("openrouter", openRouter.EngineId);
        Assert.Equal("anthropic/claude-opus-5", openRouter.Model);
    }

    [Fact]
    public async Task Gemini_응답에서_본문을_뽑는다()
    {
        var handler = new StubHandler(_ => Json("""
            {"candidates":[{"content":{"parts":[{"text":"이 곡은 성장의 불안을 다룬다."}]}}]}
            """));

        var text = await new GeminiMeaningWriter("key", null, handler.Client)
            .WriteAsync("Kids", "MGMT", [new MeaningSource("Genius", null, "about growing up")], "ko");

        Assert.Equal("이 곡은 성장의 불안을 다룬다.", text);
    }

    [Fact]
    public async Task OpenRouter_응답에서_본문을_뽑는다()
    {
        var handler = new StubHandler(_ => Json("""
            {"choices":[{"message":{"role":"assistant","content":"이 곡은 이별을 다룬다."}}]}
            """));

        var text = await new OpenRouterMeaningWriter("key", "anthropic/claude-opus-5", handler.Client)
            .WriteAsync("X", "Y", [new MeaningSource("Genius", null, "about a breakup")], "ko");

        Assert.Equal("이 곡은 이별을 다룬다.", text);
    }

    [Fact]
    public async Task 엔진_오류는_예외_대신_null()
    {
        var down = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var sources = new[] { new MeaningSource("Genius", null, "text") };

        Assert.Null(await new GeminiMeaningWriter("k", null, down.Client).WriteAsync("T", "A", sources, "ko"));
        Assert.Null(await new OpenRouterMeaningWriter("k", null, down.Client).WriteAsync("T", "A", sources, "ko"));
    }

    // ---- 테스트 더블 ----

    private sealed class EmptySource : ISongMeaningSource
    {
        public string Name => "Empty";
        public Task<MeaningSource?> FetchAsync(string t, string a, CancellationToken ct = default) =>
            Task.FromResult<MeaningSource?>(null);
    }

    private sealed class FixedSource(string name, string text) : ISongMeaningSource
    {
        public string Name => name;
        public Task<MeaningSource?> FetchAsync(string t, string a, CancellationToken ct = default) =>
            Task.FromResult<MeaningSource?>(new MeaningSource(name, "https://example/x", text));
    }

    private sealed class CountingWriter : IMeaningWriter
    {
        public int Calls { get; private set; }
        public string EngineId => "gemini";
        public string Model => "test-model";

        public Task<string?> WriteAsync(
            string title, string artist, IReadOnlyList<MeaningSource> sources,
            string targetLang, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<string?>("생성된 한국어 문단");
        }
    }

    private sealed class NullWriter : IMeaningWriter
    {
        public string EngineId => "gemini";
        public string Model => "test-model";
        public Task<string?> WriteAsync(
            string title, string artist, IReadOnlyList<MeaningSource> sources,
            string targetLang, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpClient Client => new(this);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(responder(request));
        }
    }
}
