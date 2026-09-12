# Game state and networking

The game replication framework, blackjack engine, and table presentation controller are managed code, independent of Unity, BepInEx, and ECS. `SteamGameTransport` and the adapters under `Networking/Unity/` provide the live game hookup. `Plugin.Load` registers the networking runtime and the blackjack table; five reusable seat panels replace the local Test button.

## Reusing the framework for another game

- `GameStateService<TGame, TMessage, TSnapshot>` owns lobby/session lifecycle, authoritative tables, synchronization, retries, command deduplication, revisions, removal tombstones, replica caching, and events. It has no blackjack dependencies.
- `GameMessage<TSnapshot>` supplies the common envelope; each game derives a record with its own command fields. `GameProtocol<TMessage, TSnapshot>` handles bounded JSON serialization and common envelope validation, delegating command and snapshot validation to the game.
- `IGameTransport`, `SteamGameTransport`, `LobbyContext`, `NetworkLimits`, and `ReplicatedTable<TSnapshot>` are shared. The transport carries only authenticated sender IDs and bytes; it does not interpret game messages.
- `BlackjackStateService` implements the three game hooks and keeps `TrySubmit(tableId, action, seatIndex, amount, out commandId)` and the blackjack-specific `TryGetTable` result. `BlackjackProtocol.Instance` supplies its codec; the static encode/decode API remains available. Blackjack requires v2; the generic protocol and counter-game version are unchanged.

To add roulette (or another game):

1. Define a host-only model and a data-only public snapshot. Do not put private/random future outcomes or model back-references in snapshots.
2. Derive a command record from `GameMessage<YourSnapshot>`, adding only that game's immutable command fields. Give it a public parameterless constructor. Use a unique protocol name such as `Approximately21.Roulette` and an explicit positive version.
3. Derive a codec from `GameProtocol<YourMessage, YourSnapshot>`. Implement `IsCommandValid` for payload bounds and `IsSnapshotValid` for all untrusted snapshot fields, including collection lengths and nulls. Validation must return false for malformed input, not throw. The base checks protocol identity/version before deserialization, then validates the full envelope and payload before use.
4. Derive a service from `GameStateService<YourGame, YourMessage, YourSnapshot>`, passing the transport, codec, model factory, and optional clock. Implement `TryApply` using the supplied transport-authenticated player ID, `DisconnectPlayer` (return true only if the public state changed), and `CreateSnapshot`. Rejected actions must leave the model unchanged; successful actions must produce a valid bounded snapshot. Completion error text must fit 256 characters.
5. Expose a game-specific submission method that constructs its message and calls protected `TrySubmit(tableId, message, out commandId)`. The base fills all authority/session/revision metadata. Consume the inherited `TryGetTable` returning a detached `ReplicatedTable<YourSnapshot>`; no extra replication logic is required.

Use **one service and one transport/channel per game per local lobby connection**, with matching coordinated channels on every peer. Table IDs and session epochs are scoped to that service. Do not give several services the same receive queue/channel: one pump would consume another game's packets, even though protocol markers prevent interpreting them as the wrong game. Protocol names isolate decoding, not transport routing. Dispose each service's owned transport separately and forward authorized Steam session requests to the appropriate adapters. A shared-channel multiplexer and host migration are not implemented.

The former networking names `IBlackjackTransport`, `SteamBlackjackTransport`, and `BlackjackMessageKind` are now `IGameTransport`, `SteamGameTransport`, and `GameMessageKind`. This is a source API rename, not a wire-version change. `GameStateServiceTests` provides a working test-only counter game example independent of blackjack; it does not implement roulette gameplay.

## Ownership and wire protocol

One `BlackjackStateService` belongs to one local lobby connection. The verified game host owns a separate `BlackjackGameState` for each registered shared table ID. Only that host handles commands; its own commands take the same validation path. The sender identity comes from the transport, never from command arguments.

