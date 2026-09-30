using Musebase.Core.Meaning;
using static Musebase.Server.AdminHtml;

namespace Musebase.Server;

public static partial class AdminPages
{
    /// <summary>엔진마다 고를 때 알아야 할 한 줄.</summary>
    private static readonly Dictionary<string, string> MeaningEngineNotes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gemini"] = "결제 없는 프로젝트의 키면 무료 티어(분·일 한도에서 429) · 결제가 붙으면 알리지 않고 과금 — ④의 월 상한으로 막습니다",
        ["openrouter"] = "토큰 과금 · 권장 모델 google/gemini-2.5-flash-lite(곡당 1원 미만)",
    };

    /// <summary>
    /// 의미 생성 엔진 카드 — 번역 카드와 같은 모양으로 <b>쓰는 순서대로</b> 나눈다:
    /// ① 주 엔진 → ② 키 → ③ 폴백 → ④ 그 밖의 설정. 키 칸은 고른 엔진과 체크한 폴백의 것만 보이고
    /// (CSS <c>:has()</c> — CSP 때문에 스크립트를 쓰지 않는다), 칸 옆에 마지막 확인 결과가 붙는다.
    ///
    /// "무료인 동안 Gemini, 모자라면 OpenRouter"가 기본 그림이다 — Gemini가 402·429를 주면 그 곡부터
    /// 곧바로 OpenRouter가 받고, Gemini는 잠시 쉰다(쉬는 이유가 카드에 보인다).
    /// </summary>
    private static string MeaningEngineCardHtml(MeaningEngineCard? card, string csrf)
    {
        if (card is null) return "";

        var checks = card.Checks ?? new Dictionary<string, EngineCheck>();
        var chain = card.Chain ?? [];
        var used = card.Used ?? new Dictionary<string, long>();
        var paused = card.Paused ?? new Dictionary<string, string>();

        var summary = string.Equals(card.Engine, MeaningWriterRegistry.None, StringComparison.OrdinalIgnoreCase)
            ? "<b class=\"warn\">꺼짐</b> — ①에서 엔진을 고르고 ②에 그 엔진의 키를 넣으세요."
            : !card.HasKey
                ? $"<b class=\"warn\">{Esc(card.Engine)}를 골랐지만 API 키가 없어 꺼져 있습니다</b> — ②에 넣으세요."
                : $"<b class=\"ok\">동작 중</b> · {Esc(string.Join(" → ", chain.Count > 0 ? chain : [card.Engine]))}"
                  + $" · 주 모델 <code>{Esc(card.Model)}</code>";

        // ---- ① 주 엔진 ----
        var engineIds = MeaningWriterRegistry.All.Select(d => d.Id).Append(MeaningWriterRegistry.None).ToList();
        var engines = string.Concat(engineIds.Select(id =>
        {
            var name = MeaningWriterRegistry.Find(id)?.Display ?? "끔";
            var note = id == MeaningWriterRegistry.None
                ? "의미를 만들지 않습니다(관리 화면에는 외부 링크만 뜹니다)"
                : MeaningEngineNotes.GetValueOrDefault(id, "");
            var on = string.Equals(id, card.Engine, StringComparison.OrdinalIgnoreCase) ? " checked" : "";
            return $"<label><input type=\"radio\" name=\"engine\" value=\"{Esc(id)}\"{on}>"
                 + $"<span><b>{Esc(name)}</b><br><span class=\"meta\">{Esc(note)}</span></span></label>";
        }));

        // ---- ② 키 ----
        string KeyInput(string name, string label, string format, string? hint) =>
            $"<label class=\"meta\">{Esc(label)}<br><input type=\"password\" name=\"{name}\" autocomplete=\"off\" "
            + $"placeholder=\"{Esc(hint is null ? format : $"넣어 둔 키 {hint} — 비워 두면 유지")}\"></label>";

        string TextInput(string name, string label, string? value, string placeholder) =>
            $"<label class=\"meta\">{Esc(label)}<br><input type=\"text\" name=\"{name}\" "
            + $"value=\"{Esc(value ?? "")}\" placeholder=\"{Esc(placeholder)}\"></label>";

        string Chip(string engine)
        {
            var pause = paused.TryGetValue(engine, out var why)
                ? $"<br><span class=\"warn\">쉬는 중 — {Esc(why)}</span>" : "";
            if (!checks.TryGetValue(engine, out var c)) return "<span class=\"chip\">확인 안 함</span>" + pause;
            var when = c.At.ToString("MM-dd HH:mm") + " UTC" + (c.Saved ? "" : " · 저장 전 테스트");
            return (c.Ok
                ? $"<span class=\"chip okc\">✓ 정상 · {c.Millis}ms</span><br><span class=\"meta\">{Esc(when)}"
                  + (c.Detail is { } d ? $" · {Esc(d)}" : "") + "</span>"
                : $"<span class=\"chip badc\">✗ 실패</span><br><span class=\"meta\">{Esc(c.Detail ?? "")}"
                  + $" · {Esc(when)}</span>") + pause;
        }

        (string Engine, string Name, string Inputs)[] keyRows =
        [
            ("gemini", "Gemini", KeyInput("geminiKey", "Gemini API 키", "AIza…(39자) 또는 AQ.…", card.GeminiKeyHint)
                + TextInput("geminiModel", "모델", card.GeminiModel, GeminiMeaningWriter.DefaultModel)),
            ("openrouter", "OpenRouter", KeyInput("openRouterKey", "OpenRouter 키", "sk-or-로 시작", card.OpenRouterKeyHint)
                + TextInput("openRouterModel", "모델", card.OpenRouterModel, "google/gemini-2.5-flash-lite")),
        ];
        var keys = string.Concat(keyRows.Select(r =>
            $"<div class=\"keyrow k-{r.Engine}\"><div class=\"kname\">{Esc(r.Name)}</div>"
            + $"<div class=\"kin\">{r.Inputs}</div><div class=\"kchk\">{Chip(r.Engine)}</div></div>"));

        // ---- ③ 폴백 — 주 엔진과 같은 것은 CSS가 숨긴다 ----
        var fallback = "<div class=\"srcpick\">" + string.Concat(MeaningWriterRegistry.All.Select(d =>
                $"<label class=\"fb-{Esc(d.Id)}\"><input type=\"checkbox\" name=\"fallback\" value=\"{Esc(d.Id)}\""
                + (card.FallbackOn.Contains(d.Id, StringComparer.OrdinalIgnoreCase) ? " checked" : "")
                + $"> {Esc(d.Display)}</label>"))
            + "</div>";

        var reveal = string.Join("\n", MeaningWriterRegistry.All.Select(d =>
            $".mcard:has(input[name=engine][value={d.Id}]:checked) .k-{d.Id},"
            + $".mcard:has(input[name=fallback][value={d.Id}]:checked) .k-{d.Id}{{display:grid}}"
            + $".mcard:has(input[name=engine][value={d.Id}]:checked) .fb-{d.Id}{{display:none}}"));

        var usage = "<p class=\"meta\">이번 달 서버가 쓴 호출(성공): "
            + string.Join(" · ", MeaningWriterRegistry.All.Select(d =>
            {
                var n = used.GetValueOrDefault(d.Id);
                return d.Id == "gemini" && card.GeminiCap > 0
                    ? $"{Esc(d.Id)} {n:N0} / {card.GeminiCap:N0}" + (n >= card.GeminiCap ? " <b class=\"bad\">(상한 도달)</b>" : "")
                    : $"{Esc(d.Id)} {n:N0}";
            }))
            + "</p>";

        var origin = card.Overridden
            ? $"""
              <form method="post" action="{Routes.Base}/meanings/engine/reset" class="inline" style="display:inline-flex"
                    data-confirm="화면에서 저장한 엔진·키·모델을 지우고 server.env 설정으로 되돌릴까요?">
                <input type="hidden" name="csrf" value="{Esc(csrf)}">
                <button type="submit">환경변수로 되돌리기</button>
              </form>
              """
            : "<span class=\"meta\">지금은 <code>server.env</code> 값을 그대로 쓰고 있습니다.</span>";

        var autoChecked = card.Auto is { On: true } ? " checked" : "";
        var autoCap = card.Auto?.MonthlyCap ?? 300;

        return $$"""
            <style>
            .mcard .step{margin:1.1rem 0 .45rem;font-weight:600}
            .mcard .step .meta{font-weight:400}
            .mcard .engines{display:grid;grid-template-columns:repeat(auto-fill,minmax(15rem,1fr));gap:.4rem}
            .mcard .engines label{display:flex;gap:.5rem;align-items:flex-start;padding:.45rem .6rem;
                 border:1px solid var(--line);border-radius:.45rem;cursor:pointer}
            .mcard .engines label:has(input:checked){border-color:var(--accent);background:#1b2a3a}
            .mcard .keyrow{display:grid;grid-template-columns:9rem 1fr 17rem;gap:.6rem;align-items:start;
                 padding:.5rem 0;border-bottom:1px solid var(--line)}
            .mcard .kin{display:flex;flex-wrap:wrap;gap:.5rem}
            .mcard .kin input[type=password],.mcard .kin input[type=text]{min-width:16rem}
            .mcard .kname{font-weight:600;padding-top:1.1rem}
            .mcard .kchk{padding-top:1.1rem}
            .mcard .chip.okc{border-color:var(--ok);color:var(--ok)}
            .mcard .chip.badc{border-color:var(--bad);color:var(--bad)}
            .mcard .buttons{display:flex;gap:.5rem;margin-top:1rem;flex-wrap:wrap}
            @media (max-width:48rem){.mcard .keyrow{grid-template-columns:1fr}.mcard .kname,.mcard .kchk{padding-top:0} }
            @supports selector(:has(a)) {
            .mcard .keyrow{display:none}
            {{reveal}}
            }
            </style>
            <h2>의미 생성 엔진</h2>
            <div class="mcard">
            <p>{{summary}}</p>
            <form method="post" action="{{Routes.Base}}/meanings/engine">
              <input type="hidden" name="csrf" value="{{Esc(csrf)}}">
              <input type="hidden" name="fallbackSubmitted" value="1">

              <div class="step">① 주 엔진 고르기</div>
              <div class="engines">{{engines}}</div>

              <div class="step">② 키 넣기 <span class="meta">— 고른 엔진과 ③에서 체크한 폴백의 칸만 보입니다.
                넣어 둔 키는 끝 네 글자만 보이고, 비워 두면 그대로 유지됩니다.</span></div>
              {{keys}}

              <div class="step">③ 폴백 <span class="meta">— 주 엔진이 문단을 못 쓰면(잔액·한도·키 오류·빈 응답)
                그 곡부터 이어받습니다. 막힌 엔진은 잠시 쉽니다(설정 문제 30분 · 한도 10분).</span></div>
              {{fallback}}

              <div class="step">④ 그 밖의 설정</div>
              <div class="kin">
                <label class="meta">Gemini 월 호출 상한 <span class="meta">(0 = 막지 않음)</span><br>
                  <input type="number" name="geminiCap" min="0" value="{{card.GeminiCap}}" style="min-width:8rem"></label>
                <label class="meta" style="align-self:end"><input type="checkbox" name="auto" value="1"{{autoChecked}}>
                  새 곡 자동 생성 — 기기가 올린 곡에 의미가 없으면 서버가 곧 만듭니다</label>
                <label class="meta">자동 생성 월 상한(곡)<br>
                  <input type="number" name="autoCap" min="0" value="{{autoCap}}" style="min-width:8rem"></label>
              </div>

              <div class="buttons">
                <button type="submit">저장하고 확인</button>
                <button type="submit" formaction="{{Routes.Base}}/meanings/engine/test">저장하지 않고 테스트</button>
              </div>
              <p class="meta">두 버튼 모두 키 <b>형식</b>을 먼저 보고, 맞으면 짧은 자료로 <b>실제로 한 문단</b>을
                써 보게 해 키·잔액·모델이 살아 있는지 확인합니다. 형식이 틀린 키는 저장하지 않습니다.</p>
            </form>
            {{AutoMeaningLine(card.Auto)}}
            {{usage}}
            <details><summary>주의사항 · 되돌리기</summary>
            <p class="meta">저장하면 <b>다음 생성부터 바로</b> 적용됩니다(재시작 불필요). {{origin}}<br>
            Gemini API 무료 티어는 <b>결제를 연결하지 않은 프로젝트의 키</b>에만 적용됩니다 — GCP 체험 크레딧($300)은
            Gemini API에 쓸 수 없습니다. 결제가 붙은(선불) 프로젝트는 잔액이 떨어지면 402를 줍니다.<br>
            ⚠ 여기 넣은 API 키는 <b>DB에 평문으로</b> 저장되어 백업 파일에도 들어갑니다 —
            그게 싫으면 <code>server.env</code>만 쓰세요.</p>
            </details>
            </div>
            """;
    }

    /// <summary>새 곡 자동 의미 생성 한 줄 — 켜 뒀는데 실제로는 안 도는 경우를 구별해 적는다.</summary>
    private static string AutoMeaningLine(AutoMeaningState? auto)
    {
        if (auto is null) return "";
        if (!auto.On)
            return "<p class=\"meta\">새 곡 자동 생성: <b>꺼짐</b> — 의미는 일괄 작업이나 앱의 [의미 만들기]로만 생깁니다.</p>";

        var state = auto.Running
            ? $"<b class=\"ok\">켜짐</b> · 대기 {auto.Pending}곡"
            : "<b class=\"warn\">켜 뒀지만 멈춤</b> — 엔진이나 자료원이 구성되지 않았습니다";
        var cap = auto.UsedThisMonth >= auto.MonthlyCap
            ? $" · <b class=\"bad\">이번 달 상한 {auto.MonthlyCap:N0}곡 도달</b>"
            : $" · 이번 달 {auto.UsedThisMonth:N0} / {auto.MonthlyCap:N0}곡";
        var last = auto.LastReport is null ? "" : $"<br>마지막: {Esc(auto.LastReport)}";
        return $"<p class=\"meta\">새 곡 자동 생성: {state}{cap}{last}<br>"
             + "앱은 의미 화면을 열었을 때 서버가 만드는 중이면 잠시 기다렸다가 받습니다.</p>";
    }
}
