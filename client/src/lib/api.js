export async function createGame(options = {}) {
  const res = await fetch('/api/game', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(options),
  });
  if (!res.ok) throw new Error(await res.text());
  return res.json(); // { gameId, whitePlayerToken, inviteUrl }
}

export async function getGame(gameId) {
  const res = await fetch(`/api/game/${gameId}`);
  if (!res.ok) throw new Error(await res.text());
  return res.json();
}
