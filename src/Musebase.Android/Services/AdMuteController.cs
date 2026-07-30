using Android.Content;
using Android.OS;

namespace Musebase.Android.Services;

/// <summary>
/// 광고 감지(<see cref="AndroidNowPlayingSource"/>)와 볼륨 제어(<see cref="MediaVolumeMuter"/>)를 잇는다.
///
/// 포그라운드 서비스를 따로 두지 않는다 — 알림 접근이 켜져 있으면 시스템이
/// <see cref="MediaListenerService"/>를 계속 바인드해 프로세스가 살아 있으므로,
/// <see cref="MusebaseApp"/>이 이 객체를 들고 있기만 하면 된다. 알림바 항목이 늘지 않는다.
///
/// 대상은 Spotify로 한정한다. 광고 플래그를 다른 앱이 어떻게 쓰는지 검증된 바 없다.
/// </summary>
public sealed class AdMuteController
{
    /// <summary>Spotify 안드로이드 앱 패키지.</summary>
    public const string SpotifyPackage = "com.spotify.music";

    /// <summary>곡 전환 순간 메타데이터가 비는 구간을 광고로 오인하지 않기 위한 유예.</summary>
    private static readonly TimeSpan EnterDebounce = TimeSpan.FromMilliseconds(150);

    /// <summary>뮤트 중 사용자 볼륨 개입을 확인하고, 안전 상한을 시간에 맞춰 발동시키는 주기.</summary>
    private const int TickIntervalMs = 1000;

    private readonly Context _context;
    private readonly AndroidNowPlayingSource _source;
    private readonly MediaVolumeMuter _muter;
    private readonly AndroidSettings _settings;
    private readonly Handler _handler = new(Looper.MainLooper!);

    private AdDecision _decision;
    private bool _subscribed;
    private bool _debounceRecheckPending;

    /// <summary>마지막으로 뮤트를 적용한 광고의 식별자. 여기서 바뀌면 새 광고 세그먼트다.</summary>
    private string? _mutedAdId;

    public AdMuteController(Context context, AndroidNowPlayingSource source, AndroidSettings settings)
    {
        _context = context.ApplicationContext ?? context;
        _source = source;
        _settings = settings;
        _muter = new MediaVolumeMuter(_context, settings);
        _decision = NewDecision();
    }

    /// <summary>
    /// 이전 세션이 광고 도중 죽어 볼륨이 0으로 남았으면 되돌린다.
    /// <b>설정이 꺼져 있어도 앱 시작 시 호출한다</b> — 켠 채로 죽었을 수 있다.
    /// </summary>
    public void RestoreOrphanedVolume() => _muter.RestoreOrphanedVolume();

    /// <summary>설정값에 맞춰 구독을 붙이거나 뗀다(저장 시 호출 — 재시작 불필요).</summary>
    public void Apply()
    {
        if (_settings.AdMuteEnabled) Subscribe();
        else Unsubscribe();
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;

        // 상한 설정이 바뀌었을 수 있으므로 새로 만든다.
        _decision = NewDecision();

        _source.IsAdvertisementChanged += OnAdSignalChanged;
        // 재생 상태도 구독해야 한다 — 판정이 IsAdvertisement && IsPlaying이므로 재생만 바뀌어도
        // 결과가 달라진다. 실측(광고 1/2 → 2/2 연속 구간)에서 이게 빠져 있어 두 번째 광고를
        // 1.6초 늦게 잡았다: 두 광고 사이에 재생이 잠깐 끊기는데 IsAdvertisement는 계속 true라
        // 광고 이벤트가 발화하지 않고, 1초 틱을 기다려야 했다.
        _source.IsPlayingChanged += OnPlaybackChanged;
        ScheduleTick();
        global::Android.Util.Log.Info("Musebase", "ad-mute: 켜짐");

        // 지금 이미 광고 중일 수 있다.
        Evaluate();
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _subscribed = false;

        _source.IsAdvertisementChanged -= OnAdSignalChanged;
        _source.IsPlayingChanged -= OnPlaybackChanged;
        _handler.RemoveCallbacksAndMessages(null);
        // 위에서 예약된 콜백이 취소되므로 플래그도 함께 내려야 다시 켤 때 예약이 막히지 않는다.
        _debounceRecheckPending = false;
        _decision.Reset();
        _muter.Unmute();
        global::Android.Util.Log.Info("Musebase", "ad-mute: 꺼짐");
    }

    private AdDecision NewDecision() =>
        new(EnterDebounce, TimeSpan.FromSeconds(_settings.AdMuteMaxSeconds));

    private void OnAdSignalChanged(bool _) => Evaluate();

    private void OnPlaybackChanged(bool _) => Evaluate();

    /// <summary>
    /// 틱마다 재평가한다. 이벤트만으로는 안전 상한을 시간에 맞춰 발동시킬 수 없고
    /// (신호가 계속 "광고"면 이벤트가 안 온다), 뮤트 중 사용자 개입 확인도 필요하다.
    /// </summary>
    private void ScheduleTick()
    {
        if (!_subscribed) return;
        _handler.PostDelayed(() =>
        {
            if (!_subscribed) return;
            _muter.CheckUserOverride();
            Evaluate();
            ScheduleTick();
        }, TickIntervalMs);
    }