Blackjack protocol **v2** uses bounded UTF-8 JSON envelopes, reliable full snapshots after accepted changes, and a new random session epoch on resets. Exactly five seats (indices 0–4) and authoritative `MinimumBet`/`MaximumBet` metadata are required. V1 peers are rejected; every participant needs the updated plugin. The protocol marker, version, lobby, authority, epoch, revision, enums, cards, seat identities, collection sizes, and amounts are checked before accepting state. Authoritative models are never deserialized from the network. Snapshots contain neither the shuffled deck nor the dealer's hidden card; scores are derived locally. `TryGetTable` returns a detached copy, so a view cannot alter either the host model or the replica cache.

Commands carry a monotonically increasing command ID scoped to the client's connection and the expected table revision. Duplicate commands do not run twice; a command against an older revision is rejected and receives the latest snapshot. Concurrent commands are serialized, not merged: a losing revision race must be retried as a **new** user command after processing `CommandCompleted`. There is one outstanding command per client service. Transport send failures and lost responses are retried every two seconds with the same command ID. `TrySubmit` means queued, **not** accepted; observe `CommandCompleted` for the game result.

Clients request initial synchronization and cache tables even before corresponding world entities exist. Periodic full resynchronization (30 seconds when idle, two seconds while establishing a session or awaiting a command) repairs missed sends and discovers host restarts. A welcome response must match an outstanding synchronization nonce and the client's connection ID. Limits are 64 lobby members, 256 table identities including removal tombstones per epoch, 64 KiB per message, 128 received messages per pump, 32 commands per sender per second, and one full sync per peer per two seconds.

## Implemented Unity hookup

- `BlackjackNetworkingRuntime` owns one transport/service, pumps on the Unity thread independently of visible surfaces, and exposes `ServiceChanged` when the service is replaced or stopped. Lifecycle hooks dispose the channel before Steam shutdown and clear sessions on core/world replacement.
- `SteamLobbyAdapter` reads local identity, membership, and the actual game host using `SteamManager.IsHost()` and authenticated game connection information, not lobby ownership. It retains session-request callbacks, accepts only authorized peers, and releases callbacks during teardown. It reuses the game's Steam callback loop, without another Steam initialization or callback pump. Missing verified identity/readiness disables controls.
- `BlackjackTableRegistry` tracks individual placed `NetcoreEntity._id` values. Only the host registers `blackjack:<native-id>:<incarnation>` identities. Clients associate cached replicas with local blocks, including snapshots arriving before entities. Confirmed destruction removes a table; renderer loss does not. Reused native IDs get new host-issued incarnations. The 256-identity epoch bound includes tombstones; registration failures are logged without resetting unrelated tables.
- `BlackjackInteraction` registers the five groups with actual felt bounds before renderer configuration, resolves each local `InteractionContext` through the registry, and owns a managed `BlackjackTablePresentation`. That controller subscribes to table, reset, and completion events, caches detached snapshots and per-table/seat wagers/views, and never changes gameplay optimistically. Runtime service replacement detaches old subscriptions and clears presentation state.
- Commands call `TrySubmit` with the clicked seat and table. Only Bet sends the selected amount; every other action sends zero. Pending status comes directly from the shared service (plus an in-call submission guard), so synchronous host/fast client completion cannot leave a stale UI command ID. A pending client command disables game actions across all local table views. Confirmed removal releases that pending command; resets clear all table selections and feedback. Rejections show the host's reason; the UI never automatically resubmits them. Transport retries retain the same ID and are not new gameplay commands.
- Lobby exit and verified host changes reset games rather than migrate them. No persistent bankrolls or real ship-resource transactions are implemented.

### Channel configuration

In the plugin's BepInEx configuration, set `[Networking] BlackjackChannel` (default `21021`) to the **same unused nonnegative channel on every peer**, then restart. A high number does not guarantee isolation. Do not share the receive queue with another service or the native game; there is no generic multiplexer.

### Seat controls

