/*
 * ChibiNames: Chibi Clash worldwide-unique player names + player statistics (Unity Cloud Code, JavaScript script).
 *
 * Deploy: Unity Cloud dashboard > Cloud Code > Scripts > Create script, name "Chibi_Clash", paste this file, add the two
 * parameters below (action: String, required; name: String, optional), Publish. Full steps:
 * docs/architecture/netcode/player-names.md. The client calls it by that exact name (ChibiNameRegistry.ScriptName).
 *
 * Actions (params.action):
 *   claim {name}  validate, then reserve the name for the CALLING player (context.playerId) and release that player's
 *                 previous name. Returns { status: "ok" | "taken" | "invalid", name?, reason? }.
 *   check {name}  { status: "available" | "taken" | "yours" | "invalid" } (reserves nothing).
 *   join          count this player's launch, at most once per UTC day. { status: "ok" }.
 *   stats         { status: "ok", players, joins }.
 * Errors that are the server's fault come back as { status: "error" } so the game can fall back to offline rules.
 *
 * Storage: Cloud Save PRIVATE custom items (only server code with the service token can read or write them; players
 * cannot read them through the client SDK, so player ids never leak):
 *   chibi-names-XX        key = lower-case name   value = { playerId, name, claimedAt }   (released names are deleted)
 *   chibi-name-owners-XX  key = player id         value = { name, key, claimedAt, nonce } (reverse index, one per player)
 *   XX = shard 00..ff (FNV-1a hash of the key): Cloud Save allows at most 2000 keys per custom ID, so one custom ID
 *   would cap the game at 2000 names. 256 shards hold about 400 000 names / players before a shard gets near 2000.
 *   chibi-stats        key "players" / "joins" value = { count }   (joins = player-days with a launch)
 *   chibi-joins-XX        key = player id         value = { day, nonce } (last UTC day this player's join was counted)
 * Names never expire (no clean-up of inactive players).
 *
 * Cloud Save server SDK: @unity-services/cloud-save-1.4 (DataApi.getPrivateCustomItems / setPrivateCustomItem; the
 * writeLock on an update makes it fail with HTTP 409 if someone else wrote first). Verified against the 1.4.0 package
 * shipped in com.unity.services.cloudcode 2.10.4 (UnityServicesNpmLocal~/cloud-save-1.4.0.tgz, dist/api.d.ts).
 * Docs: https://docs.unity.com/ugs/en-us/manual/cloud-code/manual/scripts/how-to-guides/integrate-with-other-ugs-services
 *       https://services.docs.unity.com/cloud-save/v1/   (REST reference: custom items, private access class, writeLock)
 *
 * The name rules MUST stay identical to ChibiSocialRules.ValidateName (Assets/ChibiClash/Scripts/Netcode).
 */
const { DataApi } = require("@unity-services/cloud-save-1.4");

const NAMES = "chibi-names";        // + "-XX" shard
const OWNERS = "chibi-name-owners"; // + "-XX" shard
const STATS = "chibi-stats";        // 2 keys only: not sharded
const JOINS = "chibi-joins";        // + "-XX" shard; key = player id, value = { day, nonce }: one join per player per UTC day
const SHARDS = 256;                 // never change once live (keys would move to other custom IDs)
const NAME_MIN = 3, NAME_MAX = 16;
const SETTLE_MS = 300;          // wait before the read-back that confirms we still own a freshly created name
const COUNTER_TRIES = 6;        // writeLock retries for a counter under contention
const JOIN_SETTLE_MS = 150;     // join marker read-back delay (same idea as SETTLE_MS)
const RENAME_COOLDOWN_MS = 60000; // one rename per player per minute (stops rename loops filling a shard's 2000 keys)

const BLOCKED = ["fuck", "shit", "bitch", "cunt", "dick", "cock", "pussy", "whore", "slut", "porn", "rape", "nazi",
  "hitler", "nigg", "fag", "retard", "admin", "moderator", "official"];
const ALLOWED = ["grape", "drape", "scrape", "trapeze", "therapist", "cocktail", "peacock", "hancock", "shuttlecock",
  "dickens", "scunthorpe", "shiitake", "skilled", "bitcoin"];
const SWAP = { "0": "o", "1": "i", "3": "e", "4": "a", "5": "s", "7": "t" };

/** Same as ChibiSocialRules.ContainsBlockedWord: lower case, undo digit swaps, keep a-z only, drop harmless words. */
function containsBlockedWord(name) {
  let flat = "";
  for (const ch of name.toLowerCase()) {
    const c = SWAP[ch] || ch;
    if (c >= "a" && c <= "z") flat += c;
  }
  for (const ok of ALLOWED) flat = flat.split(ok).join("-");
  return BLOCKED.some(w => flat.indexOf(w) >= 0);
}