    /// <summary>디바운스가 끝나는 시점에 재평가를 한 번 예약한다(중복 예약 방지).</summary>
    private void ScheduleDebounceRecheck()
    {
        if (_debounceRecheckPending) return;
        _debounceRecheckPending = true;

        _handler.PostDelayed(() =>
        {
            _debounceRecheckPending = false;
            if (_subscribed) Evaluate();
        }, (long)EnterDebounce.TotalMilliseconds + 30);
    }

    private void Evaluate()
    {
        if (!_subscribed) return;

        // Spotify 세션이 아니면 광고 판정 자체를 하지 않는다. 뮤트 중이었다면 풀어 준다.
        var isSpotify = string.Equals(
            _source.CurrentSourcePackage, SpotifyPackage, StringComparison.OrdinalIgnoreCase);

        // 재생이 멈춰 있으면 "광고 아님"이 아니라 "모름"이다. 광고 2개가 연속될 때 그 사이에
        // 재생이 잠깐 끊기는데, 이를 광고 종료로 보면 볼륨이 1.3초 돌아왔다가 다시 내려가
        // 광고 소리가 새어 나온다(실측). AdDecision이 짧은 공백 동안 판정을 유지한다.
        var signal =
            !isSpotify ? AdSignal.NotAd
            : !_source.IsPlaying ? AdSignal.Unknown
            : _source.IsAdvertisement ? AdSignal.Ad
            : AdSignal.NotAd;

        // 광고가 다음 세그먼트(1/2 → 2/2)로 넘어갔으면 사용자 개입 기억을 지운다.
        // 볼륨을 올린 것은 "그 광고를 듣겠다"는 뜻이지 "남은 광고를 전부 듣겠다"는 뜻이 아니다.
        var adId = _source.AdvertisementId;
        if (signal == AdSignal.Ad && adId is not null && adId != _mutedAdId)
        {
            _mutedAdId = adId;
            _muter.ResetUserOverride();
        }
        else if (signal == AdSignal.NotAd)
        {
            _mutedAdId = null;
            _muter.ResetUserOverride();
        }

        var tripsBefore = _decision.SafetyTrips;
        if (!_decision.Advance(signal, DateTime.UtcNow))
        {
            // 디바운스 대기 중이면 그 시점에 스스로 다시 본다. 이걸 안 하면 다음 재평가가
            // 1초 틱까지 밀려 150ms 디바운스가 실질 1초가 된다 — 실측에서 연속 광고 2/2를
            // 1.2초 늦게 잡은 원인이었다(광고 소리가 그만큼 그대로 난다).
            if (signal == AdSignal.Ad && !_decision.IsAd) ScheduleDebounceRecheck();

            // 상태는 그대로여도(계속 광고) 세그먼트가 바뀌어 개입 기억이 풀렸을 수 있다.
            // 그 경우 여기서 다시 내려 준다 — 상태 전이가 없어 아래 블록을 타지 않는다.
            if (_decision.IsAd && _muter.Mute())
                global::Android.Util.Log.Info("Musebase", "ad-mute: 다음 광고 → 미디어 볼륨 0");

            return;
        }

        if (_decision.IsAd)
        {
            // 실제로 볼륨을 내렸을 때만 그렇게 말한다 — 로그가 이 플랫폼의 유일한 프로브라
            // 사실과 다르면 나중에 디버깅을 헛돌게 만든다.
            if (_muter.Mute())
            {
                global::Android.Util.Log.Info("Musebase", "ad-mute: 광고 감지 → 미디어 볼륨 0");
                if (_settings.AdMuteNotify) Toast("광고를 음소거했습니다");
            }
            else
            {
                global::Android.Util.Log.Info("Musebase",
                    "ad-mute: 광고 감지 — 볼륨은 그대로 둠(사용자 개입 구간이거나 이미 0)");
            }
        }
        else
        {
            var restored = _muter.Unmute();
            if (_decision.SafetyTrips > tripsBefore)
            {
                global::Android.Util.Log.Warn("Musebase",
                    $"ad-mute: 광고 상태가 {_settings.AdMuteMaxSeconds}초를 넘겨 강제 복구 " +
                    "(감지 실패 가능성)");
                // 알림 설정과 무관하게 항상 알린다 — 감지가 실패했다는 뜻이라 사용자가 알아야 한다.
                Toast($"광고 뮤트: {_settings.AdMuteMaxSeconds}초를 넘겨 볼륨을 되돌렸습니다");
            }
            else
            {
                global::Android.Util.Log.Info("Musebase",
                    restored ? "ad-mute: 광고 종료 → 볼륨 복구"
                             : "ad-mute: 광고 종료 — 되돌릴 것 없음(사용자 개입 구간)");
            }
        }
    }

    /// <summary>
    /// 짧은 안내. 포그라운드 서비스를 새로 만들지 않기로 했으므로 알림 채널 대신 토스트를 쓴다
    /// (기본 텍스트 토스트는 백그라운드에서도 허용된다 — 커스텀 뷰 토스트만 Android 11+에서 막힌다).
    /// </summary>
    private void Toast(string message) => _handler.Post(() =>
    {
        try
        {
            global::Android.Widget.Toast
                .MakeText(_context, message, global::Android.Widget.ToastLength.Short)?.Show();
        }
        catch (Exception e)
        {
            global::Android.Util.Log.Warn("Musebase", $"ad-mute toast: {e.Message}");
        }
    });

    /// <summary>앱 완전 종료 경로에서 호출 — 볼륨을 남기지 않는다.</summary>
    public void Shutdown()
    {
        Unsubscribe();
        _muter.Unmute();
    }
}