Seats are labeled **1–5**, arranged along the felt's model-local −Z curved edge. Each compact rotated panel contains Join, Leave, Bet, Deal, Hit, Stand, Double Down, Split, wager minus/plus, and public status labels. Selection starts at 10 chips and changes by 1, clamped to host limits and available funds; press Bet explicitly to commit. Replacing an unplayed wager counts its existing stake, but completed-round stakes are not counted again. Balances and payouts retain decimal precision.

Only the local player's seat enables owned actions. Other occupied seats are read-only, except Join follows reconnect/reclaim rules for disconnected seats. Bet/Leave work between rounds; any connected seated player can Deal during betting once a connected player has wagered. Turn controls follow the active owned hand and visible card/fund/hand-count constraints; host validation remains authoritative for hidden constraints and revision races. Unavailable controls remain visibly disabled and cannot submit.

Labels show ownership, balance, committed stake, selected wager, phase/active hand, public cards/scores, and synchronization or command feedback. The dealer hole card remains concealed. Hidden constraints, races, or lost connectivity can still reject a command after it was visibly enabled.

## Gameplay

The game uses a fresh cryptographically shuffled 52-card deck each round and correct soft-ace scoring. Players act in seat order, with split hands completed in order. Rules include dealer stand on soft 17, immediate dealer-blackjack check, natural blackjack paying 3:2, ordinary wins paying 1:1, pushes returning the wager, doubling, and same-rank splitting up to four hands. Split aces receive one card each without resplitting; split 21 is not a natural. Insurance and surrender are not offered. Stakes are play chips only, not fuel, energy, or Steam inventory.

`BlackjackOptions` defaults to 1,000 chips and wagers from 1 to 500. Bets use cent precision and balances retain exact half-cent 3:2 payouts. Production shuffling uses unbiased Fisher–Yates with `RandomNumberGenerator.GetInt32`; tests inject complete deterministic decks instead. Actions are atomic, including exhaustion rejection, and preserve enough deck capacity to finish the dealer's hand. A new successful bet/join/leave clears completed results; each round needs fresh wagers. Any connected seated player may deal the current wagers.

Disconnected players' unresolved hands auto-stand, and unplayed wagers are refunded. `Join` reconnects the same identity at its disconnected seat without reopening finished hands. Other players may reclaim disconnected seats only between rounds. Leaving or being replaced does not refill a bankroll: a private ledger retains balances for up to 64 distinct identities over that table's lifetime. At this limit unseen identities cannot join, while known players can still return. A new table/session clears that ledger. These are deliberately bounded, table-local play-chip accounts, not a persistent economy.

## Verification

Run the deterministic managed suite without the game or Steam:

```powershell
dotnet test .\Approximately21.Tests\Approximately21.Tests.csproj
```

The test project source-links the engine, network services, geometry, and presentation controller; Unity adapters and seat renderer definitions are excluded. In-memory tests cover v2 boundaries, action/seat/table/amount routing, two-table isolation, pending blocking, synchronous host and fast/asynchronous client completion, rejection without resubmission, disconnect/reclaim, removal, reset, and service rebinding. Counter-game tests retain coverage of generic reuse and lifecycle behavior.

Fast validation commands (no live game required):

```powershell
dotnet test Approximately21.Tests\Approximately21.Tests.csproj --no-restore --verbosity minimal
dotnet build Approximately21\Approximately21.csproj --no-restore --verbosity minimal
```

**Live acceptance remains blocked: no two-peer Unity/Steam environment was available for this implementation.** An interop build and managed tests are not proof of multiplayer operation. With compatible clients, verify two distinct tables on one ship resolve identical individual-block native IDs across peers, early/late binding, callbacks and actual-host detection, command/snapshot convergence, disconnect/reconnect, destruction/native-ID reuse, and session teardown. Also verify panel readability and transformed rendering/hit alignment, including disabled nearer surfaces; see `INTERACTION_VALIDATION.md`. Record instrumented observations before claiming live convergence.