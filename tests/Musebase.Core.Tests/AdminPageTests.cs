using Musebase.Server;
using Xunit;

namespace Musebase.Core.Tests;

/// <summary>
/// 가사 서버 관리자 페이지의 순수 함수들. 화면은 DB 없이 렌더되도록 만들었으므로
/// (렌더러가 record만 받는다) HTML 생성까지 여기서 검증한다.
/// </summary>
public class AdminPageTests
{
    private static readonly TimeZoneInfo Kst = TimeZoneInfo.CreateCustomTimeZone("KST", TimeSpan.FromHours(9), "KST", "KST");

    // ---- 이스케이프 ----

    [Fact]
    public void 제목에_꺾쇠가_있어도_HTML로_새지_않는다()
    {
        var html = AdminHtml.Esc("<script>alert('x')</script> & \"q\"");
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&amp;", html);
        Assert.Contains("&quot;", html);
        Assert.Contains("&#39;", html);
    }

    [Fact]
    public void 곡_목록에_들어온_스크립트_태그는_렌더링돼도_이스케이프된다()
    {
        var song = new SongRow("k", "k", "<img onerror=x>", "아티스트", "LRCLIB", "provider",
            ["ko"], 10, false, 1, "2026-07-29T00:00:00Z", "거실PC");

        var html = AdminPages.SearchPage("q", [song], Kst);

        Assert.DoesNotContain("<img onerror", html);
        Assert.Contains("&lt;img onerror=x&gt;", html);
    }

    // ---- 히트율 ----

    [Fact]
    public void 조회가_0건이면_히트율은_0으로_표시된다()
    {
        var rate = new HitRate(0, 0, 0);
        Assert.Equal(0, rate.Percent);
        Assert.Equal(0, rate.Total);
    }

    [Theory]
    [InlineData(9, 1, 0, 100)]   // 느슨한 매치도 히트로 센다
    [InlineData(7, 0, 3, 70)]
    [InlineData(0, 0, 5, 0)]
    public void 히트율은_느슨한_매치를_포함해_계산된다(int exact, int cleaned, int miss, int expected)
    {
        Assert.Equal(expected, new HitRate(exact, cleaned, miss).Percent);
    }

    // ---- 검색어 → LIKE ----

    [Fact]
    public void 빈_검색어는_전체_목록을_뜻한다()
    {
        Assert.Null(AdminQuery.ToLikePattern(null));
        Assert.Null(AdminQuery.ToLikePattern("   "));
    }

    [Theory]
    [InlineData("love", "%love%")]
    [InlineData("Love Story", "%love story%")]          // 대소문자 무시를 위해 소문자로
    [InlineData("50%", @"%50\%%")]                      // 와일드카드는 리터럴로
    [InlineData("a_b", @"%a\_b%")]
    [InlineData(@"back\slash", @"%back\\slash%")]
    public void 검색어의_와일드카드_기호는_리터럴로_취급된다(string input, string expected)
    {
        Assert.Equal(expected, AdminQuery.ToLikePattern(input));
    }

    // ---- 쿠키 서명 ----