/** Same order and rules as ChibiSocialRules.ValidateName. Returns null when valid, else a reason. */
function validateName(name) {
  if (typeof name !== "string" || name.length === 0) return "Enter a name.";
  if (name.length < NAME_MIN) return "Name is too short.";
  if (name.length > NAME_MAX) return "Name is too long.";
  if (containsBlockedWord(name)) return "Please choose a different name.";
  if (!/^[A-Za-z0-9_]+$/.test(name)) return "Use only letters, numbers and _.";
  return null;
}

const sleep = ms => new Promise(r => setTimeout(r, ms));
const newNonce = () => Math.random().toString(36).slice(2) + Date.now().toString(36);
const conflict = () => Object.assign(new Error("write conflict"), { response: { status: 409 } });

/** Custom ID of the shard that holds `key` in a sharded collection (FNV-1a 32-bit, 2 hex digits). */
function shard(base, key) {
  let h = 0x811c9dc5;
  for (let i = 0; i < key.length; i++) { h ^= key.charCodeAt(i); h = Math.imul(h, 0x01000193) >>> 0; }
  return base + "-" + (h % SHARDS).toString(16).padStart(2, "0");
}
const isConflict = e => e && e.response && e.response.status === 409;

async function getItem(api, projectId, customId, key) {
  const res = await api.getPrivateCustomItems(projectId, customId, [key]);
  const results = (res && res.data && res.data.results) || [];
  return results.find(i => i.key === key) || null;
}

async function setItem(api, projectId, customId, key, value, writeLock) {
  const body = { key, value };
  if (writeLock) body.writeLock = writeLock;
  return api.setPrivateCustomItem(projectId, customId, body);
}

/** Add 1 to a counter with optimistic locking (lost only if the very first create races; documented limit). */
async function increment(api, projectId, key, logger) {
  for (let i = 0; i < COUNTER_TRIES; i++) {
    try {
      const item = await getItem(api, projectId, STATS, key);
      const count = item && item.value && Number.isFinite(item.value.count) ? item.value.count : 0;
      await setItem(api, projectId, STATS, key, { count: count + 1 }, item ? item.writeLock : undefined);
      return;
    } catch (e) {
      if (!isConflict(e)) throw e;
      await sleep(20 + Math.floor(Math.random() * 100 * (i + 1)));
    }
  }
  logger.error("ChibiNames: counter " + key + " busy, increment skipped");
}

async function claim(api, projectId, me, name, logger) {
  const reason = validateName(name);
  if (reason) return { status: "invalid", reason };
  const key = name.toLowerCase();

  const existing = await getItem(api, projectId, shard(NAMES, key), key);
  const owner = existing && existing.value ? existing.value.playerId : "";
  if (owner && owner !== me) return { status: "taken" };

  const prev = await getItem(api, projectId, shard(OWNERS, me), me);
  const now = new Date().toISOString();
  // Rename cooldown (review 2026-11-23): a different name within a minute of the last claim is refused ("wait").
  // Re-claiming the name we already hold is always allowed (the game re-claims before every online session).
  if (prev && prev.value && prev.value.key && prev.value.key !== key) {
    const last = Date.parse(prev.value.claimedAt || "");
    if (Number.isFinite(last) && Date.now() - last < RENAME_COOLDOWN_MS) return { status: "wait" };
  }

  const wasOurs = owner === me;

  // Reserve. An existing item (ours) is updated under its writeLock: atomic. A brand-new key cannot be
  // created conditionally in Cloud Save, so we write, wait, and read back to confirm we are still the owner.
  try {
    await setItem(api, projectId, shard(NAMES, key), key, { playerId: me, name, claimedAt: now }, existing ? existing.writeLock : undefined);
  } catch (e) {
    if (isConflict(e)) return { status: "taken" };
    throw e;
  }
  let cur = null;
  try {
    if (!existing) {
      await sleep(SETTLE_MS);
      const back = await getItem(api, projectId, shard(NAMES, key), key);
      if (!back || !back.value || back.value.playerId !== me) return { status: "taken" };   // someone else won: not ours
    }
    // Reverse index as a compare-and-swap against the version read just now: an update carries its writeLock (409 if
    // a parallel claim of ours wrote first); a first-ever entry is a create (no writeLock possible), so it carries a
    // nonce and is read back: of two parallel first claims only the last writer keeps its name.
    const ownersId = shard(OWNERS, me);
    cur = await getItem(api, projectId, ownersId, me);
    const nonce = newNonce();
    await setItem(api, projectId, ownersId, me, { name, key, claimedAt: now, nonce }, cur ? cur.writeLock : undefined);
    if (!cur) {
      await sleep(JOIN_SETTLE_MS);
      const mine = await getItem(api, projectId, ownersId, me);
      if (!mine || !mine.value || mine.value.nonce !== nonce) throw conflict();
    }
  } catch (e) {
    // Lost the owner-index race (409) or failed half-way: give back the name we just wrote (best effort: if this
    // release also fails, the name stays reserved for us until our next claim; see the doc's limits).
    if (!wasOurs) await releaseIfOurs(api, projectId, key, me, now, logger);
    if (isConflict(e)) return { status: "error" };
    throw e;
  }

  // Release the names the index pointed to before (only if still ours). Released keys are deleted, not kept as
  // tombstones, so renames never use up a shard's 2000-key limit.
  const old = new Set([prev, cur].map(i => (i && i.value ? i.value.key : "")).filter(k => k && k !== key));
  for (const k of old) await releaseIfOurs(api, projectId, k, me, now, logger);
  if (!prev) {   // first name of this player (a parallel first claim that lost returned above; rare double count possible)
    try { await increment(api, projectId, "players", logger); }
    catch (e) { logger.error("ChibiNames: players counter failed: " + (e && e.message)); }
  }
  return { status: "ok", name };
}

