using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Musebase.Engine;

namespace Musebase.Browser;

/// <summary>
/// 최신 <see cref="PlaybackViewState"/>를 보관하고, 연결된 모든 WebSocket 클라이언트에
/// JSON(System.Text.Json 기본 = PascalCase, contracts/playback-view-state.md)으로 방송한다.
/// 새 클라이언트가 접속하면 현재 상태를 즉시 1회 전송한다.
///
/// 라이브러리처럼도 쓸 수 있다: Windows 앱이 <c>LyricsCoordinator.StateChanged</c>를
/// <see cref="Publish"/>에 물리면 원격 디스플레이로 상태가 흘러간다.
/// 모든 공개 멤버는 스레드 안전하다.
/// </summary>
public sealed class StateBroadcaster
{
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private volatile string? _latestJson;

    /// <summary>현재 연결된 구독자 수(진단용).</summary>
    public int ClientCount => _clients.Count;

    /// <summary>
    /// 새 표시 상태를 방송한다. 어느 스레드에서 호출해도 안전하며 블로킹하지 않는다
    /// (느린 클라이언트는 자기 큐에서 오래된 상태부터 버린다 — 최신 상태만 의미가 있으므로).
    /// </summary>
    public void Publish(PlaybackViewState state)
    {
        var json = JsonSerializer.Serialize(state);
        _latestJson = json;
        foreach (var client in _clients.Values)
            client.Enqueue(json);
    }

    /// <summary>
    /// 수락된 WebSocket 하나를 구독자로 등록하고 연결이 끝날 때까지 처리한다.
    /// 등록 직후 현재 상태(있으면)를 1회 즉시 전송한다. 반환되면 구독이 해제된 것이다.
    /// </summary>
    public async Task HandleClientAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var client = new Client(socket);
        _clients[id] = client;
        if (_latestJson is { } latest)
            client.Enqueue(latest);
        try
        {
            await client.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _clients.TryRemove(id, out _);
        }
    }

    /// <summary>구독자 1명: 전송 큐(최신 우선, 오래된 것 드롭) + 닫힘 감지 수신 루프.</summary>
    private sealed class Client(WebSocket socket)
    {
        private readonly Channel<string> _outbox = Channel.CreateBounded<string>(
            new BoundedChannelOptions(capacity: 16)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

        public void Enqueue(string json) => _outbox.Writer.TryWrite(json);

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var send = SendLoopAsync(cts.Token);
            var receive = ReceiveLoopAsync(cts.Token);
            await Task.WhenAny(send, receive).ConfigureAwait(false);
            cts.Cancel();
            try
            {
                await Task.WhenAll(send, receive).ConfigureAwait(false);
            }
            catch
            {
                // 취소·연결 끊김은 정상 종료 흐름.
            }

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // 이미 끊긴 소켓 — 무시.
                }
            }
        }

        private async Task SendLoopAsync(CancellationToken ct)
        {
            await foreach (var json in _outbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct)
                    .ConfigureAwait(false);
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            // 구독자는 데이터를 보내지 않지만, Close 프레임/끊김을 감지하려면 계속 읽어야 한다.
            var buffer = new byte[1024];
            while (!ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
            }
        }
    }
}
