using System.Security.Cryptography;
using System.Text;

namespace Musebase.Server;

/// <summary>
/// 관리자 페이지 HTML 조각(순수 함수 — DB도 HTTP도 모른다).
/// 텔레메트리 Worker의 관리자 리포트(`backend/telemetry/src/worker.js`)와 같은 문제·같은 모양이라
/// 이스케이프 규칙과 다크 표 CSS를 그대로 이식했다.
///
/// JS는 <see cref="BusyScript"/> 하나뿐이고, 그 대가로 CSP를 느슨하게 하는 대신
/// **해시로 고정**한다(<see cref="ScriptCsp"/>) — 다른 스크립트는 여전히 실행되지 않는다.
/// </summary>
public static class AdminHtml
{
    /// <summary>
    /// HTML 이스케이프. 곡명·아티스트·가사는 전부 외부에서 들어온 문자열이라
    /// **삽입 지점 예외 없이** 이걸 통과해야 한다.
    /// </summary>
    public static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s!.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>링크에 실을 쿼리 값(키 등) 인코딩.</summary>
    public static string Url(string? s) => Uri.EscapeDataString(s ?? "");

    /// <summary>
    /// 이 페이지들의 <b>유일한</b> 스크립트. 두 가지를 한다.
    ///
    /// ① <b>스피너.</b> 의미 생성은 외부 API를 여러 번 부르므로 수 초가 걸리는데, 눌러도 아무 반응이
    ///    없으면 사람이 다시 누른다(그러면 같은 곡을 두 번 만든다).
    /// ② <b>히스토리를 늘리지 않는다.</b> 평범한 폼 제출은 [검색 → 곡 → 곡(생성 후)] 세 칸을 만들어,
    ///    뒤로 가기가 <b>생성 전의 같은 곡</b>으로 간다. 사람이 원하는 곳은 그 곡에 들어오기 전 화면이다.
    ///    그래서 <c>fetch</c>로 보내고(제출 자체가 히스토리를 만들지 않는다) 결과 주소로
    ///    <c>location.replace</c>한다 — 지금 칸을 덮어써서 [검색 → 곡(생성 후)]만 남는다.
    ///    서버가 할 수 없는 일이라(HTTP에는 히스토리를 지우는 방법이 없다) 여기서 한다.
    ///
    /// CSP는 계속 잠가 둔다 — <c>'unsafe-inline'</c>이 아니라 <b>이 문자열의 해시</b>만 허용하므로
    /// 다른 스크립트는 여전히 한 줄도 실행되지 않는다(<see cref="ScriptCsp"/>).
    ///
    /// <c>fetch</c>가 없으면 평소대로 제출한다(그때는 버튼 잠금을 <c>setTimeout</c>으로 미룬다 —
    /// 제출 전에 끄면 폼이 전송되지 않는 브라우저가 있다). 리다이렉트가 아니면(예: CSRF 실패)
    /// 같은 자리를 다시 읽어 실제 상태를 보여 준다.
    /// </summary>
    public const string BusyScript =
        "document.addEventListener('submit',function(e){" +
        "var f=e.target;if(!f.hasAttribute('data-busy'))return;" +
        "var b=f.querySelector('button[type=submit]');" +
        "var busy=function(){if(b){b.disabled=true;b.classList.add('busy');}};" +
        "if(!window.fetch||!window.FormData){setTimeout(busy,0);return;}" +
        "e.preventDefault();busy();" +
        "fetch(f.action,{method:'POST',body:new FormData(f),credentials:'same-origin'})" +
        ".then(function(r){location.replace(r.redirected?r.url:location.href);})" +
        ".catch(function(){f.submit();});},true);";

