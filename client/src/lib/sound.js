const sounds = {
  move: new Audio('/sounds/move.mp3'),
  capture: new Audio('/sounds/capture.mp3'),
  game_started: new Audio('/sounds/game_started.mp3'),
  clock: new Audio('/sounds/clock.mp3')
};

let enabled = true;

export function playSound(name) {
  if (!enabled || !sounds[name]) return;

  const sound = sounds[name];
  sound.currentTime = 0;
  sound.play();
}

export function setSoundEnabled(value) {
  enabled = value;
}

export function setVolume(volume) {
  Object.values(sounds).forEach(sound => {
    sound.volume = volume; // 0.0 - 1.0
  });
}

export function stopClockSound() {
  if (sounds.clock) {
    sounds.clock.pause();
    sounds.clock.currentTime = 0;
  }
}