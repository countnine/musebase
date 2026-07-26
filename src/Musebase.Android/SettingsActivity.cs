using Android.App;
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
    Label = "번역 설정",
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

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var settings = MusebaseApp.Instance?.Settings;

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(48, 96, 48, 48);

        var title = new TextView(this) { Text = "번역 설정" };
        title.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 20f);
        root.AddView(title);

        // ---- 번역 엔진 ----
        root.AddView(Label("번역 엔진", topPad: 40));
        _engineSpinner = new Spinner(this);
        var adapter = new ArrayAdapter<string>(
            this, global::Android.Resource.Layout.SimpleSpinnerItem,
            Array.ConvertAll(Engines, e => e.Display));
        adapter.SetDropDownViewResource(global::Android.Resource.Layout.SimpleSpinnerDropDownItem);
        _engineSpinner.Adapter = adapter;
        _engineSpinner.SetSelection(IndexOfEngine(settings?.EffectiveTranslationEngine));
        _currentEngineId = SelectedEngineId();
        _engineSpinner.ItemSelected += (_, _) => OnEngineChanged();
        root.AddView(_engineSpinner);

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
        root.AddView(_keyRow);

        // ---- 번역 대상 언어(선택) ----
        root.AddView(Label("번역 대상 언어 (선택)", topPad: 32));
        _targetLangEdit = new EditText(this)
        {
            Hint = $"비우면 기기 로케일 기본값 ({MusebaseApp.DefaultTargetLanguage()})",
            InputType = InputTypes.ClassText | InputTypes.TextFlagCapCharacters,
        };
        _targetLangEdit.SetText(settings?.TargetLanguage ?? "", TextView.BufferType.Editable);
        root.AddView(_targetLangEdit);
        var langNote = new TextView(this)
        {
            Text = "DeepL 코드 예: KO, JA, EN-US, ZH, DE …",
        };
        langNote.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12f);
        root.AddView(langNote);

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
        root.AddView(_apiTranslationCheck);
        root.AddView(_apiNote);

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
        root.AddView(_fallbackCheck);
        root.AddView(_fallbackNote);

        // ---- 저장 ----
        var saveButton = new Button(this) { Text = "저장" };
        saveButton.SetPadding(0, 24, 0, 0);
        saveButton.Click += (_, _) => Save();
        root.AddView(saveButton);

        var scroll = new ScrollView(this) { FillViewport = true };
        scroll.AddView(root, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        SetContentView(scroll, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        ApplyKeyFieldUi();
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

        MusebaseApp.Instance?.ApplyTranslationSettings(retranslateNow: apiTurnedOn);

        Toast.MakeText(this, "저장됨 — 다음 곡부터 적용됩니다.", ToastLength.Long)?.Show();
        Finish();
    }

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