/** Delete a name (under its writeLock) if (and only if) it is still ours. Never throws. */
async function releaseIfOurs(api, projectId, key, me, now, logger) {
  try {
    const item = await getItem(api, projectId, shard(NAMES, key), key);
    if (item && item.value && item.value.playerId === me)
      await api.deletePrivateCustomItem(key, projectId, shard(NAMES, key), item.writeLock);
  } catch (e) {
    logger.error("ChibiNames: release of a name failed: " + (e && e.message));
  }
}

/** Count a launch at most once per player per UTC day (marker in chibi-joins, keyed by the server's player id). */
async function join(api, projectId, me, logger) {
  const day = new Date().toISOString().slice(0, 10);
  const mark = await getItem(api, projectId, shard(JOINS, me), me);
  if (mark && mark.value && mark.value.day === day) return { status: "ok" };
  const nonce = newNonce();
  try {
    await setItem(api, projectId, shard(JOINS, me), me, { day, nonce }, mark ? mark.writeLock : undefined);
  } catch (e) {
    if (isConflict(e)) return { status: "ok" };   // a parallel join of ours already counted today
    throw e;
  }
  // A first-ever marker is a create (no writeLock possible): read back, only the last writer counts.
  await sleep(JOIN_SETTLE_MS);
  const back = await getItem(api, projectId, shard(JOINS, me), me);
  if (!back || !back.value || back.value.nonce !== nonce) return { status: "ok" };
  await increment(api, projectId, "joins", logger);
  return { status: "ok" };
}

async function check(api, projectId, me, name) {
  if (validateName(name)) return { status: "invalid" };
  const item = await getItem(api, projectId, shard(NAMES, name.toLowerCase()), name.toLowerCase());
  const owner = item && item.value ? item.value.playerId : "";
  if (!owner) return { status: "available" };
  return { status: owner === me ? "yours" : "taken" };
}

async function stats(api, projectId) {
  const res = await api.getPrivateCustomItems(projectId, STATS, ["joins", "players"]);
  const out = { status: "ok", players: 0, joins: 0 };
  for (const i of (res && res.data && res.data.results) || [])
    if ((i.key === "players" || i.key === "joins") && i.value && Number.isFinite(i.value.count)) out[i.key] = i.value.count;
  return out;
}

module.exports = async ({ params, context, logger }) => {
  const me = context.playerId;
  // Player ids are Cloud Save keys in chibi-name-owners: refuse anything that is not a plain id (and calls without a player).
  if (typeof me !== "string" || !/^[A-Za-z0-9_-]{1,100}$/.test(me)) return { status: "invalid", reason: "Not signed in." };
  const action = typeof params.action === "string" ? params.action : "";
  const name = typeof params.name === "string" ? params.name.trim() : "";
  const api = new DataApi({ accessToken: context.serviceToken });   // server authority: private custom items
  const projectId = context.projectId;
  try {
    switch (action) {
      case "claim": return await claim(api, projectId, me, name, logger);
      case "check": return await check(api, projectId, me, name);
      case "join": return await join(api, projectId, me, logger);
      case "stats": return await stats(api, projectId);
      default: return { status: "invalid", reason: "Unknown action." };
    }
  } catch (e) {
    logger.error("ChibiNames " + action + " failed: " + (e && e.message));
    return { status: "error" };
  }
};

// In-script parameters (used by the Unity editor / UGS CLI deploy; the dashboard needs them added by hand).
module.exports.params = {
  action: { type: "String", required: true },
  name: "String",
};