    /// <summary>`script-src`에 넣을 해시 토큰. 스크립트를 고치면 자동으로 따라간다.</summary>
    public static string ScriptCsp { get; } =
        $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(BusyScript)))}'";

    /// <summary>공통 레이아웃 — 다크 표 스타일 + 상단 네비게이션.</summary>
    public static string Layout(string title, string body, string? activeNav = null)
    {
        string Nav(string href, string label, string id, string? extra = null)
        {
            var classes = string.Join(" ", new[] { activeNav == id ? "on" : null, extra }.Where(c => c is not null));
            return $"<a href=\"{href}\"{(classes.Length == 0 ? "" : $" class=\"{classes}\"")}>{Esc(label)}</a>";
        }

        // CSS에 중괄호가 많아 $$(이중 보간) 원시 문자열을 쓴다 — 보간은 {{…}}, CSS 중괄호는 그대로.
        return $$"""
            <!doctype html>
            <html lang="ko">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="robots" content="noindex">
            <title>{{Esc(title)}} — Musebase 가사 서버</title>
            <style>
            :root{--bg:#111;--panel:#1c1c1c;--line:#333;--text:#eee;--dim:#888;--accent:#7cc4ff;
                   --ok:#7bd88f;--warn:#ffd166;--bad:#ff6b6b}
            *{box-sizing:border-box}
            body{font-family:"Segoe UI","Malgun Gothic","Noto Sans KR","Yu Gothic UI",system-ui,sans-serif;
                 margin:0 auto;max-width:72rem;padding:1.5rem 1rem 4rem;background:var(--bg);color:var(--text)}
            h1{font-size:1.25rem;margin:0 0 .25rem}
            h2{font-size:1.02rem;margin:2rem 0 .5rem;border-bottom:1px solid var(--line);padding-bottom:.3rem}
            a{color:var(--accent);text-decoration:none} a:hover{text-decoration:underline}
            nav{display:flex;gap:1rem;margin:.75rem 0 1.25rem;font-size:.9rem;
                 border-bottom:1px solid var(--line);padding-bottom:.6rem}
            nav a.on{color:var(--text);font-weight:600}
            /* 로그아웃은 오른쪽 끝으로 밀고 흐리게 둔다 — 가운데 있으면 잘못 누른다. */
            nav a.out{margin-left:auto;color:var(--dim)} nav a.out:hover{color:var(--bad)}
            table{border-collapse:collapse;width:100%;font-size:.85rem}
            th,td{border:1px solid var(--line);padding:.35rem .5rem;text-align:left;vertical-align:top}
            th{background:var(--panel)} tr:nth-child(even) td{background:#181818}
            .empty{color:var(--dim);text-align:center}
            .meta{color:var(--dim);font-size:.8rem}
            .tiles{display:flex;flex-wrap:wrap;gap:.75rem;margin:.5rem 0 1rem}
            .tile{flex:1 1 12rem;background:var(--panel);border:1px solid var(--line);border-radius:.5rem;padding:.7rem .9rem}
            .tile .k{color:var(--dim);font-size:.75rem}
            .tile .v{font-size:1.35rem;font-weight:600;margin-top:.15rem}
            .tile .s{color:var(--dim);font-size:.75rem;margin-top:.15rem}
            .ok{color:var(--ok)} .warn{color:var(--warn)} .bad{color:var(--bad)}
            .bar{background:#222;height:.55rem;border-radius:.3rem;overflow:hidden;min-width:6rem}
            .bar>span{display:block;height:100%;background:var(--accent)}
            form.inline{display:flex;gap:.5rem;margin:.5rem 0 1rem;flex-wrap:wrap}
            input[type=text],input[type=password],textarea,select{background:#0e0e0e;color:var(--text);
                 border:1px solid var(--line);border-radius:.35rem;padding:.4rem .55rem;font:inherit}
            input[type=text]{min-width:18rem}
            textarea{width:100%;min-height:22rem;font-family:ui-monospace,Consolas,monospace;font-size:.82rem}
            button{background:#243447;color:var(--text);border:1px solid var(--line);border-radius:.35rem;
                    padding:.4rem .8rem;font:inherit;cursor:pointer}
            button:hover{background:#2d4258} button.danger{background:#4a2020} button.danger:hover{background:#5e2727}
            button[disabled]{opacity:.6;cursor:default}
            button.busy::before{content:"";display:inline-block;width:.8em;height:.8em;
                 margin-right:.45em;vertical-align:-.08em;border:2px solid currentColor;
                 border-right-color:transparent;border-radius:50%;animation:spin .7s linear infinite}
            @keyframes spin{to{transform:rotate(360deg)} }
            @media (prefers-reduced-motion:reduce){button.busy::before{animation-duration:2.5s} }
            .song{display:flex;gap:1rem;align-items:flex-start;margin-top:1.5rem}
            .song>div{min-width:0} .song h2{margin-top:0}
            .cover{width:6rem;height:6rem;border-radius:.4rem;border:1px solid var(--line);
                   object-fit:cover;flex:0 0 auto;background:var(--panel)}
            button.love{background:#3a2330} button.love:hover{background:#4c2c3e}
            button.love.on{color:#ff8fb1}
            .srcpick{display:inline-flex;flex-wrap:wrap;gap:.15rem .8rem;align-items:center}
            .srcpick label{color:var(--dim);font-size:.8rem;white-space:nowrap}
            details{margin-top:2rem} summary{cursor:pointer;color:var(--dim)}
            pre{background:var(--panel);border:1px solid var(--line);border-radius:.4rem;padding:.75rem;
                 overflow:auto;font-size:.8rem;white-space:pre-wrap}
            .nowrap{white-space:nowrap}
            </style>
            </head>
            <body>
            <h1>Musebase 가사 서버</h1>
            <nav>
              {{Nav("/admin", "대시보드", "home")}}
              {{Nav("/admin/search", "가사 검색", "search")}}
              {{Nav("/admin/logout", "로그아웃", "logout", "out")}}
            </nav>
            {{body}}
            <script>{{BusyScript}}</script>
            </body>
            </html>
            """;
    }

    /// <summary>표 하나. 행이 없으면 안내행을 넣는다(Worker 관리자 페이지 관례).</summary>
    public static string Table(IReadOnlyList<string> headers, IEnumerable<string> rows, string emptyText = "아직 데이터 없음")
    {
        var body = string.Join("", rows);
        if (body.Length == 0)
            body = $"<tr><td colspan=\"{headers.Count}\" class=\"empty\">{Esc(emptyText)}</td></tr>";
        var head = string.Join("", headers.Select(h => $"<th>{Esc(h)}</th>"));
        return $"<table><thead><tr>{head}</tr></thead><tbody>{body}</tbody></table>";
    }

    /// <summary>요약 타일.</summary>
    public static string Tile(string label, string value, string? sub = null) =>
        $"""<div class="tile"><div class="k">{Esc(label)}</div><div class="v">{Esc(value)}</div>""" +
        (sub is null ? "" : $"""<div class="s">{Esc(sub)}</div>""") + "</div>";

    /// <summary>바이트 → 사람이 읽는 크기.</summary>
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0}GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0}MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.0}KB",
        _ => $"{bytes}B",
    };
}
