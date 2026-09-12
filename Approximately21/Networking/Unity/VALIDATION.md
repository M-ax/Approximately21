# Runtime integration validation

The plugin registers `BlackjackNetworkingRuntime` before the table component. Its
Unity `Update` pumps the service independently of rendering. `ServiceChanged`
notifies consumers when the service/registry is replaced or released; subscribe
to the current service's events again after replacement. A null service or zero
`LocalPlayerId` is not ready for input.

`Networking.BlackjackChannel` in the BepInEx configuration defaults to 21021.
Every peer must choose the same otherwise-unused channel and restart after a
change. This number does not reserve the channel or guarantee isolation from
other plugins. The transport closes only its channel, not the game's sessions.

## Checked against installed interop assemblies

- `Core.Get()._steam` provides the `SteamManager` instance. `IsHost`,
  `_lobbyState`, and `_clientConnection` are instance members, not static fields.
- Clients read the connected game's remote Steam identity through
  `SteamNetworkingSockets.GetConnectionInfo`; lobby ownership is never used as
  proof of host identity. Hosts intersect lobby membership with authenticated,
  connected entries in `_steamToConn` to detect game disconnections.
- The retained `Callback<SteamNetworkingMessagesSessionRequest_t>` uses the
  game's callback loop; no Steam initialization or extra callback pump is added.
- Shutdown prefixes cover `Core.Dispose`, `SteamManager.Dispose`,
  `SteamManager.ShutdownEverything`, and `SteamAPI.Shutdown`. Hook failure disables
  networking. After Steam API shutdown this runtime remains stopped.
- Registry discovery queries `SCPrefab` and `NetcoreEntity` on the same entity in
  `World.DefaultGameObjectInjectionWorld`, excluding prefabs through default ECS
  query behavior. It never walks to the ship root or substitutes an entity index.
  `NetcoreEntity._id` is a signed int; its native Null sentinel is excluded.

## Live acceptance still blocked

No compatible two-peer game environment was available during this step. A clean
interop build and managed tests do **not** establish IL2CPP generic callback
instantiation, callback delivery, Harmony shutdown ordering, or block mapping in
the running game. These remain acceptance gates:

1. Confirm the default injection world is the placed gameplay world, and each
   placed table has both queried components on its individual block entity.
   Logs include world name, full entity index/version, and native ID. Compare two
   tables on one ship and compare each native ID across peers.
2. Confirm session-request callback registration/delivery and that connected host
   identity and membership match the actual game host, including a lobby owner
   different from that host. Observe readiness and snapshot convergence.
3. Hide/unload only renderer entities: tables must persist. Destroy a block: only
   that table must disappear. Reuse a native ID: the host-issued GUID suffix must
   change. Exercise late joins, reconnects, core/world replacement, and lobby exit.
4. Confirm channel closure and callback disposal occur before Steam shutdown.

Managed coverage uses the existing in-memory networking rig for two-table
isolation, early replicas, late local binding, removal/reuse, resets, ambiguity,
and the retained 256-identity epoch limit. Lobby-resolution tests exercise
verified host selection and fail-closed readiness/membership decisions.