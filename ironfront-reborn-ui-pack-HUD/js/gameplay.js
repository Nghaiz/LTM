export const LOADOUT_OPTIONS = {
  primary: ['SL-DEFENDER', 'KAR-98', 'AR-7 VANGUARD'],
  secondary: ['P-12', 'S-IND7', 'REVOLVER .44'],
  gadget1: ['MEDIPACK', 'REPAIR TOOL', 'MOTION SENSOR'],
  gadget2: ['BINOCS', 'SMOKE GRENADE', 'FRAG GRENADE'],
  gadget3: ['AMMO BAG', 'ARMOR PLATE', 'SPAWN BEACON']
};

export function createMatch(team = 'blue', config = {}) {
  return {
    phase: 'deploy',
    team,
    score: { blue: 0, orange: 0 },
    players: 2,
    health: 100,
    ammo: 8,
    reserve: 40,
    spawn: 'A',
    loadout: {
      primary: 'SL-DEFENDER',
      secondary: 'P-12',
      gadget1: 'MEDIPACK',
      gadget2: 'BINOCS',
      gadget3: 'AMMO BAG'
    },
    config
  };
}

export function selectSlot(match, slot, item) {
  if (!LOADOUT_OPTIONS[slot]?.includes(item)) return match;
  return { ...match, loadout: { ...match.loadout, [slot]: item } };
}

export function selectSpawn(match, point) {
  if (!['A', 'B', 'C'].includes(point)) return match;
  return { ...match, spawn: point };
}

export function deploy(match) {
  return { ...match, phase: 'playing', health: 100, ammo: 8 };
}

export function fire(match) {
  if (match.phase !== 'playing' || match.ammo <= 0) return match;
  return { ...match, ammo: match.ammo - 1 };
}

export function reload(match) {
  if (match.phase !== 'playing') return match;
  const refill = Math.min(8 - match.ammo, match.reserve);
  return { ...match, ammo: match.ammo + refill, reserve: match.reserve - refill };
}

export function damage(match, amount) {
  if (match.phase !== 'playing') return match;
  const health = Math.max(0, match.health - Math.max(0, amount));
  return { ...match, health, phase: health === 0 ? 'eliminated' : 'playing' };
}

export function respawn(match) {
  return { ...match, phase: 'deploy', health: 100, ammo: 8 };
}

export function scorePoint(match, team) {
  if (!['blue', 'orange'].includes(team)) return match;
  return { ...match, score: { ...match.score, [team]: match.score[team] + 1 } };
}
