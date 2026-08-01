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
        Assert.DoesNotContain("<script", html);   // JS를 쓰지 않는다(CSP를 강하게 잠글 수 있는 근거)
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
        Assert.DoesNotContain("/admin/meanings/backfill", html);
    }

    [Fact]
    public void 처리할_곡이_있으면_일괄_생성_버튼에_곡_수가_보인다()
    {
        var model = EmptyDashboard() with
        {
            Meanings = new MeaningSummary(5, 1, 0, Pending: 30, Enabled: true),
        };

        var html = AdminPages.Dashboard(model, DateTimeOffset.UtcNow, Kst);

        Assert.Contains("/admin/meanings/backfill", html);
        Assert.Contains("의미 일괄 생성 (30곡)", html);
    }

    [Fact]
    public void 처리할_곡이_없으면_버튼을_숨긴다()
    {
        var model = EmptyDashboard() with
        {
            Meanings = new MeaningSummary(5, 1, 0, Pending: 0, Enabled: true),
        };

        Assert.DoesNotContain("/admin/meanings/backfill",
            AdminPages.Dashboard(model, DateTimeOffset.UtcNow, Kst));
    }

    private static DashboardModel EmptyDashboard() => new(
        new ServerStats(0, 0, null), 0, new HitRate(0, 0, 0), new HitRate(0, 0, 0),
        [], [], [], [], [], [], [], [],
        new ServerHealth(TimeSpan.FromHours(1), 0, 0, 90), [],
        new MeaningSummary(0, 0, 0, 0, false), "csrf-token");
}
