using Android.App;
using Android.Content;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using Musebase.Core.Translation;

namespace Musebase.Android;

/// <summary>
/// 번역 설정 화면 — 엔진 선택 + (엔진별) API 키 + 대상 언어 + API 번역 사용 + 실패 시 무료 엔진 자동 전환.
/// 레이아웃 리소스 없이 코드로 UI를
/// 만들어(MainActivity와 동일 스타일) 표면적을 최소화한다.
/// 키 입력란은 선택한 엔진을 따라가며(DeepL/Google), 엔진을 바꿔도 입력값은 엔진별로 보관된다.
///
/// 저장 시 <see cref="Services.AndroidSettings"/>에 반영하고 <see cref="MusebaseApp.ApplyTranslationSettings"/>로
/// 재시작 없이 엔진을 재구성한다(새 엔진은 다음 곡/재검색부터 적용 — Windows와 동일 동작).
/// Exported=false — 앱 내부에서만 여는 화면이다.
/// </summary>
[Activity(
    Label = "Musebase 설정",
    Name = "com.countnine.musebase.SettingsActivity",
    Exported = false)]
public sealed class SettingsActivity : Activity
{
    // 스피너에 노출하는 엔진(순서 = 항목 인덱스). LibreTranslate는 자체호스팅용이라 이번엔 제외.
    private static readonly (string Id, string Display)[] Engines =
    {
        ("mymemory", "MyMemory (무료·무키)"),
        ("deepl", "DeepL (API 키 필요)"),
        ("google", "Google Cloud Translation (API 키 필요)"),
        (TranslatorRegistry.None, "끄기 (제공자 번역만)"),
    };

