using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using Musebase.Core;
using Musebase.Core.Search;

namespace Musebase.Android;

/// <summary>
/// 가사 수동 검색·선택 화면 — Windows의 검색 창(`SearchWindow`)과 같은 흐름:
/// 제목·아티스트로 여러 제공자를 한 번에 검색해 **품질 순 후보**를 보여 주고, 고른 결과를
/// 미리 본 뒤 적용하면 <see cref="Musebase.Engine.LyricsCoordinator.UseLyricsAsync"/>로
/// 오버레이·캐시에 반영한다(자동 검색이 엉뚱한 가사를 잡았을 때의 교정 수단).
///
/// 검색 자체는 코어의 <see cref="LyricsSearchService"/>를 그대로 쓴다(엔진 재조립 없음).
/// 화면 진입 시 현재 재생 곡의 제목·아티스트를 채워 두어 바로 검색할 수 있다.
/// Exported=false — 앱 내부에서만 여는 화면이다.
/// </summary>
[Activity(
    Label = "가사 검색",
    Name = "com.countnine.musebase.SearchActivity",
    Exported = false)]
public sealed class SearchActivity : Activity
{
    private static readonly Color ActiveColor = Color.White;
    private static readonly Color DimColor = Color.Argb(0xFF, 0x9E, 0x9E, 0x9E);

    private EditText? _titleEdit;
    private EditText? _artistEdit;
    private Button? _searchButton;
    private Button? _applyButton;
    private TextView? _statusText;
    private LinearLayout? _resultColumn;
    private TextView? _previewText;

