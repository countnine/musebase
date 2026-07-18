using Android.App;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using Musebase.Core.Translation;

namespace Musebase.Android;

/// <summary>
/// 번역 설정 화면 — 엔진 선택 + DeepL API 키 + 대상 언어. 레이아웃 리소스 없이 코드로 UI를
/// 만들어(MainActivity와 동일 스타일) 표면적을 최소화한다.
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
        (TranslatorRegistry.None, "끄기 (제공자 번역만)"),
    };

    private Spinner? _engineSpinner;
    private LinearLayout? _deeplRow;
    private EditText? _deeplKeyEdit;
    private CheckBox? _showKeyCheck;
    private EditText? _targetLangEdit;

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
        _engineSpinner.ItemSelected += (_, _) => UpdateDeeplVisibility();
        root.AddView(_engineSpinner);

        // ---- DeepL API 키(DeepL 선택 시에만 표시) ----
        _deeplRow = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _deeplRow.AddView(Label("DeepL API 키", topPad: 32));
        _deeplKeyEdit = new EditText(this)
        {
            Hint = "DeepL API 키를 붙여 넣으세요",
            InputType = InputTypes.ClassText | InputTypes.TextVariationPassword,
        };
        _deeplKeyEdit.SetText(settings?.DeeplApiKey ?? "", TextView.BufferType.Editable);
        _deeplRow.AddView(_deeplKeyEdit);

        _showKeyCheck = new CheckBox(this) { Text = "키 표시" };
        _showKeyCheck.CheckedChange += (_, e) =>
        {
            _deeplKeyEdit.InputType = e.IsChecked
                ? InputTypes.ClassText | InputTypes.TextVariationVisiblePassword
                : InputTypes.ClassText | InputTypes.TextVariationPassword;
            _deeplKeyEdit.SetSelection(_deeplKeyEdit.Text?.Length ?? 0); // 커서 끝 유지
        };
        _deeplRow.AddView(_showKeyCheck);

        var keyNote = new TextView(this)
        {
            Text = "키는 앱 내부(private)에만 저장됩니다 — 디스크 암호화는 아닙니다.",
        };
        keyNote.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 12f);
        _deeplRow.AddView(keyNote);
        root.AddView(_deeplRow);

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

        UpdateDeeplVisibility();
    }

    private void Save()
    {
        var settings = MusebaseApp.Instance?.Settings;
        if (settings is null) { Finish(); return; }

        var engineId = Engines[Math.Clamp(_engineSpinner!.SelectedItemPosition, 0, Engines.Length - 1)].Id;
        settings.TranslationEngine = engineId;
        settings.DeeplApiKey = _deeplKeyEdit?.Text; // 빈 문자열은 저장소가 제거 처리
        settings.TargetLanguage = _targetLangEdit?.Text?.Trim();

        MusebaseApp.Instance?.ApplyTranslationSettings();

        Toast.MakeText(this, "저장됨 — 다음 곡부터 적용됩니다.", ToastLength.Long)?.Show();
        Finish();
    }

    /// <summary>DeepL 선택일 때만 키 입력 영역을 보인다.</summary>
    private void UpdateDeeplVisibility()
    {
        if (_deeplRow is null || _engineSpinner is null) return;
        var selectedId = Engines[Math.Clamp(_engineSpinner.SelectedItemPosition, 0, Engines.Length - 1)].Id;
        _deeplRow.Visibility = string.Equals(selectedId, "deepl", StringComparison.OrdinalIgnoreCase)
            ? ViewStates.Visible : ViewStates.Gone;
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
