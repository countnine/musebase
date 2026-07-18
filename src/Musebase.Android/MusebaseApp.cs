using Android.App;
using Android.Runtime;
using Musebase.Android.Services;
using Musebase.Core.Search;
using Musebase.Core.Translation;
using Musebase.Engine;

namespace Musebase.Android;

/// <summary>
/// 앱 전역 싱글턴 — 가사 엔진 조립 지점(Phase 2).
/// Activity가 아닌 Application에서 1회만 조립하므로 화면 회전(Activity 재생성)에도
/// 소스/코디네이터/캐시가 유지된다. Application.OnCreate는 메인 스레드에서 실행되어
/// <see cref="AndroidNowPlayingSource"/>의 "메인 스레드에서 생성/Start" 계약을 만족한다.
///
/// 구성(개인용 프로파일):
/// - 가사 소스: <see cref="LyricsSourceRegistry.AllIds"/> 전부 (공개 배포 시 OfficialIds로 축소 — ADR-0002)
/// - 번역 엔진: <see cref="AndroidSettings"/>에서 읽음(기본 MyMemory 무키·무료). 사용자가
///   설정 화면(SettingsActivity)에서 엔진/DeepL 키/대상 언어를 바꾸면 <see cref="ApplyTranslationSettings"/>로
///   재시작 없이 재구성한다(Windows 설정 저장 흐름과 동일 — 다음 곡/재검색부터 새 엔진 적용).
/// - 대상 언어: 설정값이 있으면 그것, 없으면 기기 로케일 → DeepL target_lang 코드(Windows와 동일 규칙)
/// - 캐시: FilesDir/translations.db 단일 SQLite 파일(라인 번역 캐시 + 곡 단위 가사 캐시)
/// - 텔레메트리: 미연결(팩토리 기본 NoopTelemetry — 수집하지 않음, ADR-0004)
/// </summary>
[Application(Name = "com.countnine.musebase.MusebaseApp")]
public sealed class MusebaseApp : Application
{
    public MusebaseApp(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership) { }

    /// <summary>프로세스 유일 인스턴스(OnCreate 이후 비-null).</summary>
    public static MusebaseApp? Instance { get; private set; }

    public AndroidNowPlayingSource Source { get; private set; } = null!;
    public LyricsCoordinator Coordinator { get; private set; } = null!;

    /// <summary>앱 설정(번역 엔진/DeepL 키/대상 언어). SettingsActivity가 읽고 쓴다.</summary>
    public AndroidSettings Settings { get; private set; } = null!;

    // 번역 엔진 재구성에 필요한 불변 컨텍스트 — OnCreate에서 1회 보관.
    private ITranslationCache _translationCache = null!;
    private string _dbPath = null!;

    /// <summary>
    /// 마지막 가사 검색 상태. StatusChanged는 재생 이벤트 시점에만 발화하므로
    /// 나중에 붙는 Activity의 초기 표시용으로 보관한다.
    /// </summary>
    public LyricsStatus LastStatus { get; private set; } = new(LyricsStatusKind.NoTrack);

    public override void OnCreate()
    {
        base.OnCreate();
        Instance = this;

        Source = new AndroidNowPlayingSource(this);
        Settings = new AndroidSettings(this);

        _dbPath = Path.Combine(FilesDir!.AbsolutePath, "translations.db");

        // Microsoft.Data.Sqlite(bundle_e_sqlite3 포함)가 net8.0-android 네이티브 e_sqlite3를
        // 함께 배치하므로 별도 초기화가 필요 없다(SqliteConnection이 Batteries_V2.Init 수행).
        _translationCache = new SqliteTranslationCache(_dbPath);

        var config = BuildConfig(); // 설정값(엔진/키/대상 언어)으로 구성 — 하드코딩 없음(기본은 여전히 mymemory)

        Coordinator = LyricsEngineFactory.Create(
            Source, new AndroidEngineDispatcher(), config, _translationCache,
            log: msg => global::Android.Util.Log.Info("Musebase", msg));
        Coordinator.StatusChanged += s => LastStatus = s;

        global::Android.Util.Log.Info("Musebase",
            $"engine assembled: sources=[{string.Join(",", config.EnabledLyricsSources)}] " +
            $"engine={config.TranslationEngineId} target={config.TargetLanguage} db={_dbPath}");

        Source.Start();      // 권한 없어도 폴링 대기 — 권한이 켜지면 즉시 감지
        Coordinator.Start(); // 속성 배선 완료 후 시작(현재 트랙 재생 중이면 즉시 검색)
    }

