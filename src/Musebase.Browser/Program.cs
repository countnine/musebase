// Musebase.Browser — PlaybackViewState를 WebSocket으로 방송하고 정적 웹 디스플레이를 서빙하는
// 서버(Phase 1). 계약: contracts/playback-view-state.md (System.Text.Json 기본 = PascalCase).
//
// 이 파일은 얇은 CLI 껍데기다 — 실제 서버 구성은 BrowserDisplayServer(다른 앱에
// 인프로세스로 내장 가능한 공개 API)에 있다.
//
// 엔드포인트:
//   GET /healthz  — 상태 확인
//   WS  /ws       — PlaybackViewState JSON 방송 구독(접속 즉시 현재 상태 1회 수신)
//   GET /         — 임베디드 웹 디스플레이(index.html)
//
// 실행: dotnet run                      (기본 http://localhost:5123)
//       dotnet run -- --demo            (샘플 가사 순환 방송)
//       dotnet run -- --urls http://0.0.0.0:5123   (LAN 공개 시)

using Musebase.Browser;

var demo = args.Contains("--demo");
var serverArgs = args.Where(static a => a != "--demo").ToArray();

await using var server = await BrowserDisplayServer.StartAsync(
    new BrowserDisplayOptions(Log: Console.WriteLine), serverArgs, CancellationToken.None);

if (demo)
    _ = DemoLoop.RunAsync(server.Broadcaster, server.ApplicationStopping);

await server.WaitForShutdownAsync();
