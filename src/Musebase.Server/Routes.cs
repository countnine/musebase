namespace Musebase.Server;

/// <summary>
/// 이 서버가 자리 잡는 경로. 한 호스트에 여러 서비스를 얹을 수 있도록 <b>전부 한 접두사 아래</b>에 둔다
/// (`tailscale serve` 한 대에 musebase 말고 다른 것을 더 붙일 수 있다).
///
/// <b>경로는 여기 한 곳에서만 정한다.</b> 관리자 쿠키의 <c>Path</c>와 라우트 접두사가 어긋나면
/// 로그인은 되는데 로그아웃이 쿠키를 못 지우는 식으로 조용히 깨진다 — 그래서 리터럴을 흩어 두지 않는다.
///
/// 앱은 <see cref="Api"/>만 쓴다(`contracts/lyrics-api.md`). 나머지 <see cref="Base"/> 아래는
/// 사람용 관리 화면이라 계약 밖이며 예고 없이 바뀔 수 있다.
/// </summary>
public static class Routes
{
    /// <summary>관리 화면의 뿌리. 대시보드가 여기.</summary>
    public const string Base = "/musebase";

    /// <summary>앱이 쓰는 API. 클라이언트는 서버 주소 뒤에 <c>v1/…</c>을 상대 경로로 붙인다.</summary>
    public const string Api = Base + "/v1";
}
