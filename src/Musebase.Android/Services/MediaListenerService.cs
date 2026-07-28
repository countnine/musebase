using Android.App;
using Android.Service.Notification;

namespace Musebase.Android.Services;

/// <summary>
/// 알림 접근(notification access) 권한의 앵커가 되는 <see cref="NotificationListenerService"/>.
///
/// Android에서 <c>MediaSessionManager.GetActiveSessions()</c>는 알림 접근이 허용된
/// NotificationListenerService의 ComponentName을 요구한다. 즉 이 서비스가 하는 일은
/// "권한의 근거"가 전부다 — 알림 자체를 파싱하지 않으며, 사용자가 설정에서 알림 접근을
/// 켜면 시스템이 이 서비스를 바인드하고, 그때부터 임의 컨텍스트에서
/// GetActiveSessions(component)가 미디어 세션 목록을 반환한다.
///
/// 매니페스트의 service 선언(BIND_NOTIFICATION_LISTENER_SERVICE 권한 + 인텐트 필터)은
/// 아래 특성에서 생성된다. Name을 고정해 ACW(Java 래퍼) 클래스명이 빌드마다
/// 흔들리지 않게 한다(설정 화면에서 사용자가 켠 토글이 유지되도록).
/// </summary>
[Service(
    Label = "Musebase",
    Name = "com.countnine.musebase.MediaListenerService",
    Exported = true,
    Permission = global::Android.Manifest.Permission.BindNotificationListenerService)]
[IntentFilter(new[] { "android.service.notification.NotificationListenerService" })]
public sealed class MediaListenerService : NotificationListenerService
{
    /// <summary>시스템이 리스너를 바인드했는지(알림 접근 허용 + 연결 완료).</summary>
    public static bool IsConnected { get; private set; }

    /// <summary>바인드된 인스턴스(앱 종료 시 <c>RequestUnbind()</c>를 호출하기 위해 보관).</summary>
    private static MediaListenerService? _instance;

    public override void OnListenerConnected()
    {
        base.OnListenerConnected();
        _instance = this;
        IsConnected = true;
        global::Android.Util.Log.Info("Musebase", "MediaListenerService connected (notification access granted).");
    }

    public override void OnListenerDisconnected()
    {
        base.OnListenerDisconnected();
        if (ReferenceEquals(_instance, this)) _instance = null;
        IsConnected = false;
        global::Android.Util.Log.Info("Musebase", "MediaListenerService disconnected.");
    }

    /// <summary>
    /// 시스템 바인드를 스스로 끊는다(앱 완전 종료용). 권한 설정은 그대로 유지되므로,
    /// 앱을 다시 열 때 <see cref="Rebind"/>로 복귀한다. API 24 미만은 지원하지 않아 무시된다.
    /// </summary>
    public static void Unbind()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(24)) return;
        try { _instance?.RequestUnbind(); }
        catch (Exception e) { global::Android.Util.Log.Warn("Musebase", $"unbind: {e.Message}"); }
    }

    /// <summary>끊어 둔 바인드를 다시 요청한다(앱 재실행 시). 이미 연결돼 있으면 아무 일도 하지 않는다.</summary>
    public static void Rebind(global::Android.Content.Context context)
    {
        if (IsConnected || !OperatingSystem.IsAndroidVersionAtLeast(24)) return;
        try
        {
            RequestRebind(new global::Android.Content.ComponentName(
                context, Java.Lang.Class.FromType(typeof(MediaListenerService))));
        }
        catch (Exception e) { global::Android.Util.Log.Warn("Musebase", $"rebind: {e.Message}"); }
    }

    // 알림 내용은 사용하지 않는다 — 재생 정보는 MediaSessionManager 경유(AndroidNowPlayingSource).
    public override void OnNotificationPosted(StatusBarNotification? sbn) { }
    public override void OnNotificationRemoved(StatusBarNotification? sbn) { }
}
