// Deliberately synthetic IDs, identity, clock, and counters. No network or steam-user import.
export const fixturePlan = {
  version: 1, expectedSteamId: '76561198000000000', readbackDelaySeconds: 5,
  targets: [
    { appId: 1001, stopAfterSeconds: 120, uninstalled: true, windowsOnly: true },
    { appId: 1002, stopAfterSeconds: 240, uninstalled: false, windowsOnly: false }
  ]
};

export function createFixture() {
  let time = 0;
  let playing = [];
  const minutes = new Map([[1001, 10], [1002, 20]]);
  const commands = [];
  return {
    now: () => time,
    wait: async (ms, signal) => {
      signal.throwIfAborted();
      time += ms;
      for (const appId of playing) minutes.set(appId, minutes.get(appId) + ms / 60000);
    },
    commands,
    transport: {
      assertReady() {},
      setPlaying(appIds) { playing = [...appIds]; commands.push([...appIds]); },
      async readOwnedApps() {
        return [...minutes].map(([appid, value]) => ({ appid, playtime_forever: Math.floor(value) }));
      },
      async close() { playing = []; }
    }
  };
}
