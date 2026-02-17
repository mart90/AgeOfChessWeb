import * as signalR from '@microsoft/signalr';

let connection = null;

/**
 * Build (or reuse) the SignalR connection to the matchmaking hub.
 * Passes the JWT from localStorage so logged-in users get Elo-based matching.
 */
export async function startMatchmakingHub() {
  if (connection && connection.state !== signalR.HubConnectionState.Disconnected) {
    return connection;
  }

  connection = new signalR.HubConnectionBuilder()
    .withUrl('/hub/matchmaking', {
      accessTokenFactory: () => localStorage.getItem('auth_token') ?? '',
    })
    .build();

  await connection.start();
  return connection;
}

export async function stopMatchmakingHub() {
  if (connection && connection.state !== signalR.HubConnectionState.Disconnected) {
    await connection.stop();
  }
  connection = null;
}