    [Fact]
    public void 서명한_쿠키는_같은_비밀로_검증된다()
    {
        var exp = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();
        var cookie = AdminAuth.Sign("secret", exp);
        Assert.True(AdminAuth.Verify(cookie, "secret", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    [Fact]
    public void 만료된_쿠키는_거부된다()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var cookie = AdminAuth.Sign("secret", now - 10);
        Assert.False(AdminAuth.Verify(cookie, "secret", now));
    }

    [Fact]
    public void 토큰이_다르면_서명이_맞지_않는다()
    {
        var exp = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();
        Assert.False(AdminAuth.Verify(AdminAuth.Sign("secret", exp), "other", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    [Fact]
    public void 형식이_깨진_쿠키는_예외없이_거부된다()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.False(AdminAuth.Verify("", "secret", now));
        Assert.False(AdminAuth.Verify("garbage", "secret", now));
        Assert.False(AdminAuth.Verify(".", "secret", now));
    }

    [Fact]
    public void CSRF_토큰은_쿠키값에_묶인다()
    {
        var a = AdminAuth.Csrf("secret", "cookie-a");
        Assert.True(AdminAuth.VerifyCsrf(a, "secret", "cookie-a"));
        Assert.False(AdminAuth.VerifyCsrf(a, "secret", "cookie-b"));
        Assert.False(AdminAuth.VerifyCsrf(null, "secret", "cookie-a"));
    }

    // ---- 기기 라벨 ----

    [Fact]
    public void 헤더가_없으면_소스IP를_기기로_쓴다()
    {
        var label = DeviceLabel.Resolve(null, null, null, "100.1.2.3", DeviceLabel.ParseLabels(null));
        Assert.Equal("100.1.2.3", label);
    }

    [Fact]
    public void 프록시_뒤에서는_XForwardedFor의_첫_IP를_쓴다()
    {
        var label = DeviceLabel.Resolve(null, null, "100.1.2.3, 10.0.0.1", "127.0.0.1",
            DeviceLabel.ParseLabels(null));
        Assert.Equal("100.1.2.3", label);
    }

    [Fact]
    public void 매핑에_있는_IP는_사람이_읽는_이름이_된다()
    {
        var labels = DeviceLabel.ParseLabels("100.1.2.3=거실PC, 100.4.5.6=갤럭시");
        Assert.Equal("거실PC", DeviceLabel.Resolve(null, null, "100.1.2.3", "127.0.0.1", labels));
        Assert.Equal("갤럭시", DeviceLabel.Resolve(null, null, null, "100.4.5.6", labels));
    }

    [Fact]
    public void 브라우저_UA는_기기_이름으로_쓰지_않는다()
    {
        // curl·브라우저로 관리자 페이지를 열었을 때 UA가 기기 이름이 되면 의미가 없다 — IP를 쓴다.
        var label = DeviceLabel.Resolve(null, "curl/8.21.0", "100.1.2.3", "127.0.0.1", DeviceLabel.ParseLabels(null));
        Assert.Equal("100.1.2.3", label);
    }

    [Fact]
    public void Musebase_앱이_보낸_UA는_기기_이름으로_쓴다()
    {
        var label = DeviceLabel.Resolve(null, "Musebase/0.16.0 (windows)", null, "127.0.0.1", DeviceLabel.ParseLabels(null));
        Assert.Equal("Musebase/0.16.0 (windows)", label);
    }

    [Fact]
    public void 매핑_문자열이_깨져도_예외없이_읽을_수_있는_것만_쓴다()
    {
        var labels = DeviceLabel.ParseLabels("=이름없음,100.1.2.3=거실PC,쓰레기,100.9.9.9=");
        Assert.Single(labels);
        Assert.Equal("거실PC", labels["100.1.2.3"]);
    }

    // ---- 시간 ----

    [Fact]
    public void KST_자정_경계가_UTC로_올바르게_환산된다()
    {
        // KST 2026-07-29 08:00 = UTC 2026-07-28 23:00 → "오늘"의 시작은 UTC 전날 15:00
        var now = new DateTimeOffset(2026, 7, 28, 23, 0, 0, TimeSpan.Zero);
        Assert.Equal("2026-07-28T15:00:00Z", AdminTime.TodayStartUtc(now, Kst));
    }

    [Theory]
    [InlineData(30, "방금")]
    [InlineData(60 * 5, "5분 전")]
    [InlineData(3600 * 3, "3시간 전")]
    [InlineData(86400 * 2, "2일 전")]
    public void 마지막_조회는_상대_시간으로_표시된다(int secondsAgo, string expected)
    {
        var now = DateTimeOffset.UtcNow;
        var at = now.AddSeconds(-secondsAgo).ToString("yyyy-MM-ddTHH:mm:ssZ");
        Assert.Equal(expected, AdminTime.Ago(at, now));
    }

    [Fact]
    public void 조회_기록이_없으면_마지막_조회는_없음이다()
    {
        Assert.Equal("없음", AdminTime.Ago(null, DateTimeOffset.UtcNow));
    }

    // ---- LRC 분해 ----

    [Fact]
    public void 번역이_붙은_LRC는_원문과_번역_두_열로_분해된다()
    {
        const string lrc = "[00:01.00]hello\n[00:01.00][tr:ko]안녕\n[00:05.50]world\n";

        var lines = AdminLrc.ToDisplayLines(lrc, "ko");

        Assert.Equal(2, lines.Count);
        Assert.Equal("hello", lines[0].Content);
        Assert.Equal("안녕", lines[0].Translation);
        Assert.Null(lines[1].Translation);
        Assert.Equal("00:01.00", lines[0].TimeTag);
    }

    [Fact]
    public void 파싱_실패시_빈_목록을_돌려준다()
    {
        Assert.Empty(AdminLrc.ToDisplayLines("가사가 아님", "ko"));
    }

    [Fact]
    public void 번역_언어_목록에서_언어미상_제공자_번역은_제외된다()
    {
        const string lrc = "[00:01.00]hello\n[00:01.00][tr:ko]안녕\n[00:01.00][tr]你好\n";
        Assert.Equal(["ko"], AdminLrc.TranslationTags(lrc));
    }

    // ---- 빈 화면 ----

    [Fact]
    public void 조회_기록이_비면_안내행이_렌더된다()
    {
        var html = AdminPages.Dashboard(EmptyDashboard(), DateTimeOffset.UtcNow, Kst);

        Assert.Contains("아직 조회가 없습니다", html);
        Assert.Contains("colspan", html);
    }

    [Fact]
    public void 스크립트는_제출_스피너_하나뿐이다()
    {
        // 예전에는 JS가 한 줄도 없었다. 지금은 제출 스피너 하나가 있고, 그 대가로 CSP를 느슨하게
        // 하지 않기 위해 **해시로 고정**한다 — 스크립트가 늘거나 바뀌면 이 테스트가 먼저 걸린다.
        var html = AdminPages.Dashboard(EmptyDashboard(), DateTimeOffset.UtcNow, Kst);

        var scripts = html.Split("<script").Length - 1;
        Assert.Equal(1, scripts);
        Assert.Contains($"<script>{AdminHtml.BusyScript}</script>", html);
    }

    [Fact]
    public void 제출은_히스토리를_늘리지_않는다()
    {
        // 평범한 폼 제출은 [검색 → 곡 → 곡(생성 후)]을 만들어 뒤로 가기가 "생성 전의 같은 곡"으로
        // 간다. fetch로 보내고 location.replace로 지금 칸을 덮어써야 한 번에 그 앞 화면으로 간다.
        Assert.Contains("fetch(", AdminHtml.BusyScript);
        Assert.Contains("location.replace", AdminHtml.BusyScript);
        Assert.DoesNotContain("history.pushState", AdminHtml.BusyScript);

        // fetch가 없는 브라우저에서는 평소대로 제출돼야 한다.
        Assert.Contains("if(!window.fetch", AdminHtml.BusyScript);
        Assert.Contains("f.submit()", AdminHtml.BusyScript);
    }

    [Fact]
    public void CSP는_그_스크립트의_해시만_허용한다()
    {
        Assert.StartsWith("'sha256-", AdminHtml.ScriptCsp);
        Assert.DoesNotContain("unsafe-inline", AdminHtml.ScriptCsp);

        // 스크립트를 고치면 해시도 따라 바뀌어야 한다(상수로 박아 두면 조용히 안 돈다).
        var expected = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(AdminHtml.BusyScript)));
        Assert.Equal($"'sha256-{expected}'", AdminHtml.ScriptCsp);
    }

    [Fact]
    public void 생성_폼은_스피너_표시_대상이다()
    {
        // data-busy가 없으면 눌러도 아무 반응이 없어 사람이 다시 누른다(같은 곡을 두 번 만든다).
        var model = EmptyDashboard() with
        {
            Meanings = new MeaningSummary(0, 0, 0, Pending: 3, Enabled: true),
        };

        Assert.Contains("data-busy", AdminPages.Dashboard(model, DateTimeOffset.UtcNow, Kst));
    }

    // ---- 곡의 의미 ----

    [Fact]
    public void 의미_엔진이_없으면_일괄_생성_버튼이_뜨지_않는다()
    {
        var model = EmptyDashboard() with
        {
            Meanings = new MeaningSummary(0, 0, 0, Pending: 30, Enabled: false),
        };

        var html = AdminPages.Dashboard(model, DateTimeOffset.UtcNow, Kst);

        Assert.Contains("엔진 미구성", html);
        Assert.DoesNotContain($"{Routes.Base}/meanings/backfill", html);
    }

    [Fact]
    public void 처리할_곡이_있으면_일괄_생성_버튼에_곡_수가_보인다()
    {
        var model = EmptyDashboard() with
        {
            Meanings = new MeaningSummary(5, 1, 0, Pending: 30, Enabled: true),
        };

        var html = AdminPages.Dashboard(model, DateTimeOffset.UtcNow, Kst);

        Assert.Contains($"{Routes.Base}/meanings/backfill", html);
        Assert.Contains("의미 일괄 생성 (30곡)", html);
    }

    [Fact]
    public void 처리할_곡이_없으면_버튼을_숨긴다()
    {
        var model = EmptyDashboard() with
        {
            Meanings = new MeaningSummary(5, 1, 0, Pending: 0, Enabled: true),
        };

        Assert.DoesNotContain($"{Routes.Base}/meanings/backfill",
            AdminPages.Dashboard(model, DateTimeOffset.UtcNow, Kst));
    }

    // ---- 로그인 ----

    [Fact]
    public void 비밀번호를_정하면_아이디_칸이_먼저_나온다()
    {
        var html = AdminPages.Login(passwordEnabled: true);

        Assert.Contains("name=\"user\"", html);
        Assert.Contains("name=\"password\"", html);
        // 토큰은 사라지지 않는다 — 비밀번호를 잊었을 때의 비상구다.
        Assert.Contains("name=\"token\"", html);
        Assert.Contains("토큰으로 들어가기", html);
    }

    [Fact]
    public void 비밀번호가_없으면_토큰_화면_그대로다()
    {
        var html = AdminPages.Login();

        Assert.Contains("name=\"token\"", html);
        Assert.DoesNotContain("name=\"password\"", html);
    }

    [Fact]
    public void 비밀번호는_해시로_검증된다()
    {
        var stored = AdminPassword.Hash("여기는비밀번호");

        Assert.StartsWith("pbkdf2$", stored);
        Assert.DoesNotContain("여기는비밀번호", stored);   // 평문이 남지 않는다
        Assert.True(AdminPassword.Verify("여기는비밀번호", stored));
        Assert.False(AdminPassword.Verify("여기는비밀번회", stored));
    }

    [Fact]
    public void 소금이_매번_달라_같은_비밀번호도_다른_해시가_된다()
    {
        Assert.NotEqual(AdminPassword.Hash("같은값"), AdminPassword.Hash("같은값"));
    }

    [Fact]
    public void 평문_설정도_받아_주되_그대로_비교한다()
    {
        // 개인 서버의 편의 — 해시를 만들기 귀찮을 때. 문서에서는 해시를 권한다.
        Assert.True(AdminPassword.Verify("평문암호", "평문암호"));
        Assert.False(AdminPassword.Verify("다른암호", "평문암호"));
    }

    [Fact]
    public void 비밀번호를_안_정했으면_무엇을_넣어도_통과하지_못한다()
    {
        // 설정이 비었을 때 빈 비밀번호로 들어가지는 사고를 막는다.
        Assert.False(AdminPassword.Verify("", null));
        Assert.False(AdminPassword.Verify("아무거나", null));
        Assert.False(AdminPassword.Verify("아무거나", "   "));
        Assert.False(AdminPassword.Verify("", ""));
    }

    [Fact]
    public void 해시가_깨져_있으면_통과시키지_않는다()
    {
        Assert.False(AdminPassword.Verify("x", "pbkdf2$210000$짧은소금"));
        Assert.False(AdminPassword.Verify("x", "pbkdf2$abc$c2FsdA==$aGFzaA=="));
        Assert.False(AdminPassword.Verify("x", "pbkdf2$210000$!!!$!!!"));
    }

    // ---- 대시보드 구성 ----

    [Fact]
    public void 대시보드는_최근_올라온_가사를_맨_위에_둔다()
    {
        // 가사 서버의 정체성은 "무슨 가사가 들어와 있는가"다 — 조회 통계보다 앞에 온다.
        var html = AdminPages.Dashboard(EmptyDashboard(), DateTimeOffset.UtcNow, Kst);

        var uploads = html.IndexOf("최근 올라온 가사", StringComparison.Ordinal);
        var lookups = html.IndexOf("최근 조회", StringComparison.Ordinal);

        Assert.True(uploads > 0 && lookups > 0);
        Assert.True(uploads < lookups, "최근 올라온 가사가 최근 조회보다 위여야 한다");
    }

    [Fact]
    public void 각_섹션에_전체_보기_링크가_있다()
    {
        var html = AdminPages.Dashboard(EmptyDashboard(), DateTimeOffset.UtcNow, Kst);

        Assert.Contains($"{Routes.Base}/search\">전체 보기", html);   // 최근 올라온 가사 = 질의 없는 검색 화면
        foreach (var view in AdminPages.ListViews.Keys)
            Assert.Contains($"{Routes.Base}/list?view={view}", html);
    }

    [Fact]
    public void 켜져_있는_의미_자료원을_화면에_보여_준다()
    {
        // 무엇에 근거해 만들어지는지가 설정에만 있으면 나중에 아무도 모른다.
        var model = EmptyDashboard() with { MeaningSources = ["Genius", "Musixmatch (AI 분석)"] };

        var html = AdminPages.Dashboard(model, DateTimeOffset.UtcNow, Kst);

        Assert.Contains("의미 자료:", html);
        Assert.Contains("Musixmatch (AI 분석)", html);
    }

    // ---- 미스 행에서 곡으로 ----

    [Fact]
    public void 미스여도_지금_서버에_있으면_가사로_가는_링크가_생긴다()
    {
        var model = EmptyDashboard() with
        {
            TopMisses = [new MissRow("Kids", "MGMT", 3, "2026-08-01T00:00:00Z", 2, "kids|mgmt")],
        };

        var html = AdminPages.Dashboard(model, DateTimeOffset.UtcNow, Kst);

        Assert.Contains($"{Routes.Base}/song?key=kids%7Cmgmt", html);
        Assert.Contains("가사 보기", html);
    }

    [Fact]
    public void 정말_없는_곡은_검색으로만_보낸다()
    {
        var model = EmptyDashboard() with
        {
            TopMisses = [new MissRow("Kids", "MGMT", 3, "2026-08-01T00:00:00Z", 2)],
        };

        var html = AdminPages.Dashboard(model, DateTimeOffset.UtcNow, Kst);

        Assert.DoesNotContain("가사 보기", html);
        Assert.Contains($"{Routes.Base}/search?q=Kids", html);
    }

    // ---- 검색 화면의 의미 필터 ----

    [Fact]
    public void 검색_결과에_의미_열이_있다()
    {
        var withMeaning = Song("Kids", MeaningEntry.StatusOk);
        var without = Song("Go!", null);

        var html = AdminPages.SearchPage(null, [withMeaning, without], Kst);

        Assert.Contains("<th>의미</th>", html);
        Assert.Contains("있음", html);
    }

    [Fact]
    public void 고른_필터_칩이_켜진_채로_남는다()
    {
        var counts = new SongCounts(All: 10, Ok: 4, Pending: 3, Insufficient: 2, NoSource: 1, Failed: 0, Loved: 5);
        var html = AdminPages.SearchPage(null, [], Kst, LyricsStore.MeaningFilterOk, counts, page: 1, total: 4);

        Assert.Contains($"class=\"chip on\" href=\"{Routes.Base}/search?q=&meaning=ok\"", html);
        Assert.Contains("의미 있음 <b>4</b>", html);
    }

    [Fact]
    public void 칩은_각_필터의_건수를_보여_준다()
    {
        var counts = new SongCounts(All: 10, Ok: 4, Pending: 3, Insufficient: 2, NoSource: 1, Failed: 0, Loved: 5);
        var html = AdminPages.SearchPage(null, [], Kst, null, counts, page: 1, total: 10);

        Assert.Contains("전체 <b>10</b>", html);
        Assert.Contains("아직 안 만듦 <b>3</b>", html);
        Assert.Contains("자료 부족 <b>2</b>", html);
        Assert.Contains("♥ 즐겨찾기 <b>5</b>", html);

        // 0건인 칩은 그리지 않는다 — 누를 이유가 없는 칸이 있는 것들을 묻는다.
        Assert.DoesNotContain("생성 실패", html);

        // 드롭다운은 사라졌다.
        Assert.DoesNotContain("<select name=\"meaning\"", html);
    }

    [Fact]
    public void 건수를_모르면_칩을_그리지_않는다()
    {
        // 곡을 못 찾아 검색 화면으로 되돌리는 경로는 건수를 세지 않고 부른다.
        var html = AdminPages.SearchPage("kids", [], Kst);

        Assert.DoesNotContain("class=\"chip", html);
    }

    [Fact]
    public void 여러_쪽이면_이전_다음이_생긴다()
    {
        var counts = new SongCounts(All: 100, Ok: 0, Pending: 100, Insufficient: 0, NoSource: 0, Failed: 0, Loved: 0);
        var rows = Enumerable.Range(0, AdminPages.PageSize).Select(i => Song($"곡{i}", null)).ToList();
        var html = AdminPages.SearchPage(null, rows, Kst, null, counts, page: 2, total: 100);

        Assert.Contains("31–60 · 전체 100건", html);
        Assert.Contains($"{Routes.Base}/search?q=&meaning=&page=1", html);
        Assert.Contains($"{Routes.Base}/search?q=&meaning=&page=3", html);
        Assert.Contains("2 / 4", html);
    }

    // ---- 로그아웃 위치 ----

    [Fact]
    public void 로그아웃은_네비의_맨_끝에_따로_있다()
    {
        // 가운데 있으면 잘못 누른다 — `out` 클래스가 오른쪽 끝으로 미는 CSS와 짝이다.
        var html = AdminPages.Dashboard(EmptyDashboard(), DateTimeOffset.UtcNow, Kst);

        var search = html.IndexOf($"{Routes.Base}/search\"", StringComparison.Ordinal);
        var logout = html.IndexOf($"{Routes.Base}/logout", StringComparison.Ordinal);

        Assert.True(search > 0 && logout > search, "로그아웃이 가사 검색보다 뒤여야 한다");
        Assert.Contains($"<a href=\"{Routes.Base}/logout\" class=\"out\">", html);
        Assert.Contains("nav a.out{margin-left:auto", html);
    }

    // ---- Last.fm 콜백 출처 ----

    /// <summary>
    /// 실제로 깨졌던 지점이다. `tailscale serve`가 HTTPS를 종단하고 앱에는 평문으로 넘기므로
    /// <c>req.Scheme</c>이 <c>http</c>다 — 그대로 콜백을 만들면 승인 후 돌아올 때
    /// 80번 포트에 아무도 없어 "서버에 연결할 수 없습니다"가 뜬다.
    /// </summary>
    [Fact]
    public void 프록시_뒤에서는_콜백이_https여야_한다()
    {
        Assert.Equal("https://oracle.oryx-skink.ts.net",
            AdminEndpoints.CallbackOrigin("http", "oracle.oryx-skink.ts.net", "oracle.oryx-skink.ts.net", null));
    }

    [Fact]
    public void 프록시가_알려_준_프로토콜을_먼저_믿는다()
    {
        Assert.Equal("https://box.ts.net",
            AdminEndpoints.CallbackOrigin("http", "box.ts.net", "box.ts.net", "https"));

        // 프록시가 여럿이면 쉼표로 이어 붙는다 — 맨 앞이 원래 클라이언트 쪽이다.
        Assert.Equal("https://box.ts.net",
            AdminEndpoints.CallbackOrigin("http", "box.ts.net", "box.ts.net", "https, http"));
    }

    [Fact]
    public void 로컬_개발만_http로_남는다()
    {
        Assert.Equal("http://127.0.0.1:5199",
            AdminEndpoints.CallbackOrigin("http", "127.0.0.1", "127.0.0.1:5199", null));
        Assert.Equal("http://localhost:5199",
            AdminEndpoints.CallbackOrigin("http", "localhost", "localhost:5199", ""));
    }

    // ---- 곡 상세: 외부 링크 · 커버 · 좋아요 ----

    [Fact]
    public void 곡_상세에_외부_링크_다섯_개가_있다()
    {
        var html = SongPage();

        foreach (var name in new[] { "Last.fm", "Tunefind", "YouTube", "Musixmatch", "Genius" })
            Assert.Contains($">{name}</a>", html);

        // 새 창으로 나가되 원본 탭을 넘겨주지 않는다.
        Assert.Contains("rel=\"noopener noreferrer\"", html);
    }

    [Fact]
    public void 커버가_없으면_이미지를_그리지_않는다()
    {
        // 깨진 이미지 아이콘이 빈자리보다 나쁘다.
        Assert.DoesNotContain("<img class=\"cover\"", SongPage());

        var withCover = SongPage(links: new SongLinks("k", "https://is1-ssl.mzstatic.com/a/600x600bb.jpg",
            CoverArt.ITunes, "2026-08-17T00:00:00Z"));
        Assert.Contains("<img class=\"cover\"", withCover);
        Assert.Contains("alt=\"Kids 커버\"", withCover);
    }

    [Fact]
    public void 계정을_연결하지_않으면_좋아요_토글이_없다()
    {
        Assert.DoesNotContain($"{Routes.Base}/song/love", SongPage());
    }

    /// <summary>
    /// 예전에는 삭제·광고·커버가 가사 편집창 <b>아래</b>에 있어, 뭘 하려면 긴 가사를 지나
    /// 스크롤해야 했다. 이제 곡 머리말 옆 한 줄에 모인다 — 편집 영역보다 앞이어야 한다.
    /// </summary>
    [Fact]
    public void 기능_버튼은_가사_편집창보다_위에_모여_있다()
    {
        var html = SongPage();

        var actions = html.IndexOf("class=\"actions\"", StringComparison.Ordinal);
        var edit = html.IndexOf($"{Routes.Base}/song/edit", StringComparison.Ordinal);

        Assert.True(actions > 0 && edit > 0);
        Assert.True(actions < edit, "기능 버튼이 편집 폼보다 위에 있어야 한다");

        foreach (var action in new[] { $"{Routes.Base}/song/cover", $"{Routes.Base}/song/ad", $"{Routes.Base}/song/delete" })
            Assert.True(html.IndexOf(action, StringComparison.Ordinal) is var i && i > actions && i < edit,
                $"{action}이 기능 줄 안에 있어야 한다");
    }

    [Fact]
    public void 되돌릴_수_없는_것만_danger로_칠한다()
    {
        // 버튼 넉 줄을 같은 색으로 늘어놓으면 무엇이 위험한지 구분이 안 된다.
        var html = SongPage(love: new LoveState(true, true, false));

        Assert.Contains("<button class=\"danger\" type=\"submit\">광고로 표시</button>", html);
        Assert.Contains("<button class=\"danger\" type=\"submit\">이 곡 삭제</button>", html);
        Assert.Contains("<button type=\"submit\">커버 다시 찾기</button>", html);
    }

    [Fact]
    public void 좋아요를_켠_곡은_끄는_쪽으로_보여_준다()
    {
        var html = SongPage(love: new LoveState(Connected: true, Known: true, Loved: true));

        Assert.Contains($"{Routes.Base}/song/love", html);
        Assert.Contains("♥ 좋아요 해제", html);
        Assert.Contains("name=\"on\" value=\"0\"", html);
        Assert.Contains("data-busy", html);   // 스피너 + 히스토리 안 늘리기가 따라온다
    }

    /// <summary>
    /// 조회에 실패한 것과 "좋아요 안 함"은 다르다. 꺼진 하트로 그리면 이미 켜 둔 곡을 끄게 된다.
    /// </summary>
    [Fact]
    public void 상태를_모르면_좋아요_안_함으로_그리지_않는다()
    {
        var html = SongPage(love: new LoveState(Connected: true, Known: false, Loved: false));

        Assert.Contains("확인하지 못했습니다", html);
        Assert.DoesNotContain("♥ 좋아요 해제", html);
    }

    [Fact]
    public void 정식_주소를_알면_LastFm_링크가_그것으로_간다()
    {
        var html = SongPage(links: new SongLinks("k", LastFmUrl: "https://www.last.fm/music/MGMT/_/Kids+"));

        Assert.Contains("https://www.last.fm/music/MGMT/_/Kids+", html);
    }

    // ---- 대시보드: Last.fm 연결 ----

    [Fact]
    public void 연결할_수_없는_구성이면_LastFm_카드를_숨긴다()
    {
        // 눌러도 안 되는 것을 보여 주면 사람을 헷갈리게 한다(secret이 없는 상태).
        Assert.DoesNotContain($"{Routes.Base}/lastfm/connect",
            AdminPages.Dashboard(EmptyDashboard(), DateTimeOffset.UtcNow, Kst));
    }

    [Fact]
    public void 연결_전에는_연결_링크를_연결_후에는_아이디를_보여_준다()
    {
        var before = AdminPages.Dashboard(
            EmptyDashboard() with { LastFm = new LastFmLink(null) }, DateTimeOffset.UtcNow, Kst);
        Assert.Contains($"<a href=\"{Routes.Base}/lastfm/connect\">", before);

        var after = AdminPages.Dashboard(
            EmptyDashboard() with { LastFm = new LastFmLink("jay") }, DateTimeOffset.UtcNow, Kst);
        Assert.Contains("연결됨: <b>jay</b>", after);
        Assert.Contains($"{Routes.Base}/lastfm/disconnect", after);
    }


    // ---- 되돌릴 수 없는 동작은 먼저 묻는다 ----

    /// <summary>
    /// 예전에는 [이 곡 삭제]·[광고로 표시]가 <b>한 번의 클릭으로</b> 실행됐다(소스 전체에 confirm이 0건).
    /// 스크립트는 하나뿐이라는 규칙을 지키려 기존 제출 핸들러에 붙였다 — CSP 해시가 자동으로 따라온다.
    /// </summary>
    [Fact]
    public void 삭제와_광고_표시는_확인을_먼저_받는다()
    {
        var html = SongPage();

        var delete = html.IndexOf($"{Routes.Base}/song/delete", StringComparison.Ordinal);
        var ad = html.IndexOf($"{Routes.Base}/song/ad", StringComparison.Ordinal);
        var cover = html.IndexOf($"{Routes.Base}/song/cover", StringComparison.Ordinal);

        Assert.Contains("data-confirm", html[ad..(ad + 200)]);
        Assert.Contains("data-confirm", html[delete..(delete + 200)]);

        // 되돌릴 수 있는 것까지 물으면 확인창이 의미를 잃는다.
        Assert.DoesNotContain("data-confirm", html[cover..(cover + 200)]);
    }

    [Fact]
    public void 확인_스크립트는_취소하면_제출을_막는다()
    {
        // 스크립트는 여전히 하나뿐이고, CSP는 그 해시만 허용한다(두 규칙 다 이미 테스트가 있다).
        Assert.Contains("data-confirm", AdminHtml.BusyScript);
        Assert.Contains("preventDefault", AdminHtml.BusyScript);
    }

    [Fact]
    public void 좋아요_버튼도_옆_버튼과_같은_높이다()
    {
        // .actions가 flex(stretch)라 margin이 붙은 폼만 눌려 작아 보였다.
        var html = SongPage(love: new LoveState(true, true, false));
        var love = html.IndexOf($"{Routes.Base}/song/love", StringComparison.Ordinal);

        Assert.Contains("style=\"margin:0\"", html[love..(love + 120)]);
    }

    // ---- 목록 ----

    [Fact]
    public void 아티스트를_누르면_그_아티스트로_검색한다()
    {
        var html = AdminPages.SearchPage(null, [Song("Kids", "ok")], Kst);

        Assert.Contains($"<a href=\"{Routes.Base}/search?q=%EC%95%84%ED%8B%B0%EC%8A%A4%ED%8A%B8\">아티스트</a>", html);
    }

    [Fact]
    public void 기기_이름은_줄바꿈되지_않는다()
    {
        // IP(100.x.y.z)나 긴 이름이 두 줄로 접혀 표가 들쭉날쭉했다.
        var html = AdminPages.SearchPage(null, [Song("Kids", "ok")], Kst);

        var device = html.IndexOf("거실PC", StringComparison.Ordinal);
        Assert.Contains("nowrap", html[(device - 40)..device]);
    }

    // ---- 의미 생성 엔진 카드 ----

    [Fact]
    public void 지금_쓰는_모델을_대시보드에_보여_준다()
    {
        var card = new MeaningEngineCard(
            "openrouter", "anthropic/claude-opus-5", HasKey: true, Overridden: true,
            GeminiKeyHint: null, GeminiModel: null,
            OpenRouterKeyHint: "…cdef", OpenRouterModel: "anthropic/claude-opus-5");
        var html = AdminPages.Dashboard(
            EmptyDashboard() with { MeaningEngine = card }, DateTimeOffset.UtcNow, Kst);

        Assert.Contains("anthropic/claude-opus-5", html);
        Assert.Contains($"{Routes.Base}/meanings/engine", html);
        Assert.Contains("환경변수로 되돌리기", html);
    }

    [Fact]
    public void 키가_없으면_엔진을_골라도_꺼져_있다고_밝힌다()
    {
        var card = new MeaningEngineCard(
            "openrouter", "google/gemini-2.5-flash", HasKey: false, Overridden: false,
            null, null, null, null);
        var html = AdminPages.Dashboard(
            EmptyDashboard() with { MeaningEngine = card }, DateTimeOffset.UtcNow, Kst);

        Assert.Contains("API 키가 없어 꺼져 있습니다", html);
        Assert.DoesNotContain("환경변수로 되돌리기", html);   // 저장한 게 없으면 되돌릴 것도 없다
    }

    [Fact]
    public void API_키는_끝_네_글자만_보여_준다()
    {
        Assert.Equal("…cdef", MeaningEngineCard.Hint("sk-or-v1-0123456789abcdef"));
        Assert.Null(MeaningEngineCard.Hint(""));
        Assert.Null(MeaningEngineCard.Hint(null));
    }

    private static string SongPage(SongLinks? links = null, LoveState? love = null) =>
        AdminPages.SongPage(
            new LyricsEntry { Key = "kids|mgmt", Title = "Kids", Artist = "MGMT", Lrc = "[00:01.00]hello" },
            [], [], null, showTags: false, "csrf-token", Kst,
            links: links, love: love);

    private static SongRow Song(string title, string? meaning) =>
        new("k-" + title, "k", title, "아티스트", "LRCLIB", "provider",
            ["ko"], 10, false, 1, "2026-07-29T00:00:00Z", "거실PC", meaning);

    private static DashboardModel EmptyDashboard() => new(
        new ServerStats(0, 0, null), 0, new HitRate(0, 0, 0), new HitRate(0, 0, 0),
        [], [], [], [], [], [], [], [],
        new ServerHealth(TimeSpan.FromHours(1), 0, 0, 90), [],
        new MeaningSummary(0, 0, 0, 0, false), [], "csrf-token");
}
