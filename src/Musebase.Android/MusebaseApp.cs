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
/// - 번역 엔진: MyMemory (무키·무료 기본 — LibreTranslate 공개 인스턴스가 API 키 필수로 바뀌어
///   무키로 동작하지 않으므로 제외. DeepL 키 입력 UI는 다음 단계)
/// - 대상 언어: 기기 로케일 → DeepL target_lang 코드(Windows AppSettings.DefaultTargetLanguage와 동일 규칙)
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

        var dbPath = Path.Combine(FilesDir!.AbsolutePath, "translations.db");
        var config = new EngineConfig(
            EnabledLyricsSources: LyricsSourceRegistry.AllIds,
            TranslationEngineId: TranslatorRegistry.DefaultFreeEngine, // mymemory — 무키 무료 기본
            TranslatorOptions: new TranslatorOptions(), // 무키
            TargetLanguage: DefaultTargetLanguage(),
            ShowOnlyTargetTranslation: true,
            ManualOffsetSeconds: 0,
            CacheDbPath: dbPath);

        // Microsoft.Data.Sqlite(bundle_e_sqlite3 포함)가 net8.0-android 네이티브 e_sqlite3를
        // 함께 배치하므로 별도 초기화가 필요 없다(SqliteConnection이 Batteries_V2.Init 수행).
        var translationCache = new SqliteTranslationCache(dbPath);

        Coordinator = LyricsEngineFactory.Create(
            Source, new AndroidEngineDispatcher(), config, translationCache,
            log: msg => global::Android.Util.Log.Info("Musebase", msg));
        Coordinator.StatusChanged += s => LastStatus = s;

        global::Android.Util.Log.Info("Musebase",
            $"engine assembled: sources=[{string.Join(",", config.EnabledLyricsSources)}] " +
            $"engine={config.TranslationEngineId} target={config.TargetLanguage} db={dbPath}");

        Source.Start();      // 권한 없어도 폴링 대기 — 권한이 켜지면 즉시 감지
        Coordinator.Start(); // 속성 배선 완료 후 시작(현재 트랙 재생 중이면 즉시 검색)
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