    /// <summary>키를 쓰는 엔진의 입력란 문구. 여기 없는 엔진은 키 입력을 감춘다.</summary>
    private static readonly Dictionary<string, (string Label, string Hint, string Note)> KeyFields =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["deepl"] = (
                "DeepL API 키",
                "DeepL API 키를 붙여 넣으세요",
                "키는 앱 내부(private)에만 저장됩니다 — 디스크 암호화는 아닙니다."),
            ["google"] = (
                "Google Cloud Translation API 키",
                "Google Cloud API 키를 붙여 넣으세요",
                "Google Cloud 콘솔에서 Cloud Translation API를 사용 설정하고 API 키를 만드세요(사용량 기반 유료). "
                + "키는 앱 내부(private)에만 저장됩니다 — 디스크 암호화는 아닙니다."),
        };

    /// <summary>
    /// 선호 음악 앱 후보(설치된 것만 목록에 뜬다). 팟캐스트·영상 앱은 넣지 않는다.
    /// 여기 없어도 현재 재생 중이면 감지된 세션 앱으로 목록에 추가된다.
    /// </summary>
    private static readonly string[] KnownMusicApps =
    {
        "com.spotify.music",                        // Spotify
        "com.apple.android.music",                  // Apple Music
        "com.sec.android.app.music",                // 삼성 뮤직
        "com.google.android.apps.youtube.music",    // YouTube Music
        "com.iloen.melon",                          // 멜론
        "com.ktmusic.geniemusic",                   // 지니뮤직
        "com.dreamus.flo",                          // FLO
        "com.neowiz.android.bugs",                  // 벅스
        "com.naver.vibe",                           // VIBE
        "com.amazon.mp3",                           // Amazon Music
        "deezer.android.app",                       // Deezer
        "com.aspiro.tidal",                         // TIDAL
        "com.maxmpz.audioplayer",                   // Poweramp
        "in.krosbits.musicolet",                    // Musicolet
    };

    /// <summary>선호 음악 앱 체크박스(Tag = 패키지명).</summary>
    private readonly List<CheckBox> _preferredChecks = new();

    /// <summary>엔진별 입력 중인 키(엔진을 바꿔도 유지 → 저장 시 전부 반영).</summary>
    private readonly Dictionary<string, string> _engineKeys = new(StringComparer.OrdinalIgnoreCase);

    private string _currentEngineId = TranslatorRegistry.DefaultFreeEngine;

    private Spinner? _engineSpinner;
    private LinearLayout? _keyRow;
    private TextView? _keyLabel;
    private EditText? _keyEdit;
    private TextView? _keyNote;
    private CheckBox? _showKeyCheck;
    private EditText? _targetLangEdit;
    private CheckBox? _fallbackCheck;
    private TextView? _fallbackNote;
    private CheckBox? _apiTranslationCheck;
    private TextView? _apiNote;
    private Spinner? _sourceSpinner;
    private (string Id, string Display)[] _sourceItems = Array.Empty<(string, string)>();
    private CheckBox? _includeVideoCheck;
    private CheckBox? _bubbleModeCheck;
    private CheckBox? _peekCheck;

    // 탭 섹션(기능별 분리 — Windows 설정 창과 같은 구성).
    private LinearLayout? _sourceSection;
    private LinearLayout? _translationSection;
    private LinearLayout? _overlaySection;
    private readonly List<Button> _tabButtons = new();

    // 오버레이 스타일 입력.
    private SeekBar? _fontSizeBar;
    private SeekBar? _cornerBar;
    private SeekBar? _backgroundOpacityBar;
    private CheckBox? _backgroundCheck;
    private CheckBox? _fadeCheck;
    private CheckBox? _characterKaraokeCheck;
    private CheckBox? _onlyTargetCheck;
    private EditText? _textColorEdit;
    private EditText? _karaokeColorEdit;
    private EditText? _translationColorEdit;
    private EditText? _backgroundColorEdit;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var settings = MusebaseApp.Instance?.Settings;

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(48, 96, 48, 48);

        var title = new TextView(this) { Text = "설정" };
        title.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 20f);
        root.AddView(title);

        // 기능별 탭(Windows 설정 창과 같은 구성): 재생 소스 / 번역 / 오버레이.
        // 리소스 없이 코드로 만들기 위해 버튼 줄 + 섹션 표시 전환으로 구현한다.
        _sourceSection = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _translationSection = new LinearLayout(this) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone };
        _overlaySection = new LinearLayout(this) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone };
        root.AddView(BuildTabBar());

        // ---- 재생 소스(어느 앱의 재생을 따라갈지) ----
        _sourceSection.AddView(Label("재생 소스", topPad: 40));
        _sourceItems = BuildSourceItems(settings);
        _sourceSpinner = new Spinner(this);
        var sourceAdapter = new ArrayAdapter<string>(
            this, global::Android.Resource.Layout.SimpleSpinnerItem,
            Array.ConvertAll(_sourceItems, s => s.Display));
        sourceAdapter.SetDropDownViewResource(global::Android.Resource.Layout.SimpleSpinnerDropDownItem);
        _sourceSpinner.Adapter = sourceAdapter;
        _sourceSpinner.SetSelection(IndexOfSource(settings?.PlaybackSource));
        _sourceSection.AddView(_sourceSpinner);

        _includeVideoCheck = new CheckBox(this)
        {
            Text = "영상·브라우저 앱도 소스로 사용",
            Checked = settings?.IncludeVideoApps == true,
        };
        _sourceSection.AddView(_includeVideoCheck);

        var sourceNote = new TextView(this)
        {
            Text = "끄면 YouTube·브라우저 등 영상 앱의 재생은 무시합니다"
                 + "(영상에 엉뚱한 곡의 가사가 뜨는 것을 막습니다). YouTube Music은 음악 앱이라 항상 포함됩니다.",
        };
        sourceNote.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12f);
        _sourceSection.AddView(sourceNote);

        // ---- 선호 음악 앱(여러 개 선택 가능, 비우면 자동) ----
        _sourceSection.AddView(Label("선호 음악 앱 (여러 개 선택 가능)", topPad: 32));
        var preferredNote = new TextView(this)
        {
            Text = "선택하면 그 앱들의 재생만 가사 소스로 씁니다 — 팟캐스트·영상 앱이 잡히지 않습니다. "
                 + "아무것도 선택하지 않으면 자동(기본)입니다.",
        };
        preferredNote.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12f);
        _sourceSection.AddView(preferredNote);

        var preferred = new HashSet<string>(
            settings?.PreferredSources ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var (package, label) in BuildMusicAppItems(preferred))
        {
            var check = new CheckBox(this) { Text = label, Checked = preferred.Contains(package) };
            check.Tag = package;
            _preferredChecks.Add(check);
            _sourceSection.AddView(check);
        }

        // ---- 번역 엔진 ----
        _translationSection.AddView(Label("번역 엔진", topPad: 40));
        _engineSpinner = new Spinner(this);
        var adapter = new ArrayAdapter<string>(
            this, global::Android.Resource.Layout.SimpleSpinnerItem,
            Array.ConvertAll(Engines, e => e.Display));
        adapter.SetDropDownViewResource(global::Android.Resource.Layout.SimpleSpinnerDropDownItem);
        _engineSpinner.Adapter = adapter;
        _engineSpinner.SetSelection(IndexOfEngine(settings?.EffectiveTranslationEngine));
        _currentEngineId = SelectedEngineId();
        _engineSpinner.ItemSelected += (_, _) => OnEngineChanged();
        _translationSection.AddView(_engineSpinner);

        // ---- API 키(키를 쓰는 엔진 선택 시에만 표시, 문구는 엔진에 따라 바뀜) ----
        foreach (var id in KeyFields.Keys)
            _engineKeys[id] = settings?.GetTranslationApiKey(id) ?? "";

        _keyRow = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _keyLabel = Label("API 키", topPad: 32);
        _keyRow.AddView(_keyLabel);
        _keyEdit = new EditText(this)
        {
            InputType = InputTypes.ClassText | InputTypes.TextVariationPassword,
        };
        _keyRow.AddView(_keyEdit);

        var keyEdit = _keyEdit; // 람다에서 non-null로 다루기 위한 지역 캡처
        _showKeyCheck = new CheckBox(this) { Text = "키 표시" };
        _showKeyCheck.CheckedChange += (_, e) =>
        {
            keyEdit.InputType = e.IsChecked
                ? InputTypes.ClassText | InputTypes.TextVariationVisiblePassword
                : InputTypes.ClassText | InputTypes.TextVariationPassword;
            keyEdit.SetSelection(keyEdit.Text?.Length ?? 0); // 커서 끝 유지
        };
        _keyRow.AddView(_showKeyCheck);

        _keyNote = new TextView(this);
        _keyNote.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12f);
        _keyRow.AddView(_keyNote);
        _translationSection.AddView(_keyRow);

        // ---- 번역 대상 언어(선택) ----
        _translationSection.AddView(Label("번역 대상 언어 (선택)", topPad: 32));
        _targetLangEdit = new EditText(this)
        {
            Hint = $"비우면 기기 로케일 기본값 ({MusebaseApp.DefaultTargetLanguage()})",
            InputType = InputTypes.ClassText | InputTypes.TextFlagCapCharacters,
        };
        _targetLangEdit.SetText(settings?.TargetLanguage ?? "", TextView.BufferType.Editable);
        _translationSection.AddView(_targetLangEdit);
        var langNote = new TextView(this)
        {
            Text = "DeepL 코드 예: KO, JA, EN-US, ZH, DE …",
        };
        langNote.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12f);
        _translationSection.AddView(langNote);

        // ---- API 번역 사용/미사용(기본 켬, Windows 트레이 토글과 같은 스위치) ----
        _apiNote = new TextView(this)
        {
            Text = "끄면 새 번역 요청을 보내지 않아 유료 API 사용량이 발생하지 않습니다"
                 + "(이미 캐시된 번역은 계속 표시). 다시 켜면 재생 중인 곡부터 바로 번역합니다.",
        };
        _apiNote.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12f);

        _apiTranslationCheck = new CheckBox(this)
        {
            Text = "API 번역 사용",
            Checked = settings?.ApiTranslationEnabled ?? true,
        };
        _apiTranslationCheck.SetPadding(0, 32, 0, 0);
        _translationSection.AddView(_apiTranslationCheck);
        _translationSection.AddView(_apiNote);

        // ---- 실패 시 무료 엔진 자동 전환(기본 꺼짐, Windows 설정과 같은 의미) ----
        _fallbackNote = new TextView(this)
        {
            Text = "켜면 주 엔진 실패 시 가사가 무료 번역 공개 서버(MyMemory)로 전송될 수 있습니다.",
            Visibility = settings?.TranslationFallbackToFree == true ? ViewStates.Visible : ViewStates.Gone,
        };
        _fallbackNote.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12f);

        _fallbackCheck = new CheckBox(this)
        {
            Text = "선택한 번역 엔진 실패 시 무료 번역(MyMemory)으로 자동 전환",
            Checked = settings?.TranslationFallbackToFree == true,
        };
        _fallbackCheck.SetPadding(0, 32, 0, 0);
        var fallbackNote = _fallbackNote; // 람다에서 non-null로 다루기 위한 지역 캡처
        _fallbackCheck.CheckedChange += (_, e) =>
            fallbackNote.Visibility = e.IsChecked ? ViewStates.Visible : ViewStates.Gone;
        _translationSection.AddView(_fallbackCheck);
        _translationSection.AddView(_fallbackNote);

        // ---- 오버레이 표시 방식(밴드 / 버블) ----
        _overlaySection.AddView(Label("오버레이 표시 방식", topPad: 48));

        _bubbleModeCheck = new CheckBox(this)
        {
            Text = "버블(플로팅) 모드 — 작은 원을 탭할 때만 가사 표시",
            Checked = settings?.OverlayBubbleMode == true,
        };
        _overlaySection.AddView(_bubbleModeCheck);

        _peekCheck = new CheckBox(this)
        {
            Text = "접혀 있어도 새 가사 줄이 나오면 잠깐 펼치기",
            Checked = settings?.OverlayPeekOnNewLine != false,
            Visibility = settings?.OverlayBubbleMode == true ? ViewStates.Visible : ViewStates.Gone,
        };
        var peekCheck = _peekCheck; // 람다에서 non-null로 다루기 위한 지역 캡처
        _bubbleModeCheck.CheckedChange += (_, e) =>
            peekCheck.Visibility = e.IsChecked ? ViewStates.Visible : ViewStates.Gone;
        _overlaySection.AddView(_peekCheck);

        var overlayNote = new TextView(this)
        {
            Text = "가사 위치는 알림바의 '위치 이동'을 눌러 드래그로 옮길 수 있습니다"
                 + "(버블은 드래그하면 가까운 가장자리에 붙습니다).",
        };
        overlayNote.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12f);
        _overlaySection.AddView(overlayNote);

        // ---- 오버레이 스타일(Windows 설정 [오버레이] 탭과 같은 항목) ----
        _overlaySection.AddView(Label("오버레이 스타일", topPad: 40));

        _fontSizeBar = SliderRow(_overlaySection, "글자 크기", settings?.OverlayFontSizeSp ?? 22,
            min: 12, max: 40, format: v => $"{v}sp");
        _cornerBar = SliderRow(_overlaySection, "모서리 둥글기", settings?.OverlayCornerRadius ?? 18,
            min: 0, max: 40, format: v => $"{v}dp");
        _backgroundOpacityBar = SliderRow(_overlaySection, "배경 불투명도",
            (int)Math.Round((settings?.OverlayBackgroundOpacity ?? 0.7f) * 100), min: 0, max: 100,
            format: v => $"{v}%");

        _backgroundCheck = new CheckBox(this)
        {
            Text = "가사 뒤 배경판 표시",
            Checked = settings?.OverlayBackgroundEnabled != false,
        };
        _overlaySection.AddView(_backgroundCheck);

        _fadeCheck = new CheckBox(this)
        {
            Text = "나타나고 사라질 때 페이드 효과",
            Checked = settings?.OverlayFadeAnimation != false,
        };
        _overlaySection.AddView(_fadeCheck);

        _characterKaraokeCheck = new CheckBox(this)
        {
            Text = "글자 단위 카라오케 채움 (끄면 줄 단위)",
            Checked = settings?.CharacterKaraoke != false,
        };
        _overlaySection.AddView(_characterKaraokeCheck);

        _onlyTargetCheck = new CheckBox(this)
        {
            Text = "대상 언어로 번역된 줄만 표시",
            Checked = settings?.ShowOnlyTargetTranslation != false,
        };
        _overlaySection.AddView(_onlyTargetCheck);
        var onlyTargetNote = new TextView(this)
        {
            Text = "끄면 제공자가 제공하는 번역(중국어 등)도 그대로 표시됩니다.",
        };
        onlyTargetNote.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12f);
        _overlaySection.AddView(onlyTargetNote);

        _textColorEdit = ColorRow(_overlaySection, "가사 색", settings?.OverlayTextColor ?? "#FFFFFF");
        _karaokeColorEdit = ColorRow(_overlaySection, "카라오케 채움 색", settings?.OverlayKaraokeColor ?? "#FFEB3B");
        _translationColorEdit = ColorRow(_overlaySection, "번역 색", settings?.OverlayTranslationColor ?? "#E8E8E8");
        _backgroundColorEdit = ColorRow(_overlaySection, "배경 색", settings?.OverlayBackgroundColor ?? "#000000");

        // ---- 섹션 + 저장 ----
        var sections = new LinearLayout(this) { Orientation = Orientation.Vertical };
        sections.AddView(_sourceSection);
        sections.AddView(_translationSection);
        sections.AddView(_overlaySection);

        var scroll = new ScrollView(this) { FillViewport = true };
        scroll.AddView(sections, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        root.AddView(scroll, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f));

        // 저장 버튼은 탭과 무관하게 항상 아래에 고정한다(스크롤 밖).
        var saveButton = new Button(this) { Text = "저장" };
        saveButton.Click += (_, _) => Save();
        root.AddView(saveButton);

        SetContentView(root, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        ApplyKeyFieldUi();
        SelectTab(0);
    }

    private void Save()
    {
        var settings = MusebaseApp.Instance?.Settings;
        if (settings is null) { Finish(); return; }

        var engineId = SelectedEngineId();
        StashCurrentKey(); // 화면에 떠 있던 입력값까지 버퍼에 담고
        settings.TranslationEngine = engineId;
        foreach (var (id, key) in _engineKeys) settings.SetTranslationApiKey(id, key); // 엔진별로 저장
        settings.TargetLanguage = _targetLangEdit?.Text?.Trim();
        settings.TranslationFallbackToFree = _fallbackCheck?.Checked == true;

        // API 번역을 껐다가 다시 켠 경우엔 다음 곡을 기다리지 않고 현재 곡을 즉시 번역한다.
        var apiEnabled = _apiTranslationCheck?.Checked ?? true;
        var apiTurnedOn = apiEnabled && !settings.ApiTranslationEnabled;
        settings.ApiTranslationEnabled = apiEnabled;

        settings.PlaybackSource = SelectedSourceId();
        settings.IncludeVideoApps = _includeVideoCheck?.Checked == true;

        var preferredSelected = new List<string>();
        foreach (var check in _preferredChecks)
            if (check.Checked && check.Tag?.ToString() is { Length: > 0 } package)
                preferredSelected.Add(package);
        settings.PreferredSources = preferredSelected;

        settings.OverlayBubbleMode = _bubbleModeCheck?.Checked == true;
        settings.OverlayPeekOnNewLine = _peekCheck?.Checked != false;

        settings.OverlayFontSizeSp = SliderValue(_fontSizeBar, settings.OverlayFontSizeSp);
        settings.OverlayCornerRadius = SliderValue(_cornerBar, settings.OverlayCornerRadius);
        settings.OverlayBackgroundOpacity = SliderValue(_backgroundOpacityBar, 70) / 100f;
        settings.OverlayBackgroundEnabled = _backgroundCheck?.Checked != false;
        settings.OverlayFadeAnimation = _fadeCheck?.Checked != false;
        settings.CharacterKaraoke = _characterKaraokeCheck?.Checked != false;
        settings.ShowOnlyTargetTranslation = _onlyTargetCheck?.Checked != false;
        settings.OverlayTextColor = ColorOrDefault(_textColorEdit, settings.OverlayTextColor);
        settings.OverlayKaraokeColor = ColorOrDefault(_karaokeColorEdit, settings.OverlayKaraokeColor);
        settings.OverlayTranslationColor = ColorOrDefault(_translationColorEdit, settings.OverlayTranslationColor);
        settings.OverlayBackgroundColor = ColorOrDefault(_backgroundColorEdit, settings.OverlayBackgroundColor);

        MusebaseApp.Instance?.ApplyPlaybackSourceSettings();
        MusebaseApp.Instance?.ApplyTranslationSettings(retranslateNow: apiTurnedOn);

        // 표시 방식(버블/밴드)은 오버레이 서비스가 다시 읽어야 반영된다.
        if (Services.OverlayService.IsRunning)
        {
            var intent = new Intent(this, typeof(Services.OverlayService))
                .SetAction(Services.OverlayService.ActionRefreshDisplay);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O) StartForegroundService(intent);
            else StartService(intent);
        }

        Toast.MakeText(this, "저장됨 — 다음 곡부터 적용됩니다.", ToastLength.Long)?.Show();
        Finish();
    }

    /// <summary>
    /// 재생 소스 선택 항목: "자동 감지" + 현재 감지된 세션 앱들(+ 목록에 없는 저장값).
    /// 표시는 앱 이름을 쓰되, 조회에 실패하면 패키지명을 그대로 보여준다.
    /// </summary>
    private (string Id, string Display)[] BuildSourceItems(Services.AndroidSettings? settings)
    {
        var items = new List<(string, string)>
        {
            (Services.AndroidNowPlayingSource.AutoSource, "자동 감지 (재생 중인 앱)"),
        };

        var packages = new List<string>(
            MusebaseApp.Instance?.Source.ActiveSessionPackages ?? (IReadOnlyList<string>)Array.Empty<string>());
        var saved = settings?.PlaybackSource;
        if (!string.IsNullOrWhiteSpace(saved)
            && !string.Equals(saved, Services.AndroidNowPlayingSource.AutoSource, StringComparison.OrdinalIgnoreCase)
            && !packages.Contains(saved!))
        {
            packages.Add(saved!); // 지금은 안 뜨는 앱이어도 선택은 유지한다
        }

        foreach (var package in packages) items.Add((package, AppLabel(package)));
        return items.ToArray();
    }

    // ---- 탭 / 입력 위젯 헬퍼 ----

    /// <summary>탭 버튼 줄. 누르면 해당 섹션만 보이고 나머지는 숨긴다.</summary>
    private LinearLayout BuildTabBar()
    {
        var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        bar.SetPadding(0, 24, 0, 8);

        void AddTab(string text, int index)
        {
            var button = new Button(this) { Text = text };
            button.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 13f);
            button.Click += (_, _) => SelectTab(index);
            _tabButtons.Add(button);
            bar.AddView(button, new LinearLayout.LayoutParams(
                0, ViewGroup.LayoutParams.WrapContent, 1f));
        }

        AddTab("재생 소스", 0);
        AddTab("번역", 1);
        AddTab("오버레이", 2);
        return bar;
    }

    private void SelectTab(int index)
    {
        if (_sourceSection is null || _translationSection is null || _overlaySection is null) return;
        _sourceSection.Visibility = index == 0 ? ViewStates.Visible : ViewStates.Gone;
        _translationSection.Visibility = index == 1 ? ViewStates.Visible : ViewStates.Gone;
        _overlaySection.Visibility = index == 2 ? ViewStates.Visible : ViewStates.Gone;
        for (var i = 0; i < _tabButtons.Count; i++)
            _tabButtons[i].Alpha = i == index ? 1f : 0.55f; // 선택된 탭을 진하게
    }

    /// <summary>"라벨 — 현재값" + 슬라이더 한 줄. 값이 바뀌면 라벨이 즉시 갱신된다.</summary>
    private SeekBar SliderRow(LinearLayout parent, string label, int value, int min, int max, Func<int, string> format)
    {
        var caption = new TextView(this) { Text = $"{label}  {format(value)}" };
        caption.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 14f);
        caption.SetPadding(0, 24, 0, 0);
        parent.AddView(caption);

        var bar = new SeekBar(this) { Max = max - min, Progress = Math.Clamp(value, min, max) - min };
        bar.ProgressChanged += (_, e) => caption.Text = $"{label}  {format(e.Progress + min)}";
        bar.Tag = min; // 저장 시 최소값을 되더해야 한다
        parent.AddView(bar);
        return bar;
    }

    /// <summary>
    /// 색 한 줄: 라벨 + [색 견본 버튼] + "#RRGGBB" 입력. 견본을 누르면 팔레트 대화상자가 열리고
    /// (Windows 설정의 색 선택과 같은 조작), 직접 입력도 계속 쓸 수 있다.
    /// </summary>
    private EditText ColorRow(LinearLayout parent, string label, string value)
    {
        var caption = new TextView(this) { Text = label };
        caption.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 14f);
        caption.SetPadding(0, 24, 0, 0);
        parent.AddView(caption);

        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);

        var swatch = new Button(this);
        var swatchSize = (int)(40 * Resources!.DisplayMetrics!.Density + 0.5f);
        var edit = new EditText(this) { Hint = "#RRGGBB" };
        edit.SetText(value, TextView.BufferType.Editable);

        void PaintSwatch()
        {
            var drawable = new global::Android.Graphics.Drawables.GradientDrawable();
            drawable.SetCornerRadius(8 * Resources.DisplayMetrics.Density);
            drawable.SetColor(Services.AndroidSettings.ParseColor(edit.Text, global::Android.Graphics.Color.Gray));
            drawable.SetStroke(2, global::Android.Graphics.Color.Argb(0x80, 0xFF, 0xFF, 0xFF));
            swatch.Background = drawable;
        }
        PaintSwatch();

        swatch.Click += (_, _) => ShowColorPalette(edit.Text, picked =>
        {
            edit.SetText(picked, TextView.BufferType.Editable);
            PaintSwatch();
        });
        edit.TextChanged += (_, _) => PaintSwatch(); // 직접 입력해도 견본이 따라간다

        row.AddView(swatch, new LinearLayout.LayoutParams(swatchSize, swatchSize));
        row.AddView(edit, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        parent.AddView(row);
        return edit;
    }

    /// <summary>팔레트 대화상자에 띄울 기본 색(윗줄=무채색, 아랫줄=유채색).</summary>
    private static readonly string[] PaletteColors =
    {
        "#FFFFFF", "#E8E8E8", "#BDBDBD", "#757575", "#424242", "#000000",
        "#FFEB3B", "#FFC107", "#FF9800", "#FF5722", "#F44336", "#E91E63",
        "#9C27B0", "#673AB7", "#3F51B5", "#2196F3", "#03A9F4", "#00BCD4",
        "#009688", "#4CAF50", "#8BC34A", "#CDDC39", "#795548", "#607D8B",
    };

    /// <summary>색 팔레트 대화상자 — 견본을 누르면 그 색으로 정한다(직접 입력은 아래 칸에서).</summary>
    private void ShowColorPalette(string? current, Action<string> onPicked)
    {
        var density = Resources!.DisplayMetrics!.Density;
        int Dp(float dp) => (int)(dp * density + 0.5f);

        var grid = new GridLayout(this) { ColumnCount = 6 };
        grid.SetPadding(Dp(16), Dp(16), Dp(16), Dp(8));

        AlertDialog? dialog = null;
        foreach (var hex in PaletteColors)
        {
            var cell = new Button(this);
            var drawable = new global::Android.Graphics.Drawables.GradientDrawable();
            drawable.SetCornerRadius(Dp(6));
            drawable.SetColor(Services.AndroidSettings.ParseColor(hex, global::Android.Graphics.Color.Gray));
            // 지금 값과 같은 색은 테두리를 굵게 해 어떤 색인지 알 수 있게 한다.
            var selected = string.Equals(hex, current?.Trim(), StringComparison.OrdinalIgnoreCase);
            drawable.SetStroke(Dp(selected ? 3 : 1),
                selected ? global::Android.Graphics.Color.White : global::Android.Graphics.Color.Argb(0x60, 0xFF, 0xFF, 0xFF));
            cell.Background = drawable;
            cell.Click += (_, _) => { onPicked(hex); dialog?.Dismiss(); };
            grid.AddView(cell, new ViewGroup.LayoutParams(Dp(44), Dp(44)));
        }

        dialog = new AlertDialog.Builder(this)
            .SetTitle("색 선택")!
            .SetView(grid)!
            .SetNegativeButton("취소", (IDialogInterfaceOnClickListener?)null)!
            .Create();
        dialog?.Show();
    }

    /// <summary>입력한 색이 유효한 "#RRGGBB"면 그 값을, 아니면 기존 값을 그대로 쓴다.</summary>
    private static string ColorOrDefault(EditText? edit, string current)
    {
        var text = edit?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return current;
        try { _ = global::Android.Graphics.Color.ParseColor(text); return text!; }
        catch { return current; }
    }

    /// <summary>슬라이더 현재값(최소값 보정 포함).</summary>
    private static int SliderValue(SeekBar? bar, int fallback)
    {
        if (bar is null) return fallback;
        var min = bar.Tag is Java.Lang.Integer i ? i.IntValue() : 0;
        return bar.Progress + min;
    }

    /// <summary>
    /// 잘 알려진 음악 앱(설치된 것만) + 현재 감지된 세션 앱 + 이미 선택해 둔 앱을 합쳐
    /// 체크박스 목록으로 만든다. 팟캐스트·영상 앱은 목록에 넣지 않는다(고르라고 권하지 않는다).
    /// </summary>
    private List<(string Package, string Label)> BuildMusicAppItems(ICollection<string> selected)
    {
        var candidates = new List<string>(KnownMusicApps);
        foreach (var p in MusebaseApp.Instance?.Source.ActiveSessionPackages ?? (IReadOnlyList<string>)Array.Empty<string>())
            if (!candidates.Contains(p, StringComparer.OrdinalIgnoreCase)) candidates.Add(p);
        foreach (var p in selected)
            if (!candidates.Contains(p, StringComparer.OrdinalIgnoreCase)) candidates.Add(p);

        var items = new List<(string, string)>();
        foreach (var package in candidates)
        {
            // 설치돼 있지 않고 선택된 적도 없는 앱은 목록을 어지럽히므로 뺀다.
            if (!IsInstalled(package) && !selected.Contains(package)) continue;
            items.Add((package, AppLabel(package)));
        }
        return items;
    }

    private bool IsInstalled(string package)
    {
        try { return PackageManager?.GetApplicationInfo(package, 0) is not null; }
        catch { return false; }
    }

    private string AppLabel(string package)
    {
        try
        {
            var pm = PackageManager;
            if (pm is null) return package;
            var info = pm.GetApplicationInfo(package, 0);
            return $"{pm.GetApplicationLabel(info)} ({package})";
        }
        catch
        {
            return package; // 제거된 앱 등 — 패키지명으로 표시
        }
    }

    private int IndexOfSource(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return 0;
        for (var i = 0; i < _sourceItems.Length; i++)
            if (string.Equals(_sourceItems[i].Id, id, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    private string SelectedSourceId() =>
        _sourceItems.Length == 0
            ? Services.AndroidNowPlayingSource.AutoSource
            : _sourceItems[Math.Clamp(_sourceSpinner?.SelectedItemPosition ?? 0, 0, _sourceItems.Length - 1)].Id;

    /// <summary>선택 중인 엔진 id(스피너 위치 → 목록).</summary>
    private string SelectedEngineId() =>
        Engines[Math.Clamp(_engineSpinner?.SelectedItemPosition ?? 0, 0, Engines.Length - 1)].Id;

    /// <summary>입력란의 현재 값을 현재 엔진 자리에 보관(엔진 전환·저장 직전에 호출).</summary>
    private void StashCurrentKey()
    {
        if (_keyEdit is not null && _engineKeys.ContainsKey(_currentEngineId))
            _engineKeys[_currentEngineId] = _keyEdit.Text ?? "";
    }

    /// <summary>엔진이 바뀌면 이전 엔진의 입력값을 보관하고 새 엔진의 키/문구로 갈아 끼운다.</summary>
    private void OnEngineChanged()
    {
        if (_keyRow is null) return; // UI 구성 완료 전 스피너 초기 콜백은 무시
        StashCurrentKey();
        _currentEngineId = SelectedEngineId();
        ApplyKeyFieldUi();
    }

    /// <summary>키를 쓰는 엔진일 때만 입력 영역을 보이고, 문구·값을 그 엔진 것으로 맞춘다.</summary>
    private void ApplyKeyFieldUi()
    {
        if (_keyRow is null || _keyEdit is null || _keyLabel is null || _keyNote is null) return;
        if (!KeyFields.TryGetValue(_currentEngineId, out var field))
        {
            _keyRow.Visibility = ViewStates.Gone;
            return;
        }

        _keyRow.Visibility = ViewStates.Visible;
        _keyLabel.Text = field.Label;
        _keyEdit.Hint = field.Hint;
        _keyNote.Text = field.Note;
        _keyEdit.SetText(_engineKeys.GetValueOrDefault(_currentEngineId, ""), TextView.BufferType.Editable);
    }

    private static int IndexOfEngine(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return 0;
        for (var i = 0; i < Engines.Length; i++)
            if (string.Equals(Engines[i].Id, id, StringComparison.OrdinalIgnoreCase)) return i;
        return 0; // 목록에 없는 엔진(libretranslate 등)은 MyMemory로 폴백 표시
    }

    private TextView Label(string text, int topPad)
    {
        var tv = new TextView(this) { Text = text };
        tv.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 14f);
        tv.SetPadding(0, topPad, 0, 8);
        return tv;
    }
}
