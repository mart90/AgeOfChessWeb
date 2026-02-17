import * as signalR from '@microsoft/signalr';

let connection = null;

/**
 * Build (or reuse) the SignalR connection.
 * Passes the JWT from localStorage as the access token so the hub can
 * associate the player with their registered user account.
 */
export async function startHub() {
  if (connection && connection.state !== signalR.HubConnectionState.Disconnected) {
    return connection;
  }

  connection = new signalR.HubConnectionBuilder()
    .withUrl('/hub/game', {
      // accessTokenFactory is called on every connect/reconnect, so it always
      // picks up the latest token even if the user logged in after the page load.
      accessTokenFactory: () => localStorage.getItem('auth_token') ?? '',
    })
    .withAutomaticReconnect()
    .build();

  await connection.start();
  return connection;
}