    /// <summary>현재 <see cref="AndroidSettings"/> 값으로 엔진 구성값을 만든다(조립·재구성 공용).</summary>
    private EngineConfig BuildConfig()
    {
        var engineId = Settings.EffectiveTranslationEngine; // 저장값 우선, 빈값이면 키 유무로 결정
        var deeplKey = Settings.DeeplApiKey;
        var target = Settings.TargetLanguage ?? DefaultTargetLanguage(); // 비면 기기 로케일 기본값
        return new EngineConfig(
            EnabledLyricsSources: LyricsSourceRegistry.AllIds,
            TranslationEngineId: engineId,
            TranslatorOptions: new TranslatorOptions(DeeplApiKey: deeplKey),
            TargetLanguage: target,
            ShowOnlyTargetTranslation: true,
            ManualOffsetSeconds: 0,
            CacheDbPath: _dbPath);
    }

    /// <summary>
    /// 설정 화면에서 엔진/DeepL 키/대상 언어를 저장한 뒤 호출 — 재시작 없이 번역을 재구성한다.
    /// <see cref="LyricsEngineFactory.ApplyTranslation"/>이 실패 라우팅·대상 언어·현재 라인 재발행을
    /// 처리한다. 새 엔진은 <b>다음 곡/재검색부터</b> 적용된다(현재 곡의 기존 tr 태그는 유지 — Windows와 동일).
    /// </summary>
    public void ApplyTranslationSettings()
    {
        if (Coordinator is null) return;
        var config = BuildConfig();
        LyricsEngineFactory.ApplyTranslation(Coordinator, config, _translationCache);
        global::Android.Util.Log.Info("Musebase",
            $"translation reconfigured: engine={config.TranslationEngineId} " +
            $"target={config.TargetLanguage} deeplKey={(string.IsNullOrEmpty(config.TranslatorOptions.DeeplApiKey) ? "-" : "set")}");
    }

    /// <summary>
    /// 기기 로케일 → DeepL target_lang 코드. Windows판 AppSettings.DefaultTargetLanguage와
    /// 같은 규칙(EN→EN-US, PT 지역 분기, ZH 번체 판별, 그 외 지원 2글자 코드, 미지원→EN-US).
    /// </summary>
    internal static string DefaultTargetLanguage()
    {
        var locale = Java.Util.Locale.Default!;
        var two = (locale.Language ?? "").ToUpperInvariant();
        if (two == "IN") two = "ID"; // Java 레거시 코드(Indonesian) 정규화
        var country = (locale.Country ?? "").ToUpperInvariant();
        return two switch
        {
            "EN" => "EN-US",
            "PT" => country == "PT" ? "PT-PT" : "PT-BR",
            "ZH" => string.Equals(locale.Script, "Hant", StringComparison.OrdinalIgnoreCase)
                    || country is "TW" or "HK" or "MO" ? "ZH-HANT" : "ZH",
            // DeepL이 target_lang으로 지원하는 언어면 2글자 코드 그대로 사용
            "KO" or "JA" or "DE" or "FR" or "ES" or "IT" or "NL" or "PL" or "RU" or "UK" or "TR"
                or "CS" or "ID" or "AR" or "VI" or "BG" or "DA" or "EL" or "ET" or "FI" or "HU"
                or "LT" or "LV" or "NB" or "RO" or "SK" or "SL" or "SV" => two,
            _ => "EN-US", // 미지원 기기 언어 → 영어
        };
    }
}
