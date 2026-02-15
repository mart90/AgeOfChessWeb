import * as signalR from '@microsoft/signalr';

let connection = null;

export function getHub() {
  if (!connection) {
    connection = new signalR.HubConnectionBuilder()
      .withUrl('/hub/game')
      .withAutomaticReconnect()
      .build();
  }
  return connection;
}

export async function startHub() {
  const hub = getHub();
  if (hub.state === signalR.HubConnectionState.Disconnected) {
    await hub.start();
  }
  return hub;
}