    private readonly List<(Lyrics Lyrics, TextView Row)> _results = new();
    private Lyrics? _selected;
    private CancellationTokenSource? _cts;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Color.Argb(0xFF, 0x11, 0x11, 0x11));
        root.SetPadding(Dp(16), Dp(20), Dp(16), Dp(12));

        var track = MusebaseApp.Instance?.Source.CurrentTrack;

        _titleEdit = new EditText(this) { Hint = "제목", InputType = InputTypes.ClassText };
        _titleEdit.SetText(track?.Title ?? "", TextView.BufferType.Editable);
        root.AddView(_titleEdit);

        _artistEdit = new EditText(this) { Hint = "아티스트 (선택)", InputType = InputTypes.ClassText };
        _artistEdit.SetText(track?.Artist ?? "", TextView.BufferType.Editable);
        root.AddView(_artistEdit);

        var buttonRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        _searchButton = new Button(this) { Text = "검색" };
        _searchButton.Click += async (_, _) => await RunSearchAsync();
        buttonRow.AddView(_searchButton, new LinearLayout.LayoutParams(
            0, ViewGroup.LayoutParams.WrapContent, 1f));

        _applyButton = new Button(this) { Text = "이 가사 사용", Enabled = false };
        _applyButton.Click += async (_, _) => await ApplySelectedAsync();
        buttonRow.AddView(_applyButton, new LinearLayout.LayoutParams(
            0, ViewGroup.LayoutParams.WrapContent, 1f));
        root.AddView(buttonRow);

        _statusText = new TextView(this) { Text = "제목을 넣고 검색하세요." };
        _statusText.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12f);
        _statusText.SetTextColor(DimColor);
        _statusText.SetPadding(0, Dp(8), 0, Dp(4));
        root.AddView(_statusText);

        // 결과 목록(품질 순) — 누르면 아래 미리보기가 바뀐다.
        _resultColumn = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var resultScroll = new ScrollView(this);
        resultScroll.AddView(_resultColumn, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        root.AddView(resultScroll, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f));

        _previewText = new TextView(this) { Text = "" };
        _previewText.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 13f);
        _previewText.SetTextColor(DimColor);
        var previewScroll = new ScrollView(this);
        previewScroll.AddView(_previewText, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        root.AddView(previewScroll, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1.2f));

        SetContentView(root, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // 곡이 이미 있으면 바로 한 번 검색해 준다(Windows 검색 창과 같은 편의).
        if (!string.IsNullOrWhiteSpace(track?.Title)) _ = RunSearchAsync();
    }

    private async Task RunSearchAsync()
    {
        var title = _titleEdit?.Text?.Trim() ?? "";
        if (title.Length == 0) { SetStatus("제목을 넣어 주세요."); return; }
        var artist = _artistEdit?.Text?.Trim() ?? "";

        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        if (_searchButton is not null) _searchButton.Enabled = false;
        SetStatus("검색 중…");
        ClearResults();

        try
        {
            var request = LyricsSearchRequest.ByInfo(
                title, artist,
                MusebaseApp.Instance?.Source.CurrentTrack?.Duration?.TotalSeconds ?? 0,
                limit: 6);
            var results = await new LyricsSearchService().SearchAllAsync(request, cts.Token);
            if (cts.IsCancellationRequested) return;

            foreach (var lyrics in results) AddResultRow(lyrics);
            SetStatus(results.Count == 0
                ? "결과가 없습니다."
                : $"{results.Count}개 결과 (품질 순) — 항목을 눌러 미리 보세요.");
            if (results.Count > 0) Select(results[0]);
        }
        // Android.OS에도 같은 이름이 있어 모호 참조(CS0104)가 난다 — System 쪽을 명시한다.
        catch (System.OperationCanceledException) { /* 재검색·화면 종료 */ }
        catch (Exception e)
        {
            SetStatus($"검색 실패: {e.Message}");
        }
        finally
        {
            if (_searchButton is not null) _searchButton.Enabled = true;
        }
    }

    private void AddResultRow(Lyrics lyrics)
    {
        if (_resultColumn is null) return;
        var service = lyrics.Metadata.ServiceName ?? "?";
        var rowTitle = lyrics.IdTags.GetValueOrDefault(Lyrics.TagTitle) ?? "?";
        var rowArtist = lyrics.IdTags.GetValueOrDefault(Lyrics.TagArtist) ?? "?";
        var translated = lyrics.HasTranslation() ? " · 번역 있음" : "";

        var row = new TextView(this)
        {
            Text = $"{service} · 품질 {lyrics.Quality():0.00} · {lyrics.Lines.Count}줄{translated}\n{rowTitle} — {rowArtist}",
        };
        row.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 14f);
        row.SetTextColor(DimColor);
        row.SetPadding(Dp(8), Dp(10), Dp(8), Dp(10));
        row.Click += (_, _) => Select(lyrics);

        _resultColumn.AddView(row, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        _results.Add((lyrics, row));
    }

    /// <summary>후보 선택 — 강조 표시 + 미리보기(원문/번역) 갱신.</summary>
    private void Select(Lyrics lyrics)
    {
        _selected = lyrics;
        if (_applyButton is not null) _applyButton.Enabled = true;

        foreach (var (candidate, row) in _results)
        {
            var isSelected = ReferenceEquals(candidate, lyrics);
            row.SetTextColor(isSelected ? ActiveColor : DimColor);
            row.SetTypeface(isSelected ? Typeface.DefaultBold : Typeface.Default,
                isSelected ? TypefaceStyle.Bold : TypefaceStyle.Normal);
        }

        var lang = MusebaseApp.Instance?.Coordinator.TargetLanguage?.ToLowerInvariant();
        var sb = new System.Text.StringBuilder();
        foreach (var line in lyrics.Lines)
        {
            sb.AppendLine(line.Content);
            var tr = string.IsNullOrEmpty(lang)
                ? line.Attachments.Translation()
                : line.Attachments.Translation(lang, null);
            if (!string.IsNullOrEmpty(tr)) sb.AppendLine("    " + tr);
        }
        if (_previewText is not null) _previewText.Text = sb.ToString().TrimEnd();
    }

    private async Task ApplySelectedAsync()
    {
        if (_selected is null || MusebaseApp.Instance is not { } app) return;
        await app.Coordinator.UseLyricsAsync(_selected);
        Toast.MakeText(this, "이 가사를 사용합니다.", ToastLength.Short)?.Show();
        Finish();
    }

    private void ClearResults()
    {
        _results.Clear();
        _selected = null;
        _resultColumn?.RemoveAllViews();
        if (_previewText is not null) _previewText.Text = "";
        if (_applyButton is not null) _applyButton.Enabled = false;
    }

    private void SetStatus(string text)
    {
        if (_statusText is not null) _statusText.Text = text;
    }

    private int Dp(float dp) => (int)(dp * Resources!.DisplayMetrics!.Density + 0.5f);

    protected override void OnDestroy()
    {
        _cts?.Cancel();
        base.OnDestroy();
    }
}
