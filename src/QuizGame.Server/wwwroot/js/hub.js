// SignalR connection with automatic reconnect (plan §7.5).
// The server identifies the player from the token on every (re)connect and pushes a fresh RoomState,
// so no explicit rejoin call is needed over the socket.
import { HUB_PATH, Events, Methods } from './hubNames.js';

/**
 * @param {string} token player JWT
 * @param {{ onState: (state: 'online'|'reconnecting'|'offline') => void, [event: string]: Function }} handlers
 */
export function createHub(token, handlers) {
  const connection = new signalR.HubConnectionBuilder()
    .withUrl(HUB_PATH, { accessTokenFactory: () => token })
    .withAutomaticReconnect([0, 1000, 2000, 5000, 10000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();

  for (const event of Object.values(Events)) {
    connection.on(event, (payload) => handlers[event]?.(payload));
  }

  connection.onreconnecting(() => handlers.onState('reconnecting'));
  connection.onreconnected(() => handlers.onState('online'));
  connection.onclose(() => handlers.onState('offline'));

  return {
    async start() {
      await connection.start();
      handlers.onState('online');
    },
    stop: () => connection.stop(),
    get isConnected() { return connection.state === signalR.HubConnectionState.Connected; },
    get isDisconnected() { return connection.state === signalR.HubConnectionState.Disconnected; },
    leaveRoom: () => connection.invoke(Methods.LeaveRoom),
    submitAnswer: (answer) => connection.invoke(Methods.SubmitAnswer, answer),
    ping: () => connection.invoke(Methods.Ping),
    rejoin: () => connection.invoke(Methods.Rejoin),
  };
}
